using System.Globalization;
using System.Text;

namespace RainArchives.Contracts;

/// <summary>
/// Produces an unambiguous, single-line representation of text that came from an archive
/// or another untrusted protocol source. This is a presentation boundary only; callers
/// must retain the original value for archive semantics and path comparisons.
/// </summary>
public static class UntrustedDisplayText
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '\\')
            {
                result.Append("\\\\");
                continue;
            }

            if (char.IsHighSurrogate(current))
            {
                if (index + 1 < value.Length
                    && Rune.TryCreate(current, value[index + 1], out var rune))
                {
                    if (MustEscape(Rune.GetUnicodeCategory(rune)))
                    {
                        AppendScalarEscape(result, rune.Value);
                        index++;
                    }
                    else
                    {
                        result.Append(current);
                        result.Append(value[++index]);
                    }

                    continue;
                }

                AppendCodeUnitEscape(result, current);
                continue;
            }

            if (char.IsLowSurrogate(current) || MustEscape(char.GetUnicodeCategory(current)))
            {
                AppendCodeUnitEscape(result, current);
                continue;
            }

            result.Append(current);
        }

        return result.ToString();
    }

    private static bool MustEscape(UnicodeCategory category) =>
        category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;

    private static void AppendCodeUnitEscape(StringBuilder result, char value) =>
        result.Append("\\u").Append(((int)value).ToString("X4", CultureInfo.InvariantCulture));

    private static void AppendScalarEscape(StringBuilder result, int value) =>
        result.Append("\\U").Append(value.ToString("X8", CultureInfo.InvariantCulture));
}
