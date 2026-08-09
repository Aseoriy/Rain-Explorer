namespace RainExplorer.Models;

/// <summary>A persisted snapshot of the window's panes, tabs, and active selections.</summary>
public sealed class ExplorerSession
{
    public List<string> LeftTabs { get; set; } = new();
    /// <summary>Whether each entry in <see cref="LeftTabs"/> was pinned when saved.</summary>
    public List<bool> LeftPinnedTabs { get; set; } = new();
    public List<string?> LeftTabGroups { get; set; } = new();
    public List<string?> LeftTabGroupNames { get; set; } = new();
    public int LeftSelectedIndex { get; set; }
    public string? LeftProjectId { get; set; }
    public List<string> RightTabs { get; set; } = new();
    /// <summary>Whether each entry in <see cref="RightTabs"/> was pinned when saved.</summary>
    public List<bool> RightPinnedTabs { get; set; } = new();
    public List<string?> RightTabGroups { get; set; } = new();
    public List<string?> RightTabGroupNames { get; set; } = new();
    public int RightSelectedIndex { get; set; }
    public string? RightProjectId { get; set; }
    public bool ActivePaneIsRight { get; set; }
}
