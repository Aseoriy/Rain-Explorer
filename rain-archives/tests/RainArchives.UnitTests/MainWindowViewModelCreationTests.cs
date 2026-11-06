using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class MainWindowViewModelCreationTests
{
    private const string UnavailableMessage =
        "This action is unavailable for the connected archive session.";
    private const string OperationWarningMessage =
        "The operation completed with a warning. Review the Activity Center for its typed lifecycle state.";

    [TestMethod]
    public async Task CreationRefusesNegativeTypedCapabilityBeforeStartingOperation()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ZipWriteCapabilities = new GetZipWriteCapabilitiesResponse(
                CanCreate: false,
                CanRebuild: true,
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

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), "refused.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        Assert.AreEqual(0, client.FileCreationCalls);
        Assert.AreEqual(UnavailableMessage, dialogs.ErrorMessage);
    }

    [TestMethod]
    public async Task FileOnlyCreationUsesFileRouteAndOpensTheCompletedArchive()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var files = new[]
        {
            new ArchiveCreationFile(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt"),
                "alpha.txt"),
        };
        var client = new RecordingArchiveClient(CreateCompletedTerminal());
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            destination,
            files,
            [],
            ArchiveCompressionPreset.Fast));

        Assert.AreEqual(1, client.FileCreationCalls);
        Assert.AreEqual(0, client.TreeCreationCalls);
        Assert.AreEqual(destination, client.DestinationPath);
        Assert.AreEqual(ArchiveCompressionPreset.Fast, client.CompressionPreset);
        CollectionAssert.AreEqual(files, client.Files.ToArray());
        Assert.AreEqual(1, client.OpenCalls);
        Assert.IsTrue(viewModel.HasArchive);
        Assert.AreEqual("ZIP archive created and opened.", viewModel.StatusText);
        Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);
    }

    [TestMethod]
    public async Task DirectoryCreationUsesTreeRouteAndPreservesCompletionWarning()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var directories = new[]
        {
            new ArchiveCreationDirectory(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                "assets"),
        };
        var warning = new ArchiveWarning("creation.test_warning", "The archive was created with a warning.");
        var client = new RecordingArchiveClient(CreateCompletedTerminal(warning));
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            destination,
            [],
            directories,
            ArchiveCompressionPreset.Maximum));

        Assert.AreEqual(0, client.FileCreationCalls);
        Assert.AreEqual(1, client.TreeCreationCalls);
        Assert.AreEqual(ArchiveCompressionPreset.Maximum, client.CompressionPreset);
        CollectionAssert.AreEqual(directories, client.Directories.ToArray());
        Assert.AreEqual("ZIP archive created with warnings and opened.", viewModel.StatusText);
        Assert.AreEqual("Completed with warnings", dialogs.InformationTitle);
        Assert.AreEqual(OperationWarningMessage, dialogs.InformationMessage);
        Assert.IsFalse(
            (dialogs.InformationMessage ?? string.Empty).Contains(
                warning.Message,
                StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailedCreationDoesNotOpenOrMisreportTheDestination()
    {
        var error = new ArchiveError(
            "filesystem.destination_exists",
            ArchiveErrorCategory.DestinationConflict,
            "The destination already exists.");
        var terminal = new OperationEvent(
            OperationId.New(),
            1,
            OperationEventKind.Terminal,
            OperationPhase.Failed,
            Error: error);
        var client = new RecordingArchiveClient(terminal);
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        Assert.AreEqual(1, client.FileCreationCalls);
        Assert.AreEqual(0, client.OpenCalls);
        Assert.IsFalse(viewModel.HasArchive);
        Assert.AreEqual(error.Message, viewModel.StatusText);
        Assert.AreEqual("Archive operation failed", dialogs.ErrorTitle);
        Assert.AreEqual(error.Message, dialogs.ErrorMessage);
    }

    [TestMethod]
    public async Task CreationCancellationUsesTheAcknowledgedWorkerOperationAndPublishesNothing()
    {
        var terminal = new OperationEvent(
            OperationId.New(),
            1,
            OperationEventKind.Terminal,
            OperationPhase.Cancelled);
        var client = new RecordingArchiveClient(terminal, deferTerminalUntilCancellation: true);
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        var creation = viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.bin"), "source.bin")],
            [],
            ArchiveCompressionPreset.Maximum));
        await client.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(viewModel.ActiveActivity.CanCancel);
        await WaitForSnapshotCancellationAsync(viewModel);
        await viewModel.CancelCurrentOperationAsync();
        await creation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, client.CancelCalls);
        Assert.AreEqual(0, client.OpenCalls);
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
    public async Task UnsupportedCreationFeatureIsReportedWithoutStartingAnActivity()
    {
        var error = new ArchiveError(
            "zip.creation_feature_not_negotiated",
            ArchiveErrorCategory.UnsupportedCapability,
            "The connected archive worker does not support secure ZIP creation.");
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            StartException = new ArchiveClientException(error),
            SupportsZipCreation = false,
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        Assert.AreEqual(0, client.FileCreationCalls);
        Assert.IsFalse(viewModel.ActiveActivity.IsVisible);
        Assert.IsFalse(viewModel.CanCreate);
        Assert.IsFalse(viewModel.CreateCommand.CanExecute(null));
        Assert.AreEqual(UnavailableMessage, viewModel.StatusText);
        Assert.AreEqual("Rain Archives", dialogs.ErrorTitle);
        Assert.AreEqual(UnavailableMessage, dialogs.ErrorMessage);
    }

    [TestMethod]
    public async Task UnsupportedDirectoryCreationIsRejectedBeforeStartingAnActivity()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            SupportsZipDirectoryCreation = false,
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [],
            [new ArchiveCreationDirectory(Path.Combine(Path.GetTempPath(), "assets"), "assets")],
            ArchiveCompressionPreset.Balanced));

        Assert.AreEqual(0, client.TreeCreationCalls);
        Assert.IsFalse(viewModel.ActiveActivity.IsVisible);
        Assert.IsTrue(viewModel.CanCreate);
        Assert.AreEqual(UnavailableMessage, viewModel.StatusText);
        Assert.AreEqual("Rain Archives", dialogs.ErrorTitle);
        Assert.AreEqual(viewModel.StatusText, dialogs.ErrorMessage);
    }

    [TestMethod]
    public async Task RecoveryReviewUsesTheNegotiatedClientThroughTheDialogBoundary()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal());
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.ReviewRecoveriesAsync();

        Assert.AreEqual(1, dialogs.RecoveryDialogCalls);
        Assert.AreSame(client, dialogs.RecoveryClient);
        Assert.IsTrue(viewModel.CanReviewRecoveries);
        Assert.AreEqual("Recovery Center closed.", viewModel.StatusText);
    }

    [TestMethod]
    public async Task RecoveryReviewAdoptsIndependentSessionWithoutInventingFilesystemAuthority()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal());
        var recoveredArchive = new ArchiveDescriptor(
            ArchiveSessionId.New(),
            "zip",
            "provider-controlled",
            ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract
                | ArchiveCapability.UpdateByRewrite,
            new ArchiveFingerprint(
                128,
                DateTimeOffset.UtcNow,
                "provider-controlled-volume",
                new string('A', 64)),
            client.ListedEntries.Count,
            IsSolid: false,
            HasEncryptedEntries: false,
            IsMultipart: false,
            IsReadOnly: true,
            Warnings: []);
        var dialogs = new RecordingDialogService
        {
            RecoveryResult = new RecoveredArchiveDialogResult(
                new ReopenNestedWorkspaceRecoveryResponse(
                    new string('a', 64),
                    new string('b', 64),
                    NestedWorkspaceRecoveryReopenMode.IndependentRoot,
                    recoveredArchive,
                    MaterializedBytes: 128,
                    Depth: 0,
                    DateTimeOffset.UtcNow),
                OpenedWithPassword: false),
        };
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.ReviewRecoveriesAsync();

        Assert.IsTrue(viewModel.HasArchive);
        Assert.AreEqual("Recovered archive", viewModel.ArchiveTitle);
        Assert.IsTrue(viewModel.CanExtract);
        Assert.IsTrue(viewModel.CanTest);
        Assert.IsFalse(viewModel.CanRebuildCopy);
        StringAssert.Contains(viewModel.RebuildCopyHelpText, "filesystem source");
        Assert.AreEqual(client.ListedEntries.Count, viewModel.Entries.Count);
        StringAssert.Contains(viewModel.StatusText, "recovery workspace remains preserved");
        Assert.IsFalse(viewModel.ArchiveTitle.Contains("provider", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task RecoveryAdoptionPreviewDrainFailureClosesRecoveredSessionAndPreservesPriorState()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal());
        var recoveredArchive = CreateRecoveredArchive(client.ListedEntries.Count);
        var dialogs = new RecordingDialogService
        {
            RecoveryResult = new RecoveredArchiveDialogResult(
                new ReopenNestedWorkspaceRecoveryResponse(
                    new string('a', 64),
                    new string('b', 64),
                    NestedWorkspaceRecoveryReopenMode.IndependentRoot,
                    recoveredArchive,
                    MaterializedBytes: 128,
                    Depth: 0,
                    DateTimeOffset.UtcNow),
                OpenedWithPassword: false),
        };
        var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        try
        {
            await viewModel.OpenArchivePathAsync(@"C:\prior.zip");
            Assert.IsTrue(viewModel.HasArchive);
            Assert.AreEqual("prior.zip", viewModel.ArchiveTitle);

            SetPreviewPump(
                viewModel,
                Task.FromException(new InvalidDataException("Deterministic preview drain failure.")));
            await viewModel.ReviewRecoveriesAsync();

            Assert.IsTrue(viewModel.HasArchive);
            Assert.IsTrue(viewModel.Browser.CanBrowse);
            Assert.AreEqual("prior.zip", viewModel.ArchiveTitle);
            Assert.AreEqual(client.ListedEntries.Count, viewModel.Entries.Count);
            CollectionAssert.AreEqual(
                new[] { recoveredArchive.SessionId },
                client.ClosedSessions.ToArray());
        }
        finally
        {
            SetPreviewPump(viewModel, Task.CompletedTask);
            await viewModel.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RecoveryAdoptionNotificationFailureClosesRecoveredSessionAndClearsPartialState()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal());
        var recoveredArchive = CreateRecoveredArchive(client.ListedEntries.Count);
        var dialogs = new RecordingDialogService
        {
            RecoveryResult = new RecoveredArchiveDialogResult(
                new ReopenNestedWorkspaceRecoveryResponse(
                    new string('c', 64),
                    new string('d', 64),
                    NestedWorkspaceRecoveryReopenMode.IndependentRoot,
                    recoveredArchive,
                    MaterializedBytes: 128,
                    Depth: 0,
                    DateTimeOffset.UtcNow),
                OpenedWithPassword: false),
        };
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        System.ComponentModel.PropertyChangedEventHandler? throwingHandler = null;
        throwingHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.HasArchive))
            {
                viewModel.PropertyChanged -= throwingHandler;
                throw new InvalidOperationException("Deterministic adoption notification failure.");
            }
        };
        viewModel.PropertyChanged += throwingHandler;

        await viewModel.ReviewRecoveriesAsync();

        Assert.IsFalse(viewModel.HasArchive);
        Assert.IsFalse(viewModel.Browser.CanBrowse);
        CollectionAssert.AreEqual(
            new[] { recoveredArchive.SessionId },
            client.ClosedSessions.ToArray());
    }

    [TestMethod]
    public async Task OrdinaryOpenNotificationFailureClosesNewSessionAndClearsPartialState()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal());
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogService(),
            _ => Task.FromResult<IArchiveClient>(client));
        System.ComponentModel.PropertyChangedEventHandler? throwingHandler = null;
        throwingHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.HasArchive))
            {
                viewModel.PropertyChanged -= throwingHandler;
                throw new InvalidOperationException("Deterministic open notification failure.");
            }
        };
        viewModel.PropertyChanged += throwingHandler;

        await viewModel.OpenArchivePathAsync(@"C:\notification-failure.zip");

        Assert.IsFalse(viewModel.HasArchive);
        Assert.IsFalse(viewModel.Browser.CanBrowse);
        CollectionAssert.AreEqual(
            new[] { client.OpenedSessionId },
            client.ClosedSessions.ToArray());
    }

    [TestMethod]
    public async Task UnsupportedRecoveryInspectionIsReportedBeforeOpeningTheDialog()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            SupportsRecoveryInspection = false,
            SupportsOtherRecoveryCenterFeatures = false,
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.ReviewRecoveriesAsync();

        Assert.AreEqual(0, dialogs.RecoveryDialogCalls);
        Assert.IsFalse(viewModel.CanReviewRecoveries);
        Assert.IsFalse(viewModel.RecoveryCommand.CanExecute(null));
        Assert.AreEqual("Rain Archives", dialogs.ErrorTitle);
        Assert.AreEqual(UnavailableMessage, viewModel.StatusText);
    }

    [TestMethod]
    public async Task FailedPostCommitOpenKeepsTheCreatedOutcomeFormatNeutral()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            OpenException = new IOException("Opening failed after creation."),
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        Assert.AreEqual(1, client.FileCreationCalls);
        Assert.AreEqual(1, client.OpenCalls);
        Assert.AreEqual("Archive was created, but it could not be opened.", viewModel.StatusText);
        Assert.AreEqual("Archive created", dialogs.InformationTitle);
        Assert.AreEqual(viewModel.StatusText, dialogs.InformationMessage);
    }

    [TestMethod]
    public async Task CancelledPostCommitOpenKeepsTheCreatedOutcomeExplicit()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            OpenException = new OperationCanceledException("Opening was cancelled."),
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        Assert.AreEqual(1, client.FileCreationCalls);
        Assert.AreEqual(1, client.OpenCalls);
        Assert.AreEqual(
            "Archive was created, but opening it was cancelled.",
            viewModel.StatusText);
        Assert.IsNull(dialogs.ErrorTitle);
    }

    [TestMethod]
    public async Task InspectorStartsHiddenAndPropertiesOpenCloseAndReopenExplicitly()
    {
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List
                | ArchiveCapability.Test
                | ArchiveCapability.Extract
                | ArchiveCapability.OpenEntryStream,
        };
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogService(),
            _ => Task.FromResult<IArchiveClient>(client));
        var propertiesFocusRequests = 0;
        ArchiveBrowserFocusTarget? restoredFocus = null;
        viewModel.PropertiesFocusRequested += (_, _) => propertiesFocusRequests++;
        viewModel.Browser.FocusRequested += (_, args) => restoredFocus = args.Target;

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        Assert.IsFalse(viewModel.IsInspectorVisible);
        Assert.IsFalse(viewModel.IsPreviewVisible);
        Assert.IsFalse(viewModel.CloseInspectorCommand.CanExecute(null));

        viewModel.ShowPropertiesCommand.Execute(null);
        Assert.IsTrue(viewModel.IsInspectorVisible);
        Assert.AreEqual(0, viewModel.InspectorSelectedTabIndex);
        Assert.AreEqual(1, propertiesFocusRequests);
        Assert.IsTrue(viewModel.CloseInspectorCommand.CanExecute(null));

        await viewModel.CloseInspectorAsync();
        Assert.IsFalse(viewModel.IsInspectorVisible);
        Assert.IsFalse(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Idle, viewModel.Preview.State);
        Assert.AreEqual(ArchiveBrowserFocusTarget.EntryList, restoredFocus);

        restoredFocus = null;
        viewModel.ShowPropertiesCommand.Execute(null);
        Assert.IsTrue(viewModel.IsInspectorVisible);
        Assert.AreEqual(0, viewModel.InspectorSelectedTabIndex);
        Assert.AreEqual(2, propertiesFocusRequests);
        Assert.IsNull(restoredFocus);
    }

    [TestMethod]
    public async Task ClosingLoadingPreviewWaitsForDrainRejectsLateCompletionAndCanReopen()
    {
        var firstEntry = CreateEntry("late.txt");
        var secondEntry = CreateEntry("reopened.txt");
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List | ArchiveCapability.OpenEntryStream,
            ListedEntries = [firstEntry, secondEntry],
        };
        var preview = new LateCompletionPreviewService(firstEntry.EntryId, secondEntry.EntryId);
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogService(),
            _ => Task.FromResult<IArchiveClient>(client),
            _ => preview);
        ArchiveBrowserFocusTarget? restoredFocus = null;
        viewModel.Browser.FocusRequested += (_, args) => restoredFocus = args.Target;
        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        var firstRequest = viewModel.ActivateEntryAsync(
            viewModel.Entries.Single(entry => entry.EntryId == firstEntry.EntryId));
        await preview.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(viewModel.IsInspectorVisible);
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Loading, viewModel.Preview.State);

        var close = viewModel.CloseInspectorAsync();
        await preview.FirstCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(viewModel.IsInspectorClosing);
        Assert.IsTrue(viewModel.IsInspectorVisible);
        Assert.IsFalse(close.IsCompleted);
        Assert.IsFalse(viewModel.PreviewSelectionCommand.CanExecute(null));

        preview.ReleaseFirst.TrySetResult();
        await Task.WhenAll(firstRequest, close).WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        Assert.IsFalse(viewModel.IsInspectorClosing);
        Assert.IsFalse(viewModel.IsInspectorVisible);
        Assert.IsFalse(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Idle, viewModel.Preview.State);
        Assert.AreEqual(string.Empty, viewModel.PreviewText);
        Assert.AreEqual(ArchiveBrowserFocusTarget.EntryList, restoredFocus);

        restoredFocus = null;
        await viewModel.ActivateEntryAsync(
            viewModel.Entries.Single(entry => entry.EntryId == secondEntry.EntryId));
        Assert.IsTrue(viewModel.IsInspectorVisible);
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual(1, viewModel.InspectorSelectedTabIndex);
        Assert.AreEqual("reopened payload", viewModel.PreviewText);
        Assert.IsNull(restoredFocus);

        await viewModel.CloseInspectorAsync();
        Assert.IsFalse(viewModel.IsInspectorVisible);
        Assert.AreEqual(ArchiveBrowserFocusTarget.EntryList, restoredFocus);
        Assert.AreEqual(2, preview.CallCount);
    }

    [TestMethod]
    public async Task NewerPreviewCancelsAndCannotBeOverwrittenByAnOlderRequest()
    {
        var firstEntry = CreateEntry("first.txt");
        var secondEntry = CreateEntry("second.txt");
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List
                | ArchiveCapability.Test
                | ArchiveCapability.Extract
                | ArchiveCapability.OpenEntryStream,
            ListedEntries = [firstEntry, secondEntry],
        };
        var preview = new RecordingPreviewService(firstEntry.EntryId, secondEntry.EntryId);
        var dialogs = new RecordingDialogService();
        var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client),
            _ => preview);
        var previewFocusRequests = 0;
        ArchiveBrowserFocusTarget? restoredFocus = null;
        viewModel.PreviewFocusRequested += (_, _) => previewFocusRequests++;
        viewModel.Browser.FocusRequested += (_, args) => restoredFocus = args.Target;

        await using (viewModel)
        {
            await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
                [],
                ArchiveCompressionPreset.Balanced));

            var first = viewModel.ActivateEntryAsync(
                viewModel.Entries.Single(entry => entry.EntryId == firstEntry.EntryId));
            await preview.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = viewModel.ActivateEntryAsync(
                viewModel.Entries.Single(entry => entry.EntryId == secondEntry.EntryId));
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(preview.FirstCancellationObserved);
            Assert.AreEqual(2, preview.CallCount);
            Assert.IsTrue(viewModel.IsPreviewVisible);
            Assert.IsTrue(viewModel.IsInspectorVisible);
            Assert.IsFalse(viewModel.IsPreviewLoading);
            Assert.AreEqual("second.txt", viewModel.PreviewTitle);
            Assert.AreEqual("second payload", viewModel.PreviewText);
            Assert.AreEqual("Previewing second.txt.", viewModel.StatusText);
            Assert.AreEqual(SafePreviewState.Text, viewModel.Preview.State);
            Assert.AreEqual(1, viewModel.InspectorSelectedTabIndex);
            Assert.AreEqual(1, previewFocusRequests);

            restoredFocus = null;
            await viewModel.ClosePreviewAsync();
            Assert.IsFalse(viewModel.IsPreviewVisible);
            Assert.IsFalse(viewModel.IsInspectorVisible);
            Assert.AreEqual(string.Empty, viewModel.PreviewText);
            Assert.AreEqual(SafePreviewState.Idle, viewModel.Preview.State);
            Assert.AreEqual(0, viewModel.InspectorSelectedTabIndex);
            Assert.AreEqual(ArchiveBrowserFocusTarget.EntryList, restoredFocus);
        }

        Assert.IsTrue(preview.Disposed);
    }

    [TestMethod]
    public async Task NewestPreviewWaitsForDelayedPriorCancellationBeforeUsingTheService()
    {
        var firstEntry = CreateEntry("slow.txt");
        var secondEntry = CreateEntry("latest.txt");
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List
                | ArchiveCapability.Test
                | ArchiveCapability.Extract
                | ArchiveCapability.OpenEntryStream,
            ListedEntries = [firstEntry, secondEntry],
        };
        var preview = new DelayedCancellationPreviewService(firstEntry.EntryId, secondEntry.EntryId);
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogService(),
            _ => Task.FromResult<IArchiveClient>(client),
            _ => preview);
        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        var first = viewModel.ActivateEntryAsync(
            viewModel.Entries.Single(entry => entry.EntryId == firstEntry.EntryId));
        await preview.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = viewModel.ActivateEntryAsync(
            viewModel.Entries.Single(entry => entry.EntryId == secondEntry.EntryId));
        await preview.FirstCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(preview.SecondStarted.Task.IsCompleted);
        preview.ReleaseFirst.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(preview.SecondStarted.Task.IsCompletedSuccessfully);
        Assert.AreEqual(2, preview.CallCount);
        Assert.AreEqual("latest payload", viewModel.PreviewText);
        Assert.AreEqual("latest.txt", viewModel.PreviewTitle);
    }

    [TestMethod]
    public async Task ContextPreviewCommandCanReplaceAndDrainItsRunningRequest()
    {
        var firstEntry = CreateEntry("context-slow.txt");
        var secondEntry = CreateEntry("context-latest.txt");
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List | ArchiveCapability.OpenEntryStream,
            ListedEntries = [firstEntry, secondEntry],
        };
        var preview = new DelayedCancellationPreviewService(firstEntry.EntryId, secondEntry.EntryId);
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogService(),
            _ => Task.FromResult<IArchiveClient>(client),
            _ => preview);
        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        viewModel.Browser.SelectedEntry = viewModel.Entries.Single(
            entry => entry.EntryId == firstEntry.EntryId);
        Assert.IsTrue(viewModel.PreviewSelectionCommand.CanExecute(null));
        viewModel.PreviewSelectionCommand.Execute(null);
        await preview.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Browser.SelectedEntry = viewModel.Entries.Single(
            entry => entry.EntryId == secondEntry.EntryId);
        Assert.IsTrue(viewModel.PreviewSelectionCommand.CanExecute(null));
        viewModel.PreviewSelectionCommand.Execute(null);
        await preview.FirstCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(preview.SecondStarted.Task.IsCompleted);

        preview.ReleaseFirst.TrySetResult();
        await preview.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.ClosePreviewAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, preview.CallCount);
        Assert.IsFalse(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Idle, viewModel.Preview.State);
    }

    [TestMethod]
    public async Task SelectionChangeCancelsPreviewAndKeepsPropertiesOnTheNewSelection()
    {
        var firstEntry = CreateEntry("first.txt");
        var secondEntry = CreateEntry("second.txt");
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List | ArchiveCapability.OpenEntryStream,
            ListedEntries = [firstEntry, secondEntry],
        };
        var preview = new RecordingPreviewService(firstEntry.EntryId, secondEntry.EntryId);
        await using var viewModel = new MainWindowViewModel(
            new RecordingDialogService(),
            _ => Task.FromResult<IArchiveClient>(client),
            _ => preview);
        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        var request = viewModel.ActivateEntryAsync(viewModel.Entries[0]);
        await preview.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.Browser.SelectedEntry = viewModel.Entries[1];
        await request.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(preview.FirstCancellationObserved);
        Assert.IsFalse(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Idle, viewModel.Preview.State);
        Assert.AreEqual("second.txt", viewModel.Properties.SelectedEntryTitle);
    }

    [TestMethod]
    public async Task MissingStreamCapabilityExplainsPreviewWithoutCreatingAService()
    {
        var entry = CreateEntry("solid.bin");
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List
                | ArchiveCapability.Test
                | ArchiveCapability.Extract,
            ArchiveWarnings =
            [
                new ArchiveWarning(
                    "archive.preview_unavailable",
                    "Direct preview is unavailable for this archive."),
            ],
            ListedEntries = [entry],
        };
        var previewFactoryCalls = 0;
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client),
            _ =>
            {
                previewFactoryCalls++;
                return new RecordingPreviewService(entry.EntryId, entry.EntryId);
            });

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));
        await viewModel.ActivateEntryAsync(viewModel.Entries.Single());

        Assert.AreEqual(0, previewFactoryCalls);
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Unsupported, viewModel.Preview.State);
        Assert.AreEqual("Safe preview is unavailable for this archive session.", viewModel.StatusText);
        Assert.IsNull(dialogs.ErrorTitle);
    }

    [TestMethod]
    public async Task EncryptedPreviewSettlesItsTemporarySecretAfterAServiceRefusal()
    {
        var entry = CreateEntry("large-secret.txt") with
        {
            IsEncrypted = true,
            OriginalSize = TextEntryPreviewService.MaximumTextPreviewBytes + 1,
        };
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List
                | ArchiveCapability.Test
                | ArchiveCapability.Extract
                | ArchiveCapability.OpenEntryStream,
            ListedEntries = [entry],
        };
        var password = "preview-password".ToCharArray();
        var dialogs = new RecordingDialogService { PasswordToReturn = password };
        var preview = new RefusingPreviewService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client),
            _ => preview);

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));
        await viewModel.ActivateEntryAsync(viewModel.Entries.Single());

        Assert.AreEqual(1, client.CreateSecretCalls);
        Assert.AreEqual(1, client.DiscardSecretCalls);
        Assert.AreEqual(client.SecretSlotId, client.LastDiscardedSecretSlot);
        Assert.IsTrue(password.All(character => character == '\0'));
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Oversize, viewModel.Preview.State);
        StringAssert.Contains(viewModel.PreviewDetails, "1 MiB display limit");
        Assert.AreEqual(string.Empty, viewModel.PreviewText);
    }

    [TestMethod]
    public async Task IncorrectEncryptedPreviewPasswordCanBeRetriedWithoutRetainingTheFailedSlot()
    {
        var entry = CreateEntry("protected.txt") with { IsEncrypted = true };
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List
                | ArchiveCapability.Test
                | ArchiveCapability.Extract
                | ArchiveCapability.OpenEntryStream,
            ListedEntries = [entry],
        };
        var wrongPassword = "wrong-password".ToCharArray();
        var correctPassword = "correct-password".ToCharArray();
        var dialogs = new RecordingDialogService();
        dialogs.PasswordsToReturn.Enqueue(wrongPassword);
        dialogs.PasswordsToReturn.Enqueue(correctPassword);
        var preview = new PasswordRetryPreviewService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client),
            _ => preview);

        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));
        var listedEntry = viewModel.Entries.Single();

        await viewModel.ActivateEntryAsync(listedEntry);
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Error, viewModel.Preview.State);
        Assert.AreEqual("The password is incorrect.", viewModel.StatusText);

        await viewModel.ActivateEntryAsync(listedEntry);
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual("protected payload", viewModel.PreviewText);
        Assert.AreEqual(2, client.CreateSecretCalls);
        Assert.AreEqual(2, client.DiscardSecretCalls);
        Assert.IsTrue(wrongPassword.All(character => character == '\0'));
        Assert.IsTrue(correctPassword.All(character => character == '\0'));
    }

    [TestMethod]
    [DataRow(ArchiveErrorCategory.SourceChanged, ArchiveBrowserState.Stale, SafePreviewState.Stale)]
    [DataRow(ArchiveErrorCategory.WorkerFailure, ArchiveBrowserState.Offline, SafePreviewState.Offline)]
    public async Task PreviewSessionFailuresInvalidateBrowserAndUseFixedRedactedPresentation(
        ArchiveErrorCategory category,
        ArchiveBrowserState browserState,
        SafePreviewState previewState)
    {
        var entry = CreateEntry("current.txt");
        var client = new RecordingArchiveClient(CreateCompletedTerminal())
        {
            ArchiveCapabilities = ArchiveCapability.List | ArchiveCapability.OpenEntryStream,
            ListedEntries = [entry],
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client),
            _ => new ThrowingPreviewService(category));
        await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            [new ArchiveCreationFile(Path.Combine(Path.GetTempPath(), "source.txt"), "source.txt")],
            [],
            ArchiveCompressionPreset.Balanced));

        await viewModel.ActivateEntryAsync(viewModel.Entries.Single());

        Assert.AreEqual(browserState, viewModel.Browser.State);
        Assert.AreEqual(previewState, viewModel.Preview.State);
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual(0, viewModel.Properties.ArchiveItems.Count);
        Assert.IsNotNull(dialogs.ErrorMessage);
        Assert.IsFalse(dialogs.ErrorMessage.Contains("C:\\provider-secret", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Preview.StateMessage.Contains("C:\\provider-secret", StringComparison.Ordinal));
    }

    private static ArchiveEntryDescriptor CreateEntry(string name) =>
        new(
            EntryId.New(),
            null,
            0,
            name,
            name,
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
            name);

    private static OperationEvent CreateCompletedTerminal(ArchiveWarning? warning = null) =>
        new(
            OperationId.New(),
            1,
            OperationEventKind.Terminal,
            warning is null ? OperationPhase.Completed : OperationPhase.CompletedWithWarnings,
            Warning: warning);

    private static ArchiveDescriptor CreateRecoveredArchive(int entryCount) =>
        new(
            ArchiveSessionId.New(),
            "zip",
            "provider-controlled",
            ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            new ArchiveFingerprint(
                128,
                DateTimeOffset.UtcNow,
                "provider-controlled-volume",
                new string('A', 64)),
            entryCount,
            IsSolid: false,
            HasEncryptedEntries: false,
            IsMultipart: false,
            IsReadOnly: true,
            Warnings: []);

    private static void SetPreviewPump(MainWindowViewModel viewModel, Task previewPump)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_previewPump",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The preview pump field was not found.");
        field.SetValue(viewModel, previewPump);
    }

    private sealed class RecordingDialogService : IAppDialogService
    {
        public string? InformationTitle { get; private set; }
        public string? InformationMessage { get; private set; }
        public string? ErrorTitle { get; private set; }
        public string? ErrorMessage { get; private set; }
        public char[]? PasswordToReturn { get; init; }
        public Queue<char[]> PasswordsToReturn { get; } = new();
        public int RecoveryDialogCalls { get; private set; }
        public IArchiveClient? RecoveryClient { get; private set; }
        public RecoveredArchiveDialogResult? RecoveryResult { get; init; }

        public string? ChooseArchive() => null;

        public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation) => null;

        public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly) => null;

        public Task<char[]?> RequestPasswordAsync(
            string archiveName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<char[]?>(
                PasswordsToReturn.Count > 0 ? PasswordsToReturn.Dequeue() : PasswordToReturn);
        }

        public Task ShowArchiveRecoveryAsync(
            IArchiveClient client,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryDialogCalls++;
            RecoveryClient = client;
            return Task.CompletedTask;
        }

        public async Task<RecoveredArchiveDialogResult?> ShowArchiveRecoveryWithResultAsync(
            IArchiveClient client,
            CancellationToken cancellationToken)
        {
            await ShowArchiveRecoveryAsync(client, cancellationToken);
            return RecoveryResult;
        }

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

    private sealed class RefusingPreviewService : IEntryPreviewService
    {
        public Task<EntryPreviewResult> LoadTextAsync(
            ArchiveSessionId sessionId,
            ArchiveEntryDescriptor entry,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            Assert.IsNotNull(passwordSlotId);
            return Task.FromResult(
                EntryPreviewResult.Unavailable(EntryPreviewOutcome.Oversize));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingPreviewService(ArchiveErrorCategory category) : IEntryPreviewService
    {
        public Task<EntryPreviewResult> LoadTextAsync(
            ArchiveSessionId sessionId,
            ArchiveEntryDescriptor entry,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) =>
            throw new ArchiveClientException(new ArchiveError(
                "preview.failure",
                category,
                "Provider failure at C:\\provider-secret\\preview-cache"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PasswordRetryPreviewService : IEntryPreviewService
    {
        private int _calls;

        public Task<EntryPreviewResult> LoadTextAsync(
            ArchiveSessionId sessionId,
            ArchiveEntryDescriptor entry,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            Assert.IsNotNull(passwordSlotId);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new ArchiveClientException(new ArchiveError(
                    "archive.incorrect_password",
                    ArchiveErrorCategory.IncorrectPassword,
                    "The password is incorrect."));
            }

            return Task.FromResult(
                EntryPreviewResult.Available(
                    "protected payload",
                    EntryPreviewTextEncoding.Utf8,
                    17));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingPreviewService : IEntryPreviewService
    {
        private readonly EntryId _firstEntryId;
        private readonly EntryId _secondEntryId;

        public RecordingPreviewService(EntryId firstEntryId, EntryId secondEntryId)
        {
            _firstEntryId = firstEntryId;
            _secondEntryId = secondEntryId;
        }

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public bool FirstCancellationObserved { get; private set; }
        public bool Disposed { get; private set; }

        public async Task<EntryPreviewResult> LoadTextAsync(
            ArchiveSessionId sessionId,
            ArchiveEntryDescriptor entry,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (entry.EntryId == _firstEntryId && _firstEntryId != _secondEntryId)
            {
                FirstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCancellationObserved = true;
                    throw;
                }
            }

            Assert.AreEqual(_secondEntryId, entry.EntryId);
            return EntryPreviewResult.Available(
                "second payload",
                EntryPreviewTextEncoding.Utf8,
                14);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedCancellationPreviewService : IEntryPreviewService
    {
        private readonly EntryId _firstEntryId;
        private readonly EntryId _secondEntryId;

        public DelayedCancellationPreviewService(EntryId firstEntryId, EntryId secondEntryId)
        {
            _firstEntryId = firstEntryId;
            _secondEntryId = secondEntryId;
        }

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<EntryPreviewResult> LoadTextAsync(
            ArchiveSessionId sessionId,
            ArchiveEntryDescriptor entry,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (entry.EntryId == _firstEntryId)
            {
                FirstStarted.TrySetResult();
                using var registration = cancellationToken.Register(
                    () => FirstCancellationObserved.TrySetResult());
                await ReleaseFirst.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }

            Assert.AreEqual(_secondEntryId, entry.EntryId);
            SecondStarted.TrySetResult();
            return EntryPreviewResult.Available(
                "latest payload",
                EntryPreviewTextEncoding.Utf8,
                14);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LateCompletionPreviewService : IEntryPreviewService
    {
        private readonly EntryId _firstEntryId;
        private readonly EntryId _secondEntryId;

        public LateCompletionPreviewService(EntryId firstEntryId, EntryId secondEntryId)
        {
            _firstEntryId = firstEntryId;
            _secondEntryId = secondEntryId;
        }

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<EntryPreviewResult> LoadTextAsync(
            ArchiveSessionId sessionId,
            ArchiveEntryDescriptor entry,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (entry.EntryId == _firstEntryId)
            {
                FirstStarted.TrySetResult();
                using var registration = cancellationToken.Register(
                    () => FirstCancellationObserved.TrySetResult());
                await ReleaseFirst.Task;
                return EntryPreviewResult.Available(
                    "late payload",
                    EntryPreviewTextEncoding.Utf8,
                    12);
            }

            Assert.AreEqual(_secondEntryId, entry.EntryId);
            cancellationToken.ThrowIfCancellationRequested();
            return EntryPreviewResult.Available(
                "reopened payload",
                EntryPreviewTextEncoding.Utf8,
                16);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingArchiveClient : IArchiveClient
    {
        private readonly OperationEvent _terminal;
        private readonly TaskCompletionSource<OperationEvent>? _deferredTerminal;
        private readonly ArchiveSessionId _sessionId = ArchiveSessionId.New();

        public RecordingArchiveClient(
            OperationEvent terminal,
            bool deferTerminalUntilCancellation = false)
        {
            _terminal = terminal;
            _deferredTerminal = deferTerminalUntilCancellation
                ? new TaskCompletionSource<OperationEvent>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }

        public event EventHandler<OperationEvent>? OperationEventReceived
        {
            add { }
            remove { }
        }

        public int FileCreationCalls { get; private set; }
        public int TreeCreationCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int CreateSecretCalls { get; private set; }
        public int DiscardSecretCalls { get; private set; }
        public ArchiveSessionId OpenedSessionId => _sessionId;
        public List<ArchiveSessionId> ClosedSessions { get; } = [];
        public Guid SecretSlotId { get; } = Guid.NewGuid();
        public Guid? LastDiscardedSecretSlot { get; private set; }
        public string? DestinationPath { get; private set; }
        public ArchiveCompressionPreset CompressionPreset { get; private set; }
        public IReadOnlyList<ArchiveCreationFile> Files { get; private set; } = [];
        public IReadOnlyList<ArchiveCreationDirectory> Directories { get; private set; } = [];
        public TaskCompletionSource<bool> OperationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ArchiveClientException? StartException { get; init; }
        public Exception? OpenException { get; init; }
        public bool SupportsZipCreation { get; init; } = true;
        public bool SupportsZipDirectoryCreation { get; init; } = true;
        public bool SupportsRecoveryInspection { get; init; } = true;
        public bool SupportsRecoveryDisposition { get; init; } = true;
        public bool SupportsOtherRecoveryCenterFeatures { get; init; } = true;
        public GetZipWriteCapabilitiesResponse ZipWriteCapabilities { get; init; } =
            new(
                true, true, true, true, true, true, true, true,
                Enum.GetValues<ArchiveCompressionPreset>(),
                ProtocolConstants.MaximumZipCreationEntries,
                64L * 1024 * 1024 * 1024,
                false, false, false, false, false);
        public ArchiveCapability ArchiveCapabilities { get; init; } =
            ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract;
        public IReadOnlyList<ArchiveWarning> ArchiveWarnings { get; init; } = [];
        public IReadOnlyList<ArchiveEntryDescriptor> ListedEntries { get; init; } =
            [CreateEntry("alpha.txt")];

        public bool SupportsFeature(string feature) => feature switch
        {
            ProtocolFeatures.ZipCreation => SupportsZipCreation,
            ProtocolFeatures.ZipDirectoryCreation => SupportsZipDirectoryCreation,
            ProtocolFeatures.ArchiveCreationRecoveryInspection => SupportsRecoveryInspection,
            ProtocolFeatures.ArchiveCreationRecoveryDisposition => SupportsRecoveryDisposition,
            ProtocolFeatures.NestedWorkspaceRecovery => SupportsOtherRecoveryCenterFeatures,
            ProtocolFeatures.ExtractionRecoveryInspection => SupportsOtherRecoveryCenterFeatures,
            ProtocolFeatures.SupportDiagnostics => SupportsOtherRecoveryCenterFeatures,
            _ => true,
        };

        public Task<GetZipWriteCapabilitiesResponse> GetZipWriteCapabilitiesAsync(
            ArchiveSessionId? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ZipWriteCapabilities);
        }

        public Task<StartOperationResponse> StartZipCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StartException is not null)
            {
                throw StartException;
            }

            FileCreationCalls++;
            DestinationPath = destinationPath;
            Files = files;
            Directories = [];
            CompressionPreset = compressionPreset;
            OperationStarted.TrySetResult(true);
            return Task.FromResult(new StartOperationResponse(_terminal.OperationId));
        }

        public Task<StartOperationResponse> StartZipTreeCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            IReadOnlyList<ArchiveCreationDirectory> directories,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StartException is not null)
            {
                throw StartException;
            }

            TreeCreationCalls++;
            DestinationPath = destinationPath;
            Files = files;
            Directories = directories;
            CompressionPreset = compressionPreset;
            OperationStarted.TrySetResult(true);
            return Task.FromResult(new StartOperationResponse(_terminal.OperationId));
        }

        public Task<OperationEvent> WaitForTerminalAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(_terminal.OperationId, operationId);
            return _deferredTerminal?.Task ?? Task.FromResult(_terminal);
        }

        public Task<OpenArchiveResponse> OpenArchiveAsync(
            string sourcePath,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCalls++;
            if (OpenException is not null)
            {
                throw OpenException;
            }

            return Task.FromResult(new OpenArchiveResponse(new ArchiveDescriptor(
                _sessionId,
                "zip",
                "fake.zip",
                ArchiveCapabilities,
                new ArchiveFingerprint(1, DateTimeOffset.UtcNow, "fake", new string('0', 64)),
                ListedEntries.Count,
                false,
                false,
                false,
                true,
                ArchiveWarnings,
                ArchiveVolumeSetKind.Single)));
        }

        public Task<ListEntriesResponse> ListEntriesAsync(
            ArchiveSessionId sessionId,
            int offset = 0,
            int limit = 512,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ListEntriesResponse(
                ListedEntries,
                0,
                ListedEntries.Count,
                null));
        }

        public Task<CloseArchiveResponse> CloseArchiveAsync(
            ArchiveSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClosedSessions.Add(sessionId);
            return Task.FromResult(new CloseArchiveResponse(true));
        }

        public Task<CancelOperationResponse> CancelOperationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancelCalls++;
            _deferredTerminal?.TrySetResult(_terminal);
            return Task.FromResult(new CancelOperationResponse(true));
        }

        public Task<GetOperationActivityResponse> GetOperationActivityAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<OperationActivitySnapshot> operations = OperationStarted.Task.IsCompleted
                ? [new OperationActivitySnapshot(
                    _terminal.OperationId,
                    OperationKind.CreateArchive,
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

        public Task<GetSupportDiagnosticsResponse> GetSupportDiagnosticsAsync(
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<Guid> CreateSecretAsync(
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateSecretCalls++;
            return Task.FromResult(SecretSlotId);
        }

        public Task<bool> DiscardSecretAsync(
            Guid slotId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscardSecretCalls++;
            LastDiscardedSecretSlot = slotId;
            return Task.FromResult(true);
        }

        public Task<OpenNestedArchiveResponse> OpenNestedArchiveAsync(
            ArchiveSessionId parentSessionId,
            EntryId entryId,
            long maximumBytes = ProtocolConstants.DefaultNestedArchiveBytes,
            Guid? parentPasswordSlotId = null,
            Guid? nestedPasswordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArchiveEntryStreamLease> OpenEntryStreamAsync(
            ArchiveSessionId sessionId,
            EntryId entryId,
            long maximumBytes = ProtocolConstants.DefaultEntryStreamBytes,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StartOperationResponse> StartExtractionAsync(
            ArchiveSessionId sessionId,
            IReadOnlyList<EntryId> entryIds,
            string destinationPath,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StartOperationResponse> StartIntegrityTestAsync(
            ArchiveSessionId sessionId,
            IReadOnlyList<EntryId> entryIds,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ListArchiveCreationRecoveriesResponse> ListArchiveCreationRecoveriesAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InspectArchiveCreationRecoveryResponse> InspectArchiveCreationRecoveryAsync(
            string recoveryId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ApplyArchiveCreationRecoveryDispositionResponse>
            ApplyArchiveCreationRecoveryDispositionAsync(
                string recoveryId,
                ArchiveCreationRecoveryPhase expectedPhase,
                int expectedSequence,
                DateTimeOffset expectedRecordedAtUtc,
                ArchiveCreationRecoveryDispositionAction action,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
