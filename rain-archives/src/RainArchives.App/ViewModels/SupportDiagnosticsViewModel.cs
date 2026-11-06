using RainArchives.App.Infrastructure;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class SupportDiagnosticsViewModel : ObservableObject
{
    private bool _isAvailable;
    private string _availabilityText = "Loading fixed-field support diagnostics…";
    private string _protocolText = "Not available";
    private string _containmentText = "Not available";
    private string _sandboxText = "Not available";
    private string _admissionText = "Not available";
    private string _currentCountsText = "Not available";
    private string _controlsText = "Not available";
    private string _recoveryText = "Not available";
    private string _persistenceText = "Not available";

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public string AvailabilityText
    {
        get => _availabilityText;
        private set => SetProperty(ref _availabilityText, value);
    }

    public string ProtocolText
    {
        get => _protocolText;
        private set => SetProperty(ref _protocolText, value);
    }

    public string ContainmentText
    {
        get => _containmentText;
        private set => SetProperty(ref _containmentText, value);
    }

    public string SandboxText
    {
        get => _sandboxText;
        private set => SetProperty(ref _sandboxText, value);
    }

    public string AdmissionText
    {
        get => _admissionText;
        private set => SetProperty(ref _admissionText, value);
    }

    public string CurrentCountsText
    {
        get => _currentCountsText;
        private set => SetProperty(ref _currentCountsText, value);
    }

    public string ControlsText
    {
        get => _controlsText;
        private set => SetProperty(ref _controlsText, value);
    }

    public string RecoveryText
    {
        get => _recoveryText;
        private set => SetProperty(ref _recoveryText, value);
    }

    public string PersistenceText
    {
        get => _persistenceText;
        private set => SetProperty(ref _persistenceText, value);
    }

    internal void Apply(GetSupportDiagnosticsResponse response)
    {
        IsAvailable = true;
        AvailabilityText = "Available from the connected Worker through the fixed protocol 1.22 schema.";
        ProtocolText = $"{response.ProtocolMajor}.{response.ProtocolMinor} · {response.EnabledFeatureCount:N0} negotiated features";
        ContainmentText = response.ContainmentModel switch
        {
            WorkerContainmentModel.SameUserWorkerProcess =>
                "Same-user on-demand Worker with an authenticated pipe and owned kill-on-close Job Object",
            _ => "Unsupported containment value",
        };
        SandboxText = response.IsSameUserSecuritySandbox
            ? "Reported as a same-user filesystem sandbox"
            : "Not a same-user filesystem sandbox; this boundary contains lifecycle and crashes only";
        AdmissionText =
            $"{response.MaximumActiveAndQueuedOperations:N0} admitted, {response.MaximumConcurrentOperations:N0} running, "
            + $"{response.MaximumRetainedTerminalOperations:N0} retained terminals; ordering {FormatOrdering(response.QueueOrdering)}";
        CurrentCountsText =
            $"{response.ActiveAndQueuedOperationCount:N0} active or queued · "
            + $"{response.RetainedTerminalOperationCount:N0} retained terminal"
            + (response.OperationHistoryTruncated ? " · older history truncated" : string.Empty);
        ControlsText = response.SupportsQueueReordering
            || response.SupportsPause
            || response.SupportsRetry
            ? "The Worker reported an unsupported lifecycle control."
            : "Cancel is the only operation control. Pause, retry, and queue reordering are unavailable.";
        RecoveryText = response.SupportsAutomaticRecoveryDisposition
            ? "The Worker reported unsupported automatic recovery disposition."
            : "Automatic recovery disposition is unavailable; recovery remains explicit and separately reviewed.";
        PersistenceText = response.PersistsOperationActivityAcrossWorkerRestart
            ? "The Worker reported unsupported persisted operation activity."
            : "Activity is connection-lifetime only and does not persist across Worker restart.";
    }

    internal void MarkUnavailable(bool requiresProtocolUpgrade)
    {
        IsAvailable = false;
        AvailabilityText = requiresProtocolUpgrade
            ? "Fixed-field support diagnostics require protocol 1.22. No fallback data is derived."
            : "Fixed-field support diagnostics are unavailable because the Worker connection is offline.";
        ProtocolText = "Not available";
        ContainmentText = "Not available";
        SandboxText = "Not available";
        AdmissionText = "Not available";
        CurrentCountsText = "Not available";
        ControlsText = "No controls are inferred while diagnostics are unavailable.";
        RecoveryText = "No recovery authority is inferred while diagnostics are unavailable.";
        PersistenceText = "No persistence is inferred while diagnostics are unavailable.";
    }

    private static string FormatOrdering(OperationQueueOrdering ordering) => ordering switch
    {
        OperationQueueOrdering.Unspecified => "unspecified",
        _ => "unsupported",
    };
}
