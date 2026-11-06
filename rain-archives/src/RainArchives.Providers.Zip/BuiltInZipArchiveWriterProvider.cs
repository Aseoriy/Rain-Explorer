using System.Buffers;
using System.IO.Compression;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Providers.Zip;

public sealed class BuiltInZipArchiveWriterProvider : IArchiveWriterProvider
{
    private static readonly IReadOnlySet<ArchiveCompressionPreset> SupportedPresets =
        new HashSet<ArchiveCompressionPreset>
        {
            ArchiveCompressionPreset.Store,
            ArchiveCompressionPreset.Fast,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Maximum,
        };

    public string Id => "dotnet/ziparchive/net10";
    public string Format => "zip";
    public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => SupportedPresets;

    public async ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The ZIP destination stream must be writable.", nameof(destination));
        }

        if (!SupportedPresets.Contains(compressionPreset))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.compression_preset_unsupported",
                "The selected ZIP compression preset is not supported by the managed writer.");
        }

        var archive = await ZipArchive.CreateAsync(
                destination,
                ZipArchiveMode.Create,
                leaveOpen: true,
                entryNameEncoding: null,
                cancellationToken)
            .ConfigureAwait(false);
        return new WriterHandle(archive, MapCompressionLevel(compressionPreset));
    }

    private static CompressionLevel MapCompressionLevel(ArchiveCompressionPreset preset) => preset switch
    {
        ArchiveCompressionPreset.Store => CompressionLevel.NoCompression,
        ArchiveCompressionPreset.Fast => CompressionLevel.Fastest,
        ArchiveCompressionPreset.Balanced => CompressionLevel.Optimal,
        ArchiveCompressionPreset.Maximum => CompressionLevel.SmallestSize,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    private sealed class WriterHandle : IArchiveWriterHandle
    {
        private const int CopyBufferBytes = 1024 * 1024;
        private readonly CompressionLevel _compressionLevel;
        private ZipArchive? _archive;
        private bool _entryOpen;

        public WriterHandle(ZipArchive archive, CompressionLevel compressionLevel)
        {
            _archive = archive;
            _compressionLevel = compressionLevel;
        }

        public ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.ArchivePath))
            {
                throw new ArgumentException("ZIP directory entry metadata is invalid.", nameof(entry));
            }

            var archive = _archive
                ?? throw new ObjectDisposedException(nameof(WriterHandle));
            if (_entryOpen)
            {
                throw new InvalidOperationException("ZIP entries must be written sequentially.");
            }

            var archivePath = entry.ArchivePath.EndsWith('/')
                ? entry.ArchivePath
                : $"{entry.ArchivePath}/";
            var zipEntry = archive.CreateEntry(archivePath, CompressionLevel.NoCompression);
            zipEntry.LastWriteTime = entry.LastWriteTime;
            zipEntry.ExternalAttributes = (entry.WindowsAttributes ?? 0)
                | (int)FileAttributes.Directory;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
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
            if (!source.CanRead)
            {
                throw new ArgumentException("The ZIP entry source must be readable.", nameof(source));
            }

            if (entry.Length < 0 || string.IsNullOrWhiteSpace(entry.ArchivePath))
            {
                throw new ArgumentException("ZIP entry metadata is invalid.", nameof(entry));
            }

            var archive = _archive
                ?? throw new ObjectDisposedException(nameof(WriterHandle));
            if (_entryOpen)
            {
                throw new InvalidOperationException("ZIP entries must be written sequentially.");
            }

            _entryOpen = true;
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
            try
            {
                var zipEntry = archive.CreateEntry(entry.ArchivePath, _compressionLevel);
                zipEntry.LastWriteTime = entry.LastWriteTime;
                if (entry.WindowsAttributes is { } attributes)
                {
                    zipEntry.ExternalAttributes = attributes & ~(int)FileAttributes.Directory;
                }
                await using var destination = zipEntry.Open();
                long copied = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytesRead = await source
                        .ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    copied = checked(copied + bytesRead);
                    if (copied > entry.Length)
                    {
                        throw SourceChanged();
                    }

                    await destination
                        .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                        .ConfigureAwait(false);
                    await reportEntryBytes(copied).ConfigureAwait(false);
                }

                if (copied != entry.Length)
                {
                    throw SourceChanged();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                _entryOpen = false;
            }
        }

        public async ValueTask AddFileFromProducerAsync(
            ArchiveWriteEntry entry,
            Func<Stream, CancellationToken, ValueTask> writeSource,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(writeSource);
            ArgumentNullException.ThrowIfNull(reportEntryBytes);
            if (entry.Length < 0 || string.IsNullOrWhiteSpace(entry.ArchivePath))
            {
                throw new ArgumentException("ZIP entry metadata is invalid.", nameof(entry));
            }

            var archive = _archive
                ?? throw new ObjectDisposedException(nameof(WriterHandle));
            if (_entryOpen)
            {
                throw new InvalidOperationException("ZIP entries must be written sequentially.");
            }

            _entryOpen = true;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var zipEntry = archive.CreateEntry(entry.ArchivePath, _compressionLevel);
                zipEntry.LastWriteTime = entry.LastWriteTime;
                if (entry.WindowsAttributes is { } attributes)
                {
                    zipEntry.ExternalAttributes = attributes & ~(int)FileAttributes.Directory;
                }

                await using var destination = zipEntry.Open();
                await using var guardedDestination = new ExactLengthReportingWriteStream(
                    destination,
                    entry.Length,
                    reportEntryBytes);
                await writeSource(guardedDestination, cancellationToken).ConfigureAwait(false);
                guardedDestination.ValidateComplete();
            }
            finally
            {
                _entryOpen = false;
            }
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = Interlocked.Exchange(ref _archive, null)
                ?? throw new ObjectDisposedException(nameof(WriterHandle));
            await archive.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async ValueTask DisposeAsync()
        {
            var archive = Interlocked.Exchange(ref _archive, null);
            if (archive is not null)
            {
                try
                {
                    await archive.DisposeAsync().ConfigureAwait(false);
                }
                catch (ArchiveProviderException)
                {
                    // CompleteAsync owns successful finalization. Reaching DisposeAsync
                    // with a live archive is the abort path, where guarded ZIP cleanup can
                    // repeat the authoritative write failure while the staging file is
                    // about to be discarded by its owner.
                }
            }
        }

        private static ArchiveProviderException SourceChanged() =>
            new(
                ArchiveErrorCategory.SourceChanged,
                "source.changed_during_creation",
                "A source file changed while it was being added to the archive.");

        private sealed class ExactLengthReportingWriteStream : Stream
        {
            private readonly Stream _destination;
            private readonly long _expectedLength;
            private readonly Func<long, ValueTask> _reportEntryBytes;
            private long _written;

            public ExactLengthReportingWriteStream(
                Stream destination,
                long expectedLength,
                Func<long, ValueTask> reportEntryBytes)
            {
                _destination = destination;
                _expectedLength = expectedLength;
                _reportEntryBytes = reportEntryBytes;
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

            public void ValidateComplete()
            {
                if (_written != _expectedLength)
                {
                    throw SourceChanged();
                }
            }

            public override void Flush() => _destination.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) =>
                _destination.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                ArgumentNullException.ThrowIfNull(buffer);
                ArgumentOutOfRangeException.ThrowIfNegative(offset);
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                if (buffer.Length - offset < count)
                {
                    throw new ArgumentException("The buffer range is invalid.");
                }

                EnsureCapacity(count);
                _destination.Write(buffer, offset, count);
                _written = checked(_written + count);
                _reportEntryBytes(_written).AsTask().GetAwaiter().GetResult();
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                EnsureCapacity(buffer.Length);
                await _destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                _written = checked(_written + buffer.Length);
                await _reportEntryBytes(_written).ConfigureAwait(false);
            }

            protected override void Dispose(bool disposing)
            {
                // The writer owns the ZIP entry stream.
                base.Dispose(disposing);
            }

            public override ValueTask DisposeAsync()
            {
                GC.SuppressFinalize(this);
                return ValueTask.CompletedTask;
            }

            private void EnsureCapacity(int count)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                long next;
                try
                {
                    next = checked(_written + count);
                }
                catch (OverflowException)
                {
                    throw SourceChanged();
                }

                if (next > _expectedLength)
                {
                    throw SourceChanged();
                }
            }
        }
    }
}
