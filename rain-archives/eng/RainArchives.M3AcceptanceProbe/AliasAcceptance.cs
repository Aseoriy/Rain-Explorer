using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.M3AcceptanceProbe;

internal static partial class Program
{
    private const string AliasParentLeaf = "RainArchives-M3-Alias";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW", SetLastError = true)]
    private static extern uint GetShortPathName(
        string longPath,
        StringBuilder shortPath,
        uint bufferLength);

    private static async Task<int> RunAliasAcceptanceMainAsync(string[] args)
    {
        if (!TryParseAliasArguments(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: RainArchives.M3AcceptanceProbe --package <artifact-root> "
                + "--evidence <new-directory> --alias-acceptance");
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
            var result = await RunAliasAcceptanceAsync(options).ConfigureAwait(false);
            await WriteEvidenceAsync(
                    Path.Combine(evidenceRoot, "alias-evidence.json"),
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
                    Path.Combine(evidenceRoot, "alias-failure.json"),
                    failure)
                .ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<AliasAcceptanceEvidence> RunAliasAcceptanceAsync(AliasProbeOptions options)
    {
        var package = await ValidateAliasPackageAsync(options.PackageArtifactRoot).ConfigureAwait(false);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        Require(
            Path.GetPathRoot(tempRoot)?.Equals("C:\\", StringComparison.OrdinalIgnoreCase) == true,
            "The alias probe requires its disposable work root on the local C: volume.");
        var parent = Path.Combine(tempRoot, AliasParentLeaf);
        var workToken = Guid.NewGuid().ToString("N");
        var workRoot = Path.Combine(parent, workToken);
        Require(!Directory.Exists(workRoot) && !File.Exists(workRoot), "The alias work root already exists.");
        Directory.CreateDirectory(workRoot);

        var substBefore = await QuerySubstAsync().ConfigureAwait(false);
        string? mappedDrive = null;
        IReadOnlyList<FormatEvidence>? formats = null;
        string? shortSource = null;
        string? shortDestination = null;
        string? mappedSource = null;
        string? mappedDestination = null;
        string? uncSource = null;
        string? uncDestination = null;
        var sourceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var recoveryBefore = -1;
        var recoveryAfter = -1;

        try
        {
            var shortSourceLong = Path.Combine(workRoot, "eight dot three source with a long name.txt");
            await File.WriteAllTextAsync(
                    shortSourceLong,
                    "Rain Archives local 8.3 alias fixture — pluie — 雨\n",
                    new UTF8Encoding(false))
                .ConfigureAwait(false);
            sourceHashes["EightDotThree"] = Sha256File(shortSourceLong);
            shortSource = ResolveDistinctShortPath(shortSourceLong);
            var shortParent = Path.GetDirectoryName(shortSource)
                ?? throw new InvalidDataException("The 8.3 source has no parent.");
            shortDestination = Path.Combine(shortParent, "EIGHTOUT.GZ");
            var shortDestinationLong = Path.Combine(workRoot, "EIGHTOUT.GZ");
            Require(!File.Exists(shortDestinationLong), "The 8.3 destination already exists.");

            var mappedSourceLong = Path.Combine(workRoot, "mapped-drive-source.txt");
            await File.WriteAllTextAsync(
                    mappedSourceLong,
                    "Rain Archives mapped-drive fixture — neige — 雪\n",
                    new UTF8Encoding(false))
                .ConfigureAwait(false);
            sourceHashes["MappedDrive"] = Sha256File(mappedSourceLong);
            mappedDrive = SelectUnusedDriveLetter();
            await SetSubstAsync(mappedDrive, workRoot).ConfigureAwait(false);
            mappedSource = Path.Combine(mappedDrive + Path.DirectorySeparatorChar, Path.GetFileName(mappedSourceLong));
            mappedDestination = Path.Combine(mappedDrive + Path.DirectorySeparatorChar, "mapped-output.bz2");
            Require(File.Exists(mappedSource), "The mapped-drive source is not reachable.");

            var uncSourceLong = Path.Combine(workRoot, "unc-source.txt");
            await File.WriteAllTextAsync(
                    uncSourceLong,
                    "Rain Archives alternate-loopback UNC fixture — pluie et neige — 雨雪\n",
                    new UTF8Encoding(false))
                .ConfigureAwait(false);
            sourceHashes["AlternateLoopbackUnc"] = Sha256File(uncSourceLong);
            uncSource = ToAdministrativeSharePath(uncSourceLong, "localhost");
            var uncDestinationLong = Path.Combine(workRoot, "unc-output.tar.gz");
            uncDestination = ToAdministrativeSharePath(uncDestinationLong, "127.0.0.1");
            Require(File.Exists(uncSource), "The localhost administrative-share source is not reachable.");
            Require(Directory.Exists(Path.GetDirectoryName(uncDestination)!),
                "The alternate loopback administrative-share destination parent is not reachable.");

            await using var client = await ArchiveClient.StartAsync(package.WorkerPath)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            VerifyManagedCapabilities(await client.GetManagedWriteCapabilitiesAsync().ConfigureAwait(false));
            recoveryBefore = (await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false))
                .Recoveries.Count;
            Require(recoveryBefore == 0, "The alias probe found pre-existing creation recovery records.");

            var results = new List<FormatEvidence>();
            var started = await client.StartGZipCreationAsync(
                    shortDestination,
                    shortSource,
                    ArchiveCompressionPreset.Maximum)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            results.Add(await CompleteAndVerifyAsync(
                    client,
                    "GZIP-8.3",
                    shortDestination,
                    "EIGHTOUT",
                    sourceHashes["EightDotThree"],
                    started)
                .ConfigureAwait(false));
            Require(File.Exists(shortDestinationLong), "The 8.3 output is absent at its canonical spelling.");

            started = await client.StartBZip2CreationAsync(
                    mappedDestination,
                    mappedSource,
                    ArchiveCompressionPreset.Balanced)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            results.Add(await CompleteAndVerifyAsync(
                    client,
                    "BZIP2-SUBST",
                    mappedDestination,
                    "mapped-output",
                    sourceHashes["MappedDrive"],
                    started)
                .ConfigureAwait(false));
            Require(
                File.Exists(Path.Combine(workRoot, "mapped-output.bz2")),
                "The mapped output is absent at its canonical spelling.");

            started = await client.StartTarGZipCreationAsync(
                    uncDestination,
                    [new ArchiveCreationFile(uncSource, "unc-payload.txt")],
                    ArchiveCompressionPreset.Fast)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            results.Add(await CompleteAndVerifyAsync(
                    client,
                    "TAR.GZ-UNC-ALTERNATE",
                    uncDestination,
                    "unc-payload.txt",
                    sourceHashes["AlternateLoopbackUnc"],
                    started)
                .ConfigureAwait(false));
            Require(File.Exists(uncDestinationLong), "The UNC output is absent at its local spelling.");

            recoveryAfter = (await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false))
                .Recoveries.Count;
            Require(recoveryAfter == 0, "The alias probe left a creation recovery record.");
            Require(CountStaging(workRoot) == 0, "The alias probe left staging residue.");
            Require(Sha256File(shortSourceLong) == sourceHashes["EightDotThree"], "The 8.3 source changed.");
            Require(Sha256File(mappedSourceLong) == sourceHashes["MappedDrive"], "The mapped source changed.");
            Require(Sha256File(uncSourceLong) == sourceHashes["AlternateLoopbackUnc"], "The UNC source changed.");
            formats = results;
        }
        finally
        {
            if (mappedDrive is not null)
            {
                await RemoveSubstAsync(mappedDrive).ConfigureAwait(false);
            }

            if (Directory.Exists(workRoot))
            {
                DeleteExactOwnedTree(workRoot, AliasParentLeaf);
            }
        }

        var substAfter = await QuerySubstAsync().ConfigureAwait(false);
        Require(substBefore == substAfter,
            "The host SUBST mapping set changed after alias acceptance.");
        await WaitForNoRainProcessesAsync().ConfigureAwait(false);
        var remainingProcesses = GetRainProcesses();
        Require(remainingProcesses.Count == 0, "Rain processes remained after alias acceptance.");
        Require(!Directory.Exists(workRoot), "The disposable alias work root was not removed.");
        var completedFormats = formats
            ?? throw new InvalidDataException("The alias format evidence was not produced.");

        return new AliasAcceptanceEvidence(
            SchemaVersion: 1,
            RecordedUtc: DateTimeOffset.UtcNow,
            Protocol: ExpectedProtocol,
            SourceInputCount: ExpectedSourceInputs,
            SourceSha256: ExpectedSourceSha256,
            PackageArtifactRoot: package.ArtifactRoot,
            ManifestSha256: package.ManifestSha256,
            WorkerSha256: package.WorkerSha256,
            MachineName: Environment.MachineName,
            OperatingSystem: Environment.OSVersion.VersionString,
            RuntimeVersion: Environment.Version.ToString(),
            UserName: WindowsIdentity.GetCurrent().Name,
            IsElevated: IsElevated(),
            WorkRoot: workRoot,
            EightDotThree: new PathAliasEvidence(shortSource!, shortDestination!, sourceHashes["EightDotThree"]),
            MappedDrive: new PathAliasEvidence(mappedSource!, mappedDestination!, sourceHashes["MappedDrive"]),
            AlternateLoopbackUnc: new PathAliasEvidence(
                uncSource!,
                uncDestination!,
                sourceHashes["AlternateLoopbackUnc"]),
            SubstBefore: substBefore,
            SubstAfter: substAfter,
            RecoveryRecordsBefore: recoveryBefore,
            RecoveryRecordsAfter: recoveryAfter,
            Formats: completedFormats,
            RemainingRainProcesses: remainingProcesses,
            RemainingStagingArtifacts: 0,
            WorkRootRemoved: true);
    }

