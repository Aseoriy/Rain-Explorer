using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// Shown for a folder that is not a Git repository: clone a GitHub repository into a new child folder, or
/// initialize metadata here. Port of the Windows GitSetupWindow. Returns the repository path, or null.
/// </summary>
public sealed class GitSetupWindow : DialogWindow
{
    private readonly string _folder;
    private readonly TextBox _urlBox;
    private readonly Button _cloneButton;
    private readonly Button _initializeButton;
    private readonly Button _cancelButton;
    private readonly TextBlock _status;
    private CancellationTokenSource? _operationCts;
    private bool _busy;

    private GitSetupWindow(string folder) : base("Git & GitHub", "folder", width: 560)
    {
        _folder = folder;

        var path = Message(folder, "TextMuted", top: 4);
        path.FontFamily = (FontFamily?)Application.Current?.FindResource("MonoFont") ?? FontFamily.Default;
        path.FontSize = 11;

        _urlBox = new TextBox { Height = 32, Watermark = "https://github.com/owner/repository" };
        _urlBox.Classes.Add("field");
        _cloneButton = new Button { Content = "Clone into a new folder", Margin = new Thickness(0, 10, 0, 0) };
        ApplyTheme(_cloneButton, "PrimaryButton");
        _cloneButton.Click += async (_, _) => await Clone();

        _initializeButton = new Button { Content = "Initialize repository", Margin = new Thickness(0, 10, 0, 0) };
        ApplyTheme(_initializeButton, "DialogButton");
        _initializeButton.Click += async (_, _) => await Initialize();

        _status = Message("No local files will be changed until you choose an action.", "TextFaint", top: 14);

        var body = new StackPanel();
        body.Children.Add(path);
        body.Children.Add(Message("This folder is not a Git repository yet. Clone an existing GitHub repository "
                                  + "safely, or start a new local repository here."));
        body.Children.Add(Section("Clone a GitHub repository",
            "Paste a GitHub repository link. Rain creates a new child folder and never merges the clone into "
            + "files already here. Private repositories use your existing Git credential helper or SSH keys. "
            + "Never paste a token into the URL.",
            _urlBox, _cloneButton));
        body.Children.Add(Section("Start a local repository here",
            "Creates only Git metadata. It does not stage, commit, add a remote, or upload files.",
            _initializeButton));
        body.Children.Add(_status);
        Body = body;

        _cancelButton = AddButton("Cancel operation", () => _operationCts?.Cancel(), DialogButtonKind.Ghost);
        _cancelButton.IsVisible = false;
        AddButton("Close", () =>
        {
            if (_busy) _operationCts?.Cancel();
            else Finish(null);
        });
        Closing += (_, _) => _operationCts?.Cancel();
    }

    private Border Section(string title, string text, params Control[] children)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Message(text, top: 4));
        foreach (Control child in children) panel.Children.Add(child);
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 12, 0, 0),
            Child = panel,
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("Glass2"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Line1"));
        return card;
    }

    public static Task<string?> ShowFor(Window? owner, string folder) =>
        new GitSetupWindow(folder).ShowFor<string>(owner);

    private async Task Clone()
    {
        string url = _urlBox.Text?.Trim() ?? string.Empty;
        if (!GitRemoteClassifier.TryGetGitHubWebUrl(url, out string? cloneUrl) || cloneUrl is null)
        {
            _status.Text = "Paste a valid github.com repository link.";
            _urlBox.Focus();
            return;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            _status.Text = "Remove the username or token from the URL before cloning.";
            _urlBox.Focus();
            return;
        }

        await RunAsync("Cloning repository…", async token =>
        {
            GitCloneResult result = await GitIntegrationRuntime.Instance.Clone.CloneIntoNewChildAsync(_folder, cloneUrl, token);
            _status.Text = result.Message ?? "Clone failed.";
            if (result.Succeeded && result.DestinationPath is not null) Finish(result.DestinationPath);
        });
    }

    private async Task Initialize()
    {
        if (!await ConfirmDialog.Ask(this, "Initialize Git repository",
                $"Create Git repository metadata in:\n{_folder}\n\n"
                + "This will not stage, commit, connect a remote, or upload files.",
                "Initialize", "Cancel"))
            return;

        await RunAsync("Initializing repository…", async token =>
        {
            GitOperationResult result = await GitIntegrationRuntime.Instance.Mutations.InitializeAsync(_folder, token);
            _status.Text = result.Message ?? (result.Succeeded
                ? "Repository initialized. No files were staged."
                : "Repository initialization failed.");
            if (!result.Succeeded) return;
            GitIntegrationRuntime.Instance.RepositoryLocator.Invalidate(_folder);
            Finish(_folder);
        });
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        _operationCts?.Dispose();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        SetBusy(true);
        _status.Text = status;
        try
        {
            await operation(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Operation canceled.";
        }
        catch (Exception ex)
        {
            _status.Text = GitSecurity.Redact(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts)) _operationCts = null;
            cts.Dispose();
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _cloneButton.IsEnabled = !busy;
        _initializeButton.IsEnabled = !busy;
        _urlBox.IsEnabled = !busy;
        _cancelButton.IsVisible = busy;
    }
}
