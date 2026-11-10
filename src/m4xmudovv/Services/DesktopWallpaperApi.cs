using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace m4xmudovv.Services;

/// <summary>Result of asking Windows what wallpaper it now reports. Available=false means Windows could not be queried.</summary>
internal sealed record WallpaperVerification(bool Available, bool Matches, string Details);

internal sealed record MonitorInfo(string Id, int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Static wallpaper + monitor layout through the Windows IDesktopWallpaper COM API (Windows 8+),
/// which is the supported way to set wallpapers per monitor with a fit mode.
/// </summary>
internal static class DesktopWallpaperApi
{
    private enum Position
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Method order must match the native vtable exactly.
    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();

        NativeRect GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(Position position);
        Position GetPosition();
        void SetSlideshow(IntPtr items);
        IntPtr GetSlideshow();
        void SetSlideshowOptions(uint options, uint slideshowTick);
        void GetSlideshowOptions(out uint options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);
        int GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [ComImport]
    [Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperCoClass
    {
    }

    private static IDesktopWallpaper Create() => (IDesktopWallpaper)new DesktopWallpaperCoClass();

    private static void Release(IDesktopWallpaper? api)
    {
        if (api is not null) Marshal.ReleaseComObject(api);
    }

    private static Position ToPosition(WallpaperFitMode fit) => fit switch
    {
        WallpaperFitMode.Fit => Position.Fit,
        WallpaperFitMode.Stretch => Position.Stretch,
        WallpaperFitMode.Center => Position.Center,
        _ => Position.Fill
    };

    /// <summary>All attached monitors with their rectangles in virtual-screen (physical pixel) coordinates.</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        IDesktopWallpaper? api = null;
        try
        {
            api = Create();
            var count = api.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    var id = api.GetMonitorDevicePathAt(i);
                    if (string.IsNullOrEmpty(id)) continue;
                    var rect = api.GetMonitorRECT(id);
                    if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) continue;
                    monitors.Add(new MonitorInfo(id, rect.Left, rect.Top, rect.Right, rect.Bottom));
                }
                catch (COMException) { /* monitor not currently attached */ }
            }
        }
        catch (Exception) { /* fall through to the virtual-screen fallback */ }
        finally { Release(api); }

        if (monitors.Count == 0)
        {
            var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            if (w > 0 && h > 0) monitors.Add(new MonitorInfo("virtual-screen", x, y, x + w, y + h));
        }
        return monitors;
    }

    /// <summary>Sets the same image on every monitor with the given fit mode. Throws on failure.</summary>
    public static void SetImage(string path, WallpaperFitMode fit)
    {
        IDesktopWallpaper? api = null;
        try
        {
            api = Create();
            api.SetPosition(ToPosition(fit));
            api.SetWallpaper(null, path);   // null monitor id = all monitors
        }
        finally { Release(api); }
    }

    private static string Normalize(string? path)
    {
        try { return System.IO.Path.GetFullPath(path ?? string.Empty); }
        catch (Exception) { return path ?? string.Empty; }
    }

    /// <summary>
    /// Asks Windows (not our own bookkeeping) which wallpaper and position each monitor now has,
    /// so "success" is only reported when the change really took effect.
    /// </summary>
    public static WallpaperVerification Verify(string expectedPath, WallpaperFitMode fit)
    {
        IDesktopWallpaper? api = null;
        try
        {
            api = Create();
            var expected = Normalize(expectedPath);
            var count = api.GetMonitorDevicePathCount();
            var problems = new List<string>();
            var queried = 0;
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    var id = api.GetMonitorDevicePathAt(i);
                    if (string.IsNullOrEmpty(id)) continue;
                    var actual = api.GetWallpaper(id);
                    queried++;
                    AppLog.Info($"Verify: monitor '{id}' reports wallpaper '{actual}'");
                    if (!string.Equals(Normalize(actual), expected, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"monitor '{id}' reports '{actual}'");
                }
                catch (COMException ex)
                {
                    AppLog.Warn($"Verify: monitor index {i} could not be queried (HRESULT 0x{ex.HResult:X8}); skipped.");
                }
            }

            if (queried == 0) return new WallpaperVerification(false, false, "no attached monitor could be queried");

            var position = api.GetPosition();
            AppLog.Info($"Verify: position reports {position}, requested {ToPosition(fit)}");
            if (position != ToPosition(fit)) problems.Add($"position is {position} instead of {ToPosition(fit)}");

            return problems.Count == 0
                ? new WallpaperVerification(true, true, $"confirmed by Windows on {queried} monitor(s)")
                : new WallpaperVerification(true, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Verify: IDesktopWallpaper could not be queried.", ex);
            return new WallpaperVerification(false, false, ex.Message);
        }
        finally { Release(api); }
    }

    /// <summary>Fallback for systems where IDesktopWallpaper is unavailable: classic registry style + SystemParametersInfo.</summary>
    public static void SetImageLegacy(string path, WallpaperFitMode fit)
    {
        var (style, tile) = fit switch
        {
            WallpaperFitMode.Fit => ("6", "0"),
            WallpaperFitMode.Stretch => ("2", "0"),
            WallpaperFitMode.Center => ("0", "0"),   // WallpaperStyle 0 + TileWallpaper 0 = centred
            _ => ("10", "0")
        };
        using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true))
        {
            if (key is not null)
            {
                key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
                key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
            }
        }
        if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETDESKWALLPAPER, 0, path, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE))
            throw new InvalidOperationException($"SystemParametersInfo failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Re-applies each monitor's current wallpaper. Used after a video wallpaper is removed so the shell
    /// repaints the static wallpaper instead of leaving the spawned WorkerW layer blank.
    /// </summary>
    public static void ReapplyCurrent()
    {
        IDesktopWallpaper? api = null;
        try
        {
            api = Create();
            var count = api.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    var id = api.GetMonitorDevicePathAt(i);
                    if (string.IsNullOrEmpty(id)) continue;
                    var current = api.GetWallpaper(id);
                    if (!string.IsNullOrEmpty(current)) api.SetWallpaper(id, current);
                }
                catch (COMException) { }
            }
        }
        catch (Exception) { }
        finally { Release(api); }
    }
}
