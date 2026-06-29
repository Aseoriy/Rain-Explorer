namespace RainExplorer.Models;

/// <summary>
/// A user-created sidebar list (e.g. "Quick access 2") with its own collapse state
/// and pinned folders. The built-in Quick access list lives in
/// <see cref="AppSettings.Pinned"/>; these are the extra ones.
/// </summary>
public sealed class SidebarGroup
{
    public string Name { get; set; } = "New list";
    public bool Collapsed { get; set; }
    public List<PinnedItem> Items { get; set; } = new();
}
