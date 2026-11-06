using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.Benchmarks;

internal static class FoundationOperationProbe
{
    private const int MeasuredRuns = 5;
    private const int FixtureEntries = 2_048;
    private const int PayloadBytes = 64 * 1_024;
    private const long FixtureOriginalBytes = (long)FixtureEntries * PayloadBytes;
    private static readonly DateTimeOffset FixtureTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync(string[] args)
    {
        string? requestedOutput = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--output" && index + 1 < args.Length && requestedOutput is null)
            {
                requestedOutput = args[++index];
                continue;
            }

            Console.Error.WriteLine(
                "Usage: RainArchives.Benchmarks --foundation-operation-probe [--output <new-directory>]");
            return 2;
        }

        var outputRoot = Path.GetFullPath(requestedOutput ?? Path.Combine(
            Path.GetTempPath(),
            "RainArchives.FoundationOperationProbe",
            Guid.NewGuid().ToString("N")));
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            Console.Error.WriteLine("The operation-probe output path must not already exist.");
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

            var archivePath = Path.Combine(outputRoot, "operation-fixture.zip");
            var payload = CreatePayload();
            CreateFixture(archivePath, payload);
            var fixtureInfo = new FileInfo(archivePath);
            var fixtureSha256 = await ComputeFileSha256Async(archivePath).ConfigureAwait(false);
            var payloadSha256 = Convert.ToHexString(SHA256.HashData(payload));

            await RunExtractionSampleAsync(
                workerPath,
                archivePath,
                outputRoot,
                payloadSha256,
                fullExtraction: false,
                run: -1).ConfigureAwait(false);
            await RunExtractionSampleAsync(
                workerPath,
                archivePath,
                outputRoot,
                payloadSha256,
                fullExtraction: true,
                run: -1).ConfigureAwait(false);
            await RunCancellationSampleAsync(
                workerPath,
                archivePath,
                outputRoot,
                run: -1).ConfigureAwait(false);

            var singleSamples = new List<ExtractionSample>(MeasuredRuns);
            var fullSamples = new List<ExtractionSample>(MeasuredRuns);
            var cancellationSamples = new List<CancellationSample>(MeasuredRuns);
            for (var run = 0; run < MeasuredRuns; run++)
            {
                singleSamples.Add(await RunExtractionSampleAsync(
                    workerPath,
                    archivePath,
                    outputRoot,
                    payloadSha256,
                    fullExtraction: false,
                    run).ConfigureAwait(false));
                fullSamples.Add(await RunExtractionSampleAsync(
                    workerPath,
                    archivePath,
                    outputRoot,
                    payloadSha256,
                    fullExtraction: true,
                    run).ConfigureAwait(false));
                cancellationSamples.Add(await RunCancellationSampleAsync(
                    workerPath,
                    archivePath,
                    outputRoot,
                    run).ConfigureAwait(false));
            }

            var result = new FoundationProbeResult(
                DateTimeOffset.UtcNow,
                Environment.OSVersion.VersionString,
                Environment.Version.ToString(),
                Environment.Is64BitProcess,
                Environment.ProcessorCount,
                workerPath,
                await ComputeFileSha256Async(workerPath).ConfigureAwait(false),
                typeof(FoundationOperationProbe).Assembly.Location,
                await ComputeFileSha256Async(typeof(FoundationOperationProbe).Assembly.Location)
                    .ConfigureAwait(false),
                archivePath,
                fixtureInfo.Length,
                fixtureSha256,
                FixtureEntries,
                PayloadBytes,
                FixtureOriginalBytes,
                payloadSha256,
                FixtureTimestamp,
                MeasuredRuns,
                singleSamples,
                SummarizeExtraction(singleSamples),
                fullSamples,
                SummarizeExtraction(fullSamples),
                cancellationSamples,
                SummarizeCancellation(cancellationSamples));

