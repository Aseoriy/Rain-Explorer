using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed record ExtractionPathIssue(string Code, string Message, EntryId EntryId, string RawPath);

public sealed record PlannedExtractionEntry(
    IndexedArchiveEntry IndexedEntry,
    string RelativePath,
    string CollisionKey);

public sealed record ExtractionPathPlan(
    IReadOnlyList<PlannedExtractionEntry> Entries,
    IReadOnlyList<ExtractionPathIssue> Issues)
{
    public bool IsSafe => Issues.Count == 0;
}

public sealed class ExtractionPathPolicy
{
    private const int MaximumReportedIssues = 500_000;
    private static readonly char[] Separators = ['/', '\\'];
    private readonly ExtractionSecurityPolicy _policy;

    public ExtractionPathPolicy(ExtractionSecurityPolicy? policy = null)
    {
        _policy = policy ?? new ExtractionSecurityPolicy();
    }

    public int MaximumFinalPathLength => _policy.MaximumFinalPathLength;

    public ExtractionPathPlan Plan(
        IReadOnlyCollection<IndexedArchiveEntry> selectedEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedEntries);

        var planned = new List<PlannedExtractionEntry>(selectedEntries.Count);
        var issues = new List<ExtractionPathIssue>();
        var issueKeys = new HashSet<(EntryId EntryId, string Code)>();
        var claimedPaths = new Dictionary<string, PlannedExtractionEntry>(StringComparer.OrdinalIgnoreCase);
        var claimedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryClaimants = new Dictionary<string, List<PlannedExtractionEntry>>(StringComparer.OrdinalIgnoreCase);
        var parentSpellingClaims = new Dictionary<string, (string Spelling, PlannedExtractionEntry Entry)>(
            StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();
        var orderedEntries = selectedEntries.ToArray();
        Array.Sort(
            orderedEntries,
            static (left, right) =>
                (left.ProviderEntry?.Ordinal ?? int.MaxValue)
                .CompareTo(right.ProviderEntry?.Ordinal ?? int.MaxValue));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var indexed in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (indexed.IsSynthetic)
            {
                continue;
            }

            var descriptor = indexed.Descriptor;
            if (descriptor.Kind == ArchiveEntryKind.Unknown)
            {
                issues.Add(new ExtractionPathIssue(
                    "path.unsupported_entry_kind",
                    "An unclassified archive entry cannot be materialized safely.",
                    descriptor.EntryId,
                    descriptor.RawPath));
                continue;
            }

            if (descriptor.Kind is ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.HardLink or ArchiveEntryKind.ReparsePoint)
            {
                issues.Add(new ExtractionPathIssue(
                    "path.link_not_materialized",
                    "Archive link entries are not materialized by the current security policy.",
                    descriptor.EntryId,
                    descriptor.RawPath));
                continue;
            }

            var pathToValidate = descriptor.Kind == ArchiveEntryKind.Directory
                && descriptor.RawPath.Length > 0
                && descriptor.RawPath[^1] is '/' or '\\'
                    ? descriptor.RawPath[..^1]
                    : descriptor.RawPath;
            if (!TryNormalize(pathToValidate, out var relativePath, out var collisionKey, out var reason))
            {
                issues.Add(new ExtractionPathIssue(
                    "path.unsafe",
                    reason,
                    descriptor.EntryId,
                    descriptor.RawPath));
                continue;
            }

            var candidate = new PlannedExtractionEntry(indexed, relativePath, collisionKey);
            if (claimedPaths.TryGetValue(collisionKey, out var existing))
            {
                AddIssueOnce(issues, issueKeys, new ExtractionPathIssue(
                    "path.collision",
                    "Another archive entry resolves to the same path under Windows name rules.",
                    descriptor.EntryId,
                    descriptor.RawPath));
                AddIssueOnce(issues, issueKeys, new ExtractionPathIssue(
                    "path.collision",
                    "Another archive entry resolves to the same path under Windows name rules.",
                    existing.IndexedEntry.Descriptor.EntryId,
                    existing.IndexedEntry.Descriptor.RawPath));
                continue;
            }

            var parentKeys = EnumerateParentKeys(collisionKey).ToArray();
            var originalComponents = pathToValidate.Split(Separators, StringSplitOptions.None);
            var originalParent = string.Empty;
            var parentSpellingCollision = false;
            for (var index = 0; index < parentKeys.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                originalParent = originalParent.Length == 0
                    ? originalComponents[index]
                    : $"{originalParent}/{originalComponents[index]}";
                if (parentSpellingClaims.TryGetValue(parentKeys[index], out var existingParent)
                    && !string.Equals(existingParent.Spelling, originalParent, StringComparison.Ordinal))
                {
                    AddIssueOnce(issues, issueKeys, new ExtractionPathIssue(
                        "path.parent_spelling_collision",
                        "A parent directory has a conflicting spelling under Windows case or Unicode normalization rules.",
                        descriptor.EntryId,
                        descriptor.RawPath));
                    AddIssueOnce(issues, issueKeys, new ExtractionPathIssue(
                        "path.parent_spelling_collision",
                        "A parent directory has a conflicting spelling under Windows case or Unicode normalization rules.",
                        existingParent.Entry.IndexedEntry.Descriptor.EntryId,
                        existingParent.Entry.IndexedEntry.Descriptor.RawPath));
                    parentSpellingCollision = true;
                }
            }

            if (parentSpellingCollision)
            {
                continue;
            }

            if (parentKeys.Any(claimedFiles.Contains))
            {
                AddIssueOnce(issues, issueKeys, new ExtractionPathIssue(
                    "path.file_directory_collision",
                    "An entry would be created below a path already claimed by a file.",
                    descriptor.EntryId,
                    descriptor.RawPath));
                foreach (var parentKey in parentKeys.Where(claimedFiles.Contains))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (claimedPaths.TryGetValue(parentKey, out var parentFile))
                    {
                        AddIssueOnce(issues, issueKeys, new ExtractionPathIssue(
                            "path.file_directory_collision",
                            "This file path is also required as a directory by another archive entry.",
                            parentFile.IndexedEntry.Descriptor.EntryId,
                            parentFile.IndexedEntry.Descriptor.RawPath));
                    }
                }

                continue;
            }

