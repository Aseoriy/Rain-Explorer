using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.M3AcceptanceProbe;

internal static partial class Program
{
    private const string ExpectedProtocol = "1.18";
    private const int ExpectedSourceInputs = 132;
    private const string ExpectedSourceSha256 =
        "1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844";
    private const string ExpectedAppTreeSha256 =
        "C61749B2CC94A4E66606D3615DF88C44A2D95F599EE1F2B78C812BAC1BCFCFB0";
    private const string ExpectedWorkerTreeSha256 =
        "36C64503CC9EA9FD6CF0F6C390F75F8871BEF9BFCA5461456CE645CBACB2966F";
    private const int ExpectedAppFiles = 609;
    private const long ExpectedAppBytes = 232_216_977;
    private const int ExpectedWorkerFiles = 205;
    private const long ExpectedWorkerBytes = 84_699_121;
    private const int BufferBytes = 64 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        if (Array.Exists(
                args,
                argument => string.Equals(argument, "--storage-ntfs", StringComparison.Ordinal)))
        {
            return await RunStorageAcceptanceMainAsync(args).ConfigureAwait(false);
        }

        if (Array.Exists(
                args,
                argument => string.Equals(argument, "--alias-acceptance", StringComparison.Ordinal)))
        {
            return await RunAliasAcceptanceMainAsync(args).ConfigureAwait(false);
        }

        if (Array.Exists(
                args,
                argument => string.Equals(argument, "--gnu-tool-conformance", StringComparison.Ordinal)))
        {
            return await RunGnuToolConformanceMainAsync(args).ConfigureAwait(false);
        }

        if (!TryParseArguments(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: RainArchives.M3AcceptanceProbe --package <artifact-root> --evidence <new-directory> " +
                "[--allow-developer-tools]");
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
            var result = await RunCleanMachineAcceptanceAsync(options, evidenceRoot).ConfigureAwait(false);
            await WriteEvidenceAsync(
                    Path.Combine(evidenceRoot, "clean-machine-evidence.json"),
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
                    Path.Combine(evidenceRoot, "clean-machine-failure.json"),
                    failure)
                .ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<CleanMachineEvidence> RunCleanMachineAcceptanceAsync(
        ProbeOptions options,
        string evidenceRoot)
    {
        var artifactRoot = Path.GetFullPath(options.PackageArtifactRoot);
        var appRoot = Path.Combine(artifactRoot, "app");
        var workerRoot = Path.Combine(appRoot, "worker");
        var appPath = Path.Combine(appRoot, "RainArchives.exe");
        var workerPath = Path.Combine(workerRoot, "RainArchives.Worker.exe");
        RequireDirectory(artifactRoot, "package artifact root");
        RequireDirectory(appRoot, "App package root");
        RequireFile(appPath, "packaged App");
        RequireFile(workerPath, "private packaged Worker");

        var manifestPath = Path.Combine(artifactRoot, "package-manifest.json");
        RequireFile(manifestPath, "package manifest");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
                await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false),
                JsonOptions)
            ?? throw new InvalidDataException("The package manifest is empty.");
        Require(manifest.SchemaVersion == 1, "The package manifest schema changed.");
        Require(manifest.Protocol == ExpectedProtocol, "The package protocol identity changed.");
        Require(manifest.SourceInputCount == ExpectedSourceInputs, "The package source count changed.");
        Require(manifest.SourceSha256 == ExpectedSourceSha256, "The package source fingerprint changed.");

        var appTree = ComputeTreeIdentity(appRoot);
        var workerTree = ComputeTreeIdentity(workerRoot);
        RequireTree(appTree, manifest.AppTree, ExpectedAppFiles, ExpectedAppBytes, ExpectedAppTreeSha256, "App");
        RequireTree(
            workerTree,
            manifest.WorkerTree,
            ExpectedWorkerFiles,
            ExpectedWorkerBytes,
            ExpectedWorkerTreeSha256,
            "Worker");
        foreach (var pair in manifest.Files)
        {
            var pinnedPath = Path.Combine(
                artifactRoot,
                pair.Key.Replace('/', Path.DirectorySeparatorChar));
            RequireFile(pinnedPath, $"manifest file {pair.Key}");
            Require(Sha256File(pinnedPath) == pair.Value, $"Manifest hash changed for {pair.Key}.");
        }

