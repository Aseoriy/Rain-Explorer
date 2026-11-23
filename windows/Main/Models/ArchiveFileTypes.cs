namespace RainExplorer.Models;

/// <summary>
/// Archive filename hints understood by Rain Explorer. Content detection remains the
/// Worker's authority after a file is opened; this list is only for shell/UI routing.
/// </summary>
internal static class ArchiveFileTypes
{
    private static readonly string[] SupportedSuffixes =
    [
        ".tar.bz2",
        ".tar.gz",
        ".tar.xz",
        ".bzip2",
        ".gzip",
        ".tbz2",
        ".tgz",
        ".txz",
        ".bz2",
        ".gz",
        ".xz",
        ".zip",
        ".7z",
        ".rar",
        ".tar",
    ];

    // Windows associates the terminal extension. Compound TAR variants are covered by
    // .gz/.bz2/.xz. Numeric split-volume extensions are intentionally excluded because
    // .001 is not archive-specific and claiming it would hijack unrelated files.
    internal static IReadOnlyList<string> AssociationExtensions { get; } =
        Array.AsReadOnly(
        [
            ".zip", ".7z", ".rar", ".tar", ".tgz", ".tbz2", ".txz",
            ".gzip", ".bzip2", ".gz", ".bz2", ".xz",
        ]);

    internal static bool IsSupportedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && SupportedSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    internal static string RemoveSupportedSuffix(string fileName)
    {
        string? suffix = SupportedSuffixes.FirstOrDefault(candidate =>
            fileName.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
        return suffix is null ? fileName : fileName[..^suffix.Length];
    }

    private const int MaximumMenuNameLength = 40;

    /// <summary>
    /// Name (without extension) for a new archive of <paramref name="sources"/>, like WinRAR: a folder
    /// keeps its whole name ("v1.2" stays "v1.2"), a file drops its extension ("report.docx" → "report"),
    /// several items take the name of the folder they're in. GZ/BZ2 compress one file as-is, so they keep
    /// its extension ("notes.txt" → "notes.txt.gz").
    /// </summary>
    internal static string SuggestedArchiveStem(
        IReadOnlyList<ArchiveCreationSource> sources,
        string currentFolder,
        ArchiveWriteFormat format)
    {
        string stem;
        if (sources.Count == 1)
        {
            string name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(sources[0].SourcePath));
            stem = sources[0].Kind == ArchiveCreationSourceKind.Directory
                || format is ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2 or ArchiveWriteFormat.Xz
                ? name
                : System.IO.Path.GetFileNameWithoutExtension(name);
        }
        else
        {
            stem = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(currentFolder));
        }
        return string.IsNullOrWhiteSpace(stem) ? "Archive" : stem;
    }

    /// <summary>The first free "name.ext", "name (2).ext", … in <paramref name="folder"/>, keeping
    /// double extensions like ".tar.gz" together.</summary>
    internal static string FreeArchivePath(string folder, string stem, string extension)
    {
        for (int n = 1; n <= 10_000; n++)
        {
            string candidate = System.IO.Path.Combine(folder, (n == 1 ? stem : $"{stem} ({n})") + extension);
            if (!System.IO.File.Exists(candidate) && !System.IO.Directory.Exists(candidate)) return candidate;
        }
        return System.IO.Path.Combine(folder, stem + extension);
    }

    /// <summary>Menu text for one-click compression: <c>Compress to "Photos.zip"</c>.</summary>
    internal static string CompressToHeader(string stem, string extension)
    {
        if (stem.Length > MaximumMenuNameLength) stem = stem[..(MaximumMenuNameLength - 1)].TrimEnd() + "…";
        return $"Compress to \"{stem}{extension}\"".Replace("_", "__", StringComparison.Ordinal);
    }

    /// <summary>
    /// Menu text for extracting each archive into a new folder beside it, named after the archive:
    /// <c>Extract to "Photos\"</c> for one (as WinRAR words it), "Extract each to its own folder" for
    /// several. Underscores are doubled so WPF doesn't read them as access keys.
    /// </summary>
    internal static string ExtractToOwnFolderHeader(IReadOnlyList<string> archivePaths)
    {
        if (archivePaths.Count != 1) return "Extract each to its own folder";
        string stem = RemoveSupportedSuffix(System.IO.Path.GetFileName(archivePaths[0]));
        if (string.IsNullOrWhiteSpace(stem)) return "Extract to new folder";
        if (stem.Length > MaximumMenuNameLength) stem = stem[..(MaximumMenuNameLength - 1)].TrimEnd() + "…";
        return $"Extract to \"{stem}\\\"".Replace("_", "__", StringComparison.Ordinal);
    }
}
