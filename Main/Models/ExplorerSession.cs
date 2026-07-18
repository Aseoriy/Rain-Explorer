namespace RainExplorer.Models;

/// <summary>A persisted snapshot of the window's panes, tabs, and active selections.</summary>
public sealed class ExplorerSession
{
    public List<string> LeftTabs { get; set; } = new();
    public int LeftSelectedIndex { get; set; }
    public List<string> RightTabs { get; set; } = new();
    public int RightSelectedIndex { get; set; }
    public bool ActivePaneIsRight { get; set; }
}
