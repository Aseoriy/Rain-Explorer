using System.Text;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.Cli;

public static class CliProgram
{
    public static void ConfigureConsoleEncoding()
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (!CliArguments.TryParse(args, out var options, out var error))
        {
            if (error is not null)
            {
                Console.Error.WriteLine(UntrustedDisplayText.Escape(error));
            }

            PrintUsage();
            return error is null ? 0 : 2;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        char[]? password = null;
        try
        {
            await using var client = await ArchiveClient
                .StartAsync(options.WorkerPath, cancellationToken: shutdown.Token)
                .ConfigureAwait(false);
            GetZipWriteCapabilitiesResponse? globalZipCapabilities = null;
            if (options.Command == CliCommand.RecoveryList)
            {
                return await ListRecoveriesAsync(client, shutdown.Token).ConfigureAwait(false);
            }

            if (options.Command == CliCommand.RecoveryInspect)
            {
                return await InspectRecoveryAsync(
                    client,
                    options.RecoveryId!,
                    shutdown.Token).ConfigureAwait(false);
            }

            if (options.Command == CliCommand.RecoveryApply)
            {
                return await ApplyRecoveryDispositionAsync(
                    client,
                    options.RecoveryId!,
                    options.RecoveryDisposition!,
                    shutdown.Token).ConfigureAwait(false);
            }

            if (options.Command == CliCommand.NestedRecoveryList)
            {
                return await ListNestedRecoveriesAsync(client, shutdown.Token).ConfigureAwait(false);
            }

            if (options.Command == CliCommand.NestedRecoveryInspect)
            {
                return await InspectNestedRecoveryAsync(
                    client,
                    options.RecoveryId!,
                    shutdown.Token).ConfigureAwait(false);
            }

            if (options.Command == CliCommand.NestedRecoveryDiscard)
            {
                return await DiscardNestedRecoveryAsync(
                    client,
                    options.RecoveryId!,
                    options.NestedRecoveryDiscard!,
                    shutdown.Token).ConfigureAwait(false);
            }

            if (options.Command == CliCommand.NestedRecoveryReopen)
            {
                password = options.PasswordFromStandardInput
                    ? await PasswordReader.ReadAsync(
                        prompt: !Console.IsInputRedirected,
                        shutdown.Token).ConfigureAwait(false)
                    : null;
                Guid? passwordSlotId = password is { Length: > 0 }
                    ? await client.CreateSecretAsync(password, shutdown.Token).ConfigureAwait(false)
                    : null;
                var reopened = await client.ReopenNestedWorkspaceRecoveryAsync(
                        options.RecoveryId!,
                        options.NestedRecoveryReopen!.ExpectedRevision,
                        options.NestedRecoveryReopen.ExpectedSourceCount,
                        options.NestedRecoveryReopen.ExpectedTotalSourceBytes,
                        options.NestedRecoveryReopen.SourceId,
                        options.NestedRecoveryReopen.ExpectedSourceLength,
                        options.NestedRecoveryReopen.ExpectedContentSha256,
                        passwordSlotId,
                        shutdown.Token)
                    .ConfigureAwait(false);
                try
                {
                    Console.WriteLine("Reopened archive: independent root");
                    Console.WriteLine($"Recovery: {UntrustedDisplayText.Escape(reopened.RecoveryId)}");
                    Console.WriteLine($"Source: {UntrustedDisplayText.Escape(reopened.SourceId)}");
                    Console.WriteLine($"Materialized bytes: {reopened.MaterializedBytes}");
                    Console.WriteLine($"Depth: {reopened.Depth}");
                    return await ListAsync(client, reopened.Archive, shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await client.CloseArchiveAsync(reopened.Archive.SessionId, closeTimeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is ArchiveClientException or OperationCanceledException)
                    {
                        // ArchiveClient.DisposeAsync owns the bounded worker teardown that follows.
                    }
                }
            }

            if (options.Command == CliCommand.SupportDiagnostics)
            {
                return await PrintSupportDiagnosticsAsync(client, shutdown.Token)
                    .ConfigureAwait(false);
            }

            if (options.Command == CliCommand.Capabilities)
            {
                return await PrintWriteCapabilitiesAsync(
                    client,
                    sessionId: null,
                    shutdown.Token).ConfigureAwait(false);
            }

            if (options.Command == CliCommand.CreateZip)
            {
                var capabilities = await GetZipWriteCapabilitiesAsync(
                    client,
                    sessionId: null,
                    shutdown.Token).ConfigureAwait(false);
                globalZipCapabilities = capabilities;
                if (!capabilities.CanCreate)
                {
                    return RefuseCapabilities(
                        "The connected worker does not advertise ZIP creation for this protocol minor.");
                }

                if (!capabilities.CompressionPresets.Contains(options.CompressionPreset))
                {
                    return RefuseCapabilities(
                        $"The connected worker does not advertise the selected compression preset '{options.CompressionPreset}'.");
                }

                if ((options.CreationFiles.Count > 0 || options.CreationDirectories.Count > 0)
                    && !capabilities.SupportsAdd)
                {
                    return RefuseCapabilities(
                        "The connected worker does not advertise ZIP add support for this creation.");
                }

                var started = options.CreationDirectories.Count > 0
                    ? await client.StartZipTreeCreationAsync(
                        options.ArchivePath,
                        options.CreationFiles,
                        options.CreationDirectories,
                        options.CompressionPreset,
                        shutdown.Token).ConfigureAwait(false)
                    : await client.StartZipCreationAsync(
                        options.ArchivePath,
                        options.CreationFiles,
                        options.CompressionPreset,
                        shutdown.Token).ConfigureAwait(false);
                var terminal = await ObserveOperationAsync(
                    client,
                    started.OperationId,
                    shutdown.Token).ConfigureAwait(false);
                return PrintTerminal(terminal);
            }

            if (options.Command == CliCommand.CreateTar)
            {
                var capabilities = await GetTarWriteCapabilitiesAsync(
                    client,
                    shutdown.Token).ConfigureAwait(false);
                if (!capabilities.CanCreate)
                {
                    return RefuseCapabilities(
                        "The connected worker does not advertise TAR creation for this protocol minor.");
                }

                if (!capabilities.CompressionPresets.Contains(ArchiveCompressionPreset.Store))
                {
                    return RefuseCapabilities(
                        "The connected worker does not advertise the Store-only TAR creation preset.");
                }

                if (options.CreationDirectories.Count > 0
                    && !capabilities.SupportsTreeCreation)
                {
                    return RefuseCapabilities(
                        "The connected worker does not advertise TAR directory-tree creation.");
                }

                var started = options.CreationDirectories.Count > 0
                    ? await client.StartTarTreeCreationAsync(
                        options.ArchivePath,
                        options.CreationFiles,
                        options.CreationDirectories,
                        shutdown.Token).ConfigureAwait(false)
                    : await client.StartTarCreationAsync(
                        options.ArchivePath,
                        options.CreationFiles,
                        shutdown.Token).ConfigureAwait(false);
                var terminal = await ObserveOperationAsync(
                    client,
                    started.OperationId,
                    shutdown.Token).ConfigureAwait(false);
                return PrintTerminal(terminal);
            }

            if (options.Command is CliCommand.CreateTarGZip
                or CliCommand.CreateTarBZip2
                or CliCommand.CreateGZip
                or CliCommand.CreateBZip2)
            {
                var capabilities = await GetManagedWriteCapabilitiesAsync(
                    client,
                    shutdown.Token).ConfigureAwait(false);
                var format = options.Command switch
                {
                    CliCommand.CreateTarGZip => ManagedArchiveCreationFormat.TarGZip,
                    CliCommand.CreateTarBZip2 => ManagedArchiveCreationFormat.TarBZip2,
                    CliCommand.CreateGZip => ManagedArchiveCreationFormat.GZip,
                    CliCommand.CreateBZip2 => ManagedArchiveCreationFormat.BZip2,
                    _ => throw new InvalidOperationException(),
                };
                var capability = capabilities.Capabilities.Single(item => item.Format == format);
                if (!capability.CanCreate)
                {
                    return RefuseCapabilities(
                        $"The connected worker does not advertise {format} creation for this protocol minor.");
                }

                if (!capability.CompressionPresets.Contains(options.CompressionPreset))
                {
                    return RefuseCapabilities(
                        $"The connected worker does not advertise the selected {format} preset '{options.CompressionPreset}'.");
                }

                if (options.CreationDirectories.Count > 0 && !capability.SupportsTreeCreation)
                {
                    return RefuseCapabilities(
                        $"The connected worker does not advertise {format} directory-tree creation.");
                }

                var started = options.Command switch
                {
                    CliCommand.CreateTarGZip when options.CreationDirectories.Count > 0 =>
                        await client.StartTarGZipTreeCreationAsync(
                            options.ArchivePath,
                            options.CreationFiles,
                            options.CreationDirectories,
                            options.CompressionPreset,
                            shutdown.Token).ConfigureAwait(false),
                    CliCommand.CreateTarGZip => await client.StartTarGZipCreationAsync(
                        options.ArchivePath,
                        options.CreationFiles,
                        options.CompressionPreset,
                        shutdown.Token).ConfigureAwait(false),
                    CliCommand.CreateTarBZip2 when options.CreationDirectories.Count > 0 =>
                        await client.StartTarBZip2TreeCreationAsync(
                            options.ArchivePath,
                            options.CreationFiles,
                            options.CreationDirectories,
                            options.CompressionPreset,
                            shutdown.Token).ConfigureAwait(false),
                    CliCommand.CreateTarBZip2 => await client.StartTarBZip2CreationAsync(
                        options.ArchivePath,
                        options.CreationFiles,
                        options.CompressionPreset,
                        shutdown.Token).ConfigureAwait(false),
                    CliCommand.CreateGZip => await client.StartGZipCreationAsync(
                        options.ArchivePath,
                        options.RawSourcePath!,
                        options.CompressionPreset,
                        shutdown.Token).ConfigureAwait(false),
                    CliCommand.CreateBZip2 => await client.StartBZip2CreationAsync(
                        options.ArchivePath,
                        options.RawSourcePath!,
                        options.CompressionPreset,
                        shutdown.Token).ConfigureAwait(false),
                    _ => throw new InvalidOperationException(),
                };
                var terminal = await ObserveOperationAsync(
                    client,
                    started.OperationId,
                    shutdown.Token).ConfigureAwait(false);
                return PrintTerminal(terminal);
            }

            if (options.Command == CliCommand.RebuildZip)
            {
                // Refuse a downgraded/missing current-minor capability before
                // reading the source archive or touching the destination.
                globalZipCapabilities = await GetZipWriteCapabilitiesAsync(
                    client,
                    sessionId: null,
                    shutdown.Token).ConfigureAwait(false);
                if (!TryValidateRebuildCapabilities(
                        globalZipCapabilities,
                        options,
                        requireRebuildEligibility: false,
                        out var globalCapabilityError))
                {
                    return RefuseCapabilities(globalCapabilityError);
                }
            }

            password = options.PasswordFromStandardInput
                ? await PasswordReader.ReadAsync(prompt: !Console.IsInputRedirected, shutdown.Token).ConfigureAwait(false)
                : null;
            Guid? openSecret = null;
            var openedWithPassword = password is { Length: > 0 };
            if (openedWithPassword)
            {
                openSecret = await client.CreateSecretAsync(password!, shutdown.Token).ConfigureAwait(false);
            }

            OpenArchiveResponse opened;
            try
            {
                opened = await client.OpenArchiveAsync(options.ArchivePath, openSecret, shutdown.Token).ConfigureAwait(false);
            }
            catch (ArchiveClientException exception)
                when (exception.Error.Category == ArchiveErrorCategory.PasswordRequired
                      && password is null
                      && !Console.IsInputRedirected)
            {
                password = await PasswordReader.ReadAsync(prompt: true, shutdown.Token).ConfigureAwait(false);
                openSecret = await client.CreateSecretAsync(password, shutdown.Token).ConfigureAwait(false);
                openedWithPassword = true;
                opened = await client.OpenArchiveAsync(options.ArchivePath, openSecret, shutdown.Token).ConfigureAwait(false);
            }

            try
            {
                if (options.Command == CliCommand.RebuildZip)
                {
                    var capabilities = await GetZipWriteCapabilitiesAsync(
                        client,
                        opened.Archive.SessionId,
                        shutdown.Token).ConfigureAwait(false);
                    if (!TryValidateRebuildCapabilities(
                            capabilities,
                            options,
                            requireRebuildEligibility: true,
                            out var capabilityError))
                    {
                        return RefuseCapabilities(capabilityError);
                    }
                }

                return options.Command switch
                {
                    CliCommand.List => await ListAsync(client, opened.Archive, shutdown.Token).ConfigureAwait(false),
                    CliCommand.Extract => await ExtractAsync(
                        client,
                        opened.Archive,
                        options,
                        password,
                        openedWithPassword,
                        shutdown.Token).ConfigureAwait(false),
                    CliCommand.Test => await TestAsync(
                        client,
                        opened.Archive,
                        password,
                        openedWithPassword,
                        shutdown.Token).ConfigureAwait(false),
                    CliCommand.RebuildZip => await RebuildZipAsync(
                        client,
                        opened.Archive,
                        options,
                        shutdown.Token).ConfigureAwait(false),
                    _ => 2,
                };
            }
            finally
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await client.CloseArchiveAsync(opened.Archive.SessionId, closeTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ArchiveClientException or OperationCanceledException)
                {
                    // ArchiveClient.DisposeAsync owns the bounded worker teardown that follows.
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (ArchiveClientException exception)
        {
            Console.Error.WriteLine(
                $"{UntrustedDisplayText.Escape(exception.Error.Code)}: "
                + UntrustedDisplayText.Escape(exception.Error.Message));
            if (exception.Error.Category == ArchiveErrorCategory.PasswordRequired)
            {
                Console.Error.WriteLine("Supply the password through standard input with --password-stdin.");
            }

            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(UntrustedDisplayText.Escape(exception.Message));
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (password is not null)
            {
                Array.Clear(password);
            }
        }
    }

    private static async Task<int> ListAsync(
        ArchiveClient client,
        ArchiveDescriptor archive,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Format: {UntrustedDisplayText.Escape(archive.Format)}");
        Console.WriteLine($"Provider: {UntrustedDisplayText.Escape(archive.ProviderId)}");
        Console.WriteLine($"Entries: {archive.EntryCount}");
        Console.WriteLine();

        var offset = 0;
        do
        {
            var page = await client.ListEntriesAsync(archive.SessionId, offset, 1024, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Entries)
            {
                var kind = entry.Kind == ArchiveEntryKind.Directory ? "D" : "F";
                var encrypted = entry.IsEncrypted ? " encrypted" : string.Empty;
                var size = entry.OriginalSize?.ToString() ?? "?";
                Console.WriteLine(
                    $"{kind} {size,12} {UntrustedDisplayText.Escape(entry.RawPath)}{encrypted}");
            }

            if (page.NextOffset is not { } next)
            {
                break;
            }

            offset = next;
        }
        while (true);

        return 0;
    }

    private static async Task<int> PrintSupportDiagnosticsAsync(
        ArchiveClient client,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsFeature(ProtocolFeatures.SupportDiagnostics)
            || ProtocolFeatures.SupportDiagnosticsDependencies.Any(
                dependency => !client.SupportsFeature(dependency)))
        {
            return RefuseCapabilities(
                "The connected worker did not negotiate bounded support diagnostics for this protocol minor.");
        }

        var diagnostics = await client
            .GetSupportDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"protocol-major: {diagnostics.ProtocolMajor}");
        Console.WriteLine($"protocol-minor: {diagnostics.ProtocolMinor}");
        Console.WriteLine($"enabled-feature-count: {diagnostics.EnabledFeatureCount}");
        Console.WriteLine($"containment-model: {diagnostics.ContainmentModel}");
        Console.WriteLine($"same-user-security-sandbox: {diagnostics.IsSameUserSecuritySandbox}");
        Console.WriteLine($"maximum-active-and-queued-operations: {diagnostics.MaximumActiveAndQueuedOperations}");
        Console.WriteLine($"maximum-concurrent-operations: {diagnostics.MaximumConcurrentOperations}");
        Console.WriteLine($"queue-ordering: {diagnostics.QueueOrdering}");
        Console.WriteLine($"maximum-retained-terminal-operations: {diagnostics.MaximumRetainedTerminalOperations}");
        Console.WriteLine($"active-and-queued-operation-count: {diagnostics.ActiveAndQueuedOperationCount}");
        Console.WriteLine($"retained-terminal-operation-count: {diagnostics.RetainedTerminalOperationCount}");
        Console.WriteLine($"operation-history-truncated: {diagnostics.OperationHistoryTruncated}");
        Console.WriteLine($"supports-queue-reordering: {diagnostics.SupportsQueueReordering}");
        Console.WriteLine($"supports-pause: {diagnostics.SupportsPause}");
        Console.WriteLine($"supports-retry: {diagnostics.SupportsRetry}");
        Console.WriteLine($"supports-automatic-recovery-disposition: {diagnostics.SupportsAutomaticRecoveryDisposition}");
        Console.WriteLine($"persists-operation-activity-across-worker-restart: {diagnostics.PersistsOperationActivityAcrossWorkerRestart}");
        return 0;
    }

    private static async Task<int> ListRecoveriesAsync(
        ArchiveClient client,
        CancellationToken cancellationToken)
    {
        var inventory = await client
            .ListArchiveCreationRecoveriesAsync(cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Recoveries: {inventory.Recoveries.Count}");
        Console.WriteLine($"Active operations: {inventory.ActiveOperationCount}");
        Console.WriteLine($"Suspicious records: {inventory.SuspiciousOperationCount}");
        Console.WriteLine($"Incomplete journals: {inventory.IncompleteJournalCount}");
        Console.WriteLine($"Inventory truncated: {inventory.IsTruncated}");
        Console.WriteLine($"Inspection incomplete: {inventory.InspectionIncomplete}");
        foreach (var recovery in inventory.Recoveries)
        {
            var incompleteTail = recovery.HasIncompleteTail ? " incomplete-tail" : string.Empty;
            Console.WriteLine(
                $"{UntrustedDisplayText.Escape(recovery.RecoveryId)} "
                + $"operation={recovery.OperationId} "
                + $"phase={recovery.Phase} "
                + $"sequence={recovery.Sequence} "
                + $"recorded={recovery.RecordedAtUtc:O}{incompleteTail}");
        }

        return 0;
    }

    private static async Task<int> InspectRecoveryAsync(
        ArchiveClient client,
        string recoveryId,
        CancellationToken cancellationToken)
    {
        var inspection = await client
            .InspectArchiveCreationRecoveryAsync(recoveryId, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Recovery: {UntrustedDisplayText.Escape(inspection.Recovery.RecoveryId)}");
        Console.WriteLine($"Original operation: {inspection.Recovery.OperationId}");
        Console.WriteLine($"Journal phase: {inspection.Recovery.Phase}");
        Console.WriteLine($"Journal sequence: {inspection.Recovery.Sequence}");
        Console.WriteLine($"Journal recorded: {inspection.Recovery.RecordedAtUtc:O}");
        Console.WriteLine($"Journal incomplete tail: {inspection.Recovery.HasIncompleteTail}");
        Console.WriteLine($"Destination: {UntrustedDisplayText.Escape(inspection.DestinationPath)}");
        Console.WriteLine($"Remote destination: {inspection.IsRemoteDestination}");
        Console.WriteLine($"Destination parent: {inspection.DestinationParent}");
        Console.WriteLine($"Staging artifact: {FormatArtifact(inspection.StagingArtifact)}");
        Console.WriteLine($"Final artifact: {FormatArtifact(inspection.FinalArtifact)}");
        Console.WriteLine($"Assessment: {inspection.Assessment}");
        Console.WriteLine($"Inspected: {inspection.InspectedAtUtc:O}");
        return 0;
    }

    private static async Task<int> ApplyRecoveryDispositionAsync(
        ArchiveClient client,
        string recoveryId,
        CliRecoveryDisposition disposition,
        CancellationToken cancellationToken)
    {
        var response = await client.ApplyArchiveCreationRecoveryDispositionAsync(
                recoveryId,
                disposition.ExpectedPhase,
                disposition.ExpectedSequence,
                disposition.ExpectedRecordedAtUtc,
                disposition.Action,
                cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Recovery: {UntrustedDisplayText.Escape(response.RecoveryId)}");
        Console.WriteLine($"Action: {response.Action}");
        Console.WriteLine($"Result: {response.Result}");
        Console.WriteLine($"Final assessment: {response.FinalAssessment}");
        Console.WriteLine($"Resolved: {response.ResolvedAtUtc:O}");
        return 0;
    }

    private static string FormatArtifact(
        ArchiveCreationRecoveryArtifactInspection artifact)
    {
        var length = artifact.ObservedLength is { } observedLength
            ? $" length={observedLength}"
            : string.Empty;
        var links = artifact.LinkCount is { } linkCount
            ? $" links={linkCount}"
            : string.Empty;
        return $"{artifact.State}{length}{links}";
    }

    private static async Task<int> ListNestedRecoveriesAsync(
        ArchiveClient client,
        CancellationToken cancellationToken)
    {
        var inventory = await client
            .ListNestedWorkspaceRecoveriesAsync(cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Recoveries: {inventory.Recoveries.Count}");
        Console.WriteLine($"Active workspaces: {inventory.ActiveWorkspaceCount}");
        Console.WriteLine($"Suspicious workspaces: {inventory.SuspiciousWorkspaceCount}");
        Console.WriteLine($"Inventory truncated: {inventory.IsTruncated}");
        Console.WriteLine($"Inspection incomplete: {inventory.InspectionIncomplete}");
        foreach (var recovery in inventory.Recoveries)
        {
            Console.WriteLine(UntrustedDisplayText.Escape(recovery.RecoveryId));
        }

        return 0;
    }

    private static async Task<int> InspectNestedRecoveryAsync(
        ArchiveClient client,
        string recoveryId,
        CancellationToken cancellationToken)
    {
        var inspection = await client
            .InspectNestedWorkspaceRecoveryAsync(recoveryId, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Recovery: {UntrustedDisplayText.Escape(inspection.RecoveryId)}");
        Console.WriteLine($"Assessment: {inspection.Assessment}");
        Console.WriteLine($"Source count: {inspection.SourceCount?.ToString() ?? "unavailable"}");
        Console.WriteLine($"Total source bytes: {inspection.TotalSourceBytes?.ToString() ?? "unavailable"}");
        Console.WriteLine($"Revision: {UntrustedDisplayText.Escape(inspection.Revision ?? "unavailable")}");
        Console.WriteLine($"Inspected: {inspection.InspectedAtUtc:O}");
        if (client.SupportsFeature(ProtocolFeatures.NestedWorkspaceIndependentReopen))
        {
            var reopenInspection = await client
                .InspectNestedWorkspaceReopenAsync(recoveryId, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine("Independent reopen evidence:");
            Console.WriteLine($"Reopen assessment: {reopenInspection.Assessment}");
            Console.WriteLine($"Reopen source count: {reopenInspection.SourceCount?.ToString() ?? "unavailable"}");
            Console.WriteLine($"Reopen total source bytes: {reopenInspection.TotalSourceBytes?.ToString() ?? "unavailable"}");
            Console.WriteLine($"Reopen revision: {UntrustedDisplayText.Escape(reopenInspection.Revision ?? "unavailable")}");
            Console.WriteLine($"Supports independent reopen: {reopenInspection.SupportsIndependentReopen}");
            if (reopenInspection.Sources is { } sources)
            {
                foreach (var source in sources)
                {
                    Console.WriteLine(
                        $"Source evidence: {UntrustedDisplayText.Escape(source.SourceId)} "
                        + $"length={source.Length} "
                        + $"sha256={UntrustedDisplayText.Escape(source.ContentSha256)}");
                }
            }
        }

        return 0;
    }

    private static async Task<int> DiscardNestedRecoveryAsync(
        ArchiveClient client,
        string recoveryId,
        CliNestedWorkspaceRecoveryDiscard discard,
        CancellationToken cancellationToken)
    {
        var response = await client.DiscardNestedWorkspaceRecoveryAsync(
                recoveryId,
                discard.ExpectedRevision,
                discard.ExpectedSourceCount,
                discard.ExpectedTotalSourceBytes,
                cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Recovery: {UntrustedDisplayText.Escape(response.RecoveryId)}");
        Console.WriteLine($"Result: {response.Result}");
        Console.WriteLine($"Resolved: {response.ResolvedAtUtc:O}");
        return 0;
    }

    private static async Task<int> ExtractAsync(
        ArchiveClient client,
        ArchiveDescriptor archive,
        CliArguments options,
        char[]? password,
        bool openedWithPassword,
        CancellationToken cancellationToken)
    {
        var selected = Array.Empty<EntryId>();
        if (options.EntryPaths.Count > 0)
        {
            var entries = await ReadAllEntriesAsync(
                client,
                archive.SessionId,
                cancellationToken).ConfigureAwait(false);
            selected = options.EntryPaths.Select(path =>
            {
                var match = entries.FirstOrDefault(
                    entry => string.Equals(entry.RawPath, path, StringComparison.Ordinal));
                return match?.EntryId
                    ?? throw new ArgumentException($"Archive entry was not found: {path}");
            }).ToArray();
        }

        var operationSecret = await CreateOperationSecretIfNeededAsync(
            client,
            archive,
            password,
            openedWithPassword,
            cancellationToken).ConfigureAwait(false);

        var started = await client.StartExtractionAsync(
            archive.SessionId,
            selected,
            options.DestinationPath!,
            operationSecret,
            cancellationToken).ConfigureAwait(false);
        var terminal = await ObserveOperationAsync(client, started.OperationId, cancellationToken).ConfigureAwait(false);
        return PrintTerminal(terminal);
    }

    private static async Task<int> RebuildZipAsync(
        ArchiveClient client,
        ArchiveDescriptor archive,
        CliArguments options,
        CancellationToken cancellationToken)
    {
        if (!archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite))
        {
            Console.Error.WriteLine(
                "ZIP Rebuild Copy is unavailable because the opened archive did not advertise the required capability; the original is untouched.");
            return 1;
        }

        if (archive.HasEncryptedEntries)
        {
            Console.Error.WriteLine(
                "ZIP Rebuild Copy refuses encrypted sources; the original is untouched.");
            return 1;
        }

        Console.WriteLine(
            "Original archive is untouched; validating the requested ZIP Rebuild Copy.");

        if (options.RebuildMetadata is { Count: > 0 }
            && !client.SupportsFeature(ProtocolFeatures.ZipRebuildMetadata))
        {
            Console.Error.WriteLine(
                "ZIP Rebuild Metadata is unavailable because the connected worker did not negotiate the required feature; the original is untouched.");
            return 1;
        }

        if (File.Exists(options.DestinationPath!) || Directory.Exists(options.DestinationPath!))
        {
            Console.Error.WriteLine(
                "ZIP Rebuild Copy requires a new destination path that does not already exist; the original is untouched.");
            return 1;
        }

        var entries = await ReadAllEntriesAsync(
            client,
            archive.SessionId,
            cancellationToken).ConfigureAwait(false);
        var indexedEntries = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.NormalizedPath))
            .ToArray();
        if (indexedEntries.Length != entries.Count)
        {
            Console.Error.WriteLine(
                "ZIP Rebuild Copy requires persistent normalized entry keys; the original is untouched.");
            return 1;
        }

        var deleteKeys = new HashSet<ArchiveEntryKey>(options.RebuildEntriesToDelete);
        foreach (var deleteKey in deleteKeys)
        {
            var matches = indexedEntries.Count(entry =>
                string.Equals(entry.NormalizedPath, deleteKey.NormalizedPath, StringComparison.Ordinal)
                && entry.DuplicateOrdinal == deleteKey.DuplicateOrdinal);
            if (matches != 1)
            {
                Console.Error.WriteLine(
                    $"ZIP Rebuild Copy delete target was not an exact existing entry: {UntrustedDisplayText.Escape(deleteKey.NormalizedPath)} [{deleteKey.DuplicateOrdinal}].");
                return 1;
            }
        }

        var directories = new List<ArchiveCreationDirectory>();
        var treeActions = new List<ArchiveRebuildTreeAction>();
        if (options.RebuildDirectories is { Count: > 0 }
            || options.RebuildTreeActions is { Count: > 0 })
        {
            if (!client.SupportsFeature(ProtocolFeatures.ZipRebuildTreeEdit))
            {
                Console.Error.WriteLine(
                    "ZIP Rebuild Tree Edit is unavailable because the connected worker did not negotiate the required feature; the original is untouched.");
                return 1;
            }

            var plan = await client
                .GetZipRebuildTreePlanAsync(archive.SessionId, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine("ZIP Rebuild Tree Plan (read-only):");
            foreach (var node in plan.Directories)
            {
                Console.WriteLine(
                    $"  {UntrustedDisplayText.Escape(node.NormalizedPath)} | descendants={node.DescendantCount} | material={node.MaterialEntryCount} | revision={UntrustedDisplayText.Escape(node.SubtreeRevision)}");
            }

            var planByPath = plan.Directories.ToDictionary(
                node => node.NormalizedPath,
                StringComparer.Ordinal);
            foreach (var requestedDirectory in options.RebuildDirectories ?? [])
            {
                directories.Add(new ArchiveCreationDirectory(
                    requestedDirectory.SourcePath,
                    requestedDirectory.ArchivePath));
            }

            if (directories.Count > 0)
            {
                if (!client.SupportsFeature(ProtocolFeatures.ZipRebuildSourceFolderReview))
                {
                    Console.Error.WriteLine(
                        "ZIP Rebuild source-folder review is unavailable because the connected worker did not negotiate the required feature; the original is untouched.");
                    return 1;
                }

                var review = await client
                    .GetZipRebuildSourceFolderReviewAsync(
                        archive.SessionId,
                        directories,
                        cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine("ZIP Rebuild source-folder review (Worker-owned, bounded):");
                foreach (var item in review.Directories)
                {
                    Console.WriteLine(
                        $"  {UntrustedDisplayText.Escape(item.SourcePath)} -> {UntrustedDisplayText.Escape(item.ArchiveRoot)} | descendants={item.DescendantCount} | files={item.FileCount}");
                }
            }

            foreach (var requestedAction in options.RebuildTreeActions ?? [])
            {
                if (!planByPath.TryGetValue(requestedAction.NormalizedPath, out var node)
                    || !string.Equals(
                        node.SubtreeRevision,
                        requestedAction.ExpectedRevision,
                        StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        $"ZIP Rebuild Tree Edit revision did not match the read-only plan for {UntrustedDisplayText.Escape(requestedAction.NormalizedPath)}; the original is untouched.");
                    return 1;
                }

                treeActions.Add(new ArchiveRebuildTreeAction(
                    requestedAction.NormalizedPath,
                    requestedAction.ExpectedRevision,
                    requestedAction.Kind,
                    requestedAction.FinalArchivePath));
            }
        }

        var files = new List<ArchiveRebuildCopyFile>(options.RebuildFiles.Count);
        foreach (var requestedFile in options.RebuildFiles)
        {
            if (!File.Exists(requestedFile.SourcePath))
            {
                Console.Error.WriteLine(
                    $"ZIP Rebuild Copy source file does not exist: {UntrustedDisplayText.Escape(requestedFile.SourcePath)}.");
                return 1;
            }

            var matches = indexedEntries
                .Where(entry => RebuildArchivePathsMatch(
                    entry.NormalizedPath!,
                    requestedFile.ArchivePath))
                .ToArray();
            if (requestedFile.IsReplacement)
            {
                if (matches.Length != 1 || matches[0].Kind != ArchiveEntryKind.File)
                {
                    Console.Error.WriteLine(
                        $"ZIP Rebuild Copy --replace must target exactly one existing file: {UntrustedDisplayText.Escape(requestedFile.ArchivePath)}.");
                    return 1;
                }
            }
            else if (matches.Length != 0)
            {
                Console.Error.WriteLine(
                    $"ZIP Rebuild Copy --add must target an archive path that does not already exist: {UntrustedDisplayText.Escape(requestedFile.ArchivePath)}.");
                return 1;
            }

            files.Add(new ArchiveRebuildCopyFile(
                requestedFile.SourcePath,
                requestedFile.ArchivePath));
        }

        var renames = new List<ArchiveRebuildRename>(options.RebuildRenames.Count);
        var renameKeys = new HashSet<ArchiveEntryKey>();
        foreach (var requestedRename in options.RebuildRenames)
        {
            if (!renameKeys.Add(requestedRename.EntryKey))
            {
                Console.Error.WriteLine(
                    $"ZIP Rebuild Rename requires unique persistent entry keys: {UntrustedDisplayText.Escape(requestedRename.EntryKey.NormalizedPath)} [{requestedRename.EntryKey.DuplicateOrdinal}].");
                return 1;
            }

            var matches = indexedEntries
                .Where(entry =>
                    string.Equals(
                        entry.NormalizedPath,
                        requestedRename.EntryKey.NormalizedPath,
                        StringComparison.Ordinal)
                    && entry.DuplicateOrdinal == requestedRename.EntryKey.DuplicateOrdinal)
                .ToArray();
            if (matches.Length != 1 || matches[0].Kind != ArchiveEntryKind.File)
            {
                Console.Error.WriteLine(
                    $"ZIP Rebuild Rename must target exactly one existing regular file: {UntrustedDisplayText.Escape(requestedRename.EntryKey.NormalizedPath)} [{requestedRename.EntryKey.DuplicateOrdinal}].");
                return 1;
            }

            if (deleteKeys.Contains(requestedRename.EntryKey)
                || options.RebuildFiles.Any(file =>
                    RebuildArchivePathsMatch(
                        file.ArchivePath,
                        requestedRename.EntryKey.NormalizedPath)))
            {
                Console.Error.WriteLine(
                    $"ZIP Rebuild Rename cannot target an entry that is also deleted or replaced: {UntrustedDisplayText.Escape(requestedRename.EntryKey.NormalizedPath)} [{requestedRename.EntryKey.DuplicateOrdinal}].");
                return 1;
            }

            renames.Add(new ArchiveRebuildRename(
                requestedRename.EntryKey,
                requestedRename.FinalArchivePath));
        }

        var metadata = options.RebuildMetadata ?? [];
        if (metadata.Count > 0)
        {
            if (!client.SupportsFeature(ProtocolFeatures.ZipRebuildMetadata))
            {
                Console.Error.WriteLine(
                    "ZIP Rebuild Metadata is unavailable because the connected worker did not negotiate the required feature; the original is untouched.");
                return 1;
            }

            Console.WriteLine("ZIP Rebuild metadata review (timezone-less DOS values; Worker remains authoritative):");
            foreach (var directive in metadata)
            {
                var target = directive.Target.Kind switch
                {
                    ArchiveRebuildMetadataTargetKind.RetainedEntryKey when directive.Target.RetainedEntryKey is { } key
                        => $"retained {UntrustedDisplayText.Escape(key.NormalizedPath)} [{key.DuplicateOrdinal}]",
                    ArchiveRebuildMetadataTargetKind.MappedFinalPath
                        => $"final {UntrustedDisplayText.Escape(directive.Target.MappedFinalArchivePath ?? "")}/{directive.Target.MappedKind}",
                    _ => "invalid target",
                };
                var time = directive.LastWriteTime is { } timestamp
                    ? $"{timestamp.Year:0000}-{timestamp.Month:00}-{timestamp.Day:00}T{timestamp.Hour:00}:{timestamp.Minute:00}:{timestamp.Second:00}"
                    : "unchanged";
                var attributes = directive.AttributesLowWord is { } lowWord
                    ? $"0x{lowWord:X4}"
                    : "unchanged";
                var current = directive.Target.Kind == ArchiveRebuildMetadataTargetKind.RetainedEntryKey
                    && directive.Target.RetainedEntryKey is { } retainedKey
                    ? FormatCurrentMetadata(indexedEntries.FirstOrDefault(entry =>
                        string.Equals(entry.NormalizedPath, retainedKey.NormalizedPath, StringComparison.Ordinal)
                        && entry.DuplicateOrdinal == retainedKey.DuplicateOrdinal))
                    : "current/default unavailable";
                Console.WriteLine($"  {target} | current: {current} | final: time={time}, attributes={attributes}");
            }
        }

        if (renames.Count > 0
            && !client.SupportsFeature(ProtocolFeatures.ZipRebuildRename))
        {
            Console.Error.WriteLine(
                "ZIP Rebuild Rename is unavailable because the connected worker did not negotiate the required feature; the original is untouched.");
            return 1;
        }

        var renameSourceKeys = renames
            .Select(rename => rename.EntryKey)
            .ToHashSet();
        foreach (var rename in renames)
        {
            var targetMatches = indexedEntries
                .Where(entry => RebuildArchivePathsMatch(
                    entry.NormalizedPath!,
                    rename.FinalArchivePath))
                .ToArray();
            if (targetMatches.Any(entry => entry.Kind == ArchiveEntryKind.Directory))
            {
                Console.Error.WriteLine(
                    $"ZIP Rebuild Rename final paths must identify regular files, not directories: {UntrustedDisplayText.Escape(rename.FinalArchivePath)}.");
                return 1;
            }

            if (targetMatches.Any(entry =>
                    !renameSourceKeys.Contains(new ArchiveEntryKey(
                        entry.NormalizedPath!,
                        entry.DuplicateOrdinal))
                    && !deleteKeys.Contains(new ArchiveEntryKey(
                        entry.NormalizedPath!,
                        entry.DuplicateOrdinal))))
            {
                Console.Error.WriteLine(
                    $"ZIP Rebuild Rename final paths may not collide with retained entries: {UntrustedDisplayText.Escape(rename.FinalArchivePath)}.");
                return 1;
            }
        }

        var started = metadata.Count > 0
            ? await client.StartZipRebuildMetadataAsync(
                archive.SessionId,
                options.DestinationPath!,
                options.RebuildEntriesToDelete,
                files,
                renames,
                directories,
                treeActions,
                metadata,
                options.CompressionPreset,
                cancellationToken).ConfigureAwait(false)
            : directories.Count > 0 || treeActions.Count > 0
            ? await client.StartZipRebuildTreeEditAsync(
                archive.SessionId,
                options.DestinationPath!,
                options.RebuildEntriesToDelete,
                files,
                renames,
                directories,
                treeActions,
                options.CompressionPreset,
                cancellationToken).ConfigureAwait(false)
            : renames.Count == 0
            ? await client.StartZipRebuildCopyAsync(
                archive.SessionId,
                options.DestinationPath!,
                options.RebuildEntriesToDelete,
                files,
                options.CompressionPreset,
                cancellationToken).ConfigureAwait(false)
            : await client.StartZipRebuildRenameAsync(
                archive.SessionId,
                options.DestinationPath!,
                options.RebuildEntriesToDelete,
                files,
                renames,
                options.CompressionPreset,
                cancellationToken).ConfigureAwait(false);
        var terminal = await ObserveOperationAsync(
            client,
            started.OperationId,
            cancellationToken).ConfigureAwait(false);
        var exitCode = PrintTerminal(terminal);
        if (exitCode == 0)
        {
            Console.WriteLine("Original archive was left untouched.");
        }

        return exitCode;
    }

    private static bool RebuildArchivePathsMatch(string left, string right) =>
        string.Equals(
            left.Normalize(NormalizationForm.FormC),
            right.Normalize(NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase);

    private static string FormatCurrentMetadata(ArchiveEntryDescriptor? entry)
    {
        if (entry is null)
        {
            return "unavailable";
        }

        // SharpCompress exposes a provider-converted UTC value here, not the
        // raw timezone-less DOS tuple. Never present that conversion as the
        // current ZIP-local value that the metadata operation will replace.
        const string time = "unavailable (raw ZIP DOS time not exposed)";
        var attributes = entry.WindowsAttributes is { } raw
            && (raw & ~0x0037) == 0
            ? $"0x{raw & 0xFFFF:X4}"
            : "unavailable";
        return $"time={time}, attributes={attributes} (timezone unavailable)";
    }

    private static async Task<int> TestAsync(
        ArchiveClient client,
        ArchiveDescriptor archive,
        char[]? password,
        bool openedWithPassword,
        CancellationToken cancellationToken)
    {
        var operationSecret = await CreateOperationSecretIfNeededAsync(
            client,
            archive,
            password,
            openedWithPassword,
            cancellationToken).ConfigureAwait(false);

        var started = await client.StartIntegrityTestAsync(
            archive.SessionId,
            [],
            operationSecret,
            cancellationToken).ConfigureAwait(false);
        var terminal = await ObserveOperationAsync(client, started.OperationId, cancellationToken).ConfigureAwait(false);
        return PrintTerminal(terminal);
    }

    private static async Task<IReadOnlyList<ArchiveEntryDescriptor>> ReadAllEntriesAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId,
        CancellationToken cancellationToken)
    {
        var entries = new List<ArchiveEntryDescriptor>();
        var offset = 0;
        do
        {
            var page = await client.ListEntriesAsync(sessionId, offset, 1024, cancellationToken).ConfigureAwait(false);
            entries.AddRange(page.Entries);
            if (page.NextOffset is not { } next)
            {
                break;
            }

            offset = next;
        }
        while (true);

        return entries;
    }

    private static async Task<OperationEvent> ObserveOperationAsync(
        ArchiveClient client,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        var lastDisplay = DateTimeOffset.MinValue;
        void Handler(object? _, OperationEvent operationEvent)
        {
            if (operationEvent.OperationId != operationId
                || operationEvent.Progress is not { } progress
                || DateTimeOffset.UtcNow - lastDisplay < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            lastDisplay = DateTimeOffset.UtcNow;
            var total = progress.TotalBytes is { } totalBytes && totalBytes > 0
                ? $" / {totalBytes}"
                : string.Empty;
            var currentEntry = UntrustedDisplayText.Escape(progress.CurrentEntryName);
            Console.Error.Write($"\r{progress.BytesProcessed}{total} bytes  {currentEntry,-30}");
        }

        client.OperationEventReceived += Handler;
        try
        {
            try
            {
                return await client.WaitForTerminalAsync(operationId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await client.CancelOperationAsync(operationId, cancelTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    cancellationToken.IsCancellationRequested
                    && exception is ArchiveClientException or OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                using var terminalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    return await client.WaitForTerminalAsync(
                        operationId,
                        terminalTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }
        finally
        {
            client.OperationEventReceived -= Handler;
            Console.Error.WriteLine();
        }
    }

    private static int PrintTerminal(OperationEvent terminal)
    {
        if (terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings)
        {
            Console.WriteLine(terminal.Phase == OperationPhase.Completed ? "Completed." : "Completed with warnings.");
            return 0;
        }

        Console.Error.WriteLine(terminal.Error is null
            ? terminal.Phase.ToString()
            : $"{UntrustedDisplayText.Escape(terminal.Error.Code)}: "
              + UntrustedDisplayText.Escape(terminal.Error.Message));
        return terminal.Phase == OperationPhase.Cancelled ? 130 : 1;
    }

    private static async Task<Guid?> CreateOperationSecretIfNeededAsync(
        ArchiveClient client,
        ArchiveDescriptor archive,
        char[]? existingPassword,
        bool openedWithPassword,
        CancellationToken cancellationToken)
    {
        if (!archive.HasEncryptedEntries || openedWithPassword)
        {
            return null;
        }

        if (existingPassword is not null)
        {
            return await client.CreateSecretAsync(existingPassword, cancellationToken).ConfigureAwait(false);
        }

        var promptedPassword = await PasswordReader
            .ReadAsync(prompt: !Console.IsInputRedirected, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await client.CreateSecretAsync(promptedPassword, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(promptedPassword);
        }
    }

    private static async Task<GetZipWriteCapabilitiesResponse> GetZipWriteCapabilitiesAsync(
        IArchiveClient client,
        ArchiveSessionId? sessionId,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.write_capabilities_unavailable",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected worker did not negotiate the ZIP write capabilities query for this protocol minor."));
        }

        try
        {
            return await client.GetZipWriteCapabilitiesAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.write_capabilities_unavailable",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected worker cannot answer the ZIP write capabilities query for this protocol minor."));
        }
    }

    private static async Task<GetTarWriteCapabilitiesResponse> GetTarWriteCapabilitiesAsync(
        IArchiveClient client,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities))
        {
            throw new ArchiveClientException(new ArchiveError(
                "tar.write_capabilities_unavailable",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected worker did not negotiate the TAR write capabilities query for this protocol minor."));
        }

        try
        {
            return await client.GetTarWriteCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            throw new ArchiveClientException(new ArchiveError(
                "tar.write_capabilities_unavailable",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected worker cannot answer the TAR write capabilities query for this protocol minor."));
        }
    }

    private static async Task<GetManagedWriteCapabilitiesResponse> GetManagedWriteCapabilitiesAsync(
        IArchiveClient client,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities))
        {
            throw new ArchiveClientException(new ArchiveError(
                "managed.write_capabilities_unavailable",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected worker did not negotiate managed compressed-format capabilities for this protocol minor."));
        }

        try
        {
            return await client.GetManagedWriteCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            throw new ArchiveClientException(new ArchiveError(
                "managed.write_capabilities_unavailable",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected worker cannot answer managed compressed-format capabilities for this protocol minor."));
        }
    }

    private static async Task<int> PrintZipWriteCapabilitiesAsync(
        IArchiveClient client,
        ArchiveSessionId? sessionId,
        CancellationToken cancellationToken)
    {
        var capabilities = await GetZipWriteCapabilitiesAsync(client, sessionId, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"can-create: {capabilities.CanCreate}");
        Console.WriteLine($"can-rebuild: {capabilities.CanRebuild}");
        Console.WriteLine($"supports-add: {capabilities.SupportsAdd}");
        Console.WriteLine($"supports-replace: {capabilities.SupportsReplace}");
        Console.WriteLine($"supports-delete: {capabilities.SupportsDelete}");
        Console.WriteLine($"supports-file-rename: {capabilities.SupportsFileRename}");
        Console.WriteLine($"supports-tree-edit: {capabilities.SupportsTreeEdit}");
        Console.WriteLine($"supports-metadata: {capabilities.SupportsMetadata}");
        Console.WriteLine($"compression-presets: {string.Join(',', capabilities.CompressionPresets)}");
        Console.WriteLine($"maximum-entries: {capabilities.MaximumEntries:N0}");
        Console.WriteLine($"maximum-source-bytes: {capabilities.MaximumSourceBytes:N0}");
        Console.WriteLine($"supports-encryption: {capabilities.SupportsEncryption}");
        Console.WriteLine($"supports-filename-encryption: {capabilities.SupportsFilenameEncryption}");
        Console.WriteLine($"supports-split-output: {capabilities.SupportsSplitOutput}");
        Console.WriteLine($"supports-existing-destination-replacement: {capabilities.SupportsExistingDestinationReplacement}");
        Console.WriteLine($"supports-in-place-update: {capabilities.SupportsInPlaceUpdate}");
        return 0;
    }

    private static string ManagedFormatName(ManagedArchiveCreationFormat format) => format switch
    {
        ManagedArchiveCreationFormat.TarGZip => "tar.gz",
        ManagedArchiveCreationFormat.TarBZip2 => "tar.bz2",
        ManagedArchiveCreationFormat.GZip => "gzip",
        ManagedArchiveCreationFormat.BZip2 => "bzip2",
        _ => throw new InvalidDataException("Unknown managed archive creation format."),
    };

    private static async Task<int> PrintWriteCapabilitiesAsync(
        IArchiveClient client,
        ArchiveSessionId? sessionId,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("format: zip");
        _ = await PrintZipWriteCapabilitiesAsync(client, sessionId, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine("format: tar");
        if (!client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities))
        {
            Console.WriteLine("can-create: False");
            Console.WriteLine("reason: protocol-feature-not-negotiated");
            return 0;
        }

        var capabilities = await GetTarWriteCapabilitiesAsync(client, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"can-create: {capabilities.CanCreate}");
        Console.WriteLine($"can-rebuild: {capabilities.CanRebuild}");
        Console.WriteLine($"supports-tree-creation: {capabilities.SupportsTreeCreation}");
        Console.WriteLine($"compression-presets: {string.Join(',', capabilities.CompressionPresets)}");
        Console.WriteLine($"maximum-entries: {capabilities.MaximumEntries:N0}");
        Console.WriteLine($"maximum-source-bytes: {capabilities.MaximumSourceBytes:N0}");
        Console.WriteLine($"supports-encryption: {capabilities.SupportsEncryption}");
        Console.WriteLine($"supports-filename-encryption: {capabilities.SupportsFilenameEncryption}");
        Console.WriteLine($"supports-split-output: {capabilities.SupportsSplitOutput}");
        Console.WriteLine($"supports-existing-destination-replacement: {capabilities.SupportsExistingDestinationReplacement}");
        Console.WriteLine($"supports-in-place-update: {capabilities.SupportsInPlaceUpdate}");
        if (!client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities))
        {
            foreach (var format in new[] { "tar.gz", "tar.bz2", "gzip", "bzip2" })
            {
                Console.WriteLine($"format: {format}");
                Console.WriteLine("can-create: False");
                Console.WriteLine("reason: protocol-feature-not-negotiated");
            }

            return 0;
        }

        var managed = await GetManagedWriteCapabilitiesAsync(client, cancellationToken)
            .ConfigureAwait(false);
        foreach (var capability in managed.Capabilities)
        {
            Console.WriteLine($"format: {ManagedFormatName(capability.Format)}");
            Console.WriteLine($"can-create: {capability.CanCreate}");
            Console.WriteLine($"can-rebuild: {capability.CanRebuild}");
            Console.WriteLine($"supports-tree-creation: {capability.SupportsTreeCreation}");
            Console.WriteLine($"compression-presets: {string.Join(',', capability.CompressionPresets)}");
            Console.WriteLine($"maximum-entries: {capability.MaximumEntries:N0}");
            Console.WriteLine($"maximum-source-bytes: {capability.MaximumSourceBytes:N0}");
            Console.WriteLine($"supports-encryption: {capability.SupportsEncryption}");
            Console.WriteLine($"supports-filename-encryption: {capability.SupportsFilenameEncryption}");
            Console.WriteLine($"supports-split-output: {capability.SupportsSplitOutput}");
            Console.WriteLine($"supports-existing-destination-replacement: {capability.SupportsExistingDestinationReplacement}");
            Console.WriteLine($"supports-in-place-update: {capability.SupportsInPlaceUpdate}");
        }

        return 0;
    }

    private static int RefuseCapabilities(string message)
    {
        Console.Error.WriteLine(UntrustedDisplayText.Escape(message));
        return 2;
    }

    internal static bool TryValidateRebuildCapabilities(
        GetZipWriteCapabilitiesResponse capabilities,
        CliArguments options,
        bool requireRebuildEligibility,
        out string error)
    {
        if (!capabilities.CanCreate)
        {
            error = "The connected worker does not advertise managed ZIP writing for this protocol minor.";
            return false;
        }

        if (requireRebuildEligibility && !capabilities.CanRebuild)
        {
            error = "The opened archive session is not eligible for ZIP rebuild under this protocol minor.";
            return false;
        }

        if (!capabilities.CompressionPresets.Contains(options.CompressionPreset))
        {
            error = $"The connected worker does not advertise the selected compression preset '{options.CompressionPreset}'.";
            return false;
        }

        if (options.RebuildFiles.Any(file => !file.IsReplacement) && !capabilities.SupportsAdd)
        {
            error = "The connected worker does not advertise ZIP add support for this rebuild.";
            return false;
        }

        if (options.RebuildFiles.Any(file => file.IsReplacement) && !capabilities.SupportsReplace)
        {
            error = "The connected worker does not advertise ZIP replace support for this rebuild.";
            return false;
        }

        if (options.RebuildEntriesToDelete.Count > 0 && !capabilities.SupportsDelete)
        {
            error = "The connected worker does not advertise ZIP delete support for this rebuild.";
            return false;
        }

        if (options.RebuildRenames.Count > 0 && !capabilities.SupportsFileRename)
        {
            error = "The connected worker does not advertise ZIP file-rename support for this rebuild.";
            return false;
        }

        if (options.RebuildDirectories is { Count: > 0 } && !capabilities.SupportsAdd)
        {
            error = "The connected worker does not advertise ZIP add support for this rebuild folder input.";
            return false;
        }

        if (options.RebuildTreeActions is { Count: > 0 } && !capabilities.SupportsTreeEdit)
        {
            error = "The connected worker does not advertise ZIP tree-edit support for this rebuild.";
            return false;
        }

        if (options.RebuildMetadata is { Count: > 0 } && !capabilities.SupportsMetadata)
        {
            error = "The connected worker does not advertise ZIP metadata support for this rebuild.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Rain Archives diagnostic CLI");
        Console.WriteLine();
        Console.WriteLine("  rain-archives capabilities [--worker <path>]");
        Console.WriteLine("  rain-archives support-diagnostics [--worker <path>]");
        Console.WriteLine("  rain-archives list <archive> [--worker <path>] [--password-stdin]");
        Console.WriteLine("  rain-archives test <archive> [--worker <path>] [--password-stdin]");
        Console.WriteLine("  rain-archives extract <archive> <new-destination> [--entry <archive-path>]...");
        Console.WriteLine("                         [--worker <path>] [--password-stdin]");
        Console.WriteLine("  rain-archives create-zip <new-archive.zip> [--preset <level>]");
        Console.WriteLine("                         [--file <source> <archive-path>]...");
        Console.WriteLine("                         [--directory <source> <archive-path>]... [--worker <path>]");
        Console.WriteLine("  rain-archives create-tar <new-archive.tar> [--preset store]");
        Console.WriteLine("                         [--file <source> <archive-path>]...");
        Console.WriteLine("                         [--directory <source> <archive-path>]... [--worker <path>]");
        Console.WriteLine("  rain-archives create-tar-gz <new-archive.tar.gz> [--preset <level>]");
        Console.WriteLine("                         [--file <source> <archive-path>]...");
        Console.WriteLine("                         [--directory <source> <archive-path>]... [--worker <path>]");
        Console.WriteLine("  rain-archives create-tar-bz2 <new-archive.tar.bz2> [--preset balanced]");
        Console.WriteLine("                         [--file <source> <archive-path>]...");
        Console.WriteLine("                         [--directory <source> <archive-path>]... [--worker <path>]");
        Console.WriteLine("  rain-archives create-gzip <new-output.gz> <source-file> [--preset <level>] [--worker <path>]");
        Console.WriteLine("  rain-archives create-bzip2 <new-output.bz2> <source-file> [--preset balanced] [--worker <path>]");
        Console.WriteLine("  rain-archives rebuild-zip <source.zip> <new.zip> [--preset store|fast|balanced|maximum]");
        Console.WriteLine("                         [--delete <archive-path> <duplicate-ordinal>]...");
        Console.WriteLine("                         [--add <source-path> <archive-path>]...");
        Console.WriteLine("                         [--replace <source-path> <archive-path>]...");
        Console.WriteLine("                         [--rename <archive-path> <duplicate-ordinal> <final-archive-path>]...");
        Console.WriteLine("                         [--add-folder <source-folder> <archive-root>]...");
        Console.WriteLine("                         [--move-folder <normalized-path> <revision> <final-prefix>]...");
        Console.WriteLine("                         [--delete-folder <normalized-path> <revision>]...");
        Console.WriteLine("                         --confirm-rebuild-copy [--password-stdin] [--worker <path>]");
        Console.WriteLine("  rain-archives recovery-list [--worker <path>]");
        Console.WriteLine("  rain-archives recovery-inspect <recovery-id> [--worker <path>]");
        Console.WriteLine("  rain-archives recovery-apply <recovery-id> <action>");
        Console.WriteLine("                         --expected-phase <phase> --expected-sequence <number>");
        Console.WriteLine("                         --expected-recorded-at <UTC> --confirm <recovery-id>");
        Console.WriteLine("                         [--worker <path>]");
        Console.WriteLine("  rain-archives nested-recovery-list [--worker <path>]");
        Console.WriteLine("  rain-archives nested-recovery-inspect <recovery-id> [--worker <path>]");
        Console.WriteLine("  rain-archives nested-recovery-discard <recovery-id>");
        Console.WriteLine("                         --expected-revision <revision>");
        Console.WriteLine("                         --expected-source-count <count>");
        Console.WriteLine("                         --expected-total-bytes <bytes> --confirm <recovery-id>");
        Console.WriteLine("                         [--worker <path>]");
        Console.WriteLine("  rain-archives nested-recovery-reopen <recovery-id> <source-id>");
        Console.WriteLine("                         --expected-revision <revision>");
        Console.WriteLine("                         --expected-source-count <count>");
        Console.WriteLine("                         --expected-total-bytes <bytes>");
        Console.WriteLine("                         --expected-source-length <bytes>");
        Console.WriteLine("                         --expected-content-sha256 <sha256>");
        Console.WriteLine("                         [--password-stdin] [--worker <path>]");
        Console.WriteLine();
        Console.WriteLine("ZIP and TAR-family creation accept explicit files and bounded directory trees.");
        Console.WriteLine("Raw GZIP/BZIP2 accept exactly one ordinary source file; TAR is uncompressed Store-only.");
        Console.WriteLine("Creation encryption, split output, overwrite, 7z, RAR, and XZ writing are unavailable.");
        Console.WriteLine("Rebuild-zip always creates a new ZIP and leaves the original archive untouched.");
        Console.WriteLine("Read-operation passwords are accepted only through an interactive prompt or standard input.");
        Console.WriteLine("Recovery-apply actions: commit-owned-staging, discard-owned-staging,");
        Console.WriteLine("accept-published-final, or acknowledge-no-owned-artifact.");
        Console.WriteLine("Copy the exact revision values from recovery-inspect; --confirm must repeat the ID.");
        Console.WriteLine("Nested recovery deletion is explicit and accepts only exact values from nested-recovery-inspect.");
        Console.WriteLine("Nested-recovery-inspect prints reopen evidence when the connected worker negotiates it.");
        Console.WriteLine("Nested recovery reopen accepts only exact revision, source identity, length, and SHA-256 evidence from that inspection; the result is a new independent root.");
    }
}
