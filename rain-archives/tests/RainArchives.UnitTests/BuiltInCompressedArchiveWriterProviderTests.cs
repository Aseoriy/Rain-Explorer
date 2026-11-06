using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.SharpCompress;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class BuiltInCompressedArchiveWriterProviderTests
{
    [TestMethod]
    public async Task RawProvidersRoundTripUnicodeAndZeroLengthWithExactManifest()
    {
        var payload = "compressed rain — 雨 — 🌧️"u8.ToArray();
        foreach (var provider in new IArchiveWriterProvider[]
        {
            new BuiltInGzipArchiveWriterProvider(),
            new BuiltInBzip2ArchiveWriterProvider(),
        })
        {
            var destination = new MemoryStream();
            await using (var writer = await provider.CreateAsync(
                destination,
                provider is BuiltInGzipArchiveWriterProvider
                    ? ArchiveCompressionPreset.Maximum
                    : ArchiveCompressionPreset.Balanced,
                CancellationToken.None))
            {
                await writer.AddFileAsync(
                    new ArchiveWriteEntry("source-name.txt", payload.Length, DateTimeOffset.UtcNow),
                    new MemoryStream(payload),
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None);
                await writer.CompleteAsync(CancellationToken.None);
            }

            Assert.IsTrue(destination.CanRead);
            Assert.IsTrue(destination.CanWrite);
            destination.Position = 1;
            await AsValidator(provider).ValidateAsync(
                destination,
                [RawManifest(payload, "source-name.txt")],
                CancellationToken.None);
            Assert.AreEqual(1, destination.Position);

            var emptyDestination = new MemoryStream();
            await using (var emptyWriter = await provider.CreateAsync(
                emptyDestination,
                provider is BuiltInGzipArchiveWriterProvider
                    ? ArchiveCompressionPreset.Store
                    : ArchiveCompressionPreset.Balanced,
                CancellationToken.None))
            {
                await emptyWriter.AddFileAsync(
                    new ArchiveWriteEntry("payload", 0, DateTimeOffset.UnixEpoch),
                    new MemoryStream(),
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None);
                await emptyWriter.CompleteAsync(CancellationToken.None);
            }

            await AsValidator(provider).ValidateAsync(
                emptyDestination,
                [RawManifest([], "source-name.txt")],
                CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task RawDisposeWithoutEntryReleasesCompressionAndLeavesDestinationOpen()
    {
        foreach (var provider in new ManagedRawSingleFileWriterProvider[]
        {
            new BuiltInGzipArchiveWriterProvider(),
            new BuiltInBzip2ArchiveWriterProvider(),
        })
        {
            var destination = new MemoryStream();
            var writer = await provider.CreateAsync(
                destination,
                provider is BuiltInGzipArchiveWriterProvider
                    ? ArchiveCompressionPreset.Store
                    : ArchiveCompressionPreset.Balanced,
                CancellationToken.None);
            await writer.DisposeAsync();
            await writer.DisposeAsync();
            Assert.IsTrue(destination.CanWrite);
            destination.WriteByte(0x7f);
        }
    }

    [TestMethod]
    public async Task TarProvidersRoundTripUnicodeDirectoriesAndZeroLengthWithExactManifest()
    {
        var payload = "tar compressed"u8.ToArray();
        foreach (var provider in new ManagedCompressedTarWriterProvider[]
        {
            new BuiltInTarGzipArchiveWriterProvider(),
            new BuiltInTarBzip2ArchiveWriterProvider(),
        })
        {
            var destination = new MemoryStream();
            var timestamp = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero).AddTicks(1234);
            await using (var writer = await provider.CreateAsync(
                destination,
                provider is BuiltInTarGzipArchiveWriterProvider
                    ? ArchiveCompressionPreset.Balanced
                    : ArchiveCompressionPreset.Balanced,
                CancellationToken.None))
            {
                await writer.AddDirectoryAsync(
                    new ArchiveWriteDirectory("空", timestamp),
                    CancellationToken.None);
                await writer.AddFileAsync(
                    new ArchiveWriteEntry("空/payload.txt", payload.Length, timestamp),
                    new MemoryStream(payload),
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None);
                await writer.AddFileAsync(
                    new ArchiveWriteEntry("zero.bin", 0, timestamp),
                    new MemoryStream(),
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None);
                await writer.CompleteAsync(CancellationToken.None);
            }

            destination.Position = 1;
            await provider.ValidateAsync(
                destination,
                [
                    new("空", ArchiveEntryKind.Directory, null, timestamp),
                    new("空/payload.txt", ArchiveEntryKind.File, payload.Length, timestamp, Sha256(payload)),
                    new("zero.bin", ArchiveEntryKind.File, 0, timestamp, Sha256([])),
                ],
                CancellationToken.None);
            Assert.AreEqual(1, destination.Position);
        }
    }

    [TestMethod]
    public async Task PresetsAreTruthfulAndUnsupportedPresetsAreRefused()
    {
        var gzip = new BuiltInGzipArchiveWriterProvider();
        CollectionAssert.AreEquivalent(
            Enum.GetValues<ArchiveCompressionPreset>(),
            gzip.CompressionPresets.ToArray());
        var tarGzip = new BuiltInTarGzipArchiveWriterProvider();
        CollectionAssert.AreEquivalent(
            Enum.GetValues<ArchiveCompressionPreset>(),
            tarGzip.CompressionPresets.ToArray());

        foreach (var provider in new IArchiveWriterProvider[]
        {
            new BuiltInBzip2ArchiveWriterProvider(),
            new BuiltInTarBzip2ArchiveWriterProvider(),
        })
        {
            CollectionAssert.AreEqual(
                new[] { ArchiveCompressionPreset.Balanced },
                provider.CompressionPresets.ToArray());
            foreach (var preset in new[]
            {
                ArchiveCompressionPreset.Store,
                ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Maximum,
            })
            {
                await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                    await provider.CreateAsync(new MemoryStream(), preset, CancellationToken.None));
            }
        }
    }

    [TestMethod]
    public async Task RawProvidersRejectDirectoriesMultipleFilesAndLengthMismatches()
    {
        foreach (var provider in new ManagedRawSingleFileWriterProvider[]
        {
            new BuiltInGzipArchiveWriterProvider(),
            new BuiltInBzip2ArchiveWriterProvider(),
        })
        {
            await using var writer = await provider.CreateAsync(
                new MemoryStream(),
                provider is BuiltInGzipArchiveWriterProvider
                    ? ArchiveCompressionPreset.Fast
                    : ArchiveCompressionPreset.Balanced,
                CancellationToken.None);
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await writer.AddDirectoryAsync(
                    new ArchiveWriteDirectory("payload", DateTimeOffset.UnixEpoch),
                    CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await writer.AddFileAsync(
                    new ArchiveWriteEntry("payload", 5, DateTimeOffset.UnixEpoch),
                    new MemoryStream("four"u8.ToArray()),
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None));
            await writer.AddFileAsync(
                new ArchiveWriteEntry("payload", 4, DateTimeOffset.UnixEpoch),
                new MemoryStream("four"u8.ToArray()),
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await writer.AddFileAsync(
                    new ArchiveWriteEntry("payload", 0, DateTimeOffset.UnixEpoch),
                    new MemoryStream(),
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task ValidatorsRejectCorruptionTruncationAndSameLengthDigestMismatch()
    {
        var payload = "integrity"u8.ToArray();
        foreach (var provider in new IArchiveWriterProvider[]
        {
            new BuiltInGzipArchiveWriterProvider(),
            new BuiltInBzip2ArchiveWriterProvider(),
        })
        {
            var bytes = await WriteRawAsync(provider, payload);
            var manifest = RawManifest(payload, "final-name.bin");
            var wrongManifest = RawManifest("different"u8.ToArray(), "final-name.bin");
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await AsValidator(provider).ValidateAsync(
                    new MemoryStream(bytes),
                    [wrongManifest],
                    CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await AsValidator(provider).ValidateAsync(
                    new MemoryStream(bytes[..Math.Max(1, bytes.Length / 2)]),
                    [manifest],
                    CancellationToken.None));

            var corrupt = bytes.ToArray();
            corrupt[corrupt.Length / 2] ^= 0x01;
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await AsValidator(provider).ValidateAsync(
                    new MemoryStream(corrupt),
                    [manifest],
                    CancellationToken.None));
        }

        foreach (var provider in new ManagedCompressedTarWriterProvider[]
        {
            new BuiltInTarGzipArchiveWriterProvider(),
            new BuiltInTarBzip2ArchiveWriterProvider(),
        })
        {
            var bytes = await WriteTarAsync(provider, payload);
            var timestamp = DateTimeOffset.UnixEpoch;
            var manifest = new[]
            {
                new ArchiveStagedOutputManifestEntry(
                    "payload",
                    ArchiveEntryKind.File,
                    payload.Length,
                    timestamp,
                    Sha256(payload)),
            };
            var wrongManifest = new[]
            {
                new ArchiveStagedOutputManifestEntry(
                    "payload",
                    ArchiveEntryKind.File,
                    payload.Length,
                    timestamp,
                    Sha256("different"u8)),
            };
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await provider.ValidateAsync(new MemoryStream(bytes), wrongManifest, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await provider.ValidateAsync(
                    new MemoryStream(bytes[..Math.Max(1, bytes.Length / 2)]),
                    manifest,
                    CancellationToken.None));

            var corrupt = bytes.ToArray();
            corrupt[corrupt.Length / 2] ^= 0x01;
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await provider.ValidateAsync(new MemoryStream(corrupt), manifest, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task ValidatorsRejectTrailingJunkAndConcatenatedMembers()
    {
        var payload = "one member only"u8.ToArray();
        foreach (var provider in new IArchiveWriterProvider[]
        {
            new BuiltInGzipArchiveWriterProvider(),
            new BuiltInBzip2ArchiveWriterProvider(),
        })
        {
            var bytes = await WriteRawAsync(provider, payload);
            var manifest = new[] { RawManifest(payload) };
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await AsValidator(provider).ValidateAsync(
                    new MemoryStream([.. bytes, 0x52, 0x41, 0x49, 0x4e]),
                    manifest,
                    CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await AsValidator(provider).ValidateAsync(
                    new MemoryStream([.. bytes, .. bytes]),
                    manifest,
                    CancellationToken.None));

            if (provider is BuiltInBzip2ArchiveWriterProvider)
            {
                var emptyMember = await WriteRawAsync(provider, []);
                await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                    await AsValidator(provider).ValidateAsync(
                        new MemoryStream([.. bytes, .. emptyMember]),
                        manifest,
                        CancellationToken.None));
            }

            var emptyBytes = await WriteRawAsync(provider, []);
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await AsValidator(provider).ValidateAsync(
                    new MemoryStream([.. emptyBytes, .. emptyBytes]),
                    [RawManifest([])],
                    CancellationToken.None));
        }

        foreach (var provider in new ManagedCompressedTarWriterProvider[]
        {
            new BuiltInTarGzipArchiveWriterProvider(),
            new BuiltInTarBzip2ArchiveWriterProvider(),
        })
        {
            var bytes = await WriteTarAsync(provider, payload);
            var manifest = new[]
            {
                new ArchiveStagedOutputManifestEntry(
                    "payload",
                    ArchiveEntryKind.File,
                    payload.Length,
                    DateTimeOffset.UnixEpoch,
                    Sha256(payload)),
            };
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await provider.ValidateAsync(
                    new MemoryStream([.. bytes, 0x52, 0x41, 0x49, 0x4e]),
                    manifest,
                    CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await provider.ValidateAsync(
                    new MemoryStream([.. bytes, .. bytes]),
                    manifest,
                    CancellationToken.None));

            if (provider is BuiltInTarBzip2ArchiveWriterProvider)
            {
                var emptyMember = await WriteRawAsync(
                    new BuiltInBzip2ArchiveWriterProvider(),
                    []);
                await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                    await provider.ValidateAsync(
                        new MemoryStream([.. bytes, .. emptyMember]),
                        manifest,
                        CancellationToken.None));
            }

            var emptyBytes = await WriteEmptyTarAsync(provider);
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await provider.ValidateAsync(
                    new MemoryStream([.. emptyBytes, .. emptyBytes]),
                    [],
                    CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task ValidatorsRejectMalformedRawManifestsBeforeParsing()
    {
        var provider = new BuiltInGzipArchiveWriterProvider();
        var archive = new MemoryStream();
        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.ValidateAsync(
                archive,
                [],
                CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.ValidateAsync(
                archive,
                [new("other", ArchiveEntryKind.File, 0, DateTimeOffset.UnixEpoch, null)],
                CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.ValidateAsync(
                archive,
                [new("payload", ArchiveEntryKind.Directory, null, DateTimeOffset.UnixEpoch)],
                CancellationToken.None));
    }

    [TestMethod]
    public async Task FinalizationIsOneShotAfterFailureAndCancellationIsHonored()
    {
        var provider = new BuiltInGzipArchiveWriterProvider();
        var destination = new SwitchingWriteStream();
        var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Store,
            CancellationToken.None);
        await writer.AddFileAsync(
            new ArchiveWriteEntry("payload", 4, DateTimeOffset.UnixEpoch),
            new MemoryStream("rain"u8.ToArray()),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);
        destination.FailWrites = true;
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await writer.CompleteAsync(CancellationToken.None));
        var attempts = destination.WriteAttempts;
        await writer.DisposeAsync();
        await writer.DisposeAsync();
        Assert.AreEqual(attempts, destination.WriteAttempts);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.CreateAsync(new MemoryStream(), ArchiveCompressionPreset.Store, cancelled.Token));
    }

    [TestMethod]
    public async Task Bzip2FinalizationFailureDoesNotRetryAndStillReleasesCodec()
    {
        var destination = new SwitchingWriteStream();
        var provider = new BuiltInBzip2ArchiveWriterProvider();
        var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Balanced,
            CancellationToken.None);
        await writer.AddFileAsync(
            new ArchiveWriteEntry("payload", 4, DateTimeOffset.UnixEpoch),
            new MemoryStream("rain"u8.ToArray()),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        destination.FailWrites = true;
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await writer.CompleteAsync(CancellationToken.None));
        var attempts = destination.WriteAttempts;
        await writer.DisposeAsync();
        await writer.DisposeAsync();
        Assert.AreEqual(attempts, destination.WriteAttempts);
    }

    private static async Task<byte[]> WriteRawAsync(
        IArchiveWriterProvider provider,
        byte[] payload)
    {
        var destination = new MemoryStream();
        await using (var writer = await provider.CreateAsync(
            destination,
            provider is BuiltInGzipArchiveWriterProvider
                ? ArchiveCompressionPreset.Balanced
                : ArchiveCompressionPreset.Balanced,
            CancellationToken.None))
        {
            await writer.AddFileAsync(
                new ArchiveWriteEntry("payload", payload.Length, DateTimeOffset.UnixEpoch),
                new MemoryStream(payload),
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        return destination.ToArray();
    }

    private static async Task<byte[]> WriteTarAsync(
        ManagedCompressedTarWriterProvider provider,
        byte[] payload)
    {
        var destination = new MemoryStream();
        await using (var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Balanced,
            CancellationToken.None))
        {
            await writer.AddFileAsync(
                new ArchiveWriteEntry("payload", payload.Length, DateTimeOffset.UnixEpoch),
                new MemoryStream(payload),
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        return destination.ToArray();
    }

    private static async Task<byte[]> WriteEmptyTarAsync(
        ManagedCompressedTarWriterProvider provider)
    {
        var destination = new MemoryStream();
        await using (var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Balanced,
            CancellationToken.None))
        {
            await writer.CompleteAsync(CancellationToken.None);
        }

        return destination.ToArray();
    }

    private static ArchiveStagedOutputManifestEntry RawManifest(
        byte[] payload,
        string path = "payload") =>
        new(path, ArchiveEntryKind.File, payload.Length, DateTimeOffset.UnixEpoch, Sha256(payload));

    private static string Sha256(ReadOnlySpan<byte> payload) => Convert.ToHexString(SHA256.HashData(payload));

    private static IArchiveStagedOutputValidator AsValidator(IArchiveWriterProvider provider) =>
        provider as IArchiveStagedOutputValidator
        ?? throw new InvalidOperationException("Provider does not implement the staged validator seam.");

    private class FailingWriteStream : Stream
    {
        public int WriteAttempts { get; private set; }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            return ValueTask.FromException(new IOException("Injected finalization failure."));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            throw new IOException("Injected finalization failure.");
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteAttempts++;
            throw new IOException("Injected finalization failure.");
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class SwitchingWriteStream : FailingWriteStream
    {
        public bool FailWrites { get; set; }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                return base.WriteAsync(buffer, cancellationToken);
            }

            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (FailWrites)
            {
                return base.WriteAsync(buffer, offset, count, cancellationToken);
            }

            return Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailWrites)
            {
                base.Write(buffer, offset, count);
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailWrites)
            {
                base.Write(buffer);
            }
        }
    }
}
