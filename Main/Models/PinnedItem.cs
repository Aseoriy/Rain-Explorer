namespace RainExplorer.Models;

/// <summary>
/// A user-pinned Quick Access entry: a folder shown in the sidebar with an
/// optional custom Lucide icon key. Persisted in <see cref="AppSettings"/>.
/// </summary>
public sealed class PinnedItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string IconKey { get; set; } = "folder";
}
