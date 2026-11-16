using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer.Views;

public partial class GitChangesWindow : Window
{
    private readonly GitChangesViewModel _viewModel;

    public GitChangesWindow(
        GitRepositoryContext repository,
        IEnumerable<string>? initialPaths = null)
    {
        InitializeComponent();
        _viewModel = new GitChangesViewModel(repository, initialPaths);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitChangesViewModel.Help)) UpdateHelpButtons();
        };
        UpdateHelpButtons();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _commandCts?.Cancel();
            _viewModel.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
        SelectInitial(UnstagedList);
        SelectInitial(UntrackedList);
        SelectInitial(StagedList);
        SelectInitial(ConflictList);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.RefreshAsync();

    private async void SwitchBranch_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.SwitchBranchAsync();

    private async void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog(
            "Create branch",
            "New local branch name:",
            "new-branch")
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
            await _viewModel.CreateBranchAsync(dialog.Value);
    }

    private async void DeleteBranch_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedBranch is not { IsCurrent: false } branch) return;
        bool confirmed = ConfirmDialog.Ask(
            this,
            "Delete local branch",
            $"Delete the local branch “{branch.Name}”?\n\n"
            + "Rain will only delete it if Git considers it fully merged. "
            + "The similarly named remote branch, if any, will not be deleted.",
            "Delete branch",
            "Cancel",
            danger: true);
        if (confirmed) await _viewModel.DeleteBranchAsync();
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e) =>
        _viewModel.CancelCurrentOperation();

    private async void Stage_Click(object sender, RoutedEventArgs e)
    {
        ListBox source = (sender as FrameworkElement)?.Tag switch
        {
            "conflicts" => ConflictList,
            "untracked" => UntrackedList,
            _ => UnstagedList,
        };
        var selected = source.SelectedItems.Cast<GitFileStatus>().ToList();
        await _viewModel.StageAsync(selected);
    }

    private async void Unstage_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.UnstageAsync(StagedList.SelectedItems.Cast<GitFileStatus>().ToList());

    private async void Commit_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SummaryBox.Text))
        {
            SummaryBox.Focus();
            return;
        }
        await _viewModel.CommitAsync(SummaryBox.Text, DescriptionBox.Text);
        if (_viewModel.Message.StartsWith("Committed locally", StringComparison.Ordinal))
        {
            SummaryBox.Clear();
            DescriptionBox.Clear();
        }
    }

    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRemote is not { } remote) return;
        string upstream = _viewModel.NeedsUpstream ? "\nThis will also set the upstream branch." : "";
        GitPushPreview preview = _viewModel.PushPreview;
        string warnings = BuildPushWarnings(preview);
        bool confirmed = ConfirmDialog.Ask(this,
            "Push commits to remote",
            $"Upload branch “{_viewModel.CurrentBranch}” to remote “{remote.Name}”?\n\n"
            + $"{remote.DisplayUrl}\n"
            + $"{preview.CommitCount} local commit{(preview.CommitCount == 1 ? "" : "s")} will be sent."
            + $"{upstream}{warnings}\n\n"
            + "This is separate from creating a local commit.",
            "Push commits", "Cancel", danger: false);
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

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) =>
        _viewModel.OpenSelectedRemoteOnGitHub();

    private void SelectInitial(ListBox list)
    {
        foreach (GitFileStatus file in list.Items)
            if (_viewModel.IsInitiallySelected(file)) list.SelectedItems.Add(file);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ---- Explained errors (ported from the Linux build) -----------------------------------------

    private void UpdateHelpButtons()
    {
        GitErrorFix fix = _viewModel.Help?.Fix ?? GitErrorFix.None;
        SetIdentityButton.Visibility = fix == GitErrorFix.SetIdentity ? Visibility.Visible : Visibility.Collapsed;
        HelpTerminalButton.Visibility = fix == GitErrorFix.OpenTerminal ? Visibility.Visible : Visibility.Collapsed;
        HelpCommandButton.Visibility = fix == GitErrorFix.OpenTerminal ? Visibility.Visible : Visibility.Collapsed;
        DetailsScroll.Visibility = Visibility.Collapsed;
        DetailsToggle.Content = "Show Git's message";
    }

    private void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = DetailsScroll.Visibility != Visibility.Visible;
        DetailsScroll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggle.Content = show ? "Hide Git's message" : "Show Git's message";
    }

    private async void SetIdentity_Click(object sender, RoutedEventArgs e)
    {
        var (saved, error) = await GitIdentityDialog.AskAndSave(this, _viewModel.RepositoryPath);
        if (error is not null) _viewModel.ShowMessage($"⚠️ {error}");
        else if (saved) _viewModel.ShowMessage("Name and email saved. Press Commit locally again.");
    }

    // ---- Terminal and command box ----------------------------------------------------------------

    private CancellationTokenSource? _commandCts;
    private readonly List<string> _history = [];
    private int _historyIndex;

    /// <summary>Height added for the command box, given back when it is hidden.</summary>
    private double _commandBoxGrowth;

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (!TerminalLauncher.TryOpen(_viewModel.RepositoryPath, out string? error))
            _viewModel.ShowMessage($"⚠️ {error}");
    }

    private void CommandToggle_Click(object sender, RoutedEventArgs e)
    {
        bool visible = CommandPanel.Visibility == Visibility.Visible;
        // The title button toggles; "Use command box" under an error only ever opens it.
        bool show = !visible || !ReferenceEquals(sender, CommandToggle);
        if (show && !visible)
        {
            _commandBoxGrowth = 0;
            if (WindowState == WindowState.Normal)
            {
                // Make room for the command box rather than squeezing the file lists.
                double before = ActualHeight;
                double target = Math.Min(SystemParameters.WorkArea.Height - 40, before + 230);
                if (target > before)
                {
                    Height = target;
                    _commandBoxGrowth = target - before;
                }
            }
            CommandPrompt.Text = CommandRunner.Prompt;
            CommandHint.Text = $"Runs in the repository folder with {(CommandRunner.UsesPowerShell ? "PowerShell" : "Command Prompt")} "
                + "(change it in Settings). Commands that ask for input (passwords, editors) can't answer here; use Open terminal for those.";
        }
        else if (!show && visible)
        {
            if (_commandBoxGrowth > 0 && WindowState == WindowState.Normal)
                Height = Math.Max(MinHeight, ActualHeight - _commandBoxGrowth);
            _commandBoxGrowth = 0;
        }
        CommandPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CommandToggle.Content = show ? "Hide command box" : "Command box";
        if (show) Dispatcher.BeginInvoke(() => CommandBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = RunCommand();
                break;
            case Key.Up when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Max(0, _historyIndex - 1);
                CommandBox.Text = _history[_historyIndex];
                CommandBox.CaretIndex = CommandBox.Text.Length;
                break;
            case Key.Down when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                CommandBox.Text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
                CommandBox.CaretIndex = CommandBox.Text.Length;
                break;
        }
    }

    private void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_commandCts is not null) _commandCts.Cancel();
        else _ = RunCommand();
    }

    private async Task RunCommand()
    {
        string command = CommandBox.Text.Trim();
        if (command.Length == 0 || _commandCts is not null) return;
        if (command is "clear" or "cls") { CommandOutput.Clear(); CommandBox.Clear(); return; }

        if (_history.Count == 0 || _history[^1] != command) _history.Add(command);
        _historyIndex = _history.Count;
        CommandBox.Clear();
        Append($"{CommandRunner.Prompt} {command}\n");

        using var cts = new CancellationTokenSource();
        _commandCts = cts;
        CommandRunButton.Content = "Stop";
        try
        {
            int? exit = await CommandRunner.RunAsync(command, _viewModel.RepositoryPath,
                text => Dispatcher.BeginInvoke(() => Append(GitSecurity.Redact(text))), cts.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (exit is int code && code != 0) Append($"[exit code {code}]\n");
                Append("\n");
            }, System.Windows.Threading.DispatcherPriority.Background);
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
        string next = CommandOutput.Text + text.Replace("\r\n", "\n");
        if (next.Length > MaxChars) next = next[^MaxChars..];
        CommandOutput.Text = next;
        CommandScroll.ScrollToEnd();
    }
}
