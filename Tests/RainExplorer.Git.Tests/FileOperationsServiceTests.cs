using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class FileOperationsServiceTests
{
    [Fact]
    public void MissingSourceFailsWithoutReportingSuccess()
    {
        using var temp = new TemporaryDirectory();
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string missing = Path.Combine(temp.Path, "missing.txt");

        OpResult result = new FileOperationsService().MoveIntoResult([missing], destination);

        Assert.Equal(OpOutcome.Failed, result.Outcome);
        Assert.Empty(result.Completed);
        Assert.Empty(result.Created);
        Assert.Contains("no longer exists", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateDestinationNamesFailBeforeAnythingMoves()
    {
        using var temp = new TemporaryDirectory();
        string firstDir = Directory.CreateDirectory(Path.Combine(temp.Path, "first")).FullName;
        string secondDir = Directory.CreateDirectory(Path.Combine(temp.Path, "second")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string first = Path.Combine(firstDir, "same.txt");
        string second = Path.Combine(secondDir, "same.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");

        OpResult result = new FileOperationsService().MoveIntoResult([first, second], destination);

        Assert.Equal(OpOutcome.Failed, result.Outcome);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(Path.Combine(destination, "same.txt")));
    }

    [Fact]
    public void CopyReportsBothCompletedSourceAndNewOutput()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "source.txt");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        File.WriteAllText(source, "rain");

        OpResult result = new FileOperationsService().CopyIntoResult([source], destination);
        string output = Path.Combine(destination, "source.txt");

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.Contains(source, result.Completed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(output, result.Created, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("rain", File.ReadAllText(output));
    }

    [Fact]
    public void CopyConflictUsesOneReplaceAllDecision()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "same.txt");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string output = Path.Combine(destination, "same.txt");
        File.WriteAllText(source, "new");
        File.WriteAllText(output, "old");
        int decisions = 0;

        OpResult result = new FileOperationsService().CopyIntoResult([source], destination, conflicts =>
        {
            decisions++;
            Assert.Single(conflicts);
            return TransferConflictChoice.ReplaceAll;
        });

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.Equal(1, decisions);
        Assert.Equal("new", File.ReadAllText(output));
        Assert.Empty(result.Created);
    }

    [Fact]
    public void CopyConflictSkipAllSkipsOnlyExistingPaths()
    {
        using var temp = new TemporaryDirectory();
        string sourceDir = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string existingSource = Path.Combine(sourceDir, "existing.txt");
        string newSource = Path.Combine(sourceDir, "new.txt");
        string destinationDir = Path.Combine(destination, "source");
        Directory.CreateDirectory(destinationDir);
        File.WriteAllText(existingSource, "new");
        File.WriteAllText(newSource, "copied");
        File.WriteAllText(Path.Combine(destinationDir, "existing.txt"), "old");

        OpResult result = new FileOperationsService().CopyIntoResult([sourceDir], destination,
            _ => TransferConflictChoice.SkipAll);

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.Equal("old", File.ReadAllText(Path.Combine(destinationDir, "existing.txt")));
        Assert.Equal("copied", File.ReadAllText(Path.Combine(destinationDir, "new.txt")));
    }

    [Fact]
    public void MoveConflictReplaceAllMergesFoldersWithoutShellPrompts()
    {
        using var temp = new TemporaryDirectory();
        string source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string sourceFolder = Directory.CreateDirectory(Path.Combine(source, "project")).FullName;
        string destinationFolder = Directory.CreateDirectory(Path.Combine(destination, "project")).FullName;
        File.WriteAllText(Path.Combine(sourceFolder, "same.txt"), "new");
        File.WriteAllText(Path.Combine(sourceFolder, "new.txt"), "moved");
        File.WriteAllText(Path.Combine(destinationFolder, "same.txt"), "old");
        File.WriteAllText(Path.Combine(destinationFolder, "keep.txt"), "keep");

        OpResult result = new FileOperationsService().MoveIntoResult([sourceFolder], destination,
            _ => TransferConflictChoice.ReplaceAll);

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.False(Directory.Exists(sourceFolder));
        Assert.Equal("new", File.ReadAllText(Path.Combine(destinationFolder, "same.txt")));
        Assert.Equal("moved", File.ReadAllText(Path.Combine(destinationFolder, "new.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destinationFolder, "keep.txt")));
    }

    [Fact]
    public void ManagedCopyReportsDeterminateByteProgress()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "source.bin");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        File.WriteAllBytes(source, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        using var control = new TransferControl();
        var updates = new List<TransferProgress>();

        OpResult result = new FileOperationsService().CopyIntoResult([source], destination,
            _ => TransferConflictChoice.SkipAll, control, new InlineProgress<TransferProgress>(updates.Add));

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        TransferProgress final = Assert.Single(updates, update => update.CurrentFile == "Finished");
        Assert.False(final.IsScanning);
        Assert.Equal(1, final.CompletedFiles);
        Assert.Equal(1, final.TotalFiles);
        Assert.Equal(4096, final.CompletedBytes);
        Assert.Equal(4096, final.TotalBytes);
        Assert.Equal(1, final.Fraction);
    }

    [Fact]
    public async Task ManagedCopyPauseBlocksUntilResumed()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "source.bin");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        File.WriteAllBytes(source, new byte[1024]);
        using var control = new TransferControl();
        Assert.True(control.TogglePause());

        Task<OpResult> transfer = Task.Run(() => new FileOperationsService().CopyIntoResult(
            [source], destination, _ => TransferConflictChoice.SkipAll, control));
        await Task.Delay(100);

        Assert.False(transfer.IsCompleted);
        Assert.False(File.Exists(Path.Combine(destination, "source.bin")));
        Assert.False(control.TogglePause());
        OpResult result = await transfer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OpOutcome.Ok, result.Outcome);
    }

    [Fact]
    public void ManagedCopyHonorsCancellationBeforeWritingOutput()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "source.bin");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        File.WriteAllBytes(source, new byte[1024]);
        using var control = new TransferControl();
        control.Cancel();

        OpResult result = new FileOperationsService().CopyIntoResult([source], destination,
            _ => TransferConflictChoice.SkipAll, control);

        Assert.Equal(OpOutcome.Canceled, result.Outcome);
        Assert.False(File.Exists(Path.Combine(destination, "source.bin")));
        Assert.Empty(Directory.EnumerateFiles(destination, "*.raincopy", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ManagedCopyCancellationMidFileRemovesTemporaryOutput()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "source.bin");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        using var control = new TransferControl();
        var progress = new InlineProgress<TransferProgress>(update =>
        {
            if (update.CompletedBytes > 0) control.Cancel();
        });

        OpResult result = new FileOperationsService().CopyIntoResult([source], destination,
            _ => TransferConflictChoice.SkipAll, control, progress);

        Assert.Equal(OpOutcome.Canceled, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(destination, "source.bin")));
        Assert.Empty(Directory.EnumerateFiles(destination, "*.raincopy", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ManagedCopyCancellationPreservesExistingDestination()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "source.bin");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string output = Path.Combine(destination, "source.bin");
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        File.WriteAllText(output, "keep this version");
        using var control = new TransferControl();
        var progress = new InlineProgress<TransferProgress>(update =>
        {
            if (update.CompletedBytes > 0) control.Cancel();
        });

        OpResult result = new FileOperationsService().CopyIntoResult([source], destination,
            _ => TransferConflictChoice.ReplaceAll, control, progress);

        Assert.Equal(OpOutcome.Canceled, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.Equal("keep this version", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(destination, "*.raincopy", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void TransferProgressClassifiesLargeFilesAndFolders()
    {
        var largeFile = new TransferProgress(0, 1, 0, TransferProgress.LargeTransferBytes,
            "large.bin", true, TimeSpan.Zero);
        var largeFolder = new TransferProgress(0, TransferProgress.LargeTransferFiles, 0, 1,
            "folder", true, TimeSpan.Zero);
        var small = new TransferProgress(0, 2, 0, 1024, "small", true, TimeSpan.Zero);

        Assert.True(largeFile.IsLarge);
        Assert.True(largeFolder.IsLarge);
        Assert.False(small.IsLarge);
    }

    [Fact]
    public void SameVolumeMoveToUnusedDestinationUsesImmediateProgressPath()
    {
        using var temp = new TemporaryDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        string sourceFolder = Directory.CreateDirectory(Path.Combine(sourceRoot, "folder")).FullName;
        File.WriteAllText(Path.Combine(sourceFolder, "file.txt"), "rain");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        using var control = new TransferControl();
        var updates = new List<TransferProgress>();

        OpResult result = new FileOperationsService().MoveIntoResult([sourceFolder], destination,
            _ => TransferConflictChoice.SkipAll, control, new InlineProgress<TransferProgress>(updates.Add));

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.False(Directory.Exists(sourceFolder));
        Assert.True(File.Exists(Path.Combine(destination, "folder", "file.txt")));
        TransferProgress update = Assert.Single(updates);
        Assert.False(update.IsScanning);
        Assert.Equal(1, update.CompletedFiles);
        Assert.Equal(1, update.TotalFiles);
        Assert.Equal(1, update.Fraction);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "RainExplorerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
