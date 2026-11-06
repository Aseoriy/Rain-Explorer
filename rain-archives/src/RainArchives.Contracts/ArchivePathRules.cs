using System.Text;

namespace RainArchives.Contracts;

public static class ArchivePathRules
{
    public const int MaximumPathDepth = 256;
    public const int MaximumComponentLength = 255;
    public const int MaximumNormalizedPathLength = 32_000;

    private static readonly char[] Separators = ['/', '\\'];
    private static readonly HashSet<string> ReservedNames = CreateReservedNames();

    public static bool TryNormalize(
        string rawPath,
        out string relativePath,
        out string collisionKey,
        out string reason) =>
        TryNormalize(
            rawPath,
            MaximumPathDepth,
            MaximumComponentLength,
            MaximumNormalizedPathLength,
            out relativePath,
            out collisionKey,
            out reason);

    public static bool TryNormalize(
        string rawPath,
        int maximumPathDepth,
        int maximumComponentLength,
        int maximumFinalPathLength,
        out string relativePath,
        out string collisionKey,
        out string reason)
    {
        relativePath = string.Empty;
        collisionKey = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrEmpty(rawPath))
        {
            reason = "Entry path is empty.";
            return false;
        }

        if (rawPath[0] is '/' or '\\'
            || rawPath.StartsWith("\\\\", StringComparison.Ordinal)
            || rawPath.StartsWith("//", StringComparison.Ordinal)
            || rawPath.StartsWith("\\??\\", StringComparison.Ordinal)
            || rawPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || rawPath.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || IsDriveRelativeOrAbsolute(rawPath))
        {
            reason = "Absolute, UNC, device, and drive-relative paths are not allowed.";
            return false;
        }

        var components = rawPath.Split(Separators, StringSplitOptions.None);
        if (components.Length > maximumPathDepth)
        {
            reason = $"Entry exceeds the {maximumPathDepth}-component depth limit.";
            return false;
        }

        var normalized = new string[components.Length];
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component.Length == 0)
            {
                reason = "Empty path components and repeated separators are not allowed.";
                return false;
            }

            if (component is "." or "..")
            {
                reason = "Dot and parent-directory components are not allowed.";
                return false;
            }

            if (component.Length > maximumComponentLength)
            {
                reason = $"A path component exceeds {maximumComponentLength} UTF-16 units.";
                return false;
            }

            if (component[^1] is '.' or ' ')
            {
                reason = "Windows removes trailing dots or spaces, so this name is unsafe.";
                return false;
            }

            if (component.Any(IsInvalidWindowsFileNameCharacter))
            {
                reason = "The path contains a control character, alternate-data-stream marker, or invalid Windows filename character.";
                return false;
            }

            string nfc;
            try
            {
                nfc = component.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                reason = "The path contains invalid Unicode data.";
                return false;
            }

            var baseName = nfc.Split('.')[0].TrimEnd(' ', '.');
            if (ReservedNames.Contains(baseName))
            {
                reason = "The path uses a reserved Windows device name.";
                return false;
            }

            normalized[index] = nfc;
        }

        relativePath = string.Join(Path.DirectorySeparatorChar, normalized);
        if (relativePath.Length > maximumFinalPathLength)
        {
            reason = $"Entry exceeds the {maximumFinalPathLength}-unit path limit.";
            return false;
        }

        collisionKey = string.Join('/', normalized).ToUpperInvariant();
        return true;
    }

    public static string GetCollisionKey(string rawPath)
    {
        if (!TryNormalize(rawPath, out _, out var collisionKey, out var reason))
        {
            throw new ArgumentException(reason, nameof(rawPath));
        }

        return collisionKey;
    }

    private static bool IsInvalidWindowsFileNameCharacter(char character) =>
        character == '\0'
        || char.IsControl(character)
        || character is '<' or '>' or ':' or '"' or '|' or '?' or '*';

    private static bool IsDriveRelativeOrAbsolute(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private static HashSet<string> CreateReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
        };

        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        foreach (var suffix in new[] { '¹', '²', '³' })
        {
            names.Add($"COM{suffix}");
            names.Add($"LPT{suffix}");
        }

        return names;
    }
}
