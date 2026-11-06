using RainArchives.App.Infrastructure;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class ActivityViewModel : ObservableObject
{
    private OperationPhase _phase;
    private string _description = string.Empty;
    private string _details = string.Empty;
    private double _progress;
    private bool _isIndeterminate = true;
    private bool _isVisible;

    public OperationId? OperationId { get; private set; }

    public bool CanCancel =>
        IsVisible
        && OperationId is not null
        && Phase is not (
            OperationPhase.Completed
            or OperationPhase.CompletedWithWarnings
            or OperationPhase.Cancelled
            or OperationPhase.Failed);

    public OperationPhase Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    public string Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public void Begin(OperationId operationId, string description)
    {
        OperationId = operationId;
        Description = description;
        Details = string.Empty;
        Phase = OperationPhase.Queued;
        Progress = 0;
        IsIndeterminate = true;
        IsVisible = true;
    }

    public void Apply(OperationEvent operationEvent)
    {
        if (operationEvent.OperationId != OperationId)
        {
            return;
        }

        Phase = operationEvent.Phase;
        if (operationEvent.Progress is { } progress)
        {
            Description = string.IsNullOrWhiteSpace(progress.CurrentEntryName)
                ? operationEvent.Phase.ToString()
                : UntrustedDisplayText.Escape(progress.CurrentEntryName);
            if (progress.TotalBytes is { } total && total > 0)
            {
                IsIndeterminate = false;
                Progress = Math.Clamp(progress.BytesProcessed * 100d / total, 0, 100);
            }

            Details = ActivityPresentationFormatter.FormatProgressDetails(progress);
        }

        if (operationEvent.Kind == OperationEventKind.Terminal)
        {
            IsIndeterminate = false;
            if (operationEvent.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings)
            {
                Progress = 100;
            }
        }
    }

    public void Hide()
    {
        IsVisible = false;
        OperationId = null;
        Details = string.Empty;
    }

}
