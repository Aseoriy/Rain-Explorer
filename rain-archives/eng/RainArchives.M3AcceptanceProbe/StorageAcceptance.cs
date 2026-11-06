using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.M3AcceptanceProbe;

internal static partial class Program
{
    private const string DisposableMarkerName = ".rainarchives-m3-disposable";
    private const string ExpectedNtfsLabel = "RAINM3NTFS";
    private const string ExpectedExFatLabel = "RAINM3EXFAT";
    private const long DiskFullSourceBytes = 128L * 1024 * 1024;

    private static async Task<int> RunStorageAcceptanceMainAsync(string[] args)
    {
        if (!TryParseStorageArguments(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: RainArchives.M3AcceptanceProbe --package <artifact-root> --evidence <new-directory> "
                + "--storage-ntfs <owned-volume-root> --storage-nonntfs <owned-volume-root> "
                + "--acl-denied <system-owned-denied-parent> --ownership-token <guid-n>");
            return 2;
        }

        var evidenceRoot = Path.GetFullPath(options.EvidenceRoot);
        if (Directory.Exists(evidenceRoot) || File.Exists(evidenceRoot))
        {
            Console.Error.WriteLine("The evidence directory must not already exist.");
            return 2;
        }

        Directory.CreateDirectory(evidenceRoot);
        try
        {
            var result = await RunStorageAcceptanceAsync(options).ConfigureAwait(false);
            await WriteEvidenceAsync(
                    Path.Combine(evidenceRoot, "storage-evidence.json"),
                    result)
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            var failure = new FailureEvidence(
                DateTimeOffset.UtcNow,
                exception.GetType().FullName ?? exception.GetType().Name,
                Sanitize(exception.Message),
                exception.StackTrace is null ? null : Sanitize(exception.StackTrace));
            await WriteEvidenceAsync(
                    Path.Combine(evidenceRoot, "storage-failure.json"),
                    failure)
                .ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<StorageAcceptanceEvidence> RunStorageAcceptanceAsync(
        StorageProbeOptions options)
    {
        Require(
            Guid.TryParseExact(options.OwnershipToken, "N", out _),
            "The disposable-volume ownership token must be a GUID in N format.");
        Require(!IsElevated(), "The storage acceptance runner must execute under a non-elevated token.");

        var package = await ValidateStoragePackageAsync(options.PackageArtifactRoot).ConfigureAwait(false);
        var ntfs = ValidateDisposableVolume(
            options.NtfsRoot,
            options.OwnershipToken,
            ExpectedNtfsLabel,
            "NTFS");
        var nonNtfs = ValidateDisposableVolume(
            options.NonNtfsRoot,
            options.OwnershipToken,
            ExpectedExFatLabel,
            "exFAT");
        Require(
            !string.Equals(ntfs.Root, nonNtfs.Root, StringComparison.OrdinalIgnoreCase),
            "The NTFS and non-NTFS acceptance roots must be different disposable volumes.");
        var aclDeniedParent = ValidateAclDeniedParent(
            options.AclDeniedParent,
            ntfs.Root,
            options.OwnershipToken);

        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-M3-Storage",
            options.OwnershipToken);
        var ntfsWork = Path.Combine(ntfs.Root, options.OwnershipToken);
        var nonNtfsWork = Path.Combine(nonNtfs.Root, options.OwnershipToken);
        Require(!Directory.Exists(sourceRoot), "The storage source root already exists.");
        Require(!Directory.Exists(ntfsWork), "The disposable NTFS work root already exists.");
        Require(!Directory.Exists(nonNtfsWork), "The disposable non-NTFS work root already exists.");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(ntfsWork);
        Directory.CreateDirectory(nonNtfsWork);

        AclDenialEvidence acl;
        IReadOnlyList<DiskFullEvidence> diskFull;
        NonNtfsEvidence nonNtfsEvidence;
        try
        {
            acl = await RunAclDenialAsync(
                    package.WorkerPath,
                    sourceRoot,
                    ntfsWork,
                    aclDeniedParent)
                .ConfigureAwait(false);
            diskFull =
            [
                await RunDiskFullAsync(
                        package.WorkerPath,
                        sourceRoot,
                        ntfsWork,
                        "early",
                        1L * 1024 * 1024)
                    .ConfigureAwait(false),
                await RunDiskFullAsync(
                        package.WorkerPath,
                        sourceRoot,
                        ntfsWork,
                        "near-final",
                        DiskFullSourceBytes * 4 / 5)
                    .ConfigureAwait(false),
            ];
            nonNtfsEvidence = await RunNonNtfsAsync(
                    package.WorkerPath,
                    sourceRoot,
                    nonNtfsWork)
                .ConfigureAwait(false);

            Require(CountStaging(ntfsWork) == 0, "The NTFS acceptance root contains staging residue.");
            Require(CountStaging(nonNtfsWork) == 0, "The non-NTFS acceptance root contains staging residue.");
        }
        finally
        {
            DeleteExactOwnedVolumeChild(ntfsWork, ntfs.Root, options.OwnershipToken);
            DeleteExactOwnedVolumeChild(nonNtfsWork, nonNtfs.Root, options.OwnershipToken);
            DeleteExactOwnedTree(sourceRoot, "RainArchives-M3-Storage");
        }

        await WaitForNoRainProcessesAsync().ConfigureAwait(false);
        var remainingProcesses = GetRainProcesses();
        Require(remainingProcesses.Count == 0, "Rain processes remained after storage acceptance.");
        Require(!Directory.Exists(ntfsWork), "The disposable NTFS work root was not removed.");
        Require(!Directory.Exists(nonNtfsWork), "The disposable non-NTFS work root was not removed.");
        Require(!Directory.Exists(sourceRoot), "The disposable source root was not removed.");

        return new StorageAcceptanceEvidence(
            SchemaVersion: 1,
            RecordedUtc: DateTimeOffset.UtcNow,
            Protocol: ExpectedProtocol,
            SourceInputCount: ExpectedSourceInputs,
            SourceSha256: ExpectedSourceSha256,
            Package: package.Evidence,
            MachineName: Environment.MachineName,
            OperatingSystem: Environment.OSVersion.VersionString,
            RuntimeVersion: Environment.Version.ToString(),
            UserName: WindowsIdentity.GetCurrent().Name,
            UserSid: WindowsIdentity.GetCurrent().User?.Value,
            IsElevated: IsElevated(),
            NtfsVolume: ntfs,
            NonNtfsVolume: nonNtfs,
            AclDenial: acl,
            DiskFull: diskFull,
            NonNtfs: nonNtfsEvidence,
            RemainingRainProcesses: remainingProcesses,
            RemainingStagingArtifacts: 0,
            OwnedWorkRootsRemoved: true);
    }

    private static async Task<StoragePackage> ValidateStoragePackageAsync(string packageArtifactRoot)
    {
        var artifactRoot = Path.GetFullPath(packageArtifactRoot);
        var appRoot = Path.Combine(artifactRoot, "app");
        var workerPath = Path.Combine(appRoot, "worker", "RainArchives.Worker.exe");
        var manifestPath = Path.Combine(artifactRoot, "package-manifest.json");
        RequireDirectory(artifactRoot, "package artifact root");
        RequireDirectory(appRoot, "App package root");
        RequireFile(workerPath, "private packaged Worker");
        RequireFile(manifestPath, "package manifest");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
                await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false),
                JsonOptions)
            ?? throw new InvalidDataException("The package manifest is empty.");
        Require(manifest.SchemaVersion == 1, "The package manifest schema changed.");
        Require(manifest.Protocol == ExpectedProtocol, "The package protocol identity changed.");
        Require(manifest.SourceInputCount == ExpectedSourceInputs, "The package source count changed.");
        Require(manifest.SourceSha256 == ExpectedSourceSha256, "The package source fingerprint changed.");
        foreach (var pair in manifest.Files)
        {
            var path = Path.Combine(
                artifactRoot,
                pair.Key.Replace('/', Path.DirectorySeparatorChar));
            RequireFile(path, $"manifest file {pair.Key}");
            Require(Sha256File(path) == pair.Value, $"Manifest hash changed for {pair.Key}.");
        }

