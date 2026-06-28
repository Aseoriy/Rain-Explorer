using System.Collections.ObjectModel;
using System.Windows.Input;
using RainExplorer.Helpers;
using RainExplorer.Services;

namespace RainExplorer.ViewModels;

/// <summary>
/// One browsing pane: its own set of tabs and active tab. The window may show
/// one pane, or two side by side in split view. Each pane is fully independent.
/// </summary>
public sealed class PaneViewModel : ObservableObject
{
    private readonly FileSystemService _fs;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    /// <summary>Active tab finished navigating (drives the list animation).</summary>
    public event Action? ActiveContentsChanged;

    /// <summary>The pane has no tabs left (window decides: collapse split or close).</summary>
    public event Action<PaneViewModel>? EmptyRequested;

    public PaneViewModel(FileSystemService fs)
    {
        _fs = fs;
        NewTabCommand = new RelayCommand(_ => NewTab(activate: true));
        CloseTabCommand = new RelayCommand(p => CloseTab(p as TabViewModel ?? SelectedTab));
        ActivateCommand = new RelayCommand(_ => RequestActivate?.Invoke(this));
    }

    /// <summary>Raised when this pane wants to become the active pane (e.g. user clicked in it).</summary>
    public event Action<PaneViewModel>? RequestActivate;

    private TabViewModel? _selectedTab;
    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab is not null) _selectedTab.ContentsChanged -= OnActiveContentsChanged;
            if (Set(ref _selectedTab, value) && _selectedTab is not null)
                _selectedTab.ContentsChanged += OnActiveContentsChanged;
        }
    }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public ICommand NewTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ActivateCommand { get; }

    private void OnActiveContentsChanged() => ActiveContentsChanged?.Invoke();

    public TabViewModel NewTab(string? path = null, bool activate = true)
    {
        var tab = new TabViewModel(_fs);
        Tabs.Add(tab);
        if (activate) SelectedTab = tab;
        _ = tab.NavigateAsync(path ?? MainViewModel.StartTarget, true);
        return tab;
    }

    public void CloseTab(TabViewModel? tab)
    {
        if (tab is null) return;
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;

        if (tab == SelectedTab) tab.ContentsChanged -= OnActiveContentsChanged;
        Tabs.Remove(tab);

        if (Tabs.Count == 0)
        {
            EmptyRequested?.Invoke(this);
            return;
        }
        SelectedTab = Tabs[Math.Min(idx, Tabs.Count - 1)];
    }

    public void CycleTab(int dir)
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        int idx = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(idx + dir + Tabs.Count) % Tabs.Count];
    }
}
