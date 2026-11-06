using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Common;
using SharpCompress.Crypto;
using SharpCompressionMode = SharpCompress.Compressors.CompressionMode;

namespace RainArchives.Providers.SharpCompress;

public sealed class BuiltInTarGzipArchiveWriterProvider : ManagedCompressedTarWriterProvider
{
    public override string Id => "dotnet/tar.gz/net10";
    public override string Format => "tar.gz";
    public override IReadOnlySet<ArchiveCompressionPreset> CompressionPresets =>
        ManagedCompressedWriterSupport.GzipPresets;

    protected override ValueTask<Stream> CreateCompressionAsync(
        Stream destination,
        ArchiveCompressionPreset preset,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(
            new GZipStream(
                destination,
                ManagedCompressedWriterSupport.MapGzipLevel(preset),
                leaveOpen: true));

    protected override ValueTask FinishCompressionAsync(
        Stream compression,
        CancellationToken cancellationToken) =>
        ManagedCompressedWriterSupport.FinishGzipAsync(compression, cancellationToken);
}

public sealed class BuiltInTarBzip2ArchiveWriterProvider : ManagedCompressedTarWriterProvider
{
    public override string Id => "sharpcompress/tar.bz2/0.50.4";
    public override string Format => "tar.bz2";
    public override IReadOnlySet<ArchiveCompressionPreset> CompressionPresets =>
        ManagedCompressedWriterSupport.Bzip2Presets;

    protected override async ValueTask<Stream> CreateCompressionAsync(
        Stream destination,
        ArchiveCompressionPreset preset,
        CancellationToken cancellationToken) =>
        await BZip2Stream.CreateAsync(
                destination,
                SharpCompressionMode.Compress,
                decompressConcatenated: false,
                leaveOpen: true,
                tolerateTruncatedStream: false,
                cancellationToken)
            .ConfigureAwait(false);

    protected override ValueTask FinishCompressionAsync(
        Stream compression,
        CancellationToken cancellationToken) =>
        ManagedCompressedWriterSupport.FinishBzip2Async((BZip2Stream)compression, cancellationToken);
}

public sealed class BuiltInGzipArchiveWriterProvider : ManagedRawSingleFileWriterProvider
{
    public override string Id => "dotnet/gzip/net10";
    public override string Format => "gzip";
    public override IReadOnlySet<ArchiveCompressionPreset> CompressionPresets =>
        ManagedCompressedWriterSupport.GzipPresets;

    protected override ValueTask<Stream> CreateCompressionAsync(
        Stream destination,
        ArchiveCompressionPreset preset,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(
            new RawGzipCompressionStream(
                destination,
                ManagedCompressedWriterSupport.MapGzipLevel(preset)));

    protected override ValueTask FinishCompressionAsync(
        Stream compression,
        CancellationToken cancellationToken) =>
        ManagedCompressedWriterSupport.FinishGzipAsync(compression, cancellationToken);

    protected override ValueTask<Stream> CreateDecompressionAsync(
        Stream archive,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(
            new GZipStream(archive, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true));
}

public sealed class BuiltInBzip2ArchiveWriterProvider : ManagedRawSingleFileWriterProvider
{
    public override string Id => "sharpcompress/bzip2/0.50.4";
    public override string Format => "bzip2";
    public override IReadOnlySet<ArchiveCompressionPreset> CompressionPresets =>
        ManagedCompressedWriterSupport.Bzip2Presets;

    protected override async ValueTask<Stream> CreateCompressionAsync(
        Stream destination,
        ArchiveCompressionPreset preset,
        CancellationToken cancellationToken) =>
        await BZip2Stream.CreateAsync(
                destination,
                SharpCompressionMode.Compress,
                decompressConcatenated: false,
                leaveOpen: true,
                tolerateTruncatedStream: false,
                cancellationToken)
            .ConfigureAwait(false);

    protected override ValueTask FinishCompressionAsync(
        Stream compression,
        CancellationToken cancellationToken) =>
        ManagedCompressedWriterSupport.FinishBzip2Async((BZip2Stream)compression, cancellationToken);

    protected override async ValueTask<Stream> CreateDecompressionAsync(
        Stream archive,
        CancellationToken cancellationToken) =>
        await BZip2Stream.CreateAsync(
                archive,
                SharpCompressionMode.Decompress,
                decompressConcatenated: false,
                leaveOpen: true,
                tolerateTruncatedStream: false,
                cancellationToken)
            .ConfigureAwait(false);

}

public abstract class ManagedCompressedTarWriterProvider :
    IArchiveWriterProvider,
    IArchiveStagedOutputValidator
{
    public abstract string Id { get; }
    public abstract string Format { get; }
    public abstract IReadOnlySet<ArchiveCompressionPreset> CompressionPresets { get; }

    protected abstract ValueTask<Stream> CreateCompressionAsync(
        Stream destination,
        ArchiveCompressionPreset preset,
        CancellationToken cancellationToken);

    protected abstract ValueTask FinishCompressionAsync(
        Stream compression,
        CancellationToken cancellationToken);

    public async ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                $"The {Format} destination stream must be writable.",
                nameof(destination));
        }

