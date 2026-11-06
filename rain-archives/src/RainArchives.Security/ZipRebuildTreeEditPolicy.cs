using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

/// <summary>
/// Bounded, snapshot-only planning for the protocol 1.14 tree edit route.
/// This class deliberately does not open provider streams or mutate the source.
/// </summary>
public sealed class ZipRebuildTreeEditPolicy
{
    private const int RepresentableFileAttributes =
        (int)(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System
            | FileAttributes.Archive | FileAttributes.Normal | FileAttributes.NotContentIndexed);
    private const int RepresentableDirectoryAttributes = RepresentableFileAttributes | (int)FileAttributes.Directory;
    private static readonly DateTime MinimumZipLocalTime = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime MaximumZipLocalTime = new(2107, 12, 31, 23, 59, 58, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset SyntheticDirectoryTime =
        new(MinimumZipLocalTime, TimeZoneInfo.Local.GetUtcOffset(MinimumZipLocalTime));
    private readonly ArchiveCreationPolicy _creationPolicy;
    private readonly ExtractionPathPolicy _pathPolicy;
    private readonly ZipRebuildCopyPolicy _copyPolicy;

    public ZipRebuildTreeEditPolicy(
        ArchiveCreationPolicy? creationPolicy = null,
        ExtractionPathPolicy? pathPolicy = null,
        ZipRebuildCopyPolicy? copyPolicy = null)
    {
        _creationPolicy = creationPolicy ?? new ArchiveCreationPolicy();
        _pathPolicy = pathPolicy ?? new ExtractionPathPolicy();
        _copyPolicy = copyPolicy ?? new ZipRebuildCopyPolicy(_creationPolicy, _pathPolicy);
    }

    public GetZipRebuildTreePlanResponse BuildPlan(
        ArchiveIndex index,
        ArchiveFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(fingerprint);
        var nodes = BuildNodes(index, fingerprint, cancellationToken);

        var response = new GetZipRebuildTreePlanResponse(nodes);
        try
        {
            ProtocolStream.ValidateJsonFrameSize(ProtocolMessageKinds.GetZipRebuildTreePlan, response);
        }
        catch (InvalidDataException)
        {
            throw Limit(
                "zip.rebuild_tree_plan_limit",
                "The ZIP tree plan exceeds the bounded protocol frame limit.");
        }

        return response;
    }

    public async ValueTask<ZipRebuildCopyPlan> PlanAsync(
        IArchiveHandle handle,
        ArchiveIndex index,
        ArchiveFingerprint fingerprint,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        string sourceArchivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(entriesToDelete);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(treeActions);
        if (directories.Count > ProtocolConstants.MaximumZipRebuildDirectoryRoots
            || treeActions.Count > ProtocolConstants.MaximumZipRebuildTreeActions)
        {
            throw Limit(
                "zip.rebuild_tree_directive_limit",
                "A ZIP tree edit request exceeds its bounded root or action limit.");
        }

        var directiveCount = checked(entriesToDelete.Count + files.Count + renames.Count
            + directories.Count + treeActions.Count);
        if (directiveCount > ProtocolConstants.MaximumZipRebuildDirectives)
        {
            throw Limit(
                "zip.rebuild_tree_directive_limit",
                "A ZIP tree edit request exceeds its bounded directive limit.");
        }

        var pathPayload = 0L;
        foreach (var key in entriesToDelete)
        {
            pathPayload = checked(pathPayload + Utf8Length(key?.NormalizedPath));
        }
        foreach (var file in files)
        {
            pathPayload = checked(pathPayload + Utf8Length(file?.SourcePath) + Utf8Length(file?.ArchivePath));
        }
        foreach (var rename in renames)
        {
            pathPayload = checked(pathPayload + Utf8Length(rename?.EntryKey?.NormalizedPath)
                + Utf8Length(rename?.FinalArchivePath));
        }
        foreach (var directory in directories)
        {
            pathPayload = checked(pathPayload + Utf8Length(directory?.SourcePath)
                + Utf8Length(directory?.ArchivePath));
        }
        foreach (var action in treeActions)
        {
            pathPayload = checked(pathPayload + Utf8Length(action?.NormalizedPath)
                + Utf8Length(action?.ExpectedRevision) + Utf8Length(action?.FinalArchivePath));
        }
        if (pathPayload > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
        {
            throw Limit(
                "zip.rebuild_tree_path_payload_limit",
                "The combined UTF-8 ZIP tree edit path payload exceeds 512 KiB.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var material = index.Entries.Where(entry => !entry.IsSynthetic).ToArray();
        if (!_copyPolicy.CanRebuild(handle, index))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "zip.rebuild_source_not_eligible",
                "ZIP Rebuild Copy requires one safe, unencrypted, single-volume ZIP with complete entry metadata.");
        }

        var sourcePathPlan = _pathPolicy.Plan(material, cancellationToken);
        if (!sourcePathPlan.IsSafe)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.PathCollision,
                "zip.rebuild_source_paths_unsafe",
                "The opened ZIP contains unsafe or ambiguous material paths and cannot be rebuilt safely.");
        }

        var byKey = sourcePathPlan.Entries.ToDictionary(
            entry => new ArchiveEntryKey(
                entry.IndexedEntry.Descriptor.NormalizedPath!,
                entry.IndexedEntry.Descriptor.DuplicateOrdinal));
        var byPath = sourcePathPlan.Entries.ToDictionary(
            entry => entry.CollisionKey,
            StringComparer.OrdinalIgnoreCase);
        var nodes = BuildNodeMap(index, fingerprint, cancellationToken);

        var deletedIds = ResolveDeletes(entriesToDelete, byKey, cancellationToken);
        var renamesById = ResolveRenames(renames, byKey, deletedIds, cancellationToken);
        var actions = ResolveActions(treeActions, nodes.Nodes, cancellationToken);
        var actionTrie = ValidateActionRelationships(actions);

        foreach (var entry in sourcePathPlan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (actionTrie.IsDeletedPath(entry.RelativePath)
                && !deletedIds.Add(entry.IndexedEntry.Descriptor.EntryId))
            {
                throw InvalidTree("zip.rebuild_tree_edit_conflict", "An entry is transformed by more than one tree operation.");
            }
        }

        foreach (var rename in renamesById.Values)
        {
            if (deletedIds.Contains(rename.Entry.IndexedEntry.Descriptor.EntryId))
            {
                throw InvalidTree("zip.rebuild_tree_edit_conflict", "An entry under a deleted subtree cannot also be renamed.");
            }
        }

        ArchiveCreationPlan mappedPlan;
        if (directories.Count > 0)
        {
            mappedPlan = await _creationPolicy
                .PlanTreeAsync(files.Select(file => new ArchiveCreationFile(file.SourcePath, file.ArchivePath)).ToArray(), directories, destinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            mappedPlan = await _creationPolicy
                .PlanAsync(files.Select(file => new ArchiveCreationFile(file.SourcePath, file.ArchivePath)).ToArray(), destinationPath, cancellationToken)
                .ConfigureAwait(false);
        }

        // The no-tree Rebuild Copy route requires every mapped regular file to
        // have a single filesystem link. Keep the same authority for files
        // discovered through a mapped directory tree instead of allowing the
        // directory route to weaken that invariant.
        foreach (var mapped in mappedPlan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = HandleRelativeExtractionRoot.OpenStableSourceFile(
                mapped.SourcePath,
                requireSingleLink: true);
            source.EnsureMatches(mapped.SourceSnapshot);
        }

        if (string.Equals(Path.GetFullPath(sourceArchivePath), mappedPlan.DestinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "zip.rebuild_source_is_destination",
                "ZIP tree editing must publish to a new path and cannot target the opened source archive.");
        }

        var output = new List<PlannedZipRebuildCopyEntry>(checked(sourcePathPlan.Entries.Count + mappedPlan.TotalEntries));
        var retainedClampedTimestamps = 0;
        foreach (var entry in sourcePathPlan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = entry.IndexedEntry.Descriptor;
            if (deletedIds.Contains(descriptor.EntryId))
            {
                continue;
            }

            var finalPath = entry.RelativePath;
            if (renamesById.TryGetValue(descriptor.EntryId, out var rename))
            {
                finalPath = rename.FinalPath;
            }
            else
            {
                var move = actionTrie.FindMove(entry.RelativePath);
                if (move is not null)
                {
                    var suffix = entry.RelativePath.Length == move.SourcePath.Length
                        ? string.Empty
                        : entry.RelativePath[(move.SourcePath.Length + 1)..];
                    finalPath = suffix.Length == 0
                        ? move.FinalPath!
                        : $"{move.FinalPath}/{suffix}";
                }
            }

            if (!_pathPolicy.TryNormalize(finalPath, out finalPath, out _, out var reason))
            {
                throw new ArchiveProviderException(ArchiveErrorCategory.UnsafePath, "zip.rebuild_unsafe_archive_path", reason);
            }
            finalPath = finalPath.Replace('\\', '/');

            var timestamp = ClampTimestamp(descriptor.ModifiedTimeUtc, out var wasClamped);
            if (wasClamped)
            {
                retainedClampedTimestamps++;
            }

            output.Add(new PlannedZipRebuildCopyEntry(
                finalPath,
                descriptor.Kind,
                descriptor.Kind == ArchiveEntryKind.File ? descriptor.OriginalSize!.Value : 0,
                timestamp,
                SanitizeAttributes(descriptor.WindowsAttributes, descriptor.Kind == ArchiveEntryKind.Directory),
                ZipRebuildCopyEntrySource.RetainedArchive,
                RetainedEntry: entry.IndexedEntry));
        }

        var treeFileSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in mappedPlan.SourceTrees)
        {
            foreach (var manifestEntry in tree.Manifest)
            {
                if (manifestEntry.Kind == ArchiveEntryKind.File)
                {
                    treeFileSources.Add(Path.GetFullPath(Path.Combine(tree.SourcePath, manifestEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                }
            }
        }

        var outputByCollision = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, outputIndex) in output.Select((entry, outputIndex) => (entry, outputIndex)))
        {
            if (_pathPolicy.TryNormalize(entry.ArchivePath, out _, out var collisionKey, out _))
            {
                outputByCollision.TryAdd(collisionKey, outputIndex);
            }
        }
        foreach (var file in mappedPlan.Files)
        {
            var archivePath = file.ArchivePath.Replace('\\', '/');
            var isTreeFile = treeFileSources.Contains(Path.GetFullPath(file.SourcePath));
            if (!isTreeFile)
            {
                if (byPath.TryGetValue(CollisionKey(archivePath), out var original))
                {
                    if (deletedIds.Contains(original.IndexedEntry.Descriptor.EntryId))
                    {
                        throw InvalidTree("zip.rebuild_tree_edit_conflict", "A mapped replacement cannot target a deleted archive subtree.");
                    }

                    if (renamesById.TryGetValue(original.IndexedEntry.Descriptor.EntryId, out var explicitRename))
                    {
                        archivePath = explicitRename.FinalPath;
                    }
                    else
                    {
                        var move = actionTrie.FindMove(original.RelativePath);
                        if (move is not null)
                        {
                            var suffix = original.RelativePath.Length == move.SourcePath.Length
                                ? string.Empty
                                : original.RelativePath[(move.SourcePath.Length + 1)..];
                            archivePath = suffix.Length == 0 ? move.FinalPath! : $"{move.FinalPath}/{suffix}";
                        }
                    }
                }
                else if (actionTrie.IsDeletedPath(archivePath))
                {
                    throw InvalidTree("zip.rebuild_tree_edit_conflict", "A mapped replacement cannot target a deleted archive subtree.");
                }
            }

            AddMappedEntry(output, outputByCollision, new PlannedZipRebuildCopyEntry(
                archivePath,
                ArchiveEntryKind.File,
                file.SourceSnapshot.Length,
                file.ZipLastWriteTime,
                null,
                ZipRebuildCopyEntrySource.NewFile,
                NewFile: file),
                allowReplacement: !isTreeFile);
        }
        foreach (var directory in mappedPlan.Directories)
        {
            AddMappedEntry(output, outputByCollision, new PlannedZipRebuildCopyEntry(
                directory.ArchivePath.Replace('\\', '/'),
                ArchiveEntryKind.Directory,
                0,
                directory.ZipLastWriteTime,
                (int)FileAttributes.Directory,
                ZipRebuildCopyEntrySource.NewDirectory),
                allowReplacement: false);
        }

        AddRequiredSyntheticAncestors(output, cancellationToken);
        ValidateFinalNamespace(output, cancellationToken);
        output = OrderFinalEntries(output);
        if (output.Count > ProtocolConstants.MaximumZipCreationEntries)
        {
            throw Limit("zip.rebuild_entry_count_limit", "A rebuilt ZIP can contain at most 4,096 material entries.");
        }

        long totalBytes = 0;
        long ratioBytes = 0;
        foreach (var entry in output)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind != ArchiveEntryKind.File)
            {
                continue;
            }

            totalBytes = checked(totalBytes + entry.Length);
            ratioBytes = checked(ratioBytes + (entry.Source == ZipRebuildCopyEntrySource.RetainedArchive
                ? entry.RetainedEntry!.Descriptor.CompressedSize!.Value
                : entry.Length));
        }
        if (totalBytes > ProtocolConstants.MaximumZipCreationSourceBytes)
        {
            throw Limit("zip.rebuild_source_byte_limit", "The rebuilt ZIP exceeds the decoded source-byte limit.");
        }

