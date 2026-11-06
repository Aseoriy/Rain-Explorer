namespace RainArchives.Contracts;

public static class ProtocolConstants
{
    public const int MajorVersion = 1;
    public const int MinorVersion = 24;
    public const int RequestCancellationMinorVersion = 2;
    public const int PersistentEntryKeysMinorVersion = 3;
    public const int VolumeSetIdentityMinorVersion = 4;
    public const int EntryStreamLeasesMinorVersion = 5;
    public const int NestedArchiveOpenMinorVersion = 6;
    public const int ZipCreationMinorVersion = 7;
    public const int ZipDirectoryCreationMinorVersion = 8;
    public const int ArchiveCreationRecoveryInspectionMinorVersion = 9;
    public const int ArchiveCreationRecoveryDispositionMinorVersion = 10;
    public const int NestedWorkspaceRecoveryMinorVersion = 11;
    public const int ZipRebuildCopyMinorVersion = 12;
    public const int ZipRebuildRenameMinorVersion = 13;
    public const int ZipRebuildTreeEditMinorVersion = 14;
    public const int ZipRebuildMetadataMinorVersion = 15;
    public const int ZipWriteCapabilitiesMinorVersion = 16;
    public const int TarCreationMinorVersion = 17;
    public const int TarDirectoryCreationMinorVersion = 17;
    public const int TarWriteCapabilitiesMinorVersion = 17;
    public const int TarGZipCreationMinorVersion = 18;
    public const int TarGZipDirectoryCreationMinorVersion = 18;
    public const int TarBZip2CreationMinorVersion = 18;
    public const int TarBZip2DirectoryCreationMinorVersion = 18;
    public const int GZipCreationMinorVersion = 18;
    public const int BZip2CreationMinorVersion = 18;
    public const int ManagedWriteCapabilitiesMinorVersion = 18;
    public const int OperationActivityMinorVersion = 19;
    public const int ExtractionRecoveryInspectionMinorVersion = 20;
    public const int NestedWorkspaceIndependentReopenMinorVersion = 21;
    public const int SupportDiagnosticsMinorVersion = 22;
    public const int RarCreationMinorVersion = 23;
    public const int ExpandedCreationTreeBudgetMinorVersion = 24;
    // Negotiated tree expansion only; explicit mappings, legacy capabilities and rebuild stay bounded separately.
    public const int MaximumExpandedCreationTreeEntries = 65_536;
    public const int MaximumControlFrameBytes = 1024 * 1024;
    public const int MaximumSecretBytes = 16 * 1024;
    public const int MaximumJsonDepth = 32;
    public const int MaximumPageSize = 4096;
    public const int MaximumPathCharacters = 32_767;
    public const int MaximumSecretSlots = 32;
    public const int MaximumOpenSessions = 16;
    public const int MaximumQueuedOperations = 32;
    public const int MaximumConcurrentOperations = 1;
    public const int MaximumRetainedOperationActivity = 64;
    public const int MaximumOperationActivityRecords =
        MaximumQueuedOperations + MaximumRetainedOperationActivity;
    public const int MaximumPendingRequests = 256;
    public const int MaximumControlRequests = 8;
    public const int MaximumEntryStreamLeases = 8;
    public const int MaximumEntryStreamPipeNameCharacters = 128;
    public const int MaximumEntryStreamFrameBytes = 64 * 1024;
    public const int MaximumEntryStreamErrorBytes = 64 * 1024;
    public const long DefaultEntryStreamBytes = 64L * 1024 * 1024;
    public const long MaximumEntryStreamBytes = 256L * 1024 * 1024;
    public const long DefaultNestedArchiveBytes = 64L * 1024 * 1024;
    public const long MaximumNestedArchiveBytes = 256L * 1024 * 1024;
    public const long MaximumNestedArchiveChainBytes = 256L * 1024 * 1024;
    public const long MaximumActiveNestedArchiveBytes = 512L * 1024 * 1024;
    public const int MaximumNestedArchiveDepth = 3;
    public const int MaximumZipCreationFiles = 4096;
    public const int MaximumZipCreationEntries = 4096;
    public const int MaximumZipRebuildDirectoryRoots = 64;
    public const int MaximumZipRebuildTreeActions = 4096;
    public const int MaximumZipRebuildDirectives = 4096;
    public const int MaximumZipRebuildPathPayloadBytes = 512 * 1024;
    public const int MaximumZipRebuildMetadataDirectives = 4096;
    public const int MaximumTarCreationFiles = 4096;
    public const int MaximumTarCreationEntries = 4096;
    public const long MaximumTarCreationSourceBytes = 64L * 1024 * 1024 * 1024;
    public const int MaximumRarCreationFiles = 4096;
    public const int MaximumRarCreationEntries = 4096;
    public const long MaximumRarCreationSourceBytes = 8L * 1024 * 1024 * 1024;
    public const int MaximumRawCreationEntries = 1;
    public const long MaximumRawCreationSourceBytes = MaximumTarCreationSourceBytes;
    public const int MaximumManagedWriteCapabilityFormats = 4;
    public const int MaximumArchiveCreationRecoveryRecords = 1024;
    public const int MaximumNestedWorkspaceRecoveryRecords = 1024;
    public const int MaximumExtractionRecoveryRecords = 1024;
    public const int MaximumExtractionRecoveryObjects = 2_000_000;
    public const long MaximumExtractionRecoveryBytes = 64L * 1024 * 1024 * 1024;
    public const long MaximumZipCreationSourceBytes = 64L * 1024 * 1024 * 1024;
    public const int MaximumRequestsPerConnection = 1_000_000;
}

