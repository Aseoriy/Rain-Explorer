using RainArchives.Contracts;

namespace RainArchives.Core;

public sealed record IndexedArchiveEntry(
    ArchiveEntryDescriptor Descriptor,
    ProviderArchiveEntry? ProviderEntry,
    bool IsSynthetic);

public sealed record EntrySafetyAnnotation(EntrySafetyStatus Status, string Reason);

public sealed class ArchiveIndex
{
    private readonly IReadOnlyList<IndexedArchiveEntry> _entries;
    private readonly IReadOnlyDictionary<EntryId, IndexedArchiveEntry> _byId;
    private readonly IReadOnlyDictionary<EntryId, List<IndexedArchiveEntry>> _childrenByParentId;
    private readonly IReadOnlyList<IndexedArchiveEntry> _providerOrderedEntries;

    private ArchiveIndex(
        IReadOnlyList<IndexedArchiveEntry> entries,
        CancellationToken cancellationToken = default)
    {
        _entries = entries;
        var byId = new Dictionary<EntryId, IndexedArchiveEntry>(entries.Count);
        var childrenByParentId = new Dictionary<EntryId, List<IndexedArchiveEntry>>();
        var providerOrderedEntries = new List<IndexedArchiveEntry>(entries.Count);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byId.Add(entry.Descriptor.EntryId, entry);
            if (!entry.IsSynthetic)
            {
                providerOrderedEntries.Add(entry);
            }

            if (entry.Descriptor.ParentEntryId is not { } parentId)
            {
                continue;
            }

            if (!childrenByParentId.TryGetValue(parentId, out var children))
            {
                children = [];
                childrenByParentId.Add(parentId, children);
            }

            children.Add(entry);
        }

        cancellationToken.ThrowIfCancellationRequested();
        providerOrderedEntries.Sort(static (left, right) =>
            left.ProviderEntry!.Ordinal.CompareTo(right.ProviderEntry!.Ordinal));
        cancellationToken.ThrowIfCancellationRequested();

