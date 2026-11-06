using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Worker;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class WorkerOperationLifecycleTests
{
    [TestMethod]
    public void CancellationIsRefusedAfterTerminalEventWasRecorded()
    {
        using var operation = new WorkerOperation(OperationKind.Extract, CancellationToken.None);
        operation.RecordSentEvent(
            new OperationEvent(
                operation.Id,
                1,
                OperationEventKind.Terminal,
                OperationPhase.Completed),
            DateTimeOffset.UtcNow);

        Assert.IsFalse(operation.TryCancel());
        Assert.IsFalse(operation.Cancellation.IsCancellationRequested);
        Assert.IsFalse(operation.CreateActivitySnapshot().CanCancel);
    }
}
