using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace m4xmudovv.Services;

internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

/// <summary>Where a video window can be parented. InsertAfter = IntPtr.Zero keeps the default z-order.</summary>
internal sealed record AttachTarget(IntPtr Parent, IntPtr InsertAfter, string Name, bool UseLayeredParenting = false);

internal sealed record ShellLayout(IntPtr Progman, IntPtr WorkerW, IntPtr ShellDefView, bool IsRaisedDesktop, string Kind, string? Error)
{
    public const string ClassicKind = "classic (WorkerW is a top-level sibling of the icon-view window)";
    public const string ProgmanChildKind = "progman-child (WorkerW is a child of Progman, Windows 11 24H2+ style)";

    public bool IsValid => Progman != IntPtr.Zero && WorkerW != IntPtr.Zero;
    public bool IsProgmanChild => Kind == ProgmanChildKind;
}

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_POPUP = 0x80000000L;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_SYSMENU = 0x00080000L;
    public const long WS_MINIMIZEBOX = 0x00020000L;
    public const long WS_MAXIMIZEBOX = 0x00010000L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;

    public const int SW_SHOWNA = 8;
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const uint SPI_GETDESKWALLPAPER = 0x0073;
    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    private const uint ProgmanSpawnWorkerW = 0x052C;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public static bool HasExtendedStyle(IntPtr hwnd, long style) =>
        hwnd != IntPtr.Zero && (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & style) != 0;

    public static IntPtr GetLastChildWindow(IntPtr parent)
    {
        var last = IntPtr.Zero;
        EnumChildWindows(parent, (child, _) => { last = child; return true; }, IntPtr.Zero);
        return last;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string? pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    public static string GetCurrentWallpaperViaSpi()
    {
        var buffer = new StringBuilder(520);
        return SystemParametersInfoGet(SPI_GETDESKWALLPAPER, (uint)buffer.Capacity, buffer, 0) ? buffer.ToString() : "(SPI_GETDESKWALLPAPER failed)";
    }

    public static string DescribeDpiAwareness()
    {
        try
        {
            return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) switch
            {
                0 => "unaware",
                1 => "system-aware",
                2 => "per-monitor-aware",
                var other => $"unknown ({other})"
            };
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return "unavailable";
        }
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "?";
    }

    public static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";
        var rect = GetWindowRect(hwnd, out var r)
            ? $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) size={r.Right - r.Left}x{r.Bottom - r.Top}"
            : "rect=?";
        return $"hwnd=0x{hwnd.ToInt64():X} class={ClassOf(hwnd)} exists={IsWindow(hwnd)} visible={IsWindowVisible(hwnd)} {rect}";
    }

    /// <summary>Human-readable snapshot of the shell windows that matter for desktop attachment (for the log).</summary>
    public static IReadOnlyList<string> DescribeShell()
    {
        var lines = new List<string>();
        var progman = FindWindow("Progman", null);
        lines.Add("Progman: " + DescribeWindow(progman));
        if (progman != IntPtr.Zero)
        {
            EnumChildWindows(progman, (child, _) =>
            {
                if (GetParent(child) == progman) lines.Add("  Progman child: " + DescribeWindow(child));
                return true;
            }, IntPtr.Zero);
        }
        EnumWindows((top, _) =>
        {
            var hostsDefView = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
            if (hostsDefView || ClassOf(top) == "WorkerW")
                lines.Add($"  Top-level: {DescribeWindow(top)} hostsIconView={hostsDefView}");
            return true;
        }, IntPtr.Zero);
        return lines;
    }

    /// <summary>
    /// Asks Progman to spawn the WorkerW wallpaper layer (message 0x052C) and locates it.
    /// Classic layout (Windows 10, Windows 11 before 24H2): WorkerW is a top-level window following the
    /// top-level window that hosts SHELLDLL_DefView. Newer layout: WorkerW is a child of Progman.
    /// </summary>
    public static ShellLayout DiscoverShell()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return new ShellLayout(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, false, "none", "The Windows desktop shell window (Progman) was not found. Is explorer.exe running?");

        var sendResult = SendMessageTimeout(progman, ProgmanSpawnWorkerW, new UIntPtr(0xD), new IntPtr(1), 0, 1000, out _);
        var sendError = Marshal.GetLastWin32Error();
        AppLog.Info($"Sent 0x052C to Progman: result={sendResult}, win32 error={sendError}");

        var isRaisedDesktop = HasExtendedStyle(progman, WS_EX_NOREDIRECTIONBITMAP);
        var classicWorkerW = IntPtr.Zero;
        var defViewHost = IntPtr.Zero;
        var shellDefView = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                defViewHost = top;
                shellDefView = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                var sibling = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                if (sibling != IntPtr.Zero) classicWorkerW = sibling;
            }
            return true;
        }, IntPtr.Zero);

        // Classic layout only counts when the icon view really moved out of Progman into its own top-level window.
        if (classicWorkerW != IntPtr.Zero && defViewHost != progman)
            return new ShellLayout(progman, classicWorkerW, shellDefView, isRaisedDesktop, ShellLayout.ClassicKind, null);

        var childWorkerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (childWorkerW != IntPtr.Zero)
        {
            shellDefView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            return new ShellLayout(progman, childWorkerW, shellDefView, isRaisedDesktop, ShellLayout.ProgmanChildKind, null);
        }

        return new ShellLayout(progman, IntPtr.Zero, shellDefView, isRaisedDesktop, "none",
            "Windows did not create the desktop wallpaper layer (WorkerW) after the 0x052C request, so a video wallpaper cannot be attached on this system.");
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    /// <summary>
    /// Asks Windows to move this process's unused pages out of RAM (they are paged back in on demand).
    /// Used when the app has been idle in the background, so it does not hold memory it is not using.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch (Exception) { /* best effort */ }
    }
}
