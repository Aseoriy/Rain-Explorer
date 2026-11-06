using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Cli;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class WorkerEndToEndTests
{
    [TestMethod]
    public void CliConfiguresBomlessUtf8ForRedirectedUnicodeOutput()
    {
        var original = Console.OutputEncoding;
        try
        {
            CliProgram.ConfigureConsoleEncoding();

            Assert.AreEqual(Encoding.UTF8.WebName, Console.OutputEncoding.WebName);
            Assert.HasCount(0, Console.OutputEncoding.GetPreamble());
        }
        finally
        {
            Console.OutputEncoding = original;
        }
    }

    [TestMethod]
    public async Task ListsTestsAndAtomicallyExtractsZip()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "sample.data");
        CreateZip(archivePath, ("folder/a.txt", "alpha"), ("b.txt", "beta"));
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        var opened = await client.OpenArchiveAsync(archivePath);
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 1024);

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.VolumeSetIdentity));
        Assert.AreEqual("zip", opened.Archive.Format, ignoreCase: true);
        Assert.AreEqual(ArchiveVolumeSetKind.Single, opened.Archive.VolumeSetKind);
        Assert.IsTrue(listed.Entries.Any(entry => entry.RawPath == "folder/a.txt"));
        Assert.IsTrue(listed.Entries.Any(entry => entry.RawPath == "b.txt"));

        var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
        var testTerminal = await client.WaitForTerminalAsync(tested.OperationId);
        Assert.AreEqual(OperationPhase.Completed, testTerminal.Phase);

        var destination = Path.Combine(root.Path, "extracted");
        var extracted = await client.StartExtractionAsync(opened.Archive.SessionId, [], destination);
        var extractTerminal = await client.WaitForTerminalAsync(extracted.OperationId);
        Assert.AreEqual(OperationPhase.Completed, extractTerminal.Phase, extractTerminal.Error?.Message);
        Assert.AreEqual("alpha", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "a.txt")));
        Assert.AreEqual("beta", await File.ReadAllTextAsync(Path.Combine(destination, "b.txt")));
    }

    [TestMethod]
    public async Task StreamsSelectedEntryThroughAuthenticatedBoundedDataPipe()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "preview.zip");
        CreateZip(archivePath, ("preview.txt", "preview payload"));
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16);
        var entry = listed.Entries.Single(item => item.RawPath == "preview.txt");

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.EntryStreamLeases));
        await using var lease = await client.OpenEntryStreamAsync(
            opened.Archive.SessionId,
            entry.EntryId,
            maximumBytes: 1024);
        using var reader = new StreamReader(
            lease.Content,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        Assert.AreEqual("preview payload", await reader.ReadToEndAsync());
        Assert.AreEqual(entry.EntryId, lease.EntryId);
        Assert.AreEqual(entry.OriginalSize, lease.DeclaredLength);
        Assert.AreEqual(1024, lease.MaximumBytes);
        Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
    }

    [TestMethod]
    public async Task DeclaredPreviewSizeOverLimitIsRejectedBeforeStreaming()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "preview-limit.zip");
        CreateZip(archivePath, ("too-large.txt", "five!"));
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenEntryStreamAsync(
                opened.Archive.SessionId,
                entry.EntryId,
                maximumBytes: 4));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, failure.Error.Category);
        Assert.AreEqual("entry.stream_declared_size_limit", failure.Error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DisposingUnreadPreviewReleasesSessionAndKeepsWorkerResponsive()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "unread-preview.zip");
        var content = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(content);
        CreateBinaryZip(archivePath, "large.bin", content);
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);
        await using var lease = await client.OpenEntryStreamAsync(
            opened.Archive.SessionId,
            entry.EntryId,
            maximumBytes: 8 * 1024 * 1024);

        var closed = await client
            .CloseArchiveAsync(opened.Archive.SessionId)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(closed.Closed);
        Assert.IsFalse(lease.Content.CanRead);
        var reopened = await client.OpenArchiveAsync(archivePath);
        Assert.AreEqual(ArchiveVolumeSetKind.Single, reopened.Archive.VolumeSetKind);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ExplicitNestedOpenEnforcesDepthAndCleansDescendantsWithParent()
    {
        using var root = new TemporaryDirectory();
        var deepest = CreateZipBytes("payload.txt", "nested payload"u8.ToArray());
        var levelThree = CreateZipBytes("next.bin", deepest);
        var levelTwo = CreateZipBytes("next.bin", levelThree);
        var levelOne = CreateZipBytes("next.bin", levelTwo);
        var rootBytes = CreateZipBytes("next.bin", levelOne);
        var archivePath = Path.Combine(root.Path, "root.data");
        await File.WriteAllBytesAsync(archivePath, rootBytes);
        var workerTemporaryRoot = CreateWorkerTemporaryRoot(root.Path);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.NestedArchiveOpen));
        var openedRoot = await client.OpenArchiveAsync(archivePath);
        var currentSessionId = openedRoot.Archive.SessionId;
        long expectedChainBytes = 0;

        for (var depth = 1; depth <= ProtocolConstants.MaximumNestedArchiveDepth; depth++)
        {
            var entry = (await client.ListEntriesAsync(currentSessionId, 0, 16))
                .Entries
                .Single(item => item.RawPath == "next.bin");
            var nested = await client.OpenNestedArchiveAsync(
                currentSessionId,
                entry.EntryId,
                maximumBytes: 1024 * 1024);
            expectedChainBytes += nested.MaterializedBytes;
            Assert.AreEqual(depth, nested.Depth);
            Assert.AreEqual(expectedChainBytes, nested.ChainMaterializedBytes);
            Assert.AreEqual("zip", nested.Archive.Format, ignoreCase: true);
            Assert.AreEqual(ArchiveVolumeSetKind.Single, nested.Archive.VolumeSetKind);
            currentSessionId = nested.Archive.SessionId;
        }

        Assert.HasCount(
            ProtocolConstants.MaximumNestedArchiveDepth,
            GetNestedSourceDirectories(workerTemporaryRoot));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(workerTemporaryRoot));

        var tooDeepEntry = (await client.ListEntriesAsync(currentSessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "next.bin");
        var tooDeep = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenNestedArchiveAsync(
                currentSessionId,
                tooDeepEntry.EntryId,
                maximumBytes: 1024 * 1024));
        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, tooDeep.Error.Category);
        Assert.AreEqual("nested.depth_limit", tooDeep.Error.Code);

        Assert.IsTrue((await client.CloseArchiveAsync(openedRoot.Archive.SessionId)).Closed);
        await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.ListEntriesAsync(currentSessionId, 0, 16));
        Assert.HasCount(0, GetNestedSourceDirectories(workerTemporaryRoot));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(workerTemporaryRoot));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task AuthenticatedProductionWorkerReviewsAndDiscardsStaleNestedWorkspace()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = CreateWorkerTemporaryRoot(root.Path);
        var recoveryId = new string('8', 64);
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        var workspacePath = Path.Combine(workerTemporaryRoot, workspaceName);
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllBytesAsync(
            Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName),
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        var sourceDirectory = Path.Combine(
            workspacePath,
            OwnedTemporaryArchiveSource.SourceDirectoryPrefix + new string('9', 64));
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName),
            [0x01, 0x02, 0x03]);

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.NestedWorkspaceRecovery));
        var listed = await client.ListNestedWorkspaceRecoveriesAsync();
        Assert.AreEqual(recoveryId, listed.Recoveries.Single().RecoveryId);
        var inspected = await client.InspectNestedWorkspaceRecoveryAsync(recoveryId);
        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
            inspected.Assessment);
        Assert.AreEqual(1, inspected.SourceCount);
        Assert.AreEqual(3L, inspected.TotalSourceBytes);

        var discarded = await client.DiscardNestedWorkspaceRecoveryAsync(
            recoveryId,
            inspected.Revision!,
            inspected.SourceCount!.Value,
            inspected.TotalSourceBytes!.Value);

        Assert.AreEqual(NestedWorkspaceRecoveryDiscardResult.Discarded, discarded.Result);
        Assert.IsFalse(Directory.Exists(workspacePath));
        Assert.HasCount(0, GetNestedWorkspaceDirectories(workerTemporaryRoot));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task AuthenticatedProductionWorkerReopensRecoveredSourceAsIndependentRootWithoutMutation()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = CreateWorkerTemporaryRoot(root.Path);
        var recoveryId = new string('6', 64);
        var sourceId = new string('7', 64);
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        var workspacePath = Path.Combine(workerTemporaryRoot, workspaceName);
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllBytesAsync(
            Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName),
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        var sourceDirectory = Path.Combine(
            workspacePath,
            OwnedTemporaryArchiveSource.SourceDirectoryPrefix + sourceId);
        Directory.CreateDirectory(sourceDirectory);
        var inner = CreateZipBytes("payload.txt", "recovered nested payload"u8.ToArray());
        var recoveredArchive = CreateZipBytes("nested.bin", inner);
        var sourcePath = Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName);
        await File.WriteAllBytesAsync(sourcePath, recoveredArchive);
        var originalHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(recoveredArchive));

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.NestedWorkspaceIndependentReopen));
        var inspection = await client.InspectNestedWorkspaceReopenAsync(recoveryId);
        var source = inspection.Sources!.Single();

        var reopened = await client.ReopenNestedWorkspaceRecoveryAsync(
            recoveryId,
            inspection.Revision!,
            inspection.SourceCount!.Value,
            inspection.TotalSourceBytes!.Value,
            source.SourceId,
            source.Length,
            source.ContentSha256);

        Assert.AreEqual(NestedWorkspaceRecoveryReopenMode.IndependentRoot, reopened.Mode);
        Assert.AreEqual(0, reopened.Depth);
        Assert.AreEqual(recoveredArchive.Length, reopened.MaterializedBytes);
        Assert.AreEqual("zip", reopened.Archive.Format, ignoreCase: true);
        Assert.IsTrue(Directory.Exists(workspacePath));
        Assert.AreEqual(originalHash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))));
        var activeWorkspace = GetNestedWorkspaceDirectories(workerTemporaryRoot)
            .Single(path => !string.Equals(path, workspacePath, StringComparison.OrdinalIgnoreCase));
        var reopenedCopyPath = Directory
            .EnumerateFiles(
                activeWorkspace,
                OwnedTemporaryArchiveSource.SourceFileName,
                SearchOption.AllDirectories)
            .Single();
        Assert.AreEqual(originalHash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(reopenedCopyPath))));

        var nestedEntry = (await client.ListEntriesAsync(reopened.Archive.SessionId, 0, 16))
            .Entries
            .Single(entry => entry.RawPath == "nested.bin");
        var nested = await client.OpenNestedArchiveAsync(
            reopened.Archive.SessionId,
            nestedEntry.EntryId,
            maximumBytes: 1024 * 1024);
        Assert.AreEqual(1, nested.Depth);
        Assert.IsTrue((await client.ListEntriesAsync(nested.Archive.SessionId, 0, 16))
            .Entries
            .Any(entry => entry.RawPath == "payload.txt"));

        Assert.IsTrue((await client.CloseArchiveAsync(reopened.Archive.SessionId)).Closed);
        Assert.IsTrue(Directory.Exists(workspacePath));
        Assert.AreEqual(originalHash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))));
        Assert.HasCount(
            0,
            Directory.EnumerateDirectories(
                activeWorkspace,
                OwnedTemporaryArchiveSource.SourceDirectoryPrefix + "*",
                SearchOption.TopDirectoryOnly).ToArray());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NestedRecoveryViewModelUsesProductionWorkerForEvidenceBoundDiscard()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = CreateWorkerTemporaryRoot(root.Path);
        var recoveryId = new string('a', 64);
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        var workspacePath = Path.Combine(workerTemporaryRoot, workspaceName);
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllBytesAsync(
            Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName),
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        var sourceDirectory = Path.Combine(
            workspacePath,
            OwnedTemporaryArchiveSource.SourceDirectoryPrefix + new string('b', 64));
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName),
            [0x01, 0x02, 0x03]);

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        NestedWorkspaceRecoveryDiscardConfirmation? confirmation = null;
        await using var viewModel = new NestedWorkspaceRecoveryCenterViewModel(
            client,
            request =>
            {
                confirmation = request;
                return true;
            });

        await viewModel.InitializeAsync();
        Assert.AreEqual(recoveryId, viewModel.Recoveries.Single().RecoveryId);
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.CanDiscard);

        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        await viewModel.InspectSelectedAsync();
        var inspectedRevision = viewModel.RevisionText;

        Assert.IsTrue(viewModel.HasInspection);
        Assert.IsTrue(viewModel.CanDiscard);
        Assert.AreEqual(recoveryId, viewModel.RecoveryIdText);
        Assert.AreEqual("1", viewModel.SourceCountText);
        StringAssert.Contains(viewModel.TotalSourceBytesText, "3 bytes");

        await viewModel.DiscardInspectedAsync();

        Assert.IsNotNull(confirmation);
        Assert.AreEqual(recoveryId, confirmation.RecoveryId);
        Assert.AreEqual(inspectedRevision, confirmation.Revision);
        Assert.AreEqual(1, confirmation.SourceCount);
        Assert.AreEqual(3L, confirmation.TotalSourceBytes);
        Assert.IsFalse(
            confirmation.Message.Contains(workerTemporaryRoot, StringComparison.OrdinalIgnoreCase),
            "The path-free recovery confirmation must not expose the Worker temporary root.");
        Assert.IsFalse(Directory.Exists(workspacePath));
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.HasRecoveries);
        Assert.IsFalse(viewModel.CanDiscard);
        StringAssert.Contains(viewModel.StatusText, "verified temporary workspace was discarded");
        Assert.HasCount(0, GetNestedWorkspaceDirectories(workerTemporaryRoot));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NestedDeclaredSizeLimitFailsBeforeTemporaryMaterialization()
    {
        using var root = new TemporaryDirectory();
        var nestedBytes = CreateZipBytes("payload.txt", "bounded"u8.ToArray());
        var archivePath = Path.Combine(root.Path, "nested-limit.zip");
        await File.WriteAllBytesAsync(archivePath, CreateZipBytes("nested.bin", nestedBytes));
        var workerTemporaryRoot = CreateWorkerTemporaryRoot(root.Path);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "nested.bin");

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenNestedArchiveAsync(
                opened.Archive.SessionId,
                entry.EntryId,
                maximumBytes: nestedBytes.Length - 1));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, failure.Error.Category);
        Assert.AreEqual("nested.declared_size_limit", failure.Error.Code);
        Assert.HasCount(1, (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16)).Entries);
        Assert.HasCount(0, GetNestedSourceDirectories(workerTemporaryRoot));
        Assert.HasCount(0, GetNestedWorkspaceDirectories(workerTemporaryRoot));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task HeaderEncryptedNestedArchiveRecoversAfterWrongChildPassword()
    {
        const string correctPassword = "RainArchives-Fixture-2026!";
        const string wrongPassword = "wrong-nested-password";
        using var root = new TemporaryDirectory();
        var encryptedPath = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "corpus",
            "archives",
            "7z",
            "header-encrypted.7z");
        var archivePath = Path.Combine(root.Path, "encrypted-child.zip");
        await File.WriteAllBytesAsync(
            archivePath,
            CreateZipBytes("child.bin", await File.ReadAllBytesAsync(encryptedPath)));
        var workerTemporaryRoot = CreateWorkerTemporaryRoot(root.Path);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "child.bin");

        var wrongSlot = await client.CreateSecretAsync(wrongPassword.AsMemory());
        var wrong = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenNestedArchiveAsync(
                opened.Archive.SessionId,
                entry.EntryId,
                nestedPasswordSlotId: wrongSlot));
        Assert.AreEqual(ArchiveErrorCategory.IncorrectPassword, wrong.Error.Category);
        Assert.IsFalse(wrong.Error.Message.Contains(wrongPassword, StringComparison.Ordinal));

        var correctSlot = await client.CreateSecretAsync(correctPassword.AsMemory());
        var child = await client.OpenNestedArchiveAsync(
            opened.Archive.SessionId,
            entry.EntryId,
            nestedPasswordSlotId: correctSlot);
        Assert.AreEqual("7z", child.Archive.Format, ignoreCase: true);
        Assert.IsTrue((await client.ListEntriesAsync(child.Archive.SessionId, 0, 16))
            .Entries
            .Any(item => item.RawPath == "alpha.txt"));
        Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
        Assert.HasCount(0, GetNestedSourceDirectories(workerTemporaryRoot));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(workerTemporaryRoot));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MalformedNestedPayloadFailsWithoutPublishingSessionOrTempSource()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "malformed-child.zip");
        await File.WriteAllBytesAsync(
            archivePath,
            CreateZipBytes("child.bin", "not an archive"u8.ToArray()));
        var workerTemporaryRoot = CreateWorkerTemporaryRoot(root.Path);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "child.bin");

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenNestedArchiveAsync(opened.Archive.SessionId, entry.EntryId));

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedFormat, failure.Error.Category);
        Assert.AreEqual("format.not_detected", failure.Error.Code);
        Assert.HasCount(1, (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16)).Entries);
        Assert.HasCount(0, GetNestedSourceDirectories(workerTemporaryRoot));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(workerTemporaryRoot));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NestedExtractionInheritsParentDownloadProvenance()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows alternate data streams are required for this test.");
        }

        using var root = new TemporaryDirectory();
        var childBytes = CreateZipBytes("payload.txt", "nested provenance"u8.ToArray());
        var archivePath = Path.Combine(root.Path, "downloaded-parent.zip");
        await File.WriteAllBytesAsync(archivePath, CreateZipBytes("child.bin", childBytes));
        await File.WriteAllTextAsync(
            archivePath + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://untrusted.invalid/nested.zip\r\n");
        var workerTemporaryRoot = CreateWorkerTemporaryRoot(root.Path);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .Single(item => item.RawPath == "child.bin");
        var child = await client.OpenNestedArchiveAsync(opened.Archive.SessionId, entry.EntryId);
        var destination = Path.Combine(root.Path, "nested-output");

        var started = await client.StartExtractionAsync(child.Archive.SessionId, [], destination);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(OperationPhase.Completed, terminal.Phase, terminal.Error?.Message);
        Assert.AreEqual("nested provenance", await File.ReadAllTextAsync(Path.Combine(destination, "payload.txt")));
        Assert.AreEqual(
            "[ZoneTransfer]\r\nZoneId=3\r\n",
            await File.ReadAllTextAsync(Path.Combine(destination, "payload.txt") + ":Zone.Identifier"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PasswordProtectedPreviewReportsWrongPasswordThenStreamsWithCorrectPassword()
    {
        const string fixturePassword = "RainArchives-Fixture-2026!";
        const string wrongPassword = "wrong-fixture-password";
        var archivePath = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "corpus",
            "archives",
            "zip",
            "aes256.zip");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16))
            .Entries
            .First(item => item.Kind == ArchiveEntryKind.File);

        var wrongSlot = await client.CreateSecretAsync(wrongPassword.AsMemory());
        await using (var wrongLease = await client.OpenEntryStreamAsync(
            opened.Archive.SessionId,
            entry.EntryId,
            passwordSlotId: wrongSlot))
        {
            var wrong = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                _ = await wrongLease.Content.ReadAsync(new byte[1024]));
            Assert.AreEqual(ArchiveErrorCategory.IncorrectPassword, wrong.Error.Category);
            Assert.IsFalse(wrong.Error.Message.Contains(wrongPassword, StringComparison.Ordinal));
        }

        var correctSlot = await client.CreateSecretAsync(fixturePassword.AsMemory());
        await using var correctLease = await client.OpenEntryStreamAsync(
            opened.Archive.SessionId,
            entry.EntryId,
            passwordSlotId: correctSlot);
        using var decoded = new MemoryStream();
        await correctLease.Content.CopyToAsync(decoded);

        Assert.IsGreaterThan(0, decoded.Length);
        Assert.AreEqual(entry.OriginalSize, decoded.Length);
    }

    [TestMethod]
    public async Task DuplicateEntryKeysRemainStableAcrossReopenWhileSessionIdsChange()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "duplicates.zip");
        CreateZip(archivePath, ("same.txt", "first"), ("same.txt", "second"));
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.PersistentEntryKeys));
        var firstOpen = await client.OpenArchiveAsync(archivePath);
        var firstEntries = (await client.ListEntriesAsync(firstOpen.Archive.SessionId, 0, 16))
            .Entries
            .Where(entry => entry.RawPath == "same.txt")
            .OrderBy(entry => entry.DuplicateOrdinal)
            .ToArray();
        await client.CloseArchiveAsync(firstOpen.Archive.SessionId);

        var secondOpen = await client.OpenArchiveAsync(archivePath);
        var secondEntries = (await client.ListEntriesAsync(secondOpen.Archive.SessionId, 0, 16))
            .Entries
            .Where(entry => entry.RawPath == "same.txt")
            .OrderBy(entry => entry.DuplicateOrdinal)
            .ToArray();

        Assert.HasCount(2, firstEntries);
        Assert.HasCount(2, secondEntries);
        CollectionAssert.AreEqual(
            new[] { (Path: "same.txt", Ordinal: 0), (Path: "same.txt", Ordinal: 1) },
            firstEntries.Select(entry => (entry.NormalizedPath, entry.DuplicateOrdinal)).ToArray());
        CollectionAssert.AreEqual(
            firstEntries.Select(entry => (entry.NormalizedPath, entry.DuplicateOrdinal)).ToArray(),
            secondEntries.Select(entry => (entry.NormalizedPath, entry.DuplicateOrdinal)).ToArray());
        Assert.IsTrue(firstEntries.Zip(secondEntries).All(pair => pair.First.EntryId != pair.Second.EntryId));
    }

    [TestMethod]
    public async Task ExtractsThroughWorkerWhenStagingRootExceedsLegacyMaxPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The production worker is Windows-specific.");
        }

        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "long-parent.zip");
        CreateZip(archivePath, ("safe.txt", "long-staging-root"));
        var longParent = Path.Combine(root.Path, "long-parent");
        Directory.CreateDirectory(longParent);
        var segment = 0;
        while (longParent.Length < 220)
        {
            longParent = Path.Combine(longParent, $"segment-{++segment:D2}-abcdefgh");
            Directory.CreateDirectory(longParent);
        }

        var destination = Path.Combine(longParent, "output");
        var representativeStaging = Path.Combine(
            longParent,
            $".output.rainarchives-{Guid.Empty:N}.staging");
        Assert.IsTrue(representativeStaging.Length > 260);
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);

        var started = await client.StartExtractionAsync(opened.Archive.SessionId, [], destination);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(OperationPhase.Completed, terminal.Phase, terminal.Error?.Message);
        Assert.AreEqual(
            "long-staging-root",
            await File.ReadAllTextAsync(Path.Combine(destination, "safe.txt")));
        Assert.HasCount(0, Directory.GetDirectories(longParent, "*.staging", SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    public async Task TraversalArchiveCannotCreateFinalDestinationOrEscapeFile()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "traversal.zip");
        CreateZip(archivePath, ("../outside.txt", "nope"));
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var destination = Path.Combine(root.Path, "output");

        var started = await client.StartExtractionAsync(opened.Archive.SessionId, [], destination);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(OperationPhase.Failed, terminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, terminal.Error!.Category);
        Assert.IsFalse(Directory.Exists(destination));
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "outside.txt")));
    }

    [TestMethod]
    public async Task SourceMutationIsDetectedOrPreventedByOpenHandle()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "mutable.zip");
        CreateZip(archivePath, ("a.txt", "alpha"));
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);

        try
        {
            await File.AppendAllTextAsync(archivePath, "changed after open");
        }
        catch (IOException)
        {
            var stillReadable = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 1024);
            Assert.IsTrue(stillReadable.Entries.Any(entry => entry.RawPath == "a.txt"));
            return;
        }

        var exception = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.ListEntriesAsync(opened.Archive.SessionId, 0, 1024));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Error.Category);
        Assert.AreEqual("archive.source_changed", exception.Error.Code);
    }

    [TestMethod]
    public async Task InternetZoneIsPropagatedAsCanonicalMotw()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows alternate data streams are required for this test.");
        }

        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "downloaded.zip");
        CreateZip(archivePath, ("a.txt", "alpha"));
        await File.WriteAllTextAsync(
            archivePath + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://untrusted.invalid/archive.zip\r\n");
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        var destination = Path.Combine(root.Path, "output");

        var started = await client.StartExtractionAsync(opened.Archive.SessionId, [], destination);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(OperationPhase.Completed, terminal.Phase, terminal.Error?.Message);
        Assert.AreEqual(
            "[ZoneTransfer]\r\nZoneId=3\r\n",
            await File.ReadAllTextAsync(Path.Combine(destination, "a.txt") + ":Zone.Identifier"));
    }

    private static void CreateZip(string archivePath, params (string Path, string Content)[] entries)
    {
        using var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Path, CompressionLevel.SmallestSize);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
    }

    private static void CreateBinaryZip(string archivePath, string entryPath, byte[] content)
    {
        using var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
        using var destination = entry.Open();
        destination.Write(content);
        Array.Clear(content);
    }

    private static byte[] CreateZipBytes(string entryPath, byte[] content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.SmallestSize);
            using var destination = entry.Open();
            destination.Write(content);
        }

        return stream.ToArray();
    }

    private static string CreateWorkerTemporaryRoot(string testRoot)
    {
        var path = Path.Combine(testRoot, "worker-temp");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] GetNestedWorkspaceDirectories(string rootPath) =>
        Directory.GetDirectories(
            rootPath,
            "rainarchives-worker-*",
            SearchOption.TopDirectoryOnly);

    private static string[] GetNestedSourceDirectories(string rootPath) =>
        GetNestedWorkspaceDirectories(rootPath)
            .SelectMany(workspacePath => Directory.GetDirectories(
                workspacePath,
                "rainarchives-nested-*",
                SearchOption.TopDirectoryOnly))
            .ToArray();

    private static string FindWorker()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
        }

        return worker;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.IntegrationTests",
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
