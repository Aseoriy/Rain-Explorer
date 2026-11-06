using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Milestone5FPackageMeasurementTests
{
    private const string PackageRootVariable = "RAIN_ARCHIVES_M5F_PACKAGE_ROOT";
    private const string MeasurementOutputVariable = "RAIN_ARCHIVES_M5F_MEASUREMENT_OUTPUT";
    private const string ExpectedPackageDirectoryName =
        "RainArchives.M5FAutomatedPackage.1.22.94811C7A";
    private const string ExpectedSourceSha256 =
        "94811C7A626839524DA43826E6AD238BAF29E85EDECFC7E8C3D581B8C217C641";
    private const string ExpectedManifestSha256 =
        "1066C02687DC900C785FB0921D9C3EFEE2359450ED05F991F64002728F1F7395";
    private const int RootFileCount = 16_384;
    private const int FolderCount = 64;
    private const int FilesPerFolder = 64;
    private const int PreviewRequestCount = 32;
    private const int SelectionIterationCount = 1_024;
    private const int NavigationIterationCount = 320;
    private const int WarmupProcessCount = 1;
    private const int MeasuredColdProcessCount = 3;
    private const long ExpectedFixtureBytes = 4_421_858;
    private const string ExpectedFixtureSha256 =
        "6CAF8851DF604339E577D63DB0D8545829C76DA790866112E6B54190A9258060";
    private static readonly DateTimeOffset FixtureTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [Timeout(360_000)]
    public async Task ExactPackageMeasuresBoundedBrowserAndPreviewLifecycle()
    {
        var packageRoot = ResolvePackageRoot();
        var manifestPath = Path.Combine(packageRoot, "package-manifest.json");
        var appRoot = Path.Combine(packageRoot, "app");
        var workerPath = Path.Combine(appRoot, "worker", "RainArchives.Worker.exe");
        Assert.IsTrue(File.Exists(workerPath));
        Assert.AreEqual(ExpectedManifestSha256, Sha256File(manifestPath));
        using (var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath)))
        {
            Assert.AreEqual(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual("1.22", manifest.RootElement.GetProperty("protocol").GetString());
            Assert.AreEqual(
                ExpectedSourceSha256,
                manifest.RootElement.GetProperty("sourceSha256").GetString());
        }

        using var temporary = new TemporaryDirectory();
        var fixturePath = Path.Combine(temporary.Path, "m5f-large-list.zip");
        CreateFixture(fixturePath);
        var fixtureInfo = new FileInfo(fixturePath);
        var fixtureSha256 = Sha256File(fixturePath);
        Assert.AreEqual(ExpectedFixtureBytes, fixtureInfo.Length);
        Assert.AreEqual(ExpectedFixtureSha256, fixtureSha256);
        var expectedEntries = checked(
            RootFileCount
            + FolderCount
            + checked(FolderCount * FilesPerFolder)
            + 2);

        var coldStarts = new List<ColdStartMeasurement>(
            WarmupProcessCount + MeasuredColdProcessCount);
        var processSamples = new List<ProcessMeasurement>();
        var terminatedWorkerIds = new List<int>();
        MainWindowViewModel? steadyViewModel = null;
        ArchiveClient? steadyClient = null;
        string? steadyWorkerRoot = null;
        string? steadyPreviewCache = null;
        MeasurementEvidence? evidence = null;
        try
        {
            for (var index = 0; index < WarmupProcessCount + MeasuredColdProcessCount; index++)
            {
                var workerRoot = Path.Combine(temporary.Path, $"worker-{index:D2}");
                var previewCache = Path.Combine(temporary.Path, $"preview-{index:D2}");
                Directory.CreateDirectory(workerRoot);
                Directory.CreateDirectory(previewCache);
                ArchiveClient? capturedClient = null;
                var iterationViewModel = new MainWindowViewModel(
                    new PassiveDialogs(),
                    async cancellationToken =>
                    {
                        capturedClient = await ArchiveClient.StartWithTemporaryRootAsync(
                                workerPath,
                                workerRoot,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return capturedClient;
                    },
                    client => new TextEntryPreviewService(client, previewCache));

                var stopwatch = Stopwatch.StartNew();
                await iterationViewModel.OpenArchivePathAsync(fixturePath);
                stopwatch.Stop();
                Assert.AreEqual(ArchiveBrowserState.Ready, iterationViewModel.Browser.State);
                Assert.AreEqual(
                    expectedEntries.ToString("N0"),
                    iterationViewModel.Properties.ArchiveItems
                        .Single(item => item.Name == "Entries").Value);
                Assert.HasCount(
                    ArchiveBrowserViewModel.VisiblePageSize,
                    iterationViewModel.Entries);
                Assert.IsNotNull(capturedClient);
                var workerSample = CaptureProcess(
                    capturedClient.WorkerProcessId,
                    index == 0 ? "warmup-open" : $"cold-open-{index}");
                processSamples.Add(workerSample);
                coldStarts.Add(new ColdStartMeasurement(
                    IsWarmup: index < WarmupProcessCount,
                    Iteration: index < WarmupProcessCount ? index + 1 : index,
                    ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
                    WorkerProcessId: capturedClient.WorkerProcessId,
                    WorkerWorkingSetBytes: workerSample.WorkingSetBytes,
                    WorkerPrivateBytes: workerSample.PrivateBytes,
                    WorkerHandleCount: workerSample.HandleCount));

                if (index + 1 == WarmupProcessCount + MeasuredColdProcessCount)
                {
                    steadyViewModel = iterationViewModel;
                    steadyClient = capturedClient;
                    steadyWorkerRoot = workerRoot;
                    steadyPreviewCache = previewCache;
                    continue;
                }

                var workerId = capturedClient.WorkerProcessId;
                await iterationViewModel.DisposeAsync();
                await WaitForExitAsync(workerId);
                terminatedWorkerIds.Add(workerId);
                Assert.HasCount(0, Directory.GetFiles(workerRoot, "*", SearchOption.AllDirectories));
                Assert.HasCount(0, Directory.GetFiles(previewCache, "*", SearchOption.AllDirectories));
            }

            Assert.IsNotNull(steadyViewModel);
            Assert.IsNotNull(steadyClient);
            Assert.IsNotNull(steadyWorkerRoot);
            Assert.IsNotNull(steadyPreviewCache);
            var viewModel = steadyViewModel;
            var browser = viewModel.Browser;
            Assert.AreEqual(checked(RootFileCount + FolderCount + 2), browser.TotalResults);
            Assert.AreEqual(
                checked((browser.TotalResults + ArchiveBrowserViewModel.VisiblePageSize - 1)
                        / ArchiveBrowserViewModel.VisiblePageSize),
                browser.PageCount);

            var search = Stopwatch.StartNew();
            viewModel.SearchText = "雨-root-12";
            search.Stop();
            var unicodeSearchResults = browser.TotalResults;
            Assert.AreEqual(1_000, unicodeSearchResults);
            Assert.IsLessThanOrEqualTo(
                ArchiveBrowserViewModel.VisiblePageSize,
                viewModel.Entries.Count);

            viewModel.Browser.SelectedEntry = null;
            viewModel.SearchText = string.Empty;
            Assert.AreEqual(0, browser.PageIndex);
            var paging = Stopwatch.StartNew();
            var pagingTransitions = 0;
            var measuredPageCount = browser.PageCount;
            for (var page = 1; page < measuredPageCount; page++)
            {
                Assert.IsTrue(
                    browser.CanGoToNextPage,
                    $"Expected next page from {browser.PageIndex:N0} of {measuredPageCount:N0}.");
                Assert.IsTrue(browser.NextPageCommand.CanExecute(null));
                browser.NextPageCommand.Execute(null);
                await WaitUntilAsync(
                    () => browser.PageIndex == page,
                    () => $"next page {page:N0}; current page {browser.PageIndex:N0}; command enabled {browser.NextPageCommand.CanExecute(null)}");
                pagingTransitions++;
                Assert.IsLessThanOrEqualTo(
                    ArchiveBrowserViewModel.VisiblePageSize,
                    viewModel.Entries.Count);
            }

            for (var page = measuredPageCount - 2; page >= 0; page--)
            {
                browser.PreviousPageCommand.Execute(null);
                await WaitUntilAsync(
                    () => browser.PageIndex == page,
                    () => $"previous page {page:N0}; current page {browser.PageIndex:N0}; command enabled {browser.PreviousPageCommand.CanExecute(null)}");
                pagingTransitions++;
            }

            paging.Stop();
            Assert.AreEqual(checked((measuredPageCount - 1) * 2), pagingTransitions);
            Assert.AreEqual(0, browser.PageIndex);

            var folderId = viewModel.Entries.First(entry => entry.IsDirectory).EntryId;
            var navigation = Stopwatch.StartNew();
            for (var iteration = 0; iteration < NavigationIterationCount; iteration++)
            {
                viewModel.Browser.SelectedEntry = viewModel.Entries.Single(
                    entry => entry.EntryId == folderId);
                Assert.IsTrue(viewModel.CommandContext.CanNavigate);
                viewModel.NavigateSelectionCommand.Execute(null);
                Assert.AreEqual(folderId, browser.CurrentFolderId);
                viewModel.RootCommand.Execute(null);
                Assert.IsNull(browser.CurrentFolderId);
            }

            navigation.Stop();

            var selectable = viewModel.Entries.Where(entry => !entry.IsDirectory).Take(128).ToArray();
            Assert.HasCount(128, selectable);
            var selection = Stopwatch.StartNew();
            for (var iteration = 0; iteration < SelectionIterationCount; iteration++)
            {
                viewModel.Browser.SelectedEntry = selectable[iteration % selectable.Length];
                Assert.AreEqual(1, viewModel.CommandContext.SelectionCount);
                Assert.IsTrue(viewModel.CommandContext.CanPreview);
            }

            selection.Stop();

            var previewEntries = viewModel.Entries
                .Where(entry => entry.Name is "preview-a.txt" or "preview-b.txt")
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            Assert.HasCount(2, previewEntries);
            var preview = Stopwatch.StartNew();
            for (var iteration = 0; iteration < PreviewRequestCount; iteration++)
            {
                viewModel.Browser.SelectedEntry = previewEntries[iteration % previewEntries.Length];
                Assert.IsTrue(viewModel.PreviewSelectionCommand.CanExecute(null));
                viewModel.PreviewSelectionCommand.Execute(null);
            }

            await viewModel.ClosePreviewAsync().WaitAsync(TimeSpan.FromSeconds(30));
            preview.Stop();
            Assert.AreEqual(SafePreviewState.Idle, viewModel.Preview.State);
            Assert.IsFalse(viewModel.IsPreviewVisible);
            Assert.HasCount(
                0,
                Directory.GetFiles(steadyPreviewCache, "*", SearchOption.AllDirectories));

            var steadySamples = new List<ProcessMeasurement>();
            for (var sample = 0; sample < 5; sample++)
            {
                steadySamples.Add(CaptureProcess(
                    steadyClient.WorkerProcessId,
                    $"steady-{sample + 1}"));
                if (sample < 4)
                {
                    await Task.Delay(100);
                }
            }

            processSamples.AddRange(steadySamples);
            foreach (var sample in steadySamples)
            {
                Assert.IsLessThan(2L * 1024 * 1024 * 1024, sample.PrivateBytes);
                Assert.IsLessThan(4_096, sample.HandleCount);
            }

            var measuredColdMilliseconds = coldStarts
                .Where(item => !item.IsWarmup)
                .Select(item => item.ElapsedMilliseconds)
                .OrderBy(value => value)
                .ToArray();
            var steadyWorkingSets = steadySamples.Select(item => item.WorkingSetBytes).ToArray();
            var steadyPrivateBytes = steadySamples.Select(item => item.PrivateBytes).ToArray();
            var steadyHandles = steadySamples.Select(item => item.HandleCount).ToArray();

            using var hostProcess = Process.GetCurrentProcess();
            hostProcess.Refresh();
            evidence = new MeasurementEvidence(
                SchemaVersion: 1,
                Protocol: "1.22",
                PackageDirectoryName: Path.GetFileName(packageRoot),
                PackageManifestSha256: ExpectedManifestSha256,
                SourceSha256: ExpectedSourceSha256,
                RecordedAtUtc: DateTimeOffset.UtcNow,
                Runtime: new RuntimeContext(
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    Environment.ProcessorCount),
                Fixture: new FixtureIdentity(
                    "ZIP",
                    expectedEntries,
                    RootFileCount,
                    FolderCount,
                    FilesPerFolder,
                    2,
                    TextEntryPreviewService.MaximumTextPreviewBytes,
                    fixtureInfo.Length,
                    fixtureSha256),
                Method: new MeasurementMethod(
                    WarmupProcessCount,
                    MeasuredColdProcessCount,
                    "Every startup launches one fresh package Worker and opens the complete archive into the production Contracts/Client browser model. The first process is warmup; the next three are measured process-cold with filesystem cache left uncontrolled.",
                    NavigationIterationCount,
                    SelectionIterationCount,
                    PreviewRequestCount,
                    5,
                    100,
                    ArchiveBrowserViewModel.VisiblePageSize,
                    500_000,
                    256),
                Results: new MeasurementResults(
                    coldStarts,
                    unicodeSearchResults,
                    search.Elapsed.TotalMilliseconds,
                    pagingTransitions,
                    paging.Elapsed.TotalMilliseconds,
                    navigation.Elapsed.TotalMilliseconds,
                    selection.Elapsed.TotalMilliseconds,
                    preview.Elapsed.TotalMilliseconds,
                    processSamples,
                    new HostProcessMeasurement(
                        hostProcess.Id,
                        "VSTest-hosted RainArchives production model",
                        hostProcess.WorkingSet64,
                        hostProcess.PrivateMemorySize64,
                        hostProcess.HandleCount)),
                Summary: new MeasurementSummary(
                    measuredColdMilliseconds.Min(),
                    Median(measuredColdMilliseconds),
                    measuredColdMilliseconds.Max(),
                    search.Elapsed.TotalMilliseconds,
                    pagingTransitions == 0
                        ? 0
                        : paging.Elapsed.TotalMilliseconds / pagingTransitions,
                    navigation.Elapsed.TotalMilliseconds / NavigationIterationCount,
                    selection.Elapsed.TotalMilliseconds / SelectionIterationCount,
                    preview.Elapsed.TotalMilliseconds / PreviewRequestCount,
                    steadyWorkingSets.Min(),
                    steadyWorkingSets.Max(),
                    steadyPrivateBytes.Min(),
                    steadyPrivateBytes.Max(),
                    steadyHandles.Min(),
                    steadyHandles.Max()),
                Cleanup: new CleanupResult(0, 0, 0, 0));
        }
        finally
        {
            if (steadyViewModel is not null)
            {
                var workerId = steadyClient?.WorkerProcessId;
                await steadyViewModel.DisposeAsync();
                if (workerId is { } exactWorkerId)
                {
                    await WaitForExitAsync(exactWorkerId);
                    terminatedWorkerIds.Add(exactWorkerId);
                }
            }
        }

        Assert.AreEqual(fixtureSha256, Sha256File(fixturePath));
        Assert.AreEqual(
            WarmupProcessCount + MeasuredColdProcessCount,
            terminatedWorkerIds.Count);
        Assert.IsTrue(terminatedWorkerIds.Distinct().Count() >= MeasuredColdProcessCount);
        var workerFiles = Directory.GetDirectories(temporary.Path, "worker-*", SearchOption.TopDirectoryOnly)
            .Sum(directory => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        var previewCacheFiles = Directory.GetDirectories(temporary.Path, "preview-*", SearchOption.TopDirectoryOnly)
            .Sum(directory => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        var stagingCandidates = Directory.GetFileSystemEntries(
            temporary.Path,
            ".rainarchives-*.staging",
            SearchOption.AllDirectories).Length;
        Assert.AreEqual(0, workerFiles);
        Assert.AreEqual(0, previewCacheFiles);
        Assert.AreEqual(0, stagingCandidates);
        Assert.IsNotNull(evidence);
        evidence = evidence with
        {
            Cleanup = new CleanupResult(
                terminatedWorkerIds.Count,
                workerFiles,
                previewCacheFiles,
                stagingCandidates),
        };
        WriteEvidenceIfRequested(evidence);
    }

    private static void CreateFixture(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        for (var folder = 0; folder < FolderCount; folder++)
        {
            var folderName = $"folder-{folder:D2}/";
            _ = CreateFixtureEntry(archive, folderName);
            for (var file = 0; file < FilesPerFolder; file++)
            {
                _ = CreateFixtureEntry(archive, $"{folderName}item-{file:D2}.txt");
            }
        }

        WriteRepeatedText(
            archive,
            "preview-a.txt",
            'a',
            TextEntryPreviewService.MaximumTextPreviewBytes);
        WriteRepeatedText(
            archive,
            "preview-b.txt",
            'b',
            TextEntryPreviewService.MaximumTextPreviewBytes);
        for (var file = 0; file < RootFileCount; file++)
        {
            _ = CreateFixtureEntry(archive, $"雨-root-{file:D5}.txt");
        }
    }

    private static ZipArchiveEntry CreateFixtureEntry(ZipArchive archive, string name)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = FixtureTimestamp;
        return entry;
    }

    private static void WriteRepeatedText(
        ZipArchive archive,
        string name,
        char value,
        int byteCount)
    {
        var entry = CreateFixtureEntry(archive, name);
        using var output = entry.Open();
        var buffer = Encoding.UTF8.GetBytes(new string(value, 64 * 1024));
        var remaining = byteCount;
        while (remaining > 0)
        {
            var length = Math.Min(buffer.Length, remaining);
            output.Write(buffer, 0, length);
            remaining -= length;
        }
    }

    private static ProcessMeasurement CaptureProcess(int processId, string label)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        Assert.IsFalse(process.HasExited);
        return new ProcessMeasurement(
            label,
            processId,
            "RainArchives.Worker",
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.HandleCount,
            process.Threads.Count);
    }

    private static async Task WaitForExitAsync(int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Exact package Worker PID {processId} did not exit within the bounded wait.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(
                    $"The bounded package measurement command did not settle{(describe is null ? string.Empty : $": {describe()}")}.");
            }

            await Task.Delay(1);
        }
    }

    private static string ResolvePackageRoot()
    {
        var configured = Environment.GetEnvironmentVariable(PackageRootVariable);
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(GetRepositoryRoot(), "artifacts", ExpectedPackageDirectoryName)
            : Path.GetFullPath(configured);
        Assert.IsTrue(Directory.Exists(path), $"M5F package is missing: {path}");
        return path;
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "RainArchives.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Rain Archives repository root was not found.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static double Median(IReadOnlyList<double> sortedValues)
    {
        Assert.IsGreaterThan(0, sortedValues.Count);
        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2
            : sortedValues[middle];
    }

    private static void WriteEvidenceIfRequested(MeasurementEvidence evidence)
    {
        var configured = Environment.GetEnvironmentVariable(MeasurementOutputVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        var outputDirectory = Path.GetFullPath(configured);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("The M5F measurement output target must be absent.");
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "measurement.json");
        using var stream = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        JsonSerializer.Serialize(stream, evidence, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        stream.Flush(flushToDisk: true);
    }

    private sealed class PassiveDialogs : IAppDialogService
    {
        public string? ChooseArchive() => null;
        public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation) => null;
        public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly) => null;
        public Task<char[]?> RequestPasswordAsync(string archiveName, CancellationToken cancellationToken) =>
            Task.FromResult<char[]?>(null);
        public Task ShowArchiveRecoveryAsync(
            IArchiveClient client,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public void ShowError(string title, string message) => Assert.Fail(message);
        public void ShowInformation(string title, string message) { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RainArchives.M5FMeasurement.{Guid.NewGuid():N}");
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

    private sealed record MeasurementEvidence(
        int SchemaVersion,
        string Protocol,
        string PackageDirectoryName,
        string PackageManifestSha256,
        string SourceSha256,
        DateTimeOffset RecordedAtUtc,
        RuntimeContext Runtime,
        FixtureIdentity Fixture,
        MeasurementMethod Method,
        MeasurementResults Results,
        MeasurementSummary Summary,
        CleanupResult Cleanup);

    private sealed record RuntimeContext(
        string Framework,
        string OperatingSystem,
        string ProcessArchitecture,
        int LogicalProcessorCount);

    private sealed record FixtureIdentity(
        string Format,
        int EntryCount,
        int RootFileCount,
        int FolderCount,
        int FilesPerFolder,
        int PreviewFileCount,
        int PreviewFileBytes,
        long ArchiveBytes,
        string Sha256);

    private sealed record MeasurementMethod(
        int WarmupProcesses,
        int MeasuredColdProcesses,
        string ColdStartDefinition,
        int NavigationIterations,
        int SelectionIterations,
        int PreviewReplacementRequests,
        int SteadyStateSamples,
        int SteadyStateSampleIntervalMilliseconds,
        int VisibleRowLimit,
        int IndexLimit,
        int HistoryLimit);

    private sealed record MeasurementResults(
        IReadOnlyList<ColdStartMeasurement> ColdStarts,
        int UnicodeSearchResults,
        double UnicodeSearchMilliseconds,
        int PagingTransitions,
        double PagingMilliseconds,
        double NavigationMilliseconds,
        double SelectionMilliseconds,
        double PreviewReplacementAndDrainMilliseconds,
        IReadOnlyList<ProcessMeasurement> WorkerSamples,
        HostProcessMeasurement HostProcess);

    private sealed record ColdStartMeasurement(
        bool IsWarmup,
        int Iteration,
        double ElapsedMilliseconds,
        int WorkerProcessId,
        long WorkerWorkingSetBytes,
        long WorkerPrivateBytes,
        int WorkerHandleCount);

    private sealed record ProcessMeasurement(
        string Label,
        int ProcessId,
        string Attribution,
        long WorkingSetBytes,
        long PrivateBytes,
        int HandleCount,
        int ThreadCount);

    private sealed record HostProcessMeasurement(
        int ProcessId,
        string Attribution,
        long WorkingSetBytes,
        long PrivateBytes,
        int HandleCount);

    private sealed record MeasurementSummary(
        double ColdStartMinimumMilliseconds,
        double ColdStartMedianMilliseconds,
        double ColdStartMaximumMilliseconds,
        double UnicodeSearchMilliseconds,
        double AveragePageTransitionMilliseconds,
        double AverageFolderRoundTripMilliseconds,
        double AverageSelectionMilliseconds,
        double AveragePreviewReplacementAndDrainMilliseconds,
        long SteadyWorkerWorkingSetMinimumBytes,
        long SteadyWorkerWorkingSetMaximumBytes,
        long SteadyWorkerPrivateMinimumBytes,
        long SteadyWorkerPrivateMaximumBytes,
        int SteadyWorkerHandleMinimum,
        int SteadyWorkerHandleMaximum);

    private sealed record CleanupResult(
        int TerminatedWorkerCount,
        int WorkerFiles,
        int PreviewCacheFiles,
        int StagingCandidates);
}
