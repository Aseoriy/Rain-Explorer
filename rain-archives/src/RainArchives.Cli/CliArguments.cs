using System.Globalization;
using RainArchives.Contracts;

namespace RainArchives.Cli;

public enum CliCommand
{
    List,
    Test,
    Extract,
    CreateZip,
    CreateTar,
    CreateTarGZip,
    CreateTarBZip2,
    CreateGZip,
    CreateBZip2,
    RebuildZip,
    RecoveryList,
    RecoveryInspect,
    RecoveryApply,
    NestedRecoveryList,
    NestedRecoveryInspect,
    NestedRecoveryDiscard,
    NestedRecoveryReopen,
    SupportDiagnostics,
    Capabilities,
}

public sealed record CliRecoveryDisposition(
    ArchiveCreationRecoveryDispositionAction Action,
    ArchiveCreationRecoveryPhase ExpectedPhase,
    int ExpectedSequence,
    DateTimeOffset ExpectedRecordedAtUtc);

public sealed record CliNestedWorkspaceRecoveryDiscard(
    string ExpectedRevision,
    int ExpectedSourceCount,
    long ExpectedTotalSourceBytes);

public sealed record CliNestedWorkspaceRecoveryReopen(
    string ExpectedRevision,
    int ExpectedSourceCount,
    long ExpectedTotalSourceBytes,
    string SourceId,
    long ExpectedSourceLength,
    string ExpectedContentSha256);

public sealed record CliRebuildCopyFile(
    string SourcePath,
    string ArchivePath,
    bool IsReplacement);

public sealed record CliRebuildRename(
    ArchiveEntryKey EntryKey,
    string FinalArchivePath);

public sealed record CliRebuildTreeAction(
    string NormalizedPath,
    string ExpectedRevision,
    ArchiveRebuildTreeActionKind Kind,
    string? FinalArchivePath = null);