        var forbiddenAssets = Directory
            .EnumerateFiles(appRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals("7z.dll", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("7za.exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("SharpSevenZip.dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(appRoot, path).Replace('\\', '/'))
            .ToArray();
        Require(forbiddenAssets.Length == 0, "A forbidden native/provider asset entered the package.");

        var developerTools = DiscoverDeveloperTools();
        if (!options.AllowDeveloperTools)
        {
            Require(
                developerTools.Count == 0,
                $"The clean machine contains disqualifying tools: {string.Join(", ", developerTools)}");
        }

        var appStartup = await VerifyAppStartupAsync(appPath).ConfigureAwait(false);
        var workRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives-M3-CleanMachine",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        IReadOnlyList<FormatEvidence> formats;
        try
        {
            formats = await ExerciseFormatsAsync(workerPath, workRoot).ConfigureAwait(false);
            Require(CountStaging(workRoot) == 0, "The clean-machine product flow left staging residue.");
        }
        finally
        {
            DeleteExactOwnedTree(workRoot, "RainArchives-M3-CleanMachine");
        }

        await WaitForNoRainProcessesAsync().ConfigureAwait(false);
        var remainingProcesses = GetRainProcesses();
        Require(remainingProcesses.Count == 0, "Rain processes remained after clean-machine acceptance.");

        return new CleanMachineEvidence(
            SchemaVersion: 1,
            RecordedUtc: DateTimeOffset.UtcNow,
            CleanMachineClaim: developerTools.Count == 0 && !options.AllowDeveloperTools,
            DeveloperToolOverride: options.AllowDeveloperTools,
            Protocol: ExpectedProtocol,
            SourceInputCount: ExpectedSourceInputs,
            SourceSha256: ExpectedSourceSha256,
            PackageArtifactRoot: artifactRoot,
            ManifestSha256: Sha256File(manifestPath),
            AppTree: appTree,
            WorkerTree: workerTree,
            PrincipalFileHashes: manifest.Files,
            ForbiddenAssets: forbiddenAssets,
            DeveloperToolEvidence: developerTools,
            MachineName: Environment.MachineName,
            OperatingSystem: Environment.OSVersion.VersionString,
            RuntimeVersion: Environment.Version.ToString(),
            Is64BitOperatingSystem: Environment.Is64BitOperatingSystem,
            Is64BitProcess: Environment.Is64BitProcess,
            UserName: WindowsIdentity.GetCurrent().Name,
            IsElevated: IsElevated(),
            AppStartup: appStartup,
            Formats: formats,
            RemainingRainProcesses: remainingProcesses,
            RemainingStagingArtifacts: 0,
            EvidenceRoot: evidenceRoot);
    }

    private static async Task<AppStartupEvidence> VerifyAppStartupAsync(string appPath)
    {
        var startInfo = new ProcessStartInfo(appPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appPath)!,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The packaged App did not start.");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            nint windowHandle = 0;
            string? windowTitle = null;
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(20) && !process.HasExited)
            {
                process.Refresh();
                windowHandle = process.MainWindowHandle;
                windowTitle = process.MainWindowTitle;
                if (windowHandle != 0)
                {
                    break;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The packaged App exited during startup with code {process.ExitCode}.");
            }

            Require(windowHandle != 0, "The packaged App did not expose a top-level window.");
            var closeRequested = process.CloseMainWindow();
            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The packaged App did not close after its main window was asked to close.");
            }

            Require(process.ExitCode == 0, $"The packaged App closed with exit code {process.ExitCode}.");
            return new AppStartupEvidence(
                Path.GetFileName(appPath),
                Sha256File(appPath),
                windowTitle,
                windowHandle != 0,
                closeRequested,
                process.ExitCode,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<IReadOnlyList<FormatEvidence>> ExerciseFormatsAsync(
        string workerPath,
        string workRoot)
    {
        var sourcePath = Path.Combine(workRoot, "manual-creation-雨.txt");
        const string content = "Rain Archives Milestone 3 clean-machine fixture.\nUnicode: 雨 雪\nProtocol: 1.18\n";
        await File.WriteAllTextAsync(sourcePath, content, new UTF8Encoding(false)).ConfigureAwait(false);
        var sourceHash = Sha256File(sourcePath);
        await using var client = await ArchiveClient
            .StartAsync(workerPath)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);

        VerifyCapabilities(await client.GetZipWriteCapabilitiesAsync().ConfigureAwait(false));
        VerifyTarCapabilities(await client.GetTarWriteCapabilitiesAsync().ConfigureAwait(false));
        VerifyManagedCapabilities(await client.GetManagedWriteCapabilitiesAsync().ConfigureAwait(false));

        var formats = new List<FormatEvidence>();
        var files = new[] { new ArchiveCreationFile(sourcePath, "manual-creation-雨.txt") };

        var zip = Path.Combine(workRoot, "clean-machine.zip");
        var started = await client.StartZipCreationAsync(zip, files, ArchiveCompressionPreset.Balanced)
            .WaitAsync(OperationTimeout).ConfigureAwait(false);
        formats.Add(await CompleteAndVerifyAsync(
            client, "ZIP", zip, "manual-creation-雨.txt", sourceHash, started).ConfigureAwait(false));

        var tar = Path.Combine(workRoot, "clean-machine.tar");
        started = await client.StartTarCreationAsync(tar, files)
            .WaitAsync(OperationTimeout).ConfigureAwait(false);
        formats.Add(await CompleteAndVerifyAsync(
            client, "TAR", tar, "manual-creation-雨.txt", sourceHash, started).ConfigureAwait(false));

        var tarGzip = Path.Combine(workRoot, "clean-machine.tar.gz");
        started = await client.StartTarGZipCreationAsync(tarGzip, files, ArchiveCompressionPreset.Maximum)
            .WaitAsync(OperationTimeout).ConfigureAwait(false);
        formats.Add(await CompleteAndVerifyAsync(
            client, "TAR.GZ", tarGzip, "manual-creation-雨.txt", sourceHash, started).ConfigureAwait(false));

        var tarBzip2 = Path.Combine(workRoot, "clean-machine.tar.bz2");
        started = await client.StartTarBZip2CreationAsync(tarBzip2, files, ArchiveCompressionPreset.Balanced)
            .WaitAsync(OperationTimeout).ConfigureAwait(false);
        formats.Add(await CompleteAndVerifyAsync(
            client, "TAR.BZ2", tarBzip2, "manual-creation-雨.txt", sourceHash, started).ConfigureAwait(false));

        var gzip = Path.Combine(workRoot, "clean-machine-gzip.gz");
        started = await client.StartGZipCreationAsync(gzip, sourcePath, ArchiveCompressionPreset.Fast)
            .WaitAsync(OperationTimeout).ConfigureAwait(false);
        formats.Add(await CompleteAndVerifyAsync(
            client, "GZIP", gzip, "clean-machine-gzip", sourceHash, started).ConfigureAwait(false));

        var bzip2 = Path.Combine(workRoot, "clean-machine-bzip2.bz2");
        started = await client.StartBZip2CreationAsync(bzip2, sourcePath, ArchiveCompressionPreset.Balanced)
            .WaitAsync(OperationTimeout).ConfigureAwait(false);
        formats.Add(await CompleteAndVerifyAsync(
            client, "BZIP2", bzip2, "clean-machine-bzip2", sourceHash, started).ConfigureAwait(false));

        Require(Sha256File(sourcePath) == sourceHash, "The source fixture changed during creation.");
        return formats;
    }

    private static async Task<FormatEvidence> CompleteAndVerifyAsync(
        ArchiveClient client,
        string format,
        string archivePath,
        string expectedEntry,
        string expectedSourceHash,
        StartOperationResponse started)
    {
        var terminal = await client.WaitForTerminalAsync(started.OperationId)
            .WaitAsync(OperationTimeout).ConfigureAwait(false);
        Require(
            terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings,
            $"{format} creation failed: {terminal.Error?.Code} {terminal.Error?.Message}");
        Require(terminal.Error is null, $"{format} creation returned an error.");
        RequireFile(archivePath, $"{format} destination");
        var archiveHashBefore = Sha256File(archivePath);
        var opened = await client.OpenArchiveAsync(archivePath)
            .WaitAsync(OperationTimeout).ConfigureAwait(false);
        int listedEntries;
        string extractedHash;
        try
        {
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 128)
                .WaitAsync(OperationTimeout).ConfigureAwait(false);
            Require(listed.Entries.Count == 1, $"{format} listed an unexpected entry count.");
            var entry = listed.Entries.Single();
            Require(entry.RawPath == expectedEntry, $"{format} exposed the wrong member name: {entry.RawPath}");
            listedEntries = listed.Entries.Count;

            var integrity = await client.StartIntegrityTestAsync(opened.Archive.SessionId, [])
                .WaitAsync(OperationTimeout).ConfigureAwait(false);
            var integrityTerminal = await client.WaitForTerminalAsync(integrity.OperationId)
                .WaitAsync(OperationTimeout).ConfigureAwait(false);
            Require(
                integrityTerminal.Phase == OperationPhase.Completed,
                $"{format} integrity failed: {integrityTerminal.Error?.Code} {integrityTerminal.Error?.Message}");

            var extractionRoot = Path.Combine(
                Path.GetDirectoryName(archivePath)!,
                $"extract-{format.Replace('.', '-')}-{Guid.NewGuid():N}");
            try
            {
                var extraction = await client.StartExtractionAsync(opened.Archive.SessionId, [], extractionRoot)
                    .WaitAsync(OperationTimeout).ConfigureAwait(false);
                var extractionTerminal = await client.WaitForTerminalAsync(extraction.OperationId)
                    .WaitAsync(OperationTimeout).ConfigureAwait(false);
                Require(
                    extractionTerminal.Phase == OperationPhase.Completed,
                    $"{format} extraction failed: {extractionTerminal.Error?.Code} {extractionTerminal.Error?.Message}");
                var extractedPath = Path.Combine(
                    extractionRoot,
                    expectedEntry.Replace('/', Path.DirectorySeparatorChar));
                RequireFile(extractedPath, $"{format} extracted member");
                extractedHash = Sha256File(extractedPath);
                Require(extractedHash == expectedSourceHash, $"{format} extracted payload hash changed.");
            }
            finally
            {
                if (Directory.Exists(extractionRoot))
                {
                    Directory.Delete(extractionRoot, recursive: true);
                }
            }
        }
        finally
        {
            var closed = await client.CloseArchiveAsync(opened.Archive.SessionId)
                .WaitAsync(OperationTimeout).ConfigureAwait(false);
            Require(closed.Closed, $"{format} session did not close.");
        }

        var archiveHashAfter = Sha256File(archivePath);
        Require(archiveHashBefore == archiveHashAfter, $"{format} archive changed during validation.");
        return new FormatEvidence(
            format,
            Path.GetFileName(archivePath),
            new FileInfo(archivePath).Length,
            archiveHashAfter,
            terminal.Phase.ToString(),
            terminal.Warning?.Code,
            listedEntries,
            expectedEntry,
            extractedHash,
            CountStaging(Path.GetDirectoryName(archivePath)!));
    }

