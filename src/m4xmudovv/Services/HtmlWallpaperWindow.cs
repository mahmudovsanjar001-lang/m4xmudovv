using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace m4xmudovv.Services;

/// <summary>Hosts one local HTML/CSS/JS wallpaper on one monitor through WebView2.</summary>
internal sealed class HtmlWallpaperWindow : Window
{
    private readonly string _path;
    private readonly MonitorInfo _monitor;
    private readonly IReadOnlyList<AttachTarget> _targets;
    private readonly WebView2 _webView = new();
    private readonly TaskCompletionSource<string?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _hwnd;
    private IntPtr _attachedParent;
    private bool _stopped;

    public HtmlWallpaperWindow(string path, MonitorInfo monitor, IReadOnlyList<AttachTarget> targets)
    {
        _path = path;
        _monitor = monitor;
        _targets = targets;
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
        _webView.IsHitTestVisible = false;
        Content = _webView;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _ready.TrySetResult("the HTML wallpaper window was closed before navigation completed");
    }

    public MonitorInfo Monitor => _monitor;
    public Task<string?> Ready => _ready.Task;
    public string AttachedTo { get; private set; } = string.Empty;

    public bool IsStillAttachedAndVisible()
    {
        if (_hwnd == IntPtr.Zero || _attachedParent == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd) ||
            !NativeMethods.IsWindowVisible(_hwnd) || NativeMethods.GetParent(_hwnd) != _attachedParent)
            return false;
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return false;
        return Math.Abs((rect.Right - rect.Left) - _monitor.Width) <= 2 &&
               Math.Abs((rect.Bottom - rect.Top) - _monitor.Height) <= 2;
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _webView.Stop(); } catch (Exception) { }
        try { _webView.Dispose(); } catch (Exception) { }
        try { if (IsVisible) Close(); } catch (InvalidOperationException) { }
    }

    private async Task StartAsync()
    {
        AppLog.Info($"[{_monitor.Id}] WebView2 HTML window loaded; attaching '{_path}'");
        _hwnd = new WindowInteropHelper(this).Handle;
        var attachError = AttachToDesktop();
        if (attachError is not null)
        {
            _ready.TrySetResult("could not attach to the desktop layer: " + attachError);
            return;
        }

        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webView.NavigationCompleted += WebView_NavigationCompleted;
            _webView.CoreWebView2.Navigate(new Uri(_path, UriKind.Absolute).AbsoluteUri);
            AppLog.Info($"[{_monitor.Id}] WebView2 navigation started for '{_path}'");
        }
        catch (Exception ex)
        {
            AppLog.Error($"[{_monitor.Id}] WebView2 could not initialize HTML playback.", ex);
            _ready.TrySetResult($"WebView2 initialization failed: {ex.Message}");
        }
    }

    private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            AppLog.Info($"[{_monitor.Id}] WebView2 HTML navigation completed successfully.");
            _ready.TrySetResult(null);
        }
        else
        {
            var message = $"HTML navigation failed: {e.WebErrorStatus}";
            AppLog.Error($"[{_monitor.Id}] {message}");
            _ready.TrySetResult(message);
        }
    }

    private string? AttachToDesktop()
    {
        if (_hwnd == IntPtr.Zero) return "the wallpaper window has no native handle";
        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                   NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);
        style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        if (_targets.Any(target => target.UseLayeredParenting)) exStyle |= NativeMethods.WS_EX_LAYERED;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        if (_targets.Any(target => target.UseLayeredParenting)) NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, 0x00000002);

        var errors = new List<string>();
        foreach (var target in _targets)
        {
            var error = TryAttach(target);
            if (error is null)
            {
                AttachedTo = target.Name;
                _attachedParent = target.Parent;
                return null;
            }
            errors.Add($"{target.Name}: {error}");
        }
        return string.Join("; ", errors);
    }

    private string? TryAttach(AttachTarget target)
    {
        NativeMethods.SetParent(_hwnd, target.Parent);
        var parentError = Marshal.GetLastWin32Error();
        if (NativeMethods.GetParent(_hwnd) != target.Parent)
            return $"SetParent did not take effect (Win32 error {parentError})";
        if (!NativeMethods.IsWindowVisible(target.Parent)) NativeMethods.ShowWindow(target.Parent, NativeMethods.SW_SHOWNA);
        var origin = new NativeMethods.POINT { X = _monitor.Left, Y = _monitor.Top };
        if (!NativeMethods.ScreenToClient(target.Parent, ref origin)) return $"ScreenToClient failed (Win32 error {Marshal.GetLastWin32Error()})";
        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW;
        if (target.InsertAfter == IntPtr.Zero) flags |= NativeMethods.SWP_NOZORDER;
        if (!NativeMethods.SetWindowPos(_hwnd, target.InsertAfter, origin.X, origin.Y, _monitor.Width, _monitor.Height, flags))
            return $"SetWindowPos failed (Win32 error {Marshal.GetLastWin32Error()})";
        if (!NativeMethods.IsWindowVisible(_hwnd) || !NativeMethods.GetWindowRect(_hwnd, out var rect)) return "window is not visible after attaching";
        if (Math.Abs((rect.Right - rect.Left) - _monitor.Width) > 2 || Math.Abs((rect.Bottom - rect.Top) - _monitor.Height) > 2)
            return $"window is {rect.Right - rect.Left}x{rect.Bottom - rect.Top} but monitor is {_monitor.Width}x{_monitor.Height}";
        return null;
    }
}
