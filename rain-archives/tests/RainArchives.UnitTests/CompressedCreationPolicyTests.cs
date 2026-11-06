using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class CompressedCreationPolicyTests
{
    [TestMethod]
    public async Task StagesRawGZipAndUsesFinalDestinationMemberName()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.bin");
        var destination = Path.Combine(root.Path, "report.gz");
        var payload = new byte[] { 0x52, 0x61, 0x69, 0x6E, 0x01 };
        await File.WriteAllBytesAsync(source, payload);
        var provider = new ControlledWriterProvider("gzip");
        var plan = await new ArchiveCreationPolicy().PlanAsync(
            [new ArchiveCreationFile(source, "ignored")],
            destination,
            ArchiveCreationOutputFormat.GZip,
            CancellationToken.None);

        await CreateWriter(root.Path).CreateGZipAsync(
            provider,
            [new ArchiveCreationFile(source, "staging-name-that-must-not-escape")],
            destination,
            ArchiveCompressionPreset.Balanced,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.IsNotNull(provider.Expected);
        Assert.HasCount(1, provider.Expected!);
        Assert.AreEqual("report", provider.Expected![0].ArchivePath);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(payload)), provider.Expected[0].ContentSha256);
        Assert.IsLessThanOrEqualTo(plan.MaximumExpectedOutputBytes, new FileInfo(destination).Length);
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task StagesCompressedTarGZipAndReadbackChecksDigest()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "bundle.tar.gz");
        await File.WriteAllTextAsync(source, "compressed TAR payload");
        var provider = new ControlledWriterProvider("tar.gz");
        var plan = await new ArchiveCreationPolicy().PlanAsync(
            [new ArchiveCreationFile(source, "payload.txt")],
            destination,
            ArchiveCreationOutputFormat.TarGZip,
            CancellationToken.None);

        await CreateWriter(root.Path).CreateTarGZipAsync(
            provider,
            [new ArchiveCreationFile(source, "payload.txt")],
            destination,
            ArchiveCompressionPreset.Balanced,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.IsTrue(File.Exists(destination));
        Assert.IsLessThanOrEqualTo(plan.MaximumExpectedOutputBytes, new FileInfo(destination).Length);
        Assert.AreEqual("payload.txt", provider.Expected![0].ArchivePath);
        Assert.IsNotNull(provider.Expected[0].ContentSha256);
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task CompressedReadbackFailurePublishesNothingAndCleansRecovery()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.bin");
        var destination = Path.Combine(root.Path, "payload.gz");
        await File.WriteAllTextAsync(source, "payload");
        var provider = new ControlledWriterProvider(
            "gzip",
            new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "gzip.output_manifest_mismatch",
                "Injected readback failure."));

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(() =>
            CreateWriter(root.Path).CreateGZipAsync(
                provider,
                [new ArchiveCreationFile(source, "payload")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual("gzip.output_manifest_mismatch", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task CompressedDestinationRacePreservesExistingBytes()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.bin");
        var destination = Path.Combine(root.Path, "payload.gz");
        var sentinel = new byte[] { 0x53, 0x41, 0x46, 0x45 };
        await File.WriteAllTextAsync(source, "payload");
        var provider = new ControlledWriterProvider("gzip");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(() =>
            CreateWriter(root.Path).CreateGZipAsync(
                provider,
                [new ArchiveCreationFile(source, "payload")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: () =>
                {
                    File.WriteAllBytes(destination, sentinel);
                    return ValueTask.CompletedTask;
                }));

        Assert.AreEqual("destination.appeared_before_commit", exception.Code);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task RawSourceMutationBeforeCommitPublishesNothing()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.bin");
        var original = Path.Combine(root.Path, "original.bin");
        var destination = Path.Combine(root.Path, "payload.gz");
        await File.WriteAllTextAsync(source, "original");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(() =>
            CreateWriter(root.Path).CreateGZipAsync(
                new ControlledWriterProvider("gzip"),
                [new ArchiveCreationFile(source, "payload")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: () =>
                {
                    File.Move(source, original);
                    File.WriteAllText(source, "replacement");
                    return ValueTask.CompletedTask;
                }));

        Assert.AreEqual("source.changed_during_creation", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task RawWriterRefusesMultipleSourcesBeforeCreatingProviderOrStaging()
    {
        var writer = new StagedArchiveCreationWriter(
            new ArchiveCreationRecoveryStore(
                Path.Combine(Path.GetTempPath(), $"rain-raw-recovery-{Guid.NewGuid():N}")));

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(() =>
            writer.CreateGZipAsync(
                new NeverUsedRawProvider(),
                [
                    new ArchiveCreationFile("C:\\missing-a.txt", "a.txt"),
                    new ArchiveCreationFile("C:\\missing-b.txt", "b.txt"),
                ],
                "C:\\missing\\output.gz",
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual("creation.raw_single_source_required", exception.Code);
    }

    [TestMethod]
    public async Task RawCompressionRequiresExactlyOneSourceBeforeFilesystemAccess()
    {
        var policy = new ArchiveCreationPolicy();

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(() =>
            policy.PlanAsync(
                [
                    new ArchiveCreationFile("C:\\missing-a.txt", "a.txt"),
                    new ArchiveCreationFile("C:\\missing-b.txt", "b.txt"),
                ],
                "C:\\missing\\output.gz",
                ArchiveCreationOutputFormat.GZip,
                CancellationToken.None).AsTask());

        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, exception.Category);
        Assert.AreEqual("creation.raw_single_source_required", exception.Code);
    }

    [TestMethod]
    public async Task RawCompressionRejectsTreesBeforeFilesystemAccess()
    {
        var policy = new ArchiveCreationPolicy();

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(() =>
            policy.PlanTreeAsync(
                [],
                [new ArchiveCreationDirectory("C:\\missing", "root")],
                "C:\\missing\\output.bz2",
                ArchiveCreationOutputFormat.BZip2,
                CancellationToken.None).AsTask());

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, exception.Category);
        Assert.AreEqual("creation.raw_tree_unsupported", exception.Code);
    }

    [TestMethod]
    public void RawEntryNameUsesFinalDestinationAndMatchesSequentialReaderConvention()
    {
        var source = new ArchiveCreationFile("C:\\source.bin", "opaque-staging-name");

        Assert.AreEqual(
            "report",
            RawArchiveEntryName.Rewrite([source], "C:\\out\\report.GZIP", [".gz", ".gzip"])[0].ArchivePath);
        Assert.AreEqual(
            "payload.tar",
            RawArchiveEntryName.Rewrite([source], "C:\\out\\payload.tar.bz2", [".bz2", ".bzip2"])[0].ArchivePath);
        Assert.AreEqual(
            "content",
            RawArchiveEntryName.Rewrite([source], "C:\\out\\.gz", [".gz", ".gzip"])[0].ArchivePath);
    }

    [TestMethod]
    public void RawEntryNameDoesNotRewriteMultipleSources()
    {
        var files = new[]
        {
            new ArchiveCreationFile("C:\\a", "a"),
            new ArchiveCreationFile("C:\\b", "b"),
        };

        var rewritten = RawArchiveEntryName.Rewrite(files, "C:\\out.gz", [".gz"]);
        Assert.AreEqual(files[0], rewritten[0]);
        Assert.AreEqual(files[1], rewritten[1]);
    }

    private sealed class NeverUsedRawProvider : IArchiveWriterProvider, IArchiveStagedOutputValidator
    {
        public string Id => "test/raw-never-used";

        public string Format => "gzip";

        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets { get; } =
            new HashSet<ArchiveCompressionPreset> { ArchiveCompressionPreset.Balanced };

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IArchiveWriterHandle>(
                new InvalidOperationException("The provider must not be created for a rejected request."));

        public string ValidatorFormat => Format;

        string IArchiveStagedOutputValidator.Format => ValidatorFormat;

        public ValueTask ValidateAsync(
            Stream archive,
            IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException("The validator must not run for a rejected request."));
    }

    private static StagedArchiveCreationWriter CreateWriter(string testRoot) =>
        new(new ArchiveCreationRecoveryStore(Path.Combine(testRoot, "creation-recovery")));

    private static void AssertNoStagingFiles(string parent)
    {
        Assert.IsEmpty(Directory.GetFiles(parent, ".rainarchives-*.zip.staging"));
        var recoveryRoot = Path.Combine(parent, "creation-recovery");
        if (Directory.Exists(recoveryRoot))
        {
            Assert.IsEmpty(Directory.GetDirectories(
                recoveryRoot,
                ArchiveCreationRecoveryStore.OperationPrefix + "*",
                SearchOption.TopDirectoryOnly));
        }
    }

    private sealed class ControlledWriterProvider : IArchiveWriterProvider, IArchiveStagedOutputValidator
    {
        private readonly Exception? _validationFailure;

        public ControlledWriterProvider(string format, Exception? validationFailure = null)
        {
            Format = format;
            _validationFailure = validationFailure;
        }

        public string Id => "test/controlled-compressed";
        public string Format { get; }
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets { get; } =
            new HashSet<ArchiveCompressionPreset> { ArchiveCompressionPreset.Balanced };
        public IReadOnlyList<ArchiveStagedOutputManifestEntry>? Expected { get; private set; }

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IArchiveWriterHandle>(new ControlledWriterHandle(destination));

        public async ValueTask ValidateAsync(
            Stream archive,
            IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
            CancellationToken cancellationToken)
        {
            Expected = expected.ToArray();
            if (_validationFailure is not null)
            {
                throw _validationFailure;
            }

            archive.Position = 0;
            using var copy = new MemoryStream();
            await archive.CopyToAsync(copy, cancellationToken);
            var actual = SHA256.HashData(copy.ToArray());
            Assert.AreEqual(1, expected.Count);
            Assert.AreEqual(copy.Length, expected[0].Length);
            Assert.AreEqual(Convert.ToHexString(actual), expected[0].ContentSha256);
        }

        private sealed class ControlledWriterHandle(Stream destination) : IArchiveWriterHandle
        {
            public ValueTask AddDirectoryAsync(
                ArchiveWriteDirectory entry,
                CancellationToken cancellationToken) =>
                ValueTask.FromException(new InvalidOperationException("Directories are not expected."));

            public async ValueTask AddFileAsync(
                ArchiveWriteEntry entry,
                Stream source,
                Func<long, ValueTask> reportEntryBytes,
                CancellationToken cancellationToken)
            {
                await source.CopyToAsync(destination, cancellationToken);
                await reportEntryBytes(entry.Length);
            }

            public ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.CompressedCreationPolicyTests",
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
