using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

internal static class ActivityPresentationFormatter
{
    public static string FormatProgressDetails(OperationProgress progress) =>
        FormatProgressDetails(
            progress.BytesProcessed,
            progress.TotalBytes,
            progress.EntriesProcessed,
            progress.TotalEntries,
            progress.Elapsed,
            progress.BytesPerSecond,
            progress.CompressionRatio);

    public static string FormatProgressDetails(OperationActivityProgress progress) =>
        FormatProgressDetails(
            progress.BytesProcessed,
            progress.TotalBytes,
            progress.EntriesProcessed,
            progress.TotalEntries,
            progress.Elapsed,
            progress.BytesPerSecond,
            progress.CompressionRatio);

    public static string FormatBytesProgress(long bytesProcessed, long? totalBytes) =>
        totalBytes is { } total and >= 0
            ? $"{FormatBytes(Math.Max(0, bytesProcessed))} of {FormatBytes(total)}"
            : $"{FormatBytes(Math.Max(0, bytesProcessed))} processed";

    public static string FormatEntriesProgress(int entriesProcessed, int? totalEntries) =>
        totalEntries is { } total and >= 0
            ? $"{Math.Max(0, entriesProcessed):N0} of {total:N0}"
            : $"{Math.Max(0, entriesProcessed):N0}";

    public static string FormatThroughput(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } value || !double.IsFinite(value) || value <= 0)
        {
            return "Not reported";
        }

        var bounded = value >= long.MaxValue ? long.MaxValue : (long)value;
        return $"{FormatBytes(bounded)}/s";
    }

    public static string FormatRatio(double? ratio) =>
        ratio is { } value && double.IsFinite(value) && value >= 0
            ? $"{value:0.###}"
            : "Not reported";

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{Math.Max(0, elapsed.Minutes)}:{Math.Max(0, elapsed.Seconds):D2}";

    public static string FormatBytes(long bytes)
    {
        const double kibibyte = 1024;
        const double mebibyte = kibibyte * 1024;
        const double gibibyte = mebibyte * 1024;
        const double tebibyte = gibibyte * 1024;
        return Math.Max(0, bytes) switch
        {
            >= (long)tebibyte => $"{bytes / tebibyte:0.##} TiB",
            >= (long)gibibyte => $"{bytes / gibibyte:0.##} GiB",
            >= (long)mebibyte => $"{bytes / mebibyte:0.##} MiB",
            >= (long)kibibyte => $"{bytes / kibibyte:0.##} KiB",
            _ => $"{Math.Max(0, bytes):N0} B",
        };
    }

    private static string FormatProgressDetails(
        long bytesProcessed,
        long? totalBytes,
        int entriesProcessed,
        int? totalEntries,
        TimeSpan elapsed,
        double? bytesPerSecond,
        double? compressionRatio)
    {
        var parts = new List<string>
        {
            FormatBytesProgress(bytesProcessed, totalBytes),
            $"{FormatEntriesProgress(entriesProcessed, totalEntries)} entries",
        };

        if (bytesPerSecond is { } rate && double.IsFinite(rate) && rate > 0)
        {
            parts.Add(FormatThroughput(rate));
        }

        if (elapsed > TimeSpan.Zero)
        {
            parts.Add(FormatElapsed(elapsed));
        }

        if (compressionRatio is { } ratio && double.IsFinite(ratio) && ratio >= 0)
        {
            parts.Add($"ratio {FormatRatio(ratio)}");
        }

        return string.Join(" · ", parts);
    }
}
