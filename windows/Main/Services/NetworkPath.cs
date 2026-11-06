using System.IO;

namespace RainExplorer.Services;

/// <summary>Lexical UNC handling, including a server before a share has been chosen.</summary>
internal static class NetworkPath
{
    public static string Normalize(string path)
    {
        string normalized = path.Trim().Replace('/', '\\');
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal)) return path.Trim();
        string remainder = normalized.TrimStart('\\');
        // Device and extended-length paths have their own Windows semantics.
        if (remainder.StartsWith(@"?\", StringComparison.Ordinal)
            || remainder.StartsWith(@".\", StringComparison.Ordinal)) return path.Trim();
        return @"\\" + remainder.TrimEnd('\\');
    }

    public static bool IsNetworkPath(string? path) => TryGetParts(path, out _);

    public static string GetDisplayName(string path)
    {
        string normalized = Normalize(path);
        // Path.GetFileName treats \\server\share as a filesystem root and returns "".
        return IsNetworkPath(normalized)
            ? normalized[(normalized.LastIndexOf('\\') + 1)..]
            : Path.GetFileName(path.TrimEnd('\\', '/'));
    }

    public static bool TryGetServerRoot(string? path, out string server)
    {
        server = string.Empty;
        if (!TryGetParts(path, out var parts) || parts.Length != 1) return false;
        server = @"\\" + parts[0];
        return true;
    }

    public static bool TryGetShareRoot(string? path, out string share)
    {
        share = string.Empty;
        if (!TryGetParts(path, out var parts) || parts.Length < 2) return false;
        share = @"\\" + parts[0] + @"\" + parts[1];
        return true;
    }

    public static string? GetParent(string path)
    {
        if (!TryGetParts(path, out var parts) || parts.Length == 1) return null;
        return @"\\" + string.Join('\\', parts[..^1]);
    }

    internal static bool IsValidShareName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool TryGetParts(string? path, out string[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = Normalize(path);
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        parts = normalized[2..].Split('\\');
        return parts.Length > 0 && parts.All(IsValidShareName);
    }
}
