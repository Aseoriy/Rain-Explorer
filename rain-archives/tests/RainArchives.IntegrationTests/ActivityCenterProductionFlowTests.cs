using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class ActivityCenterProductionFlowTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task RealWorkerModelPresentsCreationDiagnosticsAndExactQueuedCancellation()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var source = Path.Combine(root.Path, "bounded-source.bin");
        var completedDestination = Path.Combine(root.Path, "completed.zip");
        var cancelledDestination = Path.Combine(root.Path, "cancelled.zip");
        await WriteIncompressibleFileAsync(source, 96L * 1024 * 1024);
        var originalHash = await HashFileAsync(source);

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var uiQueue = new ConcurrentQueue<Action>();
        await using var activityCenter = new ActivityCenterViewModel(
            client,
            uiQueue.Enqueue,
            TimeSpan.FromMilliseconds(250));
        activityCenter.Start();
        await WaitUntilAsync(uiQueue, () => activityCenter.IsReady);

        var first = await client.StartZipCreationAsync(
            completedDestination,
            [new ArchiveCreationFile(source, "payload.bin")],
            ArchiveCompressionPreset.Maximum);
        activityCenter.TrackStarted(
            first.OperationId,
            OperationKind.CreateArchive,
            "ZIP archive creation");
        var second = await client.StartZipCreationAsync(
            cancelledDestination,
            [new ArchiveCreationFile(source, "queued-payload.bin")],
            ArchiveCompressionPreset.Maximum);
        activityCenter.TrackStarted(
            second.OperationId,
            OperationKind.CreateArchive,
            "Queued ZIP archive creation");

        await WaitUntilAsync(
            uiQueue,
            () => activityCenter.Operations.Any(operation =>
                operation.OperationId == second.OperationId && operation.CanCancel),
            TimeSpan.FromSeconds(10),
            () => $"State={activityCenter.StatusText}; rows={string.Join(',', activityCenter.Operations.Select(operation => $"{operation.Phase}:{operation.CanCancel}"))}");
        var queued = activityCenter.Operations.Single(operation =>
            operation.OperationId == second.OperationId);
        Assert.IsTrue(queued.Phase is OperationPhase.Queued or OperationPhase.Running);
        Assert.IsTrue(activityCenter.SupportDiagnostics.IsAvailable);
        StringAssert.Contains(activityCenter.PolicySummary, "32 operations");
        StringAssert.Contains(activityCenter.PolicySummary, "queue order is unspecified");
        StringAssert.Contains(
            activityCenter.SupportDiagnostics.SandboxText,
            "Not a same-user filesystem sandbox");

        queued.CancelCommand.Execute(null);
        var cancelledTerminal = await client
            .WaitForTerminalAsync(second.OperationId)
            .WaitAsync(TimeSpan.FromSeconds(15));
        var completedTerminal = await client
            .WaitForTerminalAsync(first.OperationId)
            .WaitAsync(TimeSpan.FromSeconds(45));
        Assert.AreEqual(OperationPhase.Cancelled, cancelledTerminal.Phase);
        Assert.IsTrue(completedTerminal.Phase is
            OperationPhase.Completed or OperationPhase.CompletedWithWarnings);

        activityCenter.RequestRefresh();
        await WaitUntilAsync(
            uiQueue,
            () => activityCenter.Operations.Any(operation =>
                    operation.OperationId == second.OperationId
                    && operation.Phase == OperationPhase.Cancelled)
                && activityCenter.Operations.Any(operation =>
                    operation.OperationId == first.OperationId
                    && operation.IsTerminal));

        queued = activityCenter.Operations.Single(operation =>
            operation.OperationId == second.OperationId);
        Assert.IsFalse(queued.CanCancel);
        Assert.AreEqual("Cancelled; no retry was started", queued.OutcomeText);
        Assert.IsTrue(File.Exists(completedDestination));
        Assert.IsFalse(File.Exists(cancelledDestination));
        CollectionAssert.AreEqual(originalHash, await HashFileAsync(source));
        Assert.IsEmpty(Directory.GetFiles(root.Path, ".rainarchives-*.staging", SearchOption.TopDirectoryOnly));
        Assert.HasCount(0, FindCreationRecoveryRecords(workerTemporaryRoot));
    }

    private static async Task WaitUntilAsync(
        ConcurrentQueue<Action> uiQueue,
        Func<bool> condition,
        TimeSpan? timeout = null,
        Func<string>? timeoutEvidence = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            while (uiQueue.TryDequeue(out var action))
            {
                action();
            }

            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(
                    "The production Activity Center condition was not reached before timeout. "
                    + (timeoutEvidence?.Invoke() ?? string.Empty));
            }

            await Task.Delay(10);
        }
    }

    private static async Task WriteIncompressibleFileAsync(string path, long length)
    {
        var buffer = new byte[1024 * 1024];
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long written = 0;
        while (written < length)
        {
            RandomNumberGenerator.Fill(buffer);
            var count = (int)Math.Min(buffer.Length, length - written);
            await stream.WriteAsync(buffer.AsMemory(0, count));
            written += count;
        }

        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    private static string[] FindCreationRecoveryRecords(string workerTemporaryRoot)
    {
        var recoveryRoot = Path.Combine(workerTemporaryRoot, "rainarchives-creation-recovery");
        return Directory.Exists(recoveryRoot)
            ? Directory.GetDirectories(
                recoveryRoot,
                "rainarchives-creation-*",
                SearchOption.TopDirectoryOnly)
            : [];
    }

    private static string FindWorker()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        return File.Exists(worker)
            ? worker
            : throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ActivityCenterProductionFlowTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
