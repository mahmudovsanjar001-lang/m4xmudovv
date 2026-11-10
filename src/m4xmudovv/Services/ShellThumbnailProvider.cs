using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace m4xmudovv.Services;

/// <summary>
/// Asks the Windows Shell (the same component Explorer uses) for a video thumbnail via
/// IShellItemImageFactory. This runs off the UI thread and does not depend on WPF's MediaPlayer
/// having decoded a frame. Returns null when the shell has no thumbnail handler for the file.
/// </summary>
internal static class ShellThumbnailProvider
{
    [Flags]
    private enum Siigbf : int
    {
        ResizeToFit = 0x00,
        ThumbnailOnly = 0x08
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, Siigbf flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public static Task<BitmapSource?> TryGetAsync(string path, int width, int height)
    {
        var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Shell thumbnail handlers are COM components; give them an STA thread.
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Extract(path, width, height)); }
            catch (Exception) { completion.TrySetResult(null); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static BitmapSource? Extract(string path, int width, int height)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        try
        {
            var size = new NativeSize { Cx = width, Cy = height };
            var hr = factory.GetImage(size, Siigbf.ThumbnailOnly, out var hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(hbitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
