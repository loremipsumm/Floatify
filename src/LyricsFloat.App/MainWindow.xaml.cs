using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LyricsFloat.Core;
using LyricsFloat.Core.Lyrics;
using Windows.Media;
using Windows.Storage.Streams;

namespace LyricsFloat.App;

public partial class MainWindow : Window
{
    public const double UnlockPillWidth = 112;

    /// <summary>Matches the corner radius Windows 11 gives rounded windows, which also clips the blur.</summary>
    private const double CornerRadius = 8;

    /// <summary>Space between the card edge and the header / scrubber (the content grid's margin in XAML).</summary>
    private const double CardPadding = 14;

    /// <summary>Space between the lyrics card and the footer when it opens.</summary>
    private const double FooterGap = 2;
    public const double UnlockPillHeight = 28;

    private const int HotkeyClickThrough = 1;
    private const int HotkeyToggleVisibility = 2;
    private const double OffsetStep = 0.25;
    private const double LargeOffsetStep = 2;
    // People click a beat after a line starts; this much lead makes "sync to this line" land on time.
    private const double ReactionLeadSeconds = 0.25;

    // With auto-hide on, a paused song keeps the overlay up this long before it slips away.
    private static readonly TimeSpan AutoHidePause = TimeSpan.FromMinutes(1);
    // After the user or app start shows the overlay, give it a moment before auto-hide may act.
    private static readonly TimeSpan AutoHideGrace = TimeSpan.FromSeconds(8);
    private const double DefaultWidth = 560;
    private const double DefaultHeight = 256;

    // Lyric layout relative to the text size: line height, and the gap between lines.
    private const double LineHeightFactor = 1.2;
    private const double LineGapFactor = 0.78;
    private const int MinVisibleLines = 3;
    // Share of the lyrics height lost to the fade at each edge (see the OpacityMask in XAML).
    private const double EdgeFade = 0.08;
    // Inactive lines are dimmed, softly blurred and slightly smaller, as in Apple Music.
    private const double InactiveScale = 0.94;

    private static readonly IEasingFunction Ease = CreateEase();

    // Motion settles with a slight overshoot, like a spring.
    private static readonly IEasingFunction Spring = CreateSpring();

    private readonly AppSettings _settings;
    private readonly LyricsService _lyrics;
    private readonly SpotifyWatcher _watcher = new();
    private readonly PlaybackClock _clock = new();
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private nint _hwnd;
    private bool _allowClose;
    private bool _spotifyFound;
    private bool _isPlaying;
    private bool _canSeek;
    private bool _canSkip;
    private bool _canShuffle;
    private bool _canRepeat;
    private bool? _shuffle;
    private MediaPlaybackAutoRepeatMode? _repeat;
    private bool _hasArtwork;
    private readonly DispatcherTimer _saveSoon = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private int _artworkVersion;
    private TrackInfo? _track;
    private LyricsTimeline? _timeline;
    private string _status = "";
    private CancellationTokenSource? _lookupCts;

    // Reconnect state: consecutive failed lookups, when the next attempt is due, and a way to start it early.
    private int _lookupFailures;
    private DateTime? _retryAt;
    private TaskCompletionSource _retryNow = new();

    // What is on screen, so Render only animates real changes.
    private LyricsTimeline? _shownTimeline;
    private int _shownIndex = int.MinValue;
    private string? _shownMessage;

    private DateTime _cursorLastInside;
    private DateTime _settingsClosedAt;

    // Browsing the lyrics with the wheel pauses following the song until "Back to current" or a few idle seconds.
    private static readonly TimeSpan FollowResumeDelay = TimeSpan.FromSeconds(8);
    private readonly DispatcherTimer _resumeFollow = new() { Interval = FollowResumeDelay };
    private bool _manualScroll;
    private double _scrollTarget;
    private TimeSpan _lastFrameTime;

    // Footer section: on hover the window grows by the footer's height with a smooth ease in and out
    // (no overshoot). _footerExtra is how much taller than the lyrics card the window currently is.
    private const double FooterOpenMs = 360;
    private const double FooterCloseMs = 300;
    private double _footerExtra;
    private double _footerHeight;
    private double _footerFrom, _footerTo, _footerDuration;
    private TimeSpan _footerStart;
    private bool _footerAnimating;
    private double _contentHeight;
    private double _anchorTop;
    private bool _growUp;
    private bool _userSizing;

    // Scrubbing the footer's progress bar: dragging previews a position, releasing seeks there.
    private bool _scrubbing;
    private double _scrubFraction; // between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE: the user is dragging or resizing

    // Visibility: started hidden (--background), hidden on purpose by the user, welcome card up, and
    // bookkeeping for auto-hide and the show/hide fades.
    private bool _startHidden;
    private bool _userHidden;
    private bool _welcomeOpen;
    private bool _syncingSwitches;
    private DateTime _pausedSince;
    private DateTime _autoHideAfter;
    private int _visibilityVersion;
    private (string Title, string Subtitle) _hint;
    private DateTime _hintUntil;

    public MainWindow(AppSettings settings, LyricsService lyrics)
    {
        _settings = settings;
        _lyrics = lyrics;
        InitializeComponent();

        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);
        UpdateCardLayout(Height);
        if (settings.Left is { } left && settings.Top is { } top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        ApplyTransparency();
        ApplyTextSize();
        TransparencySlider.Value = settings.Transparency * 100;
        TextSizeSlider.Value = settings.TextSize;
        _saveSoon.Tick += (_, _) =>
        {
            _saveSoon.Stop();
            _settings.Save();
        };
        LockIndicator.Visibility = settings.ClickThrough ? Visibility.Visible : Visibility.Collapsed;

        foreach (UIElement item in Toolbar.Children) item.RenderTransform = new TranslateTransform();
        // Tooltips sit centred on their control: above it in the footer, below it in the header.
        foreach (var control in new FrameworkElement[] { ShuffleButton, PrevButton, PlayButton, NextButton, RepeatButton })
            SetTip(control, control.ToolTip);
        foreach (var control in Toolbar.Children.OfType<FrameworkElement>().Where(c => c.ToolTip is not null))
            SetTip(control, control.ToolTip, below: true);

        _resumeFollow.Tick += (_, _) => ResumeFollow();
        _renderTimer.Tick += (_, _) => Render();
        // The karaoke sweep and progress bar move every frame; line changes stay on the slower render tick.
        CompositionTarget.Rendering += OnFrame;
        _watcher.Updated += snapshot => Dispatcher.InvokeAsync(() => OnSnapshot(snapshot));
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Loaded += OnLoaded;
    }

    public bool IsWaitingToRetry => _retryAt is not null;

    /// <summary>Skips the rest of the reconnect countdown.</summary>
    public void RetryLyricsNow() => _retryNow.TrySetResult();

