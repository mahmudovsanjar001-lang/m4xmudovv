using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Storage;
using Windows.System.UserProfile;

namespace m4xmudovv.Services;

/// <summary>
/// Applies a static image to the Windows lock screen (Windows has no animated lock-screen wallpapers).
/// Strategy:
///  1. The official WinRT API (UserProfilePersonalizationSettings) with a fresh, uniquely named copy of the image.
///  2. If Windows refuses (returns false / throws — common on Home/Pro builds, Spotlight, OEM images), fall back to the
///     PersonalizationCSP registry keys, which need administrator rights. The app re-launches itself elevated
///     (UAC prompt) with "--set-lockscreen" / "--clear-lockscreen"; see <see cref="RunElevatedCommand"/>.
/// </summary>
internal static class LockScreenWallpaperService
{
    public const string SetCommand = "--set-lockscreen";
    public const string ClearCommand = "--clear-lockscreen";

    private const string CspKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";
    private static readonly string[] NativeExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    private static string UserFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "m4xmudovv", "LockScreen");

    private static string SharedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "m4xmudovv", "LockScreen");

    /// <summary>True when the last successful apply went through the administrator (registry) path.</summary>
    public static bool UsedPolicyPath { get; private set; }

    public static async Task<(bool Success, string Message)> ApplyAsync(string path)
    {
        if (!OperatingSystem.IsWindows()) return (false, "Lock-screen wallpaper is available only on Windows.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return (false, "The lock-screen image file does not exist.");

        string prepared;
        try { prepared = await Task.Run(() => PrepareCopy(path, UserFolder)); }
        catch (Exception ex)
        {
            AppLog.Error("Lock-screen: preparing the image failed.", ex);
            return (false, $"The image could not be prepared for the lock screen: {ex.Message}");
        }

        // ---- 1. Official API ----
        string apiProblem;
        try
        {
            if (!UserProfilePersonalizationSettings.IsSupported())
            {
                apiProblem = "this Windows edition does not support the lock-screen API";
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(prepared);
                var accepted = await UserProfilePersonalizationSettings.Current.TrySetLockScreenImageAsync(file);
                if (accepted)
                {
                    UsedPolicyPath = false;
                    CleanupOldCopies(UserFolder, prepared);
                    AppLog.Info($"Lock-screen: API accepted '{prepared}'.");
                    return (true, $"Lock-screen image set to “{Path.GetFileName(path)}”.");
                }
                apiProblem = "Windows returned false";
            }
        }
        catch (Exception ex)
        {
            apiProblem = $"{ex.Message} (0x{ex.HResult:X8})";
            AppLog.Warn("Lock-screen: API threw.", ex);
        }
        AppLog.Warn("Lock-screen: API did not work: " + apiProblem + ". Trying the administrator fallback.");

        // ---- 2. Administrator fallback (registry) ----
        string shared;
        try { shared = await Task.Run(() => PrepareCopy(prepared, SharedFolder)); }
        catch (Exception ex)
        {
            AppLog.Error("Lock-screen: copying to the shared folder failed.", ex);
            return (false, $"Windows refused the lock-screen image ({apiProblem}).");
        }

        var (ok, message) = await RunElevatedAsync($"{SetCommand} \"{shared}\"");
        if (ok)
        {
            UsedPolicyPath = true;
            CleanupOldCopies(UserFolder, prepared);
            CleanupOldCopies(SharedFolder, shared);
            return (true, $"Lock-screen image set to “{Path.GetFileName(path)}” (applies the next time you lock).");
        }
        return (false, $"Windows refused the lock-screen image ({apiProblem}). {message}");
    }

    /// <summary>Removes the registry override created by the fallback so Windows controls the lock screen again.</summary>
    public static async Task<(bool Success, string Message)> ClearPolicyAsync()
    {
        var (ok, message) = await RunElevatedAsync(ClearCommand);
        if (ok) UsedPolicyPath = false;
        return (ok, ok ? "Lock-screen override removed. Windows controls the lock screen again." : message);
    }

    // ------------------------------------------------------------------------------------------
    //  Elevated helper (runs inside a second, administrator copy of this exe)
    // ------------------------------------------------------------------------------------------

    /// <summary>Handles "--set-lockscreen &lt;file&gt;" and "--clear-lockscreen". Returns the process exit code (0 = success).</summary>
    public static int RunElevatedCommand(string[] args)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(CspKeyPath, writable: true);
            if (key is null) return 2;

            if (args[0].Equals(ClearCommand, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue("LockScreenImagePath", throwOnMissingValue: false);
                key.DeleteValue("LockScreenImageUrl", throwOnMissingValue: false);
                key.DeleteValue("LockScreenImageStatus", throwOnMissingValue: false);
                return 0;
            }

            if (args.Length < 2 || !File.Exists(args[1])) return 3;
            key.SetValue("LockScreenImagePath", args[1], RegistryValueKind.String);
            key.SetValue("LockScreenImageUrl", args[1], RegistryValueKind.String);
            key.SetValue("LockScreenImageStatus", 1, RegistryValueKind.DWord);
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Lock-screen: elevated registry write failed.", ex);
            return 4;
        }
    }

    private static async Task<(bool Success, string Message)> RunElevatedAsync(string arguments)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return (false, "The app location could not be determined.");
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(dll)) return (false, "The app location could not be determined.");
            arguments = $"\"{dll}\" {arguments}";
        }

        try
        {
            var info = new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var process = Process.Start(info);
            if (process is null) return (false, "The administrator helper could not be started.");
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, $"The administrator helper failed (code {process.ExitCode}). See the log: {AppLog.LogPath}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Administrator permission was declined, so the lock screen was not changed.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Lock-screen: starting the administrator helper failed.", ex);
            return (false, $"The administrator helper could not be started: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Files
    // ------------------------------------------------------------------------------------------

    /// <summary>Copies (or converts to PNG when the format is not jpg/png/bmp) into <paramref name="folder"/> under a unique name.</summary>
    private static string PrepareCopy(string source, string folder)
    {
        Directory.CreateDirectory(folder);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var unique = $"lockscreen-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

        if (NativeExtensions.Contains(extension))
        {
            var target = Path.Combine(folder, unique + extension);
            File.Copy(source, target, overwrite: false);
            return target;
        }

        // GIF / WEBP / other: Windows lock screen needs a plain still image.
        var png = Path.Combine(folder, unique + ".png");
        var decoder = BitmapDecoder.Create(new Uri(source), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(decoder.Frames[0]);
        using var stream = File.Create(png);
        encoder.Save(stream);
        return png;
    }

    private static void CleanupOldCopies(string folder, string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "lockscreen-*")
                         .Where(f => !f.Equals(keep, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(File.GetCreationTimeUtc).Skip(1))
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception) { /* cleanup is best effort */ }
    }
}