public static class ProtocolMessageKinds
{
    public const string WorkerHello = "worker.hello";
    public const string ClientWelcome = "client.welcome";
    public const string OpenArchive = "archive.open";
    public const string OpenNestedArchive = "archive.nested.open";
    public const string CloseArchive = "archive.close";
    public const string ListEntries = "entries.list";
    public const string OpenEntryStream = "entry.stream.open";
    public const string CancelEntryStream = "entry.stream.cancel";
    public const string StartExtraction = "operation.extract.start";
    public const string StartIntegrityTest = "operation.test.start";
    public const string StartZipCreation = "operation.zip.create.start";
    public const string StartZipTreeCreation = "operation.zip.tree.create.start";
    public const string StartZipRebuildCopy = "operation.zip.rebuild.copy.start";
    public const string StartZipRebuildRename = "operation.zip.rebuild.rename.start";
    public const string GetZipRebuildTreePlan = "archive.zip.rebuild.tree.plan";
    public const string GetZipRebuildSourceFolderReview = "archive.zip.rebuild.source-folder.review";
    public const string StartZipRebuildTreeEdit = "operation.zip.rebuild.tree-edit.start";
    public const string StartZipRebuildMetadata = "operation.zip.rebuild.metadata.start";
    public const string GetZipWriteCapabilities = "archive.zip.write.capabilities";
    public const string StartTarCreation = "operation.tar.create.start";
    public const string StartTarTreeCreation = "operation.tar.tree.create.start";
    public const string GetTarWriteCapabilities = "archive.tar.write.capabilities";
    public const string StartRarCreation = "operation.rar.create.start";
    public const string StartRarTreeCreation = "operation.rar.tree.create.start";
    public const string GetRarWriteCapabilities = "archive.rar.write.capabilities";
    public const string StartTarGZipCreation = "operation.tar-gzip.create.start";
    public const string StartTarGZipTreeCreation = "operation.tar-gzip.tree.create.start";
    public const string StartTarBZip2Creation = "operation.tar-bzip2.create.start";
    public const string StartTarBZip2TreeCreation = "operation.tar-bzip2.tree.create.start";
    public const string StartGZipCreation = "operation.gzip.create.start";
    public const string StartBZip2Creation = "operation.bzip2.create.start";
    public const string GetManagedWriteCapabilities = "archive.managed.write.capabilities";
    public const string GetOperationActivity = "operation.activity.get";
    public const string ListExtractionRecoveries = "operation.extract.recovery.list";
    public const string InspectExtractionRecovery = "operation.extract.recovery.inspect";
    public const string ListArchiveCreationRecoveries = "archive.creation.recovery.list";
    public const string InspectArchiveCreationRecovery = "archive.creation.recovery.inspect";
    public const string ApplyArchiveCreationRecoveryDisposition = "archive.creation.recovery.dispose";
    public const string ListNestedWorkspaceRecoveries = "worker.nested.recovery.list";
    public const string InspectNestedWorkspaceRecovery = "worker.nested.recovery.inspect";
    public const string DiscardNestedWorkspaceRecovery = "worker.nested.recovery.discard";
    public const string InspectNestedWorkspaceReopen = "worker.nested.recovery.reopen.inspect";
    public const string ReopenNestedWorkspaceRecovery = "worker.nested.recovery.reopen";
    public const string GetSupportDiagnostics = "worker.support.diagnostics.get";
    public const string CancelOperation = "operation.cancel";
    public const string CancelRequest = "request.cancel";
    public const string CreateSecretSlot = "secret.create";
    public const string DiscardSecretSlot = "secret.discard";
    public const string CloseWorker = "worker.close";
    public const string OperationEvent = "operation.event";
    public const string Error = "error";
}

