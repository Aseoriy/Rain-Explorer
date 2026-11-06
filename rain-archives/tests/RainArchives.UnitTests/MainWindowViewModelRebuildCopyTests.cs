using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class MainWindowViewModelRebuildCopyTests
{
    private const string UnavailableMessage =
        "This action is unavailable for the connected archive session.";
    private const string OperationWarningMessage =
        "The operation completed with a warning. Review the Activity Center for its typed lifecycle state.";

    [TestMethod]
    public async Task RebuildRefusesNegativeTypedCapabilityBeforeStartingOperation()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new RebuildArchiveClient(
            sourcePath,
            destinationPath,
            new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.Completed))
        {
            ZipWriteCapabilities = new GetZipWriteCapabilitiesResponse(
                CanCreate: true,
                CanRebuild: false,
                SupportsAdd: true,
                SupportsReplace: true,
                SupportsDelete: true,
                SupportsFileRename: true,
                SupportsTreeEdit: true,
                SupportsMetadata: true,
                CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
                MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
                MaximumSourceBytes: 64L * 1024 * 1024 * 1024,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false),
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(sourcePath);

        await viewModel.RebuildArchiveAsync(Selection(destinationPath));

        Assert.AreEqual(0, client.RebuildCalls);
        Assert.AreEqual(UnavailableMessage, dialogs.ErrorMessage);
    }

    [TestMethod]
    public async Task CompletedRebuildForwardsClosedPlanAndReopensOnlyTheNewCopy()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var operationId = OperationId.New();
        var warning = new ArchiveWarning(
            "zip.rebuild_metadata_limited",
            "ZIP metadata outside timestamps and representable attributes was not preserved.");
        var client = new RebuildArchiveClient(
            sourcePath,
            destinationPath,
            new OperationEvent(
                operationId,
                1,
                OperationEventKind.Terminal,
                OperationPhase.CompletedWithWarnings,
                Warning: warning));
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(sourcePath);

        Assert.IsTrue(viewModel.CanRebuildCopy);
        var deletes = new[] { new ArchiveEntryKey("remove.txt", 0) };
        var files = new[]
        {
            new ArchiveRebuildCopyFile(
                Path.Combine(Path.GetTempPath(), "replacement.txt"),
                "keep.txt"),
            new ArchiveRebuildCopyFile(
                Path.Combine(Path.GetTempPath(), "addition.txt"),
                "new.txt"),
        };

        await viewModel.RebuildArchiveAsync(new ArchiveRebuildCopySelection(
            destinationPath,
            deletes,
            files,
            ArchiveCompressionPreset.Maximum));

        Assert.AreEqual(1, client.RebuildCalls);
        Assert.AreEqual(client.SourceSessionId, client.RebuildSessionId);
        Assert.AreEqual(destinationPath, client.RebuildDestinationPath);
        Assert.AreEqual(ArchiveCompressionPreset.Maximum, client.RebuildPreset);
        CollectionAssert.AreEqual(deletes, client.RebuildDeletes.ToArray());
        CollectionAssert.AreEqual(files, client.RebuildFiles.ToArray());
        CollectionAssert.AreEqual(
            new[] { sourcePath, destinationPath },
            client.OpenedPaths.ToArray());
        CollectionAssert.AreEqual(
            new[] { client.SourceSessionId },
            client.ClosedSessions.ToArray());
        Assert.IsTrue(viewModel.HasArchive);
        Assert.AreEqual(Path.GetFileName(destinationPath), viewModel.ArchiveTitle);
        Assert.AreEqual(OperationPhase.CompletedWithWarnings, viewModel.ActiveActivity.Phase);
        Assert.AreEqual(
            "ZIP rebuild copy completed and opened. Warnings were reported.",
            viewModel.StatusText);
        Assert.AreEqual("Completed with warnings", dialogs.InformationTitle);
        Assert.AreEqual(OperationWarningMessage, dialogs.InformationMessage);
        Assert.IsFalse(
            (dialogs.InformationMessage ?? string.Empty).Contains(
                warning.Message,
                StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(
            new[] { "keep.txt", "new.txt" },
            viewModel.Entries.Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public async Task FailedRebuildKeepsTheOriginalSessionOpenAndDoesNotOpenTheDestination()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var error = new ArchiveError(
            "filesystem.destination_exists",
            ArchiveErrorCategory.DestinationConflict,
            "The destination already exists.");
        var client = new RebuildArchiveClient(
            sourcePath,
            destinationPath,
            new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.Failed,
                Error: error));
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(sourcePath);

        await viewModel.RebuildArchiveAsync(Selection(destinationPath));

        Assert.AreEqual(1, client.RebuildCalls);
        CollectionAssert.AreEqual(new[] { sourcePath }, client.OpenedPaths.ToArray());
        Assert.HasCount(0, client.ClosedSessions);
        Assert.AreEqual(Path.GetFileName(sourcePath), viewModel.ArchiveTitle);
        Assert.AreEqual(OperationPhase.Failed, viewModel.ActiveActivity.Phase);
        Assert.AreEqual(error.Message, viewModel.StatusText);
        Assert.AreEqual("Archive operation failed", dialogs.ErrorTitle);
        Assert.AreEqual(error.Message, dialogs.ErrorMessage);
    }

    [TestMethod]
    public async Task MixedRenameDeleteAddReplaceSelectionUsesRenameRouteAndKeepsCopyRouteUnused()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new RebuildArchiveClient(
            sourcePath,
            destinationPath,
            new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.Completed));
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(sourcePath);

        var rename = new ArchiveRebuildRename(
            new ArchiveEntryKey("keep.txt", 0),
            "renamed.txt");
        var deletes = new[] { new ArchiveEntryKey("delete.txt", 0) };
        var files = new[]
        {
            new ArchiveRebuildCopyFile(
                Path.Combine(Path.GetTempPath(), "replacement.txt"),
                "replace.txt"),
            new ArchiveRebuildCopyFile(
                Path.Combine(Path.GetTempPath(), "addition.txt"),
                "added.txt"),
        };
        await viewModel.RebuildArchiveAsync(new ArchiveRebuildCopySelection(
            destinationPath,
            deletes,
            files,
            ArchiveCompressionPreset.Balanced,
            [rename]));

        Assert.AreEqual(0, client.RebuildCalls);
        Assert.AreEqual(1, client.RenameCalls);
        CollectionAssert.AreEqual(deletes, client.RebuildDeletes.ToArray());
        CollectionAssert.AreEqual(files, client.RebuildFiles.ToArray());
        CollectionAssert.AreEqual(new[] { rename }, client.RebuildRenames.ToArray());
        Assert.AreEqual("ZIP rebuild rename completed and opened.", viewModel.StatusText);
    }

    [TestMethod]
    public async Task MixedFolderPlanUsesTreeRouteAndForwardsWorkerRevisionEvidence()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new RebuildArchiveClient(
            sourcePath,
            destinationPath,
            new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.CompletedWithWarnings,
                Warning: new ArchiveWarning("zip.rebuild_metadata_limited", "limited")));
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogService(),
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(sourcePath);
        var folder = new ArchiveCreationDirectory(
            Path.Combine(Path.GetTempPath(), "selected-folder"),
            "Imported");
        var action = new ArchiveRebuildTreeAction(
            "docs",
            new string('b', 64),
            ArchiveRebuildTreeActionKind.Move,
            "MovedDocs");

        await viewModel.RebuildArchiveAsync(new ArchiveRebuildCopySelection(
            destinationPath,
            [],
            [],
            ArchiveCompressionPreset.Fast,
            [],
            [folder],
            [action]));

        Assert.AreEqual(0, client.RebuildCalls);
        Assert.AreEqual(0, client.RenameCalls);
        Assert.AreEqual(1, client.TreeCalls);
        CollectionAssert.AreEqual(new[] { folder }, client.RebuildDirectories.ToArray());
        CollectionAssert.AreEqual(new[] { action }, client.RebuildTreeActions.ToArray());
        Assert.AreEqual("ZIP tree edit completed and opened. Warnings were reported.", viewModel.StatusText);
    }

    [TestMethod]
    public async Task SharedCancellationCancelsTheRebuildAndNeverReopensTheDestination()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new RebuildArchiveClient(
            sourcePath,
            destinationPath,
            new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.Cancelled),
            deferTerminalUntilCancellation: true);
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(sourcePath);

        var rebuild = viewModel.RebuildArchiveAsync(Selection(destinationPath));
        await client.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(viewModel.ActiveActivity.CanCancel);
        await WaitForSnapshotCancellationAsync(viewModel);

        await viewModel.CancelCurrentOperationAsync();
        await rebuild.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, client.CancelCalls);
        CollectionAssert.AreEqual(new[] { sourcePath }, client.OpenedPaths.ToArray());
        Assert.AreEqual(Path.GetFileName(sourcePath), viewModel.ArchiveTitle);
        Assert.AreEqual(OperationPhase.Cancelled, viewModel.ActiveActivity.Phase);
        Assert.AreEqual("Operation cancelled.", viewModel.StatusText);
    }

    private static async Task WaitForSnapshotCancellationAsync(MainWindowViewModel viewModel)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!viewModel.CanCancelCurrentOperationFromSnapshot)
        {
            if (DateTime.UtcNow >= timeout)
            {
                Assert.Fail("The bounded activity snapshot did not authorize cancellation.");
            }

            await Task.Delay(10);
        }
    }

    [TestMethod]
    [DataRow(false, true, "did not negotiate")]
    [DataRow(true, false, "not eligible")]
    public async Task RebuildIsGatedByFeatureAndEligibleSession(
        bool supportsFeature,
        bool eligibleSession,
        string expectedHelpMessage)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var client = new RebuildArchiveClient(
            sourcePath,
            destinationPath,
            new OperationEvent(
                OperationId.New(),
                1,
                OperationEventKind.Terminal,
                OperationPhase.Completed))
        {
            SupportsRebuildFeature = supportsFeature,
            SourceCapabilities = eligibleSession
                ? RebuildArchiveClient.EligibleCapabilities
                : ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        await viewModel.OpenArchivePathAsync(sourcePath);

        Assert.IsFalse(viewModel.CanRebuildCopy);
        StringAssert.Contains(viewModel.RebuildCopyHelpText, expectedHelpMessage);
        await viewModel.RebuildArchiveAsync(Selection(destinationPath));

        Assert.AreEqual(0, client.RebuildCalls);
        CollectionAssert.AreEqual(new[] { sourcePath }, client.OpenedPaths.ToArray());
        Assert.AreEqual(UnavailableMessage, viewModel.StatusText);
        Assert.AreEqual("Rain Archives", dialogs.ErrorTitle);
    }

    private static ArchiveRebuildCopySelection Selection(string destinationPath) =>
        new(
            destinationPath,
            [new ArchiveEntryKey("remove.txt", 0)],
            [],
            ArchiveCompressionPreset.Balanced);

    private static ArchiveEntryDescriptor Entry(string path, int providerOrdinal) =>
        new(
            EntryId.New(),
            null,
            providerOrdinal,
            path,
            path,
            ArchiveEntryKind.File,
            16,
            16,
            null,
            null,
            "Store",
            null,
            false,
            false,
            null,
            EntrySafetyStatus.Safe,
            null,
            path,
            0);

    private sealed class RebuildArchiveClient : ArchiveClientTestDouble
    {
        public const ArchiveCapability EligibleCapabilities =
            ArchiveCapability.List
            | ArchiveCapability.Test
            | ArchiveCapability.Extract
            | ArchiveCapability.UpdateByRewrite;

        private readonly string _sourcePath;
        private readonly string _destinationPath;
        private readonly OperationEvent _terminal;
        private readonly TaskCompletionSource<OperationEvent>? _deferredTerminal;
        private readonly IReadOnlyList<ArchiveEntryDescriptor> _sourceEntries =
            [Entry("keep.txt", 0), Entry("remove.txt", 1)];
        private readonly IReadOnlyList<ArchiveEntryDescriptor> _rebuiltEntries =
            [Entry("keep.txt", 0), Entry("new.txt", 1)];

        public RebuildArchiveClient(
            string sourcePath,
            string destinationPath,
            OperationEvent terminal,
            bool deferTerminalUntilCancellation = false)
        {
            _sourcePath = Path.GetFullPath(sourcePath);
            _destinationPath = Path.GetFullPath(destinationPath);
            _terminal = terminal;
            _deferredTerminal = deferTerminalUntilCancellation
                ? new TaskCompletionSource<OperationEvent>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }

        public ArchiveSessionId SourceSessionId { get; } = ArchiveSessionId.New();
        public ArchiveSessionId RebuiltSessionId { get; } = ArchiveSessionId.New();
        public bool SupportsRebuildFeature { get; init; } = true;
        public bool SupportsRenameFeature { get; init; } = true;
        public ArchiveCapability SourceCapabilities { get; init; } = EligibleCapabilities;
        public List<string> OpenedPaths { get; } = [];
        public List<ArchiveSessionId> ClosedSessions { get; } = [];
        public int RebuildCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public ArchiveSessionId? RebuildSessionId { get; private set; }
        public string? RebuildDestinationPath { get; private set; }
        public ArchiveCompressionPreset RebuildPreset { get; private set; }
        public IReadOnlyList<ArchiveEntryKey> RebuildDeletes { get; private set; } = [];
        public IReadOnlyList<ArchiveRebuildCopyFile> RebuildFiles { get; private set; } = [];
        public int RenameCalls { get; private set; }
        public IReadOnlyList<ArchiveRebuildRename> RebuildRenames { get; private set; } = [];
        public int TreeCalls { get; private set; }
        public IReadOnlyList<ArchiveCreationDirectory> RebuildDirectories { get; private set; } = [];
        public IReadOnlyList<ArchiveRebuildTreeAction> RebuildTreeActions { get; private set; } = [];
        public TaskCompletionSource OperationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool SupportsFeature(string feature) =>
            feature switch
            {
                ProtocolFeatures.ZipRebuildCopy => SupportsRebuildFeature,
                ProtocolFeatures.ZipRebuildRename => SupportsRenameFeature,
                ProtocolFeatures.ZipRebuildTreeEdit => true,
                _ => true,
            };

        public override Task<OpenArchiveResponse> OpenArchiveAsync(
            string sourcePath,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(sourcePath);
            OpenedPaths.Add(fullPath);
            var isSource = string.Equals(fullPath, _sourcePath, StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(
                isSource || string.Equals(fullPath, _destinationPath, StringComparison.OrdinalIgnoreCase));
            var entries = isSource ? _sourceEntries : _rebuiltEntries;
            return Task.FromResult(new OpenArchiveResponse(new ArchiveDescriptor(
                isSource ? SourceSessionId : RebuiltSessionId,
                "zip",
                "fake.zip",
                isSource ? SourceCapabilities : EligibleCapabilities,
                new ArchiveFingerprint(1, DateTimeOffset.UtcNow, "fake", new string('0', 64)),
                entries.Count,
                false,
                false,
                false,
                true,
                [],
                ArchiveVolumeSetKind.Single)));
        }

        public override Task<ListEntriesResponse> ListEntriesAsync(
            ArchiveSessionId sessionId,
            int offset = 0,
            int limit = 512,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = sessionId == SourceSessionId ? _sourceEntries : _rebuiltEntries;
            return Task.FromResult(new ListEntriesResponse(entries, 0, entries.Count, null));
        }

        public override Task<StartOperationResponse> StartZipRebuildCopyAsync(
            ArchiveSessionId sessionId,
            string destinationPath,
            IReadOnlyList<ArchiveEntryKey> entriesToDelete,
            IReadOnlyList<ArchiveRebuildCopyFile> files,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RebuildCalls++;
            RebuildSessionId = sessionId;
            RebuildDestinationPath = destinationPath;
            RebuildDeletes = entriesToDelete;
            RebuildFiles = files;
            RebuildPreset = compressionPreset;
            OperationStarted.TrySetResult();
            return Task.FromResult(new StartOperationResponse(_terminal.OperationId));
        }

        public override Task<StartOperationResponse> StartZipRebuildRenameAsync(
            ArchiveSessionId sessionId,
            string destinationPath,
            IReadOnlyList<ArchiveEntryKey> entriesToDelete,
            IReadOnlyList<ArchiveRebuildCopyFile> files,
            IReadOnlyList<ArchiveRebuildRename> renames,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenameCalls++;
            RebuildSessionId = sessionId;
            RebuildDestinationPath = destinationPath;
            RebuildDeletes = entriesToDelete;
            RebuildFiles = files;
            RebuildRenames = renames;
            RebuildPreset = compressionPreset;
            OperationStarted.TrySetResult();
            return Task.FromResult(new StartOperationResponse(_terminal.OperationId));
        }

        public override Task<StartOperationResponse> StartZipRebuildTreeEditAsync(
            ArchiveSessionId sessionId,
            string destinationPath,
            IReadOnlyList<ArchiveEntryKey> entriesToDelete,
            IReadOnlyList<ArchiveRebuildCopyFile> files,
            IReadOnlyList<ArchiveRebuildRename> renames,
            IReadOnlyList<ArchiveCreationDirectory> directories,
            IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TreeCalls++;
            RebuildSessionId = sessionId;
            RebuildDestinationPath = destinationPath;
            RebuildDeletes = entriesToDelete;
            RebuildFiles = files;
            RebuildRenames = renames;
            RebuildDirectories = directories;
            RebuildTreeActions = treeActions;
            RebuildPreset = compressionPreset;
            OperationStarted.TrySetResult();
            return Task.FromResult(new StartOperationResponse(_terminal.OperationId));
        }

        public override Task<OperationEvent> WaitForTerminalAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(_terminal.OperationId, operationId);
            return _deferredTerminal?.Task ?? Task.FromResult(_terminal);
        }

        public override Task<CancelOperationResponse> CancelOperationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(_terminal.OperationId, operationId);
            CancelCalls++;
            _deferredTerminal?.TrySetResult(_terminal);
            return Task.FromResult(new CancelOperationResponse(true));
        }

        public override Task<GetOperationActivityResponse> GetOperationActivityAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<OperationActivitySnapshot> operations = OperationStarted.Task.IsCompleted
                ? [new OperationActivitySnapshot(
                    _terminal.OperationId,
                    OperationKind.RebuildArchive,
                    _deferredTerminal?.Task.IsCompleted == true
                        ? _terminal.Phase
                        : OperationPhase.Running,
                    _deferredTerminal?.Task.IsCompleted == true ? 1 : 0,
                    now,
                    now,
                    _deferredTerminal?.Task.IsCompleted == true ? now : null,
                    null,
                    0,
                    _deferredTerminal?.Task.IsCompleted != true)]
                : [];
            return Task.FromResult(new GetOperationActivityResponse(
                now,
                ProtocolConstants.MaximumQueuedOperations,
                ProtocolConstants.MaximumConcurrentOperations,
                OperationQueueOrdering.Unspecified,
                ProtocolConstants.MaximumRetainedOperationActivity,
                false,
                false,
                false,
                false,
                false,
                operations));
        }

        public override Task<GetSupportDiagnosticsResponse> GetSupportDiagnosticsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = OperationStarted.Task.IsCompleted
                && _deferredTerminal?.Task.IsCompleted != true
                ? 1
                : 0;
            return Task.FromResult(new GetSupportDiagnosticsResponse(
                ProtocolConstants.MajorVersion,
                ProtocolConstants.MinorVersion,
                ProtocolFeatures.SupportedVersion1.Count,
                WorkerContainmentModel.SameUserWorkerProcess,
                false,
                ProtocolConstants.MaximumQueuedOperations,
                ProtocolConstants.MaximumConcurrentOperations,
                OperationQueueOrdering.Unspecified,
                ProtocolConstants.MaximumRetainedOperationActivity,
                active,
                OperationStarted.Task.IsCompleted && active == 0 ? 1 : 0,
                false,
                false,
                false,
                false,
                false,
                false));
        }

        public override Task<CloseArchiveResponse> CloseArchiveAsync(
            ArchiveSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClosedSessions.Add(sessionId);
            return Task.FromResult(new CloseArchiveResponse(true));
        }
    }

    private sealed class RecordingDialogService : IAppDialogService
    {
        public string? ErrorTitle { get; private set; }
        public string? ErrorMessage { get; private set; }
        public string? InformationTitle { get; private set; }
        public string? InformationMessage { get; private set; }

        public string? ChooseArchive() => null;

        public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation) => null;

        public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly) => null;

        public Task<char[]?> RequestPasswordAsync(
            string archiveName,
            CancellationToken cancellationToken) => Task.FromResult<char[]?>(null);

        public Task ShowArchiveRecoveryAsync(
            IArchiveClient client,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public void ShowError(string title, string message)
        {
            ErrorTitle = title;
            ErrorMessage = message;
        }

        public void ShowInformation(string title, string message)
        {
            InformationTitle = title;
            InformationMessage = message;
        }
    }
}
