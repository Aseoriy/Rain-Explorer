using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RainExplorer.Controls;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    [AvaloniaFact]
    public void CommandBoxRunsInTheCurrentFolderAndCdMovesThePane()
    {
        SettingsStore.Instance.Settings.ShortcutOverrides.Clear();
        string dir = TestEnvironment.NewFolder("command-box");
        Directory.CreateDirectory(Path.Combine(dir, "inner"));
        var (window, vm, pane) = Boot(Path.Combine(dir, "inner"));
        try
        {
            var box = pane.FindControl<CommandBoxView>("CommandBox")!;
            Assert.False(box.IsVisible);
            window.KeyPress(Key.OemTilde, RawInputModifiers.Control, PhysicalKey.Backquote, "`");
            Settle();
            Assert.True(box.IsVisible);
            Assert.Equal(Path.Combine(dir, "inner"), box.FindControl<TextBlock>("FolderText")!.Text);

            var input = box.FindControl<TextBox>("Input")!;
            input.Text = "echo hello > made.txt && echo done";
            _ = box.Run();
            WaitFor(() => File.Exists(Path.Combine(dir, "inner", "made.txt")) && !box.IsRunning, 8000);
            WaitFor(() => box.FindControl<SelectableTextBlock>("Output")!.Text?.Contains("done") == true);
            WaitFor(() => vm.ActivePane.SelectedTab!.Items.Any(i => i.Name == "made.txt"));
            Snapshot(window, "99-command-box");

            input.Text = "cd ..";
            _ = box.Run();
            WaitFor(() => vm.ActivePane.SelectedTab!.CurrentPath == dir);
            WaitFor(() => box.FindControl<TextBlock>("FolderText")!.Text == dir);

            input.Text = "cd nope";
            _ = box.Run();
            Settle();
            Assert.Contains("no folder called", box.FindControl<SelectableTextBlock>("Output")!.Text);
            Assert.Equal(dir, vm.ActivePane.SelectedTab!.CurrentPath);

            // Escape in the box closes it; Ctrl+` opens it again and closes it again.
            input.Focus();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle();
            Assert.False(box.IsVisible);
            window.KeyPress(Key.OemTilde, RawInputModifiers.Control, PhysicalKey.Backquote, "`");
            Settle();
            Assert.True(box.IsVisible);
            window.KeyPress(Key.OemTilde, RawInputModifiers.Control, PhysicalKey.Backquote, "`");
            Settle();
            Assert.False(box.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