    private static async Task<AliasPackage> ValidateAliasPackageAsync(string packageArtifactRoot)
    {
        var artifactRoot = Path.GetFullPath(packageArtifactRoot);
        var manifestPath = Path.Combine(artifactRoot, "package-manifest.json");
        var workerPath = Path.Combine(artifactRoot, "app", "worker", "RainArchives.Worker.exe");
        RequireFile(manifestPath, "package manifest");
        RequireFile(workerPath, "private packaged Worker");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
                await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false),
                JsonOptions)
            ?? throw new InvalidDataException("The package manifest is empty.");
        Require(manifest.Protocol == ExpectedProtocol, "The alias package protocol identity changed.");
        Require(manifest.SourceInputCount == ExpectedSourceInputs, "The alias package source count changed.");
        Require(manifest.SourceSha256 == ExpectedSourceSha256, "The alias package source fingerprint changed.");
        var workerRelativePath = "app/worker/RainArchives.Worker.exe";
        Require(
            manifest.Files.TryGetValue(workerRelativePath, out var expectedWorkerHash),
            "The package manifest does not pin its private Worker.");
        var workerHash = Sha256File(workerPath);
        Require(workerHash == expectedWorkerHash, "The private packaged Worker hash changed.");
        return new AliasPackage(artifactRoot, workerPath, Sha256File(manifestPath), workerHash);
    }

    private static string ResolveDistinctShortPath(string longPath)
    {
        var buffer = new StringBuilder(32_768);
        var length = GetShortPathName(longPath, buffer, (uint)buffer.Capacity);
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetShortPathNameW failed.");
        }

        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetShortPathName(longPath, buffer, (uint)buffer.Capacity);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetShortPathNameW retry failed.");
            }
        }

        var shortPath = buffer.ToString();
        Require(!shortPath.Equals(longPath, StringComparison.OrdinalIgnoreCase),
            "The local volume did not produce a distinct 8.3 alias for the owned source.");
        Require(shortPath.Contains('~', StringComparison.Ordinal),
            "The resolved short path does not contain an 8.3 alias component.");
        Require(File.Exists(shortPath), "The resolved 8.3 source path is not reachable.");
        return shortPath;
    }

    private static string SelectUnusedDriveLetter()
    {
        var used = Environment.GetLogicalDrives()
            .Select(path => path[..2])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var letter = 'Z'; letter >= 'R'; letter--)
        {
            var drive = $"{letter}:";
            if (!used.Contains(drive) && !Directory.Exists(drive + Path.DirectorySeparatorChar))
            {
                return drive;
            }
        }

        throw new InvalidOperationException("No unused drive letter is available for the disposable SUBST probe.");
    }

    private static async Task SetSubstAsync(string drive, string target)
    {
        var result = await RunSubstAsync([drive, target]).ConfigureAwait(false);
        Require(result.ExitCode == 0, $"SUBST creation failed: {Sanitize(result.StandardError)}");
        Require(Directory.Exists(drive + Path.DirectorySeparatorChar), "The disposable SUBST mapping is unreachable.");
    }

    private static async Task RemoveSubstAsync(string drive)
    {
        var result = await RunSubstAsync([drive, "/D"]).ConfigureAwait(false);
        Require(result.ExitCode == 0, $"SUBST removal failed: {Sanitize(result.StandardError)}");
        Require(!Directory.Exists(drive + Path.DirectorySeparatorChar), "The disposable SUBST mapping remained reachable.");
    }

    private static async Task<SubstSnapshot> QuerySubstAsync()
    {
        var result = await RunSubstAsync([]).ConfigureAwait(false);
        Require(result.ExitCode == 0, $"SUBST enumeration failed: {Sanitize(result.StandardError)}");
        var mappings = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
        return new SubstSnapshot(
            mappings.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', mappings)))));
    }

    private static async Task<SubstResult> RunSubstAsync(IReadOnlyList<string> arguments)
    {
        var substPath = Path.Combine(Environment.SystemDirectory, "subst.exe");
        RequireFile(substPath, "System32 SUBST executable");
        var startInfo = new ProcessStartInfo(substPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("SUBST did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            throw;
        }
        return new SubstResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static string ToAdministrativeSharePath(string localPath, string host)
    {
        var canonical = Path.GetFullPath(localPath);
        Require(
            Path.GetPathRoot(canonical)?.Equals("C:\\", StringComparison.OrdinalIgnoreCase) == true,
            "The alternate-loopback UNC probe requires a local C: path.");
        return $@"\\{host}\C$\{canonical[3..]}";
    }

    private static bool TryParseAliasArguments(string[] args, out AliasProbeOptions options)
    {
        string? package = null;
        string? evidence = null;
        var alias = false;
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
            else if (args[index] == "--alias-acceptance")
            {
                alias = true;
            }
            else
            {
                options = default;
                return false;
            }
        }

        options = new AliasProbeOptions(package ?? string.Empty, evidence ?? string.Empty);
        return alias
            && !string.IsNullOrWhiteSpace(options.PackageArtifactRoot)
            && !string.IsNullOrWhiteSpace(options.EvidenceRoot);
    }

    private readonly record struct AliasProbeOptions(string PackageArtifactRoot, string EvidenceRoot);
    private sealed record AliasPackage(
        string ArtifactRoot,
        string WorkerPath,
        string ManifestSha256,
        string WorkerSha256);
    private sealed record SubstResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record SubstSnapshot(int MappingCount, string MappingSetSha256);
    private sealed record PathAliasEvidence(string SourcePath, string DestinationPath, string SourceSha256);
    private sealed record AliasAcceptanceEvidence(
        int SchemaVersion,
        DateTimeOffset RecordedUtc,
        string Protocol,
        int SourceInputCount,
        string SourceSha256,
        string PackageArtifactRoot,
        string ManifestSha256,
        string WorkerSha256,
        string MachineName,
        string OperatingSystem,
        string RuntimeVersion,
        string UserName,
        bool IsElevated,
        string WorkRoot,
        PathAliasEvidence EightDotThree,
        PathAliasEvidence MappedDrive,
        PathAliasEvidence AlternateLoopbackUnc,
        SubstSnapshot SubstBefore,
        SubstSnapshot SubstAfter,
        int RecoveryRecordsBefore,
        int RecoveryRecordsAfter,
        IReadOnlyList<FormatEvidence> Formats,
        IReadOnlyList<ProcessEvidence> RemainingRainProcesses,
        int RemainingStagingArtifacts,
        bool WorkRootRemoved);
}
