using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RainArchives.Security;

public sealed record DownloadProvenance(int ZoneId);

internal enum DownloadProvenanceStateKind
{
    Absent,
    Present,
    Malformed,
    Inaccessible,
    Unsupported,
}

internal sealed record DownloadProvenanceState(
    DownloadProvenanceStateKind Kind,
    DownloadProvenance? Provenance = null,
    long? StreamLength = null,
    int? HashedBytes = null,
    string? ContentSha256 = null)
{
    public static DownloadProvenanceState Absent { get; } =
        new(DownloadProvenanceStateKind.Absent);

    public static DownloadProvenanceState Inaccessible { get; } =
        new(DownloadProvenanceStateKind.Inaccessible);

    public static DownloadProvenanceState Unsupported { get; } =
        new(DownloadProvenanceStateKind.Unsupported);
}

public static class DownloadProvenanceService
{
    internal const int MaximumZoneIdentifierBytes = 4096;
    private const string StreamName = ":Zone.Identifier";

    public static async ValueTask<DownloadProvenance?> TryReadAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                Path.GetFullPath(sourcePath) + StreamName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                MaximumZoneIdentifierBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumZoneIdentifierBytes)
            {
                return null;
            }

            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return ParseCanonicalZone(bytes);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException
                or NotSupportedException)
        {
            // Missing, inaccessible, or malformed provenance never weakens archive parsing safety.
        }

        return null;
    }

    internal static DownloadProvenanceState InspectPayload(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var provenance = ParseCanonicalZone(bytes);
        return new DownloadProvenanceState(
            provenance is null
                ? DownloadProvenanceStateKind.Malformed
                : DownloadProvenanceStateKind.Present,
            provenance,
            bytes.LongLength,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal static DownloadProvenance? ParseCanonicalZone(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var inZoneTransferSection = false;
        int? parsedZone = null;
        var firstLine = true;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (firstLine)
            {
                line = line.TrimStart('\uFEFF');
                firstLine = false;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                inZoneTransferSection = line.Length >= 2
                    && line[^1] == ']'
                    && line[1..^1].Trim().Equals(
                        "ZoneTransfer",
                        StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var separator = line.IndexOf('=');
            if (!inZoneTransferSection
                || separator <= 0
                || !line[..separator].Trim().Equals(
                    "ZoneId",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parsedZone is not null
                || !int.TryParse(
                    line[(separator + 1)..].Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var zoneId)
                || zoneId is < 1 or > 4)
            {
                return null;
            }

            parsedZone = zoneId;
        }

        return parsedZone is { } validZone
            ? new DownloadProvenance(validZone)
            : null;
    }

    internal static byte[] CreateCanonicalPayload(DownloadProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (provenance.ZoneId is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(provenance));
        }

        return Encoding.ASCII.GetBytes(
            $"[ZoneTransfer]\r\nZoneId={provenance.ZoneId.ToString(CultureInfo.InvariantCulture)}\r\n");
    }

}
