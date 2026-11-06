using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Benchmarks;

internal static class CreationOperationProbe
{
    private const int MeasuredRuns = 5;
    private const int FixtureFileCount = 16;
    private const int FixtureFileBytes = 512 * 1024;
    private const int FixtureChunkBytes = 64 * 1024;
    private const int OperationTimeoutSeconds = 60;
    private const int CancellationTimeoutSeconds = 30;
    private const int ExternalToolTimeoutSeconds = 30;
    private const int ExternalToolOutputCharacterLimit = 64 * 1024;
    private const int CleanupDeleteMaximumAttempts = 8;
    private static readonly DateTime FixtureTimestampUtc =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: RainArchives.Benchmarks --creation-operation-probe " +
                "--output <new-guid-directory> --source-fingerprint <sha256> [--runs <1-10>] " +
                "[--bytes <262144-1048576>] --external-seven-zip <official-7z-executable>");
            return 2;
        }

        var outputRoot = Path.GetFullPath(options.OutputPath);
        if (!IsGuidOwnedOutputRoot(outputRoot)
            || Directory.Exists(outputRoot)
            || File.Exists(outputRoot)
            || Directory.GetParent(outputRoot) is not { Exists: true } outputParent)
        {
            Console.Error.WriteLine(
                "The operation-probe output must be a new directory whose leaf is a GUID, " +
                "and whose parent already exists.");
            return 2;
        }

        ExternalSevenZipSource externalSevenZipSource;
        try
        {
            externalSevenZipSource = await ResolveExternalSevenZipAsync(
                    options.ExternalSevenZipPath)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The external 7-Zip executable is invalid: {exception.Message}");
            return 2;
        }

        Directory.CreateDirectory(outputRoot);
        ExternalSevenZipTool? externalSevenZip = null;
        var externalValidationAttempts = new List<ExternalArchiveValidation>();
        try
        {
            externalSevenZip = await CreateOwnedExternalSevenZipAsync(
                    externalSevenZipSource,
                    outputRoot)
                .ConfigureAwait(false);
            var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException(
                    "The benchmark output does not contain RainArchives.Worker.exe.",
                    workerPath);
            }

            var fixture = await CreateFixtureAsync(outputRoot, options.PayloadBytes)
                .ConfigureAwait(false);
            var allSamples = new List<CreationSample>();
            var cancellationSamples = new List<CancellationSample>();
            foreach (var route in fixture.Routes)
            {
                foreach (var preset in Enum.GetValues<ArchiveCompressionPreset>())
                {
                    allSamples.AddRange(await RunMeasuredRouteAsync(
                        workerPath,
                        outputRoot,
                        fixture,
                        route,
                        preset,
                        options.MeasuredRuns,
                        externalSevenZip,
                        externalValidationAttempts).ConfigureAwait(false));
                }

                cancellationSamples.Add(await RunCancellationAsync(
                    workerPath,
                    outputRoot,
                    fixture,
                    route,
                    externalSevenZip,
                    externalValidationAttempts).ConfigureAwait(false));
            }

            var finalToolSha256 = await ComputeFileSha256Async(externalSevenZip.Path)
                .ConfigureAwait(false);
            var finalToolIdentity = CaptureFileIdentity(externalSevenZip.Path);
            if (!string.Equals(
                    finalToolSha256,
                    externalSevenZip.InitialOwnedSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    finalToolIdentity,
                    externalSevenZip.OwnedFileIdentity,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "The owned external 7-Zip executable changed during the benchmark run.");
            }

            externalSevenZip = externalSevenZip with
            {
                FinalOwnedSha256 = finalToolSha256,
                StableAfterRun = true,
            };
            DeleteOwnedExternalTool(outputRoot, externalSevenZip.Path);
            externalSevenZip = externalSevenZip with { OwnedCopyDeletedAfterRun = true };

            var directoryCleanup = DeleteOwnedDirectory(outputRoot, fixture.SourceRoot);
            var cleanup = VerifyCleanup(outputRoot, fixture.SourceRoot);
            if (!cleanup.SourceRootDeleted || cleanup.StagingArtifacts != 0 || cleanup.OtherOwnedArtifacts != 0)
            {
                throw new InvalidOperationException(
                    "The probe did not remove every owned fixture, archive, and staging artifact.");
            }

            var result = new CreationOperationProbeResult(
                DateTimeOffset.UtcNow,
                options.SourceFingerprint,
                outputRoot,
                outputParent.FullName,
                Environment.OSVersion.VersionString,
                Environment.Version.ToString(),
                Environment.Is64BitProcess,
                Environment.ProcessorCount,
                workerPath,
                await ComputeFileSha256Async(workerPath).ConfigureAwait(false),
                typeof(CreationOperationProbe).Assembly.Location,
                await ComputeFileSha256Async(typeof(CreationOperationProbe).Assembly.Location)
                    .ConfigureAwait(false),
                externalSevenZip,
                fixture.Manifest,
                fixture.ManifestSha256,
                options.MeasuredRuns,
                allSamples,
                cancellationSamples,
                externalValidationAttempts,
                directoryCleanup.Attempts,
                directoryCleanup.ElapsedMilliseconds,
                cleanup.SourceRootDeleted,
                cleanup.StagingArtifacts,
                cleanup.OtherOwnedArtifacts);
            var jsonPath = Path.Combine(outputRoot, "creation-operation-probe.json");
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);

            Console.WriteLine($"Rain Archives creation operation probe: {jsonPath}");
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception exception)
        {
            await TryWriteFailureEvidenceAsync(
                    outputRoot,
                    options.SourceFingerprint,
                    externalSevenZip,
                    externalValidationAttempts,
                    exception)
                .ConfigureAwait(false);
            Console.Error.WriteLine($"Creation operation probe failed. Evidence was preserved at: {outputRoot}");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<IReadOnlyList<CreationSample>> RunMeasuredRouteAsync(
        string workerPath,
        string outputRoot,
        Fixture fixture,
        FixtureRoute route,
        ArchiveCompressionPreset preset,
        int measuredRuns,
        ExternalSevenZipTool externalSevenZip,
        List<ExternalArchiveValidation> externalValidationAttempts)
    {
        await using var client = await ArchiveClient.StartAsync(workerPath).ConfigureAwait(false);
        VerifyWorkerOwnership(client, workerPath);

        var warmupDestination = Path.Combine(
            outputRoot,
            $"warmup-{route.Name}-{preset.ToString().ToLowerInvariant()}.zip");
        await RunCreationAsync(
            client,
            fixture,
            route,
            preset,
            warmupDestination,
            measured: false,
            run: -1,
            externalSevenZip,
            externalValidationAttempts).ConfigureAwait(false);
        DeleteOwnedFile(outputRoot, warmupDestination);

        var samples = new List<CreationSample>(measuredRuns);
        for (var run = 0; run < measuredRuns; run++)
        {
            var destination = Path.Combine(
                outputRoot,
                $"creation-{route.Name}-{preset.ToString().ToLowerInvariant()}-{run:D2}.zip");
            samples.Add(await RunCreationAsync(
                client,
                fixture,
                route,
                preset,
                destination,
                measured: true,
                run,
                externalSevenZip,
                externalValidationAttempts).ConfigureAwait(false));
            DeleteOwnedFile(outputRoot, destination);
        }

        return samples;
    }

    private static async Task<CreationSample> RunCreationAsync(
        ArchiveClient client,
        Fixture fixture,
        FixtureRoute route,
        ArchiveCompressionPreset preset,
        string destination,
        bool measured,
        int run,
        ExternalSevenZipTool externalSevenZip,
        List<ExternalArchiveValidation> externalValidationAttempts)
    {
        var workerId = GetWorkerProcessId(client);
        await using var sampler = new ProcessResourceSampler(workerId);
        await sampler.StartAsync().ConfigureAwait(false);
        OperationId targetOperation = default;
        OperationProgress? latestProgress = null;
        var progressByOperation = new ConcurrentDictionary<OperationId, OperationProgress>();
        EventHandler<OperationEvent>? handler = (_, operationEvent) =>
        {
            if (operationEvent.Kind == OperationEventKind.Progress
                && operationEvent.Progress is not null)
            {
                progressByOperation[operationEvent.OperationId] = operationEvent.Progress;
                if (operationEvent.OperationId == targetOperation)
                {
                    latestProgress = operationEvent.Progress;
                }
            }
        };
        client.OperationEventReceived += handler;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var started = route.IsDirectoryTree
                ? await client.StartZipTreeCreationAsync(
                        destination,
                        route.Files,
                        route.Directories,
                        preset)
                    .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                    .ConfigureAwait(false)
                : await client.StartZipCreationAsync(
                        destination,
                        route.Files,
                        preset)
                    .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                    .ConfigureAwait(false);
            targetOperation = started.OperationId;
            if (progressByOperation.TryGetValue(targetOperation, out var bufferedProgress))
            {
                latestProgress = bufferedProgress;
            }
            var terminal = await client.WaitForTerminalAsync(started.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                .ConfigureAwait(false);
            stopwatch.Stop();
            var resources = await sampler.StopAsync().ConfigureAwait(false);
            RequireCompleted(terminal, $"{route.Name} {preset} creation");

            var output = await ValidateCreatedArchiveAsync(
                client,
                destination,
                route,
                externalSevenZip,
                externalValidationAttempts).ConfigureAwait(false);
            var stagingArtifacts = CountStagingArtifacts(Path.GetDirectoryName(destination)!);
            if (stagingArtifacts != 0)
            {
                throw new InvalidDataException("A completed creation left a staging directory.");
            }

            return new CreationSample(
                route.Name,
                route.IsDirectoryTree ? "directory-tree" : "source-file",
                preset.ToString(),
                measured,
                run,
                output.OutputBytes,
                output.OutputSha256,
                stopwatch.Elapsed.TotalMilliseconds,
                resources.CpuMilliseconds,
                resources.PeakWorkingSetBytes,
                latestProgress?.BytesProcessed ?? terminal.Progress?.BytesProcessed,
                latestProgress?.EntriesProcessed ?? terminal.Progress?.EntriesProcessed,
                terminal.Phase.ToString(),
                output.ListedEntryCount,
                output.IntegrityPhase,
                output.IntegrityMilliseconds,
                output.VerifiedEntryCount,
                output.VerifiedFileCount,
                output.VerifiedFileBytes,
                output.ContentAggregateSha256,
                output.ExternalValidation);
        }
        finally
        {
            client.OperationEventReceived -= handler;
        }
    }

    private static async Task<CancellationSample> RunCancellationAsync(
        string workerPath,
        string outputRoot,
        Fixture fixture,
        FixtureRoute route,
        ExternalSevenZipTool externalSevenZip,
        List<ExternalArchiveValidation> externalValidationAttempts)
    {
        await using var client = await ArchiveClient.StartAsync(workerPath).ConfigureAwait(false);
        VerifyWorkerOwnership(client, workerPath);
        var warmupDestination = Path.Combine(outputRoot, $"warmup-cancel-{route.Name}.zip");
        await RunCreationAsync(
            client,
            fixture,
            route,
            ArchiveCompressionPreset.Balanced,
            warmupDestination,
            measured: false,
            run: -1,
            externalSevenZip,
            externalValidationAttempts).ConfigureAwait(false);
        DeleteOwnedFile(outputRoot, warmupDestination);

        var destination = Path.Combine(outputRoot, $"cancel-{route.Name}.zip");
        var progressReached = new TaskCompletionSource<OperationEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        OperationId targetOperation = default;
        var progressByOperation = new ConcurrentDictionary<OperationId, OperationEvent>();
        EventHandler<OperationEvent>? handler = (_, operationEvent) =>
        {
            if (operationEvent.Kind == OperationEventKind.Progress
                && operationEvent.Progress?.BytesProcessed > 0
                && operationEvent.OperationId.Value != Guid.Empty)
            {
                progressByOperation[operationEvent.OperationId] = operationEvent;
                if (operationEvent.OperationId == targetOperation)
                {
                    progressReached.TrySetResult(operationEvent);
                }
            }
        };
        client.OperationEventReceived += handler;
        try
        {
            var workerId = GetWorkerProcessId(client);
            await using var sampler = new ProcessResourceSampler(workerId);
            await sampler.StartAsync().ConfigureAwait(false);
            var started = route.IsDirectoryTree
                ? await client.StartZipTreeCreationAsync(
                        destination,
                        route.Files,
                        route.Directories,
                        ArchiveCompressionPreset.Balanced)
                    .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                    .ConfigureAwait(false)
                : await client.StartZipCreationAsync(
                        destination,
                        route.Files,
                        ArchiveCompressionPreset.Balanced)
                    .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                    .ConfigureAwait(false);
            targetOperation = started.OperationId;
            if (progressByOperation.TryGetValue(targetOperation, out var bufferedProgress))
            {
                progressReached.TrySetResult(bufferedProgress);
            }
            var progress = await progressReached.Task
                .WaitAsync(TimeSpan.FromSeconds(CancellationTimeoutSeconds))
                .ConfigureAwait(false);
            var totalSourceBytes = route.TotalFileBytes;
            var observedBytes = progress.Progress?.BytesProcessed ?? 0;
            if (observedBytes <= 0 || observedBytes >= totalSourceBytes)
            {
                throw new InvalidDataException(
                    $"Cancellation did not observe partial progress for {route.Name}: " +
                    $"observed={observedBytes}, total={totalSourceBytes}.");
            }
            var stopwatch = Stopwatch.StartNew();
            var cancellation = await client.CancelOperationAsync(started.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                .ConfigureAwait(false);
            var terminal = await client.WaitForTerminalAsync(started.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                .ConfigureAwait(false);
            stopwatch.Stop();
            var resources = await sampler.StopAsync().ConfigureAwait(false);
            var stagingArtifacts = CountStagingArtifacts(outputRoot);
            var finalExists = File.Exists(destination);
            if (!cancellation.CancellationRequested || terminal.Phase != OperationPhase.Cancelled)
            {
                throw new InvalidOperationException(
                    $"Cancellation was not authoritative: accepted={cancellation.CancellationRequested}, " +
                    $"terminal={terminal.Phase}.");
            }

            if (finalExists || stagingArtifacts != 0)
            {
                throw new InvalidDataException(
                    "Cancellation after real progress left a final archive or staging residue.");
            }

            return new CancellationSample(
                route.Name,
                observedBytes,
                totalSourceBytes,
                true,
                stopwatch.Elapsed.TotalMilliseconds,
                resources.CpuMilliseconds,
                resources.PeakWorkingSetBytes,
                cancellation.CancellationRequested,
                terminal.Phase.ToString(),
                finalExists,
                stagingArtifacts);
        }
        finally
        {
            client.OperationEventReceived -= handler;
        }
    }

    private static async Task<ArchiveValidation> ValidateCreatedArchiveAsync(
        ArchiveClient client,
        string archivePath,
        FixtureRoute route,
        ExternalSevenZipTool externalSevenZip,
        List<ExternalArchiveValidation> externalValidationAttempts)
    {
        if (!File.Exists(archivePath)
            || (File.GetAttributes(archivePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Creation did not publish a regular archive file.");
        }

        var info = new FileInfo(archivePath);
        var sha256 = await ComputeFileSha256Async(archivePath).ConfigureAwait(false);
        var opened = await client.OpenArchiveAsync(archivePath)
            .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
            .ConfigureAwait(false);
        try
        {
            if (!string.Equals(opened.Archive.Format, "zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Rain reopened the output as {opened.Archive.Format}.");
            }

            var entries = await ReadAllEntriesAsync(client, opened.Archive.SessionId)
                .ConfigureAwait(false);
            var contentVerification = await VerifyArchiveEntriesAsync(
                    client,
                    opened.Archive.SessionId,
                    route,
                    entries)
                .ConfigureAwait(false);

            var integrityStopwatch = Stopwatch.StartNew();
            var integrityStarted = await client.StartIntegrityTestAsync(opened.Archive.SessionId, [])
                .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                .ConfigureAwait(false);
            var integrityTerminal = await client.WaitForTerminalAsync(integrityStarted.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                .ConfigureAwait(false);
            integrityStopwatch.Stop();
            RequireCompleted(integrityTerminal, $"{route.Name} integrity test");
            var externalValidation = await ValidateWithExternalSevenZipAsync(
                    externalSevenZip,
                    archivePath,
                    info.Length,
                    sha256)
                .ConfigureAwait(false);
            externalValidationAttempts.Add(externalValidation);
            if (!externalValidation.Passed)
            {
                throw new InvalidDataException(
                    $"Official 7-Zip did not validate '{Path.GetFileName(archivePath)}': " +
                    externalValidation.FailureKind);
            }

            return new ArchiveValidation(
                info.Length,
                sha256,
                entries.Count,
                integrityTerminal.Phase.ToString(),
                integrityStopwatch.Elapsed.TotalMilliseconds,
                contentVerification.VerifiedEntryCount,
                contentVerification.VerifiedFileCount,
                contentVerification.VerifiedFileBytes,
                contentVerification.ContentAggregateSha256,
                externalValidation);
        }
        finally
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.CloseArchiveAsync(opened.Archive.SessionId, closeTimeout.Token)
                .ConfigureAwait(false);
        }
    }

    private static async Task<EntryContentVerification> VerifyArchiveEntriesAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId,
        FixtureRoute route,
        IReadOnlyList<ArchiveEntryDescriptor> entries)
    {
        var expectedEntries = route.ExpectedArchiveEntries()
            .OrderBy(entry => NormalizeListedPath(entry.ArchivePath), StringComparer.Ordinal)
            .ToArray();
        var actualByPath = new Dictionary<string, ArchiveEntryDescriptor>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var normalizedPath = NormalizeListedPath(entry.RawPath);
            if (normalizedPath.Length == 0 || !actualByPath.TryAdd(normalizedPath, entry))
            {
                throw new InvalidDataException(
                    "Rain listed an empty or duplicate normalized path in a created archive.");
            }
        }

        if (actualByPath.Count != expectedEntries.Length)
        {
            throw new InvalidDataException(
                $"Rain listed {actualByPath.Count:N0} material entries for {route.Name}; " +
                $"the deterministic output manifest requires {expectedEntries.Length:N0}.");
        }

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var verifiedFiles = 0;
        long verifiedBytes = 0;
        var lengthBytes = new byte[sizeof(long)];
        foreach (var expected in expectedEntries)
        {
            var normalizedPath = NormalizeListedPath(expected.ArchivePath);
            if (!actualByPath.TryGetValue(normalizedPath, out var actual))
            {
                throw new InvalidDataException(
                    $"Rain omitted the deterministic output entry '{normalizedPath}'.");
            }

            var expectedKind = expected.Kind switch
            {
                "file" => ArchiveEntryKind.File,
                "directory" => ArchiveEntryKind.Directory,
                _ => throw new InvalidDataException(
                    $"The fixture uses unsupported expected entry kind '{expected.Kind}'."),
            };
            if (actual.Kind != expectedKind || actual.SafetyStatus != EntrySafetyStatus.Safe)
            {
                throw new InvalidDataException(
                    $"The created entry '{normalizedPath}' did not retain its expected safe kind.");
            }

            var pathBytes = Encoding.UTF8.GetBytes(normalizedPath);
            aggregate.AppendData(pathBytes);
            aggregate.AppendData([0]);
            aggregate.AppendData([(byte)expectedKind]);
            BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, expected.Bytes);
            aggregate.AppendData(lengthBytes);

            if (expectedKind != ArchiveEntryKind.File)
            {
                continue;
            }

            if (expected.Sha256 is null
                || actual.OriginalSize != expected.Bytes
                || expected.Bytes < 0
                || expected.Bytes > ProtocolConstants.MaximumEntryStreamBytes)
            {
                throw new InvalidDataException(
                    $"The created file '{normalizedPath}' has inconsistent fixture or listing metadata.");
            }

            using var streamTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(OperationTimeoutSeconds));
            await using var lease = await client.OpenEntryStreamAsync(
                    sessionId,
                    actual.EntryId,
                    maximumBytes: Math.Max(1, expected.Bytes),
                    cancellationToken: streamTimeout.Token)
                .ConfigureAwait(false);
            if (lease.DeclaredLength != expected.Bytes)
            {
                throw new InvalidDataException(
                    $"The created file '{normalizedPath}' exposed an inconsistent stream length.");
            }

            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FixtureChunkBytes];
            long bytesRead = 0;
            while (true)
            {
                var count = await lease.Content
                    .ReadAsync(buffer, streamTimeout.Token)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                bytesRead = checked(bytesRead + count);
                fileHash.AppendData(buffer, 0, count);
            }

            var actualSha256 = Convert.ToHexString(fileHash.GetHashAndReset());
            if (bytesRead != expected.Bytes
                || !string.Equals(actualSha256, expected.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The created file '{normalizedPath}' did not match its deterministic source bytes.");
            }

            aggregate.AppendData(Convert.FromHexString(actualSha256));
            verifiedFiles++;
            verifiedBytes = checked(verifiedBytes + bytesRead);
        }

        return new EntryContentVerification(
            expectedEntries.Length,
            verifiedFiles,
            verifiedBytes,
            Convert.ToHexString(aggregate.GetHashAndReset()));
    }

    private static string NormalizeListedPath(string path) => path.TrimEnd('/', '\\');

    private static async Task<Fixture> CreateFixtureAsync(string outputRoot, int payloadBytes)
    {
        var sourceRoot = Path.Combine(outputRoot, "source-fixture");
        Directory.CreateDirectory(sourceRoot);
        var explicitRoot = Path.Combine(sourceRoot, "explicit");
        var treeRoot = Path.Combine(sourceRoot, "tree");
        Directory.CreateDirectory(explicitRoot);
        Directory.CreateDirectory(Path.Combine(treeRoot, "branch-a", "deep"));
        Directory.CreateDirectory(Path.Combine(treeRoot, "branch-b", "empty"));
        Directory.CreateDirectory(Path.Combine(treeRoot, "unicodé"));
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories).Append(sourceRoot))
        {
            Directory.SetLastWriteTimeUtc(directory, FixtureTimestampUtc);
        }

        var manifest = new List<FixtureManifestEntry>();
        var explicitFiles = new List<ArchiveCreationFile>();
        for (var index = 0; index < FixtureFileCount; index++)
        {
            var source = Path.Combine(explicitRoot, $"file-{index:D2}.bin");
            await CreateDeterministicFileAsync(source, payloadBytes, index).ConfigureAwait(false);
            var archivePath = $"explicit/file-{index:D2}.bin";
            explicitFiles.Add(new ArchiveCreationFile(source, archivePath));
            manifest.Add(await CreateManifestEntryAsync(
                source,
                $"explicit/{Path.GetFileName(source)}",
                archivePath,
                "explicit",
                "file").ConfigureAwait(false));
        }

        var treeFiles = new[]
        {
            (Path.Combine(treeRoot, "root.bin"), "tree/root.bin", 100),
            (Path.Combine(treeRoot, "branch-a", "a.bin"), "tree/branch-a/a.bin", 101),
            (Path.Combine(treeRoot, "branch-a", "deep", "deep.bin"), "tree/branch-a/deep/deep.bin", 102),
            (Path.Combine(treeRoot, "branch-b", "b.bin"), "tree/branch-b/b.bin", 103),
            (Path.Combine(treeRoot, "unicodé", "雨.bin"), "tree/unicodé/雨.bin", 104),
        };
        var treeFileRequests = new List<ArchiveCreationFile>();
        foreach (var (source, archivePath, seed) in treeFiles)
        {
            await CreateDeterministicFileAsync(source, payloadBytes, seed).ConfigureAwait(false);
            treeFileRequests.Add(new ArchiveCreationFile(source, archivePath));
            manifest.Add(await CreateManifestEntryAsync(
                source,
                Path.GetRelativePath(treeRoot, source).Replace('\\', '/'),
                archivePath,
                "tree",
                "file").ConfigureAwait(false));
        }

        var directories = new List<ArchiveCreationDirectory> { new(treeRoot, "tree") };
        foreach (var relative in new[] { ".", "branch-a", "branch-a/deep", "branch-b", "branch-b/empty", "unicodé" })
        {
            var archivePath = relative == "." ? "tree/" : $"tree/{relative}/";
            manifest.Add(new FixtureManifestEntry(
                "tree-directory",
                relative,
                archivePath,
                "directory",
                0,
                null));
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories).Append(sourceRoot))
        {
            Directory.SetLastWriteTimeUtc(directory, FixtureTimestampUtc);
        }

        manifest = manifest
            .OrderBy(entry => entry.ArchivePath, StringComparer.Ordinal)
            .ToList();
        var manifestJson = JsonSerializer.Serialize(manifest);
        return new Fixture(
            sourceRoot,
            new FixtureRoute(
                "files",
                false,
                explicitFiles,
                [],
                manifest.Where(entry => entry.Route == "explicit").ToArray()),
            new FixtureRoute(
                "tree",
                true,
                [],
                directories,
                manifest.Where(entry => entry.Route == "tree-directory" || entry.ArchivePath.StartsWith("tree/", StringComparison.Ordinal)).ToArray()),
            manifest,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))));
    }

    private static async Task CreateDeterministicFileAsync(string path, int bytes, int seed)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            FixtureChunkBytes,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[FixtureChunkBytes];
        var state = unchecked((uint)(0x9E3779B9 + seed * 0x45D9F3B));
        var remaining = bytes;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, buffer.Length);
            for (var index = 0; index < count; index++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                buffer[index] = (byte)state;
            }

            await stream.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            remaining -= count;
        }

        await stream.FlushAsync().ConfigureAwait(false);
        File.SetLastWriteTimeUtc(path, FixtureTimestampUtc);
    }

    private static async Task<FixtureManifestEntry> CreateManifestEntryAsync(
        string sourcePath,
        string sourceRelativePath,
        string archivePath,
        string route,
        string kind) =>
        new(
            route,
            sourceRelativePath,
            archivePath,
            kind,
            new FileInfo(sourcePath).Length,
            await ComputeFileSha256Async(sourcePath).ConfigureAwait(false));

    private static async Task<IReadOnlyList<ArchiveEntryDescriptor>> ReadAllEntriesAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId)
    {
        var entries = new List<ArchiveEntryDescriptor>();
        var offset = 0;
        while (true)
        {
            var page = await client.ListEntriesAsync(sessionId, offset, 512)
                .WaitAsync(TimeSpan.FromSeconds(OperationTimeoutSeconds))
                .ConfigureAwait(false);
            entries.AddRange(page.Entries);
            if (page.NextOffset is not { } nextOffset)
            {
                return entries;
            }

            offset = nextOffset;
        }
    }

    private static void RequireCompleted(OperationEvent terminal, string operation)
    {
        if (terminal.Phase is not (OperationPhase.Completed or OperationPhase.CompletedWithWarnings))
        {
            throw new InvalidDataException(
                $"The {operation} ended in {terminal.Phase}: {terminal.Error?.Code}");
        }
    }

    private static bool TryParseArguments(string[] args, out ProbeOptions options)
    {
        string? output = null;
        string? sourceFingerprint = null;
        string? externalSevenZipPath = null;
        var runs = MeasuredRuns;
        var bytes = FixtureFileBytes;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--output" && index + 1 < args.Length && output is null)
            {
                output = args[++index];
                continue;
            }

            if (args[index] == "--source-fingerprint"
                && index + 1 < args.Length
                && sourceFingerprint is null)
            {
                sourceFingerprint = args[++index].ToUpperInvariant();
                continue;
            }

            if (args[index] == "--runs"
                && index + 1 < args.Length
                && int.TryParse(args[++index], out var parsedRuns))
            {
                runs = parsedRuns;
                continue;
            }

            if (args[index] == "--bytes"
                && index + 1 < args.Length
                && int.TryParse(args[++index], out var parsedBytes))
            {
                bytes = parsedBytes;
                continue;
            }

            if (args[index] == "--external-seven-zip"
                && index + 1 < args.Length
                && externalSevenZipPath is null)
            {
                externalSevenZipPath = args[++index];
                continue;
            }

            options = default;
            return false;
        }

        if (string.IsNullOrWhiteSpace(output)
            || !IsSha256(sourceFingerprint)
            || string.IsNullOrWhiteSpace(externalSevenZipPath)
            || runs is < 1 or > 10
            || bytes is < 256 * 1024 or > 1 * 1024 * 1024)
        {
            options = default;
            return false;
        }

        options = new ProbeOptions(output, sourceFingerprint!, runs, bytes, externalSevenZipPath!);
        return true;
    }

    private static async Task<ExternalSevenZipSource> ResolveExternalSevenZipAsync(
        string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
        {
            throw new ArgumentException("The path must be a nonempty fully qualified path.");
        }

        var path = Path.GetFullPath(configuredPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The configured executable does not exist.", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The configured executable must not be a reparse point.");
        }

        var version = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(version.ProductName, "7-Zip", StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable metadata does not identify the product as 7-Zip.");
        }

        return new ExternalSevenZipSource(
            path,
            await ComputeFileSha256Async(path).ConfigureAwait(false),
            version.ProductName,
            version.ProductVersion,
            version.FileVersion,
            version.CompanyName,
            version.OriginalFilename);
    }

    private static async Task<ExternalSevenZipTool> CreateOwnedExternalSevenZipAsync(
        ExternalSevenZipSource source,
        string outputRoot)
    {
        var ownedPath = Path.Combine(outputRoot, "external-seven-zip-validator.exe");
        if (FileSystemIdentityService.PathEntryExists(ownedPath))
        {
            throw new IOException("The owned external-validator path unexpectedly exists.");
        }

        await using var sourceStream = new FileStream(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FixtureChunkBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceSnapshot = FileSystemIdentityService.CaptureOpenFile(sourceStream, source.Path);
        var sourceSha256 = Convert.ToHexString(
            await SHA256.HashDataAsync(sourceStream).ConfigureAwait(false));
        if (!string.Equals(sourceSha256, source.Sha256, StringComparison.Ordinal))
        {
            throw new IOException("The external 7-Zip source changed before it could be copied.");
        }

        sourceStream.Position = 0;
        await using (var ownedStream = new FileStream(
                         ownedPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         FixtureChunkBytes,
                         FileOptions.Asynchronous
                         | FileOptions.SequentialScan
                         | FileOptions.WriteThrough))
        {
            await sourceStream.CopyToAsync(ownedStream).ConfigureAwait(false);
            await ownedStream.FlushAsync().ConfigureAwait(false);
            ownedStream.Flush(flushToDisk: true);
        }

        if (FileSystemIdentityService.CaptureOpenFile(sourceStream, source.Path) != sourceSnapshot)
        {
            throw new IOException("The external 7-Zip source changed while it was copied.");
        }

        var ownedSha256 = await ComputeFileSha256Async(ownedPath).ConfigureAwait(false);
        if (!string.Equals(ownedSha256, source.Sha256, StringComparison.Ordinal))
        {
            throw new IOException("The owned external 7-Zip copy did not match its source hash.");
        }

        var version = FileVersionInfo.GetVersionInfo(ownedPath);
        if (!string.Equals(version.ProductName, "7-Zip", StringComparison.Ordinal)
            || !string.Equals(version.ProductVersion, source.ProductVersion, StringComparison.Ordinal)
            || !string.Equals(version.FileVersion, source.FileVersion, StringComparison.Ordinal))
        {
            throw new IOException("The owned validator metadata did not match the checked 7-Zip source.");
        }

        return new ExternalSevenZipTool(
            source.Path,
            source.Sha256,
            sourceSnapshot.Identity,
            ownedPath,
            ownedSha256,
            CaptureFileIdentity(ownedPath),
            version.ProductName,
            version.ProductVersion,
            version.FileVersion,
            version.CompanyName,
            version.OriginalFilename,
            ["t", "-bd", "-y", "--", "<archive-path>"],
            ExternalToolTimeoutSeconds,
            ExternalToolOutputCharacterLimit,
            FinalOwnedSha256: null,
            StableAfterRun: false,
            OwnedCopyDeletedAfterRun: false);
    }

    private static async Task<ExternalArchiveValidation> ValidateWithExternalSevenZipAsync(
        ExternalSevenZipTool tool,
        string archivePath,
        long archiveLength,
        string archiveSha256)
    {
        var arguments = new[] { "t", "-bd", "-y", "--", archivePath };
        var startInfo = new ProcessStartInfo
        {
            FileName = tool.Path,
            WorkingDirectory = Path.GetDirectoryName(tool.Path)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        CancellationTokenSource? outputCancellation = null;
        Task<ProcessOutputCapture>? stdoutTask = null;
        Task<ProcessOutputCapture>? stderrTask = null;
        var stdout = ProcessOutputCapture.Empty;
        var stderr = ProcessOutputCapture.Empty;
        string? validatorSha256BeforeLaunch = null;
        string? validatorSha256AfterExit = null;
        string? validatorIdentityBeforeLaunch = null;
        string? validatorIdentityAfterExit = null;
        string? failureKind = null;
        int? exitCode = null;
        var started = false;
        var forcedTermination = false;
        var timedOut = false;
        var outputCleanupFailed = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            validatorSha256BeforeLaunch = await ComputeFileSha256Async(tool.Path)
                .ConfigureAwait(false);
            validatorIdentityBeforeLaunch = CaptureFileIdentity(tool.Path);
            if (!string.Equals(
                    validatorSha256BeforeLaunch,
                    tool.InitialOwnedSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    validatorIdentityBeforeLaunch,
                    tool.OwnedFileIdentity,
                    StringComparison.Ordinal))
            {
                failureKind = "validator_changed_before_launch";
            }
            else
            {
                process = new Process { StartInfo = startInfo };
                try
                {
                    started = process.Start();
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or IOException
                        or System.ComponentModel.Win32Exception)
                {
                    failureKind = "process_start_failed";
                }

                if (!started && failureKind is null)
                {
                    failureKind = "process_start_refused";
                }

                if (started)
                {
                    outputCancellation = new CancellationTokenSource();
                    stdoutTask = ReadBoundedProcessOutputAsync(
                        process.StandardOutput,
                        tool.OutputCharacterLimit,
                        outputCancellation.Token);
                    stderrTask = ReadBoundedProcessOutputAsync(
                        process.StandardError,
                        tool.OutputCharacterLimit,
                        outputCancellation.Token);
                    using var timeout = new CancellationTokenSource(
                        TimeSpan.FromSeconds(tool.TimeoutSeconds));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        timedOut = true;
                        failureKind = "timeout";
                        forcedTermination = await TerminateExternalProcessAsync(process)
                            .ConfigureAwait(false);
                    }

                    if (process.HasExited)
                    {
                        exitCode = process.ExitCode;
                    }

                    var captured = await CompleteProcessOutputCaptureAsync(
                            process,
                            stdoutTask,
                            stderrTask,
                            outputCancellation)
                        .ConfigureAwait(false);
                    stdout = captured.StandardOutput;
                    stderr = captured.StandardError;
                    outputCleanupFailed = captured.CleanupFailed;
                }
            }
        }
        catch (Exception exception)
        {
            failureKind ??= $"validator_exception_{exception.GetType().Name}";
            if (started && process is not null)
            {
                try
                {
                    forcedTermination |= await TerminateExternalProcessAsync(process)
                        .ConfigureAwait(false);
                }
                catch (Exception terminationException)
                {
                    failureKind = $"termination_exception_{terminationException.GetType().Name}";
                }

                if (stdoutTask is not null
                    && stderrTask is not null
                    && outputCancellation is not null)
                {
                    var captured = await CompleteProcessOutputCaptureAsync(
                            process,
                            stdoutTask,
                            stderrTask,
                            outputCancellation)
                        .ConfigureAwait(false);
                    stdout = captured.StandardOutput;
                    stderr = captured.StandardError;
                    outputCleanupFailed = captured.CleanupFailed;
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tool.Path)
                    && (File.GetAttributes(tool.Path) & FileAttributes.ReparsePoint) == 0)
                {
                    validatorSha256AfterExit = await ComputeFileSha256Async(tool.Path)
                        .ConfigureAwait(false);
                    validatorIdentityAfterExit = CaptureFileIdentity(tool.Path);
                }
            }
            catch (Exception exception)
            {
                failureKind ??= $"validator_revalidation_{exception.GetType().Name}";
            }

            stopwatch.Stop();
            outputCancellation?.Dispose();
            process?.Dispose();
        }

        var validatorStable = string.Equals(
                validatorSha256BeforeLaunch,
                tool.InitialOwnedSha256,
                StringComparison.Ordinal)
            && string.Equals(
                validatorSha256AfterExit,
                tool.InitialOwnedSha256,
                StringComparison.Ordinal)
            && string.Equals(
                validatorIdentityBeforeLaunch,
                tool.OwnedFileIdentity,
                StringComparison.Ordinal)
            && string.Equals(
                validatorIdentityAfterExit,
                tool.OwnedFileIdentity,
                StringComparison.Ordinal);
        long? archiveLengthAfterValidation = null;
        string? archiveSha256AfterValidation = null;
        try
        {
            var finalArchive = new FileInfo(archivePath);
            finalArchive.Refresh();
            if (!finalArchive.Exists
                || (finalArchive.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                failureKind ??= "archive_missing_or_replaced";
            }
            else
            {
                archiveLengthAfterValidation = finalArchive.Length;
                archiveSha256AfterValidation = await ComputeFileSha256Async(archivePath)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failureKind ??= $"archive_revalidation_{exception.GetType().Name}";
        }

        var archiveStable = archiveLengthAfterValidation == archiveLength
            && string.Equals(
                archiveSha256AfterValidation,
                archiveSha256,
                StringComparison.Ordinal);
        var outputTruncated = stdout.Truncated || stderr.Truncated;
        failureKind ??= !archiveStable
            ? "archive_changed_during_validation"
            : !validatorStable
            ? "validator_changed"
            : outputCleanupFailed
                ? "output_cleanup_failed"
                : outputTruncated
                    ? "output_limit_exceeded"
                    : exitCode != 0
                        ? "nonzero_exit"
                        : null;
        var passed = started
            && !timedOut
            && !forcedTermination
            && !outputCleanupFailed
            && !outputTruncated
            && archiveStable
            && validatorStable
            && exitCode == 0
            && failureKind is null;

        return new ExternalArchiveValidation(
            Path.GetFileName(archivePath),
            archiveLength,
            archiveSha256,
            archiveLengthAfterValidation,
            archiveSha256AfterValidation,
            archiveStable,
            arguments,
            started,
            passed,
            exitCode,
            stopwatch.Elapsed.TotalMilliseconds,
            forcedTermination,
            timedOut,
            outputCleanupFailed,
            outputTruncated,
            failureKind,
            validatorSha256BeforeLaunch,
            validatorSha256AfterExit,
            validatorIdentityBeforeLaunch,
            validatorIdentityAfterExit,
            stdout.TotalCharacters,
            stdout.CapturedText.Length,
            ComputeTextSha256(stdout.CapturedText),
            stdout.Truncated,
            stderr.TotalCharacters,
            stderr.CapturedText.Length,
            ComputeTextSha256(stderr.CapturedText),
            stderr.Truncated);
    }

    private static async Task<ProcessOutputPair> CompleteProcessOutputCaptureAsync(
        Process process,
        Task<ProcessOutputCapture> stdoutTask,
        Task<ProcessOutputCapture> stderrTask,
        CancellationTokenSource outputCancellation)
    {
        var combined = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            var results = await combined
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            return new ProcessOutputPair(results[0], results[1], CleanupFailed: false);
        }
        catch (Exception)
        {
            outputCancellation.Cancel();
            try
            {
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
            }
            catch (Exception)
            {
            }
            try
            {
                _ = await combined
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch
            {
                _ = combined.Exception;
            }

            return new ProcessOutputPair(
                stdoutTask.IsCompletedSuccessfully
                    ? stdoutTask.Result
                    : ProcessOutputCapture.Empty,
                stderrTask.IsCompletedSuccessfully
                    ? stderrTask.Result
                    : ProcessOutputCapture.Empty,
                CleanupFailed: true);
        }
    }

    private static async Task<ProcessOutputCapture> ReadBoundedProcessOutputAsync(
        StreamReader reader,
        int characterLimit,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        long totalCharacters = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return new ProcessOutputCapture(
                    output.ToString(),
                    totalCharacters,
                    totalCharacters > output.Length);
            }

            totalCharacters = checked(totalCharacters + count);
            var remainingCapacity = characterLimit - output.Length;
            if (remainingCapacity > 0)
            {
                output.Append(buffer, 0, Math.Min(count, remainingCapacity));
            }
        }
    }

    private static async Task<bool> TerminateExternalProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return false;
        }

        process.Kill(entireProcessTree: true);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The owned external 7-Zip process did not terminate after five seconds.");
        }

        return true;
    }

    private static string ComputeTextSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        try
        {
            _ = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsGuidOwnedOutputRoot(string path) =>
        Guid.TryParseExact(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), "N", out _);

    private static int GetWorkerProcessId(ArchiveClient client)
    {
        var property = typeof(ArchiveClient).GetProperty(
            "WorkerProcessId",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(ArchiveClient).FullName, "WorkerProcessId");
        return (int)(property.GetValue(client)
            ?? throw new InvalidOperationException("The worker process ID was unavailable."));
    }

    private static void VerifyWorkerOwnership(ArchiveClient client, string workerPath)
    {
        var workerId = GetWorkerProcessId(client);
        using var process = Process.GetProcessById(workerId);
        var actualPath = process.MainModule?.FileName;
        if (!string.Equals(
                Path.GetFullPath(actualPath ?? string.Empty),
                Path.GetFullPath(workerPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The client worker PID was not the expected worker executable.");
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FixtureChunkBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static string? CaptureFileIdentity(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.None);
        return FileSystemIdentityService.CaptureOpenFile(stream, path).Identity;
    }

    private static async Task TryWriteFailureEvidenceAsync(
        string outputRoot,
        string sourceFingerprint,
        ExternalSevenZipTool? externalSevenZip,
        IReadOnlyList<ExternalArchiveValidation> externalValidationAttempts,
        Exception exception)
    {
        try
        {
            var failurePath = Path.Combine(outputRoot, "creation-operation-probe.failure.json");
            if (FileSystemIdentityService.PathEntryExists(failurePath))
            {
                return;
            }

            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 1024)
            {
                message = message[..1024];
            }

            var evidence = new CreationOperationProbeFailure(
                DateTimeOffset.UtcNow,
                sourceFingerprint,
                outputRoot,
                externalSevenZip,
                externalValidationAttempts,
                exception.GetType().FullName ?? exception.GetType().Name,
                message);
            await using var stream = new FileStream(
                failurePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                FixtureChunkBytes,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(
                    stream,
                    evidence,
                    new JsonSerializerOptions { WriteIndented = true })
                .ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception)
        {
            // The original operation failure remains authoritative.
        }
    }

    private static int CountStagingArtifacts(string outputRoot) =>
        Directory.GetFileSystemEntries(
            outputRoot,
            "*.rainarchives-*.staging",
            SearchOption.TopDirectoryOnly).Length;

    private static void DeleteOwnedFile(string outputRoot, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(
                Directory.GetParent(canonicalPath)?.FullName,
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(canonicalPath).EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean an unowned probe file.");
        }

        if ((File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to clean a reparse-point probe file.");
        }

        File.Delete(canonicalPath);
    }

    private static void DeleteOwnedExternalTool(string outputRoot, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(
                Directory.GetParent(canonicalPath)?.FullName,
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(canonicalPath),
                "external-seven-zip-validator.exe",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean an unowned external validator.");
        }

        if ((File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to clean a reparse-point external validator.");
        }

        for (var attempt = 1; attempt <= CleanupDeleteMaximumAttempts; attempt++)
        {
            try
            {
                File.Delete(canonicalPath);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException)
                && attempt < CleanupDeleteMaximumAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }

        throw new InvalidOperationException("The external-validator cleanup retry loop ended unexpectedly.");
    }

    private static DirectoryCleanup DeleteOwnedDirectory(string outputRoot, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(
                Directory.GetParent(canonicalPath)?.FullName,
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(canonicalPath), "source-fixture", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean an unowned probe directory.");
        }

        var stopwatch = Stopwatch.StartNew();
        for (var attempt = 1; attempt <= CleanupDeleteMaximumAttempts; attempt++)
        {
            EnsureOwnedFixtureContainsNoReparsePoints(canonicalPath);
            try
            {
                Directory.Delete(canonicalPath, recursive: true);
                stopwatch.Stop();
                return new DirectoryCleanup(attempt, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException)
                && attempt < CleanupDeleteMaximumAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }

        throw new InvalidOperationException("The fixture cleanup retry loop ended unexpectedly.");
    }

    private static void EnsureOwnedFixtureContainsNoReparsePoints(string canonicalPath)
    {
        if ((File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to clean a reparse-point fixture root.");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     canonicalPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing to clean a fixture containing a reparse point.");
            }
        }
    }

    private static CleanupVerification VerifyCleanup(string outputRoot, string sourceRoot)
    {
        var sourceDeleted = !Directory.Exists(sourceRoot) && !File.Exists(sourceRoot);
        var staging = CountStagingArtifacts(outputRoot);
        var otherOwnedArtifacts = Directory
            .EnumerateFileSystemEntries(outputRoot, "*", SearchOption.TopDirectoryOnly)
            .Count(path => !string.Equals(Path.GetFileName(path), "creation-operation-probe.json", StringComparison.Ordinal));
        return new CleanupVerification(sourceDeleted, staging, otherOwnedArtifacts);
    }

    private sealed class ProcessResourceSampler(int processId) : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Process process = Process.GetProcessById(processId);
        private Task? loop;
        private TimeSpan initialCpu;
        private long peakWorkingSet;

        public Task StartAsync()
        {
            process.Refresh();
            initialCpu = process.TotalProcessorTime;
            peakWorkingSet = process.WorkingSet64;
            loop = SampleAsync(stop.Token);
            return Task.CompletedTask;
        }

        public async Task<ProcessResources> StopAsync()
        {
            stop.Cancel();
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }

            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            return new ProcessResources(
                (process.TotalProcessorTime - initialCpu).TotalMilliseconds,
                peakWorkingSet);
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }

            stop.Dispose();
            process.Dispose();
        }

        private async Task SampleAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    process.Refresh();
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private sealed record Fixture(
        string SourceRoot,
        FixtureRoute ExplicitFiles,
        FixtureRoute DirectoryTree,
        IReadOnlyList<FixtureManifestEntry> Manifest,
        string ManifestSha256)
    {
        public IReadOnlyList<FixtureRoute> Routes => [ExplicitFiles, DirectoryTree];
    }

    private sealed record FixtureRoute(
        string Name,
        bool IsDirectoryTree,
        IReadOnlyList<ArchiveCreationFile> Files,
        IReadOnlyList<ArchiveCreationDirectory> Directories,
        IReadOnlyList<FixtureManifestEntry> Entries)
    {
        public long TotalFileBytes =>
            Entries
                .Where(entry => entry.Kind == "file")
                .Sum(entry => entry.Bytes);

        public IEnumerable<FixtureManifestEntry> ExpectedArchiveEntries() =>
            IsDirectoryTree
                ? Entries
                : Entries.Prepend(new FixtureManifestEntry(
                    "explicit-directory",
                    ".",
                    "explicit/",
                    "directory",
                    0,
                    null));
    }

    private sealed record FixtureManifestEntry(
        string Route,
        string SourceRelativePath,
        string ArchivePath,
        string Kind,
        long Bytes,
        string? Sha256);

    private readonly record struct ProbeOptions(
        string OutputPath,
        string SourceFingerprint,
        int MeasuredRuns,
        int PayloadBytes,
        string ExternalSevenZipPath);

    private readonly record struct CleanupVerification(
        bool SourceRootDeleted,
        int StagingArtifacts,
        int OtherOwnedArtifacts);

    private readonly record struct DirectoryCleanup(int Attempts, double ElapsedMilliseconds);

    private sealed record ProcessResources(double CpuMilliseconds, long PeakWorkingSetBytes);

    private sealed record ArchiveValidation(
        long OutputBytes,
        string OutputSha256,
        int ListedEntryCount,
        string IntegrityPhase,
        double IntegrityMilliseconds,
        int VerifiedEntryCount,
        int VerifiedFileCount,
        long VerifiedFileBytes,
        string ContentAggregateSha256,
        ExternalArchiveValidation ExternalValidation);

    private sealed record EntryContentVerification(
        int VerifiedEntryCount,
        int VerifiedFileCount,
        long VerifiedFileBytes,
        string ContentAggregateSha256);

    private sealed record ExternalSevenZipSource(
        string Path,
        string Sha256,
        string? ProductName,
        string? ProductVersion,
        string? FileVersion,
        string? CompanyName,
        string? OriginalFilename);

    private sealed record ExternalSevenZipTool(
        string SourcePath,
        string SourceSha256,
        string? SourceFileIdentity,
        string Path,
        string InitialOwnedSha256,
        string? OwnedFileIdentity,
        string? ProductName,
        string? ProductVersion,
        string? FileVersion,
        string? CompanyName,
        string? OriginalFilename,
        IReadOnlyList<string> ArgumentTemplate,
        int TimeoutSeconds,
        int OutputCharacterLimit,
        string? FinalOwnedSha256,
        bool StableAfterRun,
        bool OwnedCopyDeletedAfterRun);

    private sealed record ExternalArchiveValidation(
        string ArchiveFileName,
        long ArchiveLengthBeforeValidation,
        string ArchiveSha256BeforeValidation,
        long? ArchiveLengthAfterValidation,
        string? ArchiveSha256AfterValidation,
        bool ArchiveStableAfterValidation,
        IReadOnlyList<string> Arguments,
        bool Started,
        bool Passed,
        int? ExitCode,
        double ElapsedMilliseconds,
        bool ForcedTermination,
        bool TimedOut,
        bool OutputCleanupFailed,
        bool OutputTruncated,
        string? FailureKind,
        string? ValidatorSha256BeforeLaunch,
        string? ValidatorSha256AfterExit,
        string? ValidatorFileIdentityBeforeLaunch,
        string? ValidatorFileIdentityAfterExit,
        long StandardOutputTotalCharacters,
        int StandardOutputCapturedCharacters,
        string StandardOutputSha256,
        bool StandardOutputTruncated,
        long StandardErrorTotalCharacters,
        int StandardErrorCapturedCharacters,
        string StandardErrorSha256,
        bool StandardErrorTruncated);

    private sealed record ProcessOutputCapture(
        string CapturedText,
        long TotalCharacters,
        bool Truncated)
    {
        public static ProcessOutputCapture Empty { get; } = new(string.Empty, 0, false);
    }

    private sealed record ProcessOutputPair(
        ProcessOutputCapture StandardOutput,
        ProcessOutputCapture StandardError,
        bool CleanupFailed);

    private sealed record CreationSample(
        string Route,
        string SourceRoute,
        string CompressionPreset,
        bool Measured,
        int Run,
        long OutputBytes,
        string OutputSha256,
        double ElapsedMilliseconds,
        double WorkerCpuMilliseconds,
        long WorkerPeakWorkingSetBytes,
        long? TerminalBytesProcessed,
        int? TerminalEntriesProcessed,
        string TerminalPhase,
        int ListedEntryCount,
        string IntegrityPhase,
        double IntegrityMilliseconds,
        int VerifiedEntryCount,
        int VerifiedFileCount,
        long VerifiedFileBytes,
        string ContentAggregateSha256,
        ExternalArchiveValidation ExternalValidation);

    private sealed record CancellationSample(
        string Route,
        long BytesObservedBeforeCancellation,
        long TotalSourceBytes,
        bool PartialProgressObserved,
        double CancellationToTerminalMilliseconds,
        double WorkerCpuMilliseconds,
        long WorkerPeakWorkingSetBytes,
        bool CancellationRequested,
        string TerminalPhase,
        bool FinalDestinationExists,
        int RemainingStagingArtifacts);

    private sealed record CreationOperationProbeResult(
        DateTimeOffset RecordedUtc,
        string SourceFingerprint,
        string OutputRoot,
        string OutputParent,
        string OperatingSystem,
        string RuntimeVersion,
        bool Is64BitProcess,
        int LogicalProcessorCount,
        string WorkerPath,
        string WorkerSha256,
        string BenchmarkAssemblyPath,
        string BenchmarkAssemblySha256,
        ExternalSevenZipTool ExternalSevenZip,
        IReadOnlyList<FixtureManifestEntry> FixtureManifest,
        string FixtureManifestSha256,
        int MeasuredRuns,
        IReadOnlyList<CreationSample> CreationSamples,
        IReadOnlyList<CancellationSample> CancellationSamples,
        IReadOnlyList<ExternalArchiveValidation> ExternalValidationAttempts,
        int SourceRootCleanupAttempts,
        double SourceRootCleanupMilliseconds,
        bool SourceRootDeletedAfterCleanup,
        int RemainingStagingArtifacts,
        int RemainingOwnedArtifacts);

    private sealed record CreationOperationProbeFailure(
        DateTimeOffset RecordedUtc,
        string SourceFingerprint,
        string OutputRoot,
        ExternalSevenZipTool? ExternalSevenZip,
        IReadOnlyList<ExternalArchiveValidation> ExternalValidationAttempts,
        string ExceptionType,
        string ExceptionMessage);
}
