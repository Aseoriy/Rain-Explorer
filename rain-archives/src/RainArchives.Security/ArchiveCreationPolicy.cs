using System.ComponentModel;
using System.Text;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public enum ArchiveCreationOutputFormat
{
    Zip,
    Tar,
    TarGZip,
    TarBZip2,
    GZip,
    BZip2,
    Rar,
}

public sealed record PlannedArchiveCreationFile(
    string SourcePath,
    string ArchivePath,
    OpenFileSnapshot SourceSnapshot,
    DateTimeOffset ZipLastWriteTime,
    bool TimestampWasClamped);

public sealed record PlannedArchiveCreationDirectory(
    string SourcePath,
    string ArchivePath,
    OpenDirectorySnapshot SourceSnapshot,
    DateTimeOffset ZipLastWriteTime,
    bool TimestampWasClamped);

public sealed record ArchiveCreationTreeManifestEntry(
    string RelativePath,
    ArchiveEntryKind Kind,
    string Identity,
    long? Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed record PlannedArchiveCreationSourceTree(
    string SourcePath,
    string ArchivePath,
    IReadOnlyList<ArchiveCreationTreeManifestEntry> Manifest);

public sealed record ArchiveCreationPlan(
    string DestinationPath,
    IReadOnlyList<PlannedArchiveCreationFile> Files,
    long TotalSourceBytes,
    long MaximumExpectedOutputBytes,
    int ClampedTimestampCount)
{
    public IReadOnlyList<PlannedArchiveCreationDirectory> Directories { get; init; } = [];

    public IReadOnlyList<PlannedArchiveCreationSourceTree> SourceTrees { get; init; } = [];

    public ArchiveCreationOutputFormat OutputFormat { get; init; } =
        ArchiveCreationOutputFormat.Zip;

    public int TotalEntries => checked(Files.Count + Directories.Count);
}

public sealed class ArchiveCreationPolicy
{
    private const long MetadataBudgetBytes = 64L * 1024 * 1024;
    private static readonly DateTime MinimumZipLocalTime =
        new(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime MaximumZipLocalTime =
        new(2107, 12, 31, 23, 59, 58, DateTimeKind.Unspecified);
    private static readonly CreationLimits ZipLimits = new(
        ProtocolConstants.MaximumZipCreationFiles,
        ProtocolConstants.MaximumZipCreationEntries,
        ProtocolConstants.MaximumZipCreationSourceBytes);
    private static readonly CreationLimits TarLimits = new(
        ProtocolConstants.MaximumTarCreationFiles,
        ProtocolConstants.MaximumTarCreationEntries,
        ProtocolConstants.MaximumTarCreationSourceBytes);
    private static readonly CreationLimits RawCompressionLimits = new(
        MaximumExplicitFiles: ProtocolConstants.MaximumRawCreationEntries,
        MaximumEntries: ProtocolConstants.MaximumRawCreationEntries,
        ProtocolConstants.MaximumRawCreationSourceBytes);
    private static readonly CreationLimits RarLimits = new(
        ProtocolConstants.MaximumRarCreationFiles,
        ProtocolConstants.MaximumRarCreationEntries,
        ProtocolConstants.MaximumRarCreationSourceBytes);
    private readonly ExtractionPathPolicy _pathPolicy;
    private readonly int _maximumTreeEntries;

    public ArchiveCreationPolicy(
        ExtractionPathPolicy? pathPolicy = null,
        int maximumTreeEntries = ProtocolConstants.MaximumZipCreationEntries)
    {
        if (maximumTreeEntries is not ProtocolConstants.MaximumZipCreationEntries
            and not ProtocolConstants.MaximumExpandedCreationTreeEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTreeEntries));
        }
        _pathPolicy = pathPolicy ?? new ExtractionPathPolicy();
        _maximumTreeEntries = maximumTreeEntries;
    }

    public async ValueTask<ArchiveCreationPlan> PlanAsync(
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        CancellationToken cancellationToken) =>
        await PlanAsync(
            files,
            destinationPath,
            ArchiveCreationOutputFormat.Zip,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<ArchiveCreationPlan> PlanAsync(
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        ArchiveCreationOutputFormat outputFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (IsRawCompression(outputFormat))
        {
            files = RawArchiveEntryName.Rewrite(
                files,
                destinationPath,
                outputFormat == ArchiveCreationOutputFormat.GZip
                    ? [".gz", ".gzip"]
                    : [".bz2", ".bzip2"]);
        }
        var limits = GetLimits(outputFormat);
        if (IsRawCompression(outputFormat) && files.Count != 1)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "creation.raw_single_source_required",
                "Raw compressed creation requires exactly one ordinary source file.");
        }
        if (files.Count > limits.MaximumExplicitFiles)
        {
            throw ResourceLimit(
                "creation.file_count_limit",
                $"An archive creation request can contain at most {limits.MaximumExplicitFiles:N0} explicit files.");
        }

        var canonicalDestination = ValidateFilesystemFilePath(
            destinationPath,
            "creation.unsafe_destination_path",
            "The archive destination must be an ordinary absolute Windows file path without device syntax or ambiguous components.");
        var planned = new List<PlannedArchiveCreationFile>(files.Count);
        var claimedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        long metadataBytes = 0;
        var clampedTimestamps = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null
                || string.IsNullOrWhiteSpace(file.SourcePath)
                || string.IsNullOrWhiteSpace(file.ArchivePath)
                || file.SourcePath.Length > ProtocolConstants.MaximumPathCharacters
                || file.ArchivePath.Length > ProtocolConstants.MaximumPathCharacters)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    "creation.invalid_file",
                    "An archive creation file contains an empty or oversized source/archive path.");
            }

            var sourcePath = ValidateFilesystemFilePath(
                file.SourcePath,
                "creation.unsafe_source_path",
                "Archive creation sources must be ordinary absolute Windows file paths without device, ADS, or ambiguous syntax.");
            if (string.Equals(sourcePath, canonicalDestination, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "creation.source_is_destination",
                    "An archive creation source cannot also be the destination archive.");
            }

            if (!_pathPolicy.TryNormalize(
                    file.ArchivePath,
                    out var normalizedPath,
                    out var collisionKey,
                    out var reason))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsafePath,
                    "creation.unsafe_archive_path",
                    $"An archive entry path is unsafe: {reason}");
            }

            var archivePath = normalizedPath.Replace(Path.DirectorySeparatorChar, '/');
            var parentKeys = EnumerateParentKeys(collisionKey).ToArray();
            var archiveComponents = archivePath.Split('/');
            var parentSpelling = string.Empty;
            for (var index = 0; index < parentKeys.Length; index++)
            {
                parentSpelling = parentSpelling.Length == 0
                    ? archiveComponents[index]
                    : $"{parentSpelling}/{archiveComponents[index]}";
                if (parentSpellings.TryGetValue(parentKeys[index], out var existingSpelling)
                    && !string.Equals(existingSpelling, parentSpelling, StringComparison.Ordinal))
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.PathCollision,
                        "creation.archive_path_collision",
                        "Archive entry parent paths have conflicting spellings under Windows case or Unicode normalization rules.");
                }
            }

            if (claimedFiles.Contains(collisionKey)
                || claimedDirectories.Contains(collisionKey)
                || parentKeys.Any(claimedFiles.Contains))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.PathCollision,
                    "creation.archive_path_collision",
                    "Archive entry paths collide after Windows case and Unicode normalization.");
            }

            claimedFiles.Add(collisionKey);
            foreach (var parent in parentKeys)
            {
                claimedDirectories.Add(parent);
            }

            parentSpelling = string.Empty;
            for (var index = 0; index < parentKeys.Length; index++)
            {
                parentSpelling = parentSpelling.Length == 0
                    ? archiveComponents[index]
                    : $"{parentSpelling}/{archiveComponents[index]}";
                parentSpellings.TryAdd(parentKeys[index], parentSpelling);
            }

            await using var source = HandleRelativeExtractionRoot.OpenStableSourceFile(sourcePath);
            var snapshot = source.Snapshot;
            try
            {
                totalBytes = checked(totalBytes + snapshot.Length);
                metadataBytes = checked(
                    metadataBytes
                    + 256
                    + ((long)sourcePath.Length * sizeof(char))
                    + ((long)archivePath.Length * sizeof(char)));
            }
            catch (OverflowException exception)
            {
                throw ResourceLimit(
                    "creation.metadata_overflow",
                    "Archive creation source accounting overflowed.",
                    exception);
            }

            if (totalBytes > limits.MaximumSourceBytes)
            {
                throw ResourceLimit(
                    "creation.source_byte_limit",
                    $"Archive creation sources can total at most {limits.MaximumSourceBytes:N0} bytes at this gate.");
            }

            if (metadataBytes > MetadataBudgetBytes)
            {
                throw ResourceLimit(
                    "creation.metadata_budget",
                    "Archive creation source metadata exceeds the bounded planning budget.");
            }

            var zipTime = ClampZipTimestamp(snapshot.LastWriteTimeUtc, out var wasClamped);
            if (wasClamped)
            {
                clampedTimestamps++;
            }

            planned.Add(new PlannedArchiveCreationFile(
                sourcePath,
                archivePath,
                snapshot,
                zipTime,
                wasClamped));
        }

        return new ArchiveCreationPlan(
            canonicalDestination,
            planned,
            totalBytes,
            EstimateMaximumOutputBytes(outputFormat, planned, totalBytes),
            clampedTimestamps)
        {
            OutputFormat = outputFormat,
        };
    }

    public async ValueTask<ArchiveCreationPlan> PlanTreeAsync(
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        string destinationPath,
        CancellationToken cancellationToken) =>
        await PlanTreeAsync(
            files,
            directories,
            destinationPath,
            ArchiveCreationOutputFormat.Zip,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<ArchiveCreationPlan> PlanTreeAsync(
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        string destinationPath,
        ArchiveCreationOutputFormat outputFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);
        var limits = GetTreeLimits(outputFormat);
        if (IsRawCompression(outputFormat))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "creation.raw_tree_unsupported",
                "Raw compressed creation does not support directory trees.");
        }
        if (directories.Count == 0)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "creation.directory_required",
                "An archive tree creation request must contain at least one directory source.");
        }

        int requestedObjects;
        try
        {
            requestedObjects = checked(files.Count + directories.Count);
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "creation.entry_count_limit",
                "Archive creation input accounting overflowed.",
                exception);
        }

        if (requestedObjects > limits.MaximumExplicitFiles)
        {
            throw ResourceLimit(
                "creation.entry_count_limit",
                $"An archive creation request can contain at most {limits.MaximumExplicitFiles:N0} explicit file and directory mappings.");
        }

        var basePlan = await PlanAsync(files, destinationPath, outputFormat, cancellationToken)
            .ConfigureAwait(false);
        var claims = new ArchivePathClaims();
        foreach (var file in basePlan.Files)
        {
            var normalized = NormalizeArchivePath(file.ArchivePath);
            claims.Claim(normalized.ArchivePath, normalized.CollisionKey, isDirectory: false);
        }

        var roots = new List<DirectoryRootDefinition>(directories.Count);
        var rootIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory is null
                || string.IsNullOrWhiteSpace(directory.SourcePath)
                || string.IsNullOrWhiteSpace(directory.ArchivePath)
                || directory.SourcePath.Length > ProtocolConstants.MaximumPathCharacters
                || directory.ArchivePath.Length > ProtocolConstants.MaximumPathCharacters)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    "creation.invalid_directory",
                    "An archive creation directory contains an empty or oversized source/archive path.");
            }

            var sourcePath = ValidateFilesystemFilePath(
                directory.SourcePath,
                "creation.unsafe_source_path",
                "Archive creation directory sources must be ordinary absolute Windows paths without device, ADS, or ambiguous syntax.");
            var normalized = NormalizeArchivePath(directory.ArchivePath);
            using var source = HandleRelativeExtractionRoot.OpenStableSourceDirectory(sourcePath);
            if (!rootIdentities.Add(source.Snapshot.Identity))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    "creation.overlapping_source_directories",
                    "An archive tree creation request cannot include the same source directory more than once.");
            }

            roots.Add(new DirectoryRootDefinition(
                source.SourcePath,
                normalized.ArchivePath,
                source.Snapshot));
        }

        for (var left = 0; left < roots.Count; left++)
        {
            if (IsSameOrDescendant(roots[left].SourcePath, basePlan.DestinationPath))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.DestinationConflict,
                    "creation.destination_within_source_directory",
                    "The destination archive cannot be created inside a selected source directory tree.");
            }

            foreach (var file in basePlan.Files)
            {
                if (IsSameOrDescendant(roots[left].SourcePath, file.SourcePath))
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.InvalidRequest,
                        "creation.explicit_file_within_directory",
                        "An explicit archive source file cannot also be contained by a selected source directory tree.");
                }
            }

            for (var right = left + 1; right < roots.Count; right++)
            {
                if (IsSameOrDescendant(roots[left].SourcePath, roots[right].SourcePath)
                    || IsSameOrDescendant(roots[right].SourcePath, roots[left].SourcePath))
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.InvalidRequest,
                        "creation.overlapping_source_directories",
                        "Archive directory sources cannot overlap or contain one another.");
                }
            }
        }

        var plannedFiles = new List<PlannedArchiveCreationFile>(basePlan.Files);
        var plannedDirectories = new List<PlannedArchiveCreationDirectory>();
        var sourceTrees = new List<PlannedArchiveCreationSourceTree>(roots.Count);
        long totalBytes = basePlan.TotalSourceBytes;
        long metadataBytes = 0;
        foreach (var file in basePlan.Files)
        {
            AddMetadataBytes(ref metadataBytes, PlannedEntryMetadataBytes(file.SourcePath, file.ArchivePath));
        }
        var clampedTimestamps = basePlan.ClampedTimestampCount;
        foreach (var root in roots.OrderBy(item => item.ArchivePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = new List<ArchiveCreationTreeManifestEntry>();
            AddMetadataBytes(ref metadataBytes, PlannedEntryMetadataBytes(root.SourcePath, root.ArchivePath));
            using var source = HandleRelativeExtractionRoot.OpenStableSourceDirectory(root.SourcePath);
            source.EnsureMatches(root.Snapshot);
            CaptureSourceTree(
                source,
                relativePath: string.Empty,
                root.ArchivePath,
                claims,
                plannedFiles,
                plannedDirectories,
                manifest,
                ref totalBytes,
                ref clampedTimestamps,
                ref metadataBytes,
                limits,
                cancellationToken);
            manifest.Sort(CompareManifestEntries);
            sourceTrees.Add(new PlannedArchiveCreationSourceTree(
                root.SourcePath,
                root.ArchivePath,
                manifest));
        }

        plannedFiles.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.ArchivePath, right.ArchivePath));
        plannedDirectories.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.ArchivePath, right.ArchivePath));
        ValidateCombinedMetadataBudget(plannedFiles, plannedDirectories, sourceTrees);
        return basePlan with
        {
            Files = plannedFiles,
            Directories = plannedDirectories,
            SourceTrees = sourceTrees,
            TotalSourceBytes = totalBytes,
            MaximumExpectedOutputBytes = EstimateMaximumOutputBytes(
                outputFormat,
                plannedFiles,
                totalBytes,
                plannedDirectories),
            ClampedTimestampCount = clampedTimestamps,
        };
    }

    public ValueTask ValidateSourceTreesAsync(
        ArchiveCreationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var limits = GetTreeLimits(plan.OutputFormat);
        var materializedEntries = plan.TotalEntries - plan.SourceTrees.Sum(tree => tree.Manifest.Count);
        long metadataBytes = 0;
        foreach (var expectedTree in plan.SourceTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var source = HandleRelativeExtractionRoot.OpenStableSourceDirectory(
                    expectedTree.SourcePath);
                var actual = new List<ArchiveCreationTreeManifestEntry>();
                AddMetadataBytes(ref metadataBytes,
                    PlannedEntryMetadataBytes(expectedTree.SourcePath, expectedTree.ArchivePath));
                CaptureSourceTreeManifest(
                    source,
                    relativePath: string.Empty,
                    expectedTree.ArchivePath,
                    actual,
                    ref materializedEntries,
                    ref metadataBytes,
                    limits,
                    cancellationToken);
                actual.Sort(CompareManifestEntries);
                if (!actual.SequenceEqual(expectedTree.Manifest))
                {
                    throw SourceTreeChanged();
                }
            }
            catch (Exception exception) when (
                exception is Win32Exception
                    or IOException
                    or UnauthorizedAccessException)
            {
                throw SourceTreeChanged(exception);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static ArchiveProviderException SourceTreeChanged(Exception? innerException = null) =>
        new(
            ArchiveErrorCategory.SourceChanged,
            "source.directory_tree_changed",
            "A source directory tree changed after archive creation planning.",
            innerException);

    private void CaptureSourceTree(
        HandleRelativeExtractionRoot.HandleRelativeSourceDirectory source,
        string relativePath,
        string archivePath,
        ArchivePathClaims claims,
        List<PlannedArchiveCreationFile> plannedFiles,
        List<PlannedArchiveCreationDirectory> plannedDirectories,
        List<ArchiveCreationTreeManifestEntry> manifest,
        ref long totalBytes,
        ref int clampedTimestamps,
        ref long metadataBytes,
        CreationLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureEntryCapacity(
            checked(plannedFiles.Count + plannedDirectories.Count),
            limits.MaximumEntries);
        var normalized = NormalizeArchivePath(archivePath);
        claims.Claim(normalized.ArchivePath, normalized.CollisionKey, isDirectory: true);
        var directoryTime = ClampZipTimestamp(source.Snapshot.LastWriteTimeUtc, out var wasClamped);
        if (wasClamped)
        {
            clampedTimestamps++;
        }

        var directoryManifest = CreateDirectoryManifestEntry(relativePath, source.Snapshot);
        AddTreeEntryMetadata(ref metadataBytes, source.SourcePath, normalized.ArchivePath, directoryManifest);
        plannedDirectories.Add(new PlannedArchiveCreationDirectory(
            source.SourcePath,
            normalized.ArchivePath,
            source.Snapshot,
            directoryTime,
            wasClamped));
        manifest.Add(directoryManifest);

        var remaining = limits.MaximumEntries
            - checked(plannedFiles.Count + plannedDirectories.Count);
        var children = source.EnumerateChildren(remaining, cancellationToken);
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsafePath,
                    "source.reparse_point",
                    "Archive creation source trees cannot contain reparse points.");
            }

            var childRelativePath = relativePath.Length == 0
                ? child.Name
                : $"{relativePath}/{child.Name}";
            var childArchivePath = $"{normalized.ArchivePath}/{child.Name}";
            if (checked(source.SourcePath.Length + 1 + child.Name.Length)
                > ProtocolConstants.MaximumPathCharacters)
            {
                throw ResourceLimit(
                    "creation.source_path_limit",
                    "An expanded source-tree path exceeds the protocol path limit.");
            }

            if ((child.Attributes & FileAttributes.Directory) != 0)
            {
                using var childDirectory = source.OpenChildDirectory(child.Name, child.FileId);
                CaptureSourceTree(
                    childDirectory,
                    childRelativePath,
                    childArchivePath,
                    claims,
                    plannedFiles,
                    plannedDirectories,
                    manifest,
                    ref totalBytes,
                    ref clampedTimestamps,
                    ref metadataBytes,
                    limits,
                    cancellationToken);
                continue;
            }

            EnsureEntryCapacity(
                checked(plannedFiles.Count + plannedDirectories.Count),
                limits.MaximumEntries);
            var normalizedFile = NormalizeArchivePath(childArchivePath);
            claims.Claim(
                normalizedFile.ArchivePath,
                normalizedFile.CollisionKey,
                isDirectory: false);
            using var childFile = source.OpenChildFile(child.Name, child.FileId);
            var fileSnapshot = childFile.Snapshot;
            AddSourceBytes(ref totalBytes, fileSnapshot.Length, limits.MaximumSourceBytes);
            var fileTime = ClampZipTimestamp(fileSnapshot.LastWriteTimeUtc, out wasClamped);
            if (wasClamped)
            {
                clampedTimestamps++;
            }

            var fileManifest = CreateFileManifestEntry(childRelativePath, fileSnapshot);
            AddTreeEntryMetadata(ref metadataBytes, childFile.SourcePath, normalizedFile.ArchivePath, fileManifest);
            plannedFiles.Add(new PlannedArchiveCreationFile(
                childFile.SourcePath,
                normalizedFile.ArchivePath,
                fileSnapshot,
                fileTime,
                wasClamped));
            manifest.Add(fileManifest);
        }

        source.EnsureMatches(source.Snapshot);
    }

    private void CaptureSourceTreeManifest(
        HandleRelativeExtractionRoot.HandleRelativeSourceDirectory source,
        string relativePath,
        string archiveRootPath,
        List<ArchiveCreationTreeManifestEntry> manifest,
        ref int materializedEntries,
        ref long metadataBytes,
        CreationLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureEntryCapacity(materializedEntries, limits.MaximumEntries);
        materializedEntries++;
        var archivePath = relativePath.Length == 0 ? archiveRootPath : $"{archiveRootPath}/{relativePath}";
        var directoryManifest = CreateDirectoryManifestEntry(relativePath, source.Snapshot);
        AddTreeEntryMetadata(ref metadataBytes, source.SourcePath, archivePath, directoryManifest);
        manifest.Add(directoryManifest);
        var children = source.EnumerateChildren(
            limits.MaximumEntries - materializedEntries,
            cancellationToken);
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsafePath,
                    "source.reparse_point",
                    "Archive creation source trees cannot contain reparse points.");
            }

            var childRelativePath = relativePath.Length == 0
                ? child.Name
                : $"{relativePath}/{child.Name}";
            _ = NormalizeArchivePath($"{archiveRootPath}/{childRelativePath}");
            if ((child.Attributes & FileAttributes.Directory) != 0)
            {
                using var childDirectory = source.OpenChildDirectory(child.Name, child.FileId);
                CaptureSourceTreeManifest(
                    childDirectory,
                    childRelativePath,
                    archiveRootPath,
                    manifest,
                    ref materializedEntries,
                    ref metadataBytes,
                    limits,
                    cancellationToken);
                continue;
            }

            EnsureEntryCapacity(materializedEntries, limits.MaximumEntries);
            materializedEntries++;
            using var childFile = source.OpenChildFile(child.Name, child.FileId);
            var fileManifest = CreateFileManifestEntry(childRelativePath, childFile.Snapshot);
            AddTreeEntryMetadata(ref metadataBytes, childFile.SourcePath,
                $"{archiveRootPath}/{childRelativePath}", fileManifest);
            manifest.Add(fileManifest);
        }

        source.EnsureMatches(source.Snapshot);
    }

    private (string ArchivePath, string CollisionKey) NormalizeArchivePath(string archivePath)
    {
        if (archivePath.Length > ProtocolConstants.MaximumPathCharacters)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "creation.unsafe_archive_path",
                "An archive entry path exceeds the protocol path limit.");
        }

        if (!_pathPolicy.TryNormalize(
                archivePath,
                out var normalizedPath,
                out var collisionKey,
                out var reason))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "creation.unsafe_archive_path",
                $"An archive entry path is unsafe: {reason}");
        }

        return (
            normalizedPath.Replace(Path.DirectorySeparatorChar, '/'),
            collisionKey);
    }

    private static ArchiveCreationTreeManifestEntry CreateDirectoryManifestEntry(
        string relativePath,
        OpenDirectorySnapshot snapshot) =>
        new(
            relativePath,
            ArchiveEntryKind.Directory,
            snapshot.Identity,
            Length: null,
            snapshot.LastWriteTimeUtc);

    private static ArchiveCreationTreeManifestEntry CreateFileManifestEntry(
        string relativePath,
        OpenFileSnapshot snapshot) =>
        new(
            relativePath,
            ArchiveEntryKind.File,
            snapshot.Identity
                ?? throw new IOException("Windows did not return a stable source-file identity."),
            snapshot.Length,
            snapshot.LastWriteTimeUtc);

    private static int CompareManifestEntries(
        ArchiveCreationTreeManifestEntry left,
        ArchiveCreationTreeManifestEntry right)
    {
        var path = StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        return path != 0 ? path : left.Kind.CompareTo(right.Kind);
    }

    private static void AddSourceBytes(
        ref long totalBytes,
        long sourceBytes,
        long maximumSourceBytes)
    {
        try
        {
            totalBytes = checked(totalBytes + sourceBytes);
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "creation.source_byte_limit",
                "Archive creation source-byte accounting overflowed.",
                exception);
        }

        if (totalBytes > maximumSourceBytes)
        {
            throw ResourceLimit(
                "creation.source_byte_limit",
                $"Archive creation sources can total at most {maximumSourceBytes:N0} bytes at this gate.");
        }
    }

    internal static void EnsureEntryCapacity(int currentCount, int maximumEntries)
    {
        if (currentCount >= maximumEntries)
        {
            throw ResourceLimit(
                "creation.entry_count_limit",
                $"An archive creation request can materialize at most {maximumEntries:N0} entries.");
        }
    }

    private static bool IsSameOrDescendant(string rootPath, string candidatePath)
    {
        var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = candidatePath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            || (candidate.Length > root.Length
                && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && IsDirectorySeparator(candidate[root.Length]));
    }

    private static long PlannedEntryMetadataBytes(string sourcePath, string archivePath) =>
        256L + ((long)sourcePath.Length + archivePath.Length) * sizeof(char);

    private static void AddTreeEntryMetadata(
        ref long metadataBytes,
        string sourcePath,
        string archivePath,
        ArchiveCreationTreeManifestEntry entry) =>
        AddMetadataBytes(ref metadataBytes,
            PlannedEntryMetadataBytes(sourcePath, archivePath)
            + 192L + ((long)entry.RelativePath.Length + entry.Identity.Length) * sizeof(char));

    internal static void AddMetadataBytes(ref long metadataBytes, long additionalBytes)
    {
        if (additionalBytes < 0 || metadataBytes < 0
            || additionalBytes > MetadataBudgetBytes - metadataBytes)
        {
            throw ResourceLimit("creation.metadata_budget",
                "Archive creation source metadata exceeds the bounded planning budget.");
        }
        metadataBytes += additionalBytes;
    }

    private static void ValidateCombinedMetadataBudget(
        IEnumerable<PlannedArchiveCreationFile> files,
        IEnumerable<PlannedArchiveCreationDirectory> directories,
        IEnumerable<PlannedArchiveCreationSourceTree> sourceTrees)
    {
        try
        {
            long metadataBytes = 0;
            foreach (var file in files)
            {
                metadataBytes = checked(
                    metadataBytes
                    + 256
                    + ((long)file.SourcePath.Length * sizeof(char))
                    + ((long)file.ArchivePath.Length * sizeof(char)));
            }

            foreach (var directory in directories)
            {
                metadataBytes = checked(
                    metadataBytes
                    + 256
                    + ((long)directory.SourcePath.Length * sizeof(char))
                    + ((long)directory.ArchivePath.Length * sizeof(char)));
            }

            foreach (var tree in sourceTrees)
            {
                metadataBytes = checked(
                    metadataBytes
                    + 256
                    + ((long)tree.SourcePath.Length * sizeof(char))
                    + ((long)tree.ArchivePath.Length * sizeof(char)));
                foreach (var entry in tree.Manifest)
                {
                    metadataBytes = checked(
                        metadataBytes
                        + 192
                        + ((long)entry.RelativePath.Length * sizeof(char))
                        + ((long)entry.Identity.Length * sizeof(char)));
                }
            }

            if (metadataBytes > MetadataBudgetBytes)
            {
                throw ResourceLimit(
                    "creation.metadata_budget",
                    "Archive creation source metadata exceeds the bounded planning budget.");
            }
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "creation.metadata_overflow",
                "Archive creation source metadata accounting overflowed.",
                exception);
        }
    }

    private string ValidateFilesystemFilePath(
        string path,
        string code,
        string message)
    {
        if (path.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith("//?/", StringComparison.Ordinal)
            || path.StartsWith("//./", StringComparison.Ordinal))
        {
            throw UnsafePath(code, message);
        }

        if (!TryGetRelativeFilePath(path, out var requestedRelativePath)
            || !_pathPolicy.TryNormalize(
                requestedRelativePath,
                out _,
                out _,
                out _))
        {
            throw UnsafePath(code, message);
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw UnsafePath(code, message, exception);
        }

        if (canonicalPath.Length > ProtocolConstants.MaximumPathCharacters
            || !TryGetRelativeFilePath(canonicalPath, out var relativePath))
        {
            throw UnsafePath(code, message);
        }

        if (!_pathPolicy.TryNormalize(relativePath, out _, out _, out _))
        {
            throw UnsafePath(code, message);
        }

        return canonicalPath;
    }

    private static bool TryGetRelativeFilePath(string path, out string relativePath)
    {
        relativePath = string.Empty;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var offset = root.Length;
        if (!IsDirectorySeparator(root[^1]))
        {
            if (offset >= path.Length || !IsDirectorySeparator(path[offset]))
            {
                return false;
            }

            offset++;
        }

        if (offset >= path.Length || IsDirectorySeparator(path[offset]))
        {
            return false;
        }

        relativePath = path[offset..];
        return true;
    }

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    private static IEnumerable<string> EnumerateParentKeys(string collisionKey)
    {
        var offset = 0;
        while (true)
        {
            var separator = collisionKey.IndexOf('/', offset);
            if (separator < 0)
            {
                yield break;
            }

            yield return collisionKey[..separator];
            offset = separator + 1;
        }
    }

    private static DateTimeOffset ClampZipTimestamp(
        DateTimeOffset timestamp,
        out bool wasClamped)
    {
        var local = timestamp.ToLocalTime();
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
        ArchiveCreationOutputFormat outputFormat,
        IReadOnlyList<PlannedArchiveCreationFile> files,
        long sourceBytes,
        IReadOnlyList<PlannedArchiveCreationDirectory>? directories = null) =>
        outputFormat switch
        {
            ArchiveCreationOutputFormat.Zip => EstimateMaximumZipOutputBytes(
                files,
                sourceBytes,
                directories),
            ArchiveCreationOutputFormat.Tar => EstimateMaximumTarOutputBytes(
                files,
                directories ?? []),
            ArchiveCreationOutputFormat.TarGZip or ArchiveCreationOutputFormat.TarBZip2 =>
                EstimateMaximumCompressedTarOutputBytes(
                    files,
                    sourceBytes,
                    directories ?? []),
            ArchiveCreationOutputFormat.GZip or ArchiveCreationOutputFormat.BZip2 =>
                EstimateMaximumRawCompressedOutputBytes(files, sourceBytes),
            ArchiveCreationOutputFormat.Rar => EstimateMaximumRarOutputBytes(
                files,
                sourceBytes,
                directories),
            _ => throw new ArgumentOutOfRangeException(nameof(outputFormat)),
        };

    private static long EstimateMaximumRarOutputBytes(
        IEnumerable<PlannedArchiveCreationFile> files,
        long sourceBytes,
        IEnumerable<PlannedArchiveCreationDirectory>? directories)
    {
        // The external encoder is required to fall back to stored data rather than
        // expand incompressible input. Keep a separate conservative allowance for
        // RAR5 headers, service records, Unicode names, and the recovery-free footer.
        try
        {
            long metadata = 1024L * 1024;
            foreach (var file in files)
            {
                metadata = checked(metadata + 4096 + ((long)file.ArchivePath.Length * 8));
            }

            foreach (var directory in directories ?? [])
            {
                metadata = checked(metadata + 4096 + ((long)directory.ArchivePath.Length * 8));
            }

            return checked(sourceBytes + (sourceBytes / 8) + metadata);
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "creation.output_estimate_overflow",
                "The RAR output estimate overflowed safe accounting limits.",
                exception);
        }
    }

    // This is the qualified ZIP estimate. Keep its arithmetic unchanged when
    // adding format-specific output accounting.
    private static long EstimateMaximumZipOutputBytes(
        IEnumerable<PlannedArchiveCreationFile> files,
        long sourceBytes,
        IEnumerable<PlannedArchiveCreationDirectory>? directories)
    {
        try
        {
            long pathMetadata = 0;
            foreach (var file in files)
            {
                pathMetadata = checked(pathMetadata + 512 + ((long)file.ArchivePath.Length * 6));
            }

            foreach (var directory in directories ?? [])
            {
                pathMetadata = checked(
                    pathMetadata + 512 + ((long)directory.ArchivePath.Length * 6));
            }

            return checked(sourceBytes + (sourceBytes / 8) + pathMetadata + (1024L * 1024));
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "creation.output_estimate_overflow",
                "The archive output estimate overflowed safe accounting limits.",
                exception);
        }
    }

    private static long EstimateMaximumTarOutputBytes(
        IEnumerable<PlannedArchiveCreationFile> files,
        IEnumerable<PlannedArchiveCreationDirectory> directories)
    {
        const long TarBlockBytes = 512;
        const long TarEndMarkersBytes = TarBlockBytes * 2;
        const long PaxFixedMetadataAllowanceBytes = 2048;
        try
        {
            long estimate = TarEndMarkersBytes;
            foreach (var file in files)
            {
                estimate = checked(
                    estimate
                    + TarBlockBytes
                    + RoundUpToTarBlock(file.SourceSnapshot.Length)
                    + EstimatePaxExtendedHeaderBytes(
                        file.ArchivePath,
                        PaxFixedMetadataAllowanceBytes));
            }

            foreach (var directory in directories)
            {
                estimate = checked(
                    estimate
                    + TarBlockBytes
                    + EstimatePaxExtendedHeaderBytes(
                        directory.ArchivePath,
                        PaxFixedMetadataAllowanceBytes));
            }

            return estimate;
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "creation.output_estimate_overflow",
                "The TAR output estimate overflowed safe accounting limits.",
                exception);
        }
    }

    private static long EstimateMaximumCompressedTarOutputBytes(
        IEnumerable<PlannedArchiveCreationFile> files,
        long sourceBytes,
        IEnumerable<PlannedArchiveCreationDirectory> directories)
    {
        var tarEstimate = EstimateMaximumTarOutputBytes(files, directories);
        try
        {
            // Compression headers and worst-case expansion are bounded on top
            // of the unchanged TAR upper bound. Never permit a compressed
            // estimate to undercount either the source bytes or TAR output.
            var lowerBound = Math.Max(sourceBytes, tarEstimate);
            return checked(lowerBound + (tarEstimate / 8) + (1024L * 1024));
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "creation.output_estimate_overflow",
                "The compressed TAR output estimate overflowed safe accounting limits.",
                exception);
        }
    }

    private static long EstimateMaximumRawCompressedOutputBytes(
        IEnumerable<PlannedArchiveCreationFile> files,
        long sourceBytes)
    {
        try
        {
            long pathMetadata = 0;
            foreach (var file in files)
            {
                pathMetadata = checked(
                    pathMetadata
                    + 512
                    + ((long)file.ArchivePath.Length * 6));
            }

            return checked(sourceBytes + (sourceBytes / 8) + pathMetadata + (1024L * 1024));
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit(
                "creation.output_estimate_overflow",
                "The raw compressed output estimate overflowed safe accounting limits.",
                exception);
        }
    }

    private static long EstimatePaxExtendedHeaderBytes(
        string archivePath,
        long fixedMetadataAllowanceBytes)
    {
        // PaxTarEntry may need an extended header for UTF-8/long paths and
        // sub-second timestamps. Reserve one header plus a conservatively
        // padded record payload for every entry so the output guard never
        // undercounts valid metadata.
        var pathBytes = Encoding.UTF8.GetByteCount(archivePath);
        return checked(
            512L
            + RoundUpToTarBlock(checked(pathBytes + fixedMetadataAllowanceBytes)));
    }

    private static long RoundUpToTarBlock(long value)
    {
        const long TarBlockBytes = 512;
        if (value < 0)
        {
            throw new OverflowException("A TAR output component had a negative length.");
        }

        return checked(((value + TarBlockBytes - 1) / TarBlockBytes) * TarBlockBytes);
    }

    private static CreationLimits GetLimits(ArchiveCreationOutputFormat outputFormat) =>
        outputFormat switch
        {
            ArchiveCreationOutputFormat.Zip => ZipLimits,
            ArchiveCreationOutputFormat.Tar
                or ArchiveCreationOutputFormat.TarGZip
                or ArchiveCreationOutputFormat.TarBZip2 => TarLimits,
            ArchiveCreationOutputFormat.GZip
                or ArchiveCreationOutputFormat.BZip2 => RawCompressionLimits,
            ArchiveCreationOutputFormat.Rar => RarLimits,
            _ => throw new ArgumentOutOfRangeException(nameof(outputFormat)),
        };

    private CreationLimits GetTreeLimits(ArchiveCreationOutputFormat outputFormat) =>
        GetLimits(outputFormat) with { MaximumEntries = _maximumTreeEntries };

    private static bool IsRawCompression(ArchiveCreationOutputFormat outputFormat) =>
        outputFormat is ArchiveCreationOutputFormat.GZip or ArchiveCreationOutputFormat.BZip2;

    private sealed record DirectoryRootDefinition(
        string SourcePath,
        string ArchivePath,
        OpenDirectorySnapshot Snapshot);

    private sealed record CreationLimits(
        int MaximumExplicitFiles,
        int MaximumEntries,
        long MaximumSourceBytes);

    private sealed class ArchivePathClaims
    {
        private readonly HashSet<string> _claimedEntries = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _claimedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _claimedDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _directorySpellings =
            new(StringComparer.OrdinalIgnoreCase);

        public void Claim(string archivePath, string collisionKey, bool isDirectory)
        {
            var parentKeys = EnumerateParentKeys(collisionKey).ToArray();
            var archiveComponents = archivePath.Split('/');
            var parentSpelling = string.Empty;
            for (var index = 0; index < parentKeys.Length; index++)
            {
                parentSpelling = parentSpelling.Length == 0
                    ? archiveComponents[index]
                    : $"{parentSpelling}/{archiveComponents[index]}";
                EnsureDirectorySpelling(parentKeys[index], parentSpelling);
            }

            if (_claimedEntries.Contains(collisionKey)
                || _claimedFiles.Contains(collisionKey)
                || parentKeys.Any(_claimedFiles.Contains)
                || (!isDirectory && _claimedDirectories.Contains(collisionKey)))
            {
                throw Collision();
            }

            if (isDirectory)
            {
                EnsureDirectorySpelling(collisionKey, archivePath);
                _claimedDirectories.Add(collisionKey);
            }
            else
            {
                _claimedFiles.Add(collisionKey);
            }

            _claimedEntries.Add(collisionKey);
            parentSpelling = string.Empty;
            for (var index = 0; index < parentKeys.Length; index++)
            {
                parentSpelling = parentSpelling.Length == 0
                    ? archiveComponents[index]
                    : $"{parentSpelling}/{archiveComponents[index]}";
                _directorySpellings.TryAdd(parentKeys[index], parentSpelling);
                _claimedDirectories.Add(parentKeys[index]);
            }
        }

        private void EnsureDirectorySpelling(string collisionKey, string spelling)
        {
            if (_directorySpellings.TryGetValue(collisionKey, out var existing)
                && !string.Equals(existing, spelling, StringComparison.Ordinal))
            {
                throw Collision();
            }

            _directorySpellings.TryAdd(collisionKey, spelling);
        }

        private static ArchiveProviderException Collision() =>
            new(
                ArchiveErrorCategory.PathCollision,
                "creation.archive_path_collision",
                "Archive entry paths collide after Windows case or Unicode normalization.");
    }

    private static ArchiveProviderException ResourceLimit(
        string code,
        string message,
        Exception? innerException = null) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message, innerException);

    private static ArchiveProviderException UnsafePath(
        string code,
        string message,
        Exception? innerException = null) =>
        new(ArchiveErrorCategory.UnsafePath, code, message, innerException);
}