        var plan = new ZipRebuildCopyPlan(
            Path.GetFullPath(destinationPath),
            output,
            mappedPlan.Files,
            totalBytes,
            ratioBytes,
            EstimateMaximumOutputBytes(output, totalBytes),
            checked(mappedPlan.ClampedTimestampCount + retainedClampedTimestamps))
        {
            NewSourceDirectories = mappedPlan.Directories,
            NewSourceTrees = mappedPlan.SourceTrees,
        };
        return plan;

        string CollisionKey(string archivePath)
        {
            if (!_pathPolicy.TryNormalize(archivePath, out _, out var collisionKey, out _))
            {
                return archivePath;
            }

            return collisionKey;
        }

        void AddMappedEntry(
            List<PlannedZipRebuildCopyEntry> entries,
            Dictionary<string, int> entriesByCollision,
            PlannedZipRebuildCopyEntry mapped,
            bool allowReplacement)
        {
            if (!_pathPolicy.TryNormalize(mapped.ArchivePath, out var normalized, out var collisionKey, out var reason))
            {
                throw new ArchiveProviderException(ArchiveErrorCategory.UnsafePath, "zip.rebuild_unsafe_archive_path", reason);
            }

            if (!entriesByCollision.TryGetValue(collisionKey, out var existing))
            {
                entriesByCollision.Add(collisionKey, entries.Count);
                entries.Add(mapped with { ArchivePath = normalized.Replace('\\', '/') });
                return;
            }

            if (!allowReplacement
                || entries[existing].Kind != ArchiveEntryKind.File
                || mapped.Kind != ArchiveEntryKind.File)
            {
                throw InvalidTree("zip.rebuild_tree_path_collision", "A mapped tree entry collides with an existing directory or tree entry.");
            }

            entries[existing] = mapped with { ArchivePath = normalized.Replace('\\', '/') };
        }

    }

    private static IReadOnlyList<ZipRebuildTreePlanNode> BuildNodes(
        ArchiveIndex index,
        ArchiveFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var map = BuildNodeMap(index, fingerprint, cancellationToken);
        var ordered = new List<ZipRebuildTreePlanNode>(map.Count);
        var sortedNodes = map.Values.ToArray();
        OrdinalRadixSort.Sort(sortedNodes, static node => node.Path);
        foreach (var node in sortedNodes)
        {
            ordered.Add(new ZipRebuildTreePlanNode(
                node.Path,
                node.Explicit,
                node.DescendantCount,
                node.MaterialCount,
                node.Revision));
        }

        return ordered;
    }

    private static TreeNodeMap BuildNodeMap(
        ArchiveIndex index,
        ArchiveFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var map = new TreeNodeMap();
        foreach (var entry in index.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.Descriptor.NormalizedPath?.TrimEnd('/') ?? string.Empty;
            if (path.Length == 0)
            {
                continue;
            }

            if (entry.Descriptor.Kind == ArchiveEntryKind.Directory)
            {
                var node = map.GetOrAdd(path, cancellationToken);
                node.Explicit |= !entry.IsSynthetic;
            }

            var components = path.Split('/');
            var prefix = string.Empty;
            for (var i = 0; i < components.Length - 1; i++)
            {
                prefix = prefix.Length == 0 ? components[i] : $"{prefix}/{components[i]}";
                var node = map.GetOrAdd(prefix, cancellationToken);
                node.DescendantCount = checked(node.DescendantCount + 1);
            }
        }

        foreach (var entry in index.ProviderOrderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.Descriptor.NormalizedPath?.TrimEnd('/') ?? string.Empty;
            if (path.Length == 0)
            {
                continue;
            }

            var memberDigest = ComputeTreeMemberDigest(entry, path);
            var components = path.Split('/');
            var prefix = string.Empty;
            for (var componentIndex = 0; componentIndex < components.Length - 1; componentIndex++)
            {
                prefix = prefix.Length == 0
                    ? components[componentIndex]
                    : $"{prefix}/{components[componentIndex]}";
                var node = map.GetOrAdd(prefix, cancellationToken);
                node.MaterialCount = checked(node.MaterialCount + 1);
                node.MemberDigests.Add(memberDigest);
            }
        }

        foreach (var node in map.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendInt64(aggregate, fingerprint.Length);
            AppendInt64(aggregate, fingerprint.LastWriteTimeUtc.UtcTicks);
            AppendUtf8(aggregate, fingerprint.FileIdentity ?? string.Empty);
            AppendUtf8(aggregate, fingerprint.PrefixSha256);
            AppendInt32(aggregate, (int)ArchiveEntryKind.Directory);
            AppendUtf8(aggregate, node.Path);
            AppendInt32(aggregate, node.MemberDigests.Count);
            foreach (var memberDigest in node.MemberDigests)
            {
                AppendBytes(aggregate, memberDigest);
            }

            node.Revision = Convert.ToHexString(aggregate.GetHashAndReset())
                .ToLowerInvariant();
        }

        return map;
    }

    private static byte[] ComputeTreeMemberDigest(IndexedArchiveEntry entry, string normalizedPath)
    {
        using var member = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(member, entry.ProviderEntry?.Ordinal ?? -1);
        AppendInt32(member, (int)entry.Descriptor.Kind);
        AppendUtf8(member, normalizedPath);
        AppendUtf8(member, entry.Descriptor.NormalizedPath ?? string.Empty);
        AppendInt32(member, entry.Descriptor.DuplicateOrdinal);
        return member.GetHashAndReset();
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendBytes(IncrementalHash hash, byte[] value)
    {
        AppendInt32(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }


    private static HashSet<EntryId> ResolveDeletes(
        IReadOnlyList<ArchiveEntryKey> keys,
        IReadOnlyDictionary<ArchiveEntryKey, PlannedExtractionEntry> byKey,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<EntryId>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key is null || !byKey.TryGetValue(key, out var entry) || !ids.Add(entry.IndexedEntry.Descriptor.EntryId))
            {
                throw InvalidTree("zip.rebuild_delete_key_invalid", "A tree edit deletion key is missing or not part of the opened snapshot.");
            }
        }

        return ids;
    }

    private static Dictionary<EntryId, (PlannedExtractionEntry Entry, string FinalPath)> ResolveRenames(
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyDictionary<ArchiveEntryKey, PlannedExtractionEntry> byKey,
        IReadOnlySet<EntryId> deletedIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<EntryId, (PlannedExtractionEntry, string)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathPolicy = new ExtractionPathPolicy();
        foreach (var rename in renames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rename?.EntryKey is null || !byKey.TryGetValue(rename.EntryKey, out var entry)
                || entry.IndexedEntry.Descriptor.Kind != ArchiveEntryKind.File
                || deletedIds.Contains(entry.IndexedEntry.Descriptor.EntryId)
                || result.ContainsKey(entry.IndexedEntry.Descriptor.EntryId)
                || string.IsNullOrWhiteSpace(rename.FinalArchivePath)
                || !pathPolicy.TryNormalize(rename.FinalArchivePath, out var path, out var collision, out var reason))
            {
                throw InvalidTree("zip.rebuild_rename_key_invalid", "A tree edit rename key or target is invalid.");
            }

            if (!targets.Add(collision))
            {
                throw InvalidTree("zip.rebuild_rename_target_collision", "Tree edit rename targets are not unique.");
            }

            result.Add(entry.IndexedEntry.Descriptor.EntryId, (entry, path));
        }

        return result;
    }

    private static List<ResolvedAction> ResolveActions(
        IReadOnlyList<ArchiveRebuildTreeAction> actions,
        IReadOnlyDictionary<string, TreeNode> nodes,
        CancellationToken cancellationToken)
    {
        var result = new List<ResolvedAction>(actions.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathPolicy = new ExtractionPathPolicy();
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action is null || string.IsNullOrWhiteSpace(action.NormalizedPath)
                || !pathPolicy.TryNormalize(action.NormalizedPath, out var sourcePath, out _, out _)
                || !nodes.TryGetValue(sourcePath.Replace('\\', '/'), out var node)
                || sourcePath.Length == 0
                || !string.Equals(action.ExpectedRevision, node.Revision, StringComparison.Ordinal)
                || !paths.Add(sourcePath))
            {
                throw InvalidTree("zip.rebuild_tree_action_invalid", "A tree action is missing, stale, duplicated, or targets a non-directory node.");
            }

            sourcePath = sourcePath.Replace('\\', '/');

            if (action.Kind == ArchiveRebuildTreeActionKind.Move)
            {
                if (!pathPolicy.TryNormalize(action.FinalArchivePath ?? string.Empty, out var finalPath, out _, out _))
                {
                    throw InvalidTree("zip.rebuild_tree_target_invalid", "A tree move target is unsafe or empty.");
                }

                finalPath = finalPath.Replace('\\', '/');

                if (IsSameOrDescendant(sourcePath, finalPath))
                {
                    throw InvalidTree("zip.rebuild_tree_self_descendant", "A tree move target cannot be the source subtree or one of its descendants.");
                }

                result.Add(new ResolvedAction(sourcePath, finalPath, action.Kind));
            }
            else if (action.Kind == ArchiveRebuildTreeActionKind.Delete)
            {
                if (action.FinalArchivePath is not null)
                {
                    throw InvalidTree("zip.rebuild_tree_target_invalid", "A tree delete cannot contain a move target.");
                }

                result.Add(new ResolvedAction(sourcePath, null, action.Kind));
            }
            else
            {
                throw InvalidTree("zip.rebuild_tree_action_invalid", "The tree action kind is unsupported.");
            }
        }

        return result;
    }

    private static ActionPathTrie ValidateActionRelationships(IReadOnlyList<ResolvedAction> actions)
    {
        var sourcePaths = new ActionPathTrie();
        foreach (var action in actions)
        {
            if (!sourcePaths.TryAdd(action))
            {
                throw InvalidTree("zip.rebuild_tree_overlap", "Tree actions cannot overlap or nest.");
            }
        }

        foreach (var action in actions)
        {
            if (action.FinalPath is null)
            {
                continue;
            }

            if (sourcePaths.HasDifferentSourcePrefix(action.FinalPath, action.SourcePath))
            {
                throw InvalidTree("zip.rebuild_tree_cycle", "Tree move actions cannot create a cyclic transform.");
            }
        }

        return sourcePaths;
    }

    private static void AddRequiredSyntheticAncestors(
        List<PlannedZipRebuildCopyEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count > ProtocolConstants.MaximumZipCreationEntries)
        {
            throw Limit("zip.rebuild_entry_count_limit", "A rebuilt ZIP can contain at most 4,096 material entries.");
        }

        var directories = new HashSet<string>(
            entries.Where(entry => entry.Kind == ArchiveEntryKind.Directory).Select(entry => entry.ArchivePath),
            StringComparer.OrdinalIgnoreCase);
        var additions = new List<PlannedZipRebuildCopyEntry>();
        foreach (var entry in entries.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = entry.ArchivePath.Split('/');
            var prefix = string.Empty;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                prefix = prefix.Length == 0 ? parts[index] : $"{prefix}/{parts[index]}";
                if (directories.Add(prefix))
                {
                    if (checked(entries.Count + additions.Count + 1) > ProtocolConstants.MaximumZipCreationEntries)
                    {
                        throw Limit("zip.rebuild_entry_count_limit", "A rebuilt ZIP can contain at most 4,096 material entries.");
                    }

                    additions.Add(new PlannedZipRebuildCopyEntry(
                        prefix,
                        ArchiveEntryKind.Directory,
                        0,
                        SyntheticDirectoryTime,
                        (int)FileAttributes.Directory,
                        ZipRebuildCopyEntrySource.NewDirectory));
                }
            }
        }

        entries.AddRange(additions);
    }

    private static void ValidateFinalNamespace(
        IReadOnlyList<PlannedZipRebuildCopyEntry> entries,
        CancellationToken cancellationToken)
    {
        var namespaceTrie = new FinalNamespaceTrie();
        var policy = new ExtractionPathPolicy();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!policy.TryNormalize(entry.ArchivePath, out _, out var collision, out _))
            {
                throw InvalidTree("zip.rebuild_unsafe_archive_path", "The final tree namespace contains an unsafe path.");
            }

            if (!namespaceTrie.TryClaim(collision, entry.Kind))
            {
                throw InvalidTree("zip.rebuild_tree_path_collision", "The final tree namespace contains a duplicate or file-directory collision.");
            }
        }
    }

    private static List<PlannedZipRebuildCopyEntry> OrderFinalEntries(
        IReadOnlyList<PlannedZipRebuildCopyEntry> entries)
    {
        var ordered = entries.ToArray();
        // Stable least-significant fields first gives the required final
        // ordering: normalized path, then kind, then persistent/mapped token.
        OrdinalRadixSort.Sort(ordered, static entry => GetFinalEntryToken(entry));
        OrdinalRadixSort.Sort(ordered, static entry => (byte)entry.Kind);
        OrdinalRadixSort.Sort(ordered, static entry => entry.ArchivePath);
        return ordered.ToList();
    }

    private static string GetFinalEntryToken(PlannedZipRebuildCopyEntry entry) =>
        entry.RetainedEntry is { } retained
            ? $"{retained.Descriptor.NormalizedPath}|{retained.Descriptor.DuplicateOrdinal}"
            : entry.NewFile?.SourcePath ?? string.Empty;

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        root = root.Replace('\\', '/').TrimEnd('/');
        candidate = candidate.Replace('\\', '/').TrimEnd('/');
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            || (candidate.Length > root.Length
                && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && candidate[root.Length] == '/');
    }

    private static long Utf8Length(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private static long EstimateMaximumOutputBytes(
        IReadOnlyList<PlannedZipRebuildCopyEntry> entries,
        long sourceBytes)
    {
        try
        {
            long pathMetadata = 0;
            foreach (var entry in entries)
            {
                pathMetadata = checked(pathMetadata + 512 + ((long)entry.ArchivePath.Length * 6));
            }

            return checked(sourceBytes + (sourceBytes / 8) + pathMetadata + (1024L * 1024));
        }
        catch (OverflowException)
        {
            throw Limit("zip.rebuild_output_estimate_overflow", "The ZIP tree edit output estimate overflowed safe accounting limits.");
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

    private static DateTimeOffset ClampTimestamp(DateTimeOffset? timestamp, out bool wasClamped)
    {
        if (timestamp is null)
        {
            wasClamped = true;
            return SyntheticDirectoryTime;
        }

        var local = timestamp.Value.ToLocalTime();
        if (local.DateTime < MinimumZipLocalTime)
        {
            wasClamped = true;
            return new DateTimeOffset(MinimumZipLocalTime, TimeZoneInfo.Local.GetUtcOffset(MinimumZipLocalTime));
        }
        if (local.DateTime > MaximumZipLocalTime)
        {
            wasClamped = true;
            return new DateTimeOffset(MaximumZipLocalTime, TimeZoneInfo.Local.GetUtcOffset(MaximumZipLocalTime));
        }

        wasClamped = false;
        return local;
    }

    private static ArchiveProviderException InvalidTree(string code, string message) =>
        new(ArchiveErrorCategory.InvalidRequest, code, message);

    private static ArchiveProviderException Limit(string code, string message) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message);

    private sealed class TreeNodeMap
    {
        private readonly Dictionary<string, TreeNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private long _pathBytes;

        public int Count => _nodes.Count;

        public IReadOnlyDictionary<string, TreeNode> Nodes => _nodes;

        public IEnumerable<TreeNode> Values => _nodes.Values;

        public TreeNode GetOrAdd(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_nodes.TryGetValue(path, out var existing))
            {
                return existing;
            }

            if (_nodes.Count >= ProtocolConstants.MaximumZipRebuildTreeActions)
            {
                throw Limit(
                    "zip.rebuild_tree_plan_limit",
                    "The ZIP tree plan exceeds the bounded protocol tree-plan limits.");
            }

            var nextBytes = checked(_pathBytes + Encoding.UTF8.GetByteCount(path));
            if (nextBytes > ProtocolConstants.MaximumZipRebuildPathPayloadBytes)
            {
                throw Limit(
                    "zip.rebuild_tree_plan_limit",
                    "The ZIP tree plan exceeds the bounded protocol tree-plan limits.");
            }

            var node = new TreeNode(path);
            _nodes.Add(path, node);
            _pathBytes = nextBytes;
            return node;
        }
    }

    /// <summary>
    /// Stable iterative MSD radix ordering for exact StringComparer.Ordinal
    /// strings. Keys use big-endian UTF-16 bytes, shifted by one so zero is a
    /// shorter-key terminator. The 257 buckets are reused for every range.
    /// </summary>
    private static class OrdinalRadixSort
    {
        public static void Sort<T>(T[] values, Func<T, string> keySelector)
        {
            ArgumentNullException.ThrowIfNull(values);
            ArgumentNullException.ThrowIfNull(keySelector);
            var items = new RadixItem<T>[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                items[index] = new RadixItem<T>(values[index], Encode(keySelector(values[index])));
            }

            Sort(items);
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = items[index].Value;
            }
        }

        public static void Sort<T>(T[] values, Func<T, byte> keySelector)
        {
            ArgumentNullException.ThrowIfNull(values);
            ArgumentNullException.ThrowIfNull(keySelector);
            var items = new RadixItem<T>[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                items[index] = new RadixItem<T>(values[index], [keySelector(values[index])]);
            }

            Sort(items);
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = items[index].Value;
            }
        }

        private static void Sort<T>(RadixItem<T>[] items)
        {
            if (items.Length < 2)
            {
                return;
            }

            var scratch = new RadixItem<T>[items.Length];
            var counts = new int[257];
            var positions = new int[257];
            var writePositions = new int[257];
            var ranges = new Stack<(int Start, int Length, int Depth)>();
            ranges.Push((0, items.Length, 0));
            while (ranges.Count > 0)
            {
                var (start, length, depth) = ranges.Pop();
                if (length < 2)
                {
                    continue;
                }

                Array.Clear(counts);
                for (var index = start; index < start + length; index++)
                {
                    counts[Symbol(items[index].Key, depth)]++;
                }

                var offset = start;
                for (var bucket = 0; bucket < counts.Length; bucket++)
                {
                    positions[bucket] = offset;
                    offset += counts[bucket];
                }

                Array.Copy(positions, writePositions, positions.Length);
                for (var index = start; index < start + length; index++)
                {
                    var bucket = Symbol(items[index].Key, depth);
                    scratch[writePositions[bucket]++] = items[index];
                }

                Array.Copy(scratch, start, items, start, length);
                for (var bucket = counts.Length - 1; bucket > 0; bucket--)
                {
                    if (counts[bucket] > 1)
                    {
                        ranges.Push((positions[bucket], counts[bucket], depth + 1));
                    }
                }
            }
        }

        private static int Symbol(byte[] key, int depth) =>
            depth < key.Length ? key[depth] + 1 : 0;

        private static byte[] Encode(string value)
        {
            var key = new byte[checked(value.Length * 2)];
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                key[index * 2] = (byte)(character >> 8);
                key[(index * 2) + 1] = (byte)character;
            }

            return key;
        }

        private readonly record struct RadixItem<T>(T Value, byte[] Key);
    }

    private sealed class ActionPathTrie
    {
        private readonly ComponentNode _root = new();

        public bool TryAdd(ResolvedAction action)
        {
            var components = SplitComponents(action.SourcePath);
            var node = _root;
            foreach (var component in components)
            {
                if (node.Action is not null)
                {
                    return false;
                }

                node = node.Children.TryGetValue(component, out var child)
                    ? child
                    : node.Children[component] = new ComponentNode();
            }

            if (node.Action is not null || node.HasDescendant)
            {
                return false;
            }

            node.Action = action;
            MarkAncestors(action.SourcePath);
            return true;
        }

        public bool HasDifferentSourcePrefix(string path, string sourcePath)
        {
            var components = SplitComponents(path);
            var node = _root;
            for (var index = 0; index < components.Length; index++)
            {
                if (!node.Children.TryGetValue(components[index], out node!))
                {
                    return false;
                }

                if (node.Action is { } action
                    && !string.Equals(action.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public ResolvedAction? FindMove(string path) =>
            Find(path, ArchiveRebuildTreeActionKind.Move);

        public bool IsDeletedPath(string path) =>
            Find(path, ArchiveRebuildTreeActionKind.Delete) is not null;

        private ResolvedAction? Find(string path, ArchiveRebuildTreeActionKind kind)
        {
            var node = _root;
            ResolvedAction? result = null;
            foreach (var component in SplitComponents(path))
            {
                if (!node.Children.TryGetValue(component, out node!))
                {
                    break;
                }

                if (node.Action is { Kind: var actionKind } action && actionKind == kind)
                {
                    result = action;
                }
            }

            return result;
        }

        private void MarkAncestors(string path)
        {
            var node = _root;
            foreach (var component in SplitComponents(path))
            {
                node.HasDescendant = true;
                node = node.Children[component];
            }
        }

        private static string[] SplitComponents(string path) =>
            path.Replace('\\', '/').TrimEnd('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

        private sealed class ComponentNode
        {
            public Dictionary<string, ComponentNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
            public ResolvedAction? Action { get; set; }
            public bool HasDescendant { get; set; }
        }
    }

    private sealed class FinalNamespaceTrie
    {
        private readonly Node _root = new();

        public bool TryClaim(string path, ArchiveEntryKind kind)
        {
            var node = _root;
            var components = path.Replace('\\', '/').TrimEnd('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < components.Length; index++)
            {
                if (node.Kind == ArchiveEntryKind.File)
                {
                    return false;
                }

                node = node.Children.TryGetValue(components[index], out var child)
                    ? child
                    : node.Children[components[index]] = new Node();
            }

            if (node.Kind is not null
                || (kind == ArchiveEntryKind.File && node.Children.Count > 0))
            {
                return false;
            }

            node.Kind = kind;
            return true;
        }

        private sealed class Node
        {
            public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
            public ArchiveEntryKind? Kind { get; set; }
        }
    }

    private sealed class TreeNode(string path)
    {
        public string Path { get; } = path;
        public bool Explicit { get; set; }
        public int DescendantCount { get; set; }
        public int MaterialCount { get; set; }
        public List<byte[]> MemberDigests { get; } = [];
        public string Revision { get; set; } = string.Empty;
    }

    private sealed record ResolvedAction(
        string SourcePath,
        string? FinalPath,
        ArchiveRebuildTreeActionKind Kind);
}
