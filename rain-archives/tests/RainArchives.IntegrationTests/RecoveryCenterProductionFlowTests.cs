using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class RecoveryCenterProductionFlowTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task RealWorkerModelsPreserveExactAuthorityAndReadOnlyExtractionBoundary()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var creationRecoveryRoot = Path.Combine(
            workerTemporaryRoot,
            "rainarchives-creation-recovery");
        var extractionRecoveryRoot = Path.Combine(
            workerTemporaryRoot,
            "rainarchives-extraction-recovery");

        var creationDestination = Path.Combine(root.Path, "creation-output.zip");
        var creationStore = new ArchiveCreationRecoveryStore(creationRecoveryRoot);
        var creationTransaction = await creationStore.BeginAsync(
            OperationId.New(),
            creationDestination,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path),
            CancellationToken.None);
        await creationTransaction.DisposeAsync();

        var extractionDestination = Path.Combine(root.Path, "extraction-output");
        var extractionStore = new ExtractionRecoveryStore(extractionRecoveryRoot);
        var extractionTransaction = await extractionStore.BeginAsync(
            OperationId.New(),
            extractionDestination,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path),
            CancellationToken.None);
        await extractionTransaction.DisposeAsync();

        var nestedRecoveryId = new string('c', 64);
        var nestedSourceId = new string('d', 64);
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + nestedRecoveryId;
        var recoveryWorkspace = Path.Combine(workerTemporaryRoot, workspaceName);
        Directory.CreateDirectory(recoveryWorkspace);
        await File.WriteAllBytesAsync(
            Path.Combine(recoveryWorkspace, OwnedNestedTemporaryWorkspace.OwnerFileName),
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        var sourceDirectory = Path.Combine(
            recoveryWorkspace,
            OwnedTemporaryArchiveSource.SourceDirectoryPrefix + nestedSourceId);
        Directory.CreateDirectory(sourceDirectory);
        var recoveredSource = Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName);
        CreateSourceArchive(recoveredSource);
        var recoveredSourceHash = await Sha256FileAsync(recoveredSource);

        await using var client = await ArchiveClient.StartWithIsolatedRecoveryRootsAsync(
            FindWorker(),
            workerTemporaryRoot);
        await using var creation = new ArchiveRecoveryCenterViewModel(client, _ => true);
        await using var extraction = new ExtractionRecoveryCenterViewModel(client);
        ReopenNestedWorkspaceRecoveryResponse? reopened = null;
        await using var nested = new NestedWorkspaceRecoveryCenterViewModel(
            client,
            _ => true,
            _ => true,
            requestPasswordSlot: null,
            result => reopened = result);
        await using var support = new RecoverySupportViewModel(client);

        await creation.InitializeAsync();
        Assert.HasCount(1, creation.Recoveries);
        Assert.IsFalse(creation.InventorySummaryText.Contains(creationDestination, StringComparison.OrdinalIgnoreCase));
        creation.SelectedRecovery = creation.Recoveries.Single();
        await creation.InspectSelectedAsync();
        Assert.AreEqual(
            UntrustedDisplayText.Escape(creationDestination),
            creation.DestinationPathText);
        Assert.IsTrue(creation.CanAcknowledgeNoOwnedArtifact);
        await creation.ApplyDispositionAsync(
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
        Assert.AreEqual(0, creationStore.Inspect().PotentialRecoveryCount);

        await extraction.InitializeAsync();
        Assert.HasCount(1, extraction.Recoveries);
        Assert.IsFalse(extraction.InventorySummaryText.Contains(extractionDestination, StringComparison.OrdinalIgnoreCase));
        extraction.SelectedRecovery = extraction.Recoveries.Single();
        await extraction.InspectSelectedAsync();
        Assert.AreEqual(
            UntrustedDisplayText.Escape(extractionDestination),
            extraction.DestinationPathText);
        StringAssert.Contains(extraction.ActionGuidance, "read-only");
        StringAssert.Contains(extraction.ActionGuidance, "Cleanup");
        Assert.AreEqual(1, extractionStore.Inspect().PotentialRecoveryCount);
        Assert.IsFalse(Directory.Exists(extractionDestination));

        await nested.InitializeAsync();
        Assert.HasCount(1, nested.Recoveries);
        Assert.IsFalse(nested.InventorySummaryText.Contains(workerTemporaryRoot, StringComparison.OrdinalIgnoreCase));
        nested.SelectedRecovery = nested.Recoveries.Single();
        await nested.InspectSelectedAsync();
        Assert.IsTrue(nested.CanReopen);
        Assert.IsTrue(nested.CanDiscard);
        Assert.HasCount(1, nested.Sources);
        await nested.ReopenInspectedAsync();
        Assert.IsNotNull(reopened);
        Assert.AreEqual(0, reopened.Depth);
        Assert.AreEqual(NestedWorkspaceRecoveryReopenMode.IndependentRoot, reopened.Mode);
        Assert.IsTrue((await client.ListEntriesAsync(reopened.Archive.SessionId, 0, 32))
            .Entries.Any(entry => entry.RawPath == "keep.txt"));
        Assert.IsTrue((await client.CloseArchiveAsync(reopened.Archive.SessionId)).Closed);
        CollectionAssert.AreEqual(recoveredSourceHash, await Sha256FileAsync(recoveredSource));

        await nested.RefreshAsync();
        nested.SelectedRecovery = nested.Recoveries.Single();
        await nested.InspectSelectedAsync();
        Assert.IsTrue(nested.CanDiscard);
        await nested.DiscardInspectedAsync();
        Assert.IsFalse(Directory.Exists(recoveryWorkspace));

        await support.InitializeAsync();
        Assert.IsTrue(support.Diagnostics.IsAvailable);
        StringAssert.Contains(
            support.Diagnostics.ProtocolText,
            $"{ProtocolConstants.MajorVersion}.{ProtocolConstants.MinorVersion}");
        StringAssert.Contains(support.Diagnostics.SandboxText, "Not a same-user filesystem sandbox");
        StringAssert.Contains(support.Diagnostics.RecoveryText, "unavailable");
    }

    private static void CreateSourceArchive(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        var entry = archive.CreateEntry("keep.txt", CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write("M5E recovered source");
    }

    private static async Task<byte[]> Sha256FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
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
                "RainArchives.RecoveryCenterProductionFlowTests",
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
