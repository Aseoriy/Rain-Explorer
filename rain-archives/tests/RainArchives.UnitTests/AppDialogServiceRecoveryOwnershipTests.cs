using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class AppDialogServiceRecoveryOwnershipTests
{
    [TestMethod]
    public void CancellationAtRecoveryHandoffClosesTheUntransferredSessionExactlyOnce()
    {
        RunOnSta(async owner =>
        {
            using var cancellation = new CancellationTokenSource();
            var response = CreateReopenedArchive();
            var dialog = new FakeRecoveryDialogSession(response);
            var client = new RecordingCloseClient();
            var service = new AppDialogService(
                owner,
                (_, _) => dialog,
                _ =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                });

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await service.ShowArchiveRecoveryWithResultAsync(client, cancellation.Token));

            Assert.AreEqual(1, dialog.DrainCalls);
            Assert.AreEqual(1, dialog.CancellationRequests);
            CollectionAssert.AreEqual(
                new[] { response.Archive.SessionId },
                client.ClosedSessions.ToArray());
        });
    }

    [TestMethod]
    public void SuccessfulRecoveryHandoffTransfersOwnershipWithoutClosing()
    {
        RunOnSta(async owner =>
        {
            using var cancellation = new CancellationTokenSource();
            var response = CreateReopenedArchive();
            var dialog = new FakeRecoveryDialogSession(response, reopenedWithPassword: true);
            var client = new RecordingCloseClient();
            var service = new AppDialogService(owner, (_, _) => dialog);

            var result = await service.ShowArchiveRecoveryWithResultAsync(
                client,
                cancellation.Token);

            Assert.IsNotNull(result);
            Assert.AreSame(response, result.Response);
            Assert.IsTrue(result.OpenedWithPassword);
            Assert.HasCount(0, client.ClosedSessions);

            cancellation.Cancel();
            Assert.HasCount(0, client.ClosedSessions);
            Assert.AreEqual(0, dialog.CancellationRequests);
        });
    }

    [TestMethod]
    public void CompatibilityRecoveryEntryPointClosesTheUnreturnableSessionExactlyOnce()
    {
        RunOnSta(async owner =>
        {
            var response = CreateReopenedArchive();
            var dialog = new FakeRecoveryDialogSession(response);
            var client = new RecordingCloseClient();
            var service = new AppDialogService(owner, (_, _) => dialog);

            await service.ShowArchiveRecoveryAsync(client, CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { response.Archive.SessionId },
                client.ClosedSessions.ToArray());
        });
    }

    [TestMethod]
    public void DrainFailureAfterReopenClosesTheUntransferredSessionExactlyOnce()
    {
        RunOnSta(async owner =>
        {
            var response = CreateReopenedArchive();
            var dialog = new FakeRecoveryDialogSession(response)
            {
                DrainException = new InvalidDataException("Deterministic recovery drain failure."),
            };
            var client = new RecordingCloseClient();
            var service = new AppDialogService(owner, (_, _) => dialog);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await service.ShowArchiveRecoveryWithResultAsync(
                    client,
                    CancellationToken.None));

            CollectionAssert.AreEqual(
                new[] { response.Archive.SessionId },
                client.ClosedSessions.ToArray());
        });
    }

    private static void RunOnSta(Func<Window, Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? owner = null;
            try
            {
                owner = new Window();
                action(owner).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                owner?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "RainArchives.RecoveryOwnershipTest",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The recovery ownership test did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static ReopenNestedWorkspaceRecoveryResponse CreateReopenedArchive()
    {
        var archive = new ArchiveDescriptor(
            ArchiveSessionId.New(),
            "zip",
            "test-provider",
            ArchiveCapability.List,
            new ArchiveFingerprint(
                1,
                DateTimeOffset.UtcNow,
                FileIdentity: null,
                PrefixSha256: new string('A', 64)),
            EntryCount: 0,
            IsSolid: false,
            HasEncryptedEntries: false,
            IsMultipart: false,
            IsReadOnly: true,
            Warnings: [],
            ArchiveVolumeSetKind.Single);
        return new ReopenNestedWorkspaceRecoveryResponse(
            new string('a', 64),
            new string('b', 64),
            NestedWorkspaceRecoveryReopenMode.IndependentRoot,
            archive,
            MaterializedBytes: 1,
            Depth: 0,
            DateTimeOffset.UtcNow);
    }

    private sealed class FakeRecoveryDialogSession(
        ReopenNestedWorkspaceRecoveryResponse? reopenedArchive,
        bool reopenedWithPassword = false) : IArchiveRecoveryDialogSession
    {
        public Exception? DrainException { get; init; }

        public int CancellationRequests { get; private set; }

        public int DrainCalls { get; private set; }

        public ReopenNestedWorkspaceRecoveryResponse? ReopenedArchive { get; } = reopenedArchive;

        public bool ReopenedWithPassword { get; } = reopenedWithPassword;

        public bool? ShowDialog() => ReopenedArchive is null ? false : true;

        public void RequestCancellation() => CancellationRequests++;

        public ValueTask DrainAsync()
        {
            DrainCalls++;
            return DrainException is null
                ? ValueTask.CompletedTask
                : new ValueTask(Task.FromException(DrainException));
        }
    }

    private sealed class RecordingCloseClient : ArchiveClientTestDouble
    {
        public List<ArchiveSessionId> ClosedSessions { get; } = [];

        public override Task<CloseArchiveResponse> CloseArchiveAsync(
            ArchiveSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClosedSessions.Add(sessionId);
            return Task.FromResult(new CloseArchiveResponse(true));
        }
    }
}
