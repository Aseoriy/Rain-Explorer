using RainArchives.Contracts;

namespace RainArchives.App.Services;

public enum ArchiveCreationFormat
{
    Zip,
    Tar,
    TarGZip,
    TarBZip2,
    GZip,
    BZip2,
}

public sealed record ArchiveCreationSelection(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    IReadOnlyList<ArchiveCreationDirectory> Directories,
    ArchiveCompressionPreset CompressionPreset,
    ArchiveCreationFormat Format = ArchiveCreationFormat.Zip);
