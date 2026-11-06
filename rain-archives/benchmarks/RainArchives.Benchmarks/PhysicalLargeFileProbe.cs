using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.Benchmarks;

internal static class PhysicalLargeFileProbe
{
    private const long DefaultPayloadBytes = 3L * 1024 * 1024 * 1024;
    private const long MinimumPayloadBytes = 16L * 1024 * 1024;
    private const long MaximumPayloadBytes = 8L * 1024 * 1024 * 1024;
    private const int PayloadBlockBytes = 4 * 1024 * 1024;
    private const long CancellationProgressBytes = 64L * 1024 * 1024;
    private const uint InvalidFileSize = uint.MaxValue;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(20);
    private static readonly DateTimeOffset FixtureTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: RainArchives.Benchmarks --physical-large-file-probe "
                + "--output <new-directory> --source-fingerprint <sha256> [--bytes <16777216..8589934592>]");
            return 2;
        }

        var outputRoot = Path.GetFullPath(options.OutputPath);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            Console.Error.WriteLine("The physical-large-file probe output path must not already exist.");
            return 2;
        }

        var outputDriveRoot = Path.GetPathRoot(outputRoot);
        if (string.IsNullOrEmpty(outputDriveRoot))
        {
            Console.Error.WriteLine("The output path has no usable drive root.");
            return 2;
        }

        var drive = new DriveInfo(outputDriveRoot);
        var minimumFreeBytes = checked((options.PayloadBytes * 2) + (2L * 1024 * 1024 * 1024));
        if (drive.AvailableFreeSpace < minimumFreeBytes)
        {
            Console.Error.WriteLine(
                $"The probe requires at least {minimumFreeBytes:N0} free bytes before starting; "
                + $"the output volume has {drive.AvailableFreeSpace:N0}.");
            return 2;
        }

        Directory.CreateDirectory(outputRoot);
        try
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException(
                    "The benchmark output does not contain RainArchives.Worker.exe.",
                    workerPath);
            }

            var archivePath = Path.Combine(outputRoot, "physical-large.zip");
            var createStopwatch = Stopwatch.StartNew();
            var payloadSha256 = CreateArchive(archivePath, options.PayloadBytes);
            createStopwatch.Stop();

            var archiveInfo = new FileInfo(archivePath);
            var archiveAttributes = archiveInfo.Attributes;
            var allocatedBytes = GetAllocatedBytes(archivePath);
            var minimumExpectedAllocation = archiveInfo.Length - (archiveInfo.Length / 100);
            if ((archiveAttributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0
                || allocatedBytes < minimumExpectedAllocation)
            {
                throw new InvalidDataException(
                    "The generated archive was sparse, filesystem-compressed, or insufficiently allocated.");
            }

            var archiveSha256 = await ComputeFileSha256Async(archivePath).ConfigureAwait(false);
            var freeBytesBeforeOperations = drive.AvailableFreeSpace;
            IntegrityResult integrity;
            ExtractionResult extraction;
            CancellationResult cancellation;

            await using (var client = await ArchiveClient.StartAsync(workerPath).ConfigureAwait(false))
            {
                var opened = await client.OpenArchiveAsync(archivePath).ConfigureAwait(false);
                try
                {
                    var entries = await ReadAllEntriesAsync(client, opened.Archive.SessionId)
                        .ConfigureAwait(false);
                    var payloadEntries = entries
                        .Where(entry => entry.Kind == ArchiveEntryKind.File)
                        .ToArray();
                    if (payloadEntries.Length != 1
                        || payloadEntries[0].RawPath != "physical-large/payload.bin"
                        || payloadEntries[0].OriginalSize != options.PayloadBytes)
                    {
                        throw new InvalidDataException(
                            "The worker did not report the expected physical-large fixture metadata.");
                    }

                    integrity = await RunIntegrityAsync(client, opened.Archive.SessionId)
                        .ConfigureAwait(false);
                    extraction = await RunExtractionAsync(
                        client,
                        opened.Archive.SessionId,
                        outputRoot,
                        options.PayloadBytes,
                        payloadSha256).ConfigureAwait(false);
                    cancellation = await RunCancellationAsync(
                        client,
                        opened.Archive.SessionId,
                        outputRoot,
                        options.PayloadBytes,
                        payloadSha256).ConfigureAwait(false);
                }
                finally
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await client.CloseArchiveAsync(opened.Archive.SessionId, closeTimeout.Token)
                        .ConfigureAwait(false);
                }
            }

            DeleteOwnedFile(outputRoot, archivePath);
            var remainingStagingDirectories = CountStagingDirectories(outputRoot);
            if (remainingStagingDirectories != 0)
            {
                throw new InvalidDataException(
                    "The successful physical-large-file probe left a staging directory.");
            }

            drive = new DriveInfo(outputDriveRoot);
            var result = new PhysicalLargeFileProbeResult(
                DateTimeOffset.UtcNow,
                options.SourceFingerprint,
                Environment.OSVersion.VersionString,
                Environment.Version.ToString(),
                Environment.Is64BitProcess,
                Environment.ProcessorCount,
                outputDriveRoot,
                drive.DriveFormat,
                freeBytesBeforeOperations,
                drive.AvailableFreeSpace,
                workerPath,
                await ComputeFileSha256Async(workerPath).ConfigureAwait(false),
                typeof(PhysicalLargeFileProbe).Assembly.Location,
                await ComputeFileSha256Async(typeof(PhysicalLargeFileProbe).Assembly.Location)
                    .ConfigureAwait(false),
                options.PayloadBytes,
                payloadSha256,
                archiveInfo.Length,
                archiveSha256,
                allocatedBytes,
                archiveAttributes.ToString(),
                createStopwatch.Elapsed.TotalMilliseconds,
                integrity,
                extraction,
                cancellation,
                ArchiveDeletedAfterSuccess: !File.Exists(archivePath),
                RemainingStagingDirectories: remainingStagingDirectories);

            var jsonPath = Path.Combine(outputRoot, "physical-large-file-probe.json");
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);

            Console.WriteLine($"Rain Archives physical-large-file probe: {jsonPath}");
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Physical-large-file probe failed. Evidence was preserved at: {outputRoot}");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static bool TryParseArguments(string[] args, out ProbeOptions options)
    {
        string? outputPath = null;
        string? sourceFingerprint = null;
        var payloadBytes = DefaultPayloadBytes;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--output" && index + 1 < args.Length && outputPath is null)
            {
                outputPath = args[++index];
                continue;
            }

            if (args[index] == "--source-fingerprint"
                && index + 1 < args.Length
                && sourceFingerprint is null)
            {
                sourceFingerprint = args[++index].ToUpperInvariant();
                continue;
            }

            if (args[index] == "--bytes"
                && index + 1 < args.Length
                && long.TryParse(args[++index], out var parsedBytes))
            {
                payloadBytes = parsedBytes;
                continue;
            }

            options = default;
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputPath)
            || !IsSha256(sourceFingerprint)
            || payloadBytes < MinimumPayloadBytes
            || payloadBytes > MaximumPayloadBytes)
        {
            options = default;
            return false;
        }

        options = new ProbeOptions(outputPath, sourceFingerprint!, payloadBytes);
        return true;
    }

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

    private static string CreateArchive(string archivePath, long payloadBytes)
    {
        var payload = CreatePayloadBlock();
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var file = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            PayloadBlockBytes,
            FileOptions.SequentialScan);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(
                "physical-large/payload.bin",
                CompressionLevel.NoCompression);
            entry.LastWriteTime = FixtureTimestamp;
            using var destination = entry.Open();
            var remaining = payloadBytes;
            while (remaining > 0)
            {
                var count = checked((int)Math.Min(remaining, payload.Length));
                destination.Write(payload, 0, count);
                payloadHash.AppendData(payload.AsSpan(0, count));
                remaining -= count;
            }
        }

        file.Flush(flushToDisk: true);
        return Convert.ToHexString(payloadHash.GetHashAndReset());
    }

    private static byte[] CreatePayloadBlock()
    {
        var payload = new byte[PayloadBlockBytes];
        uint state = 0x6D2B79F5;
        for (var index = 0; index < payload.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            payload[index] = (byte)state;
        }

        return payload;
    }

    private static async Task<IReadOnlyList<ArchiveEntryDescriptor>> ReadAllEntriesAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId)
    {
        var entries = new List<ArchiveEntryDescriptor>();
        var offset = 0;
        while (true)
        {
            var page = await client.ListEntriesAsync(sessionId, offset, 1024)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            entries.AddRange(page.Entries);
            if (page.NextOffset is not { } nextOffset)
            {
                return entries;
            }

            offset = nextOffset;
        }
    }

    private static async Task<IntegrityResult> RunIntegrityAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId)
    {
        var stopwatch = Stopwatch.StartNew();
        var started = await client.StartIntegrityTestAsync(sessionId, [])
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        var terminal = await client.WaitForTerminalAsync(started.OperationId)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        stopwatch.Stop();
        RequireCompleted(terminal, "integrity test");
        return new IntegrityResult(
            stopwatch.Elapsed.TotalMilliseconds,
            terminal.Progress?.BytesProcessed,
            terminal.Phase.ToString());
    }

    private static async Task<ExtractionResult> RunExtractionAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId,
        string outputRoot,
        long expectedBytes,
        string expectedSha256)
    {
        var destination = Path.Combine(outputRoot, "full-output");
        var workerId = GetWorkerProcessId(client);
        await using var sampler = new ProcessResourceSampler(workerId);
        await sampler.StartAsync().ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var started = await client.StartExtractionAsync(sessionId, [], destination)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        var terminal = await client.WaitForTerminalAsync(started.OperationId)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        stopwatch.Stop();
        var resources = await sampler.StopAsync().ConfigureAwait(false);
        RequireCompleted(terminal, "full extraction");

        var outputPath = Path.Combine(destination, "physical-large", "payload.bin");
        var outputInfo = new FileInfo(outputPath);
        if (!outputInfo.Exists || outputInfo.Length != expectedBytes)
        {
            throw new InvalidDataException("The physical-large extraction size was incorrect.");
        }

        var outputSha256 = await ComputeFileSha256Async(outputPath).ConfigureAwait(false);
        if (!string.Equals(outputSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The physical-large extraction hash was incorrect.");
        }

        var result = new ExtractionResult(
            stopwatch.Elapsed.TotalMilliseconds,
            expectedBytes / (1024d * 1024d) / stopwatch.Elapsed.TotalSeconds,
            resources.CpuMilliseconds,
            resources.PeakWorkingSetBytes,
            outputInfo.Length,
            outputSha256,
            terminal.Phase.ToString());
        DeleteOwnedDirectory(outputRoot, destination);
        return result;
    }

    private static async Task<CancellationResult> RunCancellationAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId,
        string outputRoot,
        long payloadBytes,
        string expectedSha256)
    {
        var destination = Path.Combine(outputRoot, "cancel-output");
        var cancellationThreshold = Math.Min(
            CancellationProgressBytes,
            Math.Max(1, payloadBytes / 4));
        var progressReached = new TaskCompletionSource<OperationEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        OperationId targetOperation = default;
        EventHandler<OperationEvent>? handler = (_, operationEvent) =>
        {
            if (operationEvent.Kind == OperationEventKind.Progress
                && operationEvent.Progress?.BytesProcessed >= cancellationThreshold
                && (targetOperation.Value == Guid.Empty
                    || operationEvent.OperationId == targetOperation))
            {
                progressReached.TrySetResult(operationEvent);
            }
        };
        client.OperationEventReceived += handler;

        try
        {
            var workerId = GetWorkerProcessId(client);
            await using var sampler = new ProcessResourceSampler(workerId);
            await sampler.StartAsync().ConfigureAwait(false);
            var started = await client.StartExtractionAsync(sessionId, [], destination)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            targetOperation = started.OperationId;
            var progress = await progressReached.Task
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            var response = await client.CancelOperationAsync(started.OperationId)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            var terminal = await client.WaitForTerminalAsync(started.OperationId)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            stopwatch.Stop();
            var resources = await sampler.StopAsync().ConfigureAwait(false);
            var completionWonRace = false;
            if (response.CancellationRequested)
            {
                if (terminal.Phase != OperationPhase.Cancelled)
                {
                    throw new InvalidDataException(
                        $"Physical-large cancellation was accepted but ended in {terminal.Phase}.");
                }
            }
            else
            {
                RequireCompleted(terminal, "cancellation-race extraction");
                completionWonRace = true;
                var outputPath = Path.Combine(destination, "physical-large", "payload.bin");
                var outputInfo = new FileInfo(outputPath);
                if (!outputInfo.Exists || outputInfo.Length != payloadBytes)
                {
                    throw new InvalidDataException(
                        "The cancellation-race winner did not publish the expected output.");
                }

                var outputSha256 = await ComputeFileSha256Async(outputPath).ConfigureAwait(false);
                if (!string.Equals(outputSha256, expectedSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The cancellation-race winner published incorrect output bytes.");
                }

                DeleteOwnedDirectory(outputRoot, destination);
            }

            var remainingStagingDirectories = CountStagingDirectories(outputRoot);
            if (Directory.Exists(destination) || remainingStagingDirectories != 0)
            {
                throw new InvalidDataException(
                    "Physical-large cancellation published output or left staging data.");
            }

            return new CancellationResult(
                progress.Progress!.BytesProcessed,
                stopwatch.Elapsed.TotalMilliseconds,
                resources.CpuMilliseconds,
                resources.PeakWorkingSetBytes,
                response.CancellationRequested,
                terminal.Phase.ToString(),
                completionWonRace,
                Directory.Exists(destination),
                remainingStagingDirectories);
        }
        finally
        {
            client.OperationEventReceived -= handler;
        }
    }

    private static void RequireCompleted(OperationEvent terminal, string operation)
    {
        if (terminal.Phase is not (OperationPhase.Completed or OperationPhase.CompletedWithWarnings))
        {
            throw new InvalidDataException(
                $"The physical-large {operation} ended in {terminal.Phase}: {terminal.Error?.Code}");
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            PayloadBlockBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static int GetWorkerProcessId(ArchiveClient client)
    {
        var property = typeof(ArchiveClient).GetProperty(
            "WorkerProcessId",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(ArchiveClient).FullName, "WorkerProcessId");
        return (int)(property.GetValue(client)
            ?? throw new InvalidOperationException("The worker process ID was unavailable."));
    }

    private static int CountStagingDirectories(string outputRoot) =>
        Directory.GetDirectories(
            outputRoot,
            "*.rainarchives-*.staging",
            SearchOption.TopDirectoryOnly).Length;

    private static void DeleteOwnedDirectory(string outputRoot, string destination)
    {
        ValidateDirectChild(outputRoot, destination, "-output");
        if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to clean a reparse-point output directory.");
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     destination,
                     "*",
                     SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Refusing to clean physical-large output containing a reparse point.");
            }
        }

        Directory.Delete(destination, recursive: true);
    }

    private static void DeleteOwnedFile(string outputRoot, string path)
    {
        ValidateDirectChild(outputRoot, path, ".zip");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to clean a reparse-point archive.");
        }

        File.Delete(path);
    }

    private static void ValidateDirectChild(string outputRoot, string path, string requiredSuffix)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(
                Directory.GetParent(canonicalPath)?.FullName,
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(canonicalPath).EndsWith(requiredSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean an unowned probe path.");
        }
    }

    private static long GetAllocatedBytes(string path)
    {
        Marshal.SetLastPInvokeError(0);
        var low = GetCompressedFileSize(path, out var high);
        var error = Marshal.GetLastPInvokeError();
        if (low == InvalidFileSize && error != 0)
        {
            throw new Win32Exception(error);
        }

        var allocated = ((ulong)high << 32) | low;
        if (allocated > long.MaxValue)
        {
            throw new IOException("The allocated file size exceeds the supported range.");
        }

        return checked((long)allocated);
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetCompressedFileSizeW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    private sealed class ProcessResourceSampler(int processId) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Process _process = Process.GetProcessById(processId);
        private Task? _loop;
        private TimeSpan _initialCpu;
        private long _peakWorkingSet;

        public Task StartAsync()
        {
            _process.Refresh();
            _initialCpu = _process.TotalProcessorTime;
            _peakWorkingSet = _process.WorkingSet64;
            _loop = SampleAsync(_stop.Token);
            return Task.CompletedTask;
        }

        public async Task<ProcessResources> StopAsync()
        {
            _stop.Cancel();
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }

            _process.Refresh();
            _peakWorkingSet = Math.Max(_peakWorkingSet, _process.WorkingSet64);
            return new ProcessResources(
                (_process.TotalProcessorTime - _initialCpu).TotalMilliseconds,
                _peakWorkingSet);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }

            _stop.Dispose();
            _process.Dispose();
        }

        private async Task SampleAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    _process.Refresh();
                    _peakWorkingSet = Math.Max(_peakWorkingSet, _process.WorkingSet64);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private readonly record struct ProbeOptions(
        string OutputPath,
        string SourceFingerprint,
        long PayloadBytes);

    private sealed record ProcessResources(double CpuMilliseconds, long PeakWorkingSetBytes);

    private sealed record IntegrityResult(
        double WallMilliseconds,
        long? BytesProcessed,
        string TerminalPhase);

    private sealed record ExtractionResult(
        double WallMilliseconds,
        double ThroughputMebibytesPerSecond,
        double WorkerCpuMilliseconds,
        long WorkerPeakWorkingSetBytes,
        long OutputBytes,
        string OutputSha256,
        string TerminalPhase);

    private sealed record CancellationResult(
        long BytesObservedBeforeCancellation,
        double CancellationToTerminalMilliseconds,
        double WorkerCpuMilliseconds,
        long WorkerPeakWorkingSetBytes,
        bool CancellationRequested,
        string TerminalPhase,
        bool CompletionWonRace,
        bool FinalDestinationExists,
        int RemainingStagingDirectories);

    private sealed record PhysicalLargeFileProbeResult(
        DateTimeOffset RecordedUtc,
        string SourceFingerprint,
        string OperatingSystem,
        string RuntimeVersion,
        bool Is64BitProcess,
        int LogicalProcessorCount,
        string OutputDriveRoot,
        string OutputFileSystem,
        long FreeBytesBeforeOperations,
        long FreeBytesAfterCleanup,
        string WorkerPath,
        string WorkerSha256,
        string BenchmarkAssemblyPath,
        string BenchmarkAssemblySha256,
        long PayloadBytes,
        string PayloadSha256,
        long ArchiveBytes,
        string ArchiveSha256,
        long ArchiveAllocatedBytes,
        string ArchiveAttributes,
        double ArchiveCreationMilliseconds,
        IntegrityResult Integrity,
        ExtractionResult Extraction,
        CancellationResult Cancellation,
        bool ArchiveDeletedAfterSuccess,
        int RemainingStagingDirectories);
}
