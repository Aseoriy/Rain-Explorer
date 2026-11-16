using System.Windows;
using System.Windows.Input;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// Asks for the name and email Git records on commits, shown when a commit fails with "Author identity
/// unknown". Saved with <c>git config</c> (global by default, or only for this repository).
/// Port of the Linux GitIdentityDialog.
/// </summary>
public partial class GitIdentityDialog : Window
{
    public string IdentityName => NameBox.Text.Trim();
    public string IdentityEmail => EmailBox.Text.Trim();
    public bool RepositoryOnly => RepoOnlyBox.IsChecked == true;

    public GitIdentityDialog(string name, string email)
    {
        InitializeComponent();
        NameBox.Text = name;
        EmailBox.Text = email;
        Loaded += (_, _) => (name.Length == 0 ? NameBox : EmailBox).Focus();
    }

    internal static bool IsValid(string name, string email) =>
        name.Length > 0 && email.Length > 0 && email.Contains('@') && !email.Any(char.IsWhiteSpace);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!IsValid(IdentityName, IdentityEmail))
        {
            ErrorText.Text = "Enter a name and a valid email address.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>Ask, then save with git config. Saved is false when cancelled or on failure (Error says which).</summary>
    public static async Task<(bool Saved, string? Error)> AskAndSave(Window owner, string repositoryRoot)
    {
        var runner = new GitProcessRunner();
        var git = await GitIntegrationRuntime.Instance.ExecutableLocator.FindAsync();
        if (git is null) return (false, "Git could not be found.");

        string name = await ReadConfig(runner, git.ExecutablePath, repositoryRoot, "user.name");
        string email = await ReadConfig(runner, git.ExecutablePath, repositoryRoot, "user.email");
        var dialog = new GitIdentityDialog(name, email) { Owner = owner };
        if (dialog.ShowDialog() != true) return (false, null);

        string scope = dialog.RepositoryOnly ? "--local" : "--global";
        foreach (var (key, value) in new[] { ("user.name", dialog.IdentityName), ("user.email", dialog.IdentityEmail) })
        {
            var result = await runner.RunAsync(
                new GitProcessRequest(git.ExecutablePath, repositoryRoot, ["config", scope, key, value]),
                CancellationToken.None);
            if (!result.Succeeded) return (false, $"Couldn't save {key}: {result.StandardError.Trim()}");
        }
        return (true, null);
    }

    private static async Task<string> ReadConfig(GitProcessRunner runner, string git, string root, string key)
    {
        var result = await runner.RunAsync(
            new GitProcessRequest(git, root, ["config", "--get", key], ReadOnly: true), CancellationToken.None);
        return result.Succeeded ? result.StandardOutputText.Trim() : "";
    }
}
