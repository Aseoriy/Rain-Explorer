using RainArchives.Contracts;

namespace RainArchives.Security;

/// <summary>
/// Names the sole logical member of a raw stream archive from the caller's
/// final output name. The staging name is intentionally never used.
/// </summary>
internal static class RawArchiveEntryName
{
    public static IReadOnlyList<ArchiveCreationFile> Rewrite(
        IReadOnlyList<ArchiveCreationFile> files,
        string destinationPath,
        IReadOnlyList<string> suffixes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(suffixes);

        if (files.Count != 1 || files[0] is null)
        {
            return files;
        }

        var logicalName = Get(destinationPath, suffixes);
        return [files[0] with { ArchivePath = logicalName }];
    }

    private static string Get(string destinationPath, IReadOnlyList<string> suffixes)
    {
        var name = Path.GetFileName(destinationPath);
        foreach (var suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return string.IsNullOrWhiteSpace(name) ? "content" : name;
    }
}
