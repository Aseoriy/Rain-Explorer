using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RainExplorer.ViewModels;

namespace RainExplorer.Services;

/// <summary>
/// A reversible file operation. <see cref="Invoke"/> performs the inverse and
/// returns an error (or null) plus a redo action that re-applies the original
/// (or null when redo isn't supported, e.g. for create/copy undos).
/// </summary>
public abstract class UndoAction
{
    /// <summary>Short human label, e.g. "Move", "Rename", "Copy".</summary>
    public abstract string Label { get; }

    /// <summary>Run the inverse. Returns (error, redo-action-or-null).</summary>
    public abstract (string? error, UndoAction? redo) Invoke();

    private protected static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);
}

/// <summary>Undo a move by moving each item back to its original folder.</summary>
public sealed class MoveAction : UndoAction
{
    // Each item currently sits at Cur; undo moves it into the folder of Home.
    private readonly List<(string Cur, string Home, FileIdentity? Identity)> _items;
    public MoveAction(List<(string Cur, string Home)> items) =>
        _items = items.Select(item =>
            (item.Cur, item.Home, FileIdentityService.TryGet(item.Cur))).ToList();

    public override string Label => _items.Count == 1 ? "Move" : $"Move ({_items.Count} items)";

    public override (string?, UndoAction?) Invoke()
    {
        var ops = new FileOperationsService();
        foreach (var (cur, home, identity) in _items)
        {
            if (!PathExists(cur)) continue;
            if (identity is null || !FileIdentityService.Matches(cur, identity.Value))
                return ("An item at the destination changed, so undo was skipped.", null);
            if (PathExists(home))
                return ("An item now exists at the original location, so undo was skipped.", null);
        }

        string? err = null;
        var redo = new List<(string Cur, string Home)>();
        foreach (var (cur, home, identity) in _items)
        {
            if (!PathExists(cur)) continue;
            if (identity is null || !FileIdentityService.Matches(cur, identity.Value)
                || PathExists(home))
                return ("An item changed while undo was starting, so undo stopped.", null);
            string homeParent = Path.GetDirectoryName(home.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
            if (homeParent.Length == 0) continue;
            string? e = ops.MoveInto(new[] { cur }, homeParent);
            if (e is not null) { err ??= e; continue; }
            redo.Add((Cur: home, Home: cur));   // now back at Home; redo moves it to Cur again
        }
        return (err, redo.Count > 0 ? new MoveAction(redo) : null);
    }
}

/// <summary>Undo a rename by renaming the item back to its previous name.</summary>
public sealed class RenameAction : UndoAction
{
    private readonly string _cur;    // path after the rename being undone
    private readonly string _home;   // path it should return to
    private readonly FileIdentity? _identity;
    public RenameAction(string cur, string home)
    {
        _cur = cur;
        _home = home;
        _identity = FileIdentityService.TryGet(cur);
    }

    public override string Label => "Rename";

    public override (string?, UndoAction?) Invoke()
    {
        if (!PathExists(_cur)) return ("Item no longer exists.", null);
        if (_identity is null || !FileIdentityService.Matches(_cur, _identity.Value))
            return ("The renamed item changed, so undo was skipped.", null);
        if (PathExists(_home))
            return ("An item now exists under the original name, so undo was skipped.", null);
        var ops = new FileOperationsService();
        string? e = ops.Rename(_cur, Path.GetFileName(_home.TrimEnd(Path.DirectorySeparatorChar)));
        if (e is not null) return (e, null);
        return (null, new RenameAction(_home, _cur));
    }
}

/// <summary>Recycle a set of paths (the undo of a create/copy). Redo restores them from the bin.</summary>
public sealed class RecycleAction : UndoAction
{
    private readonly List<(string Path, FileIdentity? Identity)> _paths;
    private readonly string _label;
    public RecycleAction(IEnumerable<string> paths, string label)
    {
        _paths = paths.Select(path => (path, FileIdentityService.TryGet(path))).ToList();
        _label = label;
    }

    public override string Label => _label;

