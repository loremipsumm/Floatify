using System.Windows;
using System.Windows.Threading;
using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private MainWindow? _window;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppSettings.MigrateLegacyData(); // before anything (including the log) touches the data folder
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\Floatify", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        AutoStart.MigrateLegacyEntry();
        var background = e.Args.Contains(AutoStart.BackgroundFlag, StringComparer.OrdinalIgnoreCase);
        var lyrics = new LyricsService(
            new LrcLibClient(LrcLibClient.CreateHttpClient()),
            new LyricsCache(AppSettings.LyricsCacheDirectory));

        _window = new MainWindow(settings, lyrics);
        _tray = new TrayIcon(_window, settings, ExitApp);
        // Started with Windows: stay out of sight until Spotify plays (the welcome card still shows on first run).
        _window.Start(showNow: !background || !settings.OnboardingDone);
        Log.Info(background ? "Started in the background" : "Started");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ExitApp()
    {
        _window?.CloseForExit();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An overlay that silently disappears is worse than one that skips a beat.
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }
}
