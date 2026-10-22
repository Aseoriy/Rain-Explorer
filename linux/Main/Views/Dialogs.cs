using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>Single-line text prompt (rename, new folder, new file, section names).</summary>
public sealed class InputDialog : DialogWindow
{
    private readonly TextBox _box;

    private InputDialog(string title, string prompt, string initial, int? selectionLength)
        : base(title, iconKey: null, width: 400)
    {
        _box = new TextBox { Text = initial, Height = 32 };
        _box.Classes.Add("field");
        var panel = new StackPanel();
        panel.Children.Add(Message(prompt, top: 10));
        panel.Children.Add(_box);
        Body = panel;

        AddButton("Cancel", () => Finish(null));
        AddButton("OK", Accept, DialogButtonKind.Primary, isDefault: true);

        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _box.Focus();
            _box.SelectionStart = 0;
            _box.SelectionEnd = Math.Clamp(selectionLength ?? initial.Length, 0, initial.Length);
        }, DispatcherPriority.Input);
    }

    private void Accept()
    {
        string value = _box.Text?.Trim() ?? string.Empty;
        if (value.Length == 0) return;
        Finish(value);
    }

    public static Task<string?> Ask(Window? owner, string title, string prompt, string initial = "",
        int? selectionLength = null) =>
        new InputDialog(title, prompt, initial, selectionLength).ShowFor<string>(owner);
}

/// <summary>A small themed yes/no confirmation: red trash glyph when destructive, info otherwise.</summary>
public sealed class ConfirmDialog : DialogWindow
{
    private ConfirmDialog(string title, string message, string okText, string? cancelText, bool danger)
        : base(title, danger ? "trash" : "info", danger ? "Danger" : "AccentBright")
    {
        Body = Message(message);
        if (cancelText is not null) AddButton(cancelText, () => Finish(false));
        AddButton(okText, () => Finish(true), DialogButtonKind.Primary, isDefault: true);
    }

    public static async Task<bool> Ask(Window? owner, string title, string message,
        string okText = "OK", string cancelText = "Cancel", bool danger = false) =>
        await new ConfirmDialog(title, message, okText, cancelText, danger).ShowFor<bool>(owner);

    /// <summary>A one-button notice.</summary>
    public static Task Inform(Window? owner, string title, string message) =>
        new ConfirmDialog(title, message, "OK", cancelText: null, danger: false).ShowFor<bool>(owner);
}

public enum DeleteChoice { Cancel, Trash, Permanent }

/// <summary>Asks whether to move items to the trash or delete them permanently.</summary>
public sealed class DeleteDialog : DialogWindow
{
    private DeleteDialog(string message) : base("Delete", "trash", "Danger", width: 440)
    {
        Body = Message(message);
        AddButton("Cancel", () => Finish(DeleteChoice.Cancel));
        AddButton("Delete permanently", () => Finish(DeleteChoice.Permanent), DialogButtonKind.Danger);
        AddButton("Move to Trash", () => Finish(DeleteChoice.Trash), DialogButtonKind.Primary, isDefault: true);
    }

    public static async Task<DeleteChoice> Ask(Window? owner, string message) =>
        await new DeleteDialog(message).ShowFor<DeleteChoice>(owner);
}

/// <summary>A single themed choice for all file conflicts in one transfer.</summary>
public sealed class ConflictDialog : DialogWindow
{
    private ConflictDialog(string title, string message, string detail, bool moving)
        : base(title, "copy", width: 500)
    {
        var panel = new StackPanel();
        panel.Children.Add(Message(message, top: 14));
        var detailText = Message(detail, "TextMuted", top: 8);
        detailText.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(detailText);
        Body = panel;
        AddButton("Cancel", () => Finish(TransferConflictChoice.Cancel));
        AddButton("Skip all", () => Finish(TransferConflictChoice.SkipAll));
        AddButton(moving ? "Replace and move all" : "Replace all",
            () => Finish(TransferConflictChoice.ReplaceAll), DialogButtonKind.Primary, isDefault: true);
    }

    public static async Task<TransferConflictChoice> Ask(Window? owner, bool moving,
        IReadOnlyList<TransferConflict> conflicts)
    {
        if (conflicts.Count == 0) return TransferConflictChoice.SkipAll;

        int count = conflicts.Count;
        string itemLabel = count == 1 ? "one item" : $"{count} items";
        string operation = moving ? "move" : "copy";
        string destination = Path.GetDirectoryName(conflicts[0].Destination) ?? "the destination";
        string sample = string.Join(", ", conflicts.Take(3).Select(c =>
            $"“{PathUtil.DisplayName(c.Source)}”"));
        if (count > 3) sample += ", …";

        var dialog = new ConflictDialog(
            $"Replace items while you {operation}",
            $"The destination already contains {itemLabel} with the same name.",
            $"Choose once for this transfer to {PathUtil.Tildify(destination)}. Conflicts include {sample}.",
            moving);
        TransferConflictChoice? choice = await dialog.ShowFor<TransferConflictChoice?>(owner);
        return choice ?? TransferConflictChoice.Cancel;
    }
}

/// <summary>Grid of selectable icon swatches a pinned folder can use.</summary>
public sealed class IconPickerDialog : DialogWindow
{
    private readonly ListBox _list;

    private IconPickerDialog(string current) : base("Choose an icon", iconKey: null)
    {
        _list = new ListBox
        {
            Margin = new Thickness(0, 14, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MaxHeight = 300,
            ItemsSource = IconCatalog.Keys,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
                        ItemTemplate = new FuncDataTemplate<string>((key, _) => new Icon
            {
                Kind = key, Width = 20, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }),
            SelectedItem = IconCatalog.Keys.Contains(current) ? current : null,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ApplyTheme(_list, "IconSwatch", container: true);
        _list.DoubleTapped += (_, _) => Accept();
        Body = _list;

        AddButton("Cancel", () => Finish(null));
        AddButton("Use icon", Accept, DialogButtonKind.Primary, isDefault: true);
    }

    private void Accept()
    {
        if (_list.SelectedItem is string key) Finish(key);
    }

    public static Task<string?> Ask(Window? owner, string current) =>
        new IconPickerDialog(current).ShowFor<string>(owner);
}