    private static void VerifyCapabilities(GetZipWriteCapabilitiesResponse capabilities)
    {
        Require(capabilities.CanCreate, "ZIP creation capability is false.");
        Require(!capabilities.CanRebuild, "Global ZIP capabilities unexpectedly advertise rebuild.");
        Require(capabilities.CompressionPresets.Count == 4, "ZIP preset capability changed.");
        RequireUnsafeFlagsClosed(
            capabilities.SupportsEncryption,
            capabilities.SupportsFilenameEncryption,
            capabilities.SupportsSplitOutput,
            capabilities.SupportsExistingDestinationReplacement,
            capabilities.SupportsInPlaceUpdate,
            "ZIP");
    }

    private static void VerifyTarCapabilities(GetTarWriteCapabilitiesResponse capabilities)
    {
        Require(capabilities.CanCreate && !capabilities.CanRebuild, "TAR capability changed.");
        Require(capabilities.SupportsTreeCreation, "TAR tree capability is false.");
        Require(
            capabilities.CompressionPresets.SequenceEqual([ArchiveCompressionPreset.Store]),
            "TAR presets changed.");
        RequireUnsafeFlagsClosed(
            capabilities.SupportsEncryption,
            capabilities.SupportsFilenameEncryption,
            capabilities.SupportsSplitOutput,
            capabilities.SupportsExistingDestinationReplacement,
            capabilities.SupportsInPlaceUpdate,
            "TAR");
    }