        ManagedCompressedWriterSupport.ValidatePreset(Format, CompressionPresets, compressionPreset);
        var compression = await CreateCompressionAsync(
                destination,
                compressionPreset,
                cancellationToken)
            .ConfigureAwait(false);
        var tar = await new BuiltInTarArchiveWriterProvider()
            .CreateAsync(compression, ArchiveCompressionPreset.Store, cancellationToken)
            .ConfigureAwait(false);
        return new CompressedTarWriterHandle(tar, compression, FinishCompressionAsync);
    }

    public async ValueTask ValidateAsync(
        Stream archive,
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(expected);
        BuiltInTarArchiveWriterProvider.ValidateExpectedManifest(expected);
        if (!archive.CanRead || !archive.CanSeek)
        {
            throw new ArgumentException(
                $"{Format} staged-output validation requires a readable, seekable stream.",
                nameof(archive));
        }

        var originalPosition = archive.Position;
        try
        {
            if (expected.Count == 0 && Format == "tar.bz2")
            {
                await ManagedCompressedWriterSupport
                    .ValidateCanonicalStreamAsync(
                        archive,
                        ManagedCompressedWriterSupport.CanonicalEmptyTarBzip2Stream,
                        Format,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            archive.Position = 0;
            await using var decompressor = await CreateDecompressionAsync(
                    archive,
                    cancellationToken)
                .ConfigureAwait(false);
            using var decoded = new DecodedIntegrityReadStream(decompressor);
            await BuiltInTarArchiveWriterProvider
                .ValidateTarPayloadAsync(decoded, expected, cancellationToken)
                .ConfigureAwait(false);
            // ValidateTarPayloadAsync drains the decoded TAR to EOF, which
            // forces GZipStream/BZip2Stream to verify their outer trailer.
            // Keep this second drain as an explicit invariant if a TAR reader
            // implementation ever stops before the decoder's EOF.
            await ManagedCompressedWriterSupport.DrainAsync(decoded, cancellationToken)
                .ConfigureAwait(false);
            if (Format == "tar.gz")
            {
                await ManagedCompressedWriterSupport.ValidateGzipTrailerAsync(
                        archive,
                        decoded.TotalRead,
                        decoded.Crc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ManagedCompressedWriterSupport.ValidateBzip2FooterAsync(
                        archive,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (SharpCompressException exception)
        {
            throw ManagedCompressedWriterSupport.InvalidOutput(Format, exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or NotSupportedException
                or ArgumentException)
        {
            throw ManagedCompressedWriterSupport.InvalidOutput(Format, exception);
        }
        finally
        {
            archive.Position = originalPosition;
        }
    }

    private async ValueTask<Stream> CreateDecompressionAsync(
        Stream archive,
        CancellationToken cancellationToken)
    {
        if (Format == "tar.gz")
        {
            return new GZipStream(archive, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        }

        return await BZip2Stream.CreateAsync(
                archive,
                SharpCompressionMode.Decompress,
                decompressConcatenated: false,
                leaveOpen: true,
                tolerateTruncatedStream: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class CompressedTarWriterHandle : IArchiveWriterHandle
    {
        private readonly IArchiveWriterHandle _tar;
        private readonly Stream _compression;
        private readonly Func<Stream, CancellationToken, ValueTask> _finish;
        private int _finalizationStarted;

        public CompressedTarWriterHandle(
            IArchiveWriterHandle tar,
            Stream compression,
            Func<Stream, CancellationToken, ValueTask> finish)
        {
            _tar = tar;
            _compression = compression;
            _finish = finish;
        }

        public ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken)
        {
            EnsureActive();
            return _tar.AddDirectoryAsync(entry, cancellationToken);
        }

        public ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            EnsureActive();
            return _tar.AddFileAsync(entry, source, reportEntryBytes, cancellationToken);
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                throw new ObjectDisposedException(nameof(CompressedTarWriterHandle));
            }

            await _tar.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await _finish(_compression, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                return;
            }

            await _tar.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            await _finish(_compression, CancellationToken.None).ConfigureAwait(false);
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref _finalizationStarted) != 0)
            {
                throw new ObjectDisposedException(nameof(CompressedTarWriterHandle));
            }
        }
    }
}

public abstract class ManagedRawSingleFileWriterProvider :
    IArchiveWriterProvider,
    IArchiveStagedOutputValidator
{
    public abstract string Id { get; }
    public abstract string Format { get; }
    public abstract IReadOnlySet<ArchiveCompressionPreset> CompressionPresets { get; }

    protected abstract ValueTask<Stream> CreateCompressionAsync(
        Stream destination,
        ArchiveCompressionPreset preset,
        CancellationToken cancellationToken);

    protected abstract ValueTask FinishCompressionAsync(
        Stream compression,
        CancellationToken cancellationToken);

    protected abstract ValueTask<Stream> CreateDecompressionAsync(
        Stream archive,
        CancellationToken cancellationToken);

    public async ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                $"The {Format} destination stream must be writable.",
                nameof(destination));
        }

        ManagedCompressedWriterSupport.ValidatePreset(Format, CompressionPresets, compressionPreset);
        var compression = await CreateCompressionAsync(
                destination,
                compressionPreset,
                cancellationToken)
            .ConfigureAwait(false);
        return new RawWriterHandle(Format, compression, FinishCompressionAsync);
    }

    public async ValueTask ValidateAsync(
        Stream archive,
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(expected);
        ManagedCompressedWriterSupport.ValidateRawManifest(expected);
        if (!archive.CanRead || !archive.CanSeek)
        {
            throw new ArgumentException(
                $"{Format} staged-output validation requires a readable, seekable stream.",
                nameof(archive));
        }

        var originalPosition = archive.Position;
        try
        {
            if (expected[0].Length == 0)
            {
                await ManagedCompressedWriterSupport
                    .ValidateCanonicalEmptyRawStreamAsync(
                        archive,
                        Format,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            archive.Position = 0;
            await using var decompressor = await CreateDecompressionAsync(
                    archive,
                    cancellationToken)
                .ConfigureAwait(false);
            using var decoded = new DecodedIntegrityReadStream(decompressor);
            var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                long total = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await decoded
                        .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total = checked(total + read);
                    if (total > expected[0].Length)
                    {
                        throw ManagedCompressedWriterSupport.ManifestMismatch(Format);
                    }

                    digest.AppendData(buffer.AsSpan(0, read));
                }

                if (total != expected[0].Length)
                {
                    throw ManagedCompressedWriterSupport.ManifestMismatch(Format);
                }

                ManagedCompressedWriterSupport.ValidateDigest(
                    digest.GetHashAndReset(),
                    expected[0].ContentSha256!,
                    Format);
                if (Format == "gzip")
                {
                    await ManagedCompressedWriterSupport.ValidateGzipTrailerAsync(
                            archive,
                            decoded.TotalRead,
                            decoded.Crc,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ManagedCompressedWriterSupport.ValidateBzip2FooterAsync(
                            archive,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (SharpCompressException exception)
        {
            throw ManagedCompressedWriterSupport.InvalidOutput(Format, exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or NotSupportedException
                or ArgumentException)
        {
            throw ManagedCompressedWriterSupport.InvalidOutput(Format, exception);
        }
        finally
        {
            archive.Position = originalPosition;
        }
    }

    private sealed class RawWriterHandle : IArchiveWriterHandle
    {
        private readonly Stream _compression;
        private readonly Func<Stream, CancellationToken, ValueTask> _finish;
        private readonly string _format;
        private bool _fileWritten;
        private int _finalizationStarted;

        public RawWriterHandle(
            string format,
            Stream compression,
            Func<Stream, CancellationToken, ValueTask> finish)
        {
            _format = format;
            _compression = compression;
            _finish = finish;
        }

        public ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken)
        {
            EnsureActive();
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                $"{FormatCode()}.directory_unsupported",
                $"Raw {FormatCode()} creation accepts exactly one regular file and no directories.");
        }

        public async ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(reportEntryBytes);
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (_fileWritten)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    $"{FormatCode()}.multiple_files_unsupported",
                    $"Raw {FormatCode()} creation accepts exactly one regular file.");
            }

            if (string.IsNullOrWhiteSpace(entry.ArchivePath)
                || entry.Length < 0
                || entry.WindowsAttributes is not null
                || !source.CanRead)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    $"{FormatCode()}.entry_metadata_unsupported",
                    $"Raw {FormatCode()} creation accepts one validated logical regular-file path and does not preserve platform metadata.");
            }

            await ManagedCompressedWriterSupport.CopyExactAsync(
                    source,
                    _compression,
                    entry.Length,
                    reportEntryBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            _fileWritten = true;
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            if (!_fileWritten)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    $"{FormatCode()}.file_required",
                    $"Raw {FormatCode()} creation requires exactly one regular file.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                throw new ObjectDisposedException(nameof(RawWriterHandle));
            }

            await _finish(_compression, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                return;
            }

            await _finish(_compression, CancellationToken.None).ConfigureAwait(false);
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref _finalizationStarted) != 0)
            {
                throw new ObjectDisposedException(nameof(RawWriterHandle));
            }
        }

