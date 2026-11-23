using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// "What's new in 2.0": shown once after updating (and from Settings → About). Each feature has a
/// "Show me" that takes you straight to it.
/// </summary>
public sealed class WhatsNewDialog : DialogWindow
{
    /// <summary>The release this screen describes; stored in AppSettings.LastWhatsNewShown once seen.</summary>
    public const string Release = "2.0";

    /// <summary>"Show me" targets: "activity", "shortcuts", "commandBox", or "settings:&lt;search words&gt;".</summary>
    internal sealed record Feature(string Icon, string Title, string Text, string? Target);

    internal static IReadOnlyList<Feature> Features =>
    [
        new("zap", "Activity Center, your way",
            "Pin it open while you work, or pop it out into its own window that can stay on top.", "activity"),
        new("hash", "Every shortcut is yours",
            $"Change any keyboard shortcut in Settings → Shortcuts. Press {Keys("app.shortcuts")} anytime to see them all.", "shortcuts"),
        new("pencil", "Rename lots of files at once",
            $"Select several files and press {Keys("file.rename")}: find & replace, change case, add numbers or a new extension, with a live preview and one-step undo.", null),
        new("terminal", "Command box",
            $"Press {Keys("app.commandBox")} to run a command in the folder you're looking at. “cd” moves the pane with you.", "commandBox"),
        new("search", "Search Settings",
            "Type what you're after at the top of Settings and jump straight to it.", "settings:"),
        new("package", "Back up your setup",
            "Save your settings, pins, sidebar lists, tab projects and shortcuts to one file, and restore them anywhere.", "settings:back up restore"),
        new("external-link", "Git and GitHub, built in",
            "Status badges on files, a changes window for commits, pushes and pulls, and errors explained in plain words.", "settings:git"),
        new("sliders", "Smooth, or snappy",
            "Smooth scrolling with your own speed, always-smooth animations, or a performance mode that skips them.", "settings:smooth scrolling"),
        new("palette", "Bring your own font",
            "Search the font list, or import a font from a file or straight from a link.", "settings:import font"),
        new("list", "Tab projects",
            "Save a set of tabs as a project and get them all back in one click, from the button left of your tabs.", null),
    ];

    private readonly Action<string> _showMe;

    private WhatsNewDialog(Action<string> showMe) : base($"What's new in Rain Explorer {Release}", "star", width: 820)
    {
        _showMe = showMe;
        var intro = Message("Linux and Windows are on the same version now. Here's what's new; “Show me” takes you there.",
            "TextMuted", top: 6);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var features = Features;
        for (int i = 0; i < (features.Count + 1) / 2; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < features.Count; i++)
        {
            var tile = Tile(features[i]);
            Grid.SetRow(tile, i / 2);
            Grid.SetColumn(tile, i % 2 == 0 ? 0 : 2);
            grid.Children.Add(tile);
        }

        var body = new StackPanel();
        body.Children.Add(intro);
        body.Children.Add(new ScrollViewer
        {
            Content = grid, MaxHeight = 560, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 0, 8, 0),
        });
        Body = body;
        AddButton("Got it", () => Finish(null), DialogButtonKind.Primary, isDefault: true);
    }

    /// <summary>Show the screen; <paramref name="showMe"/> runs a feature's target after it closes.</summary>
    public static Task ShowFor(Window owner, Action<string> showMe)
    {
        var settings = SettingsStore.Instance.Settings;
        settings.LastWhatsNewShown = Release;
        return new WhatsNewDialog(showMe).ShowFor<object>(owner);
    }

    /// <summary>True when this release's screen hasn't been seen yet and Rain was updated (not freshly installed).</summary>
    public static bool ShouldShowAfterUpdate(bool firstRun)
    {
        var settings = SettingsStore.Instance.Settings;
        if (settings.LastWhatsNewShown == Release) return false;
        if (firstRun)
        {
            settings.LastWhatsNewShown = Release;   // a new install: nothing is "new" yet
            return false;
        }
        return true;
    }

    private Control Tile(Feature feature)
    {
        var icon = new Controls.Icon { Kind = feature.Icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top };
        icon.Bind(Controls.Icon.ForegroundProperty, icon.GetResourceObservable("AccentBright"));
        var badge = new Border
        {
            Child = icon, Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Padding = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Top,
        };
        badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("Glass2"));

        var title = new TextBlock { Text = feature.Title, FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Text"));
        var text = new TextBlock { Text = feature.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("TextMuted"));
        var words = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        words.Children.Add(title);
        words.Children.Add(text);
        if (feature.Target is { } target)
        {
            var showMe = new Button
            {
                Content = "Show me →", Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0),
                FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            ApplyTheme(showMe, "BareButton");
            showMe.Bind(Button.ForegroundProperty, showMe.GetResourceObservable("AccentBright"));
            Avalonia.Automation.AutomationProperties.SetName(showMe, $"Show me: {feature.Title}");
            showMe.Click += (_, _) =>
            {
                Finish(null);
                _showMe(target);
            };
            words.Children.Add(showMe);
        }

        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(words);
        var tile = new Border
        {
            Child = row, Padding = new Thickness(14, 12), Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
        };
        tile.Bind(Border.BackgroundProperty, tile.GetResourceObservable("Glass1"));
        tile.Bind(Border.BorderBrushProperty, tile.GetResourceObservable("Line1"));
        return tile;
    }

    private static string Keys(string id) =>
        ShortcutService.PrimaryGesture(id) is { } gesture ? ShortcutService.Describe(gesture) : "its shortcut";
}
