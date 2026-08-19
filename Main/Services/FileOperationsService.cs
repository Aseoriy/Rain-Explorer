using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace RainExplorer.Services;

/// <summary>Outcome of a mutating file operation, distinguishing a user cancellation (e.g.
/// clicking "No" on the Windows confirm dialog) from a genuine success or failure — so the
/// activity center never reports "done" for something that didn't actually happen.</summary>
public enum OpOutcome { Ok, Canceled, Failed }

public readonly record struct OpResult(
    OpOutcome Outcome,
    string? Error,
    IReadOnlyList<string>? CompletedPaths = null,
    IReadOnlyList<string>? CreatedPaths = null)
{
    public bool Ok => Outcome == OpOutcome.Ok;
    public bool Canceled => Outcome == OpOutcome.Canceled;
    public IReadOnlyList<string> Completed => CompletedPaths ?? Array.Empty<string>();
    public IReadOnlyList<string> Created => CreatedPaths ?? Array.Empty<string>();
    public static readonly OpResult Success = new(OpOutcome.Ok, null, Array.Empty<string>());
    public static readonly OpResult Cancelled = new(OpOutcome.Canceled, null, Array.Empty<string>());
    public static OpResult Fail(string? error) => new(OpOutcome.Failed, error);
}

/// <summary>A destination collision discovered before a drop starts.</summary>
public readonly record struct TransferConflict(
    string Source, string Destination, bool IsDirectory);

/// <summary>The one decision applied to all collisions in a transfer.</summary>
public enum TransferConflictChoice
{
    ReplaceAll,
    SkipAll,
    Cancel
}

