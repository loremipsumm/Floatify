using System.ComponentModel;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LyricsFloat.Core;
using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.App;

public partial class MainWindow : Window
{
    public const double UnlockPillWidth = 112;
    public const double UnlockPillHeight = 28;

    private const int HotkeyClickThrough = 1;
    private const int HotkeyToggleVisibility = 2;
    private const double OffsetStep = 0.25;
    private const double DefaultWidth = 560;
    private const double DefaultHeight = 165;

    // The lyric rows are 1.05 : 2.5 : 1.05 (see XAML); the font is sized so they fill the window height.
    private const double LyricsHeightInLines = 4.6;
    private const double SideScale = 0.72;
    private const double SideOpacity = 0.5;
    private const double MessageOpacity = 0.75;

    private static readonly IEasingFunction Ease = CreateEase();

    private readonly AppSettings _settings;
    private readonly LyricsService _lyrics;
    private readonly SpotifyWatcher _watcher = new();
    private readonly PlaybackClock _clock = new();
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly AnimatedLine _ghost, _prev, _current, _next;

    private nint _hwnd;
    private bool _allowClose;
    private bool _spotifyFound;
    private bool _isPlaying;
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
    private int _transition;

    private DateTime _cursorLastInside;
    private DateTime _lockHintUntil;

