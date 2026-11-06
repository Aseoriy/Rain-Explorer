using RainArchives.Contracts;

namespace RainArchives.Core;

public static class ArchiveMetrics
{
    public static double? TryCalculateCompressionRatio(IEnumerable<ArchiveEntryDescriptor> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        try
        {
            long original = 0;
            long compressed = 0;
            foreach (var entry in entries)
            {
                if (entry.OriginalSize is not { } originalSize
                    || entry.CompressedSize is not { } compressedSize
                    || originalSize < 0
                    || compressedSize < 0)
                {
                    return null;
                }

                original = checked(original + originalSize);
                compressed = checked(compressed + compressedSize);
            }

            return compressed > 0 ? original / (double)compressed : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
