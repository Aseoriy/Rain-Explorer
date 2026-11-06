using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ProtocolBinaryCompatibilityTests
{
    private const string ProtocolTwelveArtifact =
        "RainArchives.ProtocolCompatibility.1.12.D0DFC81F";
    private const string ProtocolThirteenArtifact =
        "RainArchives.ProtocolCompatibility.1.13.4A27632C";
    private const string ProtocolFourteenArtifact =
        "RainArchives.ProtocolCompatibility.1.14.4B5F3A57";
    private const string ProtocolFifteenArtifact =
        "RainArchives.ProtocolCompatibility.1.15.6F595F0D";
    private const string ProtocolSixteenArtifact =
        "RainArchives.ProtocolCompatibility.1.16.8E6E9D17";
    private const string ProtocolSeventeenArtifact =
        "RainArchives.ProtocolCompatibility.1.17.64081B70";
    private const string ProtocolEighteenArtifact =
        "RainArchives.ProtocolCompatibility.1.18.1C85F6D9";

    [TestMethod]
    [DataRow(ProtocolTwelveArtifact, "1.12", false, false, false, false)]
    [DataRow(ProtocolThirteenArtifact, "1.13", true, false, false, false)]
    [DataRow(ProtocolFourteenArtifact, "1.14", true, true, false, false)]
    [DataRow(ProtocolFifteenArtifact, "1.15", true, true, true, false)]
    [DataRow(ProtocolSixteenArtifact, "1.16", true, true, true, true)]
    [DataRow(ProtocolSeventeenArtifact, "1.17", true, true, true, true)]
    [DataRow(ProtocolEighteenArtifact, "1.18", true, true, true, true)]
    [Timeout(180_000)]
    public async Task CurrentClientUsesCommonOperationsWithRetainedWorkerAndRefusesNewerOperationsLocally(
        string artifactName,
        string expectedProtocol,
        bool supportsRename,
        bool supportsTreeEdit,
        bool supportsMetadata,
        bool supportsWriteCapabilities)
    {
        using var root = new TemporaryDirectory();
        var repository = GetRepositoryRoot();
        var artifact = Path.Combine(repository, "artifacts", artifactName);
        var worker = Path.Combine(artifact, "worker", "RainArchives.Worker.exe");
        var source = Path.Combine(artifact, "fixture", "plain.zip");
        var destination = Path.Combine(root.Path, "rebuilt-by-old-worker.zip");
        var mappedSource = Path.Combine(root.Path, "mapped.txt");
        await File.WriteAllTextAsync(mappedSource, "current-client to old-worker payload\n");
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));

        await using var client = await ArchiveClient.StartAsync(worker);
        if (supportsWriteCapabilities)
        {
            Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities));
            var globalCapabilities = await client.GetZipWriteCapabilitiesAsync();
            Assert.IsTrue(globalCapabilities.CanCreate, expectedProtocol);
            Assert.IsFalse(globalCapabilities.CanRebuild, expectedProtocol);
        }
        else
        {
            var capabilities = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.GetZipWriteCapabilitiesAsync());
            Assert.AreEqual(
                "zip.write_capabilities_feature_not_negotiated",
                capabilities.Error.Code,
                expectedProtocol);
        }
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ZipRebuildCopy));
        Assert.AreEqual(supportsRename, client.SupportsFeature(ProtocolFeatures.ZipRebuildRename));
        Assert.AreEqual(supportsTreeEdit, client.SupportsFeature(ProtocolFeatures.ZipRebuildTreeEdit));
        Assert.AreEqual(
            supportsTreeEdit,
            client.SupportsFeature(ProtocolFeatures.ZipRebuildSourceFolderReview));
        var opened = await client.OpenArchiveAsync(source);
        if (supportsWriteCapabilities)
        {
            var sessionCapabilities = await client.GetZipWriteCapabilitiesAsync(opened.Archive.SessionId);
            Assert.IsTrue(sessionCapabilities.CanCreate, expectedProtocol);
            Assert.IsTrue(sessionCapabilities.CanRebuild, expectedProtocol);
        }
        var entries = await client.ListEntriesAsync(
            opened.Archive.SessionId,
            limit: ProtocolConstants.MaximumPageSize);
        Assert.AreEqual(3, entries.TotalCount);

        var integrity = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
        var integrityTerminal = await client.WaitForTerminalAsync(integrity.OperationId);
        Assert.AreEqual(OperationPhase.Completed, integrityTerminal.Phase);

        var rebuild = await client.StartZipRebuildCopyAsync(
            opened.Archive.SessionId,
            destination,
            [],
            [new ArchiveRebuildCopyFile(mappedSource, "compat-added.txt")]);
        var rebuildTerminal = await client.WaitForTerminalAsync(rebuild.OperationId);
        Assert.AreEqual(OperationPhase.CompletedWithWarnings, rebuildTerminal.Phase);
        Assert.IsTrue(File.Exists(destination));
        CollectionAssert.AreEqual(
            originalHash,
            SHA256.HashData(await File.ReadAllBytesAsync(source)));

        var renameDestination = Path.Combine(root.Path, "renamed-by-old-worker.zip");
        var rename = new ArchiveRebuildRename(
            new ArchiveEntryKey("alpha.txt", 0),
            "renamed/alpha.txt");
        if (supportsRename)
        {
            var renamed = await client.StartZipRebuildRenameAsync(
                opened.Archive.SessionId,
                renameDestination,
                [],
                [],
                [rename]);
            var renamedTerminal = await client.WaitForTerminalAsync(renamed.OperationId);
            Assert.AreEqual(OperationPhase.CompletedWithWarnings, renamedTerminal.Phase, expectedProtocol);
            Assert.IsTrue(File.Exists(renameDestination));
        }
        else
        {
            var renameSource = Path.Combine(root.Path, "source-that-does-not-exist.txt");
            var exception = await Assert.ThrowsAsync<ArchiveClientException>(() =>
                client.StartZipRebuildRenameAsync(
                    opened.Archive.SessionId,
                    renameDestination,
                    [],
                    [new ArchiveRebuildCopyFile(renameSource, "not-read.txt")],
                    [rename]));
            Assert.AreEqual("zip.rebuild_rename_feature_not_negotiated", exception.Error.Code);
            Assert.IsFalse(File.Exists(renameDestination));
            Assert.IsFalse(File.Exists(renameSource));
        }

        if (supportsTreeEdit)
        {
            _ = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
            var folderReviewSource = Path.Combine(root.Path, "folder-review-source");
            Directory.CreateDirectory(folderReviewSource);
            await File.WriteAllTextAsync(Path.Combine(folderReviewSource, "review.txt"), "review");
            var review = await client.GetZipRebuildSourceFolderReviewAsync(
                opened.Archive.SessionId,
                [new ArchiveCreationDirectory(folderReviewSource, "reviewed")]);
            Assert.AreEqual(1, review.Directories.Single().FileCount);
        }
        else
        {
            var treeQuery = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId));
            Assert.AreEqual("zip.rebuild_tree_edit_feature_not_negotiated", treeQuery.Error.Code);
            var folderReviewSource = Path.Combine(root.Path, "folder-review-source-that-does-not-exist");
            var folderReview = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.GetZipRebuildSourceFolderReviewAsync(
                    opened.Archive.SessionId,
                    [new ArchiveCreationDirectory(folderReviewSource, "added")]));
            Assert.AreEqual(
                "zip.rebuild_source_folder_review_feature_not_negotiated",
                folderReview.Error.Code);
            Assert.IsFalse(Directory.Exists(folderReviewSource));
            var treeDestination = Path.Combine(root.Path, "tree-must-not-exist.zip");
            var treeSource = Path.Combine(root.Path, "tree-source-that-does-not-exist");
            var treeStart = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.StartZipRebuildTreeEditAsync(
                    opened.Archive.SessionId,
                    treeDestination,
                    [],
                    [],
                    [],
                    [new ArchiveCreationDirectory(treeSource, "added")],
                    []));
            Assert.AreEqual("zip.rebuild_tree_edit_feature_not_negotiated", treeStart.Error.Code);
            Assert.IsFalse(File.Exists(treeDestination));
            Assert.IsFalse(Directory.Exists(treeSource));
        }
        var metadataDestination = Path.Combine(root.Path, "metadata-must-not-exist.zip");
        if (supportsMetadata)
        {
            var metadataStart = await client.StartZipRebuildMetadataAsync(
                opened.Archive.SessionId,
                metadataDestination,
                [],
                [],
                [],
                [],
                [],
                [new ArchiveRebuildMetadataDirective(
                    new ArchiveRebuildMetadataTarget(
                        ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                        new ArchiveEntryKey("alpha.txt", 0)),
                    AttributesLowWord: 0x0001)]);
            var metadataTerminal = await client.WaitForTerminalAsync(metadataStart.OperationId);
            Assert.AreEqual(OperationPhase.CompletedWithWarnings, metadataTerminal.Phase);
            Assert.IsTrue(File.Exists(metadataDestination));
        }
        else
        {
            var metadata = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.StartZipRebuildMetadataAsync(
                    opened.Archive.SessionId,
                    metadataDestination,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [new ArchiveRebuildMetadataDirective(
                        new ArchiveRebuildMetadataTarget(
                            ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                            new ArchiveEntryKey("alpha.txt", 0)),
                        AttributesLowWord: 0x0001)]));
            Assert.AreEqual("zip.rebuild_metadata_feature_not_negotiated", metadata.Error.Code);
            Assert.IsFalse(File.Exists(metadataDestination));
        }
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    [TestMethod]
    [DataRow(ProtocolTwelveArtifact, "1.12")]
    [DataRow(ProtocolThirteenArtifact, "1.13")]
    [DataRow(ProtocolFourteenArtifact, "1.14")]
    [DataRow(ProtocolFifteenArtifact, "1.15")]
    [DataRow(ProtocolSixteenArtifact, "1.16")]
    [DataRow(ProtocolSeventeenArtifact, "1.17")]
    [DataRow(ProtocolEighteenArtifact, "1.18")]
    [Timeout(180_000)]
    public async Task RetainedClientUsesCommonOperationsWithCurrentWorker(
        string artifactName,
        string expectedProtocol)
    {
        using var root = new TemporaryDirectory();
        var repository = GetRepositoryRoot();
        var artifact = Path.Combine(repository, "artifacts", artifactName);
        var harness = Path.Combine(
            artifact,
            "client",
            "RainArchives.ProtocolCompatibilityHarness.exe");
        var source = Path.Combine(artifact, "fixture", "plain.zip");
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        var outputRoot = Path.Combine(root.Path, "retained-client-output");
        Directory.CreateDirectory(outputRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = harness,
            WorkingDirectory = Path.GetDirectoryName(harness)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(worker);
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add(outputRoot);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the retained protocol client.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        var output = await standardOutput;
        var error = await standardError;

        Assert.AreEqual(0, process.ExitCode, $"stdout: {output}\nstderr: {error}");
        StringAssert.Contains(output, $"PASS protocol={expectedProtocol}");
        if (expectedProtocol is "1.15" or "1.16" or "1.17" or "1.18")
        {
            StringAssert.Contains(output, "metadataEntries=3");
        }
        if (expectedProtocol is "1.16" or "1.17" or "1.18")
        {
            StringAssert.Contains(output, "capabilities=managed-zip-v1");
        }
        if (expectedProtocol is "1.17" or "1.18")
        {
            StringAssert.Contains(output, "tarEntries=1");
            StringAssert.Contains(output, "tarCapabilities=managed-tar-v1");
            Assert.IsTrue(File.Exists(Path.Combine(outputRoot, "compat-created.tar")));
        }
        if (expectedProtocol == "1.18")
        {
            StringAssert.Contains(output, "managedEntries=1");
            StringAssert.Contains(output, "managedCapabilities=managed-streams-v1");
            Assert.IsTrue(File.Exists(Path.Combine(outputRoot, "compat-created.tar.gz")));
        }
        Assert.IsTrue(File.Exists(Path.Combine(outputRoot, "compat-created.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(outputRoot, "compat-rebuilt.zip")));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CurrentClientNegotiatesRetainedMinorSixteenWithoutTarFeatures()
    {
        var repository = GetRepositoryRoot();
        var artifact = Path.Combine(repository, "artifacts", ProtocolSixteenArtifact);
        var worker = Path.Combine(artifact, "worker", "RainArchives.Worker.exe");

        await using var client = await ArchiveClient.StartAsync(worker);

        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.TarCreation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.TarDirectoryCreation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CurrentClientUsesRetainedMinorSeventeenTarAndRefusesMinorEighteenBeforeAccess()
    {
        using var root = new TemporaryDirectory();
        var repository = GetRepositoryRoot();
        var artifact = Path.Combine(repository, "artifacts", ProtocolSeventeenArtifact);
        var worker = Path.Combine(artifact, "worker", "RainArchives.Worker.exe");
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.tar");
        await File.WriteAllTextAsync(source, "retained protocol 1.17 TAR payload\n");

        await using var client = await ArchiveClient.StartAsync(worker);

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.TarCreation));
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.TarDirectoryCreation));
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.TarGZipCreation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.TarGZipDirectoryCreation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.TarBZip2Creation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.TarBZip2DirectoryCreation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.GZipCreation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.BZip2Creation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities));

        var capabilities = await client.GetTarWriteCapabilitiesAsync();
        Assert.IsTrue(capabilities.CanCreate);
        Assert.IsFalse(capabilities.CanRebuild);
        Assert.IsTrue(capabilities.SupportsTreeCreation);
        CollectionAssert.AreEqual(
            new[] { ArchiveCompressionPreset.Store },
            capabilities.CompressionPresets.ToArray());
        Assert.IsFalse(capabilities.SupportsEncryption);
        Assert.IsFalse(capabilities.SupportsFilenameEncryption);
        Assert.IsFalse(capabilities.SupportsSplitOutput);
        Assert.IsFalse(capabilities.SupportsExistingDestinationReplacement);
        Assert.IsFalse(capabilities.SupportsInPlaceUpdate);

        var refusedDestination = Path.Combine(root.Path, "must-not-exist.tar.gz");
        var refusedSource = Path.Combine(root.Path, "source-that-does-not-exist.txt");
        var refused = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.StartTarGZipCreationAsync(
                refusedDestination,
                [new ArchiveCreationFile(refusedSource, "not-read.txt")]));
        Assert.AreEqual("managed.creation_feature_not_negotiated", refused.Error.Code);
        Assert.IsFalse(File.Exists(refusedDestination));
        Assert.IsFalse(File.Exists(refusedSource));

        var started = await client.StartTarCreationAsync(
            destination,
            [new ArchiveCreationFile(source, "source.txt")]);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);
        Assert.AreEqual(OperationPhase.Completed, terminal.Phase);

        await using var tarStream = new FileStream(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new TarReader(tarStream, leaveOpen: true);
        var entry = await reader.GetNextEntryAsync();
        Assert.IsNotNull(entry);
        Assert.AreEqual(TarEntryType.RegularFile, entry.EntryType);
        Assert.AreEqual("source.txt", entry.Name);
        Assert.IsNotNull(entry.DataStream);
        CollectionAssert.AreEqual(
            SHA256.HashData(await File.ReadAllBytesAsync(source)),
            await SHA256.HashDataAsync(entry.DataStream));
        Assert.IsNull(await reader.GetNextEntryAsync());

        var opened = await client.OpenArchiveAsync(destination);
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId);
        Assert.AreEqual(1, listed.TotalCount);
        Assert.AreEqual("source.txt", listed.Entries.Single().NormalizedPath);
        var integrity = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
        Assert.AreEqual(
            OperationPhase.Completed,
            (await client.WaitForTerminalAsync(integrity.OperationId)).Phase);
        await client.CloseArchiveAsync(opened.Archive.SessionId);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CurrentClientUsesRetainedMinorEighteenManagedCreationAndRefusesMinorNineteenLocally()
    {
        using var root = new TemporaryDirectory();
        var repository = GetRepositoryRoot();
        var artifact = Path.Combine(repository, "artifacts", ProtocolEighteenArtifact);
        var worker = Path.Combine(artifact, "worker", "RainArchives.Worker.exe");
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.gz");
        var payload = "retained protocol 1.18 managed payload\n"u8.ToArray();
        await File.WriteAllBytesAsync(source, payload);

        await using var client = await ArchiveClient.StartAsync(worker);

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities));
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.GZipCreation));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.OperationActivity));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.ExtractionRecoveryInspection));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.NestedWorkspaceIndependentReopen));
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.SupportDiagnostics));
        var refusal = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.GetOperationActivityAsync());
        Assert.AreEqual("operation.activity_feature_not_negotiated", refusal.Error.Code);
        var recoveryListRefusal = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.ListExtractionRecoveriesAsync());
        var recoveryInspectRefusal = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.InspectExtractionRecoveryAsync(new string('a', 64)));
        Assert.AreEqual(
            "extraction_recovery.inspection_feature_not_negotiated",
            recoveryListRefusal.Error.Code);
        Assert.AreEqual(recoveryListRefusal.Error.Code, recoveryInspectRefusal.Error.Code);
        var nestedReopenRefusal = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.InspectNestedWorkspaceReopenAsync(new string('b', 64)));
        Assert.AreEqual(
            "nested_recovery.reopen_feature_not_negotiated",
            nestedReopenRefusal.Error.Code);
        var diagnosticsRefusal = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.GetSupportDiagnosticsAsync());
        Assert.AreEqual(
            "worker.support_diagnostics_feature_not_negotiated",
            diagnosticsRefusal.Error.Code);

        var capabilities = await client.GetManagedWriteCapabilitiesAsync();
        Assert.HasCount(4, capabilities.Capabilities);
        Assert.IsTrue(capabilities.Capabilities.Single(capability =>
            capability.Format == ManagedArchiveCreationFormat.GZip).CanCreate);
        var started = await client.StartGZipCreationAsync(
            destination,
            source,
            ArchiveCompressionPreset.Store);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);
        Assert.AreEqual(OperationPhase.Completed, terminal.Phase);

        await using var compressed = new FileStream(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        using var decoded = new MemoryStream();
        await gzip.CopyToAsync(decoded);
        CollectionAssert.AreEqual(payload, decoded.ToArray());
        CollectionAssert.AreEqual(
            SHA256.HashData(payload),
            SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RainArchives.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Rain Archives workspace root.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ProtocolBinaryCompatibilityTests",
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
