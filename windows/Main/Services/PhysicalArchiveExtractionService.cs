using System.IO;
using RainExplorer.Models;

namespace RainExplorer.Services;

internal sealed record PhysicalArchiveExtractionPlan(
    string SourcePath,
    string DestinationPath);

internal sealed record PhysicalArchiveExtractionBatchResult(
    int RequestedCount,
    IReadOnlyList<ArchiveOperationResult> Results)
{
    public int CompletedCount => Results.Count(result => result.Succeeded);
    public int WarningCount => Results.Count(result =>
        result.Outcome == ArchiveOperationOutcome.CompletedWithWarnings);
    public bool WasCancelled => Results.Any(result =>
        result.Outcome == ArchiveOperationOutcome.Cancelled);
    public ArchiveOperationOutcome? LastOutcome => Results.LastOrDefault()?.Outcome;

    public static PhysicalArchiveExtractionBatchResult Refused(
        int requestedCount,
        ArchiveOperationOutcome outcome) =>
        new(requestedCount, [new ArchiveOperationResult(outcome)]);
}

internal interface IPhysicalArchiveExtractionSession : IAsyncDisposable
{
    ArchiveLocationState State { get; }
    bool RequiresPassword { get; }
    bool CanExtractEntireArchive { get; }

    Task OpenAsync(string sourcePath, CancellationToken cancellationToken);

    Task OpenWithPasswordAsync(
        string sourcePath,
        char[] password,
        CancellationToken cancellationToken);

    Task<ArchiveOperationResult> ExtractEntireArchiveAsync(
        string destinationPath,
        char[]? password,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs physical-file extraction as bounded, sequential, independent Worker operations.
/// It never creates, merges, deletes, or retries a destination itself.
/// </summary>
internal sealed class PhysicalArchiveExtractionService : IAsyncDisposable
{
    public const int MaximumArchiveCount = 16;

    private readonly Func<IPhysicalArchiveExtractionSession> _sessionFactory;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private Task _activeTask = Task.CompletedTask;
    private bool _disposed;

    public PhysicalArchiveExtractionService(ArchiveClientRuntime runtime)
        : this(() => new AdapterSession(new ArchiveLocationAdapter(runtime)))
    {
    }

    internal PhysicalArchiveExtractionService(
        ArchiveClientRuntime runtime,
        IArchiveActivitySink activitySink)
        : this(() => new AdapterSession(new ArchiveLocationAdapter(runtime, activitySink)))
    {
    }

    internal PhysicalArchiveExtractionService(
        Func<IPhysicalArchiveExtractionSession> sessionFactory)
    {
        _sessionFactory = sessionFactory
            ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public Task<PhysicalArchiveExtractionBatchResult> ExtractAsync(
        IReadOnlyList<string> sourcePaths,
        Func<string, char[]?> passwordPrompt,
        CancellationToken cancellationToken = default)
        => StartExtractionAsync(sourcePaths, destinationParent: null, passwordPrompt, cancellationToken);

    public Task<PhysicalArchiveExtractionBatchResult> ExtractToAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationParent,
        Func<string, char[]?> passwordPrompt,
        CancellationToken cancellationToken = default)
        => StartExtractionAsync(sourcePaths, destinationParent, passwordPrompt, cancellationToken);

    private Task<PhysicalArchiveExtractionBatchResult> StartExtractionAsync(
        IReadOnlyList<string> sourcePaths,
        string? destinationParent,
        Func<string, char[]?> passwordPrompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(passwordPrompt);

        if (!TryCreatePlan(
                sourcePaths,
                destinationParent,
                out IReadOnlyList<PhysicalArchiveExtractionPlan> plan))
        {
            return Task.FromResult(PhysicalArchiveExtractionBatchResult.Refused(
                sourcePaths.Count,
                ArchiveOperationOutcome.InvalidSelection));
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return Task.FromResult(PhysicalArchiveExtractionBatchResult.Refused(
                    sourcePaths.Count,
                    ArchiveOperationOutcome.Cancelled));
            }
            if (!_activeTask.IsCompleted)
            {
                return Task.FromResult(PhysicalArchiveExtractionBatchResult.Refused(
                    sourcePaths.Count,
                    ArchiveOperationOutcome.Busy));
            }

            var activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            _activeCancellation = activeCancellation;
            Task<PhysicalArchiveExtractionBatchResult> operation =
                RunAndReleaseAsync(plan, passwordPrompt, activeCancellation);
            _activeTask = operation;
            return operation;
        }
    }

    internal static bool TryCreatePlan(
        IReadOnlyList<string> sourcePaths,
        out IReadOnlyList<PhysicalArchiveExtractionPlan> plan) =>
        TryCreatePlan(sourcePaths, destinationParent: null, out plan);

    internal static bool TryCreatePlan(
        IReadOnlyList<string> sourcePaths,
        string? destinationParent,
        out IReadOnlyList<PhysicalArchiveExtractionPlan> plan)
    {
        plan = Array.Empty<PhysicalArchiveExtractionPlan>();
        if (sourcePaths.Count is < 1 or > MaximumArchiveCount) return false;

        var planned = new List<PhysicalArchiveExtractionPlan>(sourcePaths.Count);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? selectedParent = null;
            if (destinationParent is not null)
            {
                if (string.IsNullOrWhiteSpace(destinationParent)
                    || destinationParent.Length
                        > RainArchives.Contracts.ProtocolConstants.MaximumPathCharacters
                    || !Path.IsPathFullyQualified(destinationParent))
                    return false;
                selectedParent = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(destinationParent));
                if (!Directory.Exists(selectedParent)
                    || !File.GetAttributes(selectedParent).HasFlag(FileAttributes.Directory))
                    return false;
            }

            foreach (string sourcePath in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(sourcePath)
                    || sourcePath.Length > RainArchives.Contracts.ProtocolConstants.MaximumPathCharacters
                    || !Path.IsPathFullyQualified(sourcePath))
                    return false;

                string source = Path.GetFullPath(sourcePath);
                if (!File.Exists(source)
                    || File.GetAttributes(source).HasFlag(FileAttributes.Directory)
                    || !sources.Add(source))
                    return false;

                string? parent = selectedParent ?? Path.GetDirectoryName(source);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return false;
                string stem = DestinationStem(Path.GetFileName(source));
                if (!TryChooseAbsentDestination(parent, stem, destinations, out string destination))
                    return false;

                destinations.Add(destination);
                planned.Add(new PhysicalArchiveExtractionPlan(source, destination));
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or UnauthorizedAccessException)
        {
            return false;
        }

