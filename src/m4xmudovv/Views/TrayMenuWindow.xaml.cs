using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace m4xmudovv.Views;

/// <summary>
/// The tray icon's right-click menu: a small themed popup instead of the plain Windows Forms menu.
/// It opens next to the mouse pointer, stays inside the monitor's work area and closes when it loses focus.
/// </summary>
public partial class TrayMenuWindow : Window
{
    private static DateTime _lastClosedUtc = DateTime.MinValue;
    private readonly Action _open;
    private readonly Action _settings;
    private readonly Action _exit;
    private bool _closing;

    public TrayMenuWindow(Action open, Action settings, Action exit, string statusText)
    {
        InitializeComponent();
        _open = open;
        _settings = settings;
        _exit = exit;
        StatusLine.Text = statusText;
        Deactivated += (_, _) => CloseMenu();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseMenu(); };
    }

    /// <summary>True right after a menu closed: the same click that closed it must not immediately reopen it.</summary>
    public static bool JustClosed => (DateTime.UtcNow - _lastClosedUtc).TotalMilliseconds < 250;

    /// <summary>Shows the menu near the pointer. <paramref name="cursorX"/>/<paramref name="cursorY"/> are physical screen pixels.</summary>
    public void ShowNear(int cursorX, int cursorY)
    {
        Opacity = 0;                     // invisible until it has been moved to the right place (no flicker)
        Show();
        UpdateLayout();

        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromPoint(new POINT { X = cursorX, Y = cursorY }, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        uint dpi = 96;
        try { if (GetDpiForMonitor(monitor, 0, out var dx, out _) == 0 && dx > 0) dpi = dx; } catch (Exception) { }

        var scale = dpi / 96.0;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var gap = (int)(6 * scale);

        // Bottom/right taskbar: open up and to the left of the pointer. Top taskbar: open downwards.
        var x = cursorX - width + (int)(14 * scale);
        var y = cursorY - height - gap;
        if (y < info.rcWork.Top) y = cursorY + gap;
        x = Math.Max(info.rcWork.Left, Math.Min(x, info.rcWork.Right - width));
        y = Math.Max(info.rcWork.Top, Math.Min(y, info.rcWork.Bottom - height));

        SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, 0, 0, 0x0001 /* SWP_NOSIZE */ | 0x0010 /* SWP_NOACTIVATE */);
        Opacity = 1;
        SetForegroundWindow(hwnd);       // without this Windows would never tell us when the user clicks elsewhere
        Activate();
    }

    public void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        _lastClosedUtc = DateTime.UtcNow;
        try { Close(); } catch (InvalidOperationException) { }
    }

    private void Run(Action action)
    {
        CloseMenu();
        Application.Current?.Dispatcher.BeginInvoke(action);
    }

    private void OpenButton_OnClick(object sender, RoutedEventArgs e) => Run(_open);
    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) => Run(_settings);
    private void ExitButton_OnClick(object sender, RoutedEventArgs e) => Run(_exit);

    // ---- Win32 ----
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
