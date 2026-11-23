using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RainExplorer.Views;

public enum RainDialogButton { Primary, Ghost, Danger }

/// <summary>
/// A code-built dialog with the same glass card look as Rain's XAML dialogs (title with an icon, a body,
/// and a row of buttons). Used by dialogs ported from the Linux build, which builds its dialogs in code too.
/// </summary>
public class RainDialog : Window
{
    private readonly ContentControl _body = new();
    private readonly StackPanel _buttons = new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
    };

    protected RainDialog(string title, string icon, double width = 480)
    {
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 820;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Title = title;
        FontSize = 13;
        SetResourceReference(FontFamilyProperty, "AppFont");
        SetResourceReference(ForegroundProperty, "Text");

        var glyph = new Path
        {
            Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(StyleProperty, "IconPath");
        glyph.SetResourceReference(Path.DataProperty, "Ic." + icon);
        glyph.SetResourceReference(Shape.StrokeProperty, "AccentBright");
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        header.Children.Add(glyph);
        header.Children.Add(heading);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_buttons, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(_buttons);
        layout.Children.Add(_body);

        // The shadow sits on its own layer behind the card. With the effect on the card itself, WPF drew
        // all the text through the effect's off-screen bitmap and it came out soft.
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(12), Margin = new Thickness(14),
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.62, BlurRadius = 32, ShadowDepth = 5 },
        };
        shadow.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        var card = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18),
            Margin = new Thickness(14), Child = layout,
        };
        card.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        card.SetResourceReference(Border.BorderBrushProperty, "Line2");
        var root = new Grid();
        root.Children.Add(shadow);
        root.Children.Add(card);
        Content = root;
    }

    /// <summary>The dialog's main content (between the title and the buttons).</summary>
    protected object? Body
    {
        get => _body.Content;
        set => _body.Content = value;
    }

    /// <summary>A wrapped line of text in a theme colour.</summary>
    protected static TextBlock Message(string text, string brush = "TextDim", double top = 10)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    protected Button AddButton(string text, Action onClick, RainDialogButton kind = RainDialogButton.Ghost,
        bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = text, IsDefault = isDefault, IsCancel = isCancel,
            Margin = new Thickness(_buttons.Children.Count == 0 ? 0 : 8, 0, 0, 0),
        };
        button.SetResourceReference(StyleProperty, kind switch
        {
            RainDialogButton.Primary => "PrimaryButton",
            RainDialogButton.Danger => "PrimaryButton",
            _ => "GhostButton",
        });
        button.Click += (_, _) => onClick();
        _buttons.Children.Add(button);
        return button;
    }

    /// <summary>Close the dialog (true = the main action was taken).</summary>
    protected void Finish(bool accepted)
    {
        try { DialogResult = accepted; }
        catch (InvalidOperationException) { Close(); }   // shown without ShowDialog
    }

    /// <summary>Show modally over <paramref name="owner"/>; true when the dialog was accepted.</summary>
    public bool ShowOver(Window? owner)
    {
        if (owner is not null && owner.IsVisible) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return ShowDialog() == true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(false);
        }
    }
}
