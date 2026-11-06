using System.IO;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainExplorer.Services;

public sealed class ArchiveClientInvalidatedEventArgs(IArchiveClient client) : EventArgs
{
    public IArchiveClient Client { get; } = client;
}

/// <summary>One lazy application-owned Rain Archives client. Tabs own only Worker sessions.</summary>
public sealed class ArchiveClientRuntime : IAsyncDisposable
{
    private static readonly Lazy<ArchiveClientRuntime> SharedRuntime = new(() =>
        new ArchiveClientRuntime(async cancellationToken => await ArchiveClient.StartAsync(
            Path.Combine(AppContext.BaseDirectory, "worker", "RainArchives.Worker.exe"),
            cancellationToken).ConfigureAwait(false)));

    private readonly Func<CancellationToken, Task<IArchiveClient>> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _nestedReservationSync = new();
    private readonly Dictionary<Guid, long> _nestedReservations = [];
    private IArchiveClient? _client;
    private bool _disposed;

    public ArchiveClientRuntime(Func<CancellationToken, Task<IArchiveClient>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public static ArchiveClientRuntime Shared => SharedRuntime.Value;

    public event EventHandler<ArchiveClientInvalidatedEventArgs>? ClientInvalidated;

    public async Task<IArchiveClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _client ??= await _factory(cancellationToken).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mirrors the Worker's connection-wide nested-byte admission ceiling so a tab can
    /// refuse before creating or transferring a one-shot password slot.
    /// </summary>
    internal bool TryReserveNestedArchiveBytes(
        long maximumBytes,
        out NestedArchiveByteReservation reservation)
    {
        reservation = null!;
        if (maximumBytes is <= 0 or > ProtocolConstants.MaximumNestedArchiveBytes) return false;

        lock (_nestedReservationSync)
        {
            long active = 0;
            foreach (long bytes in _nestedReservations.Values)
                active = checked(active + bytes);
            if (active > ProtocolConstants.MaximumActiveNestedArchiveBytes - maximumBytes)
                return false;

            Guid id;
            do { id = Guid.NewGuid(); } while (!_nestedReservations.TryAdd(id, maximumBytes));
            reservation = new NestedArchiveByteReservation(this, id, maximumBytes);
            return true;
        }
    }

    private void CommitNestedArchiveBytes(Guid id, long reservedBytes, long actualBytes)
    {
        if (actualBytes is <= 0 || actualBytes > reservedBytes)
            throw new InvalidDataException("The nested archive byte reservation was invalid.");
        lock (_nestedReservationSync)
        {
            if (!_nestedReservations.TryGetValue(id, out long current)
                || current != reservedBytes)
                throw new InvalidDataException("The nested archive byte reservation is no longer current.");
            _nestedReservations[id] = actualBytes;
        }
    }

    private void ReleaseNestedArchiveBytes(Guid id)
    {
        lock (_nestedReservationSync) _nestedReservations.Remove(id);
    }

    private void ClearNestedArchiveReservations()
    {
        lock (_nestedReservationSync) _nestedReservations.Clear();
    }

    /// <summary>Close one exact session with a fresh bounded cleanup token after UI cancellation.</summary>
    public async Task<bool> CloseSessionAsync(IArchiveClient client, ArchiveSessionId sessionId)
    {
        bool acknowledged = false;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await client.CloseArchiveAsync(sessionId, cleanup.Token).ConfigureAwait(false);
            acknowledged = response.Closed;
        }
        catch (Exception exception) when (exception is ArchiveClientException
                                              or OperationCanceledException
                                              or IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
            acknowledged = false;
        }

        if (!acknowledged) await InvalidateAsync(client).ConfigureAwait(false);
        return acknowledged;
    }

    public async Task InvalidateAsync(IArchiveClient expectedClient)
    {
        IArchiveClient? toDispose = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_client, expectedClient))
            {
                toDispose = _client;
                _client = null;
                ClearNestedArchiveReservations();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (toDispose is null) return;
        try { await toDispose.DisposeAsync().ConfigureAwait(false); }
        finally { ClientInvalidated?.Invoke(this, new ArchiveClientInvalidatedEventArgs(toDispose)); }
    }

    public async ValueTask DisposeAsync()
    {
        IArchiveClient? toDispose;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = _client;
            _client = null;
            ClearNestedArchiveReservations();
        }
        finally
        {
            _gate.Release();
        }

        if (toDispose is not null) await toDispose.DisposeAsync().ConfigureAwait(false);
    }

    internal sealed class NestedArchiveByteReservation : IDisposable
    {
        private ArchiveClientRuntime? _owner;
        private readonly Guid _id;
        private long _reservedBytes;

        internal NestedArchiveByteReservation(
            ArchiveClientRuntime owner,
            Guid id,
            long reservedBytes)
        {
            _owner = owner;
            _id = id;
            _reservedBytes = reservedBytes;
        }

        public void Commit(long actualBytes)
        {
            ArchiveClientRuntime owner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(NestedArchiveByteReservation));
            owner.CommitNestedArchiveBytes(_id, _reservedBytes, actualBytes);
            _reservedBytes = actualBytes;
        }

        public void Dispose()
        {
            ArchiveClientRuntime? owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseNestedArchiveBytes(_id);
        }
    }
}
