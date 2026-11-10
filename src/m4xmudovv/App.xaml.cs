using System;
using System.Windows;
using m4xmudovv.Services;

namespace m4xmudovv;

public partial class App : Application
{
    private SingleInstance? _instance;
    private SettingsService? _settingsService;
    private ThemeService? _themeService;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper mode (lock-screen registry fallback): no UI, no single-instance check, just do the job and exit.
        if (e.Args.Length > 0 && (e.Args[0].Equals(LockScreenWallpaperService.SetCommand, StringComparison.OrdinalIgnoreCase) ||
                                  e.Args[0].Equals(LockScreenWallpaperService.ClearCommand, StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(LockScreenWallpaperService.RunElevatedCommand(e.Args));
            return;
        }

        // Only one copy may run: a second launch just asks the first one to show itself (it may be in the tray).
        _instance = new SingleInstance("m4xmudovv");
        if (!_instance.IsFirst)
        {
            _instance.SignalFirstInstance();
            Shutdown();
            return;
        }

        // Video wallpaper windows are extra top-level windows, and the main window may stay hidden in the tray:
        // the app lives and dies with the main window, not with "the last window".
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        _settingsService = new SettingsService();
        var settings = _settingsService.Settings;

        // Theme first, so the window never flashes in the wrong colours.
        _themeService = new ThemeService();
        _themeService.Apply(settings.Theme);

        // Windows is the source of truth for "Start with Windows": if the entry was removed or disabled outside the app
        // (Task Manager, Settings > Startup apps) the saved value follows it, and a stale path is repaired.
        var startup = StartupRegistration.Synchronize();
        if (settings.StartWithWindows != startup.IsEnabled)
        {
            settings.StartWithWindows = startup.IsEnabled;
            _settingsService.ScheduleSave();
        }

        var startedFromWindows = e.Args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        _mainWindow = new MainWindow(_settingsService, _themeService, startedFromWindows);
        MainWindow = _mainWindow;
        _instance.ListenForSignal(() => Dispatcher.BeginInvoke(new Action(() => _mainWindow?.ShowFromTray())));

        SessionEnding += (_, _) => _mainWindow?.PrepareForExit();   // never block a Windows sign-out by hiding to the tray

        if (settings.StartMinimizedToTray || startedFromWindows) _mainWindow.StartInTray();
        else _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.Cleanup();
        _themeService?.Dispose();
        _settingsService?.Flush();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