        private string FormatCode() => _format;
    }
}

internal static class ManagedCompressedWriterSupport
{
    internal static readonly byte[] CanonicalEmptyGzipMember =
        Convert.FromHexString("1F8B080000000000000303000000000000000000");
    internal static readonly byte[] CanonicalEmptyBzip2Stream =
        Convert.FromHexString("425A683917724538509000000000");
    internal static readonly byte[] CanonicalEmptyTarBzip2Stream =
        Convert.FromHexString(
            "425A683931415926535974F5ADF70000044000C0000008200030802A6945AC38BB9229C28483A7AD6FB8");
    internal static readonly IReadOnlySet<ArchiveCompressionPreset> GzipPresets =
        new HashSet<ArchiveCompressionPreset>
        {
            ArchiveCompressionPreset.Store,
            ArchiveCompressionPreset.Fast,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Maximum,
        };
    internal static readonly IReadOnlySet<ArchiveCompressionPreset> Bzip2Presets =
        new HashSet<ArchiveCompressionPreset>
        {
            ArchiveCompressionPreset.Balanced,
        };

    internal static CompressionLevel MapGzipLevel(ArchiveCompressionPreset preset) => preset switch
    {
        ArchiveCompressionPreset.Store => CompressionLevel.NoCompression,
        ArchiveCompressionPreset.Fast => CompressionLevel.Fastest,
        ArchiveCompressionPreset.Balanced => CompressionLevel.Optimal,
        ArchiveCompressionPreset.Maximum => CompressionLevel.SmallestSize,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    internal static async ValueTask FinishBzip2Async(
        BZip2Stream compression,
        CancellationToken cancellationToken)
    {
        // FinishAsync emits the checksum/footer. DisposeAsync is then only
        // resource cleanup; it must not be used after a failed FinishAsync,
        // because that could retry an uncertain footer write.
        await compression.FinishAsync(cancellationToken).ConfigureAwait(false);
        await compression.DisposeAsync().ConfigureAwait(false);
    }

    internal static async ValueTask FinishGzipAsync(
        Stream compression,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (compression is RawGzipCompressionStream raw)
        {
            await raw.FinishAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await compression.DisposeAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void ValidatePreset(
        string format,
        IReadOnlySet<ArchiveCompressionPreset> supported,
        ArchiveCompressionPreset preset)
    {
        if (!supported.Contains(preset))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                $"{format}.compression_preset_unsupported",
                $"The selected {format} compression preset is not supported by the managed writer.");
        }
    }

    internal static async ValueTask CopyExactAsync(
        Stream source,
        Stream destination,
        long expectedLength,
        Func<long, ValueTask> report,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        try
        {
            long copied = 0;
            while (copied < expectedLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, expectedLength - copied);
                var read = await source
                    .ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw SourceChanged();
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                copied = checked(copied + read);
                await report(copied).ConfigureAwait(false);
            }

            var probe = await source
                .ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);
            if (probe != 0)
            {
                throw SourceChanged();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static async ValueTask DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        try
        {
            while (await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static async ValueTask ValidateGzipTrailerAsync(
        Stream archive,
        long decodedLength,
        uint decodedCrc,
        CancellationToken cancellationToken)
    {
        if (archive.Length < 18)
        {
            throw ManifestMismatch("gzip");
        }

        var trailer = new byte[8];
        archive.Position = archive.Length - trailer.Length;
        await archive.ReadExactlyAsync(trailer, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != decodedCrc
            || BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4)) != unchecked((uint)decodedLength))
        {
            throw ManifestMismatch("gzip");
        }
    }

    internal static async ValueTask ValidateBzip2FooterAsync(
        Stream archive,
        CancellationToken cancellationToken)
    {
        const ulong streamEndMarker = 0x177245385090UL;
        if (archive.Length < 14 || archive.Position != archive.Length)
        {
            throw ManifestMismatch("bzip2");
        }

        var header = new byte[4];
        archive.Position = 0;
        await archive.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != (byte)'B'
            || header[1] != (byte)'Z'
            || header[2] != (byte)'h'
            || header[3] is < (byte)'1' or > (byte)'9')
        {
            throw ManifestMismatch("bzip2");
        }

        var tailLength = (int)Math.Min(archive.Length, 11);
        var tail = new byte[tailLength];
        archive.Position = archive.Length - tailLength;
        await archive.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        var totalBits = checked(archive.Length * 8);
        var tailStartBit = checked((archive.Length - tailLength) * 8);
        for (var paddingBits = 0; paddingBits < 8; paddingBits++)
        {
            var markerStart = totalBits - paddingBits - 80;
            if (markerStart < tailStartBit
                || !BitsEqual(tail, markerStart - tailStartBit, streamEndMarker, 48))
            {
                continue;
            }

            var paddingStart = totalBits - paddingBits;
            var paddingIsZero = true;
            for (var bit = paddingStart; bit < totalBits; bit++)
            {
                if (ReadBit(tail, bit - tailStartBit))
                {
                    paddingIsZero = false;
                    break;
                }
            }

            if (paddingIsZero)
            {
                return;
            }
        }

        throw ManifestMismatch("bzip2");
    }

    internal static async ValueTask ValidateCanonicalEmptyRawStreamAsync(
        Stream archive,
        string format,
        CancellationToken cancellationToken)
    {
        var canonical = format == "gzip"
            ? CanonicalEmptyGzipMember
            : CanonicalEmptyBzip2Stream;
        await ValidateCanonicalStreamAsync(
                archive,
                canonical,
                format,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async ValueTask ValidateCanonicalStreamAsync(
        Stream archive,
        byte[] canonical,
        string format,
        CancellationToken cancellationToken)
    {
        if (archive.Length != canonical.Length)
        {
            throw ManifestMismatch(format);
        }

        archive.Position = 0;
        var actual = new byte[canonical.Length];
        await archive.ReadExactlyAsync(actual, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, canonical))
        {
            throw ManifestMismatch(format);
        }
    }

    private static bool BitsEqual(
        ReadOnlySpan<byte> bytes,
        long startBit,
        ulong expected,
        int bitCount)
    {
        for (var index = 0; index < bitCount; index++)
        {
            var expectedBit = ((expected >> (bitCount - index - 1)) & 1UL) != 0;
            if (ReadBit(bytes, startBit + index) != expectedBit)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadBit(ReadOnlySpan<byte> bytes, long bitIndex)
    {
        var byteIndex = checked((int)(bitIndex / 8));
        var offset = checked((int)(bitIndex % 8));
        return (bytes[byteIndex] & (1 << (7 - offset))) != 0;
    }

    internal static void ValidateRawManifest(
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected)
    {
        // Raw streams do not carry a portable member filename. The caller
        // owns the validated logical/final-name mapping; this provider only
        // validates the one regular-file content tuple.
        if (expected.Count != 1
            || expected[0] is null
            || expected[0].Kind != ArchiveEntryKind.File
            || expected[0].Length is null or < 0
            || !IsSha256(expected[0].ContentSha256))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "raw.output_manifest_invalid",
                "Raw staged-output validation requires exactly one regular file with an exact SHA-256 digest.");
        }
    }

    internal static void ValidateDigest(byte[] actual, string expectedHex, string format)
    {
        var expected = Convert.FromHexString(expectedHex);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw ManifestMismatch(format);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    internal static ArchiveProviderException ManifestMismatch(string format) =>
        new(
            ArchiveErrorCategory.ProviderFailure,
            $"{format}.output_manifest_mismatch",
            $"The staged {format} output did not match the exact validated output manifest.");

    internal static ArchiveProviderException InvalidOutput(string format, Exception inner) =>
        new(
            ArchiveErrorCategory.CorruptArchive,
            $"{format}.output_invalid",
            $"The staged {format} output could not be reopened and validated.",
            inner);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static ArchiveProviderException SourceChanged() =>
        new(
            ArchiveErrorCategory.SourceChanged,
            "source.changed_during_creation",
            "A source stream changed while it was being added to the compressed archive.");
}

internal sealed class RawGzipCompressionStream : Stream
{
    private readonly Stream _destination;
    private readonly CompressionLevel _level;
    private GZipStream? _compression;
    private int _finished;

    public RawGzipCompressionStream(Stream destination, CompressionLevel level)
    {
        _destination = destination;
        _level = level;
    }

    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_compression is null)
        {
            await _destination.WriteAsync(
                    ManagedCompressedWriterSupport.CanonicalEmptyGzipMember,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _compression.DisposeAsync().ConfigureAwait(false);
        }
    }

    private GZipStream GetCompression()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _finished) != 0, this);
        return _compression ??= new GZipStream(_destination, _level, leaveOpen: true);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _finished) != 0, this);
            return;
        }

        GetCompression().Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _finished) != 0, this);
            return;
        }

        GetCompression().Write(buffer);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _finished) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        return GetCompression().WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => _compression?.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _compression?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => Volatile.Read(ref _finished) == 0;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class DecodedIntegrityReadStream : Stream
{
    private readonly Stream _source;
    private readonly Crc32Stream _checksum = new(Stream.Null);

    public DecodedIntegrityReadStream(Stream source) => _source = source;

    public long TotalRead { get; private set; }
    public uint Crc => TotalRead == 0 ? 0u : _checksum.Crc;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        Track(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _source.Read(buffer);
        Track(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Track(buffer.Span[..read]);
        return read;
    }

    private void Track(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        TotalRead = checked(TotalRead + bytes.Length);
        _checksum.Write(bytes);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _checksum.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => TotalRead;
        set => throw new NotSupportedException();
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
