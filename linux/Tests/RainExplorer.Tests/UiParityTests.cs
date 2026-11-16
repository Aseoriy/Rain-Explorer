using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>Rendering checks for the 2.0.0 parity features: PDF preview and the Git windows.</summary>
public sealed partial class UiTests
{
    [AvaloniaFact]
    public void PdfPreviewRendersPagesWithNavigation()
    {
        if (!PdfRenderService.IsAvailable) return;   // poppler-utils not installed on this machine
        string dir = TestEnvironment.NewFolder("pdf-preview");
        File.WriteAllBytes(Path.Combine(dir, "two pages.pdf"), MinimalPdf(pages: 2));
        var (window, _, pane) = Boot(dir);
        try
        {
            SettingsStore.Instance.Settings.ShowPreviewPane = true;
            Select(pane, "two pages.pdf");
            var preview = pane.FindControl<PreviewPane>("Preview")!;
            WaitFor(() => preview.FindControl<Image>("ImageView")!.IsVisible, 15000);
            var bar = preview.FindControl<Border>("PdfBar")!;
            Assert.True(bar.IsVisible);
            Assert.Equal("1 / 2", preview.FindControl<TextBlock>("PdfPage")!.Text);
            Assert.False(preview.FindControl<Button>("PdfPrev")!.IsEnabled);
            Assert.True(preview.FindControl<Button>("PdfNext")!.IsEnabled);
            Snapshot(window, "60-pdf-preview");

            // Clicking inside the preview (here: next page) must keep the file selected.
            var next = preview.FindControl<Button>("PdfNext")!;
            var point = next.TranslatePoint(new Avalonia.Point(next.Bounds.Width / 2, next.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            WaitFor(() => preview.FindControl<TextBlock>("PdfPage")!.Text == "2 / 2", 15000);
            Assert.Single(FileList(pane).SelectedItems!);
            Assert.True(preview.FindControl<Image>("ImageView")!.IsVisible);
        }
        finally
        {
            SettingsStore.Instance.Settings.ShowPreviewPane = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void GitChangesWindowListsRepositoryFiles()
    {
        if (ProcessLauncher.FindExecutable("git") is null) return;
        string dir = TestEnvironment.NewFolder("git-window");
        RunGit(dir, "init", "-q");
        File.WriteAllText(Path.Combine(dir, "new file.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, "staged.txt"), "staged");
        RunGit(dir, "add", "staged.txt");

        var (main, _, _) = Boot(dir);
        GitChangesWindow? window = null;
        try
        {
            var findTask = GitIntegrationRuntime.Instance.RepositoryLocator.FindAsync(dir, CancellationToken.None, useCache: false);
            WaitFor(() => findTask.IsCompleted, 10000);
            GitRepositoryContext repository = Assert.IsType<GitRepositoryContext>(findTask.Result);

            window = new GitChangesWindow(repository, [Path.Combine(dir, "new file.txt")]);
            window.Show();
            var untracked = window.FindControl<ListBox>("UntrackedList")!;
            var staged = window.FindControl<ListBox>("StagedList")!;
            WaitFor(() => untracked.ItemCount == 1 && staged.ItemCount == 1, 10000);
            WaitFor(() => untracked.SelectedItems?.Count == 1);   // the file Rain was opened with is preselected
            Assert.Equal("new file.txt", ((GitFileStatus)untracked.Items[0]!).Path);
            Snapshot(window, "61-git-changes");

            // An explained error: identity unknown shows the plain-language card and the fix button.
            var vm = (RainExplorer.ViewModels.GitChangesViewModel)window.DataContext!;
            vm.ShowMessage("Author identity unknown\n\n*** Please tell me who you are.\nfatal: unable to auto-detect email address");
            Settle();
            Assert.True(window.FindControl<Button>("SetIdentityButton")!.IsVisible);
            window.FindControl<Button>("CommandToggle")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.True(window.FindControl<Border>("CommandPanel")!.IsVisible);
            Snapshot(window, "62-git-explained-error");

            // Hiding the command box again leaves no empty space behind.
            var toggle = window.FindControl<Button>("CommandToggle")!;
            double grown = window.Height;
            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.False(window.FindControl<Border>("CommandPanel")!.IsVisible);
            Assert.True(window.Height <= grown);

            // Sections collapse from their header like the Windows expanders.
            var stagedHeader = staged.GetVisualAncestors().OfType<StackPanel>().Skip(1).First()
                .Children.OfType<Button>().First();
            stagedHeader.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.False(staged.IsEffectivelyVisible);
            Snapshot(window, "63-git-sections");
            stagedHeader.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.True(staged.IsEffectivelyVisible);
        }
        finally
        {
            window?.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public void MouseWheelNotchGlidesInsteadOfJumping()
    {
        string dir = TestEnvironment.NewFolder("smooth-scroll");
        for (int i = 0; i < 120; i++) File.WriteAllText(Path.Combine(dir, $"file {i:000}.txt"), "x");
        var settings = SettingsStore.Instance.Settings;
        settings.SmoothScrolling = true;
        settings.ScrollSpeed = ScrollSpeed.Normal;
        var (window, _, pane) = Boot(dir);
        try
        {
            var list = FileList(pane);
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            try { WaitFor(() => viewer.Extent.Height > viewer.Viewport.Height); }
            catch { Assert.Fail($"no scroll room: extent {viewer.Extent.Height}, viewport {viewer.Viewport.Height}, items {list.ItemCount}"); }
            var point = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;

            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewer.Offset.Y < 60, $"jumped straight to {viewer.Offset.Y}");   // not there in one step
            try { WaitFor(() => Math.Abs(viewer.Offset.Y - 60) < 0.6); }                  // ...but it arrives
            catch { Assert.Fail($"stuck at {viewer.Offset.Y}, extent {viewer.Extent.Height}, viewport {viewer.Viewport.Height}"); }

            settings.SmoothScrolling = false;   // off: Avalonia's own 50 px step, instantly
            double before = viewer.Offset.Y;
            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewer.Offset.Y > before);
        }
        finally
        {
            settings.SmoothScrolling = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ActivityDetailsExplainAFailure()
    {
        var (main, _, _) = Boot();
        var entry = new ActivityEntry
        {
            Title = "Open folder", IconKey = "folder-open", StartedAt = DateTime.Now.AddSeconds(-2),
            FinishedAt = DateTime.Now, Status = ActivityStatus.Failed, BeginDetail = "/root/private",
            Detail = "Access to the path '/root/private' is denied.",
            FullDetail = "Access to the path '/root/private' is denied.",
        };
        var task = ActivityDetailsDialog.ShowFor(main, entry);
        Settle();
        var dialog = TopDialog(main);
        Assert.NotNull(dialog);
        Snapshot(dialog!, "64-activity-details");
        dialog!.Close();
        WaitFor(() => task.IsCompleted);
        main.Close();
    }

    private static void RunGit(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>A tiny valid PDF with blank pages (poppler rebuilds the offsets if they're off).</summary>
    private static byte[] MinimalPdf(int pages)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string body)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        string kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R"));
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>");
        for (int i = 0; i < pages; i++) Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] >>");
        int xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
