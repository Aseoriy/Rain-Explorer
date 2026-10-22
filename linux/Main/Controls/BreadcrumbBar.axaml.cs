using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

namespace RainExplorer.Controls;

/// <summary>A folder offered in the breadcrumb dropdowns.</summary>
public sealed record Suggestion(string Display, string Path);

/// <summary>
/// Clickable path breadcrumb that flips to an editable, autocompleting path box (Ctrl+L).
/// Port of the Windows build's BreadcrumbBar: drive-root first segment, a chevron after each
/// segment listing its subfolders, and a "…" overflow menu past five segments.
/// </summary>
public partial class BreadcrumbBar : UserControl
{
    /// <summary>Above this many segments the middle ones collapse into a "…" menu.</summary>
    private const int MaxCrumbs = 5;

    private TabViewModel? _tab;
    private CancellationTokenSource? _childCts;
    private CancellationTokenSource? _acCts;
    private Button? _dropTargetButton;
    private bool _suppressAutocomplete;
    private string? _editStartText;
    private Window? _window;

    /// <summary>Raised for "Open in new tab" from a segment's menu.</summary>
    public event Action<string>? OpenInNewTabRequested;

    /// <summary>Raised for "Properties" from a segment's menu.</summary>
    public event Action<string>? PropertiesRequested;

    /// <summary>Raised when files are dropped on a segment: (paths, destination, move).</summary>
    public event Action<string[], string, bool>? FilesDropped;

