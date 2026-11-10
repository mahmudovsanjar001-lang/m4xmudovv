namespace m4xmudovv.Services;

/// <summary>How a wallpaper is fitted to a monitor. Matches the "Fit mode" dropdown (Fill / Fit / Stretch / Center).</summary>
public enum WallpaperFitMode
{
    Fill,     // scale to cover the monitor, cropping the overflow
    Fit,      // scale to fit inside the monitor, letterboxing
    Stretch,  // stretch to the monitor, ignoring the aspect ratio
    Center    // original size (1:1 pixels), centred on the monitor
}

/// <param name="FitMode">Selected fit mode.</param>
/// <param name="SpeedRatio">Playback speed for video wallpapers (1.0 = normal).</param>
/// <param name="Volume">Video wallpaper volume 0..1. Wallpapers are muted by default (0).</param>
public sealed record WallpaperApplyOptions(
    WallpaperFitMode FitMode = WallpaperFitMode.Fill,
    double SpeedRatio = 1.0,
    double Volume = 0.0)
{
    public static WallpaperFitMode ParseFitMode(string? tag) => tag switch
    {
        "Fit" => WallpaperFitMode.Fit,
        "Stretch" => WallpaperFitMode.Stretch,
        "Center" => WallpaperFitMode.Center,
        _ => WallpaperFitMode.Fill
    };
}

public sealed record WallpaperApplyResult(bool Success, string Message)
{
    public static WallpaperApplyResult Ok(string message) => new(true, message);
    public static WallpaperApplyResult Fail(string message) => new(false, message);

    /// <summary>Failure that is written to the diagnostic log and points the user to it.</summary>
    public static WallpaperApplyResult FailWithLog(string message)
    {
        AppLog.Error("Apply failed: " + message);
        return new(false, message + "\n\nDiagnostic log:\n" + AppLog.LogPath);
    }
}
