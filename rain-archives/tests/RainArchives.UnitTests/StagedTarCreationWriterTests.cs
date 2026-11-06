using System.Formats.Tar;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.SharpCompress;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class StagedTarCreationWriterTests
{
    [TestMethod]
    public async Task CreatesReadbackValidatedTreeAndCleansTransactionState()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "source-tree");
        var empty = Path.Combine(tree, "empty");
        Directory.CreateDirectory(empty);
        var source = Path.Combine(tree, "rain-空.txt");
        var payload = "managed TAR creation"u8.ToArray();
        await File.WriteAllBytesAsync(source, payload);
        var destination = Path.Combine(root.Path, "created.tar");

        await CreateWriter(root.Path).CreateTarTreeAsync(
            new BuiltInTarArchiveWriterProvider(),
            [],
            [new ArchiveCreationDirectory(tree, "archive")],
            destination,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.IsTrue(File.Exists(destination));
        await using var archive = new FileStream(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        await using var reader = new TarReader(archive, leaveOpen: true);
        var entries = new List<(string Name, TarEntryType Type, byte[] Payload)>();
        while (await reader.GetNextEntryAsync(copyData: true, CancellationToken.None) is { } entry)
        {
            using var data = new MemoryStream();
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(data);
            }

            entries.Add((entry.Name, entry.EntryType, data.ToArray()));
        }

        CollectionAssert.AreEqual(
            new[] { "archive/", "archive/empty/", "archive/rain-空.txt" },
            entries.Select(entry => entry.Name).ToArray());
        Assert.AreEqual(TarEntryType.RegularFile, entries[^1].Type);
        CollectionAssert.AreEqual(payload, entries[^1].Payload);
        AssertNoTransactionResidue(root.Path);
    }

    [TestMethod]
    public async Task ExistingAndRacedDestinationsArePreservedWithoutStagingResidue()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "payload");
        var provider = new BuiltInTarArchiveWriterProvider();
        var writer = CreateWriter(root.Path);
        var existing = Path.Combine(root.Path, "existing.tar");
        var existingBytes = "existing"u8.ToArray();
        await File.WriteAllBytesAsync(existing, existingBytes);

        var initial = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await writer.CreateTarAsync(
                provider,
                [new ArchiveCreationFile(source, "source.txt")],
                existing,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));
        Assert.AreEqual("destination.must_not_exist", initial.Code);
        CollectionAssert.AreEqual(existingBytes, await File.ReadAllBytesAsync(existing));

        var raced = Path.Combine(root.Path, "raced.tar");
        var racedBytes = "racer"u8.ToArray();
        var race = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await writer.CreateTarAsync(
                provider,
                [new ArchiveCreationFile(source, "source.txt")],
                raced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: async () => await File.WriteAllBytesAsync(raced, racedBytes)));
        Assert.AreEqual("destination.appeared_before_commit", race.Code);
        CollectionAssert.AreEqual(racedBytes, await File.ReadAllBytesAsync(raced));
        AssertNoTransactionResidue(root.Path);
    }

    [TestMethod]
    public async Task ReadbackMismatchPublishesNothingAndCleansTransactionState()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.tar");
        await File.WriteAllTextAsync(source, "payload");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateTarAsync(
                new ReadbackFailingTarProvider(),
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual("tar.injected_readback_mismatch", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoTransactionResidue(root.Path);
    }

    [TestMethod]
    public async Task CancellationAfterStagingPublishesNothingAndCleansTransactionState()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.tar");
        await File.WriteAllTextAsync(source, "payload");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await CreateWriter(root.Path).CreateTarAsync(
                new BuiltInTarArchiveWriterProvider(),
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                _ => ValueTask.CompletedTask,
                cancellation.Token,
                beforeWriting: () =>
                {
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }));

        Assert.IsFalse(File.Exists(destination));
        AssertNoTransactionResidue(root.Path);
    }

    [TestMethod]
    public async Task PreUnixTimestampRefusesBeforeStaging()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.tar");
        await File.WriteAllTextAsync(source, "payload");
        File.SetLastWriteTimeUtc(source, new DateTime(1965, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateTarAsync(
                new BuiltInTarArchiveWriterProvider(),
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual("tar.timestamp_before_supported_range", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoTransactionResidue(root.Path);
    }

    private static StagedArchiveCreationWriter CreateWriter(string root) =>
        new(new ArchiveCreationRecoveryStore(Path.Combine(root, "creation-recovery")));

    private static void AssertNoTransactionResidue(string root)
    {
        Assert.HasCount(
            0,
            Directory.GetFiles(root, ".rainarchives-*.staging", SearchOption.TopDirectoryOnly));
        var recovery = Path.Combine(root, "creation-recovery");
        if (Directory.Exists(recovery))
        {
            Assert.HasCount(
                0,
                Directory.GetDirectories(
                    recovery,
                    ArchiveCreationRecoveryStore.OperationPrefix + "*",
                    SearchOption.TopDirectoryOnly));
        }
    }

    private sealed class ReadbackFailingTarProvider :
        IArchiveWriterProvider,
        IArchiveStagedOutputValidator
    {
        private readonly BuiltInTarArchiveWriterProvider _inner = new();

        public string Id => "tar-readback-failure-test";
        public string Format => _inner.Format;
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets =>
            _inner.CompressionPresets;

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken) =>
            _inner.CreateAsync(destination, compressionPreset, cancellationToken);

        public ValueTask ValidateAsync(
            Stream archive,
            IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "tar.injected_readback_mismatch",
                "Injected TAR staged-output mismatch."));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                nameof(StagedTarCreationWriterTests),
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
