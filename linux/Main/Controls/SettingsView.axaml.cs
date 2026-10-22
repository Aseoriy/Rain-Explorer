using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

/// <summary>A theme choice shown as a swatch card.</summary>
public sealed record ThemeSwatch(string Name, IBrush Swatch);

/// <summary>
/// Full-page settings overlay, ported from the Windows build: scrim over the blurred
/// window, sticky header, left section nav and one section visible at a time. Every
/// control binds two-way to <see cref="AppSettings"/>, which saves itself on change.
/// Windows-only rows (shell registration, Git for Windows, managed archives, updates) are not shown.
/// </summary>
public partial class SettingsView : UserControl
{
    private static AppSettings S => SettingsStore.Instance.Settings;
    private bool _syncing;
    private readonly Dictionary<string, Action> _enumSyncs = new(StringComparer.Ordinal);
    private readonly ScrollViewer[] _panes;
    private int _selectedSection;
    private int _openRevision;
    private int _sectionRevision;

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsView()
    {
        InitializeComponent();
        _panes = [PaneAppearance, PaneBrowsing, PaneFiles, PaneAdvanced, PaneAbout];
        DataContext = S;

        SectionList.SelectionChanged += (_, _) => ShowSection(SectionList.SelectedIndex);
        ThemeList.ItemsSource = ThemeService.Names.Select(n => new ThemeSwatch(n, ThemeService.Swatch(n))).ToList();
        ThemeList.SelectionChanged += (_, _) =>
        {
            if (!_syncing && ThemeList.SelectedItem is ThemeSwatch swatch) S.Theme = swatch.Name;
        };

        BindEnum(LayoutBox, nameof(AppSettings.ViewLayout), new Dictionary<ViewLayout, string>
        {
            [ViewLayout.Details] = "Details", [ViewLayout.List] = "List",
            [ViewLayout.Tiles] = "Tiles", [ViewLayout.Grid] = "Grid",
        });
        BindEnum(DensityBox, nameof(AppSettings.Density), new Dictionary<ViewDensity, string>
        {
            [ViewDensity.Comfortable] = "Comfortable", [ViewDensity.Compact] = "Compact",
        });
        BindEnum(RenameBox, nameof(AppSettings.RenameMode), new Dictionary<RenameMode, string>
        {
            [RenameMode.Dialog] = "Pop-up dialog", [RenameMode.Inline] = "Inline",
        });
        BindEnum(DeleteBehaviorBox, nameof(AppSettings.DeleteBehavior), new Dictionary<DeleteBehavior, string>
        {
            [DeleteBehavior.Prompt] = "Prompt", [DeleteBehavior.Recycle] = "Trash",
            [DeleteBehavior.Permanent] = "Permanent",
        });
        BindEnum(SizeFormatBox, nameof(AppSettings.SizeFormat), new Dictionary<SizeFormat, string>
        {
            [SizeFormat.Binary] = "Binary (KiB)", [SizeFormat.Decimal] = "Decimal (KB)",
        });

        var fonts = new List<string> { AppSettings.DefaultFontFamily };
        try
        {
            fonts.AddRange(FontManager.Current.SystemFonts.Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && n != AppSettings.DefaultFontFamily)
                .Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        }
        catch { }
        FontBox.ItemsSource = fonts;
        FontBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && FontBox.SelectedItem is string font) S.FontFamily = font;
        };

        TerminalAppBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && TerminalAppBox.SelectedItem is Choice<string> choice) S.TerminalCommand = choice.Value;
        };
        ArchiveAppBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && ArchiveAppBox.SelectedItem is Choice<string> choice) S.ArchiveManager = choice.Value;
        };

        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        ConfigPathText.Text = PathUtil.Tildify(Path.Combine(configDir, "settings.json"));
        VersionText.Text = $"Version {AppVersion}";
        Refresh();
        UpdateIntegrationStatus();
    }

    public static string AppVersion =>
        typeof(SettingsView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? typeof(SettingsView).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Re-sync controls that are not plain bindings.</summary>
    public void Refresh()
    {
        _syncing = true;
        try
        {
            ThemeList.SelectedItem = ThemeList.Items.OfType<ThemeSwatch>().FirstOrDefault(t => t.Name == S.Theme);
            FontBox.SelectedItem = FontBox.Items.OfType<string>().FirstOrDefault(f => f == S.FontFamily) ?? FontBox.Items.FirstOrDefault();
            var terminals = new List<Choice<string>> { new("", "Automatic") };
            terminals.AddRange(TerminalLauncher.Installed().Select(t => new Choice<string>(t.Exe, t.Label)));
            if (!string.IsNullOrEmpty(S.TerminalCommand) && terminals.All(c => c.Value != S.TerminalCommand))
                terminals.Add(new Choice<string>(S.TerminalCommand, S.TerminalCommand));
            TerminalAppBox.ItemsSource = terminals;
            TerminalAppBox.SelectedItem = terminals.FirstOrDefault(c => c.Value == S.TerminalCommand) ?? terminals[0];
            var selectedArchiveApp = ArchiveLauncher.Resolve(out string? archiveError);
            var archiveApps = new List<Choice<string>>
            {
                new("", string.IsNullOrEmpty(S.ArchiveManager) && selectedArchiveApp is not null
                    ? $"Automatic ({selectedArchiveApp.Label})" : "Automatic"),
            };
            archiveApps.AddRange(ArchiveLauncher.Installed().Select(app => new Choice<string>(app.Id, app.Label)));
            if (!string.IsNullOrEmpty(S.ArchiveManager) && archiveApps.All(choice => choice.Value != S.ArchiveManager))
                archiveApps.Add(new Choice<string>(S.ArchiveManager, $"{ArchiveLauncher.LabelFor(S.ArchiveManager)} (not installed)"));
            ArchiveAppBox.ItemsSource = archiveApps;
            ArchiveAppBox.SelectedItem = archiveApps.FirstOrDefault(choice => choice.Value == S.ArchiveManager) ?? archiveApps[0];
            ArchiveAppStatus.Text = archiveError ?? (selectedArchiveApp?.CanExtractHere == false
                ? "Extraction opens the app's destination dialog. Passwords and overwrite choices are handled there."
                : "Passwords, extraction options and overwrite choices are handled by the archive app.");
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.Theme) or nameof(AppSettings.FontFamily) or nameof(AppSettings.ArchiveManager)) Refresh();
        if (e.PropertyName is { } name && _enumSyncs.TryGetValue(name, out var sync)) sync();
    }

    private void ShowSection(int index)
    {
        index = Math.Clamp(index, 0, _panes.Length - 1);
        if (index == _selectedSection) return;
        _selectedSection = index;
        int revision = ++_sectionRevision;
        for (int i = 0; i < _panes.Length; i++)
        {
            if (_panes[i].Content is Control content) ResetMotion(content);
            _panes[i].IsVisible = i == index;
        }
        if (IsVisible && this.GetVisualRoot() is not null && _panes[index].Content is Control incoming)
            QueueArrival(incoming, 6, 150, () => revision == _sectionRevision);
    }

    internal void AnimateOpening()
    {
        StopAnimations();
        int revision = _openRevision;
        QueueArrival(SettingsSurface, 8, 180, () => revision == _openRevision);
    }

    private void QueueArrival(Control surface, double y, int milliseconds, Func<bool> isCurrent)
    {
        surface.Opacity = 0;
        // Wait for the newly visible section to lay out before starting its clock.
        Dispatcher.UIThread.Post(() =>
        {
            if (!isCurrent() || !IsVisible || this.GetVisualRoot() is null) return;
            surface.Opacity = 1;
            _ = Motion.Arrive(surface, scale: 1, y: y, milliseconds: milliseconds);
        }, DispatcherPriority.Render);
    }

    internal void StopAnimations()
    {
        _openRevision++;
        _sectionRevision++;
        ResetMotion(SettingsSurface);
        foreach (var pane in _panes)
            if (pane.Content is Control content) ResetMotion(content);
    }

    private static void ResetMotion(Control surface)
    {
        Motion.Cancel(surface);
        surface.Opacity = 1;
        surface.RenderTransform = null;
    }

    private void BindEnum<T>(ComboBox box, string property, IReadOnlyDictionary<T, string> labels) where T : struct, Enum
    {
        var options = labels.Select(p => new Choice<T>(p.Key, p.Value)).ToList();
        box.ItemsSource = options;
        var prop = typeof(AppSettings).GetProperty(property)!;
        void Sync()
        {
            _syncing = true;
            box.SelectedItem = options.FirstOrDefault(o => o.Value.Equals((T)prop.GetValue(S)!));
            _syncing = false;
        }
        Sync();
        box.SelectionChanged += (_, _) =>
        {
            if (!_syncing && box.SelectedItem is Choice<T> choice && !choice.Value.Equals((T)prop.GetValue(S)!))
                prop.SetValue(S, choice.Value);
        };
        _enumSyncs[property] = Sync;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        S.PropertyChanged -= OnSettingsChanged;
        S.PropertyChanged += OnSettingsChanged;
        FileManagerIntegration.Instance.PropertyChanged -= OnIntegrationChanged;
        FileManagerIntegration.Instance.PropertyChanged += OnIntegrationChanged;
        UpdateIntegrationStatus();
        foreach (var sync in _enumSyncs.Values) sync();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopAnimations();
        S.PropertyChanged -= OnSettingsChanged;
        FileManagerIntegration.Instance.PropertyChanged -= OnIntegrationChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm) vm.IsSettingsOpen = false;
    }

    private void OnIntegrationChanged(object? sender, PropertyChangedEventArgs e) => UpdateIntegrationStatus();

    private void UpdateIntegrationStatus()
    {
        var integration = FileManagerIntegration.Instance;
        ShowInFolderStatus.Text = integration.Status;
        DefaultFileManagerStatus.Text = integration.DefaultStatus;
        DefaultFileManagerButton.IsEnabled = !integration.IsMakingDefault;
    }

    private async void MakeDefaultFileManager_Click(object? sender, RoutedEventArgs e) =>
        await FileManagerIntegration.Instance.MakeDefaultAsync();

    private async void RetryShowInFolder_Click(object? sender, RoutedEventArgs e) =>
        await FileManagerIntegration.Instance.RetryAsync();

    private async void BrowseDefaultFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Default starting folder" });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) S.DefaultFolder = path;
    }

    private void NewList_Click(object? sender, RoutedEventArgs e) => MainViewModel.AddCustomGroup();

    private void OpenConfigFolder_Click(object? sender, RoutedEventArgs e)
    {
        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm)
        {
            vm.IsSettingsOpen = false;
            vm.ActivePane.NewTab(configDir, activate: true);
        }
    }

    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            try { ProcessLauncher.OpenDefault(url); } catch { }
        }
    }
}
