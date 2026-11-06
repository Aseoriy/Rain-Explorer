using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using RainArchives.App.Infrastructure;
using RainArchives.Contracts;

namespace RainArchives.App.Views;

public partial class PasswordDialog : Window
{
    private char[]? _password;
    private readonly IDisposable? _windowChromeAttachment;
    private int _cancellationRequested;
    private int _closed;

    public PasswordDialog(string archiveName)
    {
        InitializeComponent();
        _windowChromeAttachment = (Application.Current as App)?.ThemeManager
            .AttachWindowChrome(this);
        PasswordInput.MaxLength = ProtocolConstants.MaximumSecretBytes / 4;
        var escapedArchiveName = UntrustedDisplayText.Escape(archiveName);
        ArchiveNameText.Text = escapedArchiveName;
        ArchiveNameText.ToolTip = escapedArchiveName;
        AutomationProperties.SetHelpText(
            ArchiveNameText,
            $"Encrypted archive name: {escapedArchiveName}");
        Loaded += (_, _) =>
        {
            if (Volatile.Read(ref _cancellationRequested) != 0)
            {
                Close();
                return;
            }

            PasswordInput.Focus();
        };
    }

    public char[] TakePassword() =>
        Interlocked.Exchange(ref _password, null) ?? [];

    internal void ClearCapturedPassword()
    {
        var password = Interlocked.Exchange(ref _password, null);
        if (password is not null)
        {
            Array.Clear(password);
        }
    }

    internal void RequestCancellation()
    {
        Interlocked.Exchange(ref _cancellationRequested, 1);
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            try
            {
                _ = Dispatcher.InvokeAsync(RequestCancellation);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shutdown also closes the owning application and this dialog.
            }

            return;
        }

        if (Volatile.Read(ref _closed) == 0 && IsLoaded)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Interlocked.Exchange(ref _closed, 1);
        PasswordInput.Clear();
        _windowChromeAttachment?.Dispose();
        base.OnClosed(e);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        using var secure = PasswordInput.SecurePassword.Copy();
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
            var characters = new char[secure.Length];
            for (var index = 0; index < characters.Length; index++)
            {
                characters[index] = (char)Marshal.ReadInt16(pointer, index * sizeof(char));
            }

            _password = characters;
            DialogResult = true;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }

            PasswordInput.Clear();
        }
    }
}
