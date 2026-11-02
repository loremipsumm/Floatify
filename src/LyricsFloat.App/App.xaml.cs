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
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\SpotifyLyricsFloat", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        var lyrics = new LyricsService(
            new LrcLibClient(LrcLibClient.CreateHttpClient()),
            new LyricsCache(AppSettings.LyricsCacheDirectory));

        _window = new MainWindow(settings, lyrics);
        _tray = new TrayIcon(_window, settings, ExitApp);
        _window.Show();
        Log.Info("Started");
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
