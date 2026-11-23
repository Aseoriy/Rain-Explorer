using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RainExplorer.Services;

namespace RainExplorer.Controls;

/// <summary>
/// The command box at the bottom of a pane: runs one command line at a time in the folder the pane shows
/// (through <see cref="CommandRunner"/>, so nothing can wait for input). "cd" moves the pane itself, and
/// "clear" empties the output. The history is shared by every command box in the app.
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
        ToolTip.SetTip(CloseButton, ShortcutHints.GestureFor("app.commandBox") is { } keys
            ? $"Close ({ShortcutService.Describe(keys)} or Esc)" : "Close (Esc)");
    }

    public bool IsRunning => _cts is not null;

    public void FocusInput() => Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Input);

    /// <summary>Show the pane's folder; call when it changes.</summary>
    public void RefreshFolder()
    {
        string? folder = GetFolder();
        FolderText.Text = folder ?? "Not a folder: commands run in real folders only";
        ToolTip.SetTip(FolderText, folder);
        RunButton.IsEnabled = folder is not null || IsRunning;
        TerminalButton.IsEnabled = folder is not null;
    }

    /// <summary>Stop the running command (when the box is closed or the pane goes away).</summary>
    public void Stop() => _cts?.Cancel();

    private void Input_KeyDown(object? sender, KeyEventArgs e)
    {
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

    private void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (IsRunning) Stop();
        else _ = Run();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        Output.Text = string.Empty;
        Input.Focus();
    }

    private void Terminal_Click(object? sender, RoutedEventArgs e)
    {
        if (GetFolder() is not { } folder) return;
        if (!TerminalLauncher.TryOpen(folder, out string? error)) Append($"⚠️ {error}\n");
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

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
        Input.Text = string.Empty;

        if (command is "clear" or "cls")
        {
            Output.Text = string.Empty;
            return;
        }
        if (command == "cd" || command.StartsWith("cd ", StringComparison.Ordinal))
        {
            ChangeFolder(folder, command.Length > 2 ? command[3..].Trim() : "");
            return;
        }

        Append($"$ {command}\n");
        using var cts = new CancellationTokenSource();
        _cts = cts;
        RunButton.Content = "Stop";
        try
        {
            int? exit = await CommandRunner.RunAsync(command, folder,
                text => Dispatcher.UIThread.Post(() => Append(GitSecurity.Redact(text))), cts.Token);
            // Output is posted; queue the footer behind it so it lands last.
            Dispatcher.UIThread.Post(() =>
            {
                if (exit is int code && code != 0) Append($"[exit code {code}]\n");
                Append("\n");
            });
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

    private void ChangeFolder(string folder, string argument)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string target = argument.Trim('"', '\'');
        if (target.Length == 0 || target == "~") target = home;
        else if (target.StartsWith("~/", StringComparison.Ordinal)) target = Path.Combine(home, target[2..]);
        target = Path.GetFullPath(target, folder);
        Append($"$ cd {argument}\n");
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
        string next = (Output.Text ?? string.Empty) + text;
        if (next.Length > MaxChars) next = next[^MaxChars..];
        Output.Text = next;
        OutputScroll.ScrollToEnd();
    }
}
