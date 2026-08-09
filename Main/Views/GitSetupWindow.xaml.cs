using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

public partial class GitSetupWindow : Window
{
    private CancellationTokenSource? _operationCts;
    private bool _busy;

    public string FolderPath { get; }
    public string? RepositoryPath { get; private set; }

    public GitSetupWindow(string folderPath)
    {
        FolderPath = folderPath;
        InitializeComponent();
        DataContext = this;
        Closing += OnClosing;
    }

    private async void Clone_Click(object sender, RoutedEventArgs e)
    {
        string url = RepositoryUrlBox.Text.Trim();
        if (!GitRemoteClassifier.TryGetGitHubWebUrl(url, out string? cloneUrl)
            || cloneUrl is null)
        {
            StatusText.Text = "Paste a valid github.com repository link.";
            RepositoryUrlBox.Focus();
            return;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            StatusText.Text = "Remove the username or token from the URL before cloning.";
            RepositoryUrlBox.Focus();
            return;
        }

        await RunAsync("Cloning repository…", async token =>
        {
            GitCloneResult result = await GitIntegrationRuntime.Instance.Clone
                .CloneIntoNewChildAsync(FolderPath, cloneUrl, token);
            StatusText.Text = result.Message ?? "Clone failed.";
            if (!result.Succeeded || result.DestinationPath is null) return;
            RepositoryPath = result.DestinationPath;
            DialogResult = true;
        });
    }

    private async void Initialize_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Ask(
                this,
                "Initialize Git repository",
                $"Create Git repository metadata in:\n{FolderPath}\n\n"
                + "This will not stage, commit, connect a remote, or upload files.",
                "Initialize",
                "Cancel",
                danger: false))
            return;

        await RunAsync("Initializing repository…", async token =>
        {
            GitOperationResult result = await GitIntegrationRuntime.Instance.Mutations
                .InitializeAsync(FolderPath, token);
            StatusText.Text = result.Message ?? (result.Succeeded
                ? "Repository initialized. No files were staged."
                : "Repository initialization failed.");
            if (!result.Succeeded) return;
            GitIntegrationRuntime.Instance.RepositoryLocator.Invalidate(FolderPath);
            RepositoryPath = FolderPath;
            DialogResult = true;
        });
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = status;
        try
        {
            await operation(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation canceled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = GitSecurity.Redact(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CloneButton.IsEnabled = !busy;
        InitializeButton.IsEnabled = !busy;
        RepositoryUrlBox.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) _operationCts?.Cancel();
        else Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _operationCts?.Cancel();

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
