using System.IO;
using System.Windows;

namespace RainExplorer.Services;

/// <summary>
/// Shared logic for dropping OS file-drop payloads into a destination folder —
/// used by the file list, the tab strip, and the sidebar so they all behave
/// identically (same copy/move rule, same no-op filtering, same activity log).
/// </summary>
public static class FileDropService
{
    /// <summary>Ctrl = copy, Shift = move; otherwise move within a drive, copy across drives.
    /// Returns None when the drop would be a no-op or the destination isn't a real folder.</summary>
    public static DragDropEffects EffectFor(string[]? files, string? destDir, DragDropKeyStates keys)
    {
        if (files is null || files.Length == 0) return DragDropEffects.None;
        if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir)) return DragDropEffects.None;
        if (Incoming(files, destDir).Length == 0) return DragDropEffects.None;

        if ((keys & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((keys & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;
        return SameRoot(files[0], destDir) ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    /// <summary>Run the drop (filtering no-ops), logging it to the activity center. Returns an error or null.</summary>
    public static string? Perform(string[] files, string destDir, bool move)
    {
        if (!Directory.Exists(destDir)) return null;
        var incoming = Incoming(files, destDir);
        if (incoming.Length == 0) return null;

        var ops = new FileOperationsService();
        var act = ActivityService.Instance.Begin(move ? "Move" : "Copy",
            $"{Summarize(incoming)} → {FolderName(destDir)}", move ? "arrow-right" : "clipboard");
        string? err = move ? ops.MoveInto(incoming, destDir) : ops.CopyInto(incoming, destDir);
        ActivityService.Instance.Complete(act, err is null, err);
        if (err is null) RecordUndo(incoming, destDir, move);
        return err;
    }

    // Record the inverse so the transfer can be undone (only outputs that actually landed).
    private static void RecordUndo(string[] incoming, string destDir, bool move)
    {
        var outputs = incoming
            .Select(s => (Src: s, Dst: Path.Combine(destDir, Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar)))))
            .Where(o => File.Exists(o.Dst) || Directory.Exists(o.Dst))
            .ToList();
        if (outputs.Count == 0) return;

        if (move)
            UndoService.Instance.Push(new MoveAction(outputs.Select(o => (Cur: o.Dst, Home: o.Src)).ToList()));
        else
            UndoService.Instance.Push(new RecycleAction(outputs.Select(o => o.Dst), "Copy"));
    }

    /// <summary>Drop items that aren't the destination itself and aren't already inside it.</summary>
    public static string[] Incoming(string[] files, string destDir) => files
        .Where(f => !string.Equals(f.TrimEnd(Path.DirectorySeparatorChar), destDir, StringComparison.OrdinalIgnoreCase))
        .Where(f => !string.Equals(Path.GetDirectoryName(f.TrimEnd(Path.DirectorySeparatorChar)) ?? "",
                                   destDir, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private static bool SameRoot(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string Summarize(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar)) : $"{paths.Count} items";

    private static string FolderName(string dir)
    {
        string n = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(n) ? dir : n;
    }
}
