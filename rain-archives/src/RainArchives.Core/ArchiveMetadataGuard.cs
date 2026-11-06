using RainArchives.Contracts;

namespace RainArchives.Core;

public sealed record ArchiveMetadataLimits(
    int MaximumEntries = 500_000,
    int MaximumRawPathLength = 32_000,
    long MetadataBudgetBytes = 512L * 1024 * 1024,
    int MaximumHierarchyDepth = 256,
    int MaximumSyntheticDirectories = 500_000);

public sealed class ArchiveMetadataBudget
{
    private readonly ArchiveMetadataLimits _limits;
    private readonly HashSet<int> _ordinals = [];
    private long _estimatedBytes;

    public ArchiveMetadataBudget(ArchiveMetadataLimits? limits = null)
    {
        _limits = limits ?? new ArchiveMetadataLimits();
        if (_limits.MaximumEntries <= 0
            || _limits.MaximumRawPathLength <= 0
            || _limits.MetadataBudgetBytes <= 0
            || _limits.MaximumHierarchyDepth <= 0
            || _limits.MaximumSyntheticDirectories <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Archive metadata limits must be positive.");
        }
    }

    public int EntryCount => _ordinals.Count;
    public long EstimatedBytes => _estimatedBytes;

    public void Add(ProviderArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_ordinals.Count >= _limits.MaximumEntries)
        {
            throw ResourceLimit(
                "resource.entry_count",
                $"The archive contains more than {_limits.MaximumEntries:N0} entries.");
        }

        if (entry.Ordinal < 0 || !_ordinals.Add(entry.Ordinal))
        {
            throw InvalidMetadata(
                "archive.invalid_entry_ordinal",
                "The archive provider returned an invalid or duplicate entry ordinal.");
        }

        if (string.IsNullOrEmpty(entry.RawPath))
        {
            throw InvalidMetadata("archive.empty_entry_path", "The archive contains an entry with no usable path.");
        }

        if (entry.RawPath.Length > _limits.MaximumRawPathLength)
        {
            throw ResourceLimit(
                "resource.entry_path_length",
                "An archive entry path exceeds the metadata path-length limit.");
        }

        if (entry.OriginalSize is < 0 || entry.CompressedSize is < 0)
        {
            throw InvalidMetadata(
                "archive.negative_entry_size",
                "The archive provider returned a negative entry size.");
        }

        if (!Enum.IsDefined(entry.Kind))
        {
            throw InvalidMetadata(
                "archive.invalid_entry_kind",
                "The archive provider returned an unknown entry-kind value.");
        }

        try
        {
            _estimatedBytes = checked(
                _estimatedBytes
                + 256
                + Estimate(entry.RawPath)
                + Estimate(entry.CompressionMethod)
                + Estimate(entry.IntegrityValue)
                + Estimate(entry.LinkTarget));
        }
        catch (OverflowException)
        {
            throw ResourceLimit("resource.metadata_overflow", "Archive metadata accounting overflowed.");
        }

        if (_estimatedBytes > _limits.MetadataBudgetBytes)
        {
            throw ResourceLimit(
                "resource.metadata_budget",
                "Archive metadata exceeds the configured memory budget.");
        }
    }

    private static long Estimate(string? value) =>
        value is null ? 0 : checked((long)value.Length * sizeof(char));

    private static ArchiveProviderException ResourceLimit(string code, string message) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message);

    private static ArchiveProviderException InvalidMetadata(string code, string message) =>
        new(ArchiveErrorCategory.InvalidArchive, code, message);
}

public static class ArchiveMetadataGuard
{
    public static void Validate(
        IReadOnlyCollection<ProviderArchiveEntry> entries,
        ArchiveMetadataLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var budget = new ArchiveMetadataBudget(limits);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.Add(entry);
        }
    }
}
