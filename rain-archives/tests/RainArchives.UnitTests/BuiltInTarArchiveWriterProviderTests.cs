using System.Formats.Tar;
using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.SharpCompress;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class BuiltInTarArchiveWriterProviderTests
{
    [TestMethod]
    public async Task WritesUnicodeZeroLengthAndExplicitDirectoriesWithoutClosingDestination()
    {
        var destination = new MemoryStream();
        var provider = new BuiltInTarArchiveWriterProvider();
        var timestamp = new DateTimeOffset(2026, 8, 25, 12, 34, 56, TimeSpan.Zero).AddTicks(1234);
        var payload = "rain archives"u8.ToArray();
        var reported = new List<long>();

        await using (var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Store,
            CancellationToken.None))
        {
            await writer.AddDirectoryAsync(new ArchiveWriteDirectory("空", timestamp), CancellationToken.None);
            await writer.AddDirectoryAsync(new ArchiveWriteDirectory("empty/", timestamp), CancellationToken.None);
            await writer.AddFileAsync(
                new ArchiveWriteEntry("空/данные.txt", payload.Length, timestamp),
                new MemoryStream(payload),
                bytes =>
                {
                    reported.Add(bytes);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);
            await writer.AddFileAsync(
                new ArchiveWriteEntry("zero.bin", 0, timestamp),
                new MemoryStream(),
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
            var completedLength = destination.Length;
            await writer.DisposeAsync();
            await writer.DisposeAsync();
            Assert.AreEqual(completedLength, destination.Length);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
                await writer.AddDirectoryAsync(
                    new ArchiveWriteDirectory("after-complete", timestamp),
                    CancellationToken.None));
        }

        Assert.IsTrue(destination.CanRead);
        Assert.IsTrue(destination.CanWrite);
        Assert.IsTrue(reported.SequenceEqual([13L]));

        destination.Position = 0;
        await using var reader = new TarReader(destination, leaveOpen: true);
        var entries = new List<(string Name, TarEntryType Type, long Length, byte[] Data)>();
        while (await reader.GetNextEntryAsync(copyData: true, CancellationToken.None) is { } entry)
        {
            using var data = new MemoryStream();
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(data);
            }

            entries.Add((entry.Name, entry.EntryType, entry.Length, data.ToArray()));
            Assert.AreEqual(timestamp, entry.ModificationTime);
        }

        Assert.HasCount(4, entries);
        Assert.AreEqual(("空/", TarEntryType.Directory, 0L), (entries[0].Name, entries[0].Type, entries[0].Length));
        Assert.AreEqual(("empty/", TarEntryType.Directory, 0L), (entries[1].Name, entries[1].Type, entries[1].Length));
        Assert.AreEqual(("空/данные.txt", TarEntryType.RegularFile, (long)payload.Length), (entries[2].Name, entries[2].Type, entries[2].Length));
        CollectionAssert.AreEqual(payload, entries[2].Data);
        Assert.AreEqual(("zero.bin", TarEntryType.RegularFile, 0L), (entries[3].Name, entries[3].Type, entries[3].Length));
    }

    [TestMethod]
    public async Task RejectsCompressionPresetsThatDoNotApplyToUncompressedTar()
    {
        var provider = new BuiltInTarArchiveWriterProvider();
        foreach (var preset in new[]
        {
            ArchiveCompressionPreset.Fast,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Maximum,
        })
        {
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await provider.CreateAsync(new MemoryStream(), preset, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task EmptyTarIsCanonicalAndPassesStagedReadback()
    {
        var provider = new BuiltInTarArchiveWriterProvider();
        var destination = new MemoryStream();
        await using (var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Store,
            CancellationToken.None))
        {
            await writer.CompleteAsync(CancellationToken.None);
        }

        Assert.AreEqual(1024, destination.Length);
        Assert.IsTrue(destination.ToArray().All(value => value == 0));
        await provider.ValidateAsync(destination, [], CancellationToken.None);
    }

    [TestMethod]
    public async Task RejectsPlatformMetadataAndPreEpochTimestampsAndHonorsCancellation()
    {
        var provider = new BuiltInTarArchiveWriterProvider();
        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
        {
            await using var writer = await provider.CreateAsync(
                new MemoryStream(),
                ArchiveCompressionPreset.Store,
                CancellationToken.None);
            await writer.AddDirectoryAsync(
                new ArchiveWriteDirectory("folder", DateTimeOffset.UnixEpoch, (int)FileAttributes.Directory),
                CancellationToken.None);
        });

        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
        {
            await using var writer = await provider.CreateAsync(
                new MemoryStream(),
                ArchiveCompressionPreset.Store,
                CancellationToken.None);
            await writer.AddFileAsync(
                new ArchiveWriteEntry("old", 0, DateTimeOffset.UnixEpoch.AddTicks(-1)),
                new MemoryStream(),
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
        });

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.CreateAsync(new MemoryStream(), ArchiveCompressionPreset.Store, cancelled.Token));
    }

    [TestMethod]
    public async Task SourceLengthMismatchIsRejectedAndValidatorChecksOrderedManifest()
    {
        var destination = new MemoryStream();
        var provider = new BuiltInTarArchiveWriterProvider();
        await using (var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Store,
            CancellationToken.None))
        {
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await writer.AddFileAsync(
                    new ArchiveWriteEntry("short.bin", 5, DateTimeOffset.UnixEpoch),
                    new MemoryStream("rain"u8.ToArray()),
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None));
        }

        await using (var writer = await provider.CreateAsync(
            new MemoryStream(),
            ArchiveCompressionPreset.Store,
            CancellationToken.None))
        {
            await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await writer.AddFileAsync(
                    new ArchiveWriteEntry("long.bin", 4, DateTimeOffset.UnixEpoch),
                    new MemoryStream("rain!"u8.ToArray()),
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None));
        }

        destination.SetLength(0);
        await using (var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Store,
            CancellationToken.None))
        {
            await writer.AddDirectoryAsync(new ArchiveWriteDirectory("folder", DateTimeOffset.UnixEpoch), CancellationToken.None);
            await writer.AddFileAsync(
                new ArchiveWriteEntry("folder/file.txt", 4, DateTimeOffset.UnixEpoch),
                new MemoryStream("rain"u8.ToArray()),
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        var manifest = new ArchiveStagedOutputManifestEntry[]
        {
            new("folder", ArchiveEntryKind.Directory, null, DateTimeOffset.UnixEpoch),
            new(
                "folder/file.txt",
                ArchiveEntryKind.File,
                4,
                DateTimeOffset.UnixEpoch,
                Convert.ToHexString(SHA256.HashData("rain"u8))),
        };
        await provider.ValidateAsync(destination, manifest, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.ValidateAsync(
                destination,
                [manifest[1], manifest[0]],
                CancellationToken.None));

        var validBytes = destination.ToArray();
        var invalidEndMarker = validBytes.ToArray();
        invalidEndMarker[^1] = 1;
        var endMarkerException = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.ValidateAsync(
                new MemoryStream(invalidEndMarker),
                manifest,
                CancellationToken.None));
        Assert.AreEqual("tar.output_manifest_mismatch", endMarkerException.Code);

        var truncatedException = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.ValidateAsync(
                new MemoryStream(validBytes[..^1]),
                manifest,
                CancellationToken.None));
        Assert.AreEqual("tar.output_manifest_mismatch", truncatedException.Code);

        var sameLengthCorruption = validBytes.ToArray();
        var payloadOffset = sameLengthCorruption.AsSpan().IndexOf("rain"u8);
        Assert.IsGreaterThanOrEqualTo(0, payloadOffset);
        sameLengthCorruption[payloadOffset] ^= 0x01;
        var digestException = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.ValidateAsync(
                new MemoryStream(sameLengthCorruption),
                manifest,
                CancellationToken.None));
        Assert.AreEqual("tar.output_manifest_mismatch", digestException.Code);

        var invalidKind = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.ValidateAsync(
                destination,
                [new("unknown", ArchiveEntryKind.Unknown, null, DateTimeOffset.UnixEpoch)],
                CancellationToken.None));
        Assert.AreEqual("tar.output_manifest_invalid", invalidKind.Code);
    }

    [TestMethod]
    public async Task FailedFinalizationIsAttemptedOnlyOnce()
    {
        var provider = new BuiltInTarArchiveWriterProvider();
        var destination = new FinalizationFailingStream();
        var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Store,
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await writer.CompleteAsync(CancellationToken.None));
        var writesAfterFailure = destination.WriteAttempts;
        Assert.IsGreaterThan(0, writesAfterFailure);

        await writer.DisposeAsync();
        await writer.DisposeAsync();
        Assert.AreEqual(writesAfterFailure, destination.WriteAttempts);
    }

    private sealed class FinalizationFailingStream : Stream
    {
        public int WriteAttempts { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            throw new IOException("Injected TAR finalization failure.");
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteAttempts++;
            throw new IOException("Injected TAR finalization failure.");
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            return ValueTask.FromException(new IOException("Injected TAR finalization failure."));
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
}
