using RainArchives.Contracts;

namespace RainArchives.Client;

public sealed class ArchiveEntryStreamLease : IDisposable, IAsyncDisposable
{
    private readonly Action<ArchiveEntryStreamLease> _releaseRegistration;
    private readonly Func<EntryStreamId, Task> _requestRemoteCancellation;
    private readonly FramedEntryReadStream _content;
    private readonly object _cancellationGate = new();
    private Task? _cancellationTask;
    private int _registrationReleased;

    internal ArchiveEntryStreamLease(
        ArchiveSessionId sessionId,
        EntryStreamId streamId,
        EntryId entryId,
        long? declaredLength,
        long maximumBytes,
        Stream pipe,
        Func<Exception, ArchiveClientException> abortTransport,
        Func<EntryStreamId, Task> requestRemoteCancellation,
        Action<ArchiveEntryStreamLease> releaseRegistration)
    {
        SessionId = sessionId;
        StreamId = streamId;
        EntryId = entryId;
        DeclaredLength = declaredLength;
        MaximumBytes = maximumBytes;
        _releaseRegistration = releaseRegistration;
        _requestRemoteCancellation = requestRemoteCancellation;
        _content = new FramedEntryReadStream(
            pipe,
            entryId,
            maximumBytes,
            abortTransport,
            OnContentClosed);
    }

    public ArchiveSessionId SessionId { get; }
    public EntryStreamId StreamId { get; }
    public EntryId EntryId { get; }
    public long? DeclaredLength { get; }
    public long MaximumBytes { get; }
    public Stream Content => _content;

    public void Dispose()
    {
        _content.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _content.DisposeAsync().ConfigureAwait(false);
        Task? cancellationTask;
        lock (_cancellationGate)
        {
            cancellationTask = _cancellationTask;
        }

        if (cancellationTask is not null)
        {
            await cancellationTask.ConfigureAwait(false);
        }

        ReleaseRegistration();
    }

    internal void Fail(ArchiveClientException failure)
    {
        _content.Fail(failure);
    }

    private void OnContentClosed(bool requestRemoteCancellation)
    {
        if (!requestRemoteCancellation)
        {
            ReleaseRegistration();
            return;
        }

        Task cancellationTask;
        lock (_cancellationGate)
        {
            _cancellationTask ??= _requestRemoteCancellation(StreamId);
            cancellationTask = _cancellationTask;
        }

        _ = ReleaseAfterCancellationAsync(cancellationTask);
    }

    private async Task ReleaseAfterCancellationAsync(Task cancellationTask)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch
        {
            // The client transport owns cancellation failure handling.
        }
        finally
        {
            ReleaseRegistration();
        }
    }

    private void ReleaseRegistration()
    {
        if (Interlocked.Exchange(ref _registrationReleased, 1) == 0)
        {
            _releaseRegistration(this);
        }
    }
}
