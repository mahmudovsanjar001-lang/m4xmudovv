using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using m4xmudovv.Models;

namespace m4xmudovv.Services;

/// <summary>
/// Applies library items to the real Windows desktop.
/// Images use the Windows wallpaper API (per monitor, with fit mode) and are verified by asking Windows
/// what it now reports. Videos are hosted by <see cref="VideoWallpaperHost"/> in the shell's wallpaper
/// layer behind the desktop icons. Every step is logged (see <see cref="AppLog"/>).
/// Must be used from the WPF UI thread.
/// </summary>
public sealed class DesktopWallpaperHost : IWallpaperHost
{
    // Formats the Windows wallpaper API accepts reliably. Everything else (e.g. GIF) is converted to PNG first.
    private static readonly string[] NativeImageExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    private readonly VideoWallpaperHost _video = new();
    private readonly string _convertedDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "m4xmudovv",
        "wallpapers");

    public bool IsVideoWallpaperRunning => _video.IsRunning;

    public async Task<WallpaperApplyResult> ApplyAsync(WallpaperItem wallpaper, WallpaperApplyOptions options, CancellationToken cancellationToken = default)
    {
        AppLog.Info($"ApplyAsync: title='{wallpaper.Title}' type={wallpaper.Type} path='{wallpaper.FilePath}'");
        if (string.IsNullOrWhiteSpace(wallpaper.FilePath))
            return WallpaperApplyResult.FailWithLog($"“{wallpaper.Title}” has no file path stored in the library.");

        return wallpaper.Type == WallpaperType.Video
            ? await _video.StartAsync(wallpaper.FilePath, wallpaper.Title, options, cancellationToken)
            : await ApplyImageAsync(wallpaper, options, cancellationToken);
    }

    private async Task<WallpaperApplyResult> ApplyImageAsync(WallpaperItem wallpaper, WallpaperApplyOptions options, CancellationToken cancellationToken)
    {
        AppLog.Info($"=== Image wallpaper requested: '{wallpaper.Title}' fit={options.FitMode}");
        WallpaperDiagnostics.LogEnvironment();

        var policy = WallpaperDiagnostics.DetectPolicyBlock();
        if (policy is not null) return WallpaperApplyResult.FailWithLog(policy);

        var fileProblem = WallpaperDiagnostics.PreflightFile(wallpaper.FilePath);
        if (fileProblem is not null)
            return WallpaperApplyResult.FailWithLog($"The image “{wallpaper.Title}” cannot be used: {fileProblem}.");

        string file;
        try { file = await PrepareImageFileAsync(wallpaper.FilePath, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("Preparing the image failed.", ex);
            return WallpaperApplyResult.FailWithLog($"The image “{wallpaper.Title}” could not be decoded: {ex.Message}");
        }
        AppLog.Info($"Wallpaper file to apply: '{file}'");

        // A static wallpaper replaces any running video wallpaper.
        var hadVideo = _video.IsRunning;
        _video.Stop();

        var attempts = new List<string>();

        // Attempt 1: IDesktopWallpaper (per monitor, supports Fill / Fit / Stretch).
        try
        {
            DesktopWallpaperApi.SetImage(file, options.FitMode);
            AppLog.Info("IDesktopWallpaper.SetPosition + SetWallpaper(all monitors) returned S_OK.");
            var verification = DesktopWallpaperApi.Verify(file, options.FitMode);
            AppLog.Info($"IDesktopWallpaper verification: available={verification.Available} matches={verification.Matches} ({verification.Details})");
            if (verification.Matches) return Success(wallpaper, options, "IDesktopWallpaper", verification.Details);
            attempts.Add($"IDesktopWallpaper: Windows accepted the call but {(verification.Available ? "reports a different state: " : "could not confirm it: ")}{verification.Details}");
        }
        catch (Exception ex)
        {
            AppLog.Error("IDesktopWallpaper failed.", ex);
            attempts.Add($"IDesktopWallpaper: {Describe(ex)}");
        }

        // Attempt 2: classic registry + SystemParametersInfo.
        try
        {
            DesktopWallpaperApi.SetImageLegacy(file, options.FitMode);
            AppLog.Info("SystemParametersInfo(SPI_SETDESKWALLPAPER) returned TRUE.");
            var verification = DesktopWallpaperApi.Verify(file, options.FitMode);
            AppLog.Info($"Post-SPI verification: available={verification.Available} matches={verification.Matches} ({verification.Details})");
            if (verification.Matches) return Success(wallpaper, options, "SystemParametersInfo", verification.Details);

            if (!verification.Available)
            {
                // IDesktopWallpaper cannot be queried on this system. SPI_SETDESKWALLPAPER returning TRUE is
                // not enough on its own, so confirm the path through the independent SPI_GETDESKWALLPAPER API.
                var reported = NativeMethods.GetCurrentWallpaperViaSpi();
                AppLog.Info($"SPI_GETDESKWALLPAPER reports '{reported}'");
                if (PathsEqual(reported, file))
                    return Success(wallpaper, options, "SystemParametersInfo", "Windows confirmed the wallpaper path via SPI_GETDESKWALLPAPER");

                attempts.Add($"SystemParametersInfo: SPI_GETDESKWALLPAPER reports '{reported}', expected '{file}'");
            }
            attempts.Add($"SystemParametersInfo: Windows accepted the call but reports a different state: {verification.Details}");
        }
        catch (Exception ex)
        {
            AppLog.Error("SystemParametersInfo failed.", ex);
            attempts.Add($"SystemParametersInfo: {Describe(ex)}");
        }

        if (hadVideo) DesktopWallpaperApi.ReapplyCurrent();
        return WallpaperApplyResult.FailWithLog(
            $"Windows did not apply “{wallpaper.Title}” as the desktop wallpaper.\n\n" + string.Join("\n", attempts.Select(a => "• " + a)));
    }

    private static WallpaperApplyResult Success(WallpaperItem wallpaper, WallpaperApplyOptions options, string method, string detail)
    {
        var monitorCount = DesktopWallpaperApi.GetMonitors().Count;
        var message = $"Desktop wallpaper set to “{wallpaper.Title}” on {monitorCount} monitor(s) ({options.FitMode}).";
        AppLog.Info($"=== Image wallpaper OK via {method}: {message} [{detail}]");
        return WallpaperApplyResult.Ok(message);
    }

    private static string Describe(Exception ex) => (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        ? $"{ex.Message} (HRESULT 0x{ex.HResult:X8})"
        : ex.Message;

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Returns a file the Windows wallpaper API can read, converting unusual formats to a cached PNG.</summary>
    private async Task<string> PrepareImageFileAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (NativeImageExtensions.Contains(extension)) return path;

        var info = new FileInfo(path);
        var identity = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var target = Path.Combine(_convertedDirectory, hash + ".png");
        if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

        AppLog.Info($"Converting '{path}' ({extension}) to PNG '{target}'");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(_convertedDirectory);
            var decoder = BitmapDecoder.Create(new Uri(path, UriKind.Absolute), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temp)) encoder.Save(stream);
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }, cancellationToken);
        return target;
    }

    /// <summary>Applies a changed Fit mode / speed to a running video wallpaper.</summary>
    public void UpdateVideoOptions(WallpaperApplyOptions options) => _video.UpdateOptions(options);

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        _video.Pause();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Shutdown();
        return Task.CompletedTask;
    }

    /// <summary>Removes a running video wallpaper and repaints the static wallpaper. Safe to call repeatedly.</summary>
    public void Shutdown()
    {
        if (!_video.IsRunning) return;
        _video.Stop();
        DesktopWallpaperApi.ReapplyCurrent();
    }
}
