using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Helpers;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

public partial class PaneView
{
    // Like the Windows port, all panes participate in one live-tab drag. Screen
    // coordinates let the same flow find split panes and other Rain windows.
    private static readonly HashSet<PaneView> LoadedPaneViews = new();
    private static PaneView? ActiveTabDragOwner;
    private readonly Dictionary<Control, bool> _suppressedTooltips = new();
    private sealed record TabDragSlot(TabViewModel Tab, Control Container, double X, double Width);
    private readonly List<TabDragSlot> _tabDragSlots = new();
    private ListBox? _tabPreviewBar;
    private PaneView? _tabDragReceiver;
    private int _tabPreviewIndex = -1;
    private double _tabDragWidth;
    private double _tabDragGrabRatio;
    private bool _tabDragLeftSourceBar;
    private MainWindow? _tabSplitWindow;
    private bool _tabSplitOnLeft;
    private bool _tabSplitArmed;
    private DispatcherTimer? _tabSplitTimer;

    private void TabBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox bar || TabFromSource(e.Source) is not { } tab) return;
        var point = e.GetCurrentPoint(bar);
        if (point.Properties.IsMiddleButtonPressed)
        {
            _vm?.CloseTab(tab);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed
            || e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        _tabDragCandidate = tab;
        _tabDragSourceBar = bar;
        _tabDragStart = point.Position;
        if (bar.ContainerFromItem(tab) is Control container)
        {
            var grab = e.GetPosition(container);
            _tabDragGrabOffset = new Vector(grab.X, grab.Y);
            _tabDragWidth = Math.Max(1, container.Bounds.Width);
            _tabDragGrabRatio = Math.Clamp(grab.X / _tabDragWidth, 0, 1);
        }
    }

    private void TabBar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragCandidate is not { } tab || _tabDragSourceBar is not { } bar || _vm is null) return;
        var point = e.GetCurrentPoint(bar);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndTabDrag(cancelled: true);
            return;
        }
        if (!_tabDragging)
        {
            var delta = point.Position - _tabDragStart;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
            CaptureTabPreview(_vm.SelectedTab);
            ActiveTabDragOwner?.EndTabDrag(cancelled: true);
            ActiveTabDragOwner = this;
            _tabDragging = true;
            tab.IsDragging = true;
            if (IsWholeGroupDrag) _vm.SelectedTopTab = tab;
            else _vm.SelectedTab = tab;
            _vm.BeginTabDrag(tab, IsWholeGroupDrag);
            _tabDragLeftSourceBar = false;
            _tabDragPointer = e.Pointer;
            e.Pointer.Capture(bar);
            foreach (var pane in LoadedPaneViews)
                foreach (var control in pane.GetVisualDescendants().OfType<Control>()) SuppressTooltip(control);
            ShowTabDragGhost(tab, bar);
        }
        UpdateOwnedTabDrag(bar.PointToScreen(point.Position));
        e.Handled = true;
    }

    private void SuppressTooltip(Control control)
    {
        if (ToolTip.GetTip(control) is null || _suppressedTooltips.ContainsKey(control)) return;
        _suppressedTooltips.Add(control, control.GetValue(ToolTip.ServiceEnabledProperty));
        ToolTip.SetIsOpen(control, false);
        control.SetValue(ToolTip.ServiceEnabledProperty, false);
    }

    private bool TryFindTabSurface(PixelPoint screen, out PaneView? receiver, out ListBox? bar)
    {
        receiver = null;
        bar = null;
        var windowUnderPointer = FindRainWindowAt(screen);
        // Prefer the active window where windows overlap. No popup participates in
        // this registry, so the floating card cannot steal its own drop target.
        foreach (var pane in LoadedPaneViews.OrderByDescending(p => p.OwnerWindow?.IsActive == true))
        {
            if (!pane.IsEffectivelyVisible || pane._vm is null
                || pane.OwnerWindow is not { IsVisible: true } window || window.WindowState == WindowState.Minimized) continue;
            if (windowUnderPointer is not null && !ReferenceEquals(window, windowUnderPointer)) continue;
            bool validGroup = _tabDragCandidate is { IsPinned: false }
                && !IsWholeGroupDrag && pane._vm.HasActiveGroup;
            if (validGroup && ContainsScreenPoint(pane.GroupTabBar, screen, 0)) bar = pane.GroupTabBar;
            else if (ContainsScreenPoint(pane.TopTabDropSurface, screen, 0)) bar = pane.TabBar;
            else if (ReferenceEquals(pane, _tabDragReceiver) && pane._tabPreviewBar is { } previous
                && (previous != pane.GroupTabBar || validGroup)
                && ContainsScreenPoint(previous == pane.TabBar ? pane.TopTabDropSurface : previous, screen, 24)) bar = previous;
            else if (ContainsScreenPoint(pane.TopTabDropSurface, screen, 24)) bar = pane.TabBar;
            else if (validGroup && ContainsScreenPoint(pane.GroupTabBar, screen, 14)) bar = pane.GroupTabBar;
            if (bar is null) continue;
            receiver = pane;
            return true;
        }
        return false;
    }

    private static Window? FindRainWindowAt(PixelPoint screen) => LoadedPaneViews
        .Select(p => p.OwnerWindow).OfType<Window>().Distinct()
        .Where(w => w.IsVisible && w.WindowState != WindowState.Minimized)
        .OrderByDescending(w => w.IsActive)
        .FirstOrDefault(w => ContainsScreenPoint((Control?)w.FindControl<Border>("RootBorder") ?? w, screen, 0));

    private static bool ContainsScreenPoint(Control surface, PixelPoint screen, double verticalMargin)
    {
        if (!surface.IsEffectivelyVisible || surface.Bounds.Width <= 0 || surface.Bounds.Height <= 0) return false;
        var point = surface.PointToClient(screen);
        return point.X >= -12 && point.X <= surface.Bounds.Width + 12
            && point.Y >= -verticalMargin && point.Y <= surface.Bounds.Height + verticalMargin;
    }

    private bool IsWholeGroupDrag => _tabDragCandidate?.IsGroupLeader == true && _tabDragSourceBar == TabBar;

    private void UpdateOwnedTabDrag(PixelPoint screen)
    {
        if (_tabDragCandidate is not { } dragged || _tabDragSourceBar is not { } sourceBar) return;
        TryFindTabSurface(screen, out var receiver, out var bar);
        if (!ReferenceEquals(receiver, _tabDragReceiver) || receiver?._tabPreviewBar != bar)
        {
            _tabDragReceiver?.ResetGroupHover();
            _tabDragReceiver?.ClearTabDragPreview(animateBack: true);
            _tabDragReceiver = receiver;
        }
        if (receiver is not null && bar is not null)
        {
            receiver.EnsureTabDragSlots(bar);
            var point = bar.PointToClient(screen);
            receiver.UpdateTabGroupHover(this, bar, point.X);
            if (receiver._groupArmed)
            {
                foreach (var motion in receiver._tabPreviewMotions.Values) motion.Target = 0;
                receiver.EnsureTabPreviewFrame();
            }
            else receiver.UpdateTabDragPreview(this, bar, point.X);
        }
        bool overSourceBar = ReferenceEquals(receiver, this) && ReferenceEquals(bar, sourceBar);
        if (!overSourceBar) _tabDragLeftSourceBar = true;
        _vm?.ShowDraggedContents(_tabDragLeftSourceBar && overSourceBar);
        UpdateTabSplitHover(screen, receiver is null);
        HideDraggedTab(sourceBar, dragged);
        UpdateTabDragGhostPosition(screen, receiver, bar);
    }

    private void UpdateTabSplitHover(PixelPoint screen, bool outsideTabBars)
    {
        var window = outsideTabBars ? FindRainWindowAt(screen) as MainWindow : null;
        bool onLeft = false;
        if (window is null || _vm is null || _tabDragCandidate is null
            || !window.TryGetTabSplitSide(screen, _vm, _tabDragCandidate, IsWholeGroupDrag, out onLeft))
            window = null;
        if (ReferenceEquals(window, _tabSplitWindow) && (window is null || onLeft == _tabSplitOnLeft)) return;
        ClearTabSplitHover();
        if (window is null) return;
        _tabSplitWindow = window;
        _tabSplitOnLeft = onLeft;
        _tabSplitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _tabSplitTimer.Tick -= TabSplitTimer_Tick;
        _tabSplitTimer.Tick += TabSplitTimer_Tick;
        _tabSplitTimer.Start();
    }

    private void TabSplitTimer_Tick(object? sender, EventArgs e)
    {
        _tabSplitTimer?.Stop();
        if (!_tabDragging || _tabSplitWindow is not { IsVisible: true } window
            || _tabDragCandidate is not { } tab) return;
        _tabSplitArmed = true;
        window.ShowTabSplitPreview(tab, _tabSplitOnLeft);
    }

    private void ClearTabSplitHover()
    {
        _tabSplitTimer?.Stop();
        _tabSplitWindow?.ClearTabSplitPreview();
        _tabSplitWindow = null;
        _tabSplitArmed = false;
    }

    private void EnsureTabDragSlots(ListBox bar)
    {
        if (_tabPreviewBar == bar && _tabDragSlots.Select(s => s.Tab).SequenceEqual(bar.Items.OfType<TabViewModel>())) return;
        ClearTabDragPreview(animateBack: false);
        _tabPreviewBar = bar;
        if (bar.ItemsPanelRoot is not AdaptiveTabPanel panel) return;
        foreach (var (child, x, width) in panel.Slots())
            if (child.DataContext is TabViewModel tab) _tabDragSlots.Add(new(tab, child, x, width));
    }

    private double PreviewDraggedTabWidth(PaneView owner, ListBox bar, TabDragSlot? draggedSlot, bool occupiesRow)
    {
        if (occupiesRow && draggedSlot is not null) return draggedSlot.Width;
        var compatible = _tabDragSlots.Where(s => bar != TabBar || s.Tab.IsPinned == owner._tabDragCandidate!.IsPinned).ToList();
        return compatible.Count > 0 ? compatible.Average(s => s.Width) : owner._tabDragWidth;
    }

    private void UpdateTabDragPreview(PaneView owner, ListBox bar, double pointerX)
    {
        var dragged = owner._tabDragCandidate!;
        var draggedSlot = _tabDragSlots.FirstOrDefault(s => ReferenceEquals(s.Tab, dragged));
        bool occupiesRow = draggedSlot is not null
            && !(bar == TabBar && owner._tabDragSourceBar == owner.GroupTabBar && dragged.IsGrouped);
        var remaining = _tabDragSlots.Where(s => !occupiesRow || s != draggedSlot).ToList();
        double previewWidth = PreviewDraggedTabWidth(owner, bar, draggedSlot, occupiesRow);
        double projectedLeft = pointerX - owner._tabDragGrabRatio * previewWidth;
        int pinned = remaining.Count(s => s.Tab.IsPinned);
        int minimum = bar == TabBar && !dragged.IsPinned ? pinned : 0;
        int maximum = bar == TabBar && dragged.IsPinned ? pinned : remaining.Count;
        int candidate = minimum;
        double closestDistance = double.MaxValue;
        for (int index = minimum; index <= maximum; index++)
        {
            double insertionX = occupiesRow ? _tabDragSlots[Math.Min(index, _tabDragSlots.Count - 1)].X
                : remaining.Count == 0 ? 0
                : index == remaining.Count ? remaining[^1].X + remaining[^1].Width : remaining[index].X;
            double distance = Math.Abs(projectedLeft - insertionX);
            if (distance < closestDistance) { closestDistance = distance; candidate = index; }
        }
        double threshold = Math.Clamp(previewWidth / 208 * 10, 4, 10);
        if (_tabPreviewIndex >= 0 && candidate != _tabPreviewIndex
            && Math.Abs(pointerX - _tabPreviewLastReorderX) <= threshold) candidate = _tabPreviewIndex;
        if (candidate != _tabPreviewIndex) _tabPreviewLastReorderX = pointerX;
        _tabPreviewIndex = candidate;
        _tabPreviewReturning = false;
        if (occupiesRow)
        {
            for (int index = 0; index < _tabDragSlots.Count; index++)
            {
                var slot = index == candidate ? draggedSlot! : remaining[index < candidate ? index : index - 1];
                SetTabPreviewOffset(slot.Container, _tabDragSlots[index].X - slot.X);
            }
        }
        else
        {
            for (int index = 0; index < remaining.Count; index++)
                SetTabPreviewOffset(remaining[index].Container, index >= candidate ? previewWidth : 0);
        }
        _tabPreviewTarget = remaining.Count == 0 ? null : remaining[Math.Min(candidate, remaining.Count - 1)].Tab;
        _tabPreviewAfter = candidate == remaining.Count;
        EnsureTabPreviewFrame();
    }

    private void UpdateTabGroupHover(PaneView owner, ListBox bar, double pointerX)
    {
        var dragged = owner._tabDragCandidate!;
        TabDragSlot? target = null;
        if (!dragged.IsPinned && !owner.IsWholeGroupDrag)
        {
            // Windows reserves 30–48% facing-edge overlap for a deliberate group
            // hover; crossing the insertion midpoint always continues reordering.
            double left = pointerX - owner._tabDragGrabRatio * owner._tabDragWidth;
            double closest = double.MaxValue;
            foreach (var slot in _tabDragSlots)
            {
                if (ReferenceEquals(slot.Tab, dragged) || slot.Tab.IsPinned
                    || dragged.GroupId is { Length: > 0 } && dragged.GroupId == slot.Tab.GroupId) continue;
                double overlap = left + owner._tabDragWidth / 2 < slot.X + slot.Width / 2
                    ? left + owner._tabDragWidth - slot.X : slot.X + slot.Width - left;
                double ratio = overlap / Math.Min(owner._tabDragWidth, slot.Width);
                bool eligible = bar == GroupTabBar
                    ? pointerX >= slot.X && pointerX <= slot.X + slot.Width
                    : ratio is >= 0.30 and <= 0.48;
                double distance = Math.Abs(left + owner._tabDragWidth / 2 - slot.X - slot.Width / 2);
                if (eligible && distance < closest) { closest = distance; target = slot; }
            }
        }
        if (target is null) { ResetGroupHover(); return; }
        BeginGroupHover(target.Tab);
        if (bar == GroupTabBar) GroupHoverTimer_Tick(null, EventArgs.Empty);
    }

    private void BeginGroupHover(TabViewModel target)
    {
        if (ReferenceEquals(_groupHoverTarget, target)) return;
        ResetGroupHover();
        _groupHoverTarget = target;
        _groupHoverTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _groupHoverTimer.Tick -= GroupHoverTimer_Tick;
        _groupHoverTimer.Tick += GroupHoverTimer_Tick;
        _groupHoverTimer.Start();
    }

    private void GroupHoverTimer_Tick(object? sender, EventArgs e)
    {
        _groupHoverTimer?.Stop();
        if (_groupHoverTarget is null || ActiveTabDragOwner is null) return;
        _groupArmed = true;
        _groupHoverTarget.IsGroupDropTarget = true;
        foreach (var motion in _tabPreviewMotions.Values) motion.Target = 0;
        EnsureTabPreviewFrame();
    }

    private void ResetGroupHover()
    {
        _groupHoverTimer?.Stop();
        if (_groupHoverTarget is not null) _groupHoverTarget.IsGroupDropTarget = false;
        _groupHoverTarget = null;
        _groupArmed = false;
    }

    private void TabBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tabDragging || _tabDragCandidate is not { } tab || _vm is not { } source || _tabDragSourceBar is not { } sourceBar)
        {
            _tabDragCandidate = null;
            _tabDragSourceBar = null;
            return;
        }
        e.Handled = true;
        var screen = sourceBar.PointToScreen(e.GetPosition(sourceBar));
        UpdateOwnedTabDrag(screen);
        var splitWindow = _tabSplitArmed ? _tabSplitWindow : null;
        bool splitOnLeft = _tabSplitOnLeft;
        var receiver = _tabDragReceiver;
        var bar = receiver?._tabPreviewBar;
        var target = receiver?._tabPreviewTarget;
        bool after = receiver?._tabPreviewAfter == true;
        var groupTarget = receiver?._groupArmed == true ? receiver._groupHoverTarget : null;
        bool wholeGroup = IsWholeGroupDrag;
        var activateTab = wholeGroup && source.SelectedTab?.GroupId == tab.GroupId ? source.SelectedTab! : tab;
        var window = OwnerWindow;
        if (receiver is null)
        {
            var windowUnderPointer = FindRainWindowAt(screen);
            receiver = LoadedPaneViews.FirstOrDefault(p => !ReferenceEquals(p, this) && p.IsEffectivelyVisible
                && ReferenceEquals(p.OwnerWindow, windowUnderPointer) && ContainsScreenPoint(p, screen, 0));
        }
        _pendingTabLayoutBefore = CaptureTabLayout();
        if (receiver is not null) receiver._pendingTabLayoutBefore = receiver.CaptureTabLayout();
        EndTabDrag(cancelled: false);
        if (splitWindow is not null && splitWindow.DockTab(source, tab, splitOnLeft, wholeGroup))
        {
            splitWindow.Activate();
        }
        else if (receiver?._vm is { } destination)
        {
            if (!ReferenceEquals(destination, source))
            {
                int index = target is null ? -1 : destination.Tabs.IndexOf(target) + (after ? 1 : 0);
                if (wholeGroup) destination.TransferGroupFrom(source, tab, index);
                else destination.TransferTabFrom(source, tab, index, preserveGroup: false);
            }
            if (groupTarget is not null) destination.GroupTabs(tab, groupTarget);
            else if (target is not null)
            {
                if (bar == receiver.GroupTabBar) destination.MoveTabIntoGroup(tab, target, after);
                else if (wholeGroup) destination.MoveGroupBesideTopLevel(tab, target, after);
                else if (tab.IsGrouped) destination.MoveTabOutOfGroup(tab, target, after);
                else destination.MoveTabBesideTopLevel(tab, target, after);
            }
            destination.SelectedTab = activateTab;
            destination.ActivateCommand.Execute(null);
            receiver.OwnerWindow?.Activate();
            receiver.ScheduleTabLayoutAnimation();
        }
        else if (window is MainWindow main) main.TearOffTab(source, tab, screen, wholeGroup);
        ScheduleTabLayoutAnimation();
    }

    private void EndTabDrag(bool cancelled)
    {
        ClearTabSplitHover();
        _vm?.EndTabDrag();
        _tabDragLeftSourceBar = false;
        HideTabDragGhost();
        _tabDragReceiver?.ResetGroupHover();
        _tabDragReceiver?.ClearTabDragPreview(animateBack: cancelled);
        _tabDragReceiver = null;
        ClearTabDragPreview(animateBack: cancelled);
        var pointer = _tabDragPointer;
        var bar = _tabDragSourceBar;
        _tabDragPointer = null;
        if (_tabDragCandidate is { } tab) tab.IsDragging = false;
        ResetGroupHover();
        _tabDragging = false;
        _tabDragCandidate = null;
        _tabDragSourceBar = null;
        if (ReferenceEquals(ActiveTabDragOwner, this)) ActiveTabDragOwner = null;
        foreach (var (control, enabled) in _suppressedTooltips) control.SetValue(ToolTip.ServiceEnabledProperty, enabled);
        _suppressedTooltips.Clear();
        if (pointer is not null && ReferenceEquals(pointer.Captured, bar)) pointer.Capture(null);
    }

    private void HideDraggedTab(ListBox bar, TabViewModel dragged)
    {
        if (bar.ContainerFromItem(dragged) is not Control container) return;
        _tabPreviewHiddenContainer = container;
        Motion.Cancel(container);
        container.Opacity = 0;
    }

    private void SetTabPreviewOffset(Control container, double target)
    {
        if (_tabPreviewMotions.TryGetValue(container, out var motion)) { motion.Target = target; return; }
        double current = container.RenderTransform?.Value.M31 ?? 0;
        Motion.Cancel(container);
        var transform = new TranslateTransform(current, 0);
        container.RenderTransform = transform;
        _tabPreviewMotions.Add(container, new TabPreviewMotion(transform, target));
    }

    private void EnsureTabPreviewFrame()
    {
        if (_tabPreviewFrameScheduled || _tabPreviewMotions.Count == 0 || TopLevel.GetTopLevel(this) is not { } top) return;
        _tabPreviewFrameScheduled = true;
        top.RequestAnimationFrame(AdvanceTabPreviewMotion);
    }

    private void AdvanceTabPreviewMotion(TimeSpan frame)
    {
        _tabPreviewFrameScheduled = false;
        double elapsed = _tabPreviewLastFrame is { } previous && frame > previous
            ? (frame - previous).TotalSeconds : 1.0 / RenderPerformance.FramesPerSecond;
        _tabPreviewLastFrame = frame;
        double blend = TabPreviewBlend(elapsed);
        bool settled = true;
        foreach (var (container, motion) in _tabPreviewMotions)
        {
            if (!ReferenceEquals(container.RenderTransform, motion.Transform)) continue;
            double remaining = motion.Target - motion.Transform.X;
            if (Math.Abs(remaining) <= 0.12) motion.Transform.X = motion.Target;
            else { motion.Transform.X += remaining * blend; settled = false; }
        }
        if (_tabPreviewReturning && settled) ClearTabPreviewMotions();
        else if (!settled) EnsureTabPreviewFrame();
    }

    // The response must take the same time at 60, 280 or 360 Hz.
    internal static double TabPreviewBlend(double elapsed) =>
        1 - Math.Exp(-Math.Clamp(elapsed, 0, 0.05) / 0.032);

    private void ClearTabPreviewMotions()
    {
        foreach (var (container, motion) in _tabPreviewMotions)
            if (ReferenceEquals(container.RenderTransform, motion.Transform)) container.RenderTransform = null;
        _tabPreviewMotions.Clear();
        _tabPreviewReturning = false;
        _tabPreviewLastFrame = null;
    }

    private void ClearTabDragPreview(bool animateBack)
    {
        if (_tabPreviewHiddenContainer is { } hidden) hidden.Opacity = 1;
        _tabPreviewHiddenContainer = null;
        _tabPreviewTarget = null;
        _tabPreviewAfter = false;
        _tabPreviewIndex = -1;
        _tabPreviewLastReorderX = double.NaN;
        _tabPreviewBar = null;
        _tabDragSlots.Clear();
        if (animateBack && _tabPreviewMotions.Count > 0)
        {
            foreach (var motion in _tabPreviewMotions.Values) motion.Target = 0;
            _tabPreviewReturning = true;
            EnsureTabPreviewFrame();
        }
        else ClearTabPreviewMotions();
    }

    private void ShowTabDragGhost(TabViewModel tab, ListBox bar)
    {
        HideTabDragGhost();
        var icon = new Icon { Kind = "folder", Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0) };
        icon.Bind(Icon.ForegroundProperty, icon.GetResourceObservable("AccentBright"));
        var label = new TextBlock { Text = tab.TopLevelTitle, FontSize = 12, IsVisible = !tab.IsPinned,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("Text"));
        var row = new DockPanel { Children = { icon, label } };
        DockPanel.SetDock(icon, Dock.Left);
        var card = new Border { Width = _tabDragWidth, Height = Math.Max(28, bar.Bounds.Height),
            CornerRadius = new CornerRadius(8, 8, 3, 3), BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 0), Child = row };
        // A popup needs an opaque themed base as well as the glass overlay: native
        // popup hosts otherwise expose their platform's default white background.
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("Glass4"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("AccentLine"));
        var surface = new Border { CornerRadius = card.CornerRadius, Child = card, ClipToBounds = true };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("BgRaised"));
        _tabDragGhost = new Popup
        {
            Child = surface, PlacementTarget = bar, Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft, PlacementGravity = PopupGravity.BottomRight,
            PlacementRect = new Rect(0, 0, 1, 1),
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None,
            IsHitTestVisible = false, IsLightDismissEnabled = false, WindowManagerAddShadowHint = false,
        };
        // Parenting is required for Rain styles, resources and the theme variant
        // to reach the native PopupRoot (PlacementTarget alone does not parent it).
        TabAnimationLayer.Children.Add(_tabDragGhost);
        _tabDragGhost.IsOpen = true;
    }

    private void UpdateTabDragGhostPosition(PixelPoint screen, PaneView? receiver, ListBox? bar)
    {
        if (_tabDragGhost is null || _tabDragSourceBar is not { } sourceBar) return;
        var pointer = sourceBar.PointToClient(screen);
        double y = pointer.Y - _tabDragGrabOffset.Y;
        if (receiver is not null && bar is not null)
            y = sourceBar.PointToClient(bar.PointToScreen(default)).Y;
        // Avalonia intersects PlacementRect with the placement target's bounds.
        // Keep its anchor inside the bar and use unrestricted offsets, otherwise
        // the ghost snaps back to the bar when dragged past its end or outside it.
        _tabDragGhost.HorizontalOffset = pointer.X - _tabDragGrabOffset.X;
        _tabDragGhost.VerticalOffset = y;
    }

    private void HideTabDragGhost()
    {
        if (_tabDragGhost is null) return;
        _tabDragGhost.IsOpen = false;
        TabAnimationLayer.Children.Remove(_tabDragGhost);
        _tabDragGhost.Child = null;
        _tabDragGhost = null;
    }
}