public static class ProtocolFeatures
{
    public const string SecretSlots = "secret-slots";
    public const string OperationEvents = "operation-events";
    public const string NewDestinationExtraction = "new-destination-extraction";
    public const string SourceFingerprintValidation = "source-fingerprint-validation";
    public const string DownloadProvenance = "download-provenance";
    public const string SecretSlotDiscard = "secret-slot-discard";
    public const string RequestCancellation = "request-cancellation";
    public const string PersistentEntryKeys = "persistent-entry-keys";
    public const string VolumeSetIdentity = "volume-set-identity";
    public const string EntryStreamLeases = "entry-stream-leases";
    public const string NestedArchiveOpen = "nested-archive-open";
    public const string ZipCreation = "zip-create-v1";
    public const string ZipDirectoryCreation = "zip-create-directories-v1";
    public const string ArchiveCreationRecoveryInspection =
        "archive-creation-recovery-inspection-v1";
    public const string ArchiveCreationRecoveryDisposition =
        "archive-creation-recovery-disposition-v1";
    public const string NestedWorkspaceRecovery = "nested-workspace-recovery-v1";
    public const string ZipRebuildCopy = "zip-rebuild-copy-v1";
    public const string ZipRebuildRename = "zip-rebuild-rename-v1";
    public const string ZipRebuildTreeEdit = "zip-rebuild-tree-edit-v1";
    public const string ZipRebuildSourceFolderReview = "zip-rebuild-source-folder-review-v1";
    public const string ZipRebuildMetadata = "zip-rebuild-metadata-v1";
    public const string ZipWriteCapabilities = "zip-write-capabilities-v1";
    public const string TarCreation = "tar-create-v1";
    public const string TarDirectoryCreation = "tar-create-directories-v1";
    public const string TarWriteCapabilities = "tar-write-capabilities-v1";
    public const string TarGZipCreation = "tar-gzip-create-v1";
    public const string TarGZipDirectoryCreation = "tar-gzip-create-directories-v1";
    public const string TarBZip2Creation = "tar-bzip2-create-v1";
    public const string TarBZip2DirectoryCreation = "tar-bzip2-create-directories-v1";
    public const string GZipCreation = "gzip-create-v1";
    public const string BZip2Creation = "bzip2-create-v1";
    public const string ManagedWriteCapabilities = "managed-write-capabilities-v1";
    public const string OperationActivity = "operation-activity-v1";
    public const string ExtractionRecoveryInspection =
        "extraction-staging-recovery-inspection-v1";
    public const string NestedWorkspaceIndependentReopen =
        "nested-workspace-independent-reopen-v1";
    public const string SupportDiagnostics = "worker-support-diagnostics-v1";
    public const string RarCreation = "rar-create-v1";
    public const string RarDirectoryCreation = "rar-create-directories-v1";
    public const string RarWriteCapabilities = "rar-write-capabilities-v1";
    public const string ExpandedCreationTreeBudget = "creation-expanded-tree-budget-v1";

    public static IReadOnlyList<string> ExpandedCreationTreeBudgetDependencies { get; } =
        Array.AsReadOnly(new[] { RequestCancellation, SourceFingerprintValidation });

    public static IReadOnlyList<string> SupportDiagnosticsDependencies { get; } =
        Array.AsReadOnly(
        new[]
        {
            OperationActivity,
            RequestCancellation,
        });

    public static IReadOnlyList<string> NestedWorkspaceIndependentReopenDependencies { get; } =
        Array.AsReadOnly(
        new[]
        {
            NestedWorkspaceRecovery,
            NestedArchiveOpen,
            SecretSlots,
            RequestCancellation,
            SourceFingerprintValidation,
        });

