using RainExplorer.ViewModels;

namespace RainExplorer.Models;

/// <summary>A named, persisted workspace of folder tabs.</summary>
public sealed class TabProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New project";
    public List<TabProjectTab> Tabs { get; set; } = new();
    public int SelectedIndex { get; set; }
}

/// <summary>One tab remembered inside a <see cref="TabProject"/>.</summary>
public sealed class TabProjectTab
{
    public string Target { get; set; } = TabViewModel.HomeToken;
    public bool IsPinned { get; set; }
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
}