        var forbiddenAssets = Directory
            .EnumerateFiles(appRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals("7z.dll", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("7za.exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("SharpSevenZip.dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(appRoot, path).Replace('\\', '/'))
            .ToArray();
        Require(forbiddenAssets.Length == 0, "A forbidden native/provider asset entered the package.");

        return new StoragePackage(
            workerPath,
            new StoragePackageEvidence(
                artifactRoot,
                Sha256File(manifestPath),
                Sha256File(workerPath),
                manifest.Files,
                forbiddenAssets));
    }

    private static DisposableVolumeEvidence ValidateDisposableVolume(
        string path,
        string ownershipToken,
        string expectedLabel,
        string expectedFormat)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("The disposable volume path has no root.");
        Require(
            string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase),
            "A disposable storage path must be the exact root of its mounted volume.");
        RequireDirectory(root, "disposable mounted volume");
        Require(
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0,
            "The disposable volume root is a reparse point.");

        var drive = new DriveInfo(root);
        Require(drive.IsReady, "The disposable mounted volume is not ready.");
        Require(
            drive.DriveType == DriveType.Fixed,
            $"The disposable volume must be an isolated fixed VHD, not {drive.DriveType}.");
        Require(
            string.Equals(drive.VolumeLabel, expectedLabel, StringComparison.Ordinal),
            $"The disposable volume label is not {expectedLabel}.");
        Require(
            string.Equals(drive.DriveFormat, expectedFormat, StringComparison.OrdinalIgnoreCase),
            $"The disposable volume format is not {expectedFormat}.");

        var marker = Path.Combine(root, DisposableMarkerName);
        RequireFile(marker, "disposable volume ownership marker");
        Require(
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0,
            "The disposable volume ownership marker is a reparse point.");
        Require(
            string.Equals(File.ReadAllText(marker, Encoding.ASCII), ownershipToken, StringComparison.Ordinal),
            "The disposable volume ownership marker does not match this run.");

