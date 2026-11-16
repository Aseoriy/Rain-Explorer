using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>Author name and email for commits.</summary>
public sealed record GitIdentity(string Name, string Email, bool RepositoryOnly);

/// <summary>
/// Asks for the name and email Git records on commits, shown when a commit fails with "Author identity
/// unknown". Saved with <c>git config</c> (global by default, or only for this repository).
/// </summary>
public sealed class GitIdentityDialog : DialogWindow
{
    private readonly TextBox _name;
    private readonly TextBox _email;
    private readonly CheckBox _repoOnly;
    private readonly TextBlock _error;

    private GitIdentityDialog(string name, string email) : base("Set your Git name and email", "pencil", width: 440)
    {
        var panel = new StackPanel();
        panel.Children.Add(Message("Git saves these on every commit. They become public when you push to GitHub. "
                                   + "To keep your email private, use the \"noreply\" address from GitHub → Settings → Emails."));
        _name = Field(panel, "Name", name, "Your Name");
        _email = Field(panel, "Email", email, "you@example.com");
        _repoOnly = new CheckBox { Content = "Only for this repository", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(_repoOnly);
        _error = Message("", "Danger", top: 8);
        _error.IsVisible = false;
        panel.Children.Add(_error);
        Body = panel;

        AddButton("Cancel", () => Finish(null));
        AddButton("Save", Accept, DialogButtonKind.Primary, isDefault: true);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => (name.Length == 0 ? _name : _email).Focus(), DispatcherPriority.Input);
    }

    private static TextBox Field(StackPanel panel, string label, string value, string hint)
    {
        panel.Children.Add(Message(label, "TextMuted", top: 10));
        var box = new TextBox { Text = value, Height = 32, Watermark = hint };
        box.Classes.Add("field");
        panel.Children.Add(box);
        return box;
    }

    private void Accept()
    {
        string name = _name.Text?.Trim() ?? "";
        string email = _email.Text?.Trim() ?? "";
        if (name.Length == 0 || email.Length == 0 || !email.Contains('@') || email.Any(char.IsWhiteSpace))
        {
            _error.Text = "Enter a name and a valid email address.";
            _error.IsVisible = true;
            return;
        }
        Finish(new GitIdentity(name, email, _repoOnly.IsChecked == true));
    }

    /// <summary>Ask, then save with git config. Saved is false when cancelled or on failure (Error says which).</summary>
    public static async Task<(bool Saved, string? Error)> AskAndSave(Window owner, string repositoryRoot)
    {
        var git = await GitIntegrationRuntime.Instance.ExecutableLocator.FindAsync();
        if (git is null) return (false, "Git could not be found.");
        string name = await ReadConfig(git.ExecutablePath, repositoryRoot, "user.name");
        string email = await ReadConfig(git.ExecutablePath, repositoryRoot, "user.email");
        var identity = await new GitIdentityDialog(name, email).ShowFor<GitIdentity>(owner);
        if (identity is null) return (false, null);

        foreach (var (key, value) in new[] { ("user.name", identity.Name), ("user.email", identity.Email) })
        {
            var result = await ToolRunner.RunAsync(git.ExecutablePath,
                ["config", identity.RepositoryOnly ? "--local" : "--global", key, value],
                TimeSpan.FromSeconds(10), CancellationToken.None, workingDirectory: repositoryRoot);
            if (!result.Succeeded) return (false, $"Couldn't save {key}: {result.Error.Trim()}");
        }
        return (true, null);
    }

    private static async Task<string> ReadConfig(string git, string root, string key)
    {
        var result = await ToolRunner.RunAsync(git, ["config", "--get", key], TimeSpan.FromSeconds(5),
            CancellationToken.None, workingDirectory: root).ConfigureAwait(false);
        return result.Succeeded ? result.Text.Trim() : "";
    }
}
