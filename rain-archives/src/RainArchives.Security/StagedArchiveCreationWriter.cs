using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed class StagedArchiveCreationWriter
{
    private static readonly CreationFormatProfile ZipProfile = new(
        "zip",
        "ZIP",
        ArchiveCreationOutputFormat.Zip,
        "zip.creation_options_unsupported",
        UseSourceUtcTimestamps: false,
        RequiresStagedReadback: false,
        RequiresContentDigest: false);
    private static readonly CreationFormatProfile TarProfile = new(
        "tar",
        "TAR",
        ArchiveCreationOutputFormat.Tar,
        "tar.creation_options_unsupported",
        UseSourceUtcTimestamps: true,
        RequiresStagedReadback: true,
        RequiresContentDigest: true,
        MinimumTimestamp: DateTimeOffset.UnixEpoch);
    private static readonly CreationFormatProfile TarGZipProfile = new(
        "tar.gz",
        "TAR.GZ",
        ArchiveCreationOutputFormat.TarGZip,
        "tar.gz.creation_options_unsupported",
        UseSourceUtcTimestamps: true,
        RequiresStagedReadback: true,
        RequiresContentDigest: true,
        MinimumTimestamp: DateTimeOffset.UnixEpoch);
    private static readonly CreationFormatProfile TarBZip2Profile = new(
        "tar.bz2",
        "TAR.BZ2",
        ArchiveCreationOutputFormat.TarBZip2,
        "tar.bz2.creation_options_unsupported",
        UseSourceUtcTimestamps: true,
        RequiresStagedReadback: true,
        RequiresContentDigest: true,
        MinimumTimestamp: DateTimeOffset.UnixEpoch);
    private static readonly CreationFormatProfile GZipProfile = new(
        "gzip",
        "GZIP",
        ArchiveCreationOutputFormat.GZip,
        "gzip.creation_options_unsupported",
        UseSourceUtcTimestamps: true,
        RequiresStagedReadback: true,
        RequiresContentDigest: true);
    private static readonly CreationFormatProfile BZip2Profile = new(
        "bzip2",
        "BZIP2",
        ArchiveCreationOutputFormat.BZip2,
        "bzip2.creation_options_unsupported",
        UseSourceUtcTimestamps: true,
        RequiresStagedReadback: true,
        RequiresContentDigest: true);
    private static readonly CreationFormatProfile RarProfile = new(
        "rar",
        "RAR",
        ArchiveCreationOutputFormat.Rar,
        "rar.creation_options_unsupported",
        UseSourceUtcTimestamps: true,
        RequiresStagedReadback: true,
        RequiresContentDigest: true);
    private readonly ArchiveCreationPolicy _policy;
    private readonly ArchiveCreationProvenancePolicy _provenancePolicy;
    private readonly ArchiveCreationResourceGuard _resourceGuard;
    private readonly ArchiveCreationRecoveryStore _recoveryStore;

    public StagedArchiveCreationWriter(
        ArchiveCreationRecoveryStore recoveryStore,
        ArchiveCreationPolicy? policy = null,
        ArchiveCreationResourceGuard? resourceGuard = null)
    {
        ArgumentNullException.ThrowIfNull(recoveryStore);
        _recoveryStore = recoveryStore;
        _policy = policy ?? new ArchiveCreationPolicy();
        _provenancePolicy = new ArchiveCreationProvenancePolicy();
        _resourceGuard = resourceGuard ?? new ArchiveCreationResourceGuard();
    }

    public Task CreateZipAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            ZipProfile,
            provider,
            files,
            [],
            includeDirectories: false,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateZipTreeAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            ZipProfile,
            provider,
            files,
            directories,
            includeDirectories: true,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateTarAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            TarProfile,
            provider,
            files,
            [],
            includeDirectories: false,
            destinationPath,
            ArchiveCompressionPreset.Store,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateTarTreeAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        string destinationPath,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            TarProfile,
            provider,
            files,
            directories,
            includeDirectories: true,
            destinationPath,
            ArchiveCompressionPreset.Store,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateTarGZipAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            TarGZipProfile,
            provider,
            files,
            [],
            includeDirectories: false,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateTarGZipTreeAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            TarGZipProfile,
            provider,
            files,
            directories,
            includeDirectories: true,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateTarBZip2Async(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            TarBZip2Profile,
            provider,
            files,
            [],
            includeDirectories: false,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateTarBZip2TreeAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            TarBZip2Profile,
            provider,
            files,
            directories,
            includeDirectories: true,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateGZipAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            GZipProfile,
            provider,
            RawArchiveEntryName.Rewrite(files, destinationPath, [".gz", ".gzip"]),
            [],
            includeDirectories: false,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateBZip2Async(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            BZip2Profile,
            provider,
            RawArchiveEntryName.Rewrite(files, destinationPath, [".bz2", ".bzip2"]),
            [],
            includeDirectories: false,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateRarAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            RarProfile,
            provider,
            files,
            [],
            includeDirectories: false,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    public Task CreateRarTreeAsync(
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting = null,
        Func<ValueTask>? beforeCommit = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null,
        OperationId? operationId = null) =>
        CreateCoreAsync(
            RarProfile,
            provider,
            files,
            directories,
            includeDirectories: true,
            destinationPath,
            compressionPreset,
            reportProgress,
            cancellationToken,
            beforeWriting,
            beforeCommit,
            reportWarning,
            operationId);

    private async Task CreateCoreAsync(
        CreationFormatProfile profile,
        IArchiveWriterProvider provider,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        bool includeDirectories,
        string destinationPath,
        ArchiveCompressionPreset compressionPreset,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<ValueTask>? beforeWriting,
        Func<ValueTask>? beforeCommit,
        Func<ArchiveWarning, ValueTask>? reportWarning,
        OperationId? operationId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(reportProgress);
        if (!string.Equals(provider.Format, profile.Format, StringComparison.OrdinalIgnoreCase)
            || !provider.CompressionPresets.Contains(compressionPreset))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                profile.UnsupportedOptionsCode,
                $"The active {profile.DisplayName} writer does not support the requested creation options.");
        }

        IArchiveStagedOutputValidator? outputValidator = null;
        if (profile.RequiresStagedReadback)
        {
            outputValidator = provider as IArchiveStagedOutputValidator;
            if (outputValidator is null
                || !string.Equals(
                    outputValidator.Format,
                    profile.Format,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    $"{profile.Format}.staged_readback_unsupported",
                    $"The active {profile.DisplayName} writer cannot validate staged output before publication.");
            }
        }

        var plan = includeDirectories
            ? await _policy
                .PlanTreeAsync(
                    files,
                    directories,
                    destinationPath,
                    profile.OutputFormat,
                    cancellationToken)
                .ConfigureAwait(false)
            : await _policy
                .PlanAsync(
                    files,
                    destinationPath,
                    profile.OutputFormat,
                    cancellationToken)
                .ConfigureAwait(false);
        if (profile.MinimumTimestamp is { } minimumTimestamp
            && (plan.Files.Any(file => file.SourceSnapshot.LastWriteTimeUtc < minimumTimestamp)
                || plan.Directories.Any(directory =>
                    directory.SourceSnapshot.LastWriteTimeUtc < minimumTimestamp)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                $"{profile.Format}.timestamp_before_supported_range",
                $"{profile.DisplayName} creation does not support source timestamps before {minimumTimestamp:O}.");
        }

        var provenancePlan = await _provenancePolicy
            .CaptureAsync(plan.Files, cancellationToken)
            .ConfigureAwait(false);
        var destination = plan.DestinationPath;
        if (FileSystemIdentityService.PathEntryExists(destination))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "destination.must_not_exist",
                $"{profile.DisplayName} creation requires a destination that does not already exist.");
        }

        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new IOException($"The {profile.DisplayName} destination must have a parent directory.");
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"The {profile.DisplayName} destination parent does not exist.");
        }

        ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(parent);
        var parentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(parent);
        var outputGuard = _resourceGuard.CreateOutputGuard(
            parent,
            plan.MaximumExpectedOutputBytes);

        ArchiveCreationRecoveryStore.ArchiveCreationRecoveryTransaction? recovery = null;
        HandleRelativeExtractionRoot.HandleRelativeStagedFile? staged = null;
        IArchiveWriterHandle? writer = null;
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
                    prepareReadback: profile.RequiresStagedReadback);
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
                // CreateStagedFile either returns the owned handle or completes its
                // own exact cleanup. An aggregate above is the only unproven case.
                stagingAbsenceProven = true;
                throw;
            }
            if (!string.Equals(parentIdentity, staged.ParentIdentity, StringComparison.Ordinal))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "destination.identity_changed",
                    $"The {profile.DisplayName} destination parent changed while its handle chain was being pinned.");
            }

            await recovery
                .RecordWritingAsync(staged.FileIdentity, CancellationToken.None)
                .ConfigureAwait(false);

            var guardedOutput = new ArchiveCreationGuardWriteStream(staged.Stream, outputGuard);
            writer = await provider
                .CreateAsync(guardedOutput, compressionPreset, cancellationToken)
                .ConfigureAwait(false);
            if (beforeWriting is not null)
            {
                await beforeWriting().ConfigureAwait(false);
            }

            var stopwatch = Stopwatch.StartNew();
            long bytesProcessed = 0;
            var entriesProcessed = 0;
            var expectedOutput = profile.RequiresStagedReadback
                ? new List<ArchiveStagedOutputManifestEntry>(plan.TotalEntries)
                : null;
            var fileIndex = 0;
            var directoryIndex = 0;
            while (fileIndex < plan.Files.Count || directoryIndex < plan.Directories.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextIsDirectory = directoryIndex < plan.Directories.Count
                    && (fileIndex >= plan.Files.Count
                        || StringComparer.Ordinal.Compare(
                            plan.Directories[directoryIndex].ArchivePath,
                            plan.Files[fileIndex].ArchivePath) < 0);
                if (nextIsDirectory)
                {
                    var directory = plan.Directories[directoryIndex++];
                    var directoryLastWriteTime = profile.UseSourceUtcTimestamps
                        ? directory.SourceSnapshot.LastWriteTimeUtc
                        : directory.ZipLastWriteTime;
                    await writer.AddDirectoryAsync(
                        new ArchiveWriteDirectory(
                            directory.ArchivePath,
                            directoryLastWriteTime),
                        cancellationToken).ConfigureAwait(false);
                    expectedOutput?.Add(new ArchiveStagedOutputManifestEntry(
                        directory.ArchivePath,
                        ArchiveEntryKind.Directory,
                        Length: null,
                        directoryLastWriteTime));
                    entriesProcessed++;
                    await reportProgress(CreateProgress(
                        bytesProcessed,
                        plan.TotalSourceBytes,
                        entriesProcessed,
                        plan.TotalEntries,
                        directory.ArchivePath,
                        stopwatch.Elapsed,
                        guardedOutput.BytesWritten)).ConfigureAwait(false);
                    continue;
                }

                var file = plan.Files[fileIndex++];
                await using var source = HandleRelativeExtractionRoot.OpenStableSourceFile(file.SourcePath);
                source.EnsureMatches(file.SourceSnapshot);
                var entryStart = bytesProcessed;
                var fileLastWriteTime = profile.UseSourceUtcTimestamps
                    ? file.SourceSnapshot.LastWriteTimeUtc
                    : file.ZipLastWriteTime;
                using var digestSource = profile.RequiresContentDigest
                    ? new ContentDigestReadStream(source.Stream, file.SourceSnapshot.Length)
                    : null;
                await writer.AddFileAsync(
                    new ArchiveWriteEntry(
                        file.ArchivePath,
                        file.SourceSnapshot.Length,
                        fileLastWriteTime),
                    digestSource ?? source.Stream,
                    entryBytes => reportProgress(CreateProgress(
                        checked(entryStart + entryBytes),
                        plan.TotalSourceBytes,
                        entriesProcessed,
                        plan.TotalEntries,
                        file.ArchivePath,
                        stopwatch.Elapsed,
                        guardedOutput.BytesWritten)),
                    cancellationToken).ConfigureAwait(false);
                source.EnsureMatches(file.SourceSnapshot);
                var contentSha256 = digestSource?.CompleteDigest();
                expectedOutput?.Add(new ArchiveStagedOutputManifestEntry(
                    file.ArchivePath,
                    ArchiveEntryKind.File,
                    file.SourceSnapshot.Length,
                    fileLastWriteTime,
                    contentSha256));
                bytesProcessed = checked(bytesProcessed + file.SourceSnapshot.Length);
                entriesProcessed++;
                await reportProgress(CreateProgress(
                    bytesProcessed,
                    plan.TotalSourceBytes,
                    entriesProcessed,
                    plan.TotalEntries,
                    file.ArchivePath,
                    stopwatch.Elapsed,
                    guardedOutput.BytesWritten)).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await guardedOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
            await staged.CompleteWritingAsync(cancellationToken).ConfigureAwait(false);
            if (profile.RequiresStagedReadback
                && staged.Length != guardedOutput.BytesWritten)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProviderFailure,
                    $"{profile.Format}.staged_length_mismatch",
                    $"The staged {profile.DisplayName} output length changed before readback validation.");
            }
            if (outputValidator is not null)
            {
                await outputValidator
                    .ValidateAsync(
                        staged.GetReadbackStream(),
                        expectedOutput!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            await reportProgress(CreateProgress(
                bytesProcessed,
                plan.TotalSourceBytes,
                entriesProcessed,
                plan.TotalEntries,
                currentEntryName: null,
                stopwatch.Elapsed,
                guardedOutput.BytesWritten)).ConfigureAwait(false);

            if (!profile.UseSourceUtcTimestamps
                && plan.ClampedTimestampCount > 0
                && reportWarning is not null)
            {
                await reportWarning(new ArchiveWarning(
                    "zip.timestamp_clamped",
                    $"{plan.ClampedTimestampCount:N0} source timestamp(s) were outside ZIP's representable range and were clamped.")).ConfigureAwait(false);
            }

            if (provenancePlan.ProvenanceToApply is not null
                && staged.ProvenancePreparation
                    != StagedProvenancePreparationStatus.Prepared
                && reportWarning is not null)
            {
                await reportWarning(new ArchiveWarning(
                    "provenance.not_applied",
                    "The created archive could not retain the sources' Windows download-security provenance on this destination.")).ConfigureAwait(false);
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
            await _policy.ValidateSourceTreesAsync(plan, cancellationToken).ConfigureAwait(false);
            await _provenancePolicy
                .ValidateAsync(provenancePlan, cancellationToken)
                .ConfigureAwait(false);

            if (provenancePlan.ProvenanceToApply is { } provenance)
            {
                if (staged.ProvenancePreparation
                    == StagedProvenancePreparationStatus.Prepared)
                {
                    outputGuard.ValidateNextChunk(
                        guardedOutput.BytesWritten,
                        DownloadProvenanceService.MaximumZoneIdentifierBytes);
                    await staged
                        .ApplyPreparedProvenanceAsync(provenance, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(parent);
            var finalParentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(parent);
            if (!string.Equals(parentIdentity, finalParentIdentity, StringComparison.Ordinal))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "destination.identity_changed",
                    $"The {profile.DisplayName} destination parent changed before commit.");
            }

            if (FileSystemIdentityService.PathEntryExists(destination))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "destination.appeared_before_commit",
                    $"The {profile.DisplayName} destination appeared before the staged archive could be committed.");
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
                        $"The {profile.DisplayName} archive was committed, but its final local recovery phase could not be recorded. Rain preserved the prior recovery state for later review."))
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
                : new AggregateException("Multiple archive creation cleanup steps failed.", cleanupFailure, exception);
        }

        if (recovery is not null)
        {
            try
            {
                if ((stagedCleanupSucceeded || stagingAbsenceProven)
                    && !preserveRecovery)
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
                            $"The {profile.DisplayName} archive was created, but its local recovery record could not be removed. Rain preserved any remaining state for later review."))
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
                                "Multiple archive creation cleanup steps failed.",
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
                            "Multiple archive creation cleanup steps failed.",
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
                    $"{profile.DisplayName} creation and cleanup both failed.",
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

    private static OperationProgress CreateProgress(
        long bytesProcessed,
        long totalBytes,
        int entriesProcessed,
        int totalEntries,
        string? currentEntryName,
        TimeSpan elapsed,
        long archiveBytesWritten) =>
        new(
            bytesProcessed,
            totalBytes,
            entriesProcessed,
            totalEntries,
            CurrentEntryId: null,
            currentEntryName,
            elapsed,
            elapsed.TotalSeconds > 0 ? bytesProcessed / elapsed.TotalSeconds : null,
            archiveBytesWritten > 0 ? (double)bytesProcessed / archiveBytesWritten : null);

    private sealed record CreationFormatProfile(
        string Format,
        string DisplayName,
        ArchiveCreationOutputFormat OutputFormat,
        string UnsupportedOptionsCode,
        bool UseSourceUtcTimestamps,
        bool RequiresStagedReadback,
        bool RequiresContentDigest,
        DateTimeOffset? MinimumTimestamp = null);

    private sealed class ContentDigestReadStream : Stream
    {
        private readonly Stream _source;
        private readonly long _expectedLength;
        private readonly long _sourceStart;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _hashedLength;
        private bool _completed;

        public ContentDigestReadStream(Stream source, long expectedLength)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead || expectedLength < 0)
            {
                throw new ArgumentException(
                    "Archive content digests require a readable source and a non-negative exact length.",
                    nameof(source));
            }

            _source = source;
            _expectedLength = expectedLength;
            _sourceStart = source.CanSeek ? source.Position : 0;
        }

        public string CompleteDigest()
        {
            if (_completed || _hashedLength != _expectedLength)
            {
                throw SourceDigestFailure();
            }

            _completed = true;
            var digest = _hash.GetHashAndReset();
            try
            {
                return Convert.ToHexString(digest);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateArguments(buffer, offset, count);
            EnsureSequentialPosition();
            var read = _source.Read(buffer, offset, count);
            Track(buffer.AsSpan(offset, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            EnsureSequentialPosition();
            var read = _source.Read(buffer);
            Track(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSequentialPosition();
            var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Track(buffer.Span[..read]);
            return read;
        }

        private void Track(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return;
            }

            long nextLength;
            try
            {
                nextLength = checked(_hashedLength + bytes.Length);
            }
            catch (OverflowException exception)
            {
                throw SourceDigestFailure(exception);
            }

            if (nextLength > _expectedLength)
            {
                throw SourceDigestFailure();
            }

            _hash.AppendData(bytes);
            _hashedLength = nextLength;
        }

        private void EnsureSequentialPosition()
        {
            if (_completed
                || (_source.CanSeek && _source.Position != checked(_sourceStart + _hashedLength)))
            {
                throw NonSequentialRead();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek)
            {
                throw new NotSupportedException();
            }

            long target;
            try
            {
                target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => checked(_hashedLength + offset),
                    SeekOrigin.End => checked(_expectedLength + offset),
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
            }
            catch (OverflowException exception)
            {
                throw NonSequentialRead(exception);
            }

            if (target != _hashedLength)
            {
                throw NonSequentialRead();
            }

            _source.Position = checked(_sourceStart + target);
            return target;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _expectedLength;
        public override long Position
        {
            get => _hashedLength;
            set => _ = Seek(value, SeekOrigin.Begin);
        }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private static ArchiveProviderException SourceDigestFailure(Exception? inner = null) =>
            new(
                ArchiveErrorCategory.SourceChanged,
                "source.changed_during_creation",
                "A source stream changed while its exact archive-content digest was being calculated.",
                inner);

        private static ArchiveProviderException NonSequentialRead(Exception? inner = null) =>
            new(
                ArchiveErrorCategory.ProviderFailure,
                "creation.source_digest_nonsequential",
                "The archive writer did not consume a digest-protected source exactly once in sequential order.",
                inner);

        private static void ValidateArguments(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("The buffer range is invalid.");
            }
        }
    }
}
