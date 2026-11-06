using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

/// <summary>
/// Resolves and applies protocol 1.15 metadata only after the complete 1.14
/// namespace plan exists. The timestamp remains a raw, timezone-less DOS tuple.
/// </summary>
public sealed class ZipRebuildMetadataPolicy
{
    public const int AllowedAttributes =
        (int)(FileAttributes.ReadOnly
            | FileAttributes.Hidden
            | FileAttributes.System
            | FileAttributes.Archive);

    private const int DirectoryAttribute = (int)FileAttributes.Directory;
    private readonly ExtractionPathPolicy _pathPolicy;

    public ZipRebuildMetadataPolicy(ExtractionPathPolicy? pathPolicy = null)
    {
        _pathPolicy = pathPolicy ?? new ExtractionPathPolicy();
    }

    public ZipRebuildCopyPlan Apply(
        ZipRebuildCopyPlan finalPlan,
        IReadOnlyList<ArchiveRebuildMetadataDirective> directives,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalPlan);
        ArgumentNullException.ThrowIfNull(directives);
        if (directives.Count > ProtocolConstants.MaximumZipRebuildMetadataDirectives)
        {
            throw Invalid(
                "zip.metadata_directive_limit",
                "A ZIP metadata request exceeds its bounded directive limit.",
                ArchiveErrorCategory.ResourceLimit);
        }

        var entries = finalPlan.Entries.ToArray();
        var byRetained = new Dictionary<ArchiveEntryKey, int>();
        var byMapped = new Dictionary<(string Path, ArchiveEntryKind Kind), int>();
        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            if (entry.RetainedEntry is { } retained
                && retained.Descriptor.NormalizedPath is { } path)
            {
                if (!byRetained.TryAdd(
                        new ArchiveEntryKey(path, retained.Descriptor.DuplicateOrdinal),
                        index))
                {
                    throw Invalid(
                        "zip.metadata_target_ambiguous",
                        "The final plan contains more than one retained entry for a persistent metadata key.");
                }
            }

            if (entry.Source != ZipRebuildCopyEntrySource.RetainedArchive)
            {
                if (!byMapped.TryAdd((ArchivePathRules.GetCollisionKey(entry.ArchivePath), entry.Kind), index))
                {
                    throw Invalid(
                        "zip.metadata_target_ambiguous",
                        "The final plan contains more than one mapped entry for a final metadata path and kind.");
                }
            }
        }

        var seen = new HashSet<int>();
        foreach (var directive in directives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateShape(directive);
            var index = Resolve(directive.Target, byRetained, byMapped);
            if (!seen.Add(index))
            {
                throw Invalid(
                    "zip.metadata_target_duplicate",
                    "A ZIP metadata target is specified more than once.");
            }

            var entry = entries[index];
            var timestamp = directive.LastWriteTime is { } rawTimestamp
                ? ToDateTime(rawTimestamp)
                : entry.ZipLastWriteTime.DateTime;
            var attributes = directive.AttributesLowWord is { } rawAttributes
                ? ValidateAttributes(rawAttributes, entry.Kind)
                : entry.WindowsAttributes;
            entries[index] = entry with
            {
                ZipLastWriteTime = new DateTimeOffset(timestamp, TimeSpan.Zero),
                WindowsAttributes = attributes,
            };
        }

        return directives.Count == 0
            ? finalPlan
            : finalPlan with { Entries = entries };
    }

    public static DateTime ToDateTime(ArchiveDosLocalTimestamp timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        if (timestamp.Year is < 1980 or > 2107
            || timestamp.Month is < 1 or > 12
            || timestamp.Hour is < 0 or > 23
            || timestamp.Minute is < 0 or > 59
            || timestamp.Second is < 0 or > 58
            || (timestamp.Second & 1) != 0)
        {
            throw Invalid(
                "zip.metadata_timestamp_invalid",
                "The ZIP DOS timestamp is outside the exact representable range.");
        }

        try
        {
            return new DateTime(
                timestamp.Year,
                timestamp.Month,
                timestamp.Day,
                timestamp.Hour,
                timestamp.Minute,
                timestamp.Second,
                DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw Invalid(
                "zip.metadata_timestamp_invalid",
                "The ZIP DOS timestamp is not a valid calendar date.",
                inner: exception);
        }
    }

    public static ushort ValidateAttributes(ushort attributes, ArchiveEntryKind kind)
    {
        if ((attributes & ~AllowedAttributes) != 0)
        {
            throw Invalid(
                "zip.metadata_attributes_unsupported",
                "The ZIP metadata attribute mask contains an unsupported bit.");
        }

        return (ushort)(kind == ArchiveEntryKind.Directory
            ? attributes | DirectoryAttribute
            : attributes & ~DirectoryAttribute);
    }

    private int Resolve(
        ArchiveRebuildMetadataTarget target,
        IReadOnlyDictionary<ArchiveEntryKey, int> byRetained,
        IReadOnlyDictionary<(string Path, ArchiveEntryKind Kind), int> byMapped)
    {
        ArgumentNullException.ThrowIfNull(target);
        switch (target.Kind)
        {
            case ArchiveRebuildMetadataTargetKind.RetainedEntryKey:
                if (target.RetainedEntryKey is null
                    || target.MappedFinalArchivePath is not null
                    || target.MappedKind is not null
                    || !byRetained.TryGetValue(target.RetainedEntryKey, out var retainedIndex))
                {
                    throw Invalid(
                        "zip.metadata_target_missing",
                        "The retained ZIP metadata target is missing from the final plan.");
                }

                return retainedIndex;

            case ArchiveRebuildMetadataTargetKind.MappedFinalPath:
                if (target.RetainedEntryKey is not null
                    || target.MappedFinalArchivePath is null
                    || target.MappedKind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory)
                    || !string.Equals(
                        target.MappedFinalArchivePath,
                        CanonicalPath(target.MappedFinalArchivePath),
                        StringComparison.Ordinal)
                    || !_pathPolicy.TryNormalize(
                        target.MappedFinalArchivePath,
                        out var normalized,
                        out _,
                        out _)
                    || !string.Equals(
                        normalized.Replace('\\', '/'),
                        target.MappedFinalArchivePath,
                        StringComparison.Ordinal)
                    || !byMapped.TryGetValue(
                        (ArchivePathRules.GetCollisionKey(target.MappedFinalArchivePath), target.MappedKind.Value),
                        out var mappedIndex))
                {
                    throw Invalid(
                        "zip.metadata_target_missing",
                        "The mapped ZIP metadata target is missing, deleted, ambiguous, or has a different kind.");
                }

                return mappedIndex;

            default:
                throw Invalid(
                    "zip.metadata_target_invalid",
                    "The ZIP metadata target kind is unsupported.");
        }
    }

    private static void ValidateShape(ArchiveRebuildMetadataDirective directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        if (directive.Target is null
            || (directive.LastWriteTime is null && directive.AttributesLowWord is null))
        {
            throw Invalid(
                "zip.metadata_target_invalid",
                "A ZIP metadata directive must contain one typed target and at least one editable field.");
        }
    }

    private static string CanonicalPath(string path) =>
        path.Replace('\\', '/').TrimEnd('/');

    private static ArchiveProviderException Invalid(
        string code,
        string message,
        ArchiveErrorCategory category = ArchiveErrorCategory.InvalidRequest,
        Exception? inner = null) =>
        new(category, code, message, inner);
}
