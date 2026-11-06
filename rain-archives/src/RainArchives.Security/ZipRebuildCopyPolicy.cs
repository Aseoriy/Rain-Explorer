using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public enum ZipRebuildCopyEntrySource
{
    RetainedArchive,
    NewFile,
    NewDirectory,
}

public sealed record PlannedZipRebuildCopyEntry(
    string ArchivePath,
    ArchiveEntryKind Kind,
    long Length,
    DateTimeOffset ZipLastWriteTime,
    int? WindowsAttributes,
    ZipRebuildCopyEntrySource Source,
    IndexedArchiveEntry? RetainedEntry = null,
    PlannedArchiveCreationFile? NewFile = null);

public sealed record ZipRebuildCopyPlan(
    string DestinationPath,
    IReadOnlyList<PlannedZipRebuildCopyEntry> Entries,
    IReadOnlyList<PlannedArchiveCreationFile> NewSourceFiles,
    long TotalSourceBytes,
    long DeclaredInputBytesForRatio,
    long MaximumExpectedOutputBytes,
    int ClampedTimestampCount)
{
    public int TotalEntries => Entries.Count;

    // Tree-edit additions are carried alongside the flattened output manifest so
    // the writer can revalidate the pinned source trees immediately before commit.
    public IReadOnlyList<PlannedArchiveCreationDirectory> NewSourceDirectories { get; init; } = [];

    public IReadOnlyList<PlannedArchiveCreationSourceTree> NewSourceTrees { get; init; } = [];
}

public sealed class ZipRebuildCopyPolicy
{
    private const long MetadataBudgetBytes = 64L * 1024 * 1024;
    private const int RepresentableFileAttributes =
        (int)(FileAttributes.ReadOnly
            | FileAttributes.Hidden
            | FileAttributes.System
            | FileAttributes.Archive
            | FileAttributes.Normal
            | FileAttributes.NotContentIndexed);
    private const int RepresentableDirectoryAttributes =
        RepresentableFileAttributes | (int)FileAttributes.Directory;
    private static readonly DateTime MinimumZipLocalTime =
        new(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime MaximumZipLocalTime =
        new(2107, 12, 31, 23, 59, 58, DateTimeKind.Unspecified);
    private readonly ArchiveCreationPolicy _creationPolicy;
    private readonly ExtractionPathPolicy _pathPolicy;

    public ZipRebuildCopyPolicy(
        ArchiveCreationPolicy? creationPolicy = null,
        ExtractionPathPolicy? pathPolicy = null)
    {
        _creationPolicy = creationPolicy ?? new ArchiveCreationPolicy();
        _pathPolicy = pathPolicy ?? new ExtractionPathPolicy();
    }

    public bool CanRebuild(IArchiveHandle handle, ArchiveIndex index)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(index);
        if (!string.Equals(handle.Format, "zip", StringComparison.OrdinalIgnoreCase)
            || handle.VolumeSetKind != ArchiveVolumeSetKind.Single
            || !handle.Capabilities.HasFlag(ArchiveCapability.Extract))
        {
            return false;
        }

        var materialEntries = index.Entries.Where(entry => !entry.IsSynthetic).ToArray();
        if (materialEntries.Length > ProtocolConstants.MaximumZipCreationEntries
            || materialEntries.Any(entry => !IsEligibleMaterialEntry(entry)))
        {
            return false;
        }

        try
        {
            long totalBytes = 0;
            foreach (var entry in materialEntries)
            {
                if (entry.Descriptor.Kind == ArchiveEntryKind.File)
                {
                    totalBytes = checked(totalBytes + entry.Descriptor.OriginalSize!.Value);
                }
            }

            return totalBytes <= ProtocolConstants.MaximumZipCreationSourceBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public ValueTask<ZipRebuildCopyPlan> PlanAsync(
        IArchiveHandle handle,
        ArchiveIndex index,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        string sourceArchivePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        PlanWithRenamesAsync(
            handle,
            index,
            entriesToDelete,
            files,
            Array.Empty<ArchiveRebuildRename>(),
            sourceArchivePath,
            destinationPath,
            cancellationToken);

    public async ValueTask<ZipRebuildCopyPlan> PlanWithRenamesAsync(
        IArchiveHandle handle,
        ArchiveIndex index,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        string sourceArchivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(entriesToDelete);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!CanRebuild(handle, index))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_source_not_eligible",
                "ZIP Rebuild Copy requires one safe, unencrypted, single-volume ZIP with complete entry metadata.");
        }

        if (entriesToDelete.Count > ProtocolConstants.MaximumZipCreationEntries
            || files.Count > ProtocolConstants.MaximumZipCreationEntries
            || renames.Count > ProtocolConstants.MaximumZipCreationEntries)
        {
            throw ResourceLimit(
                "zip.rebuild_edit_count_limit",
                $"A ZIP Rebuild Copy request can contain at most {ProtocolConstants.MaximumZipCreationEntries:N0} deletions, mapped files, or renames of each kind.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var materialEntries = index.Entries.Where(entry => !entry.IsSynthetic).ToArray();
        var sourcePathPlan = _pathPolicy.Plan(materialEntries, cancellationToken);
        if (!sourcePathPlan.IsSafe)
        {
            throw new ArchiveProviderException(
                sourcePathPlan.Issues.Any(issue => issue.Code.Contains("collision", StringComparison.Ordinal))
                    ? ArchiveErrorCategory.PathCollision
                    : ArchiveErrorCategory.UnsafePath,
                "zip.rebuild_source_paths_unsafe",
                "The opened ZIP contains unsafe, colliding, duplicate, or unsupported material paths and cannot be rebuilt safely.");
        }

        var byExactKey = new Dictionary<ArchiveEntryKey, PlannedExtractionEntry>();
        var byCollisionKey = new Dictionary<string, PlannedExtractionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sourcePathPlan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = entry.IndexedEntry.Descriptor;
            if (descriptor.NormalizedPath is not { Length: > 0 } normalizedPath
                || !byExactKey.TryAdd(
                    new ArchiveEntryKey(normalizedPath, descriptor.DuplicateOrdinal),
                    entry)
                || !byCollisionKey.TryAdd(entry.CollisionKey, entry))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.PathCollision,
                    "zip.rebuild_source_paths_ambiguous",
                    "The opened ZIP does not have one unambiguous persistent path key for every material entry.");
            }
        }

