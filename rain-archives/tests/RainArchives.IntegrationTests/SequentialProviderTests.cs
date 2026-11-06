using System.IO.Compression;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.SharpCompress;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class SequentialProviderTests
{
    [TestMethod]
    public async Task GzipZeroMtimeIsUnknownWhileNonzeroMtimeIsPreserved()
    {
        using var root = new TemporaryDirectory();
        var zeroMtime = Path.Combine(root.Path, "zero-mtime.txt.gz");
        var explicitMtime = Path.Combine(root.Path, "explicit-mtime.txt.gz");
        CreateGzip(zeroMtime, "gzip timestamp payload"u8.ToArray());

        var bytes = File.ReadAllBytes(zeroMtime);
        Assert.IsTrue(bytes.Length >= 10);
        Assert.IsTrue(bytes.AsSpan(4, 4).SequenceEqual(new byte[4]));
        const uint timestampSeconds = 1_788_000_000;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4, 4),
            timestampSeconds);
        File.WriteAllBytes(explicitMtime, bytes);

        var provider = new SharpCompressArchiveProvider();
        await using (var zeroHandle = await provider.OpenAsync(
                         zeroMtime,
                         ReadOnlyMemory<byte>.Empty,
                         CancellationToken.None))
        {
            Assert.IsNull(zeroHandle.Entries.Single().ModifiedTimeUtc);
        }

        await using (var explicitHandle = await provider.OpenAsync(
                         explicitMtime,
                         ReadOnlyMemory<byte>.Empty,
                         CancellationToken.None))
        {
            Assert.AreEqual(
                DateTimeOffset.FromUnixTimeSeconds(timestampSeconds),
                explicitHandle.Entries.Single().ModifiedTimeUtc);
        }
    }

    [TestMethod]
    public void ZipMultipartDetectionRecognizesNumberedClassicAndSpanningSignals()
    {
        using var root = new TemporaryDirectory();
        var ordinary = Path.Combine(root.Path, "ordinary.zip");
        using (var file = new FileStream(ordinary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("alpha.txt", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write("alpha"u8);
        }

        Assert.IsFalse(SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(ordinary, "zip"));
        Assert.IsFalse(SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(ordinary, "7z"));
        Assert.AreEqual(
            ArchiveVolumeSetKind.Single,
            SharpCompressArchiveProvider.ClassifyZipVolumeSetForDiagnostics(ordinary, "zip"));
        Assert.AreEqual(
            ArchiveVolumeSetKind.Unknown,
            SharpCompressArchiveProvider.ClassifyZipVolumeSetForDiagnostics(ordinary, "7z"));

        var numbered = Path.Combine(root.Path, "numbered.zip.001");
        File.Copy(ordinary, numbered);
        Assert.IsTrue(SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(numbered, "zip"));
        Assert.AreEqual(
            ArchiveVolumeSetKind.ConfirmedMultipart,
            SharpCompressArchiveProvider.ClassifyZipVolumeSetForDiagnostics(numbered, "zip"));

        var classicName = Path.Combine(root.Path, "classic.z01");
        File.Copy(ordinary, classicName);
        Assert.IsTrue(SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(classicName, "zip"));
        Assert.AreEqual(
            ArchiveVolumeSetKind.ConfirmedMultipart,
            SharpCompressArchiveProvider.ClassifyZipVolumeSetForDiagnostics(classicName, "zip"));

        var classicEocd = Path.Combine(root.Path, "classic-final.zip");
        var classicBytes = File.ReadAllBytes(ordinary);
        var eocdOffset = FindLastSignature(classicBytes, [0x50, 0x4B, 0x05, 0x06]);
        Assert.IsGreaterThanOrEqualTo(0, eocdOffset);
        classicBytes[eocdOffset + 4] = 1;
        classicBytes[eocdOffset + 6] = 1;
        File.WriteAllBytes(classicEocd, classicBytes);
        Assert.IsTrue(SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(classicEocd, "zip"));
        Assert.AreEqual(
            ArchiveVolumeSetKind.ConfirmedMultipart,
            SharpCompressArchiveProvider.ClassifyZipVolumeSetForDiagnostics(classicEocd, "zip"));

        var eocdInComment = Path.Combine(root.Path, "multipart-eocd-hidden-by-comment.zip");
        var originalEocd = classicBytes.AsSpan(eocdOffset, 22).ToArray();
        originalEocd[4] = 0;
        originalEocd[6] = 0;
        var commentBytes = new byte[classicBytes.Length + originalEocd.Length];
        classicBytes.CopyTo(commentBytes, 0);
        commentBytes[eocdOffset + 20] = checked((byte)originalEocd.Length);
        originalEocd.CopyTo(commentBytes, classicBytes.Length);
        File.WriteAllBytes(eocdInComment, commentBytes);
        Assert.IsTrue(
            SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(eocdInComment, "zip"),
            "An ordinary-looking EOCD embedded in the archive comment must not hide earlier multipart disk fields.");

        var zip64Locator = Path.Combine(root.Path, "zip64-multipart-locator.zip");
        var zip64Bytes = new byte[classicBytes.Length + 20];
        classicBytes.AsSpan(0, eocdOffset).CopyTo(zip64Bytes);
        "PK\u0006\u0007"u8.CopyTo(zip64Bytes.AsSpan(eocdOffset));
        zip64Bytes[eocdOffset + 4] = 1;
        zip64Bytes[eocdOffset + 16] = 2;
        originalEocd.CopyTo(zip64Bytes, eocdOffset + 20);
        File.WriteAllBytes(zip64Locator, zip64Bytes);
        Assert.IsTrue(
            SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(zip64Locator, "zip"),
            "ZIP64 locator disk fields must activate list-only multipart policy.");

        var noEndRecord = Path.Combine(root.Path, "zip-without-terminal-record.zip");
        File.WriteAllBytes(noEndRecord, "PK\u0003\u0004incomplete"u8.ToArray());
        Assert.IsTrue(
            SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(noEndRecord, "zip"),
            "A ZIP without a verified single-disk end record must fail closed to list-only policy.");
        Assert.AreEqual(
            ArchiveVolumeSetKind.Unknown,
            SharpCompressArchiveProvider.ClassifyZipVolumeSetForDiagnostics(noEndRecord, "zip"));

        var zip64WithoutLocator = Path.Combine(root.Path, "zip64-without-locator.zip");
        var zip64WithoutLocatorBytes = File.ReadAllBytes(ordinary);
        var zip64WithoutLocatorEocd = FindLastSignature(
            zip64WithoutLocatorBytes,
            [0x50, 0x4B, 0x05, 0x06]);
        zip64WithoutLocatorBytes.AsSpan(zip64WithoutLocatorEocd + 16, 4).Fill(0xFF);
        File.WriteAllBytes(zip64WithoutLocator, zip64WithoutLocatorBytes);
        Assert.IsTrue(
            SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(zip64WithoutLocator, "zip"),
            "ZIP64 sentinel metadata without its locator must fail closed to list-only policy.");
        Assert.AreEqual(
            ArchiveVolumeSetKind.Unknown,
            SharpCompressArchiveProvider.ClassifyZipVolumeSetForDiagnostics(zip64WithoutLocator, "zip"));

        var spanningMarker = Path.Combine(root.Path, "spanning-marker.zip");
        var spanningBytes = File.ReadAllBytes(ordinary);
        "PK\u0007\b"u8.CopyTo(spanningBytes);
        File.WriteAllBytes(spanningMarker, spanningBytes);
        Assert.IsTrue(SharpCompressArchiveProvider.IsZipMultipartSourceForDiagnostics(spanningMarker, "zip"));
        Assert.AreEqual(
            ArchiveVolumeSetKind.ConfirmedMultipart,
            SharpCompressArchiveProvider.ClassifyZipVolumeSetForDiagnostics(spanningMarker, "zip"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task GzipTrailerCrcCorruptionFailsExtractionAndIntegrityWhileControlsPass()
    {
        using var root = new TemporaryDirectory();
        var payload = "same-size-gzip-payload"u8.ToArray();
        var validGzip = Path.Combine(root.Path, "sequential-control.txt.gz");
        var corruptGzip = Path.Combine(root.Path, "sequential-corrupt.txt.gz");
        var corruptTarGzip = Path.Combine(root.Path, "sequential-corrupt.tar.gz");
        var corruptTarBzip2 = Path.Combine(root.Path, "sequential-corrupt.tar.bz2");
        var corruptTarXz = Path.Combine(root.Path, "sequential-corrupt.tar.xz");
        CreateGzip(validGzip, payload);
        CreateCrcCorruptedCopy(validGzip, corruptGzip);
        CreateCrcCorruptedCopy(
            FixturePath("archives", "streams", "sample.tar.gz"),
            corruptTarGzip);
        CreateTailCorruptedCopy(
            FixturePath("archives", "streams", "sample.tar.bz2"),
            corruptTarBzip2,
            offsetFromEnd: 2);
        CreateTailCorruptedCopy(
            FixturePath("archives", "streams", "sample.tar.xz"),
            corruptTarXz,
            offsetFromEnd: 1);

        var validDecoded = await ReadRawEntryAsync(validGzip);
        var corruptDecoded = await ReadRawEntryAsync(corruptGzip);
        CollectionAssert.AreEqual(validDecoded, corruptDecoded);
        CollectionAssert.AreEqual(payload, corruptDecoded);

        await using var client = await ArchiveClient.StartAsync(FindWorker());

        var valid = await client.OpenArchiveAsync(validGzip);
        try
        {
            var listed = await ReadAllEntriesAsync(client, valid.Archive.SessionId);
            Assert.AreEqual(1, listed.Count);

            var tested = await client.StartIntegrityTestAsync(valid.Archive.SessionId, []);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "valid GZIP");

            var destination = Path.Combine(root.Path, "valid-gzip-output");
            var extracted = await client.StartExtractionAsync(
                valid.Archive.SessionId,
                [],
                destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "valid GZIP");
            CollectionAssert.AreEqual(
                payload,
                File.ReadAllBytes(Path.Combine(destination, listed[0].RawPath)));
        }
        finally
        {
            await client.CloseArchiveAsync(valid.Archive.SessionId);
        }

        var corrupt = await client.OpenArchiveAsync(corruptGzip);
        try
        {
            var tested = await client.StartIntegrityTestAsync(corrupt.Archive.SessionId, []);
            var testTerminal = await client.WaitForTerminalAsync(tested.OperationId);
            AssertFailed(testTerminal, ArchiveErrorCategory.CorruptArchive, "corrupt GZIP integrity test");
            Assert.AreEqual("archive.entry_checksum_or_decode_failed", testTerminal.Error!.Code);

            var destination = Path.Combine(root.Path, "corrupt-gzip-output");
            var extracted = await client.StartExtractionAsync(
                corrupt.Archive.SessionId,
                [],
                destination);
            var extractTerminal = await client.WaitForTerminalAsync(extracted.OperationId);
            AssertFailed(extractTerminal, ArchiveErrorCategory.CorruptArchive, "corrupt GZIP extraction");
            Assert.AreEqual("archive.entry_checksum_or_decode_failed", extractTerminal.Error!.Code);
            Assert.IsFalse(Directory.Exists(destination));
            AssertNoStagingArtifacts(root.Path);
        }
        finally
        {
            await client.CloseArchiveAsync(corrupt.Archive.SessionId);
        }

        var tar = await client.OpenArchiveAsync(FixturePath("archives", "streams", "sample.tar.gz"));
        try
        {
            Assert.AreEqual("tar.gz", tar.Archive.Format);
            var tested = await client.StartIntegrityTestAsync(tar.Archive.SessionId, []);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "TAR control");

            var destination = Path.Combine(root.Path, "tar-output");
            var extracted = await client.StartExtractionAsync(
                tar.Archive.SessionId,
                [],
                destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "TAR control");
            Assert.IsTrue(File.Exists(Path.Combine(destination, "alpha.txt")));
        }
        finally
        {
            await client.CloseArchiveAsync(tar.Archive.SessionId);
        }

        var corruptTar = await client.OpenArchiveAsync(corruptTarGzip);
        try
        {
            Assert.AreEqual("tar.gz", corruptTar.Archive.Format);
            var tested = await client.StartIntegrityTestAsync(corruptTar.Archive.SessionId, []);
            var testTerminal = await client.WaitForTerminalAsync(tested.OperationId);
            AssertFailed(testTerminal, ArchiveErrorCategory.CorruptArchive, "corrupt TAR.GZ integrity test");

            var destination = Path.Combine(root.Path, "corrupt-tar-output");
            var extracted = await client.StartExtractionAsync(
                corruptTar.Archive.SessionId,
                [],
                destination);
            var extractTerminal = await client.WaitForTerminalAsync(extracted.OperationId);
            AssertFailed(extractTerminal, ArchiveErrorCategory.CorruptArchive, "corrupt TAR.GZ extraction");
            Assert.IsFalse(Directory.Exists(destination));
            AssertNoStagingArtifacts(root.Path);
        }
        finally
        {
            await client.CloseArchiveAsync(corruptTar.Archive.SessionId);
        }

        await AssertCorruptCompressedTarAsync(
            client,
            corruptTarBzip2,
            "tar.bz2",
            Path.Combine(root.Path, "corrupt-tar-bzip2-output"),
            root.Path);
        await AssertCorruptCompressedTarAsync(
            client,
            corruptTarXz,
            "tar.xz",
            Path.Combine(root.Path, "corrupt-tar-xz-output"),
            root.Path);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task OrderedOperationsReuseForwardReaderAndResetAfterBackwardFailureCancellationAndDisposal()
    {
        var provider = new SharpCompressArchiveProvider();
        await using var handle = await provider.OpenAsync(
            FixturePath("archives", "streams", "sample.tar.gz"),
            ReadOnlyMemory<byte>.Empty,
            CancellationToken.None);
        Assert.IsTrue(handle.Entries.Count >= 2);

        var first = await CopyEntryAsync(handle, 0);
        var firstReader = GetRetainedReader(handle);
        Assert.IsNotNull(firstReader);

        var second = await CopyEntryAsync(handle, 1);
        var secondReader = GetRetainedReader(handle);
        Assert.AreSame(firstReader, secondReader);

        var backward = await CopyEntryAsync(handle, 0);
        var backwardReader = GetRetainedReader(handle);
        Assert.AreNotSame(secondReader, backwardReader);
        CollectionAssert.AreEqual(first, backward);

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await handle.CopyEntryToAsync(1, new ThrowingWriteStream(), CancellationToken.None));
        Assert.IsNull(GetRetainedReader(handle));

        var recovered = await CopyEntryAsync(handle, 1);
        var recoveredReader = GetRetainedReader(handle);
        Assert.IsNotNull(recoveredReader);
        Assert.AreNotSame(backwardReader, recoveredReader);
        CollectionAssert.AreEqual(second, recovered);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await CopyEntryAsync(handle, 0, cancelled.Token));
        Assert.IsNull(GetRetainedReader(handle));

        await handle.DisposeAsync();
        Assert.IsNull(GetRetainedReader(handle));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await CopyEntryAsync(handle, 0));
    }

    private static async Task<byte[]> ReadRawEntryAsync(string path)
    {
        var provider = new SharpCompressArchiveProvider();
        await using var handle = await provider.OpenAsync(
            path,
            ReadOnlyMemory<byte>.Empty,
            CancellationToken.None);
        await using var source = await handle.OpenEntryStreamAsync(0, CancellationToken.None);
        using var destination = new MemoryStream();
        await source.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static async Task<byte[]> CopyEntryAsync(
        IArchiveHandle handle,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        using var destination = new MemoryStream();
        await handle.CopyEntryToAsync(ordinal, destination, cancellationToken);
        return destination.ToArray();
    }

    private static object? GetRetainedReader(IArchiveHandle handle)
    {
        var field = handle.GetType().GetField(
            "_reader",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return field.GetValue(handle);
    }

    private static void CreateGzip(string path, byte[] payload)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(payload);
    }

    private static void CreateCrcCorruptedCopy(string sourcePath, string destinationPath)
    {
        var original = File.ReadAllBytes(sourcePath);
        Assert.IsTrue(original.Length >= 8);
        var corrupted = (byte[])original.Clone();
        corrupted[^8] ^= 0x01;
        Assert.AreEqual(original.Length, corrupted.Length);
        Assert.IsTrue(original.AsSpan(0, original.Length - 8)
            .SequenceEqual(corrupted.AsSpan(0, corrupted.Length - 8)));
        Assert.IsTrue(original.AsSpan(original.Length - 4)
            .SequenceEqual(corrupted.AsSpan(corrupted.Length - 4)));
        File.WriteAllBytes(destinationPath, corrupted);
    }

    private static void CreateTailCorruptedCopy(
        string sourcePath,
        string destinationPath,
        int offsetFromEnd)
    {
        var original = File.ReadAllBytes(sourcePath);
        Assert.IsTrue(offsetFromEnd > 0 && offsetFromEnd <= original.Length);
        var corrupted = (byte[])original.Clone();
        corrupted[^offsetFromEnd] ^= 0x01;
        Assert.IsFalse(original.AsSpan().SequenceEqual(corrupted));
        File.WriteAllBytes(destinationPath, corrupted);
    }

    private static async Task AssertCorruptCompressedTarAsync(
        ArchiveClient client,
        string archivePath,
        string expectedFormat,
        string destination,
        string stagingParent)
    {
        var opened = await client.OpenArchiveAsync(archivePath);
        try
        {
            Assert.AreEqual(expectedFormat, opened.Archive.Format);
            var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
            AssertFailed(
                await client.WaitForTerminalAsync(tested.OperationId),
                ArchiveErrorCategory.CorruptArchive,
                $"corrupt {expectedFormat} integrity test");

            var extracted = await client.StartExtractionAsync(
                opened.Archive.SessionId,
                [],
                destination);
            AssertFailed(
                await client.WaitForTerminalAsync(extracted.OperationId),
                ArchiveErrorCategory.CorruptArchive,
                $"corrupt {expectedFormat} extraction");
            Assert.IsFalse(Directory.Exists(destination));
            AssertNoStagingArtifacts(stagingParent);
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    private static async Task<IReadOnlyList<ArchiveEntryDescriptor>> ReadAllEntriesAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId)
    {
        var entries = new List<ArchiveEntryDescriptor>();
        var offset = 0;
        while (true)
        {
            var page = await client.ListEntriesAsync(sessionId, offset, 1024);
            entries.AddRange(page.Entries);
            if (page.NextOffset is not { } next)
            {
                return entries;
            }

            offset = next;
        }
    }

    private static void AssertCompleted(OperationEvent terminal, string description)
    {
        Assert.IsTrue(
            terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings,
            $"{description}: {terminal.Error?.Code} {terminal.Error?.Message}");
    }

    private static void AssertFailed(
        OperationEvent terminal,
        ArchiveErrorCategory category,
        string description)
    {
        Assert.AreEqual(OperationPhase.Failed, terminal.Phase, description);
        Assert.IsNotNull(terminal.Error, description);
        Assert.AreEqual(category, terminal.Error.Category, $"{description}: {terminal.Error.Code}");
    }

    private static void AssertNoStagingArtifacts(string parent)
    {
        var leftovers = Directory
            .EnumerateFileSystemEntries(parent, "*.rainarchives-*.staging", SearchOption.TopDirectoryOnly)
            .ToArray();
        Assert.AreEqual(0, leftovers.Length, string.Join(Environment.NewLine, leftovers));
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

    private static int FindLastSignature(byte[] haystack, ReadOnlySpan<byte> signature)
    {
        for (var offset = haystack.Length - signature.Length; offset >= 0; offset--)
        {
            if (haystack.AsSpan(offset, signature.Length).SequenceEqual(signature))
            {
                return offset;
            }
        }

        return -1;
    }

    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("The test destination rejected the write.");

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("The test destination rejected the write.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The test destination rejected the write."));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.SequentialProviderTests",
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
