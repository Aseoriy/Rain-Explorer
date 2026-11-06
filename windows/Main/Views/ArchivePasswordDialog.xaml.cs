using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using RainArchives.Contracts;

namespace RainExplorer.Views;

/// <summary>One-shot password prompt for an encrypted Rain Archives operation.</summary>
public partial class ArchivePasswordDialog : Window
{
    private char[]? _capturedPassword;

    public ArchivePasswordDialog()
        : this("encrypted archive")
    {
    }

    public ArchivePasswordDialog(string archiveName)
    {
        InitializeComponent();

        PasswordInput.MaxLength = ProtocolConstants.MaximumSecretBytes / 4;
        var escapedArchiveName = UntrustedDisplayText.Escape(archiveName);
        ArchiveNameText.Text = escapedArchiveName;
        ArchiveNameText.ToolTip = escapedArchiveName;
        AutomationProperties.SetHelpText(
            ArchiveNameText,
            $"Encrypted archive name: {escapedArchiveName}");

        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>Takes ownership of the captured password; the caller must clear the returned array.</summary>
    public char[] TakePassword() =>
        Interlocked.Exchange(ref _capturedPassword, null) ?? [];

    /// <summary>Clears an accepted password that the caller did not take.</summary>
    internal void ClearCapturedPassword()
    {
        var captured = Interlocked.Exchange(ref _capturedPassword, null);
        if (captured is not null)
            Array.Clear(captured);
    }

    protected override void OnClosed(EventArgs e)
    {
        PasswordInput.Clear();

        // An accepted password is owned by the caller after the modal result is
        // returned; cancelled and otherwise unused prompts clear their capture here.
        if (DialogResult != true)
            ClearCapturedPassword();

        base.OnClosed(e);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        using var secure = PasswordInput.SecurePassword.Copy();
        var pointer = IntPtr.Zero;
        char[]? characters = null;
        bool transferred = false;
        bool accepted = false;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
            characters = new char[secure.Length];
            for (var index = 0; index < characters.Length; index++)
                characters[index] = (char)Marshal.ReadInt16(pointer, index * sizeof(char));

            var previous = Interlocked.Exchange(ref _capturedPassword, characters);
            transferred = true;
            if (previous is not null)
                Array.Clear(previous);

            DialogResult = true;
            accepted = true;
        }
        finally
        {
            if (!transferred && characters is not null)
                Array.Clear(characters);
            else if (transferred && !accepted)
                ClearCapturedPassword();

            if (pointer != IntPtr.Zero)
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);

            PasswordInput.Clear();
        }
    }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
