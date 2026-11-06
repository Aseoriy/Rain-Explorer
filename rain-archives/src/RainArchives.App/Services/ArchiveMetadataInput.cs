using System.Globalization;
using RainArchives.Contracts;

namespace RainArchives.App.Services;

internal static class ArchiveMetadataInput
{
    private const ushort AllowedAttributes = 0x0001 | 0x0002 | 0x0004 | 0x0020;

    public static bool TryParseTimestamp(string value, out ArchiveDosLocalTimestamp timestamp)
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
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ushort.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out attributes))
            {
                return false;
            }
        }
        else if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out attributes))
        {
            var tokens = value.Split([',', '|'], StringSplitOptions.TrimEntries);
            if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            foreach (var token in tokens)
            {
                var bit = token.ToLowerInvariant() switch
                {
                    "none" => (ushort)0,
                    "ro" or "readonly" or "read-only" => (ushort)0x0001,
                    "hidden" => (ushort)0x0002,
                    "system" => (ushort)0x0004,
                    "archive" => (ushort)0x0020,
                    _ => ushort.MaxValue,
                };
                attributes |= bit;
            }
        }

        if ((attributes & ~AllowedAttributes) != 0)
        {
            attributes = 0;
            return false;
        }

        return true;
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
