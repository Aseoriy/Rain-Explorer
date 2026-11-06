using System.IO;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.Services;

public enum SettingsCapabilityState
{
    Loading,
    Ready,
    Downgraded,
    Offline,
    Error,
    Closed,
}

public sealed record SettingsCapabilitySnapshot(
    SettingsCapabilityState State,
    GetZipWriteCapabilitiesResponse? Zip,
    GetTarWriteCapabilitiesResponse? Tar,
    GetManagedWriteCapabilitiesResponse? Managed,
    string Status)
{
    public static SettingsCapabilitySnapshot Loading { get; } = new(
        SettingsCapabilityState.Loading,
        null,
        null,
        null,
        "Loading authenticated Worker capabilities.");

    public static SettingsCapabilitySnapshot Offline { get; } = new(
        SettingsCapabilityState.Offline,
        null,
        null,
        null,
        "The authenticated Worker is offline. Saved defaults remain local hints and no archive path was accessed.");

    public static async Task<SettingsCapabilitySnapshot> LoadAsync(
        IArchiveClient? client,
        CancellationToken cancellationToken)
    {
        if (client is null)
        {
            return Offline;
        }

        try
        {
            var supportsZip = client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities);
            var supportsTar = client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities);
            var supportsManaged = client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities);
            if (!supportsZip && !supportsTar && !supportsManaged)
            {
                return new SettingsCapabilitySnapshot(
                    SettingsCapabilityState.Downgraded,
                    null,
                    null,
                    null,
                    "The connected protocol minor does not advertise managed creation capability queries. Defaults are not applied.");
            }

            GetZipWriteCapabilitiesResponse? zip = null;
            GetTarWriteCapabilitiesResponse? tar = null;
            GetManagedWriteCapabilitiesResponse? managed = null;
            if (supportsZip)
            {
                zip = await client
                    .GetZipWriteCapabilitiesAsync(sessionId: null, cancellationToken)
                    .ConfigureAwait(true);
            }

            if (supportsTar)
            {
                tar = await client
                    .GetTarWriteCapabilitiesAsync(cancellationToken)
                    .ConfigureAwait(true);
            }

            if (supportsManaged)
            {
                managed = await client
                    .GetManagedWriteCapabilitiesAsync(cancellationToken)
                    .ConfigureAwait(true);
            }

            return new SettingsCapabilitySnapshot(
                SettingsCapabilityState.Ready,
                zip,
                tar,
                managed,
                "Current managed-format capabilities were authenticated. Defaults remain hints and are checked again before an operation starts.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveClientException exception) when (
            exception.Error.Category is ArchiveErrorCategory.WorkerFailure
                or ArchiveErrorCategory.ProtocolMismatch)
        {
            return Offline;
        }
        catch (Exception exception) when (
            exception is ArchiveClientException
                or InvalidDataException
                or NotSupportedException)
        {
            return new SettingsCapabilitySnapshot(
                SettingsCapabilityState.Error,
                null,
                null,
                null,
                "Managed-format capabilities were refused. Defaults are not applied and no archive path was accessed.");
        }
        catch (Exception exception) when (
            exception is IOException
                or ObjectDisposedException)
        {
            return Offline;
        }
    }
}

public static class ManagedCreationDefaultResolver
{
    public static ArchiveCreationFormat ResolveFormat(
        AppUiSettings settings,
        IReadOnlyList<ArchiveCreationFormat> availableFormats)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(availableFormats);
        if (availableFormats.Count == 0)
        {
            throw new InvalidOperationException("No managed archive creation format is available.");
        }

        var preferred = settings.ManagedCreation.DefaultFormat;
        return availableFormats.Contains(preferred)
            ? preferred
            : availableFormats[0];
    }

    public static ArchiveCompressionPreset ResolvePreset(
        AppUiSettings settings,
        ArchiveCreationFormat format,
        IReadOnlyList<ArchiveCompressionPreset> availablePresets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(availablePresets);
        if (availablePresets.Count == 0)
        {
            throw new InvalidOperationException("No compression preset is available.");
        }

        var preferred = settings.ManagedCreation.GetPreset(format);
        if (availablePresets.Contains(preferred))
        {
            return preferred;
        }

        if (availablePresets.Contains(ArchiveCompressionPreset.Balanced))
        {
            return ArchiveCompressionPreset.Balanced;
        }

        if (availablePresets.Contains(ArchiveCompressionPreset.Store))
        {
            return ArchiveCompressionPreset.Store;
        }

        return availablePresets[0];
    }

    public static ArchiveCompressionPreset ResolveZipRebuildPreset(
        AppUiSettings settings,
        IReadOnlyList<ArchiveCompressionPreset> availablePresets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(availablePresets);
        if (availablePresets.Count == 0)
        {
            throw new InvalidOperationException("No ZIP rebuild compression preset is available.");
        }

        var preferred = settings.ManagedCreation.ZipRebuildPreset;
        if (availablePresets.Contains(preferred))
        {
            return preferred;
        }

        return availablePresets.Contains(ArchiveCompressionPreset.Balanced)
            ? ArchiveCompressionPreset.Balanced
            : availablePresets[0];
    }
}
