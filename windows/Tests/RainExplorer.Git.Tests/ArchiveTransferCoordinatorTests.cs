using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveTransferCoordinatorTests
{
    [Fact]
    public async Task FirstRenderOwnsPasswordAndAllReentrantRequestsShareOneNestedPublication()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueExtraction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int extractionCount = 0;
        string? observedPassword = null;
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, ownedStorage, progress, password, cancellationToken) =>
            {
                extractionCount++;
                observedPassword = password is null ? null : new string(password);
                if (password is not null) Array.Clear(password);
                ArchiveTransferSelectionManifest selection = NestedSelection();
                progress.BindSelection(selection);
                progress.MarkStartAttempted();
                entered.TrySetResult();
                await continueExtraction.Task.WaitAsync(cancellationToken);
                string folder = Directory.CreateDirectory(
                    Path.Combine(allocation.DestinationPath, "folder")).FullName;
                await File.WriteAllTextAsync(Path.Combine(folder, "file.txt"), "rain", cancellationToken);
                progress.ConfirmPublished();
                ArchiveTransferManifest manifest = await ownedStorage.ReviewCompletedAsync(
                    allocation, selection, cancellationToken);
                progress.SetReviewedManifest(manifest);
                return new ArchiveOperationResult(ArchiveOperationOutcome.Completed);
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(
                await coordinator.BeginAsync());
            Assert.False(owner.HasStartedRendering);
            Assert.Equal(0, extractionCount);

            char[] firstPassword = "first-owner".ToCharArray();
            char[] unusedPassword = "must-clear".ToCharArray();
            Task<ArchiveTransferRenderResult> first = owner.RenderAsync(firstPassword);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<ArchiveTransferRenderResult> reentrant = owner.RenderAsync(unusedPassword);

            Assert.Same(first, reentrant);
            Assert.All(unusedPassword, character => Assert.Equal('\0', character));
            Assert.Equal(1, extractionCount);
            continueExtraction.TrySetResult();
            ArchiveTransferRenderResult result = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.IsReady);
            Assert.True(result.CopyOnly);
            string published = Assert.Single(result.FileDropPaths);
            Assert.EndsWith(Path.Combine("folder", "file.txt"), published, StringComparison.Ordinal);
            Assert.Equal("rain", await File.ReadAllTextAsync(published));
            Assert.Equal("first-owner", observedPassword);
            Assert.All(firstPassword, character => Assert.Equal('\0', character));

            Task<ArchiveTransferCleanupOutcome> firstRelease = owner.ReleaseAsync(
                ArchiveTransferReleaseReason.DragCompleted);
            Task<ArchiveTransferCleanupOutcome> secondRelease = owner.ReleaseAsync(
                ArchiveTransferReleaseReason.TargetException);
            Assert.Same(firstRelease, secondRelease);
            Assert.Equal(ArchiveTransferCleanupOutcome.Removed, await firstRelease);
            Assert.Equal(ArchiveTransferReleaseReason.DragCompleted, owner.ReleaseReason);
            await coordinator.DrainAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            continueExtraction.TrySetResult();
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FirstRequestCancellationCancelsTheOneSharedRenderWithoutRetry()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int extractionCount = 0;
        var coordinator = new ArchiveTransferCoordinator(
            async (_, _, progress, password, cancellationToken) =>
            {
                extractionCount++;
                if (password is not null) Array.Clear(password);
                progress.BindSelection(NestedSelection());
                progress.MarkStartAttempted();
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ArchiveOperationResult(ArchiveOperationOutcome.Failed);
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(await coordinator.BeginAsync());
            using var firstRequest = new CancellationTokenSource();
            Task<ArchiveTransferRenderResult> first = owner.RenderAsync(
                "clear-me".ToCharArray(), firstRequest.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<ArchiveTransferRenderResult> second = owner.RenderAsync();
            firstRequest.Cancel();

            Assert.Same(first, second);
            Assert.Equal(ArchiveTransferOutcome.Cancelled, (await second).Outcome);
            Assert.Equal(1, extractionCount);
            Assert.Equal(
                ArchiveTransferCleanupOutcome.NotNeeded,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetRefused));
            await coordinator.DrainAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExceptionAfterWorkerPublicationStillCleansTheExactReviewedOutput()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, _, progress, password, cancellationToken) =>
            {
                if (password is not null) Array.Clear(password);
                ArchiveTransferSelectionManifest selection = FlatSelection();
                progress.BindSelection(selection);
                progress.MarkStartAttempted();
                Directory.CreateDirectory(allocation.DestinationPath);
                await File.WriteAllTextAsync(
                    Path.Combine(allocation.DestinationPath, "selected.txt"),
                    "yes",
                    cancellationToken);
                progress.ConfirmPublished();
                throw new IOException("simulated post-publication client failure");
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(await coordinator.BeginAsync());
            ArchiveTransferRenderResult result = await owner.RenderAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ArchiveTransferOutcome.Failed, result.Outcome);
            Assert.Equal(ArchiveTransferCleanupOutcome.Removed, owner.CleanupOutcome);
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Removed,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetException));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnexpectedOrdinaryObjectIsPreservedInsteadOfPublishedOrDeleted()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        string? destination = null;
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, _, progress, _, cancellationToken) =>
            {
                destination = allocation.DestinationPath;
                ArchiveTransferSelectionManifest selection = FlatSelection();
                progress.BindSelection(selection);
                progress.MarkStartAttempted();
                Directory.CreateDirectory(allocation.DestinationPath);
                await File.WriteAllTextAsync(
                    Path.Combine(allocation.DestinationPath, "selected.txt"), "yes", cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(allocation.DestinationPath, "injected.txt"), "no", cancellationToken);
                progress.ConfirmPublished();
                throw new InvalidDataException("simulated exact-manifest mismatch");
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(await coordinator.BeginAsync());
            ArchiveTransferRenderResult result = await owner.RenderAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ArchiveTransferOutcome.Preserved, result.Outcome);
            Assert.Equal(ArchiveTransferCleanupOutcome.Preserved, owner.CleanupOutcome);
            Assert.NotNull(destination);
            Assert.True(File.Exists(Path.Combine(destination!, "selected.txt")));
            Assert.True(File.Exists(Path.Combine(destination!, "injected.txt")));
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Preserved,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetException));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonFatalUnexpectedExceptionAlwaysSettlesRenderCompletion()
    {
        using var temporary = new TemporaryDirectory();
        var coordinator = new ArchiveTransferCoordinator(
            (_, _, _, _, _) => throw new OverflowException("bounded review overflow"),
            new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root")));
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(await coordinator.BeginAsync());
            Assert.Equal(
                ArchiveTransferOutcome.Failed,
                (await owner.RenderAsync().WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
            await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetException);
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledUnownedOutputIsNeverClaimedOrDeleted(bool createForeignOutput)
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        string? destination = null;
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, _, progress, _, cancellationToken) =>
            {
                destination = allocation.DestinationPath;
                progress.BindSelection(FlatSelection());
                progress.MarkStartAttempted();
                if (createForeignOutput)
                {
                    Directory.CreateDirectory(allocation.DestinationPath);
                    await File.WriteAllTextAsync(
                        Path.Combine(allocation.DestinationPath, "partial.txt"),
                        "partial",
                        cancellationToken);
                }
                return new ArchiveOperationResult(ArchiveOperationOutcome.Cancelled);
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(await coordinator.BeginAsync());
            ArchiveTransferRenderResult result = await owner.RenderAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                createForeignOutput ? ArchiveTransferOutcome.Preserved : ArchiveTransferOutcome.Cancelled,
                result.Outcome);
            Assert.NotNull(destination);
            Assert.Equal(createForeignOutput, Directory.Exists(destination!));
            if (createForeignOutput)
                Assert.True(File.Exists(Path.Combine(destination!, "partial.txt")));
            else
                Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
            await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetRefused);
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task DestinationConflictNeverDeletesForeignExactLookingTree()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        string? destination = null;
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, _, progress, _, cancellationToken) =>
            {
                destination = allocation.DestinationPath;
                progress.BindSelection(FlatSelection());
                progress.MarkStartAttempted();
                Directory.CreateDirectory(allocation.DestinationPath);
                await File.WriteAllTextAsync(
                    Path.Combine(allocation.DestinationPath, "selected.txt"),
                    "yes",
                    cancellationToken);
                return new ArchiveOperationResult(ArchiveOperationOutcome.DestinationConflict);
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(await coordinator.BeginAsync());
            ArchiveTransferRenderResult result = await owner.RenderAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ArchiveTransferOutcome.Preserved, result.Outcome);
            Assert.NotNull(destination);
            Assert.Equal("yes", await File.ReadAllTextAsync(
                Path.Combine(destination!, "selected.txt")));
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Preserved,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetRefused));
            Assert.Equal("yes", await File.ReadAllTextAsync(
                Path.Combine(destination!, "selected.txt")));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentBeginSerializesReplacementAndLeavesOneLiveOwner()
    {
        using var temporary = new TemporaryDirectory();
        int extractionCount = 0;
        var coordinator = new ArchiveTransferCoordinator(
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref extractionCount);
                return Task.FromResult(new ArchiveOperationResult(ArchiveOperationOutcome.Failed));
            },
            new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root")));
        try
        {
            ArchiveTransferOwner[] owners = (await Task.WhenAll(
                    Enumerable.Range(0, 8).Select(_ => coordinator.BeginAsync())))
                .Select(owner => Assert.IsType<ArchiveTransferOwner>(owner))
                .ToArray();
            ArchiveTransferOwner live = Assert.Single(owners, owner => owner.ReleaseReason is null);
            Assert.All(owners.Where(owner => !ReferenceEquals(owner, live)), owner =>
                Assert.Equal(ArchiveTransferReleaseReason.ClipboardReplaced, owner.ReleaseReason));
            Assert.Equal(ArchiveTransferOutcome.Failed, (await live.RenderAsync()).Outcome);
            Assert.Equal(1, extractionCount);
            await live.ReleaseAsync(ArchiveTransferReleaseReason.TargetRefused);
            await coordinator.DrainAsync();
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TimedOutReleaseKeepsLateRenderTrackedBeforeNewAdmission()
    {
        using var temporary = new TemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ArchiveTransferCoordinator(
            async (_, _, _, _, _) =>
            {
                entered.TrySetResult();
                await finish.Task;
                return new ArchiveOperationResult(ArchiveOperationOutcome.Failed);
            },
            new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root")),
            renderDrainTimeout: TimeSpan.FromMilliseconds(50));
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(await coordinator.BeginAsync());
            Task<ArchiveTransferRenderResult> render = owner.RenderAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Preserved,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.ApplicationShutdown));

            Task drain = coordinator.DrainAsync();
            Task<ArchiveTransferOwner?> nextAdmission = coordinator.BeginAsync();
            await Task.Delay(30);
            Assert.False(drain.IsCompleted);
            Assert.False(nextAdmission.IsCompleted);

            finish.TrySetResult();
            Assert.Equal(ArchiveTransferOutcome.Failed, (await render).Outcome);
            await drain.WaitAsync(TimeSpan.FromSeconds(2));
            ArchiveTransferOwner next = Assert.IsType<ArchiveTransferOwner>(
                await nextAdmission.WaitAsync(TimeSpan.FromSeconds(2)));
            await next.ReleaseAsync(ArchiveTransferReleaseReason.TargetRefused);
        }
        finally
        {
            finish.TrySetResult();
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidationRacingReplacementPreventsAnOwnerEscapingThatInvalidation()
    {
        using var temporary = new TemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ArchiveTransferCoordinator(
            async (_, _, _, _, _) =>
            {
                entered.TrySetResult();
                await finish.Task;
                return new ArchiveOperationResult(ArchiveOperationOutcome.Failed);
            },
            new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root")),
            renderDrainTimeout: TimeSpan.FromMilliseconds(50));
        try
        {
            ArchiveTransferOwner first = Assert.IsType<ArchiveTransferOwner>(
                await coordinator.BeginAsync());
            Task<ArchiveTransferRenderResult> render = first.RenderAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Task<ArchiveTransferOwner?> replacement = coordinator.BeginAsync();
            await WaitUntilAsync(() => first.ReleaseReason == ArchiveTransferReleaseReason.ClipboardReplaced);
            await coordinator.InvalidateAsync(ArchiveTransferReleaseReason.SelectionChanged);
            Assert.False(replacement.IsCompleted);

            finish.TrySetResult();
            Assert.Equal(ArchiveTransferOutcome.Failed, (await render).Outcome);
            Assert.Null(await replacement.WaitAsync(TimeSpan.FromSeconds(2)));
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(coordinator.HasOwnedWork);
        }
        finally
        {
            finish.TrySetResult();
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReleaseDuringReadyRevalidationCancelsPublicationAndCleansExactOutput()
    {
        using var temporary = new TemporaryDirectory();
        var revalidationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new ArchiveTransferStorage(
            Path.Combine(temporary.Path, "transfer-root"),
            beforeReadyRevalidation: async cancellationToken =>
            {
                revalidationEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, ownedStorage, progress, _, cancellationToken) =>
            {
                ArchiveTransferSelectionManifest selection = FlatSelection();
                progress.BindSelection(selection);
                progress.MarkStartAttempted();
                Directory.CreateDirectory(allocation.DestinationPath);
                await File.WriteAllTextAsync(
                    Path.Combine(allocation.DestinationPath, "selected.txt"),
                    "yes",
                    cancellationToken);
                progress.ConfirmPublished();
                ArchiveTransferManifest manifest = await ownedStorage.ReviewCompletedAsync(
                    allocation, selection, cancellationToken);
                progress.SetReviewedManifest(manifest);
                return new ArchiveOperationResult(ArchiveOperationOutcome.Completed);
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(
                await coordinator.BeginAsync());
            Task<ArchiveTransferRenderResult> render = owner.RenderAsync();
            await revalidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Task<ArchiveTransferCleanupOutcome> release = owner.ReleaseAsync(
                ArchiveTransferReleaseReason.SelectionChanged);

            Assert.Equal(
                ArchiveTransferOutcome.Cancelled,
                (await render.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Removed,
                await release.WaitAsync(TimeSpan.FromSeconds(2)));
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellationAfterDrainAndBeforeAdmissionCreatesNoOwner()
    {
        using var temporary = new TemporaryDirectory();
        var admissionEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int admissionCount = 0;
        var coordinator = new ArchiveTransferCoordinator(
            (_, _, _, _, _) => Task.FromResult(
                new ArchiveOperationResult(ArchiveOperationOutcome.Failed)),
            new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root")),
            beforeOwnerAdmission: async () =>
            {
                if (Interlocked.Increment(ref admissionCount) != 2) return;
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        try
        {
            ArchiveTransferOwner first = Assert.IsType<ArchiveTransferOwner>(
                await coordinator.BeginAsync());
            using var cancellation = new CancellationTokenSource();
            Task<ArchiveTransferOwner?> replacement = coordinator.BeginAsync(cancellation.Token);
            await admissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            releaseAdmission.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement);
            Assert.Equal(ArchiveTransferReleaseReason.ClipboardReplaced, first.ReleaseReason);
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(coordinator.HasOwnedWork);
        }
        finally
        {
            releaseAdmission.TrySetResult();
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task NormalReleaseCleanupExceptionIsPreservedAndOwnershipStillSettles()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(
            Path.Combine(temporary.Path, "transfer-root"),
            beforeCleanup: _ => throw new OverflowException("simulated cleanup failure"));
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, ownedStorage, progress, _, cancellationToken) =>
            {
                ArchiveTransferSelectionManifest selection = FlatSelection();
                progress.BindSelection(selection);
                progress.MarkStartAttempted();
                Directory.CreateDirectory(allocation.DestinationPath);
                await File.WriteAllTextAsync(
                    Path.Combine(allocation.DestinationPath, "selected.txt"),
                    "yes",
                    cancellationToken);
                progress.ConfirmPublished();
                ArchiveTransferManifest manifest = await ownedStorage.ReviewCompletedAsync(
                    allocation, selection, cancellationToken);
                progress.SetReviewedManifest(manifest);
                return new ArchiveOperationResult(ArchiveOperationOutcome.Completed);
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(
                await coordinator.BeginAsync());
            Assert.Equal(ArchiveTransferOutcome.Ready, (await owner.RenderAsync()).Outcome);

            Assert.Equal(
                ArchiveTransferCleanupOutcome.Preserved,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.DragCompleted));
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(coordinator.HasOwnedWork);
            Assert.Single(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TimedOutLateCleanupExceptionRemainsTrackedUntilOwnershipSettles()
    {
        using var temporary = new TemporaryDirectory();
        var extractionEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishExtraction = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new ArchiveTransferStorage(
            Path.Combine(temporary.Path, "transfer-root"),
            beforeCleanup: _ => throw new OverflowException("simulated late cleanup failure"));
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, ownedStorage, progress, _, cancellationToken) =>
            {
                ArchiveTransferSelectionManifest selection = FlatSelection();
                progress.BindSelection(selection);
                progress.MarkStartAttempted();
                Directory.CreateDirectory(allocation.DestinationPath);
                await File.WriteAllTextAsync(
                    Path.Combine(allocation.DestinationPath, "selected.txt"),
                    "yes",
                    cancellationToken);
                progress.ConfirmPublished();
                ArchiveTransferManifest manifest = await ownedStorage.ReviewCompletedAsync(
                    allocation, selection, cancellationToken);
                progress.SetReviewedManifest(manifest);
                extractionEntered.TrySetResult();
                await finishExtraction.Task;
                return new ArchiveOperationResult(ArchiveOperationOutcome.Completed);
            },
            storage,
            renderDrainTimeout: TimeSpan.FromMilliseconds(50));
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(
                await coordinator.BeginAsync());
            Task<ArchiveTransferRenderResult> render = owner.RenderAsync();
            await extractionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(
                ArchiveTransferCleanupOutcome.Preserved,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.ApplicationShutdown));
            Task drain = coordinator.DrainAsync();
            Assert.False(drain.IsCompleted);

            finishExtraction.TrySetResult();
            Assert.Equal(
                ArchiveTransferOutcome.Preserved,
                (await render.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
            await drain.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(coordinator.HasOwnedWork);
            Assert.Single(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            finishExtraction.TrySetResult();
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailedSafetyReviewRemainsPreservationRequiredAfterUnsafeConditionDisappears()
    {
        using var temporary = new TemporaryDirectory();
        var storage = new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root"));
        string? selected = null;
        var coordinator = new ArchiveTransferCoordinator(
            async (allocation, ownedStorage, progress, _, cancellationToken) =>
            {
                ArchiveTransferSelectionManifest selection = FlatSelection();
                progress.BindSelection(selection);
                progress.MarkStartAttempted();
                Directory.CreateDirectory(allocation.DestinationPath);
                selected = Path.Combine(allocation.DestinationPath, "selected.txt");
                await File.WriteAllTextAsync(selected, "yes", cancellationToken);
                string unsafeStream = selected + ":unsafe";
                await File.WriteAllTextAsync(unsafeStream, "observed", cancellationToken);
                progress.ConfirmPublished();
                await Assert.ThrowsAsync<InvalidDataException>(() => progress.ReviewAndBindAsync(
                    ownedStorage, allocation, cancellationToken));

                File.Delete(unsafeStream);
                return new ArchiveOperationResult(ArchiveOperationOutcome.UnsafeOutput);
            },
            storage);
        try
        {
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(
                await coordinator.BeginAsync());
            ArchiveTransferRenderResult result = await owner.RenderAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ArchiveTransferOutcome.Preserved, result.Outcome);
            Assert.NotNull(selected);
            Assert.Equal("yes", await File.ReadAllTextAsync(selected!));
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Preserved,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetException));
            Assert.Equal("yes", await File.ReadAllTextAsync(selected!));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    private static ArchiveTransferSelectionManifest NestedSelection() => new(
        [
            new("folder", IsDirectory: true, Length: 0),
            new("folder/file.txt", IsDirectory: false, Length: 4),
        ],
        ["folder/file.txt"]);

    private static ArchiveTransferSelectionManifest FlatSelection() => new(
        [new("selected.txt", IsDirectory: false, Length: 3)],
        ["selected.txt"]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainExplorer-ArchiveCoordinator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
