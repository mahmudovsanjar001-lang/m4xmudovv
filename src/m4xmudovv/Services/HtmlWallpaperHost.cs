using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace m4xmudovv.Services;

/// <summary>Creates one WebView2 HTML wallpaper window per monitor.</summary>
internal sealed class HtmlWallpaperHost
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
    private readonly List<HtmlWallpaperWindow> _windows = new();

    public bool IsRunning => _windows.Count > 0;

    public async Task<WallpaperApplyResult> StartAsync(string path, string title, CancellationToken cancellationToken)
    {
        Stop();
        AppLog.Info($"=== HTML wallpaper requested: '{title}' path='{path}'");
        if (!File.Exists(path)) return WallpaperApplyResult.FailWithLog($"The HTML file “{title}” does not exist.");

        var layout = NativeMethods.DiscoverShell();
        for (var attempt = 2; !layout.IsValid && attempt <= 5; attempt++)
        {
            AppLog.Warn($"HTML shell discovery attempt {attempt - 1} failed: {layout.Error}. Retrying…");
            await Task.Delay(300, cancellationToken);
            layout = NativeMethods.DiscoverShell();
        }
        if (!layout.IsValid) return WallpaperApplyResult.FailWithLog($"HTML wallpaper could not start: {layout.Error}");

        var monitors = DesktopWallpaperApi.GetMonitors();
        if (monitors.Count == 0) return WallpaperApplyResult.FailWithLog("No monitors were found for the HTML wallpaper.");
        var targets = layout.IsRaisedDesktop && layout.ShellDefView != IntPtr.Zero
            ? new List<AttachTarget> { new(layout.Progman, layout.ShellDefView, "Progman raised desktop", UseLayeredParenting: true) }
            : new List<AttachTarget> { new(layout.WorkerW, IntPtr.Zero, "WorkerW") };
        if (layout.IsProgmanChild && !layout.IsRaisedDesktop)
            targets.Add(new AttachTarget(layout.Progman, NativeMethods.HWND_BOTTOM, "Progman (bottom of z-order)"));

        try
        {
            foreach (var monitor in monitors)
            {
                var window = new HtmlWallpaperWindow(path, monitor, targets);
                _windows.Add(window);
                window.Show();
            }
            var all = Task.WhenAll(_windows.Select(window => window.Ready));
            var finished = await Task.WhenAny(all, Task.Delay(OpenTimeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(finished, all))
                return Fail("WebView2 timed out while loading the HTML wallpaper.");

            var results = await all;
            var failures = new List<string>();
            var survivors = new List<HtmlWallpaperWindow>();
            for (var i = 0; i < _windows.Count; i++)
            {
                if (results[i] is null) survivors.Add(_windows[i]);
                else
                {
                    failures.Add($"monitor {_windows[i].Monitor.Id}: {results[i]}");
                    _windows[i].Stop();
                }
            }
            _windows.Clear();
            _windows.AddRange(survivors);
            if (_windows.Count == 0)
            {
                var detail = failures.Count > 0 ? failures[0] : "all WebView2 windows failed without a detail";
                return Fail($"The HTML wallpaper could not be loaded: {detail}");
            }

            var attached = string.Join(", ", _windows.Select(window => window.AttachedTo).Distinct());
            var message = $"HTML wallpaper “{title}” is running on {_windows.Count} of {monitors.Count} monitor(s) via WebView2 ({attached}).";
            if (failures.Count > 0) message += $" Problems: {string.Join("; ", failures)}";
            AppLog.Info("=== HTML wallpaper OK: " + message);
            return WallpaperApplyResult.Ok(message);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("HTML wallpaper start failed.", ex);
            return Fail($"The HTML wallpaper could not be started: {ex.Message}");
        }
    }

    public void Stop()
    {
        foreach (var window in _windows) window.Stop();
        _windows.Clear();
    }

    private WallpaperApplyResult Fail(string message)
    {
        Stop();
        DesktopWallpaperApi.ReapplyCurrent();
        return WallpaperApplyResult.FailWithLog(message);
    }
}
