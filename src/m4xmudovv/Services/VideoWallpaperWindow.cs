using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using LibVLCSharp.Shared;

namespace m4xmudovv.Services;

/// <summary>
/// One native LibVLC video renderer per monitor. The LibVLC output is bound directly to this window's HWND,
/// then the HWND is attached to the Windows shell wallpaper layer. This avoids WPF MediaElement codec limits
/// and avoids a detached overlay window being placed above desktop icons.
/// </summary>
internal sealed class VideoWallpaperWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly IReadOnlyList<AttachTarget> _targets;
    private readonly string _path;
    private readonly WallpaperApplyOptions _initialOptions;
    private readonly TaskCompletionSource<string?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly Lazy<LibVLC> SharedLibVlc = new(CreateLibVlc);
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private Media? _media;
    private IntPtr _hwnd;
    private IntPtr _attachedParent;
    private WallpaperFitMode _fit;
    private bool _stopped;
    private int _loopRestartPending;

    public VideoWallpaperWindow(string path, MonitorInfo monitor, IReadOnlyList<AttachTarget> targets, WallpaperApplyOptions options)
    {
        _path = path;
        _monitor = monitor;
        _targets = targets;
        _initialOptions = options;
        _fit = options.FitMode;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowsTransparency = false;
        Background = System.Windows.Media.Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 16;
        Height = 16;

        // LibVLC is the same class of native playback backend used by desktop wallpaper applications:
        // broad codec support, hardware acceleration when available, and output directly into an HWND.
        // One engine is shared by all monitors; creating a native engine per monitor made sign-in startup slow.
        _libVlc = SharedLibVlc.Value;
        _player = new MediaPlayer(_libVlc)
        {
            Volume = (int)Math.Round(Math.Clamp(options.Volume, 0, 1) * 100),
            Mute = options.Volume <= 0
        };
        _player.SetRate((float)Math.Clamp(options.SpeedRatio, 0.25, 4.0));
        _player.Playing += Player_OnPlaying;
        _player.EncounteredError += Player_OnError;
        _player.EndReached += Player_OnEnded;

        Loaded += (_, _) => StartNativePlayback();
        Closed += (_, _) => _ready.TrySetResult("the wallpaper window was closed before the video opened");
    }

    public MonitorInfo Monitor => _monitor;
    public string? AttachedTo { get; private set; }
    public Task<string?> Ready => _ready.Task;

    public bool IsPlaybackProgressing() => _player.Time > 0 || (_player.Length > 0 && _player.Position > 0);

    public string DescribeState() =>
        $"[{_monitor.Id}] attachedTo={AttachedTo ?? "(none)"} time={_player.Time}ms length={_player.Length}ms " +
        $"state={_player.State} {NativeMethods.DescribeWindow(_hwnd)}";

    public bool IsStillAttachedAndVisible()
    {
        if (_hwnd == IntPtr.Zero || _attachedParent == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd) ||
            !NativeMethods.IsWindowVisible(_hwnd) || NativeMethods.GetParent(_hwnd) != _attachedParent)
            return false;

        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return false;
        return Math.Abs((rect.Right - rect.Left) - _monitor.Width) <= 2 &&
               Math.Abs((rect.Bottom - rect.Top) - _monitor.Height) <= 2;
    }

    public void ApplyOptions(WallpaperApplyOptions options)
    {
        _fit = options.FitMode;
        ApplyVideoFit();
        _player.SetRate((float)Math.Clamp(options.SpeedRatio, 0.25, 4.0));
        _player.Volume = (int)Math.Round(Math.Clamp(options.Volume, 0, 1) * 100);
        _player.Mute = options.Volume <= 0;
    }

    public void PauseVideo()
    {
        try { _player.Pause(); } catch (Exception ex) { AppLog.Warn($"[{_monitor.Id}] pause failed: {ex.Message}"); }
    }

    public void ResumeVideo()
    {
        try { _player.Play(); } catch (Exception ex) { AppLog.Warn($"[{_monitor.Id}] resume failed: {ex.Message}"); }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _player.Stop(); } catch (Exception) { }
        _player.Playing -= Player_OnPlaying;
        _player.EncounteredError -= Player_OnError;
        _player.EndReached -= Player_OnEnded;
        try { _media?.Dispose(); } catch (Exception) { }
        try { _player.Dispose(); } catch (Exception) { }
        try { if (IsVisible) Close(); } catch (InvalidOperationException) { }
    }

    private static LibVLC CreateLibVlc()
    {
        Core.Initialize();
        return new LibVLC(
            "--no-video-title-show",
            "--avcodec-hw=any",
            "--no-osd",
            "--quiet",
            "--file-caching=150",
            "--drop-late-frames",
            "--skip-frames");
    }

    private void StartNativePlayback()
    {
        AppLog.Info($"[{_monitor.Id}] LibVLC window loaded; attaching to desktop ({_targets.Count} candidate(s))");
        _hwnd = new WindowInteropHelper(this).Handle;
        var attachError = AttachToDesktop();
        if (attachError is not null)
        {
            _ready.TrySetResult("could not attach to the desktop layer: " + attachError);
            return;
        }

        try
        {
            _player.Hwnd = _hwnd;
            ApplyVideoFit();
            _media = CreateLoopingMedia();
            if (!_player.Play(_media))
                _ready.TrySetResult("LibVLC rejected the video file");
            else
            {
                // Do not wait for the asynchronous Playing callback here; the host performs a
                // short visibility/clock health check immediately after all monitors are created.
                _ready.TrySetResult(null);
                AppLog.Info($"[{_monitor.Id}] LibVLC media opened: '{_path}'");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"[{_monitor.Id}] LibVLC could not start playback", ex);
            _ready.TrySetResult(ex.Message);
        }
    }

    private void Player_OnPlaying(object? sender, EventArgs e)
    {
        AppLog.Info($"[{_monitor.Id}] LibVLC Playing: length={_player.Length}ms");
        _ready.TrySetResult(null);
    }

    private void Player_OnError(object? sender, EventArgs e)
    {
        var message = "LibVLC could not decode this video or create its video output";
        AppLog.Error($"[{_monitor.Id}] {message}");
        _ready.TrySetResult(message);
    }

    private void Player_OnEnded(object? sender, EventArgs e)
    {
        if (_stopped) return;
        // EndReached is raised from LibVLC's worker thread. Reopen on the WPF dispatcher; this is
        // more reliable than assigning Position=0, which can leave some container formats on their
        // final frame. The media option below is also enabled as a first-line infinite-repeat path.
        if (Interlocked.Exchange(ref _loopRestartPending, 1) != 0) return;
        Dispatcher.BeginInvoke(new Action(RestartLoopingMedia), System.Windows.Threading.DispatcherPriority.Background);
    }

    private Media CreateLoopingMedia()
    {
        var media = new Media(_libVlc, new Uri(_path, UriKind.Absolute));
        media.AddOption(":input-repeat=65535");
        media.AddOption(":no-video-title-show");
        return media;
    }

    private void RestartLoopingMedia()
    {
        try
        {
            if (_stopped) return;
            _media?.Dispose();
            _media = CreateLoopingMedia();
            ApplyVideoFit();
            if (!_player.Play(_media))
                AppLog.Warn($"[{_monitor.Id}] LibVLC did not accept the loop restart.");
            else
                AppLog.Info($"[{_monitor.Id}] LibVLC video loop restarted.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[{_monitor.Id}] loop restart failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _loopRestartPending, 0);
        }
    }

    private void ApplyVideoFit()
    {
        var ratio = $"{_monitor.Width}:{_monitor.Height}";
        switch (_fit)
        {
            case WallpaperFitMode.Fill:
                // Crop to the monitor ratio, then LibVLC fills the output rectangle.
                _player.CropGeometry = ratio;
                _player.AspectRatio = null;
                _player.Scale = 0;
                break;
            case WallpaperFitMode.Fit:
                _player.CropGeometry = string.Empty;
                _player.AspectRatio = ratio;
                _player.Scale = 0;
                break;
            case WallpaperFitMode.Stretch:
                // The native HWND is exactly monitor-sized; remove crop and let the video output stretch.
                _player.CropGeometry = string.Empty;
                _player.AspectRatio = ratio;
                _player.Scale = 0;
                break;
            case WallpaperFitMode.Center:
                _player.CropGeometry = string.Empty;
                _player.AspectRatio = null;
                _player.Scale = 1.0f;
                break;
        }
    }

    private string? AttachToDesktop()
    {
        if (_hwnd == IntPtr.Zero) return "the wallpaper window has no native handle";
        AppLog.Info($"[{_monitor.Id}] before attach: {NativeMethods.DescribeWindow(_hwnd)}");

        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                   NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);
        style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        if (_targets.Any(target => target.UseLayeredParenting)) exStyle |= NativeMethods.WS_EX_LAYERED;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        if (_targets.Any(target => target.UseLayeredParenting))
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, 0x00000002);

        var errors = new List<string>();
        foreach (var target in _targets)
        {
            var error = TryAttach(target);
            if (error is null)
            {
                AttachedTo = target.Name;
                _attachedParent = target.Parent;
                AppLog.Info($"[{_monitor.Id}] attached via {target.Name}: {NativeMethods.DescribeWindow(_hwnd)}");
                return null;
            }
            AppLog.Warn($"[{_monitor.Id}] attach via {target.Name} failed: {error}");
            errors.Add($"{target.Name}: {error}");
        }
        return string.Join("; ", errors);
    }

    private string? TryAttach(AttachTarget target)
    {
        NativeMethods.SetParent(_hwnd, target.Parent);
        var parentError = Marshal.GetLastWin32Error();
        var actualParent = NativeMethods.GetParent(_hwnd);
        if (actualParent != target.Parent)
            return $"SetParent did not take effect (parent is 0x{actualParent.ToInt64():X}, Win32 error {parentError})";

        if (!NativeMethods.IsWindowVisible(target.Parent)) NativeMethods.ShowWindow(target.Parent, NativeMethods.SW_SHOWNA);
        var origin = new NativeMethods.POINT { X = _monitor.Left, Y = _monitor.Top };
        if (!NativeMethods.ScreenToClient(target.Parent, ref origin))
            return $"ScreenToClient failed (Win32 error {Marshal.GetLastWin32Error()})";

        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW;
        if (target.InsertAfter == IntPtr.Zero) flags |= NativeMethods.SWP_NOZORDER;
        if (!NativeMethods.SetWindowPos(_hwnd, target.InsertAfter, origin.X, origin.Y, _monitor.Width, _monitor.Height, flags))
            return $"SetWindowPos failed (Win32 error {Marshal.GetLastWin32Error()})";

        if (!NativeMethods.IsWindowVisible(_hwnd) || !NativeMethods.GetWindowRect(_hwnd, out var rect))
            return "the window is not visible after attaching";
        if (Math.Abs((rect.Right - rect.Left) - _monitor.Width) > 2 || Math.Abs((rect.Bottom - rect.Top) - _monitor.Height) > 2)
            return $"the window is {rect.Right - rect.Left}x{rect.Bottom - rect.Top} but the monitor is {_monitor.Width}x{_monitor.Height}";
        return null;
    }
}
