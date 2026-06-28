using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RainExplorer.Services;

/// <summary>
/// Asks the Windows Shell for the same thumbnail/preview bitmap Explorer shows for
/// a file — works for images, videos (a frame), PDFs and Office docs (when a
/// thumbnail handler is installed), falling back to the file-type icon otherwise.
/// Uses <c>IShellItemImageFactory::GetImage</c>. All work happens on a dedicated
/// STA thread because some shell thumbnail handlers require single-threaded apartment.
/// </summary>
public static class ShellThumbnailService
{
    /// <summary>Get a thumbnail no larger than <paramref name="size"/>px, or null if none/error.</summary>
    public static Task<BitmapSource?> GetThumbnailAsync(string path, int size, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BitmapSource?>();
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                tcs.SetResult(GetThumbnail(path, size));
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(); }
            catch (Exception) { tcs.TrySetResult(null); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static BitmapSource? GetThumbnail(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (factory is null) return null;

            // No ICONONLY/THUMBNAILONLY flag => thumbnail if available, else the type icon.
            int hr = factory.GetImage(new SIZE(size, size), SIIGBF.ResizeToFit, out IntPtr hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try { return HBitmapToSource(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
        finally { if (factory is not null) Marshal.ReleaseComObject(factory); }
    }

    // HBITMAP -> BitmapSource via GetDIBits into a top-down 32bpp BGRA buffer.
    // (CreateBitmapSourceFromHBitmap paints transparent areas black, so we avoid it.)
    private static BitmapSource? HBitmapToSource(IntPtr hbm)
    {
        var bm = new BITMAP();
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        if (w <= 0 || h <= 0) return null;

        var bmi = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFO>(),
            biWidth = w,
            biHeight = -h,          // negative => top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,       // BI_RGB
        };

        int stride = w * 4;
        byte[] bits = new byte[stride * h];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bmi, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        // If the alpha channel is entirely zero the shell handed us an opaque image
        // with no alpha info — force it opaque so it isn't rendered fully transparent.
        bool hasAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) { if (bits[i] != 0) { hasAlpha = true; break; } }
        PixelFormat fmt;
        if (!hasAlpha)
        {
            for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
            fmt = PixelFormats.Bgra32;
        }
        else fmt = PixelFormats.Pbgra32;   // shell thumbnails are premultiplied

        var src = BitmapSource.Create(w, h, 96, 96, fmt, null, bits, stride);
        src.Freeze();
        return src;
    }

    // ---- Interop -----------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, ref BITMAP pv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
