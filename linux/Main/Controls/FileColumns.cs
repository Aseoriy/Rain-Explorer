using Avalonia.Controls;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

/// <summary>
/// Details-view column widths shared by the header and every row (the role WPF's
/// GridView columns play in the Windows build). Dragging a header edge resizes the
/// column in every pane, like the Windows build's resizable GridView headers.
/// </summary>
public sealed class FileColumns : ObservableObject
{
    public static FileColumns Shared { get; } = new();

    private const double MinWidth = 50;

    private GridLength _name = new(380);
    private GridLength _modified = new(158);
    private GridLength _created = new(158);
    private GridLength _type = new(116);
    private GridLength _size = new(92);

    public GridLength Name { get => _name; private set => Set(ref _name, value); }
    public GridLength Modified { get => _modified; private set => Set(ref _modified, value); }
    public GridLength Created { get => _created; private set => Set(ref _created, value); }
    public GridLength Type { get => _type; private set => Set(ref _type, value); }
    public GridLength Size { get => _size; private set => Set(ref _size, value); }

    public void Resize(string column, double delta)
    {
        GridLength Grow(GridLength current) => new(Math.Max(MinWidth, current.Value + delta));
        switch (column)
        {
            case "Name": Name = Grow(Name); break;
            case "Modified": Modified = Grow(Modified); break;
            case "Created": Created = Grow(Created); break;
            case "Type": Type = Grow(Type); break;
            case "Size": Size = Grow(Size); break;
        }
    }
}
