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
}
