using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainArchives.Client;
using RainArchives.Contracts;

const long MaxFixtureBytes = 256L * 1024 * 1024;
const int MaxFiles = 20_000;
const int MaxBytesPerFile = 65_536;
const string MeasurementName = "measurement.json";
const string FixedTimestamp = "2020-01-02T03:04:05.0000000Z";

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
    var options = Parse(args);
    var outputRoot = ValidateNewOutputRoot(options.Output);
    var startedAt = DateTimeOffset.UtcNow;
    var worker = await HashWorkerAsync(options.Worker, shutdown.Token);
    var drives = new[] { Drive("C:\\"), Drive("E:\\") };
    Directory.CreateDirectory(outputRoot);
    EnsureNoReparse(outputRoot);

    Fixture? fixture = null;
    var archiveNames = new List<string>(options.Runs);
    var results = new List<RunResult>(options.Runs);
    Exception? failure = null;
    try
    {
        fixture = await CreateFixtureAsync(outputRoot, options.Files, options.Bytes, shutdown.Token);
        await using var client = await ArchiveClient.StartAsync(options.Worker, shutdown.Token)
            .WaitAsync(TimeSpan.FromSeconds(30), shutdown.Token);
        VerifyWorker(client, options.Worker);
        for (var run = 0; run < options.Runs; run++)
        {
            var archiveName = $"run-{run:D2}.rar";
            archiveNames.Add(archiveName);
            results.Add(await RunAsync(client, outputRoot, archiveName, fixture.Value, options.Preset, shutdown.Token));
        }
    }
    catch (Exception ex)
    {
        failure = ex;
    }
    finally
    {
        var cleanupErrors = await CleanupAsync(outputRoot, fixture, archiveNames);
        var unexpected = Directory.EnumerateFileSystemEntries(outputRoot)
            .Where(path => !string.Equals(Path.GetFileName(path), MeasurementName, StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (cleanupErrors.Count != 0 || unexpected.Length != 0)
            failure ??= new IOException("Owned cleanup was incomplete or unexpected output remained.");

        var measurement = new Measurement(
            startedAt, DateTimeOffset.UtcNow, options with { Output = outputRoot }, worker, drives,
            fixture is null ? null : new FixtureSummary(fixture.Value.Files.Count, fixture.Value.Bytes, fixture.Value.ManifestSha256),
            results, failure?.GetType().FullName, Trim(failure?.Message), cleanupErrors, unexpected,
            failure is null && unexpected.Length == 0 && cleanupErrors.Count == 0);
        await WriteMeasurementAsync(outputRoot, measurement);
    }
    Console.WriteLine(Path.Combine(outputRoot, MeasurementName));
    return failure is null ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Probe refused/failed: {ex.Message}");
    return 2;
}

static Options Parse(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var known = new HashSet<string>(["--worker", "--output", "--files", "--bytes", "--runs", "--preset"], StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Arguments must be --name value pairs.");
        if (!known.Contains(args[i])) throw new ArgumentException($"Unknown argument: {args[i]}");
        if (!values.TryAdd(args[i], args[++i])) throw new ArgumentException($"Duplicate argument: {args[i]}");
    }
    string Required(string name) => values.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing {name}.");
    string Absolute(string name) { var value = Required(name); if (!Path.IsPathFullyQualified(value)) throw new ArgumentException($"{name} must be absolute."); return Path.GetFullPath(value); }
    int Number(string name, int fallback, int min, int max) =>
        int.TryParse(values.GetValueOrDefault(name, fallback.ToString()), out var value) && value >= min && value <= max
            ? value : throw new ArgumentException($"{name} must be between {min} and {max}.");
    var presetText = values.GetValueOrDefault("--preset", "Balanced");
    if (!Enum.TryParse<ArchiveCompressionPreset>(presetText, true, out var preset) || !Enum.IsDefined(preset))
        throw new ArgumentException("--preset must be Store, Fast, Balanced, or Maximum.");
    var files = Number("--files", 1000, 1, MaxFiles);
    var bytes = Number("--bytes", 4096, 0, MaxBytesPerFile);
    if ((long)files * bytes > MaxFixtureBytes) throw new ArgumentException("The fixture cannot exceed 256 MiB.");
    return new Options(Absolute("--worker"), Absolute("--output"), files, bytes,
        Number("--runs", 1, 1, 5), preset);
}

static string ValidateNewOutputRoot(string path)
{
    if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("--output must be absolute.");
    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    if (!Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new ArgumentException("--output leaf must be a N-format GUID.");
    if (Directory.Exists(full) || File.Exists(full)) throw new IOException("--output must name a new, absent directory.");
    var parent = Directory.GetParent(full)?.FullName ?? throw new ArgumentException("--output has no existing parent.");
    if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
    EnsureNoReparse(parent);
    return full;
}

static void EnsureNoReparse(string path)
{
    for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Reparse point refused: {current.FullName}");
}

static DriveSummary Drive(string root)
{
    try
    {
        var d = new DriveInfo(root);
        return new(root, d.DriveType.ToString(), d.IsReady ? d.DriveFormat : null, d.IsReady ? d.TotalSize : null,
            d.IsReady ? d.AvailableFreeSpace : null, d.IsReady ? d.TotalFreeSpace : null, null);
    }
    catch (Exception ex) { return new(root, null, null, null, null, null, Trim(ex.Message)); }
}

static async Task<WorkerSummary> HashWorkerAsync(string path, CancellationToken token)
{
    var dll = Path.ChangeExtension(path, ".dll");
    EnsureRegularFile(path); EnsureRegularFile(dll);
    return new(Path.GetFullPath(path), await HashAsync(path, token), Path.GetFullPath(dll), await HashAsync(dll, token));
}

static void EnsureRegularFile(string path)
{
    if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        throw new FileNotFoundException("A required regular file was not found.", path);
}

static async Task<Fixture> CreateFixtureAsync(string root, int count, int bytes, CancellationToken token)
{
    var source = Path.Combine(root, "source");
    Directory.CreateDirectory(source);
    var groups = (count + 199) / 200;
    var files = new List<ArchiveCreationFile>(count);
    var sourceFiles = new List<SourceFile>(count);
    var sourceDirectories = new List<string>(groups + 1) { source };
    var names = new HashSet<string>(StringComparer.Ordinal);
    using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    AddManifest(manifest, $"D|group|{FixedTimestamp}");
    for (var i = 0; i < groups; i++)
    {
        var directoryName = $"d{i:D2}";
        var directory = Path.Combine(source, directoryName);
        Directory.CreateDirectory(directory);
        AddManifest(manifest, $"D|group/{directoryName}|{FixedTimestamp}");
        sourceDirectories.Add(directory);
        for (var n = i * 200; n < Math.Min(count, (i + 1) * 200); n++)
        {
            var name = $"group/{directoryName}/f{n:D5}.bin";
            var local = Path.Combine(directory, $"f{n:D5}.bin");
            var payload = new byte[bytes];
            for (var b = 0; b < payload.Length; b++) payload[b] = (byte)((b * 31 + n * 17 + 0x5D) & 0xFF);
            await File.WriteAllBytesAsync(local, payload, token);
            File.SetLastWriteTimeUtc(local, FixedTime());
            var hash = Convert.ToHexString(SHA256.HashData(payload));
            AddManifest(manifest, $"F|{name}|{bytes}|{hash}|{FixedTimestamp}");
            files.Add(new(local, name));
            sourceFiles.Add(new(local, bytes, hash));
            names.Add(name);
        }
        Directory.SetLastWriteTimeUtc(directory, FixedTime());
    }
    Directory.SetLastWriteTimeUtc(source, FixedTime());
    return new(source, files, sourceFiles, sourceDirectories, groups, (long)count * bytes, Convert.ToHexString(manifest.GetHashAndReset()), names);
}

static async Task<RunResult> RunAsync(ArchiveClient client, string root, string archiveName, Fixture fixture, ArchiveCompressionPreset preset, CancellationToken shutdown)
{
    using var run = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    run.CancelAfter(TimeSpan.FromMinutes(10));
    var runStartedAt = DateTimeOffset.UtcNow;
    var archive = Path.Combine(root, archiveName);
    var observed = new List<Observed>();
    var gate = new object();
    var terminalPresented = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    OperationId active = default;
    ArchiveSessionId session = default;
    EventHandler<OperationEvent>? handler = (_, e) => { lock (gate) observed.Add(new(DateTimeOffset.UtcNow, e)); if (e.Kind == OperationEventKind.Terminal) terminalPresented.TrySetResult(true); };
    client.OperationEventReceived += handler;
    var stopwatch = Stopwatch.StartNew();
    WorkerSampler? sampler = null;
    try
    {
        sampler = new WorkerSampler(GetWorkerPid(client));
        sampler.Start();
        var started = await client.StartRarTreeCreationAsync(archive, [], [new ArchiveCreationDirectory(fixture.Source, "group")], preset, run.Token).WaitAsync(run.Token);
        active = started.OperationId;
        var terminal = await client.WaitForTerminalAsync(active, run.Token).WaitAsync(run.Token);
        active = default;
        stopwatch.Stop();
        await terminalPresented.Task.WaitAsync(TimeSpan.FromSeconds(2), run.Token);
        var resources = sampler.Stop();
        if (terminal.Phase is not (OperationPhase.Completed or OperationPhase.CompletedWithWarnings)) throw new InvalidDataException($"Creation terminal phase was {terminal.Phase}.");
        if (!File.Exists(archive) || (File.GetAttributes(archive) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("RAR output was not a regular file.");
        var archiveBytes = new FileInfo(archive).Length;
        var archiveSha256 = await HashAsync(archive, run.Token);
        var opened = await client.OpenArchiveAsync(archive, cancellationToken: run.Token).WaitAsync(run.Token);
        session = opened.Archive.SessionId;
        if (!string.Equals(opened.Archive.Format, "rar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Rain reopened output as {opened.Archive.Format}.");
        var entries = await ListAllAsync(client, session, run.Token);
        ValidateEntries(entries, fixture);
        var integrity = await client.StartIntegrityTestAsync(session, [], cancellationToken: run.Token).WaitAsync(run.Token);
        active = integrity.OperationId;
        var integrityTerminal = await client.WaitForTerminalAsync(active, run.Token).WaitAsync(run.Token);
        active = default;
        if (integrityTerminal.Phase is not (OperationPhase.Completed or OperationPhase.CompletedWithWarnings)) throw new InvalidDataException($"Integrity terminal phase was {integrityTerminal.Phase}.");
        Observed[] events;
        lock (gate) events = observed.Where(x => x.Event.OperationId == started.OperationId).ToArray();
        var firstProgress = events.FirstOrDefault(x => x.Event.Kind == OperationEventKind.Progress);
        var finalizing = events.FirstOrDefault(x => x.Event.Phase == OperationPhase.Finalizing);
        var terminalObserved = events.FirstOrDefault(x => x.Event.Kind == OperationEventKind.Terminal);
        var lastProgress = events.LastOrDefault(x => x.Event.Kind == OperationEventKind.Progress
            && x.Event.Progress is { BytesProcessed: var processed, TotalBytes: var total }
            && processed == fixture.Bytes && total is not null && total == processed
            && (finalizing.Event is null || x.At < finalizing.At));
        return new(runStartedAt, archiveName, archiveBytes, archiveSha256, stopwatch.Elapsed.TotalMilliseconds, resources,
            firstProgress.Event is null ? null : Point(firstProgress),
            lastProgress.Event is null ? null : Point(lastProgress),
            AtOrNull(finalizing), AtOrNull(terminalObserved),
            terminal.Phase.ToString(), entries.Count, integrityTerminal.Phase.ToString(), true);
    }
    catch (OperationCanceledException ex) when (run.IsCancellationRequested)
    {
        if (active.Value != Guid.Empty) await CancelAndDrainAsync(client, active);
        throw new TimeoutException("The 10-minute run bound expired; the active operation was cancelled and drained.", ex);
    }
    finally
    {
        if (session.Value != Guid.Empty) { using var close = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await client.CloseArchiveAsync(session, close.Token); }
        if (sampler is not null) sampler.Dispose();
        client.OperationEventReceived -= handler;
    }
}

static async Task<IReadOnlyList<ArchiveEntryDescriptor>> ListAllAsync(ArchiveClient client, ArchiveSessionId session, CancellationToken token)
{
    var all = new List<ArchiveEntryDescriptor>();
    var offset = 0;
    while (true)
    {
        var page = await client.ListEntriesAsync(session, offset, 4096, token).WaitAsync(token);
        if (page.Offset != offset || page.TotalCount < all.Count || page.Entries.Count == 0 && page.NextOffset is not null) throw new InvalidDataException("Malformed archive list pagination.");
        all.AddRange(page.Entries);
        if (page.NextOffset is null) { if (page.TotalCount != all.Count) throw new InvalidDataException("Archive list count was inconsistent."); return all; }
        if (page.NextOffset <= offset || page.NextOffset > page.TotalCount) throw new InvalidDataException("Malformed archive list continuation.");
        offset = page.NextOffset.Value;
    }
}

static void ValidateEntries(IReadOnlyList<ArchiveEntryDescriptor> entries, Fixture fixture)
{
    var expected = new Dictionary<string, ArchiveEntryKind>(StringComparer.Ordinal) { ["group"] = ArchiveEntryKind.Directory };
    for (var i = 0; i < fixture.Groups; i++) expected[$"group/d{i:D2}"] = ArchiveEntryKind.Directory;
    foreach (var file in fixture.Names) expected[file] = ArchiveEntryKind.File;
    if (entries.Count != expected.Count) throw new InvalidDataException($"RAR listed {entries.Count} entries; expected {expected.Count}.");
    var actual = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in entries)
    {
        var name = entry.RawPath.Replace('\\', '/').TrimEnd('/');
        if (name.Length == 0 || !actual.Add(name) || !expected.TryGetValue(name, out var kind) || entry.Kind != kind || entry.SafetyStatus != EntrySafetyStatus.Safe)
            throw new InvalidDataException($"Malformed RAR entry: '{entry.RawPath}'.");
    }
    if (!actual.SetEquals(expected.Keys)) throw new InvalidDataException("RAR entry names did not match the deterministic fixture.");
}

static async Task CancelAndDrainAsync(ArchiveClient client, OperationId operation)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await client.CancelOperationAsync(operation, timeout.Token);
    await client.WaitForTerminalAsync(operation, timeout.Token);
}

static int GetWorkerPid(ArchiveClient client)
{
    var property = typeof(ArchiveClient).GetProperty("WorkerProcessId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("WorkerProcessId");
    return (int)(property.GetValue(client) ?? throw new InvalidOperationException("Worker PID unavailable."));
}

static void VerifyWorker(ArchiveClient client, string expectedPath)
{
    using var process = Process.GetProcessById(GetWorkerPid(client));
    var actual = process.MainModule?.FileName;
    if (!string.Equals(Path.GetFullPath(actual ?? string.Empty), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The client PID was not the requested worker executable.");
}

static ProgressPoint? Point(Observed observed) => observed.Event.Progress is { } p ? new(observed.At, p.BytesProcessed, p.EntriesProcessed, p.CurrentEntryName) : null;
static DateTimeOffset? AtOrNull(Observed observed) => observed.Event is null ? null : observed.At;
static void AddManifest(IncrementalHash hash, string line) => hash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
static async Task<string> HashAsync(string path, CancellationToken token) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)); }
static string? Trim(string? value) => value is null ? null : value.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(1024, value.Length)];
static DateTime FixedTime() => new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

static async Task<List<string>> CleanupAsync(string root, Fixture? fixture, IReadOnlyList<string> archives)
{
    var errors = new List<string>();
    try
    {
        if (fixture is { } f)
        {
            await ValidateSourceAsync(root, f);
            foreach (var file in f.SourceFiles.OrderByDescending(x => x.Path.Length))
            {
                EnsureRegularFile(file.Path);
                File.Delete(file.Path);
            }
            foreach (var directory in f.SourceDirectories.OrderByDescending(x => x.Length))
            {
                EnsureDirectory(directory);
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new IOException("The owned directory is no longer empty; preserve it.");
                // Windows can mark ordinary folders read-only. Only clear this
                // bit on an exact, verified empty fixture directory, never a source
                // supplied by the user or an unexpected object.
                File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
                Directory.Delete(directory, false);
            }
        }
    }
    catch (Exception ex) { errors.Add($"source: {Trim(ex.Message)}"); }
    foreach (var name in archives)
        try { var path = Path.Combine(root, name); if (File.Exists(path)) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse point"); File.Delete(path); } }
        catch (Exception ex) { errors.Add($"{name}: {Trim(ex.Message)}"); }
    return errors;
}

static async Task ValidateSourceAsync(string root, Fixture fixture)
{
    var source = Path.GetFullPath(fixture.Source);
    var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    if (!string.Equals(Directory.GetParent(source)?.FullName, output, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(Path.GetFileName(source), "source", StringComparison.Ordinal)) throw new IOException("Fixture ancestry changed.");
    var expectedDirectories = new HashSet<string>(fixture.SourceDirectories.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
    var expectedFiles = fixture.SourceFiles.ToDictionary(x => Path.GetFullPath(x.Path), StringComparer.OrdinalIgnoreCase);
    foreach (var directory in fixture.SourceDirectories.OrderBy(x => x.Length))
    {
        EnsureDirectory(directory);
        var expected = new HashSet<string>(
            expectedDirectories.Where(x => string.Equals(Directory.GetParent(x)?.FullName, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                .Concat(expectedFiles.Keys.Where(x => string.Equals(Directory.GetParent(x)?.FullName, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))),
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in Directory.EnumerateFileSystemEntries(directory)
            .Select(path => (Path: Path.GetFullPath(path), Attributes: File.GetAttributes(path))))
        {
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Reparse point refused: {item.Path}");
            if (!expected.Remove(item.Path)) throw new IOException($"Unexpected fixture entry: {item.Path}");
        }
        if (expected.Count != 0) throw new IOException($"Fixture entry missing below {directory}.");
    }
    foreach (var file in fixture.SourceFiles)
    {
        EnsureRegularFile(file.Path);
        if (!expectedFiles.ContainsKey(Path.GetFullPath(file.Path)) || new FileInfo(file.Path).Length != file.Bytes
            || !string.Equals(await HashAsync(file.Path, CancellationToken.None), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Fixture file changed: {file.Path}");
    }
}

static void EnsureDirectory(string path)
{
    if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException($"Expected directory missing or unsafe: {path}");
}

static async Task WriteMeasurementAsync(string root, Measurement measurement)
{
    var path = Path.Combine(root, MeasurementName);
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough);
    await JsonSerializer.SerializeAsync(stream, measurement, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    await stream.FlushAsync();
    stream.Flush(true);
}

sealed class WorkerSampler(int pid) : IDisposable
{
    readonly Process process = Process.GetProcessById(pid);
    readonly CancellationTokenSource stop = new();
    Task? loop;
    TimeSpan initialCpu;
    TimeSpan lastCpu;
    long peakMemory;
    double peakCpu;
    long start;
    long lastSample;
    public void Start() { process.Refresh(); initialCpu = process.TotalProcessorTime; lastCpu = initialCpu; peakMemory = process.WorkingSet64; start = Stopwatch.GetTimestamp(); lastSample = start; loop = SampleAsync(); }
    public Resources Stop() { stop.Cancel(); loop?.GetAwaiter().GetResult(); process.Refresh(); peakMemory = Math.Max(peakMemory, process.WorkingSet64); return new((process.TotalProcessorTime - initialCpu).TotalMilliseconds, peakMemory, peakCpu); }
    async Task SampleAsync()
    {
        try
        {
            while (true)
            {
                process.Refresh();
                var now = Stopwatch.GetTimestamp();
                var currentCpu = process.TotalProcessorTime;
                var sampleWall = Stopwatch.GetElapsedTime(lastSample).TotalMilliseconds;
                var sampleCpu = (currentCpu - lastCpu).TotalMilliseconds;
                peakMemory = Math.Max(peakMemory, process.WorkingSet64);
                // Ignore the immediate startup sample: CPU accounting granularity
                // is larger than that interval and can produce a fictitious peak.
                if (sampleWall >= 40)
                {
                    peakCpu = Math.Max(peakCpu, sampleCpu / sampleWall / Environment.ProcessorCount * 100);
                    lastCpu = currentCpu; lastSample = now;
                }
                await Task.Delay(50, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public void Dispose() { stop.Cancel(); loop?.GetAwaiter().GetResult(); stop.Dispose(); process.Dispose(); }
}

readonly record struct Options(string Worker, string Output, int Files, int Bytes, int Runs, ArchiveCompressionPreset Preset);
readonly record struct Fixture(string Source, List<ArchiveCreationFile> Files, List<SourceFile> SourceFiles, List<string> SourceDirectories, int Groups, long Bytes, string ManifestSha256, HashSet<string> Names);
readonly record struct SourceFile(string Path, long Bytes, string Sha256);
readonly record struct WorkerSummary(string ExePath, string ExeSha256, string DllPath, string DllSha256);
readonly record struct DriveSummary(string Root, string? Type, string? Format, long? TotalBytes, long? AvailableBytes, long? FreeBytes, string? Error);
readonly record struct ProgressPoint(DateTimeOffset At, long BytesProcessed, int EntriesProcessed, string? CurrentEntryName);
readonly record struct Observed(DateTimeOffset At, OperationEvent Event);
readonly record struct Resources(double CpuMilliseconds, long PeakWorkingSetBytes, double PeakCpuPercent);
sealed record RunResult(DateTimeOffset StartedAt, string Archive, long ArchiveBytes, string ArchiveSha256, double CreationMilliseconds, Resources Resources, ProgressPoint? FirstProgress, ProgressPoint? LastSourceProgress, DateTimeOffset? FinalizingAt, DateTimeOffset? TerminalAt, string TerminalPhase, int ListedEntryCount, string IntegrityPhase, bool OperationSucceeded);
sealed record FixtureSummary(int Files, long Bytes, string ManifestSha256);
sealed record Measurement(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, Options Options, WorkerSummary Worker, DriveSummary[] Drives, FixtureSummary? Fixture, List<RunResult> Runs, string? FailureType, string? Failure, List<string> CleanupErrors, string[] UnexpectedEntries, bool CleanupComplete);
