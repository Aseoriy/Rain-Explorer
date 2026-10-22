using System.Diagnostics;
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

/// <summary>Live progress for a managed copy or move. Scanning is indeterminate.</summary>
public readonly record struct TransferProgress(
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string CurrentFile,
    bool IsScanning,
    TimeSpan Elapsed)
{
    public const long LargeTransferBytes = 32L * 1024 * 1024;
    public const int LargeTransferFiles = 100;

    public double? Fraction
    {
        get
        {
            if (IsScanning) return null;
            if (TotalBytes > 0)
                return Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1);
            return TotalFiles > 0
                ? Math.Clamp((double)CompletedFiles / TotalFiles, 0, 1)
                : 1;
        }
    }

    public bool IsLarge => TotalBytes >= LargeTransferBytes || TotalFiles >= LargeTransferFiles;
}

/// <summary>Cancellation and pause control shared by the transfer worker and Activity Center.</summary>
public sealed class TransferControl : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _pause = new(initialState: true);
    private readonly object _gate = new();
    private bool _disposed;

    public CancellationToken Token => _cancellation.Token;

    public void Cancel()
    {
        try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public bool TogglePause()
    {
        lock (_gate)
        {
            if (_disposed || _cancellation.IsCancellationRequested) return false;
            if (_pause.IsSet)
            {
                _pause.Reset();
                return true;
            }

            _pause.Set();
            return false;
        }
    }

    internal void WaitIfPaused()
    {
        _pause.Wait(_cancellation.Token);
        _cancellation.Token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
            _pause.Set();
            _pause.Dispose();
            _cancellation.Dispose();
        }
    }
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
        Func<IReadOnlyList<TransferConflict>, TransferConflictChoice>? resolveConflicts = null,
        TransferControl? control = null,
        IProgress<TransferProgress>? progress = null) =>
        Transfer(sources, destDir, move: false, resolveConflicts, control, progress);

    /// <summary>Move with an explicit outcome and the source paths that actually landed.</summary>
    public OpResult MoveIntoResult(IEnumerable<string> sources, string destDir,
        Func<IReadOnlyList<TransferConflict>, TransferConflictChoice>? resolveConflicts = null,
        TransferControl? control = null,
        IProgress<TransferProgress>? progress = null) =>
        Transfer(sources, destDir, move: true, resolveConflicts, control, progress);

    private static string? LegacyTransfer(IEnumerable<string> sources, string destDir, bool move)
    {
        var result = Transfer(sources, destDir, move);
        return result.Outcome == OpOutcome.Canceled ? "Operation cancelled." : result.Error;
    }

    private static OpResult Transfer(IEnumerable<string> sources, string destDir, bool move,
        Func<IReadOnlyList<TransferConflict>, TransferConflictChoice>? resolveConflicts = null,
        TransferControl? control = null,
        IProgress<TransferProgress>? progress = null)
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
            return TransferWithoutShell(items, move, resolveConflicts, completed, created,
                control ?? new TransferControl(), progress, ownsControl: control is null);

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
        List<string> completed, List<string> created, TransferControl control,
        IProgress<TransferProgress>? progress, bool ownsControl)
    {
        var destinationsThatExisted = items
            .Where(item => PathExists(item.Destination))
            .Select(item => item.Destination)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool transferStarted = false;
        try
        {
            var conflicts = items
                .Where(item => PathExists(item.Destination))
                .Select(item => new TransferConflict(item.Source, item.Destination, item.IsDirectory))
                .ToList();

            // No prompt is needed for the common case. Skip a destination that appears
            // during the copy rather than overwriting a file without user approval.
            TransferConflictChoice choice = conflicts.Count == 0
                ? TransferConflictChoice.SkipAll
                : resolveConflicts(conflicts);
            if (choice == TransferConflictChoice.Cancel)
                return new OpResult(OpOutcome.Canceled, null, completed, created);

            // A same-volume move into unused names is an atomic filesystem rename.
            // Keep that common path instant instead of walking a large folder merely
            // to calculate progress for work Windows completes in one operation.
            if (move && items.All(item => !PathExists(item.Destination)
                                         && SameRoot(item.Source, item.Destination)))
            {
                transferStarted = true;
                var watch = Stopwatch.StartNew();
                for (int index = 0; index < items.Count; index++)
                {
                    control.WaitIfPaused();
                    TransferItem item = items[index];
                    if (!PathExists(item.Source))
                        return new OpResult(OpOutcome.Failed,
                            $"Source item no longer exists: {Path.GetFileName(item.Source)}", completed, created);
                    if (item.IsDirectory) Directory.Move(item.Source, item.Destination);
                    else File.Move(item.Source, item.Destination);
                    completed.Add(item.Source);
                    created.Add(item.Destination);
                    progress?.Report(new TransferProgress(index + 1, items.Count, 0, 0,
                        Path.GetFileName(item.Source), false, watch.Elapsed));
                }
                return new OpResult(OpOutcome.Ok, null, completed, created);
            }

            var tracker = TransferProgressTracker.Scan(items, control, progress);
            transferStarted = true;
            foreach (TransferItem item in items)
            {
                control.WaitIfPaused();
                if (!PathExists(item.Source))
                    return new OpResult(OpOutcome.Failed,
                        $"Source item no longer exists: {Path.GetFileName(item.Source)}", completed, created);

                bool destinationExisted = PathExists(item.Destination);
                ManagedTransferResult transfer = move
                    ? MoveManaged(item.Source, item.Destination, item.IsDirectory, choice, control, tracker)
                    : CopyManaged(item.Source, item.Destination, item.IsDirectory, choice, control, tracker);

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

            tracker.Finish();
            return new OpResult(OpOutcome.Ok, null, completed, created);
        }
        catch (OperationCanceledException ex)
        {
            bool leftOutput = transferStarted && CaptureManagedInterruptedOutputs(
                items, destinationsThatExisted, move, completed, created);
            string? cleanupWarning = ex.Data[TransferCleanupWarningKey] as string;
            string? warning = leftOutput
                ? $"{(move ? "Move" : "Copy")} was cancelled. The destination may contain partial changes; review both locations."
                : cleanupWarning;
            if (leftOutput && cleanupWarning is not null) warning += $" {cleanupWarning}";
            return new OpResult(OpOutcome.Canceled, warning, completed, created);
        }
        catch (Exception ex)
        {
            bool leftOutput = CaptureManagedInterruptedOutputs(
                items, destinationsThatExisted, move, completed, created);
            string error = leftOutput
                ? $"{ex.Message} The destination may contain partial changes."
                : ex.Message;
            if (ex.Data[TransferCleanupWarningKey] is string cleanupWarning)
                error += $" {cleanupWarning}";
            return new OpResult(OpOutcome.Failed, error, completed, created);
        }
        finally
        {
            if (ownsControl) control.Dispose();
        }
    }

    private static ManagedTransferResult CopyManaged(string source, string destination,
        bool isDirectory, TransferConflictChoice choice, TransferControl control,
        TransferProgressTracker tracker)
    {
        control.WaitIfPaused();
        if (!isDirectory)
        {
            if (Directory.Exists(destination))
            {
                if (choice == TransferConflictChoice.SkipAll)
                {
                    tracker.CompleteFile(source);
                    return new ManagedTransferResult(false, true);
                }
            }
            else if (File.Exists(destination) && choice == TransferConflictChoice.SkipAll)
            {
                tracker.CompleteFile(source);
                return new ManagedTransferResult(false, true);
            }

            CopyFileChunked(source, destination,
                overwrite: choice == TransferConflictChoice.ReplaceAll, control, tracker);
            return new ManagedTransferResult(true, false);
        }

        if (File.Exists(destination))
        {
            if (choice == TransferConflictChoice.SkipAll)
            {
                tracker.CompleteTree(source);
                return new ManagedTransferResult(false, true);
            }
            File.Delete(destination);
        }

        Directory.CreateDirectory(destination);
        CopyDirectoryContents(source, destination, choice, control, tracker, out bool skipped);
        return new ManagedTransferResult(true, skipped);
    }

    private static void CopyDirectoryContents(string source, string destination,
        TransferConflictChoice choice, TransferControl control,
        TransferProgressTracker tracker, out bool skipped)
    {
        skipped = false;
        foreach (string childDirectory in Directory.EnumerateDirectories(source).ToArray())
        {
            control.WaitIfPaused();
            string childDestination = Path.Combine(destination, Path.GetFileName(childDirectory));
            if (File.Exists(childDestination))
            {
                if (choice == TransferConflictChoice.SkipAll)
                {
                    skipped = true;
                    tracker.CompleteTree(childDirectory);
                    continue;
                }
                File.Delete(childDestination);
            }

            ManagedTransferResult result = CopyManaged(
                childDirectory, childDestination, isDirectory: true, choice, control, tracker);
            skipped |= result.Skipped || !result.Completed;
        }

        foreach (string childFile in Directory.EnumerateFiles(source).ToArray())
        {
            control.WaitIfPaused();
            string childDestination = Path.Combine(destination, Path.GetFileName(childFile));
            if (Directory.Exists(childDestination))
            {
                if (choice == TransferConflictChoice.SkipAll)
                {
                    skipped = true;
                    tracker.CompleteFile(childFile);
                    continue;
                }
            }
            else if (File.Exists(childDestination) && choice == TransferConflictChoice.SkipAll)
            {
                skipped = true;
                tracker.CompleteFile(childFile);
                continue;
            }

            CopyFileChunked(childFile, childDestination,
                overwrite: choice == TransferConflictChoice.ReplaceAll, control, tracker);
        }
    }

    private static ManagedTransferResult MoveManaged(string source, string destination,
        bool isDirectory, TransferConflictChoice choice, TransferControl control,
        TransferProgressTracker tracker)
    {
        control.WaitIfPaused();
        if (!PathExists(destination))
            return MoveToNewDestination(source, destination, isDirectory, choice, control, tracker);

        if (!isDirectory)
        {
            if (choice == TransferConflictChoice.SkipAll)
            {
                tracker.CompleteFile(source);
                return new ManagedTransferResult(false, true);
            }

            if (SameRoot(source, destination))
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                File.Move(source, destination, overwrite: true);
                tracker.CompleteFile(source);
            }
            else
            {
                CopyFileChunked(source, destination, overwrite: true, control, tracker);
                File.Delete(source);
            }
            return new ManagedTransferResult(true, false);
        }

        if (File.Exists(destination))
        {
            if (choice == TransferConflictChoice.SkipAll)
            {
                tracker.CompleteTree(source);
                return new ManagedTransferResult(false, true);
            }
            File.Delete(destination);
            return MoveToNewDestination(source, destination, isDirectory: true, choice, control, tracker);
        }

        MoveDirectoryContents(source, destination, choice, control, tracker, out bool skipped);
        if (!skipped && !Directory.EnumerateFileSystemEntries(source).Any())
            Directory.Delete(source);

        return new ManagedTransferResult(!PathExists(source), skipped);
    }

    private static ManagedTransferResult MoveToNewDestination(string source, string destination,
        bool isDirectory, TransferConflictChoice choice, TransferControl control,
        TransferProgressTracker tracker)
    {
        if (SameRoot(source, destination))
        {
            if (isDirectory) Directory.Move(source, destination);
            else File.Move(source, destination);
            if (isDirectory) tracker.CompleteTree(source);
            else tracker.CompleteFile(source);
            return new ManagedTransferResult(true, false);
        }

        ManagedTransferResult copied = CopyManaged(source, destination, isDirectory, choice, control, tracker);
        if (!copied.Completed) return copied;
        if (copied.Skipped)
            return new ManagedTransferResult(false, true);

        if (isDirectory) Directory.Delete(source, recursive: true);
        else File.Delete(source);
        return new ManagedTransferResult(true, copied.Skipped);
    }

    private static void MoveDirectoryContents(string source, string destination,
        TransferConflictChoice choice, TransferControl control,
        TransferProgressTracker tracker, out bool skipped)
    {
        skipped = false;
        Directory.CreateDirectory(destination);

        foreach (string childDirectory in Directory.EnumerateDirectories(source).ToArray())
        {
            control.WaitIfPaused();
            string childDestination = Path.Combine(destination, Path.GetFileName(childDirectory));
            if (File.Exists(childDestination) && choice == TransferConflictChoice.SkipAll)
            {
                skipped = true;
                tracker.CompleteTree(childDirectory);
                continue;
            }

            ManagedTransferResult result = MoveManaged(
                childDirectory, childDestination, isDirectory: true, choice, control, tracker);
            skipped |= result.Skipped || !result.Completed;
        }

        foreach (string childFile in Directory.EnumerateFiles(source).ToArray())
        {
            control.WaitIfPaused();
            string childDestination = Path.Combine(destination, Path.GetFileName(childFile));
            ManagedTransferResult result = MoveManaged(
                childFile, childDestination, isDirectory: false, choice, control, tracker);
            skipped |= result.Skipped || !result.Completed;
        }
    }

    private static void CopyFileChunked(string source, string destination, bool overwrite,
        TransferControl control, TransferProgressTracker tracker)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent)) throw new IOException("Destination folder not found.");
        Directory.CreateDirectory(parent);
        string temp = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.raincopy");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                       256 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       256 * 1024, FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[256 * 1024];
                while (true)
                {
                    control.WaitIfPaused();
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                    tracker.AddBytes(source, read);
                }
            }

            TryCopyFileMetadata(source, temp);
            control.WaitIfPaused();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            File.Move(temp, destination, overwrite);
            tracker.CompleteFile(source);
        }
        catch (Exception ex)
        {
            if (!TryDeleteTemporaryFile(temp, out string? cleanupError))
                ex.Data[TransferCleanupWarningKey] =
                    $"Temporary copy could not be removed: {temp}. {cleanupError}";
            throw;
        }
    }

    private const string TransferCleanupWarningKey = "TransferCleanupWarning";

    private static bool TryDeleteTemporaryFile(string path, out string? error)
    {
        try
        {
            if (File.Exists(path))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                File.Delete(path);
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryCopyFileMetadata(string source, string destination)
    {
        try { File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source)); } catch { }
        try { File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source)); } catch { }
        try { File.SetLastAccessTimeUtc(destination, File.GetLastAccessTimeUtc(source)); } catch { }
        try { File.SetAttributes(destination, File.GetAttributes(source)); } catch { }
    }

    private static bool CaptureManagedInterruptedOutputs(IReadOnlyList<TransferItem> items,
        IReadOnlySet<string> destinationsThatExisted, bool move,
        List<string> completed, List<string> created)
    {
        bool leftOutput = false;
        foreach (TransferItem item in items)
        {
            bool destinationExists = PathExists(item.Destination);
            if (!destinationExists) continue;
            leftOutput = true;
            if (!destinationsThatExisted.Contains(item.Destination)
                && !created.Contains(item.Destination, StringComparer.OrdinalIgnoreCase))
                created.Add(item.Destination);
            if (move && !PathExists(item.Source)
                && !completed.Contains(item.Source, StringComparer.OrdinalIgnoreCase))
                completed.Add(item.Source);
        }
        return leftOutput;
    }

    private sealed class TransferProgressTracker
    {
        private readonly Dictionary<string, long> _lengths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _copied = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
        private readonly IProgress<TransferProgress>? _progress;
        private readonly Stopwatch _watch = new();
        private long _completedBytes;
        private int _completedFiles;
        private long _lastReport;

        private TransferProgressTracker(IProgress<TransferProgress>? progress) => _progress = progress;

        public static TransferProgressTracker Scan(IReadOnlyList<TransferItem> items,
            TransferControl control, IProgress<TransferProgress>? progress)
        {
            var tracker = new TransferProgressTracker(progress);
            foreach (TransferItem item in items) tracker.ScanPath(item.Source, item.IsDirectory, control);
            tracker._watch.Start();
            tracker.Report("Starting…", scanning: false, force: true);
            return tracker;
        }

        private void ScanPath(string path, bool isDirectory, TransferControl control)
        {
            control.WaitIfPaused();
            if (!isDirectory)
            {
                long length = Math.Max(0, new FileInfo(path).Length);
                _lengths[path] = length;
                Report(Path.GetFileName(path), scanning: true, force: false);
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(path))
                ScanPath(directory, isDirectory: true, control);
            foreach (string file in Directory.EnumerateFiles(path))
                ScanPath(file, isDirectory: false, control);
        }

        public void AddBytes(string source, int bytes)
        {
            if (bytes <= 0) return;
            bool firstCopiedBytes = _completedBytes == 0;
            _completedBytes = AddLength(_completedBytes, bytes);
            _copied[source] = AddLength(_copied.GetValueOrDefault(source), bytes);
            Report(Path.GetFileName(source), scanning: false, force: firstCopiedBytes);
        }

        public void CompleteFile(string source)
        {
            if (!_completed.Add(source)) return;
            long length = _lengths.GetValueOrDefault(source);
            long copied = _copied.GetValueOrDefault(source);
            if (length > copied) _completedBytes = AddLength(_completedBytes, length - copied);
            _completedFiles++;
            Report(Path.GetFileName(source), scanning: false, force: true);
        }

        public void CompleteTree(string source)
        {
            string prefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
            foreach (string file in _lengths.Keys.Where(path =>
                         string.Equals(path, source, StringComparison.OrdinalIgnoreCase)
                         || Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                CompleteFile(file);
        }

        public void Finish()
        {
            foreach (string file in _lengths.Keys.ToArray()) CompleteFile(file);
            Report("Finished", scanning: false, force: true);
        }

        private void Report(string currentFile, bool scanning, bool force)
        {
            if (_progress is null) return;
            long now = Stopwatch.GetTimestamp();
            if (!force && _lastReport != 0 && now - _lastReport < Stopwatch.Frequency / 12) return;
            _lastReport = now;
            _progress.Report(new TransferProgress(
                _completedFiles,
                _lengths.Count,
                _completedBytes,
                _lengths.Values.Aggregate(0L, AddLength),
                currentFile,
                scanning,
                _watch.Elapsed));
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

    private static long AddLength(long left, long right) =>
        right <= 0 || left == long.MaxValue || right > long.MaxValue - left
            ? left
            : left + right;

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
