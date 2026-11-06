using System.Diagnostics;
using RainArchives.Contracts;

namespace RainArchives.Worker;

internal sealed class WorkerOperation : IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly object _lifecycleGate = new();
    private long _sequence;
    private int _warningCount;
    private ArchiveWarning? _lastWarning;
    private OperationPhase _phase = OperationPhase.Queued;
    private long _lastEventSequence;
    private OperationActivityProgress? _lastProgress;
    private DateTimeOffset _updatedAtUtc;
    private DateTimeOffset? _completedAtUtc;
    private TimeSpan _lastProgressAt;
    private int _lastProgressEntryCount = -1;
    private bool _disposed;

    public WorkerOperation(OperationKind kind, CancellationToken shutdownToken)
    {
        Id = OperationId.New();
        Kind = kind;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        Stopwatch = Stopwatch.StartNew();
        CreatedAtUtc = DateTimeOffset.UtcNow;
        _updatedAtUtc = CreatedAtUtc;
    }

    public OperationId Id { get; }
    public OperationKind Kind { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public CancellationTokenSource Cancellation { get; }
    public Stopwatch Stopwatch { get; }
    public int WarningCount
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _warningCount;
            }
        }
    }

    public ArchiveWarning? LastWarning
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _lastWarning;
            }
        }
    }

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public bool TryCancel()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _phase is OperationPhase.Completed
                or OperationPhase.CompletedWithWarnings
                or OperationPhase.Cancelled
                or OperationPhase.Failed)
            {
                return false;
            }

            Cancellation.Cancel();
            return true;
        }
    }

    public bool ShouldReportProgress(OperationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return false;
            }

            var now = Stopwatch.Elapsed;
            if (progress.EntriesProcessed == _lastProgressEntryCount
                && now - _lastProgressAt < ProgressInterval)
            {
                return false;
            }

            _lastProgressAt = now;
            _lastProgressEntryCount = progress.EntriesProcessed;
            return true;
        }
    }

    public void RecordSentEvent(OperationEvent operationEvent, DateTimeOffset sentAtUtc)
    {
        ArgumentNullException.ThrowIfNull(operationEvent);
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WorkerOperation));
            }

            var recordedAtUtc = sentAtUtc.ToUniversalTime();
            if (recordedAtUtc < _updatedAtUtc)
            {
                recordedAtUtc = _updatedAtUtc;
            }

            _phase = operationEvent.Phase;
            _lastEventSequence = operationEvent.Sequence;
            _updatedAtUtc = recordedAtUtc;
            if (operationEvent.Kind == OperationEventKind.Progress
                && operationEvent.Progress is { } progress)
            {
                _lastProgress = new OperationActivityProgress(
                    progress.BytesProcessed,
                    progress.TotalBytes,
                    progress.EntriesProcessed,
                    progress.TotalEntries,
                    progress.Elapsed,
                    progress.BytesPerSecond,
                    progress.CompressionRatio);
            }

            if (operationEvent.Kind == OperationEventKind.Warning
                && operationEvent.Warning is { } warning)
            {
                _lastWarning = warning;
                _warningCount = checked(_warningCount + 1);
            }

            if (operationEvent.Kind == OperationEventKind.Terminal)
            {
                _completedAtUtc = recordedAtUtc;
            }
        }
    }

    public OperationActivitySnapshot CreateActivitySnapshot()
    {
        lock (_lifecycleGate)
        {
            var terminal = _phase is OperationPhase.Completed
                or OperationPhase.CompletedWithWarnings
                or OperationPhase.Cancelled
                or OperationPhase.Failed;
            return new OperationActivitySnapshot(
                Id,
                Kind,
                _phase,
                _lastEventSequence,
                CreatedAtUtc,
                _updatedAtUtc,
                _completedAtUtc,
                _lastProgress,
                _warningCount,
                CanCancel: !terminal);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stopwatch.Stop();
            Cancellation.Dispose();
        }
    }
}
