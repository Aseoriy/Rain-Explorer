namespace RainExplorer.Models;

/// <summary>The Lucide icon keys offered when customizing a pinned item's icon.
/// Each must exist as an "Ic.&lt;key&gt;" resource in Themes/Icons.xaml.</summary>
public static class IconCatalog
{
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "folder", "folder-open", "folder-plus", "home", "star",
        "download", "image", "music", "film", "file-text",
        "file-code", "file", "archive", "package", "hard-drive",
        "monitor", "globe", "cloud", "zap", "terminal",
        "palette", "shield", "calendar", "clock", "info",
        "list", "hash", "link", "settings", "search",
    };
}
