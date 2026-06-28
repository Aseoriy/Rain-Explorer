using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RainExplorer.Services;

/// <summary>
/// Bridges the Windows Explorer context menu for a set of file-system paths to
/// Rain Explorer. Two modes:
///  • <see cref="Show"/> pops the raw OS menu (used for Shift+F10 — full fidelity,
///    but it's Win32-rendered so it can't be themed).
///  • <see cref="Create"/> + <see cref="BuildItems"/> enumerate the shell menu and
///    return our own themed <see cref="MenuItem"/>s (so WinRAR / 7-Zip / "Send to"
///    appear inside our fluent menu), invoking the real verb on click.
/// All items must share one parent folder (always true for one selection). Built
/// on the shell's <c>IContextMenu</c> COM interfaces. Best-effort / no-throw.
/// </summary>
public sealed class ShellContextMenu : IDisposable
{
    private const uint IdFirst = 1, IdLast = 0x7FFF;

    private IntPtr _hwnd;
    private readonly List<IntPtr> _fullPidls = new();
    private IShellFolder? _parent;
    private IntPtr _hMenu;
    private IContextMenu? _ctx;
    private IContextMenu2? _ctx2;
    private IContextMenu3? _ctx3;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RainExplorer", "shellmenu.log");

    private static void Log(string s)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {s}{Environment.NewLine}"); }
        catch { }
    }

    // ---- Raw OS menu (Shift+F10) -------------------------------------------
    public static void Show(IReadOnlyList<string> paths, Window owner)
    {
        if (paths.Count == 0) return;
        var m = new ShellContextMenu();
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        try
        {
            if (!m.Setup(paths, owner)) return;
            source = HwndSource.FromHwnd(m._hwnd);
            if (source is not null) { hook = m.WndProcHook; source.AddHook(hook); }

            GetCursorPos(out POINT pt);
            uint cmd = TrackPopupMenuEx(m._hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, m._hwnd, IntPtr.Zero);
            if (source is not null && hook is not null) { source.RemoveHook(hook); hook = null; }
            if (cmd >= IdFirst) m.Invoke(cmd);
        }
        catch (Exception ex) { Log($"Show EXCEPTION: {ex.Message}"); }
        finally
        {
            if (source is not null && hook is not null) source.RemoveHook(hook);
            m.Dispose();
        }
    }

    // ---- Themed session ----------------------------------------------------
    public static ShellContextMenu? Create(IReadOnlyList<string> paths, Window owner)
    {
        if (paths.Count == 0) return null;
        var m = new ShellContextMenu();
        try
        {
            if (m.Setup(paths, owner)) return m;
            m.Dispose();
            return null;
        }
        catch (Exception ex) { Log($"Create EXCEPTION: {ex.Message}"); m.Dispose(); return null; }
    }

    /// <summary>Walk the native menu and return themed WPF menu controls.</summary>
    public List<Control> BuildItems()
    {
        try { return Walk(_hMenu, 0); }
        catch (Exception ex) { Log($"BuildItems EXCEPTION: {ex.Message}"); return new(); }
    }

    public void Invoke(uint cmdId)
    {
        if (_ctx is null) return;
        GetCursorPos(out POINT pt);
        try { InvokeVerb(_ctx, cmdId - IdFirst, _hwnd, pt); }
        catch (Exception ex) { Log($"Invoke EXCEPTION: {ex.Message}"); }
    }

    // ---- Native setup / teardown -------------------------------------------
    private bool Setup(IReadOnlyList<string> paths, Window owner)
    {
        _hwnd = new WindowInteropHelper(owner).Handle;

        foreach (var p in paths)
            if (SHParseDisplayName(p, IntPtr.Zero, out IntPtr full, 0, out _) == 0 && full != IntPtr.Zero)
                _fullPidls.Add(full);
        if (_fullPidls.Count == 0) return false;

        Guid iidFolder = IID_IShellFolder;
        if (SHBindToParent(_fullPidls[0], ref iidFolder, out IntPtr pParent, out IntPtr firstChild) != 0
            || pParent == IntPtr.Zero)
            return false;
        _parent = (IShellFolder)Marshal.GetObjectForIUnknown(pParent);
        Marshal.Release(pParent);

        var childPidls = new List<IntPtr> { firstChild };
        for (int i = 1; i < _fullPidls.Count; i++)
            if (SHBindToParent(_fullPidls[i], ref iidFolder, out IntPtr _, out IntPtr child) == 0)
                childPidls.Add(child);

        Guid iidCtx = IID_IContextMenu;
        var apidl = childPidls.ToArray();
        if (_parent.GetUIObjectOf(_hwnd, (uint)apidl.Length, apidl, ref iidCtx, IntPtr.Zero, out IntPtr pCtx) != 0
            || pCtx == IntPtr.Zero)
            return false;

        _ctx = (IContextMenu)Marshal.GetObjectForIUnknown(pCtx);
        Marshal.Release(pCtx);
        _ctx2 = _ctx as IContextMenu2;
        _ctx3 = _ctx as IContextMenu3;

        _hMenu = CreatePopupMenu();
        _ctx.QueryContextMenu(_hMenu, 0, IdFirst, IdLast, CMF_NORMAL | CMF_EXPLORE);
        return true;
    }

    public void Dispose()
    {
        if (_hMenu != IntPtr.Zero) { DestroyMenu(_hMenu); _hMenu = IntPtr.Zero; }
        foreach (var p in _fullPidls) if (p != IntPtr.Zero) ILFree(p);
        _fullPidls.Clear();
        if (_ctx is not null) { Marshal.ReleaseComObject(_ctx); _ctx = null; }
        if (_parent is not null) { Marshal.ReleaseComObject(_parent); _parent = null; }
        _ctx2 = null; _ctx3 = null;
    }

    // ---- Menu enumeration ---------------------------------------------------
    private List<Control> Walk(IntPtr menu, int depth)
    {
        var items = new List<Control>();
        int count = GetMenuItemCount(menu);
        if (count <= 0) return items;

        for (int pos = 0; pos < count; pos++)
        {
            var mii = new MENUITEMINFO
            {
                cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_BITMAP,
            };
            if (!GetMenuItemInfo(menu, (uint)pos, true, ref mii)) continue;

            if ((mii.fType & MFT_SEPARATOR) != 0)
            {
                if (items.Count > 0 && items[^1] is not Separator) items.Add(new Separator());
                continue;
            }

            string text = ReadText(menu, pos);
            if (string.IsNullOrWhiteSpace(text)) continue;

            bool isLeaf = mii.hSubMenu == IntPtr.Zero;

            // At the top level, hide entries Rain Explorer already provides itself
            // (Open/Cut/Copy/Paste/Delete/Rename/Properties/Create shortcut/Open with)
            // so the menu only shows the genuine *extra* shell items.
            if (depth == 0 && isLeaf && IsStandardVerb(mii.wID)) continue;

            var item = new MenuItem { Header = Clean(text) };
            if ((mii.fState & (MFS_DISABLED | MFS_GRAYED)) != 0) item.IsEnabled = false;

            var icon = IconFromHBitmap(mii.hbmpItem);
            if (icon is not null)
                item.Icon = new Image { Source = icon, Width = 16, Height = 16,
                                        SnapsToDevicePixels = true };

            if (!isLeaf && depth < 5)
            {
                try { _ctx2?.HandleMenuMsg(WM_INITMENUPOPUP, mii.hSubMenu, (IntPtr)pos); } catch { }
                foreach (var child in Walk(mii.hSubMenu, depth + 1)) item.Items.Add(child);
                if (item.Items.Count == 0) continue;   // empty submenu — drop it
            }
            else
            {
                uint id = mii.wID;
                item.Click += (_, e) => { e.Handled = true; Invoke(id); };
            }
            items.Add(item);
        }

        while (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
        return items;
    }

    private static string ReadText(IntPtr menu, int pos)
    {
        var sb = new StringBuilder(320);
        int n = GetMenuStringW(menu, (uint)pos, sb, sb.Capacity, MF_BYPOSITION);
        return n > 0 ? sb.ToString() : string.Empty;
    }

    // Canonical verbs of the items we already expose in our own menu.
    private static readonly HashSet<string> StdVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennew", "edit", "explore", "find", "cut", "copy", "paste",
        "delete", "rename", "properties", "link", "openas", "cutto", "copyto", "pintohome",
    };

    private bool IsStandardVerb(uint id)
    {
        if (_ctx is null || id < IdFirst) return false;
        var sb = new StringBuilder(128);
        try
        {
            if (_ctx.GetCommandString((UIntPtr)(id - IdFirst), GCS_VERBW, IntPtr.Zero, sb, (uint)sb.Capacity) != 0)
                return false;   // no canonical verb -> a custom/extra item, keep it
            return StdVerbs.Contains(sb.ToString());
        }
        catch { return false; }
    }

    private static string Clean(string s)
    {
        int tab = s.IndexOf('\t');
        if (tab >= 0) s = s[..tab];
        // Strip access-key ampersands ("&File" -> "File", "&&" -> "&").
        s = s.Replace("&&", "\x01").Replace("&", "").Replace("\x01", "&");
        return s.Trim();
    }

    /// <summary>
    /// Convert a menu item's <c>hbmpItem</c> HBITMAP to a WPF image. Pulls the raw
    /// 32bpp pixels via GetDIBits so alpha is handled correctly (a plain
    /// CreateBitmapSourceFromHBitmap renders transparent areas as black). Returns
    /// null for the HBMMENU_* magic handles or non-bitmap items.
    /// </summary>
    private static ImageSource? IconFromHBitmap(IntPtr hbmp)
    {
        long magic = hbmp.ToInt64();
        if (magic > -16 && magic < 16) return null;   // null / HBMMENU_* system values

        try
        {
            var bm = new BITMAP();
            if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,        // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,    // BI_RGB
            };

            int stride = w * 4;
            byte[] bits = new byte[stride * h];
            IntPtr hdc = GetDC(IntPtr.Zero);
            IntPtr pbi = Marshal.AllocHGlobal(header.biSize + 256 * 4);
            try
            {
                Marshal.StructureToPtr(header, pbi, false);
                if (GetDIBits(hdc, hbmp, 0, (uint)h, bits, pbi, 0) == 0) return null;
            }
            finally
            {
                Marshal.FreeHGlobal(pbi);
                ReleaseDC(IntPtr.Zero, hdc);
            }

            // Some shell bitmaps carry no real alpha (channel all-zero) and rely on a
            // mask — those would come out fully transparent, so force them opaque.
            bool anyAlpha = false;
            for (int i = 3; i < bits.Length; i += 4)
                if (bits[i] != 0) { anyAlpha = true; break; }

            PixelFormat fmt;
            if (!anyAlpha)
            {
                for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
                fmt = PixelFormats.Bgra32;
            }
            else
            {
                fmt = PixelFormats.Pbgra32;   // shell icons are premultiplied
            }

            var src = BitmapSource.Create(w, h, 96, 96, fmt, null, bits, stride);
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
            return IntPtr.Zero;
        try
        {
            if (_ctx3 is not null)
            {
                if (_ctx3.HandleMenuMsg2((uint)msg, wParam, lParam, out IntPtr res) == 0) { handled = true; return res; }
                return IntPtr.Zero;
            }
            if (_ctx2 is not null)
            {
                _ctx2.HandleMenuMsg((uint)msg, wParam, lParam);
                handled = true;
                return (msg is WM_DRAWITEM or WM_MEASUREITEM) ? new IntPtr(1) : IntPtr.Zero;
            }
        }
        catch (Exception ex) { Log($"hook EXCEPTION msg=0x{msg:X}: {ex.Message}"); }
        return IntPtr.Zero;
    }

    private static void InvokeVerb(IContextMenu ctx, uint offset, IntPtr hwnd, POINT pt)
    {
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
            hwnd = hwnd,
            lpVerb = (IntPtr)offset,
            lpVerbW = (IntPtr)offset,
            nShow = SW_SHOWNORMAL,
            ptInvoke = pt,
        };
        ctx.InvokeCommand(ref info);
    }

    // ===================== interop =====================
    private const uint CMF_NORMAL = 0x0000, CMF_EXPLORE = 0x0004;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const int CMIC_MASK_UNICODE = unchecked((int)0x00004000);
    private const int CMIC_MASK_PTINVOKE = unchecked((int)0x20000000);
    private const int SW_SHOWNORMAL = 1;
    private const int WM_INITMENUPOPUP = 0x0117, WM_DRAWITEM = 0x002B, WM_MEASUREITEM = 0x002C, WM_MENUCHAR = 0x0120;
    private const uint MF_BYPOSITION = 0x0400;
    private const uint GCS_VERBW = 0x00000004;
    private const uint MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_BITMAP = 0x80, MIIM_FTYPE = 0x100;
    private const uint MFT_SEPARATOR = 0x800;
    private const uint MFS_GRAYED = 0x3, MFS_DISABLED = 0x2;

    private static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext,
        out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
        out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")] private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO mii);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int nMaxCount, uint uFlag);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        [Out] byte[] lpvBits, IntPtr lpbmi, uint usage);

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl,
            [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
            [Out] StringBuilder pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }
}
