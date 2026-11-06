using System.Collections.ObjectModel;
using System.IO;
using RainArchives.App.Infrastructure;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class ArchiveRecoveryCenterViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IArchiveClient _client;
    private readonly Func<ArchiveRecoveryDispositionConfirmation, bool> _confirmDisposition;
    private readonly object _operationGate = new();
    private CancellationTokenSource? _lifetime = new();
    private Task _activeOperation = Task.CompletedTask;
    private ArchiveCreationRecoveryItemViewModel? _selectedRecovery;
    private InspectArchiveCreationRecoveryResponse? _inspection;
    private string _statusText = "Loading interrupted archive creations…";
    private string _inventorySummaryText = string.Empty;
    private bool _isBusy;

    public ArchiveRecoveryCenterViewModel(
        IArchiveClient client,
        Func<ArchiveRecoveryDispositionConfirmation, bool> confirmDisposition)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _confirmDisposition = confirmDisposition
            ?? throw new ArgumentNullException(nameof(confirmDisposition));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        InspectCommand = new AsyncRelayCommand(InspectSelectedAsync, () => CanInspect);
        CommitCommand = new AsyncRelayCommand(
            () => ApplyDispositionAsync(ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging),
            () => CanCommitOwnedStaging);
        DiscardCommand = new AsyncRelayCommand(
            () => ApplyDispositionAsync(ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging),
            () => CanDiscardOwnedStaging);
        AcceptCommand = new AsyncRelayCommand(
            () => ApplyDispositionAsync(ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal),
            () => CanAcceptPublishedFinal);
        AcknowledgeCommand = new AsyncRelayCommand(
            () => ApplyDispositionAsync(ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact),
            () => CanAcknowledgeNoOwnedArtifact);
    }

    public ObservableCollection<ArchiveCreationRecoveryItemViewModel> Recoveries { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand InspectCommand { get; }
    public AsyncRelayCommand CommitCommand { get; }
    public AsyncRelayCommand DiscardCommand { get; }
    public AsyncRelayCommand AcceptCommand { get; }
    public AsyncRelayCommand AcknowledgeCommand { get; }

    public bool SupportsDisposition =>
        _client.SupportsFeature(ProtocolFeatures.ArchiveCreationRecoveryDisposition);

    public ArchiveCreationRecoveryItemViewModel? SelectedRecovery
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
                StatusText = "Select Inspect before choosing any recovery action.";
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
    public bool CanInspect => !IsBusy && SelectedRecovery is not null;
    public bool CanCommitOwnedStaging =>
        !IsBusy
        && SupportsDisposition
        && IsApplicable(
            _inspection,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
    public bool CanDiscardOwnedStaging =>
        !IsBusy
        && SupportsDisposition
        && IsApplicable(
            _inspection,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging);
    public bool CanAcceptPublishedFinal =>
        !IsBusy
        && SupportsDisposition
        && IsApplicable(
            _inspection,
            ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal);
    public bool CanAcknowledgeNoOwnedArtifact =>
        !IsBusy
        && SupportsDisposition
        && IsApplicable(
            _inspection,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);

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

    public string DestinationPathText =>
        _inspection is null
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

    public string AssessmentText => _inspection is null
        ? string.Empty
        : FormatAssessment(_inspection.Assessment);

    public string InspectedAtText => _inspection is null
        ? string.Empty
        : $"Inspected {_inspection.InspectedAtUtc.LocalDateTime:G}";

    public string ActionGuidance
    {
        get
        {
            if (_inspection is null)
            {
                return "Select a recovery record and inspect it. Listing alone never contacts its destination.";
            }

            if (!SupportsDisposition)
            {
                return "This worker supports inspection only. No recovery action can be applied.";
            }

            if (CanCommitOwnedStaging && CanDiscardOwnedStaging)
            {
                return "The exact staged archive can be published or discarded. Choose one action explicitly.";
            }

            if (CanDiscardOwnedStaging)
            {
                return "Rain can revalidate and discard only its owned staging artifact.";
            }

            if (CanAcceptPublishedFinal)
            {
                return "Rain can revalidate the exact published archive and close this recovery record.";
            }

            if (CanAcknowledgeNoOwnedArtifact)
            {
                return "Rain can revalidate that no owned artifact remains and close this recovery record.";
            }

            return "The evidence is incomplete, conflicting, or changed. Rain will leave every artifact untouched.";
        }
    }

    public Task InitializeAsync() => RefreshAsync();

    public Task RefreshAsync() => RunTrackedAsync(
        RefreshCoreAsync,
        RecoveryUiOperation.Refresh);

    public Task InspectSelectedAsync()
    {
        var recovery = SelectedRecovery;
        if (recovery is null)
        {
            return Task.CompletedTask;
        }

        return RunTrackedAsync(
            cancellationToken => InspectCoreAsync(recovery, cancellationToken),
            RecoveryUiOperation.Inspect);
    }

    public Task ApplyDispositionAsync(ArchiveCreationRecoveryDispositionAction action)
    {
        var inspection = _inspection;
        if (!SupportsDisposition || !IsApplicable(inspection, action))
        {
            StatusText = "That action is not available for the current inspected evidence.";
            return Task.CompletedTask;
        }

        return RunTrackedAsync(
            cancellationToken => ApplyDispositionCoreAsync(inspection!, action, cancellationToken),
            RecoveryUiOperation.Disposition);
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
            // Cancellation is translated into dialog status by the tracked operation.
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private Task RunTrackedAsync(
        Func<CancellationToken, Task> action,
        RecoveryUiOperation operation)
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
        RecoveryUiOperation operation,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await action(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (operation == RecoveryUiOperation.Disposition)
            {
                ClearInspection();
                StatusText =
                    "The recovery action was cancelled before its final result was confirmed. "
                    + "Rain did not retry it. Refresh and inspect again.";
            }
            else
            {
                StatusText = "Recovery review cancelled.";
            }
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or IOException or InvalidDataException)
        {
            HandleOperationFailure(operation);
        }
        catch (Exception)
        {
            HandleOperationFailure(operation);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!_client.SupportsFeature(ProtocolFeatures.ArchiveCreationRecoveryInspection))
        {
            Recoveries.Clear();
            ClearInspection();
            OnPropertyChanged(nameof(HasRecoveries));
            OnPropertyChanged(nameof(HasNoRecoveries));
            InventorySummaryText = "Recovery inspection is unavailable.";
            StatusText = "The connected archive worker does not support recovery inspection.";
            return;
        }

        ClearInspection();
        var inventory = await _client
            .ListArchiveCreationRecoveriesAsync(cancellationToken)
            .ConfigureAwait(true);
        ReplaceInventory(inventory);
        StatusText = BuildInventoryStatus(inventory);
    }

    private async Task InspectCoreAsync(
        ArchiveCreationRecoveryItemViewModel selected,
        CancellationToken cancellationToken)
    {
        ClearInspection();
        var inspection = await _client
            .InspectArchiveCreationRecoveryAsync(selected.RecoveryId, cancellationToken)
            .ConfigureAwait(true);

        if (!ReferenceEquals(SelectedRecovery, selected))
        {
            StatusText = "The selection changed. Inspect the newly selected record before taking action.";
            return;
        }

        _inspection = inspection;
        NotifyInspectionChanged();
        StatusText = HasAnyApplicableAction()
            ? "Inspection complete. Review the evidence and choose an explicit action."
            : "Inspection complete. No safe recovery action is available for this evidence.";
    }

    private async Task ApplyDispositionCoreAsync(
        InspectArchiveCreationRecoveryResponse inspection,
        ArchiveCreationRecoveryDispositionAction action,
        CancellationToken cancellationToken)
    {
        var confirmation = BuildConfirmation(inspection, action);
        if (!_confirmDisposition(confirmation))
        {
            StatusText = "No recovery changes were made.";
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await _client.ApplyArchiveCreationRecoveryDispositionAsync(
                inspection.Recovery.RecoveryId,
                inspection.Recovery.Phase,
                inspection.Recovery.Sequence,
                inspection.Recovery.RecordedAtUtc,
                action,
                cancellationToken)
            .ConfigureAwait(true);

        ClearInspection();
        var completionText = result.Result == ArchiveCreationRecoveryDispositionResult.AlreadyCompleted
            ? "That exact recovery action had already completed safely."
            : ActionCompletedText(action);

        try
        {
            var inventory = await _client
                .ListArchiveCreationRecoveriesAsync(cancellationToken)
                .ConfigureAwait(true);
            ReplaceInventory(inventory);
            StatusText = $"{completionText} {BuildInventoryStatus(inventory)}";
        }
        catch (Exception exception) when (
            exception is ArchiveClientException
                or IOException
                or InvalidDataException
                or OperationCanceledException)
        {
            StatusText = $"{completionText} The recovery list could not be refreshed. No further action was taken.";
        }
    }

    private void ReplaceInventory(ListArchiveCreationRecoveriesResponse inventory)
    {
        SelectedRecovery = null;
        Recoveries.Clear();
        foreach (var recovery in inventory.Recoveries)
        {
            Recoveries.Add(new ArchiveCreationRecoveryItemViewModel(recovery));
        }

        OnPropertyChanged(nameof(HasRecoveries));
        OnPropertyChanged(nameof(HasNoRecoveries));
        InventorySummaryText =
            $"{inventory.Recoveries.Count:N0} pending · "
            + $"{inventory.ActiveOperationCount:N0} active · "
            + $"{inventory.SuspiciousOperationCount:N0} suspicious · "
            + $"{inventory.IncompleteJournalCount:N0} incomplete";
        NotifyCommandStates();
    }

    private static string BuildInventoryStatus(ListArchiveCreationRecoveriesResponse inventory)
    {
        var status = inventory.Recoveries.Count == 0
            ? "No interrupted archive creations currently need recovery review."
            : $"{inventory.Recoveries.Count:N0} interrupted archive creation"
              + (inventory.Recoveries.Count == 1 ? " needs" : "s need")
              + " review.";

        if (inventory.IsTruncated || inventory.InspectionIncomplete)
        {
            status += " The inventory is incomplete; no automatic action was taken.";
        }

        return status;
    }

    private void HandleOperationFailure(RecoveryUiOperation operation)
    {
        switch (operation)
        {
            case RecoveryUiOperation.Refresh:
                ClearInspection();
                StatusText = "The recovery list could not be refreshed. No action was taken.";
                break;
            case RecoveryUiOperation.Inspect:
                ClearInspection();
                StatusText = "Inspection failed. No recovery action is available.";
                break;
            case RecoveryUiOperation.Disposition:
                ClearInspection();
                StatusText =
                    "The recovery result could not be confirmed. Rain did not retry automatically. "
                    + "Refresh and inspect again.";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
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
        OnPropertyChanged(nameof(AssessmentText));
        OnPropertyChanged(nameof(InspectedAtText));
        OnPropertyChanged(nameof(ActionGuidance));
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanInspect));
        OnPropertyChanged(nameof(CanCommitOwnedStaging));
        OnPropertyChanged(nameof(CanDiscardOwnedStaging));
        OnPropertyChanged(nameof(CanAcceptPublishedFinal));
        OnPropertyChanged(nameof(CanAcknowledgeNoOwnedArtifact));
        OnPropertyChanged(nameof(ActionGuidance));
        RefreshCommand.NotifyCanExecuteChanged();
        InspectCommand.NotifyCanExecuteChanged();
        CommitCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        AcceptCommand.NotifyCanExecuteChanged();
        AcknowledgeCommand.NotifyCanExecuteChanged();
    }

    private bool HasAnyApplicableAction() =>
        SupportsDisposition
        && (IsApplicable(
                _inspection,
                ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging)
            || IsApplicable(
                _inspection,
                ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging)
            || IsApplicable(
                _inspection,
                ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal)
            || IsApplicable(
                _inspection,
                ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact));

    private static bool IsApplicable(
        InspectArchiveCreationRecoveryResponse? inspection,
        ArchiveCreationRecoveryDispositionAction action)
    {
        if (inspection is null)
        {
            return false;
        }

        return action switch
        {
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging =>
                inspection.Recovery.Phase == ArchiveCreationRecoveryPhase.CommitIntent
                && inspection.Assessment == ArchiveCreationRecoveryAssessment.OwnedStagingObserved
                && inspection.StagingArtifact.State
                    == ArchiveCreationRecoveryArtifactState.ExactMatch
                && inspection.FinalArtifact.State == ArchiveCreationRecoveryArtifactState.Absent,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging =>
                inspection.Assessment == ArchiveCreationRecoveryAssessment.OwnedStagingObserved
                && inspection.StagingArtifact.State is
                    ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound
                    or ArchiveCreationRecoveryArtifactState.ExactMatch
                && inspection.FinalArtifact.State == ArchiveCreationRecoveryArtifactState.Absent,
            ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal =>
                inspection.Assessment == ArchiveCreationRecoveryAssessment.OwnedFinalObserved
                && inspection.StagingArtifact.State == ArchiveCreationRecoveryArtifactState.Absent
                && inspection.FinalArtifact.State
                    == ArchiveCreationRecoveryArtifactState.ExactMatch,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact =>
                inspection.Assessment == ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved
                && inspection.StagingArtifact.State == ArchiveCreationRecoveryArtifactState.Absent
                && inspection.FinalArtifact.State == ArchiveCreationRecoveryArtifactState.Absent,
            _ => false,
        };
    }

    private static ArchiveRecoveryDispositionConfirmation BuildConfirmation(
        InspectArchiveCreationRecoveryResponse inspection,
        ArchiveCreationRecoveryDispositionAction action)
    {
        var destination = UntrustedDisplayText.Escape(inspection.DestinationPath);
        return action switch
        {
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging => new(
                action,
                "Publish recovered archive?",
                "Publish Rain's exact staged archive as:\n\n"
                + destination
                + "\n\nThe worker will revalidate identity, size, parent, and destination absence "
                + "before committing. Continue?"),
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging => new(
                action,
                "Discard recovered staging file?",
                "Permanently delete only Rain's revalidated staging artifact for:\n\n"
                + destination
                + "\n\nThe final archive will not be created. Continue?"),
            ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal => new(
                action,
                "Accept published archive?",
                "Revalidate the exact published archive and close its recovery record:\n\n"
                + destination
                + "\n\nNo archive bytes will be changed. Continue?"),
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact => new(
                action,
                "Close empty recovery record?",
                "Revalidate that no Rain-owned staging or final artifact exists for:\n\n"
                + destination
                + "\n\nThen close the recovery record. Continue?"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static string ActionCompletedText(ArchiveCreationRecoveryDispositionAction action) =>
        action switch
        {
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging =>
                "The staged archive was published and its recovery record was completed.",
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging =>
                "The owned staging artifact was discarded and its recovery record was completed.",
            ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal =>
                "The published archive was accepted and its recovery record was completed.",
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact =>
                "The empty recovery record was completed.",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static string FormatPhase(ArchiveCreationRecoveryPhase phase) => phase switch
    {
        ArchiveCreationRecoveryPhase.Admitted => "Admitted",
        ArchiveCreationRecoveryPhase.Writing => "Writing",
        ArchiveCreationRecoveryPhase.Finalized => "Finalized",
        ArchiveCreationRecoveryPhase.CommitIntent => "Commit intent",
        ArchiveCreationRecoveryPhase.Committed => "Committed",
        _ => "Unknown phase",
    };

    private static string FormatParentState(ArchiveCreationRecoveryParentState state) => state switch
    {
        ArchiveCreationRecoveryParentState.MatchesJournal => "Matches the recovery journal",
        ArchiveCreationRecoveryParentState.Missing => "Destination parent is missing",
        ArchiveCreationRecoveryParentState.IdentityMismatch => "Destination parent identity changed",
        ArchiveCreationRecoveryParentState.ReparsePointOrWrongKind => "Destination parent is a reparse point or wrong kind",
        ArchiveCreationRecoveryParentState.AccessDenied => "Destination parent access was denied",
        ArchiveCreationRecoveryParentState.Unavailable => "Destination parent is unavailable",
        _ => "Destination parent was not safely inspected",
    };

    private static string FormatArtifact(ArchiveCreationRecoveryArtifactInspection artifact)
    {
        var state = artifact.State switch
        {
            ArchiveCreationRecoveryArtifactState.NotBoundByJournal => "not bound by the journal",
            ArchiveCreationRecoveryArtifactState.NotInspected => "not inspected",
            ArchiveCreationRecoveryArtifactState.Absent => "absent",
            ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound => "owned identity match; length not yet bound",
            ArchiveCreationRecoveryArtifactState.ExactMatch => "exact owned match",
            ArchiveCreationRecoveryArtifactState.IdentityMismatch => "identity mismatch",
            ArchiveCreationRecoveryArtifactState.LengthMismatch => "length mismatch",
            ArchiveCreationRecoveryArtifactState.WrongKind => "wrong filesystem object kind",
            ArchiveCreationRecoveryArtifactState.ReparsePoint => "reparse point",
            ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks => "unexpected hard links",
            ArchiveCreationRecoveryArtifactState.AccessDenied => "access denied",
            ArchiveCreationRecoveryArtifactState.Unavailable => "unavailable",
            _ => "unknown",
        };

        if (artifact.ObservedLength is { } length)
        {
            state += $", {length:N0} bytes";
        }

        if (artifact.LinkCount is { } links)
        {
            state += $", {links:N0} link{(links == 1 ? string.Empty : "s")}";
        }

        return state;
    }

    private static string FormatAssessment(ArchiveCreationRecoveryAssessment assessment) =>
        assessment switch
        {
            ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved => "No owned artifact observed",
            ArchiveCreationRecoveryAssessment.OwnedStagingObserved => "Owned staging artifact observed",
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved => "Owned final archive observed",
            ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts => "Multiple or conflicting artifacts",
            ArchiveCreationRecoveryAssessment.ParentChanged => "Destination parent changed",
            ArchiveCreationRecoveryAssessment.InspectionIncomplete => "Inspection incomplete",
            _ => "Unknown assessment",
        };

    private enum RecoveryUiOperation
    {
        Refresh,
        Inspect,
        Disposition,
    }
}

public sealed class ArchiveCreationRecoveryItemViewModel
{
    public ArchiveCreationRecoveryItemViewModel(ArchiveCreationRecoverySummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    internal ArchiveCreationRecoverySummary Summary { get; }
    public string RecoveryId => Summary.RecoveryId;
    public string PhaseText => Summary.Phase switch
    {
        ArchiveCreationRecoveryPhase.CommitIntent => "Commit intent",
        _ => Summary.Phase.ToString(),
    };
    public string RecordedAtText => Summary.RecordedAtUtc.LocalDateTime.ToString("G");
    public string JournalStateText => Summary.HasIncompleteTail
        ? "Incomplete tail"
        : "Verified record";
}

public sealed record ArchiveRecoveryDispositionConfirmation(
    ArchiveCreationRecoveryDispositionAction Action,
    string Title,
    string Message);
