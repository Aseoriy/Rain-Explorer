using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.Worker;

internal sealed class WorkerArchiveSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private TaskCompletionSource<bool> _reservationsDrained = CreateCompletedSource();
    private int _reservations;
    private bool _closing;
    private Task? _disposeTask;

    public WorkerArchiveSession(
        string sourcePath,
        IAsyncDisposable sourceOwner,
        IArchiveProvider provider,
        IArchiveHandle handle,
        ArchiveIndex index,
        ArchiveDescriptor descriptor,
        int nestedDepth = 0,
        long nestedChainBytes = 0,
        ArchiveSessionId? parentSessionId = null,
        EntryId? sourceEntryId = null,
        DownloadProvenance? inheritedProvenance = null,
        IDisposable? nestedByteReservation = null)
    {
        SourcePath = sourcePath;
        SourceOwner = sourceOwner;
        Provider = provider;
        Handle = handle;
        Index = index;
        Descriptor = descriptor;
        NestedDepth = nestedDepth;
        NestedChainBytes = nestedChainBytes;
        ParentSessionId = parentSessionId;
        SourceEntryId = sourceEntryId;
        InheritedProvenance = inheritedProvenance;
        NestedByteReservation = nestedByteReservation;
    }

    public string SourcePath { get; }
    private IAsyncDisposable SourceOwner { get; }
    private IDisposable? NestedByteReservation { get; }
    public IArchiveProvider Provider { get; }
    public IArchiveHandle Handle { get; private set; }
    public ArchiveIndex Index { get; }
    public ArchiveDescriptor Descriptor { get; }
    public int NestedDepth { get; }
    public long NestedChainBytes { get; }
    public ArchiveSessionId? ParentSessionId { get; }
    public EntryId? SourceEntryId { get; }
    public DownloadProvenance? InheritedProvenance { get; }

    public IDisposable ReserveUse()
    {
        lock (_lifecycleGate)
        {
            if (_closing)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    "session.closing",
                    "The archive session is closing and cannot accept another request.");
            }

            if (_reservations == 0)
            {
                _reservationsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _reservations++;
            return new Reservation(this);
        }
    }

    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    public async ValueTask ReopenWithPasswordAsync(
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken)
    {
        if (password.IsEmpty)
        {
            return;
        }

        await EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
        var replacement = await Provider.OpenAsync(SourcePath, password, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(replacement.Format, Handle.Format, StringComparison.Ordinal)
                || !string.Equals(replacement.ProviderId, Handle.ProviderId, StringComparison.Ordinal)
                || replacement.Capabilities != Handle.Capabilities
                || replacement.IsSolid != Handle.IsSolid
                || replacement.VolumeSetKind != Handle.VolumeSetKind)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProviderFailure,
                    "provider.password_reopen_contract_changed",
                    "The archive provider changed its identity or capabilities during password reopen.");
            }

            if (replacement.Entries.Count != Handle.Entries.Count)
            {
                throw SourceChangedDuringPasswordReopen();
            }

            for (var index = 0; index < replacement.Entries.Count; index++)
            {
                if (replacement.Entries[index].Ordinal != Handle.Entries[index].Ordinal
                    || replacement.Entries[index].Kind != Handle.Entries[index].Kind
                    || !string.Equals(
                        replacement.Entries[index].RawPath,
                        Handle.Entries[index].RawPath,
                        StringComparison.Ordinal))
                {
                    throw SourceChangedDuringPasswordReopen();
                }
            }
        }
        catch
        {
            await replacement.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var previous = Handle;
        Handle = replacement;
        await previous.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask EnsureSourceUnchangedAsync(CancellationToken cancellationToken) =>
        ArchiveFingerprintService.EnsureUnchangedAsync(
            SourcePath,
            Descriptor.Fingerprint,
            cancellationToken);

    public ValueTask<DownloadProvenance?> GetDownloadProvenanceAsync(
        CancellationToken cancellationToken) =>
        InheritedProvenance is { } provenance
            ? ValueTask.FromResult<DownloadProvenance?>(provenance)
            : DownloadProvenanceService.TryReadAsync(SourcePath, cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                _closing = true;
                _disposeTask = DisposeCoreAsync(_reservationsDrained.Task);
            }

            return new ValueTask(_disposeTask!);
        }
    }

    private async Task DisposeCoreAsync(Task reservationsDrained)
    {
        await reservationsDrained.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Handle.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await SourceOwner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    NestedByteReservation?.Dispose();
                }
                finally
                {
                    _gate.Release();
                    _gate.Dispose();
                }
            }
        }
    }

    private void ReleaseReservation()
    {
        lock (_lifecycleGate)
        {
            if (_reservations <= 0)
            {
                throw new InvalidOperationException("Archive session reservation accounting underflowed.");
            }

            _reservations--;
            if (_reservations == 0)
            {
                _reservationsDrained.TrySetResult(true);
            }
        }
    }

    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Reservation : IDisposable
    {
        private WorkerArchiveSession? _session;

        public Reservation(WorkerArchiveSession session)
        {
            _session = session;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _session, null)?.ReleaseReservation();
        }
    }

    private static ArchiveProviderException SourceChangedDuringPasswordReopen() =>
        new(
            ArchiveErrorCategory.SourceChanged,
            "archive.changed_during_password_reopen",
            "The archive changed while it was being reopened with a password.");
}
