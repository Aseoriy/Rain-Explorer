using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace RainExplorer.Services;

/// <summary>Progress for a ZIP operation. The initial scan is indeterminate.</summary>
public readonly record struct CompressionProgress(
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string CurrentFile,
    bool IsScanning)
{
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
}

/// <summary>Cancellation and pause control shared by the UI and compression worker.</summary>
public sealed class CompressionControl : IDisposable
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

    /// <summary>Toggle the pause gate and return the new paused state.</summary>
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

    public void WaitIfPaused()
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

/// <summary>Creates ZIP archives without blocking the WPF dispatcher.</summary>
public static class CompressionService
{
    private readonly record struct ArchiveFile(
        string SourcePath, string EntryName, long Length, bool Required);

    public static void CreateZip(
        IEnumerable<string> sourcePaths,
        string destinationPath,
        CompressionControl control,
        IProgress<CompressionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(control);

        control.WaitIfPaused();
        progress?.Report(new CompressionProgress(0, 0, 0, 0, "Scanning files…", true));
        List<ArchiveFile> files = CollectFiles(sourcePaths, control);
        long totalBytes = files.Aggregate(0L, (total, file) => AddLength(total, file.Length));
        progress?.Report(new CompressionProgress(0, files.Count, 0, totalBytes,
            files.Count == 0 ? "Creating archive…" : files[0].EntryName, false));

        using var output = new FileStream(destinationPath, FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);

        byte[] buffer = new byte[128 * 1024];
        int completedFiles = 0;
        long completedBytes = 0;
        long lastReport = 0;
        int skippedFiles = 0;

        foreach (ArchiveFile file in files)
        {
            control.WaitIfPaused();
            ZipArchiveEntry? entry = null;
            long copied = 0;
            try
            {
                entry = zip.CreateEntry(file.EntryName, CompressionLevel.Optimal);
                using (var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read,
                           FileShare.Read, buffer.Length, FileOptions.SequentialScan))
                using (Stream target = entry.Open())
                {
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        control.WaitIfPaused();
                        target.Write(buffer, 0, read);
                        copied = AddLength(copied, read);
                        ReportProgress(progress, completedFiles, files.Count,
                            AddLength(completedBytes, Math.Min(copied, file.Length)), totalBytes,
                            file.EntryName, force: false, ref lastReport);
                    }
                }

                completedFiles++;
                completedBytes = AddLength(completedBytes, file.Length);
                ReportProgress(progress, completedFiles, files.Count, completedBytes,
                    totalBytes, file.EntryName, force: true, ref lastReport);
            }
            catch (OperationCanceledException) { throw; }
            catch when (!file.Required)
            {
                try { entry?.Delete(); } catch { }
                skippedFiles++;
                completedFiles++;
                completedBytes = AddLength(completedBytes, file.Length);
                ReportProgress(progress, completedFiles, files.Count, completedBytes,
                    totalBytes, $"Skipped: {file.EntryName}", force: true, ref lastReport);
            }
        }

        progress?.Report(new CompressionProgress(completedFiles, files.Count, totalBytes,
            totalBytes, skippedFiles == 0 ? "Finished" : $"Finished ({skippedFiles} skipped)", false));
    }

    private static List<ArchiveFile> CollectFiles(
        IEnumerable<string> sourcePaths, CompressionControl control)
    {
        var files = new List<ArchiveFile>();
        foreach (string source in sourcePaths)
        {
            control.WaitIfPaused();
            if (string.IsNullOrWhiteSpace(source))
                throw new IOException("A source path is empty.");

            string fullPath = Path.GetFullPath(source);
            if (File.Exists(fullPath))
            {
                files.Add(CreateArchiveFile(fullPath, Path.GetFileName(fullPath), required: true));
                continue;
            }

            if (!Directory.Exists(fullPath))
                throw new FileNotFoundException("Source item no longer exists.", fullPath);

            string rootName = FolderName(fullPath);
            foreach (string file in EnumerateFiles(fullPath, control))
            {
                control.WaitIfPaused();
                try
                {
                    string relative = Path.GetRelativePath(fullPath, file);
                    string entryName = Path.Combine(rootName, relative).Replace('\\', '/');
                    files.Add(CreateArchiveFile(file, entryName, required: false));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return files;
    }

    private static IEnumerable<string> EnumerateFiles(string root, CompressionControl control)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            control.WaitIfPaused();
            string current = pending.Pop();

            string[] files;
            try { files = Directory.EnumerateFiles(current).ToArray(); }
            catch (IOException) { files = Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { files = Array.Empty<string>(); }
            foreach (string file in files)
            {
                control.WaitIfPaused();
                yield return file;
            }

            string[] children;
            try { children = Directory.GetDirectories(current); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            for (int i = children.Length - 1; i >= 0; i--) pending.Push(children[i]);
        }
    }

    private static ArchiveFile CreateArchiveFile(string path, string entryName, bool required)
    {
        long length = new FileInfo(path).Length;
        return new ArchiveFile(path, entryName, Math.Max(0, length), required);
    }

    private static string FolderName(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        try { return new DirectoryInfo(path).Name; }
        catch { return "Folder"; }
    }

    private static long AddLength(long left, long right) =>
        right <= 0 || left == long.MaxValue || right > long.MaxValue - left
            ? left
            : left + right;

    private static void ReportProgress(
        IProgress<CompressionProgress>? progress,
        int completedFiles,
        int totalFiles,
        long completedBytes,
        long totalBytes,
        string currentFile,
        bool force,
        ref long lastReport)
    {
        if (progress is null) return;
        long now = Stopwatch.GetTimestamp();
        if (!force && lastReport != 0
            && now - lastReport < Stopwatch.Frequency / 12) return;
        lastReport = now;
        progress.Report(new CompressionProgress(completedFiles, totalFiles, completedBytes,
            totalBytes, currentFile, false));
    }
}
