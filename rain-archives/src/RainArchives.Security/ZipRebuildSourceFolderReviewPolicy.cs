using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

/// <summary>
/// Worker-owned, read-only review of mapped source folders for ZIP Rebuild.
/// It opens pinned non-reparse directory handles and validates every regular
/// child without reading file contents or making WPF the source authority.
/// </summary>
public sealed class ZipRebuildSourceFolderReviewPolicy
{
    public GetZipRebuildSourceFolderReviewResponse BuildReview(
        IReadOnlyList<ArchiveCreationDirectory> directories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);
        if (directories.Count == 0)
        {
            throw InvalidRequest("zip.rebuild_source_folder_review_empty", "At least one source folder is required for review.");
        }

        if (directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots)
        {
            throw Limit("zip.rebuild_source_folder_review_limit", "A source-folder review can contain at most 64 roots.");
        }

        var canonicalRoots = new List<(string SourcePath, string ArchiveRoot)>(directories.Count);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory is null
                || string.IsNullOrWhiteSpace(directory.SourcePath)
                || directory.SourcePath.Length > ProtocolConstants.MaximumPathCharacters
                || string.IsNullOrWhiteSpace(directory.ArchivePath)
                || directory.ArchivePath.Length > ProtocolConstants.MaximumPathCharacters)
            {
                throw InvalidRequest("zip.rebuild_source_folder_review_invalid", "A source-folder review root is empty or oversized.");
            }

            var sourcePath = CanonicalizeSourcePath(directory.SourcePath);
            var archiveRoot = CanonicalizeArchiveRoot(directory.ArchivePath);
            canonicalRoots.Add((sourcePath, archiveRoot));
        }

        for (var left = 0; left < canonicalRoots.Count; left++)
        {
            for (var right = left + 1; right < canonicalRoots.Count; right++)
            {
                if (AreSameOrDescendant(canonicalRoots[left].SourcePath, canonicalRoots[right].SourcePath)
                    || AreSameOrDescendant(canonicalRoots[right].SourcePath, canonicalRoots[left].SourcePath))
                {
                    throw InvalidRequest(
                        "zip.rebuild_source_folder_review_overlap",
                        "Source-folder review roots cannot overlap or contain one another.");
                }

                if (AreSameOrDescendantArchive(canonicalRoots[left].ArchiveRoot, canonicalRoots[right].ArchiveRoot)
                    || AreSameOrDescendantArchive(canonicalRoots[right].ArchiveRoot, canonicalRoots[left].ArchiveRoot))
                {
                    throw InvalidRequest(
                        "zip.rebuild_source_folder_review_archive_collision",
                        "Source-folder review archive roots cannot overlap or contain one another.");
                }
            }
        }

        var results = new List<ZipRebuildSourceFolderReview>(directories.Count);
        var totalEntries = directories.Count;
        long totalSourceBytes = 0;
        foreach (var root in canonicalRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = HandleRelativeExtractionRoot.OpenStableSourceDirectory(root.SourcePath);
            var counts = new ReviewCounts();
            ReviewDirectory(source, counts, cancellationToken);
            source.EnsureMatches(source.Snapshot);
            totalEntries = checked(totalEntries + counts.DescendantCount);
            totalSourceBytes = checked(totalSourceBytes + counts.SourceBytes);
            if (totalEntries > ProtocolConstants.MaximumZipCreationEntries)
            {
                throw Limit(
                    "zip.rebuild_source_folder_review_limit",
                    "The selected source folders exceed the global bounded entry budget.");
            }

            if (totalSourceBytes > ProtocolConstants.MaximumZipCreationSourceBytes)
            {
                throw Limit(
                    "zip.rebuild_source_folder_review_bytes",
                    "The selected source folders exceed the global bounded decoded source-byte budget.");
            }

            results.Add(new ZipRebuildSourceFolderReview(
                source.SourcePath,
                root.ArchiveRoot,
                counts.DescendantCount,
                counts.FileCount));
        }

        var response = new GetZipRebuildSourceFolderReviewResponse(results);
        try
        {
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetZipRebuildSourceFolderReview,
                response);
        }
        catch (InvalidDataException exception)
        {
            throw Limit(
                "zip.rebuild_source_folder_review_limit",
                "The source-folder review exceeds the bounded protocol frame size.",
                exception);
        }

        return response;
    }

    private static void ReviewDirectory(
        HandleRelativeExtractionRoot.HandleRelativeSourceDirectory directory,
        ReviewCounts counts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var children = directory.EnumerateChildren(
            ProtocolConstants.MaximumZipCreationEntries,
            cancellationToken);
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsafePath,
                    "source.reparse_point",
                    "Archive creation source trees cannot contain reparse points.");
            }

            counts.DescendantCount = checked(counts.DescendantCount + 1);
            if (counts.DescendantCount > ProtocolConstants.MaximumZipCreationEntries)
            {
                throw Limit(
                    "zip.rebuild_source_folder_review_limit",
                    "A source-folder review exceeds the bounded material-entry limit.");
            }

            if (child.Attributes.HasFlag(FileAttributes.Directory))
            {
                using var nested = directory.OpenChildDirectory(child.Name, child.FileId);
                ReviewDirectory(nested, counts, cancellationToken);
                continue;
            }

            counts.FileCount = checked(counts.FileCount + 1);
            // Opening the default stream pins the exact file identity and
            // rejects hard links. Alternate data streams are never opened or
            // treated as archive input.
            using var file = directory.OpenChildFile(
                child.Name,
                child.FileId,
                requireSingleLink: true);
            counts.SourceBytes = checked(counts.SourceBytes + file.Snapshot.Length);
            if (counts.SourceBytes > ProtocolConstants.MaximumZipCreationSourceBytes)
            {
                throw Limit(
                    "zip.rebuild_source_folder_review_bytes",
                    "A source-folder review exceeds the bounded decoded source-byte limit.");
            }
        }

        directory.EnsureMatches(directory.Snapshot);
    }

    private static string CanonicalizeSourcePath(string value)
    {
        if (!Path.IsPathFullyQualified(value)
            || value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal)
            || value.StartsWith(@"\??\", StringComparison.Ordinal)
            || value.StartsWith("//?", StringComparison.Ordinal)
            || value.StartsWith("//.", StringComparison.Ordinal))
        {
            throw InvalidRequest(
                "zip.rebuild_source_folder_review_path",
                "Source-folder review paths must be ordinary absolute paths.");
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(value);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidRequest(
                "zip.rebuild_source_folder_review_path",
                "Source-folder review paths must be ordinary absolute paths.",
                exception);
        }

        if (canonical.Length > ProtocolConstants.MaximumPathCharacters
            || string.IsNullOrEmpty(Path.GetPathRoot(canonical))
            || canonical.EndsWith(Path.DirectorySeparatorChar)
                && canonical.Length == Path.GetPathRoot(canonical)!.Length)
        {
            throw InvalidRequest(
                "zip.rebuild_source_folder_review_path",
                "The source-folder review path is not a bounded directory path.");
        }

        return canonical;
    }

    private static string CanonicalizeArchiveRoot(string value)
    {
        var candidate = value.TrimEnd('/', '\\');
        if (!ArchivePathRules.TryNormalize(candidate, out var normalized, out _, out var reason)
            || string.IsNullOrWhiteSpace(normalized))
        {
            throw InvalidRequest(
                "zip.rebuild_source_folder_review_archive_path",
                $"The source-folder review archive root is unsafe: {reason}");
        }

        return normalized.Replace('\\', '/');
    }

    private static bool AreSameOrDescendant(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
            || (normalizedCandidate.Length > normalizedRoot.Length
                && normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && IsSeparator(normalizedCandidate[normalizedRoot.Length]));
    }

    private static bool AreSameOrDescendantArchive(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd('/');
        var normalizedCandidate = candidate.TrimEnd('/');
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
            || (normalizedCandidate.Length > normalizedRoot.Length
                && normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && normalizedCandidate[normalizedRoot.Length] == '/');
    }

    private static bool IsSeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    private static ArchiveProviderException InvalidRequest(
        string code,
        string message,
        Exception? innerException = null) =>
        new(ArchiveErrorCategory.InvalidRequest, code, message, innerException);

    private static ArchiveProviderException Limit(
        string code,
        string message,
        Exception? innerException = null) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message, innerException);

    private sealed class ReviewCounts
    {
        public int DescendantCount { get; set; }
        public int FileCount { get; set; }
        public long SourceBytes { get; set; }
    }
}