    private static void VerifyManagedCapabilities(GetManagedWriteCapabilitiesResponse response)
    {
        Require(
            response.Capabilities.Select(item => item.Format).SequenceEqual(
                Enum.GetValues<ManagedArchiveCreationFormat>()),
            "Managed format ordering changed.");
        foreach (var capability in response.Capabilities)
        {
            Require(capability.CanCreate && !capability.CanRebuild, $"{capability.Format} capability changed.");
            RequireUnsafeFlagsClosed(
                capability.SupportsEncryption,
                capability.SupportsFilenameEncryption,
                capability.SupportsSplitOutput,
                capability.SupportsExistingDestinationReplacement,
                capability.SupportsInPlaceUpdate,
                capability.Format.ToString());
        }
    }

    private static void RequireUnsafeFlagsClosed(
        bool encryption,
        bool filenameEncryption,
        bool split,
        bool replacement,
        bool inPlace,
        string format)
    {
        Require(!encryption, $"{format} unexpectedly advertises encryption.");
        Require(!filenameEncryption, $"{format} unexpectedly advertises filename encryption.");
        Require(!split, $"{format} unexpectedly advertises split output.");
        Require(!replacement, $"{format} unexpectedly advertises replacement.");
        Require(!inPlace, $"{format} unexpectedly advertises in-place update.");
    }

