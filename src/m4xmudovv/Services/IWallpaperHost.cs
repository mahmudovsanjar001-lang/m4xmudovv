using m4xmudovv.Models;

namespace m4xmudovv.Services;

/// <summary>
/// Boundary between the UI and the Windows desktop. Rendering, monitor enumeration and Win32
/// details stay behind this interface so they never leak into the main window.
/// </summary>
public interface IWallpaperHost
{
    /// <summary>Applies the wallpaper to the real Windows desktop. Never throws for expected failures; inspect the result.</summary>
    Task<WallpaperApplyResult> ApplyAsync(WallpaperItem wallpaper, WallpaperApplyOptions options, CancellationToken cancellationToken = default);

    /// <summary>Pauses a running video wallpaper (no effect for static wallpapers).</summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops a running video wallpaper and returns the desktop to its static wallpaper.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
