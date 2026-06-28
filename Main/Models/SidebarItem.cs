namespace RainExplorer.Models;

/// <summary>An entry in the left navigation pane (quick-access place or drive).</summary>
public sealed class SidebarItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    /// <summary>Lucide icon key for this entry (see Themes/Icons.xaml).</summary>
    public required string IconKey { get; init; }
    public bool IsHeader { get; init; }
    /// <summary>True for user-pinned Quick Access entries (offer "Unpin").</summary>
    public bool IsPinned { get; init; }
}
