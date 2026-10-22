// Keep the same Avalonia 11.x drag API as file dragging in PaneView.
#pragma warning disable CS0618
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer;

public partial class MainWindow
{
    internal const string PinDragFormat = "RainExplorerPinReorder";
    internal const string SectionDragFormat = "RainExplorerSectionReorder";
    private Point _sidebarDragStart;
    private SidebarNode? _sidebarDragNode;
    private PointerPressedEventArgs? _sidebarPress;
    private bool _sidebarDragging;
    private bool _sidebarSuppressTap;
    private SidebarNode? _sidebarDropTarget;

    private void Sidebar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_sidebarDragging) return;
        _sidebarSuppressTap = false;
        _sidebarPress = null;
        _sidebarDragNode = null;
        if (!e.GetCurrentPoint(SidebarTree).Properties.IsLeftButtonPressed || e.Source is not Visual source
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        var node = source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as SidebarNode;
        if (node is not { IsHeader: true } and not { Kind: NodeKind.Pinned }) return;
        _sidebarDragStart = e.GetPosition(SidebarTree);
        _sidebarDragNode = node;
        _sidebarPress = e;
    }

    private async void Sidebar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_sidebarDragging || _sidebarPress is null || _sidebarDragNode is null
            || !e.GetCurrentPoint(SidebarTree).Properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(SidebarTree);
        if (Math.Abs(position.X - _sidebarDragStart.X) < 6 && Math.Abs(position.Y - _sidebarDragStart.Y) < 6) return;
        var node = _sidebarDragNode;
        _sidebarPress = null;
        _sidebarDragNode = null;
        _sidebarDragging = _sidebarSuppressTap = true;
        var data = new DataObject();
        data.Set(node.IsHeader ? SectionDragFormat : PinDragFormat,
            node.IsHeader ? node.GroupKey : node.GroupKey + "|" + node.Path);
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        catch (Exception ex) { ShowStatus($"⚠️ {ex.Message}"); }
        finally
        {
            _sidebarDragging = false;
            e.Pointer.Capture(null);
            ClearSidebarDropFeedback();
        }
    }

    private TreeViewItem? SidebarItemUnder(DragEventArgs e) =>
        (SidebarTree.InputHitTest(e.GetPosition(SidebarTree)) as Visual)?
            .FindAncestorOfType<TreeViewItem>(includeSelf: true);

    private static bool DropsAfter(TreeViewItem? item, DragEventArgs e) =>
        item?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Header") is { } header
        && e.GetPosition(header).Y > header.Bounds.Height / 2;

    private static (string Key, string Path) ParsePinDrag(DragEventArgs e)
    {
        string value = e.Data.Get(PinDragFormat) as string ?? "";
        int separator = value.IndexOf('|');
        return separator < 0 ? ("", "") : (value[..separator], value[(separator + 1)..]);
    }

    private void Sidebar_DragOver(object? sender, DragEventArgs e)
    {
        var item = SidebarItemUnder(e);
        var node = item?.DataContext as SidebarNode;
        ClearSidebarDropFeedback();
        if (e.Data.Contains(SectionDragFormat))
        {
            string key = e.Data.Get(SectionDragFormat) as string ?? "";
            bool valid = node is not null && node.GroupKey.Length > 0 && key.Length > 0 && key != node.GroupKey;
            e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
            if (valid) ShowSidebarInsertion(item!, DropsAfter(item, e), wholeSection: true);
        }
        else if (e.Data.Contains(PinDragFormat))
        {
            var (key, path) = ParsePinDrag(e);
            bool reorder = node is { Kind: NodeKind.Pinned } && node.GroupKey == key && !PathUtil.Same(node.Path, path);
            bool transfer = node is { IsHeader: true, IsPinnedHeader: true } && node.GroupKey != key;
            bool valid = MainViewModel.IsPinnedTo(key, path) && (reorder || transfer);
            e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
            if (valid && reorder) ShowSidebarInsertion(item!, DropsAfter(item, e));
            else if (valid) SetSidebarDropTarget(node);
        }
        else
        {
            var paths = PaneView.DraggedPaths(e);
            bool pin = CanPinFolders(node, paths);
            e.DragEffects = pin ? DragDropEffects.Link : FileDropService.EffectFor(paths, DropFolderFor(node), e.KeyModifiers);
            if (e.DragEffects != DragDropEffects.None) SetSidebarDropTarget(node);
        }
        e.Handled = true;
        if (e.DragEffects != DragDropEffects.None) ScrollSidebarDrag(e.GetPosition(SidebarTree));
    }

    private async void Sidebar_Drop(object? sender, DragEventArgs e)
    {
        var item = SidebarItemUnder(e);
        var node = item?.DataContext as SidebarNode;
        bool after = DropsAfter(item, e);
        ClearSidebarDropFeedback();
        e.Handled = true;
        if (e.Data.Contains(SectionDragFormat))
        {
            if (node is not null) MainViewModel.ReorderGroup(e.Data.Get(SectionDragFormat) as string ?? "", node.GroupKey, after);
            return;
        }
        if (e.Data.Contains(PinDragFormat))
        {
            var (key, path) = ParsePinDrag(e);
            if (node is { Kind: NodeKind.Pinned } && node.GroupKey == key)
                MainViewModel.ReorderPin(key, path, node.Path, after);
            else if (node is { IsHeader: true, IsPinnedHeader: true })
                MainViewModel.MovePin(key, node.GroupKey, path);
            return;
        }
        var files = PaneView.DraggedPaths(e);
        if (CanPinFolders(node, files))
        {
            foreach (string folder in files!) MainViewModel.PinTo(node!.GroupKey, folder);
            return;
        }
        string? dest = DropFolderFor(node);
        var effect = FileDropService.EffectFor(files, dest, e.KeyModifiers);
        if (files is null || dest is null || effect == DragDropEffects.None) return;
        string? error = await FileDropService.Perform(files, dest, effect == DragDropEffects.Move, this);
        if (error is not null) ShowStatus($"⚠️ {error}");
        _vm.RefreshAll(afterKnownOperation: true);
    }

    private static bool CanPinFolders(SidebarNode? node, IReadOnlyList<string>? paths) =>
        node is { IsHeader: true, IsPinnedHeader: true } && paths is { Count: > 0 } && paths.All(Directory.Exists);

    private static string? DropFolderFor(SidebarNode? node) =>
        node is { IsHeader: false } && Directory.Exists(node.Path) ? node.Path : null;

    private void ShowSidebarInsertion(TreeViewItem item, bool after, bool wholeSection = false)
    {
        if (wholeSection && item.DataContext is SidebarNode node)
        {
            var rows = SidebarTree.GetVisualDescendants().OfType<TreeViewItem>()
                .Where(row => row.DataContext is SidebarNode target && target.GroupKey == node.GroupKey).ToArray();
            if (rows.Length > 0) item = after ? rows[^1] : rows[0];
        }
        var header = item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Header");
        if (header?.TranslatePoint(default, SidebarDragOverlay) is not { } top) return;
        Canvas.SetLeft(SidebarInsertionLine, top.X);
        Canvas.SetTop(SidebarInsertionLine, top.Y + (after ? header.Bounds.Height : 0) - 1);
        SidebarInsertionLine.Width = header.Bounds.Width;
        SidebarInsertionLine.IsVisible = true;
    }

    private void ScrollSidebarDrag(Point position)
    {
        var scroll = SidebarTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        double change = position.Y < 25 ? -12 : position.Y > SidebarTree.Bounds.Height - 25 ? 12 : 0;
        if (change != 0) scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + change, 0,
            Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
    }

    private void ClearSidebarDropFeedback()
    {
        SetSidebarDropTarget(null);
        SidebarInsertionLine.IsVisible = false;
    }

    private void SetSidebarDropTarget(SidebarNode? node)
    {
        if (ReferenceEquals(_sidebarDropTarget, node)) return;
        if (_sidebarDropTarget is not null) _sidebarDropTarget.IsDropTarget = false;
        _sidebarDropTarget = node;
        if (node is not null) node.IsDropTarget = true;
    }
}