            var jsonPath = Path.Combine(outputRoot, "foundation-operation-probe.json");
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);

            Console.WriteLine($"Rain Archives operation probe: {jsonPath}");
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Operation probe failed. Evidence was preserved at: {outputRoot}");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<ExtractionSample> RunExtractionSampleAsync(
        string workerPath,
        string archivePath,
        string outputRoot,
        string payloadSha256,
        bool fullExtraction,
        int run)
    {
        var sampleName = fullExtraction ? "full" : "single";
        var runName = run < 0 ? "warmup" : run.ToString("D2");
        var destination = Path.Combine(outputRoot, $"{sampleName}-{runName}-output");
        await using var client = await ArchiveClient.StartAsync(workerPath).ConfigureAwait(false);
        var opened = await client.OpenArchiveAsync(archivePath).ConfigureAwait(false);
        try
        {
            IReadOnlyList<EntryId> selection = [];
            long expectedBytes = FixtureOriginalBytes;
            var expectedFiles = FixtureEntries;
            if (!fullExtraction)
            {
                var page = await client
                    .ListEntriesAsync(opened.Archive.SessionId, FixtureEntries / 2, 1)
                    .ConfigureAwait(false);
                var selected = page.Entries.Single();
                selection = [selected.EntryId];
                expectedBytes = PayloadBytes;
                expectedFiles = 1;
            }

            var workerId = GetWorkerProcessId(client);
            await using var sampler = new ProcessResourceSampler(workerId);
            await sampler.StartAsync().ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            var started = await client
                .StartExtractionAsync(opened.Archive.SessionId, selection, destination)
                .ConfigureAwait(false);
            var terminal = await client.WaitForTerminalAsync(started.OperationId).ConfigureAwait(false);
            stopwatch.Stop();
            var resources = await sampler.StopAsync().ConfigureAwait(false);

            if (terminal.Phase is not (OperationPhase.Completed or OperationPhase.CompletedWithWarnings))
            {
                throw new InvalidOperationException(
                    $"{sampleName} extraction ended in {terminal.Phase}: {terminal.Error?.Code}");
            }

            var output = await ValidateOutputAsync(
                destination,
                fullExtraction,
                payloadSha256).ConfigureAwait(false);
            if (output.FileCount != expectedFiles || output.TotalBytes != expectedBytes)
            {
                throw new InvalidDataException(
                    $"{sampleName} extraction output count or size was incorrect.");
            }

            var sample = new ExtractionSample(
                run,
                expectedBytes,
                output.FileCount,
                stopwatch.Elapsed.TotalMilliseconds,
                ToMebibytesPerSecond(expectedBytes, stopwatch.Elapsed),
                resources.CpuMilliseconds,
                resources.PeakWorkingSetBytes,
                output.AggregateSha256,
                terminal.Phase.ToString());
            DeleteOwnedOutput(outputRoot, destination);
            return sample;
        }
        finally
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.CloseArchiveAsync(opened.Archive.SessionId, closeTimeout.Token)
                .ConfigureAwait(false);
        }
    }

    private static async Task<CancellationSample> RunCancellationSampleAsync(
        string workerPath,
        string archivePath,
        string outputRoot,
        int run)
    {
        var runName = run < 0 ? "warmup" : run.ToString("D2");
        var destination = Path.Combine(outputRoot, $"cancel-{runName}-output");
        await using var client = await ArchiveClient.StartAsync(workerPath).ConfigureAwait(false);
        var opened = await client.OpenArchiveAsync(archivePath).ConfigureAwait(false);
        EventHandler<OperationEvent>? handler = null;
        var firstProgress = new TaskCompletionSource<OperationEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        OperationId targetOperation = default;
        handler = (_, operationEvent) =>
        {
            if (targetOperation.Value == Guid.Empty
                || operationEvent.OperationId != targetOperation
                || operationEvent.Kind != OperationEventKind.Progress
                || operationEvent.Progress?.BytesProcessed <= 0)
            {
                return;
            }

            firstProgress.TrySetResult(operationEvent);
        };
        client.OperationEventReceived += handler;

        try
        {
            var workerId = GetWorkerProcessId(client);
            await using var sampler = new ProcessResourceSampler(workerId);
            await sampler.StartAsync().ConfigureAwait(false);
            var started = await client
                .StartExtractionAsync(opened.Archive.SessionId, [], destination)
                .ConfigureAwait(false);
            targetOperation = started.OperationId;
            var progress = await firstProgress.Task
                .WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            var cancellation = await client.CancelOperationAsync(started.OperationId).ConfigureAwait(false);
            var terminal = await client.WaitForTerminalAsync(started.OperationId).ConfigureAwait(false);
            stopwatch.Stop();
            var resources = await sampler.StopAsync().ConfigureAwait(false);

            if (!cancellation.CancellationRequested || terminal.Phase != OperationPhase.Cancelled)
            {
                throw new InvalidOperationException(
                    $"Cancellation was not authoritative: accepted={cancellation.CancellationRequested}, " +
                    $"terminal={terminal.Phase}.");
            }

            if (Directory.Exists(destination))
            {
                throw new InvalidOperationException("A cancelled extraction published its final destination.");
            }

            var stagingCount = Directory
                .GetDirectories(outputRoot, $".{Path.GetFileName(destination)}.rainarchives-*.staging")
                .Length;
            if (stagingCount != 0)
            {
                throw new InvalidOperationException("A cancelled extraction left a staging directory.");
            }

            return new CancellationSample(
                run,
                progress.Progress!.BytesProcessed,
                stopwatch.Elapsed.TotalMilliseconds,
                resources.CpuMilliseconds,
                resources.PeakWorkingSetBytes,
                cancellation.CancellationRequested,
                terminal.Phase.ToString(),
                stagingCount,
                Directory.Exists(destination));
        }
        finally
        {
            client.OperationEventReceived -= handler;
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.CloseArchiveAsync(opened.Archive.SessionId, closeTimeout.Token)
                .ConfigureAwait(false);
        }
    }

    private static byte[] CreatePayload()
    {
        var payload = new byte[PayloadBytes];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31 + 17) % 251);
        }

        return payload;
    }

    private static void CreateFixture(string archivePath, byte[] payload)
    {
        using var file = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1 * 1_024 * 1_024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        for (var index = 0; index < FixtureEntries; index++)
        {
            var entry = archive.CreateEntry(
                $"payloads/{index:D4}.bin",
                CompressionLevel.NoCompression);
            entry.LastWriteTime = FixtureTimestamp;
            using var destination = entry.Open();
            destination.Write(payload);
        }
    }

    private static async Task<OutputValidation> ValidateOutputAsync(
        string destination,
        bool fullExtraction,
        string expectedPayloadSha256)
    {
        if (!Directory.Exists(destination))
        {
            throw new DirectoryNotFoundException("Extraction did not publish its destination.");
        }

        var files = Directory
            .GetFiles(destination, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(destination, path), StringComparer.Ordinal)
            .ToArray();
        var expectedCount = fullExtraction ? FixtureEntries : 1;
        if (files.Length != expectedCount)
        {
            throw new InvalidDataException(
                $"Expected {expectedCount} output files, found {files.Length}.");
        }

        long totalBytes = 0;
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files)
        {
            var info = new FileInfo(path);
            if (info.Length != PayloadBytes)
            {
                throw new InvalidDataException($"Output file had an unexpected size: {info.Length}.");
            }

            var fileSha256 = await ComputeFileSha256Async(path).ConfigureAwait(false);
            if (!string.Equals(fileSha256, expectedPayloadSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Output content hash did not match the fixture payload.");
            }

            totalBytes = checked(totalBytes + info.Length);
            var relative = Path.GetRelativePath(destination, path).Replace('\\', '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            aggregate.AppendData(Convert.FromHexString(fileSha256));
        }

        return new OutputValidation(
            files.Length,
            totalBytes,
            Convert.ToHexString(aggregate.GetHashAndReset()));
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1 * 1_024 * 1_024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static int GetWorkerProcessId(ArchiveClient client)
    {
        var property = typeof(ArchiveClient).GetProperty(
            "WorkerProcessId",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(
                typeof(ArchiveClient).FullName,
                "WorkerProcessId");
        return (int)(property.GetValue(client)
            ?? throw new InvalidOperationException("The worker process ID was unavailable."));
    }

    private static void DeleteOwnedOutput(string outputRoot, string destination)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var canonicalDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var parent = Directory.GetParent(canonicalDestination)?.FullName;
        if (!string.Equals(parent, canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(canonicalDestination).EndsWith("-output", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean an unowned benchmark output path.");
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     canonicalDestination,
                     "*",
                     SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing to clean benchmark output containing a reparse point.");
            }
        }

        Directory.Delete(canonicalDestination, recursive: true);
    }

    private static double ToMebibytesPerSecond(long bytes, TimeSpan elapsed) =>
        bytes / (1_024d * 1_024d) / elapsed.TotalSeconds;

    private static ExtractionSummary SummarizeExtraction(IReadOnlyList<ExtractionSample> samples) =>
        new(
            Summarize(samples.Select(sample => sample.WallMilliseconds)),
            Summarize(samples.Select(sample => sample.ThroughputMebibytesPerSecond)),
            Summarize(samples.Select(sample => sample.WorkerCpuMilliseconds)),
            Summarize(samples.Select(sample => (double)sample.WorkerPeakWorkingSetBytes)));

    private static CancellationSummary SummarizeCancellation(
        IReadOnlyList<CancellationSample> samples) =>
        new(
            Summarize(samples.Select(sample => sample.CancellationToTerminalMilliseconds)),
            Summarize(samples.Select(sample => sample.WorkerCpuMilliseconds)),
            Summarize(samples.Select(sample => (double)sample.WorkerPeakWorkingSetBytes)));

    private static MetricSummary Summarize(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            throw new ArgumentException("At least one measurement is required.", nameof(values));
        }

        var median = sorted.Length % 2 == 0
            ? (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2
            : sorted[sorted.Length / 2];
        var p95Index = Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
        return new MetricSummary(sorted.Average(), median, sorted[p95Index], sorted[0], sorted[^1]);
    }

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
                    await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private sealed record ProcessResources(double CpuMilliseconds, long PeakWorkingSetBytes);

    private sealed record OutputValidation(int FileCount, long TotalBytes, string AggregateSha256);

    private sealed record ExtractionSample(
        int Run,
        long DecodedBytes,
        int OutputFiles,
        double WallMilliseconds,
        double ThroughputMebibytesPerSecond,
        double WorkerCpuMilliseconds,
        long WorkerPeakWorkingSetBytes,
        string OutputAggregateSha256,
        string TerminalPhase);

    private sealed record CancellationSample(
        int Run,
        long BytesObservedBeforeCancellation,
        double CancellationToTerminalMilliseconds,
        double WorkerCpuMilliseconds,
        long WorkerPeakWorkingSetBytes,
        bool CancellationRequested,
        string TerminalPhase,
        int RemainingStagingDirectories,
        bool FinalDestinationExists);

    private sealed record MetricSummary(
        double Mean,
        double Median,
        double P95NearestRank,
        double Minimum,
        double Maximum);

    private sealed record ExtractionSummary(
        MetricSummary WallMilliseconds,
        MetricSummary ThroughputMebibytesPerSecond,
        MetricSummary WorkerCpuMilliseconds,
        MetricSummary WorkerPeakWorkingSetBytes);

    private sealed record CancellationSummary(
        MetricSummary CancellationToTerminalMilliseconds,
        MetricSummary WorkerCpuMilliseconds,
        MetricSummary WorkerPeakWorkingSetBytes);

    private sealed record FoundationProbeResult(
        DateTimeOffset RecordedUtc,
        string OperatingSystem,
        string RuntimeVersion,
        bool Is64BitProcess,
        int LogicalProcessorCount,
        string WorkerPath,
        string WorkerSha256,
        string BenchmarkAssemblyPath,
        string BenchmarkAssemblySha256,
        string FixturePath,
        long FixtureArchiveBytes,
        string FixtureArchiveSha256,
        int FixtureEntries,
        int PayloadBytesPerEntry,
        long FixtureOriginalBytes,
        string PayloadSha256,
        DateTimeOffset FixtureTimestamp,
        int MeasuredRuns,
        IReadOnlyList<ExtractionSample> SingleEntrySamples,
        ExtractionSummary SingleEntrySummary,
        IReadOnlyList<ExtractionSample> FullExtractionSamples,
        ExtractionSummary FullExtractionSummary,
        IReadOnlyList<CancellationSample> CancellationSamples,
        CancellationSummary CancellationSummary);
}
