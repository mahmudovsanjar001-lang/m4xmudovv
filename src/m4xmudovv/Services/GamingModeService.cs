using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace m4xmudovv.Services;

internal sealed class GamingModeService
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    public bool IsFullscreenGameActive()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.Id == Environment.ProcessId || process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { return false; }
        if (!GetWindowRect(hwnd, out var window)) return false;
        var monitors = DesktopWallpaperApi.GetMonitors();
        if (monitors.Count == 0) return false;
        foreach (var monitor in monitors)
        {
            var covers = window.Left <= monitor.Left && window.Top <= monitor.Top &&
                         window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
            if (covers) return true;
        }
        return false;
    }
}
