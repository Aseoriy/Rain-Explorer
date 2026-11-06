using RainArchives.Contracts;
using RainArchives.App.Services;

namespace RainArchives.App.Views;

internal sealed record CreationFormatOption(
    ArchiveCreationFormat Value,
    string Name,
    string Extension,
    bool SupportsDirectories,
    bool IsAvailable,
    IReadOnlyList<ArchiveCompressionPreset> CompressionPresets,
    GetTarWriteCapabilitiesResponse? TarCapability = null,
    ManagedWriteCapability? ManagedCapability = null);

internal static class CreationFormatOptionFactory
{
    public static IReadOnlyList<CreationFormatOption> Load(
        bool supportsZipDirectories,
        GetZipWriteCapabilitiesResponse? zipCapabilities,
        GetTarWriteCapabilitiesResponse? tarCapabilities,
        GetManagedWriteCapabilitiesResponse? managedCapabilities)
    {
        var zipPresets = zipCapabilities?.CompressionPresets
            ?? Enum.GetValues<ArchiveCompressionPreset>();
        var options = new List<CreationFormatOption>();
        if (zipCapabilities is not null
            || (tarCapabilities is null && managedCapabilities is null))
        {
            options.Add(new(
                ArchiveCreationFormat.Zip,
                "ZIP",
                ".zip",
                supportsZipDirectories,
                zipCapabilities?.CanCreate ?? true,
                zipPresets));
        }

        if (tarCapabilities is not null)
        {
            options.Add(new(
                ArchiveCreationFormat.Tar,
                "TAR",
                ".tar",
                tarCapabilities.SupportsTreeCreation,
                tarCapabilities.CanCreate,
                tarCapabilities.CompressionPresets,
                tarCapabilities));
        }

        if (managedCapabilities is null)
        {
            return options;
        }

        AddManaged(options, managedCapabilities, ManagedArchiveCreationFormat.TarGZip,
            ArchiveCreationFormat.TarGZip, "TAR.GZ", ".tar.gz");
        AddManaged(options, managedCapabilities, ManagedArchiveCreationFormat.TarBZip2,
            ArchiveCreationFormat.TarBZip2, "TAR.BZ2", ".tar.bz2");
        AddManaged(options, managedCapabilities, ManagedArchiveCreationFormat.GZip,
            ArchiveCreationFormat.GZip, "GZIP", ".gz");
        AddManaged(options, managedCapabilities, ManagedArchiveCreationFormat.BZip2,
            ArchiveCreationFormat.BZip2, "BZIP2", ".bz2");
        return options;
    }

    public static IReadOnlyList<CompressionPresetOption> LoadPresets(
        CreationFormatOption format,
        ZipPresetStore? zipPresetStore,
        GetZipWriteCapabilitiesResponse? zipCapabilities)
    {
        if (format.Value == ArchiveCreationFormat.Zip)
        {
            return ZipPresetOptionFactory.Load(zipPresetStore, zipCapabilities).Options;
        }

        return format.CompressionPresets
            .Distinct()
            .Select(preset => new CompressionPresetOption(
                preset,
                preset.ToString(),
                $"{format.Name} compression preset · {preset}."))
            .ToArray();
    }

    private static void AddManaged(
        ICollection<CreationFormatOption> options,
        GetManagedWriteCapabilitiesResponse capabilities,
        ManagedArchiveCreationFormat managedFormat,
        ArchiveCreationFormat format,
        string name,
        string extension)
    {
        var capability = capabilities.Capabilities.FirstOrDefault(item => item.Format == managedFormat);
        if (capability is null)
        {
            return;
        }

        options.Add(new CreationFormatOption(
            format,
            name,
            extension,
            capability.SupportsTreeCreation,
            capability.CanCreate,
            capability.CompressionPresets,
            TarCapability: null,
            capability));
    }
}