    public static IReadOnlyList<string> ExtractionRecoveryInspectionDependencies { get; } =
        Array.AsReadOnly(
        new[]
        {
            NewDestinationExtraction,
            SourceFingerprintValidation,
            RequestCancellation,
        });

    public static IReadOnlyList<string> OperationActivityDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        OperationEvents,
        RequestCancellation,
    });

    public static IReadOnlyList<string> TarCreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        RequestCancellation,
    });

    public static IReadOnlyList<string> TarDirectoryCreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        TarCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> TarWriteCapabilitiesDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        TarCreation,
        TarDirectoryCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> RarCreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        RequestCancellation,
    });

    public static IReadOnlyList<string> RarDirectoryCreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        RarCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> RarWriteCapabilitiesDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        RarCreation,
        RarDirectoryCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> TarGZipCreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        TarCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> TarGZipDirectoryCreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        TarGZipCreation,
        TarDirectoryCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> TarBZip2CreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        TarCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> TarBZip2DirectoryCreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        TarBZip2Creation,
        TarDirectoryCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> GZipCreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        RequestCancellation,
    });

    public static IReadOnlyList<string> BZip2CreationDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        RequestCancellation,
    });

    public static IReadOnlyList<string> ManagedWriteCapabilitiesDependencies { get; } = Array.AsReadOnly(
    new[]
    {
        TarGZipCreation,
        TarGZipDirectoryCreation,
        TarBZip2Creation,
        TarBZip2DirectoryCreation,
        GZipCreation,
        BZip2Creation,
        TarCreation,
        TarDirectoryCreation,
        RequestCancellation,
    });

    public static IReadOnlyList<string> RequiredVersion1 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor16 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
        ArchiveCreationRecoveryInspection,
        ArchiveCreationRecoveryDisposition,
        NestedWorkspaceRecovery,
        ZipRebuildCopy,
        ZipRebuildRename,
        ZipRebuildTreeEdit,
        ZipRebuildSourceFolderReview,
        ZipRebuildMetadata,
        ZipWriteCapabilities,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor17 { get; } = Array.AsReadOnly(
        SupportedVersion1Minor16
            .Concat(
            [
                TarCreation,
                TarDirectoryCreation,
                TarWriteCapabilities,
            ])
            .ToArray());

    private static IReadOnlyList<string> SupportedVersion1Minor18 { get; } = Array.AsReadOnly(
        SupportedVersion1Minor17
            .Concat(
            [
                TarGZipCreation,
                TarGZipDirectoryCreation,
                TarBZip2Creation,
                TarBZip2DirectoryCreation,
                GZipCreation,
                BZip2Creation,
                ManagedWriteCapabilities,
            ])
            .ToArray());

    private static IReadOnlyList<string> SupportedVersion1Minor19 { get; } = Array.AsReadOnly(
        SupportedVersion1Minor18
            .Concat(
            [
                OperationActivity,
            ])
            .ToArray());

    private static IReadOnlyList<string> SupportedVersion1Minor20 { get; } = Array.AsReadOnly(
        SupportedVersion1Minor19
            .Concat(
            [
                ExtractionRecoveryInspection,
            ])
            .ToArray());

    private static IReadOnlyList<string> SupportedVersion1Minor21 { get; } = Array.AsReadOnly(
        SupportedVersion1Minor20
            .Concat(
            [
                NestedWorkspaceIndependentReopen,
            ])
            .ToArray());

    private static IReadOnlyList<string> SupportedVersion1Minor22 { get; } = Array.AsReadOnly(
        SupportedVersion1Minor21
            .Concat(
            [
                SupportDiagnostics,
            ])
            .ToArray());

    private static IReadOnlyList<string> SupportedVersion1Minor23 { get; } = Array.AsReadOnly(
        SupportedVersion1Minor22
            .Concat(
            [
                RarCreation,
                RarDirectoryCreation,
                RarWriteCapabilities,
            ])
            .ToArray());

    public static IReadOnlyList<string> SupportedVersion1 { get; } = Array.AsReadOnly(
        SupportedVersion1Minor23.Append(ExpandedCreationTreeBudget).ToArray());

    private static IReadOnlyList<string> SupportedVersion1Minor15 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
        ArchiveCreationRecoveryInspection,
        ArchiveCreationRecoveryDisposition,
        NestedWorkspaceRecovery,
        ZipRebuildCopy,
        ZipRebuildRename,
        ZipRebuildTreeEdit,
        ZipRebuildSourceFolderReview,
        ZipRebuildMetadata,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor13 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
        ArchiveCreationRecoveryInspection,
        ArchiveCreationRecoveryDisposition,
        NestedWorkspaceRecovery,
        ZipRebuildCopy,
        ZipRebuildRename,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor14 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
        ArchiveCreationRecoveryInspection,
        ArchiveCreationRecoveryDisposition,
        NestedWorkspaceRecovery,
        ZipRebuildCopy,
        ZipRebuildRename,
        ZipRebuildTreeEdit,
        ZipRebuildSourceFolderReview,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor12 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
        ArchiveCreationRecoveryInspection,
        ArchiveCreationRecoveryDisposition,
        NestedWorkspaceRecovery,
        ZipRebuildCopy,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor11 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
        ArchiveCreationRecoveryInspection,
        ArchiveCreationRecoveryDisposition,
        NestedWorkspaceRecovery,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor10 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
        ArchiveCreationRecoveryInspection,
        ArchiveCreationRecoveryDisposition,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor9 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
        ArchiveCreationRecoveryInspection,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor8 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
        ZipDirectoryCreation,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor7 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
        ZipCreation,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor6 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
        NestedArchiveOpen,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor5 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
        EntryStreamLeases,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor4 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
        VolumeSetIdentity,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor3 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
        PersistentEntryKeys,
    });

    private static IReadOnlyList<string> SupportedVersion1Minor2 { get; } = Array.AsReadOnly(
    new[]
    {
        SecretSlots,
        OperationEvents,
        NewDestinationExtraction,
        SourceFingerprintValidation,
        DownloadProvenance,
        SecretSlotDiscard,
        RequestCancellation,
    });

    public static IReadOnlyList<string> SupportedForMinorVersion(int minorVersion) => minorVersion switch
    {
        >= ProtocolConstants.ExpandedCreationTreeBudgetMinorVersion => SupportedVersion1,
        >= ProtocolConstants.RarCreationMinorVersion => SupportedVersion1Minor23,
        >= ProtocolConstants.SupportDiagnosticsMinorVersion => SupportedVersion1Minor22,
        >= ProtocolConstants.NestedWorkspaceIndependentReopenMinorVersion => SupportedVersion1Minor21,
        >= ProtocolConstants.ExtractionRecoveryInspectionMinorVersion => SupportedVersion1Minor20,
        >= ProtocolConstants.OperationActivityMinorVersion => SupportedVersion1Minor19,
        >= ProtocolConstants.ManagedWriteCapabilitiesMinorVersion => SupportedVersion1Minor18,
        >= ProtocolConstants.TarWriteCapabilitiesMinorVersion => SupportedVersion1Minor17,
        >= ProtocolConstants.ZipWriteCapabilitiesMinorVersion => SupportedVersion1Minor16,
        >= ProtocolConstants.ZipRebuildMetadataMinorVersion => SupportedVersion1Minor15,
        >= ProtocolConstants.ZipRebuildTreeEditMinorVersion => SupportedVersion1Minor14,
        >= ProtocolConstants.ZipRebuildRenameMinorVersion => SupportedVersion1Minor13,
        >= ProtocolConstants.ZipRebuildCopyMinorVersion => SupportedVersion1Minor12,
        >= ProtocolConstants.NestedWorkspaceRecoveryMinorVersion => SupportedVersion1Minor11,
        >= ProtocolConstants.ArchiveCreationRecoveryDispositionMinorVersion => SupportedVersion1Minor10,
        >= ProtocolConstants.ArchiveCreationRecoveryInspectionMinorVersion => SupportedVersion1Minor9,
        >= ProtocolConstants.ZipDirectoryCreationMinorVersion => SupportedVersion1Minor8,
        >= ProtocolConstants.ZipCreationMinorVersion => SupportedVersion1Minor7,
        >= ProtocolConstants.NestedArchiveOpenMinorVersion => SupportedVersion1Minor6,
        >= ProtocolConstants.EntryStreamLeasesMinorVersion => SupportedVersion1Minor5,
        >= ProtocolConstants.VolumeSetIdentityMinorVersion => SupportedVersion1Minor4,
        >= ProtocolConstants.PersistentEntryKeysMinorVersion => SupportedVersion1Minor3,
        >= ProtocolConstants.RequestCancellationMinorVersion => SupportedVersion1Minor2,
        _ => RequiredVersion1,
    };
}