public sealed record CliArguments(
    CliCommand Command,
    string ArchivePath,
    string? DestinationPath,
    string WorkerPath,
    bool PasswordFromStandardInput,
    IReadOnlyList<string> EntryPaths,
    IReadOnlyList<ArchiveCreationFile> CreationFiles,
    IReadOnlyList<ArchiveCreationDirectory> CreationDirectories,
    IReadOnlyList<ArchiveEntryKey> RebuildEntriesToDelete,
    IReadOnlyList<CliRebuildCopyFile> RebuildFiles,
    IReadOnlyList<CliRebuildRename> RebuildRenames,
    ArchiveCompressionPreset CompressionPreset,
    string? RecoveryId,
    CliRecoveryDisposition? RecoveryDisposition,
    CliNestedWorkspaceRecoveryDiscard? NestedRecoveryDiscard,
    IReadOnlyList<ArchiveCreationDirectory>? RebuildDirectories = null,
    IReadOnlyList<CliRebuildTreeAction>? RebuildTreeActions = null,
    IReadOnlyList<ArchiveRebuildMetadataDirective>? RebuildMetadata = null,
    string? RawSourcePath = null,
    CliNestedWorkspaceRecoveryReopen? NestedRecoveryReopen = null)
{
    public static bool TryParse(
        string[] args,
        out CliArguments options,
        out string? error)
    {
        options = null!;
        error = null;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            return false;
        }

        CliCommand command;
        if (string.Equals(args[0], "create-zip", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.CreateZip;
        }
        else if (string.Equals(args[0], "create-tar", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.CreateTar;
        }
        else if (string.Equals(args[0], "create-tar-gz", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.CreateTarGZip;
        }
        else if (string.Equals(args[0], "create-tar-bz2", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.CreateTarBZip2;
        }
        else if (string.Equals(args[0], "create-gzip", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.CreateGZip;
        }
        else if (string.Equals(args[0], "create-bzip2", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.CreateBZip2;
        }
        else if (string.Equals(args[0], "rebuild-zip", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.RebuildZip;
        }
        else if (string.Equals(args[0], "recovery-list", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.RecoveryList;
        }
        else if (string.Equals(args[0], "recovery-inspect", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.RecoveryInspect;
        }
        else if (string.Equals(args[0], "recovery-apply", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.RecoveryApply;
        }
        else if (string.Equals(args[0], "nested-recovery-list", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.NestedRecoveryList;
        }
        else if (string.Equals(args[0], "nested-recovery-inspect", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.NestedRecoveryInspect;
        }
        else if (string.Equals(args[0], "nested-recovery-discard", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.NestedRecoveryDiscard;
        }
        else if (string.Equals(args[0], "nested-recovery-reopen", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.NestedRecoveryReopen;
        }
        else if (string.Equals(args[0], "support-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.SupportDiagnostics;
        }
        else if (string.Equals(args[0], "capabilities", StringComparison.OrdinalIgnoreCase))
        {
            command = CliCommand.Capabilities;
        }
        else if (!Enum.TryParse(args[0], ignoreCase: true, out command))
        {
            error = $"Unknown command: {args[0]}";
            return false;
        }

        var positional = new List<string>();
        var entries = new List<string>();
        var creationFiles = new List<(string SourcePath, string ArchivePath)>();
        var creationDirectories = new List<(string SourcePath, string ArchivePath)>();
        var rebuildEntriesToDelete = new List<ArchiveEntryKey>();
        var rebuildFiles = new List<(string SourcePath, string ArchivePath, bool IsReplacement)>();
        var rebuildRenames = new List<(ArchiveEntryKey EntryKey, string FinalArchivePath)>();
        var rebuildDirectories = new List<(string SourcePath, string ArchivePath)>();
        var rebuildTreeActions = new List<CliRebuildTreeAction>();
        var rebuildMetadata = new List<ArchiveRebuildMetadataDirective>();
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        var workerSpecified = false;
        var passwordFromStandardInput = false;
        var passwordOptionSpecified = false;
        var compressionPreset = command == CliCommand.CreateTar
            ? ArchiveCompressionPreset.Store
            : ArchiveCompressionPreset.Balanced;
        var compressionPresetSpecified = false;
        string? expectedPhaseText = null;
        string? expectedSequenceText = null;
        string? expectedRecordedAtText = null;
        string? expectedRevision = null;
        string? expectedSourceCountText = null;
        string? expectedTotalSourceBytesText = null;
        string? expectedSourceLengthText = null;
        string? expectedContentSha256Text = null;
        string? confirmation = null;
        var rebuildConfirmationSpecified = false;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--worker" when workerSpecified:
                    error = "--worker may be specified only once.";
                    return false;
                case "--worker" when index + 1 < args.Length:
                    workerSpecified = true;
                    worker = args[++index];
                    break;
                case "--worker":
                    error = "--worker requires an executable path.";
                    return false;
                case "--entry" when index + 1 < args.Length:
                    entries.Add(args[++index]);
                    break;
                case "--entry":
                    error = "--entry requires an archive entry path.";
                    return false;
                case "--file" when index + 2 < args.Length:
                    creationFiles.Add((args[++index], args[++index]));
                    break;
                case "--file":
                    error = "--file requires a source path followed by an archive-relative path.";
                    return false;
                case "--directory" when index + 2 < args.Length:
                    creationDirectories.Add((args[++index], args[++index]));
                    break;
                case "--directory":
                    error = "--directory requires a source path followed by an archive-relative path.";
                    return false;
                case "--delete" when index + 2 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && (!args[index + 2].StartsWith('-')
                        || int.TryParse(
                            args[index + 2],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out _)):
                    var deletePath = args[++index];
                    var deleteOrdinalText = args[++index];
                    if (!TryNormalizeRebuildArchivePath(
                            deletePath,
                            out var normalizedDeletePath,
                            allowDirectory: true))
                    {
                        error = "--delete requires a valid archive-relative path.";
                        return false;
                    }

                    if (!int.TryParse(
                            deleteOrdinalText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var deleteOrdinal)
                        || deleteOrdinal < 0)
                    {
                        error = "--delete requires a non-negative decimal duplicate ordinal.";
                        return false;
                    }

                    rebuildEntriesToDelete.Add(new ArchiveEntryKey(normalizedDeletePath, deleteOrdinal));
                    break;
                case "--delete":
                    error = "--delete requires an archive path followed by a duplicate ordinal.";
                    return false;
                case "--add" when index + 2 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && !args[index + 2].StartsWith('-'):
                    var addPath = args[++index];
                    var addArchivePath = args[++index];
                    if (!TryNormalizeRebuildArchivePath(addArchivePath, out var normalizedAddArchivePath))
                    {
                        error = "--add requires a valid archive-relative destination path.";
                        return false;
                    }

                    rebuildFiles.Add((addPath, normalizedAddArchivePath, false));
                    break;
                case "--add":
                    error = "--add requires a source path followed by an archive-relative path.";
                    return false;
                case "--replace" when index + 2 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && !args[index + 2].StartsWith('-'):
                    var replacePath = args[++index];
                    var replaceArchivePath = args[++index];
                    if (!TryNormalizeRebuildArchivePath(replaceArchivePath, out var normalizedReplaceArchivePath))
                    {
                        error = "--replace requires a valid archive-relative destination path.";
                        return false;
                    }

                    rebuildFiles.Add((replacePath, normalizedReplaceArchivePath, true));
                    break;
                case "--replace":
                    error = "--replace requires a source path followed by an archive-relative path.";
                    return false;
                case "--add-folder" when index + 2 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && !args[index + 2].StartsWith('-'):
                    var addFolderSource = args[++index];
                    var addFolderArchivePath = args[++index];
                    if (!TryNormalizeRebuildArchivePath(
                            addFolderArchivePath,
                            out var normalizedAddFolderArchivePath,
                            allowDirectory: true))
                    {
                        error = "--add-folder requires a valid archive-relative directory path.";
                        return false;
                    }

                    rebuildDirectories.Add((addFolderSource, normalizedAddFolderArchivePath));
                    break;
                case "--add-folder":
                    error = "--add-folder requires a source directory followed by an archive-relative directory path.";
                    return false;
                case "--move-folder" when index + 3 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && !args[index + 2].StartsWith('-')
                    && !args[index + 3].StartsWith('-'):
                    var moveFolderPath = args[++index];
                    var moveFolderRevision = args[++index];
                    var moveFolderFinalPrefix = args[++index];
                    if (!TryNormalizeRebuildArchivePath(moveFolderPath, out var normalizedMoveFolderPath, allowDirectory: true)
                        || !TryNormalizeRebuildArchivePath(moveFolderFinalPrefix, out var normalizedMoveFolderFinalPrefix, allowDirectory: true)
                        || !IsValidRecoveryId(moveFolderRevision))
                    {
                        error = "--move-folder requires normalized path, revision, and final archive-relative directory prefix.";
                        return false;
                    }

                    rebuildTreeActions.Add(new CliRebuildTreeAction(
                        normalizedMoveFolderPath,
                        moveFolderRevision,
                        ArchiveRebuildTreeActionKind.Move,
                        normalizedMoveFolderFinalPrefix));
                    break;
                case "--move-folder":
                    error = "--move-folder requires normalized path, revision, and final archive-relative directory prefix.";
                    return false;
                case "--delete-folder" when index + 2 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && !args[index + 2].StartsWith('-'):
                    var deleteFolderPath = args[++index];
                    var deleteFolderRevision = args[++index];
                    if (!TryNormalizeRebuildArchivePath(deleteFolderPath, out var normalizedDeleteFolderPath, allowDirectory: true)
                        || !IsValidRecoveryId(deleteFolderRevision))
                    {
                        error = "--delete-folder requires normalized path and revision.";
                        return false;
                    }

                    rebuildTreeActions.Add(new CliRebuildTreeAction(
                        normalizedDeleteFolderPath,
                        deleteFolderRevision,
                        ArchiveRebuildTreeActionKind.Delete));
                    break;
                case "--delete-folder":
                    error = "--delete-folder requires normalized path and revision.";
                    return false;
                case "--metadata-key" when index + 4 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && !args[index + 2].StartsWith('-')
                    && IsMetadataValue(args[index + 3])
                    && IsMetadataValue(args[index + 4]):
                    var metadataKeyPath = args[++index];
                    var metadataKeyOrdinalText = args[++index];
                    var metadataKeyTimeText = args[++index];
                    var metadataKeyAttributesText = args[++index];
                    if (!TryNormalizeRebuildArchivePath(
                            metadataKeyPath,
                            out var normalizedMetadataKeyPath,
                            allowDirectory: true)
                        || !int.TryParse(
                            metadataKeyOrdinalText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var metadataKeyOrdinal)
                        || metadataKeyOrdinal < 0
                        || !CliMetadataSyntax.TryParseValues(
                            metadataKeyTimeText,
                            metadataKeyAttributesText,
                            ArchiveEntryKind.File,
                            out var metadataKeyTime,
                            out var metadataKeyAttributes))
                    {
                        error = "--metadata-key requires a normalized path, non-negative ordinal, exact archive time, and allowed attributes (use '-' to leave a field unchanged).";
                        return false;
                    }

                    rebuildMetadata.Add(new ArchiveRebuildMetadataDirective(
                        new ArchiveRebuildMetadataTarget(
                            ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                            new ArchiveEntryKey(normalizedMetadataKeyPath, metadataKeyOrdinal)),
                        metadataKeyTime,
                        metadataKeyAttributes));
                    break;
                case "--metadata-path" when index + 4 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && !args[index + 2].StartsWith('-')
                    && IsMetadataValue(args[index + 3])
                    && IsMetadataValue(args[index + 4]):
                    var metadataPath = args[++index];
                    var metadataKindText = args[++index];
                    var metadataTimeText = args[++index];
                    var metadataAttributesText = args[++index];
                    if (!TryNormalizeRebuildArchivePath(
                            metadataPath,
                            out var normalizedMetadataPath,
                            allowDirectory: true)
                        || !TryParseMetadataKind(metadataKindText, out var metadataKind)
                        || !CliMetadataSyntax.TryParseValues(
                            metadataTimeText,
                            metadataAttributesText,
                            metadataKind,
                            out var metadataTime,
                            out var metadataAttributes))
                    {
                        error = "--metadata-path requires a normalized final path, file or directory kind, exact archive time, and allowed attributes (use '-' to leave a field unchanged).";
                        return false;
                    }

                    rebuildMetadata.Add(new ArchiveRebuildMetadataDirective(
                        new ArchiveRebuildMetadataTarget(
                            ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                            MappedFinalArchivePath: normalizedMetadataPath,
                            MappedKind: metadataKind),
                        metadataTime,
                        metadataAttributes));
                    break;
                case "--metadata-key":
                    error = "--metadata-key requires a normalized path, non-negative ordinal, exact archive time, and allowed attributes.";
                    return false;
                case "--metadata-path":
                    error = "--metadata-path requires a normalized final path, file or directory kind, exact archive time, and allowed attributes.";
                    return false;
                case "--rename" when index + 3 < args.Length
                    && !args[index + 1].StartsWith('-')
                    && (!args[index + 2].StartsWith('-')
                        || int.TryParse(
                            args[index + 2],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out _))
                    && !args[index + 3].StartsWith('-'):
                    var renamePath = args[++index];
                    var renameOrdinalText = args[++index];
                    var renameFinalPath = args[++index];
                    if (!TryNormalizeRebuildArchivePath(
                            renamePath,
                            out var normalizedRenamePath))
                    {
                        error = "--rename requires a valid normalized persistent archive path.";
                        return false;
                    }

                    if (!int.TryParse(
                            renameOrdinalText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var renameOrdinal)
                        || renameOrdinal < 0)
                    {
                        error = "--rename requires a non-negative decimal duplicate ordinal.";
                        return false;
                    }

                    if (!TryNormalizeRebuildArchivePath(
                            renameFinalPath,
                            out var normalizedRenameFinalPath))
                    {
                        error = "--rename requires a valid final archive-relative file path.";
                        return false;
                    }

                    rebuildRenames.Add((
                        new ArchiveEntryKey(normalizedRenamePath, renameOrdinal),
                        normalizedRenameFinalPath));
                    break;
                case "--rename":
                    error = "--rename requires an archive path, duplicate ordinal, and final archive-relative file path.";
                    return false;
                case "--confirm-rebuild-copy" when rebuildConfirmationSpecified:
                    error = "--confirm-rebuild-copy may be specified only once.";
                    return false;
                case "--confirm-rebuild-copy":
                    rebuildConfirmationSpecified = true;
                    break;
                case "--preset" when compressionPresetSpecified:
                    error = "--preset may be specified only once.";
                    return false;
                case "--preset" when index + 1 < args.Length:
                    compressionPresetSpecified = true;
                    var presetText = args[++index];
                    if (!Enum.TryParse<ArchiveCompressionPreset>(
                            presetText,
                            ignoreCase: true,
                            out compressionPreset)
                        || !Enum.IsDefined(compressionPreset)
                        || !string.Equals(
                            presetText,
                            compressionPreset.ToString(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        error = "--preset must be store, fast, balanced, or maximum.";
                        return false;
                    }

                    break;
                case "--preset":
                    error = "--preset requires store, fast, balanced, or maximum.";
                    return false;
                case "--password-stdin" when !passwordOptionSpecified:
                    passwordOptionSpecified = true;
                    passwordFromStandardInput = true;
                    break;
                case "--password-stdin":
                    error = "--password-stdin may be specified only once.";
                    return false;
                case "--expected-phase" when expectedPhaseText is not null:
                    error = "--expected-phase may be specified only once.";
                    return false;
                case "--expected-phase" when index + 1 < args.Length:
                    expectedPhaseText = args[++index];
                    break;
                case "--expected-phase":
                    error = "--expected-phase requires a recovery journal phase.";
                    return false;
                case "--expected-sequence" when expectedSequenceText is not null:
                    error = "--expected-sequence may be specified only once.";
                    return false;
                case "--expected-sequence" when index + 1 < args.Length:
                    expectedSequenceText = args[++index];
                    break;
                case "--expected-sequence":
                    error = "--expected-sequence requires a recovery journal sequence.";
                    return false;
                case "--expected-recorded-at" when expectedRecordedAtText is not null:
                    error = "--expected-recorded-at may be specified only once.";
                    return false;
                case "--expected-recorded-at" when index + 1 < args.Length:
                    expectedRecordedAtText = args[++index];
                    break;
                case "--expected-recorded-at":
                    error = "--expected-recorded-at requires the journal's UTC timestamp.";
                    return false;
                case "--expected-revision" when expectedRevision is not null:
                    error = "--expected-revision may be specified only once.";
                    return false;
                case "--expected-revision" when index + 1 < args.Length:
                    expectedRevision = args[++index];
                    break;
                case "--expected-revision":
                    error = "--expected-revision requires the inspected revision.";
                    return false;
                case "--expected-source-count" when expectedSourceCountText is not null:
                    error = "--expected-source-count may be specified only once.";
                    return false;
                case "--expected-source-count" when index + 1 < args.Length:
                    expectedSourceCountText = args[++index];
                    break;
                case "--expected-source-count":
                    error = "--expected-source-count requires the inspected source count.";
                    return false;
                case "--expected-total-bytes" when expectedTotalSourceBytesText is not null:
                    error = "--expected-total-bytes may be specified only once.";
                    return false;
                case "--expected-total-bytes" when index + 1 < args.Length:
                    expectedTotalSourceBytesText = args[++index];
                    break;
                case "--expected-total-bytes":
                    error = "--expected-total-bytes requires the inspected byte total.";
                    return false;
                case "--expected-source-length" when expectedSourceLengthText is not null:
                    error = "--expected-source-length may be specified only once.";
                    return false;
                case "--expected-source-length" when index + 1 < args.Length:
                    expectedSourceLengthText = args[++index];
                    break;
                case "--expected-source-length":
                    error = "--expected-source-length requires the inspected source length.";
                    return false;
                case "--expected-content-sha256" when expectedContentSha256Text is not null:
                    error = "--expected-content-sha256 may be specified only once.";
                    return false;
                case "--expected-content-sha256" when index + 1 < args.Length:
                    expectedContentSha256Text = args[++index];
                    break;
                case "--expected-content-sha256":
                    error = "--expected-content-sha256 requires the inspected SHA-256 value.";
                    return false;
                case "--confirm" when confirmation is not null:
                    error = "--confirm may be specified only once.";
                    return false;
                case "--confirm" when index + 1 < args.Length:
                    confirmation = args[++index];
                    break;
                case "--confirm":
                    error = "--confirm requires the recovery identifier.";
                    return false;
                default:
                    if (args[index].StartsWith('-'))
                    {
                        error = $"Unknown option: {args[index]}";
                        return false;
                    }

                    positional.Add(args[index]);
                    break;
            }
        }

        if (command != CliCommand.Extract && entries.Count > 0)
        {
            error = "--entry is valid only for extract.";
            return false;
        }

        if (!IsTreeCreationCommand(command)
            && creationFiles.Count > 0)
        {
            error = "--file is valid only for ZIP or TAR-family creation.";
            return false;
        }

        if (!IsTreeCreationCommand(command)
            && creationDirectories.Count > 0)
        {
            error = "--directory is valid only for ZIP or TAR-family creation.";
            return false;
        }

        var rebuildTreeSpecificOptionSpecified = rebuildDirectories.Count > 0
            || rebuildTreeActions.Count > 0;
        if (command != CliCommand.RebuildZip && rebuildTreeSpecificOptionSpecified)
        {
            error = "--add-folder, --move-folder, and --delete-folder are valid only for rebuild-zip.";
            return false;
        }

        if (command != CliCommand.RebuildZip && rebuildMetadata.Count > 0)
        {
            error = "--metadata-key and --metadata-path are valid only for rebuild-zip.";
            return false;
        }

        if (!IsCreationCommand(command)
            && compressionPresetSpecified)
        {
            if (command != CliCommand.RebuildZip)
            {
                error = "--preset is valid only for archive creation or rebuild-zip.";
                return false;
            }
        }

        if (command == CliCommand.CreateTar
            && compressionPreset != ArchiveCompressionPreset.Store)
        {
            error = "Create-tar supports only --preset store because uncompressed TAR has no compression level.";
            return false;
        }

        if (command is CliCommand.CreateTarBZip2 or CliCommand.CreateBZip2
            && compressionPreset != ArchiveCompressionPreset.Balanced)
        {
            error = "Managed BZIP2 creation supports only --preset balanced.";
            return false;
        }

        var rebuildSpecificOptionSpecified = rebuildEntriesToDelete.Count > 0
            || rebuildFiles.Count > 0
            || rebuildRenames.Count > 0
            || rebuildTreeSpecificOptionSpecified
            || rebuildMetadata.Count > 0
            || rebuildConfirmationSpecified;
        if (command != CliCommand.RebuildZip && rebuildSpecificOptionSpecified)
        {
            error = "--delete, --add, --replace, --rename, --add-folder, --move-folder, --delete-folder, --metadata-key, --metadata-path, and --confirm-rebuild-copy are valid only for rebuild-zip.";
            return false;
        }

        if (command == CliCommand.RebuildZip && !rebuildConfirmationSpecified)
        {
            error = "Rebuild-zip requires --confirm-rebuild-copy.";
            return false;
        }

        if (IsCreationCommand(command)
            && passwordFromStandardInput)
        {
            error = "Managed archive creation does not support encryption or --password-stdin.";
            return false;
        }

        if (passwordFromStandardInput
            && command is not CliCommand.NestedRecoveryReopen
            && (command is CliCommand.RecoveryList
                or CliCommand.RecoveryInspect
                or CliCommand.RecoveryApply
                or CliCommand.NestedRecoveryList
                or CliCommand.NestedRecoveryInspect
                or CliCommand.NestedRecoveryDiscard))
        {
            error = "Recovery diagnostics do not accept --password-stdin.";
            return false;
        }

        if (command == CliCommand.Capabilities && passwordFromStandardInput)
        {
            error = "Capabilities diagnostics do not accept --password-stdin.";
            return false;
        }

        if (command == CliCommand.SupportDiagnostics && passwordFromStandardInput)
        {
            error = "Support diagnostics do not accept --password-stdin.";
            return false;
        }

        var recoveryApplySpecificOptionSpecified = expectedPhaseText is not null
            || expectedSequenceText is not null
            || expectedRecordedAtText is not null;
        if (command != CliCommand.RecoveryApply && recoveryApplySpecificOptionSpecified)
        {
            error = "Recovery disposition options are valid only for recovery-apply.";
            return false;
        }

        var nestedDiscardSpecificOptionSpecified = expectedRevision is not null
            || expectedSourceCountText is not null
            || expectedTotalSourceBytesText is not null;
        var nestedReopenSpecificOptionSpecified = expectedSourceLengthText is not null
            || expectedContentSha256Text is not null;
        if (command is not CliCommand.NestedRecoveryDiscard
                and not CliCommand.NestedRecoveryReopen
            && (nestedDiscardSpecificOptionSpecified || nestedReopenSpecificOptionSpecified))
        {
            error = "Nested recovery evidence options are valid only for nested-recovery-discard or nested-recovery-reopen.";
            return false;
        }

        if (command == CliCommand.NestedRecoveryDiscard
            && nestedReopenSpecificOptionSpecified)
        {
            error = "--expected-source-length and --expected-content-sha256 are valid only for nested-recovery-reopen.";
            return false;
        }

        if (command == CliCommand.NestedRecoveryReopen
            && !nestedDiscardSpecificOptionSpecified)
        {
            error = "Nested-recovery-reopen requires --expected-revision, --expected-source-count, and --expected-total-bytes.";
            return false;
        }

        if (confirmation is not null
            && command is not CliCommand.RecoveryApply
                and not CliCommand.NestedRecoveryDiscard)
        {
            error = "--confirm is valid only for recovery-apply or nested-recovery-discard.";
            return false;
        }

        if (command == CliCommand.RecoveryApply
            && (expectedPhaseText is null
                || expectedSequenceText is null
                || expectedRecordedAtText is null
                || confirmation is null))
        {
            error = "Recovery-apply requires --expected-phase, --expected-sequence, "
                + "--expected-recorded-at, and --confirm.";
            return false;
        }

        if (command == CliCommand.NestedRecoveryDiscard
            && (expectedRevision is null
                || expectedSourceCountText is null
                || expectedTotalSourceBytesText is null
                || confirmation is null))
        {
            error = "Nested-recovery-discard requires --expected-revision, "
                + "--expected-source-count, --expected-total-bytes, and --confirm.";
            return false;
        }

        var required = command switch
        {
            CliCommand.Extract => 2,
            CliCommand.RebuildZip => 2,
            CliCommand.CreateGZip or CliCommand.CreateBZip2 => 2,
            CliCommand.RecoveryList => 0,
            CliCommand.RecoveryApply => 2,
            CliCommand.NestedRecoveryList => 0,
            CliCommand.NestedRecoveryDiscard => 1,
            CliCommand.NestedRecoveryReopen => 2,
            CliCommand.SupportDiagnostics => 0,
            CliCommand.Capabilities => 0,
            _ => 1,
        };
        if (positional.Count != required)
        {
            error = command switch
            {
                CliCommand.Extract => "Extract requires an archive and a new destination path.",
                CliCommand.RecoveryList => "Recovery-list does not accept a positional path.",
                CliCommand.RecoveryInspect => "Recovery-inspect requires exactly one recovery identifier.",
                CliCommand.RecoveryApply =>
                    "Recovery-apply requires one recovery identifier and one action.",
                CliCommand.NestedRecoveryList =>
                    "Nested-recovery-list does not accept a positional path.",
                CliCommand.Capabilities =>
                    "Capabilities does not accept a positional path.",
                CliCommand.SupportDiagnostics =>
                    "Support-diagnostics does not accept a positional path.",
                CliCommand.NestedRecoveryInspect =>
                    "Nested-recovery-inspect requires exactly one recovery identifier.",
                CliCommand.NestedRecoveryDiscard =>
                    "Nested-recovery-discard requires exactly one recovery identifier.",
                CliCommand.NestedRecoveryReopen =>
                    "Nested-recovery-reopen requires a recovery identifier and one opaque source identifier.",
                CliCommand.RebuildZip =>
                    "Rebuild-zip requires a source archive and a new destination path.",
                CliCommand.CreateGZip =>
                    "Create-gzip requires a new destination and exactly one source file.",
                CliCommand.CreateBZip2 =>
                    "Create-bzip2 requires a new destination and exactly one source file.",
                _ => $"{command} requires exactly one archive path.",
            };
            return false;
        }

        var recoveryId = command is CliCommand.RecoveryInspect
            or CliCommand.RecoveryApply
            or CliCommand.NestedRecoveryInspect
            or CliCommand.NestedRecoveryDiscard
            or CliCommand.NestedRecoveryReopen
            ? positional[0]
            : null;
        if (recoveryId is not null
            && !IsValidRecoveryId(recoveryId))
        {
            error = "Recovery commands require a 64-character lowercase hexadecimal identifier.";
            return false;
        }

        CliRecoveryDisposition? recoveryDisposition = null;
        if (command == CliCommand.RecoveryApply)
        {
            if (!TryParseRecoveryAction(positional[1], out var action))
            {
                error = "Recovery-apply action must be commit-owned-staging, "
                    + "discard-owned-staging, accept-published-final, or "
                    + "acknowledge-no-owned-artifact.";
                return false;
            }

            if (!Enum.TryParse<ArchiveCreationRecoveryPhase>(
                    expectedPhaseText,
                    ignoreCase: true,
                    out var expectedPhase)
                || !Enum.IsDefined(expectedPhase)
                || !string.Equals(
                    expectedPhaseText,
                    expectedPhase.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "--expected-phase is not a valid recovery journal phase.";
                return false;
            }

            if (!int.TryParse(
                    expectedSequenceText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedSequence)
                || expectedSequence != (int)expectedPhase + 1)
            {
                error = "--expected-sequence must match --expected-phase.";
                return false;
            }

            if (!DateTimeOffset.TryParseExact(
                    expectedRecordedAtText,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var expectedRecordedAtUtc)
                || expectedRecordedAtUtc == default
                || expectedRecordedAtUtc.Offset != TimeSpan.Zero)
            {
                error = "--expected-recorded-at must be the exact non-default UTC timestamp "
                    + "printed by recovery-inspect.";
                return false;
            }

            if (!string.Equals(confirmation, recoveryId, StringComparison.Ordinal))
            {
                error = "--confirm must exactly repeat the recovery identifier.";
                return false;
            }

            recoveryDisposition = new CliRecoveryDisposition(
                action,
                expectedPhase,
                expectedSequence,
                expectedRecordedAtUtc);
        }

        CliNestedWorkspaceRecoveryDiscard? nestedRecoveryDiscard = null;
        if (command == CliCommand.NestedRecoveryDiscard)
        {
            if (!IsValidRecoveryId(expectedRevision!))
            {
                error = "--expected-revision must be the exact 64-character lowercase hexadecimal revision printed by nested-recovery-inspect.";
                return false;
            }

            if (!int.TryParse(
                    expectedSourceCountText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedSourceCount)
                || expectedSourceCount is < 0 or > ProtocolConstants.MaximumOpenSessions)
            {
                error = "--expected-source-count is outside the nested recovery bound.";
                return false;
            }

            if (!long.TryParse(
                    expectedTotalSourceBytesText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedTotalSourceBytes)
                || expectedTotalSourceBytes is < 0
                    or > ProtocolConstants.MaximumActiveNestedArchiveBytes)
            {
                error = "--expected-total-bytes is outside the nested recovery bound.";
                return false;
            }

            if (!string.Equals(confirmation, recoveryId, StringComparison.Ordinal))
            {
                error = "--confirm must exactly repeat the nested-workspace recovery identifier.";
                return false;
            }

            nestedRecoveryDiscard = new CliNestedWorkspaceRecoveryDiscard(
                expectedRevision!,
                expectedSourceCount,
                expectedTotalSourceBytes);
        }

        CliNestedWorkspaceRecoveryReopen? nestedRecoveryReopen = null;
        if (command == CliCommand.NestedRecoveryReopen)
        {
            if (!IsValidRecoveryId(expectedRevision!))
            {
                error = "--expected-revision must be the exact 64-character lowercase hexadecimal revision printed by nested-recovery-inspect.";
                return false;
            }

            if (!int.TryParse(
                    expectedSourceCountText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedSourceCount)
                || expectedSourceCount is <= 0 or > ProtocolConstants.MaximumOpenSessions)
            {
                error = "--expected-source-count must identify at least one source within the nested recovery bound.";
                return false;
            }

            if (!long.TryParse(
                    expectedTotalSourceBytesText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedTotalSourceBytes)
                || expectedTotalSourceBytes is <= 0
                    or > ProtocolConstants.MaximumActiveNestedArchiveBytes)
            {
                error = "--expected-total-bytes must be positive and within the nested recovery bound.";
                return false;
            }

            var sourceId = positional[1];
            if (!IsValidRecoveryId(sourceId))
            {
                error = "The source identifier must be the exact 64-character lowercase hexadecimal identifier printed by nested-recovery-inspect.";
                return false;
            }

            if (!long.TryParse(
                    expectedSourceLengthText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedSourceLength)
                || expectedSourceLength is <= 0 or > ProtocolConstants.MaximumNestedArchiveBytes
                || expectedSourceLength > expectedTotalSourceBytes)
            {
                error = "--expected-source-length must be positive, bounded, and no greater than --expected-total-bytes.";
                return false;
            }

            if (expectedContentSha256Text is null
                || !IsValidRecoveryId(expectedContentSha256Text))
            {
                error = "--expected-content-sha256 must be the exact 64-character lowercase hexadecimal SHA-256 value printed by nested-recovery-inspect.";
                return false;
            }

            nestedRecoveryReopen = new CliNestedWorkspaceRecoveryReopen(
                expectedRevision!,
                expectedSourceCount,
                expectedTotalSourceBytes,
                sourceId,
                expectedSourceLength,
                expectedContentSha256Text!);
        }

        try
        {
            var archivePath = command switch
            {
                CliCommand.RecoveryList
                    or CliCommand.RecoveryInspect
                    or CliCommand.RecoveryApply
                    or CliCommand.NestedRecoveryList
                    or CliCommand.NestedRecoveryInspect
                    or CliCommand.NestedRecoveryDiscard
                    or CliCommand.NestedRecoveryReopen
                    or CliCommand.SupportDiagnostics
                    or CliCommand.Capabilities => string.Empty,
                CliCommand.CreateZip
                    or CliCommand.CreateTar
                    or CliCommand.CreateTarGZip
                    or CliCommand.CreateTarBZip2
                    or CliCommand.CreateGZip
                    or CliCommand.CreateBZip2 =>
                    GetCreationFilesystemPath(positional[0]),
                CliCommand.RebuildZip => GetRebuildFilesystemPath(positional[0]),
                _ => Path.GetFullPath(positional[0]),
            };
            var destinationPath = command == CliCommand.RebuildZip
                ? GetRebuildFilesystemPath(positional[1])
                : command == CliCommand.Extract
                    ? Path.GetFullPath(positional[1])
                    : null;
            var rawSourcePath = IsRawCreationCommand(command)
                ? GetCreationFilesystemPath(positional[1])
                : null;
            var rawSuffixes = command switch
            {
                CliCommand.CreateGZip => new[] { ".gz", ".gzip" },
                CliCommand.CreateBZip2 => new[] { ".bz2", ".bzip2" },
                _ => [],
            };
            if (rawSourcePath is not null
                && !rawSuffixes.Any(suffix => archivePath.EndsWith(
                    suffix,
                    StringComparison.OrdinalIgnoreCase)))
            {
                error = command == CliCommand.CreateGZip
                    ? "Create-gzip destination must end in .gz or .gzip."
                    : "Create-bzip2 destination must end in .bz2 or .bzip2.";
                return false;
            }

            if (rawSourcePath is not null && PathsAlias(archivePath, rawSourcePath))
            {
                error = "Raw compression source and destination must be distinct paths.";
                return false;
            }

            if (command == CliCommand.RebuildZip)
            {
                if (PathsAlias(archivePath, destinationPath!))
                {
                    error = "Rebuild-zip source and destination must not be aliases of the same path.";
                    return false;
                }

                foreach (var file in rebuildFiles)
                {
                    var sourcePath = GetRebuildFilesystemPath(file.SourcePath);
                    if (PathsAlias(sourcePath, destinationPath!))
                    {
                        error = "Rebuild-zip add/replace source paths must not alias the destination.";
                        return false;
                    }
                }

                if (rebuildEntriesToDelete.Distinct().Count() != rebuildEntriesToDelete.Count)
                {
                    error = "Rebuild-zip delete entries may not be repeated.";
                    return false;
                }

                var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var operationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var deletion in rebuildEntriesToDelete)
                {
                    targetPaths.Add(GetRebuildTargetCollisionKey(deletion.NormalizedPath));
                    operationKeys.Add(GetRebuildOperationKey(deletion));
                }

                foreach (var file in rebuildFiles)
                {
                    if (!targetPaths.Add(GetRebuildTargetCollisionKey(file.ArchivePath)))
                    {
                        error = "Rebuild-zip delete, add, and replace targets may not conflict or repeat.";
                        return false;
                    }
                }

                foreach (var directory in rebuildDirectories)
                {
                    var sourcePath = GetRebuildFilesystemPath(directory.SourcePath);
                    if (PathsAlias(sourcePath, destinationPath!))
                    {
                        error = "Rebuild-zip add-folder source paths must not alias the destination.";
                        return false;
                    }

                    if (!targetPaths.Add(GetRebuildTargetCollisionKey(directory.ArchivePath)))
                    {
                        error = "Rebuild-zip folder and file targets may not conflict or repeat.";
                        return false;
                    }
                }

                foreach (var rename in rebuildRenames)
                {
                    if (!operationKeys.Add(GetRebuildOperationKey(rename.EntryKey)))
                    {
                        error = "Rebuild-zip each persistent entry may be deleted, replaced, or renamed only once.";
                        return false;
                    }

                    if (rebuildFiles.Any(file =>
                            string.Equals(
                                GetRebuildTargetCollisionKey(file.ArchivePath),
                                GetRebuildTargetCollisionKey(rename.EntryKey.NormalizedPath),
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        error = "Rebuild-zip delete, add, replace, and rename operations may not conflict or repeat.";
                        return false;
                    }

                    if (!targetPaths.Add(GetRebuildTargetCollisionKey(rename.FinalArchivePath)))
                    {
                        error = "Rebuild-zip delete, add, replace, and rename targets may not conflict or repeat.";
                        return false;
                    }
                }

                foreach (var action in rebuildTreeActions)
                {
                    if (!operationKeys.Add(GetRebuildOperationKey(new ArchiveEntryKey(action.NormalizedPath, 0))))
                    {
                        error = "Rebuild-zip each folder may be moved or deleted only once.";
                        return false;
                    }

                    if (action.Kind == ArchiveRebuildTreeActionKind.Move
                        && action.FinalArchivePath is not null
                        && !targetPaths.Add(GetRebuildTargetCollisionKey(action.FinalArchivePath)))
                    {
                        error = "Rebuild-zip folder and file targets may not conflict or repeat.";
                        return false;
                    }
                }

                var metadataTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directive in rebuildMetadata)
                {
                    var targetKey = directive.Target.Kind switch
                    {
                        ArchiveRebuildMetadataTargetKind.RetainedEntryKey when directive.Target.RetainedEntryKey is { } key
                            => $"key:{GetRebuildOperationKey(key)}",
                        ArchiveRebuildMetadataTargetKind.MappedFinalPath when directive.Target.MappedFinalArchivePath is { } path
                            => $"path:{GetRebuildTargetCollisionKey(path)}:{directive.Target.MappedKind}",
                        _ => string.Empty,
                    };
                    if (targetKey.Length == 0 || !metadataTargetKeys.Add(targetKey))
                    {
                        error = "Rebuild-zip metadata targets may not be repeated or malformed.";
                        return false;
                    }
                }
            }

            options = new CliArguments(
                command,
                archivePath,
                destinationPath,
                Path.GetFullPath(worker),
                passwordFromStandardInput,
                entries,
                creationFiles
                    .Select(file => new ArchiveCreationFile(
                        GetCreationFilesystemPath(file.SourcePath),
                        file.ArchivePath))
                    .ToArray(),
                creationDirectories
                    .Select(directory => new ArchiveCreationDirectory(
                        GetCreationFilesystemPath(directory.SourcePath),
                        directory.ArchivePath))
                    .ToArray(),
                rebuildEntriesToDelete.ToArray(),
                rebuildFiles
                    .Select(file => new CliRebuildCopyFile(
                        GetRebuildFilesystemPath(file.SourcePath),
                        file.ArchivePath,
                        file.IsReplacement))
                    .ToArray(),
                rebuildRenames
                    .Select(rename => new CliRebuildRename(
                        rename.EntryKey,
                        rename.FinalArchivePath))
                    .ToArray(),
                compressionPreset,
                recoveryId,
                recoveryDisposition,
                nestedRecoveryDiscard,
                rebuildDirectories
                    .Select(directory => new ArchiveCreationDirectory(
                        GetRebuildFilesystemPath(directory.SourcePath),
                        directory.ArchivePath))
                    .ToArray(),
                rebuildTreeActions,
                rebuildMetadata,
                rawSourcePath,
                nestedRecoveryReopen);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "An archive, destination, or worker path is invalid.";
            return false;
        }
    }

    private static bool TryParseRecoveryAction(
        string value,
        out ArchiveCreationRecoveryDispositionAction action)
    {
        action = value.ToLowerInvariant() switch
        {
            "commit-owned-staging" =>
                ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging,
            "discard-owned-staging" =>
                ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging,
            "accept-published-final" =>
                ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal,
            "acknowledge-no-owned-artifact" =>
                ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact,
            _ => default,
        };
        return value.Equals("commit-owned-staging", StringComparison.OrdinalIgnoreCase)
            || value.Equals("discard-owned-staging", StringComparison.OrdinalIgnoreCase)
            || value.Equals("accept-published-final", StringComparison.OrdinalIgnoreCase)
            || value.Equals("acknowledge-no-owned-artifact", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseMetadataKind(string value, out ArchiveEntryKind kind)
    {
        kind = value.ToLowerInvariant() switch
        {
            "file" => ArchiveEntryKind.File,
            "directory" or "dir" => ArchiveEntryKind.Directory,
            _ => ArchiveEntryKind.Unknown,
        };
        return kind is ArchiveEntryKind.File or ArchiveEntryKind.Directory;
    }

    private static bool IsMetadataValue(string value) =>
        value == "-" || !value.StartsWith('-');

    private static bool IsValidRecoveryId(string recoveryId) =>
        recoveryId.Length == 64
        && recoveryId.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static string GetCreationFilesystemPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var components = path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var fullyQualified = Path.IsPathFullyQualified(path);
        if (components.Any(component =>
                (component is not "." and not ".."
                    && (component.EndsWith('.') || component.EndsWith(' ')))
                || (fullyQualified && (component is "." or ".."))))
        {
            throw new ArgumentException(
                "ZIP creation paths cannot contain ambiguous dot or trailing-dot/space components.",
                nameof(path));
        }

        return fullyQualified ? path : Path.GetFullPath(path);
    }

    private static string GetRebuildFilesystemPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0'))
        {
            throw new ArgumentException("Filesystem paths cannot contain NUL characters.", nameof(path));
        }

        var components = path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component =>
                component is "." or ".."
                || component.EndsWith('.')
                || component.EndsWith(' ')))
        {
            throw new ArgumentException(
                "Rebuild-zip filesystem paths cannot contain dot or trailing-dot/space components.",
                nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private static bool TryNormalizeRebuildArchivePath(
        string path,
        out string normalized,
        bool allowDirectory = false)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > ProtocolConstants.MaximumPathCharacters)
        {
            return false;
        }

        var candidate = path.Replace('\\', '/');
        if (!allowDirectory && candidate.EndsWith('/'))
        {
            return false;
        }

        var isDirectory = allowDirectory && candidate.EndsWith('/');
        if (isDirectory)
        {
            candidate = candidate[..^1];
        }

        if (!ArchivePathRules.TryNormalize(
                candidate,
                out var safePath,
                out _,
                out _))
        {
            return false;
        }

        normalized = safePath.Replace(Path.DirectorySeparatorChar, '/');
        return true;
    }

    private static bool PathsAlias(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsCreationCommand(CliCommand command) =>
        command is CliCommand.CreateZip
            or CliCommand.CreateTar
            or CliCommand.CreateTarGZip
            or CliCommand.CreateTarBZip2
            or CliCommand.CreateGZip
            or CliCommand.CreateBZip2;

    private static bool IsTreeCreationCommand(CliCommand command) =>
        command is CliCommand.CreateZip
            or CliCommand.CreateTar
            or CliCommand.CreateTarGZip
            or CliCommand.CreateTarBZip2;

    private static bool IsRawCreationCommand(CliCommand command) =>
        command is CliCommand.CreateGZip or CliCommand.CreateBZip2;

    private static string GetRebuildTargetCollisionKey(string archivePath) =>
        ArchivePathRules.GetCollisionKey(archivePath.TrimEnd('/', '\\'));

    private static string GetRebuildOperationKey(ArchiveEntryKey entryKey) =>
        $"{GetRebuildTargetCollisionKey(entryKey.NormalizedPath)}\0{entryKey.DuplicateOrdinal.ToString(CultureInfo.InvariantCulture)}";
}

public static class CliMetadataSyntax
{
    private const ushort AllowedAttributes = 0x0001 | 0x0002 | 0x0004 | 0x0020;

    public static bool TryParseTimestamp(
        string value,
        out ArchiveDosLocalTimestamp timestamp)
    {
        timestamp = null!;
        if (!HasExactTimestampShape(value)
            || !DateTime.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            || parsed.Year is < 1980 or > 2107
            || parsed.Second is < 0 or > 58
            || parsed.Second % 2 != 0)
        {
            return false;
        }

        timestamp = new ArchiveDosLocalTimestamp(
            parsed.Year,
            parsed.Month,
            parsed.Day,
            parsed.Hour,
            parsed.Minute,
            parsed.Second);
        return true;
    }

    public static bool TryParseAttributes(string value, out ushort attributes)
    {
        attributes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ushort.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out attributes))
            {
                return false;
            }
        }
        else if (ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            attributes = numeric;
        }
        else
        {
            var tokens = value.Split([',', '|'], StringSplitOptions.TrimEntries);
            if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            foreach (var token in tokens)
            {
                attributes |= token.ToLowerInvariant() switch
                {
                    "none" => (ushort)0,
                    "ro" or "readonly" or "read-only" => 0x0001,
                    "hidden" => 0x0002,
                    "system" => 0x0004,
                    "archive" => 0x0020,
                    _ => ushort.MaxValue,
                };
                if ((attributes & ~AllowedAttributes) != 0)
                {
                    attributes = 0;
                    return false;
                }
            }
        }

        if ((attributes & ~AllowedAttributes) != 0)
        {
            attributes = 0;
            return false;
        }

        return true;
    }

    public static bool TryParseValues(
        string timestampText,
        string attributesText,
        ArchiveEntryKind kind,
        out ArchiveDosLocalTimestamp? timestamp,
        out ushort? attributes)
    {
        timestamp = null;
        attributes = null;
        if (timestampText != "-" && !TryParseTimestamp(timestampText, out var parsedTimestamp))
        {
            return false;
        }

        if (attributesText != "-" && !TryParseAttributes(attributesText, out var parsedAttributes))
        {
            return false;
        }

        if (timestampText == "-" && attributesText == "-")
        {
            return false;
        }

        if (timestampText != "-")
        {
            TryParseTimestamp(timestampText, out parsedTimestamp);
            timestamp = parsedTimestamp;
        }

        if (attributesText != "-")
        {
            TryParseAttributes(attributesText, out var finalAttributes);
            attributes = finalAttributes;
        }

        return kind is ArchiveEntryKind.File or ArchiveEntryKind.Directory;
    }

    private static bool HasExactTimestampShape(string value) =>
        value.Length == 19
        && value[4] == '-'
        && value[7] == '-'
        && value[10] == 'T'
        && value[13] == ':'
        && value[16] == ':'
        && value.Where((character, index) => index is not (4 or 7 or 10 or 13 or 16))
            .All(character => character is >= '0' and <= '9');
}
