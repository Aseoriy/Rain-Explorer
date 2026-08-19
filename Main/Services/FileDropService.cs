using System.IO;
using System.Windows;

using RainExplorer.Views;

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
        var incoming = Incoming(files, destDir);
        if (incoming.Length == 0) return DragDropEffects.None;

        if ((keys & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((keys & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;
        // A mixed-drive drop cannot be a single native move. Default to copy if
        // any incoming item is on another volume; Ctrl/Shift above remain explicit.
        return incoming.All(f => SameRoot(f, destDir)) ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    /// <summary>Run the drop (filtering no-ops), logging it to the activity center. Returns an error or null.</summary>
    public static async Task<string?> Perform(string[] files, string destDir, bool move,
        Window? owner = null)
    {
        if (!Directory.Exists(destDir)) return null;
        var incoming = Incoming(files, destDir);
        if (incoming.Length == 0) return null;

        var conflicts = incoming
            .Select(source =>
            {
                string name = Path.GetFileName(
                    source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return new TransferConflict(source, Path.Combine(destDir, name), Directory.Exists(source));
            })
            .Where(conflict => File.Exists(conflict.Destination) || Directory.Exists(conflict.Destination))
            .ToList();

        TransferConflictChoice conflictChoice = conflicts.Count == 0
            ? TransferConflictChoice.SkipAll
            : ConflictDialog.Ask(owner, move, conflicts);
        if (conflictChoice == TransferConflictChoice.Cancel) return null;

        var sourceIdentities = new Dictionary<string, FileIdentity?>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in incoming)
            sourceIdentities.TryAdd(source, FileIdentityService.TryGet(source));

        var ops = new FileOperationsService();
        var act = ActivityService.Instance.Begin(move ? "Move" : "Copy",
            $"{Summarize(incoming)} → {FolderName(destDir)}", move ? "arrow-right" : "clipboard");
        Func<IReadOnlyList<TransferConflict>, TransferConflictChoice> resolveConflicts =
            _ => conflictChoice;
        OpResult result;
        try
        {
            // Direct file APIs are materially faster for ordinary local drops than
            // the shell's dialog-oriented copy engine, and the UI stays responsive
            // while the activity center records the operation.
            result = await Task.Run(() => move
                ? ops.MoveIntoResult(incoming, destDir, resolveConflicts)
                : ops.CopyIntoResult(incoming, destDir, resolveConflicts));
        }
        catch (Exception ex)
        {
            result = OpResult.Fail(ex.Message);
        }

        RecordUndo(result, destDir, move, sourceIdentities);

        if (result.Canceled && string.IsNullOrWhiteSpace(result.Error))
            ActivityService.Instance.Cancel(act);
        else
            ActivityService.Instance.Complete(act, result.Ok, result.Error);
        return result.Error;
    }

    // Record the inverse so the transfer can be undone (only outputs that actually landed).
    private static void RecordUndo(OpResult result, string destDir, bool move,
        IReadOnlyDictionary<string, FileIdentity?> sourceIdentities)
    {
        var created = result.Created.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!move)
        {
            // The shell API does not return an ownership token for copied output.
            // A path-only undo could recycle a file another process created during the
            // copy, so ambiguous copy cleanup is deliberately left to the shell/user.
            return;
        }

        var outputs = result.Completed
            .Select(s => (Src: s, Dst: Path.Combine(destDir, Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar)))))
            // Never move back a destination that pre-dated this drop. It may contain
            // unrelated content, and undoing it wholesale would destroy that content.
            .Where(o => created.Contains(o.Dst))
            // Same-volume moves retain their Windows file identity. Matching it to the
            // source proves this output is ours instead of a path created by another app.
            .Where(o => sourceIdentities.TryGetValue(o.Src, out FileIdentity? sourceIdentity)
                && sourceIdentity is { } expected
                && FileIdentityService.Matches(o.Dst, expected))
            .Where(o => File.Exists(o.Dst) || Directory.Exists(o.Dst))
            .ToList();
        if (outputs.Count == 0) return;

        UndoService.Instance.Push(new MoveAction(outputs.Select(o => (Cur: o.Dst, Home: o.Src)).ToList()));
    }

    /// <summary>Drop items that aren't the destination itself or an immediate child
    /// already in that folder. Deeper descendants remain valid for breadcrumb moves.</summary>
    public static string[] Incoming(string[] files, string destDir) => files
        .Where(f => !string.IsNullOrWhiteSpace(f))
        .Where(f => !IsSamePath(f, destDir))
        // An item whose immediate parent is the destination is already in place.
        // Deeper descendants remain valid breadcrumb sources (for example moving
        // C:\A\B\file to the C:\A breadcrumb).
        .Where(f => !IsImmediateChildOf(f, destDir))
        // Reject an ancestor directory source: copying/moving it into one of its own
        // descendants cannot produce a valid result and would trigger a shell error.
        .Where(f => !Directory.Exists(f) || !IsWithin(f, destDir))
        .ToArray();

    private static bool IsSamePath(string left, string right)
    {
        try { return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsImmediateChildOf(string child, string parent)
    {
        try
        {
            string childParent = Path.GetDirectoryName(
                child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
            return IsSamePath(childParent, parent);
        }
        catch { return false; }
    }

    private static bool IsWithin(string parent, string child)
    {
        try
        {
            string p = Normalize(parent);
            string c = Normalize(child);
            return string.Equals(p, c, StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
