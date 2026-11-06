using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class ArchivePreviewCacheTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task MaterializedPreviewIsReadOnlySeekableAndDeletedWithItsLease()
    {
        using var root = new TemporaryDirectory();
        var cacheDirectory = CreateCacheDirectory(root.Path);
        var archivePath = FixturePath("archives", "zip", "plain.zip");
        var expected = ReadZipEntry(archivePath, "alpha.txt");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "alpha.txt");
        await using var cache = new ArchivePreviewCache(client, cacheDirectory);

        await using (var preview = await cache.MaterializeAsync(
                         opened.Archive.SessionId,
                         entry.EntryId,
                         maximumBytes: expected.Length))
        {
            Assert.AreEqual(expected.Length, preview.Length);
            Assert.IsTrue(preview.Content.CanRead);
            Assert.IsTrue(preview.Content.CanSeek);
            Assert.IsFalse(preview.Content.CanWrite);
            Assert.IsFalse(preview.Content is FileStream);
            Assert.AreEqual(1, cache.ActiveLeaseCount);
            Assert.AreEqual(expected.Length, cache.ReservedBytes);
            Assert.HasCount(1, Directory.GetFiles(cacheDirectory));
            var privateCachePath = Directory.GetFiles(cacheDirectory).Single();
            Assert.ThrowsExactly<IOException>(() =>
            {
                using var unexpectedReader = new FileStream(
                    privateCachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            });

            preview.Content.Seek(expected.Length / 2, SeekOrigin.Begin);
            var suffix = new byte[expected.Length - expected.Length / 2];
            await preview.Content.ReadExactlyAsync(suffix);
            CollectionAssert.AreEqual(expected[(expected.Length / 2)..], suffix);

            preview.Content.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = preview.Content);
            Assert.AreEqual(0, cache.ActiveLeaseCount);
            Assert.AreEqual(0, cache.ReservedBytes);
            Assert.HasCount(0, Directory.GetFiles(cacheDirectory));
        }

        Assert.AreEqual(0, cache.ActiveLeaseCount);
        Assert.AreEqual(0, cache.ReservedBytes);
        Assert.HasCount(0, Directory.GetFiles(cacheDirectory));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task AggregateByteBudgetIsReleasedWhenPreviewLeaseCloses()
    {
        using var root = new TemporaryDirectory();
        var cacheDirectory = CreateCacheDirectory(root.Path);
        var archivePath = FixturePath("archives", "zip", "plain.zip");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "alpha.txt");
        var entryBytes = entry.OriginalSize
            ?? throw new InvalidDataException("The checked preview fixture must declare its entry size.");
        await using var cache = new ArchivePreviewCache(
            client,
            cacheDirectory,
            maximumActiveBytes: entryBytes,
            maximumActiveLeases: 2);
        var first = await cache.MaterializeAsync(
            opened.Archive.SessionId,
            entry.EntryId,
            maximumBytes: entryBytes);
        try
        {
            var limited = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await cache.MaterializeAsync(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    maximumBytes: entryBytes));
            Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, limited.Error.Category);
            Assert.AreEqual("preview.cache_byte_limit", limited.Error.Code);
            Assert.AreEqual(1, cache.ActiveLeaseCount);
            Assert.AreEqual(entryBytes, cache.ReservedBytes);
            Assert.HasCount(1, Directory.GetFiles(cacheDirectory));
        }
        finally
        {
            await first.DisposeAsync();
        }

        Assert.AreEqual(0, cache.ReservedBytes);
        await using var afterRelease = await cache.MaterializeAsync(
            opened.Archive.SessionId,
            entry.EntryId,
            maximumBytes: entryBytes);
        Assert.AreEqual(entryBytes, afterRelease.Length);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task LeaseAdmissionLimitFailsImmediatelyAndRecoversAfterRelease()
    {
        using var root = new TemporaryDirectory();
        var cacheDirectory = CreateCacheDirectory(root.Path);
        var archivePath = FixturePath("archives", "zip", "plain.zip");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "alpha.txt");
        await using var cache = new ArchivePreviewCache(
            client,
            cacheDirectory,
            maximumActiveLeases: 1);
        var first = await cache.MaterializeAsync(opened.Archive.SessionId, entry.EntryId);
        try
        {
            var limited = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await cache.MaterializeAsync(opened.Archive.SessionId, entry.EntryId));
            Assert.AreEqual("preview.cache_lease_limit", limited.Error.Code);
            Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, limited.Error.Category);
            Assert.AreEqual(1, cache.ActiveLeaseCount);
        }
        finally
        {
            await first.DisposeAsync();
        }

        await using var afterRelease = await cache.MaterializeAsync(
            opened.Archive.SessionId,
            entry.EntryId);
        Assert.IsTrue(afterRelease.Content.CanSeek);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MissingCacheDirectoryFailsBeforeMaterializationAndCanBeRestored()
    {
        using var root = new TemporaryDirectory();
        var cacheDirectory = CreateCacheDirectory(root.Path);
        var archivePath = FixturePath("archives", "zip", "plain.zip");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "alpha.txt");
        await using var cache = new ArchivePreviewCache(client, cacheDirectory);
        Directory.Delete(cacheDirectory);

        var changed = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await cache.MaterializeAsync(opened.Archive.SessionId, entry.EntryId));
        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, changed.Error.Category);
        Assert.AreEqual("preview.cache_directory_changed", changed.Error.Code);
        Assert.AreEqual(0, cache.ActiveLeaseCount);
        Assert.AreEqual(0, cache.ReservedBytes);

        Directory.CreateDirectory(cacheDirectory);
        await using var restored = await cache.MaterializeAsync(
            opened.Archive.SessionId,
            entry.EntryId);
        Assert.IsTrue(restored.Content.CanSeek);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CacheDisposalCancelsAdmissionClosesEveryLeaseAndDeletesPrivateFiles()
    {
        using var root = new TemporaryDirectory();
        var cacheDirectory = CreateCacheDirectory(root.Path);
        var archivePath = FixturePath("archives", "zip", "plain.zip");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entries = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Where(item => item.Kind == ArchiveEntryKind.File)
            .Take(2)
            .ToArray();
        Assert.HasCount(2, entries);
        var cache = new ArchivePreviewCache(client, cacheDirectory);
        var first = await cache.MaterializeAsync(opened.Archive.SessionId, entries[0].EntryId);
        var second = await cache.MaterializeAsync(opened.Archive.SessionId, entries[1].EntryId);
        Assert.HasCount(2, Directory.GetFiles(cacheDirectory));

        await cache.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = first.Content);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = second.Content);
        Assert.AreEqual(0, cache.ActiveLeaseCount);
        Assert.AreEqual(0, cache.ReservedBytes);
        Assert.HasCount(0, Directory.GetFiles(cacheDirectory));
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            cache.MaterializeAsync(opened.Archive.SessionId, entries[0].EntryId));
        await cache.DisposeAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PreCancelledMaterializationLeavesNoFileLeaseOrBudget()
    {
        using var root = new TemporaryDirectory();
        var cacheDirectory = CreateCacheDirectory(root.Path);
        var archivePath = FixturePath("archives", "zip", "plain.zip");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "alpha.txt");
        await using var cache = new ArchivePreviewCache(client, cacheDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            _ = await cache.MaterializeAsync(
                opened.Archive.SessionId,
                entry.EntryId,
                cancellationToken: cancellation.Token);
            Assert.Fail("The pre-cancelled preview materialization unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
            Assert.IsTrue(cancellation.IsCancellationRequested);
        }

        Assert.AreEqual(0, cache.ActiveLeaseCount);
        Assert.AreEqual(0, cache.ReservedBytes);
        Assert.HasCount(0, Directory.GetFiles(cacheDirectory));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CancellationAfterPrivateFileCreationDeletesPartialStateAndKeepsWorkerHealthy()
    {
        using var root = new TemporaryDirectory();
        var cacheDirectory = CreateCacheDirectory(root.Path);
        var archivePath = FixturePath("archives", "zip", "plain.zip");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "alpha.txt");
        await using var cache = new ArchivePreviewCache(client, cacheDirectory);
        using var cancellation = new CancellationTokenSource();

        var materialization = cache.MaterializeAsync(
            opened.Archive.SessionId,
            entry.EntryId,
            cancellationToken: cancellation.Token);
        Assert.HasCount(1, Directory.GetFiles(cacheDirectory));
        cancellation.Cancel();
        try
        {
            _ = await materialization;
            Assert.Fail("The cancelled preview materialization unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
            Assert.IsTrue(cancellation.IsCancellationRequested);
        }

        Assert.AreEqual(0, cache.ActiveLeaseCount);
        Assert.AreEqual(0, cache.ReservedBytes);
        Assert.HasCount(0, Directory.GetFiles(cacheDirectory));
        Assert.HasCount(
            3,
            (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16)).Entries);
    }

    private static byte[] ReadZipEntry(string archivePath, string entryPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(entryPath)
            ?? throw new InvalidDataException("The checked ZIP preview fixture entry is missing.");
        using var source = entry.Open();
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static string CreateCacheDirectory(string rootPath)
    {
        var path = Path.Combine(rootPath, "preview-cache");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindWorker()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
        }

        return worker;
    }

    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", "corpus", .. parts]);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ArchivePreviewCacheTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
