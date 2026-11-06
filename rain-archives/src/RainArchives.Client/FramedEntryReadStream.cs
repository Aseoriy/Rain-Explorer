using System.Text.Json;
using RainArchives.Contracts;

namespace RainArchives.Client;

internal sealed class FramedEntryReadStream : Stream
{
    private readonly Stream _pipe;
    private readonly EntryId _entryId;
    private readonly long _maximumBytes;
    private readonly Func<Exception, ArchiveClientException> _abortTransport;
    private readonly Action<bool> _onClosed;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private byte[]? _currentData;
    private int _currentOffset;
    private long _receivedBytes;
    private Exception? _failure;
    private int _pipeClosed;
    private int _disposed;
    private bool _complete;

    public FramedEntryReadStream(
        Stream pipe,
        EntryId entryId,
        long maximumBytes,
        Func<Exception, ArchiveClientException> abortTransport,
        Action<bool> onClosed)
    {
        _pipe = pipe;
        _entryId = entryId;
        _maximumBytes = maximumBytes;
        _abortTransport = abortTransport;
        _onClosed = onClosed;
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        ThrowIfUnavailable();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        try
        {
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfUnavailable();
                while (_currentData is null || _currentOffset >= _currentData.Length)
                {
                    _currentData = null;
                    _currentOffset = 0;
                    if (_complete)
                    {
                        return 0;
                    }

                    await ReadNextFrameAsync(cancellationToken).ConfigureAwait(false);
                }

                var count = Math.Min(buffer.Length, _currentData.Length - _currentOffset);
                _currentData.AsMemory(_currentOffset, count).CopyTo(buffer);
                _currentOffset += count;
                return count;
            }
            finally
            {
                _readGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _disposed, 1);
            ClosePipe(requestRemoteCancellation: true);
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or IOException)
        {
            if (Volatile.Read(ref _failure) is { } recordedFailure)
            {
                throw recordedFailure;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(ArchiveEntryStreamLease));
            }

            var protocolFailure = _abortTransport(exception);
            Fail(protocolFailure);
            throw protocolFailure;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
            ClosePipe(requestRemoteCancellation: true);
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        ClosePipe(requestRemoteCancellation: true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    internal void Fail(ArchiveClientException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Interlocked.CompareExchange(ref _failure, failure, null);
        _complete = true;
        ClosePipe(requestRemoteCancellation: false);
    }

    private async ValueTask ReadNextFrameAsync(CancellationToken cancellationToken)
    {
        var frame = await EntryStreamPipeProtocol.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
        switch (frame.Type)
        {
            case EntryStreamFrameType.Data:
                var nextTotal = checked(_receivedBytes + frame.Payload.Length);
                if (nextTotal > _maximumBytes)
                {
                    throw new InvalidDataException("The worker exceeded the negotiated entry stream byte limit.");
                }

                _receivedBytes = nextTotal;
                _currentData = frame.Payload;
                break;
            case EntryStreamFrameType.Complete:
                var completedTotal = EntryStreamPipeProtocol.ReadCompletionTotal(frame);
                if (completedTotal != _receivedBytes)
                {
                    throw new InvalidDataException("The worker entry stream completion total was inconsistent.");
                }

                _complete = true;
                ClosePipe(requestRemoteCancellation: false);
                break;
            case EntryStreamFrameType.Error:
                var error = EntryStreamPipeProtocol.ReadError(frame);
                ValidateError(error);
                var entryStreamFailure = new ArchiveClientException(error);
                Interlocked.CompareExchange(ref _failure, entryStreamFailure, null);
                _complete = true;
                ClosePipe(requestRemoteCancellation: false);
                throw entryStreamFailure;
            case EntryStreamFrameType.Hello:
            default:
                throw new InvalidDataException("The worker sent an unexpected entry stream frame.");
        }
    }

    private void ValidateError(ArchiveError error)
    {
        if (!Enum.IsDefined(error.Category)
            || string.IsNullOrWhiteSpace(error.Code)
            || string.IsNullOrWhiteSpace(error.Message)
            || error.EntryId?.Value == Guid.Empty
            || (error.EntryId is { } errorEntryId && errorEntryId != _entryId)
            || error.DiagnosticId?.Length > 128)
        {
            throw new InvalidDataException("The worker returned an invalid entry stream error.");
        }
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw failure;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void ClosePipe(bool requestRemoteCancellation)
    {
        if (Interlocked.Exchange(ref _pipeClosed, 1) != 0)
        {
            return;
        }

        try
        {
            _pipe.Dispose();
        }
        finally
        {
            _onClosed(requestRemoteCancellation);
        }
    }
}
