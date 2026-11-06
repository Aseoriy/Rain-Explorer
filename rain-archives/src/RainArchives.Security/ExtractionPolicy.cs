namespace RainArchives.Security;

public sealed record ExtractionSecurityPolicy(
    int MaximumPathDepth = 256,
    int MaximumComponentLength = 255,
    int MaximumFinalPathLength = 32_000,
    int EntryWarningThreshold = 250_000,
    int DefaultMaximumEntries = 500_000,
    int AbsoluteMaximumEntries = 2_000_000,
    long MetadataBudgetBytes = 512L * 1024 * 1024,
    double ExpansionRatioWarning = 1000,
    long ExpansionRatioMinimumOutputBytes = 64L * 1024 * 1024,
    long DefaultMaximumOutputBytes = 64L << 30,
    long MaximumUnknownEntryOutputBytes = 8L << 30,
    long DiskSpaceCheckIntervalBytes = 256L * 1024 * 1024,
    int DiskSpaceCheckIntervalObjects = 1024,
    long MinimumDiskReserveBytes = 512L * 1024 * 1024,
    double MinimumDiskReserveFraction = 0.05,
    long FileObjectChargeBytes = 4L * 1024,
    long DirectoryObjectChargeBytes = 4L * 1024,
    long ProvenanceStreamChargeBytes = 4L * 1024);