/// <summary>
/// Mutating file operations. Deletes intentionally delegate to the Windows shell
/// engine so Recycle Bin behavior remains intact. Transfers without a conflict
/// resolver retain the legacy shell behavior; the Rain drag/drop path supplies a
/// resolver and uses direct file APIs so its single custom conflict choice applies
/// to the complete operation.
/// </summary>
public sealed class FileOperationsService
{
    /// <summary>Send paths to the Recycle Bin without Windows asking once per item.
    /// Windows still owns genuine error dialogs.</summary>
    public OpResult Delete(IEnumerable<string> paths)
    {
        var completed = new List<string>();
        try
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                    completed.Add(p);
                }
                else if (File.Exists(p))
                {
                    FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                    completed.Add(p);
                }
            }
            return new OpResult(OpOutcome.Ok, null, completed);
        }
        catch (OperationCanceledException) { return new(OpOutcome.Canceled, null, completed); }
        catch (Exception ex) { return new(OpOutcome.Failed, ex.Message, completed); }
    }

    /// <summary>Permanently delete paths (bypasses the Recycle Bin — not undoable).
    /// The caller supplies the single themed confirmation; Windows only surfaces errors.</summary>
    public OpResult DeletePermanent(IEnumerable<string> paths)
    {
        var completed = new List<string>();
        try
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently,
                        UICancelOption.ThrowException);
                    completed.Add(p);
                }
                else if (File.Exists(p))
                {
                    FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently,
                        UICancelOption.ThrowException);
                    completed.Add(p);
                }
            }
            return new OpResult(OpOutcome.Ok, null, completed);
        }
        catch (OperationCanceledException) { return new(OpOutcome.Canceled, null, completed); }
        catch (Exception ex) { return new(OpOutcome.Failed, ex.Message, completed); }
    }

    public string? CopyInto(IEnumerable<string> sources, string destDir) => LegacyTransfer(sources, destDir, move: false);

    public string? MoveInto(IEnumerable<string> sources, string destDir) => LegacyTransfer(sources, destDir, move: true);

    /// <summary>Copy with an explicit outcome and the source paths that actually landed.</summary>
    public OpResult CopyIntoResult(IEnumerable<string> sources, string destDir,
        Func<IReadOnlyList<TransferConflict>, TransferConflictChoice>? resolveConflicts = null) =>
        Transfer(sources, destDir, move: false, resolveConflicts);

    /// <summary>Move with an explicit outcome and the source paths that actually landed.</summary>
    public OpResult MoveIntoResult(IEnumerable<string> sources, string destDir,
        Func<IReadOnlyList<TransferConflict>, TransferConflictChoice>? resolveConflicts = null) =>
        Transfer(sources, destDir, move: true, resolveConflicts);

    private static string? LegacyTransfer(IEnumerable<string> sources, string destDir, bool move)
    {
        var result = Transfer(sources, destDir, move);
        return result.Outcome == OpOutcome.Canceled ? "Operation cancelled." : result.Error;
    }

    private static OpResult Transfer(IEnumerable<string> sources, string destDir, bool move,
        Func<IReadOnlyList<TransferConflict>, TransferConflictChoice>? resolveConflicts = null)
    {
        var completed = new List<string>();
        var created = new List<string>();
        if (sources is null || string.IsNullOrWhiteSpace(destDir) || !Directory.Exists(destDir))
            return OpResult.Fail("Destination folder not found.");

        var items = new List<TransferItem>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string src in sources)
            {
                if (string.IsNullOrWhiteSpace(src))
                    return new OpResult(OpOutcome.Failed, "A source path is empty.", completed, created);

                bool sourceIsDirectory = Directory.Exists(src);
                bool sourceIsFile = File.Exists(src);
                if (!sourceIsDirectory && !sourceIsFile)
                    return new OpResult(OpOutcome.Failed,
                        $"Source item no longer exists: {Path.GetFileName(src)}", completed, created);

                string name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name))
                    return new OpResult(OpOutcome.Failed, "Source path has no item name.", completed, created);
                string dest = Path.Combine(destDir, name);

                // Multiple source folders can contain the same name. Letting both target
                // one path makes a move impossible to undo safely, so reject it up front.
                if (!destinations.Add(dest))
                    return new OpResult(OpOutcome.Failed,
                        $"More than one source item is named \"{name}\".", completed, created);

                // Don't copy/move a folder into itself or its own subtree.
                if (sourceIsDirectory &&
                    (PathsEqual(src, destDir) || IsSubPath(src, destDir)))
                    return new OpResult(OpOutcome.Failed, "Can't move a folder into itself.", completed, created);

                items.Add(new TransferItem(src, dest, sourceIsDirectory));
            }
        }
        catch (Exception ex) { return new OpResult(OpOutcome.Failed, ex.Message, completed, created); }

        if (items.Count == 0) return OpResult.Fail("No source items were provided.");

        // The drag/drop surface supplies a resolver so it can show one themed Rain
        // dialog for the whole operation. Keep the shell-backed path below for the
        // older non-UI helpers (notably undo) that do not supply one.
        if (resolveConflicts is not null)
            return TransferWithoutShell(items, move, resolveConflicts, completed, created);

        foreach (TransferItem item in items)
        {
            // A source can disappear after preflight but before its turn starts.
            if (!PathExists(item.Source))
                return new OpResult(OpOutcome.Failed,
                    $"Source item no longer exists: {Path.GetFileName(item.Source)}", completed, created);

            // Snapshot as close to the shell call as possible. A destination that
            // appeared since preflight is a conflict, not output owned by this action.
            bool destinationExisted = PathExists(item.Destination);
            try
            {
                if (item.IsDirectory)
                {
                    if (move) FileSystem.MoveDirectory(item.Source, item.Destination,
                        UIOption.AllDialogs, UICancelOption.ThrowException);
                    else FileSystem.CopyDirectory(item.Source, item.Destination,
                        UIOption.AllDialogs, UICancelOption.ThrowException);
                }
                else
                {
                    // ThrowException lets us distinguish a cancelled shell dialog from
                    // a completed operation; DoNothing made cancellation look successful.
                    if (move) FileSystem.MoveFile(item.Source, item.Destination,
                        UIOption.AllDialogs, UICancelOption.ThrowException);
                    else FileSystem.CopyFile(item.Source, item.Destination,
                        UIOption.AllDialogs, UICancelOption.ThrowException);
                }
            }
            catch (OperationCanceledException)
            {
                string? warning = CaptureInterruptedOutput(
                    item, destinationExisted, move, completed, created)
                    ? $"{(move ? "Move" : "Copy")} was cancelled. The destination may contain partial changes; use Undo where available and review both locations."
                    : null;
                return new OpResult(OpOutcome.Canceled, warning, completed, created);
            }
            catch (Exception ex)
            {
                bool leftOutput = CaptureInterruptedOutput(
                    item, destinationExisted, move, completed, created);
                string error = leftOutput
                    ? $"{ex.Message} The destination may contain partial changes."
                    : ex.Message;
                return new OpResult(OpOutcome.Failed, error, completed, created);
            }

            bool destinationExists = PathExists(item.Destination);
            bool sourceExists = PathExists(item.Source);
            if (!destinationExisted && destinationExists)
                created.Add(item.Destination);

            bool landed = destinationExists && (!move || !sourceExists);
            if (!landed)
                return new OpResult(OpOutcome.Failed,
                    $"{(move ? "Move" : "Copy")} did not complete for {Path.GetFileName(item.Source)}.",
                    completed, created);

            completed.Add(item.Source);
        }

        return new OpResult(OpOutcome.Ok, null, completed, created);
    }

    /// <summary>
    /// Transfer files without invoking the shell UI. The drag/drop path uses this
    /// after resolving all existing destinations once, which keeps the operation
    /// deterministic and prevents nested Windows overwrite prompts.
    /// </summary>
    private static OpResult TransferWithoutShell(IReadOnlyList<TransferItem> items, bool move,
        Func<IReadOnlyList<TransferConflict>, TransferConflictChoice> resolveConflicts,
        List<string> completed, List<string> created)
    {
        var conflicts = items
            .Where(item => PathExists(item.Destination))
            .Select(item => new TransferConflict(item.Source, item.Destination, item.IsDirectory))
            .ToList();

        TransferConflictChoice choice;
        try
        {
            // No prompt is needed for the common case. Skip a destination that appears
            // during the copy rather than overwriting a file without user approval.
            choice = conflicts.Count == 0
                ? TransferConflictChoice.SkipAll
                : resolveConflicts(conflicts);
        }
        catch (Exception ex)
        {
            return new OpResult(OpOutcome.Failed, ex.Message, completed, created);
        }

        if (choice == TransferConflictChoice.Cancel)
            return new OpResult(OpOutcome.Canceled, null, completed, created);

        foreach (TransferItem item in items)
        {
            if (!PathExists(item.Source))
                return new OpResult(OpOutcome.Failed,
                    $"Source item no longer exists: {Path.GetFileName(item.Source)}", completed, created);

            bool destinationExisted = PathExists(item.Destination);
            try
            {
                ManagedTransferResult transfer = move
                    ? MoveManaged(item.Source, item.Destination, item.IsDirectory, choice)
                    : CopyManaged(item.Source, item.Destination, item.IsDirectory, choice);

                bool destinationExists = PathExists(item.Destination);
                if (!destinationExisted && destinationExists && !created.Contains(
                        item.Destination, StringComparer.OrdinalIgnoreCase))
                    created.Add(item.Destination);

                if (!transfer.Completed)
                {
                    // Skip all is an intentional successful choice. Leave the source
                    // in place and continue with other non-conflicting items.
                    if (transfer.Skipped) continue;
                    return new OpResult(OpOutcome.Failed,
                        $"{(move ? "Move" : "Copy")} did not complete for {Path.GetFileName(item.Source)}.",
                        completed, created);
                }

                completed.Add(item.Source);
            }
            catch (Exception ex)
            {
                bool leftOutput = CaptureInterruptedOutput(
                    item, destinationExisted, move, completed, created);
                string error = leftOutput
                    ? $"{ex.Message} The destination may contain partial changes."
                    : ex.Message;
                return new OpResult(OpOutcome.Failed, error, completed, created);
            }
        }

        return new OpResult(OpOutcome.Ok, null, completed, created);
    }

    private static ManagedTransferResult CopyManaged(string source, string destination,
        bool isDirectory, TransferConflictChoice choice)
    {
        if (!isDirectory)
        {
            if (Directory.Exists(destination))
            {
                if (choice == TransferConflictChoice.SkipAll)
                    return new ManagedTransferResult(false, true);
                Directory.Delete(destination, recursive: true);
            }
            else if (File.Exists(destination) && choice == TransferConflictChoice.SkipAll)
            {
                return new ManagedTransferResult(false, true);
            }

            File.Copy(source, destination, overwrite: choice == TransferConflictChoice.ReplaceAll);
            return new ManagedTransferResult(true, false);
        }

        if (File.Exists(destination))
        {
            if (choice == TransferConflictChoice.SkipAll)
                return new ManagedTransferResult(false, true);
            File.Delete(destination);
        }

        Directory.CreateDirectory(destination);
        CopyDirectoryContents(source, destination, choice, out bool skipped);
        return new ManagedTransferResult(true, skipped);
    }

    private static void CopyDirectoryContents(string source, string destination,
        TransferConflictChoice choice, out bool skipped)
    {
        skipped = false;
        foreach (string childDirectory in Directory.EnumerateDirectories(source).ToArray())
        {
            string childDestination = Path.Combine(destination, Path.GetFileName(childDirectory));
            if (File.Exists(childDestination))
            {
                if (choice == TransferConflictChoice.SkipAll)
                {
                    skipped = true;
                    continue;
                }
                File.Delete(childDestination);
            }

            ManagedTransferResult result = CopyManaged(
                childDirectory, childDestination, isDirectory: true, choice);
            skipped |= result.Skipped || !result.Completed;
        }

        foreach (string childFile in Directory.EnumerateFiles(source).ToArray())
        {
            string childDestination = Path.Combine(destination, Path.GetFileName(childFile));
            if (Directory.Exists(childDestination))
            {
                if (choice == TransferConflictChoice.SkipAll)
                {
                    skipped = true;
                    continue;
                }
                Directory.Delete(childDestination, recursive: true);
            }
            else if (File.Exists(childDestination) && choice == TransferConflictChoice.SkipAll)
            {
                skipped = true;
                continue;
            }

            File.Copy(childFile, childDestination,
                overwrite: choice == TransferConflictChoice.ReplaceAll);
        }
    }

    private static ManagedTransferResult MoveManaged(string source, string destination,
        bool isDirectory, TransferConflictChoice choice)
    {
        if (!PathExists(destination))
            return MoveToNewDestination(source, destination, isDirectory, choice);

        if (!isDirectory)
        {
            if (choice == TransferConflictChoice.SkipAll)
                return new ManagedTransferResult(false, true);

            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            if (SameRoot(source, destination))
                File.Move(source, destination, overwrite: true);
            else
            {
                File.Copy(source, destination, overwrite: true);
                File.Delete(source);
            }
            return new ManagedTransferResult(true, false);
        }

        if (File.Exists(destination))
        {
            if (choice == TransferConflictChoice.SkipAll)
                return new ManagedTransferResult(false, true);
            File.Delete(destination);
            return MoveToNewDestination(source, destination, isDirectory: true, choice);
        }

        MoveDirectoryContents(source, destination, choice, out bool skipped);
        if (!skipped && !Directory.EnumerateFileSystemEntries(source).Any())
            Directory.Delete(source);

        return new ManagedTransferResult(!PathExists(source), skipped);
    }

    private static ManagedTransferResult MoveToNewDestination(string source, string destination,
        bool isDirectory, TransferConflictChoice choice)
    {
        if (SameRoot(source, destination))
        {
            if (isDirectory) Directory.Move(source, destination);
            else File.Move(source, destination);
            return new ManagedTransferResult(true, false);
        }

        ManagedTransferResult copied = CopyManaged(source, destination, isDirectory, choice);
        if (!copied.Completed) return copied;
        if (copied.Skipped)
            return new ManagedTransferResult(false, true);

        if (isDirectory) Directory.Delete(source, recursive: true);
        else File.Delete(source);
        return new ManagedTransferResult(true, copied.Skipped);
    }

    private static void MoveDirectoryContents(string source, string destination,
        TransferConflictChoice choice, out bool skipped)
    {
        skipped = false;
        Directory.CreateDirectory(destination);

        foreach (string childDirectory in Directory.EnumerateDirectories(source).ToArray())
        {
            string childDestination = Path.Combine(destination, Path.GetFileName(childDirectory));
            if (File.Exists(childDestination) && choice == TransferConflictChoice.SkipAll)
            {
                skipped = true;
                continue;
            }

            ManagedTransferResult result = MoveManaged(
                childDirectory, childDestination, isDirectory: true, choice);
            skipped |= result.Skipped || !result.Completed;
        }

        foreach (string childFile in Directory.EnumerateFiles(source).ToArray())
        {
            string childDestination = Path.Combine(destination, Path.GetFileName(childFile));
            ManagedTransferResult result = MoveManaged(
                childFile, childDestination, isDirectory: false, choice);
            skipped |= result.Skipped || !result.Completed;
        }
    }

    private readonly record struct ManagedTransferResult(bool Completed, bool Skipped);

    private static bool CaptureInterruptedOutput(TransferItem item, bool destinationExisted,
        bool move, List<string> completed, List<string> created)
    {
        bool destinationExists = PathExists(item.Destination);
        if (!destinationExisted && destinationExists && !created.Contains(
                item.Destination, StringComparer.OrdinalIgnoreCase))
            created.Add(item.Destination);

        // If a move fully landed before the shell reported cancellation/error, it can
        // still be reversed. A split source/destination state is deliberately not
        // treated as complete because merging it back could destroy data.
        if (move && destinationExists && !PathExists(item.Source))
            completed.Add(item.Source);

        return destinationExists;
    }

    private readonly record struct TransferItem(
        string Source, string Destination, bool IsDirectory);

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Rename within the same folder. Refuses invalid names and existing targets (no overwrite).</summary>
    public string? Rename(string path, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName)) return "Name can't be empty.";
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "Name contains invalid characters.";

        string? dir = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (dir is null) return "Can't rename this item.";
        string target = Path.Combine(dir, newName);

        if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), target,
                StringComparison.OrdinalIgnoreCase))
            return null; // unchanged

        if (File.Exists(target) || Directory.Exists(target))
            return $"\"{newName}\" already exists here.";

        try
        {
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Create a folder, auto-uniquifying the name. Returns (error, createdPath).</summary>
    public (string? error, string? created) CreateFolder(string parentDir, string desiredName)
    {
        desiredName = desiredName.Trim();
        if (string.IsNullOrEmpty(desiredName)) desiredName = "New folder";
        if (desiredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return ("Name contains invalid characters.", null);

        try
        {
            string target = Path.Combine(parentDir, desiredName);
            int n = 2;
            while (Directory.Exists(target) || File.Exists(target))
                target = Path.Combine(parentDir, $"{desiredName} ({n++})");

            Directory.CreateDirectory(target);
            return (null, target);
        }
        catch (Exception ex) { return (ex.Message, null); }
    }

    /// <summary>Create an empty file, auto-uniquifying the name. Returns (error, createdPath).</summary>
    public (string? error, string? created) CreateFile(string parentDir, string desiredName)
    {
        desiredName = desiredName.Trim();
        if (string.IsNullOrEmpty(desiredName)) desiredName = "New file";
        if (desiredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return ("Name contains invalid characters.", null);

        try
        {
            string ext = Path.GetExtension(desiredName);
            string stem = Path.GetFileNameWithoutExtension(desiredName);
            string target = Path.Combine(parentDir, desiredName);
            int n = 2;
            while (File.Exists(target) || Directory.Exists(target))
                target = Path.Combine(parentDir, $"{stem} ({n++}){ext}");

            using (File.Create(target)) { }
            return (null, target);
        }
        catch (Exception ex) { return (ex.Message, null); }
    }

    /// <summary>Return a non-colliding path by appending " (n)" before the extension.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int n = 2;
        string candidate;
        do { candidate = Path.Combine(dir, $"{stem} ({n++}){ext}"); }
        while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }

    private static bool IsSubPath(string parent, string child)
    {
        string p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        string c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool SameRoot(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(left), Path.GetPathRoot(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
