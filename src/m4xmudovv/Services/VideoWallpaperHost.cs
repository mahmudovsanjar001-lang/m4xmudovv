using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace m4xmudovv.Services;

/// <summary>Creates and owns one video window per monitor. Must be used from the WPF UI thread.</summary>
internal sealed class VideoWallpaperHost
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(1500);
    private readonly List<VideoWallpaperWindow> _windows = new();

    public bool IsRunning => _windows.Count > 0;

    public async Task<WallpaperApplyResult> StartAsync(string path, string title, WallpaperApplyOptions options, CancellationToken cancellationToken)
    {
        Stop();
        AppLog.Info($"=== Video wallpaper requested: '{title}' path='{path}' fit={options.FitMode} speed={options.SpeedRatio}x volume={options.Volume}");
        WallpaperDiagnostics.LogEnvironment();

        var fileProblem = WallpaperDiagnostics.PreflightFile(path);
        if (fileProblem is not null)
            return WallpaperApplyResult.FailWithLog($"The video “{title}” cannot be used: {fileProblem}.");

        // The WorkerW layer is created on request; retry briefly in case the shell is slow to create it.
        var layout = NativeMethods.DiscoverShell();
        for (var attempt = 2; !layout.IsValid && attempt <= 5; attempt++)
        {
            AppLog.Warn($"Shell discovery attempt {attempt - 1} failed: {layout.Error}. Retrying…");
            await Task.Delay(300, cancellationToken);
            layout = NativeMethods.DiscoverShell();
        }
        foreach (var line in NativeMethods.DescribeShell()) AppLog.Info("Shell: " + line);

        if (!layout.IsValid)
            return WallpaperApplyResult.FailWithLog($"Video wallpaper could not start: {layout.Error}");
        AppLog.Info($"Shell layout: {layout.Kind}; WorkerW: {NativeMethods.DescribeWindow(layout.WorkerW)}");

        var monitors = DesktopWallpaperApi.GetMonitors();
        if (monitors.Count == 0)
            return WallpaperApplyResult.FailWithLog("No monitors were found, so the video wallpaper could not be placed.");

        // Lively's current WinDesktopCore handles the Windows 11 raised-desktop layout specially:
        // the video must be a layered child of Progman and sit immediately below SHELLDLL_DefView.
        var targets = layout.IsRaisedDesktop && layout.ShellDefView != IntPtr.Zero
            ? new List<AttachTarget> { new(layout.Progman, layout.ShellDefView, "Progman raised desktop", UseLayeredParenting: true) }
            : new List<AttachTarget> { new(layout.WorkerW, IntPtr.Zero, "WorkerW") };

        if (layout.IsProgmanChild && !layout.IsRaisedDesktop)
            targets.Add(new AttachTarget(layout.Progman, NativeMethods.HWND_BOTTOM, "Progman (bottom of z-order)"));

        try
        {
            foreach (var monitor in monitors)
            {
                var window = new VideoWallpaperWindow(path, monitor, targets, options);
                _windows.Add(window);
                window.Show();
            }

            if (layout.IsRaisedDesktop)
                EnsureWorkerWZOrder(layout);

            var all = Task.WhenAll(_windows.Select(window => window.Ready));
            var finished = await Task.WhenAny(all, Task.Delay(OpenTimeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(finished, all))
            {
                Stop();
                DesktopWallpaperApi.ReapplyCurrent();
                return WallpaperApplyResult.FailWithLog($"Timed out after {OpenTimeout.TotalSeconds:0} s while opening “{title}”. The video format or codec may not be supported by Windows.");
            }

            var results = await all;
            var failures = new List<string>();
            var survivors = new List<VideoWallpaperWindow>();
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
                DesktopWallpaperApi.ReapplyCurrent();
                return WallpaperApplyResult.FailWithLog($"The video wallpaper could not be started: {failures.First()}");
            }

            // "Opened" is not enough: give playback a moment, then confirm the windows are still attached and the clock advances.
            await Task.Delay(SettleTime, cancellationToken);
            var healthy = new List<VideoWallpaperWindow>();
            foreach (var window in _windows)
            {
                AppLog.Info("Check: " + window.DescribeState());
                if (!window.IsStillAttachedAndVisible())
                {
                    failures.Add($"monitor {window.Monitor.Id}: the desktop window disappeared or is not visible");
                    window.Stop();
                }
                else if (!window.IsPlaybackProgressing())
                {
                    failures.Add($"monitor {window.Monitor.Id}: the video opened but playback did not advance (decoder stalled)");
                    window.Stop();
                }
                else healthy.Add(window);
            }
            _windows.Clear();
            _windows.AddRange(healthy);

            if (_windows.Count == 0)
            {
                DesktopWallpaperApi.ReapplyCurrent();
                return WallpaperApplyResult.FailWithLog($"The video wallpaper could not be started: {failures.First()}");
            }

            var attached = string.Join(", ", _windows.Select(window => window.AttachedTo).Distinct());
            var message = $"Video wallpaper “{title}” is playing on {_windows.Count} of {monitors.Count} monitor(s) ({options.FitMode}, {options.SpeedRatio:0.##}x, muted; attached via {attached}).";
            if (failures.Count > 0) message += $" Problems: {string.Join("; ", failures)}";
            AppLog.Info("=== Video wallpaper OK: " + message);
            return WallpaperApplyResult.Ok(message);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Video wallpaper start failed with an exception.", ex);
            Stop();
            DesktopWallpaperApi.ReapplyCurrent();
            return WallpaperApplyResult.FailWithLog($"The video wallpaper could not be started: {ex.Message}");
        }
    }

    public void UpdateOptions(WallpaperApplyOptions options)
    {
        foreach (var window in _windows) window.ApplyOptions(options);
    }

    public void Pause()
    {
        foreach (var window in _windows) window.PauseVideo();
    }

    public void Resume()
    {
        foreach (var window in _windows) window.ResumeVideo();
    }

    public void Stop()
    {
        if (_windows.Count == 0) return;
        AppLog.Info($"Stopping {_windows.Count} video wallpaper window(s).");
        foreach (var window in _windows) window.Stop();
        _windows.Clear();
    }

    private static void EnsureWorkerWZOrder(ShellLayout layout)
    {
        if (!layout.IsRaisedDesktop || layout.WorkerW == IntPtr.Zero || layout.Progman == IntPtr.Zero) return;
        if (NativeMethods.GetLastChildWindow(layout.Progman) == layout.WorkerW) return;

        AppLog.Warn("Raised desktop WorkerW was not the last Progman child; restoring it to HWND_BOTTOM.");
        NativeMethods.SetWindowPos(layout.WorkerW, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
