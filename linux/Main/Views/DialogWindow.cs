using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace RainExplorer.Views;

public enum DialogButtonKind { Dialog, Primary, Ghost, Danger }

/// <summary>
/// Shared chrome for Rain's modal dialogs, matching the Windows build: a frameless,
/// transparent window holding one rounded glass card (12px corners, Line2 border,
/// 18px padding), a draggable glyph + title row, the body, and a right-aligned
/// button row. Escape cancels and Enter triggers the default button.
/// </summary>
public class DialogWindow : Window
{
    private StackPanel? _buttons;
    private ContentControl? _body;
    private Action? _defaultClick;
    private object? _result;

    /// <summary>Chrome only — derived XAML dialogs supply their own card layout.</summary>
    public DialogWindow()
    {
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        FontSize = 13;
        Bind(ForegroundProperty, this.GetResourceObservable("Text"));

        // Without a compositor the transparent corners would render black; fall back to
        // a square card that fills the window instead.
        Opened += (_, _) =>
        {
            if (ActualTransparencyLevel != WindowTransparencyLevel.None || Content is not Border card) return;
            card.CornerRadius = default;
            Bind(BackgroundProperty, this.GetResourceObservable("MenuBg"));
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(null);
            }
            else if (e.Key == Key.Enter && _defaultClick is not null
                     && FocusManager?.GetFocusedElement() is not Button)
            {
                e.Handled = true;
                _defaultClick();
            }
        };
    }

    /// <summary>The standard small dialog: glyph + title, body, button row.</summary>
    protected DialogWindow(string title, string? iconKey = null, string iconBrush = "AccentBright",
        double width = 420) : this()
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        if (iconKey is not null)
        {
            var glyph = new Controls.Icon
            {
                Kind = iconKey, Width = 20, Height = 20, StrokeThickness = 1.7,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            glyph.Bind(Controls.Icon.ForegroundProperty, glyph.GetResourceObservable(iconBrush));
            header.Children.Add(glyph);
        }
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        _body = new ContentControl();
        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(_body);
        root.Children.Add(_buttons);
        Content = Card(root, new Thickness(18));
    }

    /// <summary>The rounded glass card every dialog sits in.</summary>
    protected Border Card(Control child, Thickness padding)
    {
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = padding,
            ClipToBounds = true,
            Child = child,
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("MenuBg"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Line2"));
        return card;
    }

    /// <summary>Set the dialog body.</summary>
    protected object? Body
    {
        get => _body?.Content;
        set { if (_body is not null) _body.Content = value; }
    }

    protected Button AddButton(string text, Action onClick, DialogButtonKind kind = DialogButtonKind.Dialog,
        bool isDefault = false)
    {
        var button = new Button { Content = text };
        ApplyTheme(button, kind switch
        {
            DialogButtonKind.Primary => "PrimaryButton",
            DialogButtonKind.Ghost => "GhostButton",
            DialogButtonKind.Danger => "DangerDialogButton",
            _ => "DialogButton",
        });
        button.Click += (_, _) => onClick();
        _buttons?.Children.Add(button);
        if (isDefault) SetDefault(onClick);
        return button;
    }

    /// <summary>The action Enter triggers when focus is not on a button.</summary>
    protected void SetDefault(Action onClick) => _defaultClick = onClick;

    /// <summary>Apply a keyed ControlTheme from Styles.axaml (to the item containers for <paramref name="container"/>).</summary>
    protected static void ApplyTheme(StyledElement element, string themeKey, bool container = false)
    {
        if (Application.Current?.TryFindResource(themeKey, out object? theme) != true || theme is not ControlTheme ct)
            return;
        if (container && element is ItemsControl items) items.ItemContainerTheme = ct;
        else element.Theme = ct;
    }

    /// <summary>Dim message text, as in the Windows dialogs.</summary>
    protected static TextBlock Message(string text, string brush = "TextDim", double top = 12)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 4),
        };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brush));
        return block;
    }

    /// <summary>Show modally over <paramref name="owner"/> (or standalone when there is none).</summary>
    public async Task<T?> ShowFor<T>(Window? owner)
    {
        if (owner is null)
        {
            var done = new TaskCompletionSource<T?>();
            Closed += (_, _) => done.TrySetResult(_result is T t ? t : default);
            Show();
            return await done.Task;
        }
        await ShowDialog(owner);
        return _result is T value ? value : default;
    }

    protected void Finish(object? result)
    {
        _result = result;
        Close(result);
    }

    protected void DragFrom(Control handle) =>
        handle.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
}

internal static class ResourceExtensions
{
    public static object? FindResourceOrDefault(this Control control, string key) =>
        control.TryFindResource(key, out object? value) ? value
        : Application.Current?.TryFindResource(key, out value) == true ? value
        : null;

    public static Window? OwnerWindow(this Visual? visual) => visual?.GetVisualRoot() as Window;
}
