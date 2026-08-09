using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RainExplorer.Models;
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
        Loaded += OnLoaded;
        Closed += (_, _) => _viewModel.Dispose();
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
}
