using RainArchives.Core;

namespace RainArchives.Security;

/// <summary>
/// Keeps provider-owned copy and checksum logic behind the provider boundary while
/// enforcing Rain Archives' decoded-byte limits before bytes reach the destination.
/// </summary>
public sealed class RuntimeGuardWriteStream : Stream
{
    private readonly Stream _destination;
    private readonly ExtractionRuntimeGuard _guard;
    private readonly long _aggregateStart;
    private readonly long? _declaredEntrySize;
    private readonly Func<long, long, ValueTask>? _afterWrite;
    private readonly bool _leaveOpen;
    private long _entryBytesWritten;

    public RuntimeGuardWriteStream(
        Stream destination,
        ExtractionRuntimeGuard guard,
        long aggregateStart,
        long? declaredEntrySize,
        Func<long, long, ValueTask>? afterWrite = null,
        bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(guard);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        if (aggregateStart < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregateStart));
        }

        _destination = destination;
        _guard = guard;
        _aggregateStart = aggregateStart;
        _declaredEntrySize = declaredEntrySize;
        _afterWrite = afterWrite;
        _leaveOpen = leaveOpen;
    }

    public long EntryBytesWritten => _entryBytesWritten;
    public long TotalBytesWritten => checked(_aggregateStart + _entryBytesWritten);

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
        Validate(buffer, offset, count);
        if (count == 0)
        {
            return;
        }

        _guard.ValidateNextChunk(TotalBytesWritten, _entryBytesWritten, count, _declaredEntrySize);
        _destination.Write(buffer, offset, count);
        Advance(count).AsTask().GetAwaiter().GetResult();
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        Validate(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        _guard.ValidateNextChunk(
            TotalBytesWritten,
            _entryBytesWritten,
            buffer.Length,
            _declaredEntrySize);
        await _destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await Advance(buffer.Length).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _destination.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _destination.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private async ValueTask Advance(int count)
    {
        _entryBytesWritten = checked(_entryBytesWritten + count);
        if (_afterWrite is not null)
        {
            await _afterWrite(TotalBytesWritten, _entryBytesWritten).ConfigureAwait(false);
        }
    }

    private static void Validate(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("The buffer range is invalid.", nameof(count));
        }
    }
}
