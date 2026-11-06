using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveTransferStorageTests
{
    [Fact]
    public void AggregatePathBudgetAcceptsExactBoundaryAndRejectsEitherOverage()
    {
        var incremental = new ArchiveTransferPathBudget(
            maximumCharacters: 4,
            maximumUtf8Bytes: 6);
        Assert.True(incremental.TryAdd("123"));
        Assert.True(incremental.TryAdd("雨"));
        Assert.False(incremental.TryAdd("x"));

        Assert.True(ArchiveTransferStorage.FitsAggregatePathBudget(
            ["123", "雨"], maximumCharacters: 4, maximumUtf8Bytes: 6));
        Assert.False(ArchiveTransferStorage.FitsAggregatePathBudget(
            ["123", "雨"], maximumCharacters: 3, maximumUtf8Bytes: 6));
        Assert.False(ArchiveTransferStorage.FitsAggregatePathBudget(
            ["123", "雨"], maximumCharacters: 4, maximumUtf8Bytes: 5));
    }

    [Fact]
    public async Task ExactNestedPublicationAndHandleCleanupLeaveNoOrdinaryResidue()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        ArchiveTransferAllocation allocation = storage.Allocate();
        string folder = Directory.CreateDirectory(Path.Combine(allocation.DestinationPath, "folder")).FullName;
        string file = Path.Combine(folder, "file.txt");
        await File.WriteAllTextAsync(file, "rain");
        var expected = new ArchiveTransferSelectionManifest(
            [
                new("folder", IsDirectory: true, Length: 0),
                new("folder/file.txt", IsDirectory: false, Length: 4),
            ],
            ["folder/file.txt"]);

        ArchiveTransferManifest manifest = await storage.ReviewCompletedAsync(
            allocation, expected, CancellationToken.None);

        Assert.Equal([file], manifest.PublishedPaths);
        Assert.True(await storage.RevalidateReadyAsync(manifest, CancellationToken.None));
        Assert.Equal(
            ArchiveTransferCleanupOutcome.Removed,
            await storage.CleanupAsync(manifest, CancellationToken.None));
        Assert.False(Directory.Exists(allocation.DestinationPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
    }

    [Fact]
    public async Task InjectedOrdinaryObjectCannotEnterExactSelectionManifest()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        ArchiveTransferAllocation allocation = storage.Allocate();
        Directory.CreateDirectory(allocation.DestinationPath);
        await File.WriteAllTextAsync(Path.Combine(allocation.DestinationPath, "selected.txt"), "yes");
        await File.WriteAllTextAsync(Path.Combine(allocation.DestinationPath, "injected.txt"), "no");
        var expected = new ArchiveTransferSelectionManifest(
            [new("selected.txt", IsDirectory: false, Length: 3)],
            ["selected.txt"]);

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReviewCompletedAsync(
            allocation, expected, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(allocation.DestinationPath, "injected.txt")));
    }

    [Fact]
    public async Task NamedDataStreamIsRefusedWithoutCleanupMutation()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        ArchiveTransferAllocation allocation = storage.Allocate();
        Directory.CreateDirectory(allocation.DestinationPath);
        string file = Path.Combine(allocation.DestinationPath, "selected.txt");
        await File.WriteAllTextAsync(file, "yes");
        await File.WriteAllTextAsync(file + ":unexpected", "stream");
        var expected = new ArchiveTransferSelectionManifest(
            [new("selected.txt", IsDirectory: false, Length: 3)],
            ["selected.txt"]);

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReviewCompletedAsync(
            allocation, expected, CancellationToken.None));
        Assert.True(File.Exists(file));
        Assert.Equal("stream", await File.ReadAllTextAsync(file + ":unexpected"));
    }

    [Fact]
    public async Task EnumerationStopsAtConfiguredReviewCapWithoutDeletingOutput()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(
            Path.Combine(temporary.Path, "transfer-root"),
            maximumReviewObjects: 2);
        ArchiveTransferAllocation allocation = storage.Allocate();
        Directory.CreateDirectory(allocation.DestinationPath);
        foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
            await File.WriteAllTextAsync(Path.Combine(allocation.DestinationPath, name), "x");
        var expected = new ArchiveTransferSelectionManifest(
            [
                new("a.txt", IsDirectory: false, Length: 1),
                new("b.txt", IsDirectory: false, Length: 1),
                new("c.txt", IsDirectory: false, Length: 1),
            ],
            ["a.txt"]);

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReviewCompletedAsync(
            allocation, expected, CancellationToken.None));
        Assert.Equal(3, Directory.EnumerateFiles(allocation.DestinationPath).Count());
    }

    [Fact]
    public async Task ReviewHashesThroughPinnedNoFollowHandleAndPublicationPinsTransferRoot()
    {
        using var temporary = new TemporaryDirectory();
        int replacementAttempts = 0;
        var storage = new ArchiveTransferStorage(
            Path.Combine(temporary.Path, "transfer-root"),
            reviewHandlePinned: path =>
            {
                if (!path.EndsWith("selected.txt", StringComparison.Ordinal)) return;
                replacementAttempts++;
                Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".replacement"));
                Assert.ThrowsAny<IOException>(() => File.AppendAllText(path, "growth"));
            });
        ArchiveTransferAllocation allocation = storage.Allocate();
        Directory.CreateDirectory(allocation.DestinationPath);
        string selected = Path.Combine(allocation.DestinationPath, "selected.txt");
        await File.WriteAllTextAsync(selected, "yes");
        var expected = new ArchiveTransferSelectionManifest(
            [new("selected.txt", IsDirectory: false, Length: 3)],
            ["selected.txt"]);

        ArchiveTransferManifest manifest = await storage.ReviewCompletedAsync(
            allocation, expected, CancellationToken.None);

        Assert.True(replacementAttempts >= 2);
        Assert.Equal("yes", await File.ReadAllTextAsync(selected));
        string renamedRoot = storage.TransferRootPath + ".renamed";
        Assert.ThrowsAny<IOException>(() => Directory.Move(storage.TransferRootPath, renamedRoot));
        Assert.Equal(24, ArchiveTransferStorage.FileIdInformationNativeSize);
        Assert.Equal(
            ArchiveTransferCleanupOutcome.Removed,
            await storage.CleanupAsync(manifest, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
    }

    [Fact]
    public async Task PreexistingWriterPreventsReviewInsteadOfAllowingPathReopen()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        ArchiveTransferAllocation allocation = storage.Allocate();
        Directory.CreateDirectory(allocation.DestinationPath);
        string selected = Path.Combine(allocation.DestinationPath, "selected.txt");
        await File.WriteAllTextAsync(selected, "yes");
        var expected = new ArchiveTransferSelectionManifest(
            [new("selected.txt", IsDirectory: false, Length: 3)],
            ["selected.txt"]);

        await using (var writer = new FileStream(
                         selected,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.ReadWrite | FileShare.Delete))
        {
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => storage.ReviewCompletedAsync(
                allocation, expected, CancellationToken.None));
        }

        Assert.Equal("yes", await File.ReadAllTextAsync(selected));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainExplorer-ArchiveTransfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
