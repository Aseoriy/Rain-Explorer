namespace RainArchives.Security;

public sealed class ArchiveCreationGuardWriteStream : Stream
{
    private readonly Stream _destination;
    private readonly ArchiveCreationOutputGuard _guard;
    private long _bytesWritten;

    public ArchiveCreationGuardWriteStream(
        Stream destination,
        ArchiveCreationOutputGuard guard)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(guard);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The ZIP staging stream must be writable.", nameof(destination));
        }

        _destination = destination;
        _guard = guard;
    }

    public long BytesWritten => _bytesWritten;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _destination.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        if (count == 0)
        {
            return;
        }

        _guard.ValidateNextChunk(_bytesWritten, count);
        _destination.Write(buffer, offset, count);
        _bytesWritten = checked(_bytesWritten + count);
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
        if (buffer.IsEmpty)
        {
            return;
        }

        _guard.ValidateNextChunk(_bytesWritten, buffer.Length);
        await _destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _bytesWritten = checked(_bytesWritten + buffer.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("The ZIP output buffer range is invalid.");
        }
    }
}