        _byId = byId;
        _childrenByParentId = childrenByParentId;
        _providerOrderedEntries = providerOrderedEntries;
    }

    public IReadOnlyList<IndexedArchiveEntry> Entries => _entries;

    /// <summary>
    /// Material entries in the provider's stable ordinal order. Consumers that
    /// need deterministic snapshot traversal should reuse this order instead of
    /// sorting the materialized index again.
    /// </summary>
    public IReadOnlyList<IndexedArchiveEntry> ProviderOrderedEntries => _providerOrderedEntries;

    public IndexedArchiveEntry Get(EntryId entryId) =>
        _byId.TryGetValue(entryId, out var entry)
            ? entry
            : throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "entry.not_found",
                "An entry identifier does not belong to this archive session.");

    public ArchiveIndex WithSafetyAnnotations(
        IReadOnlyDictionary<EntryId, EntrySafetyAnnotation> annotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        var updated = new IndexedArchiveEntry[_entries.Count];
        for (var index = 0; index < _entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _entries[index];
            if (!annotations.TryGetValue(entry.Descriptor.EntryId, out var annotation))
            {
                updated[index] = entry;
                continue;
            }

            updated[index] = entry with
            {
                Descriptor = entry.Descriptor with
                {
                    SafetyStatus = annotation.Status,
                    SafetyReason = annotation.Reason,
                },
            };
        }

        return new ArchiveIndex(updated, cancellationToken);
    }

    public IReadOnlyList<IndexedArchiveEntry> ResolveSelection(
        IReadOnlyCollection<EntryId> selectedIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedIds.Count == 0)
        {
            var allEntries = new IndexedArchiveEntry[_providerOrderedEntries.Count];
            for (var index = 0; index < _providerOrderedEntries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                allEntries[index] = _providerOrderedEntries[index];
            }

            return allEntries;
        }

        var resolvedIds = new HashSet<EntryId>();
        var visitedDirectories = new HashSet<EntryId>();
        var pendingDirectories = new Stack<IndexedArchiveEntry>();
        foreach (var entryId in selectedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = Get(entryId);
            if (!selected.IsSynthetic)
            {
                resolvedIds.Add(selected.Descriptor.EntryId);
            }

            if (selected.Descriptor.Kind == ArchiveEntryKind.Directory
                && visitedDirectories.Add(selected.Descriptor.EntryId))
            {
                pendingDirectories.Push(selected);
            }
        }

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            if (!_childrenByParentId.TryGetValue(directory.Descriptor.EntryId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!child.IsSynthetic)
                {
                    resolvedIds.Add(child.Descriptor.EntryId);
                }

                if (child.Descriptor.Kind == ArchiveEntryKind.Directory
                    && visitedDirectories.Add(child.Descriptor.EntryId))
                {
                    pendingDirectories.Push(child);
                }
            }
        }

        var resolvedEntries = new IndexedArchiveEntry[resolvedIds.Count];
        var resolvedCount = 0;
        foreach (var entry in _providerOrderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolvedIds.Contains(entry.Descriptor.EntryId))
            {
                resolvedEntries[resolvedCount++] = entry;
            }
        }

        return resolvedEntries;
    }

    public static ArchiveIndex Create(
        IReadOnlyList<ProviderArchiveEntry> providerEntries,
        ArchiveMetadataLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerEntries);
        var effectiveLimits = limits ?? new ArchiveMetadataLimits();
        if (effectiveLimits.MaximumHierarchyDepth <= 0
            || effectiveLimits.MaximumSyntheticDirectories <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Archive index limits must be positive.");
        }

        var entries = new List<IndexedArchiveEntry>();
        var directoryByPath = new Dictionary<string, IndexedArchiveEntry>(StringComparer.Ordinal);
        var directoryPositionByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicateCountByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var syntheticDirectoryCount = 0;

        cancellationToken.ThrowIfCancellationRequested();
        var orderedEntries = providerEntries.ToArray();
        Array.Sort(orderedEntries, static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var providerEntry in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeHierarchyPath(providerEntry.RawPath).TrimEnd('/');
            duplicateCountByPath.TryGetValue(normalized, out var duplicateOrdinal);
            duplicateCountByPath[normalized] = checked(duplicateOrdinal + 1);
            var components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length > effectiveLimits.MaximumHierarchyDepth)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "resource.hierarchy_depth",
                    $"An archive path exceeds the {effectiveLimits.MaximumHierarchyDepth}-component indexing limit.");
            }

            EntryId? parentId = null;
            var currentPath = string.Empty;

            for (var index = 0; index < Math.Max(0, components.Length - 1); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentPath = currentPath.Length == 0
                    ? components[index]
                    : $"{currentPath}/{components[index]}";

                if (!directoryByPath.TryGetValue(currentPath, out var directory))
                {
                    if (syntheticDirectoryCount >= effectiveLimits.MaximumSyntheticDirectories)
                    {
                        throw new ArchiveProviderException(
                            ArchiveErrorCategory.ResourceLimit,
                            "resource.synthetic_directory_count",
                            $"Archive hierarchy reconstruction exceeds the {effectiveLimits.MaximumSyntheticDirectories:N0}-directory limit.");
                    }

                    directory = CreateSyntheticDirectory(currentPath, components[index], parentId);
                    directoryByPath.Add(currentPath, directory);
                    directoryPositionByPath.Add(currentPath, entries.Count);
                    entries.Add(directory);
                    syntheticDirectoryCount++;
                }

                parentId = directory.Descriptor.EntryId;
            }

            if (providerEntry.Kind == ArchiveEntryKind.Directory
                && directoryByPath.TryGetValue(normalized, out var synthetic)
                && synthetic.IsSynthetic)
            {
                var descriptor = ToDescriptor(
                    providerEntry,
                    synthetic.Descriptor.EntryId,
                    synthetic.Descriptor.ParentEntryId,
                    normalized,
                    duplicateOrdinal);
                var replacement = new IndexedArchiveEntry(descriptor, providerEntry, false);
                var position = directoryPositionByPath[normalized];
                entries[position] = replacement;
                directoryByPath[normalized] = replacement;
                syntheticDirectoryCount--;
                continue;
            }

            var displayName = components.Length == 0 ? providerEntry.RawPath : components[^1];
            var indexed = new IndexedArchiveEntry(
                ToDescriptor(providerEntry, EntryId.New(), parentId, normalized, duplicateOrdinal, displayName),
                providerEntry,
                false);
            entries.Add(indexed);

            if (providerEntry.Kind == ArchiveEntryKind.Directory && !directoryByPath.ContainsKey(normalized))
            {
                directoryByPath.Add(normalized, indexed);
                directoryPositionByPath.Add(normalized, entries.Count - 1);
            }
        }

        return new ArchiveIndex(entries, cancellationToken);
    }

    private static IndexedArchiveEntry CreateSyntheticDirectory(
        string rawPath,
        string displayName,
        EntryId? parentId)
    {
        var descriptor = new ArchiveEntryDescriptor(
            EntryId.New(),
            parentId,
            -1,
            rawPath,
            displayName,
            ArchiveEntryKind.Directory,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            null,
            EntrySafetyStatus.Safe,
            null,
            rawPath,
            0);

        return new IndexedArchiveEntry(descriptor, null, true);
    }

    private static ArchiveEntryDescriptor ToDescriptor(
        ProviderArchiveEntry entry,
        EntryId id,
        EntryId? parentId,
        string normalizedPath,
        int duplicateOrdinal,
        string? displayName = null) =>
        new(
            id,
            parentId,
            entry.Ordinal,
            entry.RawPath,
            displayName ?? NormalizeHierarchyPath(entry.RawPath).TrimEnd('/').Split('/').LastOrDefault() ?? entry.RawPath,
            entry.Kind,
            entry.OriginalSize,
            entry.CompressedSize,
            entry.ModifiedTimeUtc,
            entry.WindowsAttributes,
            entry.CompressionMethod,
            entry.IntegrityValue,
            entry.IsEncrypted,
            entry.IsSolid,
            entry.LinkTarget,
            entry.Kind is ArchiveEntryKind.Unknown
                or ArchiveEntryKind.SymbolicLink
                or ArchiveEntryKind.HardLink
                or ArchiveEntryKind.ReparsePoint
                ? entry.Kind == ArchiveEntryKind.Unknown
                    ? EntrySafetyStatus.UnsupportedEntryKind
                    : EntrySafetyStatus.UnsupportedLink
                : EntrySafetyStatus.Safe,
            entry.Kind is ArchiveEntryKind.Unknown
                ? "The provider could not classify this entry, so it cannot be materialized safely."
                : entry.Kind is ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.HardLink or ArchiveEntryKind.ReparsePoint
                    ? "Archive links are listed but are not materialized by the current extraction policy."
                    : null,
            normalizedPath,
            duplicateOrdinal);

    private static string NormalizeHierarchyPath(string rawPath) => rawPath.Replace('\\', '/');
}