            if (descriptor.Kind == ArchiveEntryKind.File && claimedDirectories.Contains(collisionKey))
            {
                AddIssueOnce(issues, issueKeys, new ExtractionPathIssue(
                    "path.file_directory_collision",
                    "An entry is both a file and a directory under Windows path rules.",
                    descriptor.EntryId,
                    descriptor.RawPath));
                foreach (var child in directoryClaimants.GetValueOrDefault(collisionKey) ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddIssueOnce(issues, issueKeys, new ExtractionPathIssue(
                        "path.file_directory_collision",
                        "This entry requires a path claimed by another archive entry to be a directory.",
                        child.IndexedEntry.Descriptor.EntryId,
                        child.IndexedEntry.Descriptor.RawPath));
                }

                continue;
            }

            claimedPaths.Add(collisionKey, candidate);
            if (descriptor.Kind == ArchiveEntryKind.Directory)
            {
                claimedDirectories.Add(collisionKey);
            }
            else
            {
                claimedFiles.Add(collisionKey);
            }

            foreach (var parent in parentKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                claimedDirectories.Add(parent);
                if (!directoryClaimants.TryGetValue(parent, out var claimants))
                {
                    claimants = [];
                    directoryClaimants.Add(parent, claimants);
                }

                claimants.Add(candidate);
            }

            originalParent = string.Empty;
            for (var index = 0; index < parentKeys.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                originalParent = originalParent.Length == 0
                    ? originalComponents[index]
                    : $"{originalParent}/{originalComponents[index]}";
                parentSpellingClaims.TryAdd(parentKeys[index], (originalParent, candidate));
            }

            planned.Add(candidate);
        }

        return new ExtractionPathPlan(planned, issues);
    }

    public bool TryNormalize(
        string rawPath,
        out string relativePath,
        out string collisionKey,
        out string reason) =>
        ArchivePathRules.TryNormalize(
            rawPath,
            _policy.MaximumPathDepth,
            _policy.MaximumComponentLength,
            _policy.MaximumFinalPathLength,
            out relativePath,
            out collisionKey,
            out reason);

    private static void AddIssueOnce(
        List<ExtractionPathIssue> issues,
        HashSet<(EntryId EntryId, string Code)> issueKeys,
        ExtractionPathIssue issue)
    {
        if (!issueKeys.Add((issue.EntryId, issue.Code)))
        {
            return;
        }

        if (issues.Count >= MaximumReportedIssues)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "resource.path_issue_count",
                "Archive path validation produced too many distinct safety issues.");
        }

        issues.Add(issue);
    }

    private static IEnumerable<string> EnumerateParentKeys(string collisionKey)
    {
        var offset = collisionKey.IndexOf('/', StringComparison.Ordinal);
        while (offset >= 0)
        {
            yield return collisionKey[..offset];
            offset = collisionKey.IndexOf('/', offset + 1);
        }
    }

}
