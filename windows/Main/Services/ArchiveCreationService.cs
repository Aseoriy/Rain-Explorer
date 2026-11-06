using System.IO;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>
/// Capability-gated managed creation over the authenticated Rain Archives client boundary.
/// Rain Explorer never enumerates selected folders or opens the destination archive.
/// </summary>
public sealed class ArchiveCreationService : IAsyncDisposable
{
    private readonly ArchiveClientRuntime _runtime;
    private readonly IArchiveActivitySink _activitySink;
    private readonly Func<Task>? _beforeStart;
    private readonly object _sync = new();
    private ActiveCreationOperation? _active;
    private bool _disposed;

    public ArchiveCreationService()
        : this(ArchiveClientRuntime.Shared, ArchiveActivitySink.Instance)
    {
    }

    internal ArchiveCreationService(
        ArchiveClientRuntime runtime,
        IArchiveActivitySink activitySink,
        Func<Task>? beforeStart = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _activitySink = activitySink ?? throw new ArgumentNullException(nameof(activitySink));
        _beforeStart = beforeStart;
    }

    public async Task<ArchiveCreationCapabilitySnapshot> LoadCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return ArchiveCreationCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Offline);
        IArchiveClient? client = null;
        try
        {
            client = await _runtime.GetClientAsync(cancellationToken).ConfigureAwait(false);
            return await LoadCapabilitiesAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArchiveClientException exception)
        {
            if (exception.Error.Category is ArchiveErrorCategory.WorkerFailure
                or ArchiveErrorCategory.ProtocolMismatch)
            {
                if (client is not null) await InvalidateClientAsync(client).ConfigureAwait(false);
                return ArchiveCreationCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Offline);
            }
            return ArchiveCreationCapabilitySnapshot.Fixed(
                exception.Error.Category is ArchiveErrorCategory.UnsupportedCapability
                    or ArchiveErrorCategory.UnsupportedFormat
                    ? ArchiveWriteCapabilityState.Unavailable
                    : ArchiveWriteCapabilityState.Error);
        }
        catch (Exception exception) when (exception is IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
            if (client is not null) await InvalidateClientAsync(client).ConfigureAwait(false);
            return ArchiveCreationCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Offline);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or UnauthorizedAccessException)
        {
            return ArchiveCreationCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Unavailable);
        }
    }

    public async Task<ArchiveOperationResult> CreateAsync(
        ArchiveCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActiveCreationOperation operation;
        lock (_sync)
        {
            if (_disposed)
                return new ArchiveOperationResult(ArchiveOperationOutcome.Offline);
            if (_active is not null)
                return new ArchiveOperationResult(ArchiveOperationOutcome.Busy);
            operation = new ActiveCreationOperation(
                _activitySink.Begin(ActivityOperationKind.ArchiveCreation));
            _active = operation;
        }

        try
        {
            operation.Activity.AttachCancel(() => RequestCancellation(operation));
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state =>
                {
                    var tuple = ((ArchiveCreationService Owner, ActiveCreationOperation Operation))state!;
                    tuple.Owner.RequestCancellation(tuple.Operation);
                },
                (this, operation));
            return await ExecuteAsync(operation, request).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, operation)) _active = null;
            }
            operation.AdmissionCancellation.Dispose();
            operation.TerminalWaitCancellation.Dispose();
            operation.Drained.TrySetResult();
        }
    }

    private async Task<ArchiveOperationResult> ExecuteAsync(
        ActiveCreationOperation operation,
        ArchiveCreationRequest request)
    {
        operation.Activity.Report(operation.CreateUpdate(ActivityOperationPhase.Validating));
        if (!ArchiveWriteValidation.TryValidateCreationShape(request, option: null))
            return FinishRefusal(operation, ArchiveOperationOutcome.InvalidRequest,
                ActivityOperationFailure.InvalidRequest);
        if (!ArchiveWriteValidation.TryValidateOutputDestination(
                request.DestinationPath,
                ArchiveWriteValidation.ExtensionFor(request.Format),
                request.Sources,
                out string canonicalDestination,
                out bool initialDestinationExists))
        {
            return FinishRefusal(operation,
                initialDestinationExists
                    ? ArchiveOperationOutcome.DestinationConflict
                    : ArchiveOperationOutcome.InvalidDestination,
                initialDestinationExists
                    ? ActivityOperationFailure.DestinationConflict
                    : ActivityOperationFailure.InvalidRequest);
        }
        if (!ArchiveWriteValidation.TryValidateSourceRoots(request.Sources))
            return FinishRefusal(operation, ArchiveOperationOutcome.Stale,
                ActivityOperationFailure.SourceChanged);

        IArchiveClient? client = null;
        EventHandler<OperationEvent>? handler = null;
        try
        {
            client = await _runtime.GetClientAsync(operation.AdmissionCancellation.Token)
                .ConfigureAwait(false);
            operation.SetClient(client);
            ArchiveCreationCapabilitySnapshot capabilities = await LoadCapabilitiesAsync(
                    client, operation.AdmissionCancellation.Token)
                .ConfigureAwait(false);
            ArchiveCreationFormatOption? option = capabilities.Formats.SingleOrDefault(
                item => item.Format == request.Format);
            if (!capabilities.IsReady || option is null
                || !ArchiveWriteValidation.TryValidateCreationShape(request, option))
            {
                return FinishRefusal(operation, ArchiveOperationOutcome.Unsupported,
                    ActivityOperationFailure.Unsupported);
            }

            if (operation.IsCancellationRequested) return FinishCancellation(operation);
            operation.SetLimits(option, request.Sources.Any(
                source => source.Kind == ArchiveCreationSourceKind.Directory));
            handler = (_, operationEvent) => ReceiveEvent(operation, operationEvent);
            client.OperationEventReceived += handler;

            // This is the last Explorer-side destination observation. The Worker owns the
            // no-replace open and therefore wins any race after this point.
            if (!ArchiveWriteValidation.TryValidateOutputDestination(
                    canonicalDestination,
                    option.Extension,
                    request.Sources,
                    out canonicalDestination,
                    out bool destinationExists))
            {
                return FinishRefusal(operation,
                    destinationExists
                        ? ArchiveOperationOutcome.DestinationConflict
                        : ArchiveOperationOutcome.InvalidDestination,
                    destinationExists
                        ? ActivityOperationFailure.DestinationConflict
                        : ActivityOperationFailure.InvalidRequest);
            }

            if (_beforeStart is not null) await _beforeStart().ConfigureAwait(false);
            if (operation.IsCancellationRequested) return FinishCancellation(operation);
            if (!ArchiveWriteValidation.TryValidateSourceRoots(request.Sources))
                return FinishRefusal(operation, ArchiveOperationOutcome.Stale,
                    ActivityOperationFailure.SourceChanged);

            StartOperationResponse started = await StartAsync(
                    client,
                    request with { DestinationPath = canonicalDestination },
                    operation.TerminalWaitCancellation.Token)
                .ConfigureAwait(false);
            operation.SetOperationId(started.OperationId, RequestCancellation);
            operation.Activity.Report(operation.CreateUpdate(ActivityOperationPhase.Queued));

            OperationEvent terminal = await client.WaitForTerminalAsync(
                    started.OperationId,
                    operation.TerminalWaitCancellation.Token)
                .ConfigureAwait(false);
            if (terminal.OperationId != started.OperationId
                || terminal.Kind != OperationEventKind.Terminal
                || terminal.Phase is not (OperationPhase.Completed
                    or OperationPhase.CompletedWithWarnings
                    or OperationPhase.Cancelled
                    or OperationPhase.Failed))
            {
                operation.ReportTerminal(
                    ActivityOperationPhase.Failed,
                    ActivityOperationFailure.ProtocolMismatch);
                await InvalidateClientAsync(client).ConfigureAwait(false);
                return new ArchiveOperationResult(ArchiveOperationOutcome.Offline);
            }

            await ApplyFinalSnapshotAsync(client, operation, started.OperationId)
                .ConfigureAwait(false);
            return FinishTerminal(operation, terminal);
        }
        catch (OperationCanceledException)
        {
            return FinishCancellation(operation);
        }
        catch (ArchiveClientException exception)
        {
            ArchiveOperationResult result = FinishFailure(operation, exception.Error);
            if (exception.Error.Category is (ArchiveErrorCategory.WorkerFailure
                or ArchiveErrorCategory.ProtocolMismatch)
                && client is not null)
                await InvalidateClientAsync(client).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
            operation.ReportTerminal(
                ActivityOperationPhase.Failed,
                ActivityOperationFailure.WorkerUnavailable);
            if (client is not null) await InvalidateClientAsync(client).ConfigureAwait(false);
            return new ArchiveOperationResult(ArchiveOperationOutcome.Offline);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or UnauthorizedAccessException)
        {
            operation.ReportTerminal(
                ActivityOperationPhase.Failed,
                exception is NotSupportedException
                    ? ActivityOperationFailure.Unsupported
                    : ActivityOperationFailure.InvalidRequest);
            return new ArchiveOperationResult(exception is NotSupportedException
                ? ArchiveOperationOutcome.Unsupported
                : ArchiveOperationOutcome.InvalidRequest);
        }
        finally
        {
            if (handler is not null && client is not null)
                client.OperationEventReceived -= handler;
        }
    }

    private static async Task<ArchiveCreationCapabilitySnapshot> LoadCapabilitiesAsync(
        IArchiveClient client,
        CancellationToken cancellationToken)
    {
        if (!SupportsOperationProtocol(client))
            return ArchiveCreationCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Unavailable);

        var formats = new List<ArchiveCreationFormatOption>(7);
        RarEncoderAvailability? rarAvailability = null;
        if (client.SupportsFeature(ProtocolFeatures.ZipCreation)
            && client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities))
        {
            GetZipWriteCapabilitiesResponse zip = await client.GetZipWriteCapabilitiesAsync(
                    sessionId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (zip.CanCreate && zip.SupportsAdd && IsSafe(zip))
            {
                formats.Add(new ArchiveCreationFormatOption(
                    ArchiveWriteFormat.Zip,
                    "ZIP",
                    ".zip",
                    client.SupportsFeature(ProtocolFeatures.ZipDirectoryCreation),
                    zip.CompressionPresets.ToArray(),
                    zip.MaximumEntries,
                    zip.MaximumSourceBytes));
            }
        }

        if (client.SupportsFeature(ProtocolFeatures.TarCreation)
            && client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities))
        {
            GetTarWriteCapabilitiesResponse tar = await client
                .GetTarWriteCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (tar.CanCreate && IsSafe(tar))
            {
                formats.Add(new ArchiveCreationFormatOption(
                    ArchiveWriteFormat.Tar,
                    "TAR",
                    ".tar",
                    tar.SupportsTreeCreation
                        && client.SupportsFeature(ProtocolFeatures.TarDirectoryCreation),
                    tar.CompressionPresets.ToArray(),
                    tar.MaximumEntries,
                    tar.MaximumSourceBytes));
            }
        }

        if (client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities))
        {
            GetManagedWriteCapabilitiesResponse managed = await client
                .GetManagedWriteCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            AddManagedFormats(client, managed, formats);
        }

        if (client.SupportsFeature(ProtocolFeatures.RarCreation)
            && client.SupportsFeature(ProtocolFeatures.RarWriteCapabilities))
        {
            GetRarWriteCapabilitiesResponse rar = await client
                .GetRarWriteCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            rarAvailability = rar.Availability;
            if (rar.CanCreate
                && IsSafe(rar)
                && client.SupportsFeature(ProtocolFeatures.RarDirectoryCreation))
            {
                formats.Add(new ArchiveCreationFormatOption(
                    ArchiveWriteFormat.Rar,
                    "RAR",
                    ".rar",
                    rar.SupportsTreeCreation,
                    rar.CompressionPresets.ToArray(),
                    rar.MaximumEntries,
                    rar.MaximumSourceBytes));
            }
        }

        // The additive feature expands folder enumeration, not wire/source-root counts or rebuilds.
        int treeLimit = client.SupportsFeature(ProtocolFeatures.ExpandedCreationTreeBudget)
            && client.MaximumCreationTreeEntries == ProtocolConstants.MaximumExpandedCreationTreeEntries
            ? ProtocolConstants.MaximumExpandedCreationTreeEntries
            : ProtocolConstants.MaximumZipCreationEntries;
        formats = formats.Select(option => option with
        {
            MaximumTreeEntries = option.SupportsDirectories ? treeLimit : option.MaximumEntries,
        }).ToList();

        return formats.Count == 0
            ? new ArchiveCreationCapabilitySnapshot(
                ArchiveWriteCapabilityState.Unavailable,
                Array.Empty<ArchiveCreationFormatOption>(),
                rarAvailability)
            : new ArchiveCreationCapabilitySnapshot(
                ArchiveWriteCapabilityState.Ready,
                formats,
                rarAvailability);
    }

    private static void AddManagedFormats(
        IArchiveClient client,
        GetManagedWriteCapabilitiesResponse response,
        ICollection<ArchiveCreationFormatOption> formats)
    {
        foreach (ManagedWriteCapability capability in response.Capabilities)
        {
            if (!capability.CanCreate || !IsSafe(capability)) continue;
            (ArchiveWriteFormat format, string name, string extension, string feature, string? treeFeature) =
                capability.Format switch
                {
                    ManagedArchiveCreationFormat.TarGZip => (
                        ArchiveWriteFormat.TarGZip,
                        "TAR.GZ",
                        ".tar.gz",
                        ProtocolFeatures.TarGZipCreation,
                        ProtocolFeatures.TarGZipDirectoryCreation),
                    ManagedArchiveCreationFormat.TarBZip2 => (
                        ArchiveWriteFormat.TarBZip2,
                        "TAR.BZ2",
                        ".tar.bz2",
                        ProtocolFeatures.TarBZip2Creation,
                        ProtocolFeatures.TarBZip2DirectoryCreation),
                    ManagedArchiveCreationFormat.GZip => (
                        ArchiveWriteFormat.GZip,
                        "GZIP",
                        ".gz",
                        ProtocolFeatures.GZipCreation,
                        null),
                    ManagedArchiveCreationFormat.BZip2 => (
                        ArchiveWriteFormat.BZip2,
                        "BZIP2",
                        ".bz2",
                        ProtocolFeatures.BZip2Creation,
                        null),
                    _ => throw new InvalidDataException("The Worker returned an unknown managed format."),
                };
            if (!client.SupportsFeature(feature)) continue;
            bool supportsDirectories = capability.SupportsTreeCreation
                && treeFeature is not null
                && client.SupportsFeature(treeFeature);
            formats.Add(new ArchiveCreationFormatOption(
                format,
                name,
                extension,
                supportsDirectories,
                capability.CompressionPresets.ToArray(),
                capability.MaximumEntries,
                capability.MaximumSourceBytes));
        }
    }

    private static Task<StartOperationResponse> StartAsync(
        IArchiveClient client,
        ArchiveCreationRequest request,
        CancellationToken cancellationToken)
    {
        ArchiveCreationFile[] files = request.Sources
            .Where(source => source.Kind == ArchiveCreationSourceKind.File)
            .Select(source => new ArchiveCreationFile(source.SourcePath, source.ArchivePath))
            .ToArray();
        ArchiveCreationDirectory[] directories = request.Sources
            .Where(source => source.Kind == ArchiveCreationSourceKind.Directory)
            .Select(source => new ArchiveCreationDirectory(source.SourcePath, source.ArchivePath))
            .ToArray();

        return request.Format switch
        {
            ArchiveWriteFormat.Zip when directories.Length == 0 =>
                client.StartZipCreationAsync(
                    request.DestinationPath, files, request.CompressionPreset, cancellationToken),
            ArchiveWriteFormat.Zip => client.StartZipTreeCreationAsync(
                request.DestinationPath, files, directories, request.CompressionPreset, cancellationToken),
            ArchiveWriteFormat.Tar when directories.Length == 0 =>
                client.StartTarCreationAsync(request.DestinationPath, files, cancellationToken),
            ArchiveWriteFormat.Tar =>
                client.StartTarTreeCreationAsync(request.DestinationPath, files, directories, cancellationToken),
            ArchiveWriteFormat.Rar when directories.Length == 0 =>
                client.StartRarCreationAsync(
                    request.DestinationPath, files, request.CompressionPreset, cancellationToken),
            ArchiveWriteFormat.Rar => client.StartRarTreeCreationAsync(
                request.DestinationPath, files, directories, request.CompressionPreset, cancellationToken),
            ArchiveWriteFormat.TarGZip when directories.Length == 0 =>
                client.StartTarGZipCreationAsync(
                    request.DestinationPath, files, request.CompressionPreset, cancellationToken),
            ArchiveWriteFormat.TarGZip => client.StartTarGZipTreeCreationAsync(
                request.DestinationPath, files, directories, request.CompressionPreset, cancellationToken),
            ArchiveWriteFormat.TarBZip2 when directories.Length == 0 =>
                client.StartTarBZip2CreationAsync(
                    request.DestinationPath, files, request.CompressionPreset, cancellationToken),
            ArchiveWriteFormat.TarBZip2 => client.StartTarBZip2TreeCreationAsync(
                request.DestinationPath, files, directories, request.CompressionPreset, cancellationToken),
            ArchiveWriteFormat.GZip => client.StartGZipCreationAsync(
                request.DestinationPath,
                files.Single().SourcePath,
                request.CompressionPreset,
                cancellationToken),
            ArchiveWriteFormat.BZip2 => client.StartBZip2CreationAsync(
                request.DestinationPath,
                files.Single().SourcePath,
                request.CompressionPreset,
                cancellationToken),
            _ => throw new NotSupportedException("The selected managed creation format is unavailable."),
        };
    }

    private static bool SupportsOperationProtocol(IArchiveClient client) =>
        client.SupportsFeature(ProtocolFeatures.OperationEvents)
        && client.SupportsFeature(ProtocolFeatures.RequestCancellation)
        && client.SupportsFeature(ProtocolFeatures.OperationActivity);

    private static bool IsSafe(GetZipWriteCapabilitiesResponse capability) =>
        capability.MaximumEntries is > 0 and <= ProtocolConstants.MaximumZipCreationEntries
        && capability.MaximumSourceBytes is > 0 and <= ProtocolConstants.MaximumZipCreationSourceBytes
        && !capability.SupportsEncryption
        && !capability.SupportsFilenameEncryption
        && !capability.SupportsSplitOutput
        && !capability.SupportsExistingDestinationReplacement
        && !capability.SupportsInPlaceUpdate;

    private static bool IsSafe(GetTarWriteCapabilitiesResponse capability) =>
        capability.MaximumEntries is > 0 and <= ProtocolConstants.MaximumTarCreationEntries
        && capability.MaximumSourceBytes is > 0 and <= ProtocolConstants.MaximumTarCreationSourceBytes
        && !capability.SupportsEncryption
        && !capability.SupportsFilenameEncryption
        && !capability.SupportsSplitOutput
        && !capability.SupportsExistingDestinationReplacement
        && !capability.SupportsInPlaceUpdate;

    private static bool IsSafe(GetRarWriteCapabilitiesResponse capability) =>
        capability.Availability == RarEncoderAvailability.Available
        && capability.MaximumEntries is > 0 and <= ProtocolConstants.MaximumRarCreationEntries
        && capability.MaximumSourceBytes is > 0 and <= ProtocolConstants.MaximumRarCreationSourceBytes
        && !capability.CanRebuild
        && capability.SupportsTreeCreation
        && !capability.SupportsEncryption
        && !capability.SupportsFilenameEncryption
        && !capability.SupportsSplitOutput
        && !capability.SupportsExistingDestinationReplacement
        && !capability.SupportsInPlaceUpdate;

    private static bool IsSafe(ManagedWriteCapability capability) =>
        capability.MaximumEntries is > 0 and <= ProtocolConstants.MaximumTarCreationEntries
        && capability.MaximumSourceBytes is > 0 and <= ProtocolConstants.MaximumTarCreationSourceBytes
        && !capability.SupportsEncryption
        && !capability.SupportsFilenameEncryption
        && !capability.SupportsSplitOutput
        && !capability.SupportsExistingDestinationReplacement
        && !capability.SupportsInPlaceUpdate;

    private void ReceiveEvent(
        ActiveCreationOperation operation,
        OperationEvent operationEvent)
    {
        if (!operation.TryAcceptEvent(operationEvent, out ActivityOperationUpdate? update)) return;
        if (update is not null) operation.Activity.Report(update);
        if (IsUnexpectedInteractiveEvent(operationEvent))
        {
            operation.MarkUnexpectedWaiting();
            RequestCancellation(operation);
        }
    }

    private static bool IsUnexpectedInteractiveEvent(OperationEvent operationEvent) =>
        operationEvent.Kind != OperationEventKind.Terminal
        && operationEvent.Phase is OperationPhase.AwaitingPassword
            or OperationPhase.AwaitingConflict
            or OperationPhase.AwaitingRiskApproval;

    private void RequestCancellation(ActiveCreationOperation operation)
    {
        if (!operation.MarkCancellationPending()) return;
        if (!operation.TryBeginCancel(out IArchiveClient? client, out OperationId operationId)) return;
        _ = SendCancellationAsync(client, operationId);
    }

    private async Task SendCancellationAsync(IArchiveClient client, OperationId operationId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _ = await client.CancelOperationAsync(operationId, timeout.Token).ConfigureAwait(false);
        }
        catch (ArchiveClientException exception) when (exception.Error.Category is
            ArchiveErrorCategory.WorkerFailure or ArchiveErrorCategory.ProtocolMismatch)
        {
            await InvalidateClientAsync(client).ConfigureAwait(false);
        }
        catch (ArchiveClientException)
        {
            // A nonfatal cancellation race belongs to this operation. Its terminal event
            // remains authoritative and unrelated sessions keep the shared client.
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                              or IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
            await InvalidateClientAsync(client).ConfigureAwait(false);
        }
    }

    private async Task InvalidateClientAsync(IArchiveClient client)
    {
        try { await _runtime.InvalidateAsync(client).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ArchiveClientException
                                              or OperationCanceledException
                                              or IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
        }
    }

    private static async Task ApplyFinalSnapshotAsync(
        IArchiveClient client,
        ActiveCreationOperation operation,
        OperationId operationId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            GetOperationActivityResponse response = await client
                .GetOperationActivityAsync(timeout.Token)
                .ConfigureAwait(false);
            OperationActivitySnapshot? snapshot = response.Operations.SingleOrDefault(
                item => item.OperationId == operationId
                    && item.Kind == OperationKind.CreateArchive);
            if (snapshot is not null) operation.ApplySnapshot(snapshot);
        }
        catch (Exception exception) when (exception is ArchiveClientException
                                              or OperationCanceledException
                                              or IOException
                                              or InvalidDataException
                                              or NotSupportedException
                                              or ObjectDisposedException)
        {
            // The exact terminal event remains authoritative and is never retried.
        }
    }

    private static ArchiveOperationResult FinishRefusal(
        ActiveCreationOperation operation,
        ArchiveOperationOutcome outcome,
        ActivityOperationFailure failure)
    {
        operation.ReportTerminal(ActivityOperationPhase.Failed, failure);
        return new ArchiveOperationResult(outcome);
    }

    private static ArchiveOperationResult FinishCancellation(ActiveCreationOperation operation)
    {
        if (operation.UnexpectedWaiting)
        {
            operation.ReportTerminal(
                ActivityOperationPhase.Failed,
                ActivityOperationFailure.Unsupported);
            return new ArchiveOperationResult(ArchiveOperationOutcome.Unsupported);
        }
        operation.ReportTerminal(ActivityOperationPhase.Cancelled, ActivityOperationFailure.None);
        return new ArchiveOperationResult(ArchiveOperationOutcome.Cancelled);
    }

    private static ArchiveOperationResult FinishTerminal(
        ActiveCreationOperation operation,
        OperationEvent terminal)
    {
        if (terminal.Progress is { } progress) operation.ApplyProgress(progress);
        if (operation.UnexpectedWaiting)
        {
            operation.ReportTerminal(
                ActivityOperationPhase.Failed,
                ActivityOperationFailure.Unsupported);
            return new ArchiveOperationResult(ArchiveOperationOutcome.Unsupported);
        }
        return terminal.Phase switch
        {
            OperationPhase.Completed => FinishSuccess(
                operation, ActivityOperationPhase.Completed, ArchiveOperationOutcome.Completed),
            OperationPhase.CompletedWithWarnings => FinishSuccess(
                operation.EnsureWarningCount(),
                ActivityOperationPhase.CompletedWithWarnings,
                ArchiveOperationOutcome.CompletedWithWarnings),
            OperationPhase.Cancelled => FinishCancellation(operation),
            _ => FinishFailure(operation, terminal.Error),
        };
    }

    private static ArchiveOperationResult FinishSuccess(
        ActiveCreationOperation operation,
        ActivityOperationPhase phase,
        ArchiveOperationOutcome outcome)
    {
        operation.ReportTerminal(phase, ActivityOperationFailure.None);
        return new ArchiveOperationResult(outcome);
    }

    private static ArchiveOperationResult FinishFailure(
        ActiveCreationOperation operation,
        ArchiveError? error)
    {
        ArchiveErrorCategory? category = error?.Category;
        ActivityOperationFailure failure = category == ArchiveErrorCategory.ResourceLimit
            ? error?.Code switch
            {
                "creation.entry_count_limit" => ActivityOperationFailure.CreationEntryLimit,
                "creation.source_byte_limit" => ActivityOperationFailure.CreationSourceSizeLimit,
                "creation.metadata_budget" => ActivityOperationFailure.CreationMetadataLimit,
                "creation.source_path_limit" => ActivityOperationFailure.CreationPathLimit,
                "creation.output_limit" or "creation.output_overflow" => ActivityOperationFailure.CreationOutputLimit,
                _ => ActivityOperationFailure.ResourceLimit,
            }
            : MapFailure(error);
        long? limit = operation.GetFailureLimit(failure);
        operation.ReportTerminal(ActivityOperationPhase.Failed, failure, limit);
        return new ArchiveOperationResult(category switch
        {
            ArchiveErrorCategory.Cancelled => ArchiveOperationOutcome.Cancelled,
            ArchiveErrorCategory.SourceChanged => ArchiveOperationOutcome.Stale,
            ArchiveErrorCategory.WorkerFailure or ArchiveErrorCategory.ProtocolMismatch =>
                ArchiveOperationOutcome.Offline,
            ArchiveErrorCategory.UnsupportedCapability or ArchiveErrorCategory.UnsupportedFormat =>
                ArchiveOperationOutcome.Unsupported,
            ArchiveErrorCategory.DestinationConflict => ArchiveOperationOutcome.DestinationConflict,
            ArchiveErrorCategory.InvalidRequest => ArchiveOperationOutcome.InvalidRequest,
            _ => ArchiveOperationOutcome.Failed,
        }, failure, limit);
    }

    private static ActivityOperationFailure MapFailure(ArchiveError? error)
    {
        if (error is { Category: ArchiveErrorCategory.ProviderFailure } providerFailure)
        {
            return providerFailure.Code switch
            {
                "rar.workspace_path_too_long" => ActivityOperationFailure.CreationRarWorkspacePathTooLong,
                "rar.workspace_io_failure" => ActivityOperationFailure.CreationRarWorkspaceIoFailure,
                _ => MapFailure(providerFailure.Category),
            };
        }

        return MapFailure(error?.Category);
    }

    private static ActivityOperationFailure MapFailure(ArchiveErrorCategory? category) => category switch
    {
        ArchiveErrorCategory.InvalidRequest => ActivityOperationFailure.InvalidRequest,
        ArchiveErrorCategory.UnsupportedFormat or ArchiveErrorCategory.UnsupportedCapability =>
            ActivityOperationFailure.Unsupported,
        ArchiveErrorCategory.InvalidArchive => ActivityOperationFailure.InvalidArchive,
        ArchiveErrorCategory.CorruptArchive => ActivityOperationFailure.CorruptArchive,
        ArchiveErrorCategory.UnsafePath => ActivityOperationFailure.UnsafePath,
        ArchiveErrorCategory.PathCollision => ActivityOperationFailure.PathCollision,
        ArchiveErrorCategory.ResourceLimit => ActivityOperationFailure.ResourceLimit,
        ArchiveErrorCategory.SourceChanged => ActivityOperationFailure.SourceChanged,
        ArchiveErrorCategory.DestinationConflict => ActivityOperationFailure.DestinationConflict,
        ArchiveErrorCategory.PermissionDenied => ActivityOperationFailure.PermissionDenied,
        ArchiveErrorCategory.InsufficientSpace => ActivityOperationFailure.InsufficientSpace,
        ArchiveErrorCategory.ProviderFailure => ActivityOperationFailure.ProviderFailure,
        ArchiveErrorCategory.WorkerFailure => ActivityOperationFailure.WorkerUnavailable,
        ArchiveErrorCategory.ProtocolMismatch => ActivityOperationFailure.ProtocolMismatch,
        _ => ActivityOperationFailure.Unknown,
    };

    private static ActivityOperationPhase MapPhase(OperationPhase phase) => phase switch
    {
        OperationPhase.Queued => ActivityOperationPhase.Queued,
        OperationPhase.Validating => ActivityOperationPhase.Validating,
        OperationPhase.AwaitingPassword or OperationPhase.AwaitingConflict
            or OperationPhase.AwaitingRiskApproval => ActivityOperationPhase.WaitingForInput,
        OperationPhase.Running => ActivityOperationPhase.Running,
        OperationPhase.Finalizing => ActivityOperationPhase.Finalizing,
        OperationPhase.Completed => ActivityOperationPhase.Completed,
        OperationPhase.CompletedWithWarnings => ActivityOperationPhase.CompletedWithWarnings,
        OperationPhase.Cancelled => ActivityOperationPhase.Cancelled,
        _ => ActivityOperationPhase.Failed,
    };

    public async ValueTask DisposeAsync()
    {
        ActiveCreationOperation? operation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            operation = _active;
        }
        if (operation is null) return;
        RequestCancellation(operation);
        Task drained = operation.Drained.Task;
        if (await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false)
            != drained
            && operation.Client is { } client)
        {
            await InvalidateClientAsync(client).ConfigureAwait(false);
            operation.EndTerminalWaitAfterClientTeardown();
        }
        await drained.ConfigureAwait(false);
    }

    private sealed class ActiveCreationOperation(IArchiveActivityHandle activity)
    {
        private readonly object _sync = new();
        private readonly List<OperationEvent> _pendingEvents = [];
        private IArchiveClient? _client;
        private OperationId? _operationId;
        private long _lastSequence;
        private long _bytesProcessed;
        private long? _totalBytes;
        private int _entriesProcessed;
        private int? _totalEntries;
        private TimeSpan? _elapsed;
        private double? _bytesPerSecond;
        private double? _compressionRatio;
        private string? _currentItem;
        private int _warningCount;
        private int _cancelStarted;
        private int _terminalReported;
        private int _unexpectedWaiting;
        private int _maximumEntries;
        private long _maximumSourceBytes;

        public IArchiveActivityHandle Activity { get; } = activity;
        public CancellationTokenSource AdmissionCancellation { get; } = new();
        public CancellationTokenSource TerminalWaitCancellation { get; } = new();
        public TaskCompletionSource Drained { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCancellationRequested => AdmissionCancellation.IsCancellationRequested;
        public bool UnexpectedWaiting => Volatile.Read(ref _unexpectedWaiting) != 0;
        public IArchiveClient? Client { get { lock (_sync) return _client; } }

        public void SetClient(IArchiveClient client)
        {
            lock (_sync) _client = client;
        }

        public void SetLimits(ArchiveCreationFormatOption option, bool hasDirectories)
        {
            lock (_sync)
            {
                _maximumEntries = hasDirectories ? option.MaximumTreeEntries : option.MaximumEntries;
                _maximumSourceBytes = option.MaximumSourceBytes;
            }
        }

        public long? GetFailureLimit(ActivityOperationFailure failure)
        {
            lock (_sync) return failure switch
            {
                ActivityOperationFailure.CreationEntryLimit => _maximumEntries,
                ActivityOperationFailure.CreationSourceSizeLimit => _maximumSourceBytes,
                _ => null,
            };
        }

        public void SetOperationId(
            OperationId operationId,
            Action<ActiveCreationOperation> requestCancellation)
        {
            List<OperationEvent> pending;
            bool cancelled;
            lock (_sync)
            {
                _operationId = operationId;
                pending = _pendingEvents
                    .Where(item => item.OperationId == operationId)
                    .OrderBy(item => item.Sequence)
                    .ToList();
                _pendingEvents.Clear();
                cancelled = AdmissionCancellation.IsCancellationRequested;
            }
            foreach (OperationEvent operationEvent in pending)
            {
                if (!TryAcceptEvent(operationEvent, out ActivityOperationUpdate? update)) continue;
                if (update is not null) Activity.Report(update);
                if (IsUnexpectedInteractiveEvent(operationEvent))
                {
                    MarkUnexpectedWaiting();
                    requestCancellation(this);
                }
            }
            if (cancelled) requestCancellation(this);
        }

        public bool TryAcceptEvent(
            OperationEvent operationEvent,
            out ActivityOperationUpdate? update)
        {
            lock (_sync)
            {
                update = null;
                if (Volatile.Read(ref _terminalReported) != 0) return false;
                if (_operationId is null)
                {
                    if (_pendingEvents.Count < ProtocolConstants.MaximumPendingRequests)
                        _pendingEvents.Add(operationEvent);
                    return false;
                }
                if (operationEvent.OperationId != _operationId
                    || operationEvent.Sequence <= _lastSequence)
                    return false;
                _lastSequence = operationEvent.Sequence;
                if (operationEvent.Kind == OperationEventKind.Warning) _warningCount++;
                if (operationEvent.Progress is { } progress) ApplyProgressUnsafe(progress);
                update = operationEvent.Kind == OperationEventKind.Terminal
                    ? null
                    : CreateUpdateUnsafe(MapPhase(operationEvent.Phase));
                return true;
            }
        }

        public void ApplyProgress(OperationProgress progress)
        {
            lock (_sync) ApplyProgressUnsafe(progress);
        }

        public void ApplySnapshot(OperationActivitySnapshot snapshot)
        {
            lock (_sync)
            {
                if (_operationId != snapshot.OperationId
                    || snapshot.Kind != OperationKind.CreateArchive)
                    return;
                _lastSequence = Math.Max(_lastSequence, snapshot.LastEventSequence);
                _warningCount = Math.Max(_warningCount, snapshot.WarningCount);
                if (snapshot.Progress is not { } progress) return;
                _bytesProcessed = progress.BytesProcessed;
                _totalBytes = progress.TotalBytes;
                _entriesProcessed = progress.EntriesProcessed;
                _totalEntries = progress.TotalEntries;
                _elapsed = progress.Elapsed;
                _bytesPerSecond = progress.BytesPerSecond;
                _compressionRatio = progress.CompressionRatio;
            }
        }

        public ActiveCreationOperation EnsureWarningCount()
        {
            lock (_sync) _warningCount = Math.Max(1, _warningCount);
            return this;
        }

        public void MarkUnexpectedWaiting() =>
            Interlocked.Exchange(ref _unexpectedWaiting, 1);

        public ActivityOperationUpdate CreateUpdate(ActivityOperationPhase phase)
        {
            lock (_sync) return CreateUpdateUnsafe(phase);
        }

        public void ReportTerminal(
            ActivityOperationPhase phase,
            ActivityOperationFailure failure,
            long? failureLimit = null)
        {
            if (Interlocked.Exchange(ref _terminalReported, 1) != 0) return;
            ActivityOperationUpdate update;
            lock (_sync)
            {
                _currentItem = null;
                update = CreateUpdateUnsafe(phase, failure, failureLimit);
            }
            Activity.Report(update);
        }

        public bool MarkCancellationPending()
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _terminalReported) != 0) return false;
                try { AdmissionCancellation.Cancel(); }
                catch (ObjectDisposedException) { return false; }
            }
            Activity.MarkCancellationPending();
            return true;
        }

        public bool TryBeginCancel(out IArchiveClient client, out OperationId operationId)
        {
            lock (_sync)
            {
                if (_client is null || _operationId is null
                    || Interlocked.Exchange(ref _cancelStarted, 1) != 0)
                {
                    client = null!;
                    operationId = default;
                    return false;
                }
                client = _client;
                operationId = _operationId.Value;
                return true;
            }
        }

        public void EndTerminalWaitAfterClientTeardown()
        {
            try { TerminalWaitCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void ApplyProgressUnsafe(OperationProgress progress)
        {
            _bytesProcessed = progress.BytesProcessed;
            _totalBytes = progress.TotalBytes;
            _entriesProcessed = progress.EntriesProcessed;
            _totalEntries = progress.TotalEntries;
            _elapsed = progress.Elapsed;
            _bytesPerSecond = progress.BytesPerSecond;
            _compressionRatio = progress.CompressionRatio;
            _currentItem = string.IsNullOrWhiteSpace(progress.CurrentEntryName)
                ? null
                : BoundCurrentItem(UntrustedDisplayText.Escape(progress.CurrentEntryName));
        }

        private ActivityOperationUpdate CreateUpdateUnsafe(
            ActivityOperationPhase phase,
            ActivityOperationFailure failure = ActivityOperationFailure.None,
            long? failureLimit = null) =>
            new(
                ActivityOperationKind.ArchiveCreation,
                phase,
                _bytesProcessed,
                _totalBytes,
                _entriesProcessed,
                _totalEntries,
                _elapsed,
                _bytesPerSecond,
                _compressionRatio,
                _currentItem,
                _warningCount,
                failure,
                failureLimit);

        private static string BoundCurrentItem(string value)
        {
            int maximum = ActivityOperationUpdate.MaximumCurrentItemCharacters;
            if (value.Length <= maximum) return value;
            int length = maximum - 1;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])
                && char.IsLowSurrogate(value[length]))
                length--;
            return value[..length] + "…";
        }
    }
}