        return new DisposableVolumeEvidence(
            root,
            drive.VolumeLabel,
            drive.DriveFormat,
            drive.DriveType.ToString(),
            drive.TotalSize,
            drive.AvailableFreeSpace,
            ownershipToken,
            Sha256File(marker));
    }

    private static string ValidateAclDeniedParent(
        string path,
        string expectedNtfsRoot,
        string ownershipToken)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Directory.GetParent(fullPath)
            ?? throw new InvalidDataException("The ACL-denied fixture has no parent.");
        Require(
            string.Equals(
                Path.TrimEndingDirectorySeparator(parent.FullName),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedNtfsRoot)),
                StringComparison.OrdinalIgnoreCase),
            "The ACL-denied fixture is not a direct child of the disposable NTFS root.");
        Require(
            string.Equals(
                Path.GetFileName(fullPath),
                $"rain-m3-acl-denied-{ownershipToken}",
                StringComparison.Ordinal),
            "The ACL-denied fixture name does not match this run.");
        RequireDirectory(fullPath, "ACL-denied fixture parent");
        Require(
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0,
            "The ACL-denied fixture parent is a reparse point.");
        var marker = Path.Combine(fullPath, ".rainarchives-m3-acl-denied");
        RequireFile(marker, "ACL-denied fixture marker");
        Require(
            string.Equals(File.ReadAllText(marker, Encoding.ASCII), ownershipToken, StringComparison.Ordinal),
            "The ACL-denied fixture marker does not match this run.");
        return fullPath;
    }

    private static async Task<AclDenialEvidence> RunAclDenialAsync(
        string workerPath,
        string sourceRoot,
        string ntfsWork,
        string deniedParent)
    {
        var source = Path.Combine(sourceRoot, "acl-source.txt");
        await File.WriteAllTextAsync(source, "Rain Archives ACL denial fixture.\n", new UTF8Encoding(false))
            .ConfigureAwait(false);
        var sourceHash = Sha256File(source);
        var directory = new DirectoryInfo(deniedParent);
        var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User
            ?? throw new InvalidDataException("The acceptance principal has no Windows SID.");
        var denyRuleObserved = directory
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Deny
                && rule.IdentityReference == sid
                && (rule.FileSystemRights & FileSystemRights.Write) == FileSystemRights.Write);
        Require(denyRuleObserved, "The system-owned ACL fixture has no matching explicit write denial.");
        string? directException = null;
        OperationEvent terminal;
        int recoveryBefore;
        int recoveryAfter;
        FormatEvidence health;

        var directPath = Path.Combine(deniedParent, "direct-write-probe.tmp");
        try
        {
            await File.WriteAllBytesAsync(directPath, [0x52, 0x41]).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            directException = exception.GetType().Name;
        }
        Require(directException == nameof(UnauthorizedAccessException), "The direct ACL probe was not denied.");
        Require(!File.Exists(directPath), "The direct ACL probe unexpectedly created a file.");

        await using var client = await ArchiveClient.StartAsync(workerPath)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        recoveryBefore = (await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false))
            .Recoveries.Count;
        Require(recoveryBefore == 0, "The fresh storage worker reported pre-existing creation recovery records.");
        var destination = Path.Combine(deniedParent, "denied.zip");
        var started = await client.StartZipCreationAsync(
                destination,
                [new ArchiveCreationFile(source, "acl-source.txt")],
                ArchiveCompressionPreset.Store)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        terminal = await client.WaitForTerminalAsync(started.OperationId)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        Require(terminal.Phase == OperationPhase.Failed, "The ACL-denied creation did not fail.");
        Require(
            terminal.Error?.Category == ArchiveErrorCategory.PermissionDenied,
            $"The ACL-denied creation returned {terminal.Error?.Category}.");
        Require(
            terminal.Error?.Code == "filesystem.permission_denied",
            $"The ACL-denied creation returned {terminal.Error?.Code}.");
        Require(!File.Exists(destination), "The ACL-denied creation published a destination.");
        Require(CountStaging(deniedParent) == 0, "The ACL-denied creation left staging residue.");

        var healthDestination = Path.Combine(ntfsWork, "acl-follow-up.zip");
        var healthStart = await client.StartZipCreationAsync(
                healthDestination,
                [new ArchiveCreationFile(source, "acl-source.txt")],
                ArchiveCompressionPreset.Store)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        health = await CompleteAndVerifyAsync(
                client,
                "ZIP-ACL-FOLLOW-UP",
                healthDestination,
                "acl-source.txt",
                sourceHash,
                healthStart)
            .ConfigureAwait(false);
        recoveryAfter = (await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false))
            .Recoveries.Count;
        Require(recoveryAfter == 0, "The ACL-denied creation left a recovery record.");

        Require(Sha256File(source) == sourceHash, "The ACL-denial source changed.");
        return new AclDenialEvidence(
            WindowsIdentity.GetCurrent().Name,
            sid.Value,
            IsElevated(),
            denyRuleObserved,
            directException,
            terminal.Phase.ToString(),
            terminal.Error?.Category.ToString(),
            terminal.Error?.Code,
            SourcePreserved: true,
            DestinationAbsent: true,
            RemainingStagingArtifacts: 0,
            recoveryBefore,
            recoveryAfter,
            health);
    }

    private static async Task<DiskFullEvidence> RunDiskFullAsync(
        string workerPath,
        string sourceRoot,
        string ntfsWork,
        string caseName,
        long triggerBytes)
    {
        var source = Path.Combine(sourceRoot, $"disk-full-{caseName}.bin");
        if (!File.Exists(source))
        {
            await CreateIncompressibleFileAsync(source, DiskFullSourceBytes).ConfigureAwait(false);
        }
        Require(new FileInfo(source).Length == DiskFullSourceBytes, "The disk-full source length changed.");
        var sourceHash = Sha256File(source);
        var caseRoot = Path.Combine(ntfsWork, $"disk-full-{caseName}");
        Directory.CreateDirectory(caseRoot);
        var destination = Path.Combine(caseRoot, $"disk-full-{caseName}.bz2");
        var fillerPath = Path.Combine(caseRoot, "physical-filler.bin");
        var driveRoot = Path.GetPathRoot(caseRoot)!;
        var freeBefore = new DriveInfo(driveRoot).AvailableFreeSpace;
        OperationEvent terminal;
        DiskExhaustionEvidence exhaustion;
        OperationProgress triggerProgress;
        int recoveryBefore;
        int recoveryAfter;
        DiskFullRecoveryEvidence recoveryResolution;
        FormatEvidence health;

        await using (var client = await ArchiveClient.StartAsync(workerPath)
                         .WaitAsync(OperationTimeout)
                         .ConfigureAwait(false))
        {
            recoveryBefore = (await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false))
                .Recoveries.Count;
            Require(recoveryBefore == 0, "The disk-full worker reported pre-existing creation recovery records.");
            var threshold = new TaskCompletionSource<OperationProgress>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            OperationId? operationId = null;
            EventHandler<OperationEvent>? handler = null;
            handler = (_, operationEvent) =>
            {
                if (operationId is not { } expected
                    || operationEvent.OperationId != expected
                    || operationEvent.Kind != OperationEventKind.Progress
                    || operationEvent.Progress is not { } progress
                    || progress.BytesProcessed < triggerBytes)
                {
                    return;
                }

                client.OperationEventReceived -= handler;
                threshold.TrySetResult(progress);
            };
            client.OperationEventReceived += handler;
            try
            {
                var started = await client.StartBZip2CreationAsync(
                        destination,
                        source,
                        ArchiveCompressionPreset.Balanced)
                    .WaitAsync(OperationTimeout)
                    .ConfigureAwait(false);
                operationId = started.OperationId;
                triggerProgress = await threshold.Task.WaitAsync(OperationTimeout).ConfigureAwait(false);
                var terminalTask = client.WaitForTerminalAsync(started.OperationId)
                    .WaitAsync(OperationTimeout);
                using var fillerCancellation = new CancellationTokenSource();
                var fillerTask = FillUntilDiskFullAsync(fillerPath, driveRoot, fillerCancellation.Token);
                var first = await Task.WhenAny(fillerTask, terminalTask).ConfigureAwait(false);
                if (first == terminalTask)
                {
                    fillerCancellation.Cancel();
                    try
                    {
                        await fillerTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (fillerCancellation.IsCancellationRequested)
                    {
                    }

                    terminal = await terminalTask.ConfigureAwait(false);
                    throw new InvalidDataException(
                        $"The {caseName} creation reached terminal phase {terminal.Phase} before physical disk exhaustion.");
                }

                exhaustion = await fillerTask.ConfigureAwait(false);
                terminal = await terminalTask.ConfigureAwait(false);
            }
            finally
            {
                client.OperationEventReceived -= handler;
            }

            Require(terminal.Phase == OperationPhase.Failed, $"The {caseName} disk-full creation did not fail.");
            Require(
                terminal.Error?.Category == ArchiveErrorCategory.InsufficientSpace,
                $"The {caseName} disk-full creation returned "
                    + $"{terminal.Error?.Category}/{terminal.Error?.Code}.");
            Require(
                terminal.Error?.Code is "resource.insufficient_space" or "creation.insufficient_space_during_write",
                $"The {caseName} disk-full creation returned {terminal.Error?.Code}.");
            Require(
                exhaustion.Win32Code is 0x27 or 0x70,
                $"The {caseName} filler did not receive a Windows disk-full error.");

            File.Delete(fillerPath);
            await Task.Delay(250).ConfigureAwait(false);
            Require(!File.Exists(destination), $"The {caseName} disk-full creation published a destination.");
            Require(CountStaging(caseRoot) == 0, $"The {caseName} disk-full creation left staging residue.");
            recoveryResolution = await ResolveDiskFullRecoveryAsync(
                    client,
                    operationId
                        ?? throw new InvalidDataException(
                            $"The {caseName} disk-full operation ID was not retained."),
                    destination,
                    caseName)
                .ConfigureAwait(false);
            recoveryAfter = recoveryResolution.RecordsAfter;

            var healthDestination = Path.Combine(caseRoot, $"health-{caseName}.zip");
            var healthStart = await client.StartZipCreationAsync(
                    healthDestination,
                    [new ArchiveCreationFile(source, $"disk-full-{caseName}.bin")],
                    ArchiveCompressionPreset.Fast)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            health = await CompleteAndVerifyAsync(
                    client,
                    $"ZIP-{caseName}-FOLLOW-UP",
                    healthDestination,
                    $"disk-full-{caseName}.bin",
                    sourceHash,
                    healthStart)
                .ConfigureAwait(false);
        }

        Require(Sha256File(source) == sourceHash, $"The {caseName} disk-full source changed.");
        return new DiskFullEvidence(
            caseName,
            DiskFullSourceBytes,
            sourceHash,
            triggerBytes,
            triggerProgress.BytesProcessed,
            freeBefore,
            exhaustion,
            terminal.Phase.ToString(),
            terminal.Error?.Category.ToString(),
            terminal.Error?.Code,
            SourcePreserved: true,
            DestinationAbsent: true,
            RemainingStagingArtifacts: 0,
            recoveryBefore,
            recoveryAfter,
            recoveryResolution,
            health);
    }

    private static async Task<DiskFullRecoveryEvidence> ResolveDiskFullRecoveryAsync(
        ArchiveClient client,
        OperationId operationId,
        string destination,
        string caseName)
    {
        var inventory = await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false);
        Require(!inventory.IsTruncated, $"The {caseName} recovery inventory was truncated.");
        Require(!inventory.InspectionIncomplete, $"The {caseName} recovery inventory was incomplete.");
        Require(inventory.SuspiciousOperationCount == 0, $"The {caseName} recovery inventory was suspicious.");
        Require(inventory.IncompleteJournalCount == 0, $"The {caseName} recovery journal was incomplete.");
        Require(inventory.Recoveries.Count <= 1, $"The {caseName} disk-full creation left multiple recovery records.");

        if (inventory.Recoveries.Count == 0)
        {
            return new DiskFullRecoveryEvidence(
                RecordsObserved: 0,
                RecoveryId: null,
                OperationId: null,
                Phase: null,
                Sequence: null,
                InspectionAssessment: null,
                StagingArtifactState: null,
                FinalArtifactState: null,
                DispositionAction: null,
                DispositionResult: null,
                FinalAssessment: null,
                RecordsAfter: 0);
        }

        var recovery = inventory.Recoveries[0];
        Require(recovery.OperationId == operationId, $"The {caseName} recovery record belongs to another operation.");
        Require(recovery.Phase == ArchiveCreationRecoveryPhase.Writing, $"The {caseName} recovery record has phase {recovery.Phase}.");
        Require(!recovery.HasIncompleteTail, $"The {caseName} recovery record has an incomplete journal tail.");
        var inspection = await client
            .InspectArchiveCreationRecoveryAsync(recovery.RecoveryId)
            .ConfigureAwait(false);
        Require(
            string.Equals(
                Path.GetFullPath(inspection.DestinationPath),
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase),
            $"The {caseName} recovery record names another destination.");
        Require(!inspection.IsRemoteDestination, $"The {caseName} recovery record unexpectedly describes a remote destination.");
        Require(
            inspection.DestinationParent == ArchiveCreationRecoveryParentState.MatchesJournal,
            $"The {caseName} recovery destination parent did not match its journal.");
        Require(
            inspection.StagingArtifact.State == ArchiveCreationRecoveryArtifactState.Absent,
            $"The {caseName} recovery staging artifact was {inspection.StagingArtifact.State}.");
        Require(
            inspection.FinalArtifact.State == ArchiveCreationRecoveryArtifactState.Absent,
            $"The {caseName} recovery final artifact was {inspection.FinalArtifact.State}.");
        Require(
            inspection.Assessment == ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved,
            $"The {caseName} recovery assessment was {inspection.Assessment}.");

        var disposition = await client.ApplyArchiveCreationRecoveryDispositionAsync(
                recovery.RecoveryId,
                recovery.Phase,
                recovery.Sequence,
                recovery.RecordedAtUtc,
                ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact)
            .ConfigureAwait(false);
        Require(
            disposition.Result == ArchiveCreationRecoveryDispositionResult.Completed,
            $"The {caseName} recovery disposition returned {disposition.Result}.");
        Require(
            disposition.FinalAssessment == ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved,
            $"The {caseName} recovery disposition ended as {disposition.FinalAssessment}.");
        var after = await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false);
        Require(after.Recoveries.Count == 0, $"The {caseName} recovery remained actionable after acknowledgement.");
        Require(after.SuspiciousOperationCount == 0, $"The {caseName} recovery acknowledgement left suspicious state.");
        Require(after.IncompleteJournalCount == 0, $"The {caseName} recovery acknowledgement left an incomplete journal.");
        Require(!after.IsTruncated, $"The {caseName} post-disposition inventory was truncated.");
        Require(!after.InspectionIncomplete, $"The {caseName} post-disposition inventory was incomplete.");

        return new DiskFullRecoveryEvidence(
            RecordsObserved: 1,
            recovery.RecoveryId,
            recovery.OperationId.Value.ToString("D"),
            recovery.Phase.ToString(),
            recovery.Sequence,
            inspection.Assessment.ToString(),
            inspection.StagingArtifact.State.ToString(),
            inspection.FinalArtifact.State.ToString(),
            disposition.Action.ToString(),
            disposition.Result.ToString(),
            disposition.FinalAssessment.ToString(),
            RecordsAfter: after.Recoveries.Count);
    }

    private static async Task<DiskExhaustionEvidence> FillUntilDiskFullAsync(
        string fillerPath,
        string driveRoot,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4 * 1024 * 1024];
        RandomNumberGenerator.Fill(buffer);
        long bytesWritten = 0;
        try
        {
            await using var stream = new FileStream(
                fillerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                    bytesWritten = checked(bytesWritten + buffer.Length);
                }
                catch (IOException exception) when ((exception.HResult & 0xFFFF) is 0x27 or 0x70)
                {
                    return new DiskExhaustionEvidence(
                        bytesWritten,
                        exception.HResult,
                        exception.HResult & 0xFFFF,
                        new DriveInfo(driveRoot).AvailableFreeSpace,
                        DateTimeOffset.UtcNow);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task CreateIncompressibleFileAsync(string path, long length)
    {
        var buffer = new byte[1024 * 1024];
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        long written = 0;
        try
        {
            while (written < length)
            {
                RandomNumberGenerator.Fill(buffer);
                var count = (int)Math.Min(buffer.Length, length - written);
                await stream.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                written = checked(written + count);
            }

            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task<NonNtfsEvidence> RunNonNtfsAsync(
        string workerPath,
        string sourceRoot,
        string nonNtfsWork)
    {
        var source = Path.Combine(sourceRoot, "motw-雨.txt");
        const string content = "Rain Archives non-NTFS provenance fixture.\nUnicode: 雨 雪\nProtocol: 1.18\n";
        const string zone = "[ZoneTransfer]\r\nZoneId=3\r\n";
        await File.WriteAllTextAsync(source, content, new UTF8Encoding(false)).ConfigureAwait(false);
        await File.WriteAllTextAsync(source + ":Zone.Identifier", zone, Encoding.ASCII)
            .ConfigureAwait(false);
        var sourceHash = Sha256File(source);
        var files = new[] { new ArchiveCreationFile(source, "motw-雨.txt") };
        var formats = new List<NonNtfsFormatEvidence>();
        int recoveryBefore;
        int recoveryAfter;

        await using (var client = await ArchiveClient.StartAsync(workerPath)
                         .WaitAsync(OperationTimeout)
                         .ConfigureAwait(false))
        {
            recoveryBefore = (await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false))
                .Recoveries.Count;
            Require(recoveryBefore == 0, "The non-NTFS worker reported pre-existing creation recovery records.");
            var zipCapabilities = await client.GetZipWriteCapabilitiesAsync().ConfigureAwait(false);
            var tarCapabilities = await client.GetTarWriteCapabilitiesAsync().ConfigureAwait(false);
            var managedCapabilities = await client.GetManagedWriteCapabilitiesAsync().ConfigureAwait(false);

            foreach (var preset in zipCapabilities.CompressionPresets)
            {
                var leaf = $"exfat-zip-{preset.ToString().ToLowerInvariant()}.zip";
                var destination = Path.Combine(nonNtfsWork, leaf);
                var started = await client.StartZipCreationAsync(destination, files, preset)
                    .WaitAsync(OperationTimeout).ConfigureAwait(false);
                formats.Add(await CompleteNonNtfsAsync(
                        client,
                        $"ZIP-{preset}",
                        destination,
                        "motw-雨.txt",
                        sourceHash,
                        started)
                    .ConfigureAwait(false));
            }

            foreach (var preset in tarCapabilities.CompressionPresets)
            {
                Require(preset == ArchiveCompressionPreset.Store, "TAR exposed a non-Store preset.");
                var destination = Path.Combine(nonNtfsWork, "exfat-tar-store.tar");
                var started = await client.StartTarCreationAsync(destination, files)
                    .WaitAsync(OperationTimeout).ConfigureAwait(false);
                formats.Add(await CompleteNonNtfsAsync(
                        client,
                        "TAR-Store",
                        destination,
                        "motw-雨.txt",
                        sourceHash,
                        started)
                    .ConfigureAwait(false));
            }

            foreach (var capability in managedCapabilities.Capabilities)
            {
                foreach (var preset in capability.CompressionPresets)
                {
                    var code = capability.Format.ToString().ToLowerInvariant();
                    var extension = capability.Format switch
                    {
                        ManagedArchiveCreationFormat.TarGZip => ".tar.gz",
                        ManagedArchiveCreationFormat.TarBZip2 => ".tar.bz2",
                        ManagedArchiveCreationFormat.GZip => ".gz",
                        ManagedArchiveCreationFormat.BZip2 => ".bz2",
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                    var baseName = $"exfat-{code}-{preset.ToString().ToLowerInvariant()}";
                    var destination = Path.Combine(nonNtfsWork, baseName + extension);
                    var started = capability.Format switch
                    {
                        ManagedArchiveCreationFormat.TarGZip =>
                            await client.StartTarGZipCreationAsync(destination, files, preset)
                                .WaitAsync(OperationTimeout).ConfigureAwait(false),
                        ManagedArchiveCreationFormat.TarBZip2 =>
                            await client.StartTarBZip2CreationAsync(destination, files, preset)
                                .WaitAsync(OperationTimeout).ConfigureAwait(false),
                        ManagedArchiveCreationFormat.GZip =>
                            await client.StartGZipCreationAsync(destination, source, preset)
                                .WaitAsync(OperationTimeout).ConfigureAwait(false),
                        ManagedArchiveCreationFormat.BZip2 =>
                            await client.StartBZip2CreationAsync(destination, source, preset)
                                .WaitAsync(OperationTimeout).ConfigureAwait(false),
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                    var expectedEntry = capability.Format is ManagedArchiveCreationFormat.GZip
                        or ManagedArchiveCreationFormat.BZip2
                        ? baseName
                        : "motw-雨.txt";
                    formats.Add(await CompleteNonNtfsAsync(
                            client,
                            $"{capability.Format}-{preset}",
                            destination,
                            expectedEntry,
                            sourceHash,
                            started)
                        .ConfigureAwait(false));
                }
            }

            recoveryAfter = (await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false))
                .Recoveries.Count;
            Require(recoveryAfter == 0, "The non-NTFS creations left a recovery record.");
        }

        Require(Sha256File(source) == sourceHash, "The non-NTFS source payload changed.");
        Require(
            await File.ReadAllTextAsync(source + ":Zone.Identifier", Encoding.ASCII).ConfigureAwait(false) == zone,
            "The non-NTFS source provenance changed.");
        Require(CountStaging(nonNtfsWork) == 0, "The non-NTFS creations left staging residue.");
        return new NonNtfsEvidence(
            new DriveInfo(Path.GetPathRoot(nonNtfsWork)!).DriveFormat,
            sourceHash,
            Sha256File(source + ":Zone.Identifier"),
            formats,
            recoveryBefore,
            recoveryAfter,
            SourcePreserved: true,
            SourceProvenancePreserved: true,
            RemainingStagingArtifacts: 0);
    }

    private static async Task<NonNtfsFormatEvidence> CompleteNonNtfsAsync(
        ArchiveClient client,
        string format,
        string archivePath,
        string expectedEntry,
        string sourceHash,
        StartOperationResponse started)
    {
        var evidence = await CompleteAndVerifyAsync(
                client,
                format,
                archivePath,
                expectedEntry,
                sourceHash,
                started)
            .ConfigureAwait(false);
        Require(
            evidence.TerminalPhase == OperationPhase.CompletedWithWarnings.ToString(),
            $"{format} on exFAT did not complete with a warning.");
        Require(
            evidence.WarningCode == "provenance.not_applied",
            $"{format} on exFAT returned warning {evidence.WarningCode}.");
        var provenancePath = archivePath + ":Zone.Identifier";
        Require(!File.Exists(provenancePath), $"{format} created an unsupported exFAT provenance stream.");
        return new NonNtfsFormatEvidence(evidence, ProvenanceStreamAbsent: true);
    }

    private static void DeleteExactOwnedVolumeChild(
        string path,
        string expectedVolumeRoot,
        string ownershipToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Directory.GetParent(canonical)
            ?? throw new InvalidOperationException("The disposable volume work root has no parent.");
        Require(
            string.Equals(
                Path.TrimEndingDirectorySeparator(parent.FullName),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedVolumeRoot)),
                StringComparison.OrdinalIgnoreCase),
            "Refusing to delete a work root outside the exact disposable volume root.");
        Require(
            string.Equals(Path.GetFileName(canonical), ownershipToken, StringComparison.Ordinal),
            "Refusing to delete a disposable-volume work root with the wrong ownership token.");
        EnsureNoReparsePoints(canonical);
        Directory.Delete(canonical, recursive: true);
    }

    private static bool TryParseStorageArguments(string[] args, out StorageProbeOptions options)
    {
        string? package = null;
        string? evidence = null;
        string? ntfs = null;
        string? nonNtfs = null;
        string? aclDenied = null;
        string? token = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--package" && index + 1 < args.Length)
            {
                package = args[++index];
            }
            else if (args[index] == "--evidence" && index + 1 < args.Length)
            {
                evidence = args[++index];
            }
            else if (args[index] == "--storage-ntfs" && index + 1 < args.Length)
            {
                ntfs = args[++index];
            }
            else if (args[index] == "--storage-nonntfs" && index + 1 < args.Length)
            {
                nonNtfs = args[++index];
            }
            else if (args[index] == "--acl-denied" && index + 1 < args.Length)
            {
                aclDenied = args[++index];
            }
            else if (args[index] == "--ownership-token" && index + 1 < args.Length)
            {
                token = args[++index];
            }
            else
            {
                options = default;
                return false;
            }
        }

        options = new StorageProbeOptions(
            package ?? string.Empty,
            evidence ?? string.Empty,
            ntfs ?? string.Empty,
            nonNtfs ?? string.Empty,
            aclDenied ?? string.Empty,
            token ?? string.Empty);
        return !string.IsNullOrWhiteSpace(options.PackageArtifactRoot)
            && !string.IsNullOrWhiteSpace(options.EvidenceRoot)
            && !string.IsNullOrWhiteSpace(options.NtfsRoot)
            && !string.IsNullOrWhiteSpace(options.NonNtfsRoot)
            && !string.IsNullOrWhiteSpace(options.AclDeniedParent)
            && !string.IsNullOrWhiteSpace(options.OwnershipToken);
    }

    private readonly record struct StorageProbeOptions(
        string PackageArtifactRoot,
        string EvidenceRoot,
        string NtfsRoot,
        string NonNtfsRoot,
        string AclDeniedParent,
        string OwnershipToken);
    private sealed record StoragePackage(string WorkerPath, StoragePackageEvidence Evidence);
    private sealed record StoragePackageEvidence(
        string PackageArtifactRoot,
        string ManifestSha256,
        string WorkerSha256,
        IReadOnlyDictionary<string, string> PrincipalFileHashes,
        IReadOnlyList<string> ForbiddenAssets);
    private sealed record DisposableVolumeEvidence(
        string Root,
        string VolumeLabel,
        string FileSystem,
        string DriveType,
        long TotalBytes,
        long AvailableBytesBefore,
        string OwnershipToken,
        string OwnershipMarkerSha256);
    private sealed record AclDenialEvidence(
        string UserName,
        string UserSid,
        bool IsElevated,
        bool DenyRuleObserved,
        string? DirectException,
        string TerminalPhase,
        string? ErrorCategory,
        string? ErrorCode,
        bool SourcePreserved,
        bool DestinationAbsent,
        int RemainingStagingArtifacts,
        int RecoveryRecordsBefore,
        int RecoveryRecordsAfter,
        FormatEvidence SameWorkerHealth);
    private sealed record DiskExhaustionEvidence(
        long FillerBytesWritten,
        int HResult,
        int Win32Code,
        long AvailableBytesAtFailure,
        DateTimeOffset ExhaustedUtc);
    private sealed record DiskFullRecoveryEvidence(
        int RecordsObserved,
        string? RecoveryId,
        string? OperationId,
        string? Phase,
        int? Sequence,
        string? InspectionAssessment,
        string? StagingArtifactState,
        string? FinalArtifactState,
        string? DispositionAction,
        string? DispositionResult,
        string? FinalAssessment,
        int RecordsAfter);
    private sealed record DiskFullEvidence(
        string Case,
        long SourceBytes,
        string SourceSha256,
        long RequestedTriggerBytes,
        long ObservedTriggerBytes,
        long AvailableBytesBefore,
        DiskExhaustionEvidence PhysicalExhaustion,
        string TerminalPhase,
        string? ErrorCategory,
        string? ErrorCode,
        bool SourcePreserved,
        bool DestinationAbsent,
        int RemainingStagingArtifacts,
        int RecoveryRecordsBefore,
        int RecoveryRecordsAfter,
        DiskFullRecoveryEvidence RecoveryResolution,
        FormatEvidence SameWorkerHealth);
    private sealed record NonNtfsFormatEvidence(
        FormatEvidence Format,
        bool ProvenanceStreamAbsent);
    private sealed record NonNtfsEvidence(
        string FileSystem,
        string SourceSha256,
        string SourceProvenanceSha256,
        IReadOnlyList<NonNtfsFormatEvidence> Formats,
        int RecoveryRecordsBefore,
        int RecoveryRecordsAfter,
        bool SourcePreserved,
        bool SourceProvenancePreserved,
        int RemainingStagingArtifacts);
    private sealed record StorageAcceptanceEvidence(
        int SchemaVersion,
        DateTimeOffset RecordedUtc,
        string Protocol,
        int SourceInputCount,
        string SourceSha256,
        StoragePackageEvidence Package,
        string MachineName,
        string OperatingSystem,
        string RuntimeVersion,
        string UserName,
        string? UserSid,
        bool IsElevated,
        DisposableVolumeEvidence NtfsVolume,
        DisposableVolumeEvidence NonNtfsVolume,
        AclDenialEvidence AclDenial,
        IReadOnlyList<DiskFullEvidence> DiskFull,
        NonNtfsEvidence NonNtfs,
        IReadOnlyList<ProcessEvidence> RemainingRainProcesses,
        int RemainingStagingArtifacts,
        bool OwnedWorkRootsRemoved);
}
