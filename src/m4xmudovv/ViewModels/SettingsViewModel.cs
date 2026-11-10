using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using m4xmudovv.Services;

namespace m4xmudovv.ViewModels;

/// <summary>
/// Binding surface of the Settings page. Every property writes straight into <see cref="AppSettings"/>,
/// schedules a debounced save to settings.json, and raises <see cref="SettingChanged"/> (with the AppSettings
/// property name) so the main window can apply the change immediately.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly string[] SpeedTags = ["0.5", "0.75", "1", "1.25", "1.5", "2"];

    private readonly AppSettings _settings;
    private readonly SettingsService _service;
    private readonly WallpaperThumbnailService _thumbnails;
    private readonly Func<(int Total, int Images, int Videos)> _libraryStats;
    private string _cacheInfoText = "Calculating…";
    private string _libraryCountText = "0 wallpapers";
    private string _updateStatusText = "Check whether a newer version of m4xmudovv is available.";

    public SettingsViewModel(AppSettings settings, SettingsService service, WallpaperThumbnailService thumbnails,
                             Func<(int Total, int Images, int Videos)> libraryStats)
    {
        _settings = settings;
        _service = service;
        _thumbnails = thumbnails;
        _libraryStats = libraryStats;
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
    }

    /// <summary>Raised after a setting changed from the Settings page. The argument is the AppSettings property name.</summary>
    public event Action<string>? SettingChanged;

    /// <summary>Raised when an operation needs to tell the user something in a dialog (e.g. the startup entry failed).</summary>
    public event Action<string>? ErrorRaised;

    public ICommand ClearCacheCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }

    // ---------------- General ----------------
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            var result = StartupRegistration.SetEnabled(value);
            if (!result.Success)
            {
                // Windows did not accept the change: the saved value follows reality (never "on" after a failure),
                // the user is told why, and the switch snaps back once the binding has finished updating.
                _settings.StartWithWindows = result.State.IsEnabled;
                _service.ScheduleSave();
                ErrorRaised?.Invoke($"Could not turn “Start with Windows” {(value ? "on" : "off")}.\n\n{result.Message}");
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnPropertyChanged(nameof(StartWithWindows))));
                return;
            }
            // Store what Windows reports, not what was asked for.
            Commit(nameof(AppSettings.StartWithWindows), () => _settings.StartWithWindows = result.State.IsEnabled, nameof(StartWithWindows));
        }
    }

    /// <summary>
    /// Re-reads the startup state from Windows. The user can change it outside the app (Task Manager, Settings >
    /// Startup apps), so the switch follows Windows whenever the Settings page is shown or the window is activated.
    /// </summary>
    public void RefreshStartupState()
    {
        var actual = StartupRegistration.Synchronize().IsEnabled;
        if (_settings.StartWithWindows == actual) return;
        _settings.StartWithWindows = actual;
        _service.ScheduleSave();
        OnPropertyChanged(nameof(StartWithWindows));
    }

    public bool StartMinimizedToTray
    {
        get => _settings.StartMinimizedToTray;
        set => Commit(nameof(AppSettings.StartMinimizedToTray), () => _settings.StartMinimizedToTray = value, nameof(StartMinimizedToTray));
    }

    public bool MinimizeToTrayOnClose
    {
        get => _settings.MinimizeToTrayOnClose;
        set => Commit(nameof(AppSettings.MinimizeToTrayOnClose), () => _settings.MinimizeToTrayOnClose = value, nameof(MinimizeToTrayOnClose));
    }

    public bool RememberWindowBounds
    {
        get => _settings.RememberWindowBounds;
        set => Commit(nameof(AppSettings.RememberWindowBounds), () => _settings.RememberWindowBounds = value, nameof(RememberWindowBounds));
    }

    public bool RestoreAppState
    {
        get => _settings.RestoreAppState;
        set => Commit(nameof(AppSettings.RestoreAppState), () => _settings.RestoreAppState = value, nameof(RestoreAppState));
    }

    // ---------------- Performance ----------------
    public bool PausePreviewsWhenMinimized
    {
        get => _settings.PausePreviewsWhenMinimized;
        set => Commit(nameof(AppSettings.PausePreviewsWhenMinimized), () => _settings.PausePreviewsWhenMinimized = value, nameof(PausePreviewsWhenMinimized));
    }

    public bool PausePreviewsWhenInactive
    {
        get => _settings.PausePreviewsWhenInactive;
        set => Commit(nameof(AppSettings.PausePreviewsWhenInactive), () => _settings.PausePreviewsWhenInactive = value, nameof(PausePreviewsWhenInactive));
    }

    public bool HoverPreviewEnabled
    {
        get => _settings.HoverPreviewEnabled;
        set => Commit(nameof(AppSettings.HoverPreviewEnabled), () => _settings.HoverPreviewEnabled = value, nameof(HoverPreviewEnabled));
    }

    public int HoverDelayMs
    {
        get => _settings.HoverPreviewDelayMs;
        set
        {
            var clamped = Math.Clamp(value, 0, 2000);
            Commit(nameof(AppSettings.HoverPreviewDelayMs), () => _settings.HoverPreviewDelayMs = clamped, nameof(HoverDelayMs), nameof(HoverDelayText));
        }
    }

    public string HoverDelayText => _settings.HoverPreviewDelayMs == 0 ? "Instant" : $"{_settings.HoverPreviewDelayMs} ms";

    public bool ReduceBackgroundActivityWhenIdle
    {
        get => _settings.ReduceBackgroundActivityWhenIdle;
        set => Commit(nameof(AppSettings.ReduceBackgroundActivityWhenIdle), () => _settings.ReduceBackgroundActivityWhenIdle = value, nameof(ReduceBackgroundActivityWhenIdle));
    }

    // ---------------- Appearance ----------------
    /// <summary>0 = System default, 1 = Dark, 2 = Light (same order as the dropdown).</summary>
    public int ThemeIndex
    {
        get => (int)_settings.Theme;
        set
        {
            if (!Enum.IsDefined(typeof(AppTheme), value)) return;
            Commit(nameof(AppSettings.Theme), () => _settings.Theme = (AppTheme)value, nameof(ThemeIndex));
        }
    }

    // ---------------- Playback ----------------
    public double DefaultVolumePercent
    {
        get => Math.Round(_settings.DefaultVolume * 100);
        set
        {
            var volume = Math.Clamp(value, 0, 100) / 100.0;
            Commit(nameof(AppSettings.DefaultVolume), () => _settings.DefaultVolume = volume, nameof(DefaultVolumePercent), nameof(DefaultVolumeText));
        }
    }

    public string DefaultVolumeText => $"{Math.Round(_settings.DefaultVolume * 100)}%";

    /// <summary>Invariant text of the speed ("0.5", "1", "1.25" …) so it can be matched against the dropdown items' Tag.</summary>
    public string DefaultSpeedTag
    {
        get => _settings.DefaultSpeed.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)) return;
            Commit(nameof(AppSettings.DefaultSpeed), () => _settings.DefaultSpeed = Math.Clamp(speed, 0.25, 4), nameof(DefaultSpeedTag));
        }
    }

    public bool MutePreviewsByDefault
    {
        get => _settings.MutePreviewsByDefault;
        set => Commit(nameof(AppSettings.MutePreviewsByDefault), () => _settings.MutePreviewsByDefault = value, nameof(MutePreviewsByDefault));
    }

    public bool DesktopWallpaperSoundEnabled
    {
        get => _settings.DesktopWallpaperSoundEnabled;
        set => Commit(nameof(AppSettings.DesktopWallpaperSoundEnabled), () => _settings.DesktopWallpaperSoundEnabled = value, nameof(DesktopWallpaperSoundEnabled));
    }

    public bool ApplyToLockScreen
    {
        get => _settings.ApplyToLockScreen;
        set => Commit(nameof(AppSettings.ApplyToLockScreen), () => _settings.ApplyToLockScreen = value, nameof(ApplyToLockScreen));
    }

    public bool LoopVideoPreviews
    {
        get => _settings.LoopVideoPreviews;
        set => Commit(nameof(AppSettings.LoopVideoPreviews), () => _settings.LoopVideoPreviews = value, nameof(LoopVideoPreviews));
    }

    // ---------------- Library ----------------
    public bool RememberLastCategory
    {
        get => _settings.RememberLastCategory;
        set => Commit(nameof(AppSettings.RememberLastCategory), () => _settings.RememberLastCategory = value, nameof(RememberLastCategory));
    }

    /// <summary>0 = Low, 1 = Balanced, 2 = High (same order as the dropdown).</summary>
    public int ThumbnailQualityIndex
    {
        get => (int)_settings.ThumbnailQuality;
        set
        {
            if (!Enum.IsDefined(typeof(ThumbnailQuality), value)) return;
            Commit(nameof(AppSettings.ThumbnailQuality), () => _settings.ThumbnailQuality = (ThumbnailQuality)value, nameof(ThumbnailQualityIndex));
        }
    }

    public string CacheInfoText
    {
        get => _cacheInfoText;
        private set => SetField(ref _cacheInfoText, value);
    }

    public string LibraryCountText
    {
        get => _libraryCountText;
        private set => SetField(ref _libraryCountText, value);
    }

    // ---------------- About ----------------
    public string AppName => "m4xmudovv";

    public Version AppVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    public string VersionText => $"{AppVersion.Major}.{AppVersion.Minor}.{AppVersion.Build}";

    public string ArchitectureText =>
        $"{RuntimeInformation.OSDescription.Trim()} • system {RuntimeInformation.OSArchitecture} • app {RuntimeInformation.ProcessArchitecture}";

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetField(ref _updateStatusText, value);
    }

    public string LicenseText =>
        "No license file is bundled with this build. Until the author adds one, all rights remain with the author.";

    public string AcknowledgementsText =>
        ".NET 8, Windows Presentation Foundation and Windows Forms (used only for the tray icon) are © Microsoft and released under the MIT License. " +
        "Icons come from Segoe Fluent Icons / Segoe MDL2 Assets, which are Windows system fonts and are not bundled. " +
        "No other third-party packages are used.";

    // ---------------- Refresh helpers (called by the main window) ----------------
    /// <summary>Recomputes the cache size and library counts; call when the Settings page opens or the library changes.</summary>
    public void Refresh()
    {
        RefreshStartupState();
        RefreshLibrary();
        RefreshCache();
    }

    public void RefreshLibrary()
    {
        var (total, images, videos) = _libraryStats();
        LibraryCountText = $"{total} wallpaper{(total == 1 ? string.Empty : "s")} • {images} image{(images == 1 ? string.Empty : "s")} • {videos} video{(videos == 1 ? string.Empty : "s")}";
    }

    public void RefreshCache()
    {
        var (files, bytes) = _thumbnails.GetCacheStats();
        CacheInfoText = files == 0
            ? "The thumbnail cache is empty."
            : $"{FormatBytes(bytes)} in {files} file{(files == 1 ? string.Empty : "s")}. Cached thumbnails make the library open quickly.";
    }

    /// <summary>The main window changed a setting itself (e.g. the Volume slider): refresh the page and save, without re-applying it.</summary>
    public void NotifyChangedExternally(string settingName)
    {
        switch (settingName)
        {
            case nameof(AppSettings.DefaultVolume):
                OnPropertyChanged(nameof(DefaultVolumePercent));
                OnPropertyChanged(nameof(DefaultVolumeText));
                break;
            case nameof(AppSettings.DefaultSpeed):
                OnPropertyChanged(nameof(DefaultSpeedTag));
                break;
        }
        _service.ScheduleSave();
    }

    // ---------------- Commands ----------------
    private async System.Threading.Tasks.Task ClearCacheAsync()
    {
        CacheInfoText = "Clearing…";
        var (files, bytes) = await System.Threading.Tasks.Task.Run(() => _thumbnails.ClearCache());
        CacheInfoText = files == 0
            ? "The thumbnail cache was already empty."
            : $"Cleared {files} file{(files == 1 ? string.Empty : "s")} ({FormatBytes(bytes)}). Thumbnails already on screen stay visible; they are recreated when needed.";
        AppLog.Info($"Thumbnail cache cleared: {files} files, {bytes} bytes.");
    }

    private async System.Threading.Tasks.Task CheckForUpdatesAsync()
    {
        UpdateStatusText = "Checking for updates…";
        try
        {
            UpdateStatusText = await UpdateChecker.CheckAsync(_settings.UpdateFeedUrl, AppVersion);
        }
        catch (Exception ex)
        {
            UpdateStatusText = "The update check failed: " + ex.Message;
        }
    }

    // ---------------- Plumbing ----------------
    private void Commit(string settingName, Action write, params string[] changedProperties)
    {
        write();
        foreach (var property in changedProperties) OnPropertyChanged(property);
        _service.ScheduleSave();
        SettingChanged?.Invoke(settingName);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024d):0} KB";
}
