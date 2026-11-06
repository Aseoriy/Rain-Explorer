using RainArchives.Contracts;

namespace RainArchives.Client;

public sealed class ArchivePreviewLease : IDisposable, IAsyncDisposable
{
    private readonly Action _release;
    private PreviewReadStream? _content;
    private int _disposed;

    internal ArchivePreviewLease(
        ArchiveSessionId sessionId,
        EntryId entryId,
        long length,
        FileStream content,
        Action release)
    {
        SessionId = sessionId;
        EntryId = entryId;
        Length = length;
        _content = new PreviewReadStream(content, Dispose);
        _release = release;
    }

    public ArchiveSessionId SessionId { get; }
    public EntryId EntryId { get; }
    public long Length { get; }
    public Stream Content => _content
        ?? throw new ObjectDisposedException(nameof(ArchivePreviewLease));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var content = Interlocked.Exchange(ref _content, null);
        try
        {
            content?.Dispose();
        }
        finally
        {
            _release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var content = Interlocked.Exchange(ref _content, null);
        try
        {
            if (content is not null)
            {
                await content.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _release();
        }
    }

    private sealed class PreviewReadStream : Stream
    {
        private readonly FileStream _inner;
        private readonly Action _onClosed;
        private int _disposed;

        public PreviewReadStream(FileStream inner, Action onClosed)
        {
            _inner = inner;
            _onClosed = onClosed;
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => Volatile.Read(ref _disposed) == 0;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _inner.Length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _inner.Position;
            }
            set
            {
                ThrowIfDisposed();
                _inner.Position = value;
            }
        }

        public override void Flush()
        {
            ThrowIfDisposed();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            return _inner.Read(buffer);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _onClosed();
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _onClosed();
                }
            }

            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
