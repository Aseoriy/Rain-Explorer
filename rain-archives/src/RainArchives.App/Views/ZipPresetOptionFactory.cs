using RainArchives.App.Services;
using RainArchives.Contracts;

namespace RainArchives.App.Views;

internal sealed record ZipPresetOptionSet(
    IReadOnlyList<CompressionPresetOption> Options,
    string? Warning,
    ZipPresetLoadState State)
{
    public bool CanMutate => State is ZipPresetLoadState.Ready or ZipPresetLoadState.Missing;
}

internal static class ZipPresetOptionFactory
{
    internal const string ResetRequiredWarning =
        "ZIP presets are unavailable or invalid. Built-in presets remain available for this operation. Use explicit Reset in Settings and Presets before changing saved presets.";

    public static ZipPresetOptionSet Load(
        ZipPresetStore? store,
        GetZipWriteCapabilitiesResponse? capabilities)
    {
        var loaded = store is null
            ? new ZipPresetLoadResult(ZipPresetStore.BuiltIns, null)
            : store.Load();
        var supported = capabilities?.CompressionPresets;
        var options = loaded.Presets
            .Where(preset => supported is null || supported.Contains(preset.CompressionPreset))
            .Select(ToOption)
            .ToArray();
        var warning = loaded.State == ZipPresetLoadState.UnavailableOrInvalid
            ? ResetRequiredWarning
            : loaded.Warning;
        return new ZipPresetOptionSet(options, warning, loaded.State);
    }

    private static CompressionPresetOption ToOption(ZipPreset preset)
    {
        var description = preset.IsBuiltIn
            ? preset.CompressionPreset switch
            {
                ArchiveCompressionPreset.Store => "No compression; fastest for already-compressed files.",
                ArchiveCompressionPreset.Fast => "Prioritizes speed while still reducing compressible data.",
                ArchiveCompressionPreset.Balanced => "Recommended default for everyday archives.",
                ArchiveCompressionPreset.Maximum => "Uses more CPU to pursue a smaller ZIP.",
                _ => $"Uses the {preset.CompressionPreset} compression preset.",
            }
            : $"User preset · {preset.CompressionPreset}.";
        return new CompressionPresetOption(preset.CompressionPreset, preset.Name, description, preset);
    }
}
