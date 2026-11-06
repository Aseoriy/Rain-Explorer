using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;

namespace RainArchives.Client;

public sealed partial class ArchiveClient : IArchiveClient
{
    private const int MaximumPresentationEvents = 256;
    private const int MaximumRetiredOperations = 256;
    private static readonly IReadOnlySet<string> RecoveryReservedNames =
        CreateRecoveryReservedNames();
    private readonly NamedPipeServerStream _pipe;
    private readonly Process _worker;
    private readonly WorkerJob _job;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _requestSlots = new(
        ProtocolConstants.MaximumPendingRequests,
        ProtocolConstants.MaximumPendingRequests);
    private readonly SemaphoreSlim _controlRequestSlots = new(
        ProtocolConstants.MaximumControlRequests,
        ProtocolConstants.MaximumControlRequests);
    private readonly SemaphoreSlim _entryStreamSlots = new(
        ProtocolConstants.MaximumEntryStreamLeases,
        ProtocolConstants.MaximumEntryStreamLeases);
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<EntryStreamId, ArchiveEntryStreamLease> _entryStreams = new();
    private readonly ConcurrentDictionary<EntryStreamId, byte> _seenEntryStreamIds = new();
    private readonly ConcurrentDictionary<ArchiveSessionId, ArchiveSessionId> _nestedParents = new();
    private readonly SemaphoreSlim _sessionLifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<OperationId, TaskCompletionSource<OperationEvent>> _terminals = new();
    private readonly ConcurrentDictionary<OperationId, byte> _authorizedOperations = new();
    private readonly Dictionary<OperationId, long> _eventSequences = [];
    private readonly Dictionary<OperationId, OperationPhase> _eventPhases = [];
    private readonly ConcurrentDictionary<OperationId, byte> _completedEventStreams = new();
    private readonly ConcurrentQueue<OperationId> _retiredOperationOrder = new();
    private readonly Channel<OperationEvent> _presentationEvents;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IReadOnlySet<string> _enabledFeatures;
    private readonly int _negotiatedProtocolMinor;
    private readonly Task _readLoop;
    private readonly Task _eventDispatchLoop;
    private readonly object _disposeGate = new();
    private ArchiveClientException? _transportFailure;
    private Task? _disposeTask;
    private long _nextRequestSequence;
    private int _disposeStarted;
    private int _transportAbortStarted;

