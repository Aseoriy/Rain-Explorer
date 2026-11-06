using RainArchives.App.Infrastructure;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class ActivityOperationViewModel : ObservableObject
{
    private readonly Func<ActivityOperationViewModel, Task> _cancel;
    private string _operationName = string.Empty;
    private OperationKind _kind;
    private OperationPhase _phase;
    private long _lastEventSequence;
    private DateTimeOffset _createdAtUtc;
    private DateTimeOffset _updatedAtUtc;
    private DateTimeOffset? _completedAtUtc;
    private OperationActivityProgress? _progress;
    private string _currentItem = "Not reported";
    private int _warningCount;
    private ArchiveErrorCategory? _errorCategory;
    private bool _snapshotCanCancel;
    private bool _isCancellationPending;

    internal ActivityOperationViewModel(
        OperationId operationId,
        Func<ActivityOperationViewModel, Task> cancel)
    {
        OperationId = operationId;
        _cancel = cancel;
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => CanCancel);
    }

    public OperationId OperationId { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public string OperationName => _operationName;
    public OperationKind Kind => _kind;
    public string KindText => Kind switch
    {
        OperationKind.Extract => "Extraction",
        OperationKind.IntegrityTest => "Integrity test",
        OperationKind.CreateArchive => "Archive creation",
        OperationKind.RebuildArchive => "Archive rebuild",
        _ => "Archive operation",
    };
    public OperationPhase Phase => _phase;
    public string PhaseText => Phase switch
    {
        OperationPhase.CompletedWithWarnings => "Completed with warnings",
        OperationPhase.AwaitingPassword => "Waiting for password — no continuation control",
        OperationPhase.AwaitingConflict => "Waiting for conflict decision — no continuation control",
        OperationPhase.AwaitingRiskApproval => "Waiting for risk approval — no continuation control",
        _ => Phase.ToString(),
    };
    public long LastEventSequence => _lastEventSequence;
    public DateTimeOffset CreatedAtUtc => _createdAtUtc;
    public DateTimeOffset UpdatedAtUtc => _updatedAtUtc;
    public DateTimeOffset? CompletedAtUtc => _completedAtUtc;
    public string UpdatedAtText => UpdatedAtUtc == default
        ? "Not reported"
        : UpdatedAtUtc.ToLocalTime().ToString("g");
    public string CurrentItem => _currentItem;
    public int WarningCount => _warningCount;
    public string WarningText => WarningCount == 0
        ? "None"
        : $"{WarningCount:N0} reported";
    public string OutcomeText => Phase switch
    {
        OperationPhase.Completed => "Completed successfully",
        OperationPhase.CompletedWithWarnings => $"Completed with {WarningCount:N0} warning{(WarningCount == 1 ? string.Empty : "s")}",
        OperationPhase.Cancelled => "Cancelled; no retry was started",
        OperationPhase.Failed when _errorCategory is { } category => $"Failed ({FormatErrorCategory(category)})",
        OperationPhase.Failed => "Failed; detailed error text is not retained in activity history",
        _ when IsCancellationPending => "Cancellation requested; waiting for the authoritative terminal state",
        _ => "In progress",
    };
    public bool IsTerminal => Phase is OperationPhase.Completed
        or OperationPhase.CompletedWithWarnings
        or OperationPhase.Cancelled
        or OperationPhase.Failed;
    public bool HasProgress => _progress is not null;
    public bool IsIndeterminate => !IsTerminal && (_progress?.TotalBytes is not > 0);
    public double ProgressPercent => _progress?.TotalBytes is { } total and > 0
        ? Math.Clamp(_progress.BytesProcessed * 100d / total, 0, 100)
        : IsTerminal && Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings
            ? 100
            : 0;
    public string ProgressText => _progress?.TotalBytes is { } total and > 0
        ? $"{ProgressPercent:0.#}%"
        : HasProgress
            ? "In progress"
            : IsTerminal
                ? PhaseText
                : "Waiting";
    public string BytesText => _progress is null
        ? "Not reported"
        : ActivityPresentationFormatter.FormatBytesProgress(
            _progress.BytesProcessed,
            _progress.TotalBytes);
    public string EntriesText => _progress is null
        ? "Not reported"
        : ActivityPresentationFormatter.FormatEntriesProgress(
            _progress.EntriesProcessed,
            _progress.TotalEntries);
    public string ElapsedText => _progress is null
        ? "Not reported"
        : ActivityPresentationFormatter.FormatElapsed(_progress.Elapsed);
    public string ThroughputText => ActivityPresentationFormatter.FormatThroughput(
        _progress?.BytesPerSecond);
    public string RatioText => ActivityPresentationFormatter.FormatRatio(
        _progress?.CompressionRatio);
    public bool CanCancel => _snapshotCanCancel && !IsTerminal && !IsCancellationPending;
    public bool IsCancellationPending => _isCancellationPending;
    public string AutomationName =>
        $"{OperationName}, {PhaseText}, {ProgressText}, {OutcomeText}";

    internal void Apply(ActivityPresentationState state)
    {
        _operationName = state.OperationName;
        _kind = state.Kind;
        _phase = state.Phase;
        _lastEventSequence = state.LastEventSequence;
        _createdAtUtc = state.CreatedAtUtc;
        _updatedAtUtc = state.UpdatedAtUtc;
        _completedAtUtc = state.CompletedAtUtc;
        _progress = state.Progress;
        _currentItem = state.CurrentItem;
        _warningCount = state.WarningCount;
        _errorCategory = state.ErrorCategory;
        _snapshotCanCancel = state.SnapshotCanCancel;
        if (IsTerminal || !_snapshotCanCancel)
        {
            _isCancellationPending = false;
        }

        RaiseAllPropertiesChanged();
    }

    internal void MarkCancellationPending()
    {
        if (_isCancellationPending)
        {
            return;
        }

        _isCancellationPending = true;
        RaiseAllPropertiesChanged();
    }

    internal void ClearCancellationPending()
    {
        if (!_isCancellationPending)
        {
            return;
        }

        _isCancellationPending = false;
        RaiseAllPropertiesChanged();
    }

    private async Task CancelAsync()
    {
        if (!CanCancel)
        {
            return;
        }

        MarkCancellationPending();
        await _cancel(this).ConfigureAwait(true);
    }

    private void RaiseAllPropertiesChanged()
    {
        OnPropertyChanged(nameof(OperationName));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(LastEventSequence));
        OnPropertyChanged(nameof(CreatedAtUtc));
        OnPropertyChanged(nameof(UpdatedAtUtc));
        OnPropertyChanged(nameof(CompletedAtUtc));
        OnPropertyChanged(nameof(UpdatedAtText));
        OnPropertyChanged(nameof(CurrentItem));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(OutcomeText));
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(BytesText));
        OnPropertyChanged(nameof(EntriesText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(ThroughputText));
        OnPropertyChanged(nameof(RatioText));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsCancellationPending));
        OnPropertyChanged(nameof(AutomationName));
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static string FormatErrorCategory(ArchiveErrorCategory category) => category switch
    {
        ArchiveErrorCategory.InvalidRequest => "invalid request",
        ArchiveErrorCategory.UnsupportedFormat => "unsupported format",
        ArchiveErrorCategory.UnsupportedCapability => "unsupported capability",
        ArchiveErrorCategory.InvalidArchive => "invalid archive",
        ArchiveErrorCategory.CorruptArchive => "corrupt archive",
        ArchiveErrorCategory.PasswordRequired => "password required",
        ArchiveErrorCategory.IncorrectPassword => "incorrect password",
        ArchiveErrorCategory.UnsafePath => "unsafe path",
        ArchiveErrorCategory.PathCollision => "path collision",
        ArchiveErrorCategory.ResourceLimit => "resource limit",
        ArchiveErrorCategory.SourceChanged => "source changed",
        ArchiveErrorCategory.DestinationConflict => "destination conflict",
        ArchiveErrorCategory.PermissionDenied => "permission denied",
        ArchiveErrorCategory.InsufficientSpace => "insufficient space",
        ArchiveErrorCategory.Cancelled => "cancelled",
        ArchiveErrorCategory.ProviderFailure => "provider failure",
        ArchiveErrorCategory.WorkerFailure => "worker failure",
        ArchiveErrorCategory.ProtocolMismatch => "protocol mismatch",
        _ => "unknown error",
    };
}

internal sealed record ActivityPresentationState(
    OperationId OperationId,
    string OperationName,
    OperationKind Kind,
    OperationPhase Phase,
    long LastEventSequence,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    OperationActivityProgress? Progress,
    string CurrentItem,
    int WarningCount,
    ArchiveErrorCategory? ErrorCategory,
    bool SnapshotCanCancel,
    int SnapshotOrder,
    bool SeenInSnapshot);
