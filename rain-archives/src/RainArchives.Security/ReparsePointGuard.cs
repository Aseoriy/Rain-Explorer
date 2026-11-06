namespace RainArchives.Security;

public static class ReparsePointGuard
{
    public static void EnsureExistingPathHasNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The path does not have a filesystem root.");
        var relative = fullPath[root.Length..];
        var current = root;

        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Destination path contains a reparse point: '{current}'.");
            }
        }
    }
}
