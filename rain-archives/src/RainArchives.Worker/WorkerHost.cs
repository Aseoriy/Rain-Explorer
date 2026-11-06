using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.SevenZip;
using RainArchives.Providers.SharpCompress;
using RainArchives.Providers.WinRar;
using RainArchives.Providers.Zip;
using RainArchives.Security;

namespace RainArchives.Worker;

internal sealed class WorkerHost : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly string _bootstrapNonce;
    private readonly ArchiveProviderRegistry _providers;
    private readonly ArchiveWriterProviderRegistry _writerProviders;
    private readonly SecretSlotStore _secrets = new();
    private readonly StagedExtractionWriter _extractionWriter;
    private readonly ExtractionRecoveryStore _extractionRecoveryStore;
    private readonly ArchiveCreationRecoveryStore _creationRecoveryStore;
    private readonly Action<ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint>?
        _recoveryDispositionCheckpoint;
    private readonly Action<
        NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint>?
        _nestedRecoveryDiscardCheckpoint;
    private StagedArchiveCreationWriter _creationWriter;
    private readonly StagedZipRebuildCopyWriter _zipRebuildCopyWriter;
    private readonly ZipRebuildCopyPolicy _zipRebuildCopyPolicy = new();
    private readonly ZipRebuildTreeEditPolicy _zipRebuildTreeEditPolicy;
    private readonly ZipRebuildSourceFolderReviewPolicy _zipRebuildSourceFolderReviewPolicy = new();
    private readonly NestedTemporaryWorkspaceRecoveryStore _nestedRecoveryStore;
    private readonly ConcurrentDictionary<ArchiveSessionId, WorkerArchiveSession> _sessions = new();
    private readonly ConcurrentDictionary<OperationId, WorkerOperation> _operations = new();
    private readonly object _operationActivityGate = new();
    private readonly LinkedList<OperationActivitySnapshot> _retainedOperationActivity = new();
    private readonly ConcurrentDictionary<Guid, WorkerRequestCancellation> _requestCancellations = new();
    private readonly HashSet<Guid> _seenRequestIds = [];
    private readonly ConcurrentDictionary<long, Task> _dispatchTasks = new();
    private readonly ConcurrentDictionary<OperationId, Task> _operationTasks = new();
    private readonly ConcurrentDictionary<EntryStreamId, Task> _entryStreamTasks = new();
    private readonly ConcurrentDictionary<EntryStreamId, byte> _issuedEntryStreamIds = new();
    private readonly ConcurrentDictionary<EntryStreamId, WorkerRequestCancellation> _entryStreamCancellations = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _dispatchGate = new(8, 8);
    private readonly SemaphoreSlim _controlDispatchSlots = new(
        ProtocolConstants.MaximumControlRequests,
        ProtocolConstants.MaximumControlRequests);
    private readonly SemaphoreSlim _openExecutionGate = new(2, 2);
    private readonly SemaphoreSlim _operationExecutionGate = new(
        ProtocolConstants.MaximumConcurrentOperations,
        ProtocolConstants.MaximumConcurrentOperations);
    private readonly SemaphoreSlim _entryStreamExecutionGate = new(2, 2);
    private readonly SemaphoreSlim _recoveryInspectionGate = new(1, 1);
    private readonly SemaphoreSlim _sessionSlots = new(
        ProtocolConstants.MaximumOpenSessions,
        ProtocolConstants.MaximumOpenSessions);
    private readonly SemaphoreSlim _operationSlots = new(
        ProtocolConstants.MaximumQueuedOperations,
        ProtocolConstants.MaximumQueuedOperations);
    private readonly SemaphoreSlim _entryStreamSlots = new(
        ProtocolConstants.MaximumEntryStreamLeases,
        ProtocolConstants.MaximumEntryStreamLeases);
    private readonly string _nestedTemporaryRoot;
    private readonly NestedTemporaryWorkspaceInventory _nestedWorkspaceInventory;
    private readonly Func<WinRarWorkspaceMaintenanceResult> _rarWorkspaceMaintenanceAction;
    private WinRarWorkspaceMaintenanceResult _rarWorkspaceMaintenance =
        new(0, 0, 0, false, false);
    private Task? _rarWorkspaceMaintenanceTask;
    private readonly ArchiveCreationRecoveryInventory _creationRecoveryInventory;
    private readonly ExtractionRecoveryInventory _extractionRecoveryInventory;
    private readonly Lazy<OwnedNestedTemporaryWorkspace> _nestedWorkspace;
    private readonly object _nestedBudgetGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private IReadOnlySet<string> _enabledFeatures = new HashSet<string>(StringComparer.Ordinal);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private long _nextDispatchId;
    private long _lastRequestSequence;
    private long _activeNestedArchiveBytes;
    private bool _operationActivityHistoryTruncated;
    private int _negotiatedProtocolMinor = -1;

    public WorkerHost(
        Stream stream,
        string bootstrapNonce,
        string? creationRecoveryRoot = null,
        string? extractionRecoveryRoot = null)
        : this(
            stream,
            bootstrapNonce,
            [
                new SevenZipProviderGate(gateApproved: false),
                new SharpCompressArchiveProvider(),
            ],
            nestedTemporaryRoot: null,
            writerProviders: CreateDefaultWriterProviders(),
            creationRecoveryRoot: creationRecoveryRoot,
            extractionRecoveryRoot: extractionRecoveryRoot)
    {
    }

    internal WorkerHost(
        Stream stream,
        string bootstrapNonce,
        string? creationRecoveryRoot,
        string? extractionRecoveryRoot,
        Action<ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint>
            recoveryDispositionCheckpoint)
        : this(
            stream,
            bootstrapNonce,
            [
                new SevenZipProviderGate(gateApproved: false),
                new SharpCompressArchiveProvider(),
            ],
            nestedTemporaryRoot: null,
            writerProviders: CreateDefaultWriterProviders(),
            creationRecoveryRoot: creationRecoveryRoot,
            recoveryMaintenanceNowUtc: null,
            recoveryDispositionCheckpoint: recoveryDispositionCheckpoint,
            extractionRecoveryRoot: extractionRecoveryRoot)
    {
        ArgumentNullException.ThrowIfNull(recoveryDispositionCheckpoint);
    }

    internal WorkerHost(
        Stream stream,
        string bootstrapNonce,
        string? creationRecoveryRoot,
        string? extractionRecoveryRoot,
        Action<NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint>
            nestedRecoveryDiscardCheckpoint)
        : this(
            stream,
            bootstrapNonce,
            [
                new SevenZipProviderGate(gateApproved: false),
                new SharpCompressArchiveProvider(),
            ],
            nestedTemporaryRoot: null,
            writerProviders: CreateDefaultWriterProviders(),
            creationRecoveryRoot: creationRecoveryRoot,
            recoveryMaintenanceNowUtc: null,
            recoveryDispositionCheckpoint: null,
            nestedRecoveryDiscardCheckpoint: nestedRecoveryDiscardCheckpoint,
            extractionRecoveryRoot: extractionRecoveryRoot)
    {
        ArgumentNullException.ThrowIfNull(nestedRecoveryDiscardCheckpoint);
    }

    internal WorkerHost(
        Stream stream,
        string bootstrapNonce,
        IEnumerable<IArchiveProvider> providers,
        string? nestedTemporaryRoot = null,
        IEnumerable<IArchiveWriterProvider>? writerProviders = null,
        string? creationRecoveryRoot = null,
        DateTimeOffset? recoveryMaintenanceNowUtc = null,
        Action<ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint>?
            recoveryDispositionCheckpoint = null,
        Action<NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint>?
            nestedRecoveryDiscardCheckpoint = null,
        ArchiveCreationResourceGuard? zipRebuildCreationResourceGuard = null,
        ExtractionResourceGuard? zipRebuildDecodedResourceGuard = null,
        string? extractionRecoveryRoot = null,
        Func<WinRarWorkspaceMaintenanceResult>? rarWorkspaceMaintenanceAction = null)
    {
        _stream = stream;
        _bootstrapNonce = bootstrapNonce;
        _providers = new ArchiveProviderRegistry(providers);
        _rarWorkspaceMaintenanceAction = rarWorkspaceMaintenanceAction
            ?? (static () => WinRarArchiveWriterProvider.PerformStartupMaintenance());
        _writerProviders = new ArchiveWriterProviderRegistry(
            writerProviders ?? CreateDefaultWriterProviders());
        _creationRecoveryStore = new ArchiveCreationRecoveryStore(
            creationRecoveryRoot ?? ArchiveCreationRecoveryStore.GetDefaultRootDirectory());
        _extractionRecoveryStore = new ExtractionRecoveryStore(
            extractionRecoveryRoot ?? ExtractionRecoveryStore.GetDefaultRootDirectory());
        _extractionWriter = new StagedExtractionWriter(
            recoveryStore: _extractionRecoveryStore);
        _recoveryDispositionCheckpoint = recoveryDispositionCheckpoint;
        _nestedRecoveryDiscardCheckpoint = nestedRecoveryDiscardCheckpoint;
        _creationRecoveryInventory = _creationRecoveryStore.InspectForStartup(
            recoveryMaintenanceNowUtc ?? DateTimeOffset.UtcNow,
            CancellationToken.None);
        _extractionRecoveryInventory = _extractionRecoveryStore.Inspect();
        _creationWriter = new StagedArchiveCreationWriter(_creationRecoveryStore);
        _zipRebuildCopyWriter = new StagedZipRebuildCopyWriter(
            _creationRecoveryStore,
            creationResourceGuard: zipRebuildCreationResourceGuard,
            decodedResourceGuard: zipRebuildDecodedResourceGuard);
        _zipRebuildTreeEditPolicy = new ZipRebuildTreeEditPolicy(copyPolicy: _zipRebuildCopyPolicy);
        _nestedTemporaryRoot = Path.GetFullPath(nestedTemporaryRoot ?? Path.GetTempPath());
        if (!Directory.Exists(_nestedTemporaryRoot))
        {
            throw new DirectoryNotFoundException("The nested-archive temporary root does not exist.");
        }

        _nestedWorkspaceInventory = OwnedNestedTemporaryWorkspace.Inspect(_nestedTemporaryRoot);
        _nestedRecoveryStore = new NestedTemporaryWorkspaceRecoveryStore(_nestedTemporaryRoot);
        _nestedWorkspace = new Lazy<OwnedNestedTemporaryWorkspace>(
            () => OwnedNestedTemporaryWorkspace.Create(_nestedTemporaryRoot),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static IReadOnlyList<IArchiveWriterProvider> CreateDefaultWriterProviders()
    {
        var providers = new List<IArchiveWriterProvider>
        {
            new BuiltInZipArchiveWriterProvider(),
            new BuiltInTarArchiveWriterProvider(),
            new BuiltInTarGzipArchiveWriterProvider(),
            new BuiltInTarBzip2ArchiveWriterProvider(),
            new BuiltInGzipArchiveWriterProvider(),
            new BuiltInBzip2ArchiveWriterProvider(),
        };

        if (WinRarArchiveWriterProvider.TryCreateInstalled(
                out var rarProvider,
                out _)
            && rarProvider is not null)
        {
            providers.Add(rarProvider);
        }

        return providers;
    }

    private void RunRarWorkspaceMaintenance()
    {
        WinRarWorkspaceMaintenanceResult result;
        try
        {
            result = _rarWorkspaceMaintenanceAction();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArchiveProviderException
                or System.ComponentModel.Win32Exception)
        {
            result = new WinRarWorkspaceMaintenanceResult(0, 0, 0, false, true);
        }

        Volatile.Write(ref _rarWorkspaceMaintenance, result);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
        {
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await HandshakeAsync(handshakeTimeout.Token).ConfigureAwait(false);
        }

        _rarWorkspaceMaintenanceTask = Task.Run(
            RunRarWorkspaceMaintenance,
            CancellationToken.None);

        while (!linked.Token.IsCancellationRequested)
        {
            (ProtocolFrameType Type, ProtocolFrame? Json, SecretFrame? Secret) incoming;
            try
            {
                incoming = await ProtocolStream.ReadAsync(_stream, linked.Token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            if (incoming.Type == ProtocolFrameType.Secret)
            {
                try
                {
                    _secrets.Store(incoming.Secret!.SlotId, incoming.Secret.SecretBytes);
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(incoming.Secret!.SecretBytes);
                }

                continue;
            }

            var frame = incoming.Json!;
            ValidateRequestEnvelope(frame);
            if (_dispatchTasks.Count >=
                ProtocolConstants.MaximumPendingRequests + ProtocolConstants.MaximumControlRequests)
            {
                throw new InvalidDataException("The worker request queue is full.");
            }

            var controlRequest = IsControlRequest(frame.Kind);
            if (controlRequest && !_controlDispatchSlots.Wait(0))
            {
                throw new InvalidDataException("The worker control request queue is full.");
            }

            WorkerRequestCancellation? requestCancellation = null;
            if ((frame.Kind is ProtocolMessageKinds.OpenArchive
                    or ProtocolMessageKinds.OpenNestedArchive
                    or ProtocolMessageKinds.OpenEntryStream
                    or ProtocolMessageKinds.ListArchiveCreationRecoveries
                    or ProtocolMessageKinds.InspectArchiveCreationRecovery
                    or ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition
                    or ProtocolMessageKinds.ListNestedWorkspaceRecoveries
                    or ProtocolMessageKinds.InspectNestedWorkspaceRecovery
                    or ProtocolMessageKinds.DiscardNestedWorkspaceRecovery
                    or ProtocolMessageKinds.InspectNestedWorkspaceReopen
                    or ProtocolMessageKinds.ReopenNestedWorkspaceRecovery
                    or ProtocolMessageKinds.GetZipRebuildSourceFolderReview
                    or ProtocolMessageKinds.GetOperationActivity
                    or ProtocolMessageKinds.GetSupportDiagnostics
                    or ProtocolMessageKinds.ListExtractionRecoveries
                    or ProtocolMessageKinds.InspectExtractionRecovery)
                && _enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
            {
                requestCancellation = new WorkerRequestCancellation(linked.Token);
                if (!_requestCancellations.TryAdd(frame.RequestId, requestCancellation))
                {
                    requestCancellation.Dispose();
                    throw new InvalidDataException("A cancellable request identifier was reused.");
                }
            }

            var dispatchId = Interlocked.Increment(ref _nextDispatchId);
            var dispatchTask = DispatchWithReleaseAsync(
                frame,
                linked.Token,
                requestCancellation,
                controlRequest);
            _dispatchTasks[dispatchId] = dispatchTask;
            _ = dispatchTask.ContinueWith(
                _completed => _dispatchTasks.TryRemove(dispatchId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
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
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var operation in _operations.Values)
        {
            operation.TryCancel();
        }

        await AwaitTrackedWorkAsync().ConfigureAwait(false);
        if (_rarWorkspaceMaintenanceTask is { } rarWorkspaceMaintenanceTask)
        {
            await rarWorkspaceMaintenanceTask.ConfigureAwait(false);
        }
        foreach (var pair in _sessions.ToArray())
        {
            if (!_sessions.TryRemove(pair.Key, out var session))
            {
                continue;
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Continue disposing independent sessions during worker shutdown.
            }
            finally
            {
                _sessionSlots.Release();
            }
        }

        if (_nestedWorkspace.IsValueCreated)
        {
            try
            {
                await _nestedWorkspace.Value.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve unproven workspace contents; a later worker inventories the durable owner claim.
            }
        }

        _secrets.Dispose();
        _writeGate.Dispose();
        _dispatchGate.Dispose();
        _controlDispatchSlots.Dispose();
        _openExecutionGate.Dispose();
        _operationExecutionGate.Dispose();
        _entryStreamExecutionGate.Dispose();
        _recoveryInspectionGate.Dispose();
        _sessionSlots.Dispose();
        _operationSlots.Dispose();
        _entryStreamSlots.Dispose();
        _shutdown.Dispose();
    }

    private async Task DispatchWithReleaseAsync(
        ProtocolFrame frame,
        CancellationToken connectionCancellationToken,
        WorkerRequestCancellation? requestCancellation,
        bool controlSlotHeld)
    {
        var dispatchSlotHeld = false;
        try
        {
            if (!controlSlotHeld)
            {
                await _dispatchGate
                    .WaitAsync(requestCancellation?.Token ?? connectionCancellationToken)
                    .ConfigureAwait(false);
                dispatchSlotHeld = true;
            }

            await DispatchAsync(
                    frame,
                    requestCancellation?.Token ?? connectionCancellationToken,
                    connectionCancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            requestCancellation?.Token.IsCancellationRequested == true
            && !connectionCancellationToken.IsCancellationRequested)
        {
            SettleQueuedRequestSecret(frame);
            await SendErrorAsync(
                frame,
                ToError(exception),
                connectionCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            _shutdown.IsCancellationRequested
            && exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            if (requestCancellation is not null)
            {
                _requestCancellations.TryRemove(frame.RequestId, out _);
                requestCancellation.Dispose();
            }

            if (dispatchSlotHeld)
            {
                _dispatchGate.Release();
            }

            if (controlSlotHeld)
            {
                _controlDispatchSlots.Release();
            }
        }
    }

    private async Task AwaitTrackedWorkAsync()
    {
        while (true)
        {
            foreach (var operation in _operations.Values)
            {
                operation.TryCancel();
            }

            var tasks = _dispatchTasks.Values
                .Concat(_operationTasks.Values)
                .Concat(_entryStreamTasks.Values)
                .Distinct()
                .ToArray();
            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // Individual request failures have already been translated or the pipe is gone.
            }
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var build = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var advertisedFeatures = ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion);
        var hello = new WorkerHelloRequest(
            ProtocolConstants.MajorVersion,
            ProtocolConstants.MinorVersion,
            build,
            _bootstrapNonce,
            advertisedFeatures);
        await WriteJsonAsync(
            ProtocolMessageKinds.WorkerHello,
            Guid.Empty,
            hello,
            0,
            cancellationToken).ConfigureAwait(false);

        var incoming = await ProtocolStream.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
        if (incoming.Type != ProtocolFrameType.Json
            || incoming.Json?.Kind != ProtocolMessageKinds.ClientWelcome
            || incoming.Json.RequestId != Guid.Empty
            || incoming.Json.Sequence != 0)
        {
            throw new InvalidDataException("Client did not complete the worker handshake.");
        }

        var welcome = incoming.Json.ReadPayload<ClientWelcomeResponse>();
        if (welcome.ProtocolMajor != ProtocolConstants.MajorVersion
            || welcome.ProtocolMinor < 0
            || welcome.ProtocolMinor > ProtocolConstants.MinorVersion
            || string.IsNullOrWhiteSpace(welcome.ClientBuild)
            || welcome.ClientBuild.Length > 128)
        {
            throw new InvalidDataException("Client and worker protocol major versions do not match.");
        }

        if (welcome.EnabledFeatures is null
            || welcome.EnabledFeatures.Count > 64
            || welcome.EnabledFeatures.Any(string.IsNullOrWhiteSpace)
            || welcome.EnabledFeatures.Distinct(StringComparer.Ordinal).Count() != welcome.EnabledFeatures.Count)
        {
            throw new InvalidDataException("Client handshake features were missing.");
        }

        var enabled = welcome.EnabledFeatures.ToHashSet(StringComparer.Ordinal);
        if (ProtocolFeatures.RequiredVersion1.Any(feature => !enabled.Contains(feature)))
        {
            throw new InvalidDataException("Client did not enable all required worker protocol features.");
        }

        if (enabled.Contains(ProtocolFeatures.ArchiveCreationRecoveryInspection)
            && !enabled.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new InvalidDataException(
                "Client enabled archive-creation recovery inspection without request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.ArchiveCreationRecoveryDisposition)
            && (!enabled.Contains(ProtocolFeatures.ArchiveCreationRecoveryInspection)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled archive-creation recovery disposition without inspection and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.NestedWorkspaceRecovery)
            && !enabled.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new InvalidDataException(
                "Client enabled nested-workspace recovery without request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.ZipRebuildCopy)
            && (!enabled.Contains(ProtocolFeatures.RequestCancellation)
                || !enabled.Contains(ProtocolFeatures.PersistentEntryKeys)
                || !enabled.Contains(ProtocolFeatures.VolumeSetIdentity)
                || !enabled.Contains(ProtocolFeatures.ZipCreation)))
        {
            throw new InvalidDataException(
                "Client enabled ZIP Rebuild Copy without its cancellation, persistent-key, volume-identity, and ZIP-creation dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.ZipRebuildRename)
            && (!enabled.Contains(ProtocolFeatures.ZipRebuildCopy)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)
                || !enabled.Contains(ProtocolFeatures.PersistentEntryKeys)
                || !enabled.Contains(ProtocolFeatures.VolumeSetIdentity)
                || !enabled.Contains(ProtocolFeatures.ZipCreation)))
        {
            throw new InvalidDataException(
                "Client enabled ZIP Rebuild Rename without its Rebuild Copy, cancellation, persistent-key, volume-identity, and ZIP-creation dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
            && (!enabled.Contains(ProtocolFeatures.ZipRebuildRename)
                || !enabled.Contains(ProtocolFeatures.ZipRebuildCopy)
                || !enabled.Contains(ProtocolFeatures.ZipDirectoryCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)
                || !enabled.Contains(ProtocolFeatures.PersistentEntryKeys)
                || !enabled.Contains(ProtocolFeatures.VolumeSetIdentity)
                || !enabled.Contains(ProtocolFeatures.ZipCreation)))
        {
            throw new InvalidDataException(
                "Client enabled ZIP Rebuild Tree Edit without its rename, copy, directory-creation, cancellation, persistent-key, volume-identity, and ZIP-creation dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
            && (!enabled.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
                || !enabled.Contains(ProtocolFeatures.ZipDirectoryCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)
                || !enabled.Contains(ProtocolFeatures.ZipCreation)))
        {
            throw new InvalidDataException(
                "Client enabled ZIP Rebuild source-folder review without its tree-edit, directory-creation, cancellation, and ZIP-creation dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.ZipRebuildMetadata)
            && (!enabled.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
                || !enabled.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
                || !enabled.Contains(ProtocolFeatures.ZipRebuildRename)
                || !enabled.Contains(ProtocolFeatures.ZipRebuildCopy)
                || !enabled.Contains(ProtocolFeatures.ZipDirectoryCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)
                || !enabled.Contains(ProtocolFeatures.PersistentEntryKeys)
                || !enabled.Contains(ProtocolFeatures.VolumeSetIdentity)
                || !enabled.Contains(ProtocolFeatures.ZipCreation)))
        {
            throw new InvalidDataException(
                "Client enabled ZIP Rebuild Metadata without all preceding rebuild and transport dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.ZipWriteCapabilities)
            && (!enabled.Contains(ProtocolFeatures.ZipRebuildMetadata)
                || !enabled.Contains(ProtocolFeatures.ZipRebuildTreeEdit)
                || !enabled.Contains(ProtocolFeatures.ZipRebuildSourceFolderReview)
                || !enabled.Contains(ProtocolFeatures.ZipRebuildRename)
                || !enabled.Contains(ProtocolFeatures.ZipRebuildCopy)
                || !enabled.Contains(ProtocolFeatures.ZipDirectoryCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)
                || !enabled.Contains(ProtocolFeatures.PersistentEntryKeys)
                || !enabled.Contains(ProtocolFeatures.VolumeSetIdentity)
                || !enabled.Contains(ProtocolFeatures.ZipCreation)))
        {
            throw new InvalidDataException(
                "Client enabled ZIP write capabilities without all preceding rebuild and transport dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.TarCreation)
            && !enabled.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new InvalidDataException(
                "Client enabled TAR creation without request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.TarDirectoryCreation)
            && (!enabled.Contains(ProtocolFeatures.TarCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled TAR directory creation without TAR creation and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.TarWriteCapabilities)
            && (!enabled.Contains(ProtocolFeatures.TarCreation)
                || !enabled.Contains(ProtocolFeatures.TarDirectoryCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled TAR write capabilities without both TAR creation routes and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.RarCreation)
            && !enabled.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new InvalidDataException(
                "Client enabled RAR creation without request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.RarDirectoryCreation)
            && (!enabled.Contains(ProtocolFeatures.RarCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled RAR directory creation without RAR creation and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.RarWriteCapabilities)
            && (!enabled.Contains(ProtocolFeatures.RarCreation)
                || !enabled.Contains(ProtocolFeatures.RarDirectoryCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled RAR write capabilities without both RAR creation routes and request cancellation.");
        }

        ValidateManagedCreationFeatureDependencies(enabled);

        if (enabled.Contains(ProtocolFeatures.OperationActivity)
            && ProtocolFeatures.OperationActivityDependencies.Any(
                dependency => !enabled.Contains(dependency)))
        {
            throw new InvalidDataException(
                "Client enabled operation activity without operation events and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.ExtractionRecoveryInspection)
            && ProtocolFeatures.ExtractionRecoveryInspectionDependencies.Any(
                dependency => !enabled.Contains(dependency)))
        {
            throw new InvalidDataException(
                "Client enabled extraction recovery inspection without new-destination extraction, source validation, and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.NestedWorkspaceIndependentReopen)
            && ProtocolFeatures.NestedWorkspaceIndependentReopenDependencies.Any(
                dependency => !enabled.Contains(dependency)))
        {
            throw new InvalidDataException(
                "Client enabled independent nested-workspace reopen without all recovery, nested-open, secret, cancellation, and source-validation dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.SupportDiagnostics)
            && ProtocolFeatures.SupportDiagnosticsDependencies.Any(
                dependency => !enabled.Contains(dependency)))
        {
            throw new InvalidDataException(
                "Client enabled support diagnostics without operation activity and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.ExpandedCreationTreeBudget)
            && ProtocolFeatures.ExpandedCreationTreeBudgetDependencies.Any(
                dependency => !enabled.Contains(dependency)))
        {
            throw new InvalidDataException(
                "Client enabled the expanded creation-tree budget without cancellation and source validation.");
        }

        var supported = ProtocolFeatures
            .SupportedForMinorVersion(welcome.ProtocolMinor)
            .ToHashSet(StringComparer.Ordinal);
        if (enabled.Any(feature => !supported.Contains(feature)))
        {
            throw new InvalidDataException("Client enabled a feature unavailable in the negotiated protocol minor version.");
        }

        var advertised = advertisedFeatures.ToHashSet(StringComparer.Ordinal);
        if (enabled.Any(feature => !advertised.Contains(feature)))
        {
            throw new InvalidDataException("Client enabled a feature the worker did not advertise.");
        }

        _negotiatedProtocolMinor = welcome.ProtocolMinor;
        _enabledFeatures = enabled;
        // Only authenticated, mutually negotiated creation gets the larger expansion budget.
        // Rebuild owns a separate default policy and all request validators keep their old bounds.
        _creationWriter = new StagedArchiveCreationWriter(
            _creationRecoveryStore,
            new ArchiveCreationPolicy(maximumTreeEntries:
                enabled.Contains(ProtocolFeatures.ExpandedCreationTreeBudget)
                    ? ProtocolConstants.MaximumExpandedCreationTreeEntries
                    : ProtocolConstants.MaximumZipCreationEntries));
    }

    private static void ValidateManagedCreationFeatureDependencies(IReadOnlySet<string> enabled)
    {
        if (enabled.Contains(ProtocolFeatures.TarGZipCreation)
            && (!enabled.Contains(ProtocolFeatures.TarCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled TAR.GZ creation without TAR creation and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.TarGZipDirectoryCreation)
            && (!enabled.Contains(ProtocolFeatures.TarGZipCreation)
                || !enabled.Contains(ProtocolFeatures.TarDirectoryCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled TAR.GZ directory creation without its compressed and TAR tree dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.TarBZip2Creation)
            && (!enabled.Contains(ProtocolFeatures.TarCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled TAR.BZ2 creation without TAR creation and request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.TarBZip2DirectoryCreation)
            && (!enabled.Contains(ProtocolFeatures.TarBZip2Creation)
                || !enabled.Contains(ProtocolFeatures.TarDirectoryCreation)
                || !enabled.Contains(ProtocolFeatures.RequestCancellation)))
        {
            throw new InvalidDataException(
                "Client enabled TAR.BZ2 directory creation without its compressed and TAR tree dependencies.");
        }

        if (enabled.Contains(ProtocolFeatures.GZipCreation)
            && !enabled.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new InvalidDataException(
                "Client enabled GZIP creation without request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.BZip2Creation)
            && !enabled.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new InvalidDataException(
                "Client enabled BZIP2 creation without request cancellation.");
        }

        if (enabled.Contains(ProtocolFeatures.ManagedWriteCapabilities)
            && ProtocolFeatures.ManagedWriteCapabilitiesDependencies.Any(
                dependency => !enabled.Contains(dependency)))
        {
            throw new InvalidDataException(
                "Client enabled managed write capabilities without every managed creation dependency.");
        }
    }

    private async Task DispatchAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken,
        CancellationToken responseCancellationToken)
    {
        try
        {
            switch (frame.Kind)
            {
                case ProtocolMessageKinds.CreateSecretSlot:
                    await CreateSecretSlotAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.DiscardSecretSlot:
                    await DiscardSecretSlotAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.OpenArchive:
                    await OpenArchiveAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.OpenNestedArchive:
                    await OpenNestedArchiveAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.ListEntries:
                    await ListEntriesAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.OpenEntryStream:
                    await OpenEntryStreamAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.CloseArchive:
                    await CloseArchiveAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartExtraction:
                    await StartExtractionAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartIntegrityTest:
                    await StartIntegrityTestAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartZipCreation:
                    await StartZipCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartZipTreeCreation:
                    await StartZipTreeCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartTarCreation:
                    await StartTarCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartTarTreeCreation:
                    await StartTarTreeCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartRarCreation:
                    await StartRarCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartRarTreeCreation:
                    await StartRarTreeCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartTarGZipCreation:
                    await StartTarGZipCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartTarGZipTreeCreation:
                    await StartTarGZipTreeCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartTarBZip2Creation:
                    await StartTarBZip2CreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartTarBZip2TreeCreation:
                    await StartTarBZip2TreeCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartGZipCreation:
                    await StartGZipCreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartBZip2Creation:
                    await StartBZip2CreationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartZipRebuildCopy:
                    await StartZipRebuildCopyAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartZipRebuildRename:
                    await StartZipRebuildRenameAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.GetZipRebuildTreePlan:
                    await GetZipRebuildTreePlanAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.GetZipRebuildSourceFolderReview:
                    await GetZipRebuildSourceFolderReviewAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartZipRebuildTreeEdit:
                    await StartZipRebuildTreeEditAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.StartZipRebuildMetadata:
                    await StartZipRebuildMetadataAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.GetZipWriteCapabilities:
                    await GetZipWriteCapabilitiesAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.GetTarWriteCapabilities:
                    await GetTarWriteCapabilitiesAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.GetRarWriteCapabilities:
                    await GetRarWriteCapabilitiesAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.GetManagedWriteCapabilities:
                    await GetManagedWriteCapabilitiesAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.GetOperationActivity:
                    await GetOperationActivityAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.GetSupportDiagnostics:
                    await GetSupportDiagnosticsAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.ListExtractionRecoveries:
                    await ListExtractionRecoveriesAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.InspectExtractionRecovery:
                    await InspectExtractionRecoveryAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.ListArchiveCreationRecoveries:
                    await ListArchiveCreationRecoveriesAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.InspectArchiveCreationRecovery:
                    await InspectArchiveCreationRecoveryAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition:
                    await ApplyArchiveCreationRecoveryDispositionAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.ListNestedWorkspaceRecoveries:
                    await ListNestedWorkspaceRecoveriesAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.InspectNestedWorkspaceRecovery:
                    await InspectNestedWorkspaceRecoveryAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.DiscardNestedWorkspaceRecovery:
                    await DiscardNestedWorkspaceRecoveryAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.InspectNestedWorkspaceReopen:
                    await InspectNestedWorkspaceReopenAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.ReopenNestedWorkspaceRecovery:
                    await ReopenNestedWorkspaceRecoveryAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.CancelOperation:
                    await CancelOperationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.CancelEntryStream:
                    await CancelEntryStreamAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.CancelRequest:
                    await CancelRequestAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolMessageKinds.CloseWorker:
                    await CloseWorkerAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.InvalidRequest,
                        "protocol.unknown_message",
                        "The worker received an unknown request type.");
            }
        }
        catch (Exception exception)
        {
            await SendErrorAsync(frame, ToError(exception), responseCancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreateSecretSlotAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        _ = frame.ReadPayload<CreateSecretSlotRequest>();
        var slot = _secrets.Create();
        await SendResponseAsync(
            frame,
            new CreateSecretSlotResponse(slot.Id, slot.ExpiresAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DiscardSecretSlotAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<DiscardSecretSlotRequest>();
        if (request.SlotId == Guid.Empty)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "secret.invalid_slot",
                "The secret slot identifier is invalid.");
        }

        await SendResponseAsync(
            frame,
            new DiscardSecretSlotResponse(_secrets.Discard(request.SlotId)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenArchiveAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<OpenArchiveRequest>();
        var password = Array.Empty<byte>();
        var sessionSlotHeld = false;
        var openExecutionSlotHeld = false;
        WorkerArchiveSession? registeredSession = null;
        try
        {
            var sourcePath = CanonicalizeRequestPath(request.SourcePath, "source.invalid_path");
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Archive source was not found.", sourcePath);
            }

            if (!_sessionSlots.Wait(0))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "session.limit",
                    $"A worker can keep at most {ProtocolConstants.MaximumOpenSessions} archive sessions open.");
            }

            sessionSlotHeld = true;
            await _openExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            openExecutionSlotHeld = true;
            password = _secrets.Consume(request.PasswordSlotId);
            var session = await CreateSessionAsync(
                    sourcePath,
                    password,
                    sourceOwner: null,
                    nestedDepth: 0,
                    nestedChainBytes: 0,
                    parentSessionId: null,
                    sourceEntryId: null,
                    inheritedProvenance: null,
                    nestedByteReservation: null,
                    cancellationToken)
                .ConfigureAwait(false);
            registeredSession = session;
            if (!_sessions.TryAdd(session.Descriptor.SessionId, session))
            {
                throw new InvalidOperationException("Could not allocate an archive session.");
            }

            var responseDescriptor = DescriptorForNegotiatedFeatures(session.Descriptor);
            await SendResponseAsync(
                frame,
                new OpenArchiveResponse(responseDescriptor),
                cancellationToken).ConfigureAwait(false);
            registeredSession = null;
            sessionSlotHeld = false;
        }
        catch
        {
            if (registeredSession is not null)
            {
                _sessions.TryRemove(registeredSession.Descriptor.SessionId, out _);
                await registeredSession.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            if (request.PasswordSlotId is { } passwordSlotId)
            {
                _secrets.Discard(passwordSlotId);
            }

            if (sessionSlotHeld)
            {
                _sessionSlots.Release();
            }

            if (openExecutionSlotHeld)
            {
                _openExecutionGate.Release();
            }
        }
    }

    private async ValueTask<WorkerArchiveSession> CreateSessionAsync(
        string sourcePath,
        ReadOnlyMemory<byte> password,
        IAsyncDisposable? sourceOwner,
        int nestedDepth,
        long nestedChainBytes,
        ArchiveSessionId? parentSessionId,
        EntryId? sourceEntryId,
        DownloadProvenance? inheritedProvenance,
        IDisposable? nestedByteReservation,
        CancellationToken cancellationToken)
    {
        IArchiveHandle? handle = null;
        try
        {
            sourceOwner ??= ArchiveSourceLease.Acquire(sourcePath);
            var openingFingerprint = await ArchiveFingerprintService
                .CreateAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            var prefix = await ArchiveFingerprintService
                .ReadDetectionPrefixAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            var provider = _providers.Select(prefix, sourcePath);
            handle = await provider.OpenAsync(sourcePath, password, cancellationToken).ConfigureAwait(false);
            ArchiveMetadataGuard.Validate(handle.Entries, cancellationToken: cancellationToken);
            var index = ArchiveIndex.Create(handle.Entries, cancellationToken: cancellationToken);
            var safetyPlan = new ExtractionPathPolicy().Plan(index.Entries, cancellationToken);
            var annotations = CreateSafetyAnnotations(safetyPlan.Issues, cancellationToken);
            index = index.WithSafetyAnnotations(annotations, cancellationToken);
            var fingerprint = await ArchiveFingerprintService
                .CreateAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (!ArchiveFingerprintService.Matches(openingFingerprint, fingerprint))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.SourceChanged,
                    "archive.changed_while_opening",
                    "The archive changed while it was being opened. Try again after the file is stable.");
            }

            ValidateVolumeSetContract(handle);
            var capabilities = handle.Capabilities & ~ArchiveCapability.UpdateByRewrite;
            if (_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
                && _zipRebuildCopyPolicy.CanRebuild(handle, index))
            {
                capabilities |= ArchiveCapability.UpdateByRewrite;
            }

            var descriptor = new ArchiveDescriptor(
                ArchiveSessionId.New(),
                handle.Format,
                handle.ProviderId,
                capabilities,
                fingerprint,
                index.Entries.Count,
                handle.IsSolid,
                handle.Entries.Any(entry => entry.IsEncrypted),
                handle.IsMultipart,
                true,
                CreateArchiveWarnings(provider, handle),
                handle.VolumeSetKind);
            var session = new WorkerArchiveSession(
                sourcePath,
                sourceOwner,
                provider,
                handle,
                index,
                descriptor,
                nestedDepth,
                nestedChainBytes,
                parentSessionId,
                sourceEntryId,
                inheritedProvenance,
                nestedByteReservation);
            sourceOwner = null;
            handle = null;
            nestedByteReservation = null;
            return session;
        }
        finally
        {
            await DisposeSessionCreationResourcesAsync(
                    handle,
                    sourceOwner,
                    nestedByteReservation)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeSessionCreationResourcesAsync(
        IArchiveHandle? handle,
        IAsyncDisposable? sourceOwner,
        IDisposable? nestedByteReservation)
    {
        Exception? cleanupFailure = null;
        try
        {
            if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            if (sourceOwner is not null)
            {
                await sourceOwner.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineCleanupFailures(cleanupFailure, exception);
        }

        try
        {
            nestedByteReservation?.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineCleanupFailures(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }
    }

    private IReadOnlyList<ArchiveWarning> CreateArchiveWarnings(
        IArchiveProvider provider,
        IArchiveHandle handle)
    {
        var warnings = new List<ArchiveWarning>();
        if (provider.Id.StartsWith("sharpcompress", StringComparison.Ordinal)
            && handle.Format is "7z" or "rar")
        {
            warnings.Add(new ArchiveWarning(
                "provider.native_archive_fallback",
                "The audited native 7-Zip provider is unavailable; managed read compatibility is in use."));
        }

        if (handle.VolumeSetKind == ArchiveVolumeSetKind.Unknown)
        {
            warnings.Add(new ArchiveWarning(
                "archive.volume_set_unknown",
                "The provider could not prove whether this source is a complete single-volume archive, so data operations are disabled."));
        }

        if (handle.VolumeSetKind == ArchiveVolumeSetKind.Single
            && handle.Capabilities.HasFlag(ArchiveCapability.Extract)
            && !handle.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream))
        {
            warnings.Add(new ArchiveWarning(
                "archive.preview_unavailable",
                "Direct preview is unavailable for this opened archive; integrity testing and extraction may still be available."));
        }

        if (_nestedWorkspaceInventory.PotentialStaleWorkspaceCount > 0)
        {
            warnings.Add(new ArchiveWarning(
                "worker.nested_recovery_required",
                $"A previous worker left {_nestedWorkspaceInventory.PotentialStaleWorkspaceCount} token-verified nested archive workspace(s). Rain preserved them for recovery; no automatic deletion was attempted."));
        }

        if (_nestedWorkspaceInventory.SuspiciousWorkspaceCount > 0
            || _nestedWorkspaceInventory.IsTruncated
            || _nestedWorkspaceInventory.InspectionIncomplete)
        {
            warnings.Add(new ArchiveWarning(
                "worker.nested_inventory_incomplete",
                "Nested temporary workspace inventory found unverified or unreadable state. Rain preserved it and did not attempt automatic cleanup."));
        }

        var rarWorkspaceMaintenance = Volatile.Read(ref _rarWorkspaceMaintenance);
        if (rarWorkspaceMaintenance.RequiresAttention)
        {
            warnings.Add(new ArchiveWarning(
                "worker.rar_workspace_cleanup_incomplete",
                "RAR encoder workspace startup maintenance found unverified or unreadable local state. Rain preserved it and did not follow or recursively clean an unverified path."));
        }

        if (_creationRecoveryInventory.PotentialRecoveryCount > 0)
        {
            warnings.Add(new ArchiveWarning(
                "worker.creation_recovery_required",
                $"A previous worker left {_creationRecoveryInventory.PotentialRecoveryCount} durable archive-creation recovery record(s). Rain preserved them for review; no destination was contacted and no automatic cleanup was attempted."));
        }

        if (_creationRecoveryInventory.SuspiciousOperationCount > 0
            || _creationRecoveryInventory.IncompleteJournalCount > 0
            || _creationRecoveryInventory.IsTruncated
            || _creationRecoveryInventory.InspectionIncomplete)
        {
            warnings.Add(new ArchiveWarning(
                "worker.creation_recovery_inventory_incomplete",
                "Archive-creation recovery inventory found incomplete, unverified, or unreadable local state. Rain preserved it, did not contact recorded destinations, and did not attempt automatic cleanup."));
        }

        if (_extractionRecoveryInventory.PotentialRecoveryCount > 0)
        {
            warnings.Add(new ArchiveWarning(
                "worker.extraction_recovery_required",
                $"A previous worker left {_extractionRecoveryInventory.PotentialRecoveryCount} durable extraction recovery record(s). Rain preserved them for review; no destination was contacted and no automatic cleanup was attempted."));
        }

        if (_extractionRecoveryInventory.ActiveOperationCount > 0
            || _extractionRecoveryInventory.SuspiciousOperationCount > 0
            || _extractionRecoveryInventory.IncompleteJournalCount > 0
            || _extractionRecoveryInventory.IsTruncated
            || _extractionRecoveryInventory.InspectionIncomplete)
        {
            warnings.Add(new ArchiveWarning(
                "worker.extraction_recovery_inventory_incomplete",
                "Extraction recovery inventory found active, incomplete, unverified, or unreadable local state. Rain preserved it, did not contact recorded destinations, and did not attempt automatic cleanup."));
        }

        return warnings;
    }

    private ArchiveDescriptor DescriptorForNegotiatedFeatures(ArchiveDescriptor descriptor) =>
        _enabledFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
            ? descriptor
            : descriptor with { VolumeSetKind = null };

    private async Task OpenNestedArchiveAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<OpenNestedArchiveRequest>();
        var parentPassword = Array.Empty<byte>();
        var nestedPassword = Array.Empty<byte>();
        IDisposable? parentReservation = null;
        NestedByteReservation? byteReservation = null;
        OwnedTemporaryArchiveSource? temporarySource = null;
        WorkerArchiveSession? registeredSession = null;
        var sessionSlotHeld = false;
        var openExecutionSlotHeld = false;
        try
        {
            if (!_enabledFeatures.Contains(ProtocolFeatures.NestedArchiveOpen))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProtocolMismatch,
                    "protocol.feature_not_negotiated",
                    "Nested archive opening was not negotiated for this connection.");
            }

            ValidateNestedArchiveRequest(request);
            var parent = GetSession(request.ParentSessionId);
            EnsureOperationSupportedForSession(parent);
            if (parent.NestedDepth >= ProtocolConstants.MaximumNestedArchiveDepth)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "nested.depth_limit",
                    $"Nested archive depth is limited to {ProtocolConstants.MaximumNestedArchiveDepth} explicit levels.");
            }

            if (!parent.Descriptor.Capabilities.HasFlag(ArchiveCapability.Extract))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "nested.extraction_not_supported",
                    "The selected archive provider cannot integrity-copy this entry for nested opening.");
            }

            if (parent.Descriptor.IsSolid
                && !parent.Descriptor.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "nested.solid_source_deferred",
                    "Nested opening from this solid archive is deferred until the provider can bound skipped decode work.");
            }

            var entry = parent.Index.Get(request.EntryId);
            if (entry.IsSynthetic
                || entry.ProviderEntry is null
                || entry.Descriptor.Kind != ArchiveEntryKind.File)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "nested.requires_file",
                    "Only material file entries can be opened explicitly as nested archives.");
            }

            var remainingChainBytes = checked(
                ProtocolConstants.MaximumNestedArchiveChainBytes - parent.NestedChainBytes);
            if (remainingChainBytes <= 0)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "nested.chain_byte_limit",
                    "The nested archive chain has reached its decoded-byte limit.");
            }

            var effectiveMaximumBytes = Math.Min(request.MaximumBytes, remainingChainBytes);
            if (entry.Descriptor.OriginalSize is { } declaredLength
                && declaredLength > effectiveMaximumBytes)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    declaredLength > request.MaximumBytes
                        ? "nested.declared_size_limit"
                        : "nested.chain_byte_limit",
                    "The entry's declared size exceeds the bounded nested-archive limit.");
            }

            if (!_sessionSlots.Wait(0))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "session.limit",
                    $"A worker can keep at most {ProtocolConstants.MaximumOpenSessions} archive sessions open.");
            }

            sessionSlotHeld = true;
            byteReservation = ReserveNestedArchiveBytes(effectiveMaximumBytes);
            parentReservation = parent.ReserveUse();
            await _openExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            openExecutionSlotHeld = true;
            parentPassword = _secrets.Consume(request.ParentPasswordSlotId);

            DownloadProvenance? inheritedProvenance;
            long materializedBytes;
            var nestedWorkspace = _nestedWorkspace.Value;
            temporarySource = nestedWorkspace.CreateSource();
            await using (var parentLease = await parent.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                await parent.EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
                await parent.ReopenWithPasswordAsync(parentPassword, cancellationToken).ConfigureAwait(false);
                inheritedProvenance = await parent
                    .GetDownloadProvenanceAsync(cancellationToken)
                    .ConfigureAwait(false);
                var policy = new ExtractionSecurityPolicy(
                    DefaultMaximumOutputBytes: effectiveMaximumBytes,
                    MaximumUnknownEntryOutputBytes: effectiveMaximumBytes,
                    DiskSpaceCheckIntervalBytes: 4L * 1024 * 1024);
                var guard = new ExtractionResourceGuard(policy).CreateRuntimeGuard(
                    nestedWorkspace.DirectoryPath,
                    entry.Descriptor.CompressedSize);
                guard.ValidateObjectCreation(
                    totalBytesProcessed: 0,
                    ExtractionObjectKind.File,
                    "source.archive");
                await using var guardedOutput = new RuntimeGuardWriteStream(
                    temporarySource.Output,
                    guard,
                    aggregateStart: 0,
                    entry.Descriptor.OriginalSize,
                    leaveOpen: true);
                await parent.Handle.CopyEntryToAsync(
                    entry.ProviderEntry.Ordinal,
                    guardedOutput,
                    cancellationToken).ConfigureAwait(false);
                await guardedOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                guard.ValidateCompletedEntry(
                    guardedOutput.EntryBytesWritten,
                    entry.Descriptor.OriginalSize);
                materializedBytes = guardedOutput.EntryBytesWritten;
                await parent.EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
            }

            await temporarySource.CompleteAsync(cancellationToken).ConfigureAwait(false);
            byteReservation.Commit(materializedBytes);
            var chainBytes = checked(parent.NestedChainBytes + materializedBytes);
            nestedPassword = _secrets.Consume(request.NestedPasswordSlotId);
            var transferredSource = temporarySource;
            var transferredReservation = byteReservation;
            temporarySource = null;
            byteReservation = null;
            var child = await CreateSessionAsync(
                    transferredSource.SourcePath,
                    nestedPassword,
                    transferredSource,
                    parent.NestedDepth + 1,
                    chainBytes,
                    parent.Descriptor.SessionId,
                    entry.Descriptor.EntryId,
                    inheritedProvenance,
                    transferredReservation,
                    cancellationToken)
                .ConfigureAwait(false);
            registeredSession = child;
            if (!_sessions.TryAdd(child.Descriptor.SessionId, child))
            {
                throw new InvalidOperationException("Could not allocate a nested archive session.");
            }

            await SendResponseAsync(
                frame,
                new OpenNestedArchiveResponse(
                    DescriptorForNegotiatedFeatures(child.Descriptor),
                    parent.Descriptor.SessionId,
                    entry.Descriptor.EntryId,
                    child.NestedDepth,
                    materializedBytes,
                    chainBytes),
                cancellationToken).ConfigureAwait(false);
            registeredSession = null;
            sessionSlotHeld = false;
        }
        catch
        {
            if (registeredSession is not null)
            {
                _sessions.TryRemove(registeredSession.Descriptor.SessionId, out _);
                await registeredSession.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            await CleanupNestedOpenAsync(
                    parentReservation,
                    temporarySource,
                    byteReservation,
                    parentPassword,
                    nestedPassword,
                    request.ParentPasswordSlotId,
                    request.NestedPasswordSlotId,
                    sessionSlotHeld,
                    openExecutionSlotHeld)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask CleanupNestedOpenAsync(
        IDisposable? parentReservation,
        OwnedTemporaryArchiveSource? temporarySource,
        IDisposable? byteReservation,
        byte[] parentPassword,
        byte[] nestedPassword,
        Guid? parentPasswordSlotId,
        Guid? nestedPasswordSlotId,
        bool sessionSlotHeld,
        bool openExecutionSlotHeld)
    {
        Exception? cleanupFailure = null;
        try
        {
            parentReservation?.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            if (temporarySource is not null)
            {
                await temporarySource.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineCleanupFailures(cleanupFailure, exception);
        }

        try
        {
            byteReservation?.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineCleanupFailures(cleanupFailure, exception);
        }

        try
        {
            CryptographicOperations.ZeroMemory(parentPassword);
            CryptographicOperations.ZeroMemory(nestedPassword);
            SettleSecretSlot(parentPasswordSlotId);
            SettleSecretSlot(nestedPasswordSlotId);
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineCleanupFailures(cleanupFailure, exception);
        }

        try
        {
            if (sessionSlotHeld)
            {
                _sessionSlots.Release();
            }

            if (openExecutionSlotHeld)
            {
                _openExecutionGate.Release();
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineCleanupFailures(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }
    }

    private static Exception CombineCleanupFailures(Exception? current, Exception next) =>
        current is null
            ? next
            : new AggregateException("Multiple archive cleanup steps failed.", current, next);

    private async Task ListEntriesAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<ListEntriesRequest>();
        var session = GetSession(request.SessionId);
        if (request.Offset < 0 || request.Limit <= 0 || request.Limit > ProtocolConstants.MaximumPageSize)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "entries.invalid_page",
                "Entry page bounds were invalid.");
        }

        using var reservation = session.ReserveUse();
        await session.EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
        var page = new List<ArchiveEntryDescriptor>(request.Limit);
        long estimatedFrameBytes = 4096;
        var frameBudget = ProtocolConstants.MaximumControlFrameBytes - (32 * 1024);
        var endExclusive = (int)Math.Min(
            session.Index.Entries.Count,
            (long)request.Offset + request.Limit);
        for (var index = request.Offset; index < endExclusive; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = session.Index.Entries[index].Descriptor;
            if (!_enabledFeatures.Contains(ProtocolFeatures.PersistentEntryKeys))
            {
                descriptor = descriptor with
                {
                    NormalizedPath = null,
                    DuplicateOrdinal = 0,
                };
            }

            var descriptorBytes = EstimateListEntryJsonBytes(descriptor);
            if (estimatedFrameBytes + descriptorBytes > frameBudget)
            {
                if (page.Count == 0)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.ResourceLimit,
                        "entries.descriptor_too_large",
                        "An archive entry contains too much metadata for the worker protocol.");
                }

                break;
            }

            estimatedFrameBytes += descriptorBytes;
            page.Add(descriptor);
        }

        int? next = request.Offset + page.Count < session.Index.Entries.Count
            ? request.Offset + page.Count
            : null;
        await SendResponseAsync(
            frame,
            new ListEntriesResponse(page, request.Offset, session.Index.Entries.Count, next),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenEntryStreamAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<OpenEntryStreamRequest>();
        NamedPipeClientStream? dataPipe = null;
        IDisposable? sessionReservation = null;
        TaskCompletionSource? trackedCompletion = null;
        WorkerRequestCancellation? streamCancellation = null;
        var password = Array.Empty<byte>();
        var entryStreamSlotHeld = false;
        var ownershipTransferred = false;
        try
        {
            if (!_enabledFeatures.Contains(ProtocolFeatures.EntryStreamLeases))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProtocolMismatch,
                    "protocol.feature_not_negotiated",
                    "Entry stream leases were not negotiated for this connection.");
            }

            ValidateEntryStreamRequest(request);
            var session = GetSession(request.SessionId);
            EnsureOperationSupportedForSession(session);
            if (!session.Descriptor.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "entry.stream_not_supported",
                    "The selected archive provider cannot open individual entry streams.");
            }

            var entry = session.Index.Get(request.EntryId);
            if (entry.IsSynthetic
                || entry.ProviderEntry is null
                || entry.Descriptor.Kind != ArchiveEntryKind.File)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "entry.stream_requires_file",
                    "Only material file entries can be opened as preview streams.");
            }

            if (entry.Descriptor.OriginalSize is { } declaredLength
                && declaredLength > request.MaximumBytes)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "entry.stream_declared_size_limit",
                    "The entry's declared size exceeds the requested preview-stream limit.");
            }

            if (!_entryStreamSlots.Wait(0))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "entry.stream_limit",
                    $"A worker can keep at most {ProtocolConstants.MaximumEntryStreamLeases} entry streams open.");
            }

            entryStreamSlotHeld = true;
            sessionReservation = session.ReserveUse();
            password = _secrets.Consume(request.PasswordSlotId);
            var streamId = AllocateEntryStreamId();
            (trackedCompletion, streamCancellation) = RegisterEntryStream(streamId);
            dataPipe = new NamedPipeClientStream(
                ".",
                request.PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await dataPipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.WorkerFailure,
                        "entry.stream_pipe_timeout",
                        "The worker could not connect the entry stream data pipe in time.",
                        exception);
                }
            }

            var nonce = Convert.FromHexString(request.BootstrapNonce);
            try
            {
                await EntryStreamPipeProtocol.WriteHelloAsync(
                    dataPipe,
                    streamId,
                    nonce,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
            }

            await SendResponseAsync(
                frame,
                new OpenEntryStreamResponse(
                    streamId,
                    entry.Descriptor.EntryId,
                    entry.Descriptor.OriginalSize),
                cancellationToken).ConfigureAwait(false);

            var streamTask = RunEntryStreamAsync(
                dataPipe,
                session,
                entry,
                request.MaximumBytes,
                password,
                sessionReservation,
                streamCancellation.Token);
            _ = ObserveEntryStreamTaskAsync(streamTask, trackedCompletion);
            ownershipTransferred = true;
            entryStreamSlotHeld = false;
            dataPipe = null;
            sessionReservation = null;
            trackedCompletion = null;
            streamCancellation = null;
            password = [];
        }
        finally
        {
            if (!ownershipTransferred)
            {
                SettleSecretSlot(request.PasswordSlotId);
            }

            CryptographicOperations.ZeroMemory(password);
            if (dataPipe is not null)
            {
                await dataPipe.DisposeAsync().ConfigureAwait(false);
            }

            sessionReservation?.Dispose();
            if (entryStreamSlotHeld)
            {
                _entryStreamSlots.Release();
            }

            trackedCompletion?.TrySetResult();
        }
    }

    private async Task RunEntryStreamAsync(
        NamedPipeClientStream dataPipe,
        WorkerArchiveSession session,
        IndexedArchiveEntry entry,
        long maximumBytes,
        byte[] password,
        IDisposable sessionReservation,
        CancellationToken cancellationToken)
    {
        byte[]? buffer = null;
        var executionSlotHeld = false;
        try
        {
            await _entryStreamExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            executionSlotHeld = true;
            await using var sessionLease = await session.EnterAsync(cancellationToken).ConfigureAwait(false);
            await session.EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
            await session.ReopenWithPasswordAsync(password, cancellationToken).ConfigureAwait(false);
            await using var source = await session.Handle
                .OpenEntryStreamAsync(entry.ProviderEntry!.Ordinal, cancellationToken)
                .ConfigureAwait(false);
            buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaximumEntryStreamFrameBytes);
            long totalBytes = 0;
            while (true)
            {
                var read = await source
                    .ReadAsync(
                        buffer.AsMemory(0, ProtocolConstants.MaximumEntryStreamFrameBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                long nextTotal;
                try
                {
                    nextTotal = checked(totalBytes + read);
                }
                catch (OverflowException exception)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.ResourceLimit,
                        "entry.stream_byte_overflow",
                        "The decoded entry byte count exceeded the supported numeric range.",
                        exception);
                }

                if (nextTotal > maximumBytes)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.ResourceLimit,
                        "entry.stream_byte_limit",
                        "The decoded entry exceeded the requested preview-stream limit.");
                }

                await EntryStreamPipeProtocol.WriteDataAsync(
                    dataPipe,
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                totalBytes = nextTotal;
            }

            await session.EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
            await EntryStreamPipeProtocol.WriteCompleteAsync(
                dataPipe,
                totalBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var error = ToError(exception) with { EntryId = entry.Descriptor.EntryId };
            using var errorTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            errorTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await EntryStreamPipeProtocol.WriteErrorAsync(
                    dataPipe,
                    error,
                    errorTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception writeException) when (
                writeException is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closing the client-owned data pipe is the cancellation signal for this lease.
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (executionSlotHeld)
            {
                _entryStreamExecutionGate.Release();
            }

            try
            {
                await dataPipe.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    sessionReservation.Dispose();
                }
                finally
                {
                    _entryStreamSlots.Release();
                }
            }
        }
    }

    private (TaskCompletionSource Completion, WorkerRequestCancellation Cancellation) RegisterEntryStream(
        EntryStreamId streamId)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new WorkerRequestCancellation(_shutdown.Token);
        if (!_entryStreamCancellations.TryAdd(streamId, cancellation))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("Could not allocate entry stream cancellation state.");
        }

        if (!_entryStreamTasks.TryAdd(streamId, completion.Task))
        {
            _entryStreamCancellations.TryRemove(streamId, out _);
            cancellation.Dispose();
            throw new InvalidOperationException("Could not allocate an entry stream lease.");
        }

        _ = completion.Task.ContinueWith(
            _completed =>
            {
                _entryStreamTasks.TryRemove(streamId, out _);
                if (_entryStreamCancellations.TryRemove(streamId, out var completedCancellation))
                {
                    completedCancellation.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return (completion, cancellation);
    }

    private EntryStreamId AllocateEntryStreamId()
    {
        if (_issuedEntryStreamIds.Count >= ProtocolConstants.MaximumRequestsPerConnection)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "entry.stream_identifier_limit",
                "The connection entry stream identifier limit was reached.");
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var streamId = EntryStreamId.New();
            if (_issuedEntryStreamIds.TryAdd(streamId, 0))
            {
                return streamId;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique entry stream identifier.");
    }

    private static async Task ObserveEntryStreamTaskAsync(
        Task streamTask,
        TaskCompletionSource trackedCompletion)
    {
        try
        {
            await streamTask.ConfigureAwait(false);
        }
        catch
        {
            // The stream runner translates expected failures onto the data pipe.
        }
        finally
        {
            trackedCompletion.TrySetResult();
        }
    }

    private async Task CloseArchiveAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<CloseArchiveRequest>();
        var closed = _sessions.TryRemove(request.SessionId, out var session);
        if (session is not null)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _sessionSlots.Release();
            }
        }

        await SendResponseAsync(frame, new CloseArchiveResponse(closed), cancellationToken).ConfigureAwait(false);
    }

    private async Task StartExtractionAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<StartExtractionRequest>();
        var passwordSlotTransferred = false;
        try
        {
            ValidateEntryIds(request.EntryIds);
            var destinationPath = CanonicalizeRequestPath(request.DestinationPath, "destination.invalid_path");
            request = request with { DestinationPath = destinationPath };
            var session = GetSession(request.SessionId);
            EnsureOperationSupportedForSession(session);
            IDisposable? reservation = session.ReserveUse();
            WorkerOperation? operation = null;
            try
            {
                operation = AddOperation(OperationKind.Extract);
                await SendResponseAsync(frame, new StartOperationResponse(operation.Id), cancellationToken).ConfigureAwait(false);
                var task = RunExtractionAsync(operation, session, request, reservation!);
                passwordSlotTransferred = true;
                TrackOperation(operation, task);
                reservation = null;
                operation = null;
            }
            finally
            {
                reservation?.Dispose();
                if (operation is not null)
                {
                    CompleteOperation(operation);
                }
            }
        }
        finally
        {
            if (!passwordSlotTransferred)
            {
                SettleSecretSlot(request.PasswordSlotId);
            }
        }
    }

    private async Task RunExtractionAsync(
        WorkerOperation operation,
        WorkerArchiveSession session,
        StartExtractionRequest request,
        IDisposable sessionReservation)
    {
        var password = Array.Empty<byte>();
        var executionLeaseHeld = false;
        ExtractionRecoveryCompletion? recoveryCompletion = null;
        try
        {
            await SendStateAsync(operation, OperationPhase.Queued).ConfigureAwait(false);
            await _operationExecutionGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            executionLeaseHeld = true;
            await SendStateAsync(operation, OperationPhase.Validating).ConfigureAwait(false);
            await using var lease = await session.EnterAsync(operation.Cancellation.Token).ConfigureAwait(false);
            await session.EnsureSourceUnchangedAsync(operation.Cancellation.Token).ConfigureAwait(false);
            password = _secrets.Consume(request.PasswordSlotId);
            await session.ReopenWithPasswordAsync(password, operation.Cancellation.Token).ConfigureAwait(false);
            var selection = session.Index.ResolveSelection(
                request.EntryIds,
                operation.Cancellation.Token);
            var provenance = await session
                .GetDownloadProvenanceAsync(operation.Cancellation.Token)
                .ConfigureAwait(false);
            await SendStateAsync(operation, OperationPhase.Running).ConfigureAwait(false);
            recoveryCompletion = await _extractionWriter.ExtractTrackedAsync(
                operation.Id,
                session.Handle,
                selection,
                request.DestinationPath,
                progress => SendProgressAsync(operation, progress),
                operation.Cancellation.Token,
                async commitCancellationToken =>
                {
                    await SendStateAsync(operation, OperationPhase.Finalizing).ConfigureAwait(false);
                    await session.EnsureSourceUnchangedAsync(commitCancellationToken).ConfigureAwait(false);
                },
                provenance,
                warning => SendWarningAsync(operation, warning)).ConfigureAwait(false);
            SettleOperationSecret(request.PasswordSlotId, password);
            await SendTerminalAsync(
                operation,
                operation.WarningCount > 0 ? OperationPhase.CompletedWithWarnings : OperationPhase.Completed,
                null,
                operation.LastWarning).ConfigureAwait(false);
            try
            {
                await recoveryCompletion.DeleteAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArchiveProviderException
                    or AggregateException)
            {
                // The terminal event is already authoritative. Preserve any recovery
                // record whose bounded cleanup could not be completed.
            }
        }
        catch (OperationCanceledException)
        {
            SettleOperationSecret(request.PasswordSlotId, password);
            await SendTerminalAsync(operation, OperationPhase.Cancelled, new ArchiveError(
                "operation.cancelled",
                ArchiveErrorCategory.Cancelled,
                "The operation was cancelled.")).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SettleOperationSecret(request.PasswordSlotId, password);
            await SendTerminalAsync(operation, OperationPhase.Failed, ToError(exception)).ConfigureAwait(false);
        }
        finally
        {
            SettleOperationSecret(request.PasswordSlotId, password);
            if (recoveryCompletion is not null)
            {
                await recoveryCompletion.DisposeAsync().ConfigureAwait(false);
            }

            if (executionLeaseHeld)
            {
                _operationExecutionGate.Release();
            }

            sessionReservation.Dispose();
            CompleteOperation(operation);
        }
    }

    private async Task StartIntegrityTestAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<StartIntegrityTestRequest>();
        var passwordSlotTransferred = false;
        try
        {
            ValidateEntryIds(request.EntryIds);
            var session = GetSession(request.SessionId);
            EnsureOperationSupportedForSession(session);
            IDisposable? reservation = session.ReserveUse();
            WorkerOperation? operation = null;
            try
            {
                operation = AddOperation(OperationKind.IntegrityTest);
                await SendResponseAsync(frame, new StartOperationResponse(operation.Id), cancellationToken).ConfigureAwait(false);
                var task = RunIntegrityTestAsync(operation, session, request, reservation!);
                passwordSlotTransferred = true;
                TrackOperation(operation, task);
                reservation = null;
                operation = null;
            }
            finally
            {
                reservation?.Dispose();
                if (operation is not null)
                {
                    CompleteOperation(operation);
                }
            }
        }
        finally
        {
            if (!passwordSlotTransferred)
            {
                SettleSecretSlot(request.PasswordSlotId);
            }
        }
    }

    private async Task RunIntegrityTestAsync(
        WorkerOperation operation,
        WorkerArchiveSession session,
        StartIntegrityTestRequest request,
        IDisposable sessionReservation)
    {
        var password = Array.Empty<byte>();
        var executionLeaseHeld = false;
        try
        {
            await SendStateAsync(operation, OperationPhase.Queued).ConfigureAwait(false);
            await _operationExecutionGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            executionLeaseHeld = true;
            await SendStateAsync(operation, OperationPhase.Validating).ConfigureAwait(false);
            await using var lease = await session.EnterAsync(operation.Cancellation.Token).ConfigureAwait(false);
            await session.EnsureSourceUnchangedAsync(operation.Cancellation.Token).ConfigureAwait(false);
            password = _secrets.Consume(request.PasswordSlotId);
            await session.ReopenWithPasswordAsync(password, operation.Cancellation.Token).ConfigureAwait(false);
            var selection = session.Index.ResolveSelection(
                    request.EntryIds,
                    operation.Cancellation.Token)
                .Where(entry => entry.Descriptor.Kind == ArchiveEntryKind.File)
                .ToArray();
            new ExtractionResourceGuard().ValidateRead(selection);
            long processed = 0;
            var entryCount = 0;
            var total = TrySum(selection.Select(entry => entry.Descriptor.OriginalSize));
            var compressionRatio = ArchiveMetrics.TryCalculateCompressionRatio(
                selection.Select(entry => entry.Descriptor));
            var compressedBytes = TrySum(selection.Select(entry => entry.Descriptor.CompressedSize));
            var runtimeGuard = new ExtractionResourceGuard().CreateRuntimeGuard(
                declaredCompressedBytes: compressedBytes);
            await SendStateAsync(operation, OperationPhase.Running).ConfigureAwait(false);

            foreach (var entry in selection)
            {
                await using var guardedOutput = new RuntimeGuardWriteStream(
                    Stream.Null,
                    runtimeGuard,
                    processed,
                    entry.Descriptor.OriginalSize,
                    (totalWritten, _) => SendProgressAsync(operation, new OperationProgress(
                        totalWritten,
                        total,
                        entryCount,
                        selection.Length,
                        entry.Descriptor.EntryId,
                        entry.Descriptor.DisplayName,
                        operation.Stopwatch.Elapsed,
                        operation.Stopwatch.Elapsed.TotalSeconds > 0
                            ? totalWritten / operation.Stopwatch.Elapsed.TotalSeconds
                            : null,
                        compressionRatio)));
                await session.Handle.CopyEntryToAsync(
                    entry.ProviderEntry!.Ordinal,
                    guardedOutput,
                    operation.Cancellation.Token).ConfigureAwait(false);
                runtimeGuard.ValidateCompletedEntry(
                    guardedOutput.EntryBytesWritten,
                    entry.Descriptor.OriginalSize);
                processed = guardedOutput.TotalBytesWritten;

                entryCount++;
                await SendProgressAsync(operation, new OperationProgress(
                    processed,
                    total,
                    entryCount,
                    selection.Length,
                    entry.Descriptor.EntryId,
                    entry.Descriptor.DisplayName,
                    operation.Stopwatch.Elapsed,
                    operation.Stopwatch.Elapsed.TotalSeconds > 0
                        ? processed / operation.Stopwatch.Elapsed.TotalSeconds
                        : null,
                    compressionRatio)).ConfigureAwait(false);
            }

            await SendStateAsync(operation, OperationPhase.Finalizing).ConfigureAwait(false);
            await session.EnsureSourceUnchangedAsync(operation.Cancellation.Token).ConfigureAwait(false);
            SettleOperationSecret(request.PasswordSlotId, password);
            await SendTerminalAsync(operation, OperationPhase.Completed, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SettleOperationSecret(request.PasswordSlotId, password);
            await SendTerminalAsync(operation, OperationPhase.Cancelled, new ArchiveError(
                "operation.cancelled",
                ArchiveErrorCategory.Cancelled,
                "The integrity test was cancelled.")).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SettleOperationSecret(request.PasswordSlotId, password);
            await SendTerminalAsync(operation, OperationPhase.Failed, ToError(exception)).ConfigureAwait(false);
        }
        finally
        {
            SettleOperationSecret(request.PasswordSlotId, password);
            if (executionLeaseHeld)
            {
                _operationExecutionGate.Release();
            }

            sessionReservation.Dispose();
            CompleteOperation(operation);
        }
    }

    private async Task StartZipCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "ZIP creation was not negotiated for this worker connection.");
        }

        var request = frame.ReadPayload<StartZipCreationRequest>();
        ValidateZipCreationRequest(request);

        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.CreateArchive);
            await SendResponseAsync(
                    frame,
                    new StartOperationResponse(operation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            var task = RunZipCreationAsync(operation, request);
            TrackOperation(operation, task);
            operation = null;
        }
        finally
        {
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task StartTarCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureTarCreationFeature();
        var request = frame.ReadPayload<StartTarCreationRequest>();
        ValidateTarCreationRequest(request);

        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.CreateArchive);
            await SendResponseAsync(
                    frame,
                    new StartOperationResponse(operation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            var task = RunTarCreationAsync(operation, request);
            TrackOperation(operation, task);
            operation = null;
        }
        finally
        {
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task StartRarCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureRarCreationFeature();
        var request = frame.ReadPayload<StartRarCreationRequest>();
        ValidateRarCreationRequest(request);
        EnsureRarWriterAvailable();
        await StartCreationOperationAsync(
            frame,
            operation => RunRarCreationAsync(operation, request),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartRarTreeCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureRarDirectoryCreationFeature();
        var request = frame.ReadPayload<StartRarTreeCreationRequest>();
        ValidateRarTreeCreationRequest(request);
        EnsureRarWriterAvailable();
        await StartCreationOperationAsync(
            frame,
            operation => RunRarTreeCreationAsync(operation, request),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartTarGZipCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.TarGZipCreation,
            ProtocolFeatures.TarGZipCreationDependencies,
            "TAR.GZ creation");
        var request = frame.ReadPayload<StartTarGZipCreationRequest>();
        ValidateManagedTarCreationRequest(
            request.DestinationPath,
            request.Files,
            request.CompressionPreset,
            gzip: true,
            "tar.gz.creation_invalid_request");
        await StartCreationOperationAsync(
            frame,
            operation => RunTarGZipCreationAsync(operation, request),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartTarBZip2CreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.TarBZip2Creation,
            ProtocolFeatures.TarBZip2CreationDependencies,
            "TAR.BZ2 creation");
        var request = frame.ReadPayload<StartTarBZip2CreationRequest>();
        ValidateManagedTarCreationRequest(
            request.DestinationPath,
            request.Files,
            request.CompressionPreset,
            gzip: false,
            "tar.bz2.creation_invalid_request");
        await StartCreationOperationAsync(
            frame,
            operation => RunTarBZip2CreationAsync(operation, request),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartGZipCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.GZipCreation,
            ProtocolFeatures.GZipCreationDependencies,
            "GZIP creation");
        var request = frame.ReadPayload<StartGZipCreationRequest>();
        ValidateRawCreationRequest(
            request.DestinationPath,
            request.SourcePath,
            request.CompressionPreset,
            gzip: true,
            "gzip.creation_invalid_request");
        await StartCreationOperationAsync(
            frame,
            operation => RunGZipCreationAsync(operation, request),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartBZip2CreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.BZip2Creation,
            ProtocolFeatures.BZip2CreationDependencies,
            "BZIP2 creation");
        var request = frame.ReadPayload<StartBZip2CreationRequest>();
        ValidateRawCreationRequest(
            request.DestinationPath,
            request.SourcePath,
            request.CompressionPreset,
            gzip: false,
            "bzip2.creation_invalid_request");
        await StartCreationOperationAsync(
            frame,
            operation => RunBZip2CreationAsync(operation, request),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartCreationOperationAsync(
        ProtocolFrame frame,
        Func<WorkerOperation, Task> run,
        CancellationToken cancellationToken)
    {
        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.CreateArchive);
            await SendResponseAsync(
                    frame,
                    new StartOperationResponse(operation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            var task = run(operation);
            TrackOperation(operation, task);
            operation = null;
        }
        finally
        {
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task StartZipRebuildCopyAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureZipRebuildCopyFeature();
        var request = frame.ReadPayload<StartZipRebuildCopyRequest>();
        ValidateZipRebuildCopyRequest(request);

        var canonicalFiles = new ArchiveRebuildCopyFile[request.Files.Count];
        for (var index = 0; index < request.Files.Count; index++)
        {
            var file = request.Files[index];
            canonicalFiles[index] = file with
            {
                SourcePath = CanonicalizeRequestPath(
                    file.SourcePath,
                    "zip.rebuild_source_path_invalid"),
            };
        }

        request = request with
        {
            DestinationPath = CanonicalizeRequestPath(
                request.DestinationPath,
                "destination.invalid_path"),
            Files = canonicalFiles,
        };

        var session = GetSession(request.SessionId);
        EnsureOperationSupportedForSession(session);
        if (!session.Descriptor.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_source_not_eligible",
                "ZIP Rebuild Copy requires one safe, unencrypted, single-volume ZIP with complete persistent entry metadata.");
        }

        IDisposable? reservation = session.ReserveUse();
        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.RebuildArchive);
            await SendResponseAsync(
                    frame,
                    new StartOperationResponse(operation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            var task = RunZipRebuildCopyAsync(operation, session, request, reservation);
            TrackOperation(operation, task);
            reservation = null;
            operation = null;
        }
        finally
        {
            reservation?.Dispose();
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task RunZipRebuildCopyAsync(
        WorkerOperation operation,
        WorkerArchiveSession session,
        StartZipRebuildCopyRequest request,
        IDisposable sessionReservation)
    {
        var executionLeaseHeld = false;
        try
        {
            await SendStateAsync(operation, OperationPhase.Queued).ConfigureAwait(false);
            await _operationExecutionGate
                .WaitAsync(operation.Cancellation.Token)
                .ConfigureAwait(false);
            executionLeaseHeld = true;
            await SendStateAsync(operation, OperationPhase.Validating).ConfigureAwait(false);
            await using var sessionLease = await session
                .EnterAsync(operation.Cancellation.Token)
                .ConfigureAwait(false);
            await session
                .EnsureSourceUnchangedAsync(operation.Cancellation.Token)
                .ConfigureAwait(false);

            var fingerprint = session.Descriptor.Fingerprint;
            await _zipRebuildCopyWriter
                .RebuildAsync(
                    _writerProviders.GetRequired("zip"),
                    session.Handle,
                    session.Index,
                    request.EntriesToDelete,
                    request.Files,
                    session.SourcePath,
                    new OpenFileSnapshot(
                        fingerprint.Length,
                        fingerprint.LastWriteTimeUtc,
                        fingerprint.FileIdentity),
                    request.DestinationPath,
                    request.CompressionPreset,
                    progress => SendProgressAsync(operation, progress),
                    session.EnsureSourceUnchangedAsync,
                    operation.Cancellation.Token,
                    beforeWriting: () => SendStateAsync(operation, OperationPhase.Running),
                    beforeCommit: () => SendStateAsync(operation, OperationPhase.Finalizing),
                    reportWarning: warning => SendWarningAsync(operation, warning),
                    operationId: operation.Id)
                .ConfigureAwait(false);

            await SendTerminalAsync(
                    operation,
                    operation.WarningCount > 0
                        ? OperationPhase.CompletedWithWarnings
                        : OperationPhase.Completed,
                    null,
                    operation.LastWarning)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendTerminalAsync(
                    operation,
                    OperationPhase.Cancelled,
                    new ArchiveError(
                        "operation.cancelled",
                        ArchiveErrorCategory.Cancelled,
                        "ZIP Rebuild Copy was cancelled. The original archive was not changed."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SendTerminalAsync(operation, OperationPhase.Failed, ToError(exception))
                .ConfigureAwait(false);
        }
        finally
        {
            if (executionLeaseHeld)
            {
                _operationExecutionGate.Release();
            }

            sessionReservation.Dispose();
            CompleteOperation(operation);
        }
    }

    private void EnsureZipRebuildCopyFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation)
            || !_enabledFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
            || !_enabledFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "ZIP Rebuild Copy and its required protocol dependencies were not negotiated for this connection.");
        }
    }

    private async Task StartZipRebuildRenameAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureZipRebuildRenameFeature();
        var request = frame.ReadPayload<StartZipRebuildRenameRequest>();
        ValidateZipRebuildRenameRequest(request);

        var canonicalFiles = new ArchiveRebuildCopyFile[request.Files.Count];
        for (var index = 0; index < request.Files.Count; index++)
        {
            var file = request.Files[index];
            canonicalFiles[index] = file with
            {
                SourcePath = CanonicalizeRequestPath(
                    file.SourcePath,
                    "zip.rebuild_source_path_invalid"),
            };
        }

        request = request with
        {
            DestinationPath = CanonicalizeRequestPath(
                request.DestinationPath,
                "destination.invalid_path"),
            Files = canonicalFiles,
        };

        var session = GetSession(request.SessionId);
        EnsureOperationSupportedForSession(session);
        if (!session.Descriptor.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_source_not_eligible",
                "ZIP Rebuild Rename requires one safe, unencrypted, single-volume ZIP with complete persistent entry metadata.");
        }

        IDisposable? reservation = session.ReserveUse();
        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.RebuildArchive);
            await SendResponseAsync(
                    frame,
                    new StartOperationResponse(operation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            var task = RunZipRebuildRenameAsync(operation, session, request, reservation);
            TrackOperation(operation, task);
            reservation = null;
            operation = null;
        }
        finally
        {
            reservation?.Dispose();
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task RunZipRebuildRenameAsync(
        WorkerOperation operation,
        WorkerArchiveSession session,
        StartZipRebuildRenameRequest request,
        IDisposable sessionReservation)
    {
        var executionLeaseHeld = false;
        try
        {
            await SendStateAsync(operation, OperationPhase.Queued).ConfigureAwait(false);
            await _operationExecutionGate
                .WaitAsync(operation.Cancellation.Token)
                .ConfigureAwait(false);
            executionLeaseHeld = true;
            await SendStateAsync(operation, OperationPhase.Validating).ConfigureAwait(false);
            await using var sessionLease = await session
                .EnterAsync(operation.Cancellation.Token)
                .ConfigureAwait(false);
            await session
                .EnsureSourceUnchangedAsync(operation.Cancellation.Token)
                .ConfigureAwait(false);

            var fingerprint = session.Descriptor.Fingerprint;
            await _zipRebuildCopyWriter
                .RebuildWithRenamesAsync(
                    _writerProviders.GetRequired("zip"),
                    session.Handle,
                    session.Index,
                    request.EntriesToDelete,
                    request.Files,
                    request.Renames,
                    session.SourcePath,
                    new OpenFileSnapshot(
                        fingerprint.Length,
                        fingerprint.LastWriteTimeUtc,
                        fingerprint.FileIdentity),
                    request.DestinationPath,
                    request.CompressionPreset,
                    progress => SendProgressAsync(operation, progress),
                    session.EnsureSourceUnchangedAsync,
                    operation.Cancellation.Token,
                    beforeWriting: () => SendStateAsync(operation, OperationPhase.Running),
                    beforeCommit: () => SendStateAsync(operation, OperationPhase.Finalizing),
                    reportWarning: warning => SendWarningAsync(operation, warning),
                    operationId: operation.Id)
                .ConfigureAwait(false);

            await SendTerminalAsync(
                    operation,
                    operation.WarningCount > 0
                        ? OperationPhase.CompletedWithWarnings
                        : OperationPhase.Completed,
                    null,
                    operation.LastWarning)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendTerminalAsync(
                    operation,
                    OperationPhase.Cancelled,
                    new ArchiveError(
                        "operation.cancelled",
                        ArchiveErrorCategory.Cancelled,
                        "ZIP Rebuild Rename was cancelled. The original archive was not changed."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SendTerminalAsync(operation, OperationPhase.Failed, ToError(exception))
                .ConfigureAwait(false);
        }
        finally
        {
            if (executionLeaseHeld)
            {
                _operationExecutionGate.Release();
            }

            sessionReservation.Dispose();
            CompleteOperation(operation);
        }
    }

    private void EnsureZipRebuildRenameFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildRename)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipRebuildCopy)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation)
            || !_enabledFeatures.Contains(ProtocolFeatures.PersistentEntryKeys)
            || !_enabledFeatures.Contains(ProtocolFeatures.VolumeSetIdentity)
            || !_enabledFeatures.Contains(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "ZIP Rebuild Rename and its required protocol dependencies were not negotiated for this connection.");
        }
    }

    private async Task GetZipWriteCapabilitiesAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureZipWriteCapabilitiesFeature();
        var request = frame.ReadPayload<GetZipWriteCapabilitiesRequest>();
        ValidateZipWriteCapabilitiesRequest(request);

        IArchiveWriterProvider? zipWriter = null;
        try
        {
            zipWriter = _writerProviders.GetRequired("zip");
        }
        catch (ArchiveProviderException exception) when (
            exception.Category == ArchiveErrorCategory.UnsupportedFormat)
        {
            // The capability query reports an honest false when a managed ZIP
            // writer is unavailable; it does not turn a query into a write.
        }

        var canCreate = zipWriter is not null;
        var canRebuild = false;
        if (request.SessionId is { } sessionId)
        {
            var session = GetSession(sessionId);
            canRebuild = canCreate
                && session.Descriptor.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
                && _zipRebuildCopyPolicy.CanRebuild(session.Handle, session.Index);
        }

        var response = new GetZipWriteCapabilitiesResponse(
            CanCreate: canCreate,
            CanRebuild: canRebuild,
            SupportsAdd: canCreate,
            SupportsReplace: canCreate,
            SupportsDelete: canCreate,
            SupportsFileRename: canCreate,
            SupportsTreeEdit: canCreate,
            SupportsMetadata: canCreate,
            CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumZipCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateZipWriteCapabilitiesRequest(
        GetZipWriteCapabilitiesRequest? request)
    {
        if (request?.SessionId is { Value: var value } && value == Guid.Empty)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "zip.write_capabilities_invalid_request",
                "The ZIP write capability request contained an empty session identifier.");
        }

        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetZipWriteCapabilities,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "zip.write_capabilities_frame_limit",
                "The ZIP write capability request exceeds the bounded protocol frame size.",
                exception);
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
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "ZIP write capabilities and all preceding rebuild dependencies were not negotiated for this connection.");
        }
    }

    private async Task GetTarWriteCapabilitiesAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureTarWriteCapabilitiesFeature();
        var request = frame.ReadPayload<GetTarWriteCapabilitiesRequest>();
        ValidateTarWriteCapabilitiesRequest(request);

        IArchiveWriterProvider? tarWriter = null;
        try
        {
            tarWriter = _writerProviders.GetRequired("tar");
        }
        catch (ArchiveProviderException exception) when (
            exception.Category == ArchiveErrorCategory.UnsupportedFormat)
        {
            // Capability discovery reports an unavailable managed TAR writer
            // as false without attempting any filesystem operation.
        }

        var canCreate = tarWriter is not null
            && tarWriter is IArchiveStagedOutputValidator
            && tarWriter.CompressionPresets.SetEquals([ArchiveCompressionPreset.Store]);
        var response = new GetTarWriteCapabilitiesResponse(
            CanCreate: canCreate,
            CanRebuild: false,
            SupportsTreeCreation: canCreate,
            CompressionPresets: [ArchiveCompressionPreset.Store],
            MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateTarWriteCapabilitiesRequest(
        GetTarWriteCapabilitiesRequest? request)
    {
        if (request is null)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "tar.write_capabilities_invalid_request",
                "The TAR write capability request is missing.");
        }

        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetTarWriteCapabilities,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "tar.write_capabilities_frame_limit",
                "The TAR write capability request exceeds the bounded protocol frame size.",
                exception);
        }
    }

    private async Task GetRarWriteCapabilitiesAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureRarWriteCapabilitiesFeature();
        var request = frame.ReadPayload<GetRarWriteCapabilitiesRequest>();
        ValidateRarWriteCapabilitiesRequest(request);

        var presets = Enum.GetValues<ArchiveCompressionPreset>();
        var canCreate = TryGetQualifiedRarWriter(out _, out var availability);

        var response = new GetRarWriteCapabilitiesResponse(
            CanCreate: canCreate,
            CanRebuild: false,
            SupportsTreeCreation: canCreate,
            CompressionPresets: presets,
            MaximumEntries: ProtocolConstants.MaximumRarCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumRarCreationSourceBytes,
            Availability: availability,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRarWriteCapabilitiesRequest(
        GetRarWriteCapabilitiesRequest? request)
    {
        if (request is null)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "rar.write_capabilities_invalid_request",
                "The RAR write capability request is missing.");
        }

        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetRarWriteCapabilities,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "rar.write_capabilities_frame_limit",
                "The RAR write capability request exceeds the bounded protocol frame size.",
                exception);
        }
    }

    private void EnsureTarCreationFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.TarCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "TAR creation and request cancellation were not negotiated for this connection.");
        }
    }

    private void EnsureTarDirectoryCreationFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.TarDirectoryCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.TarCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "TAR directory creation and its dependencies were not negotiated for this connection.");
        }
    }

    private void EnsureTarWriteCapabilitiesFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.TarWriteCapabilities)
            || !_enabledFeatures.Contains(ProtocolFeatures.TarDirectoryCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.TarCreation)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "TAR write capabilities and both creation routes were not negotiated for this connection.");
        }
    }

    private void EnsureRarCreationFeature() =>
        EnsureManagedCreationFeature(
            ProtocolFeatures.RarCreation,
            ProtocolFeatures.RarCreationDependencies,
            "RAR creation");

    private void EnsureRarDirectoryCreationFeature() =>
        EnsureManagedCreationFeature(
            ProtocolFeatures.RarDirectoryCreation,
            ProtocolFeatures.RarDirectoryCreationDependencies,
            "RAR directory creation");

    private void EnsureRarWriteCapabilitiesFeature() =>
        EnsureManagedCreationFeature(
            ProtocolFeatures.RarWriteCapabilities,
            ProtocolFeatures.RarWriteCapabilitiesDependencies,
            "RAR write capabilities");

    private void EnsureRarWriterAvailable() => _ = GetQualifiedRarWriterOrThrow();

    private WinRarArchiveWriterProvider GetQualifiedRarWriterOrThrow()
    {
        if (TryGetQualifiedRarWriter(out var provider, out var availability)
            && provider is not null)
        {
            return provider;
        }

        var (code, message) = availability switch
        {
            RarEncoderAvailability.NotInstalled => (
                "rar.encoder_not_installed",
                "RAR creation requires a separately installed, licensed WinRAR 7.23 or later from the 7.x series."),
            RarEncoderAvailability.UpdateRequired => (
                "rar.encoder_update_required",
                "RAR creation requires WinRAR 7.23 or later from the 7.x series. The installed signed WinRAR is older."),
            RarEncoderAvailability.UnsupportedPlatform => (
                "rar.encoder_platform_unsupported",
                "RAR creation is available only on 64-bit Windows with a separately installed WinRAR encoder."),
            RarEncoderAvailability.RestartRequired => (
                "rar.encoder_restart_required",
                "WinRAR now passes the encoder identity gate. Restart Rain Explorer so a fresh Worker can adopt it."),
            _ => (
                "rar.encoder_untrusted",
                "Rain could not verify the installed WinRAR encoder, so RAR creation remains disabled."),
        };
        throw new ArchiveProviderException(
            ArchiveErrorCategory.UnsupportedCapability,
            code,
            message);
    }

    private bool TryGetQualifiedRarWriter(
        out WinRarArchiveWriterProvider? provider,
        out RarEncoderAvailability availability)
    {
        provider = null;
        try
        {
            var candidate = _writerProviders.GetRequired("rar");
            if (candidate is WinRarArchiveWriterProvider winRar
                && candidate is IArchiveStagedOutputValidator validator
                && string.Equals(validator.Format, "rar", StringComparison.OrdinalIgnoreCase)
                && candidate.CompressionPresets.SetEquals(
                    Enum.GetValues<ArchiveCompressionPreset>()))
            {
                availability = winRar.GetCurrentAvailability();
                if (availability == RarEncoderAvailability.Available)
                {
                    provider = winRar;
                    return true;
                }

                return false;
            }
        }
        catch (ArchiveProviderException exception) when (
            exception.Category == ArchiveErrorCategory.UnsupportedFormat)
        {
            // Optional encoder discovery remains read-only. The exact typed
            // reason is reported below without granting an arbitrary provider
            // RAR creation authority.
        }

        availability = WinRarEncoderProbe.ProbeInstalled();
        if (availability == RarEncoderAvailability.Available)
        {
            availability = RarEncoderAvailability.RestartRequired;
        }

        return false;
    }

    private async Task GetManagedWriteCapabilitiesAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.ManagedWriteCapabilities,
            ProtocolFeatures.ManagedWriteCapabilitiesDependencies,
            "managed write capabilities");
        var request = frame.ReadPayload<GetManagedWriteCapabilitiesRequest>();
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetManagedWriteCapabilities,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "managed.write_capabilities_frame_limit",
                "The managed write capability request exceeds the bounded protocol frame size.",
                exception);
        }

        var gzipPresets = Enum.GetValues<ArchiveCompressionPreset>();
        var bzip2Presets = new[] { ArchiveCompressionPreset.Balanced };
        var response = new GetManagedWriteCapabilitiesResponse(
        [
            CreateManagedCapability(
                ManagedArchiveCreationFormat.TarGZip,
                "tar.gz",
                gzipPresets,
                supportsTreeCreation: true,
                ProtocolConstants.MaximumTarCreationEntries,
                ProtocolConstants.MaximumTarCreationSourceBytes),
            CreateManagedCapability(
                ManagedArchiveCreationFormat.TarBZip2,
                "tar.bz2",
                bzip2Presets,
                supportsTreeCreation: true,
                ProtocolConstants.MaximumTarCreationEntries,
                ProtocolConstants.MaximumTarCreationSourceBytes),
            CreateManagedCapability(
                ManagedArchiveCreationFormat.GZip,
                "gzip",
                gzipPresets,
                supportsTreeCreation: false,
                ProtocolConstants.MaximumRawCreationEntries,
                ProtocolConstants.MaximumRawCreationSourceBytes),
            CreateManagedCapability(
                ManagedArchiveCreationFormat.BZip2,
                "bzip2",
                bzip2Presets,
                supportsTreeCreation: false,
                ProtocolConstants.MaximumRawCreationEntries,
                ProtocolConstants.MaximumRawCreationSourceBytes),
        ]);
        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task GetOperationActivityAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureOperationActivityFeature();
        var request = frame.ReadPayload<GetOperationActivityRequest>();
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetOperationActivity,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "operation.activity_frame_limit",
                "The operation activity request exceeds the bounded protocol frame size.",
                exception);
        }

        await SendOperationActivityResponseAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureOperationActivityFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.OperationActivity)
            || ProtocolFeatures.OperationActivityDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Operation activity and its event and cancellation dependencies were not negotiated for this connection.");
        }
    }

    private async Task GetSupportDiagnosticsAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureSupportDiagnosticsFeature();
        var request = frame.ReadPayload<GetSupportDiagnosticsRequest>();
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetSupportDiagnostics,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "worker.support_diagnostics_frame_limit",
                "The support diagnostics request exceeds the bounded protocol frame size.",
                exception);
        }

        int activeAndQueuedOperationCount;
        int retainedTerminalOperationCount;
        bool operationHistoryTruncated;
        lock (_operationActivityGate)
        {
            activeAndQueuedOperationCount = _operations.Count;
            retainedTerminalOperationCount = _retainedOperationActivity.Count;
            operationHistoryTruncated = _operationActivityHistoryTruncated;
        }

        var response = new GetSupportDiagnosticsResponse(
            ProtocolConstants.MajorVersion,
            _negotiatedProtocolMinor,
            _enabledFeatures.Count,
            WorkerContainmentModel.SameUserWorkerProcess,
            IsSameUserSecuritySandbox: false,
            MaximumActiveAndQueuedOperations: ProtocolConstants.MaximumQueuedOperations,
            MaximumConcurrentOperations: ProtocolConstants.MaximumConcurrentOperations,
            QueueOrdering: OperationQueueOrdering.Unspecified,
            MaximumRetainedTerminalOperations:
                ProtocolConstants.MaximumRetainedOperationActivity,
            activeAndQueuedOperationCount,
            retainedTerminalOperationCount,
            operationHistoryTruncated,
            SupportsQueueReordering: false,
            SupportsPause: false,
            SupportsRetry: false,
            SupportsAutomaticRecoveryDisposition: false,
            PersistsOperationActivityAcrossWorkerRestart: false);
        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSupportDiagnosticsFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.SupportDiagnostics)
            || ProtocolFeatures.SupportDiagnosticsDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Support diagnostics and its activity and cancellation dependencies were not negotiated for this connection.");
        }
    }

    private ManagedWriteCapability CreateManagedCapability(
        ManagedArchiveCreationFormat managedFormat,
        string providerFormat,
        IReadOnlyList<ArchiveCompressionPreset> presets,
        bool supportsTreeCreation,
        int maximumEntries,
        long maximumSourceBytes)
    {
        IArchiveWriterProvider? provider = null;
        try
        {
            provider = _writerProviders.GetRequired(providerFormat);
        }
        catch (ArchiveProviderException exception) when (
            exception.Category == ArchiveErrorCategory.UnsupportedFormat)
        {
            // Discovery remains filesystem-free and reports the missing writer as unavailable.
        }

        var canCreate = provider is IArchiveStagedOutputValidator validator
            && string.Equals(validator.Format, providerFormat, StringComparison.OrdinalIgnoreCase)
            && provider.CompressionPresets.SetEquals(presets);
        return new ManagedWriteCapability(
            managedFormat,
            canCreate,
            SupportsTreeCreation: canCreate && supportsTreeCreation,
            CanRebuild: false,
            CompressionPresets: presets,
            maximumEntries,
            maximumSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
    }

    private void EnsureManagedCreationFeature(
        string feature,
        IReadOnlyList<string> dependencies,
        string displayName)
    {
        if (!_enabledFeatures.Contains(feature)
            || dependencies.Any(dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                $"{displayName} and its dependencies were not negotiated for this connection.");
        }
    }

    private async Task GetZipRebuildTreePlanAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureZipRebuildTreeEditFeature();
        var request = frame.ReadPayload<GetZipRebuildTreePlanRequest>();
        ValidateZipRebuildTreePlanRequest(request);
        var session = GetSession(request.SessionId);
        EnsureOperationSupportedForSession(session);
        if (!session.Descriptor.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
            || !_zipRebuildCopyPolicy.CanRebuild(session.Handle, session.Index))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_source_not_eligible",
                "ZIP Rebuild Tree Edit requires one safe, unencrypted, single-volume ZIP with complete persistent entry metadata.");
        }

        using var reservation = session.ReserveUse();
        await using var sessionLease = await session.EnterAsync(cancellationToken).ConfigureAwait(false);
        await session.EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
        var response = _zipRebuildTreeEditPolicy.BuildPlan(
            session.Index,
            session.Descriptor.Fingerprint,
            cancellationToken);
        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task GetZipRebuildSourceFolderReviewAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureZipRebuildSourceFolderReviewFeature();
        var request = frame.ReadPayload<GetZipRebuildSourceFolderReviewRequest>();
        ValidateZipRebuildSourceFolderReviewRequest(request);
        var canonicalDirectories = new ArchiveCreationDirectory[request.Directories.Count];
        for (var index = 0; index < request.Directories.Count; index++)
        {
            var directory = request.Directories[index];
            canonicalDirectories[index] = directory with
            {
                SourcePath = CanonicalizeRequestPath(
                    directory.SourcePath,
                    "zip.rebuild_source_folder_review_path_invalid"),
            };
        }

        var session = GetSession(request.SessionId);
        EnsureOperationSupportedForSession(session);
        if (!session.Descriptor.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
            || !_zipRebuildCopyPolicy.CanRebuild(session.Handle, session.Index))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_source_not_eligible",
                "ZIP Rebuild source-folder review requires one safe, unencrypted, single-volume ZIP with complete persistent entry metadata.");
        }

        using var reservation = session.ReserveUse();
        await using var sessionLease = await session.EnterAsync(cancellationToken).ConfigureAwait(false);
        await session.EnsureSourceUnchangedAsync(cancellationToken).ConfigureAwait(false);
        var response = _zipRebuildSourceFolderReviewPolicy.BuildReview(
            canonicalDirectories,
            cancellationToken);
        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartZipRebuildTreeEditAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureZipRebuildTreeEditFeature();
        var request = frame.ReadPayload<StartZipRebuildTreeEditRequest>();
        ValidateZipRebuildTreeEditRequest(request);

        var canonicalFiles = new ArchiveRebuildCopyFile[request.Files.Count];
        for (var index = 0; index < request.Files.Count; index++)
        {
            var file = request.Files[index];
            canonicalFiles[index] = file with
            {
                SourcePath = CanonicalizeRequestPath(
                    file.SourcePath,
                    "zip.rebuild_source_path_invalid"),
            };
        }

        var canonicalDirectories = new ArchiveCreationDirectory[request.Directories.Count];
        for (var index = 0; index < request.Directories.Count; index++)
        {
            var directory = request.Directories[index];
            canonicalDirectories[index] = directory with
            {
                SourcePath = CanonicalizeRequestPath(
                    directory.SourcePath,
                    "zip.rebuild_tree_source_path_invalid"),
            };
        }

        request = request with
        {
            DestinationPath = CanonicalizeRequestPath(
                request.DestinationPath,
                "destination.invalid_path"),
            Files = canonicalFiles,
            Directories = canonicalDirectories,
        };

        var session = GetSession(request.SessionId);
        EnsureOperationSupportedForSession(session);
        if (!session.Descriptor.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
            || !_zipRebuildCopyPolicy.CanRebuild(session.Handle, session.Index))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_source_not_eligible",
                "ZIP Rebuild Tree Edit requires one safe, unencrypted, single-volume ZIP with complete persistent entry metadata.");
        }

        IDisposable? reservation = session.ReserveUse();
        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.RebuildArchive);
            await SendResponseAsync(
                    frame,
                    new StartOperationResponse(operation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            // The Security lane supplies the closed namespace planner/writer seam.
            var task = RunZipRebuildTreeEditAsync(operation, session, request, reservation);
            TrackOperation(operation, task);
            reservation = null;
            operation = null;
        }
        finally
        {
            reservation?.Dispose();
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task RunZipRebuildTreeEditAsync(
        WorkerOperation operation,
        WorkerArchiveSession session,
        StartZipRebuildTreeEditRequest request,
        IDisposable sessionReservation)
    {
        var executionLeaseHeld = false;
        try
        {
            await SendStateAsync(operation, OperationPhase.Queued).ConfigureAwait(false);
            await _operationExecutionGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            executionLeaseHeld = true;
            await SendStateAsync(operation, OperationPhase.Validating).ConfigureAwait(false);
            await using var sessionLease = await session.EnterAsync(operation.Cancellation.Token).ConfigureAwait(false);
            await session.EnsureSourceUnchangedAsync(operation.Cancellation.Token).ConfigureAwait(false);

            var fingerprint = session.Descriptor.Fingerprint;
            await _zipRebuildCopyWriter
                .RebuildWithTreeEditsAsync(
                    _writerProviders.GetRequired("zip"),
                    session.Handle,
                    session.Index,
                    fingerprint,
                    request.EntriesToDelete,
                    request.Files,
                    request.Renames,
                    request.Directories,
                    request.TreeActions,
                    session.SourcePath,
                    new OpenFileSnapshot(
                        fingerprint.Length,
                        fingerprint.LastWriteTimeUtc,
                        fingerprint.FileIdentity),
                    request.DestinationPath,
                    request.CompressionPreset,
                    progress => SendProgressAsync(operation, progress),
                    session.EnsureSourceUnchangedAsync,
                    operation.Cancellation.Token,
                    beforeWriting: () => SendStateAsync(operation, OperationPhase.Running),
                    beforeCommit: () => SendStateAsync(operation, OperationPhase.Finalizing),
                    reportWarning: warning => SendWarningAsync(operation, warning),
                    operationId: operation.Id)
                .ConfigureAwait(false);

            await SendTerminalAsync(
                    operation,
                    operation.WarningCount > 0
                        ? OperationPhase.CompletedWithWarnings
                        : OperationPhase.Completed,
                    null,
                    operation.LastWarning)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendTerminalAsync(
                    operation,
                    OperationPhase.Cancelled,
                    new ArchiveError(
                        "operation.cancelled",
                        ArchiveErrorCategory.Cancelled,
                        "ZIP Rebuild Tree Edit was cancelled. The original archive was not changed."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SendTerminalAsync(operation, OperationPhase.Failed, ToError(exception)).ConfigureAwait(false);
        }
        finally
        {
            if (executionLeaseHeld)
            {
                _operationExecutionGate.Release();
            }

            sessionReservation.Dispose();
            CompleteOperation(operation);
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
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "ZIP Rebuild Tree Edit and its required protocol dependencies were not negotiated for this connection.");
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
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "ZIP Rebuild source-folder review and its required protocol dependencies were not negotiated for this connection.");
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
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "ZIP Rebuild Metadata and its required protocol dependencies were not negotiated for this connection.");
        }
    }

    private async Task StartZipRebuildMetadataAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureZipRebuildMetadataFeature();
        var request = frame.ReadPayload<StartZipRebuildMetadataRequest>();
        ValidateZipRebuildMetadataRequest(request);

        var canonicalFiles = new ArchiveRebuildCopyFile[request.Files.Count];
        for (var index = 0; index < request.Files.Count; index++)
        {
            var file = request.Files[index];
            canonicalFiles[index] = file with
            {
                SourcePath = CanonicalizeRequestPath(file.SourcePath, "zip.rebuild_source_path_invalid"),
            };
        }

        var canonicalDirectories = new ArchiveCreationDirectory[request.Directories.Count];
        for (var index = 0; index < request.Directories.Count; index++)
        {
            var directory = request.Directories[index];
            canonicalDirectories[index] = directory with
            {
                SourcePath = CanonicalizeRequestPath(directory.SourcePath, "zip.rebuild_metadata_source_path_invalid"),
            };
        }

        request = request with
        {
            DestinationPath = CanonicalizeRequestPath(request.DestinationPath, "destination.invalid_path"),
            Files = canonicalFiles,
            Directories = canonicalDirectories,
        };

        var session = GetSession(request.SessionId);
        EnsureOperationSupportedForSession(session);
        if (!session.Descriptor.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
            || !_zipRebuildCopyPolicy.CanRebuild(session.Handle, session.Index))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_source_not_eligible",
                "ZIP Rebuild Metadata requires one safe, unencrypted, single-volume ZIP with complete persistent entry metadata.");
        }

        IDisposable? reservation = session.ReserveUse();
        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.RebuildArchive);
            await SendResponseAsync(frame, new StartOperationResponse(operation.Id), cancellationToken)
                .ConfigureAwait(false);
            var task = RunZipRebuildMetadataAsync(operation, session, request, reservation);
            TrackOperation(operation, task);
            reservation = null;
            operation = null;
        }
        finally
        {
            reservation?.Dispose();
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task RunZipRebuildMetadataAsync(
        WorkerOperation operation,
        WorkerArchiveSession session,
        StartZipRebuildMetadataRequest request,
        IDisposable sessionReservation)
    {
        var executionLeaseHeld = false;
        try
        {
            await SendStateAsync(operation, OperationPhase.Queued).ConfigureAwait(false);
            await _operationExecutionGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            executionLeaseHeld = true;
            await SendStateAsync(operation, OperationPhase.Validating).ConfigureAwait(false);
            await using var sessionLease = await session.EnterAsync(operation.Cancellation.Token).ConfigureAwait(false);
            await session.EnsureSourceUnchangedAsync(operation.Cancellation.Token).ConfigureAwait(false);

            var fingerprint = session.Descriptor.Fingerprint;
            await _zipRebuildCopyWriter
                .RebuildWithMetadataAsync(
                    _writerProviders.GetRequired("zip"),
                    session.Handle,
                    session.Index,
                    fingerprint,
                    request.EntriesToDelete,
                    request.Files,
                    request.Renames,
                    request.Directories,
                    request.TreeActions,
                    request.Metadata,
                    session.SourcePath,
                    new OpenFileSnapshot(fingerprint.Length, fingerprint.LastWriteTimeUtc, fingerprint.FileIdentity),
                    request.DestinationPath,
                    request.CompressionPreset,
                    progress => SendProgressAsync(operation, progress),
                    session.EnsureSourceUnchangedAsync,
                    operation.Cancellation.Token,
                    beforeWriting: () => SendStateAsync(operation, OperationPhase.Running),
                    beforeCommit: () => SendStateAsync(operation, OperationPhase.Finalizing),
                    reportWarning: warning => SendWarningAsync(operation, warning),
                    operationId: operation.Id)
                .ConfigureAwait(false);

            await SendTerminalAsync(
                    operation,
                    operation.WarningCount > 0 ? OperationPhase.CompletedWithWarnings : OperationPhase.Completed,
                    null,
                    operation.LastWarning)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendTerminalAsync(
                    operation,
                    OperationPhase.Cancelled,
                    new ArchiveError(
                        "operation.cancelled",
                        ArchiveErrorCategory.Cancelled,
                        "ZIP Rebuild Metadata was cancelled. The original archive was not changed."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SendTerminalAsync(operation, OperationPhase.Failed, ToError(exception)).ConfigureAwait(false);
        }
        finally
        {
            if (executionLeaseHeld)
            {
                _operationExecutionGate.Release();
            }

            sessionReservation.Dispose();
            CompleteOperation(operation);
        }
    }

    private async Task ListExtractionRecoveriesAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureExtractionRecoveryInspectionFeature();
        _ = frame.ReadPayload<ListExtractionRecoveriesRequest>();
        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ListExtractionRecoveriesResponse response;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = _extractionRecoveryStore.Inspect();
            cancellationToken.ThrowIfCancellationRequested();
            response = new ListExtractionRecoveriesResponse(
                inventory.RecoveryRecords
                    .Select(ExtractionRecoveryStore.ToSummary)
                    .ToArray(),
                inventory.ActiveOperationCount,
                inventory.SuspiciousOperationCount,
                inventory.IncompleteJournalCount,
                inventory.IsTruncated,
                inventory.InspectionIncomplete);
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task InspectExtractionRecoveryAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureExtractionRecoveryInspectionFeature();
        var request = frame.ReadPayload<InspectExtractionRecoveryRequest>();
        if (!IsValidRecoveryId(request.RecoveryId))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "extraction_recovery.invalid_id",
                "The extraction recovery identifier is invalid.");
        }

        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        InspectExtractionRecoveryResponse response;
        try
        {
            response = await _extractionRecoveryStore
                .InspectArtifactsAsync(request.RecoveryId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureExtractionRecoveryInspectionFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ExtractionRecoveryInspection)
            || ProtocolFeatures.ExtractionRecoveryInspectionDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Extraction recovery inspection was not negotiated for this connection.");
        }
    }

    private async Task ListArchiveCreationRecoveriesAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureRecoveryInspectionFeature();
        _ = frame.ReadPayload<ListArchiveCreationRecoveriesRequest>();
        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ListArchiveCreationRecoveriesResponse response;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = _creationRecoveryStore.Inspect();
            cancellationToken.ThrowIfCancellationRequested();
            response = new ListArchiveCreationRecoveriesResponse(
                inventory.RecoveryRecords
                    .Select(record => new ArchiveCreationRecoverySummary(
                        record.RecoveryId,
                        record.OperationId,
                        record.Phase,
                        record.Sequence,
                        record.RecordedAtUtc,
                        record.HasIncompleteTail))
                    .ToArray(),
                inventory.ActiveOperationCount,
                inventory.SuspiciousOperationCount,
                inventory.IncompleteJournalCount,
                inventory.IsTruncated,
                inventory.InspectionIncomplete);
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task InspectArchiveCreationRecoveryAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureRecoveryInspectionFeature();
        var request = frame.ReadPayload<InspectArchiveCreationRecoveryRequest>();
        if (!IsValidRecoveryId(request.RecoveryId))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "recovery.invalid_id",
                "The archive-creation recovery identifier is invalid.");
        }

        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        InspectArchiveCreationRecoveryResponse response;
        try
        {
            response = await _creationRecoveryStore
                .InspectArtifactsAsync(request.RecoveryId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyArchiveCreationRecoveryDispositionAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureRecoveryDispositionFeature();
        var request = frame.ReadPayload<ApplyArchiveCreationRecoveryDispositionRequest>();
        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ApplyArchiveCreationRecoveryDispositionResponse response;
        try
        {
            if (_recoveryDispositionCheckpoint is null)
            {
                response = await _creationRecoveryStore
                    .ApplyDispositionAsync(
                        request.RecoveryId,
                        request.ExpectedPhase,
                        request.ExpectedSequence,
                        request.ExpectedRecordedAtUtc,
                        request.Action,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                response = await _creationRecoveryStore
                    .ApplyDispositionAsync(
                        request.RecoveryId,
                        request.ExpectedPhase,
                        request.ExpectedSequence,
                        request.ExpectedRecordedAtUtc,
                        request.Action,
                        _recoveryDispositionCheckpoint,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureRecoveryInspectionFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryInspection))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Archive-creation recovery inspection was not negotiated for this connection.");
        }
    }

    private void EnsureRecoveryDispositionFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryDisposition)
            || !_enabledFeatures.Contains(ProtocolFeatures.ArchiveCreationRecoveryInspection)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Archive-creation recovery disposition was not negotiated for this connection.");
        }
    }

    private async Task ListNestedWorkspaceRecoveriesAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureNestedWorkspaceRecoveryFeature();
        _ = frame.ReadPayload<ListNestedWorkspaceRecoveriesRequest>();
        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ListNestedWorkspaceRecoveriesResponse response;
        try
        {
            response = _nestedRecoveryStore.List(cancellationToken);
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task InspectNestedWorkspaceRecoveryAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureNestedWorkspaceRecoveryFeature();
        var request = frame.ReadPayload<InspectNestedWorkspaceRecoveryRequest>();
        if (!IsValidRecoveryId(request.RecoveryId))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "nested_recovery.invalid_id",
                "The nested-workspace recovery identifier is invalid.");
        }

        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        InspectNestedWorkspaceRecoveryResponse response;
        try
        {
            response = _nestedRecoveryStore.Inspect(request.RecoveryId, cancellationToken);
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task DiscardNestedWorkspaceRecoveryAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureNestedWorkspaceRecoveryFeature();
        var request = frame.ReadPayload<DiscardNestedWorkspaceRecoveryRequest>();
        if (!IsValidRecoveryId(request.RecoveryId)
            || !IsValidRecoveryId(request.ExpectedRevision)
            || request.ExpectedSourceCount is < 0 or > ProtocolConstants.MaximumOpenSessions
            || request.ExpectedTotalSourceBytes is < 0
                or > ProtocolConstants.MaximumActiveNestedArchiveBytes)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "nested_recovery.invalid_discard_request",
                "The nested-workspace recovery discard evidence is invalid.");
        }

        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DiscardNestedWorkspaceRecoveryResponse response;
        try
        {
            response = _nestedRecoveryDiscardCheckpoint is null
                ? _nestedRecoveryStore.Discard(
                    request.RecoveryId,
                    request.ExpectedRevision,
                    request.ExpectedSourceCount,
                    request.ExpectedTotalSourceBytes,
                    cancellationToken)
                : _nestedRecoveryStore.Discard(
                    request.RecoveryId,
                    request.ExpectedRevision,
                    request.ExpectedSourceCount,
                    request.ExpectedTotalSourceBytes,
                    _nestedRecoveryDiscardCheckpoint,
                    cancellationToken);
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureNestedWorkspaceRecoveryFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.NestedWorkspaceRecovery)
            || !_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Nested-workspace recovery was not negotiated for this connection.");
        }
    }

    private async Task InspectNestedWorkspaceReopenAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureNestedWorkspaceIndependentReopenFeature();
        var request = frame.ReadPayload<InspectNestedWorkspaceReopenRequest>();
        if (!IsValidRecoveryId(request.RecoveryId))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "nested_recovery.invalid_id",
                "The nested-workspace recovery identifier is invalid.");
        }

        await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        InspectNestedWorkspaceReopenResponse response;
        try
        {
            response = await _nestedRecoveryStore
                .InspectForReopenAsync(request.RecoveryId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _recoveryInspectionGate.Release();
        }

        await SendResponseAsync(frame, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReopenNestedWorkspaceRecoveryAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureNestedWorkspaceIndependentReopenFeature();
        var request = frame.ReadPayload<ReopenNestedWorkspaceRecoveryRequest>();
        ValidateNestedWorkspaceReopenRequest(request);
        var password = Array.Empty<byte>();
        NestedByteReservation? byteReservation = null;
        OwnedTemporaryArchiveSource? temporarySource = null;
        WorkerArchiveSession? registeredSession = null;
        var sessionSlotHeld = false;
        var openExecutionSlotHeld = false;
        try
        {
            if (!_sessionSlots.Wait(0))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "session.limit",
                    $"A worker can keep at most {ProtocolConstants.MaximumOpenSessions} archive sessions open.");
            }

            sessionSlotHeld = true;
            byteReservation = ReserveNestedArchiveBytes(request.ExpectedSourceLength);
            await _openExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            openExecutionSlotHeld = true;
            temporarySource = _nestedWorkspace.Value.CreateSource();

            await _recoveryInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _nestedRecoveryStore.CopySourceForIndependentReopenAsync(
                        request.RecoveryId,
                        request.ExpectedRevision,
                        request.ExpectedSourceCount,
                        request.ExpectedTotalSourceBytes,
                        request.SourceId,
                        request.ExpectedSourceLength,
                        request.ExpectedContentSha256,
                        temporarySource.Output,
                        cancellationToken)
                    .ConfigureAwait(false);
                await temporarySource.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
                _nestedRecoveryDiscardCheckpoint?.Invoke(
                    NestedTemporaryWorkspaceRecoveryStore
                        .NestedWorkspaceRecoveryDiscardCheckpoint
                        .IndependentReopenSourceCopiedAndFlushed);
            }
            finally
            {
                _recoveryInspectionGate.Release();
            }

            await temporarySource.CompleteAsync(cancellationToken).ConfigureAwait(false);
            byteReservation.Commit(request.ExpectedSourceLength);
            password = _secrets.Consume(request.PasswordSlotId);
            var transferredSource = temporarySource;
            var transferredReservation = byteReservation;
            temporarySource = null;
            byteReservation = null;
            var session = await CreateSessionAsync(
                    transferredSource.SourcePath,
                    password,
                    transferredSource,
                    nestedDepth: 0,
                    nestedChainBytes: 0,
                    parentSessionId: null,
                    sourceEntryId: null,
                    inheritedProvenance: null,
                    transferredReservation,
                    cancellationToken)
                .ConfigureAwait(false);
            registeredSession = session;
            if (!_sessions.TryAdd(session.Descriptor.SessionId, session))
            {
                throw new InvalidOperationException("Could not allocate an independent recovery session.");
            }

            await SendResponseAsync(
                frame,
                new ReopenNestedWorkspaceRecoveryResponse(
                    request.RecoveryId,
                    request.SourceId,
                    NestedWorkspaceRecoveryReopenMode.IndependentRoot,
                    DescriptorForNegotiatedFeatures(session.Descriptor),
                    request.ExpectedSourceLength,
                    Depth: 0,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            registeredSession = null;
            sessionSlotHeld = false;
        }
        catch
        {
            if (registeredSession is not null)
            {
                _sessions.TryRemove(registeredSession.Descriptor.SessionId, out _);
                await registeredSession.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            SettleSecretSlot(request.PasswordSlotId);
            if (temporarySource is not null)
            {
                await temporarySource.DisposeAsync().ConfigureAwait(false);
            }

            byteReservation?.Dispose();
            if (sessionSlotHeld)
            {
                _sessionSlots.Release();
            }

            if (openExecutionSlotHeld)
            {
                _openExecutionGate.Release();
            }
        }
    }

    private void EnsureNestedWorkspaceIndependentReopenFeature()
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.NestedWorkspaceIndependentReopen)
            || ProtocolFeatures.NestedWorkspaceIndependentReopenDependencies.Any(
                dependency => !_enabledFeatures.Contains(dependency)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Independent nested-workspace recovery reopen was not negotiated for this connection.");
        }
    }

    private static void ValidateNestedWorkspaceReopenRequest(
        ReopenNestedWorkspaceRecoveryRequest request)
    {
        if (!IsValidRecoveryId(request.RecoveryId)
            || !IsValidRecoveryId(request.ExpectedRevision)
            || !IsValidRecoveryId(request.SourceId)
            || !IsValidRecoveryId(request.ExpectedContentSha256)
            || request.ExpectedSourceCount is <= 0 or > ProtocolConstants.MaximumOpenSessions
            || request.ExpectedTotalSourceBytes is <= 0
                or > ProtocolConstants.MaximumActiveNestedArchiveBytes
            || request.ExpectedSourceLength is <= 0
                or > ProtocolConstants.MaximumNestedArchiveBytes
            || request.ExpectedSourceLength > request.ExpectedTotalSourceBytes
            || request.Mode != NestedWorkspaceRecoveryReopenMode.IndependentRoot
            || request.PasswordSlotId == Guid.Empty)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "nested_recovery.invalid_reopen_request",
                "The nested-workspace recovery reopen evidence is invalid.");
        }
    }

    private static bool IsValidRecoveryId(string? recoveryId) =>
        recoveryId is { Length: 64 }
        && recoveryId.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private async Task RunZipCreationAsync(
        WorkerOperation operation,
        StartZipCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            [],
            includeDirectories: false,
            request.DestinationPath,
            request.CompressionPreset,
            format: "zip").ConfigureAwait(false);

    private async Task RunTarCreationAsync(
        WorkerOperation operation,
        StartTarCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            [],
            includeDirectories: false,
            request.DestinationPath,
            request.CompressionPreset,
            format: "tar").ConfigureAwait(false);

    private async Task RunRarCreationAsync(
        WorkerOperation operation,
        StartRarCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            [],
            includeDirectories: false,
            request.DestinationPath,
            request.CompressionPreset,
            format: "rar").ConfigureAwait(false);

    private async Task RunRarTreeCreationAsync(
        WorkerOperation operation,
        StartRarTreeCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            request.Directories,
            includeDirectories: true,
            request.DestinationPath,
            request.CompressionPreset,
            format: "rar").ConfigureAwait(false);

    private async Task StartZipTreeCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.ZipDirectoryCreation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "ZIP directory creation was not negotiated for this worker connection.");
        }

        var request = frame.ReadPayload<StartZipTreeCreationRequest>();
        ValidateZipTreeCreationRequest(request);

        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.CreateArchive);
            await SendResponseAsync(
                    frame,
                    new StartOperationResponse(operation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            var task = RunZipTreeCreationAsync(operation, request);
            TrackOperation(operation, task);
            operation = null;
        }
        finally
        {
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task RunZipTreeCreationAsync(
        WorkerOperation operation,
        StartZipTreeCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            request.Directories,
            includeDirectories: true,
            request.DestinationPath,
            request.CompressionPreset,
            format: "zip").ConfigureAwait(false);

    private async Task StartTarTreeCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureTarDirectoryCreationFeature();
        var request = frame.ReadPayload<StartTarTreeCreationRequest>();
        ValidateTarTreeCreationRequest(request);

        WorkerOperation? operation = null;
        try
        {
            operation = AddOperation(OperationKind.CreateArchive);
            await SendResponseAsync(
                    frame,
                    new StartOperationResponse(operation.Id),
                    cancellationToken)
                .ConfigureAwait(false);
            var task = RunTarTreeCreationAsync(operation, request);
            TrackOperation(operation, task);
            operation = null;
        }
        finally
        {
            if (operation is not null)
            {
                CompleteOperation(operation);
            }
        }
    }

    private async Task StartTarGZipTreeCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.TarGZipDirectoryCreation,
            ProtocolFeatures.TarGZipDirectoryCreationDependencies,
            "TAR.GZ directory creation");
        var request = frame.ReadPayload<StartTarGZipTreeCreationRequest>();
        ValidateManagedTarTreeCreationRequest(
            request.DestinationPath,
            request.Files,
            request.Directories,
            request.CompressionPreset,
            gzip: true,
            "tar.gz.creation_invalid_request");
        await StartCreationOperationAsync(
            frame,
            operation => RunTarGZipTreeCreationAsync(operation, request),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartTarBZip2TreeCreationAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        EnsureManagedCreationFeature(
            ProtocolFeatures.TarBZip2DirectoryCreation,
            ProtocolFeatures.TarBZip2DirectoryCreationDependencies,
            "TAR.BZ2 directory creation");
        var request = frame.ReadPayload<StartTarBZip2TreeCreationRequest>();
        ValidateManagedTarTreeCreationRequest(
            request.DestinationPath,
            request.Files,
            request.Directories,
            request.CompressionPreset,
            gzip: false,
            "tar.bz2.creation_invalid_request");
        await StartCreationOperationAsync(
            frame,
            operation => RunTarBZip2TreeCreationAsync(operation, request),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunTarTreeCreationAsync(
        WorkerOperation operation,
        StartTarTreeCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            request.Directories,
            includeDirectories: true,
            request.DestinationPath,
            request.CompressionPreset,
            format: "tar").ConfigureAwait(false);

    private async Task RunTarGZipCreationAsync(
        WorkerOperation operation,
        StartTarGZipCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            [],
            includeDirectories: false,
            request.DestinationPath,
            request.CompressionPreset,
            format: "tar.gz").ConfigureAwait(false);

    private async Task RunTarGZipTreeCreationAsync(
        WorkerOperation operation,
        StartTarGZipTreeCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            request.Directories,
            includeDirectories: true,
            request.DestinationPath,
            request.CompressionPreset,
            format: "tar.gz").ConfigureAwait(false);

    private async Task RunTarBZip2CreationAsync(
        WorkerOperation operation,
        StartTarBZip2CreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            [],
            includeDirectories: false,
            request.DestinationPath,
            request.CompressionPreset,
            format: "tar.bz2").ConfigureAwait(false);

    private async Task RunTarBZip2TreeCreationAsync(
        WorkerOperation operation,
        StartTarBZip2TreeCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            request.Files,
            request.Directories,
            includeDirectories: true,
            request.DestinationPath,
            request.CompressionPreset,
            format: "tar.bz2").ConfigureAwait(false);

    private async Task RunGZipCreationAsync(
        WorkerOperation operation,
        StartGZipCreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            [new ArchiveCreationFile(request.SourcePath, "content")],
            [],
            includeDirectories: false,
            request.DestinationPath,
            request.CompressionPreset,
            format: "gzip").ConfigureAwait(false);

    private async Task RunBZip2CreationAsync(
        WorkerOperation operation,
        StartBZip2CreationRequest request) =>
        await RunArchiveCreationCoreAsync(
            operation,
            [new ArchiveCreationFile(request.SourcePath, "content")],
            [],
            includeDirectories: false,
            request.DestinationPath,
            request.CompressionPreset,
            format: "bzip2").ConfigureAwait(false);

    private async Task RunArchiveCreationCoreAsync(
        WorkerOperation operation,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        bool includeDirectories,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        string format)
    {
        var executionLeaseHeld = false;
        try
        {
            await SendStateAsync(operation, OperationPhase.Queued).ConfigureAwait(false);
            await _operationExecutionGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            executionLeaseHeld = true;
            await SendStateAsync(operation, OperationPhase.Validating).ConfigureAwait(false);
            IArchiveWriterProvider provider = format == "rar"
                ? GetQualifiedRarWriterOrThrow()
                : _writerProviders.GetRequired(format);
            var progress = new Func<OperationProgress, ValueTask>(
                value => SendProgressAsync(operation, value));
            var beforeWriting = new Func<ValueTask>(
                () => SendStateAsync(operation, OperationPhase.Running));
            var beforeCommit = new Func<ValueTask>(
                () => SendStateAsync(operation, OperationPhase.Finalizing));
            var reportWarning = new Func<ArchiveWarning, ValueTask>(
                warning => SendWarningAsync(operation, warning));
            var creation = (format, includeDirectories) switch
            {
                ("zip", false) => _creationWriter.CreateZipAsync(
                    provider, files, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("zip", true) => _creationWriter.CreateZipTreeAsync(
                    provider, files, directories, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("tar", false) => _creationWriter.CreateTarAsync(
                    provider, files, destinationPath, progress, operation.Cancellation.Token,
                    beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("tar", true) => _creationWriter.CreateTarTreeAsync(
                    provider, files, directories, destinationPath, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("rar", false) => _creationWriter.CreateRarAsync(
                    provider, files, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("rar", true) => _creationWriter.CreateRarTreeAsync(
                    provider, files, directories, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("tar.gz", false) => _creationWriter.CreateTarGZipAsync(
                    provider, files, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("tar.gz", true) => _creationWriter.CreateTarGZipTreeAsync(
                    provider, files, directories, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("tar.bz2", false) => _creationWriter.CreateTarBZip2Async(
                    provider, files, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("tar.bz2", true) => _creationWriter.CreateTarBZip2TreeAsync(
                    provider, files, directories, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("gzip", false) => _creationWriter.CreateGZipAsync(
                    provider, files, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                ("bzip2", false) => _creationWriter.CreateBZip2Async(
                    provider, files, destinationPath, compressionPreset, progress,
                    operation.Cancellation.Token, beforeWriting, beforeCommit, reportWarning, operation.Id),
                _ => throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedFormat,
                    "creation.format_not_supported",
                    "The requested archive creation route is not available."),
            };
            await creation.ConfigureAwait(false);
            await SendTerminalAsync(
                    operation,
                    operation.WarningCount > 0
                        ? OperationPhase.CompletedWithWarnings
                        : OperationPhase.Completed,
                    null,
                    operation.LastWarning)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (ContainsRarEncoderContainmentFailure(exception))
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendTerminalAsync(
                    operation,
                    OperationPhase.Cancelled,
                    new ArchiveError(
                        "operation.cancelled",
                        ArchiveErrorCategory.Cancelled,
                        $"{format.ToUpperInvariant()} creation was cancelled."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SendTerminalAsync(operation, OperationPhase.Failed, ToError(exception))
                .ConfigureAwait(false);
        }
        finally
        {
            if (executionLeaseHeld)
            {
                _operationExecutionGate.Release();
            }

            CompleteOperation(operation);
        }
    }

    private async Task CancelOperationAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var request = frame.ReadPayload<CancelOperationRequest>();
        var found = _operations.TryGetValue(request.OperationId, out var operation);
        var requested = operation?.TryCancel() == true;
        await SendResponseAsync(frame, new CancelOperationResponse(found && requested), cancellationToken).ConfigureAwait(false);
    }

    private async Task CancelEntryStreamAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.EntryStreamLeases))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Entry stream leases were not negotiated for this connection.");
        }

        var request = frame.ReadPayload<CancelEntryStreamRequest>();
        if (request.StreamId.Value == Guid.Empty)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "entry.stream_invalid_cancellation_target",
                "The entry stream cancellation target is invalid.");
        }

        var requested = _entryStreamCancellations.TryGetValue(request.StreamId, out var target)
            && target.TryCancel();
        await SendResponseAsync(
            frame,
            new CancelEntryStreamResponse(requested),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CancelRequestAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        if (!_enabledFeatures.Contains(ProtocolFeatures.RequestCancellation))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProtocolMismatch,
                "protocol.feature_not_negotiated",
                "Request cancellation was not negotiated for this connection.");
        }

        var request = frame.ReadPayload<CancelRequestRequest>();
        if (request.TargetRequestId == Guid.Empty || request.TargetRequestId == frame.RequestId)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "request.invalid_cancellation_target",
                "The request cancellation target is invalid.");
        }

        var requested = _requestCancellations.TryGetValue(
                request.TargetRequestId,
                out var target)
            && target.TryCancel();
        await SendResponseAsync(
            frame,
            new CancelRequestResponse(requested),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseWorkerAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        _ = frame.ReadPayload<CloseRequest>();
        await SendResponseAsync(frame, new CloseResponse(true), cancellationToken).ConfigureAwait(false);
        _shutdown.Cancel();
    }

    private WorkerArchiveSession GetSession(ArchiveSessionId id) =>
        _sessions.TryGetValue(id, out var session)
            ? session
            : throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "session.not_found",
                "The archive session is closed or unknown.");

    private static string CanonicalizeRequestPath(string? path, string code)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > ProtocolConstants.MaximumPathCharacters)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                code,
                "The requested filesystem path is empty or exceeds the protocol limit.");
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                code,
                "The requested filesystem path is invalid.",
                exception);
        }
    }

    private static void ValidateEntryIds(IReadOnlyList<EntryId>? entryIds)
    {
        if (entryIds is null
            || entryIds.Count > 500_000
            || entryIds.Any(entryId => entryId.Value == Guid.Empty)
            || entryIds.Distinct().Count() != entryIds.Count)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "entry.invalid_selection",
                "The entry selection is missing, too large, or contains an invalid or duplicate identifier.");
        }
    }

    private static void ValidateEntryStreamRequest(OpenEntryStreamRequest request)
    {
        if (request.SessionId.Value == Guid.Empty
            || request.EntryId.Value == Guid.Empty
            || request.MaximumBytes is <= 0 or > ProtocolConstants.MaximumEntryStreamBytes
            || !IsEntryStreamPipeName(request.PipeName)
            || !IsLowercaseHex(request.BootstrapNonce, EntryStreamPipeProtocol.BootstrapNonceBytes * 2))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "entry.stream_invalid_request",
                "The entry stream request contains invalid identifiers, limits, or bootstrap data.");
        }
    }

    private static void ValidateNestedArchiveRequest(OpenNestedArchiveRequest request)
    {
        if (request.ParentSessionId.Value == Guid.Empty
            || request.EntryId.Value == Guid.Empty
            || request.MaximumBytes is <= 0 or > ProtocolConstants.MaximumNestedArchiveBytes
            || request.ParentPasswordSlotId == Guid.Empty
            || request.NestedPasswordSlotId == Guid.Empty
            || (request.ParentPasswordSlotId is { } parentSlot
                && request.NestedPasswordSlotId == parentSlot))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "nested.invalid_request",
                "The nested archive request contains invalid identifiers, limits, or secret slots.");
        }
    }

    private static void ValidateZipCreationRequest(StartZipCreationRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || request.DestinationPath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || request.Files is null
            || request.Files.Count > ProtocolConstants.MaximumZipCreationFiles
            || !Enum.IsDefined(request.CompressionPreset)
            || request.Files.Any(file =>
                file is null
                || string.IsNullOrWhiteSpace(file.SourcePath)
                || file.SourcePath.Length > ProtocolConstants.MaximumPathCharacters
                || !Path.IsPathFullyQualified(file.SourcePath)
                || string.IsNullOrWhiteSpace(file.ArchivePath)
                || file.ArchivePath.Length > ProtocolConstants.MaximumPathCharacters))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "creation.invalid_request",
                "The ZIP creation request contains invalid paths, files, or compression options.");
        }
    }

    private static void ValidateZipTreeCreationRequest(StartZipTreeCreationRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || request.DestinationPath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || request.Files is null
            || request.Directories is null
            || request.Directories.Count == 0
            || !TryCountWithinCreationLimit(
                request.Files.Count,
                request.Directories.Count,
                ProtocolConstants.MaximumZipCreationEntries)
            || !Enum.IsDefined(request.CompressionPreset)
            || request.Files.Any(file => !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath))
            || request.Directories.Any(directory =>
                !IsValidCreationMapping(directory?.SourcePath, directory?.ArchivePath)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "creation.invalid_request",
                "The ZIP tree creation request contains invalid paths, inputs, or compression options.");
        }
    }

    private static void ValidateTarCreationRequest(StartTarCreationRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || request.DestinationPath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || request.Files is null
            || request.Files.Count > ProtocolConstants.MaximumTarCreationFiles
            || request.CompressionPreset != ArchiveCompressionPreset.Store
            || request.Files.Any(file =>
                !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "tar.creation_invalid_request",
                "The TAR creation request contains invalid paths, files, or compression options.");
        }
    }

    private static void ValidateTarTreeCreationRequest(StartTarTreeCreationRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || request.DestinationPath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || request.Files is null
            || request.Directories is null
            || request.Directories.Count == 0
            || !TryCountWithinCreationLimit(
                request.Files.Count,
                request.Directories.Count,
                ProtocolConstants.MaximumTarCreationEntries)
            || request.CompressionPreset != ArchiveCompressionPreset.Store
            || request.Files.Any(file =>
                !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath))
            || request.Directories.Any(directory =>
                !IsValidCreationMapping(directory?.SourcePath, directory?.ArchivePath)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "tar.creation_invalid_request",
                "The TAR tree creation request contains invalid paths, inputs, or compression options.");
        }
    }

    private static void ValidateRarCreationRequest(StartRarCreationRequest request)
    {
        if (request is null
            || !IsValidCreationDestination(request.DestinationPath)
            || !request.DestinationPath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || request.Files is null
            || request.Files.Count == 0
            || request.Files.Count > ProtocolConstants.MaximumRarCreationFiles
            || !Enum.IsDefined(request.CompressionPreset)
            || request.Files.Any(file =>
                !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "rar.creation_invalid_request",
                "RAR creation requires at least one ordinary source file, valid archive paths, a .rar destination, and a supported compression preset.");
        }
    }

    private static void ValidateRarTreeCreationRequest(StartRarTreeCreationRequest request)
    {
        if (request is null
            || !IsValidCreationDestination(request.DestinationPath)
            || !request.DestinationPath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || request.Files is null
            || request.Directories is null
            || request.Directories.Count == 0
            || !TryCountWithinCreationLimit(
                request.Files.Count,
                request.Directories.Count,
                ProtocolConstants.MaximumRarCreationEntries)
            || !Enum.IsDefined(request.CompressionPreset)
            || request.Files.Any(file =>
                !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath))
            || request.Directories.Any(directory =>
                !IsValidCreationMapping(directory?.SourcePath, directory?.ArchivePath)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "rar.creation_invalid_request",
                "RAR tree creation requires valid source mappings, at least one directory root, a .rar destination, and a supported compression preset.");
        }
    }

    private static void ValidateManagedTarCreationRequest(
        string? destinationPath,
        IReadOnlyList<ArchiveCreationFile>? files,
        ArchiveCompressionPreset compressionPreset,
        bool gzip,
        string errorCode)
    {
        if (!IsValidCreationDestination(destinationPath)
            || files is null
            || files.Count > ProtocolConstants.MaximumTarCreationFiles
            || !IsSupportedManagedPreset(compressionPreset, gzip)
            || files.Any(file => !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                errorCode,
                "The compressed TAR creation request contains invalid paths, files, or compression options.");
        }
    }

    private static void ValidateManagedTarTreeCreationRequest(
        string? destinationPath,
        IReadOnlyList<ArchiveCreationFile>? files,
        IReadOnlyList<ArchiveCreationDirectory>? directories,
        ArchiveCompressionPreset compressionPreset,
        bool gzip,
        string errorCode)
    {
        if (!IsValidCreationDestination(destinationPath)
            || files is null
            || directories is null
            || directories.Count == 0
            || !TryCountWithinCreationLimit(
                files.Count,
                directories.Count,
                ProtocolConstants.MaximumTarCreationEntries)
            || !IsSupportedManagedPreset(compressionPreset, gzip)
            || files.Any(file => !IsValidCreationMapping(file?.SourcePath, file?.ArchivePath))
            || directories.Any(directory =>
                !IsValidCreationMapping(directory?.SourcePath, directory?.ArchivePath)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                errorCode,
                "The compressed TAR tree request contains invalid paths, inputs, or compression options.");
        }
    }

    private static void ValidateRawCreationRequest(
        string? destinationPath,
        string? sourcePath,
        ArchiveCompressionPreset compressionPreset,
        bool gzip,
        string errorCode)
    {
        var suffixes = gzip
            ? new[] { ".gz", ".gzip" }
            : [".bz2", ".bzip2"];
        if (!IsValidCreationDestination(destinationPath)
            || string.IsNullOrWhiteSpace(sourcePath)
            || sourcePath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(sourcePath)
            || !IsSupportedManagedPreset(compressionPreset, gzip)
            || !suffixes.Any(suffix => destinationPath!.EndsWith(
                suffix,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                errorCode,
                $"The raw {(gzip ? "GZIP" : "BZIP2")} request requires one ordinary source and a matching new destination suffix.");
        }
    }

    private static bool IsValidCreationDestination(string? destinationPath) =>
        !string.IsNullOrWhiteSpace(destinationPath)
        && destinationPath.Length <= ProtocolConstants.MaximumPathCharacters
        && Path.IsPathFullyQualified(destinationPath);

    private static bool IsSupportedManagedPreset(
        ArchiveCompressionPreset compressionPreset,
        bool gzip) =>
        gzip
            ? Enum.IsDefined(compressionPreset)
            : compressionPreset == ArchiveCompressionPreset.Balanced;

    private static void ValidateZipRebuildCopyRequest(StartZipRebuildCopyRequest request)
    {
        if (request is null
            || request.SessionId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || request.DestinationPath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || request.EntriesToDelete is null
            || request.Files is null
            || request.EntriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Files.Count > ProtocolConstants.MaximumZipCreationEntries
            || !Enum.IsDefined(request.CompressionPreset)
            || request.EntriesToDelete.Any(key =>
                key is null
                || string.IsNullOrWhiteSpace(key.NormalizedPath)
                || key.NormalizedPath.Length > ProtocolConstants.MaximumPathCharacters
                || key.DuplicateOrdinal < 0)
            || request.EntriesToDelete.Distinct().Count() != request.EntriesToDelete.Count
            || request.Files.Any(file =>
                file is null
                || !IsValidCreationMapping(file.SourcePath, file.ArchivePath)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "zip.rebuild_invalid_request",
                "The ZIP Rebuild Copy request contains invalid session evidence, paths, edit keys, mapped files, or compression options.");
        }
    }

    private static void ValidateZipRebuildRenameRequest(StartZipRebuildRenameRequest request)
    {
        if (request is null
            || request.SessionId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || request.DestinationPath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || request.EntriesToDelete is null
            || request.Files is null
            || request.Renames is null
            || request.EntriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Files.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Renames.Count > ProtocolConstants.MaximumZipCreationEntries
            || !Enum.IsDefined(request.CompressionPreset)
            || request.EntriesToDelete.Any(key => !IsValidRebuildEntryKey(key))
            || request.EntriesToDelete.Distinct().Count() != request.EntriesToDelete.Count
            || request.Files.Any(file =>
                file is null
                || !IsValidCreationMapping(file.SourcePath, file.ArchivePath))
            || request.Renames.Any(rename => !IsValidRebuildRename(rename))
            || request.Renames.Select(rename => rename.EntryKey).Distinct().Count()
                != request.Renames.Count
            || request.Renames.Select(rename => rename.EntryKey)
                .Intersect(request.EntriesToDelete).Any()
            || request.Renames.Select(rename => ArchivePathRules.GetCollisionKey(rename.FinalArchivePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != request.Renames.Count)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "zip.rebuild_rename_invalid_request",
                "The ZIP Rebuild Rename request contains invalid session evidence, paths, edit keys, final paths, counts, mapped files, or compression options.");
        }
    }

    private static void ValidateZipRebuildTreePlanRequest(GetZipRebuildTreePlanRequest request)
    {
        if (request is null || request.SessionId.Value == Guid.Empty)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "zip.rebuild_tree_plan_invalid_request",
                "The ZIP Rebuild Tree Plan request did not contain a valid session identifier.");
        }
    }

    private static void ValidateZipRebuildSourceFolderReviewRequest(
        GetZipRebuildSourceFolderReviewRequest request)
    {
        if (request is null
            || request.SessionId.Value == Guid.Empty
            || request.Directories is null
            || request.Directories.Count == 0
            || request.Directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots
            || request.Directories.Any(directory =>
                directory is null
                || !IsValidCreationMapping(directory.SourcePath, directory.ArchivePath)
                || !ArchivePathRules.TryNormalize(
                    directory.ArchivePath.TrimEnd('/', '\\'),
                    out var normalized,
                    out _,
                    out _)
                || string.IsNullOrWhiteSpace(normalized)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "zip.rebuild_source_folder_review_invalid_request",
                "The ZIP Rebuild source-folder review request contains invalid session, root, or bounded path data.");
        }

        if (!TryGetSourceFolderReviewPathPayloadBytes(request.Directories, out var pathPayloadBytes)
            || pathPayloadBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "zip.rebuild_source_folder_review_path_limit",
                "The ZIP Rebuild source-folder review request exceeds the bounded UTF-8 path payload limit.");
        }

        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetZipRebuildSourceFolderReview,
                request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "zip.rebuild_source_folder_review_frame_limit",
                "The ZIP Rebuild source-folder review request exceeds the bounded protocol frame size.",
                exception);
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

    private static void ValidateZipRebuildTreeEditRequest(StartZipRebuildTreeEditRequest request)
    {
        if (request is null
            || request.SessionId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || request.DestinationPath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || request.EntriesToDelete is null
            || request.Files is null
            || request.Renames is null
            || request.Directories is null
            || request.TreeActions is null
            || request.EntriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Files.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Renames.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots
            || request.TreeActions.Count > ProtocolConstants.MaximumZipRebuildTreeActions
            || (long)request.EntriesToDelete.Count
                + request.Files.Count
                + request.Renames.Count
                + request.Directories.Count
                + request.TreeActions.Count > ProtocolConstants.MaximumZipRebuildDirectives
            || !Enum.IsDefined(request.CompressionPreset)
            || request.EntriesToDelete.Any(key => !IsValidRebuildEntryKey(key))
            || request.EntriesToDelete.Distinct().Count() != request.EntriesToDelete.Count
            || request.Files.Any(file => file is null || !IsValidCreationMapping(file.SourcePath, file.ArchivePath))
            || request.Renames.Any(rename => !IsValidRebuildRename(rename))
            || request.Renames.Select(rename => rename.EntryKey).Distinct().Count() != request.Renames.Count
            || request.Renames.Select(rename => rename.EntryKey).Intersect(request.EntriesToDelete).Any()
            || request.Renames.Select(rename => ArchivePathRules.GetCollisionKey(rename.FinalArchivePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Renames.Count
            || request.Directories.Any(directory =>
                directory is null
                || !IsValidCreationMapping(directory.SourcePath, directory.ArchivePath)
                || !ArchivePathRules.TryNormalize(directory.ArchivePath.TrimEnd('/', '\\'), out _, out _, out _))
            || request.TreeActions.Any(action => !IsValidTreeAction(action))
            || !TryGetTreePathPayloadBytes(request, out var pathPayloadBytes)
            || pathPayloadBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "zip.rebuild_tree_edit_invalid_request",
                "The ZIP Rebuild Tree Edit request contains invalid paths, mappings, actions, counts, or bounded metadata.");
        }
    }

    private static void ValidateZipRebuildMetadataRequest(StartZipRebuildMetadataRequest request)
    {
        if (request is null
            || request.SessionId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || request.DestinationPath.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || request.EntriesToDelete is null
            || request.Files is null
            || request.Renames is null
            || request.Directories is null
            || request.TreeActions is null
            || request.Metadata is null
            || request.EntriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Files.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Renames.Count > ProtocolConstants.MaximumZipCreationEntries
            || request.Directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots
            || request.TreeActions.Count > ProtocolConstants.MaximumZipRebuildTreeActions
            || request.Metadata.Count > ProtocolConstants.MaximumZipRebuildMetadataDirectives
            || (long)request.EntriesToDelete.Count
                + request.Files.Count
                + request.Renames.Count
                + request.Directories.Count
                + request.TreeActions.Count
                + request.Metadata.Count > ProtocolConstants.MaximumZipRebuildDirectives
            || !Enum.IsDefined(request.CompressionPreset)
            || request.EntriesToDelete.Any(key => !IsValidRebuildEntryKey(key))
            || request.EntriesToDelete.Distinct().Count() != request.EntriesToDelete.Count
            || request.Files.Any(file => file is null || !IsValidCreationMapping(file.SourcePath, file.ArchivePath))
            || request.Renames.Any(rename => !IsValidRebuildRename(rename))
            || request.Renames.Select(rename => rename.EntryKey).Distinct().Count() != request.Renames.Count
            || request.Renames.Select(rename => rename.EntryKey).Intersect(request.EntriesToDelete).Any()
            || request.Renames.Select(rename => ArchivePathRules.GetCollisionKey(rename.FinalArchivePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Renames.Count
            || request.Directories.Any(directory =>
                directory is null
                || !IsValidCreationMapping(directory.SourcePath, directory.ArchivePath)
                || !ArchivePathRules.TryNormalize(directory.ArchivePath.TrimEnd('/', '\\'), out _, out _, out _))
            || request.TreeActions.Any(action => !IsValidTreeAction(action))
            || request.Metadata.Any(directive => !IsValidMetadataDirective(directive))
            || request.Metadata.Select(MetadataTargetIdentity).Distinct(StringComparer.Ordinal).Count()
                != request.Metadata.Count
            || !TryGetMetadataPathPayloadBytes(request, out var pathPayloadBytes)
            || pathPayloadBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "zip.rebuild_metadata_invalid_request",
                "The ZIP Rebuild Metadata request contains invalid paths, mappings, actions, metadata, counts, or bounded data.");
        }

        try
        {
            ProtocolStream.ValidateJsonFrameSize(ProtocolMessageKinds.StartZipRebuildMetadata, request);
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "zip.rebuild_metadata_frame_limit",
                "The ZIP Rebuild Metadata request exceeds the bounded protocol frame size.",
                exception);
        }
    }

    private static bool IsValidMetadataDirective(ArchiveRebuildMetadataDirective? directive)
    {
        if (directive is null
            || directive.Target is null
            || !Enum.IsDefined(directive.Target.Kind)
            || (directive.LastWriteTime is null && directive.AttributesLowWord is null)
            || (directive.AttributesLowWord is { } attributes && (attributes & ~((ushort)0x0027)) != 0))
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
        else
        {
            return false;
        }

        if (directive.LastWriteTime is { } timestamp)
        {
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

            if (timestamp.Year is < 1980 or > 2107
                || timestamp.Second is < 0 or > 58
                || (timestamp.Second & 1) != 0)
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

    private static bool TryGetMetadataPathPayloadBytes(
        StartZipRebuildMetadataRequest request,
        out int bytes)
    {
        if (!TryGetTreePathPayloadBytes(request, out bytes))
        {
            return false;
        }

        long total = bytes;
        try
        {
            foreach (var directive in request.Metadata)
            {
                if (directive.Target.RetainedEntryKey is { } key)
                {
                    total = checked(total + Encoding.UTF8.GetByteCount(key.NormalizedPath));
                }
                if (directive.Target.MappedFinalArchivePath is { } path)
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

    private static bool TryGetTreePathPayloadBytes(
        StartZipRebuildMetadataRequest request,
        out int bytes)
    {
        long total = 0;
        try
        {
            total = checked(Encoding.UTF8.GetByteCount(request.DestinationPath));
            foreach (var key in request.EntriesToDelete)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(key.NormalizedPath));
            }
            foreach (var file in request.Files)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(file.SourcePath));
                total = checked(total + Encoding.UTF8.GetByteCount(file.ArchivePath));
            }
            foreach (var rename in request.Renames)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(rename.EntryKey.NormalizedPath));
                total = checked(total + Encoding.UTF8.GetByteCount(rename.FinalArchivePath));
            }
            foreach (var directory in request.Directories)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(directory.SourcePath));
                total = checked(total + Encoding.UTF8.GetByteCount(directory.ArchivePath));
            }
            foreach (var action in request.TreeActions)
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

    private static bool IsValidTreeAction(ArchiveRebuildTreeAction? action)
    {
        if (action is null
            || string.IsNullOrWhiteSpace(action.NormalizedPath)
            || action.NormalizedPath.Length > ProtocolConstants.MaximumPathCharacters
            || action.NormalizedPath.EndsWith('/')
            || action.NormalizedPath.TrimEnd('/') is { Length: 0 }
            || !ArchivePathRules.TryNormalize(action.NormalizedPath.TrimEnd('/'), out var normalized, out _, out _)
            || !string.Equals(
                normalized.Replace('\\', '/'),
                action.NormalizedPath.TrimEnd('/'),
                StringComparison.Ordinal)
            || !Enum.IsDefined(action.Kind)
            || !IsLowercaseHex(action.ExpectedRevision, 64))
        {
            return false;
        }

        return action.Kind == ArchiveRebuildTreeActionKind.Delete
            ? action.FinalArchivePath is null
            : !string.IsNullOrWhiteSpace(action.FinalArchivePath)
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

    private static bool TryGetTreePathPayloadBytes(
        StartZipRebuildTreeEditRequest request,
        out int bytes)
    {
        long total = 0;
        try
        {
            total = checked(Encoding.UTF8.GetByteCount(request.DestinationPath));
            foreach (var key in request.EntriesToDelete)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(key.NormalizedPath));
            }

            foreach (var file in request.Files)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(file.SourcePath));
                total = checked(total + Encoding.UTF8.GetByteCount(file.ArchivePath));
            }

            foreach (var rename in request.Renames)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(rename.EntryKey.NormalizedPath));
                total = checked(total + Encoding.UTF8.GetByteCount(rename.FinalArchivePath));
            }

            foreach (var directory in request.Directories)
            {
                total = checked(total + Encoding.UTF8.GetByteCount(directory.SourcePath));
                total = checked(total + Encoding.UTF8.GetByteCount(directory.ArchivePath));
            }

            foreach (var action in request.TreeActions)
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

    private static bool TryCountWithinCreationLimit(
        int files,
        int directories,
        int maximumEntries)
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

    private static bool IsValidRebuildRename(ArchiveRebuildRename? rename) =>
        rename is not null
        && IsValidRebuildEntryKey(rename.EntryKey)
        && !string.IsNullOrWhiteSpace(rename.FinalArchivePath)
        && rename.FinalArchivePath.Length <= ProtocolConstants.MaximumPathCharacters
        && ArchivePathRules.TryNormalize(rename.FinalArchivePath, out _, out _, out _);

    private static bool IsEntryStreamPipeName(string? value)
    {
        const string prefix = "rainarchives-entry-";
        return value is { Length: <= ProtocolConstants.MaximumEntryStreamPipeNameCharacters }
            && value.Length == prefix.Length + 64
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && IsLowercaseHex(value[prefix.Length..], 64);
    }

    private static bool IsLowercaseHex(string? value, int expectedLength) =>
        value is not null
        && value.Length == expectedLength
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static void EnsureOperationSupportedForSession(WorkerArchiveSession session)
    {
        if (session.Descriptor.VolumeSetKind == ArchiveVolumeSetKind.ConfirmedMultipart)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "archive.multipart_operation_deferred",
                "Multipart archives are list-only until every source volume can be identity-locked and fingerprinted.");
        }

        if (session.Descriptor.VolumeSetKind is null or ArchiveVolumeSetKind.Unknown)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "archive.volume_set_unknown",
                "Data operations require the provider to prove that the source is a complete single-volume archive.");
        }
    }

    private static void ValidateVolumeSetContract(IArchiveHandle handle)
    {
        const ArchiveCapability dataOperationCapabilities = ArchiveCapability.OpenEntryStream
            | ArchiveCapability.Test
            | ArchiveCapability.Extract
            | ArchiveCapability.UpdateByRewrite;
        var readsVolumes = handle.Capabilities.HasFlag(ArchiveCapability.ReadVolumes);
        var advertisesDataOperations = (handle.Capabilities & dataOperationCapabilities) != 0;
        var valid = Enum.IsDefined(handle.VolumeSetKind)
            && handle.IsMultipart == (handle.VolumeSetKind == ArchiveVolumeSetKind.ConfirmedMultipart)
            && (handle.VolumeSetKind switch
            {
                ArchiveVolumeSetKind.Single => !readsVolumes,
                ArchiveVolumeSetKind.ConfirmedMultipart => readsVolumes && !advertisesDataOperations,
                ArchiveVolumeSetKind.Unknown => !readsVolumes && !advertisesDataOperations,
                _ => false,
            });
        if (!valid)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "provider.invalid_volume_set_contract",
                "The archive provider returned inconsistent volume-set identity and capabilities.");
        }
    }

    private WorkerOperation AddOperation(OperationKind kind)
    {
        if (!_operationSlots.Wait(0))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "operation.queue_limit",
                $"A worker can queue at most {ProtocolConstants.MaximumQueuedOperations} operations.");
        }

        var operation = new WorkerOperation(kind, _shutdown.Token);
        var added = false;
        lock (_operationActivityGate)
        {
            added = _operations.TryAdd(operation.Id, operation);
        }

        if (!added)
        {
            operation.Dispose();
            _operationSlots.Release();
            throw new InvalidOperationException("Could not allocate an operation.");
        }

        return operation;
    }

    private void TrackOperation(WorkerOperation operation, Task task)
    {
        if (!_operationTasks.TryAdd(operation.Id, task))
        {
            operation.TryCancel();
            throw new InvalidOperationException("Could not track the archive operation task.");
        }

        _ = task.ContinueWith(
            _completed => _operationTasks.TryRemove(operation.Id, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteOperation(WorkerOperation operation)
    {
        var removed = false;
        lock (_operationActivityGate)
        {
            removed = _operations.TryRemove(operation.Id, out _);
            if (removed)
            {
                var snapshot = operation.CreateActivitySnapshot();
                if (IsTerminalPhase(snapshot.Phase)
                    && snapshot.CompletedAtUtc is not null)
                {
                    InsertRetainedOperationActivity(snapshot);
                    while (_retainedOperationActivity.Count
                        > ProtocolConstants.MaximumRetainedOperationActivity)
                    {
                        _retainedOperationActivity.RemoveLast();
                        _operationActivityHistoryTruncated = true;
                    }
                }
            }
        }

        if (removed)
        {
            operation.Dispose();
            _operationSlots.Release();
        }
    }

    private ValueTask SendStateAsync(WorkerOperation operation, OperationPhase phase) =>
        SendEventAsync(
            operation,
            sequence => new OperationEvent(
                operation.Id,
                sequence,
                OperationEventKind.StateChanged,
                phase),
            operation.Cancellation.Token);

    private ValueTask SendProgressAsync(WorkerOperation operation, OperationProgress progress)
    {
        if (!operation.ShouldReportProgress(progress))
        {
            return ValueTask.CompletedTask;
        }

        return SendEventAsync(
            operation,
            sequence => new OperationEvent(
                operation.Id,
                sequence,
                OperationEventKind.Progress,
                OperationPhase.Running,
                progress),
            operation.Cancellation.Token);
    }

    private ValueTask SendWarningAsync(WorkerOperation operation, ArchiveWarning warning)
    {
        return SendEventAsync(
            operation,
            sequence => new OperationEvent(
                operation.Id,
                sequence,
                OperationEventKind.Warning,
                OperationPhase.Running,
                Warning: warning),
            operation.Cancellation.Token);
    }

    private ValueTask SendTerminalAsync(
        WorkerOperation operation,
        OperationPhase phase,
        ArchiveError? error,
        ArchiveWarning? warning = null) =>
        SendEventAsync(
            operation,
            sequence => new OperationEvent(
                operation.Id,
                sequence,
                OperationEventKind.Terminal,
                phase,
                Warning: warning,
                Error: error),
            _shutdown.Token);

    private async ValueTask SendEventAsync(
        WorkerOperation operation,
        Func<long, OperationEvent> createEvent,
        CancellationToken admissionToken)
    {
        await _writeGate.WaitAsync(admissionToken).ConfigureAwait(false);
        try
        {
            var operationEvent = createEvent(operation.NextSequence());
            await ProtocolStream.WriteJsonAsync(
                _stream,
                ProtocolMessageKinds.OperationEvent,
                Guid.Empty,
                operationEvent,
                operationEvent.Sequence,
                _shutdown.Token).ConfigureAwait(false);
            operation.RecordSentEvent(operationEvent, DateTimeOffset.UtcNow);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SendOperationActivityResponseAsync(
        ProtocolFrame request,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = CreateOperationActivityResponse();
            await ProtocolStream.WriteJsonAsync(
                _stream,
                $"{request.Kind}.response",
                request.RequestId,
                response,
                request.Sequence,
                _shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private GetOperationActivityResponse CreateOperationActivityResponse()
    {
        OperationActivitySnapshot[] snapshots;
        bool historyTruncated;
        lock (_operationActivityGate)
        {
            snapshots = _operations.Values
                .Select(operation => operation.CreateActivitySnapshot())
                .Concat(_retainedOperationActivity)
                .ToArray();
            historyTruncated = _operationActivityHistoryTruncated;
        }

        var operations = snapshots
            .Where(snapshot => !IsTerminalPhase(snapshot.Phase))
            .OrderBy(snapshot => snapshot.CreatedAtUtc)
            .ThenBy(snapshot => snapshot.OperationId.Value)
            .Concat(
                snapshots
                    .Where(snapshot => IsTerminalPhase(snapshot.Phase))
                    .OrderByDescending(snapshot => snapshot.CompletedAtUtc)
                    .ThenBy(snapshot => snapshot.OperationId.Value))
            .ToArray();
        var snapshotAtUtc = DateTimeOffset.UtcNow;
        if (operations.Length > 0)
        {
            var latestUpdate = operations.Max(operation => operation.UpdatedAtUtc);
            if (snapshotAtUtc < latestUpdate)
            {
                snapshotAtUtc = latestUpdate;
            }
        }

        return new GetOperationActivityResponse(
            snapshotAtUtc,
            MaximumActiveAndQueuedOperations: ProtocolConstants.MaximumQueuedOperations,
            MaximumConcurrentOperations: ProtocolConstants.MaximumConcurrentOperations,
            QueueOrdering: OperationQueueOrdering.Unspecified,
            MaximumRetainedTerminalOperations:
                ProtocolConstants.MaximumRetainedOperationActivity,
            SupportsReordering: false,
            SupportsPause: false,
            SupportsRetry: false,
            PersistsAcrossWorkerRestart: false,
            HistoryTruncated: historyTruncated,
            Operations: operations);
    }

    private static bool IsTerminalPhase(OperationPhase phase) =>
        phase is OperationPhase.Completed
            or OperationPhase.CompletedWithWarnings
            or OperationPhase.Cancelled
            or OperationPhase.Failed;

    private void InsertRetainedOperationActivity(OperationActivitySnapshot snapshot)
    {
        var candidateCompletedAtUtc = snapshot.CompletedAtUtc!.Value;
        var cursor = _retainedOperationActivity.First;
        while (cursor is not null)
        {
            var existing = cursor.Value;
            var existingCompletedAtUtc = existing.CompletedAtUtc!.Value;
            if (candidateCompletedAtUtc > existingCompletedAtUtc
                || (candidateCompletedAtUtc == existingCompletedAtUtc
                    && snapshot.OperationId.Value.CompareTo(existing.OperationId.Value) < 0))
            {
                _retainedOperationActivity.AddBefore(cursor, snapshot);
                return;
            }

            cursor = cursor.Next;
        }

        _retainedOperationActivity.AddLast(snapshot);
    }

    private Task SendResponseAsync<T>(
        ProtocolFrame request,
        T response,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(
            $"{request.Kind}.response",
            request.RequestId,
            response,
            request.Sequence,
            cancellationToken).AsTask();

    private Task SendErrorAsync(ProtocolFrame request, ArchiveError error, CancellationToken cancellationToken) =>
        WriteJsonAsync(
            ProtocolMessageKinds.Error,
            request.RequestId,
            new ErrorResponse(error),
            request.Sequence,
            cancellationToken).AsTask();

    private async ValueTask WriteJsonAsync<T>(
        string kind,
        Guid requestId,
        T payload,
        long sequence,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProtocolStream.WriteJsonAsync(
                _stream,
                kind,
                requestId,
                payload,
                sequence,
                _shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void ValidateRequestEnvelope(ProtocolFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.Kind)
            || frame.RequestId == Guid.Empty
            || frame.Sequence <= 0)
        {
            throw new InvalidDataException("Client request envelope was invalid.");
        }

        var expected = checked(_lastRequestSequence + 1);
        if (frame.Sequence != expected)
        {
            throw new InvalidDataException("Client request sequence was not contiguous.");
        }

        if (_seenRequestIds.Count >= ProtocolConstants.MaximumRequestsPerConnection)
        {
            throw new InvalidDataException("The connection request limit was reached.");
        }

        if (!_seenRequestIds.Add(frame.RequestId))
        {
            throw new InvalidDataException("A client request identifier was reused.");
        }

        _lastRequestSequence = frame.Sequence;
    }

    private static IReadOnlyDictionary<EntryId, EntrySafetyAnnotation> CreateSafetyAnnotations(
        IReadOnlyList<ExtractionPathIssue> issues,
        CancellationToken cancellationToken)
    {
        var byEntry = new Dictionary<EntryId, List<ExtractionPathIssue>>();
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byEntry.TryGetValue(issue.EntryId, out var entryIssues))
            {
                entryIssues = [];
                byEntry.Add(issue.EntryId, entryIssues);
            }

            entryIssues.Add(issue);
        }

        var annotations = new Dictionary<EntryId, EntrySafetyAnnotation>(byEntry.Count);
        foreach (var pair in byEntry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryIssues = pair.Value;
            var status = entryIssues.Any(issue => issue.Code is "path.collision"
                    or "path.file_directory_collision"
                    or "path.parent_spelling_collision")
                ? EntrySafetyStatus.PathCollision
                : entryIssues.Any(issue => issue.Code == "path.link_not_materialized")
                    ? EntrySafetyStatus.UnsupportedLink
                    : entryIssues.Any(issue => issue.Code == "path.unsupported_entry_kind")
                        ? EntrySafetyStatus.UnsupportedEntryKind
                        : EntrySafetyStatus.UnsafePath;
            annotations.Add(
                pair.Key,
                new EntrySafetyAnnotation(
                    status,
                    string.Join(
                        " ",
                        entryIssues.Select(issue => issue.Message).Distinct(StringComparer.Ordinal))));
        }

        return annotations;
    }

    private static long? TrySum(IEnumerable<long?> sizes)
    {
        try
        {
            long total = 0;
            foreach (var size in sizes)
            {
                if (size is null or < 0)
                {
                    return null;
                }

                total = checked(total + size.Value);
            }

            return total;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long EstimateListEntryJsonBytes(ArchiveEntryDescriptor entry)
    {
        var characters = (long)entry.RawPath.Length
            + entry.DisplayName.Length
            + (entry.CompressionMethod?.Length ?? 0)
            + (entry.IntegrityValue?.Length ?? 0)
            + (entry.LinkTarget?.Length ?? 0)
            + (entry.SafetyReason?.Length ?? 0);

        // System.Text.Json can represent one UTF-16 code unit as a six-byte \uXXXX
        // escape. The fixed allowance covers property names, numbers, GUIDs and commas.
        return checked(2048 + (characters * 6));
    }

    private static bool ContainsRarEncoderContainmentFailure(Exception exception)
    {
        if (exception is WinRarEncoderContainmentException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(ContainsRarEncoderContainmentFailure);
        }

        return exception.InnerException is { } inner
            && !ReferenceEquals(inner, exception)
            && ContainsRarEncoderContainmentFailure(inner);
    }

    internal static ArchiveError ToError(Exception exception)
    {
        exception = GetPrimaryFailure(exception);
        var diagnosticId = Guid.NewGuid().ToString("N");
        return exception switch
        {
            ArchiveProviderException provider => new ArchiveError(
                provider.Code,
                provider.Category,
                provider.Message,
                provider.Category is ArchiveErrorCategory.PasswordRequired or ArchiveErrorCategory.IncorrectPassword,
                DiagnosticId: diagnosticId),
            FileNotFoundException => new ArchiveError(
                "source.not_found",
                ArchiveErrorCategory.InvalidRequest,
                "The archive source was not found.",
                DiagnosticId: diagnosticId),
            UnauthorizedAccessException => new ArchiveError(
                "filesystem.permission_denied",
                ArchiveErrorCategory.PermissionDenied,
                "Access to the requested path was denied.",
                true,
                DiagnosticId: diagnosticId),
            DirectoryNotFoundException => new ArchiveError(
                "filesystem.directory_not_found",
                ArchiveErrorCategory.InvalidRequest,
                "A requested filesystem directory does not exist.",
                DiagnosticId: diagnosticId),
            PathTooLongException => new ArchiveError(
                "filesystem.path_too_long",
                ArchiveErrorCategory.InvalidRequest,
                "A requested filesystem path exceeds the supported length.",
                DiagnosticId: diagnosticId),
            ArgumentException or NotSupportedException => new ArchiveError(
                "request.invalid_argument",
                ArchiveErrorCategory.InvalidRequest,
                "The worker request contains an invalid argument.",
                DiagnosticId: diagnosticId),
            InvalidDataException or JsonException => new ArchiveError(
                "protocol.invalid_request_frame",
                ArchiveErrorCategory.ProtocolMismatch,
                "The worker request payload is invalid.",
                DiagnosticId: diagnosticId),
            OperationCanceledException => new ArchiveError(
                "operation.cancelled",
                ArchiveErrorCategory.Cancelled,
                "The operation was cancelled.",
                DiagnosticId: diagnosticId),
            IOException io when IsDiskFull(io) => new ArchiveError(
                "resource.insufficient_space",
                ArchiveErrorCategory.InsufficientSpace,
                "The destination does not have enough available space.",
                true,
                DiagnosticId: diagnosticId),
            IOException => new ArchiveError(
                "filesystem.io_failure",
                ArchiveErrorCategory.ProviderFailure,
                "A filesystem or archive I/O operation failed.",
                true,
                DiagnosticId: diagnosticId),
            Win32Exception native when IsPermissionDenied(native) => new ArchiveError(
                "filesystem.permission_denied",
                ArchiveErrorCategory.PermissionDenied,
                "Access to the requested path was denied.",
                true,
                DiagnosticId: diagnosticId),
            Win32Exception => new ArchiveError(
                "filesystem.native_failure",
                ArchiveErrorCategory.ProviderFailure,
                "A native filesystem safety check failed.",
                true,
                DiagnosticId: diagnosticId),
            _ => new ArchiveError(
                "worker.unexpected_failure",
                ArchiveErrorCategory.WorkerFailure,
                "The archive worker encountered an unexpected failure.",
                DiagnosticId: diagnosticId),
        };
    }

    private static Exception GetPrimaryFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        while (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            // Staged operations put the authoritative operation failure first
            // and append cleanup failures. Preserve that ordering at the wire
            // boundary instead of collapsing a known failure to WorkerFailure.
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }

    private static bool IsDiskFull(IOException exception)
    {
        var win32Code = exception.HResult & 0xFFFF;
        return win32Code is 0x27 or 0x70;
    }

    private static bool IsPermissionDenied(Win32Exception exception) =>
        exception.NativeErrorCode is 5 or 1314;

    private static bool IsControlRequest(string kind) =>
        kind is ProtocolMessageKinds.CancelRequest
            or ProtocolMessageKinds.CancelOperation
            or ProtocolMessageKinds.CancelEntryStream
            or ProtocolMessageKinds.CloseWorker;

    private void SettleSecretSlot(Guid? slotId)
    {
        if (slotId is { } id)
        {
            _secrets.Discard(id);
        }
    }

    private void SettleOperationSecret(Guid? slotId, byte[] password)
    {
        // A terminal event is authoritative to the client, so settle both the
        // unconsumed slot and any consumed local copy before publishing it.
        SettleSecretSlot(slotId);
        CryptographicOperations.ZeroMemory(password);
    }

    private void SettleQueuedRequestSecret(ProtocolFrame frame)
    {
        if (frame.Kind is not ProtocolMessageKinds.OpenArchive
            and not ProtocolMessageKinds.OpenNestedArchive
            and not ProtocolMessageKinds.OpenEntryStream
            and not ProtocolMessageKinds.ReopenNestedWorkspaceRecovery)
        {
            return;
        }

        try
        {
            switch (frame.Kind)
            {
                case ProtocolMessageKinds.OpenArchive:
                    SettleSecretSlot(frame.ReadPayload<OpenArchiveRequest>().PasswordSlotId);
                    break;
                case ProtocolMessageKinds.OpenNestedArchive:
                    var nested = frame.ReadPayload<OpenNestedArchiveRequest>();
                    SettleSecretSlot(nested.ParentPasswordSlotId);
                    SettleSecretSlot(nested.NestedPasswordSlotId);
                    break;
                case ProtocolMessageKinds.ReopenNestedWorkspaceRecovery:
                    SettleSecretSlot(
                        frame.ReadPayload<ReopenNestedWorkspaceRecoveryRequest>().PasswordSlotId);
                    break;
                default:
                    SettleSecretSlot(frame.ReadPayload<OpenEntryStreamRequest>().PasswordSlotId);
                    break;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            // A malformed request cannot identify a valid slot. Dispatch will return the
            // protocol error for that request if it reaches normal request handling.
        }
    }

    private NestedByteReservation ReserveNestedArchiveBytes(long maximumBytes)
    {
        lock (_nestedBudgetGate)
        {
            long next;
            try
            {
                next = checked(_activeNestedArchiveBytes + maximumBytes);
            }
            catch (OverflowException exception)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "nested.active_byte_limit",
                    "The active nested-archive byte budget was exceeded.",
                    exception);
            }

            if (next > ProtocolConstants.MaximumActiveNestedArchiveBytes)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "nested.active_byte_limit",
                    $"Active nested archives can reserve at most {ProtocolConstants.MaximumActiveNestedArchiveBytes:N0} decoded bytes.");
            }

            _activeNestedArchiveBytes = next;
            return new NestedByteReservation(this, maximumBytes);
        }
    }

    private void ResizeNestedArchiveReservation(long previousBytes, long actualBytes)
    {
        lock (_nestedBudgetGate)
        {
            if (previousBytes < 0
                || actualBytes < 0
                || actualBytes > previousBytes
                || previousBytes > _activeNestedArchiveBytes)
            {
                throw new InvalidOperationException("Nested-archive byte reservation accounting is invalid.");
            }

            _activeNestedArchiveBytes = checked(
                _activeNestedArchiveBytes - previousBytes + actualBytes);
        }
    }

    private void ReleaseNestedArchiveReservation(long bytes)
    {
        lock (_nestedBudgetGate)
        {
            if (bytes < 0 || bytes > _activeNestedArchiveBytes)
            {
                throw new InvalidOperationException("Nested-archive byte reservation accounting underflowed.");
            }

            _activeNestedArchiveBytes -= bytes;
        }
    }

    private sealed class NestedByteReservation : IDisposable
    {
        private WorkerHost? _owner;
        private long _bytes;

        public NestedByteReservation(WorkerHost owner, long bytes)
        {
            _owner = owner;
            _bytes = bytes;
        }

        public void Commit(long actualBytes)
        {
            var owner = _owner
                ?? throw new ObjectDisposedException(nameof(NestedByteReservation));
            var previous = _bytes;
            owner.ResizeNestedArchiveReservation(previous, actualBytes);
            _bytes = actualBytes;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                owner.ReleaseNestedArchiveReservation(_bytes);
            }
        }
    }

    private sealed class WorkerRequestCancellation : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public WorkerRequestCancellation(CancellationToken connectionCancellationToken)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellationToken);
        }

        public CancellationToken Token => _source.Token;

        public bool TryCancel()
        {
            lock (_gate)
            {
                if (_disposed || _source.IsCancellationRequested)
                {
                    return false;
                }

                _source.Cancel();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _source.Dispose();
            }
        }
    }
}
