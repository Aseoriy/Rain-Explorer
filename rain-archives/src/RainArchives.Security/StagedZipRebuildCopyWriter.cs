using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed class StagedZipRebuildCopyWriter
{
    private readonly ZipRebuildCopyPolicy _policy;
    private readonly ArchiveCreationProvenancePolicy _provenancePolicy;
    private readonly ArchiveCreationResourceGuard _creationResourceGuard;
    private readonly ExtractionResourceGuard _decodedResourceGuard;
    private readonly ArchiveCreationRecoveryStore _recoveryStore;

    public StagedZipRebuildCopyWriter(
        ArchiveCreationRecoveryStore recoveryStore,
        ZipRebuildCopyPolicy? policy = null,
        ArchiveCreationResourceGuard? creationResourceGuard = null,
        ExtractionResourceGuard? decodedResourceGuard = null)
    {
        _recoveryStore = recoveryStore
            ?? throw new ArgumentNullException(nameof(recoveryStore));
        _policy = policy ?? new ZipRebuildCopyPolicy();
        _provenancePolicy = new ArchiveCreationProvenancePolicy();
        _creationResourceGuard = creationResourceGuard ?? new ArchiveCreationResourceGuard();
        _decodedResourceGuard = decodedResourceGuard ?? new ExtractionResourceGuard();
    }

    public Task RebuildAsync(
        IArchiveWriterProvider writerProvider,
        IArchiveHandle sourceArchive,
        ArchiveIndex sourceIndex,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        string sourceArchivePath,
        OpenFileSnapshot sourceArchiveSnapshot,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        Func<CancellationToken, ValueTask> validateSourceArchive,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        RebuildWithRenamesAsync(
            writerProvider,
            sourceArchive,
            sourceIndex,
            entriesToDelete,
            files,
            Array.Empty<ArchiveRebuildRename>(),
            sourceArchivePath,
            sourceArchiveSnapshot,
            destinationPath,
            compressionPreset,
            reportProgress,
            validateSourceArchive,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public async Task RebuildWithRenamesAsync(
        IArchiveWriterProvider writerProvider,
        IArchiveHandle sourceArchive,
        ArchiveIndex sourceIndex,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        string sourceArchivePath,
        OpenFileSnapshot sourceArchiveSnapshot,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        Func<CancellationToken, ValueTask> validateSourceArchive,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null,
        ZipRebuildCopyPlan? plannedPlan = null)
    {
        ArgumentNullException.ThrowIfNull(writerProvider);
        ArgumentNullException.ThrowIfNull(sourceArchive);
        ArgumentNullException.ThrowIfNull(sourceIndex);
        ArgumentNullException.ThrowIfNull(entriesToDelete);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentNullException.ThrowIfNull(reportProgress);
        ArgumentNullException.ThrowIfNull(validateSourceArchive);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!string.Equals(writerProvider.Format, "zip", StringComparison.OrdinalIgnoreCase)
            || !writerProvider.CompressionPresets.Contains(compressionPreset))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_options_unsupported",
                "The active ZIP writer does not support the requested Rebuild Copy options.");
        }

        await validateSourceArchive(cancellationToken).ConfigureAwait(false);
        var plan = plannedPlan ?? await _policy
            .PlanWithRenamesAsync(
                sourceArchive,
                sourceIndex,
                entriesToDelete,
                files,
                renames,
                sourceArchivePath,
                destinationPath,
                cancellationToken)
            .ConfigureAwait(false);
        var retainedReadEntries = plan.Entries
            .Where(entry => entry.Source == ZipRebuildCopyEntrySource.RetainedArchive)
            .Select(entry => entry.RetainedEntry!)
            .ToArray();
        _decodedResourceGuard.ValidateRead(retainedReadEntries);

        var provenanceSources = new List<PlannedArchiveCreationFile>(
            plan.NewSourceFiles.Count + 1)
        {
            new(
                sourceArchivePath,
                "source.zip",
                sourceArchiveSnapshot,
                sourceArchiveSnapshot.LastWriteTimeUtc,
                TimestampWasClamped: false),
        };
        provenanceSources.AddRange(plan.NewSourceFiles);
        var provenancePlan = await _provenancePolicy
            .CaptureAsync(provenanceSources, cancellationToken)
            .ConfigureAwait(false);

        var destination = plan.DestinationPath;
        if (FileSystemIdentityService.PathEntryExists(destination))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "destination.must_not_exist",
                "ZIP Rebuild Copy requires a destination that does not already exist.");
        }

        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new IOException("The ZIP Rebuild Copy destination must have a parent directory.");
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "The ZIP Rebuild Copy destination parent does not exist.");
        }

        ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(parent);
        var parentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(parent);
        var outputGuard = _creationResourceGuard.CreateOutputGuard(
            parent,
            plan.MaximumExpectedOutputBytes);
        var decodedRuntimeGuard = _decodedResourceGuard.CreateRuntimeGuard(
            parent,
            plan.DeclaredInputBytesForRatio);

        ArchiveCreationRecoveryStore.ArchiveCreationRecoveryTransaction? recovery = null;
        HandleRelativeExtractionRoot.HandleRelativeStagedFile? staged = null;
        IArchiveWriterHandle? writer = null;
        Dictionary<string, HandleRelativeExtractionRoot.HandleRelativeSourceFile>? mappedSourceLeases = null;
        Exception? operationFailure = null;
        var preserveRecovery = false;
        var stagingAbsenceProven = false;
        try
        {
            recovery = await _recoveryStore
                .BeginAsync(
                    operationId ?? OperationId.New(),
                    destination,
                    parentIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                staged = HandleRelativeExtractionRoot.CreateStagedFile(
                    parent,
                    recovery.StagingFileName,
                    prepareProvenanceStream: provenancePlan.ProvenanceToApply is not null,
                    prepareReadback: true);
            }
            catch (ArchiveProviderException exception)
                when (exception.Code == "destination.path_appeared")
            {
                preserveRecovery = true;
                throw;
            }
            catch (AggregateException)
            {
                preserveRecovery = true;
                throw;
            }
            catch
            {
                stagingAbsenceProven = true;
                throw;
            }

            if (!string.Equals(parentIdentity, staged.ParentIdentity, StringComparison.Ordinal))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "destination.identity_changed",
                    "The ZIP Rebuild Copy destination parent changed while its handle chain was being pinned.");
            }

            await recovery
                .RecordWritingAsync(staged.FileIdentity, CancellationToken.None)
                .ConfigureAwait(false);
            var guardedOutput = new ArchiveCreationGuardWriteStream(staged.Stream, outputGuard);
            writer = await writerProvider
                .CreateAsync(guardedOutput, compressionPreset, cancellationToken)
                .ConfigureAwait(false);
            if (beforeWriting is not null)
            {
                await beforeWriting().ConfigureAwait(false);
            }

            mappedSourceLeases = await AcquireMappedSourceLeasesAsync(
                    plan.NewSourceFiles,
                    cancellationToken)
                .ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            long bytesProcessed = 0;
            var entriesProcessed = 0;
            foreach (var entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var displayName = UntrustedDisplayText.Escape(entry.ArchivePath);
                if (entry.Kind == ArchiveEntryKind.Directory)
                {
                    await writer.AddDirectoryAsync(
                        new ArchiveWriteDirectory(
                            entry.ArchivePath,
                            entry.ZipLastWriteTime,
                            entry.WindowsAttributes),
                        cancellationToken).ConfigureAwait(false);
                    entriesProcessed++;
                    await reportProgress(CreateProgress(
                        bytesProcessed,
                        plan.TotalSourceBytes,
                        entriesProcessed,
                        plan.TotalEntries,
                        entry.RetainedEntry?.Descriptor.EntryId,
                        displayName,
                        stopwatch.Elapsed,
                        guardedOutput.BytesWritten)).ConfigureAwait(false);
                    continue;
                }

                var entryStart = bytesProcessed;
                await writer.AddFileFromProducerAsync(
                    new ArchiveWriteEntry(
                        entry.ArchivePath,
                        entry.Length,
                        entry.ZipLastWriteTime,
                        entry.WindowsAttributes),
                    async (destinationStream, producerCancellationToken) =>
                    {
                        await using var decodedGuard = new RuntimeGuardWriteStream(
                            destinationStream,
                            decodedRuntimeGuard,
                            entryStart,
                            entry.Length,
                            leaveOpen: true);
                        if (entry.Source == ZipRebuildCopyEntrySource.RetainedArchive)
                        {
                            await sourceArchive.CopyEntryToAsync(
                                entry.RetainedEntry!.ProviderEntry!.Ordinal,
                                decodedGuard,
                                producerCancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            var newFile = entry.NewFile!;
                            var source = mappedSourceLeases[newFile.SourcePath];
                            source.EnsureMatches(newFile.SourceSnapshot);
                            source.Stream.Position = 0;
                            await source.Stream
                                .CopyToAsync(decodedGuard, producerCancellationToken)
                                .ConfigureAwait(false);
                            source.EnsureMatches(newFile.SourceSnapshot);
                        }

                        decodedRuntimeGuard.ValidateCompletedEntry(
                            decodedGuard.EntryBytesWritten,
                            entry.Length);
                    },
                    entryBytes => reportProgress(CreateProgress(
                        checked(entryStart + entryBytes),
                        plan.TotalSourceBytes,
                        entriesProcessed,
                        plan.TotalEntries,
                        entry.RetainedEntry?.Descriptor.EntryId,
                        displayName,
                        stopwatch.Elapsed,
                        guardedOutput.BytesWritten)),
                    cancellationToken).ConfigureAwait(false);
                bytesProcessed = checked(bytesProcessed + entry.Length);
                entriesProcessed++;
                await reportProgress(CreateProgress(
                    bytesProcessed,
                    plan.TotalSourceBytes,
                    entriesProcessed,
                    plan.TotalEntries,
                    entry.RetainedEntry?.Descriptor.EntryId,
                    displayName,
                    stopwatch.Elapsed,
                    guardedOutput.BytesWritten)).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await guardedOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
            await staged.CompleteWritingAsync(cancellationToken).ConfigureAwait(false);
            ValidateFinalManifest(
                staged.GetReadbackStream(),
                plan.Entries,
                cancellationToken);
            await reportProgress(CreateProgress(
                bytesProcessed,
                plan.TotalSourceBytes,
                entriesProcessed,
                plan.TotalEntries,
                currentEntryId: null,
                currentEntryName: null,
                stopwatch.Elapsed,
                guardedOutput.BytesWritten)).ConfigureAwait(false);

            if (plan.ClampedTimestampCount > 0 && reportWarning is not null)
            {
                await reportWarning(new ArchiveWarning(
                    "zip.rebuild_timestamp_clamped",
                    $"{plan.ClampedTimestampCount:N0} retained or mapped timestamp(s) were unavailable or outside ZIP's representable range and were clamped."))
                    .ConfigureAwait(false);
            }

            if (reportWarning is not null)
            {
                await reportWarning(new ArchiveWarning(
                    "zip.rebuild_metadata_limited",
                    "ZIP Rebuild Copy recompressed every file. Entry comments, extra fields, original compression methods, and metadata outside representable timestamps and basic Windows attributes were not preserved."))
                    .ConfigureAwait(false);
            }

            if (provenancePlan.ProvenanceToApply is not null
                && staged.ProvenancePreparation != StagedProvenancePreparationStatus.Prepared
                && reportWarning is not null)
            {
                await reportWarning(new ArchiveWarning(
                    "provenance.not_applied",
                    "The rebuilt archive could not retain the sources' Windows download-security provenance on this destination."))
                    .ConfigureAwait(false);
            }
            else if (provenancePlan.Warning is { } provenanceWarning
                && reportWarning is not null)
            {
                await reportWarning(provenanceWarning).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (beforeCommit is not null)
            {
                await beforeCommit().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await validateSourceArchive(cancellationToken).ConfigureAwait(false);
            await ValidateMappedSourcesAsync(
                    plan.NewSourceFiles,
                    mappedSourceLeases,
                    cancellationToken)
                .ConfigureAwait(false);
            if (plan.NewSourceTrees.Count > 0)
            {
                var treeValidationPlan = new ArchiveCreationPlan(
                    plan.DestinationPath,
                    plan.NewSourceFiles,
                    plan.TotalSourceBytes,
                    plan.MaximumExpectedOutputBytes,
                    plan.ClampedTimestampCount)
                {
                    Directories = plan.NewSourceDirectories,
                    SourceTrees = plan.NewSourceTrees,
                };
                await new ArchiveCreationPolicy()
                    .ValidateSourceTreesAsync(treeValidationPlan, cancellationToken)
                    .ConfigureAwait(false);
            }
            await _provenancePolicy
                .ValidateAsync(provenancePlan, cancellationToken)
                .ConfigureAwait(false);

            if (provenancePlan.ProvenanceToApply is { } provenance
                && staged.ProvenancePreparation == StagedProvenancePreparationStatus.Prepared)
            {
                outputGuard.ValidateNextChunk(
                    guardedOutput.BytesWritten,
                    DownloadProvenanceService.MaximumZoneIdentifierBytes);
                await staged
                    .ApplyPreparedProvenanceAsync(provenance, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(parent);
            var finalParentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(parent);
            if (!string.Equals(parentIdentity, finalParentIdentity, StringComparison.Ordinal))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "destination.identity_changed",
                    "The ZIP Rebuild Copy destination parent changed before commit.");
            }

            if (FileSystemIdentityService.PathEntryExists(destination))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "destination.appeared_before_commit",
                    "The ZIP Rebuild Copy destination appeared before commit and was preserved.");
            }

            await recovery
                .RecordFinalizedAsync(staged.Length, CancellationToken.None)
                .ConfigureAwait(false);
            await recovery
                .RecordCommitIntentAsync(CancellationToken.None)
                .ConfigureAwait(false);
            staged.CommitAs(Path.GetFileName(destination));
            try
            {
                await recovery
                    .RecordCommittedAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                preserveRecovery = true;
                if (reportWarning is not null)
                {
                    await reportWarning(new ArchiveWarning(
                        "creation.recovery_commit_unrecorded",
                        "The rebuilt ZIP was committed, but its final local recovery phase could not be recorded. Rain preserved the prior recovery state for later review."))
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            if (mappedSourceLeases is not null)
            {
                foreach (var source in mappedSourceLeases.Values)
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(
                    "Multiple ZIP Rebuild Copy cleanup steps failed.",
                    cleanupFailure,
                    exception);
        }

        var stagedCleanupSucceeded = false;
        try
        {
            if (staged is not null)
            {
                await staged.DisposeAsync().ConfigureAwait(false);
                stagedCleanupSucceeded = true;
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(
                    "Multiple ZIP Rebuild Copy cleanup steps failed.",
                    cleanupFailure,
                    exception);
        }

        if (recovery is not null)
        {
            try
            {
                if ((stagedCleanupSucceeded || stagingAbsenceProven) && !preserveRecovery)
                {
                    await recovery.DeleteAsync().ConfigureAwait(false);
                }
                else
                {
                    await recovery.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                if (operationFailure is null
                    && stagedCleanupSucceeded
                    && !preserveRecovery
                    && reportWarning is not null)
                {
                    try
                    {
                        await reportWarning(new ArchiveWarning(
                            "creation.recovery_cleanup_failed",
                            "The rebuilt ZIP was committed, but its local recovery record could not be removed. Rain preserved any remaining state for later review."))
                            .ConfigureAwait(false);
                    }
                    catch (Exception warningFailure)
                    {
                        cleanupFailure = cleanupFailure is null
                            ? new AggregateException(
                                "Archive recovery cleanup and warning delivery both failed.",
                                exception,
                                warningFailure)
                            : new AggregateException(
                                "Multiple ZIP Rebuild Copy cleanup steps failed.",
                                cleanupFailure,
                                exception,
                                warningFailure);
                    }
                }
                else
                {
                    cleanupFailure = cleanupFailure is null
                        ? exception
                        : new AggregateException(
                            "Multiple ZIP Rebuild Copy cleanup steps failed.",
                            cleanupFailure,
                            exception);
                }
            }
        }

        if (operationFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "ZIP Rebuild Copy and cleanup both failed.",
                    operationFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    public async Task RebuildWithTreeEditsAsync(
        IArchiveWriterProvider writerProvider,
        IArchiveHandle sourceArchive,
        ArchiveIndex sourceIndex,
        ArchiveFingerprint sourceArchiveFingerprint,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        string sourceArchivePath,
        OpenFileSnapshot sourceArchiveSnapshot,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        Func<CancellationToken, ValueTask> validateSourceArchive,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(sourceArchiveFingerprint);
        await validateSourceArchive(cancellationToken).ConfigureAwait(false);
        var plan = await new ZipRebuildTreeEditPolicy(copyPolicy: _policy)
            .PlanAsync(
                sourceArchive,
                sourceIndex,
                sourceArchiveFingerprint,
                entriesToDelete,
                files,
                renames,
                directories,
                treeActions,
                sourceArchivePath,
                destinationPath,
                cancellationToken)
            .ConfigureAwait(false);

        await RebuildWithRenamesAsync(
            writerProvider,
            sourceArchive,
            sourceIndex,
            entriesToDelete,
            files,
            renames,
            sourceArchivePath,
            sourceArchiveSnapshot,
            destinationPath,
            compressionPreset,
            reportProgress,
            validateSourceArchive,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId,
            plan).ConfigureAwait(false);
    }

    public async Task RebuildWithMetadataAsync(
        IArchiveWriterProvider writerProvider,
        IArchiveHandle sourceArchive,
        ArchiveIndex sourceIndex,
        ArchiveFingerprint sourceArchiveFingerprint,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        IReadOnlyList<ArchiveRebuildMetadataDirective> metadata,
        string sourceArchivePath,
        OpenFileSnapshot sourceArchiveSnapshot,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        Func<CancellationToken, ValueTask> validateSourceArchive,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(sourceArchiveFingerprint);
        ArgumentNullException.ThrowIfNull(metadata);
        await validateSourceArchive(cancellationToken).ConfigureAwait(false);
        var plan = await new ZipRebuildTreeEditPolicy(copyPolicy: _policy)
            .PlanAsync(
                sourceArchive,
                sourceIndex,
                sourceArchiveFingerprint,
                entriesToDelete,
                files,
                renames,
                directories,
                treeActions,
                sourceArchivePath,
                destinationPath,
                cancellationToken)
            .ConfigureAwait(false);
        plan = new ZipRebuildMetadataPolicy().Apply(plan, metadata, cancellationToken);

        await RebuildWithRenamesAsync(
            writerProvider,
            sourceArchive,
            sourceIndex,
            entriesToDelete,
            files,
            renames,
            sourceArchivePath,
            sourceArchiveSnapshot,
            destinationPath,
            compressionPreset,
            reportProgress,
            validateSourceArchive,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId,
            plan).ConfigureAwait(false);
    }

    private static async ValueTask<Dictionary<string, HandleRelativeExtractionRoot.HandleRelativeSourceFile>>
        AcquireMappedSourceLeasesAsync(
            IReadOnlyList<PlannedArchiveCreationFile> files,
            CancellationToken cancellationToken)
    {
        var leases = new Dictionary<string, HandleRelativeExtractionRoot.HandleRelativeSourceFile>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (leases.ContainsKey(file.SourcePath))
                {
                    continue;
                }

                var source = HandleRelativeExtractionRoot.OpenStableSourceFile(
                    file.SourcePath,
                    requireSingleLink: true,
                    retainAncestry: false);
                try
                {
                    source.EnsureMatches(file.SourceSnapshot);
                    leases.Add(file.SourcePath, source);
                }
                catch
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            return leases;
        }
        catch
        {
            foreach (var source in leases.Values)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async ValueTask ValidateMappedSourcesAsync(
        IReadOnlyList<PlannedArchiveCreationFile> files,
        IReadOnlyDictionary<string, HandleRelativeExtractionRoot.HandleRelativeSourceFile> leases,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            leases[file.SourcePath].EnsureMatches(file.SourceSnapshot);
            await using var source = HandleRelativeExtractionRoot.OpenStableSourceFile(
                file.SourcePath,
                requireSingleLink: true);
            source.EnsureMatches(file.SourceSnapshot);
        }
    }

    private static void ValidateFinalManifest(
        Stream stagedArchive,
        IReadOnlyList<PlannedZipRebuildCopyEntry> expected,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = new ZipArchive(stagedArchive, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count != expected.Count)
            {
                throw OutputManifestMismatch();
            }

            for (var index = 0; index < expected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var planned = expected[index];
                var actual = archive.Entries[index];
                var expectedPath = planned.Kind == ArchiveEntryKind.Directory
                    ? $"{planned.ArchivePath.TrimEnd('/')}/"
                    : planned.ArchivePath;
                var actualKind = actual.FullName.EndsWith("/", StringComparison.Ordinal)
                    ? ArchiveEntryKind.Directory
                    : ArchiveEntryKind.File;
                var expectedAttributes = planned.WindowsAttributes
                    ?? (planned.Kind == ArchiveEntryKind.Directory
                        ? (int)FileAttributes.Directory
                        : 0);
                if (!string.Equals(actual.FullName, expectedPath, StringComparison.Ordinal)
                    || actualKind != planned.Kind
                    || actual.Length != planned.Length
                    || actual.ExternalAttributes != expectedAttributes
                    || actual.LastWriteTime.DateTime != NormalizeZipTimestamp(planned.ZipLastWriteTime))
                {
                    throw OutputManifestMismatch();
                }
            }
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or NotSupportedException
                or ArgumentException)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.CorruptArchive,
                "zip.rebuild_output_invalid",
                "The staged ZIP Rebuild Copy output could not be reopened and validated before commit.",
                exception);
        }
    }

    private static DateTime NormalizeZipTimestamp(DateTimeOffset timestamp)
    {
        var value = timestamp.DateTime;
        value = new DateTime(
            value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond),
            DateTimeKind.Unspecified);
        return value.AddSeconds(-(value.Second % 2));
    }

    private static ArchiveProviderException OutputManifestMismatch() =>
        new(
            ArchiveErrorCategory.ProviderFailure,
            "zip.rebuild_output_manifest_mismatch",
            "The staged ZIP Rebuild Copy output did not match the exact validated output manifest.");

    private static OperationProgress CreateProgress(
        long bytesProcessed,
        long totalBytes,
        int entriesProcessed,
        int totalEntries,
        EntryId? currentEntryId,
        string? currentEntryName,
        TimeSpan elapsed,
        long archiveBytesWritten) =>
        new(
            bytesProcessed,
            totalBytes,
            entriesProcessed,
            totalEntries,
            currentEntryId,
            currentEntryName,
            elapsed,
            elapsed.TotalSeconds > 0 ? bytesProcessed / elapsed.TotalSeconds : null,
            archiveBytesWritten > 0 ? (double)bytesProcessed / archiveBytesWritten : null);
}