    public override (string?, UndoAction?) Invoke()
    {
        var existing = new List<string>();
        foreach (var item in _paths)
        {
            if (!PathExists(item.Path)) continue;
            if (item.Identity is null || !FileIdentityService.Matches(item.Path, item.Identity.Value))
                return ("An item at the destination changed, so undo was skipped.", null);
            existing.Add(item.Path);
        }
        if (existing.Count == 0) return (null, null);
        foreach (var item in _paths)
        {
            if (PathExists(item.Path)
                && (item.Identity is null || !FileIdentityService.Matches(item.Path, item.Identity.Value)))
                return ("An item changed while undo was starting, so undo stopped.", null);
        }
        var res = new FileOperationsService().Delete(existing);   // to the Recycle Bin
        if (!res.Ok) return (res.Error, null);
        return (null, new RestoreFromBinAction(existing, _label));
    }
}

internal readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex);

internal static class FileIdentityService
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static FileIdentity? TryGet(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using SafeFileHandle handle = CreateFileW(
                path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
                OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                return null;
            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return new FileIdentity(info.VolumeSerialNumber, index);
        }
        catch { return null; }
    }

    public static bool Matches(string path, FileIdentity expected) =>
        TryGet(path) is { } current && current == expected;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation information);
}

/// <summary>Restore a set of paths from the Recycle Bin (the undo of a delete). Redo recycles them again.</summary>
public sealed class RestoreFromBinAction : UndoAction
{
    private readonly List<string> _paths;
    private readonly string _label;
    public RestoreFromBinAction(IEnumerable<string> paths, string label)
    {
        _paths = paths.ToList();
        _label = label;
    }

    public override string Label => _label;

    public override (string?, UndoAction?) Invoke()
    {
        string? err = null;
        var restored = new List<string>();
        foreach (var p in _paths)
        {
            var (e, finalPath) = RecycleBinService.Restore(p);
            if (e is not null) { err ??= e; continue; }
            if (finalPath is not null) restored.Add(finalPath);
        }
        UndoAction? redo = restored.Count > 0 ? new RecycleAction(restored, _label) : null;
        return (err, redo);
    }
}

/// <summary>
/// Session undo/redo stack for file operations. Singleton + observable so the
/// title-bar Undo button can reflect availability and the next action's label.
/// </summary>
public sealed class UndoService : ObservableObject
{
    public static UndoService Instance { get; } = new();
    private const int Cap = 50;

    // Tail = top of stack.
    private readonly List<UndoAction> _undo = new();
    private readonly List<UndoAction> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoText => _undo.Count > 0 ? $"Undo {_undo[^1].Label} (Ctrl+Z)" : "Nothing to undo";
    public string RedoText => _redo.Count > 0 ? $"Redo {_redo[^1].Label} (Ctrl+Y)" : "Nothing to redo";

    /// <summary>Record a completed operation so it can be undone.</summary>
    public void Push(UndoAction action)
    {
        _undo.Add(action);
        if (_undo.Count > Cap) _undo.RemoveAt(0);
        _redo.Clear();
        Changed();
    }

    /// <summary>Undo the most recent operation. Returns an error message or null.</summary>
    public string? Undo() => Run(_undo, _redo);

    /// <summary>Redo the most recently undone operation. Returns an error message or null.</summary>
    public string? Redo() => Run(_redo, _undo);

    private string? Run(List<UndoAction> from, List<UndoAction> to)
    {
        if (from.Count == 0) return null;
        var action = from[^1];

        var entry = ActivityService.Instance.Begin(
            ReferenceEquals(from, _undo) ? "Undo" : "Redo", action.Label, "undo");
        string? err = null;
        UndoAction? inverse = null;
        try
        {
            (err, inverse) = action.Invoke();
        }
        catch (Exception ex)
        {
            err = ex.Message;
        }
        ActivityService.Instance.Complete(entry, err is null, err);

        // Keep a failed action available for another attempt. An inverse may describe
        // only a partial operation, so it must not be promoted to the other stack when
        // the original action did not finish successfully.
        if (err is null)
        {
            from.RemoveAt(from.Count - 1);
            if (inverse is not null) to.Add(inverse);
        }
        Changed();
        return err;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
    }
}