    private static IReadOnlyList<string> DiscoverDeveloperTools()
    {
        var evidence = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
                 })
        {
            if (File.Exists(path))
            {
                evidence.Add(path);
            }
        }

        foreach (var executable in new[] { "dotnet.exe", "7z.exe", "7za.exe", "7zz.exe" })
        {
            foreach (var path in FindOnPath(executable))
            {
                evidence.Add(path);
            }
        }

        foreach (var keyName in new[]
                 {
                     @"SOFTWARE\dotnet\Setup\InstalledVersions\x64",
                     @"SOFTWARE\7-Zip",
                     @"SOFTWARE\WOW6432Node\7-Zip",
                 })
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyName, writable: false);
            if (key is not null)
            {
                evidence.Add($"HKLM\\{keyName}");
            }
        }

        return evidence.ToArray();
    }

    private static IEnumerable<string> FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), executable);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                yield return Path.GetFullPath(candidate);
            }
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static TreeIdentity ComputeTreeIdentity(string root)
    {
        var items = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
            })
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        foreach (var item in items)
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes(item.RelativePath));
            aggregate.AppendData([0]);
            using var stream = File.OpenRead(item.FullPath);
            aggregate.AppendData(SHA256.HashData(stream));
            bytes = checked(bytes + stream.Length);
        }

        return new TreeIdentity(items.Length, bytes, Convert.ToHexString(aggregate.GetHashAndReset()));
    }

    private static void RequireTree(
        TreeIdentity actual,
        TreeIdentity manifest,
        int files,
        long bytes,
        string sha256,
        string label)
    {
        Require(actual == manifest, $"{label} tree does not match the manifest.");
        Require(actual.Files == files, $"{label} file count changed.");
        Require(actual.Bytes == bytes, $"{label} byte count changed.");
        Require(actual.Sha256 == sha256, $"{label} tree hash changed.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task WaitForNoRainProcessesAsync()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (GetRainProcesses().Count == 0)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<ProcessEvidence> GetRainProcesses()
    {
        var results = new List<ProcessEvidence>();
        foreach (var name in new[] { "RainArchives", "RainArchives.Worker" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    results.Add(new ProcessEvidence(process.Id, process.ProcessName));
                }
            }
        }

        return results;
    }

    private static int CountStaging(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(
                root,
                "*.rainarchives-*.staging",
                SearchOption.AllDirectories).Count()
            : 0;

    private static void DeleteExactOwnedTree(string path, string requiredParentLeaf)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Directory.GetParent(canonical)
            ?? throw new InvalidOperationException("The owned work root has no parent.");
        Require(
            parent.Name.Equals(requiredParentLeaf, StringComparison.Ordinal),
            "Refusing to delete a work root outside the expected disposable parent.");
        Require(Guid.TryParseExact(Path.GetFileName(canonical), "N", out _), "The work root is not GUID-owned.");
        EnsureNoReparsePoints(canonical);
        Directory.Delete(canonical, recursive: true);
    }

    private static void EnsureNoReparsePoints(string root)
    {
        Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "The work root became a reparse point.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            Require((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0, "The work tree contains a reparse point.");
        }
    }

    private static async Task WriteEvidenceAsync<T>(string path, T value)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            BufferBytes,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static bool TryParseArguments(string[] args, out ProbeOptions options)
    {
        string? package = null;
        string? evidence = null;
        var allowDeveloperTools = false;
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
            else if (args[index] == "--allow-developer-tools")
            {
                allowDeveloperTools = true;
            }
            else
            {
                options = default;
                return false;
            }
        }

        options = new ProbeOptions(package ?? string.Empty, evidence ?? string.Empty, allowDeveloperTools);
        return !string.IsNullOrWhiteSpace(options.PackageArtifactRoot)
            && !string.IsNullOrWhiteSpace(options.EvidenceRoot);
    }

    private static void RequireFile(string path, string label) =>
        Require(File.Exists(path), $"The {label} is missing: {path}");

    private static void RequireDirectory(string path, string label) =>
        Require(Directory.Exists(path), $"The {label} is missing: {path}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= 4096 ? sanitized : sanitized[..4096];
    }

    private readonly record struct ProbeOptions(
        string PackageArtifactRoot,
        string EvidenceRoot,
        bool AllowDeveloperTools);
    private sealed record TreeIdentity(int Files, long Bytes, string Sha256);
    private sealed record PackageManifest(
        int SchemaVersion,
        string Protocol,
        int SourceInputCount,
        string SourceSha256,
        TreeIdentity AppTree,
        TreeIdentity WorkerTree,
        IReadOnlyDictionary<string, string> Files);
    private sealed record AppStartupEvidence(
        string FileName,
        string Sha256,
        string? WindowTitle,
        bool MainWindowObserved,
        bool CloseRequested,
        int ExitCode,
        double StartupMilliseconds);
    private sealed record FormatEvidence(
        string Format,
        string ArchiveFileName,
        long ArchiveBytes,
        string ArchiveSha256,
        string TerminalPhase,
        string? WarningCode,
        int ListedEntries,
        string MemberName,
        string ExtractedSha256,
        int RemainingStagingArtifacts);
    private sealed record ProcessEvidence(int ProcessId, string ProcessName);
    private sealed record CleanMachineEvidence(
        int SchemaVersion,
        DateTimeOffset RecordedUtc,
        bool CleanMachineClaim,
        bool DeveloperToolOverride,
        string Protocol,
        int SourceInputCount,
        string SourceSha256,
        string PackageArtifactRoot,
        string ManifestSha256,
        TreeIdentity AppTree,
        TreeIdentity WorkerTree,
        IReadOnlyDictionary<string, string> PrincipalFileHashes,
        IReadOnlyList<string> ForbiddenAssets,
        IReadOnlyList<string> DeveloperToolEvidence,
        string MachineName,
        string OperatingSystem,
        string RuntimeVersion,
        bool Is64BitOperatingSystem,
        bool Is64BitProcess,
        string UserName,
        bool IsElevated,
        AppStartupEvidence AppStartup,
        IReadOnlyList<FormatEvidence> Formats,
        IReadOnlyList<ProcessEvidence> RemainingRainProcesses,
        int RemainingStagingArtifacts,
        string EvidenceRoot);
    private sealed record FailureEvidence(
        DateTimeOffset RecordedUtc,
        string ExceptionType,
        string ExceptionMessage,
        string? StackTrace);
}
