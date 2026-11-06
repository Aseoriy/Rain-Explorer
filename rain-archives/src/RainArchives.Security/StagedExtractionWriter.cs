using System.Diagnostics;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed class StagedExtractionWriter
{
    private readonly ExtractionPathPolicy _pathPolicy;
    private readonly ExtractionResourceGuard _resourceGuard;
    private readonly ExtractionRecoveryStore? _recoveryStore;

    public StagedExtractionWriter(
        ExtractionPathPolicy? pathPolicy = null,
        ExtractionResourceGuard? resourceGuard = null,
        ExtractionRecoveryStore? recoveryStore = null)
    {
        _pathPolicy = pathPolicy ?? new ExtractionPathPolicy();
        _resourceGuard = resourceGuard ?? new ExtractionResourceGuard();
        _recoveryStore = recoveryStore;
    }

    public Task ExtractAsync(
        IArchiveHandle archive,
        IReadOnlyCollection<IndexedArchiveEntry> selectedEntries,
        string destinationPath,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? beforeCommit = null,
        DownloadProvenance? provenance = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null) =>
        ExtractCoreAsync(
            operationId: null,
            archive,
            selectedEntries,
            destinationPath,
            reportProgress,
            cancellationToken,
            beforeCommit,
            provenance,
            reportWarning);

    internal async Task<ExtractionRecoveryCompletion> ExtractTrackedAsync(
        OperationId operationId,
        IArchiveHandle archive,
        IReadOnlyCollection<IndexedArchiveEntry> selectedEntries,
        string destinationPath,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? beforeCommit = null,
        DownloadProvenance? provenance = null,
        Func<ArchiveWarning, ValueTask>? reportWarning = null)
    {
        if (_recoveryStore is null)
        {
            throw new InvalidOperationException(
                "Tracked extraction requires an extraction recovery store.");
        }

        return await ExtractCoreAsync(
            operationId,
            archive,
            selectedEntries,
            destinationPath,
            reportProgress,
            cancellationToken,
            beforeCommit,
            provenance,
            reportWarning).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Tracked extraction completed without a recovery lease.");
    }

    private async Task<ExtractionRecoveryCompletion?> ExtractCoreAsync(
        OperationId? operationId,
        IArchiveHandle archive,
        IReadOnlyCollection<IndexedArchiveEntry> selectedEntries,
        string destinationPath,
        Func<OperationProgress, ValueTask> reportProgress,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? beforeCommit,
        DownloadProvenance? provenance,
        Func<ArchiveWarning, ValueTask>? reportWarning)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(selectedEntries);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(reportProgress);

        var destination = Path.GetFullPath(destinationPath);
        if (FileSystemIdentityService.PathEntryExists(destination))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "destination.must_not_exist",
                "Milestone 1 extraction requires a destination that does not already exist.");
        }

        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new IOException("The extraction destination must have a parent directory.");
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The extraction destination parent does not exist.");
        }

        ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(parent);
        var parentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(parent);
        var plan = _pathPolicy.Plan(selectedEntries);
        if (!plan.IsSafe)
        {
            var first = plan.Issues[0];
            throw new ArchiveProviderException(
                first.Code is "path.collision"
                    or "path.file_directory_collision"
                    or "path.parent_spelling_collision"
                    ? ArchiveErrorCategory.PathCollision
                    : ArchiveErrorCategory.UnsafePath,
                first.Code,
                first.Message);
        }

        _resourceGuard.Validate(plan, parent, provenance is not null);

        ExtractionRecoveryStore.ExtractionRecoveryTransaction? recovery = null;
        if (operationId is not null)
        {
            recovery = await (_recoveryStore
                ?? throw new InvalidOperationException(
                    "Tracked extraction requires an extraction recovery store."))
                .BeginAsync(
                    operationId.Value,
                    destination,
                    parentIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var stagingName = recovery?.StagingDirectoryName
            ?? $".rainarchives-extract-{Guid.NewGuid():N}.staging";
        var staging = Path.Combine(parent, stagingName);
        var stopwatch = Stopwatch.StartNew();
        long bytesProcessed = 0;
        var entriesProcessed = 0;
        var extractable = plan.Entries
            .Where(entry => entry.IndexedEntry.Descriptor.Kind != ArchiveEntryKind.Directory)
            .ToArray();
        var totalBytes = TrySumSizes(extractable);
        var compressionRatio = ArchiveMetrics.TryCalculateCompressionRatio(
            extractable.Select(entry => entry.IndexedEntry.Descriptor));
        var compressedBytes = TrySumSizes(
            extractable,
            entry => entry.IndexedEntry.Descriptor.CompressedSize);
        var runtimeGuard = _resourceGuard.CreateRuntimeGuard(parent, compressedBytes);
        var provenanceAvailable = provenance is not null;

        string? stagingIdentity = null;
        HandleRelativeExtractionRoot? extractionRoot = null;
        var stagingCreated = false;
        var published = false;
        try
        {
            runtimeGuard.ValidateObjectCreation(
                bytesProcessed,
                ExtractionObjectKind.Directory);
            extractionRoot = HandleRelativeExtractionRoot.Create(parent, stagingName);
            stagingCreated = true;
            if (!string.Equals(parentIdentity, extractionRoot.ParentIdentity, StringComparison.Ordinal))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "destination.identity_changed",
                    "The extraction destination parent changed while its handle chain was being pinned.");
            }

            stagingIdentity = extractionRoot.StagingIdentity;
            if (recovery is not null)
            {
                await recovery
                    .RecordWritingAsync(stagingIdentity, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            foreach (var planned in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = Path.Combine(staging, planned.RelativePath);
                EnsureContained(staging, outputPath);
                if (outputPath.Length > _pathPolicy.MaximumFinalPathLength)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.UnsafePath,
                        "path.final_length",
                        "An extraction path exceeds the configured final path-length limit.");
                }

                if (planned.IndexedEntry.Descriptor.Kind == ArchiveEntryKind.Directory)
                {
                    runtimeGuard.ValidateObjectCreation(
                        bytesProcessed,
                        ExtractionObjectKind.Directory,
                        planned.RelativePath);
                    extractionRoot.EnsureDirectory(planned.RelativePath);
                    continue;
                }

                EnsureDirectoryIdentity(staging, stagingIdentity, "The extraction staging directory changed during the operation.");
                var descriptor = planned.IndexedEntry.Descriptor;
                runtimeGuard.ValidateObjectCreation(
                    bytesProcessed,
                    ExtractionObjectKind.File,
                    planned.RelativePath);
                await using (var outputFile = extractionRoot.CreateFile(planned.RelativePath))
                {
                    await using var guardedOutput = new RuntimeGuardWriteStream(
                        outputFile.Stream,
                        runtimeGuard,
                        bytesProcessed,
                        descriptor.OriginalSize,
                        (totalWritten, _) => reportProgress(CreateProgress(
                            totalWritten,
                            totalBytes,
                            entriesProcessed,
                            extractable.Length,
                            descriptor,
                            stopwatch.Elapsed,
                            compressionRatio)));
                    await archive.CopyEntryToAsync(
                        planned.IndexedEntry.ProviderEntry!.Ordinal,
                        guardedOutput,
                        cancellationToken).ConfigureAwait(false);
                    runtimeGuard.ValidateCompletedEntry(
                        guardedOutput.EntryBytesWritten,
                        descriptor.OriginalSize);
                    bytesProcessed = guardedOutput.TotalBytesWritten;
                    await guardedOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (descriptor.ModifiedTimeUtc is { } modified)
                    {
                        outputFile.SetLastWriteTimeUtc(modified);
                    }

                    await outputFile.CloseDataAsync().ConfigureAwait(false);
                    entriesProcessed++;

                    if (provenanceAvailable && provenance is not null)
                    {
                        runtimeGuard.ValidateObjectCreation(
                            bytesProcessed,
                            ExtractionObjectKind.ProvenanceStream,
                            planned.RelativePath);
                        if (!await outputFile
                            .TryApplyProvenanceAsync(provenance, cancellationToken)
                            .ConfigureAwait(false))
                        {
                            provenanceAvailable = false;
                            if (reportWarning is not null)
                            {
                                await reportWarning(new ArchiveWarning(
                                    "provenance.not_applied",
                                    "Windows download-security provenance could not be applied on the destination filesystem.",
                                    planned.IndexedEntry.Descriptor.EntryId)).ConfigureAwait(false);
                            }
                        }
                    }

                }

                await reportProgress(CreateProgress(
                    bytesProcessed,
                    totalBytes,
                    entriesProcessed,
                    extractable.Length,
                    planned.IndexedEntry.Descriptor,
                    stopwatch.Elapsed,
                    compressionRatio)).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (beforeCommit is not null)
            {
                await beforeCommit(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(parent);
            EnsureDirectoryIdentity(parent, parentIdentity, "The extraction destination parent changed during the operation.");
            EnsureDirectoryIdentity(staging, stagingIdentity, "The extraction staging directory changed during the operation.");
            var manifest = extractionRoot.ValidateKnownTree();
            EnsureTreeMatchesManifest(staging, manifest);
            extractionRoot.ValidateKnownTree();
            var recoveryEvidence = extractionRoot.CaptureExtractionRecoveryEvidence(
                CancellationToken.None);
            if (recovery is not null)
            {
                await recovery
                    .RecordFinalizedAsync(recoveryEvidence, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (FileSystemIdentityService.PathEntryExists(destination))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "destination.appeared_before_commit",
                    "The extraction destination appeared before the staged result could be committed.");
            }

            if (recovery is not null)
            {
                await recovery
                    .RecordCommitIntentAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            extractionRoot.CommitAs(Path.GetFileName(destination));
            published = true;
            if (recovery is not null)
            {
                await recovery
                    .RecordCommittedAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var completion = new ExtractionRecoveryCompletion(recovery);
                recovery = null;
                return completion;
            }

            return null;
        }
        catch (Exception exception)
        {
            extractionRoot?.Dispose();
            var stagingDeleted = false;
            if (!MustLeaveStagingForManualRecovery(exception))
            {
                stagingDeleted = DeleteOwnedStaging(staging, stagingIdentity);
            }

            if (recovery is not null)
            {
                if ((!stagingCreated || stagingDeleted) && !published)
                {
                    await recovery.DeleteAsync().ConfigureAwait(false);
                }
                else
                {
                    await recovery.DisposeAsync().ConfigureAwait(false);
                }

                recovery = null;
            }

            throw;
        }
        finally
        {
            extractionRoot?.Dispose();
            if (recovery is not null)
            {
                await recovery.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static long? TrySumSizes(IEnumerable<PlannedExtractionEntry> entries)
        => TrySumSizes(entries, entry => entry.IndexedEntry.Descriptor.OriginalSize);

    private static long? TrySumSizes(
        IEnumerable<PlannedExtractionEntry> entries,
        Func<PlannedExtractionEntry, long?> selectSize)
    {
        try
        {
            long sum = 0;
            foreach (var entry in entries)
            {
                if (selectSize(entry) is not { } size || size < 0)
                {
                    return null;
                }

                sum = checked(sum + size);
            }

            return sum;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static OperationProgress CreateProgress(
        long bytesProcessed,
        long? totalBytes,
        int entriesProcessed,
        int totalEntries,
        ArchiveEntryDescriptor current,
        TimeSpan elapsed,
        double? compressionRatio) =>
        new(
            bytesProcessed,
            totalBytes,
            entriesProcessed,
            totalEntries,
            current.EntryId,
            current.DisplayName,
            elapsed,
            elapsed.TotalSeconds > 0 ? bytesProcessed / elapsed.TotalSeconds : null,
            compressionRatio);

    private static void EnsureContained(string root, string candidate)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var canonicalCandidate = Path.GetFullPath(candidate);
        if (!canonicalCandidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("An extraction path escaped the staging root.");
        }
    }

    private static void EnsureDirectoryIdentity(string path, string expectedIdentity, string message)
    {
        var currentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(path);
        if (!string.Equals(expectedIdentity, currentIdentity, StringComparison.Ordinal))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "destination.identity_changed",
                message);
        }
    }

    private static bool DeleteOwnedStaging(string staging, string? expectedIdentity)
    {
        if (!Directory.Exists(staging))
        {
            return !FileSystemIdentityService.PathEntryExists(staging);
        }

        try
        {
            var attributes = File.GetAttributes(staging);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            if (expectedIdentity is null)
            {
                Directory.Delete(staging, recursive: false);
                return !FileSystemIdentityService.PathEntryExists(staging);
            }

            var currentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(staging);
            if (string.Equals(expectedIdentity, currentIdentity, StringComparison.Ordinal))
            {
                try
                {
                    EnsureTreeContainsNoReparsePoints(staging);
                }
                catch (ArchiveProviderException)
                {
                    // Leave suspicious staging in place. Recursive cleanup must never cross
                    // an attacker-inserted link, even if that means manual orphan recovery.
                    return false;
                }

                Directory.Delete(staging, recursive: true);
            }

            return !FileSystemIdentityService.PathEntryExists(staging);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort. Never recurse into a path whose identity cannot be proven.
            return false;
        }
    }

    private static bool MustLeaveStagingForManualRecovery(Exception exception) =>
        exception is ArchiveProviderException
        {
            Category: ArchiveErrorCategory.DestinationConflict,
            Code: "destination.identity_changed"
                or "destination.path_appeared"
                or "destination.path_replaced"
                or "destination.reparse_point_appeared"
                or "destination.reparse_point_encountered"
                or "destination.unexpected_path_appeared"
                or "destination.hard_link_appeared"
                or "destination.staging_shape_changed",
        };

    private static void EnsureTreeContainsNoReparsePoints(string root) =>
        EnsureTreeMatchesManifest(root, expected: null);

    private static void EnsureTreeMatchesManifest(
        string root,
        HandleRelativeExtractionRoot.StagingTreeManifest? expected)
    {
        var expectedDirectories = expected?.DirectoryPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedFiles = expected?.FilePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                child.Refresh();
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.DestinationConflict,
                        "destination.reparse_point_appeared",
                        "A reparse point appeared inside the operation-owned staging tree.");
                }

                if ((child.Attributes & FileAttributes.Directory) != 0)
                {
                    var relativePath = Path.GetRelativePath(root, child.FullName);
                    if (expectedDirectories is not null && !expectedDirectories.Contains(relativePath))
                    {
                        throw new ArchiveProviderException(
                            ArchiveErrorCategory.DestinationConflict,
                            "destination.unexpected_path_appeared",
                            "An unexpected directory appeared inside the operation-owned staging tree.");
                    }

                    seenDirectories.Add(relativePath);
                    pending.Push((DirectoryInfo)child);
                }
                else
                {
                    var relativePath = Path.GetRelativePath(root, child.FullName);
                    if (expectedFiles is not null && !expectedFiles.Contains(relativePath))
                    {
                        throw new ArchiveProviderException(
                            ArchiveErrorCategory.DestinationConflict,
                            "destination.unexpected_path_appeared",
                            "An unexpected file appeared inside the operation-owned staging tree.");
                    }

                    seenFiles.Add(relativePath);
                }
            }
        }

        if (expectedDirectories is not null
            && expectedFiles is not null
            && (!seenDirectories.SetEquals(expectedDirectories) || !seenFiles.SetEquals(expectedFiles)))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "destination.path_replaced",
                "An operation-owned staging path disappeared or changed type before commit.");
        }
    }
}

internal sealed class ExtractionRecoveryCompletion : IAsyncDisposable
{
    private ExtractionRecoveryStore.ExtractionRecoveryTransaction? _transaction;

    internal ExtractionRecoveryCompletion(
        ExtractionRecoveryStore.ExtractionRecoveryTransaction transaction)
    {
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    internal async ValueTask DeleteAsync()
    {
        var transaction = Interlocked.Exchange(ref _transaction, null);
        if (transaction is not null)
        {
            await transaction.DeleteAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var transaction = Interlocked.Exchange(ref _transaction, null);
        if (transaction is not null)
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}