        var deletedEntryIds = new HashSet<EntryId>();
        foreach (var key in entriesToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key is null
                || string.IsNullOrWhiteSpace(key.NormalizedPath)
                || key.NormalizedPath.Length > ProtocolConstants.MaximumPathCharacters
                || key.DuplicateOrdinal < 0
                || !byExactKey.TryGetValue(key, out var existing)
                || !deletedEntryIds.Add(existing.IndexedEntry.Descriptor.EntryId))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    "zip.rebuild_delete_key_invalid",
                    "A ZIP Rebuild Copy deletion key is missing, duplicated, or does not belong to the exact opened session snapshot.");
            }
        }

        var renameByEntryId = new Dictionary<EntryId, (PlannedExtractionEntry Entry, string ArchivePath, string CollisionKey)>();
        var renameTargets = new Dictionary<string, EntryId>(StringComparer.OrdinalIgnoreCase);
        foreach (var rename in renames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rename is null
                || rename.EntryKey is null
                || string.IsNullOrWhiteSpace(rename.EntryKey.NormalizedPath)
                || rename.EntryKey.NormalizedPath.Length > ProtocolConstants.MaximumPathCharacters
                || rename.EntryKey.DuplicateOrdinal < 0
                || !byExactKey.TryGetValue(rename.EntryKey, out var existing)
                || existing.IndexedEntry.Descriptor.Kind != ArchiveEntryKind.File
                || deletedEntryIds.Contains(existing.IndexedEntry.Descriptor.EntryId)
                || !renameByEntryId.TryAdd(
                    existing.IndexedEntry.Descriptor.EntryId,
                    (existing, string.Empty, string.Empty)))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    "zip.rebuild_rename_key_invalid",
                    "A ZIP Rebuild Copy rename key is missing, duplicated, deleted, non-regular, or does not belong to the exact opened session snapshot.");
            }

            if (string.IsNullOrWhiteSpace(rename.FinalArchivePath)
                || rename.FinalArchivePath.Length > ProtocolConstants.MaximumPathCharacters)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsafePath,
                    "zip.rebuild_unsafe_archive_path",
                    "A ZIP Rebuild Copy target path is unsafe: it is empty or exceeds the maximum path length.");
            }

            var (archivePath, collisionKey) = NormalizeArchivePath(rename.FinalArchivePath);
            if (!renameTargets.TryAdd(collisionKey, existing.IndexedEntry.Descriptor.EntryId))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.PathCollision,
                    "zip.rebuild_rename_target_collision",
                    "Two ZIP Rebuild Copy renames target the same final path under Windows name rules.");
            }

            renameByEntryId[existing.IndexedEntry.Descriptor.EntryId] =
                (existing, archivePath, collisionKey);
        }

        var mappedPlan = await _creationPolicy
            .PlanAsync(
                files.Select(file => new ArchiveCreationFile(file.SourcePath, file.ArchivePath)).ToArray(),
                destinationPath,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var mapped in mappedPlan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = HandleRelativeExtractionRoot.OpenStableSourceFile(
                mapped.SourcePath,
                requireSingleLink: true);
            source.EnsureMatches(mapped.SourceSnapshot);
        }

        var canonicalSource = Path.GetFullPath(sourceArchivePath);
        if (string.Equals(
                canonicalSource,
                mappedPlan.DestinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "zip.rebuild_source_is_destination",
                "ZIP Rebuild Copy must publish to a new path and cannot target the opened source archive.");
        }

        var replacementIds = new HashSet<EntryId>();
        var output = new List<PlannedZipRebuildCopyEntry>(materialEntries.Length + mappedPlan.Files.Count);
        foreach (var mapped in mappedPlan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (archivePath, collisionKey) = NormalizeArchivePath(mapped.ArchivePath);
            if (byCollisionKey.TryGetValue(collisionKey, out var existing))
            {
                var descriptor = existing.IndexedEntry.Descriptor;
                if (descriptor.Kind != ArchiveEntryKind.File)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.PathCollision,
                        "zip.rebuild_directory_replacement_refused",
                        "A mapped rebuild file cannot replace a directory entry.");
                }

                if (deletedEntryIds.Contains(descriptor.EntryId)
                    || renameByEntryId.ContainsKey(descriptor.EntryId)
                    || !replacementIds.Add(descriptor.EntryId))
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.InvalidRequest,
                        "zip.rebuild_duplicate_edit_target",
                        "A ZIP Rebuild Copy target cannot be deleted, replaced, or mapped more than once.");
                }

                output.Add(new PlannedZipRebuildCopyEntry(
                    existing.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    ArchiveEntryKind.File,
                    mapped.SourceSnapshot.Length,
                    mapped.ZipLastWriteTime,
                    SanitizeAttributes(descriptor.WindowsAttributes, isDirectory: false),
                    ZipRebuildCopyEntrySource.NewFile,
                    NewFile: mapped));
                continue;
            }

            output.Add(new PlannedZipRebuildCopyEntry(
                archivePath,
                ArchiveEntryKind.File,
                mapped.SourceSnapshot.Length,
                mapped.ZipLastWriteTime,
                WindowsAttributes: null,
                ZipRebuildCopyEntrySource.NewFile,
                NewFile: mapped));
        }

        var clampedTimestamps = mappedPlan.ClampedTimestampCount;
        foreach (var existing in sourcePathPlan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = existing.IndexedEntry.Descriptor;
            if (deletedEntryIds.Contains(descriptor.EntryId)
                || replacementIds.Contains(descriptor.EntryId))
            {
                continue;
            }

            var timestamp = ClampZipTimestamp(
                descriptor.ModifiedTimeUtc,
                out var timestampClamped);
            if (timestampClamped)
            {
                clampedTimestamps++;
            }

            var archivePath = existing.RelativePath.Replace(Path.DirectorySeparatorChar, '/');
            if (renameByEntryId.TryGetValue(descriptor.EntryId, out var rename))
            {
                archivePath = rename.ArchivePath;
                if (byCollisionKey.TryGetValue(rename.CollisionKey, out var target)
                    && target.IndexedEntry.Descriptor.Kind == ArchiveEntryKind.Directory)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.PathCollision,
                        "zip.rebuild_rename_directory_target_refused",
                        "A retained ZIP Rebuild Copy file cannot be renamed onto a directory entry.");
                }
            }

            output.Add(new PlannedZipRebuildCopyEntry(
                archivePath,
                descriptor.Kind,
                descriptor.Kind == ArchiveEntryKind.File
                    ? descriptor.OriginalSize!.Value
                    : 0,
                timestamp,
                SanitizeAttributes(
                    descriptor.WindowsAttributes,
                    descriptor.Kind == ArchiveEntryKind.Directory),
                ZipRebuildCopyEntrySource.RetainedArchive,
                RetainedEntry: existing.IndexedEntry));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateFinalPaths(output, cancellationToken);
        output.Sort(static (left, right) =>
        {
            var path = StringComparer.Ordinal.Compare(left.ArchivePath, right.ArchivePath);
            return path != 0 ? path : left.Kind.CompareTo(right.Kind);
        });
        if (output.Count > ProtocolConstants.MaximumZipCreationEntries)
        {
            throw ResourceLimit(
                "zip.rebuild_entry_count_limit",
                $"A rebuilt ZIP can contain at most {ProtocolConstants.MaximumZipCreationEntries:N0} material entries.");
        }

        long totalBytes = 0;
        long ratioInputBytes = 0;
        long metadataBytes = 0;
        try
        {
            foreach (var entry in output)
            {
                metadataBytes = checked(metadataBytes + 256 + ((long)entry.ArchivePath.Length * sizeof(char)));
                if (entry.Kind != ArchiveEntryKind.File)
                {
                    continue;
                }

                totalBytes = checked(totalBytes + entry.Length);
                ratioInputBytes = checked(
                    ratioInputBytes
                    + (entry.Source == ZipRebuildCopyEntrySource.RetainedArchive
                        ? entry.RetainedEntry!.Descriptor.CompressedSize!.Value
                        : entry.Length));
            }
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "zip.rebuild_accounting_overflow",
                "ZIP Rebuild Copy entry accounting overflowed.",
                exception);
        }

        if (totalBytes > ProtocolConstants.MaximumZipCreationSourceBytes)
        {
            throw ResourceLimit(
                "zip.rebuild_source_byte_limit",
                $"A rebuilt ZIP can contain at most {ProtocolConstants.MaximumZipCreationSourceBytes:N0} decoded and new-source bytes.");
        }

        if (metadataBytes > MetadataBudgetBytes)
        {
            throw ResourceLimit(
                "zip.rebuild_metadata_budget",
                "ZIP Rebuild Copy metadata exceeds the bounded planning budget.");
        }

        return new ZipRebuildCopyPlan(
            mappedPlan.DestinationPath,
            output,
            mappedPlan.Files,
            totalBytes,
            ratioInputBytes,
            EstimateMaximumOutputBytes(output, totalBytes),
            clampedTimestamps);
    }

    private bool IsEligibleMaterialEntry(IndexedArchiveEntry entry)
    {
        var descriptor = entry.Descriptor;
        if (descriptor.SafetyStatus != EntrySafetyStatus.Safe
            || descriptor.IsEncrypted
            || descriptor.NormalizedPath is not { Length: > 0 }
            || descriptor.DuplicateOrdinal != 0
            || descriptor.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory)
            || entry.ProviderEntry is null)
        {
            return false;
        }

        if (descriptor.Kind == ArchiveEntryKind.File
            && (descriptor.OriginalSize is not (>= 0)
                || descriptor.CompressedSize is not (>= 0)))
        {
            return false;
        }

        var pathToValidate = descriptor.Kind == ArchiveEntryKind.Directory
            ? descriptor.NormalizedPath.TrimEnd('/')
            : descriptor.NormalizedPath;
        return _pathPolicy.TryNormalize(pathToValidate, out _, out _, out _);
    }

    private (string ArchivePath, string CollisionKey) NormalizeArchivePath(string archivePath)
    {
        if (!_pathPolicy.TryNormalize(
                archivePath,
                out var normalizedPath,
                out var collisionKey,
                out var reason))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "zip.rebuild_unsafe_archive_path",
                $"A ZIP Rebuild Copy target path is unsafe: {reason}");
        }

        return (normalizedPath.Replace(Path.DirectorySeparatorChar, '/'), collisionKey);
    }

    private void ValidateFinalPaths(
        IReadOnlyList<PlannedZipRebuildCopyEntry> entries,
        CancellationToken cancellationToken)
    {
        var providerEntries = entries.Select((entry, ordinal) => new ProviderArchiveEntry(
            ordinal,
            entry.Kind == ArchiveEntryKind.Directory ? $"{entry.ArchivePath}/" : entry.ArchivePath,
            entry.Kind,
            entry.Kind == ArchiveEntryKind.File ? entry.Length : null,
            CompressedSize: null,
            entry.ZipLastWriteTime,
            entry.WindowsAttributes,
            CompressionMethod: null,
            IntegrityValue: null,
            IsEncrypted: false,
            IsSolid: false,
            LinkTarget: null)).ToArray();
        var finalIndex = ArchiveIndex.Create(providerEntries, cancellationToken: cancellationToken);
        var finalPlan = _pathPolicy.Plan(finalIndex.Entries, cancellationToken);
        if (!finalPlan.IsSafe || finalPlan.Entries.Count != entries.Count)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.PathCollision,
                "zip.rebuild_output_path_collision",
                "The closed ZIP Rebuild Copy edit plan produces an unsafe, duplicate, case, Unicode-normalization, or file/directory-prefix collision.");
        }
    }

    private static int? SanitizeAttributes(int? attributes, bool isDirectory)
    {
        if (attributes is not { } value)
        {
            return isDirectory ? (int)FileAttributes.Directory : null;
        }

        var mask = isDirectory ? RepresentableDirectoryAttributes : RepresentableFileAttributes;
        var sanitized = value & mask;
        return isDirectory
            ? sanitized | (int)FileAttributes.Directory
            : sanitized & ~(int)FileAttributes.Directory;
    }

    private static DateTimeOffset ClampZipTimestamp(
        DateTimeOffset? timestamp,
        out bool wasClamped)
    {
        if (timestamp is null)
        {
            wasClamped = true;
            return new DateTimeOffset(
                MinimumZipLocalTime,
                TimeZoneInfo.Local.GetUtcOffset(MinimumZipLocalTime));
        }

        var local = timestamp.Value.ToLocalTime();
        if (local.DateTime < MinimumZipLocalTime)
        {
            wasClamped = true;
            return new DateTimeOffset(
                MinimumZipLocalTime,
                TimeZoneInfo.Local.GetUtcOffset(MinimumZipLocalTime));
        }

        if (local.DateTime > MaximumZipLocalTime)
        {
            wasClamped = true;
            return new DateTimeOffset(
                MaximumZipLocalTime,
                TimeZoneInfo.Local.GetUtcOffset(MaximumZipLocalTime));
        }

        wasClamped = false;
        return local;
    }

    private static long EstimateMaximumOutputBytes(
        IEnumerable<PlannedZipRebuildCopyEntry> entries,
        long sourceBytes)
    {
        try
        {
            long pathMetadata = 0;
            foreach (var entry in entries)
            {
                pathMetadata = checked(
                    pathMetadata + 512 + ((long)entry.ArchivePath.Length * 6));
            }

            return checked(sourceBytes + (sourceBytes / 8) + pathMetadata + (1024L * 1024));
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "zip.rebuild_output_estimate_overflow",
                "The ZIP Rebuild Copy output estimate overflowed safe accounting limits.",
                exception);
        }
    }

    private static ArchiveProviderException ResourceLimit(
        string code,
        string message,
        Exception? innerException = null) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message, innerException);
}
