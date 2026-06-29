using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RainExplorer.Services;

/// <summary>
/// Resolves the real Windows associated icon for a file or folder — the same icon
/// Explorer shows (an .exe's embedded icon, a registered file-type icon, the folder
/// icon) instead of a generic glyph. Backed by the classic <c>SHGetFileInfo</c> API,
/// which is fast and synchronous; results are cached so a folder of hundreds of
/// items only pays once per distinct icon.
///
/// Caching strategy:
///   • Folders share one cached generic folder icon (resolved by attributes, no disk
///     hit) — keeps big trees cheap.
///   • Files that embed their own icon (exe/lnk/ico/…) are cached per full path.
///   • All other files are cached per extension (resolved by attributes, no disk hit).
/// </summary>
public static class ShellIconService
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    // Extensions whose icon is embedded in / specific to the individual file, so we
    // must query the real path rather than reusing an extension-level icon.
    private static readonly HashSet<string> PerFileExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".cur", ".ani", ".scr", ".msc", ".cpl", ".url",
    };

    /// <summary>
    /// Fetch the icon for <paramref name="path"/>. Returns instantly from cache when
    /// possible; otherwise resolves it on a background thread and invokes
    /// <paramref name="onLoaded"/> on the UI thread (only when an icon was found).
    /// </summary>
    public static void LoadAsync(string path, bool isDirectory, Action<BitmapSource> onLoaded)
    {
        string key = CacheKey(path, isDirectory);

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                if (cached is not null) onLoaded(cached);
                return;
            }
        }

        Task.Run(() =>
        {
            BitmapSource? img = null;
            try { img = Resolve(path, isDirectory); } catch { /* fall back to the vector glyph */ }
            if (img is not null && img.CanFreeze) img.Freeze();

            lock (Gate) { Cache[key] = img; }

            if (img is not null)
                Application.Current?.Dispatcher.BeginInvoke(() => onLoaded(img));
        });
    }

    private static string CacheKey(string path, bool isDirectory)
    {
        if (isDirectory) return "::dir";
        string ext = Path.GetExtension(path);
        return PerFileExts.Contains(ext) ? path : (ext.Length == 0 ? "::noext" : ext.ToLowerInvariant());
    }

    private static BitmapSource? Resolve(string path, bool isDirectory)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;

        // Folders + ordinary files can be resolved purely from attributes (no disk
        // touch). Files that carry a unique icon must hit the real path.
        bool byAttributes = isDirectory || !PerFileExts.Contains(Path.GetExtension(path));
        uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        if (byAttributes) flags |= SHGFI_USEFILEATTRIBUTES;

        string query = byAttributes ? (isDirectory ? "folder" : Path.GetExtension(path)) : path;

        IntPtr res = SHGetFileInfo(query, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally { DestroyIcon(info.hIcon); }
    }

    // ---- Win32 -------------------------------------------------------------
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
