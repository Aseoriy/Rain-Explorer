using System.Windows.Threading;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class PhysicalArchiveExtractionServiceTests
{
    [Fact]
    public void PlanIsBoundedOrderedAndUsesDistinctAbsentSiblingFolders()
    {
        using var root = new TemporaryDirectory("plan path with spaces 雨");
        string first = root.CreateFile("same.zip");
        string second = root.CreateFile("same.7z");
        Directory.CreateDirectory(Path.Combine(root.Path, "same"));

        Assert.True(PhysicalArchiveExtractionService.TryCreatePlan(
            [second, first],
            out IReadOnlyList<PhysicalArchiveExtractionPlan> plan));

        Assert.Equal([second, first], plan.Select(item => item.SourcePath));
        Assert.Equal(Path.Combine(root.Path, "same (2)"), plan[0].DestinationPath);
        Assert.Equal(Path.Combine(root.Path, "same (3)"), plan[1].DestinationPath);
        Assert.All(plan, item =>
        {
            Assert.Equal(root.Path, Path.GetDirectoryName(item.DestinationPath), ignoreCase: true);
            Assert.False(File.Exists(item.DestinationPath));
            Assert.False(Directory.Exists(item.DestinationPath));
        });

        string[] excessive = Enumerable.Range(0,
                PhysicalArchiveExtractionService.MaximumArchiveCount + 1)
            .Select(index => root.CreateFile($"archive-{index}.zip"))
            .ToArray();
        Assert.False(PhysicalArchiveExtractionService.TryCreatePlan(excessive, out _));
        Assert.False(PhysicalArchiveExtractionService.TryCreatePlan(
            [root.Path],
            out _));
    }

    [Fact]
    public void ExtractToPlanUsesChosenParentAndDistinctCollisionFreeChildFolders()
    {
        using var sourceRoot = new TemporaryDirectory("extract-to sources");
        using var destinationRoot = new TemporaryDirectory("extract-to destination");
        string first = sourceRoot.CreateFile("same.zip");
        string second = sourceRoot.CreateFile("same.7z");
        Directory.CreateDirectory(Path.Combine(destinationRoot.Path, "same"));

        Assert.True(PhysicalArchiveExtractionService.TryCreatePlan(
            [first, second],
            destinationRoot.Path,
            out IReadOnlyList<PhysicalArchiveExtractionPlan> plan));

        Assert.Equal([first, second], plan.Select(item => item.SourcePath));
        Assert.Equal(Path.Combine(destinationRoot.Path, "same (2)"), plan[0].DestinationPath);
        Assert.Equal(Path.Combine(destinationRoot.Path, "same (3)"), plan[1].DestinationPath);
        Assert.All(plan, item => Assert.Equal(
            destinationRoot.Path,
            Path.GetDirectoryName(item.DestinationPath),
            ignoreCase: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void ExtractToPlanRefusesInvalidDestinationParents(string destinationParent)
    {
        using var root = new TemporaryDirectory("extract-to refusal");
        string source = root.CreateFile("archive.zip");

        Assert.False(PhysicalArchiveExtractionService.TryCreatePlan(
            [source], destinationParent, out _));
        Assert.False(PhysicalArchiveExtractionService.TryCreatePlan(
            [source], Path.Combine(root.Path, "missing"), out _));
    }

    [Fact]
    public async Task EverySelectedFileIsContentProbedInOrderWithoutExtensionAuthority()
    {
        using var root = new TemporaryDirectory("content detection");
        string extensionless = root.CreateFile("opaque-input");
        string misleading = root.CreateFile("not-an-archive.zip");
        string alternate = root.CreateFile("archive.data");
        var sessions = new Queue<FakeSession>(
        [
            new FakeSession(ArchiveLocationState.Ready, canExtract: true,
                ArchiveOperationOutcome.Completed),
            new FakeSession(ArchiveLocationState.Error, canExtract: false,
                ArchiveOperationOutcome.Failed),
            new FakeSession(ArchiveLocationState.Ready, canExtract: true,
                ArchiveOperationOutcome.CompletedWithWarnings),
        ]);
        var created = sessions.ToArray();
        await using var service = new PhysicalArchiveExtractionService(() => sessions.Dequeue());

        PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
            [extensionless, misleading, alternate],
            _ => throw new InvalidOperationException("No password should be requested."));

        Assert.Equal(3, result.RequestedCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(1, result.WarningCount);
        Assert.Equal(
            [extensionless, misleading, alternate],
            created.Select(session => Assert.Single(session.OpenedPaths)));
        Assert.Equal([1, 0, 1], created.Select(session => session.ExtractionCount));
        Assert.All(created, session => Assert.Equal(1, session.DisposeCount));
    }

    [Fact]
    public async Task SequentialPasswordPromptsStayOnOriginatingDispatcherAfterAsynchronousWork()
    {
        using var root = new TemporaryDirectory("dispatcher password sequence");
        string firstSource = root.CreateFile("first encrypted.zip");
        string secondSource = root.CreateFile("second encrypted.7z");
        var firstSession = new DispatcherProofSession();
        var secondSession = new DispatcherProofSession();
        var sessions = new Queue<IPhysicalArchiveExtractionSession>(
            [firstSession, secondSession]);
        using var dispatcherHost = new DispatcherTestHost();
        await using var service = new PhysicalArchiveExtractionService(
            () => sessions.Dequeue());
        var promptNames = new List<string>();
        var passwords = new List<char[]>();
        Dispatcher? originatingDispatcher = null;
        int dispatcherThreadId = 0;

        Task<PhysicalArchiveExtractionBatchResult> extraction = dispatcherHost.InvokeAsync(
            async () =>
            {
                originatingDispatcher = Dispatcher.CurrentDispatcher;
                dispatcherThreadId = Environment.CurrentManagedThreadId;
                return await service.ExtractAsync(
                    [firstSource, secondSource],
                    archiveName =>
                    {
                        Assert.True(originatingDispatcher.CheckAccess());
                        promptNames.Add(archiveName);
                        char[] password = $"password-{promptNames.Count}".ToCharArray();
                        passwords.Add(password);
                        return password;
                    });
            });

        await firstSession.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(() => firstSession.ReleaseOpen.TrySetResult());
        await firstSession.ExtractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(() => firstSession.ReleaseExtraction.TrySetResult());
        await secondSession.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(() => secondSession.ReleaseOpen.TrySetResult());
        await secondSession.ExtractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(() => secondSession.ReleaseExtraction.TrySetResult());

        PhysicalArchiveExtractionBatchResult result = await extraction.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(["first encrypted.zip", "second encrypted.7z"], promptNames);
        Assert.NotEqual(dispatcherThreadId, firstSession.OpenCompletionThreadId);
        Assert.NotEqual(dispatcherThreadId, firstSession.ExtractionCompletionThreadId);
        Assert.NotEqual(dispatcherThreadId, secondSession.OpenCompletionThreadId);
        Assert.NotEqual(dispatcherThreadId, secondSession.ExtractionCompletionThreadId);
        Assert.All(passwords, password =>
            Assert.All(password, character => Assert.Equal('\0', character)));
        Assert.Equal(1, firstSession.DisposeCount);
        Assert.Equal(1, secondSession.DisposeCount);
    }

    [Theory]
    [InlineData(ArchiveOperationOutcome.Completed)]
    [InlineData(ArchiveOperationOutcome.Failed)]
    public async Task EncryptedArchivePromptsOnceAndNeverRetriesARejectedPassword(
        ArchiveOperationOutcome terminalOutcome)
    {
        using var root = new TemporaryDirectory("password ownership");
        string source = root.CreateFile("encrypted.zip");
        var session = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: true,
            terminalOutcome,
            requiresPassword: true);
        await using var service = new PhysicalArchiveExtractionService(() => session);
        char[] password = "one-shot-password".ToCharArray();
        int promptCount = 0;

        PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
            [source],
            archiveName =>
            {
                promptCount++;
                Assert.Equal("encrypted.zip", archiveName);
                return password;
            });

        Assert.Equal(terminalOutcome, Assert.Single(result.Results).Outcome);
        Assert.Equal(1, promptCount);
        Assert.Equal(1, session.ExtractionCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.All(password, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task HeaderEncryptedArchiveUsesOnePasswordOpenWithoutASecondPrompt()
    {
        using var root = new TemporaryDirectory("header password ownership");
        string source = root.CreateFile("header-encrypted.7z");
        var session = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: true,
            ArchiveOperationOutcome.Completed,
            requiresPassword: true,
            requiresPasswordToOpen: true);
        await using var service = new PhysicalArchiveExtractionService(() => session);
        char[] password = "one-shot-header-password".ToCharArray();
        int promptCount = 0;

        PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
            [source],
            _ =>
            {
                promptCount++;
                return password;
            });

        Assert.Equal(ArchiveOperationOutcome.Completed, Assert.Single(result.Results).Outcome);
        Assert.Equal(1, promptCount);
        Assert.Equal(1, session.PasswordOpenCount);
        Assert.Equal(1, session.ExtractionCount);
        Assert.Null(session.LastExtractionPassword);
        Assert.All(password, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task DisposeCancelsHeaderPasswordOpenAndReportsTheActiveArchiveCancelled()
    {
        using var root = new TemporaryDirectory("header password shutdown");
        string source = root.CreateFile("header-encrypted.rar");
        var session = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: true,
            ArchiveOperationOutcome.Completed,
            requiresPassword: true,
            requiresPasswordToOpen: true,
            blockPasswordOpen: true);
        var service = new PhysicalArchiveExtractionService(() => session);
        char[] password = "shutdown-header-password".ToCharArray();

        Task<PhysicalArchiveExtractionBatchResult> extraction = service.ExtractAsync(
            [source],
            _ => password);
        await session.PasswordOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = service.DisposeAsync().AsTask();
        PhysicalArchiveExtractionBatchResult result = await extraction.WaitAsync(
            TimeSpan.FromSeconds(2));
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ArchiveOperationOutcome.Cancelled, Assert.Single(result.Results).Outcome);
        Assert.True(session.ObservedCancellation);
        Assert.Equal(1, session.PasswordOpenCount);
        Assert.Equal(0, session.ExtractionCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.All(password, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task CancelledPasswordPromptStopsLaterIndependentOperations()
    {
        using var root = new TemporaryDirectory("cancel prompt");
        string first = root.CreateFile("first.zip");
        string second = root.CreateFile("second.zip");
        var firstSession = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: true,
            ArchiveOperationOutcome.Completed,
            requiresPassword: true);
        var secondSession = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: true,
            ArchiveOperationOutcome.Completed);
        var sessions = new Queue<FakeSession>([firstSession, secondSession]);
        await using var service = new PhysicalArchiveExtractionService(() => sessions.Dequeue());

        PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
            [first, second],
            _ => null);

        Assert.True(result.WasCancelled);
        Assert.Single(result.Results);
        Assert.Equal(0, firstSession.ExtractionCount);
        Assert.Equal(1, firstSession.DisposeCount);
        Assert.Equal(0, secondSession.DisposeCount);
        Assert.Single(sessions);
    }

    [Fact]
    public async Task CapabilityRefusalClosesSessionAndContinuesSequentially()
    {
        using var root = new TemporaryDirectory("volume refusal");
        string multipart = root.CreateFile("multipart.zip");
        string ordinary = root.CreateFile("ordinary.tar");
        var listOnly = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: false,
            ArchiveOperationOutcome.Unsupported);
        var extractable = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: true,
            ArchiveOperationOutcome.Completed);
        var sessions = new Queue<FakeSession>([listOnly, extractable]);
        await using var service = new PhysicalArchiveExtractionService(() => sessions.Dequeue());

        PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
            [multipart, ordinary],
            _ => throw new InvalidOperationException());

        Assert.Equal(
            [ArchiveOperationOutcome.Unsupported, ArchiveOperationOutcome.Completed],
            result.Results.Select(item => item.Outcome));
        Assert.Equal(0, listOnly.ExtractionCount);
        Assert.Equal(1, extractable.ExtractionCount);
        Assert.Equal(1, listOnly.DisposeCount);
        Assert.Equal(1, extractable.DisposeCount);
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsTheActiveSessionExactlyOnce()
    {
        using var root = new TemporaryDirectory("shutdown drain");
        string source = root.CreateFile("large.zip");
        var session = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: true,
            ArchiveOperationOutcome.Completed,
            blockExtraction: true);
        var service = new PhysicalArchiveExtractionService(() => session);
        Task<PhysicalArchiveExtractionBatchResult> extraction = service.ExtractAsync(
            [source],
            _ => throw new InvalidOperationException());
        await session.ExtractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = service.DisposeAsync().AsTask();
        PhysicalArchiveExtractionBatchResult result = await extraction.WaitAsync(
            TimeSpan.FromSeconds(2));
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ArchiveOperationOutcome.Cancelled, Assert.Single(result.Results).Outcome);
        Assert.Equal(1, session.ExtractionCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.True(session.ObservedCancellation);
    }

    [Fact]
    public async Task ConcurrentBatchIsRefusedWithoutOpeningAnotherSession()
    {
        using var root = new TemporaryDirectory("busy bound");
        string source = root.CreateFile("busy.zip");
        var session = new FakeSession(
            ArchiveLocationState.Ready,
            canExtract: true,
            ArchiveOperationOutcome.Completed,
            blockExtraction: true);
        await using var service = new PhysicalArchiveExtractionService(() => session);
        Task<PhysicalArchiveExtractionBatchResult> first = service.ExtractAsync(
            [source],
            _ => throw new InvalidOperationException());
        await session.ExtractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        PhysicalArchiveExtractionBatchResult busy = await service.ExtractAsync(
            [source],
            _ => throw new InvalidOperationException());
        Assert.Equal(ArchiveOperationOutcome.Busy, Assert.Single(busy.Results).Outcome);

        session.ReleaseExtraction.TrySetResult();
        Assert.Equal(ArchiveOperationOutcome.Completed,
            Assert.Single((await first).Results).Outcome);
        Assert.Equal(1, session.ExtractionCount);
        Assert.Equal(1, session.DisposeCount);
    }

    private sealed class DispatcherProofSession : IPhysicalArchiveExtractionSession
    {
        public ArchiveLocationState State { get; private set; }
        public bool RequiresPassword { get; private set; }
        public bool CanExtractEntireArchive { get; private set; }
        public int OpenCompletionThreadId { get; private set; }
        public int ExtractionCompletionThreadId { get; private set; }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource OpenStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOpen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExtractionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExtraction { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OpenAsync(string sourcePath, CancellationToken cancellationToken)
        {
            OpenStarted.TrySetResult();
            await ReleaseOpen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            OpenCompletionThreadId = Environment.CurrentManagedThreadId;
            State = ArchiveLocationState.Ready;
            RequiresPassword = true;
            CanExtractEntireArchive = true;
        }

        public Task OpenWithPasswordAsync(
            string sourcePath,
            char[] password,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The dispatcher proof uses entry-encrypted, not header-encrypted, archives.");

        public async Task<ArchiveOperationResult> ExtractEntireArchiveAsync(
            string destinationPath,
            char[]? password,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(password);
            ExtractionStarted.TrySetResult();
            try
            {
                await ReleaseExtraction.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                ExtractionCompletionThreadId = Environment.CurrentManagedThreadId;
                return new ArchiveOperationResult(ArchiveOperationOutcome.Completed);
            }
            finally
            {
                Array.Clear(password);
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession(
        ArchiveLocationState state,
        bool canExtract,
        ArchiveOperationOutcome extractionOutcome,
        bool requiresPassword = false,
        bool blockExtraction = false,
        bool requiresPasswordToOpen = false,
        bool blockPasswordOpen = false) : IPhysicalArchiveExtractionSession
    {
        public ArchiveLocationState State { get; private set; }
        public bool RequiresPassword { get; private set; }
        public bool CanExtractEntireArchive { get; private set; }
        public int ExtractionCount { get; private set; }
        public int PasswordOpenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ObservedCancellation { get; private set; }
        public char[]? LastExtractionPassword { get; private set; }
        public List<string> OpenedPaths { get; } = [];
        public TaskCompletionSource ExtractionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExtraction { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PasswordOpenStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePasswordOpen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OpenAsync(string sourcePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedPaths.Add(sourcePath);
            State = requiresPasswordToOpen ? ArchiveLocationState.PasswordRequired : state;
            RequiresPassword = requiresPassword;
            CanExtractEntireArchive = !requiresPasswordToOpen && canExtract;
            return Task.CompletedTask;
        }

        public async Task OpenWithPasswordAsync(
            string sourcePath,
            char[] password,
            CancellationToken cancellationToken)
        {
            PasswordOpenCount++;
            PasswordOpenStarted.TrySetResult();
            try
            {
                if (blockPasswordOpen)
                    await ReleasePasswordOpen.Task.WaitAsync(cancellationToken);
                else
                    cancellationToken.ThrowIfCancellationRequested();
                OpenedPaths.Add(sourcePath);
                State = state;
                RequiresPassword = requiresPassword;
                CanExtractEntireArchive = canExtract;
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
            finally
            {
                Array.Clear(password);
            }
        }

        public async Task<ArchiveOperationResult> ExtractEntireArchiveAsync(
            string destinationPath,
            char[]? password,
            CancellationToken cancellationToken)
        {
            ExtractionCount++;
            LastExtractionPassword = password;
            ExtractionStarted.TrySetResult();
            try
            {
                if (blockExtraction)
                    await ReleaseExtraction.Task.WaitAsync(cancellationToken);
                else
                    cancellationToken.ThrowIfCancellationRequested();
                return new ArchiveOperationResult(extractionOutcome);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
            finally
            {
                if (password is not null) Array.Clear(password);
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string label)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RainExplorer-M6Exit-{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class DispatcherTestHost : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private Dispatcher? _dispatcher;

        public DispatcherTestHost()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = _dispatcher!.BeginInvoke(async () =>
            {
                try
                {
                    completion.TrySetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return completion.Task;
        }

        public void Dispose()
        {
            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(
                _thread.Join(TimeSpan.FromSeconds(2)),
                "The dispatcher proof thread did not stop.");
            _ready.Dispose();
        }

        private void Run()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        }
    }
}
