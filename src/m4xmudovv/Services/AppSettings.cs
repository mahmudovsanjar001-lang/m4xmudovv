namespace m4xmudovv.Services;

public enum AppTheme { System, Dark, Light }

public enum ThumbnailQuality { Low, Balanced, High }

public enum LibraryCategory { All, Images, Videos }

/// <summary>Persisted user preferences and remembered application state (settings.json).</summary>
public sealed class AppSettings
{
    // ---- General ----
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool MinimizeToTrayOnClose { get; set; }
    public bool RememberWindowBounds { get; set; } = true;
    public bool RestoreAppState { get; set; } = true;

    // ---- Performance ----
    public bool PausePreviewsWhenMinimized { get; set; } = true;
    public bool PausePreviewsWhenInactive { get; set; }
    public bool HoverPreviewEnabled { get; set; } = true;
    public int HoverPreviewDelayMs { get; set; } = 220;
    public bool ReduceBackgroundActivityWhenIdle { get; set; } = true;

    // ---- Appearance (Dark is the approved default look) ----
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // ---- Playback ----
    public double DefaultVolume { get; set; } = 0.3;
    public double DefaultSpeed { get; set; } = 1.0;
    public bool LoopVideoPreviews { get; set; } = true;
    public bool MutePreviewsByDefault { get; set; }
    public bool DesktopWallpaperSoundEnabled { get; set; } = true;
    public bool ApplyToLockScreen { get; set; }

    /// <summary>The lock screen was set through the administrator (registry) fallback and can be handed back to Windows.</summary>
    public bool LockScreenPolicyApplied { get; set; }

    // ---- Library ----
    public bool RememberLastCategory { get; set; } = true;
    public LibraryCategory LastCategory { get; set; } = LibraryCategory.All;
    public ThumbnailQuality ThumbnailQuality { get; set; } = ThumbnailQuality.Balanced;

    // ---- Remembered state ----
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string? LastSelectedPath { get; set; }

    /// <summary>Version of the bundled wallpaper set that has already been added to the library (0 = none yet).</summary>
    public int BundledWallpaperSet { get; set; }
    public string LastFitMode { get; set; } = "Fill";
    public string? LastAppliedWallpaperPath { get; set; }
    public string LastAppliedFitMode { get; set; } = "Fill";
    public double LastAppliedSpeed { get; set; } = 1.0;

    /// <summary>Optional https URL of a JSON update feed: {"version":"1.2.0","url":"https://..."}. Empty = no update source.</summary>
    public string UpdateFeedUrl { get; set; } = string.Empty;

    /// <summary>Clamps values that could have been edited by hand to safe ranges.</summary>
    public void Sanitize()
    {
        DefaultVolume = double.IsNaN(DefaultVolume) ? 0.3 : Math.Clamp(DefaultVolume, 0, 1);
        DefaultSpeed = double.IsNaN(DefaultSpeed) ? 1.0 : Math.Clamp(DefaultSpeed, 0.25, 4);
        HoverPreviewDelayMs = Math.Clamp(HoverPreviewDelayMs, 0, 2000);
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.Dark;
        if (!Enum.IsDefined(ThumbnailQuality)) ThumbnailQuality = ThumbnailQuality.Balanced;
        if (!Enum.IsDefined(LastCategory)) LastCategory = LibraryCategory.All;
        if (WindowWidth < 0 || double.IsNaN(WindowWidth)) WindowWidth = 0;
        if (WindowHeight < 0 || double.IsNaN(WindowHeight)) WindowHeight = 0;
        LastFitMode = LastFitMode is "Fit" or "Stretch" or "Center" ? LastFitMode : "Fill";
        LastAppliedFitMode = LastAppliedFitMode is "Fit" or "Stretch" or "Center" ? LastAppliedFitMode : "Fill";
        LastAppliedSpeed = double.IsNaN(LastAppliedSpeed) ? 1.0 : Math.Clamp(LastAppliedSpeed, 0.25, 4);
        UpdateFeedUrl ??= string.Empty;
    }
}
