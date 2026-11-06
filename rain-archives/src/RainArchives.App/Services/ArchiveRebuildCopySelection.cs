using RainArchives.Contracts;

namespace RainArchives.App.Services;

public sealed record ArchiveRebuildCopySelection(
    string DestinationPath,
    IReadOnlyList<ArchiveEntryKey> EntriesToDelete,
    IReadOnlyList<ArchiveRebuildCopyFile> Files,
    ArchiveCompressionPreset CompressionPreset,
    IReadOnlyList<ArchiveRebuildRename>? Renames = null,
    IReadOnlyList<ArchiveCreationDirectory>? Directories = null,
    IReadOnlyList<ArchiveRebuildTreeAction>? TreeActions = null,
    IReadOnlyList<ArchiveRebuildMetadataDirective>? Metadata = null);