        plan = planned;
        return true;
    }

    private async Task<PhysicalArchiveExtractionBatchResult> RunAndReleaseAsync(
        IReadOnlyList<PhysicalArchiveExtractionPlan> plan,
        Func<string, char[]?> passwordPrompt,
        CancellationTokenSource activeCancellation)
    {
        await Task.Yield();
        try
        {
            return await RunAsync(
                plan,
                passwordPrompt,
                activeCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeCancellation, activeCancellation))
                    _activeCancellation = null;
            }
            activeCancellation.Dispose();
        }
    }

    private async Task<PhysicalArchiveExtractionBatchResult> RunAsync(
        IReadOnlyList<PhysicalArchiveExtractionPlan> plan,
        Func<string, char[]?> passwordPrompt,
        CancellationToken cancellationToken)
    {
        var results = new List<ArchiveOperationResult>(plan.Count);
        foreach (PhysicalArchiveExtractionPlan item in plan)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new ArchiveOperationResult(ArchiveOperationOutcome.Cancelled));
                break;
            }

            ArchiveOperationResult result;
            try
            {
                await using IPhysicalArchiveExtractionSession session = _sessionFactory();
                await session.OpenAsync(item.SourcePath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                bool openedWithPassword = false;
                if (session.State == ArchiveLocationState.PasswordRequired)
                {
                    char[]? openPassword = null;
                    bool passwordTransferred = false;
                    try
                    {
                        openPassword = passwordPrompt(Path.GetFileName(item.SourcePath));
                        if (openPassword is null)
                        {
                            result = new ArchiveOperationResult(
                                ArchiveOperationOutcome.Cancelled);
                            results.Add(result);
                            break;
                        }

                        Task passwordOpen = session.OpenWithPasswordAsync(
                            item.SourcePath,
                            openPassword,
                            cancellationToken);
                        passwordTransferred = true;
                        openPassword = null;
                        await passwordOpen;
                        cancellationToken.ThrowIfCancellationRequested();
                        openedWithPassword = true;
                    }
                    finally
                    {
                        if (!passwordTransferred && openPassword is not null)
                            Array.Clear(openPassword);
                    }
                }

                if (session.State is not (ArchiveLocationState.Ready or ArchiveLocationState.Empty))
                {
                    result = new ArchiveOperationResult(MapOpenFailure(session.State));
                }
                else if (!session.CanExtractEntireArchive)
                {
                    result = new ArchiveOperationResult(ArchiveOperationOutcome.Unsupported);
                }
                else
                {
                    char[]? password = null;
                    bool passwordTransferred = false;
                    try
                    {
                        if (session.RequiresPassword && !openedWithPassword)
                        {
                            password = passwordPrompt(Path.GetFileName(item.SourcePath));
                            if (password is null)
                            {
                                result = new ArchiveOperationResult(
                                    ArchiveOperationOutcome.Cancelled);
                                results.Add(result);
                                break;
                            }
                        }

                        Task<ArchiveOperationResult> extraction =
                            session.ExtractEntireArchiveAsync(
                                item.DestinationPath,
                                password,
                                cancellationToken);
                        passwordTransferred = true;
                        password = null;
                        // Preserve the caller's WPF context so a later archive in this
                        // sequential batch can safely invoke the password dialog callback.
                        result = await extraction;
                    }
                    finally
                    {
                        if (!passwordTransferred && password is not null)
                            Array.Clear(password);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = new ArchiveOperationResult(ArchiveOperationOutcome.Cancelled);
            }
            catch (Exception exception) when (exception is ArgumentException
                                                  or IOException
                                                  or InvalidDataException
                                                  or NotSupportedException
                                                  or ObjectDisposedException
                                                  or UnauthorizedAccessException)
            {
                result = new ArchiveOperationResult(
                    exception is IOException or ObjectDisposedException
                        ? ArchiveOperationOutcome.Offline
                        : ArchiveOperationOutcome.Failed);
            }

            results.Add(result);
            if (result.Outcome is ArchiveOperationOutcome.Cancelled
                or ArchiveOperationOutcome.Offline)
                break;
        }

        return new PhysicalArchiveExtractionBatchResult(plan.Count, results);
    }

    private static ArchiveOperationOutcome MapOpenFailure(ArchiveLocationState state) =>
        state switch
        {
            ArchiveLocationState.Unsupported => ArchiveOperationOutcome.Unsupported,
            ArchiveLocationState.Stale => ArchiveOperationOutcome.Stale,
            ArchiveLocationState.Offline => ArchiveOperationOutcome.Offline,
            _ => ArchiveOperationOutcome.Failed,
        };

    private static string DestinationStem(string fileName)
    {
        string stem = ArchiveFileTypes.RemoveSupportedSuffix(fileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = fileName + " extracted";
        return stem;
    }

    private static bool TryChooseAbsentDestination(
        string parent,
        string stem,
        HashSet<string> reserved,
        out string destination)
    {
        destination = string.Empty;
        for (int suffix = 1; suffix <= 10_000; suffix++)
        {
            string name = suffix == 1 ? stem : $"{stem} ({suffix})";
            string candidate = Path.GetFullPath(Path.Combine(parent, name));
            if (candidate.Length > RainArchives.Contracts.ProtocolConstants.MaximumPathCharacters)
                return false;
            if (!string.Equals(
                    Path.GetDirectoryName(candidate),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (reserved.Contains(candidate)
                || File.Exists(candidate)
                || Directory.Exists(candidate))
                continue;
            destination = candidate;
            return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Task active;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _activeCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            active = _activeTask;
        }
        await active.ConfigureAwait(false);
    }

    private sealed class AdapterSession(ArchiveLocationAdapter adapter)
        : IPhysicalArchiveExtractionSession
    {
        public ArchiveLocationState State => adapter.State;
        public bool RequiresPassword => adapter.RequiresOperationPassword;
        public bool CanExtractEntireArchive => adapter.CanExtractEntireArchive;

        public Task OpenAsync(string sourcePath, CancellationToken cancellationToken) =>
            adapter.OpenAsync(sourcePath, cancellationToken);

        public Task OpenWithPasswordAsync(
            string sourcePath,
            char[] password,
            CancellationToken cancellationToken) =>
            adapter.OpenWithPasswordAsync(sourcePath, password, cancellationToken);

        public Task<ArchiveOperationResult> ExtractEntireArchiveAsync(
            string destinationPath,
            char[]? password,
            CancellationToken cancellationToken) =>
            adapter.ExtractEntireArchiveAsync(destinationPath, password, cancellationToken);

        public ValueTask DisposeAsync() => adapter.DisposeAsync();
    }
}
