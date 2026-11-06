using System.Windows;
using System.Windows.Threading;
using RainExplorer.Models;

namespace RainExplorer.Services;

internal interface IArchiveActivitySink
{
    IArchiveActivityHandle Begin(ActivityOperationKind kind);
}

internal interface IArchiveActivityHandle
{
    void AttachCancel(Action cancel);
    void MarkCancellationPending();
    void Report(ActivityOperationUpdate update);
}

internal sealed class ArchiveActivitySink : IArchiveActivitySink
{
    public static ArchiveActivitySink Instance { get; } = new();

    private ArchiveActivitySink()
    {
    }

    public IArchiveActivityHandle Begin(ActivityOperationKind kind)
    {
        ActivityService service = ActivityService.Instance;
        string title = kind switch
        {
            ActivityOperationKind.Extraction => "Archive extraction",
            ActivityOperationKind.TransferOut => "Copy from archive",
            ActivityOperationKind.ArchiveCreation => "Create archive",
            ActivityOperationKind.ArchiveRebuild => "Rebuild ZIP copy",
            _ => "Archive integrity test",
        };
        ActivityEntry entry = service.Begin(
            title,
            "Validating the current archive operation.",
            kind is ActivityOperationKind.Extraction
                or ActivityOperationKind.TransferOut
                or ActivityOperationKind.ArchiveCreation
                or ActivityOperationKind.ArchiveRebuild
                ? "archive"
                : "shield");
        service.RequestShow(owner: null);
        return new Handle(service, entry);
    }

    private sealed class Handle(ActivityService service, ActivityEntry entry) : IArchiveActivityHandle
    {
        public void AttachCancel(Action cancel) => service.AttachCancel(entry, cancel);

        public void MarkCancellationPending() => service.MarkCancellationPending(entry);

        public void Report(ActivityOperationUpdate update)
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                service.ReportOperation(entry, update);
                return;
            }

            _ = dispatcher.BeginInvoke(() => service.ReportOperation(entry, update));
        }
    }
}
