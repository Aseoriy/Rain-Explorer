using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// Sign-in for a network location. The password goes straight to <c>gio mount</c> and is not stored; GVfs
/// may still offer to remember it in the desktop keyring on its own terms.
/// </summary>
public sealed class NetworkSignInDialog : DialogWindow
{
    private readonly TextBox _user;
    private readonly TextBox? _domain;
    private readonly TextBox _password;
    private readonly CheckBox _guest;

    private NetworkSignInDialog(string location, string? defaultUser, string? defaultDomain, bool asksDomain)
        : base("Connect to server", "globe", width: 420)
    {
        var panel = new StackPanel();
        panel.Children.Add(Message($"Sign in to {location}"));

        _user = Field(panel, "User name", defaultUser ?? Environment.UserName);
        if (asksDomain) _domain = Field(panel, "Domain", string.IsNullOrEmpty(defaultDomain) ? "WORKGROUP" : defaultDomain);
        _password = Field(panel, "Password", "");
        _password.PasswordChar = '•';
        _guest = new CheckBox { Content = "Connect as guest (no password)", Margin = new Thickness(0, 12, 0, 0) };
        _guest.IsCheckedChanged += (_, _) =>
        {
            bool guest = _guest.IsChecked == true;
            _user.IsEnabled = !guest;
            if (_domain is not null) _domain.IsEnabled = !guest;
            _password.IsEnabled = !guest;
        };
        panel.Children.Add(_guest);
        Body = panel;

        AddButton("Cancel", () => Finish(null));
        AddButton("Connect", Accept, DialogButtonKind.Primary, isDefault: true);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _password.Focus(), DispatcherPriority.Input);
    }

    private static TextBox Field(StackPanel panel, string label, string value)
    {
        panel.Children.Add(Message(label, "TextMuted", top: 10));
        var box = new TextBox { Text = value, Height = 32 };
        box.Classes.Add("field");
        panel.Children.Add(box);
        return box;
    }

    private void Accept()
    {
        bool guest = _guest.IsChecked == true;
        Finish(new NetworkCredentials(
            guest ? string.Empty : _user.Text?.Trim() ?? string.Empty,
            guest ? string.Empty : _domain?.Text?.Trim() ?? string.Empty,
            guest ? string.Empty : _password.Text ?? string.Empty,
            guest));
        // Drop the typed password from the control as soon as it has been handed over.
        _password.Text = string.Empty;
    }

    public static Task<NetworkCredentials?> Ask(Window? owner, string location, string? defaultUser,
        string? defaultDomain, bool asksDomain) =>
        new NetworkSignInDialog(location, defaultUser, defaultDomain, asksDomain).ShowFor<NetworkCredentials>(owner);
}

/// <summary>Pick one shared folder of an SMB server.</summary>
public sealed class SharePickerDialog : DialogWindow
{
    private readonly ListBox _list;

    private SharePickerDialog(string server, IReadOnlyList<string> shares)
        : base("Shared folders", "globe", width: 380)
    {
        _list = new ListBox { ItemsSource = shares, MaxHeight = 320, Margin = new Thickness(0, 10, 0, 0), SelectedIndex = 0 };
        _list.DoubleTapped += (_, _) => Accept();
        var panel = new StackPanel();
        panel.Children.Add(Message($"Shared folders on {server}:"));
        panel.Children.Add(_list);
        Body = panel;
        AddButton("Cancel", () => Finish(null));
        AddButton("Open", Accept, DialogButtonKind.Primary, isDefault: true);
    }

    private void Accept()
    {
        if (_list.SelectedItem is string share) Finish(share);
    }

    public static Task<string?> Ask(Window? owner, string server, IReadOnlyList<string> shares) =>
        new SharePickerDialog(server, shares).ShowFor<string>(owner);
}

/// <summary>Address-bar flow for <c>smb://</c>, <c>sftp://</c>… locations: sign in, pick a share, mount.</summary>
public static class NetworkConnectFlow
{
    /// <summary>The local folder to open, or null (cancelled or failed; <paramref name="report"/> got the reason).</summary>
    public static async Task<string?> OpenAsync(Window? owner, string uri, Action<string> report)
    {
        report($"Connecting to {DisplayHost(uri)}…");
        var result = await ConnectWithSignIn(owner, uri);
        if (result is null) { report(string.Empty); return null; }
        if (result.Outcome != NetworkConnectOutcome.Connected)
        {
            report($"⚠️ {result.Error ?? "Couldn't connect."}");
            return null;
        }

        if (NetworkLocationService.IsSmbServerRoot(uri))
        {
            var shares = await NetworkLocationService.ListSharesAsync(uri, CancellationToken.None);
            if (shares.Count == 0)
            {
                report($"⚠️ {DisplayHost(uri)} has no shared folders you can open.");
                return null;
            }
            string? share = await SharePickerDialog.Ask(owner, DisplayHost(uri), shares);
            if (share is null) { report(string.Empty); return null; }
            string shareUri = uri.TrimEnd('/') + "/" + Uri.EscapeDataString(share);
            var shareResult = await ConnectWithSignIn(owner, shareUri);
            if (shareResult is null) { report(string.Empty); return null; }
            if (shareResult.Outcome != NetworkConnectOutcome.Connected)
            {
                report($"⚠️ {shareResult.Error ?? "Couldn't connect."}");
                return null;
            }
            result = shareResult;
        }

        report(string.Empty);
        return result.LocalPath;
    }

    /// <summary>Connect, asking for credentials when the server wants them. Null when the user cancels.</summary>
    private static async Task<NetworkConnectResult?> ConnectWithSignIn(Window? owner, string uri)
    {
        var result = await NetworkLocationService.ConnectAsync(uri, null, CancellationToken.None);
        if (result.Outcome != NetworkConnectOutcome.NeedsCredentials) return result;
        var credentials = await NetworkSignInDialog.Ask(owner, DisplayHost(uri), result.DefaultUser,
            result.DefaultDomain, result.AsksDomain);
        if (credentials is null) return null;
        return await NetworkLocationService.ConnectAsync(uri, credentials, CancellationToken.None);
    }

    private static string DisplayHost(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri? u) ? u.Host + u.AbsolutePath.TrimEnd('/') : uri;
}
