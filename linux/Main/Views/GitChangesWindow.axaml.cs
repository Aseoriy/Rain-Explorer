using Avalonia.Controls;
using Avalonia.Interactivity;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer.Views;

/// <summary>
/// Git changes for one repository: branch switching, staging, a local commit and a separate, confirmed push.
/// Port of the Windows GitChangesWindow; the view model and Git services are shared code.
/// </summary>
public partial class GitChangesWindow : DialogWindow
{
    private readonly GitChangesViewModel _viewModel;

    public GitChangesWindow() : this(new GitRepositoryContext(XdgDirs.Home, XdgDirs.Home, XdgDirs.Home, null,
        GitRepositoryOperationState.Normal)) { }

    public GitChangesWindow(GitRepositoryContext repository, IEnumerable<string>? initialPaths = null)
    {
        InitializeComponent();
        CanResize = true;
        ShowInTaskbar = true;
        _viewModel = new GitChangesViewModel(repository, initialPaths);
        DataContext = _viewModel;
        DragFrom(TitleBar);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitChangesViewModel.Help)) UpdateHelpButtons();
        };
        UpdateHelpButtons();
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            _commandCts?.Cancel();
            _viewModel.Dispose();
        };
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _viewModel.RefreshAsync();
        SelectInitial(UnstagedList);
        SelectInitial(UntrackedList);
        SelectInitial(StagedList);
        SelectInitial(ConflictList);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();

    private async void SwitchBranch_Click(object? sender, RoutedEventArgs e) => await _viewModel.SwitchBranchAsync();

    private async void NewBranch_Click(object? sender, RoutedEventArgs e)
    {
        string? name = await InputDialog.Ask(this, "Create branch", "New local branch name:", "new-branch");
        if (!string.IsNullOrWhiteSpace(name)) await _viewModel.CreateBranchAsync(name);
    }

    private async void DeleteBranch_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedBranch is not { IsCurrent: false } branch) return;
        bool confirmed = await ConfirmDialog.Ask(this, "Delete local branch",
            $"Delete the local branch “{branch.Name}”?\n\n"
            + "Rain will only delete it if Git considers it fully merged. "
            + "The similarly named remote branch, if any, will not be deleted.",
            "Delete branch", "Cancel", danger: true);
        if (confirmed) await _viewModel.DeleteBranchAsync();
    }

    private void CancelOperation_Click(object? sender, RoutedEventArgs e) => _viewModel.CancelCurrentOperation();

    private async void Stage_Click(object? sender, RoutedEventArgs e)
    {
        ListBox source = (sender as Control)?.Tag switch
        {
            "conflicts" => ConflictList,
            "untracked" => UntrackedList,
            _ => UnstagedList,
        };
        await _viewModel.StageAsync(Selected(source));
    }

    private async void Unstage_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.UnstageAsync(Selected(StagedList));

    private async void Commit_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SummaryBox.Text))
        {
            SummaryBox.Focus();
            return;
        }
        await _viewModel.CommitAsync(SummaryBox.Text, DescriptionBox.Text ?? string.Empty);
        if (_viewModel.Message.StartsWith("Committed locally", StringComparison.Ordinal))
        {
            SummaryBox.Text = string.Empty;
            DescriptionBox.Text = string.Empty;
        }
    }

    private async void Push_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRemote is not { } remote) return;
        string upstream = _viewModel.NeedsUpstream ? "\nThis will also set the upstream branch." : "";
        GitPushPreview preview = _viewModel.PushPreview;
        bool confirmed = await ConfirmDialog.Ask(this, "Push commits to remote",
            $"Upload branch “{_viewModel.CurrentBranch}” to remote “{remote.Name}”?\n\n"
            + $"{remote.DisplayUrl}\n"
            + $"{preview.CommitCount} local commit{(preview.CommitCount == 1 ? "" : "s")} will be sent."
            + $"{upstream}{BuildPushWarnings(preview)}\n\n"
            + "This is separate from creating a local commit.",
            "Push commits", "Cancel");
        if (confirmed) await _viewModel.PushAsync();
    }

    private static string BuildPushWarnings(GitPushPreview preview)
    {
        var lines = new List<string>();
        if (preview.SensitivePaths.Count > 0)
            lines.Add($"\n\nSensitive filename warning: {SummarizePaths(preview.SensitivePaths)}");
        if (preview.LargePaths.Count > 0)
            lines.Add($"\nLarge file warning (50 MB+): {SummarizePaths(preview.LargePaths)}");
        return string.Concat(lines);
    }

    private static string SummarizePaths(IReadOnlyList<string> paths)
    {
        string shown = string.Join(", ", paths.Take(3));
        return paths.Count > 3 ? $"{shown}, and {paths.Count - 3} more" : shown;
    }

    private void OpenGitHub_Click(object? sender, RoutedEventArgs e) => _viewModel.OpenSelectedRemoteOnGitHub();

    private static List<GitFileStatus> Selected(ListBox list) =>
        list.SelectedItems?.OfType<GitFileStatus>().ToList() ?? [];

    private void SelectInitial(ListBox list)
    {
        if (list.SelectedItems is null) return;
        foreach (GitFileStatus file in list.Items.OfType<GitFileStatus>())
            if (_viewModel.IsInitiallySelected(file)) list.SelectedItems.Add(file);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ---- Explained errors ------------------------------------------------------------------------

    private void UpdateHelpButtons()
    {
        GitErrorFix fix = _viewModel.Help?.Fix ?? GitErrorFix.None;
        SetIdentityButton.IsVisible = fix == GitErrorFix.SetIdentity;
        HelpTerminalButton.IsVisible = fix == GitErrorFix.OpenTerminal;
        HelpCommandButton.IsVisible = fix == GitErrorFix.OpenTerminal;
        DetailsScroll.IsVisible = false;
        DetailsToggle.Content = "Show Git's message";
    }

    private void DetailsToggle_Click(object? sender, RoutedEventArgs e)
    {
        DetailsScroll.IsVisible = !DetailsScroll.IsVisible;
        DetailsToggle.Content = DetailsScroll.IsVisible ? "Hide Git's message" : "Show Git's message";
    }

    private async void SetIdentity_Click(object? sender, RoutedEventArgs e)
    {
        var (saved, error) = await GitIdentityDialog.AskAndSave(this, _viewModel.RepositoryPath);
        if (error is not null) _viewModel.ShowMessage($"⚠️ {error}");
        else if (saved) _viewModel.ShowMessage("Name and email saved. Press Commit locally again.");
    }

    /// <summary>Collapse or expand a file section (like the Windows build's expanders).</summary>
    private void Section_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Parent: StackPanel { Children: [_, Control body, ..] } } header) return;
        body.IsVisible = !body.IsVisible;
        if (header.Content is DockPanel { Children: [RainExplorer.Controls.Icon chevron, ..] })
            chevron.Kind = body.IsVisible ? "chevron-down" : "chevron-right";
    }

    // ---- Terminal and command box ----------------------------------------------------------------

    private CancellationTokenSource? _commandCts;
    private readonly List<string> _history = [];
    private int _historyIndex;

    private void OpenTerminal_Click(object? sender, RoutedEventArgs e)
    {
        if (!TerminalLauncher.TryOpen(_viewModel.RepositoryPath, out string? error))
            _viewModel.ShowMessage($"⚠️ {error}");
    }

    /// <summary>Height added for the command box, given back when it is hidden.</summary>
    private double _commandBoxGrowth;

    private void CommandToggle_Click(object? sender, RoutedEventArgs e)
    {
        bool show = !CommandPanel.IsVisible || !ReferenceEquals(sender, CommandToggle);
        if (show && !CommandPanel.IsVisible)
        {
            _commandBoxGrowth = 0;
            if (WindowState == Avalonia.Controls.WindowState.Normal && Screens.ScreenFromWindow(this) is { } screen)
            {
                // Make room for the command box rather than squeezing the file lists.
                double before = Bounds.Height;
                double available = screen.WorkingArea.Height / screen.Scaling;
                double target = Math.Min(available - 40, before + 230);
                if (target > before)
                {
                    Height = target;
                    _commandBoxGrowth = target - before;
                }
            }
        }
        else if (!show && CommandPanel.IsVisible)
        {
            if (_commandBoxGrowth > 0 && WindowState == Avalonia.Controls.WindowState.Normal)
                Height = Math.Max(MinHeight, Bounds.Height - _commandBoxGrowth);
            _commandBoxGrowth = 0;
        }
        CommandPanel.IsVisible = show;
        CommandToggle.Content = show ? "Hide command box" : "Command box";
        if (show) Avalonia.Threading.Dispatcher.UIThread.Post(() => CommandBox.Focus());
    }

    private void CommandBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Avalonia.Input.Key.Enter:
                e.Handled = true;   // keep the dialog's Enter = default button out of it
                _ = RunCommand();
                break;
            case Avalonia.Input.Key.Up when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Max(0, _historyIndex - 1);
                CommandBox.Text = _history[_historyIndex];
                CommandBox.CaretIndex = CommandBox.Text.Length;
                break;
            case Avalonia.Input.Key.Down when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                CommandBox.Text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
                CommandBox.CaretIndex = CommandBox.Text.Length;
                break;
        }
    }

    private void RunCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (_commandCts is not null) _commandCts.Cancel();
        else _ = RunCommand();
    }

    private async Task RunCommand()
    {
        string command = CommandBox.Text?.Trim() ?? string.Empty;
        if (command.Length == 0 || _commandCts is not null) return;
        if (command is "clear" or "cls") { CommandOutput.Text = string.Empty; CommandBox.Text = string.Empty; return; }

        if (_history.Count == 0 || _history[^1] != command) _history.Add(command);
        _historyIndex = _history.Count;
        CommandBox.Text = string.Empty;
        Append($"$ {command}\n");

        using var cts = new CancellationTokenSource();
        _commandCts = cts;
        CommandRunButton.Content = "Stop";
        try
        {
            int? exit = await CommandRunner.RunAsync(command, _viewModel.RepositoryPath,
                text => Avalonia.Threading.Dispatcher.UIThread.Post(() => Append(GitSecurity.Redact(text))), cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (exit is int code && code != 0) Append($"[exit code {code}]\n");
                Append("\n");
            });
        }
        catch (OperationCanceledException)
        {
            Append("[stopped]\n\n");
        }
        finally
        {
            _commandCts = null;
            CommandRunButton.Content = "Run";
            CommandBox.Focus();
        }
        await _viewModel.RefreshAsync();   // the command may have staged, committed or switched branches
    }

    private void Append(string text)
    {
        const int MaxChars = 200_000;
        string current = CommandOutput.Text ?? string.Empty;
        string next = current + text;
        if (next.Length > MaxChars) next = next[^MaxChars..];
        CommandOutput.Text = next;
        CommandScroll.ScrollToEnd();
    }
}
