using System.Collections.ObjectModel;
using System.IO;
using RainArchives.App.Infrastructure;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class ExtractionRecoveryCenterViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IArchiveClient _client;
    private readonly object _operationGate = new();
    private CancellationTokenSource? _lifetime = new();
    private Task _activeOperation = Task.CompletedTask;
    private ExtractionRecoveryItemViewModel? _selectedRecovery;
    private InspectExtractionRecoveryResponse? _inspection;
    private string _statusText = "Loading extraction-staging recovery records…";
    private string _inventorySummaryText = string.Empty;
    private bool _isBusy;

    public ExtractionRecoveryCenterViewModel(IArchiveClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        InspectCommand = new AsyncRelayCommand(InspectSelectedAsync, () => CanInspect);
    }

    public ObservableCollection<ExtractionRecoveryItemViewModel> Recoveries { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand InspectCommand { get; }

    public bool SupportsInspection =>
        _client.SupportsFeature(ProtocolFeatures.ExtractionRecoveryInspection);

    public ExtractionRecoveryItemViewModel? SelectedRecovery
    {
        get => _selectedRecovery;
        set
        {
            if (!SetProperty(ref _selectedRecovery, value))
            {
                return;
            }

            ClearInspection();
            if (value is not null)
            {
                StatusText = "Select Inspect to collect fresh read-only evidence. No action is available.";
            }

            NotifyCommandStates();
        }
    }

    public bool HasRecoveries => Recoveries.Count > 0;

    public bool HasNoRecoveries => !HasRecoveries;

    public bool HasInspection => _inspection is not null;

    public bool HasNoInspection => !HasInspection;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public bool CanRefresh => !IsBusy;

    public bool CanInspect => !IsBusy && SupportsInspection && SelectedRecovery is not null;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string InventorySummaryText
    {
        get => _inventorySummaryText;
        private set => SetProperty(ref _inventorySummaryText, value);
    }

    public string DestinationPathText => _inspection is null
        ? string.Empty
        : UntrustedDisplayText.Escape(_inspection.DestinationPath);

    public string DestinationKindText => _inspection switch
    {
        null => string.Empty,
        { IsRemoteDestination: true } => "Network destination",
        _ => "Local destination",
    };

    public string RecoveryRevisionText => _inspection is null
        ? string.Empty
        : $"{FormatPhase(_inspection.Recovery.Phase)}, sequence {_inspection.Recovery.Sequence:N0}, "
          + $"recorded {_inspection.Recovery.RecordedAtUtc.LocalDateTime:G}";

    public string DestinationParentText => _inspection is null
        ? string.Empty
        : FormatParentState(_inspection.DestinationParent);

    public string StagingArtifactText => _inspection is null
        ? string.Empty
        : FormatArtifact(_inspection.StagingArtifact);

    public string FinalArtifactText => _inspection is null
        ? string.Empty
        : FormatArtifact(_inspection.FinalArtifact);

    public string JournalEvidenceText => _inspection is null
        ? string.Empty
        : FormatJournalEvidence(_inspection);

    public string AssessmentText => _inspection is null
        ? string.Empty
        : FormatAssessment(_inspection.Assessment);

    public string InspectedAtText => _inspection is null
        ? string.Empty
        : $"Inspected {_inspection.InspectedAtUtc.LocalDateTime:G}";

    public string ActionGuidance => _inspection is null
        ? "Listing is local, path-free, and non-destructive. Inspect explicitly to contact and reveal one destination."
        : "Extraction recovery is read-only. Cleanup, restore, retry, replacement, and disposition are unavailable.";

    public Task InitializeAsync() => RefreshAsync();

    public Task RefreshAsync() => RunTrackedAsync(RefreshCoreAsync, ExtractionRecoveryUiOperation.Refresh);

    public Task InspectSelectedAsync()
    {
        var recovery = SelectedRecovery;
        return recovery is null
            ? Task.CompletedTask
            : RunTrackedAsync(
                cancellationToken => InspectCoreAsync(recovery, cancellationToken),
                ExtractionRecoveryUiOperation.Inspect);
    }

    public void RequestCancellation() => _lifetime?.Cancel();

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        Task activeOperation;
        lock (_operationGate)
        {
            activeOperation = _activeOperation;
        }

        try
        {
            await activeOperation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The tracked operation translates cancellation into a fixed UI state.
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private Task RunTrackedAsync(
        Func<CancellationToken, Task> action,
        ExtractionRecoveryUiOperation operation)
    {
        lock (_operationGate)
        {
            if (_lifetime is null || !_activeOperation.IsCompleted)
            {
                return Task.CompletedTask;
            }

            _activeOperation = RunTrackedCoreAsync(action, operation, _lifetime.Token);
            return _activeOperation;
        }
    }

    private async Task RunTrackedCoreAsync(
        Func<CancellationToken, Task> action,
        ExtractionRecoveryUiOperation operation,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await action(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearInspection();
            StatusText = "Extraction recovery review cancelled. No action was taken.";
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or IOException or InvalidDataException)
        {
            HandleFailure(operation);
        }
        catch (Exception)
        {
            HandleFailure(operation);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        ClearInspection();
        if (!SupportsInspection)
        {
            ReplaceInventory(new ListExtractionRecoveriesResponse(
                [],
                ActiveOperationCount: 0,
                SuspiciousOperationCount: 0,
                IncompleteJournalCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
            InventorySummaryText = "Extraction recovery inspection is unavailable.";
            StatusText = "The connected archive worker does not support read-only extraction recovery inspection.";
            return;
        }

        var inventory = await _client.ListExtractionRecoveriesAsync(cancellationToken)
            .ConfigureAwait(true);
        ReplaceInventory(inventory);
        StatusText = BuildInventoryStatus(inventory);
    }

    private async Task InspectCoreAsync(
        ExtractionRecoveryItemViewModel selected,
        CancellationToken cancellationToken)
    {
        ClearInspection();
        var inspection = await _client
            .InspectExtractionRecoveryAsync(selected.RecoveryId, cancellationToken)
            .ConfigureAwait(true);

        if (!ReferenceEquals(SelectedRecovery, selected))
        {
            StatusText = "The selection changed. Inspect the newly selected record for fresh evidence.";
            return;
        }

        if (inspection.SupportsMutation || inspection.SupportsRetry || inspection.SupportsCleanup)
        {
            throw new InvalidDataException(
                "The Worker advertised unsupported extraction recovery authority.");
        }

        _inspection = inspection;
        NotifyInspectionChanged();
        StatusText = "Read-only inspection complete. No recovery action is available.";
    }

    private void ReplaceInventory(ListExtractionRecoveriesResponse inventory)
    {
        SelectedRecovery = null;
        Recoveries.Clear();
        foreach (var recovery in inventory.Recoveries)
        {
            Recoveries.Add(new ExtractionRecoveryItemViewModel(recovery));
        }

        OnPropertyChanged(nameof(HasRecoveries));
        OnPropertyChanged(nameof(HasNoRecoveries));
        InventorySummaryText =
            $"{inventory.Recoveries.Count:N0} reviewable · "
            + $"{inventory.ActiveOperationCount:N0} active · "
            + $"{inventory.SuspiciousOperationCount:N0} suspicious · "
            + $"{inventory.IncompleteJournalCount:N0} incomplete";
        NotifyCommandStates();
    }

    private static string BuildInventoryStatus(ListExtractionRecoveriesResponse inventory)
    {
        var status = inventory.Recoveries.Count == 0
            ? "No interrupted extraction staging currently needs read-only review."
            : $"{inventory.Recoveries.Count:N0} interrupted extraction staging record"
              + (inventory.Recoveries.Count == 1 ? " needs" : "s need")
              + " read-only review.";

        if (inventory.ActiveOperationCount > 0)
        {
            status += " Active operations were left untouched.";
        }

        if (inventory.SuspiciousOperationCount > 0)
        {
            status += " Suspicious state was preserved and is not actionable.";
        }

        if (inventory.IsTruncated || inventory.InspectionIncomplete)
        {
            status += " The inventory is incomplete; no automatic action was taken.";
        }

        return status;
    }

    private void HandleFailure(ExtractionRecoveryUiOperation operation)
    {
        ClearInspection();
        StatusText = operation switch
        {
            ExtractionRecoveryUiOperation.Refresh =>
                "The extraction recovery list could not be refreshed. No action was taken.",
            ExtractionRecoveryUiOperation.Inspect =>
                "Read-only inspection failed. No recovery action is available.",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private void ClearInspection()
    {
        if (_inspection is null)
        {
            return;
        }

        _inspection = null;
        NotifyInspectionChanged();
    }

    private void NotifyInspectionChanged()
    {
        OnPropertyChanged(nameof(HasInspection));
        OnPropertyChanged(nameof(HasNoInspection));
        OnPropertyChanged(nameof(DestinationPathText));
        OnPropertyChanged(nameof(DestinationKindText));
        OnPropertyChanged(nameof(RecoveryRevisionText));
        OnPropertyChanged(nameof(DestinationParentText));
        OnPropertyChanged(nameof(StagingArtifactText));
        OnPropertyChanged(nameof(FinalArtifactText));
        OnPropertyChanged(nameof(JournalEvidenceText));
        OnPropertyChanged(nameof(AssessmentText));
        OnPropertyChanged(nameof(InspectedAtText));
        OnPropertyChanged(nameof(ActionGuidance));
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanInspect));
        OnPropertyChanged(nameof(ActionGuidance));
        RefreshCommand.NotifyCanExecuteChanged();
        InspectCommand.NotifyCanExecuteChanged();
    }

    private static string FormatPhase(ExtractionRecoveryPhase phase) => phase switch
    {
        ExtractionRecoveryPhase.Admitted => "Admitted",
        ExtractionRecoveryPhase.Writing => "Writing",
        ExtractionRecoveryPhase.Finalized => "Finalized",
        ExtractionRecoveryPhase.CommitIntent => "Commit intent",
        ExtractionRecoveryPhase.Committed => "Committed",
        _ => "Unknown phase",
    };

    private static string FormatParentState(ExtractionRecoveryParentState state) => state switch
    {
        ExtractionRecoveryParentState.MatchesJournal => "Matches the recovery journal",
        ExtractionRecoveryParentState.Missing => "Destination parent is missing",
        ExtractionRecoveryParentState.IdentityMismatch => "Destination parent identity changed",
        ExtractionRecoveryParentState.ReparsePointOrWrongKind => "Destination parent is a reparse point or wrong kind",
        ExtractionRecoveryParentState.AccessDenied => "Destination parent access was denied",
        ExtractionRecoveryParentState.Unavailable => "Destination parent is unavailable",
        _ => "Destination parent was not safely inspected",
    };

    private static string FormatArtifact(ExtractionRecoveryArtifactInspection artifact)
    {
        var state = artifact.State switch
        {
            ExtractionRecoveryArtifactState.NotBoundByJournal => "not bound by the journal",
            ExtractionRecoveryArtifactState.NotInspected => "not inspected",
            ExtractionRecoveryArtifactState.Absent => "absent",
            ExtractionRecoveryArtifactState.IdentityMatchRevisionUnbound => "owned identity match; revision not bound",
            ExtractionRecoveryArtifactState.ExactMatch => "exact owned match",
            ExtractionRecoveryArtifactState.IdentityMismatch => "identity mismatch",
            ExtractionRecoveryArtifactState.RevisionMismatch => "revision mismatch",
            ExtractionRecoveryArtifactState.WrongKind => "wrong filesystem object kind",
            ExtractionRecoveryArtifactState.ReparsePoint => "reparse point",
            ExtractionRecoveryArtifactState.UnexpectedHardLinks => "unexpected hard links",
            ExtractionRecoveryArtifactState.UnexpectedDataStreams => "unexpected data streams",
            ExtractionRecoveryArtifactState.Excessive => "resource limits exceeded",
            ExtractionRecoveryArtifactState.AccessDenied => "access denied",
            ExtractionRecoveryArtifactState.Unavailable => "unavailable",
            _ => "unknown",
        };

        if (artifact.ObservedFileCount is { } files)
        {
            state += $", {files:N0} files";
        }

        if (artifact.ObservedDirectoryCount is { } directories)
        {
            state += $", {directories:N0} directories";
        }

        if (artifact.ObservedTotalBytes is { } bytes)
        {
            state += $", {bytes:N0} bytes";
        }

        if (artifact.ObservedNamedStreamCount is { } streams)
        {
            state += $", {streams:N0} named streams";
        }

        return state;
    }

    private static string FormatJournalEvidence(InspectExtractionRecoveryResponse inspection)
    {
        var pieces = new List<string>();
        if (inspection.JournalFileCount is { } files)
        {
            pieces.Add($"{files:N0} files");
        }

        if (inspection.JournalDirectoryCount is { } directories)
        {
            pieces.Add($"{directories:N0} directories");
        }

        if (inspection.JournalTotalBytes is { } bytes)
        {
            pieces.Add($"{bytes:N0} bytes");
        }

        if (inspection.JournalNamedStreamCount is { } streams)
        {
            pieces.Add($"{streams:N0} named streams");
        }

        if (inspection.JournalRevision is { Length: > 0 })
        {
            pieces.Add("revision recorded");
        }

        return pieces.Count == 0 ? "No complete journal totals are available" : string.Join(" · ", pieces);
    }

    private static string FormatAssessment(ExtractionRecoveryAssessment assessment) => assessment switch
    {
        ExtractionRecoveryAssessment.NoOwnedArtifactObserved => "No owned artifact observed",
        ExtractionRecoveryAssessment.OwnedIncompleteStagingObserved => "Owned incomplete staging observed",
        ExtractionRecoveryAssessment.OwnedFinalizedStagingObserved => "Owned finalized staging observed",
        ExtractionRecoveryAssessment.OwnedFinalObserved => "Owned final output observed",
        ExtractionRecoveryAssessment.MultipleOrConflictingArtifacts => "Multiple or conflicting artifacts",
        ExtractionRecoveryAssessment.ParentChanged => "Destination parent changed",
        ExtractionRecoveryAssessment.ChangedOrSuspicious => "Changed or suspicious state",
        ExtractionRecoveryAssessment.Excessive => "Resource limits exceeded",
        ExtractionRecoveryAssessment.InspectionIncomplete => "Inspection incomplete",
        _ => "Unknown assessment",
    };

    private enum ExtractionRecoveryUiOperation
    {
        Refresh,
        Inspect,
    }
}

public sealed class ExtractionRecoveryItemViewModel
{
    public ExtractionRecoveryItemViewModel(ExtractionRecoverySummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    internal ExtractionRecoverySummary Summary { get; }

    public string RecoveryId => Summary.RecoveryId;

    public string StateText => Summary.State switch
    {
        ExtractionRecoveryInventoryState.Incomplete => "Incomplete",
        ExtractionRecoveryInventoryState.Finalized => "Finalized",
        ExtractionRecoveryInventoryState.PublicationUncertain => "Publication uncertain",
        ExtractionRecoveryInventoryState.PublishedWithoutTerminal => "Published without terminal record",
        _ => "Unknown",
    };

    public string PhaseText => Summary.Phase == ExtractionRecoveryPhase.CommitIntent
        ? "Commit intent"
        : Summary.Phase.ToString();

    public string RecordedAtText => Summary.RecordedAtUtc.LocalDateTime.ToString("G");

    public string JournalStateText => Summary.HasIncompleteTail ? "Incomplete tail" : "Verified record";
}
