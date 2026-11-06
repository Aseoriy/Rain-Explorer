using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.WinRar;
using RainArchives.Providers.SharpCompress;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class WinRarArchiveWriterProviderTests
{
    private const string QualificationVariable = "RAINARCHIVES_TEST_WINRAR_723";

    [TestMethod]
    public void InstalledEncoderProbeReturnsOneTypedClosedState()
    {
        var availability = WinRarEncoderProbe.ProbeInstalled();
        Assert.IsTrue(Enum.IsDefined(availability));

        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Assert.AreEqual(RarEncoderAvailability.UnsupportedPlatform, availability);
        }
        else if (!File.Exists(WinRarEncoderProbe.InstalledExecutablePath))
        {
            Assert.AreEqual(RarEncoderAvailability.NotInstalled, availability);
        }
    }

    [TestMethod]
    public void StartupMaintenanceRemovesAnAbandonedOwnedWorkspaceAndPreservesSiblingState()
    {
        _ = WinRarArchiveWriterProvider.PerformStartupMaintenance();
        var workspace = WinRarEncoderWorkspace.Create();
        var workspacePath = workspace.DirectoryPath;
        var maintenanceRoot = Path.GetDirectoryName(workspacePath)!;
        var sibling = Path.Combine(
            maintenanceRoot,
            "unrelated-sentinel-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(sibling, "preserve me");
        Directory.CreateDirectory(Path.Combine(workspacePath, "input"));
        File.WriteAllText(Path.Combine(workspacePath, "input", "source.bin"), "source copy");
        workspace.AbandonForTest();

        try
        {
            var maintenance = WinRarArchiveWriterProvider.PerformStartupMaintenance();
            Assert.IsTrue(maintenance.CleanedWorkspaceCount >= 1);
            Assert.IsFalse(Directory.Exists(workspacePath));
            Assert.AreEqual("preserve me", File.ReadAllText(sibling));
            Assert.IsFalse(maintenance.RequiresAttention);
        }
        finally
        {
            if (File.Exists(sibling))
            {
                File.Delete(sibling);
            }
        }
    }

    [TestMethod]
    public void StartupMaintenanceNeverRemovesAnActiveOwnedWorkspace()
    {
        _ = WinRarArchiveWriterProvider.PerformStartupMaintenance();
        using var workspace = WinRarEncoderWorkspace.Create();
        var workspacePath = workspace.DirectoryPath;

        var maintenance = WinRarArchiveWriterProvider.PerformStartupMaintenance();

        Assert.IsTrue(Directory.Exists(workspacePath));
        Assert.AreEqual(0, maintenance.CleanedWorkspaceCount);
        Assert.IsFalse(maintenance.RequiresAttention);
    }

    [TestMethod]
    public async Task RarMaintenanceRunsAfterHandshakeWithoutBlockingUnrelatedRequests()
    {
        using var maintenanceStarted = new ManualResetEventSlim();
        using var releaseMaintenance = new ManualResetEventSlim();
        await using var harness = await WorkerProtocolIntegrationTests.RawWorkerHarness
            .StartUnnegotiatedAsync(
                rarWorkspaceMaintenanceAction: () =>
                {
                    maintenanceStarted.Set();
                    _ = releaseMaintenance.Wait(TimeSpan.FromSeconds(20));
                    return new WinRarWorkspaceMaintenanceResult(0, 0, 0, false, false);
                });

        try
        {
            Assert.IsFalse(maintenanceStarted.IsSet);
            await harness.SendWelcomeAsync(
                ProtocolConstants.MinorVersion,
                ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
            Assert.IsTrue(maintenanceStarted.Wait(TimeSpan.FromSeconds(2)));

            var capability = await harness.SendAsync<
                GetZipWriteCapabilitiesRequest,
                GetZipWriteCapabilitiesResponse>(
                ProtocolMessageKinds.GetZipWriteCapabilities,
                new GetZipWriteCapabilitiesRequest());
            Assert.IsTrue(capability.CanCreate);
        }
        finally
        {
            releaseMaintenance.Set();
        }
    }

    [TestMethod]
    public void StartupMaintenanceUsesOneGlobalObjectBudgetWithoutPartialDeletion()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-RarMaintenanceBudget-" + Guid.NewGuid().ToString("N"));
        var maintenanceRoot = Path.Combine(fixtureRoot, "workspaces");
        Directory.CreateDirectory(maintenanceRoot);
        var workspace = WinRarEncoderWorkspace.Create(maintenanceRoot);
        var workspacePath = workspace.DirectoryPath;
        Directory.CreateDirectory(Path.Combine(workspacePath, "input"));
        File.WriteAllText(Path.Combine(workspacePath, "input", "source.bin"), "source");
        workspace.AbandonForTest();

        try
        {
            var bounded = WinRarArchiveWriterProvider.PerformStartupMaintenance(
                maintenanceRoot,
                maximumEntries: 1,
                maximumObjects: 1,
                maximumDuration: TimeSpan.FromSeconds(2));
            Assert.IsTrue(bounded.IsTruncated);
            Assert.IsTrue(Directory.Exists(workspacePath));
            Assert.IsTrue(File.Exists(Path.Combine(workspacePath, "owner.lock")));

            var recovered = WinRarArchiveWriterProvider.PerformStartupMaintenance(maintenanceRoot);
            Assert.AreEqual(1, recovered.CleanedWorkspaceCount);
            Assert.IsFalse(Directory.Exists(workspacePath));
        }
        finally
        {
            _ = WinRarArchiveWriterProvider.PerformStartupMaintenance(maintenanceRoot);
            if (Directory.Exists(maintenanceRoot))
            {
                Directory.Delete(maintenanceRoot, recursive: false);
            }

            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: false);
            }
        }
    }

    [TestMethod]
    public void WorkspaceRootCreationRefusesStableReparseAncestryBeforeWritingThroughIt()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-RarWorkspaceReparse-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(fixtureRoot, "outside");
        var link = Path.Combine(fixtureRoot, "linked-root");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
                WinRarEncoderWorkspace.Create(Path.Combine(link, "workspaces")));
            Assert.AreEqual("rar.workspace_ancestry_unsafe", exception.Code);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(link, recursive: false);
            Directory.Delete(outside, recursive: false);
            Directory.Delete(fixtureRoot, recursive: false);
        }
    }

    [TestMethod]
    public void StartupMaintenanceRefusesOwnerSymlinkWithoutReadingOutsideTarget()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-RarOwnerSymlink-" + Guid.NewGuid().ToString("N"));
        var maintenanceRoot = Path.Combine(fixtureRoot, "workspaces");
        var outside = Path.Combine(fixtureRoot, "outside.txt");
        Directory.CreateDirectory(maintenanceRoot);
        File.WriteAllText(outside, "outside sentinel");
        var workspace = WinRarEncoderWorkspace.Create(maintenanceRoot);
        var workspacePath = workspace.DirectoryPath;
        var ownerPath = Path.Combine(workspacePath, "owner.lock");
        workspace.AbandonForTest();
        File.Delete(ownerPath);
        File.CreateSymbolicLink(ownerPath, outside);

        try
        {
            var maintenance = WinRarArchiveWriterProvider.PerformStartupMaintenance(maintenanceRoot);
            Assert.IsTrue(maintenance.PreservedWorkspaceCount >= 1);
            Assert.IsTrue(Directory.Exists(workspacePath));
            Assert.AreEqual("outside sentinel", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(ownerPath);
            Directory.Delete(workspacePath, recursive: false);
            File.Delete(outside);
            Directory.Delete(maintenanceRoot, recursive: false);
            Directory.Delete(fixtureRoot, recursive: false);
        }
    }

    [TestMethod]
    public void UnverifiedEncoderTerminationRaisesContainmentFailure()
    {
        using var current = Process.GetCurrentProcess();
        Assert.ThrowsExactly<WinRarEncoderContainmentException>(() =>
            WinRarArchiveWriterProvider.TerminateEncoderProcessForTest(
                current,
                _ => { },
                (_, _) => false));
    }

    [TestMethod]
    [TestCategory("ExternalQualification")]
    public async Task DisposeReportsWorkspaceCleanupFailureAndPreservesOutsideTarget()
    {
        var executablePath = Environment.GetEnvironmentVariable(QualificationVariable);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-RarCleanupFailure-" + Guid.NewGuid().ToString("N"));
        var maintenanceRoot = Path.Combine(fixtureRoot, "workspaces");
        var outside = Path.Combine(fixtureRoot, "outside");
        Directory.CreateDirectory(maintenanceRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "preserve me");
        Assert.IsTrue(WinRarArchiveWriterProvider.TryCreateVerifiedExecutable(
            executablePath,
            maintenanceRoot,
            out var provider,
            out var availability));
        Assert.AreEqual(RarEncoderAvailability.Available, availability);
        Assert.IsNotNull(provider);

        IArchiveWriterHandle? writer = null;
        string? workspacePath = null;
        string? plantedLink = null;
        try
        {
            writer = await provider.CreateAsync(
                new MemoryStream(),
                ArchiveCompressionPreset.Store,
                CancellationToken.None);
            workspacePath = Directory.GetDirectories(maintenanceRoot).Single();
            plantedLink = Path.Combine(workspacePath, "input", "outside-link");
            Directory.CreateSymbolicLink(plantedLink, outside);

            var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(
                async () => await writer.DisposeAsync());
            Assert.AreEqual("rar.workspace_cleanup_failed", exception.Code);
            writer = null;
            Assert.AreEqual("preserve me", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
            Assert.IsTrue(Directory.Exists(workspacePath));
        }
        finally
        {
            if (writer is not null)
            {
                try
                {
                    await writer.DisposeAsync();
                }
                catch
                {
                }
            }

            if (plantedLink is not null && Directory.Exists(plantedLink))
            {
                Directory.Delete(plantedLink, recursive: false);
            }

            _ = WinRarArchiveWriterProvider.PerformStartupMaintenance(maintenanceRoot);
            File.Delete(Path.Combine(outside, "sentinel.txt"));
            Directory.Delete(outside, recursive: false);
            Directory.Delete(maintenanceRoot, recursive: false);
            Directory.Delete(fixtureRoot, recursive: false);
        }
    }

    [TestMethod]
    [Timeout(45_000)]
    public async Task ExactPidHardKillLeavesOnlyTokenOwnedWorkspaceThatStartupMaintenanceRemoves()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-RarWorkspaceCrash-" + Guid.NewGuid().ToString("N"));
        var maintenanceRoot = Path.Combine(fixtureRoot, "workspaces");
        Directory.CreateDirectory(maintenanceRoot);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var readyPath = Path.Combine(fixtureRoot, "ready-" + nonce + ".txt");
        var sibling = Path.Combine(
            maintenanceRoot,
            "unrelated-hard-kill-sentinel-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(sibling, "preserve me");

        Process? process = null;
        string? workspacePath = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FindRecoveryCrashProbe(),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--rar-workspace-checkpoint");
            startInfo.ArgumentList.Add(maintenanceRoot);
            startInfo.ArgumentList.Add(readyPath);
            startInfo.ArgumentList.Add(nonce);
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The RAR workspace crash probe did not start.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(readyPath))
                    {
                        var lines = await File.ReadAllLinesAsync(readyPath, timeout.Token);
                        Assert.HasCount(2, lines);
                        Assert.AreEqual($"READY:{nonce}", lines[0]);
                        workspacePath = Path.GetFullPath(lines[1]);
                        break;
                    }
                }
                catch (IOException)
                {
                    // The probe may still be durably closing the signal file.
                }

                await Task.Delay(10, timeout.Token);
            }

            Assert.IsNotNull(workspacePath, "The RAR workspace checkpoint was not reached.");
            Assert.IsTrue(Directory.Exists(workspacePath));
            Assert.IsTrue(File.Exists(Path.Combine(
                workspacePath,
                "input",
                "materialized-source.bin")));

            var activeMaintenance = WinRarArchiveWriterProvider.PerformStartupMaintenance(maintenanceRoot);
            Assert.IsTrue(Directory.Exists(workspacePath));
            Assert.AreEqual(0, activeMaintenance.CleanedWorkspaceCount);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var recovered = WinRarArchiveWriterProvider.PerformStartupMaintenance(maintenanceRoot);
            Assert.IsTrue(recovered.CleanedWorkspaceCount >= 1);
            Assert.IsFalse(Directory.Exists(workspacePath));
            Assert.AreEqual("preserve me", File.ReadAllText(sibling));
            Assert.IsFalse(recovered.RequiresAttention);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            process?.Dispose();
            File.Delete(readyPath);
            File.Delete(sibling);
            _ = WinRarArchiveWriterProvider.PerformStartupMaintenance(maintenanceRoot);
            if (Directory.Exists(maintenanceRoot))
            {
                Directory.Delete(maintenanceRoot, recursive: false);
            }

            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: false);
            }
        }
    }

    [TestMethod]
    public async Task StructurallyRarShapedProviderCannotClaimQualifiedEncoderAuthority()
    {
        await using var harness = await WorkerProtocolIntegrationTests.RawWorkerHarness
            .StartUnnegotiatedAsync(
                providers: [new SharpCompressArchiveProvider()],
                writerProviders: [new StructurallyRarShapedProvider()]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var capability = await harness.SendAsync<
            GetRarWriteCapabilitiesRequest,
            GetRarWriteCapabilitiesResponse>(
            ProtocolMessageKinds.GetRarWriteCapabilities,
            new GetRarWriteCapabilitiesRequest());

        Assert.IsFalse(capability.CanCreate);
        Assert.IsFalse(capability.SupportsTreeCreation);
        var installed = WinRarEncoderProbe.ProbeInstalled();
        Assert.AreEqual(
            installed == RarEncoderAvailability.Available
                ? RarEncoderAvailability.RestartRequired
                : installed,
            capability.Availability);
    }

    [TestMethod]
    [TestCategory("ExternalQualification")]
    public async Task RemovedPreviouslyVerifiedEncoderIsNoLongerAdvertised()
    {
        var executablePath = Environment.GetEnvironmentVariable(QualificationVariable);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-RarRemovedEncoder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var copiedExecutable = Path.Combine(root, "Rar.exe");
            File.Copy(executablePath, copiedExecutable);
            Assert.IsTrue(WinRarArchiveWriterProvider.TryCreateVerifiedExecutable(
                copiedExecutable,
                out var provider,
                out var availability));
            Assert.AreEqual(RarEncoderAvailability.Available, availability);
            Assert.IsNotNull(provider);
            File.Delete(copiedExecutable);

            await using var harness = await WorkerProtocolIntegrationTests.RawWorkerHarness
                .StartUnnegotiatedAsync(
                    providers: [new SharpCompressArchiveProvider()],
                    writerProviders: [provider]);
            await harness.SendWelcomeAsync(
                ProtocolConstants.MinorVersion,
                ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
            var capability = await harness.SendAsync<
                GetRarWriteCapabilitiesRequest,
                GetRarWriteCapabilitiesResponse>(
                ProtocolMessageKinds.GetRarWriteCapabilities,
                new GetRarWriteCapabilitiesRequest());

            Assert.IsFalse(capability.CanCreate);
            Assert.AreEqual(RarEncoderAvailability.UntrustedBinary, capability.Availability);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    [TestCategory("ExternalQualification")]
    public async Task ExplicitSignedEncoderCreatesAndValidatesExactRar5OutputWhenProvided()
    {
        var executablePath = Environment.GetEnvironmentVariable(QualificationVariable);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        Assert.IsTrue(WinRarArchiveWriterProvider.TryCreateVerifiedExecutable(
            executablePath,
            out var provider,
            out var availability));
        Assert.AreEqual(RarEncoderAvailability.Available, availability);
        Assert.IsNotNull(provider);

        var timestamp = new DateTimeOffset(2026, 9, 4, 12, 34, 56, TimeSpan.Zero);
        var payload = "Rain Archives qualified RAR output."u8.ToArray();
        var destination = new MemoryStream();
        await using (var writer = await provider.CreateAsync(
            destination,
            ArchiveCompressionPreset.Maximum,
            CancellationToken.None))
        {
            await writer.AddDirectoryAsync(
                new ArchiveWriteDirectory("empty", timestamp),
                CancellationToken.None);
            await writer.AddFileAsync(
                new ArchiveWriteEntry("Unicode/空.txt", payload.Length, timestamp),
                new MemoryStream(payload, writable: false),
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await writer.AddFileAsync(
                new ArchiveWriteEntry("zero.bin", 0, timestamp),
                new MemoryStream([], writable: false),
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        Assert.IsTrue(destination.CanRead);
        Assert.IsTrue(destination.Length > 0);
        destination.Position = 0;
        await provider.ValidateAsync(
            destination,
            [
                new ArchiveStagedOutputManifestEntry(
                    "empty",
                    ArchiveEntryKind.Directory,
                    null,
                    timestamp),
                new ArchiveStagedOutputManifestEntry(
                    "Unicode/空.txt",
                    ArchiveEntryKind.File,
                    payload.Length,
                    timestamp,
                    Convert.ToHexString(SHA256.HashData(payload))),
                new ArchiveStagedOutputManifestEntry(
                    "zero.bin",
                    ArchiveEntryKind.File,
                    0,
                    timestamp,
                    Convert.ToHexString(SHA256.HashData([]))),
            ],
            CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("ExternalQualification")]
    public async Task ProtocolTwentyThreeWorkerCreatesRarThroughStagedNoReplaceRouteWhenProvided()
    {
        var executablePath = Environment.GetEnvironmentVariable(QualificationVariable);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        Assert.IsTrue(WinRarArchiveWriterProvider.TryCreateVerifiedExecutable(
            executablePath,
            out var provider,
            out var availability));
        Assert.AreEqual(RarEncoderAvailability.Available, availability);
        Assert.IsNotNull(provider);

        var root = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-RarWorkerQualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source 雨.txt");
            var destinationPath = Path.Combine(root, "created output.rar");
            var recoveryRoot = Path.Combine(root, "recovery");
            var nestedRoot = Path.Combine(root, "nested");
            var extractionRecoveryRoot = Path.Combine(root, "extraction-recovery");
            Directory.CreateDirectory(recoveryRoot);
            Directory.CreateDirectory(nestedRoot);
            Directory.CreateDirectory(extractionRecoveryRoot);
            var payload = "Protocol 1.23 RAR creation proof."u8.ToArray();
            await File.WriteAllBytesAsync(sourcePath, payload);
            var sourceHash = await HashFileAsync(sourcePath);

            await using var harness = await WorkerProtocolIntegrationTests.RawWorkerHarness
                .StartUnnegotiatedAsync(
                    providers: [new SharpCompressArchiveProvider()],
                    nestedTemporaryRoot: nestedRoot,
                    writerProviders: [provider],
                    creationRecoveryRoot: recoveryRoot,
                    extractionRecoveryRoot: extractionRecoveryRoot);
            await harness.SendWelcomeAsync(
                ProtocolConstants.MinorVersion,
                ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

            var capability = await harness.SendAsync<
                GetRarWriteCapabilitiesRequest,
                GetRarWriteCapabilitiesResponse>(
                ProtocolMessageKinds.GetRarWriteCapabilities,
                new GetRarWriteCapabilitiesRequest());
            Assert.IsTrue(capability.CanCreate);
            Assert.IsTrue(capability.SupportsTreeCreation);
            Assert.AreEqual(RarEncoderAvailability.Available, capability.Availability);

            var started = await harness.SendAsync<StartRarCreationRequest, StartOperationResponse>(
                ProtocolMessageKinds.StartRarCreation,
                new StartRarCreationRequest(
                    destinationPath,
                    [new ArchiveCreationFile(sourcePath, "source 雨.txt")],
                    ArchiveCompressionPreset.Fast));

            OperationEvent? terminal = null;
            while (terminal is null)
            {
                var frame = await harness.ReadFrameAsync();
                Assert.AreEqual(ProtocolMessageKinds.OperationEvent, frame.Kind);
                var operationEvent = frame.ReadPayload<OperationEvent>();
                Assert.AreEqual(started.OperationId, operationEvent.OperationId);
                if (operationEvent.Kind == OperationEventKind.Terminal)
                {
                    terminal = operationEvent;
                }
            }

            Assert.IsTrue(terminal.Phase is OperationPhase.Completed
                or OperationPhase.CompletedWithWarnings);
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.AreEqual(sourceHash, await HashFileAsync(sourcePath));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(recoveryRoot));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 80 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string FindRecoveryCrashProbe()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RainArchives.RecoveryCrashProbe.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "The integration test output does not contain the recovery crash probe.",
                path);
    }

    private sealed class StructurallyRarShapedProvider :
        IArchiveWriterProvider,
        IArchiveStagedOutputValidator
    {
        public string Id => "test/rar-shape-only";
        public string Format => "rar";
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets { get; } =
            Enum.GetValues<ArchiveCompressionPreset>().ToHashSet();

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A shape-only provider must never be invoked.");

        public ValueTask ValidateAsync(
            Stream archive,
            IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A shape-only provider must never be invoked.");
    }
}