    public MainWindow(AppSettings settings, LyricsService lyrics)
    {
        _settings = settings;
        _lyrics = lyrics;
        InitializeComponent();

        _ghost = new AnimatedLine(GhostLine);
        _prev = new AnimatedLine(PrevLine);
        _current = new AnimatedLine(CurrentLine);
        _next = new AnimatedLine(NextLine);

        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);
        if (settings.Left is { } left && settings.Top is { } top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        ApplyTint();
        LockIndicator.Visibility = settings.ClickThrough ? Visibility.Visible : Visibility.Collapsed;

        _renderTimer.Tick += (_, _) => Render();
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
            LockIndicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _lockHintUntil = value ? DateTime.Now.AddSeconds(4) : default;
            Render();
        }
    }

    public double GlassTint
    {
        get => _settings.GlassTint;
        set
        {
            _settings.GlassTint = value;
            _settings.Save();
            ApplyTint();
        }
    }

    private double CurrentOffset => _track is null ? 0 : _settings.TrackOffsets.GetValueOrDefault(_track.Key);

    public void ToggleVisibility()
    {
        if (IsVisible) Hide();
        else Show();
    }

    public void ResetPosition()
    {
        var area = SystemParameters.WorkArea;
        Width = DefaultWidth;
        Height = DefaultHeight;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 80;
        SaveBounds();
        Show();
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
        if (!NativeMethods.EnableBackdropBlur(_hwnd)) Log.Info("Backdrop blur is not available on this system");
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Left is null || !IsOnScreen(Left, Top)) ResetPosition();

        _renderTimer.Start();
        Render();

        try
        {
            await _watcher.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the media session watcher", ex);
        }
    }

    private void OnSnapshot(PlaybackSnapshot snapshot)
    {
        _spotifyFound = snapshot.SpotifyFound;
        _isPlaying = snapshot.IsPlaying;

        if (snapshot.Track != _track)
        {
            _track = snapshot.Track;
            _timeline = null;
            _clock.Reset();
            TrackText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = Ease });
            _ = LoadLyricsAsync(_track);
        }

        if (_track is not null)
            _clock.Update(snapshot.Position, snapshot.SampledAt, snapshot.IsPlaying, snapshot.Duration);

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
        _status = "Memuat lirik…";

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
                    _status = _timeline is not null ? "" : entry.Instrumental ? "♪  Instrumental  ♪" : "Lirik tidak ditemukan";
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

                    _status = ex is HttpRequestException { StatusCode: not null } ? "Server lirik sedang sibuk" : "Koneksi internet terputus";
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
            _status = "Gagal memuat lirik";
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
            ? $"Mencoba lagi dalam {Math.Max(1, Math.Ceiling((at - DateTime.Now).TotalSeconds)):0} dtk"
            : "Menghubungkan ulang…");
        RetryButton.Visibility = _retryAt is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render()
    {
        UpdateUnlockPopup();
        UpdateRetryPanel();
        SetText(TrackText, BuildHeader());
        SetText(OffsetText, CurrentOffset.ToString("+0.00;-0.00;0.00") + "s");

        if (!_spotifyFound) ShowMessage("Buka Spotify untuk menampilkan lirik");
        else if (_track is null) ShowMessage("Menunggu lagu…");
        else if (_timeline is null) ShowMessage(_status);
        else ShowLyrics(_timeline, _timeline.IndexAt(_clock.Position + TimeSpan.FromSeconds(CurrentOffset)));
    }

    private string BuildHeader()
    {
        if (DateTime.Now < _lockHintUntil) return "Terkunci  ·  arahkan kursor ke lirik untuk membuka";
        if (!_spotifyFound) return "Spotify Lyrics Float";
        if (_track is null) return "Spotify";

        return $"{(_isPlaying ? "" : "Dijeda  ·  ")}{_track.Title}  —  {_track.Artist}" +
               (_timeline?.IsApproximate == true ? "  ·  tanpa sinkron" : "");
    }

    private void ShowMessage(string message)
    {
        if (_shownTimeline is null && _shownMessage == message) return;

        _shownTimeline = null;
        _shownIndex = int.MinValue;
        _shownMessage = message;
        FadeTo("", message, "", MessageOpacity);
    }

    private void ShowLyrics(LyricsTimeline timeline, int index)
    {
        var sameTimeline = ReferenceEquals(timeline, _shownTimeline);
        if (sameTimeline && index == _shownIndex) return;

        var advancedOneLine = sameTimeline && index == _shownIndex + 1;
        _shownTimeline = timeline;
        _shownIndex = index;
        _shownMessage = null;

        var prev = LineText(timeline, index - 1);
        var current = index < 0 ? "♪" : LineText(timeline, index);
        var next = LineText(timeline, index + 1);

        // Normal playback scrolls; seeks, track changes and the first display fade instead.
        if (advancedOneLine) Scroll(prev, current, next, ScrollDuration(timeline, index));
        else FadeTo(prev, current, next, 1);
    }

    private static string LineText(LyricsTimeline timeline, int index)
    {
        if (index < 0 || index >= timeline.Lines.Count) return "";
        var text = timeline.Lines[index].Text;
        return text.Length == 0 ? "♪" : text; // empty LRC lines mark instrumental breaks
    }

    private static Duration ScrollDuration(LyricsTimeline timeline, int index)
    {
        // Glide unhurried, but settle before a quick next line arrives.
        var gap = index + 1 < timeline.Lines.Count
            ? timeline.Lines[index + 1].Time - timeline.Lines[index].Time
            : TimeSpan.FromSeconds(2);
        return TimeSpan.FromMilliseconds(Math.Clamp(gap.TotalMilliseconds * 0.5, 250, 650));
    }

    /// <summary>
    /// Moves every line up one row: the current line shrinks into the previous row, the next line
    /// grows into the current row, a new line rises from below and the old previous line drifts out.
    /// </summary>
    private void Scroll(string prev, string current, string next, Duration duration)
    {
        _transition++;
        var rows = LyricsArea.RowDefinitions;
        var upperStep = (rows[0].ActualHeight + rows[1].ActualHeight) / 2; // previous row centre → current row centre
        var lowerStep = (rows[1].ActualHeight + rows[2].ActualHeight) / 2; // current row centre → next row centre

        SetText(GhostLine, PrevLine.Text);
        SetLines(prev, current, next);

        _ghost.Animate(fromY: 0, toY: -upperStep, fromScale: 1, fromOpacity: SideOpacity, toOpacity: 0, duration);
        _prev.Animate(fromY: upperStep, toY: 0, fromScale: 1 / SideScale, fromOpacity: 1, toOpacity: SideOpacity, duration);
        _current.Animate(fromY: lowerStep, toY: 0, fromScale: SideScale, fromOpacity: SideOpacity, toOpacity: 1, duration);
        _next.Animate(fromY: lowerStep, toY: 0, fromScale: 1, fromOpacity: 0, toOpacity: SideOpacity, duration);
    }

    /// <summary>Fades the visible lines out, swaps the text, then fades the new lines in.</summary>
    private void FadeTo(string prev, string current, string next, double currentOpacity)
    {
        var transition = ++_transition;
        var fadeOut = new Duration(TimeSpan.FromMilliseconds(180));
        var fadeIn = new Duration(TimeSpan.FromMilliseconds(420));

        var lastFade = new DoubleAnimation(0, fadeOut) { EasingFunction = Ease };
        lastFade.Completed += (_, _) =>
        {
            if (transition != _transition) return; // superseded by a newer change

            SetLines(prev, current, next);
            _prev.Animate(fromY: 10, toY: 0, fromScale: 1, fromOpacity: 0, toOpacity: SideOpacity, fadeIn);
            _current.Animate(fromY: 10, toY: 0, fromScale: 1, fromOpacity: 0, toOpacity: currentOpacity, fadeIn);
            _next.Animate(fromY: 10, toY: 0, fromScale: 1, fromOpacity: 0, toOpacity: SideOpacity, fadeIn);
        };

        foreach (var line in new[] { _ghost, _prev, _next })
            line.Block.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fadeOut) { EasingFunction = Ease });
        _current.Block.BeginAnimation(OpacityProperty, lastFade);
    }

    private void SetLines(string prev, string current, string next)
    {
        SetText(PrevLine, prev);
        SetText(CurrentLine, current);
        SetText(NextLine, next);
    }

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }

    private void OnLyricsAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var size = Math.Clamp(e.NewSize.Height / LyricsHeightInLines, 10, 96);
        CurrentLine.FontSize = size;
        CurrentLine.LineHeight = Math.Round(size * 1.2);
        CurrentLine.MaxHeight = CurrentLine.LineHeight * 2 + 2;
        GhostLine.FontSize = PrevLine.FontSize = NextLine.FontSize = Math.Round(size * SideScale);
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

    private void ChangeOffset(double delta)
    {
        if (_track is null) return;

        var value = Math.Round(CurrentOffset + delta, 2);
        if (Math.Abs(value) < 0.001) _settings.TrackOffsets.Remove(_track.Key);
        else _settings.TrackOffsets[_track.Key] = value;

        _settings.Save();
        Render();
    }

    private void ApplyTint() => TintBrush.Opacity = _settings.GlassTint;

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
            case NativeMethods.WM_EXITSIZEMOVE: // end of a drag or resize
                SaveBounds();
                break;

            case NativeMethods.WM_HOTKEY:
                if ((int)wParam == HotkeyClickThrough)
                {
                    ClickThrough = !ClickThrough;
                    Show();
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
        _settings.Height = Height;
        _settings.Save();
    }

    private static bool IsOnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 100 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top >= SystemParameters.VirtualScreenTop &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        ease.Freeze();
        return ease;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (ClickThrough) return;
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => Toolbar.Visibility = Visibility.Collapsed;

    private void OnOffsetDown(object sender, RoutedEventArgs e) => ChangeOffset(-OffsetStep);

    private void OnOffsetUp(object sender, RoutedEventArgs e) => ChangeOffset(OffsetStep);

    private void OnLockClick(object sender, RoutedEventArgs e) => ClickThrough = true;

    private void OnUnlockClick(object sender, RoutedEventArgs e) => ClickThrough = false;

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryLyricsNow();

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    /// <summary>A lyric line with the transforms the scroll animation drives.</summary>
    private sealed class AnimatedLine
    {
        private readonly ScaleTransform _scale = new();
        private readonly TranslateTransform _shift = new();

        public AnimatedLine(TextBlock block)
        {
            Block = block;
            block.RenderTransformOrigin = new Point(0.5, 0.5);
            block.RenderTransform = new TransformGroup { Children = { _scale, _shift } };
        }

        public TextBlock Block { get; }

        public void Animate(double fromY, double toY, double fromScale, double fromOpacity, double toOpacity, Duration duration)
        {
            _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, toY, duration) { EasingFunction = Ease });
            var scale = new DoubleAnimation(fromScale, 1, duration) { EasingFunction = Ease };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            Block.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = Ease });
        }
    }
}