    public BreadcrumbBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => OnDataContextChanged();
        CrumbHost.AddHandler(PointerPressedEvent, CrumbHost_PointerPressed, RoutingStrategies.Tunnel);
        CrumbHost.AddHandler(DragDrop.DragOverEvent, CrumbHost_DragOver);
        CrumbHost.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(null));
        CrumbHost.AddHandler(DragDrop.DropEvent, CrumbHost_Drop);
        EditBox.AddHandler(KeyDownEvent, EditBox_KeyDown, RoutingStrategies.Tunnel);
        EditBox.TextChanged += (_, _) => EditBox_TextChanged();
        EditBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!AcPopup.IsPointerOver && !EditBox.IsFocused) ExitEdit();
        }, DispatcherPriority.Input);
        ChildList.AddHandler(PointerReleasedEvent, ChildList_PointerReleased, RoutingStrategies.Tunnel);
        AcList.AddHandler(PointerReleasedEvent, AcList_PointerReleased, RoutingStrategies.Tunnel);
        CrumbScroll.SizeChanged += (_, _) => ScrollToEnd();
    }

    public bool IsEditing => EditHost.IsVisible;

    // ---- Leave edit mode when the user clicks elsewhere or switches windows --
    // The suggestion popup is not light-dismiss (so it can't close on a stray click by itself),
    // and clicking blank space does not move focus out of the path box, so close it explicitly.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null) return;
        _window.AddHandler(PointerPressedEvent, Window_PointerPressed, RoutingStrategies.Tunnel);
        _window.Deactivated += Window_Deactivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null)
        {
            _window.RemoveHandler(PointerPressedEvent, Window_PointerPressed);
            _window.Deactivated -= Window_Deactivated;
            _window = null;
        }
        ExitEdit();
    }

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEditing || EditHost.IsPointerOver || AcPopup.IsPointerOver) return;
        ExitEdit();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => ExitEdit();

    // ---- Track the bound tab ------------------------------------------------
    private void OnDataContextChanged()
    {
        if (_tab is not null) _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab = DataContext as TabViewModel;
        if (_tab is not null) _tab.PropertyChanged += OnTabPropertyChanged;
        ExitEdit();
        Rebuild();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.CurrentPath) or nameof(TabViewModel.Page))
        {
            if (!IsEditing) Rebuild();
        }
    }

    // ---- Breadcrumb mode ----------------------------------------------------
    private sealed record Crumb(string Label, string Path);

    private void Rebuild()
    {
        CrumbStrip.Children.Clear();
        var tab = _tab;
        if (tab is null) return;

        // Special dashboard pages aren't a filesystem path — show one labelled pill.
        if (!tab.IsFolderView)
        {
            AddSpecialCrumb(tab.IsHomeView);
            return;
        }

        string path = tab.CurrentPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        var crumbs = BuildCrumbs(path);
        if (crumbs.Count == 0) return;

        if (crumbs.Count > MaxCrumbs)
        {
            // root  ›  …  ›  (last few)
            AddCrumb(crumbs[0], current: false);
            AddChevron(crumbs[0].Path);
            int tail = MaxCrumbs - 1;
            int start = crumbs.Count - tail;
            AddOverflow(crumbs.GetRange(1, start - 1));
            for (int i = start; i < crumbs.Count; i++)
            {
                AddCrumb(crumbs[i], current: i == crumbs.Count - 1);
                AddChevron(crumbs[i].Path);
            }
        }
        else
        {
            for (int i = 0; i < crumbs.Count; i++)
            {
                AddCrumb(crumbs[i], current: i == crumbs.Count - 1);
                AddChevron(crumbs[i].Path);   // trailing chevron lists that folder's children
            }
        }

        ScrollToEnd();
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => CrumbScroll.Offset = new Vector(double.MaxValue, 0), DispatcherPriority.Background);

    /// <summary>The containing drive (mount) first, like "Stuff (E:)" on Windows, then each folder.</summary>
    private static List<Crumb> BuildCrumbs(string path)
    {
        var crumbs = new List<Crumb>();
        string mount = MountService.GetMountPoint(path);
        var drive = MountService.GetDrives().FirstOrDefault(d => PathUtil.Same(d.MountPoint, mount));
        string rootLabel = drive?.Label ?? (mount == "/" ? "File System" : PathUtil.DisplayName(mount));
        crumbs.Add(new Crumb(rootLabel, mount));

        string rest = path.Length > mount.Length ? path[mount.Length..].TrimStart('/') : string.Empty;
        string current = mount;
        foreach (string segment in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current == "/" ? "/" + segment : current + "/" + segment;
            crumbs.Add(new Crumb(segment, current));
        }
        return crumbs;
    }

    private void AddCrumb(Crumb c, bool current)
    {
        var btn = new Button
        {
            Content = c.Label,
            Theme = (Avalonia.Styling.ControlTheme?)this.FindResourceOrDefault("CrumbButton"),
            Tag = c.Path,
        };
        ToolTip.SetTip(btn, c.Path);
        if (current) btn.Classes.Add("current");
        btn.Click += Crumb_Click;
        btn.ContextRequested += Crumb_ContextRequested;
        CrumbStrip.Children.Add(btn);
    }

    private void AddChevron(string folderPath)
    {
        var chev = new Button
        {
            Theme = (Avalonia.Styling.ControlTheme?)this.FindResourceOrDefault("CrumbChevron"),
            Tag = folderPath,
        };
        chev.Click += Chevron_Click;
        CrumbStrip.Children.Add(chev);
    }

    private void AddOverflow(List<Crumb> hidden)
    {
        var btn = new Button
        {
            Content = "…",
            Theme = (Avalonia.Styling.ControlTheme?)this.FindResourceOrDefault("CrumbButton"),
        };
        btn.Click += (_, _) => ShowDropdown(hidden.Select(h => new Suggestion(h.Label, h.Path)).ToList(), btn);
        CrumbStrip.Children.Add(btn);
    }

    private void AddSpecialCrumb(bool home)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Icon
        {
            Kind = home ? "home" : "hard-drive", Width = 14, Height = 14, StrokeThickness = 1.7,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0),
        };
        icon.Bind(Icon.ForegroundProperty, icon.GetResourceObservable("AccentBright"));
        row.Children.Add(icon);
        var label = new TextBlock
        {
            Text = home ? "Home" : "All drives",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("Text"));
        row.Children.Add(label);
        CrumbStrip.Children.Add(new Button
        {
            Content = row,
            Theme = (Avalonia.Styling.ControlTheme?)this.FindResourceOrDefault("CrumbButton"),
            IsHitTestVisible = false,
        });
    }

    private void Crumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string p }) _ = _tab?.NavigateAsync(p, true);
        e.Handled = true;
    }

    private void Chevron_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string dir } b) _ = OpenChildPopup(dir, b);
        e.Handled = true;
    }

    private void Crumb_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { Tag: string path } button) return;
        var top = TopLevel.GetTopLevel(this);
        bool pinned = MainViewModel.IsPinned(path);
        var items = new List<Control>
        {
            MenuFactory.Item("Open", "arrow-right", () => _ = _tab?.NavigateAsync(path, true), iconBrush: "AccentBright"),
            MenuFactory.Item("Open in new tab", "plus", () => OpenInNewTabRequested?.Invoke(path)),
            MenuFactory.Item("Open in Terminal", "terminal", () =>
            {
                if (!TerminalLauncher.TryOpen(path, out string? error) && _tab is not null) _tab.Status = $"⚠️ {error}";
            }),
            new Separator(),
            MenuFactory.Item("Copy path", "copy", () => _ = ClipboardService.SetTextAsync(top, path)),
            MenuFactory.Item(pinned ? "Unpin from Quick Access" : "Pin to Quick Access", "star", () =>
            {
                if (pinned) MainViewModel.Unpin(path);
                else MainViewModel.Pin(path);
            }, iconBrush: "AccentBright"),
            new Separator(),
            MenuFactory.Item("Properties", "sliders", () => PropertiesRequested?.Invoke(path)),
        };
        new ContextMenu { ItemsSource = items }.Open(button);
        e.Handled = true;
    }

    // ---- Subfolder dropdowns -----------------------------------------------
    private async Task OpenChildPopup(string dir, Control anchor)
    {
        _childCts?.Cancel();
        var cts = _childCts = new CancellationTokenSource();
        var children = await Task.Run(() => ListSubfolders(dir, null, cts.Token), cts.Token).ConfigureAwait(true);
        if (cts.IsCancellationRequested) return;
        if (children.Count == 0)
        {
            children.Add(new Suggestion("(no subfolders)", string.Empty));
        }
        ShowDropdown(children, anchor);
    }

    private void ShowDropdown(List<Suggestion> items, Control anchor)
    {
        ChildList.ItemsSource = items;
        ChildPopup.PlacementTarget = anchor;
        ChildPopup.IsOpen = true;
    }

    private void ChildList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not Suggestion s) return;
        ChildPopup.IsOpen = false;
        if (s.Path.Length > 0) _ = _tab?.NavigateAsync(s.Path, true);
    }

    private static List<Suggestion> ListSubfolders(string dir, string? prefix, CancellationToken ct)
    {
        var result = new List<Suggestion>();
        bool showHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                if (!showHidden && info.Name.StartsWith('.') && !(prefix?.StartsWith('.') ?? false)) continue;
                if (prefix is not null && !info.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) continue;
                result.Add(new Suggestion(info.Name, info.FullName));
                if (result.Count >= 400) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* unreadable folder — empty list */ }
        result.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Display, b.Display));
        return result;
    }

    // ---- Edit mode ----------------------------------------------------------
    private void CrumbHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        BeginEdit();
        e.Handled = true;
    }

    /// <summary>Switch to the raw, editable path (Ctrl+L / F6 / Alt+D).</summary>
    public void BeginEdit()
    {
        if (_tab is null) return;
        _suppressAutocomplete = true;
        EditBox.Text = _tab.IsFolderView ? _tab.CurrentPath : string.Empty;
        _suppressAutocomplete = false;
        _editStartText = EditBox.Text;
        CrumbHost.IsVisible = false;
        EditHost.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            EditBox.Focus();
            EditBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public void ExitEdit()
    {
        _acCts?.Cancel();
        AcPopup.IsOpen = false;
        EditHost.IsVisible = false;
        CrumbHost.IsVisible = true;
    }

    private void EditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                string text = AcPopup.IsOpen && AcList.SelectedItem is Suggestion picked
                    ? picked.Path
                    : EditBox.Text?.Trim() ?? string.Empty;
                e.Handled = true;
                Commit(text);
                break;
            case Key.Escape:
                e.Handled = true;
                ExitEdit();
                break;
            case Key.Down when AcPopup.IsOpen && AcList.ItemCount > 0:
                AcList.SelectedIndex = Math.Min(AcList.ItemCount - 1, AcList.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up when AcPopup.IsOpen && AcList.ItemCount > 0:
                AcList.SelectedIndex = Math.Max(0, AcList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Tab when AcPopup.IsOpen && (AcList.SelectedItem ?? AcList.Items.FirstOrDefault()) is Suggestion first:
                _suppressAutocomplete = true;
                EditBox.Text = first.Path + "/";
                EditBox.CaretIndex = EditBox.Text.Length;
                _suppressAutocomplete = false;
                EditBox_TextChanged();
                e.Handled = true;
                break;
        }
    }

    private void Commit(string text)
    {
        ExitEdit();
        if (_tab is null || text.Length == 0) return;

        // Browser-style shortcuts: typing a terminal name opens one in this folder.
        if (TerminalCommands.Contains(text.ToLowerInvariant()))
        {
            string dir = _tab.IsFolderView ? _tab.CurrentPath : XdgDirs.Home;
            if (!TerminalLauncher.TryOpen(dir, out string? error)) _tab.Status = $"⚠️ {error}";
            return;
        }

        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            text = uri.LocalPath;
        string path = PathUtil.ExpandTilde(text);
        if (!path.StartsWith('/') && _tab.IsFolderView) path = Path.Combine(_tab.CurrentPath, path);

        if (File.Exists(path))
        {
            try { ProcessLauncher.OpenDefault(path); }
            catch (Exception ex) { _tab.Status = $"⚠️ {ex.Message}"; }
            return;
        }
        _ = _tab.NavigateAsync(path, true);
    }

    private static readonly HashSet<string> TerminalCommands = new(StringComparer.Ordinal)
    {
        "terminal", "term", "cmd", "konsole", "bash", "sh", "zsh", "fish", "gnome-terminal", "kgx",
    };

    private async void EditBox_TextChanged()
    {
        if (_suppressAutocomplete || !EditHost.IsVisible) return;
        _acCts?.Cancel();
        // The raw path that BeginEdit filled in is not a user edit; the TextChanged for it can
        // arrive after the suppress flag is cleared, which used to pop up the current folder.
        if (EditBox.Text == _editStartText)
        {
            AcPopup.IsOpen = false;
            return;
        }
        var cts = _acCts = new CancellationTokenSource();
        string text = PathUtil.ExpandTilde(EditBox.Text ?? string.Empty);
        int slash = text.LastIndexOf('/');
        if (slash < 0)
        {
            AcPopup.IsOpen = false;
            return;
        }
        string dir = slash == 0 ? "/" : text[..slash];
        string prefix = text[(slash + 1)..];
        List<Suggestion> matches;
        try
        {
            await Task.Delay(90, cts.Token).ConfigureAwait(true);
            matches = await Task.Run(() => ListSubfolders(dir, prefix, cts.Token), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested || !EditHost.IsVisible) return;
        AcList.ItemsSource = matches.Take(60).ToList();
        AcList.SelectedIndex = -1;
        AcPopup.IsOpen = matches.Count > 0;
    }

    private void AcList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not Suggestion s) return;
        e.Handled = true;
        Commit(s.Path);
    }

    // ---- Drop files onto a segment -------------------------------------------
    private Button? CrumbAt(DragEventArgs e) =>
        (CrumbHost.InputHitTest(e.GetPosition(CrumbHost)) as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is
            { Tag: string } b && CrumbStrip.Children.Contains(b) && b.Theme == this.FindResource("CrumbButton")
            ? b
            : null;

    private void CrumbHost_DragOver(object? sender, DragEventArgs e)
    {
        var button = CrumbAt(e);
        string? dest = button?.Tag as string;
        e.DragEffects = FileDropService.EffectFor(PaneView.DraggedPaths(e), dest, e.KeyModifiers);
        SetDropTarget(e.DragEffects == DragDropEffects.None ? null : button);
        e.Handled = true;
    }

    private void CrumbHost_Drop(object? sender, DragEventArgs e)
    {
        var button = CrumbAt(e);
        SetDropTarget(null);
        string? dest = button?.Tag as string;
        var files = PaneView.DraggedPaths(e);
        var effect = FileDropService.EffectFor(files, dest, e.KeyModifiers);
        if (files is null || dest is null || effect == DragDropEffects.None) return;
        e.Handled = true;
        FilesDropped?.Invoke(files, dest, effect == DragDropEffects.Move);
    }

    private void SetDropTarget(Button? button)
    {
        if (ReferenceEquals(_dropTargetButton, button)) return;
        _dropTargetButton?.Classes.Set("drop", false);
        _dropTargetButton = button;
        _dropTargetButton?.Classes.Set("drop", true);
    }
}
