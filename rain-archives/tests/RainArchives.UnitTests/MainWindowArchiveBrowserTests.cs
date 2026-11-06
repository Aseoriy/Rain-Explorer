using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class MainWindowArchiveBrowserTests
{
    [TestMethod]
    public async Task PagedOpenRestoresFolderSearchAndSelectionOnlyAcrossExactSameSourceReopen()
    {
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new BrowserArchiveClient(source, rootEntryCount: 4_100);
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogs(),
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.OpenArchivePathAsync(source);
        Assert.IsTrue(viewModel.HasArchive);
        Assert.HasCount(ArchiveBrowserViewModel.VisiblePageSize, viewModel.Entries);
        Assert.AreEqual(4_101, viewModel.Browser.TotalResults);
        var folder = viewModel.Entries.Single(entry => entry.Name == "folder");
        viewModel.NavigateTo(folder.EntryId);
        viewModel.SearchText = "雨";
        Assert.AreEqual("雨.txt", viewModel.Browser.SelectedEntry?.Name);

        await viewModel.OpenArchivePathAsync(source);

        Assert.AreEqual("folder", viewModel.Browser.CurrentLocationName);
        Assert.AreEqual("雨", viewModel.SearchText);
        Assert.AreEqual("雨.txt", viewModel.Browser.SelectedEntry?.Name);
        CollectionAssert.AreEqual(
            new[] { 0, ProtocolConstants.MaximumPageSize, 0, ProtocolConstants.MaximumPageSize },
            client.ListOffsets.ToArray());
        Assert.AreEqual(1, client.ClosedSessions.Count);
        Assert.IsFalse(viewModel.ArchiveDetails.Contains("provider-controlled", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.StatusText.Contains("warning-controlled", StringComparison.Ordinal));
        StringAssert.Contains(viewModel.StatusText, "1 warning");
    }

    [TestMethod]
    public async Task SourceChangedTerminalMakesBrowserStaleWithoutReopenOrMessageDisclosure()
    {
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new BrowserArchiveClient(source)
        {
            IntegrityTerminal = new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.Failed,
                Error: new ArchiveError(
                    "archive.source_changed",
                    ArchiveErrorCategory.SourceChanged,
                    "C:\\secret\\worker-temp\\payload.zip\u202E")),
        };
        var dialogs = new RecordingDialogs();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(source);

        viewModel.TestCommand.Execute(null);
        await client.TerminalRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.AreEqual(ArchiveBrowserState.Stale, viewModel.Browser.State);
        Assert.IsFalse(viewModel.CanExtract);
        Assert.IsFalse(viewModel.CanTest);
        Assert.IsFalse(viewModel.CanRebuildCopy);
        Assert.AreEqual(1, client.OpenCalls);
        Assert.AreEqual(1, client.IntegrityStarts);
        Assert.IsFalse(viewModel.StatusText.Contains("worker-temp", StringComparison.Ordinal));
        Assert.IsFalse(dialogs.ErrorMessage?.Contains("worker-temp", StringComparison.Ordinal) == true);
        StringAssert.Contains(viewModel.StatusText, "source changed");
    }

    [TestMethod]
    public async Task MalformedCrossPageCountClosesNewSessionAndShowsOnlyFixedErrorState()
    {
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new BrowserArchiveClient(source)
        {
            MalformedTotalCount = true,
        };
        var dialogs = new RecordingDialogs();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.OpenArchivePathAsync(source);

        Assert.IsFalse(viewModel.HasArchive);
        Assert.AreEqual(ArchiveBrowserState.Error, viewModel.Browser.State);
        Assert.AreEqual(1, client.ClosedSessions.Count);
        Assert.AreEqual("Rain Archives could not complete the request safely.", viewModel.StatusText);
        Assert.AreEqual(viewModel.StatusText, dialogs.ErrorMessage);
        Assert.IsFalse(viewModel.Browser.CanBrowse);
    }

    [TestMethod]
    public async Task SessionWithoutListCapabilityIsTruthfullyUnsupportedAndNeverLists()
    {
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var client = new BrowserArchiveClient(source)
        {
            Capabilities = ArchiveCapability.None,
        };
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogs(),
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.OpenArchivePathAsync(source);

        Assert.IsTrue(viewModel.HasArchive);
        Assert.AreEqual(ArchiveBrowserState.Unsupported, viewModel.Browser.State);
        Assert.AreEqual(0, client.ListOffsets.Count);
        Assert.IsFalse(viewModel.CanExtract);
        Assert.IsFalse(viewModel.CanTest);
        Assert.IsFalse(viewModel.Browser.CanSearch);
    }

    [TestMethod]
    public async Task FailedSecondOpenPreservesTheExistingAuthoritativeSessionAndBrowserState()
    {
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var second = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.rar");
        var client = new BrowserArchiveClient(source)
        {
            FailedPath = second,
        };
        var dialogs = new RecordingDialogs();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(source);
        var selectedBefore = viewModel.Browser.SelectedPersistentKey;

        await viewModel.OpenArchivePathAsync(second);

        Assert.IsTrue(viewModel.HasArchive);
        Assert.AreEqual(ArchiveBrowserState.Ready, viewModel.Browser.State);
        Assert.AreEqual(Path.GetFileName(source), viewModel.ArchiveTitle);
        Assert.AreEqual(selectedBefore, viewModel.Browser.SelectedPersistentKey);
        Assert.AreEqual(1, client.ClosedSessions.Count, "Only the failed new session should be closed.");
        Assert.IsFalse(viewModel.StatusText.Contains("provider-message", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NonAuthoritativePriorSessionCloseDisablesTheOldBrowserAndCleansTheNewSession()
    {
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new BrowserArchiveClient(source);
        var dialogs = new RecordingDialogs();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(source);
        client.RefuseNextClose = true;

        await viewModel.OpenArchivePathAsync(source);

        Assert.IsFalse(viewModel.HasArchive);
        Assert.AreEqual(ArchiveBrowserState.Error, viewModel.Browser.State);
        Assert.IsFalse(viewModel.Browser.CanBrowse);
        Assert.IsFalse(viewModel.CanExtract);
        Assert.IsFalse(viewModel.CanTest);
        Assert.AreEqual(2, client.CloseAttempts);
        Assert.HasCount(1, client.ClosedSessions, "The newly opened session must be closed after refusal.");
        Assert.AreEqual("Rain Archives could not complete the request safely.", dialogs.ErrorMessage);
    }

    [TestMethod]
    public async Task ContextCommandsShareCurrentSelectionAuthorityAndInvalidateOnStaleSource()
    {
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new BrowserArchiveClient(source)
        {
            IntegrityTerminal = new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.Failed,
                Error: new ArchiveError(
                    "archive.source_changed",
                    ArchiveErrorCategory.SourceChanged,
                    "provider-controlled path")),
        };
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogs(),
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(source);

        var folder = viewModel.Entries.Single(entry => entry.IsDirectory);
        viewModel.Browser.SelectedEntry = folder;
        viewModel.UpdateContextSelection([folder]);
        Assert.IsTrue(viewModel.NavigateSelectionCommand.CanExecute(null));
        Assert.IsFalse(viewModel.PreviewSelectionCommand.CanExecute(null));
        Assert.IsTrue(viewModel.ShowPropertiesCommand.CanExecute(null));
        Assert.IsTrue(viewModel.ExtractSelectionCommand.CanExecute(null));
        Assert.IsTrue(viewModel.TestCommand.CanExecute(null));

        var propertiesFocusRequests = 0;
        viewModel.PropertiesFocusRequested += (_, _) => propertiesFocusRequests++;
        viewModel.InspectorSelectedTabIndex = 1;
        viewModel.ShowPropertiesCommand.Execute(null);
        await Task.Yield();
        Assert.AreEqual(0, viewModel.InspectorSelectedTabIndex);
        Assert.IsTrue(viewModel.IsInspectorVisible);
        Assert.AreEqual(1, propertiesFocusRequests);

        viewModel.NavigateSelectionCommand.Execute(null);
        await Task.Yield();
        Assert.AreEqual(folder.EntryId, viewModel.CurrentFolderId);
        var file = viewModel.Entries.Single();
        viewModel.Browser.SelectedEntry = file;
        viewModel.UpdateContextSelection([file]);
        Assert.IsFalse(viewModel.NavigateSelectionCommand.CanExecute(null));
        Assert.IsTrue(viewModel.PreviewSelectionCommand.CanExecute(null));

        viewModel.TestCommand.Execute(null);
        await client.TerminalRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.AreEqual(ArchiveBrowserState.Stale, viewModel.Browser.State);
        Assert.IsFalse(viewModel.IsInspectorVisible);
        Assert.AreEqual(ArchiveCommandContext.Unavailable, viewModel.CommandContext);
        Assert.IsFalse(viewModel.NavigateSelectionCommand.CanExecute(null));
        Assert.IsFalse(viewModel.PreviewSelectionCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ShowPropertiesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ExtractSelectionCommand.CanExecute(null));
        Assert.IsFalse(viewModel.TestCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ContextExtractionUsesOnlyCurrentVisibleSelectionInBrowserOrder()
    {
        var source = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new BrowserArchiveClient(source, rootEntryCount: 5);
        var dialogs = new RecordingDialogs
        {
            ExtractionDestination = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}"),
        };
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(source);
        var selected = viewModel.Entries
            .Where(entry => !entry.IsDirectory)
            .Skip(1)
            .Take(2)
            .Reverse()
            .ToArray();
        viewModel.Browser.SelectedEntry = selected[0];
        viewModel.UpdateContextSelection([selected[0], selected[1], selected[0]]);

        Assert.AreEqual(2, viewModel.CommandContext.SelectionCount);
        Assert.IsTrue(viewModel.ExtractSelectionCommand.CanExecute(null));
        viewModel.ExtractSelectionCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy && client.ExtractionStarts == 1);

        CollectionAssert.AreEqual(
            viewModel.Entries
                .Where(entry => selected.Any(item => item.EntryId == entry.EntryId))
                .Select(entry => entry.EntryId)
                .ToArray(),
            client.LastExtractionEntryIds.ToArray());
        Assert.IsTrue(dialogs.LastExtractionWasSelectedOnly);

        viewModel.SearchText = "item-00000";
        Assert.AreEqual(1, viewModel.CommandContext.SelectionCount);
        Assert.AreEqual(viewModel.Browser.SelectedEntry?.EntryId, viewModel.CommandContext.PrimaryEntryId);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("The view model did not settle before the timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class BrowserArchiveClient : ArchiveClientTestDouble
    {
        private static readonly DateTimeOffset FingerprintTime =
            new(2026, 8, 30, 13, 0, 0, TimeSpan.Zero);
        private readonly string _source;
        private readonly int _rootEntryCount;
        private readonly ConcurrentDictionary<ArchiveSessionId, IReadOnlyList<ArchiveEntryDescriptor>> _entries = new();
        private readonly HashSet<ArchiveSessionId> _failedListingSessions = [];

        public BrowserArchiveClient(string source, int rootEntryCount = 3)
        {
            _source = Path.GetFullPath(source);
            _rootEntryCount = rootEntryCount;
        }

        public List<int> ListOffsets { get; } = [];
        public List<ArchiveSessionId> ClosedSessions { get; } = [];
        public TaskCompletionSource TerminalRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenCalls { get; private set; }
        public int IntegrityStarts { get; private set; }
        public int ExtractionStarts { get; private set; }
        public int CloseAttempts { get; private set; }
        public IReadOnlyList<EntryId> LastExtractionEntryIds { get; private set; } = [];
        public bool RefuseNextClose { get; set; }
        public bool MalformedTotalCount { get; init; }
        public string? FailedPath { get; init; }
        public ArchiveCapability Capabilities { get; init; } = ArchiveCapability.List
            | ArchiveCapability.OpenEntryStream
            | ArchiveCapability.Test
            | ArchiveCapability.Extract;
        public OperationEvent? IntegrityTerminal { get; init; }

        public override bool SupportsFeature(string feature) =>
            feature == ProtocolFeatures.PersistentEntryKeys;

        public override Task<OpenArchiveResponse> OpenArchiveAsync(
            string sourcePath,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCalls++;
            var fullPath = Path.GetFullPath(sourcePath);
            var sessionId = ArchiveSessionId.New();
            if (string.Equals(fullPath, FailedPath, StringComparison.OrdinalIgnoreCase))
            {
                var failedEntries = BuildEntries(1);
                _entries[sessionId] = failedEntries;
                _failedListingSessions.Add(sessionId);
                return Task.FromResult(new OpenArchiveResponse(new ArchiveDescriptor(
                    sessionId,
                    "zip",
                    "provider-controlled",
                    Capabilities,
                    new ArchiveFingerprint(4096, FingerprintTime, "volume:file", new string('A', 64)),
                    failedEntries.Count,
                    IsSolid: false,
                    HasEncryptedEntries: false,
                    IsMultipart: false,
                    IsReadOnly: true,
                    Warnings: [],
                    ArchiveVolumeSetKind.Single)));
            }

            Assert.AreEqual(_source, fullPath, ignoreCase: true);
            var entries = BuildEntries(_rootEntryCount);
            _entries[sessionId] = entries;
            return Task.FromResult(new OpenArchiveResponse(new ArchiveDescriptor(
                sessionId,
                "zip",
                "provider-controlled",
                Capabilities,
                new ArchiveFingerprint(4096, FingerprintTime, "volume:file", new string('A', 64)),
                entries.Count,
                IsSolid: false,
                HasEncryptedEntries: false,
                IsMultipart: false,
                IsReadOnly: true,
                Warnings: [new ArchiveWarning("warning.code", "warning-controlled C:\\temp")],
                ArchiveVolumeSetKind.Single)));
        }

        public override Task<ListEntriesResponse> ListEntriesAsync(
            ArchiveSessionId sessionId,
            int offset = 0,
            int limit = 512,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListOffsets.Add(offset);
            if (_failedListingSessions.Contains(sessionId))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "provider.list_failed",
                    ArchiveErrorCategory.ProviderFailure,
                    "provider-message C:\\temporary\\archive.bin"));
            }

            var entries = _entries[sessionId];
            var page = entries.Skip(offset).Take(limit).ToArray();
            int? next = offset + page.Length < entries.Count ? offset + page.Length : null;
            return Task.FromResult(new ListEntriesResponse(
                page,
                offset,
                MalformedTotalCount ? entries.Count - 1 : entries.Count,
                next));
        }

        public override Task<CloseArchiveResponse> CloseArchiveAsync(
            ArchiveSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseAttempts++;
            if (RefuseNextClose)
            {
                RefuseNextClose = false;
                return Task.FromResult(new CloseArchiveResponse(false));
            }

            ClosedSessions.Add(sessionId);
            _failedListingSessions.Remove(sessionId);
            _entries.TryRemove(sessionId, out _);
            return Task.FromResult(new CloseArchiveResponse(true));
        }

        public override Task<StartOperationResponse> StartIntegrityTestAsync(
            ArchiveSessionId sessionId,
            IReadOnlyList<EntryId> entryIds,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntegrityStarts++;
            var terminal = IntegrityTerminal ?? new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.Completed);
            return Task.FromResult(new StartOperationResponse(terminal.OperationId));
        }

        public override Task<StartOperationResponse> StartExtractionAsync(
            ArchiveSessionId sessionId,
            IReadOnlyList<EntryId> entryIds,
            string destinationPath,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractionStarts++;
            LastExtractionEntryIds = entryIds.ToArray();
            return Task.FromResult(new StartOperationResponse(OperationId.New()));
        }

        public override Task<OperationEvent> WaitForTerminalAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var terminal = IntegrityTerminal ?? new OperationEvent(
                operationId,
                1,
                OperationEventKind.Terminal,
                OperationPhase.Completed);
            Assert.AreEqual(operationId, terminal.OperationId);
            TerminalRequested.TrySetResult();
            return Task.FromResult(terminal);
        }

        private static IReadOnlyList<ArchiveEntryDescriptor> BuildEntries(int rootEntryCount)
        {
            var entries = new List<ArchiveEntryDescriptor>(rootEntryCount + 2);
            var folderId = EntryId.New();
            entries.Add(Entry(folderId, null, "folder", "folder", ArchiveEntryKind.Directory, 0));
            for (var index = 0; index < rootEntryCount; index++)
            {
                var name = $"item-{index:D5}.txt";
                entries.Add(Entry(EntryId.New(), null, name, name, ArchiveEntryKind.File, index + 1));
            }

            entries.Add(Entry(
                EntryId.New(),
                folderId,
                "folder/雨.txt",
                "雨.txt",
                ArchiveEntryKind.File,
                rootEntryCount + 1));
            return entries;
        }

        private static ArchiveEntryDescriptor Entry(
            EntryId entryId,
            EntryId? parentId,
            string path,
            string name,
            ArchiveEntryKind kind,
            int ordinal) =>
            new(
                entryId,
                parentId,
                ordinal,
                path,
                name,
                kind,
                kind == ArchiveEntryKind.Directory ? null : 4,
                kind == ArchiveEntryKind.Directory ? null : 4,
                FingerprintTime,
                null,
                kind == ArchiveEntryKind.Directory ? null : "Store",
                null,
                false,
                false,
                null,
                EntrySafetyStatus.Safe,
                null,
                path,
                0);
    }

    private sealed class RecordingDialogs : IAppDialogService
    {
        public string? ErrorMessage { get; private set; }
        public string? ExtractionDestination { get; init; }
        public bool LastExtractionWasSelectedOnly { get; private set; }

        public string? ChooseArchive() => null;
        public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation) => null;
        public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly)
        {
            LastExtractionWasSelectedOnly = selectedEntriesOnly;
            return ExtractionDestination;
        }
        public Task<char[]?> RequestPasswordAsync(string archiveName, CancellationToken cancellationToken) =>
            Task.FromResult<char[]?>(null);
        public Task ShowArchiveRecoveryAsync(IArchiveClient client, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public void ShowError(string title, string message) => ErrorMessage = message;
        public void ShowInformation(string title, string message) { }
    }
}