internal static class ArchiveWriteValidation
{
    public static bool TryBuildSources(
        IEnumerable<(string SourcePath, bool IsDirectory)> selected,
        out IReadOnlyList<ArchiveCreationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var result = new List<ArchiveCreationSource>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archiveRoots = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach ((string sourcePath, bool isDirectory) in selected)
            {
                if (string.IsNullOrWhiteSpace(sourcePath)
                    || sourcePath.Length > ProtocolConstants.MaximumPathCharacters
                    || !Path.IsPathFullyQualified(sourcePath))
                {
                    sources = Array.Empty<ArchiveCreationSource>();
                    return false;
                }
                string canonical = isDirectory
                    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath))
                    : Path.GetFullPath(sourcePath);
                string archivePath = Path.GetFileName(canonical);
                if (string.IsNullOrWhiteSpace(archivePath)
                    || !ArchivePathRules.TryNormalize(
                        archivePath,
                        out string normalizedRoot,
                        out string collisionKey,
                        out _)
                    || normalizedRoot.IndexOfAny(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                    || !paths.Add(canonical)
                    || !archiveRoots.Add(collisionKey))
                {
                    sources = Array.Empty<ArchiveCreationSource>();
                    return false;
                }
                result.Add(new ArchiveCreationSource(
                    isDirectory ? ArchiveCreationSourceKind.Directory : ArchiveCreationSourceKind.File,
                    canonical,
                    normalizedRoot));
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            sources = Array.Empty<ArchiveCreationSource>();
            return false;
        }
        sources = result;
        return result.Count > 0;
    }

    public static bool TryValidateCreationRequest(
        ArchiveCreationRequest request,
        ArchiveCreationFormatOption? option,
        out string canonicalDestination)
    {
        if (!TryValidateCreationShape(request, option))
        {
            canonicalDestination = string.Empty;
            return false;
        }

        string extension = option?.Extension ?? ExtensionFor(request.Format);
        return TryValidateOutputDestination(
            request.DestinationPath,
            extension,
            request.Sources,
            out canonicalDestination,
            out _);
    }

    public static bool TryValidateCreationShape(
        ArchiveCreationRequest request,
        ArchiveCreationFormatOption? option)
    {
        if (request.Sources.Count == 0
            || request.Sources.Count > ProtocolConstants.MaximumZipCreationEntries
            || option is not null && (option.Format != request.Format
                || request.Sources.Count > option.MaximumEntries
                || !option.CompressionPresets.Contains(request.CompressionPreset)))
            return false;

        bool hasDirectory = request.Sources.Any(
            source => source.Kind == ArchiveCreationSourceKind.Directory);
        if (hasDirectory && option?.SupportsDirectories == false) return false;
        if (request.Format is ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2
            && (request.Sources.Count != 1
                || request.Sources[0].Kind != ArchiveCreationSourceKind.File))
            return false;
        if (request.Format == ArchiveWriteFormat.Tar
            && request.CompressionPreset != ArchiveCompressionPreset.Store)
            return false;

        if (!TryBuildSources(
                request.Sources.Select(source => (
                    source.SourcePath,
                    source.Kind == ArchiveCreationSourceKind.Directory)),
                out IReadOnlyList<ArchiveCreationSource> rebuilt)
            || !rebuilt.SequenceEqual(request.Sources))
            return false;
        return true;
    }

    /// <summary>
    /// Rechecks only the selected roots immediately before dispatch. Directory contents
    /// remain entirely Worker-owned and are never enumerated by Rain Explorer.
    /// </summary>
    public static bool TryValidateSourceRoots(IReadOnlyList<ArchiveCreationSource> sources)
    {
        if (sources.Count == 0) return false;
        try
        {
            foreach (ArchiveCreationSource source in sources)
            {
                FileAttributes attributes = File.GetAttributes(source.SourcePath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)
                    || attributes.HasFlag(FileAttributes.Device)
                    || (source.Kind == ArchiveCreationSourceKind.Directory)
                    != attributes.HasFlag(FileAttributes.Directory))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryValidateOutputDestination(
        string requested,
        string requiredExtension,
        IReadOnlyList<ArchiveCreationSource> sources,
        out string canonical,
        out bool destinationExists)
    {
        canonical = string.Empty;
        destinationExists = false;
        if (string.IsNullOrWhiteSpace(requested)
            || requested.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(requested))
            return false;
        try
        {
            canonical = Path.GetFullPath(requested);
            if (!canonical.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
                return false;
            string? root = Path.GetPathRoot(canonical);
            if (string.IsNullOrEmpty(root)
                || string.Equals(
                    Path.TrimEndingDirectorySeparator(canonical),
                    Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (File.Exists(canonical) || Directory.Exists(canonical))
            {
                destinationExists = true;
                return false;
            }
            DirectoryInfo? parent = Directory.GetParent(canonical);
            if (parent is null || !parent.Exists) return false;
            for (DirectoryInfo? current = parent; current is not null; current = current.Parent)
            {
                FileAttributes attributes = current.Attributes;
                if (!attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }
            foreach (ArchiveCreationSource source in sources)
            {
                if (string.Equals(canonical, source.SourcePath, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (source.Kind == ArchiveCreationSourceKind.Directory
                    && canonical.StartsWith(
                        Path.TrimEndingDirectorySeparator(source.SourcePath)
                            + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or UnauthorizedAccessException)
        {
            canonical = string.Empty;
            return false;
        }
    }

    public static string ExtensionFor(ArchiveWriteFormat format) => format switch
    {
        ArchiveWriteFormat.Zip => ".zip",
        ArchiveWriteFormat.Tar => ".tar",
        ArchiveWriteFormat.TarGZip => ".tar.gz",
        ArchiveWriteFormat.TarBZip2 => ".tar.bz2",
        ArchiveWriteFormat.GZip => ".gz",
        ArchiveWriteFormat.BZip2 => ".bz2",
        ArchiveWriteFormat.Rar => ".rar",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