    public bool ClickThrough
    {
        get => _settings.ClickThrough;
        set
        {
            _settings.ClickThrough = value;
            _settings.Save();
            ApplyWindowStyles();
            Toolbar.Visibility = Visibility.Collapsed;
            if (value) ShowChrome(false);
            LockIndicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value) ShowHint("Locked", "Hover over the lyrics to unlock", 4);
            else _hintUntil = default;
            Render();
        }
    }

    public double Transparency
    {
        get => _settings.Transparency;
        set
        {
            _settings.Transparency = Math.Clamp(value, 0, 1);
            _settings.Save();
            ApplyTransparency();
            TransparencySlider.Value = _settings.Transparency * 100;
        }
    }

    // Both the dark material and the artwork backdrop thin out as transparency rises, but never vanish:
    // a little dark tint always remains so white lyrics stay readable over bright windows.
    private double MaterialOpacityTarget => 0.25 + 0.67 * (1 - _settings.Transparency);
    private double BackdropOpacityTarget => 0.12 + 0.68 * (1 - _settings.Transparency);

    private double CurrentOffset => _track is null ? 0 : _settings.TrackOffsets.GetValueOrDefault(_track.Key);

    public bool AutoHide
    {
        get => _settings.AutoHide;
        set
        {
            _settings.AutoHide = value;
            _settings.Save();
            if (!value && !_userHidden && _spotifyFound) ShowAnimated();
            UpdateAutoVisibility();
        }
    }

    /// <summary>Creates the window (hidden or not) and starts following Spotify.</summary>
    public async void Start(bool showNow)
    {
        _startHidden = !showNow;
        new WindowInteropHelper(this).EnsureHandle(); // hotkeys and blur need the window even while hidden
        _renderTimer.Start();
        if (showNow) ShowAnimated();
        if (!_settings.OnboardingDone) ShowWelcome();

        try
        {
            await _watcher.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the media session watcher", ex);
        }
    }

    public void ToggleVisibility()
    {
        if (IsVisible && Root.Opacity > 0)
        {
            _userHidden = true;
            HideAnimated();
        }
        else
        {
            _userHidden = false;
            ShowAnimated();
        }
    }

    private void ShowAnimated()
    {
        _visibilityVersion++;
        _autoHideAfter = DateTime.Now + AutoHideGrace;
        if (!IsVisible)
        {
            Root.Opacity = 0;
            Show();
        }
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(350)) { EasingFunction = Ease });
    }

    private void HideAnimated()
    {
        if (!IsVisible) return;
        var version = ++_visibilityVersion;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = Ease };
        fade.Completed += (_, _) =>
        {
            if (version == _visibilityVersion) Hide(); // unless it was shown again meanwhile
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Shows the overlay when Spotify starts playing and, with auto-hide on, hides it when it stops.</summary>
    private void UpdateAutoVisibility()
    {
        if (_welcomeOpen) return;

        var active = _spotifyFound && _track is not null && (_isPlaying || DateTime.Now - _pausedSince < AutoHidePause);
        if (active && _startHidden)
        {
            _startHidden = false;
            if (!_userHidden) ShowAnimated();
            return;
        }

        if (!_settings.AutoHide || _startHidden) return;

        if (active && !IsVisible && !_userHidden)
            ShowAnimated();
        else if (!active && IsVisible && Root.Opacity > 0 && DateTime.Now > _autoHideAfter && !IsMouseOver && !SettingsPopup.IsOpen)
            HideAnimated();
    }

    private void ShowWelcome()
    {
        _welcomeOpen = true;
        if (Height < 320)
        {
            Height = 320; // room for the card
            UpdateCardLayout(Height);
        }
        WelcomePanel.Visibility = Visibility.Visible;
        WelcomePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = Ease });
    }

    private void OnWelcomeDone(object sender, RoutedEventArgs e)
    {
        AutoStart.Enabled = WelcomeStartupSwitch.IsChecked == true;
        _settings.OnboardingDone = true;
        _settings.Save();
        _welcomeOpen = false;
        _autoHideAfter = DateTime.Now + AutoHideGrace;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = Ease };
        fade.Completed += (_, _) => WelcomePanel.Visibility = Visibility.Collapsed;
        WelcomePanel.BeginAnimation(OpacityProperty, fade);
    }

    private void OnSettingsOpened(object? sender, EventArgs e)
    {
        _syncingSwitches = true;
        AutoHideSwitch.IsChecked = _settings.AutoHide;
        StartupSwitch.IsChecked = AutoStart.Enabled;
        _syncingSwitches = false;
    }

    private void OnAutoHideSwitch(object sender, RoutedEventArgs e)
    {
        if (!_syncingSwitches) AutoHide = AutoHideSwitch.IsChecked == true;
    }

    private void OnStartupSwitch(object sender, RoutedEventArgs e)
    {
        if (!_syncingSwitches) AutoStart.Enabled = StartupSwitch.IsChecked == true;
    }

    public void ResetPosition()
    {
        var area = SystemParameters.WorkArea;
        Width = DefaultWidth;
        Height = DefaultHeight;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 80;
        if (!_footerAnimating) UpdateCardLayout(Height + _footerExtra);
        SaveBounds();
        _userHidden = false;
        ShowAnimated();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyWindowStyles();
        ApplyBackdropBlur();
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        RegisterHotkey(HotkeyClickThrough, NativeMethods.VK_L);
        RegisterHotkey(HotkeyToggleVisibility, NativeMethods.VK_K);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 and friends only hide the overlay; quitting goes through the tray menu.
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderTimer.Stop();
        CompositionTarget.Rendering -= OnFrame;
        _lookupCts?.Cancel();
        _watcher.Dispose();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        UnlockPopup.IsOpen = false;
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyClickThrough);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyToggleVisibility);
        _settings.Save();
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Left is null || !IsOnScreen(Left, Top)) ResetPosition();
        ApplyBackdropBlur();
        Render();
        Dispatcher.BeginInvoke(WarmUpFooter, DispatcherPriority.Background);
    }

    /// <summary>
    /// Builds the footer once, out of sight, so the first hover opens it as quickly as every later one
    /// (otherwise its buttons and text are created on that first hover).
    /// </summary>
    private void WarmUpFooter()
    {
        if (_footerAnimating || _footerExtra > 0) return;
        ScrubberRow.Visibility = Visibility.Visible; // placed below the card, outside the window for now
        Root.UpdateLayout();
        _footerHeight = ScrubberRow.ActualHeight + FooterGap + CardPadding;
        ScrubberRow.Visibility = Visibility.Collapsed;
        UpdateCardLayout(Height);
    }

    private void OnSnapshot(PlaybackSnapshot snapshot)
    {
        _spotifyFound = snapshot.SpotifyFound;
        _canSeek = snapshot.CanSeek;
        _canSkip = snapshot.CanSkip;
        _canShuffle = snapshot.CanShuffle;
        _canRepeat = snapshot.CanRepeat;
        _shuffle = snapshot.ShuffleActive;
        _repeat = snapshot.RepeatMode;
        UpdateModeButtons();
        if (snapshot.IsPlaying != _isPlaying || EqBars.Tag is null) SetPlaying(snapshot.IsPlaying);

        if (snapshot.Track != _track)
        {
            _track = snapshot.Track;
            _timeline = null;
            _clock.Reset();
            foreach (var text in new[] { TitleText, ArtistText })
                text.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = Ease });
            _hasArtwork = false;
            _ = LoadArtworkAsync(snapshot.Artwork);
            _ = LoadLyricsAsync(_track);
        }

        else if (_track is not null && !_hasArtwork && snapshot.Artwork is not null)
        {
            _ = LoadArtworkAsync(snapshot.Artwork); // Spotify can publish the cover a moment after the title
        }

        if (_track is not null)
            _clock.Update(snapshot.Position, snapshot.SampledAt, snapshot.IsPlaying, snapshot.Duration);

        UpdateAutoVisibility();
        Render();
    }

    private async Task LoadLyricsAsync(TrackInfo? track)
    {
        _lookupCts?.Cancel();
        _lookupCts = null;
        if (track is null) return;

        var cts = _lookupCts = new CancellationTokenSource();
        _lookupFailures = 0;
        _retryAt = null;
        _status = "Loading lyrics…";

        try
        {
            // Let the timeline (duration) catch up, and skip tracks flicked past quickly.
            await Task.Delay(400, cts.Token);

            // Keep reconnecting for as long as this track is playing.
            while (true)
            {
                try
                {
                    var entry = await _lyrics.GetAsync(track, _clock.Duration, cts.Token);
                    cts.Token.ThrowIfCancellationRequested(); // the track may have changed while we waited

                    _timeline = LyricsTimeline.FromEntry(entry, _clock.Duration);
                    _status = _timeline is not null ? "" : entry.Instrumental ? "♪  Instrumental  ♪" : "No lyrics found";
                    Log.Info($"Lyrics for \"{track.Artist} - {track.Title}\": " +
                             (_timeline is null ? _status : $"{_timeline.Lines.Count} lines{(_timeline.IsApproximate ? ", unsynced" : "")}"));
                    break;
                }
                catch (Exception ex) when (!cts.IsCancellationRequested && LookupRetry.IsTransient(ex))
                {
                    _lookupFailures++;
                    var delay = LookupRetry.DelayAfter(_lookupFailures, ex);
                    Log.Error($"Lyrics lookup failed for \"{track.Artist} - {track.Title}\" " +
                              $"(attempt {_lookupFailures}, next in {delay.TotalSeconds:0}s)", ex);

                    _status = ex is HttpRequestException { StatusCode: not null } ? "Lyrics server is busy" : "No internet connection";
                    _retryAt = DateTime.Now + delay;
                    Render();

                    await WaitForRetryAsync(delay, cts.Token);
                    _retryAt = null;
                    Render();
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"Lyrics lookup failed for \"{track.Artist} - {track.Title}\"", ex);
            _status = "Couldn't load lyrics";
        }

        _lookupFailures = 0;
        Render();
    }

    /// <summary>Waits out the reconnect delay, unless the network comes back or the user asks to retry first.</summary>
    private async Task WaitForRetryAsync(TimeSpan delay, CancellationToken ct)
    {
        _retryNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(Task.Delay(delay, wait.Token), _retryNow.Task);
        wait.Cancel();
        ct.ThrowIfCancellationRequested();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(async () =>
        {
            if (!IsWaitingToRetry) return;
            // Give DHCP and DNS a moment to settle after a reconnect.
            await Task.Delay(1500);
            Log.Info("Network changed, retrying lyrics lookup now");
            RetryLyricsNow();
        });

    private void UpdateRetryPanel()
    {
        var reconnecting = _lookupFailures > 0 && _track is not null && _timeline is null && _spotifyFound;
        if (!reconnecting)
        {
            RetryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (RetryPanel.Visibility != Visibility.Visible)
        {
            RetryPanel.Visibility = Visibility.Visible;
            RetryPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = Ease });
        }

        SetText(RetryText, _retryAt is { } at
            ? $"Retrying in {Math.Max(1, Math.Ceiling((at - DateTime.Now).TotalSeconds)):0}s"
            : "Reconnecting…");
        RetryButton.Visibility = _retryAt is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render()
    {
        UpdateUnlockPopup();
        UpdateRetryPanel();
        UpdateHeader();
        SetText(OffsetText, CurrentOffset.ToString("+0.00;-0.00;0.00") + "s");

        if (!_spotifyFound) ShowMessage("Open Spotify to see lyrics");
        else if (_track is null) ShowMessage("Waiting for a song…");
        else if (_timeline is null) ShowMessage(_status);
        else ShowLyrics(_timeline, _timeline.IndexAt(_clock.Position + TimeSpan.FromSeconds(CurrentOffset)));
    }

    private void UpdateHeader()
    {
        var (title, subtitle) =
            DateTime.Now < _hintUntil ? _hint
            : !_spotifyFound ? ("Floatify", "Spotify not detected")
            : _track is null ? ("Spotify", "Nothing playing")
            : (_track.Title, _track.Artist + (_timeline?.IsApproximate == true ? "  ·  unsynced" : ""));

        SetText(TitleText, title);
        SetText(ArtistText, subtitle);
        EqBars.Visibility = _spotifyFound && _track is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the album cover in the header and, decoded tiny and scaled up, as a soft colour backdrop.</summary>
    private async Task LoadArtworkAsync(IRandomAccessStreamReference? artwork)
    {
        var version = ++_artworkVersion;
        BitmapImage? cover = null, backdrop = null;

        if (artwork is not null)
        {
            try
            {
                using var stream = await artwork.OpenReadAsync();
                using var memory = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(memory);
                cover = Decode(memory, 96);
                backdrop = Decode(memory, 16);
            }
            catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or NotSupportedException)
            {
                Log.Error("Could not read the album art", ex);
            }
        }

        if (version != _artworkVersion) return; // a newer track's cover is already on its way
        _hasArtwork = cover is not null;
        ArtBrush.ImageSource = cover;
        ArtBackdrop.Source = backdrop;

        var fade = TimeSpan.FromMilliseconds(700);
        ArtBrush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, cover is null ? 0 : 1, fade) { EasingFunction = Ease });
        ArtBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, backdrop is null ? 0 : BackdropOpacityTarget, fade) { EasingFunction = Ease });
    }

    private static BitmapImage Decode(MemoryStream data, int width)
    {
        data.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.StreamSource = data;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private double LineHeightPx => Math.Round(_settings.TextSize * LineHeightFactor);
    private double LineGapPx => Math.Round(_settings.TextSize * LineGapFactor);

    private void ShowMessage(string message)
    {
        if (_shownTimeline is null && _shownMessage == message) return;

        var wasShowingLyrics = _shownTimeline is not null;
        _shownTimeline = null;
        _shownIndex = int.MinValue;
        _shownMessage = message;

        SetText(MessageText, message);
        if (wasShowingLyrics) LinesPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { EasingFunction = Ease });
        MessagePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = Ease });
    }

    private void ShowLyrics(LyricsTimeline timeline, int index)
    {
        // Before the first line is sung (index -1), the list already sits on it (ScrollTo clamps to the first
        // line), but it is styled as an upcoming line; it lights up once singing actually starts.

        if (!ReferenceEquals(timeline, _shownTimeline))
        {
            // New lyrics: lay out every line, place the current one, then fade the list in.
            StopBrowsing();
            _shownTimeline = timeline;
            _shownIndex = index;
            _shownMessage = null;
            BuildLines(timeline);
            StyleLines(index, animate: false);
            ScrollTo(index, animate: false);
            MessagePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease });
            LinesPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = Ease });
            return;
        }

        if (index == _shownIndex) return;

        // One line forward glides at the song's pace; seeks move a little quicker.
        var duration = index == _shownIndex + 1 ? ScrollDuration(timeline, index) : new Duration(TimeSpan.FromMilliseconds(450));
        _shownIndex = index;
        StyleLines(index, animate: true, duration);
        if (!_manualScroll) ScrollTo(index, animate: true, duration); // while browsing, only the highlight moves
    }

    private static string LineText(LyricsTimeline timeline, int index)
    {
        var text = timeline.Lines[index].Text;
        return text.Length == 0 ? "♪" : text; // empty LRC lines mark instrumental breaks
    }

    private static Duration ScrollDuration(LyricsTimeline timeline, int index)
    {
        // Glide unhurried, but settle before a quick next line arrives.
        var gap = index + 1 < timeline.Lines.Count
            ? timeline.Lines[index + 1].Time - timeline.Lines[index].Time
            : TimeSpan.FromSeconds(2);
        return TimeSpan.FromMilliseconds(Math.Clamp(gap.TotalMilliseconds * 0.6, 350, 800));
    }

    private void BuildLines(LyricsTimeline timeline)
    {
        LinesPanel.Children.Clear();
        LinesShift.BeginAnimation(TranslateTransform.YProperty, null);

        for (var i = 0; i < timeline.Lines.Count; i++)
        {
            var text = new TextBlock
            {
                Text = LineText(timeline, i),
                Style = (Style)FindResource("Lyric"),
                TextWrapping = TextWrapping.Wrap,
                RenderTransformOrigin = new Point(0, 0.5), // left-aligned, so lines grow from their left edge
                RenderTransform = new ScaleTransform(InactiveScale, InactiveScale),
                Effect = new System.Windows.Media.Effects.BlurEffect(),
            };
            // Each line is a soft hover target; clicking it seeks there.
            var item = new Border
            {
                Child = text,
                Tag = i,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(Color.FromArgb(0, 0xFF, 0xFF, 0xFF)),
            };
            item.MouseEnter += OnLineEnter;
            item.MouseLeave += OnLineLeave;
            item.MouseLeftButtonDown += OnLinePress;
            item.MouseMove += OnLineDragCheck;
            item.MouseLeftButtonUp += OnLineRelease;
            item.MouseRightButtonUp += OnLineSync;
            LinesPanel.Children.Add(item);
        }

        ApplyLineMetrics();
        LinesPanel.Width = LyricsArea.ActualWidth;
        LyricsArea.UpdateLayout();
    }

    private void ApplyLineMetrics()
    {
        foreach (Border item in LinesPanel.Children)
        {
            var text = (TextBlock)item.Child;
            text.FontSize = _settings.TextSize;
            text.LineHeight = LineHeightPx;
            item.Margin = new Thickness(-8, 0, -8, LineGapPx - item.Padding.Top - item.Padding.Bottom);
        }
    }

    /// <summary>Brightens the current line and dims and blurs the others more the further away they are.</summary>
    private void StyleLines(int current, bool animate, Duration duration = default)
    {
        var reach = (int)Math.Ceiling(LyricsArea.ActualHeight / (LineHeightPx + LineGapPx)) + 2;

        for (var i = 0; i < LinesPanel.Children.Count; i++)
        {
            var item = (Border)LinesPanel.Children[i];
            var text = (TextBlock)item.Child;
            var distance = Math.Abs(i - current);

            // Lines well outside the viewport skip rendering (their blur is the costly part). While browsing,
            // every line must be drawn, since the view can go anywhere (and browsing uses no blur).
            item.Visibility = _manualScroll || distance <= reach ? Visibility.Visible : Visibility.Hidden;

            // Upcoming lines read more clearly than ones already sung, but stay dimmer than the current line.
            var upcoming = i > current;
            var opacity = distance == 0 ? 1
                : _manualScroll ? 0.6 // browsing: every line readable, the sung one still brightest
                : upcoming ? Math.Max(0.22, 0.65 - 0.1 * (distance - 1))
                : Math.Max(0.1, 0.35 - 0.07 * (distance - 1));
            var blur = distance == 0 || _manualScroll ? 0
                : upcoming ? Math.Min(4, 0.8 + 0.9 * (distance - 1))
                : Math.Min(5, 1.5 + 1.1 * (distance - 1));
            var scale = distance == 0 ? 1 : InactiveScale;
            var effect = (System.Windows.Media.Effects.BlurEffect)text.Effect;
            var transform = (ScaleTransform)text.RenderTransform;

            if (!animate || item.Visibility != Visibility.Visible)
            {
                text.BeginAnimation(OpacityProperty, null);
                effect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                text.Opacity = opacity;
                effect.Radius = blur;
                transform.ScaleX = transform.ScaleY = scale;
                continue;
            }

            // Lines react a beat apart, rippling outward from the current one.
            var delay = TimeSpan.FromMilliseconds(30 * Math.Min(distance, 6));
            text.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { EasingFunction = Ease, BeginTime = delay });
            effect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty,
                new DoubleAnimation(blur, duration) { EasingFunction = Ease, BeginTime = delay });
            var grow = new DoubleAnimation(scale, duration) { EasingFunction = Spring, BeginTime = delay };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    /// <summary>Moves the list so the current line sits in the middle of the window.</summary>
    private void ScrollTo(int index, bool animate, Duration duration = default)
    {
        if (LinesPanel.Children.Count == 0) return;

        var anchor = (FrameworkElement)LinesPanel.Children[Math.Clamp(index, 0, LinesPanel.Children.Count - 1)];
        var centre = VisualTreeHelper.GetOffset(anchor).Y + anchor.ActualHeight / 2;
        var target = Math.Round(LyricsArea.ActualHeight / 2 - centre);

        if (animate)
            LinesShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(target, duration) { EasingFunction = Spring });
        else
        {
            LinesShift.BeginAnimation(TranslateTransform.YProperty, null);
            LinesShift.Y = target;
        }
    }

    private void OnLyricsAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Sub-pixel changes come from layout rounding, not from the user; don't re-place the lyrics for them.
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1 && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 1) return;
        LinesPanel.Width = e.NewSize.Width;
        if (_shownTimeline is null) return;
        LyricsArea.UpdateLayout(); // the new width can change how lines wrap
        StopBrowsing();
        StyleLines(_shownIndex, animate: false);
        ScrollTo(_shownIndex, animate: false);
    }

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }

    private void ApplyTextSize()
    {
        SetText(TextSizeText, $"{_settings.TextSize:0} pt");
        MessageText.FontSize = Math.Round(_settings.TextSize * 0.75);
        ApplyLineMetrics();

        // Tall enough for at least three lines clear of the edge fade; taller windows simply show more.
        MinHeight = Math.Ceiling((MinVisibleLines * LineHeightPx + (MinVisibleLines - 1) * LineGapPx) / (1 - 2 * EdgeFade));
        if (IsLoaded && !_footerAnimating && _footerExtra == 0 && Height < MinHeight) UpdateCardLayout(MinHeight);

        if (_shownTimeline is not null)
        {
            LyricsArea.UpdateLayout();
            StyleLines(_shownIndex, animate: false);
            ScrollTo(_shownIndex, animate: false);
        }
    }

    private void UpdateUnlockPopup()
    {
        if (!ClickThrough || !IsVisible)
        {
            UnlockPopup.IsOpen = false;
            return;
        }

        // A locked window ignores the mouse entirely, so watch the cursor ourselves.
        NativeMethods.GetCursorPos(out var cursor);
        NativeMethods.GetWindowRect(_hwnd, out var bounds);
        var inside = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;

        if (inside)
        {
            _cursorLastInside = DateTime.Now;
            if (!UnlockPopup.IsOpen)
            {
                UnlockPopup.HorizontalOffset = HeaderRow.ActualWidth - UnlockPillWidth + 6;
                UnlockPopup.VerticalOffset = (HeaderRow.ActualHeight - UnlockPillHeight) / 2;
                UnlockPopup.IsOpen = true;
            }
        }
        else if (UnlockPopup.IsOpen && DateTime.Now - _cursorLastInside > TimeSpan.FromMilliseconds(700))
        {
            UnlockPopup.IsOpen = false;
        }
    }

    /// <summary>Per-frame work: move the scrubber and update the elapsed and remaining time.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        var position = _clock.Position;
        var duration = _clock.Duration;
        var hasTime = _track is not null && duration > TimeSpan.Zero;

        // While browsing, ease the list toward the scroll target (frame-rate independent smoothing).
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = Math.Clamp((now - _lastFrameTime).TotalSeconds, 0, 0.05);
        _lastFrameTime = now;

        if (_footerAnimating)
        {
            var t = Math.Clamp((now - _footerStart).TotalMilliseconds / _footerDuration, 0, 1);
            var eased = Ease.Ease(t);
            SetFooterExtra(_footerFrom + (_footerTo - _footerFrom) * eased);
            if (t >= 1) FinishFooterAnimation();
        }
        if (_manualScroll)
        {
            var y = LinesShift.Y;
            var gap = _scrollTarget - y;
            if (Math.Abs(gap) > 0.25) LinesShift.Y = y + gap * (1 - Math.Exp(-dt * 12));
        }
        var fraction = !hasTime ? 0 : _scrubbing ? _scrubFraction : Math.Clamp(position / duration, 0, 1);
        if (_scrubbing) position = duration * fraction; // preview the time being dragged to
        ProgressScale.ScaleX = fraction;
        Canvas.SetLeft(ScrubThumb, fraction * ScrubArea.ActualWidth - ScrubThumb.Width / 2);
        SetText(ElapsedText, hasTime ? FormatTime(position) : "");
        SetText(RemainingText, hasTime ? "-" + FormatTime(duration - position) : "");

    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        if (!playing) _pausedSince = DateTime.Now;
        EqBars.Tag = playing;

        // Play/pause icon: shrink away, swap glyph, spring back in.
        var shrink = new DoubleAnimation(0.4, TimeSpan.FromMilliseconds(90));
        shrink.Completed += (_, _) =>
        {
            PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            var grow = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut },
            };
            PlayIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PlayIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        };
        PlayIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        PlayIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

        // Now-playing bars bounce out of step while playing and settle low when paused.
        foreach (var (bar, seconds) in new[] { (Eq1, 0.43), (Eq2, 0.31), (Eq3, 0.37) })
        {
            bar.BeginAnimation(ScaleTransform.ScaleYProperty, playing
                ? new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ease }
                : new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(300)) { EasingFunction = Ease });
        }
    }

    /// <summary>Fades the header and scrubber in on hover and out again, leaving only the lyrics.</summary>
    private void ShowChrome(bool visible)
    {
        if (visible) PrevButton.IsEnabled = NextButton.IsEnabled = _canSkip;
        SetFooterOpen(visible); // the footer is revealed and hidden by the window growing and shrinking

        // Appear promptly (ease-out), leave a little more slowly.
        var fade = TimeSpan.FromMilliseconds(visible ? 180 : 320);
        IEasingFunction ease = visible ? new QuadraticEase { EasingMode = EasingMode.EaseOut } : Ease;
        foreach (var part in new UIElement[] { HeaderRow, TopScrim })
            part.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, fade) { EasingFunction = ease });
    }

    /// <summary>
    /// Opens or closes the footer section by growing or shrinking the window, so the footer sits below
    /// the lyrics rather than over them. Near the bottom of the screen the window grows upward instead.
    /// </summary>
    private void SetFooterOpen(bool open)
    {
        if (!_footerAnimating)
        {
            _contentHeight = Height - _footerExtra;
            if (_footerExtra < 0.5)
            {
                _anchorTop = Top;
                _growUp = false;
            }
        }

        double target = 0;
        if (open)
        {
            // Lay the footer out (clipped for now) to learn its height; the card content stays as it is.
            PlaceCardContent();
            // Fully visible from the first frame: the growing window reveals it, with no empty gap.
            ScrubberRow.BeginAnimation(OpacityProperty, null);
            ScrubberRow.Opacity = 1;
            ScrubberRow.Visibility = Visibility.Visible;
            Root.UpdateLayout();
            target = _footerHeight = ScrubberRow.ActualHeight + FooterGap + CardPadding;
            if (_footerExtra < 0.5) _growUp = Top + _contentHeight + target > SystemParameters.WorkArea.Bottom;
        }

        if (!_footerAnimating && Math.Abs(target - _footerExtra) < 0.5) return;

        _footerFrom = _footerExtra;
        _footerTo = target;
        _footerDuration = open ? FooterOpenMs : FooterCloseMs;
        _footerStart = _lastFrameTime;
        _footerAnimating = true;
    }

    private void SetFooterExtra(double extra)
    {
        _footerExtra = Math.Max(extra, 0);
        Height = _contentHeight + _footerExtra;
        if (_growUp) Top = _anchorTop - _footerExtra;
    }

    private void FinishFooterAnimation()
    {
        _footerAnimating = false;
        SetFooterExtra(_footerTo);
        if (_footerTo == 0) ScrubberRow.Visibility = Visibility.Collapsed;
        // The card content keeps its fixed height; UpdateCardLayout adjusts it when the user resizes.
    }

    private void ShowToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;

        // Items slide in from the right one after another, overshooting slightly.
        var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        var i = 0;
        foreach (UIElement item in Toolbar.Children)
        {
            var delay = TimeSpan.FromMilliseconds(18 * i++);
            var duration = TimeSpan.FromMilliseconds(260);
            item.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = delay });
            ((TranslateTransform)item.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(10, 0, duration) { BeginTime = delay, EasingFunction = ease });
        }
    }

    private bool LineIsSeekable(int index) =>
        _canSeek && _shownTimeline is { IsApproximate: false } timeline && index >= 0 && index < timeline.Lines.Count;

    private void SeekToLine(int index)
    {
        if (!LineIsSeekable(index)) return;
        SeekTo(_shownTimeline!.Lines[index].Time - TimeSpan.FromSeconds(CurrentOffset));
    }

    private void SeekTo(TimeSpan target)
    {

        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        // Jump the clock right away; Spotify confirms through its next timeline update.
        _clock.Update(target, DateTimeOffset.Now, _isPlaying, _clock.Duration);
        ResumeFollow();
        Render();
        _ = _watcher.SeekAsync(target);
    }

    private static void AnimateColor(SolidColorBrush brush, Color to, int millis) =>
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(to, TimeSpan.FromMilliseconds(millis)) { EasingFunction = Ease });

    private void ChangeOffset(double delta) => SetOffset(CurrentOffset + delta);

    private void SetOffset(double seconds)
    {
        if (_track is null) return;

        var value = Math.Round(seconds, 2);
        if (Math.Abs(value) < 0.001) _settings.TrackOffsets.Remove(_track.Key);
        else _settings.TrackOffsets[_track.Key] = value;

        _settings.Save();
        Render();
    }

    /// <summary>Shift held means bigger steps; read from Windows since this window never takes keyboard focus.</summary>
    private static double OffsetStepNow() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0 ? LargeOffsetStep : OffsetStep;

    /// <summary>Right-click a line as it is sung: shift this song's lyrics so that line is the one playing now.</summary>
    private void OnLineSync(object sender, MouseButtonEventArgs e)
    {
        if (_track is null || _shownTimeline is null) return;
        e.Handled = true;

        var line = _shownTimeline.Lines[(int)((Border)sender).Tag];
        var offset = (line.Time - _clock.Position).TotalSeconds + ReactionLeadSeconds;
        SetOffset(Math.Round(offset * 20) / 20); // nearest 0.05 s
        ShowHint("Synced to this line", $"Offset {CurrentOffset:+0.00;-0.00;0.00}s saved for this song", 3);
    }

    /// <summary>Mouse wheel browses the lyrics; the list stops following the song until told to resume.</summary>
    private void OnLyricsWheel(object sender, MouseWheelEventArgs e)
    {
        if (_shownTimeline is null || LinesPanel.Children.Count == 0 || _welcomeOpen) return;
        e.Handled = true;

        if (!_manualScroll)
        {
            _manualScroll = true;
            // Take over from wherever the list is (even mid-animation); OnFrame moves it from here on.
            var y = LinesShift.Y;
            LinesShift.BeginAnimation(TranslateTransform.YProperty, null);
            LinesShift.Y = _scrollTarget = y;
            StyleLines(_shownIndex, animate: true, new Duration(TimeSpan.FromMilliseconds(250)));
            ShowFollowPill(true);
        }

        // Wheel input only moves the target; the list glides toward it every frame, so rapid or
        // high-resolution (touchpad) scrolling stays fluid instead of restarting an animation per notch.
        var (min, max) = ScrollBounds();
        _scrollTarget = Math.Clamp(_scrollTarget + e.Delta / 120.0 * (LineHeightPx + LineGapPx) * 0.8, min, max);

        _resumeFollow.Stop();
        _resumeFollow.Start();
    }

    /// <summary>
    /// Browsing range: from the first line resting just below the header to the last line resting just
    /// above the scrubber. Browsing happens with the cursor over the window, so the header and scrubber
    /// are showing and would otherwise cover the ends. Short lyrics that fit entirely stay at the top.
    /// </summary>
    private (double Min, double Max) ScrollBounds()
    {
        var first = (FrameworkElement)LinesPanel.Children[0];
        var last = (FrameworkElement)LinesPanel.Children[LinesPanel.Children.Count - 1];
        var height = LyricsArea.ActualHeight;
        var fade = height * EdgeFade;
        var top = Math.Max(fade, CardPadding + HeaderRow.ActualHeight + 4);
        var bottom = fade; // the footer opens below the lyrics, so nothing covers the bottom edge

        var max = top - VisualTreeHelper.GetOffset(first).Y;
        var min = height - bottom - (VisualTreeHelper.GetOffset(last).Y + last.ActualHeight);
        return (Math.Min(min, max), max);
    }

    /// <summary>Glides back to the line being sung and follows the song again.</summary>
    private void ResumeFollow()
    {
        if (!_manualScroll) return;
        StopBrowsing();
        if (_shownTimeline is null) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(600));
        StyleLines(_shownIndex, animate: true, duration);
        ScrollTo(_shownIndex, animate: true, duration);
    }

    private void StopBrowsing()
    {
        _resumeFollow.Stop();
        if (!_manualScroll) return;
        _manualScroll = false;
        ShowFollowPill(false);
    }

    private void ShowFollowPill(bool visible)
    {
        var duration = TimeSpan.FromMilliseconds(visible ? 300 : 200);
        if (visible) FollowPill.Visibility = Visibility.Visible;

        var fade = new DoubleAnimation(visible ? 1 : 0, duration) { EasingFunction = Ease };
        if (!visible) fade.Completed += (_, _) => { if (!_manualScroll) FollowPill.Visibility = Visibility.Collapsed; };
        FollowPill.BeginAnimation(OpacityProperty, fade);
        FollowPillShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(visible ? 12 : 0, visible ? 0 : 8, duration) { EasingFunction = visible ? Spring : Ease });
    }

    private void OnFollowClick(object sender, RoutedEventArgs e) => ResumeFollow();

    private void OnOffsetReset(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetOffset(0);
        ShowHint("Sync offset reset", "Using the lyrics' own timing", 2);
    }

    /// <summary>Briefly replaces the header's song info with a short message.</summary>
    private void ShowHint(string title, string subtitle, double seconds)
    {
        _hint = (title, subtitle);
        _hintUntil = DateTime.Now.AddSeconds(seconds);
        Render();
    }

    /// <summary>
    /// Turns on the frosted desktop blur and Windows 11's rounded corners. The blur ignores window
    /// regions, but DWM's own corner rounding clips it cleanly.
    /// </summary>
    private void ApplyBackdropBlur()
    {
        if (_hwnd != 0 && !NativeMethods.EnableBackdropBlur(_hwnd)) Log.Info("Backdrop blur is not available on this system");
        if (_hwnd != 0 && !NativeMethods.RequestRoundedCorners(_hwnd)) Log.Info("Rounded corners are not available on this system");
        if (_hwnd != 0) NativeMethods.HideWindowBorder(_hwnd);
    }

    private void ApplyTransparency()
    {
        MaterialBrush.Opacity = MaterialOpacityTarget;
        ArtBackdrop.BeginAnimation(OpacityProperty, null);
        ArtBackdrop.Opacity = _hasArtwork ? BackdropOpacityTarget : 0;
        SetText(TransparencyText, $"{Math.Round(_settings.Transparency * 100)}%");
    }

    private void OnTransparencyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Sliders raise ValueChanged while the XAML loads (clamping to their Minimum); that must not overwrite settings.
        if (!IsInitialized) return;
        _settings.Transparency = e.NewValue / 100;
        ApplyTransparency();
        // Dragging fires many changes; write the file once the slider settles.
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    private void OnTextSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        _settings.TextSize = Math.Round(e.NewValue);
        ApplyTextSize();
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // Clicking the gear while the panel is open first closes it (StaysOpen=False reacts to any click
        // outside the panel); that same click must not open it again.
        if (DateTime.Now - _settingsClosedAt < TimeSpan.FromMilliseconds(300)) return;
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void OnSettingsClosed(object? sender, EventArgs e)
    {
        _settingsClosedAt = DateTime.Now;
        if (!IsMouseOver) OnMouseLeave(this, null!);
        else if (!HeaderRow.IsMouseOver) Toolbar.Visibility = Visibility.Collapsed;
    }

    // Playback and window controls appear only while the cursor is over the header; otherwise the header
    // shows just the song.
    private void OnHeaderEnter(object sender, MouseEventArgs e)
    {
        if (!ClickThrough) ShowToolbar();
    }

    private void OnHeaderLeave(object sender, MouseEventArgs e)
    {
        if (!SettingsPopup.IsOpen) Toolbar.Visibility = Visibility.Collapsed;
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Clip the artwork backdrop to the card's rounded corners with smooth (anti-aliased) edges.
        Root.Clip = new RectangleGeometry(new Rect(e.NewSize), CornerRadius, CornerRadius);
        // Only a resize by the user changes the card's height. Size events from the footer animation can
        // arrive after it finishes; treating those as resizes would fold the footer into the card.
        if (_userSizing && !_footerAnimating) UpdateCardLayout(e.NewSize.Height);
    }

    /// <summary>
    /// Pins the lyrics row and the artwork backdrop to fixed heights. Only resizing the window changes
    /// them; the hover footer opening and closing below never does, so the lyrics stay exactly put.
    /// </summary>
    private void UpdateCardLayout(double windowHeight)
    {
        _contentHeight = windowHeight - _footerExtra;
        PlaceCardContent();
        ArtBackdrop.Height = _contentHeight + _footerHeight;
    }

    /// <summary>The card content keeps a fixed height; the footer sits directly below it.</summary>
    private void PlaceCardContent()
    {
        var content = Math.Max(0, _contentHeight - CardContent.Margin.Top - CardContent.Margin.Bottom);
        if (Math.Abs(CardContent.Height - content) >= 0.5 || double.IsNaN(CardContent.Height)) CardContent.Height = content;
        ScrubberRow.Margin = new Thickness(16, _contentHeight + FooterGap, 16, 0);
    }

    private static string FormatTime(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private void ApplyWindowStyles()
    {
        if (_hwnd == 0) return;

        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        // Tool window: hidden from Alt+Tab. No-activate: clicking it never steals focus from your work.
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        style = ClickThrough ? style | NativeMethods.WS_EX_TRANSPARENT : style & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, style);
    }

    private void RegisterHotkey(int id, uint key)
    {
        const uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, modifiers, key))
            Log.Info($"Hotkey Ctrl+Alt+{(char)key} is already used by another app");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_ENTERSIZEMOVE:
                _userSizing = true;
                break;

            case NativeMethods.WM_EXITSIZEMOVE: // end of a drag or resize
                _userSizing = false;
                if (!_footerAnimating) UpdateCardLayout(Height);
                SaveBounds();
                ApplyBackdropBlur();
                break;

            case NativeMethods.WM_HOTKEY:
                if ((int)wParam == HotkeyClickThrough)
                {
                    ClickThrough = !ClickThrough;
                    _userHidden = false;
                    ShowAnimated();
                }
                else if ((int)wParam == HotkeyToggleVisibility)
                {
                    ToggleVisibility();
                }
                handled = true;
                break;
        }
        return 0;
    }

    private void SaveBounds()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = Width;
        _settings.Height = Height - _footerExtra; // remember the lyrics card, not the hover footer
        _settings.Save();
    }

    private static bool IsOnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 100 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top >= SystemParameters.VirtualScreenTop &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    private static IEasingFunction CreateSpring()
    {
        var spring = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        spring.Freeze();
        return spring;
    }

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        ease.Freeze();
        return ease;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        // The header sits over the lyrics list; take the press here so it never reaches a line below.
        e.Handled = true;
        DragMove();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (ClickThrough) return;
        ShowChrome(true);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (SettingsPopup.IsOpen) return;
        ShowChrome(false);
        Toolbar.Visibility = Visibility.Collapsed;
    }

    private void OnOffsetDown(object sender, RoutedEventArgs e) => ChangeOffset(-OffsetStepNow());

    private void OnOffsetUp(object sender, RoutedEventArgs e) => ChangeOffset(OffsetStepNow());

    private void OnLockClick(object sender, RoutedEventArgs e) => ClickThrough = true;

    private void OnUnlockClick(object sender, RoutedEventArgs e) => ClickThrough = false;

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryLyricsNow();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _watcher.TogglePlayPauseAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _watcher.NextAsync();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _watcher.PreviousAsync();

    private void OnShuffleClick(object sender, RoutedEventArgs e)
    {
        _shuffle = !(_shuffle ?? false);
        UpdateModeButtons(); // show the change at once; Spotify confirms on its next update
        _ = _watcher.SetShuffleAsync(_shuffle.Value);
    }

    /// <summary>Cycles repeat the way Spotify does: off, then the whole list, then the current song.</summary>
    private void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        _repeat = _repeat switch
        {
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.None,
            _ => MediaPlaybackAutoRepeatMode.List,
        };
        UpdateModeButtons();
        _ = _watcher.SetRepeatAsync(_repeat.Value);
    }

    /// <summary>Shuffle and repeat: green with a dot when on, dimmed and disabled when Spotify doesn't offer them.</summary>
    private void UpdateModeButtons()
    {
        var shuffleOn = _shuffle == true;
        ShuffleButton.IsEnabled = _canShuffle && _shuffle is not null;
        ShuffleButton.Foreground = (Brush)FindResource(shuffleOn ? "ModeOn" : "ModeOff");
        ShuffleDot.Visibility = shuffleOn ? Visibility.Visible : Visibility.Collapsed;
        SetTip(ShuffleButton, shuffleOn ? "Shuffle: on" : "Shuffle: off");

        var repeatOn = _repeat is MediaPlaybackAutoRepeatMode.List or MediaPlaybackAutoRepeatMode.Track;
        RepeatButton.IsEnabled = _canRepeat && _repeat is not null;
        RepeatButton.Foreground = (Brush)FindResource(repeatOn ? "ModeOn" : "ModeOff");
        RepeatDot.Visibility = repeatOn ? Visibility.Visible : Visibility.Collapsed;
        RepeatOneBadge.Visibility = _repeat == MediaPlaybackAutoRepeatMode.Track ? Visibility.Visible : Visibility.Collapsed;
        SetTip(RepeatButton, _repeat switch
        {
            MediaPlaybackAutoRepeatMode.List => "Repeat: all",
            MediaPlaybackAutoRepeatMode.Track => "Repeat: this song",
            _ => "Repeat: off",
        });
    }

    /// <summary>Gives a control a tooltip centred just above (or below) it, or updates the text of the one it has.</summary>
    private static void SetTip(FrameworkElement element, object? content, bool below = false)
    {
        if (element.ToolTip is ToolTip tip)
        {
            tip.Content = content;
            return;
        }
        element.ToolTip = new ToolTip
        {
            Content = content,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
                [new CustomPopupPlacement(
                    new Point((target.Width - popup.Width) / 2, below ? target.Height + 6 : -popup.Height - 6),
                    PopupPrimaryAxis.Horizontal)],
        };
    }

    private bool CanScrub => _canSeek && _track is not null && _clock.Duration > TimeSpan.Zero;

    private double ScrubFractionAt(MouseEventArgs e) =>
        Math.Clamp(e.GetPosition(ScrubArea).X / Math.Max(1, ScrubArea.ActualWidth), 0, 1);

    private void OnScrubEnter(object sender, MouseEventArgs e)
    {
        if (!CanScrub) return;
        ScrubArea.Cursor = Cursors.Hand;
        ShowScrubHover(true);
        UpdateScrubBubble(ScrubFractionAt(e));
    }

    private void OnScrubMove(object sender, MouseEventArgs e)
    {
        if (!CanScrub) return;
        var fraction = ScrubFractionAt(e);
        if (_scrubbing) _scrubFraction = fraction;
        UpdateScrubBubble(fraction);
    }

    private void OnScrubLeave(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) ShowScrubHover(false);
    }

    private void OnScrubPress(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // a press on the bar never drags the window
        if (!CanScrub) return;
        _scrubbing = true;
        _scrubFraction = ScrubFractionAt(e);
        ScrubArea.CaptureMouse();
    }

    private void OnScrubRelease(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        var target = _clock.Duration * _scrubFraction;
        _scrubbing = false;
        ScrubArea.ReleaseMouseCapture();
        SeekTo(target);
        if (!ScrubArea.IsMouseOver) ShowScrubHover(false);
    }

    private void OnScrubCaptureLost(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false; // capture taken away (e.g. focus change): cancel without seeking
        ShowScrubHover(false);
    }

    /// <summary>Hovering the bar turns its fill green and shows the knob and the time bubble.</summary>
    private void ShowScrubHover(bool on)
    {
        ScrubFill.Background = on ? (Brush)FindResource("ModeOn") : new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        var fade = TimeSpan.FromMilliseconds(on ? 120 : 200);
        ScrubThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, fade));
        ScrubBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, fade));
    }

    private void UpdateScrubBubble(double fraction)
    {
        SetText(ScrubBubbleText, FormatTime(_clock.Duration * fraction));
        ScrubBubble.UpdateLayout();
        var anchor = ScrubArea.TranslatePoint(new Point(fraction * ScrubArea.ActualWidth, 0), ScrubberRow);
        Canvas.SetLeft(ScrubBubble, anchor.X - ScrubBubble.ActualWidth / 2);
        Canvas.SetTop(ScrubBubble, anchor.Y - ScrubBubble.ActualHeight - 4);
    }

    // A press on a lyric line becomes a seek only if it is released without moving; moving past the
    // system drag threshold turns it into a window drag instead.
    private Border? _pressedLine;
    private Point _pressPoint;

    private void OnLinePress(object sender, MouseButtonEventArgs e)
    {
        _pressedLine = (Border)sender;
        _pressPoint = e.GetPosition(this);
        _pressedLine.CaptureMouse();
        e.Handled = true;
    }

    private void OnLineDragCheck(object sender, MouseEventArgs e)
    {
        if (_pressedLine is null || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pressedLine.ReleaseMouseCapture();
        _pressedLine = null;
        DragMove();
    }

    private void OnLineRelease(object sender, MouseButtonEventArgs e)
    {
        if (_pressedLine != sender) return;
        _pressedLine.ReleaseMouseCapture();
        _pressedLine = null;
        e.Handled = true;
        SeekToLine((int)((Border)sender).Tag);
    }

    private void OnLineEnter(object sender, MouseEventArgs e)
    {
        var item = (Border)sender;
        var seekable = LineIsSeekable((int)item.Tag);
        item.Cursor = seekable ? Cursors.Hand : null;
        if (seekable && item.Background is SolidColorBrush brush) AnimateColor(brush, Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 150);
    }

    private void OnLineLeave(object sender, MouseEventArgs e)
    {
        if (((Border)sender).Background is SolidColorBrush brush) AnimateColor(brush, Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 250);
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        _userHidden = true;
        HideAnimated();
    }
}
