using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ActivityViewModelTests
{
    [TestMethod]
    public void ProgressExposesAggregateBytesEntriesThroughputElapsedAndRatio()
    {
        var operationId = OperationId.New();
        var activity = new ActivityViewModel();
        activity.Begin(operationId, "Preparing ZIP creation");

        activity.Apply(new OperationEvent(
            operationId,
            1,
            OperationEventKind.Progress,
            OperationPhase.Running,
            new OperationProgress(
                1024 * 1024,
                2 * 1024 * 1024,
                2,
                4,
                null,
                "assets/雨.txt",
                TimeSpan.FromSeconds(65),
                512 * 1024,
                0.5)));

        Assert.AreEqual("assets/雨.txt", activity.Description);
        StringAssert.Contains(activity.Details, "1 MiB of 2 MiB");
        StringAssert.Contains(activity.Details, "2 of 4 entries");
        StringAssert.Contains(activity.Details, "512 KiB/s");
        StringAssert.Contains(activity.Details, "1:05");
        StringAssert.Contains(activity.Details, "ratio 0.5");
        Assert.AreEqual(50, activity.Progress);
        Assert.IsFalse(activity.IsIndeterminate);
    }

    [TestMethod]
    public void ActivityIgnoresUnrelatedOperationsAndClearsDetailsWhenHidden()
    {
        var operationId = OperationId.New();
        var activity = new ActivityViewModel();
        activity.Begin(operationId, "Preparing extraction");

        activity.Apply(new OperationEvent(
            OperationId.New(),
            1,
            OperationEventKind.Progress,
            OperationPhase.Running,
            new OperationProgress(10, 10, 1, 1, null, "other.txt", TimeSpan.Zero, null, null)));

        Assert.AreEqual("Preparing extraction", activity.Description);
        Assert.AreEqual(string.Empty, activity.Details);
        activity.Hide();
        Assert.IsFalse(activity.IsVisible);
        Assert.IsNull(activity.OperationId);
        Assert.AreEqual(string.Empty, activity.Details);
    }
}