    private ArchiveClient(
        NamedPipeServerStream pipe,
        Process worker,
        WorkerJob job,
        IReadOnlySet<string> enabledFeatures,
        int negotiatedProtocolMinor)
    {
        _pipe = pipe;
        _worker = worker;
        _job = job;
        _enabledFeatures = enabledFeatures;
        _negotiatedProtocolMinor = negotiatedProtocolMinor;
        _presentationEvents = Channel.CreateBounded<OperationEvent>(new BoundedChannelOptions(MaximumPresentationEvents)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _eventDispatchLoop = Task.Run(() => DispatchPresentationEventsAsync(_shutdown.Token));
        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));
    }

    public event EventHandler<OperationEvent>? OperationEventReceived;

    /// <summary>
    /// Total files and directories allowed after creation-tree expansion, including mapped roots.
    /// Requires the format's tree-creation capability. Explicit request mappings and ZIP rebuild
    /// retain their legacy limits; byte, path and metadata budgets can further limit a tree.
    /// </summary>
    public int MaximumCreationTreeEntries => SupportsFeature(ProtocolFeatures.ExpandedCreationTreeBudget)
        ? ProtocolConstants.MaximumExpandedCreationTreeEntries
        : ProtocolConstants.MaximumZipCreationEntries;

    public bool SupportsFeature(string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        return _enabledFeatures.Contains(feature);
    }

    internal int WorkerProcessId => _worker.Id;

    public static Task<ArchiveClient> StartAsync(
        string workerPath,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(
            workerPath,
            workerTemporaryRoot: null,
            isolateExtractionRecovery: false,
            cancellationToken);

    internal static Task<ArchiveClient> StartWithTemporaryRootAsync(
        string workerPath,
        string workerTemporaryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerTemporaryRoot);
        var canonicalTemporaryRoot = Path.GetFullPath(workerTemporaryRoot);
        if (!Directory.Exists(canonicalTemporaryRoot))
        {
            throw new DirectoryNotFoundException("The worker temporary root does not exist.");
        }

        return StartCoreAsync(
            workerPath,
            canonicalTemporaryRoot,
            isolateExtractionRecovery: false,
            cancellationToken);
    }

    internal static Task<ArchiveClient> StartWithIsolatedRecoveryRootsAsync(
        string workerPath,
        string workerTemporaryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerTemporaryRoot);
        var canonicalTemporaryRoot = Path.GetFullPath(workerTemporaryRoot);
        if (!Directory.Exists(canonicalTemporaryRoot))
        {
            throw new DirectoryNotFoundException("The worker temporary root does not exist.");
        }

        return StartCoreAsync(
            workerPath,
            canonicalTemporaryRoot,
            isolateExtractionRecovery: true,
            cancellationToken);
    }

    private static async Task<ArchiveClient> StartCoreAsync(
        string workerPath,
        string? workerTemporaryRoot,
        bool isolateExtractionRecovery,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        var canonicalWorker = Path.GetFullPath(workerPath);
        if (!File.Exists(canonicalWorker))
        {
            throw new FileNotFoundException("Rain Archives worker executable was not found.", canonicalWorker);
        }

        var pipeName = $"rainarchives-{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Convert.ToHexString(nonceBytes).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(nonceBytes);
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            64 * 1024,
            64 * 1024);
        using var bootstrap = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);

        var startInfo = new ProcessStartInfo
        {
            FileName = canonicalWorker,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(canonicalWorker)!,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--bootstrap-handle");
        startInfo.ArgumentList.Add(bootstrap.GetClientHandleAsString());
        if (workerTemporaryRoot is not null)
        {
            // Integration tests use a pre-created disposable root so hard-kill
            // residue can be attributed without inspecting or cleaning general TEMP.
            startInfo.Environment["TEMP"] = workerTemporaryRoot;
            startInfo.Environment["TMP"] = workerTemporaryRoot;
            startInfo.ArgumentList.Add("--creation-recovery-root");
            startInfo.ArgumentList.Add(Path.Combine(
                workerTemporaryRoot,
                "rainarchives-creation-recovery"));
            if (isolateExtractionRecovery)
            {
                startInfo.ArgumentList.Add("--extraction-recovery-root");
                startInfo.ArgumentList.Add(Path.Combine(
                    workerTemporaryRoot,
                    "rainarchives-extraction-recovery"));
            }
        }

        Process? worker = null;
        WorkerJob? job = null;
        try
        {
            worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Rain Archives worker.");
            bootstrap.DisposeLocalCopyOfClientHandle();
            job = new WorkerJob(worker, (nuint)(2UL * 1024 * 1024 * 1024));
            try
            {
                worker.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (InvalidOperationException)
            {
                // The process may have exited before its optional priority could be adjusted.
            }

            await using (var writer = new StreamWriter(bootstrap, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
            {
                await writer.WriteLineAsync(nonce.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (GetConnectedProcessId(pipe.SafePipeHandle) != worker.Id)
            {
                throw new InvalidDataException("The process connected to the worker pipe was not the launched worker.");
            }

            var helloFrame = await ProtocolStream.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (helloFrame.Type != ProtocolFrameType.Json
                || helloFrame.Json?.Kind != ProtocolMessageKinds.WorkerHello
                || helloFrame.Json.RequestId != Guid.Empty
                || helloFrame.Json.Sequence != 0)
            {
                throw new InvalidDataException("Worker handshake frame was invalid.");
            }

            var hello = helloFrame.Json.ReadPayload<WorkerHelloRequest>();
            if (string.IsNullOrEmpty(hello.BootstrapNonce)
                || !BootstrapNonceMatches(hello.BootstrapNonce, nonce))
            {
                throw new InvalidDataException("Worker bootstrap proof was invalid.");
            }

            if (hello.ProtocolMajor != ProtocolConstants.MajorVersion
                || hello.ProtocolMinor < 0
                || string.IsNullOrWhiteSpace(hello.WorkerBuild)
                || hello.WorkerBuild.Length > 128)
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.major_mismatch",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "Client and worker protocol major versions do not match."));
            }

            if (hello.Features is null
                || hello.Features.Count > 64
                || hello.Features.Any(string.IsNullOrWhiteSpace)
                || hello.Features.Distinct(StringComparer.Ordinal).Count() != hello.Features.Count)
            {
                throw new InvalidDataException("Worker handshake features were missing.");
            }

            var workerFeatures = hello.Features.ToHashSet(StringComparer.Ordinal);
            if (ProtocolFeatures.RequiredVersion1.Any(feature => !workerFeatures.Contains(feature)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.required_feature_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker does not support every required protocol feature."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryInspection)
                && !workerFeatures.Contains(ProtocolFeatures.RequestCancellation))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised recovery inspection without its required cancellation feature."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryDisposition)
                && (!workerFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryInspection)
                    || !workerFeatures.Contains(ProtocolFeatures.RequestCancellation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised recovery disposition without its required inspection and cancellation features."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.NestedWorkspaceRecovery)
                && !workerFeatures.Contains(ProtocolFeatures.RequestCancellation))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised nested-workspace recovery without its required cancellation feature."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
                && (!workerFeatures.Contains(ProtocolFeatures.RequestCancellation)
                    || !workerFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
                    || !workerFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipCreation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised ZIP Rebuild Copy without its required cancellation, persistent-key, volume-identity, and ZIP-creation features."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
                && (!workerFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
                    || !workerFeatures.Contains(ProtocolFeatures.RequestCancellation)
                    || !workerFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
                    || !workerFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipCreation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised ZIP Rebuild Rename without its required Rebuild Copy, cancellation, persistent-key, volume-identity, and ZIP-creation features."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
                && (!workerFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation)
                    || !workerFeatures.Contains(ProtocolFeatures.RequestCancellation)
                    || !workerFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
                    || !workerFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipCreation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised ZIP Rebuild Tree Edit without its required tree, rename, copy, directory-creation, cancellation, persistent-key, volume-identity, and ZIP-creation features."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
                && (!workerFeatures.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation)
                    || !workerFeatures.Contains(ProtocolFeatures.RequestCancellation)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipCreation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised ZIP Rebuild source-folder review without its required tree-edit, directory-creation, cancellation, and ZIP-creation features."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ZipRebuildMetadata)
                && (!workerFeatures.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation)
                    || !workerFeatures.Contains(ProtocolFeatures.RequestCancellation)
                    || !workerFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
                    || !workerFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipCreation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised ZIP Rebuild Metadata without all preceding rebuild and transport dependencies."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ZipWriteCapabilities)
                && (!workerFeatures.Contains(ProtocolFeatures.ZipRebuildMetadata)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation)
                    || !workerFeatures.Contains(ProtocolFeatures.RequestCancellation)
                    || !workerFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
                    || !workerFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
                    || !workerFeatures.Contains(ProtocolFeatures.ZipCreation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised ZIP write capabilities without all preceding rebuild and transport dependencies."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.TarCreation)
                && !workerFeatures.Contains(ProtocolFeatures.RequestCancellation))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised TAR creation without request cancellation."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.TarDirectoryCreation)
                && (!workerFeatures.Contains(ProtocolFeatures.TarCreation)
                    || !workerFeatures.Contains(ProtocolFeatures.RequestCancellation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised TAR directory creation without TAR creation and request cancellation."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.TarWriteCapabilities)
                && (!workerFeatures.Contains(ProtocolFeatures.TarCreation)
                    || !workerFeatures.Contains(ProtocolFeatures.TarDirectoryCreation)
                    || !workerFeatures.Contains(ProtocolFeatures.RequestCancellation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised TAR write capabilities without both TAR creation routes and request cancellation."));
            }

            ValidateManagedCreationFeatureDependencies(workerFeatures);

            if (workerFeatures.Contains(ProtocolFeatures.OperationActivity)
                && ProtocolFeatures.OperationActivityDependencies.Any(
                    dependency => !workerFeatures.Contains(dependency)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised operation activity without operation events and request cancellation."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ExtractionRecoveryInspection)
                && ProtocolFeatures.ExtractionRecoveryInspectionDependencies.Any(
                    dependency => !workerFeatures.Contains(dependency)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised extraction recovery inspection without new-destination extraction, source validation, and request cancellation."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.NestedWorkspaceIndependentReopen)
                && ProtocolFeatures.NestedWorkspaceIndependentReopenDependencies.Any(
                    dependency => !workerFeatures.Contains(dependency)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised independent nested-workspace reopen without all recovery, nested-open, secret, cancellation, and source-validation dependencies."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.SupportDiagnostics)
                && ProtocolFeatures.SupportDiagnosticsDependencies.Any(
                    dependency => !workerFeatures.Contains(dependency)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised support diagnostics without operation activity and request cancellation."));
            }

            if (workerFeatures.Contains(ProtocolFeatures.ExpandedCreationTreeBudget)
                && ProtocolFeatures.ExpandedCreationTreeBudgetDependencies.Any(
                    dependency => !workerFeatures.Contains(dependency)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    "The archive worker advertised the expanded creation-tree budget without cancellation and source validation."));
            }

            var negotiatedMinor = Math.Min(ProtocolConstants.MinorVersion, hello.ProtocolMinor);
            var enabledFeatures = ProtocolFeatures
                .SupportedForMinorVersion(negotiatedMinor)
                .Where(workerFeatures.Contains)
                .ToArray();

            var clientBuild = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            await ProtocolStream.WriteJsonAsync(
                pipe,
                ProtocolMessageKinds.ClientWelcome,
                Guid.Empty,
                new ClientWelcomeResponse(
                    ProtocolConstants.MajorVersion,
                    negotiatedMinor,
                    clientBuild,
                    enabledFeatures),
                0,
                timeout.Token).ConfigureAwait(false);

            return new ArchiveClient(
                pipe,
                worker,
                job,
                enabledFeatures.ToHashSet(StringComparer.Ordinal),
                negotiatedMinor);
        }
        catch
        {
            job?.Dispose();
            if (worker is { HasExited: false })
            {
                worker.Kill(entireProcessTree: true);
            }

            worker?.Dispose();
            pipe.Dispose();
            throw;
        }
    }

    private static void ValidateManagedCreationFeatureDependencies(IReadOnlySet<string> features)
    {
        var dependencySets = new (string Feature, IReadOnlyList<string> Dependencies, string Name)[]
        {
            (ProtocolFeatures.TarGZipCreation,
                ProtocolFeatures.TarGZipCreationDependencies, "TAR.GZ creation"),
            (ProtocolFeatures.TarGZipDirectoryCreation,
                ProtocolFeatures.TarGZipDirectoryCreationDependencies, "TAR.GZ directory creation"),
            (ProtocolFeatures.TarBZip2Creation,
                ProtocolFeatures.TarBZip2CreationDependencies, "TAR.BZ2 creation"),
            (ProtocolFeatures.TarBZip2DirectoryCreation,
                ProtocolFeatures.TarBZip2DirectoryCreationDependencies, "TAR.BZ2 directory creation"),
            (ProtocolFeatures.GZipCreation,
                ProtocolFeatures.GZipCreationDependencies, "GZIP creation"),
            (ProtocolFeatures.BZip2Creation,
                ProtocolFeatures.BZip2CreationDependencies, "BZIP2 creation"),
            (ProtocolFeatures.ManagedWriteCapabilities,
                ProtocolFeatures.ManagedWriteCapabilitiesDependencies, "managed write capabilities"),
        };
        foreach (var (feature, dependencies, name) in dependencySets)
        {
            if (features.Contains(feature)
                && dependencies.Any(dependency => !features.Contains(dependency)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "protocol.feature_dependency_missing",
                    ArchiveErrorCategory.ProtocolMismatch,
                    $"The archive worker advertised {name} without every required dependency."));
            }
        }
    }

    public async Task<Guid> CreateSecretAsync(
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        var byteCount = Encoding.UTF8.GetByteCount(secret.Span);
        if (byteCount > ProtocolConstants.MaximumSecretBytes)
        {
            throw new ArgumentException(
                $"Secrets cannot exceed {ProtocolConstants.MaximumSecretBytes:N0} UTF-8 bytes.",
                nameof(secret));
        }

        var slot = await SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest(),
            cancellationToken,
            finishResponseAfterWrite: true).ConfigureAwait(false);
        if (slot.SlotId == Guid.Empty || slot.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw ProtocolFailure("The worker returned an invalid secret slot.");
        }

        var bytes = new byte[byteCount];
        Encoding.UTF8.GetBytes(secret.Span, bytes);
        var submitted = false;
        try
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            await _writeGate.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                ThrowIfUnavailable();
                try
                {
                    await ProtocolStream.WriteSecretAsync(_pipe, slot.SlotId, bytes, _shutdown.Token).ConfigureAwait(false);
                    submitted = true;
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    throw RecordTransportFailure(exception);
                }
            }
            finally
            {
                _writeGate.Release();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await TryDiscardSecretSlotAsync(slot.SlotId).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
        }
        catch
        {
            if (!submitted || cancellationToken.IsCancellationRequested)
            {
                await TryDiscardSecretSlotAsync(slot.SlotId).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return slot.SlotId;
    }

    public async Task<bool> DiscardSecretAsync(
        Guid slotId,
        CancellationToken cancellationToken = default)
    {
        if (slotId == Guid.Empty)
        {
            throw new ArgumentException("Secret slot identifier cannot be empty.", nameof(slotId));
        }

        var response = await SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(slotId),
            cancellationToken,
            finishResponseAfterWrite: true,
            abortTransportOnResponseCancellation: true).ConfigureAwait(false);
        return response.Discarded;
    }

    public async Task<OpenArchiveResponse> OpenArchiveAsync(
        string sourcePath,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default)
    {
        OpenArchiveResponse response;
        try
        {
            ValidatePathArgument(sourcePath, nameof(sourcePath));
            response = await SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
                ProtocolMessageKinds.OpenArchive,
                new OpenArchiveRequest(sourcePath, passwordSlotId),
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true).ConfigureAwait(false);

            ValidateArchiveDescriptor(response.Archive);
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled or ArchiveErrorCategory.WorkerFailure)
        {
            // A Worker response is sent only after OpenArchive has settled its slot. A transport
            // failure instead closes the client-owned Job and lets Worker shutdown clear all slots.
            // Sending a second discard here can queue behind the cancelled indexing work and turn
            // a successful remote cancellation into an unnecessary transport-abort timeout.
            throw new OperationCanceledException(
                "Opening the archive was cancelled.",
                exception,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (passwordSlotId is { } cancelledSlot)
            {
                await TryDiscardSecretSlotAsync(cancelledSlot).ConfigureAwait(false);
            }

            throw;
        }
        catch
        {
            if (passwordSlotId is { } failedSlot)
            {
                await TryDiscardSecretSlotAsync(failedSlot).ConfigureAwait(false);
            }

            throw;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                _ = await SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
                    ProtocolMessageKinds.CloseArchive,
                    new CloseArchiveRequest(response.Archive.SessionId),
                    closeTimeout.Token,
                    finishResponseAfterWrite: true,
                    abortTransportOnResponseCancellation: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!_shutdown.IsCancellationRequested)
            {
                AbortTransport(new TimeoutException(
                    "The worker did not acknowledge cancellation-race session cleanup in time.",
                    exception));
            }
            catch (Exception exception) when (
                exception is ArchiveClientException or OperationCanceledException or ObjectDisposedException)
            {
                // Worker shutdown remains the final cleanup boundary if compensation cannot be acknowledged.
            }

            if (passwordSlotId is { } cancelledSlot)
            {
                await TryDiscardSecretSlotAsync(cancelledSlot).ConfigureAwait(false);
            }

            throw new OperationCanceledException(cancellationToken);
        }

        return response;
    }

    public async Task<OpenNestedArchiveResponse> OpenNestedArchiveAsync(
        ArchiveSessionId parentSessionId,
        EntryId entryId,
        long maximumBytes = ProtocolConstants.DefaultNestedArchiveBytes,
        Guid? parentPasswordSlotId = null,
        Guid? nestedPasswordSlotId = null,
        CancellationToken cancellationToken = default)
    {
        var sessionGateHeld = false;
        OpenNestedArchiveResponse? response = null;
        try
        {
            ValidateSessionId(parentSessionId);
            if (entryId.Value == Guid.Empty)
            {
                throw new ArgumentException("Archive entry identifier cannot be empty.", nameof(entryId));
            }

            if (maximumBytes is <= 0 or > ProtocolConstants.MaximumNestedArchiveBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumBytes),
                    $"Nested archive byte limits must be between 1 and {ProtocolConstants.MaximumNestedArchiveBytes:N0} bytes.");
            }

            if (parentPasswordSlotId == Guid.Empty
                || nestedPasswordSlotId == Guid.Empty
                || (parentPasswordSlotId is { } parentSlot && nestedPasswordSlotId == parentSlot))
            {
                throw new ArgumentException("Nested archive secret slots must be non-empty and distinct.");
            }

            if (!_enabledFeatures.Contains(ProtocolFeatures.NestedArchiveOpen))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "nested.feature_not_negotiated",
                    ArchiveErrorCategory.UnsupportedCapability,
                    "The connected archive worker does not support bounded nested archive opening."));
            }

            await _sessionLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            sessionGateHeld = true;
            response = await SendAsync<OpenNestedArchiveRequest, OpenNestedArchiveResponse>(
                ProtocolMessageKinds.OpenNestedArchive,
                new OpenNestedArchiveRequest(
                    parentSessionId,
                    entryId,
                    maximumBytes,
                    parentPasswordSlotId,
                    nestedPasswordSlotId),
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true).ConfigureAwait(false);
            ValidateNestedArchiveResponse(response, parentSessionId, entryId, maximumBytes);

            if (cancellationToken.IsCancellationRequested)
            {
                await CompensateOpenedSessionAsync(
                    response.Archive.SessionId,
                    "The worker did not acknowledge nested-open cancellation-race cleanup in time.")
                    .ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }

            if (!_nestedParents.TryAdd(response.Archive.SessionId, parentSessionId))
            {
                await CompensateOpenedSessionAsync(
                    response.Archive.SessionId,
                    "The worker reused a nested archive session identifier.")
                    .ConfigureAwait(false);
                throw ProtocolFailure("The worker reused a nested archive session identifier.");
            }

            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Opening the nested archive was cancelled.",
                exception,
                cancellationToken);
        }
        finally
        {
            if (response is null)
            {
                await TryDiscardNestedSecretSlotsAsync(
                    parentPasswordSlotId,
                    nestedPasswordSlotId).ConfigureAwait(false);
            }

            if (sessionGateHeld)
            {
                _sessionLifecycleGate.Release();
            }
        }
    }

    public async Task<ListEntriesResponse> ListEntriesAsync(
        ArchiveSessionId sessionId,
        int offset = 0,
        int limit = 512,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        if (offset < 0 || limit is <= 0 or > ProtocolConstants.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Entry page bounds are invalid.");
        }

        var response = await SendAsync<ListEntriesRequest, ListEntriesResponse>(
            ProtocolMessageKinds.ListEntries,
            new ListEntriesRequest(sessionId, offset, limit),
            cancellationToken).ConfigureAwait(false);
        ValidateEntryPage(response, offset, limit);
        return response;
    }

    public async Task<ArchiveEntryStreamLease> OpenEntryStreamAsync(
        ArchiveSessionId sessionId,
        EntryId entryId,
        long maximumBytes = ProtocolConstants.DefaultEntryStreamBytes,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default)
    {
        NamedPipeServerStream? dataPipe = null;
        byte[]? bootstrapNonce = null;
        var streamSlotHeld = false;
        var leaseRegistered = false;
        try
        {
            ValidateSessionId(sessionId);
            if (entryId.Value == Guid.Empty)
            {
                throw new ArgumentException("Archive entry identifier cannot be empty.", nameof(entryId));
            }

            if (maximumBytes is <= 0 or > ProtocolConstants.MaximumEntryStreamBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumBytes),
                    $"Entry stream byte limits must be between 1 and {ProtocolConstants.MaximumEntryStreamBytes:N0} bytes.");
            }

            if (!_enabledFeatures.Contains(ProtocolFeatures.EntryStreamLeases))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "entry.stream_feature_not_negotiated",
                    ArchiveErrorCategory.UnsupportedCapability,
                    "The connected archive worker does not support entry preview streams."));
            }

            ThrowIfUnavailable();
            if (!_entryStreamSlots.Wait(0))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "entry.stream_limit",
                    ArchiveErrorCategory.ResourceLimit,
                    $"A client can keep at most {ProtocolConstants.MaximumEntryStreamLeases} entry streams open."));
            }

            streamSlotHeld = true;
            var pipeName = $"rainarchives-entry-{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
            bootstrapNonce = RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes);
            var bootstrapProof = Convert.ToHexString(bootstrapNonce).ToLowerInvariant();
            dataPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                ProtocolConstants.MaximumEntryStreamFrameBytes,
                ProtocolConstants.MaximumEntryStreamFrameBytes);

            using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            setup.CancelAfter(TimeSpan.FromSeconds(10));
            var connectionTask = dataPipe.WaitForConnectionAsync(setup.Token);
            var response = await SendAsync<OpenEntryStreamRequest, OpenEntryStreamResponse>(
                ProtocolMessageKinds.OpenEntryStream,
                new OpenEntryStreamRequest(
                    sessionId,
                    entryId,
                    maximumBytes,
                    pipeName,
                    bootstrapProof,
                    passwordSlotId),
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true).ConfigureAwait(false);

            if (response.StreamId.Value == Guid.Empty
                || response.EntryId != entryId
                || response.DeclaredLength is < 0
                || response.DeclaredLength > maximumBytes)
            {
                throw ProtocolFailure("The worker returned an invalid entry stream lease response.");
            }

            if (_seenEntryStreamIds.Count >= ProtocolConstants.MaximumRequestsPerConnection
                || !_seenEntryStreamIds.TryAdd(response.StreamId, 0))
            {
                throw ProtocolFailure("The worker reused an entry stream identifier.");
            }

            try
            {
                await connectionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                !cancellationToken.IsCancellationRequested
                && !_shutdown.IsCancellationRequested)
            {
                throw AbortTransport(new TimeoutException(
                    "The worker did not connect the entry stream data pipe in time.",
                    exception));
            }

            uint connectedProcessId;
            try
            {
                connectedProcessId = GetConnectedProcessId(dataPipe.SafePipeHandle);
            }
            catch (InvalidOperationException exception)
            {
                throw AbortTransport(exception);
            }

            if (connectedProcessId != _worker.Id)
            {
                throw AbortTransport(new InvalidDataException(
                    "The process connected to the entry stream pipe was not the launched worker."));
            }

            EntryStreamFrame hello;
            try
            {
                hello = await EntryStreamPipeProtocol.ReadAsync(dataPipe, setup.Token).ConfigureAwait(false);
                EntryStreamPipeProtocol.ValidateHello(hello, response.StreamId, bootstrapNonce);
            }
            catch (OperationCanceledException exception) when (
                !cancellationToken.IsCancellationRequested
                && !_shutdown.IsCancellationRequested)
            {
                throw AbortTransport(new TimeoutException(
                    "The worker did not authenticate the entry stream data pipe in time.",
                    exception));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                throw AbortTransport(exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
            var lease = new ArchiveEntryStreamLease(
                sessionId,
                response.StreamId,
                entryId,
                response.DeclaredLength,
                maximumBytes,
                dataPipe,
                AbortTransport,
                CancelEntryStreamLeaseAsync,
                ReleaseEntryStream);
            if (!_entryStreams.TryAdd(response.StreamId, lease))
            {
                lease.Dispose();
                throw ProtocolFailure("The worker reused an active entry stream identifier.");
            }

            leaseRegistered = true;
            streamSlotHeld = false;
            dataPipe = null;
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ArchiveClient));
            }

            return lease;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Opening the archive entry stream was cancelled.",
                exception,
                cancellationToken);
        }
        finally
        {
            if (!leaseRegistered && passwordSlotId is { } unsettledPasswordSlot)
            {
                await TryDiscardSecretSlotAsync(unsettledPasswordSlot).ConfigureAwait(false);
            }

            if (bootstrapNonce is not null)
            {
                CryptographicOperations.ZeroMemory(bootstrapNonce);
            }

            dataPipe?.Dispose();
            if (streamSlotHeld)
            {
                _entryStreamSlots.Release();
            }
        }
    }

    public async Task<CloseArchiveResponse> CloseArchiveAsync(
        ArchiveSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        await _sessionLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var descendant in GetNestedDescendantsDeepestFirst(sessionId))
            {
                _ = await CloseSingleArchiveAsync(descendant, cancellationToken).ConfigureAwait(false);
                _nestedParents.TryRemove(descendant, out _);
            }

            var response = await CloseSingleArchiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            _nestedParents.TryRemove(sessionId, out _);
            return response;
        }
        finally
        {
            _sessionLifecycleGate.Release();
        }
    }

    private async Task<CloseArchiveResponse> CloseSingleArchiveAsync(
        ArchiveSessionId sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var lease in _entryStreams.Values
                     .Where(item => item.SessionId == sessionId)
                     .ToArray())
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        var response = await SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
            ProtocolMessageKinds.CloseArchive,
            new CloseArchiveRequest(sessionId),
            cancellationToken,
            finishResponseAfterWrite: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<StartOperationResponse> StartExtractionAsync(
        ArchiveSessionId sessionId,
        IReadOnlyList<EntryId> entryIds,
        string destinationPath,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateSessionId(sessionId);
            ValidateEntrySelection(entryIds);
            ValidatePathArgument(destinationPath, nameof(destinationPath));
        }
        catch
        {
            if (passwordSlotId is { } invalidRequestSlot)
            {
                await TryDiscardSecretSlotAsync(invalidRequestSlot).ConfigureAwait(false);
            }

            throw;
        }

        return await StartOperationAsync(
            ProtocolMessageKinds.StartExtraction,
            new StartExtractionRequest(sessionId, entryIds, destinationPath, passwordSlotId),
            passwordSlotId,
            cancellationToken);
    }

    public async Task<StartOperationResponse> StartIntegrityTestAsync(
        ArchiveSessionId sessionId,
        IReadOnlyList<EntryId> entryIds,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateSessionId(sessionId);
            ValidateEntrySelection(entryIds);
        }
        catch
        {
            if (passwordSlotId is { } invalidRequestSlot)
            {
                await TryDiscardSecretSlotAsync(invalidRequestSlot).ConfigureAwait(false);
            }

            throw;
        }

        return await StartOperationAsync(
            ProtocolMessageKinds.StartIntegrityTest,
            new StartIntegrityTestRequest(sessionId, entryIds, passwordSlotId),
            passwordSlotId,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartZipCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(destinationPath)
            || files.Count > ProtocolConstants.MaximumZipCreationFiles
            || !Enum.IsDefined(compressionPreset)
            || files.Any(file =>
                file is null
                || string.IsNullOrWhiteSpace(file.SourcePath)
                || file.SourcePath.Length > ProtocolConstants.MaximumPathCharacters
                || !Path.IsPathFullyQualified(file.SourcePath)
                || string.IsNullOrWhiteSpace(file.ArchivePath)
                || file.ArchivePath.Length > ProtocolConstants.MaximumPathCharacters))
        {
            throw new ArgumentException(
                "ZIP creation files or compression options are invalid.",
                nameof(files));
        }

        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP creation."));
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartZipCreation,
            new StartZipCreationRequest(destinationPath, files.ToArray(), compressionPreset),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartZipTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);
        if (!Path.IsPathFullyQualified(destinationPath)
            || directories.Count == 0
            || !TryCountWithinCreationLimit(files.Count, directories.Count)
            || !Enum.IsDefined(compressionPreset)
            || files.Any(file => !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath))
            || directories.Any(directory =>
                !IsValidCreationMapping(directory?.SourcePath, directory?.ArchivePath)))
        {
            throw new ArgumentException(
                "ZIP tree creation files, directories, or compression options are invalid.",
                nameof(directories));
        }

        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.directory_creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP directory creation."));
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartZipTreeCreation,
            new StartZipTreeCreationRequest(
                destinationPath,
                files.ToArray(),
                directories.ToArray(),
                compressionPreset),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartTarCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        CancellationToken cancellationToken = default)
    {
        EnsureTarCreationFeature();
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(destinationPath)
            || files.Count > ProtocolConstants.MaximumTarCreationFiles
            || files.Any(file =>
                !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath)))
        {
            throw new ArgumentException(
                "TAR creation files are invalid.",
                nameof(files));
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartTarCreation,
            new StartTarCreationRequest(
                destinationPath,
                files.ToArray(),
                ArchiveCompressionPreset.Store),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartTarTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        CancellationToken cancellationToken = default)
    {
        EnsureTarDirectoryCreationFeature();
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);
        if (!Path.IsPathFullyQualified(destinationPath)
            || directories.Count == 0
            || !TryCountWithinCreationLimit(
                files.Count,
                directories.Count,
                ProtocolConstants.MaximumTarCreationEntries)
            || files.Any(file =>
                !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath))
            || directories.Any(directory =>
                !IsValidCreationMapping(directory?.SourcePath, directory?.ArchivePath)))
        {
            throw new ArgumentException(
                "TAR tree creation files or directories are invalid.",
                nameof(directories));
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartTarTreeCreation,
            new StartTarTreeCreationRequest(
                destinationPath,
                files.ToArray(),
                directories.ToArray(),
                ArchiveCompressionPreset.Store),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartRarCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        EnsureRarCreationFeature();
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(destinationPath)
            || !destinationPath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || files.Count == 0
            || files.Count > ProtocolConstants.MaximumRarCreationFiles
            || files.Any(file =>
                !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath))
            || !Enum.IsDefined(compressionPreset))
        {
            throw new ArgumentException(
                "RAR creation files are invalid.",
                nameof(files));
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartRarCreation,
            new StartRarCreationRequest(
                destinationPath,
                files.ToArray(),
                compressionPreset),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartRarTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        EnsureRarDirectoryCreationFeature();
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);
        if (!Path.IsPathFullyQualified(destinationPath)
            || !destinationPath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || directories.Count == 0
            || !TryCountWithinCreationLimit(
                files.Count,
                directories.Count,
                ProtocolConstants.MaximumRarCreationEntries)
            || files.Any(file =>
                !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath))
            || directories.Any(directory =>
                !IsValidCreationMapping(directory?.SourcePath, directory?.ArchivePath))
            || !Enum.IsDefined(compressionPreset))
        {
            throw new ArgumentException(
                "RAR tree creation files or directories are invalid.",
                nameof(directories));
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartRarTreeCreation,
            new StartRarTreeCreationRequest(
                destinationPath,
                files.ToArray(),
                directories.ToArray(),
                compressionPreset),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartTarGZipCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.TarGZipCreation,
            ProtocolFeatures.TarGZipCreationDependencies,
            "TAR.GZ creation");
        ValidateManagedTarCreationArguments(
            destinationPath,
            files,
            compressionPreset,
            gzip: true);
        return StartOperationAsync(
            ProtocolMessageKinds.StartTarGZipCreation,
            new StartTarGZipCreationRequest(destinationPath, files.ToArray(), compressionPreset),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartTarGZipTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.TarGZipDirectoryCreation,
            ProtocolFeatures.TarGZipDirectoryCreationDependencies,
            "TAR.GZ directory creation");
        ValidateManagedTarTreeCreationArguments(
            destinationPath,
            files,
            directories,
            compressionPreset,
            gzip: true);
        return StartOperationAsync(
            ProtocolMessageKinds.StartTarGZipTreeCreation,
            new StartTarGZipTreeCreationRequest(
                destinationPath,
                files.ToArray(),
                directories.ToArray(),
                compressionPreset),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartTarBZip2CreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.TarBZip2Creation,
            ProtocolFeatures.TarBZip2CreationDependencies,
            "TAR.BZ2 creation");
        ValidateManagedTarCreationArguments(
            destinationPath,
            files,
            compressionPreset,
            gzip: false);
        return StartOperationAsync(
            ProtocolMessageKinds.StartTarBZip2Creation,
            new StartTarBZip2CreationRequest(destinationPath, files.ToArray(), compressionPreset),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartTarBZip2TreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.TarBZip2DirectoryCreation,
            ProtocolFeatures.TarBZip2DirectoryCreationDependencies,
            "TAR.BZ2 directory creation");
        ValidateManagedTarTreeCreationArguments(
            destinationPath,
            files,
            directories,
            compressionPreset,
            gzip: false);
        return StartOperationAsync(
            ProtocolMessageKinds.StartTarBZip2TreeCreation,
            new StartTarBZip2TreeCreationRequest(
                destinationPath,
                files.ToArray(),
                directories.ToArray(),
                compressionPreset),
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartGZipCreationAsync(
        string destinationPath,
        string sourcePath,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        StartRawCreationAsync(
            destinationPath,
            sourcePath,
            compressionPreset,
            gzip: true,
            cancellationToken);

    public Task<StartOperationResponse> StartBZip2CreationAsync(
        string destinationPath,
        string sourcePath,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        StartRawCreationAsync(
            destinationPath,
            sourcePath,
            compressionPreset,
            gzip: false,
            cancellationToken);

    public Task<StartOperationResponse> StartZipRebuildCopyAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(entriesToDelete);
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(destinationPath)
            || entriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || files.Count > ProtocolConstants.MaximumZipCreationEntries
            || !Enum.IsDefined(compressionPreset)
            || entriesToDelete.Any(entry => !IsValidRebuildEntryKey(entry))
            || entriesToDelete.Distinct().Count() != entriesToDelete.Count
            || files.Any(file => !IsValidRebuildMapping(file)))
        {
            throw new ArgumentException(
                "ZIP Rebuild Copy contains invalid session keys, paths, mappings, or compression options.");
        }

        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildCopy))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.rebuild_copy_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP Rebuild Copy."));
        }

        var request = new StartZipRebuildCopyRequest(
            sessionId,
            destinationPath,
            entriesToDelete.ToArray(),
            files.ToArray(),
            compressionPreset);
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.StartZipRebuildCopy,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "ZIP Rebuild Copy request metadata exceeds the bounded protocol frame size.",
                nameof(files),
                exception);
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartZipRebuildCopy,
            request,
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartZipRebuildRenameAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(entriesToDelete);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(renames);
        if (!Path.IsPathFullyQualified(destinationPath)
            || entriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || files.Count > ProtocolConstants.MaximumZipCreationEntries
            || renames.Count > ProtocolConstants.MaximumZipCreationEntries
            || !Enum.IsDefined(compressionPreset)
            || entriesToDelete.Any(entry => !IsValidRebuildEntryKey(entry))
            || entriesToDelete.Distinct().Count() != entriesToDelete.Count
            || files.Any(file => !IsValidRebuildMapping(file))
            || renames.Any(rename => !IsValidRebuildRename(rename))
            || renames.Select(rename => rename.EntryKey).Distinct().Count() != renames.Count
            || renames.Select(rename => rename.EntryKey).Intersect(entriesToDelete).Any()
            || renames.Select(rename => ArchivePathRules.GetCollisionKey(rename.FinalArchivePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != renames.Count)
        {
            throw new ArgumentException(
                "ZIP Rebuild Rename contains invalid session keys, paths, mappings, counts, or compression options.");
        }

        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation)
            || !_enabledFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
            || !_enabledFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.rebuild_rename_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP Rebuild Rename."));
        }

        var request = new StartZipRebuildRenameRequest(
            sessionId,
            destinationPath,
            entriesToDelete.ToArray(),
            files.ToArray(),
            renames.ToArray(),
            compressionPreset);
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.StartZipRebuildRename,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "ZIP Rebuild Rename request metadata exceeds the bounded protocol frame size.",
                nameof(files),
                exception);
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartZipRebuildRename,
            request,
            passwordSlotId: null,
            cancellationToken);
    }

    public async Task<GetZipRebuildTreePlanResponse> GetZipRebuildTreePlanAsync(
        ArchiveSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        EnsureZipRebuildTreeEditFeature();
        var response = await SendAsync<
                GetZipRebuildTreePlanRequest,
                GetZipRebuildTreePlanResponse>(
                ProtocolMessageKinds.GetZipRebuildTreePlan,
                new GetZipRebuildTreePlanRequest(sessionId),
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        ValidateTreePlanResponse(response);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<GetZipRebuildSourceFolderReviewResponse> GetZipRebuildSourceFolderReviewAsync(
        ArchiveSessionId sessionId,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(directories);
        if (directories.Count == 0
            || directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots
            || directories.Any(directory => !IsValidDirectoryMapping(directory)))
        {
            throw new ArgumentException(
                "ZIP Rebuild source-folder review contains invalid roots or counts.",
                nameof(directories));
        }

        EnsureZipRebuildSourceFolderReviewFeature();
        var request = new GetZipRebuildSourceFolderReviewRequest(
            sessionId,
            directories.ToArray());
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetZipRebuildSourceFolderReview,
                request);
            if (!TryGetSourceFolderReviewPathPayloadBytes(directories, out var pathPayloadBytes)
                || pathPayloadBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
            {
                throw new ArgumentException(
                    "ZIP Rebuild source-folder review exceeds the bounded UTF-8 path payload limit.",
                    nameof(directories));
            }
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "ZIP Rebuild source-folder review request metadata exceeds the bounded protocol frame size.",
                nameof(directories),
                exception);
        }

        var response = await SendAsync<
                GetZipRebuildSourceFolderReviewRequest,
                GetZipRebuildSourceFolderReviewResponse>(
                ProtocolMessageKinds.GetZipRebuildSourceFolderReview,
                request,
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        ValidateSourceFolderReviewResponse(response, directories);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public Task<StartOperationResponse> StartZipRebuildTreeEditAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(entriesToDelete);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(treeActions);
        if (!Path.IsPathFullyQualified(destinationPath)
            || entriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || files.Count > ProtocolConstants.MaximumZipCreationEntries
            || renames.Count > ProtocolConstants.MaximumZipCreationEntries
            || directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots
            || treeActions.Count > ProtocolConstants.MaximumZipRebuildTreeActions
            || !Enum.IsDefined(compressionPreset)
            || entriesToDelete.Any(entry => !IsValidRebuildEntryKey(entry))
            || entriesToDelete.Distinct().Count() != entriesToDelete.Count
            || files.Any(file => !IsValidRebuildMapping(file))
            || renames.Any(rename => !IsValidRebuildRename(rename))
            || renames.Select(rename => rename.EntryKey).Distinct().Count() != renames.Count
            || renames.Select(rename => rename.EntryKey).Intersect(entriesToDelete).Any()
            || renames.Select(rename => ArchivePathRules.GetCollisionKey(rename.FinalArchivePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != renames.Count
            || directories.Any(directory => !IsValidDirectoryMapping(directory))
            || treeActions.Any(action => !IsValidTreeAction(action)))
        {
            throw new ArgumentException(
                "ZIP Rebuild Tree Edit contains invalid session keys, paths, mappings, actions, counts, or compression options.");
        }

        var directiveCount = (long)entriesToDelete.Count
            + files.Count
            + renames.Count
            + directories.Count
            + treeActions.Count;
        if (directiveCount > ProtocolConstants.MaximumZipRebuildDirectives
            || !TryGetTreePathPayloadBytes(
                destinationPath,
                entriesToDelete,
                files,
                renames,
                directories,
                treeActions,
                out var pathPayloadBytes)
            || pathPayloadBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
        {
            throw new ArgumentException(
                "ZIP Rebuild Tree Edit exceeds the bounded directive or UTF-8 path payload limit.");
        }

        EnsureZipRebuildTreeEditFeature();
        var request = new StartZipRebuildTreeEditRequest(
            sessionId,
            destinationPath,
            entriesToDelete.ToArray(),
            files.ToArray(),
            renames.ToArray(),
            directories.ToArray(),
            treeActions.ToArray(),
            compressionPreset);
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.StartZipRebuildTreeEdit,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "ZIP Rebuild Tree Edit request metadata exceeds the bounded protocol frame size.",
                nameof(files),
                exception);
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartZipRebuildTreeEdit,
            request,
            passwordSlotId: null,
            cancellationToken);
    }

    public Task<StartOperationResponse> StartZipRebuildMetadataAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        IReadOnlyList<ArchiveRebuildMetadataDirective> metadata,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(entriesToDelete);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(treeActions);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!Path.IsPathFullyQualified(destinationPath)
            || entriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || files.Count > ProtocolConstants.MaximumZipCreationEntries
            || renames.Count > ProtocolConstants.MaximumZipCreationEntries
            || directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots
            || treeActions.Count > ProtocolConstants.MaximumZipRebuildTreeActions
            || metadata.Count > ProtocolConstants.MaximumZipRebuildMetadataDirectives
            || !Enum.IsDefined(compressionPreset)
            || entriesToDelete.Any(entry => !IsValidRebuildEntryKey(entry))
            || entriesToDelete.Distinct().Count() != entriesToDelete.Count
            || files.Any(file => !IsValidRebuildMapping(file))
            || renames.Any(rename => !IsValidRebuildRename(rename))
            || renames.Select(rename => rename.EntryKey).Distinct().Count() != renames.Count
            || renames.Select(rename => rename.EntryKey).Intersect(entriesToDelete).Any()
            || renames.Select(rename => ArchivePathRules.GetCollisionKey(rename.FinalArchivePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != renames.Count
            || directories.Any(directory => !IsValidDirectoryMapping(directory))
            || treeActions.Any(action => !IsValidTreeAction(action))
            || metadata.Any(directive => !IsValidMetadataDirective(directive))
            || metadata.Select(MetadataTargetIdentity).Distinct(StringComparer.Ordinal).Count() != metadata.Count)
        {
            throw new ArgumentException(
                "ZIP Rebuild Metadata contains invalid session keys, paths, actions, metadata, counts, or compression options.");
        }

        var directiveCount = (long)entriesToDelete.Count
            + files.Count
            + renames.Count
            + directories.Count
            + treeActions.Count
            + metadata.Count;
        if (directiveCount > ProtocolConstants.MaximumZipRebuildDirectives
            || !TryGetMetadataPathPayloadBytes(
                destinationPath,
                entriesToDelete,
                files,
                renames,
                directories,
                treeActions,
                metadata,
                out var pathPayloadBytes)
            || pathPayloadBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
        {
            throw new ArgumentException(
                "ZIP Rebuild Metadata exceeds the bounded directive or UTF-8 path payload limit.");
        }

        EnsureZipRebuildMetadataFeature();
        var request = new StartZipRebuildMetadataRequest(
            sessionId,
            destinationPath,
            entriesToDelete.ToArray(),
            files.ToArray(),
            renames.ToArray(),
            directories.ToArray(),
            treeActions.ToArray(),
            metadata.ToArray(),
            compressionPreset);
        try
        {
            ProtocolStream.ValidateJsonFrameSize(ProtocolMessageKinds.StartZipRebuildMetadata, request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "ZIP Rebuild Metadata request metadata exceeds the bounded protocol frame size.",
                nameof(metadata),
                exception);
        }

        return StartOperationAsync(
            ProtocolMessageKinds.StartZipRebuildMetadata,
            request,
            passwordSlotId: null,
            cancellationToken);
    }

    public async Task<GetZipWriteCapabilitiesResponse> GetZipWriteCapabilitiesAsync(
        ArchiveSessionId? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionId is { Value: var value } && value == Guid.Empty)
        {
            throw new ArgumentException("The optional archive session identifier cannot be empty.", nameof(sessionId));
        }

        EnsureZipWriteCapabilitiesFeature();
        var request = new GetZipWriteCapabilitiesRequest(sessionId);
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetZipWriteCapabilities,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "ZIP write capability request metadata exceeds the bounded protocol frame size.",
                nameof(sessionId),
                exception);
        }

        var response = await SendAsync<
                GetZipWriteCapabilitiesRequest,
                GetZipWriteCapabilitiesResponse>(
                ProtocolMessageKinds.GetZipWriteCapabilities,
                request,
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        ValidateZipWriteCapabilitiesResponse(response, request.SessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<GetTarWriteCapabilitiesResponse> GetTarWriteCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureTarWriteCapabilitiesFeature();
        var request = new GetTarWriteCapabilitiesRequest();
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetTarWriteCapabilities,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "TAR write capability request metadata exceeds the bounded protocol frame size.",
                nameof(cancellationToken),
                exception);
        }

        var response = await SendAsync<
                GetTarWriteCapabilitiesRequest,
                GetTarWriteCapabilitiesResponse>(
                ProtocolMessageKinds.GetTarWriteCapabilities,
                request,
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        ValidateTarWriteCapabilitiesResponse(response);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<GetRarWriteCapabilitiesResponse> GetRarWriteCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureRarWriteCapabilitiesFeature();
        var request = new GetRarWriteCapabilitiesRequest();
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetRarWriteCapabilities,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "RAR write capability request metadata exceeds the bounded protocol frame size.",
                nameof(cancellationToken),
                exception);
        }

        var response = await SendAsync<
                GetRarWriteCapabilitiesRequest,
                GetRarWriteCapabilitiesResponse>(
                ProtocolMessageKinds.GetRarWriteCapabilities,
                request,
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        ValidateRarWriteCapabilitiesResponse(response);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<GetManagedWriteCapabilitiesResponse> GetManagedWriteCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.ManagedWriteCapabilities,
            ProtocolFeatures.ManagedWriteCapabilitiesDependencies,
            "managed write capabilities");
        var request = new GetManagedWriteCapabilitiesRequest();
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetManagedWriteCapabilities,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "Managed write capability request metadata exceeds the bounded protocol frame size.",
                nameof(cancellationToken),
                exception);
        }

        var response = await SendAsync<
                GetManagedWriteCapabilitiesRequest,
                GetManagedWriteCapabilitiesResponse>(
                ProtocolMessageKinds.GetManagedWriteCapabilities,
                request,
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        ValidateManagedWriteCapabilitiesResponse(response);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<GetOperationActivityResponse> GetOperationActivityAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOperationActivityFeature();
        var request = new GetOperationActivityRequest();
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetOperationActivity,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "Operation activity request metadata exceeds the bounded protocol frame size.",
                nameof(cancellationToken),
                exception);
        }

        var response = await SendAsync<
                GetOperationActivityRequest,
                GetOperationActivityResponse>(
                ProtocolMessageKinds.GetOperationActivity,
                request,
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        ValidateOperationActivityResponse(response);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<GetSupportDiagnosticsResponse> GetSupportDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureSupportDiagnosticsFeature();
        var request = new GetSupportDiagnosticsRequest();
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetSupportDiagnostics,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                "Support diagnostics request metadata exceeds the bounded protocol frame size.",
                nameof(cancellationToken),
                exception);
        }

        var response = await SendAsync<
                GetSupportDiagnosticsRequest,
                GetSupportDiagnosticsResponse>(
                ProtocolMessageKinds.GetSupportDiagnostics,
                request,
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        ValidateSupportDiagnosticsResponse(response);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    private static bool TryCountWithinCreationLimit(
        int files,
        int directories,
        int maximumEntries = ProtocolConstants.MaximumZipCreationEntries)
    {
        try
        {
            return checked(files + directories) <= maximumEntries;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool IsValidCreationMapping(string? sourcePath, string? archivePath) =>
        !string.IsNullOrWhiteSpace(sourcePath)
        && sourcePath.Length <= ProtocolConstants.MaximumPathCharacters
        && Path.IsPathFullyQualified(sourcePath)
        && !string.IsNullOrWhiteSpace(archivePath)
        && archivePath.Length <= ProtocolConstants.MaximumPathCharacters;

    private static void ValidateManagedTarCreationArguments(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset,
        bool gzip)
    {
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(destinationPath)
            || files.Count > ProtocolConstants.MaximumTarCreationFiles
            || !IsSupportedManagedPreset(compressionPreset, gzip)
            || files.Any(file => !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath)))
        {
            throw new ArgumentException(
                "Compressed TAR creation files or compression options are invalid.",
                nameof(files));
        }
    }

    private static void ValidateManagedTarTreeCreationArguments(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset,
        bool gzip)
    {
        ValidateManagedTarCreationArguments(
            destinationPath,
            files,
            compressionPreset,
            gzip);
        ArgumentNullException.ThrowIfNull(directories);
        if (directories.Count == 0
            || !TryCountWithinCreationLimit(
                files.Count,
                directories.Count,
                ProtocolConstants.MaximumTarCreationEntries)
            || directories.Any(directory =>
                !IsValidCreationMapping(directory?.SourcePath, directory?.ArchivePath)))
        {
            throw new ArgumentException(
                "Compressed TAR directory inputs are invalid.",
                nameof(directories));
        }
    }

    private Task<StartOperationResponse> StartRawCreationAsync(
        string destinationPath,
        string sourcePath,
        ArchiveCompressionPreset compressionPreset,
        bool gzip,
        CancellationToken cancellationToken)
    {
        var feature = gzip ? ProtocolFeatures.GZipCreation : ProtocolFeatures.BZip2Creation;
        var dependencies = gzip
            ? ProtocolFeatures.GZipCreationDependencies
            : ProtocolFeatures.BZip2CreationDependencies;
        EnsureManagedCreationFeature(
            feature,
            dependencies,
            gzip ? "GZIP creation" : "BZIP2 creation");
        ValidatePathArgument(destinationPath, nameof(destinationPath));
        ValidatePathArgument(sourcePath, nameof(sourcePath));
        var suffixes = gzip
            ? new[] { ".gz", ".gzip" }
            : [".bz2", ".bzip2"];
        if (!Path.IsPathFullyQualified(destinationPath)
            || !Path.IsPathFullyQualified(sourcePath)
            || !IsSupportedManagedPreset(compressionPreset, gzip)
            || !suffixes.Any(suffix => destinationPath.EndsWith(
                suffix,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Raw {(gzip ? "GZIP" : "BZIP2")} creation requires a matching fully qualified destination and one ordinary source.",
                nameof(destinationPath));
        }

        return gzip
            ? StartOperationAsync(
                ProtocolMessageKinds.StartGZipCreation,
                new StartGZipCreationRequest(destinationPath, sourcePath, compressionPreset),
                passwordSlotId: null,
                cancellationToken)
            : StartOperationAsync(
                ProtocolMessageKinds.StartBZip2Creation,
                new StartBZip2CreationRequest(destinationPath, sourcePath, compressionPreset),
                passwordSlotId: null,
                cancellationToken);
    }

    private static bool IsSupportedManagedPreset(
        ArchiveCompressionPreset compressionPreset,
        bool gzip) =>
        gzip
            ? Enum.IsDefined(compressionPreset)
            : compressionPreset == ArchiveCompressionPreset.Balanced;

    private static bool IsValidRebuildEntryKey(ArchiveEntryKey? entry)
    {
        if (entry is null
            || string.IsNullOrWhiteSpace(entry.NormalizedPath)
            || entry.NormalizedPath.Length > ProtocolConstants.MaximumPathCharacters
            || entry.DuplicateOrdinal < 0)
        {
            return false;
        }

        var path = entry.NormalizedPath.EndsWith('/')
            ? entry.NormalizedPath[..^1]
            : entry.NormalizedPath;
        return ArchivePathRules.TryNormalize(path, out _, out _, out _);
    }

    private static bool IsValidRebuildMapping(ArchiveRebuildCopyFile? file) =>
        file is not null
        && !string.IsNullOrWhiteSpace(file.SourcePath)
        && file.SourcePath.Length <= ProtocolConstants.MaximumPathCharacters
        && Path.IsPathFullyQualified(file.SourcePath)
        && !string.IsNullOrWhiteSpace(file.ArchivePath)
        && file.ArchivePath.Length <= ProtocolConstants.MaximumPathCharacters
        && ArchivePathRules.TryNormalize(file.ArchivePath, out _, out _, out _);

    private static bool IsValidRebuildRename(ArchiveRebuildRename? rename) =>
        rename is not null
        && IsValidRebuildEntryKey(rename.EntryKey)
        && !string.IsNullOrWhiteSpace(rename.FinalArchivePath)
        && rename.FinalArchivePath.Length <= ProtocolConstants.MaximumPathCharacters
        && ArchivePathRules.TryNormalize(rename.FinalArchivePath, out _, out _, out _);

    private static bool IsValidDirectoryMapping(ArchiveCreationDirectory? directory) =>
        directory is not null
        && IsValidCreationMapping(directory.SourcePath, directory.ArchivePath)
        && ArchivePathRules.TryNormalize(
            directory.ArchivePath.TrimEnd('/', '\\'),
            out _,
            out _,
            out _);

    private static bool IsValidTreeAction(ArchiveRebuildTreeAction? action)
    {
        if (action is null
            || string.IsNullOrWhiteSpace(action.NormalizedPath)
            || action.NormalizedPath.Length > ProtocolConstants.MaximumPathCharacters
            || action.NormalizedPath.EndsWith('/')
            || action.NormalizedPath.TrimEnd('/') is { Length: 0 }
            || !ArchivePathRules.TryNormalize(
                action.NormalizedPath.TrimEnd('/'),
                out var normalized,
                out _,
                out _)
            || !string.Equals(
                normalized.Replace('\\', '/'),
                action.NormalizedPath.TrimEnd('/'),
                StringComparison.Ordinal)
            || !Enum.IsDefined(action.Kind)
            || string.IsNullOrWhiteSpace(action.ExpectedRevision)
            || action.ExpectedRevision.Length != 64
            || action.ExpectedRevision.Any(character => !Uri.IsHexDigit(character)
                || (character >= 'A' && character <= 'F')))
        {
            return false;
        }

        if (action.Kind == ArchiveRebuildTreeActionKind.Move)
        {
            return !string.IsNullOrWhiteSpace(action.FinalArchivePath)
                && action.FinalArchivePath.Length <= ProtocolConstants.MaximumPathCharacters
                && !action.FinalArchivePath.EndsWith('/')
                && ArchivePathRules.TryNormalize(
                    action.FinalArchivePath.TrimEnd('/'),
                    out var finalPath,
                    out _,
                    out _)
                && string.Equals(
                    finalPath.Replace('\\', '/'),
                    action.FinalArchivePath.TrimEnd('/'),
                    StringComparison.Ordinal);
        }

        return action.FinalArchivePath is null;
    }

    private static bool IsValidMetadataDirective(ArchiveRebuildMetadataDirective? directive)
    {
        if (directive is null
            || directive.Target is null
            || !Enum.IsDefined(directive.Target.Kind)
            || (directive.LastWriteTime is null && directive.AttributesLowWord is null)
            || (directive.AttributesLowWord is { } attributes
                && (attributes & ~((ushort)0x0027)) != 0))
        {
            return false;
        }

        var target = directive.Target;
        if (target.Kind == ArchiveRebuildMetadataTargetKind.RetainedEntryKey)
        {
            if (!IsValidRebuildEntryKey(target.RetainedEntryKey)
                || target.MappedFinalArchivePath is not null
                || target.MappedKind is not null)
            {
                return false;
            }
        }
        else if (target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath)
        {
            if (target.RetainedEntryKey is not null
                || string.IsNullOrWhiteSpace(target.MappedFinalArchivePath)
                || target.MappedFinalArchivePath.Length > ProtocolConstants.MaximumPathCharacters
                || target.MappedFinalArchivePath.EndsWith('/')
                || !ArchivePathRules.TryNormalize(target.MappedFinalArchivePath, out var normalized, out _, out _)
                || !string.Equals(normalized.Replace('\\', '/'), target.MappedFinalArchivePath, StringComparison.Ordinal)
                || target.MappedKind is not ArchiveEntryKind.File and not ArchiveEntryKind.Directory)
            {
                return false;
            }
        }

        if (directive.LastWriteTime is { } timestamp)
        {
            if (timestamp.Year is < 1980 or > 2107
                || timestamp.Second is < 0 or > 58
                || (timestamp.Second & 1) != 0)
            {
                return false;
            }

            try
            {
                _ = new DateTime(
                    timestamp.Year,
                    timestamp.Month,
                    timestamp.Day,
                    timestamp.Hour,
                    timestamp.Minute,
                    timestamp.Second,
                    DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return true;
    }

    private static string MetadataTargetIdentity(ArchiveRebuildMetadataDirective directive)
    {
        var target = directive.Target;
        return target.Kind == ArchiveRebuildMetadataTargetKind.RetainedEntryKey
            ? $"retained:{target.RetainedEntryKey!.NormalizedPath}\u001f{target.RetainedEntryKey.DuplicateOrdinal}"
            : $"mapped:{ArchivePathRules.GetCollisionKey(target.MappedFinalArchivePath!)}\u001f{target.MappedKind!.Value}";
    }

    private static bool TryGetTreePathPayloadBytes(
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        out int bytes)
    {
        long total = 0;
        try
        {
            total = checked(Encoding.UTF8.GetByteCount(destinationPath));
            foreach (var entry in entriesToDelete)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(entry.NormalizedPath));
            }

            foreach (var file in files)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(file.SourcePath));
                total = checked(total + Encoding.UTF8.GetByteCount(file.ArchivePath));
            }

            foreach (var rename in renames)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(rename.EntryKey.NormalizedPath));
                total = checked(total + Encoding.UTF8.GetByteCount(rename.FinalArchivePath));
            }

            foreach (var directory in directories)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(directory.SourcePath));
                total = checked(total + Encoding.UTF8.GetByteCount(directory.ArchivePath));
            }

            foreach (var action in treeActions)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(action.NormalizedPath));
                total = checked(total + Encoding.UTF8.GetByteCount(action.ExpectedRevision));
                if (action.FinalArchivePath is not null)
                {
                    total = checked(total + Encoding.UTF8.GetByteCount(action.FinalArchivePath));
                }
            }
        }
        catch (OverflowException)
        {
            bytes = int.MaxValue;
            return false;
        }

        bytes = total > int.MaxValue ? int.MaxValue : (int)total;
        return true;
    }

    private static bool TryGetMetadataPathPayloadBytes(
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        IReadOnlyList<ArchiveRebuildMetadataDirective> metadata,
        out int bytes)
    {
        if (!TryGetTreePathPayloadBytes(
                destinationPath,
                entriesToDelete,
                files,
                renames,
                directories,
                treeActions,
                out bytes))
        {
            return false;
        }

        long total = bytes;
        try
        {
            foreach (var directive in metadata)
            {
                var target = directive.Target;
                if (target.RetainedEntryKey is { } key)
                {
                    total = checked(total + Encoding.UTF8.GetByteCount(key.NormalizedPath));
                }
                if (target.MappedFinalArchivePath is { } path)
                {
                    total = checked(total + Encoding.UTF8.GetByteCount(path));
                }
            }
        }
        catch (OverflowException)
        {
            bytes = int.MaxValue;
            return false;
        }

        bytes = total > int.MaxValue ? int.MaxValue : (int)total;
        return true;
    }

    private void ValidateTreePlanResponse(GetZipRebuildTreePlanResponse response)
    {
        if (response is null
            || response.Directories is null
            || response.Directories.Count > ProtocolConstants.MaximumZipCreationEntries)
        {
            throw ProtocolFailure("The worker returned an invalid ZIP Rebuild Tree Plan response.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        long pathBytes = 0;
        foreach (var node in response.Directories)
        {
            if (node is null
                || string.IsNullOrWhiteSpace(node.NormalizedPath)
                || node.NormalizedPath.Length > ProtocolConstants.MaximumPathCharacters
                || node.NormalizedPath.EndsWith('/')
                || !ArchivePathRules.TryNormalize(node.NormalizedPath, out var normalized, out _, out _)
                || !string.Equals(
                    normalized.Replace('\\', '/'),
                    node.NormalizedPath,
                    StringComparison.Ordinal)
                || !seen.Add(node.NormalizedPath)
                || (previous is not null
                    && string.Compare(previous, node.NormalizedPath, StringComparison.Ordinal) >= 0)
                || node.DescendantCount < 0
                || node.MaterialEntryCount < 0
                || node.DescendantCount > ProtocolConstants.MaximumZipCreationEntries
                || node.MaterialEntryCount > ProtocolConstants.MaximumZipCreationEntries
                || string.IsNullOrWhiteSpace(node.SubtreeRevision)
                || node.SubtreeRevision.Length != 64
                || node.SubtreeRevision.Any(character => !Uri.IsHexDigit(character)
                    || (character >= 'A' && character <= 'F')))
            {
                throw ProtocolFailure("The worker returned invalid, duplicate, or unordered ZIP Rebuild Tree Plan nodes.");
            }

            pathBytes = checked(pathBytes + Encoding.UTF8.GetByteCount(node.NormalizedPath));
            if (pathBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
            {
                throw ProtocolFailure("The worker returned a ZIP Rebuild Tree Plan beyond the bounded path payload.");
            }

            previous = node.NormalizedPath;
        }
    }

    private void ValidateSourceFolderReviewResponse(
        GetZipRebuildSourceFolderReviewResponse response,
        IReadOnlyList<ArchiveCreationDirectory> requestedDirectories)
    {
        if (response is null
            || response.Directories is null
            || response.Directories.Count == 0
            || response.Directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots)
        {
            throw ProtocolFailure("The worker returned an invalid ZIP Rebuild source-folder review response.");
        }

        if (response.Directories.Count != requestedDirectories.Count)
        {
            throw ProtocolFailure("The worker returned a source-folder review with unexpected cardinality.");
        }

        long pathBytes = 0;
        var totalEntries = response.Directories.Count;
        for (var index = 0; index < response.Directories.Count; index++)
        {
            var review = response.Directories[index];
            var requested = requestedDirectories[index];
            string expectedSourcePath;
            string expectedArchiveRoot;
            try
            {
                expectedSourcePath = Path.GetFullPath(requested.SourcePath);
                if (!ArchivePathRules.TryNormalize(
                        requested.ArchivePath.TrimEnd('/', '\\'),
                        out var normalizedArchiveRoot,
                        out _,
                        out _))
                {
                    throw new InvalidDataException("The requested source-folder archive root was not canonicalizable.");
                }

                expectedArchiveRoot = normalizedArchiveRoot.Replace('\\', '/');
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException)
            {
                throw ProtocolFailure("The requested source-folder review root could not be canonicalized.");
            }

            if (review is null
                || string.IsNullOrWhiteSpace(review.SourcePath)
                || review.SourcePath.Length > ProtocolConstants.MaximumPathCharacters
                || !Path.IsPathFullyQualified(review.SourcePath)
                || !string.Equals(review.SourcePath, expectedSourcePath, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(review.ArchiveRoot)
                || !ArchivePathRules.TryNormalize(review.ArchiveRoot, out var normalized, out _, out _)
                || !string.Equals(normalized.Replace('\\', '/'), review.ArchiveRoot, StringComparison.Ordinal)
                || !string.Equals(review.ArchiveRoot, expectedArchiveRoot, StringComparison.Ordinal)
                || review.DescendantCount < 0
                || review.FileCount < 0
                || review.FileCount > review.DescendantCount
                || review.DescendantCount > ProtocolConstants.MaximumZipCreationEntries
                || review.FileCount > ProtocolConstants.MaximumZipCreationEntries)
            {
                throw ProtocolFailure("The worker returned invalid, duplicate, or unsafe ZIP Rebuild source-folder review data.");
            }

            pathBytes = checked(pathBytes + Encoding.UTF8.GetByteCount(review.SourcePath));
            pathBytes = checked(pathBytes + Encoding.UTF8.GetByteCount(review.ArchiveRoot));
            totalEntries = checked(totalEntries + review.DescendantCount);
            if (pathBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
            {
                throw ProtocolFailure("The worker returned source-folder review data beyond the bounded path payload.");
            }

            if (totalEntries > ProtocolConstants.MaximumZipCreationEntries)
            {
                throw ProtocolFailure("The worker returned source-folder review data beyond the global entry budget.");
            }
        }
    }

    private static bool TryGetSourceFolderReviewPathPayloadBytes(
        IReadOnlyList<ArchiveCreationDirectory> directories,
        out int bytes)
    {
        long total = 0;
        try
        {
            foreach (var directory in directories)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(directory.SourcePath));
                total = checked(total + Encoding.UTF8.GetByteCount(directory.ArchivePath));
            }
        }
        catch (OverflowException)
        {
            bytes = int.MaxValue;
            return false;
        }

        bytes = total > int.MaxValue ? int.MaxValue : (int)total;
        return true;
    }

    public async Task<ListExtractionRecoveriesResponse> ListExtractionRecoveriesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureExtractionRecoveryInspectionFeature();
        try
        {
            var response = await SendAsync<
                    ListExtractionRecoveriesRequest,
                    ListExtractionRecoveriesResponse>(
                    ProtocolMessageKinds.ListExtractionRecoveries,
                    new ListExtractionRecoveriesRequest(),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            ValidateExtractionRecoveryListResponse(response);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Listing extraction recoveries was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<InspectExtractionRecoveryResponse> InspectExtractionRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        EnsureExtractionRecoveryInspectionFeature();
        if (!IsValidRecoveryId(recoveryId))
        {
            throw new ArgumentException(
                "The extraction recovery identifier must be 64 lowercase hexadecimal characters.",
                nameof(recoveryId));
        }

        try
        {
            var response = await SendAsync<
                    InspectExtractionRecoveryRequest,
                    InspectExtractionRecoveryResponse>(
                    ProtocolMessageKinds.InspectExtractionRecovery,
                    new InspectExtractionRecoveryRequest(recoveryId),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            ValidateExtractionRecoveryInspectionResponse(response, recoveryId);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Inspecting the extraction recovery was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<ListArchiveCreationRecoveriesResponse> ListArchiveCreationRecoveriesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureRecoveryInspectionFeature();
        try
        {
            var response = await SendAsync<
                    ListArchiveCreationRecoveriesRequest,
                    ListArchiveCreationRecoveriesResponse>(
                    ProtocolMessageKinds.ListArchiveCreationRecoveries,
                    new ListArchiveCreationRecoveriesRequest(),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            ValidateRecoveryListResponse(response);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Listing archive-creation recoveries was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<InspectArchiveCreationRecoveryResponse> InspectArchiveCreationRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        EnsureRecoveryInspectionFeature();
        if (!IsValidRecoveryId(recoveryId))
        {
            throw new ArgumentException(
                "The archive-creation recovery identifier must be 64 lowercase hexadecimal characters.",
                nameof(recoveryId));
        }

        try
        {
            var response = await SendAsync<
                    InspectArchiveCreationRecoveryRequest,
                    InspectArchiveCreationRecoveryResponse>(
                    ProtocolMessageKinds.InspectArchiveCreationRecovery,
                    new InspectArchiveCreationRecoveryRequest(recoveryId),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            ValidateRecoveryInspectionResponse(response, recoveryId);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Inspecting the archive-creation recovery was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<ApplyArchiveCreationRecoveryDispositionResponse>
        ApplyArchiveCreationRecoveryDispositionAsync(
            string recoveryId,
            ArchiveCreationRecoveryPhase expectedPhase,
            int expectedSequence,
            DateTimeOffset expectedRecordedAtUtc,
            ArchiveCreationRecoveryDispositionAction action,
            CancellationToken cancellationToken = default)
    {
        EnsureRecoveryDispositionFeature();
        if (!IsValidRecoveryId(recoveryId))
        {
            throw new ArgumentException(
                "The archive-creation recovery identifier must be 64 lowercase hexadecimal characters.",
                nameof(recoveryId));
        }

        if (!Enum.IsDefined(expectedPhase))
        {
            throw new ArgumentException(
                "The archive-creation recovery phase is invalid.",
                nameof(expectedPhase));
        }

        if (expectedSequence <= 0
            || expectedSequence != (int)expectedPhase + 1)
        {
            throw new ArgumentException(
                "The archive-creation recovery sequence must match the expected phase.",
                nameof(expectedSequence));
        }

        if (!IsUtcTimestamp(expectedRecordedAtUtc))
        {
            throw new ArgumentException(
                "The archive-creation recovery timestamp must be a non-default UTC timestamp.",
                nameof(expectedRecordedAtUtc));
        }

        if (!Enum.IsDefined(action))
        {
            throw new ArgumentException(
                "The archive-creation recovery disposition action is invalid.",
                nameof(action));
        }

        try
        {
            var response = await SendAsync<
                    ApplyArchiveCreationRecoveryDispositionRequest,
                    ApplyArchiveCreationRecoveryDispositionResponse>(
                    ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
                    new ApplyArchiveCreationRecoveryDispositionRequest(
                        recoveryId,
                        expectedPhase,
                        expectedSequence,
                        expectedRecordedAtUtc,
                        action),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            ValidateRecoveryDispositionResponse(response, recoveryId, action);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Applying the archive-creation recovery disposition was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<ListNestedWorkspaceRecoveriesResponse> ListNestedWorkspaceRecoveriesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureNestedWorkspaceRecoveryFeature();
        try
        {
            var response = await SendAsync<
                    ListNestedWorkspaceRecoveriesRequest,
                    ListNestedWorkspaceRecoveriesResponse>(
                    ProtocolMessageKinds.ListNestedWorkspaceRecoveries,
                    new ListNestedWorkspaceRecoveriesRequest(),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            ValidateNestedWorkspaceRecoveryListResponse(response);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Listing nested-workspace recoveries was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<InspectNestedWorkspaceRecoveryResponse> InspectNestedWorkspaceRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        EnsureNestedWorkspaceRecoveryFeature();
        if (!IsValidRecoveryId(recoveryId))
        {
            throw new ArgumentException(
                "The nested-workspace recovery identifier must be 64 lowercase hexadecimal characters.",
                nameof(recoveryId));
        }

        try
        {
            var response = await SendAsync<
                    InspectNestedWorkspaceRecoveryRequest,
                    InspectNestedWorkspaceRecoveryResponse>(
                    ProtocolMessageKinds.InspectNestedWorkspaceRecovery,
                    new InspectNestedWorkspaceRecoveryRequest(recoveryId),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            ValidateNestedWorkspaceRecoveryInspectionResponse(response, recoveryId);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Inspecting the nested-workspace recovery was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<DiscardNestedWorkspaceRecoveryResponse> DiscardNestedWorkspaceRecoveryAsync(
        string recoveryId,
        string expectedRevision,
        int expectedSourceCount,
        long expectedTotalSourceBytes,
        CancellationToken cancellationToken = default)
    {
        EnsureNestedWorkspaceRecoveryFeature();
        if (!IsValidRecoveryId(recoveryId))
        {
            throw new ArgumentException(
                "The nested-workspace recovery identifier must be 64 lowercase hexadecimal characters.",
                nameof(recoveryId));
        }

        if (!IsValidRecoveryId(expectedRevision))
        {
            throw new ArgumentException(
                "The nested-workspace recovery revision must be 64 lowercase hexadecimal characters.",
                nameof(expectedRevision));
        }

        if (expectedSourceCount is < 0 or > ProtocolConstants.MaximumOpenSessions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSourceCount),
                "The nested-workspace source count is outside the recovery bound.");
        }

        if (expectedTotalSourceBytes is < 0
            or > ProtocolConstants.MaximumActiveNestedArchiveBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedTotalSourceBytes),
                "The nested-workspace byte total is outside the recovery bound.");
        }

        try
        {
            var response = await SendAsync<
                    DiscardNestedWorkspaceRecoveryRequest,
                    DiscardNestedWorkspaceRecoveryResponse>(
                    ProtocolMessageKinds.DiscardNestedWorkspaceRecovery,
                    new DiscardNestedWorkspaceRecoveryRequest(
                        recoveryId,
                        expectedRevision,
                        expectedSourceCount,
                        expectedTotalSourceBytes),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            ValidateNestedWorkspaceRecoveryDiscardResponse(response, recoveryId);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Discarding the nested-workspace recovery was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<InspectNestedWorkspaceReopenResponse> InspectNestedWorkspaceReopenAsync(
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        EnsureNestedWorkspaceIndependentReopenFeature();
        ValidateRecoveryIdArgument(recoveryId, nameof(recoveryId));
        try
        {
            var response = await SendAsync<
                    InspectNestedWorkspaceReopenRequest,
                    InspectNestedWorkspaceReopenResponse>(
                    ProtocolMessageKinds.InspectNestedWorkspaceReopen,
                    new InspectNestedWorkspaceReopenRequest(recoveryId),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            ValidateNestedWorkspaceReopenInspectionResponse(response, recoveryId);
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch (ArchiveClientException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.Error.Category is ArchiveErrorCategory.Cancelled
                or ArchiveErrorCategory.WorkerFailure)
        {
            throw new OperationCanceledException(
                "Inspecting nested-workspace sources for independent reopen was cancelled.",
                exception,
                cancellationToken);
        }
    }

    public async Task<ReopenNestedWorkspaceRecoveryResponse> ReopenNestedWorkspaceRecoveryAsync(
        string recoveryId,
        string expectedRevision,
        int expectedSourceCount,
        long expectedTotalSourceBytes,
        string sourceId,
        long expectedSourceLength,
        string expectedContentSha256,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default)
    {
        var sessionGateHeld = false;
        ReopenNestedWorkspaceRecoveryResponse? response = null;
        try
        {
            EnsureNestedWorkspaceIndependentReopenFeature();
            ValidateRecoveryIdArgument(recoveryId, nameof(recoveryId));
            ValidateRecoveryIdArgument(expectedRevision, nameof(expectedRevision));
            ValidateRecoveryIdArgument(sourceId, nameof(sourceId));
            ValidateRecoveryIdArgument(expectedContentSha256, nameof(expectedContentSha256));
            if (expectedSourceCount is <= 0 or > ProtocolConstants.MaximumOpenSessions
                || expectedTotalSourceBytes is <= 0
                    or > ProtocolConstants.MaximumActiveNestedArchiveBytes
                || expectedSourceLength is <= 0 or > ProtocolConstants.MaximumNestedArchiveBytes
                || expectedSourceLength > expectedTotalSourceBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedSourceLength),
                    "The nested-workspace reopen evidence is outside the bounded limits.");
            }

            if (passwordSlotId == Guid.Empty)
            {
                throw new ArgumentException("The password secret slot cannot be empty.", nameof(passwordSlotId));
            }

            await _sessionLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            sessionGateHeld = true;
            response = await SendAsync<
                    ReopenNestedWorkspaceRecoveryRequest,
                    ReopenNestedWorkspaceRecoveryResponse>(
                    ProtocolMessageKinds.ReopenNestedWorkspaceRecovery,
                    new ReopenNestedWorkspaceRecoveryRequest(
                        recoveryId,
                        expectedRevision,
                        expectedSourceCount,
                        expectedTotalSourceBytes,
                        sourceId,
                        expectedSourceLength,
                        expectedContentSha256,
                        NestedWorkspaceRecoveryReopenMode.IndependentRoot,
                        passwordSlotId),
                    cancellationToken,
                    finishResponseAfterWrite: true,
                    cancelRemoteRequest: true,
                    remoteCancellationAbortAfter: TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            ValidateNestedWorkspaceReopenResponse(
                response,
                recoveryId,
                sourceId,
                expectedSourceLength);
            if (cancellationToken.IsCancellationRequested)
            {
                await CompensateOpenedSessionAsync(
                        response.Archive.SessionId,
                        "The worker did not acknowledge recovery-reopen cancellation-race cleanup in time.")
                    .ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }

            return response;
        }
        finally
        {
            if (response is null && passwordSlotId is { } unsettledSlot)
            {
                await TryDiscardSecretSlotAsync(unsettledSlot).ConfigureAwait(false);
            }

            if (sessionGateHeld)
            {
                _sessionLifecycleGate.Release();
            }
        }
    }

    private void EnsureRecoveryInspectionFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryInspection)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "recovery.inspection_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support archive-creation recovery inspection."));
        }
    }

    private void EnsureExtractionRecoveryInspectionFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ExtractionRecoveryInspection)
            || ProtocolFeatures.ExtractionRecoveryInspectionDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveClientException(new ArchiveError(
                "extraction_recovery.inspection_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support read-only extraction recovery inspection."));
        }
    }

    private void EnsureRecoveryDispositionFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryDisposition)
            || !_enabledFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryInspection)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "recovery.disposition_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support archive-creation recovery disposition."));
        }
    }

    private void EnsureNestedWorkspaceRecoveryFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.NestedWorkspaceRecovery)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "nested_recovery.feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support nested-workspace recovery."));
        }
    }

    private void EnsureNestedWorkspaceIndependentReopenFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.NestedWorkspaceIndependentReopen)
            || ProtocolFeatures.NestedWorkspaceIndependentReopenDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveClientException(new ArchiveError(
                "nested_recovery.reopen_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support independent nested-workspace recovery reopen."));
        }
    }

    private void EnsureZipRebuildTreeEditFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation)
            || !_enabledFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
            || !_enabledFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.rebuild_tree_edit_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP Rebuild Tree Edit."));
        }
    }

    private void EnsureZipRebuildSourceFolderReviewFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.rebuild_source_folder_review_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP Rebuild source-folder review."));
        }
    }

    private void EnsureZipRebuildMetadataFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildMetadata)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation)
            || !_enabledFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
            || !_enabledFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.rebuild_metadata_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP Rebuild Metadata."));
        }
    }

    private void EnsureZipWriteCapabilitiesFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipWriteCapabilities)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildMetadata)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation)
            || !_enabledFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
            || !_enabledFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.write_capabilities_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support ZIP write capabilities and all preceding rebuild features."));
        }
    }

    private void EnsureTarCreationFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.TarCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "tar.creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure TAR creation."));
        }
    }

    private void EnsureTarDirectoryCreationFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.TarDirectoryCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.TarCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "tar.directory_creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure TAR directory creation."));
        }
    }

    private void EnsureTarWriteCapabilitiesFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.TarWriteCapabilities)
            || !_enabledFeatures.Contains(ProtocolFeatures.TarDirectoryCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.TarCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "tar.write_capabilities_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support TAR write capabilities and both creation routes."));
        }
    }

    private void EnsureRarCreationFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.RarCreation)
            || ProtocolFeatures.RarCreationDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveClientException(new ArchiveError(
                "rar.creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure RAR creation."));
        }
    }

    private void EnsureRarDirectoryCreationFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.RarDirectoryCreation)
            || ProtocolFeatures.RarDirectoryCreationDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveClientException(new ArchiveError(
                "rar.directory_creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure RAR directory creation."));
        }
    }

    private void EnsureRarWriteCapabilitiesFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.RarWriteCapabilities)
            || ProtocolFeatures.RarWriteCapabilitiesDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveClientException(new ArchiveError(
                "rar.write_capabilities_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support RAR write capabilities and both creation routes."));
        }
    }

    private void EnsureManagedCreationFeature(
        string feature,
        IReadOnlyList<string> dependencies,
        string displayName)
    {
        if (!_enabledFeatures.Contains(feature)
            || dependencies.Any(dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveClientException(new ArchiveError(
                "managed.creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                $"The connected archive worker does not support secure {displayName}."));
        }
    }

    private void EnsureOperationActivityFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.OperationActivity)
            || ProtocolFeatures.OperationActivityDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveClientException(new ArchiveError(
                "operation.activity_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support bounded operation activity reporting."));
        }
    }

    private void EnsureSupportDiagnosticsFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.SupportDiagnostics)
            || ProtocolFeatures.SupportDiagnosticsDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveClientException(new ArchiveError(
                "worker.support_diagnostics_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support bounded redacted support diagnostics."));
        }
    }

    private static void ValidateTarWriteCapabilitiesResponse(
        GetTarWriteCapabilitiesResponse? response)
    {
        if (response is null
            || response.CompressionPresets is null
            || !new[] { ArchiveCompressionPreset.Store }
                .SequenceEqual(response.CompressionPresets)
            || response.MaximumEntries != ProtocolConstants.MaximumTarCreationEntries
            || response.MaximumSourceBytes != ProtocolConstants.MaximumTarCreationSourceBytes
            || response.CanRebuild
            || response.SupportsTreeCreation != response.CanCreate
            || response.SupportsEncryption
            || response.SupportsFilenameEncryption
            || response.SupportsSplitOutput
            || response.SupportsExistingDestinationReplacement
            || response.SupportsInPlaceUpdate)
        {
            throw new InvalidDataException(
                "The worker returned an invalid or unsafe TAR write capability response.");
        }
    }

    private static void ValidateRarWriteCapabilitiesResponse(
        GetRarWriteCapabilitiesResponse? response)
    {
        if (response is null
            || response.CompressionPresets is null
            || !Enum.GetValues<ArchiveCompressionPreset>()
                .SequenceEqual(response.CompressionPresets)
            || response.MaximumEntries != ProtocolConstants.MaximumRarCreationEntries
            || response.MaximumSourceBytes != ProtocolConstants.MaximumRarCreationSourceBytes
            || !Enum.IsDefined(response.Availability)
            || response.CanCreate != (response.Availability == RarEncoderAvailability.Available)
            || response.CanRebuild
            || response.SupportsTreeCreation != response.CanCreate
            || response.SupportsEncryption
            || response.SupportsFilenameEncryption
            || response.SupportsSplitOutput
            || response.SupportsExistingDestinationReplacement
            || response.SupportsInPlaceUpdate)
        {
            throw new InvalidDataException(
                "The worker returned an invalid or unsafe RAR write capability response.");
        }
    }

    private static void ValidateManagedWriteCapabilitiesResponse(
        GetManagedWriteCapabilitiesResponse? response)
    {
        if (response?.Capabilities is null
            || response.Capabilities.Count != ProtocolConstants.MaximumManagedWriteCapabilityFormats)
        {
            throw new InvalidDataException(
                "The worker returned invalid managed write capability bounds.");
        }

        var expected = new[]
        {
            (ManagedArchiveCreationFormat.TarGZip,
                (IReadOnlyList<ArchiveCompressionPreset>)Enum.GetValues<ArchiveCompressionPreset>(),
                true,
                ProtocolConstants.MaximumTarCreationEntries,
                ProtocolConstants.MaximumTarCreationSourceBytes),
            (ManagedArchiveCreationFormat.TarBZip2,
                (IReadOnlyList<ArchiveCompressionPreset>)new[] { ArchiveCompressionPreset.Balanced },
                true,
                ProtocolConstants.MaximumTarCreationEntries,
                ProtocolConstants.MaximumTarCreationSourceBytes),
            (ManagedArchiveCreationFormat.GZip,
                (IReadOnlyList<ArchiveCompressionPreset>)Enum.GetValues<ArchiveCompressionPreset>(),
                false,
                ProtocolConstants.MaximumRawCreationEntries,
                ProtocolConstants.MaximumRawCreationSourceBytes),
            (ManagedArchiveCreationFormat.BZip2,
                (IReadOnlyList<ArchiveCompressionPreset>)new[] { ArchiveCompressionPreset.Balanced },
                false,
                ProtocolConstants.MaximumRawCreationEntries,
                ProtocolConstants.MaximumRawCreationSourceBytes),
        };

        for (var index = 0; index < expected.Length; index++)
        {
            var capability = response.Capabilities[index];
            var (format, presets, supportsTreeCreation, maximumEntries, maximumSourceBytes) = expected[index];
            if (capability is null
                || !Enum.IsDefined(capability.Format)
                || capability.Format != format
                || capability.CompressionPresets is null
                || !presets.SequenceEqual(capability.CompressionPresets)
                || capability.MaximumEntries != maximumEntries
                || capability.MaximumSourceBytes != maximumSourceBytes
                || capability.CanRebuild
                || capability.SupportsTreeCreation != (capability.CanCreate && supportsTreeCreation)
                || capability.SupportsEncryption
                || capability.SupportsFilenameEncryption
                || capability.SupportsSplitOutput
                || capability.SupportsExistingDestinationReplacement
                || capability.SupportsInPlaceUpdate)
            {
                throw new InvalidDataException(
                    "The worker returned an invalid or unsafe managed write capability response.");
            }
        }
    }

    private void ValidateOperationActivityResponse(GetOperationActivityResponse? response)
    {
        if (response is null
            || response.Operations is null
            || !IsCanonicalUtcTimestamp(response.SnapshotAtUtc)
            || response.MaximumActiveAndQueuedOperations
                != ProtocolConstants.MaximumQueuedOperations
            || response.MaximumConcurrentOperations
                != ProtocolConstants.MaximumConcurrentOperations
            || !Enum.IsDefined(response.QueueOrdering)
            || response.QueueOrdering != OperationQueueOrdering.Unspecified
            || response.MaximumRetainedTerminalOperations
                != ProtocolConstants.MaximumRetainedOperationActivity
            || response.SupportsReordering
            || response.SupportsPause
            || response.SupportsRetry
            || response.PersistsAcrossWorkerRestart
            || response.Operations.Count > ProtocolConstants.MaximumOperationActivityRecords)
        {
            throw ProtocolFailure("The worker returned invalid operation activity policy or bounds.");
        }

        var identifiers = new HashSet<OperationId>();
        OperationActivitySnapshot? previousActive = null;
        OperationActivitySnapshot? previousTerminal = null;
        var sawTerminal = false;
        var activeCount = 0;
        var terminalCount = 0;
        foreach (var operation in response.Operations)
        {
            if (operation is null
                || operation.OperationId.Value == Guid.Empty
                || !identifiers.Add(operation.OperationId)
                || !Enum.IsDefined(operation.Kind)
                || !Enum.IsDefined(operation.Phase)
                || operation.LastEventSequence < 0
                || !IsCanonicalUtcTimestamp(operation.CreatedAtUtc)
                || !IsCanonicalUtcTimestamp(operation.UpdatedAtUtc)
                || operation.CreatedAtUtc > operation.UpdatedAtUtc
                || operation.UpdatedAtUtc > response.SnapshotAtUtc
                || operation.WarningCount < 0
                || operation.WarningCount > operation.LastEventSequence)
            {
                throw ProtocolFailure("The worker returned invalid or duplicate operation activity data.");
            }

            var terminal = IsTerminalActivityPhase(operation.Phase);
            if (terminal)
            {
                sawTerminal = true;
                terminalCount++;
                if (operation.CompletedAtUtc is not { } completedAtUtc
                    || !IsCanonicalUtcTimestamp(completedAtUtc)
                    || completedAtUtc != operation.UpdatedAtUtc
                    || operation.CanCancel
                    || (operation.Phase == OperationPhase.Completed
                        && operation.WarningCount != 0)
                    || (operation.Phase == OperationPhase.CompletedWithWarnings
                        && operation.WarningCount == 0)
                    || (previousTerminal is not null
                        && IsTerminalActivityOutOfOrder(previousTerminal, operation)))
                {
                    throw ProtocolFailure("The worker returned inconsistent terminal operation activity.");
                }

                previousTerminal = operation;
            }
            else
            {
                activeCount++;
                if (sawTerminal
                    || operation.CompletedAtUtc is not null
                    || !operation.CanCancel
                    || (previousActive is not null
                        && IsActiveActivityOutOfOrder(previousActive, operation)))
                {
                    throw ProtocolFailure("The worker returned inconsistent active operation activity.");
                }

                previousActive = operation;
            }

            if (operation.LastEventSequence == 0
                && (operation.Phase != OperationPhase.Queued
                    || operation.UpdatedAtUtc != operation.CreatedAtUtc
                    || operation.Progress is not null
                    || operation.WarningCount != 0))
            {
                throw ProtocolFailure("The worker returned impossible pre-event operation activity.");
            }

            if (operation.Progress is { } progress)
            {
                if (operation.LastEventSequence == 0
                    || operation.Phase is OperationPhase.Queued
                        or OperationPhase.Validating
                        or OperationPhase.AwaitingPassword
                        or OperationPhase.AwaitingConflict
                        or OperationPhase.AwaitingRiskApproval)
                {
                    throw ProtocolFailure("The worker returned operation progress in an invalid phase.");
                }

                ValidateOperationActivityProgress(progress);
            }
        }

        if (activeCount > ProtocolConstants.MaximumQueuedOperations)
        {
            throw ProtocolFailure("The worker returned too many active or queued operations.");
        }

        if (terminalCount > ProtocolConstants.MaximumRetainedOperationActivity)
        {
            throw ProtocolFailure("The worker returned too many retained terminal operations.");
        }
    }

    private void ValidateSupportDiagnosticsResponse(GetSupportDiagnosticsResponse? response)
    {
        if (response is null
            || response.ProtocolMajor != ProtocolConstants.MajorVersion
            || response.ProtocolMinor != _negotiatedProtocolMinor
            || response.EnabledFeatureCount != _enabledFeatures.Count
            || !Enum.IsDefined(response.ContainmentModel)
            || response.ContainmentModel != WorkerContainmentModel.SameUserWorkerProcess
            || response.IsSameUserSecuritySandbox
            || response.MaximumActiveAndQueuedOperations
                != ProtocolConstants.MaximumQueuedOperations
            || response.MaximumConcurrentOperations
                != ProtocolConstants.MaximumConcurrentOperations
            || !Enum.IsDefined(response.QueueOrdering)
            || response.QueueOrdering != OperationQueueOrdering.Unspecified
            || response.MaximumRetainedTerminalOperations
                != ProtocolConstants.MaximumRetainedOperationActivity
            || response.ActiveAndQueuedOperationCount < 0
            || response.ActiveAndQueuedOperationCount
                > ProtocolConstants.MaximumQueuedOperations
            || response.RetainedTerminalOperationCount < 0
            || response.RetainedTerminalOperationCount
                > ProtocolConstants.MaximumRetainedOperationActivity
            || (response.OperationHistoryTruncated
                && response.RetainedTerminalOperationCount
                    != ProtocolConstants.MaximumRetainedOperationActivity)
            || response.SupportsQueueReordering
            || response.SupportsPause
            || response.SupportsRetry
            || response.SupportsAutomaticRecoveryDisposition
            || response.PersistsOperationActivityAcrossWorkerRestart)
        {
            throw ProtocolFailure(
                "The worker returned invalid, unsafe, or inconsistent support diagnostics.");
        }
    }

    private void ValidateOperationActivityProgress(OperationActivityProgress progress)
    {
        if (progress.BytesProcessed < 0
            || progress.TotalBytes is < 0
            || progress.EntriesProcessed < 0
            || progress.TotalEntries is < 0
            || (progress.TotalEntries is { } totalEntries
                && progress.EntriesProcessed > totalEntries)
            || (progress.TotalBytes is { } totalBytes
                && progress.BytesProcessed > totalBytes)
            || progress.Elapsed < TimeSpan.Zero
            || IsInvalidMetric(progress.BytesPerSecond)
            || IsInvalidMetric(progress.CompressionRatio))
        {
            throw ProtocolFailure("Worker operation activity progress values were invalid.");
        }
    }

    private static bool IsCanonicalUtcTimestamp(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    private static bool IsTerminalActivityPhase(OperationPhase phase) =>
        phase is OperationPhase.Completed
            or OperationPhase.CompletedWithWarnings
            or OperationPhase.Cancelled
            or OperationPhase.Failed;

    private static bool IsActiveActivityOutOfOrder(
        OperationActivitySnapshot previous,
        OperationActivitySnapshot current) =>
        previous.CreatedAtUtc > current.CreatedAtUtc
        || (previous.CreatedAtUtc == current.CreatedAtUtc
            && previous.OperationId.Value.CompareTo(current.OperationId.Value) > 0);

    private static bool IsTerminalActivityOutOfOrder(
        OperationActivitySnapshot previous,
        OperationActivitySnapshot current) =>
        previous.CompletedAtUtc < current.CompletedAtUtc
        || (previous.CompletedAtUtc == current.CompletedAtUtc
            && previous.OperationId.Value.CompareTo(current.OperationId.Value) > 0);

    private void ValidateZipWriteCapabilitiesResponse(
        GetZipWriteCapabilitiesResponse? response,
        ArchiveSessionId? requestedSessionId)
    {
        if (response is null
            || response.CompressionPresets is null
            || !Enum.GetValues<ArchiveCompressionPreset>().SequenceEqual(response.CompressionPresets)
            || response.MaximumEntries != ProtocolConstants.MaximumZipCreationEntries
            || response.MaximumSourceBytes != ProtocolConstants.MaximumZipCreationSourceBytes
            || (!response.CanCreate
                && (response.CanRebuild
                    || response.SupportsAdd
                    || response.SupportsReplace
                    || response.SupportsDelete
                    || response.SupportsFileRename
                    || response.SupportsTreeEdit
                    || response.SupportsMetadata))
            || (response.CanRebuild && !response.CanCreate)
            || (response.CanRebuild && requestedSessionId is null)
            || response.SupportsAdd != response.CanCreate
            || response.SupportsReplace != response.CanCreate
            || response.SupportsDelete != response.CanCreate
            || response.SupportsFileRename != response.CanCreate
            || response.SupportsTreeEdit != response.CanCreate
            || response.SupportsMetadata != response.CanCreate
            || response.SupportsEncryption
            || response.SupportsFilenameEncryption
            || response.SupportsSplitOutput
            || response.SupportsExistingDestinationReplacement
            || response.SupportsInPlaceUpdate)
        {
            throw ProtocolFailure("The worker returned an invalid ZIP write capability response.");
        }
    }

    public async Task<CancelOperationResponse> CancelOperationAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Operation identifier cannot be empty.", nameof(operationId));
        }

        var response = await SendAsync<CancelOperationRequest, CancelOperationResponse>(
            ProtocolMessageKinds.CancelOperation,
            new CancelOperationRequest(operationId),
            cancellationToken,
            finishResponseAfterWrite: true,
            controlRequest: true,
            abortTransportOnResponseCancellation: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<OperationEvent> WaitForTerminalAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (operationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Operation identifier cannot be empty.", nameof(operationId));
        }

        if (!_authorizedOperations.ContainsKey(operationId)
            && !_terminals.ContainsKey(operationId)
            && !_completedEventStreams.ContainsKey(operationId))
        {
            throw new InvalidOperationException("The operation identifier was not authorized by a successful start response.");
        }

        if (_completedEventStreams.ContainsKey(operationId)
            && !_terminals.ContainsKey(operationId))
        {
            throw new InvalidOperationException("The operation terminal result has already been consumed.");
        }

        var source = _terminals.GetOrAdd(
            operationId,
            static _ => new TaskCompletionSource<OperationEvent>(TaskCreationOptions.RunContinuationsAsynchronously));
        var result = await source.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_terminals.TryGetValue(operationId, out var current)
            && ReferenceEquals(source, current))
        {
            _terminals.TryRemove(operationId, out _);
        }

        return result;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeStarted, 1);
        foreach (var lease in _entryStreams.Values.ToArray())
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        if (!_shutdown.IsCancellationRequested && Volatile.Read(ref _transportFailure) is null)
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                _ = await SendAsync<CloseRequest, CloseResponse>(
                    ProtocolMessageKinds.CloseWorker,
                    new CloseRequest(),
                    closeTimeout.Token,
                    allowDuringDisposal: true,
                    controlRequest: true,
                    abortTransportOnResponseCancellation: true).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is ArchiveClientException
                    or OperationCanceledException
                    or IOException
                    or ObjectDisposedException)
            {
            }
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _presentationEvents.Writer.TryComplete();
        _pipe.Dispose();
        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (TimeoutException)
        {
            // The process/job fallback below is the final boundary for a stuck pipe read.
        }

        try
        {
            await _eventDispatchLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A presentation callback may be blocked in caller code. Transport shutdown must still complete.
        }
        catch (OperationCanceledException)
        {
        }

        if (!_worker.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _job.Dispose();
                if (!_worker.HasExited)
                {
                    _worker.Kill(entireProcessTree: true);
                }
            }
        }

        _job.Dispose();
        _worker.Dispose();
    }

    private async Task<StartOperationResponse> StartOperationAsync<TRequest>(
        string kind,
        TRequest request,
        Guid? passwordSlotId,
        CancellationToken cancellationToken)
    {
        StartOperationResponse response;
        try
        {
            try
            {
                ProtocolStream.ValidateJsonFrameSize(kind, request);
            }
            catch (InvalidDataException exception)
            {
                throw new ArgumentException(
                    "Operation start metadata exceeds the bounded protocol frame size.",
                    nameof(request),
                    exception);
            }

            response = await SendAsync<TRequest, StartOperationResponse>(
                kind,
                request,
                cancellationToken,
                finishResponseAfterWrite: true,
                cancelRemoteRequest: true,
                remoteCancellationAbortAfter: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            if (passwordSlotId is { } failedStartSlot)
            {
                await TryDiscardSecretSlotAsync(failedStartSlot).ConfigureAwait(false);
            }

            throw;
        }
        if (response.OperationId.Value == Guid.Empty)
        {
            throw ProtocolFailure("The worker returned an invalid operation identifier.");
        }

        _terminals.TryAdd(
            response.OperationId,
            new TaskCompletionSource<OperationEvent>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (cancellationToken.IsCancellationRequested)
        {
            using var cancelTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            cancelTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            var cancellationAcknowledged = false;
            try
            {
                var cancelResponse = await CancelOperationAsync(response.OperationId, cancelTimeout.Token).ConfigureAwait(false);
                cancellationAcknowledged = cancelResponse.CancellationRequested;
            }
            catch (Exception exception) when (
                exception is ArchiveClientException or OperationCanceledException or ObjectDisposedException)
            {
                // A worker error without a positive cancellation response does not prove that work stopped.
            }

            if (cancellationAcknowledged
                || Volatile.Read(ref _transportAbortStarted) != 0
                || _shutdown.IsCancellationRequested)
            {
                ForgetTerminal(response.OperationId);
                throw new OperationCanceledException(cancellationToken);
            }

            return response;
        }

        return response;
    }

    private async Task TryDiscardSecretSlotAsync(Guid slotId)
    {
        if (slotId == Guid.Empty
            || _shutdown.IsCancellationRequested
            || Volatile.Read(ref _transportFailure) is not null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            _ = await DiscardSecretAsync(slotId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!_shutdown.IsCancellationRequested)
        {
            AbortTransport(new TimeoutException(
                "The worker did not acknowledge secret cleanup in time.",
                exception));
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or OperationCanceledException or ObjectDisposedException)
        {
            // The slot expires quickly and worker shutdown clears it if transport cleanup cannot be acknowledged.
        }
    }

    private async Task TryDiscardNestedSecretSlotsAsync(
        Guid? parentPasswordSlotId,
        Guid? nestedPasswordSlotId)
    {
        if (parentPasswordSlotId is { } parentSlot)
        {
            await TryDiscardSecretSlotAsync(parentSlot).ConfigureAwait(false);
        }

        if (nestedPasswordSlotId is { } nestedSlot && nestedSlot != parentPasswordSlotId)
        {
            await TryDiscardSecretSlotAsync(nestedSlot).ConfigureAwait(false);
        }
    }

    private async Task CompensateOpenedSessionAsync(
        ArchiveSessionId sessionId,
        string timeoutMessage)
    {
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            _ = await SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
                ProtocolMessageKinds.CloseArchive,
                new CloseArchiveRequest(sessionId),
                closeTimeout.Token,
                finishResponseAfterWrite: true,
                abortTransportOnResponseCancellation: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!_shutdown.IsCancellationRequested)
        {
            AbortTransport(new TimeoutException(timeoutMessage, exception));
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or OperationCanceledException or ObjectDisposedException)
        {
            // Worker shutdown remains the final cleanup boundary if compensation cannot be acknowledged.
        }
    }

    private IReadOnlyList<ArchiveSessionId> GetNestedDescendantsDeepestFirst(
        ArchiveSessionId rootSessionId)
    {
        var relationships = _nestedParents.ToArray();
        var result = new List<ArchiveSessionId>();
        var visited = new HashSet<ArchiveSessionId> { rootSessionId };

        void Visit(ArchiveSessionId parentSessionId)
        {
            foreach (var relationship in relationships.Where(pair => pair.Value == parentSessionId))
            {
                if (!visited.Add(relationship.Key))
                {
                    throw ProtocolFailure("The nested archive session graph contains a cycle.");
                }

                Visit(relationship.Key);
                result.Add(relationship.Key);
            }
        }

        Visit(rootSessionId);
        return result;
    }

    private async Task TryCancelRequestAsync(Guid requestId)
    {
        if (requestId == Guid.Empty
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation)
            || _shutdown.IsCancellationRequested
            || Volatile.Read(ref _transportFailure) is not null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            _ = await SendAsync<CancelRequestRequest, CancelRequestResponse>(
                ProtocolMessageKinds.CancelRequest,
                new CancelRequestRequest(requestId),
                timeout.Token,
                finishResponseAfterWrite: true,
                controlRequest: true,
                abortTransportOnResponseCancellation: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!_shutdown.IsCancellationRequested)
        {
            AbortTransport(new TimeoutException(
                "The worker did not acknowledge request cancellation in time.",
                exception));
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or OperationCanceledException or ObjectDisposedException)
        {
            // The original response or worker shutdown remains the authoritative completion boundary.
        }
    }

    private async Task CancelEntryStreamLeaseAsync(EntryStreamId streamId)
    {
        if (streamId.Value == Guid.Empty
            || !_enabledFeatures.Contains(ProtocolFeatures.EntryStreamLeases)
            || _shutdown.IsCancellationRequested
            || Volatile.Read(ref _transportFailure) is not null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            _ = await SendAsync<CancelEntryStreamRequest, CancelEntryStreamResponse>(
                ProtocolMessageKinds.CancelEntryStream,
                new CancelEntryStreamRequest(streamId),
                timeout.Token,
                finishResponseAfterWrite: true,
                allowDuringDisposal: true,
                controlRequest: true,
                abortTransportOnResponseCancellation: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or OperationCanceledException or ObjectDisposedException)
        {
            // Transport abort is the final containment boundary when cancellation cannot be acknowledged.
        }
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string kind,
        TRequest request,
        CancellationToken cancellationToken,
        bool finishResponseAfterWrite = false,
        bool allowDuringDisposal = false,
        bool cancelRemoteRequest = false,
        bool controlRequest = false,
        bool abortTransportOnResponseCancellation = false,
        TimeSpan? remoteCancellationAbortAfter = null)
    {
        ThrowIfUnavailable(allowDuringDisposal);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var requestSlots = controlRequest ? _controlRequestSlots : _requestSlots;
        var requestId = Guid.Empty;
        PendingRequest? pending = null;
        var requestSlotAcquired = false;
        var unownedRequestSlot = true;
        var frameWritten = false;
        try
        {
            try
            {
                await requestSlots.WaitAsync(admission.Token).ConfigureAwait(false);
                requestSlotAcquired = true;
            }
            catch (OperationCanceledException exception) when (
                controlRequest
                    && abortTransportOnResponseCancellation
                    && pending is null
                    && cancellationToken.IsCancellationRequested
                    && !_shutdown.IsCancellationRequested)
            {
                throw AbortTransport(new TimeoutException(
                    "The bounded control request could not be admitted before cancellation.",
                    exception));
            }

            try
            {
                await _writeGate.WaitAsync(admission.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                controlRequest
                    && abortTransportOnResponseCancellation
                    && pending is null
                    && cancellationToken.IsCancellationRequested
                    && !_shutdown.IsCancellationRequested)
            {
                throw AbortTransport(new TimeoutException(
                    "The bounded control request could not acquire the write lane before cancellation.",
                    exception));
            }

            try
            {
                ThrowIfUnavailable(allowDuringDisposal);
                requestId = Guid.NewGuid();
                var sequence = Interlocked.Increment(ref _nextRequestSequence);
                pending = new PendingRequest(
                    sequence,
                    requestSlots,
                    $"{kind}.response",
                    IsOperationStartKind(kind));
                if (!_pending.TryAdd(requestId, pending))
                {
                    throw new InvalidOperationException("Could not allocate an IPC request.");
                }

                unownedRequestSlot = false;
                try
                {
                    await ProtocolStream.WriteJsonAsync(
                        _pipe,
                        kind,
                        requestId,
                        request,
                        sequence,
                        _shutdown.Token).ConfigureAwait(false);
                    frameWritten = true;
                }
                catch (Exception exception) when (
                    exception is IOException
                        or ObjectDisposedException
                        or InvalidDataException
                        or JsonException)
                {
                    throw controlRequest && abortTransportOnResponseCancellation
                        ? AbortTransport(exception)
                        : RecordTransportFailure(exception);
                }
            }
            finally
            {
                _writeGate.Release();
            }

            var response = await WaitForResponseAsync(
                    pending!,
                    requestId,
                    cancellationToken,
                    finishResponseAfterWrite,
                    cancelRemoteRequest,
                    abortTransportOnResponseCancellation,
                    remoteCancellationAbortAfter)
                .ConfigureAwait(false);
            try
            {
                if (response.Kind == ProtocolMessageKinds.Error)
                {
                    var error = response.ReadPayload<ErrorResponse>().Error
                        ?? throw ProtocolFailure("The worker returned an empty error response.");
                    if (!Enum.IsDefined(error.Category))
                    {
                        throw ProtocolFailure("The worker returned an unknown error category.");
                    }

                    throw new ArchiveClientException(error);
                }

                var expectedKind = $"{kind}.response";
                if (!string.Equals(response.Kind, expectedKind, StringComparison.Ordinal))
                {
                    throw ProtocolFailure("The archive worker returned an unexpected response type.");
                }

                return response.ReadPayload<TResponse>();
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                throw AbortTransport(exception);
            }
        }
        finally
        {
            if (pending is not null && (!frameWritten || pending.Completion.Task.IsCompleted))
            {
                RemovePendingRequest(requestId, pending);
            }

            if (unownedRequestSlot && requestSlotAcquired)
            {
                requestSlots.Release();
            }
        }
    }

    private async Task<ProtocolFrame> WaitForResponseAsync(
        PendingRequest pending,
        Guid requestId,
        CancellationToken cancellationToken,
        bool finishResponseAfterWrite,
        bool cancelRemoteRequest,
        bool abortTransportOnResponseCancellation,
        TimeSpan? remoteCancellationAbortAfter)
    {
        if (abortTransportOnResponseCancellation)
        {
            try
            {
                return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                cancellationToken.IsCancellationRequested
                && !_shutdown.IsCancellationRequested)
            {
                throw AbortTransport(new TimeoutException(
                    "The worker did not acknowledge a bounded control request in time.",
                    exception));
            }
        }

        if (!finishResponseAfterWrite)
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!cancelRemoteRequest
            || !cancellationToken.CanBeCanceled
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            return await pending.Completion.Task.ConfigureAwait(false);
        }

        var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.UnsafeRegister(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellationSignal);
        var completed = await Task
            .WhenAny(pending.Completion.Task, cancellationSignal.Task)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, cancellationSignal.Task)
            && !pending.Completion.Task.IsCompleted)
        {
            await TryCancelRequestAsync(requestId).ConfigureAwait(false);
            if (remoteCancellationAbortAfter is { } timeout
                && !pending.Completion.Task.IsCompleted)
            {
                try
                {
                    return await pending.Completion.Task
                        .WaitAsync(timeout)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    throw AbortTransport(new TimeoutException(
                        "The worker did not finish the cancelled request before its containment deadline.",
                        exception));
                }
            }
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var incoming = await ProtocolStream.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
                if (incoming.Type != ProtocolFrameType.Json || incoming.Json is null)
                {
                    throw new InvalidDataException("Worker sent a non-JSON or empty frame to the client.");
                }

                var frame = incoming.Json;
                if (frame.Kind == ProtocolMessageKinds.OperationEvent)
                {
                    var operationEvent = frame.ReadPayload<OperationEvent>();
                    ValidateOperationEvent(frame, operationEvent);
                    if (operationEvent.Kind == OperationEventKind.Terminal)
                    {
                        var terminal = _terminals.GetOrAdd(
                            operationEvent.OperationId,
                            static _ => new TaskCompletionSource<OperationEvent>(TaskCreationOptions.RunContinuationsAsynchronously));
                        terminal.TrySetResult(operationEvent);
                        _authorizedOperations.TryRemove(operationEvent.OperationId, out _);
                        RetireOperation(operationEvent.OperationId);
                    }

                    await _presentationEvents.Writer.WriteAsync(operationEvent, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (frame.RequestId == Guid.Empty || frame.Sequence <= 0)
                {
                    throw new InvalidDataException("Worker response envelope was invalid.");
                }

                if (_pending.TryGetValue(frame.RequestId, out var pending))
                {
                    if (frame.Sequence != pending.Sequence)
                    {
                        throw new InvalidDataException("Worker response sequence did not match its request.");
                    }

                    if (pending.AuthorizesOperation
                        && string.Equals(frame.Kind, pending.ExpectedResponseKind, StringComparison.Ordinal))
                    {
                        var startResponse = frame.ReadPayload<StartOperationResponse>();
                        AuthorizeOperation(startResponse.OperationId);
                    }

                    if (_pending.TryRemove(frame.RequestId, out var removed))
                    {
                        removed.ReleaseSlot();
                        removed.Completion.TrySetResult(frame);
                    }

                    continue;
                }

                throw new InvalidDataException("Worker response did not match an active request.");
            }
        }
        catch (Exception exception)
        {
            var transportError = exception is InvalidDataException or JsonException
                ? AbortTransport(exception)
                : RecordTransportFailure(exception);
            FailOutstandingRequests(transportError);
        }
        finally
        {
            if (!_pending.IsEmpty)
            {
                RecordTransportFailure(new EndOfStreamException(
                    "The worker response loop ended with requests still pending."));
            }

            _presentationEvents.Writer.TryComplete();
        }
    }

    private async Task DispatchPresentationEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var operationEvent in _presentationEvents.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                RaiseOperationEvent(operationEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ThrowIfUnavailable(bool allowDuringDisposal = false)
    {
        if (!allowDuringDisposal && Volatile.Read(ref _disposeStarted) != 0)
        {
            throw new ObjectDisposedException(nameof(ArchiveClient));
        }

        if (Volatile.Read(ref _transportFailure) is { } failure)
        {
            throw failure;
        }
    }

    private ArchiveClientException RecordTransportFailure(Exception exception)
    {
        var failure = ToTransportError(exception);
        var recorded = Interlocked.CompareExchange(ref _transportFailure, failure, null) ?? failure;
        FailOutstandingRequests(recorded);

        foreach (var terminal in _terminals.Values)
        {
            terminal.TrySetException(recorded);
        }

        foreach (var lease in _entryStreams.Values.ToArray())
        {
            lease.Fail(recorded);
        }

        return recorded;
    }

    private void ReleaseEntryStream(ArchiveEntryStreamLease lease)
    {
        if (_entryStreams.TryGetValue(lease.StreamId, out var current)
            && ReferenceEquals(current, lease)
            && _entryStreams.TryRemove(lease.StreamId, out _))
        {
            _entryStreamSlots.Release();
        }
    }

    private ArchiveClientException AbortTransport(Exception exception)
    {
        var recorded = RecordTransportFailure(exception);
        if (Interlocked.Exchange(ref _transportAbortStarted, 1) != 0)
        {
            return recorded;
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _presentationEvents.Writer.TryComplete();
        try
        {
            _pipe.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        // The client owns this Job Object. Closing it terminates only the launched worker.
        _job.Dispose();
        return recorded;
    }

    private void FailOutstandingRequests(ArchiveClientException failure)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                pending.ReleaseSlot();
                pending.Completion.TrySetException(failure);
            }
        }
    }

    private void RemovePendingRequest(Guid requestId, PendingRequest pending)
    {
        if (_pending.TryGetValue(requestId, out var current)
            && ReferenceEquals(current, pending)
            && _pending.TryRemove(requestId, out _))
        {
            pending.ReleaseSlot();
        }
    }

    private void AuthorizeOperation(OperationId operationId)
    {
        if (operationId.Value == Guid.Empty
            || _completedEventStreams.ContainsKey(operationId)
            || !_authorizedOperations.TryAdd(operationId, 0))
        {
            throw new InvalidDataException("Worker returned an invalid or reused operation identifier.");
        }
    }

    private void RetireOperation(OperationId operationId)
    {
        if (!_completedEventStreams.TryAdd(operationId, 0))
        {
            return;
        }

        _retiredOperationOrder.Enqueue(operationId);
        while (_completedEventStreams.Count > MaximumRetiredOperations
            && _retiredOperationOrder.TryDequeue(out var retiredOperationId))
        {
            _completedEventStreams.TryRemove(retiredOperationId, out _);
        }
    }

    private void ForgetTerminal(OperationId operationId)
    {
        var terminal = _terminals.GetOrAdd(
            operationId,
            static _ => new TaskCompletionSource<OperationEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        _ = terminal.Task.ContinueWith(
            (completedTask, state) =>
            {
                _ = completedTask;
                var (client, id, expected) =
                    ((ArchiveClient Client, OperationId Id, TaskCompletionSource<OperationEvent> Expected))state!;
                if (client._terminals.TryGetValue(id, out var current)
                    && ReferenceEquals(current, expected))
                {
                    client._terminals.TryRemove(id, out _);
                }
            },
            (Client: this, Id: operationId, Expected: terminal),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsOperationStartKind(string kind) =>
        kind is ProtocolMessageKinds.StartExtraction
            or ProtocolMessageKinds.StartIntegrityTest
            or ProtocolMessageKinds.StartZipCreation
            or ProtocolMessageKinds.StartZipTreeCreation
            or ProtocolMessageKinds.StartTarCreation
            or ProtocolMessageKinds.StartTarTreeCreation
            or ProtocolMessageKinds.StartRarCreation
            or ProtocolMessageKinds.StartRarTreeCreation
            or ProtocolMessageKinds.StartTarGZipCreation
            or ProtocolMessageKinds.StartTarGZipTreeCreation
            or ProtocolMessageKinds.StartTarBZip2Creation
            or ProtocolMessageKinds.StartTarBZip2TreeCreation
            or ProtocolMessageKinds.StartGZipCreation
            or ProtocolMessageKinds.StartBZip2Creation
            or ProtocolMessageKinds.StartZipRebuildCopy
            or ProtocolMessageKinds.StartZipRebuildRename
            or ProtocolMessageKinds.StartZipRebuildTreeEdit
            or ProtocolMessageKinds.StartZipRebuildMetadata;

    private void ValidateOperationEvent(ProtocolFrame frame, OperationEvent operationEvent)
    {
        if (frame.RequestId != Guid.Empty
            || frame.Sequence != operationEvent.Sequence
            || operationEvent.Sequence <= 0
            || operationEvent.OperationId.Value == Guid.Empty)
        {
            throw new InvalidDataException("Worker operation event envelope was invalid.");
        }

        if (!Enum.IsDefined(operationEvent.Kind)
            || !Enum.IsDefined(operationEvent.Phase)
            || (operationEvent.Error is not null
                && (!Enum.IsDefined(operationEvent.Error.Category)
                    || string.IsNullOrWhiteSpace(operationEvent.Error.Code)
                    || string.IsNullOrWhiteSpace(operationEvent.Error.Message)))
            || (operationEvent.Warning is not null
                && (string.IsNullOrWhiteSpace(operationEvent.Warning.Code)
                    || string.IsNullOrWhiteSpace(operationEvent.Warning.Message))))
        {
            throw new InvalidDataException("Worker operation event contained an unknown enum value.");
        }

        if (!_authorizedOperations.ContainsKey(operationEvent.OperationId))
        {
            throw new InvalidDataException(
                _completedEventStreams.ContainsKey(operationEvent.OperationId)
                    ? "Worker sent an event after an operation terminal event."
                    : "Worker sent an event for an unknown operation identifier.");
        }

        var expectedSequence = _eventSequences.TryGetValue(operationEvent.OperationId, out var previous)
            ? previous + 1
            : 1;
        if (operationEvent.Sequence != expectedSequence)
        {
            throw new InvalidDataException("Worker operation event sequence was not contiguous.");
        }

        var terminalPhase = operationEvent.Phase is OperationPhase.Completed
            or OperationPhase.CompletedWithWarnings
            or OperationPhase.Cancelled
            or OperationPhase.Failed;
        var hasOnlyExpectedPayload = operationEvent.Kind switch
        {
            OperationEventKind.StateChanged => operationEvent.Progress is null
                && operationEvent.Warning is null
                && operationEvent.Error is null,
            OperationEventKind.Progress => operationEvent.Progress is not null
                && operationEvent.Warning is null
                && operationEvent.Error is null,
            OperationEventKind.Warning => operationEvent.Progress is null
                && operationEvent.Warning is not null
                && operationEvent.Error is null,
            OperationEventKind.Terminal => operationEvent.Progress is null,
            _ => false,
        };
        if ((operationEvent.Kind == OperationEventKind.Terminal) != terminalPhase
            || !hasOnlyExpectedPayload
            || (operationEvent.Phase is OperationPhase.Cancelled or OperationPhase.Failed && operationEvent.Error is null)
            || (operationEvent.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings && operationEvent.Error is not null)
            || (operationEvent.Kind is OperationEventKind.Progress or OperationEventKind.Warning
                && operationEvent.Phase != OperationPhase.Running))
        {
            throw new InvalidDataException("Worker operation event state was inconsistent.");
        }

        ValidateOperationPhaseTransition(operationEvent);
        ValidateOperationProgress(operationEvent.Progress);

        _eventSequences[operationEvent.OperationId] = operationEvent.Sequence;
        if (operationEvent.Kind == OperationEventKind.Terminal)
        {
            _eventSequences.Remove(operationEvent.OperationId);
            _eventPhases.Remove(operationEvent.OperationId);
        }
    }

    private void RaiseOperationEvent(OperationEvent operationEvent)
    {
        var handlers = OperationEventReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<OperationEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, operationEvent);
            }
            catch
            {
                // Presentation callbacks are isolated from the transport loop.
            }
        }
    }

    private void ValidateOperationPhaseTransition(OperationEvent operationEvent)
    {
        if (!_eventPhases.TryGetValue(operationEvent.OperationId, out var previous))
        {
            if (operationEvent.Kind == OperationEventKind.Terminal
                && operationEvent.Phase is OperationPhase.Cancelled or OperationPhase.Failed)
            {
                return;
            }

            if (operationEvent.Kind != OperationEventKind.StateChanged
                || operationEvent.Phase != OperationPhase.Queued)
            {
                throw new InvalidDataException("Worker operation event stream did not begin in the queued phase.");
            }

            _eventPhases[operationEvent.OperationId] = operationEvent.Phase;
            return;
        }

        if (operationEvent.Kind == OperationEventKind.StateChanged)
        {
            if (!IsAllowedPhaseTransition(previous, operationEvent.Phase))
            {
                throw new InvalidDataException("Worker operation phase transition was invalid.");
            }

            _eventPhases[operationEvent.OperationId] = operationEvent.Phase;
            return;
        }

        if (operationEvent.Kind is OperationEventKind.Progress or OperationEventKind.Warning
            && previous != OperationPhase.Running)
        {
            throw new InvalidDataException("Worker sent operation activity outside the running phase.");
        }

        if (operationEvent.Kind == OperationEventKind.Terminal
            && operationEvent.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings
            && previous is not OperationPhase.Running and not OperationPhase.Finalizing)
        {
            throw new InvalidDataException("Worker completed an operation from an invalid phase.");
        }
    }

    private static bool IsAllowedPhaseTransition(OperationPhase previous, OperationPhase next) =>
        (previous, next) switch
        {
            (OperationPhase.Queued, OperationPhase.Validating) => true,
            (OperationPhase.Validating, OperationPhase.AwaitingPassword) => true,
            (OperationPhase.Validating, OperationPhase.AwaitingConflict) => true,
            (OperationPhase.Validating, OperationPhase.AwaitingRiskApproval) => true,
            (OperationPhase.Validating, OperationPhase.Running) => true,
            (OperationPhase.AwaitingPassword, OperationPhase.Validating) => true,
            (OperationPhase.AwaitingConflict, OperationPhase.Running) => true,
            (OperationPhase.AwaitingRiskApproval, OperationPhase.Running) => true,
            (OperationPhase.Running, OperationPhase.Finalizing) => true,
            _ => false,
        };

    private static void ValidateOperationProgress(OperationProgress? progress)
    {
        if (progress is null)
        {
            return;
        }

        if (progress.BytesProcessed < 0
            || progress.TotalBytes is < 0
            || progress.EntriesProcessed < 0
            || progress.TotalEntries is < 0
            || (progress.TotalEntries is { } totalEntries && progress.EntriesProcessed > totalEntries)
            || (progress.TotalBytes is { } totalBytes && progress.BytesProcessed > totalBytes)
            || progress.CurrentEntryId?.Value == Guid.Empty
            || progress.CurrentEntryName?.Length > ProtocolConstants.MaximumPathCharacters
            || progress.Elapsed < TimeSpan.Zero
            || IsInvalidMetric(progress.BytesPerSecond)
            || IsInvalidMetric(progress.CompressionRatio))
        {
            throw new InvalidDataException("Worker operation progress values were invalid.");
        }
    }

    private static bool IsInvalidMetric(double? value) =>
        value is { } metric && (metric < 0 || !double.IsFinite(metric));

    private static void ValidatePathArgument(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > ProtocolConstants.MaximumPathCharacters)
        {
            throw new ArgumentException("Path is empty or exceeds the protocol limit.", parameterName);
        }
    }

    private static void ValidateSessionId(ArchiveSessionId sessionId)
    {
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Archive session identifier cannot be empty.", nameof(sessionId));
        }
    }

    private static void ValidateEntrySelection(IReadOnlyList<EntryId> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count > 500_000
            || entryIds.Any(entryId => entryId.Value == Guid.Empty)
            || entryIds.Distinct().Count() != entryIds.Count)
        {
            throw new ArgumentException("Entry selection is too large or contains invalid or duplicate identifiers.", nameof(entryIds));
        }
    }

    private void ValidateArchiveDescriptor(ArchiveDescriptor? archive)
    {
        const ArchiveCapability knownCapabilities = ArchiveCapability.List
            | ArchiveCapability.OpenEntryStream
            | ArchiveCapability.Test
            | ArchiveCapability.Extract
            | ArchiveCapability.ReadEncrypted
            | ArchiveCapability.ReadVolumes
            | ArchiveCapability.Create
            | ArchiveCapability.UpdateByRewrite
            | ArchiveCapability.EncryptContent
            | ArchiveCapability.EncryptNames
            | ArchiveCapability.CreateVolumes;
        if (archive is null
            || archive.SessionId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(archive.Format)
            || string.IsNullOrWhiteSpace(archive.ProviderId)
            || archive.EntryCount < 0
            || archive.Fingerprint is null
            || archive.Fingerprint.Length < 0
            || !IsSha256Hex(archive.Fingerprint.PrefixSha256)
            || archive.Warnings is null
            || (archive.Capabilities & ~knownCapabilities) != 0
            || !archive.Capabilities.HasFlag(ArchiveCapability.List)
            || archive.IsMultipart != archive.Capabilities.HasFlag(ArchiveCapability.ReadVolumes)
            || !IsVolumeSetDescriptorValid(
                archive,
                _enabledFeatures.Contains(ProtocolFeatures.VolumeSetIdentity))
            || (archive.IsMultipart
                && (archive.Capabilities & (ArchiveCapability.OpenEntryStream
                    | ArchiveCapability.Test
                    | ArchiveCapability.Extract)) != 0)
            || archive.Warnings.Any(warning =>
                warning is null
                || string.IsNullOrWhiteSpace(warning.Code)
                || string.IsNullOrWhiteSpace(warning.Message)))
        {
            throw ProtocolFailure("The worker returned an invalid archive descriptor.");
        }
    }

    private void ValidateNestedArchiveResponse(
        OpenNestedArchiveResponse? response,
        ArchiveSessionId expectedParentSessionId,
        EntryId expectedEntryId,
        long requestedMaximumBytes)
    {
        if (response is null)
        {
            throw ProtocolFailure("The worker returned a missing nested archive response.");
        }

        ValidateArchiveDescriptor(response.Archive);
        if (response.ParentSessionId != expectedParentSessionId
            || response.SourceEntryId != expectedEntryId
            || response.Archive.SessionId == expectedParentSessionId
            || response.Depth is <= 0 or > ProtocolConstants.MaximumNestedArchiveDepth
            || response.MaterializedBytes is <= 0
            || response.MaterializedBytes > requestedMaximumBytes
            || response.ChainMaterializedBytes < response.MaterializedBytes
            || response.ChainMaterializedBytes > ProtocolConstants.MaximumNestedArchiveChainBytes
            || response.Archive.Fingerprint.Length != response.MaterializedBytes)
        {
            throw ProtocolFailure("The worker returned inconsistent nested archive identity or byte accounting.");
        }
    }

    private static bool IsVolumeSetDescriptorValid(
        ArchiveDescriptor archive,
        bool volumeSetIdentityEnabled)
    {
        if (!volumeSetIdentityEnabled)
        {
            return archive.VolumeSetKind is null;
        }

        if (archive.VolumeSetKind is not { } volumeSetKind
            || !Enum.IsDefined(volumeSetKind))
        {
            return false;
        }

        const ArchiveCapability dataOperationCapabilities = ArchiveCapability.OpenEntryStream
            | ArchiveCapability.Test
            | ArchiveCapability.Extract
            | ArchiveCapability.UpdateByRewrite;
        var readsVolumes = archive.Capabilities.HasFlag(ArchiveCapability.ReadVolumes);
        var advertisesDataOperations = (archive.Capabilities & dataOperationCapabilities) != 0;
        return volumeSetKind switch
        {
            ArchiveVolumeSetKind.Single => !archive.IsMultipart && !readsVolumes,
            ArchiveVolumeSetKind.ConfirmedMultipart => archive.IsMultipart
                && readsVolumes
                && !advertisesDataOperations,
            ArchiveVolumeSetKind.Unknown => !archive.IsMultipart
                && !readsVolumes
                && !advertisesDataOperations,
            _ => false,
        };
    }

    private void ValidateEntryPage(ListEntriesResponse? response, int requestedOffset, int requestedLimit)
    {
        if (response is null
            || response.Entries is null
            || response.Offset != requestedOffset
            || response.TotalCount < 0
            || response.Entries.Count > requestedLimit)
        {
            throw ProtocolFailure("The worker returned invalid entry page bounds.");
        }

        var expectedNext = (long)response.Offset + response.Entries.Count;
        if (response.NextOffset is { } next
            && (next != expectedNext || next <= response.Offset || next > response.TotalCount))
        {
            throw ProtocolFailure("The worker returned a non-advancing entry page.");
        }

        if (response.NextOffset is null && expectedNext < response.TotalCount)
        {
            throw ProtocolFailure("The worker ended an entry page before the declared entry count.");
        }

        foreach (var entry in response.Entries)
        {
            var persistentKeysEnabled = _enabledFeatures.Contains(ProtocolFeatures.PersistentEntryKeys);
            if (entry is null
                || entry.EntryId.Value == Guid.Empty
                || entry.ParentEntryId?.Value == Guid.Empty
                || string.IsNullOrEmpty(entry.RawPath)
                || string.IsNullOrEmpty(entry.DisplayName)
                || !Enum.IsDefined(entry.Kind)
                || !Enum.IsDefined(entry.SafetyStatus)
                || entry.OriginalSize is < 0
                || entry.CompressedSize is < 0
                || entry.DuplicateOrdinal < 0
                || (persistentKeysEnabled && string.IsNullOrEmpty(entry.NormalizedPath))
                || (!persistentKeysEnabled
                    && (entry.NormalizedPath is not null || entry.DuplicateOrdinal != 0)))
            {
                throw ProtocolFailure("The worker returned invalid archive entry metadata.");
            }
        }

        if (response.Entries.Select(entry => entry.EntryId).Distinct().Count() != response.Entries.Count)
        {
            throw ProtocolFailure("The worker returned duplicate archive entry identifiers.");
        }
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    private void ValidateExtractionRecoveryListResponse(
        ListExtractionRecoveriesResponse response)
    {
        if (response?.Recoveries is null
            || response.Recoveries.Count > ProtocolConstants.MaximumExtractionRecoveryRecords
            || response.ActiveOperationCount < 0
            || response.SuspiciousOperationCount < 0
            || response.IncompleteJournalCount < 0
            || response.IncompleteJournalCount > response.Recoveries.Count
            || (long)response.Recoveries.Count
                + response.ActiveOperationCount
                + response.SuspiciousOperationCount
                > ProtocolConstants.MaximumExtractionRecoveryRecords)
        {
            throw ProtocolFailure(
                "The worker returned invalid extraction recovery inventory bounds.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        var incompleteCount = 0;
        foreach (var recovery in response.Recoveries)
        {
            ValidateExtractionRecoverySummary(recovery);
            if (!identifiers.Add(recovery.RecoveryId)
                || previous is not null
                    && string.Compare(previous, recovery.RecoveryId, StringComparison.Ordinal) >= 0)
            {
                throw ProtocolFailure(
                    "The worker returned duplicate or unordered extraction recovery identifiers.");
            }

            previous = recovery.RecoveryId;
            if (recovery.HasIncompleteTail)
            {
                incompleteCount++;
            }
        }

        if (incompleteCount != response.IncompleteJournalCount)
        {
            throw ProtocolFailure(
                "The worker returned inconsistent extraction recovery tail counts.");
        }
    }

    private void ValidateExtractionRecoveryInspectionResponse(
        InspectExtractionRecoveryResponse response,
        string requestedRecoveryId)
    {
        if (response is null)
        {
            throw ProtocolFailure("The worker returned a missing extraction recovery inspection.");
        }

        ValidateExtractionRecoverySummary(response.Recovery);
        if (!string.Equals(
                response.Recovery.RecoveryId,
                requestedRecoveryId,
                StringComparison.Ordinal)
            || !IsCanonicalRecoveryDestination(response.DestinationPath)
            || response.IsRemoteDestination
                != (Path.GetPathRoot(response.DestinationPath)?.StartsWith(
                    @"\\",
                    StringComparison.Ordinal) == true)
            || response.SupportsMutation
            || response.SupportsRetry
            || response.SupportsCleanup
            || !Enum.IsDefined(response.DestinationParent)
            || !Enum.IsDefined(response.Assessment)
            || !IsCanonicalUtcTimestamp(response.InspectedAtUtc))
        {
            throw ProtocolFailure(
                "The worker returned invalid extraction recovery inspection identity or authority.");
        }

        var hasJournalEvidence = response.Recovery.Phase >= ExtractionRecoveryPhase.Finalized;
        if (hasJournalEvidence != (response.JournalFileCount is not null)
            || hasJournalEvidence != (response.JournalDirectoryCount is not null)
            || hasJournalEvidence != (response.JournalTotalBytes is not null)
            || hasJournalEvidence != (response.JournalNamedStreamCount is not null)
            || hasJournalEvidence != (response.JournalRevision is not null))
        {
            throw ProtocolFailure(
                "The worker returned partial extraction recovery journal evidence.");
        }

        if (hasJournalEvidence
            && (response.JournalFileCount is < 0
                    or > ProtocolConstants.MaximumExtractionRecoveryObjects
                || response.JournalDirectoryCount is < 0
                    or > ProtocolConstants.MaximumExtractionRecoveryObjects
                || (long)response.JournalFileCount.GetValueOrDefault()
                    + response.JournalDirectoryCount.GetValueOrDefault()
                    > ProtocolConstants.MaximumExtractionRecoveryObjects
                || response.JournalTotalBytes is < 0
                    or > ProtocolConstants.MaximumExtractionRecoveryBytes
                || response.JournalNamedStreamCount is < 0
                    or > ProtocolConstants.MaximumExtractionRecoveryObjects
                || response.JournalNamedStreamCount > response.JournalFileCount
                || !IsValidRecoveryId(response.JournalRevision!)))
        {
            throw ProtocolFailure(
                "The worker returned out-of-bounds extraction recovery journal evidence.");
        }

        ValidateExtractionRecoveryArtifact(response.StagingArtifact);
        ValidateExtractionRecoveryArtifact(response.FinalArtifact);
        var expectedAssessment = ExpectedExtractionRecoveryAssessment(
            response.Recovery,
            response.DestinationParent,
            response.StagingArtifact.State,
            response.FinalArtifact.State);
        if (response.Assessment != expectedAssessment)
        {
            throw ProtocolFailure(
                "The worker returned an inconsistent extraction recovery assessment.");
        }
    }

    private void ValidateExtractionRecoverySummary(ExtractionRecoverySummary recovery)
    {
        if (recovery is null
            || !IsValidRecoveryId(recovery.RecoveryId)
            || !Enum.IsDefined(recovery.State)
            || !Enum.IsDefined(recovery.Phase)
            || recovery.Sequence != (int)recovery.Phase + 1
            || !IsCanonicalUtcTimestamp(recovery.RecordedAtUtc)
            || recovery.State != recovery.Phase switch
            {
                ExtractionRecoveryPhase.Admitted or ExtractionRecoveryPhase.Writing =>
                    ExtractionRecoveryInventoryState.Incomplete,
                ExtractionRecoveryPhase.Finalized => ExtractionRecoveryInventoryState.Finalized,
                ExtractionRecoveryPhase.CommitIntent =>
                    ExtractionRecoveryInventoryState.PublicationUncertain,
                ExtractionRecoveryPhase.Committed =>
                    ExtractionRecoveryInventoryState.PublishedWithoutTerminal,
                _ => (ExtractionRecoveryInventoryState)(-1),
            })
        {
            throw ProtocolFailure("The worker returned an invalid extraction recovery summary.");
        }
    }

    private void ValidateExtractionRecoveryArtifact(
        ExtractionRecoveryArtifactInspection artifact)
    {
        if (artifact is null || !Enum.IsDefined(artifact.State))
        {
            throw ProtocolFailure("The worker returned an invalid extraction recovery artifact.");
        }

        var hasEvidence = artifact.State is
            ExtractionRecoveryArtifactState.IdentityMatchRevisionUnbound
            or ExtractionRecoveryArtifactState.ExactMatch
            or ExtractionRecoveryArtifactState.RevisionMismatch;
        if (hasEvidence != (artifact.ObservedFileCount is not null)
            || hasEvidence != (artifact.ObservedDirectoryCount is not null)
            || hasEvidence != (artifact.ObservedTotalBytes is not null)
            || hasEvidence != (artifact.ObservedNamedStreamCount is not null)
            || hasEvidence != (artifact.ObservedRevision is not null))
        {
            throw ProtocolFailure(
                "The worker returned partial extraction recovery artifact evidence.");
        }

        if (hasEvidence
            && (artifact.ObservedFileCount is < 0
                    or > ProtocolConstants.MaximumExtractionRecoveryObjects
                || artifact.ObservedDirectoryCount is < 0
                    or > ProtocolConstants.MaximumExtractionRecoveryObjects
                || (long)artifact.ObservedFileCount.GetValueOrDefault()
                    + artifact.ObservedDirectoryCount.GetValueOrDefault()
                    > ProtocolConstants.MaximumExtractionRecoveryObjects
                || artifact.ObservedTotalBytes is < 0
                    or > ProtocolConstants.MaximumExtractionRecoveryBytes
                || artifact.ObservedNamedStreamCount is < 0
                    or > ProtocolConstants.MaximumExtractionRecoveryObjects
                || artifact.ObservedNamedStreamCount > artifact.ObservedFileCount
                || !IsValidRecoveryId(artifact.ObservedRevision!)))
        {
            throw ProtocolFailure(
                "The worker returned out-of-bounds extraction recovery artifact evidence.");
        }
    }

    private static ExtractionRecoveryAssessment ExpectedExtractionRecoveryAssessment(
        ExtractionRecoverySummary recovery,
        ExtractionRecoveryParentState parent,
        ExtractionRecoveryArtifactState staging,
        ExtractionRecoveryArtifactState final)
    {
        if (parent != ExtractionRecoveryParentState.MatchesJournal)
        {
            return parent == ExtractionRecoveryParentState.IdentityMismatch
                ? ExtractionRecoveryAssessment.ParentChanged
                : ExtractionRecoveryAssessment.InspectionIncomplete;
        }

        if (recovery.HasIncompleteTail)
        {
            return ExtractionRecoveryAssessment.InspectionIncomplete;
        }

        var stagingOwned = IsOwned(staging);
        var finalOwned = IsOwned(final);
        if (stagingOwned && finalOwned)
        {
            return ExtractionRecoveryAssessment.MultipleOrConflictingArtifacts;
        }

        if (staging == ExtractionRecoveryArtifactState.Excessive
            || final == ExtractionRecoveryArtifactState.Excessive)
        {
            return ExtractionRecoveryAssessment.Excessive;
        }

        if (IsIncomplete(staging) || IsIncomplete(final))
        {
            return ExtractionRecoveryAssessment.InspectionIncomplete;
        }

        if (IsConflicting(staging) || IsConflicting(final))
        {
            return ExtractionRecoveryAssessment.ChangedOrSuspicious;
        }

        if (stagingOwned)
        {
            return recovery.Phase < ExtractionRecoveryPhase.Finalized
                ? ExtractionRecoveryAssessment.OwnedIncompleteStagingObserved
                : ExtractionRecoveryAssessment.OwnedFinalizedStagingObserved;
        }

        return finalOwned
            ? ExtractionRecoveryAssessment.OwnedFinalObserved
            : ExtractionRecoveryAssessment.NoOwnedArtifactObserved;

        static bool IsOwned(ExtractionRecoveryArtifactState state) =>
            state is ExtractionRecoveryArtifactState.IdentityMatchRevisionUnbound
                or ExtractionRecoveryArtifactState.ExactMatch;

        static bool IsIncomplete(ExtractionRecoveryArtifactState state) =>
            state is ExtractionRecoveryArtifactState.NotInspected
                or ExtractionRecoveryArtifactState.AccessDenied
                or ExtractionRecoveryArtifactState.Unavailable;

        static bool IsConflicting(ExtractionRecoveryArtifactState state) =>
            state is ExtractionRecoveryArtifactState.NotBoundByJournal
                or ExtractionRecoveryArtifactState.IdentityMismatch
                or ExtractionRecoveryArtifactState.RevisionMismatch
                or ExtractionRecoveryArtifactState.WrongKind
                or ExtractionRecoveryArtifactState.ReparsePoint
                or ExtractionRecoveryArtifactState.UnexpectedHardLinks
                or ExtractionRecoveryArtifactState.UnexpectedDataStreams;
    }

    private static bool IsCanonicalRecoveryDestination(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith("//?/", StringComparison.Ordinal)
            || path.StartsWith("//./", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(Path.GetFileName(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void ValidateRecoveryListResponse(
        ListArchiveCreationRecoveriesResponse response)
    {
        if (response.Recoveries is null
            || response.Recoveries.Count > ProtocolConstants.MaximumArchiveCreationRecoveryRecords
            || response.ActiveOperationCount < 0
            || response.SuspiciousOperationCount < 0
            || response.IncompleteJournalCount < 0
            || response.IncompleteJournalCount > response.Recoveries.Count)
        {
            throw ProtocolFailure(
                "The worker returned invalid archive-creation recovery inventory bounds.");
        }


        var observedInventoryEntries = (long)response.Recoveries.Count
            + response.ActiveOperationCount
            + response.SuspiciousOperationCount;
        if (observedInventoryEntries > ProtocolConstants.MaximumArchiveCreationRecoveryRecords)
        {
            throw ProtocolFailure(
                "The worker returned an archive-creation recovery inventory beyond its scan bound.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        var incompleteCount = 0;
        foreach (var recovery in response.Recoveries)
        {
            ValidateRecoverySummary(recovery);
            if (!identifiers.Add(recovery.RecoveryId)
                || (previous is not null
                    && string.Compare(previous, recovery.RecoveryId, StringComparison.Ordinal) >= 0))
            {
                throw ProtocolFailure(
                    "The worker returned duplicate or unordered archive-creation recovery identifiers.");
            }

            previous = recovery.RecoveryId;
            if (recovery.HasIncompleteTail)
            {
                incompleteCount++;
            }
        }

        if (incompleteCount != response.IncompleteJournalCount)
        {
            throw ProtocolFailure(
                "The worker returned inconsistent archive-creation recovery tail counts.");
        }
    }

    private void ValidateRecoveryInspectionResponse(
        InspectArchiveCreationRecoveryResponse response,
        string requestedRecoveryId)
    {
        if (response is null)
        {
            throw ProtocolFailure(
                "The worker returned an empty archive-creation recovery inspection.");
        }
        ValidateRecoverySummary(response.Recovery);
        if (!string.Equals(
                response.Recovery.RecoveryId,
                requestedRecoveryId,
                StringComparison.Ordinal)
            || !Enum.IsDefined(response.DestinationParent)
            || response.StagingArtifact is null
            || response.FinalArtifact is null
            || !Enum.IsDefined(response.StagingArtifact.State)
            || !Enum.IsDefined(response.FinalArtifact.State)
            || !Enum.IsDefined(response.Assessment)
            || response.DestinationParent == ArchiveCreationRecoveryParentState.NotInspected
            || !IsOrdinaryAbsoluteRecoveryPath(response.DestinationPath)
            || !string.Equals(
                response.StagingFileName,
                $".rainarchives-{requestedRecoveryId}.zip.staging",
                StringComparison.Ordinal)
            || response.InspectedAtUtc.Offset != TimeSpan.Zero
            || response.InspectedAtUtc == default
            || response.IsRemoteDestination != IsUncPath(response.DestinationPath))
        {
            throw ProtocolFailure(
                "The worker returned invalid archive-creation recovery inspection metadata.");
        }

        var phaseHasBoundLength = response.Recovery.Phase
            is ArchiveCreationRecoveryPhase.Finalized
            or ArchiveCreationRecoveryPhase.CommitIntent
            or ArchiveCreationRecoveryPhase.Committed;
        if (phaseHasBoundLength != response.JournalBoundLength.HasValue
            || response.JournalBoundLength is < 0)
        {
            throw ProtocolFailure(
                "The worker returned an invalid archive-creation recovery journal length.");
        }

        ValidateRecoveryArtifact(response.StagingArtifact, response.JournalBoundLength);
        ValidateRecoveryArtifact(response.FinalArtifact, response.JournalBoundLength);
        if (!IsArtifactStateCompatibleWithPhase(
                response.StagingArtifact.State,
                response.Recovery.Phase)
            || !IsArtifactStateCompatibleWithPhase(
                response.FinalArtifact.State,
                response.Recovery.Phase))
        {
            throw ProtocolFailure(
                "The worker returned archive-creation recovery evidence incompatible with the journal phase.");
        }
        var parentMatched = response.DestinationParent
            == ArchiveCreationRecoveryParentState.MatchesJournal;
        var childrenInspected = response.StagingArtifact.State
                != ArchiveCreationRecoveryArtifactState.NotInspected
            && response.FinalArtifact.State
                != ArchiveCreationRecoveryArtifactState.NotInspected;
        if (parentMatched != childrenInspected)
        {
            throw ProtocolFailure(
                "The worker returned inconsistent archive-creation recovery parent evidence.");
        }

        if (response.Recovery.HasIncompleteTail
            && response.Assessment != ArchiveCreationRecoveryAssessment.InspectionIncomplete)
        {
            throw ProtocolFailure(
                "The worker treated an incomplete recovery journal as complete evidence.");
        }

        var expectedAssessment = ComputeRecoveryAssessment(response);
        if (response.Assessment != expectedAssessment)
        {
            throw ProtocolFailure(
                "The worker returned an inconsistent archive-creation recovery assessment.");
        }
    }

    private void ValidateRecoveryDispositionResponse(
        ApplyArchiveCreationRecoveryDispositionResponse response,
        string requestedRecoveryId,
        ArchiveCreationRecoveryDispositionAction requestedAction)
    {
        if (response is null
            || !IsValidRecoveryId(response.RecoveryId)
            || !string.Equals(
                response.RecoveryId,
                requestedRecoveryId,
                StringComparison.Ordinal)
            || !Enum.IsDefined(response.Action)
            || response.Action != requestedAction
            || !Enum.IsDefined(response.Result)
            || !Enum.IsDefined(response.FinalAssessment)
            || !IsUtcTimestamp(response.ResolvedAtUtc))
        {
            throw ProtocolFailure(
                "The worker returned invalid archive-creation recovery disposition metadata.");
        }

        var expectedAssessment = requestedAction is
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging
            or ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal
            ? ArchiveCreationRecoveryAssessment.OwnedFinalObserved
            : ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved;
        if (response.FinalAssessment != expectedAssessment)
        {
            throw ProtocolFailure(
                "The worker returned an archive-creation recovery disposition assessment that does not match the requested action.");
        }
    }

    private void ValidateNestedWorkspaceRecoveryListResponse(
        ListNestedWorkspaceRecoveriesResponse response)
    {
        if (response is null
            || response.Recoveries is null
            || response.Recoveries.Count > ProtocolConstants.MaximumNestedWorkspaceRecoveryRecords
            || response.ActiveWorkspaceCount < 0
            || response.SuspiciousWorkspaceCount < 0)
        {
            throw ProtocolFailure(
                "The worker returned invalid nested-workspace recovery inventory bounds.");
        }

        var observedEntries = (long)response.Recoveries.Count
            + response.ActiveWorkspaceCount
            + response.SuspiciousWorkspaceCount;
        if (observedEntries > ProtocolConstants.MaximumNestedWorkspaceRecoveryRecords)
        {
            throw ProtocolFailure(
                "The worker returned a nested-workspace recovery inventory beyond its scan bound.");
        }

        string? previous = null;
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recovery in response.Recoveries)
        {
            if (recovery is null
                || !IsValidRecoveryId(recovery.RecoveryId)
                || !identifiers.Add(recovery.RecoveryId)
                || (previous is not null
                    && string.Compare(previous, recovery.RecoveryId, StringComparison.Ordinal) >= 0))
            {
                throw ProtocolFailure(
                    "The worker returned invalid, duplicate, or unordered nested-workspace recovery identifiers.");
            }

            previous = recovery.RecoveryId;
        }
    }

    private void ValidateNestedWorkspaceRecoveryInspectionResponse(
        InspectNestedWorkspaceRecoveryResponse response,
        string requestedRecoveryId)
    {
        if (response is null
            || !IsValidRecoveryId(response.RecoveryId)
            || !string.Equals(response.RecoveryId, requestedRecoveryId, StringComparison.Ordinal)
            || !Enum.IsDefined(response.Assessment)
            || !IsUtcTimestamp(response.InspectedAtUtc))
        {
            throw ProtocolFailure(
                "The worker returned invalid nested-workspace recovery inspection metadata.");
        }

        var verified = response.Assessment
            == NestedWorkspaceRecoveryAssessment.VerifiedDisposable;
        if (verified)
        {
            if (response.SourceCount is null
                || response.TotalSourceBytes is null
                || response.SourceCount is < 0 or > ProtocolConstants.MaximumOpenSessions
                || response.TotalSourceBytes is < 0
                    or > ProtocolConstants.MaximumActiveNestedArchiveBytes
                || !IsValidRecoveryId(response.Revision)
                || (response.SourceCount == 0 && response.TotalSourceBytes != 0))
            {
                throw ProtocolFailure(
                    "The worker returned invalid verified nested-workspace recovery evidence.");
            }
        }
        else if (response.SourceCount is not null
                 || response.TotalSourceBytes is not null
                 || response.Revision is not null)
        {
            throw ProtocolFailure(
                "The worker disclosed disposition evidence for an unverified nested-workspace recovery.");
        }
    }

    private void ValidateNestedWorkspaceRecoveryDiscardResponse(
        DiscardNestedWorkspaceRecoveryResponse response,
        string requestedRecoveryId)
    {
        if (response is null
            || !IsValidRecoveryId(response.RecoveryId)
            || !string.Equals(response.RecoveryId, requestedRecoveryId, StringComparison.Ordinal)
            || !Enum.IsDefined(response.Result)
            || !IsUtcTimestamp(response.ResolvedAtUtc))
        {
            throw ProtocolFailure(
                "The worker returned invalid nested-workspace recovery discard metadata.");
        }
    }

    private void ValidateNestedWorkspaceReopenInspectionResponse(
        InspectNestedWorkspaceReopenResponse response,
        string requestedRecoveryId)
    {
        if (response is null
            || !IsValidRecoveryId(response.RecoveryId)
            || !string.Equals(response.RecoveryId, requestedRecoveryId, StringComparison.Ordinal)
            || !Enum.IsDefined(response.Assessment)
            || !IsUtcTimestamp(response.InspectedAtUtc))
        {
            throw ProtocolFailure(
                "The worker returned invalid nested-workspace reopen inspection metadata.");
        }

        var verified = response.Assessment
            == NestedWorkspaceRecoveryAssessment.VerifiedDisposable;
        if (!verified)
        {
            if (response.SourceCount is not null
                || response.TotalSourceBytes is not null
                || response.Revision is not null
                || response.Sources is not null
                || response.SupportsIndependentReopen)
            {
                throw ProtocolFailure(
                    "The worker disclosed reopen authority for an unverified nested-workspace recovery.");
            }

            return;
        }

        if (response.SourceCount is null
            || response.TotalSourceBytes is null
            || response.Sources is null
            || response.SourceCount is < 0 or > ProtocolConstants.MaximumOpenSessions
            || response.TotalSourceBytes is < 0
                or > ProtocolConstants.MaximumActiveNestedArchiveBytes
            || response.Sources.Count != response.SourceCount
            || !IsValidRecoveryId(response.Revision))
        {
            throw ProtocolFailure(
                "The worker returned invalid verified nested-workspace reopen evidence.");
        }

        long total = 0;
        string? previous = null;
        foreach (var source in response.Sources)
        {
            if (source is null
                || !IsValidRecoveryId(source.SourceId)
                || source.Length is < 0 or > ProtocolConstants.MaximumNestedArchiveBytes
                || !IsValidRecoveryId(source.ContentSha256)
                || (previous is not null
                    && string.Compare(previous, source.SourceId, StringComparison.Ordinal) >= 0))
            {
                throw ProtocolFailure(
                    "The worker returned invalid, duplicate, or unordered nested-workspace source evidence.");
            }

            total = checked(total + source.Length);
            previous = source.SourceId;
        }

        if (total != response.TotalSourceBytes
            || response.SupportsIndependentReopen != response.Sources.Any(source => source.Length > 0))
        {
            throw ProtocolFailure(
                "The worker returned inconsistent nested-workspace source accounting.");
        }
    }

    private void ValidateNestedWorkspaceReopenResponse(
        ReopenNestedWorkspaceRecoveryResponse response,
        string expectedRecoveryId,
        string expectedSourceId,
        long expectedSourceLength)
    {
        if (response is null
            || !string.Equals(response.RecoveryId, expectedRecoveryId, StringComparison.Ordinal)
            || !string.Equals(response.SourceId, expectedSourceId, StringComparison.Ordinal)
            || response.Mode != NestedWorkspaceRecoveryReopenMode.IndependentRoot
            || response.Depth != 0
            || response.MaterializedBytes != expectedSourceLength
            || !IsUtcTimestamp(response.ReopenedAtUtc))
        {
            throw ProtocolFailure(
                "The worker returned inconsistent independent recovery-reopen metadata.");
        }

        ValidateArchiveDescriptor(response.Archive);
        if (response.Archive.Fingerprint.Length != expectedSourceLength)
        {
            throw ProtocolFailure(
                "The independently reopened archive length did not match the selected recovery source.");
        }
    }

    private void ValidateRecoverySummary(ArchiveCreationRecoverySummary recovery)
    {
        if (recovery is null
            || !IsValidRecoveryId(recovery.RecoveryId)
            || recovery.OperationId.Value == Guid.Empty
            || !Enum.IsDefined(recovery.Phase)
            || recovery.Sequence != (int)recovery.Phase + 1
            || recovery.RecordedAtUtc.Offset != TimeSpan.Zero
            || recovery.RecordedAtUtc == default)
        {
            throw ProtocolFailure(
                "The worker returned invalid archive-creation recovery metadata.");
        }
    }

    private void ValidateRecoveryArtifact(
        ArchiveCreationRecoveryArtifactInspection artifact,
        long? journalBoundLength)
    {
        if (artifact is null
            || !Enum.IsDefined(artifact.State)
            || artifact.ObservedLength is < 0
            || artifact.LinkCount is < 0)
        {
            throw ProtocolFailure(
                "The worker returned invalid archive-creation recovery artifact evidence.");
        }

        var openedRegularFile = artifact.State is
            ArchiveCreationRecoveryArtifactState.NotBoundByJournal
            or ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound
            or ArchiveCreationRecoveryArtifactState.ExactMatch
            or ArchiveCreationRecoveryArtifactState.IdentityMismatch
            or ArchiveCreationRecoveryArtifactState.LengthMismatch
            or ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks;
        if (openedRegularFile != (artifact.ObservedLength.HasValue && artifact.LinkCount.HasValue)
            || (openedRegularFile && artifact.LinkCount < 1)
            || (artifact.State == ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks
                && artifact.LinkCount == 1)
            || (artifact.State != ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks
                && openedRegularFile
                && artifact.LinkCount != 1)
            || (artifact.State == ArchiveCreationRecoveryArtifactState.ExactMatch
                && artifact.ObservedLength != journalBoundLength)
            || (artifact.State == ArchiveCreationRecoveryArtifactState.LengthMismatch
                && (journalBoundLength is null
                    || artifact.ObservedLength == journalBoundLength))
            || ((artifact.State is ArchiveCreationRecoveryArtifactState.NotBoundByJournal
                    or ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound)
                && journalBoundLength is not null))
        {
            throw ProtocolFailure(
                "The worker returned inconsistent archive-creation recovery artifact evidence.");
        }
    }

    private static ArchiveCreationRecoveryAssessment ComputeRecoveryAssessment(
        InspectArchiveCreationRecoveryResponse response)
    {
        if (response.Recovery.HasIncompleteTail)
        {
            return ArchiveCreationRecoveryAssessment.InspectionIncomplete;
        }

        if (response.DestinationParent is ArchiveCreationRecoveryParentState.AccessDenied
            or ArchiveCreationRecoveryParentState.Unavailable)
        {
            return ArchiveCreationRecoveryAssessment.InspectionIncomplete;
        }

        if (response.DestinationParent != ArchiveCreationRecoveryParentState.MatchesJournal)
        {
            return ArchiveCreationRecoveryAssessment.ParentChanged;
        }

        var staging = response.StagingArtifact.State;
        var destination = response.FinalArtifact.State;
        if (IsIncompleteRecoveryArtifact(staging)
            || IsIncompleteRecoveryArtifact(destination))
        {
            return ArchiveCreationRecoveryAssessment.InspectionIncomplete;
        }

        if (IsConflictingRecoveryArtifact(staging)
            || IsConflictingRecoveryArtifact(destination))
        {
            return ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts;
        }

        var stagingOwned = IsOwnedRecoveryArtifact(staging);
        var destinationOwned = IsOwnedRecoveryArtifact(destination);
        if (stagingOwned && destinationOwned)
        {
            return ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts;
        }

        if (stagingOwned)
        {
            return response.Recovery.Phase == ArchiveCreationRecoveryPhase.Committed
                ? ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts
                : ArchiveCreationRecoveryAssessment.OwnedStagingObserved;
        }

        if (destinationOwned)
        {
            return response.Recovery.Phase is ArchiveCreationRecoveryPhase.CommitIntent
                or ArchiveCreationRecoveryPhase.Committed
                ? ArchiveCreationRecoveryAssessment.OwnedFinalObserved
                : ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts;
        }

        return ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved;
    }

    private static bool IsOwnedRecoveryArtifact(ArchiveCreationRecoveryArtifactState state) =>
        state is ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound
            or ArchiveCreationRecoveryArtifactState.ExactMatch;

    private static bool IsConflictingRecoveryArtifact(ArchiveCreationRecoveryArtifactState state) =>
        state is ArchiveCreationRecoveryArtifactState.NotBoundByJournal
            or ArchiveCreationRecoveryArtifactState.IdentityMismatch
            or ArchiveCreationRecoveryArtifactState.LengthMismatch
            or ArchiveCreationRecoveryArtifactState.WrongKind
            or ArchiveCreationRecoveryArtifactState.ReparsePoint
            or ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks;

    private static bool IsIncompleteRecoveryArtifact(ArchiveCreationRecoveryArtifactState state) =>
        state is ArchiveCreationRecoveryArtifactState.NotInspected
            or ArchiveCreationRecoveryArtifactState.AccessDenied
            or ArchiveCreationRecoveryArtifactState.Unavailable;

    private static bool IsArtifactStateCompatibleWithPhase(
        ArchiveCreationRecoveryArtifactState state,
        ArchiveCreationRecoveryPhase phase) =>
        state switch
        {
            ArchiveCreationRecoveryArtifactState.NotBoundByJournal =>
                phase == ArchiveCreationRecoveryPhase.Admitted,
            ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound =>
                phase == ArchiveCreationRecoveryPhase.Writing,
            ArchiveCreationRecoveryArtifactState.ExactMatch
                or ArchiveCreationRecoveryArtifactState.LengthMismatch =>
                phase is ArchiveCreationRecoveryPhase.Finalized
                    or ArchiveCreationRecoveryPhase.CommitIntent
                    or ArchiveCreationRecoveryPhase.Committed,
            ArchiveCreationRecoveryArtifactState.IdentityMismatch =>
                phase != ArchiveCreationRecoveryPhase.Admitted,
            _ => true,
        };

    private static bool IsValidRecoveryId(string? recoveryId) =>
        recoveryId is { Length: 64 }
        && recoveryId.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static void ValidateRecoveryIdArgument(string recoveryId, string parameterName)
    {
        if (!IsValidRecoveryId(recoveryId))
        {
            throw new ArgumentException(
                "Recovery evidence must be 64 lowercase hexadecimal characters.",
                parameterName);
        }
    }

    private static bool IsUtcTimestamp(DateTimeOffset timestamp) =>
        timestamp != default && timestamp.Offset == TimeSpan.Zero;

    private static bool IsOrdinaryAbsoluteRecoveryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith("//?/", StringComparison.Ordinal)
            || path.StartsWith("//./", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (!string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal))
            {
                return false;
            }

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length >= path.Length)
            {
                return false;
            }

            var relative = path[root.Length..];
            var components = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.None);
            return components.Length > 0 && components.All(IsSafeRecoveryPathComponent);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsUncPath(string path) =>
        Path.GetPathRoot(path)?.StartsWith(@"\\", StringComparison.Ordinal) == true;

    private static bool IsSafeRecoveryPathComponent(string component)
    {
        if (string.IsNullOrEmpty(component)
            || component is "." or ".."
            || component.EndsWith('.')
            || component.EndsWith(' ')
            || component.Any(character =>
                character == '\0'
                || character == ':'
                || character is '<' or '>' or '"' or '|' or '?' or '*'
                || char.IsControl(character)))
        {
            return false;
        }

        var baseName = component.Split('.')[0].TrimEnd(' ', '.');
        return !RecoveryReservedNames.Contains(baseName);
    }

    private static IReadOnlySet<string> CreateRecoveryReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
        };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        foreach (var suffix in new[] { '¹', '²', '³' })
        {
            names.Add($"COM{suffix}");
            names.Add($"LPT{suffix}");
        }

        return names;
    }

    private ArchiveClientException ProtocolFailure(string message) =>
        AbortTransport(new InvalidDataException(message));

    private static ArchiveClientException ToTransportError(Exception exception)
    {
        if (exception is ArchiveClientException clientException)
        {
            return clientException;
        }

        var protocolFailure = exception is InvalidDataException or JsonException;
        return new ArchiveClientException(new ArchiveError(
            protocolFailure ? "protocol.invalid_worker_frame" : "worker.disconnected",
            protocolFailure ? ArchiveErrorCategory.ProtocolMismatch : ArchiveErrorCategory.WorkerFailure,
            protocolFailure
                ? "The archive worker sent an invalid protocol frame."
                : "The archive worker disconnected before completing the request.",
            !protocolFailure));
    }

    private static uint GetConnectedProcessId(SafePipeHandle pipeHandle)
    {
        if (!NativeMethods.GetNamedPipeClientProcessId(pipeHandle, out var processId))
        {
            throw new InvalidOperationException("Could not verify the worker pipe client process.");
        }

        return processId;
    }

    private static bool BootstrapNonceMatches(string received, string expected)
    {
        var receivedBytes = Encoding.UTF8.GetBytes(received);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        try
        {
            return CryptographicOperations.FixedTimeEquals(receivedBytes, expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(receivedBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private sealed class PendingRequest
    {
        private readonly SemaphoreSlim _slots;
        private int _slotReleased;

        public PendingRequest(
            long sequence,
            SemaphoreSlim slots,
            string expectedResponseKind,
            bool authorizesOperation)
        {
            Sequence = sequence;
            ExpectedResponseKind = expectedResponseKind;
            AuthorizesOperation = authorizesOperation;
            _slots = slots;
            Completion = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public long Sequence { get; }
        public string ExpectedResponseKind { get; }
        public bool AuthorizesOperation { get; }
        public TaskCompletionSource<ProtocolFrame> Completion { get; }

        public void ReleaseSlot()
        {
            if (Interlocked.Exchange(ref _slotReleased, 1) == 0)
            {
                _slots.Release();
            }
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    }
}
