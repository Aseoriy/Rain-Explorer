using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

/// <summary>
/// Hybrid address bar: a segmented, clickable breadcrumb that flips to a raw
/// editable path box (with folder autocomplete) on click or Ctrl+L. DataContext
/// is the active <see cref="TabViewModel"/>; it follows that tab's CurrentPath/Page.
/// </summary>
public partial class BreadcrumbBar : UserControl
{
    private TabViewModel? _tab;
    private bool _editing;
    private CancellationTokenSource? _childCts;
    private CancellationTokenSource? _acCts;

    /// <summary>Above this many segments the middle ones collapse into a "…" menu.</summary>
    private const int MaxCrumbs = 5;

    public BreadcrumbBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Rebuild();
    }

    private TabViewModel? Tab => DataContext as TabViewModel;

    // ---- Track the bound tab ------------------------------------------------
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tab is not null) _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab = DataContext as TabViewModel;
        if (_tab is not null) _tab.PropertyChanged += OnTabPropertyChanged;
        ExitEdit();
        Rebuild();
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.CurrentPath) or nameof(TabViewModel.Page))
        {
            ExitEdit();   // a navigation (ours or external) supersedes any in-progress edit
            Rebuild();
        }
    }

    // ===================== Breadcrumb rendering =====================

    private sealed record Crumb(string Label, string Path);

    private void Rebuild()
    {
        if (CrumbStrip is null) return;
        CrumbStrip.Children.Clear();

        var tab = Tab;
        if (tab is null) return;

        // Special dashboard pages aren't a filesystem path — show one labelled pill.
        if (tab.Page != PageKind.Folder)
        {
            AddSpecialCrumb(tab.Page);
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

        // Keep the current (right-most) segment in view on deep paths.
        Dispatcher.BeginInvoke(() => CrumbScroll.ScrollToRightEnd(), DispatcherPriority.Background);
    }

    private void AddCrumb(Crumb c, bool current)
    {
        var btn = new Button
        {
            Content = c.Label,
            Style = (Style)FindResource("CrumbButtonStyle"),
            Tag = c.Path,
            ToolTip = c.Path,
        };
        if (current)
        {
            btn.Foreground = (Brush)FindResource("Text");
            btn.FontWeight = FontWeights.SemiBold;
        }
        btn.Click += Crumb_Click;
        CrumbStrip.Children.Add(btn);
    }

    private void AddChevron(string folderPath)
    {
        var chev = new Button { Style = (Style)FindResource("CrumbChevronStyle"), Tag = folderPath };
        chev.Click += Chevron_Click;
        CrumbStrip.Children.Add(chev);
    }

    private void AddOverflow(List<Crumb> hidden)
    {
        var btn = new Button { Content = "…", Style = (Style)FindResource("CrumbButtonStyle") };
        btn.Click += (_, _) => ShowDropdown(hidden.Select(h => new Suggestion(h.Label, h.Path)), btn);
        CrumbStrip.Children.Add(btn);
    }

    private void AddSpecialCrumb(PageKind page)
    {
        string label = page == PageKind.Home ? "Home" : "All drives";
        string icon = page == PageKind.Home ? "home" : "hard-drive";

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (TryFindResource($"Ic.{icon}") is Geometry g)
            row.Children.Add(new System.Windows.Shapes.Path
            {
                Data = g,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                Stroke = (Brush)FindResource("AccentBright"),
                StrokeThickness = 1.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0),
            });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("Text"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        CrumbStrip.Children.Add(new Button
        {
            Content = row,
            Style = (Style)FindResource("CrumbButtonStyle"),
            IsHitTestVisible = false,
        });
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string p }) _ = Tab?.NavigateAsync(p, true);
    }

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string dir } b) OpenChildPopup(dir, b);
    }

    /// <summary>Build the segment list from root → current using the parent chain.</summary>
    private static List<Crumb> BuildCrumbs(string path)
    {
        var list = new List<Crumb>();
        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(path); }
        catch { return list; }

        while (dir is not null)
        {
            string label = dir.Parent is null ? RootLabel(dir) : dir.Name;
            list.Add(new Crumb(label, dir.FullName));
            dir = dir.Parent;
        }
        list.Reverse();
        return list;
    }

    private static string RootLabel(DirectoryInfo root)
    {
        try
        {
            var di = new DriveInfo(root.FullName);
            string letter = di.Name.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (di.IsReady && !string.IsNullOrWhiteSpace(di.VolumeLabel))
                return $"{di.VolumeLabel} ({letter})";
            return letter;
        }
        catch
        {
            return root.Name.TrimEnd(System.IO.Path.DirectorySeparatorChar);   // UNC share, etc.
        }
    }

    // ===================== Subfolder dropdowns =====================

    private async void OpenChildPopup(string dir, UIElement anchor)
    {
        _childCts?.Cancel();
        var cts = new CancellationTokenSource();
        _childCts = cts;

        ChildList.ItemsSource = new[] { Suggestion.Placeholder("Loading…") };
        ChildPopup.PlacementTarget = anchor;
        ChildPopup.IsOpen = true;

        List<string> dirs;
        try { dirs = await GetSubdirsAsync(dir, "", 500, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;

        ChildList.ItemsSource = dirs.Count == 0
            ? new[] { Suggestion.Placeholder("(no subfolders)") }
            : dirs.Select(d => new Suggestion(System.IO.Path.GetFileName(d), d)).ToList();
    }

    private void ShowDropdown(IEnumerable<Suggestion> items, UIElement anchor)
    {
        _childCts?.Cancel();
        ChildList.ItemsSource = items.ToList();
        ChildPopup.PlacementTarget = anchor;
        ChildPopup.IsOpen = true;
    }

    private void ChildList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { IsPlaceholder: false } s)
        {
            ChildPopup.IsOpen = false;
            _ = Tab?.NavigateAsync(s.FullPath, true);
            e.Handled = true;
        }
    }

    // ===================== Edit mode =====================

    /// <summary>Switch to the raw editable path box (Ctrl+L / click empty strip).</summary>
    public void BeginEdit()
    {
        var tab = Tab;
        if (tab is null) return;
        _editing = true;
        ChildPopup.IsOpen = false;
        EditBox.Text = tab.Page == PageKind.Folder ? tab.CurrentPath : string.Empty;
        CrumbHost.Visibility = Visibility.Collapsed;
        EditHost.Visibility = Visibility.Visible;
        EditBox.Focus();
        EditBox.SelectAll();
    }

    private void ExitEdit()
    {
        if (!_editing) return;
        _editing = false;
        _acCts?.Cancel();
        AcPopup.IsOpen = false;
        EditHost.Visibility = Visibility.Collapsed;
        CrumbHost.Visibility = Visibility.Visible;
    }

    private void CrumbHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks on a crumb or chevron navigate; only empty strip enters edit mode.
        if (IsWithin<ButtonBase>(e.OriginalSource as DependencyObject)) return;
        BeginEdit();
        e.Handled = true;
    }

    private void EditBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAutocomplete();

    private async void UpdateAutocomplete()
    {
        if (!_editing) return;
        _acCts?.Cancel();

        string text = EditBox.Text;
        string dir, partial;
        if (text.EndsWith(System.IO.Path.DirectorySeparatorChar) ||
            text.EndsWith(System.IO.Path.AltDirectorySeparatorChar))
        {
            dir = text;
            partial = string.Empty;
        }
        else
        {
            try { dir = System.IO.Path.GetDirectoryName(text) ?? string.Empty; } catch { dir = string.Empty; }
            partial = SafeFileName(text);
        }

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { AcPopup.IsOpen = false; return; }

        var cts = new CancellationTokenSource();
        _acCts = cts;
        List<string> dirs;
        try { dirs = await GetSubdirsAsync(dir, partial, 14, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested || !_editing) return;

        if (dirs.Count == 0) { AcPopup.IsOpen = false; return; }
        AcList.ItemsSource = dirs.Select(d => new Suggestion(System.IO.Path.GetFileName(d), d)).ToList();
        AcList.SelectedIndex = -1;
        AcPopup.Width = EditHost.ActualWidth;
        AcPopup.IsOpen = true;
    }

    private void EditBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (!AcPopup.IsOpen)
                {
                    if (AcList.Items.Count > 0) { AcPopup.IsOpen = true; AcList.SelectedIndex = 0; }
                    else UpdateAutocomplete();
                }
                else if (AcList.Items.Count > 0)
                {
                    AcList.SelectedIndex = Math.Min(AcList.SelectedIndex + 1, AcList.Items.Count - 1);
                    AcList.ScrollIntoView(AcList.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (AcPopup.IsOpen && AcList.Items.Count > 0)
                {
                    AcList.SelectedIndex = Math.Max(AcList.SelectedIndex - 1, 0);
                    AcList.ScrollIntoView(AcList.SelectedItem);
                    e.Handled = true;
                }
                break;

            case Key.Tab:
                if (AcPopup.IsOpen && AcList.Items.Count > 0)
                {
                    Complete((AcList.SelectedItem ?? AcList.Items[0]) as Suggestion);
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                if (AcPopup.IsOpen && AcList.SelectedItem is Suggestion { IsPlaceholder: false } sug)
                {
                    AcPopup.IsOpen = false;
                    _ = Tab?.NavigateAsync(sug.FullPath, true);
                }
                else Commit();
                e.Handled = true;
                break;

            case Key.Escape:
                if (AcPopup.IsOpen) AcPopup.IsOpen = false;
                else { ExitEdit(); Rebuild(); }
                e.Handled = true;
                break;
        }
    }

    private void AcList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { IsPlaceholder: false } s)
        {
            AcPopup.IsOpen = false;
            _ = Tab?.NavigateAsync(s.FullPath, true);
            e.Handled = true;
        }
    }

    private void EditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Defer so a click on a suggestion (which navigates + exits) runs first.
        Dispatcher.BeginInvoke(() =>
        {
            if (_editing) { ExitEdit(); Rebuild(); }
        }, DispatcherPriority.Background);
    }

    /// <summary>Fill the box with a suggested folder and keep editing (so you can go deeper).</summary>
    private void Complete(Suggestion? s)
    {
        if (s is null || s.IsPlaceholder) return;
        string p = s.FullPath;
        if (!p.EndsWith(System.IO.Path.DirectorySeparatorChar)) p += System.IO.Path.DirectorySeparatorChar;
        EditBox.Text = p;
        EditBox.CaretIndex = p.Length;   // TextChanged refreshes suggestions for the new folder
    }

    private void Commit()
    {
        var tab = Tab;
        if (tab is null) return;
        string p = EditBox.Text.Trim();
        AcPopup.IsOpen = false;

        if (p.Length == 0) { ExitEdit(); Rebuild(); return; }

        // Browser-style command shortcuts: typing "cmd", "powershell", "wt", etc. in the
        // address bar launches that tool with the current folder as its working directory.
        if (tab.Page == PageKind.Folder && Directory.Exists(tab.CurrentPath)
            && TryLaunchInFolder(p, tab.CurrentPath))
        {
            ExitEdit();
            return;
        }

        // A file path opens the file; a folder navigates; anything else shows a status warning.
        if (File.Exists(p))
        {
            try { Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true }); }
            catch (Exception ex) { tab.Status = $"⚠️ {ex.Message}"; }
            ExitEdit();
            return;
        }
        _ = CommitNavigate(tab, p);
    }

    // Recognised address-bar commands → the executable to run in the current folder.
    // "explorer" reveals the folder in Windows Explorer; the rest open a shell there.
    private static readonly Dictionary<string, string> ShellCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cmd"] = "cmd.exe",
            ["powershell"] = "powershell.exe",
            ["pwsh"] = "pwsh.exe",
            ["wt"] = "wt.exe",
            ["bash"] = "bash.exe",
            ["explorer"] = "explorer.exe",
        };

    /// <summary>If <paramref name="text"/> is a known shell command, launch it with
    /// <paramref name="folder"/> as the working directory. Returns true if handled.</summary>
    private static bool TryLaunchInFolder(string text, string folder)
    {
        if (!ShellCommands.TryGetValue(text, out var exe)) return false;
        try
        {
            // explorer.exe ignores WorkingDirectory; pass the folder as its argument.
            var psi = string.Equals(exe, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo(exe, $"\"{folder}\"") { UseShellExecute = true }
                : new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = folder };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // wt/pwsh/bash may not be installed — fall through so the text is treated
            // as a path instead (and the user gets the normal "not a folder" warning).
            return false;
        }
    }

    private async Task CommitNavigate(TabViewModel tab, string p)
    {
        string before = tab.CurrentPath;
        await tab.NavigateAsync(p, true);
        // On success CurrentPath changed → OnTabPropertyChanged already exited edit mode.
        // On failure it's unchanged (NavigateAsync set a warning) → stay editing, reselect.
        if (_editing && string.Equals(tab.CurrentPath, before, StringComparison.Ordinal))
        {
            EditBox.SelectAll();
            EditBox.Focus();
        }
    }

    // ===================== Shared helpers =====================

    private sealed class Suggestion
    {
        public string Display { get; }
        public string FullPath { get; }
        public bool IsPlaceholder { get; }

        public Suggestion(string display, string fullPath, bool placeholder = false)
        {
            Display = display;
            FullPath = fullPath;
            IsPlaceholder = placeholder;
        }

        public static Suggestion Placeholder(string text) => new(text, string.Empty, placeholder: true);
    }

    /// <summary>List immediate subdirectories of <paramref name="dir"/> (optionally prefix-filtered),
    /// honoring the hidden-files setting, sorted, capped — all off the UI thread.</summary>
    private static async Task<List<string>> GetSubdirsAsync(
        string dir, string startsWith, int cap, CancellationToken ct)
    {
        bool showHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;
        return await Task.Run(() =>
        {
            var matches = new List<string>();
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    string n = System.IO.Path.GetFileName(d);
                    if (startsWith.Length > 0 &&
                        !n.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!showHidden)
                    {
                        try
                        {
                            var a = File.GetAttributes(d);
                            if (a.HasFlag(FileAttributes.Hidden) || a.HasFlag(FileAttributes.System)) continue;
                        }
                        catch { }
                    }
                    matches.Add(d);
                    if (matches.Count >= 2000) break;
                }
            }
            catch { /* unreadable folder */ }

            matches.Sort((a, b) => string.Compare(
                System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
            if (matches.Count > cap) matches = matches.GetRange(0, cap);
            return matches;
        }, ct);
    }

    private static string SafeFileName(string p)
    {
        try { return System.IO.Path.GetFileName(p) ?? string.Empty; } catch { return string.Empty; }
    }

    private static bool IsWithin<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d is T;
    }

    private static Suggestion? ItemUnder(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as Suggestion;
    }
}
