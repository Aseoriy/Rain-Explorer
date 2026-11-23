using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RainExplorer.Services;

namespace RainExplorer.Controls;

/// <summary>
/// The command box at the bottom of a pane: runs one command line at a time in the folder the pane shows
/// (through <see cref="CommandRunner"/> with the shell chosen in Settings, so nothing can wait for input).
/// "cd" moves the pane itself, and "clear"/"cls" empty the output. The history is shared by every command
/// box in the app. Port of the Linux CommandBoxView.
/// </summary>
public partial class CommandBoxView : UserControl
{
    private static readonly List<string> History = [];
    private int _historyIndex;
    private CancellationTokenSource? _cts;

    /// <summary>The folder commands run in, or null when the pane isn't showing a real folder.</summary>
    public Func<string?> GetFolder { get; set; } = () => null;

    public event EventHandler? CloseRequested;
    /// <summary>"cd somewhere": the pane should open this folder.</summary>
    public event EventHandler<string>? NavigateRequested;
    /// <summary>A command finished in this folder (it may have changed files there).</summary>
    public event EventHandler<string>? CommandFinished;

    public CommandBoxView()
    {
        InitializeComponent();
        UpdateCloseTip();
        Unloaded += (_, _) => ShortcutService.Changed -= UpdateCloseTip;
        Loaded += (_, _) =>
        {
            ShortcutService.Changed -= UpdateCloseTip;
            ShortcutService.Changed += UpdateCloseTip;
            UpdateCloseTip();
        };
    }

    private void UpdateCloseTip()
    {
        string keys = ShortcutService.Hint("app.commandBox");
        CloseButton.ToolTip = keys.Length > 0 ? $"Close ({keys} or Esc)" : "Close (Esc)";
    }

    public bool IsRunning => _cts is not null;

    public void FocusInput() => Dispatcher.BeginInvoke(() => Input.Focus(), DispatcherPriority.Input);

    /// <summary>Show the pane's folder and the shell in use; call when it changes.</summary>
    public void RefreshFolder()
    {
        string? folder = GetFolder();
        FolderText.Text = folder ?? "Not a folder: commands run in real folders only";
        FolderText.ToolTip = folder;
        PromptText.Text = CommandRunner.Prompt;
        RunButton.IsEnabled = folder is not null || IsRunning;
        TerminalButton.IsEnabled = folder is not null;
        Input.ToolTip = $"Runs with {(CommandRunner.UsesPowerShell ? "PowerShell" : "Command Prompt")} (change it in Settings). "
            + "Commands that ask for input (passwords, editors) can't answer here; use the terminal button for those.";
    }

    /// <summary>Stop the running command (when the box is closed or the pane goes away).</summary>
    public void Stop() => _cts?.Cancel();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = Run();
                break;
            case Key.Escape:
                e.Handled = true;
                if (IsRunning) Stop();
                else CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Up when History.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Clamp(_historyIndex - 1, 0, History.Count - 1);
                Input.Text = History[_historyIndex];
                Input.CaretIndex = Input.Text.Length;
                break;
            case Key.Down when History.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Min(History.Count, _historyIndex + 1);
                Input.Text = _historyIndex < History.Count ? History[_historyIndex] : string.Empty;
                Input.CaretIndex = Input.Text.Length;
                break;
        }
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning) Stop();
        else _ = Run();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Output.Clear();
        Input.Focus();
    }

    private void Terminal_Click(object sender, RoutedEventArgs e)
    {
        if (GetFolder() is not { } folder) return;
        if (!TerminalLauncher.TryOpen(folder, out string? error)) Append($"⚠️ {error}\n");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Run what's typed (also used by tests).</summary>
    internal async Task Run()
    {
        string command = Input.Text?.Trim() ?? string.Empty;
        if (command.Length == 0 || IsRunning) return;
        string? folder = GetFolder();
        if (folder is null)
        {
            Append("Commands run in real folders only. Open a folder first.\n");
            return;
        }

        if (History.Count == 0 || History[^1] != command) History.Add(command);
        _historyIndex = History.Count;
        Input.Clear();

        if (command is "clear" or "cls")
        {
            Output.Clear();
            return;
        }
        if (IsChangeFolder(command, out string argument))
        {
            ChangeFolder(folder, argument, command);
            return;
        }

        Append($"{CommandRunner.Prompt} {command}\n");
        using var cts = new CancellationTokenSource();
        _cts = cts;
        RunButton.Content = "Stop";
        try
        {
            int? exit = await CommandRunner.RunAsync(command, folder,
                text => Dispatcher.BeginInvoke(() => Append(GitSecurity.Redact(text))), cts.Token);
            // Output is posted; queue the footer behind it so it lands last.
            await Dispatcher.InvokeAsync(() =>
            {
                if (exit is int code && code != 0) Append($"[exit code {code}]\n");
                Append("\n");
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            Append("[stopped]\n\n");
        }
        finally
        {
            _cts = null;
            RunButton.Content = "Run";
            RefreshFolder();
            if (IsVisible) Input.Focus();
        }
        CommandFinished?.Invoke(this, folder);
    }

    /// <summary>"cd", "cd folder", "cd /d D:\folder", "chdir …", "Set-Location …" or "sl …".</summary>
    internal static bool IsChangeFolder(string command, out string argument)
    {
        argument = string.Empty;
        foreach (string verb in new[] { "cd", "chdir", "Set-Location", "sl" })
        {
            if (command.Equals(verb, StringComparison.OrdinalIgnoreCase)) return true;
            if (command.Length > verb.Length && command.StartsWith(verb, StringComparison.OrdinalIgnoreCase)
                && (command[verb.Length] == ' ' || (verb is "cd" or "chdir" && command[verb.Length] is '\\' or '.')))
            {
                argument = command[verb.Length..].Trim();
                if (argument.StartsWith("/d ", StringComparison.OrdinalIgnoreCase)) argument = argument[3..].Trim();
                // Chained commands ("cd x && dir") run in the shell like any other command.
                return !argument.Contains('&') && !argument.Contains('|') && !argument.Contains(';');
            }
        }
        return false;
    }

    private void ChangeFolder(string folder, string argument, string command)
    {
        Append($"{CommandRunner.Prompt} {command}\n");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string target = argument.Trim('"', '\'');
        if (target.Length == 0)
        {
            Append(folder + "\n\n");   // like cmd: plain "cd" prints the current folder
            return;
        }
        if (target == "~") target = home;
        else if (target.StartsWith("~\\", StringComparison.Ordinal) || target.StartsWith("~/", StringComparison.Ordinal))
            target = Path.Combine(home, target[2..]);
        try
        {
            target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target), folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Append($"That isn't a folder path: {ex.Message}\n\n");
            return;
        }
        if (!Directory.Exists(target))
        {
            Append($"There's no folder called “{argument}” here.\n\n");
            return;
        }
        NavigateRequested?.Invoke(this, target);
    }

    private void Append(string text)
    {
        const int MaxChars = 200_000;
        string next = Output.Text + text.Replace("\r\n", "\n");
        if (next.Length > MaxChars) next = next[^MaxChars..];
        Output.Text = next;
        OutputScroll.ScrollToEnd();
    }
}
