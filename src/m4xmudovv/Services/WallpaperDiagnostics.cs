using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace m4xmudovv.Services;

/// <summary>Environment, file and policy checks whose results go to the log and into error messages.</summary>
internal static class WallpaperDiagnostics
{
    private static bool _environmentLogged;

    // Win32 cloud-placeholder attribute bits (winnt.h). The .NET FileAttributes enum does not define them.
    private const int FileAttributeRecallOnOpen = 0x00040000;         // FILE_ATTRIBUTE_RECALL_ON_OPEN
    private const int FileAttributeRecallOnDataAccess = 0x00400000;   // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    private const int FileAttributeOffline = 0x00001000;              // FILE_ATTRIBUTE_OFFLINE (same as FileAttributes.Offline)

    private static bool IsCloudPlaceholder(FileAttributes attributes)
    {
        var raw = (int)attributes;
        return (raw & (FileAttributeOffline | FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess)) != 0;
    }

    public static void LogEnvironment()
    {
        if (_environmentLogged) return;
        _environmentLogged = true;
        try
        {
            AppLog.Info($"Environment: {RuntimeInformation.OSDescription}, version={Environment.OSVersion.Version}, " +
                        $"64-bit process={Environment.Is64BitProcess}, elevated={IsElevated()}, " +
                        $"apartment={Thread.CurrentThread.GetApartmentState()}, dpiAwareness={NativeMethods.DescribeDpiAwareness()}, " +
                        $"interactive={Environment.UserInteractive}");
            AppLog.Info($"Registry WallPaper='{ReadDesktopValue("WallPaper")}', WallpaperStyle='{ReadDesktopValue("WallpaperStyle")}', TileWallpaper='{ReadDesktopValue("TileWallpaper")}'");
            AppLog.Info($"SPI_GETDESKWALLPAPER='{NativeMethods.GetCurrentWallpaperViaSpi()}'");
            foreach (var monitor in DesktopWallpaperApi.GetMonitors())
                AppLog.Info($"Monitor id='{monitor.Id}' rect=({monitor.Left},{monitor.Top})-({monitor.Right},{monitor.Bottom}) size={monitor.Width}x{monitor.Height}");
        }
        catch (Exception ex) { AppLog.Warn("Could not log the environment.", ex); }
    }

    private static bool IsElevated()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch (Exception) { return false; }
    }

    private static string ReadDesktopValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue(name)?.ToString() ?? "(missing)";
        }
        catch (Exception ex) { return "(error: " + ex.Message + ")"; }
    }

    /// <summary>Returns a reason when a Windows policy forbids changing the wallpaper, otherwise null.</summary>
    public static string? DetectPolicyBlock()
    {
        try
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using (var activeDesktop = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop"))
                {
                    if (activeDesktop?.GetValue("NoChangingWallPaper") is int blocked && blocked == 1)
                        return $"Windows policy ({hive.Name}\\...\\Policies\\ActiveDesktop\\NoChangingWallPaper) prevents changing the desktop wallpaper.";
                }
                using (var system = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    if (system?.GetValue("Wallpaper") is string forced && !string.IsNullOrWhiteSpace(forced))
                        return $"Windows policy forces the desktop wallpaper to '{forced}' ({hive.Name}\\...\\Policies\\System\\Wallpaper).";
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Could not read wallpaper policies.", ex); }
        return null;
    }

    /// <summary>Checks that the file exists, is an absolute path and can actually be read. Returns an error or null.</summary>
    public static string? PreflightFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            AppLog.Info($"File check: path='{path}' length={path.Length} rooted={Path.IsPathRooted(path)} exists={info.Exists}" +
                        (info.Exists ? $" size={info.Length} attributes={info.Attributes}" : string.Empty));
            if (!Path.IsPathRooted(path)) return "the stored path is not an absolute path";
            if (!info.Exists) return "the file does not exist any more (moved, renamed, deleted or on a disconnected drive)";
            if (IsCloudPlaceholder(info.Attributes))
                AppLog.Warn("The file is a cloud-only placeholder (for example OneDrive); reading it will trigger a download.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _ = stream.ReadByte();
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error("File check failed: access denied.", ex);
            return "access to the file was denied (check the file permissions)";
        }
        catch (IOException ex)
        {
            AppLog.Error("File check failed.", ex);
            return "the file could not be read: " + ex.Message;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            AppLog.Error("File check failed: invalid path.", ex);
            return "the stored path is invalid: " + ex.Message;
        }
    }
}
