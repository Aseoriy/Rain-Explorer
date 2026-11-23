using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RainExplorer.Services;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Tests;

public sealed class BatchRenameTests
{
    private static string Name(string original, BatchRenameOptions o, bool dir = false, int n = 1) =>
        BatchRenameService.Apply(original, dir, o, o.UseRegex && o.Find.Length > 0
            ? new System.Text.RegularExpressions.Regex(o.Find, o.MatchCase ? default : System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : null, n);

    [Theory]
    [InlineData("IMG_001.JPG", "IMG_", "Holiday ", false, "Holiday 001.JPG")]
    [InlineData("img_001.jpg", "IMG_", "", false, "001.jpg")]          // ignore case by default
    [InlineData("img_001.jpg", "IMG_", "", true, "img_001.jpg")]       // match case
    [InlineData("report.pdf.pdf", ".pdf", "", false, "report.pdf")]    // the extension itself is kept
    public void FindAndReplace(string original, string find, string replace, bool matchCase, string expected) =>
        Assert.Equal(expected, Name(original, new() { Find = find, Replace = replace, MatchCase = matchCase }));

    [Fact]
    public void RegexCaseNumbersAndExtension()
    {
        Assert.Equal("2024-05 trip.png", Name("trip 2024-05.png", new() { Find = @"^(.*) (\d{4}-\d{2})$", Replace = "$2 $1", UseRegex = true }));
        Assert.Equal("my holiday.jpg", Name("My HOLIDAY.JPG", new() { Case = NameCase.Lower }));
        Assert.Equal("My Holiday.jpg", Name("my HOLIDAY.jpg", new() { Case = NameCase.Title }));
        Assert.Equal("007 - song.mp3", Name("song.mp3", new() { Numbering = Numbering.Before, StartAt = 7, Digits = 3, Separator = " - " }, n: 7));
        Assert.Equal("song_2.mp3", Name("song.mp3", new() { Numbering = Numbering.After, Separator = "_" }, n: 2));
        Assert.Equal("notes.md", Name("notes.txt", new() { Extension = "md" }));
        Assert.Equal("notes", Name("notes.txt", new() { Extension = "" }));
        Assert.Equal("Folder.v2", Name("Folder.v2", new() { Extension = "md" }, dir: true));   // folders keep their name
    }

    [Fact]
    public void ClashesAndBadNamesAreFlagged()
    {
        string dir = TestEnvironment.NewFolder("batch-plan");
        foreach (string f in new[] { "a.txt", "b.txt", "keep.txt" }) File.WriteAllText(Path.Combine(dir, f), f);
        var items = new[] { "a.txt", "b.txt" }.Select(f => (Path.Combine(dir, f), false)).ToList();

        var same = BatchRenameService.Plan(items, new() { Find = "a", Replace = "b" });   // a.txt -> b.txt while b stays
        Assert.Contains("Two items", same[0].Problem);

        var taken = BatchRenameService.Plan(items.Take(1).ToList(), new() { Find = "a", Replace = "keep" });
        Assert.Contains("already exists", taken[0].Problem);

        var bad = BatchRenameService.Plan(items, new() { Find = "a", Replace = "x/y" });
        Assert.NotNull(bad[0].Problem);
        Assert.Null(bad[1].Problem);   // b.txt doesn't contain "a", unchanged

        var regex = BatchRenameService.Plan(items, new() { Find = "(", UseRegex = true });
        Assert.All(regex, p => Assert.Contains("pattern", p.Problem));
        var refused = BatchRenameService.Execute(regex);
        Assert.NotNull(refused.Error);
        Assert.Empty(refused.Done);
    }

    [Fact]
    public void SwappingNamesWorksAndUndoPutsThemBack()
    {
        string dir = TestEnvironment.NewFolder("batch-swap");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "B");
        // a -> b and b -> a in one go.
        var plan = new List<RenamePlanItem>
        {
            new(Path.Combine(dir, "a.txt"), "a.txt", "b.txt", false, null),
            new(Path.Combine(dir, "b.txt"), "b.txt", "a.txt", false, null),
        };

        var (done, error) = BatchRenameService.Execute(plan);
        Assert.Null(error);
        Assert.Equal(2, done.Count);
        Assert.Equal("B", File.ReadAllText(Path.Combine(dir, "a.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(dir, "b.txt")));
        Assert.DoesNotContain(Directory.GetFiles(dir), f => Path.GetFileName(f).StartsWith(".rain-rename-"));

        var undo = new BatchRenameAction(done);
        var (undoError, redo) = undo.Invoke();
        Assert.Null(undoError);
        Assert.Equal("A", File.ReadAllText(Path.Combine(dir, "a.txt")));
        Assert.NotNull(redo);
        Assert.Null(redo!.Invoke().Item1);
        Assert.Equal("B", File.ReadAllText(Path.Combine(dir, "a.txt")));
    }

    [Fact]
    public void UndoRefusesWhenSomethingTookAnOriginalName()
    {
        string dir = TestEnvironment.NewFolder("batch-undo-guard");
        File.WriteAllText(Path.Combine(dir, "one.txt"), "1");
        var plan = BatchRenameService.Plan([(Path.Combine(dir, "one.txt"), false)], new() { Find = "one", Replace = "uno" });
        var (done, _) = BatchRenameService.Execute(plan);
        File.WriteAllText(Path.Combine(dir, "one.txt"), "new file");
        var (error, _) = new BatchRenameAction(done).Invoke();
        Assert.NotNull(error);
        Assert.Equal("1", File.ReadAllText(Path.Combine(dir, "uno.txt")));
        Assert.Equal("new file", File.ReadAllText(Path.Combine(dir, "one.txt")));
    }
}

public sealed partial class UiTests
{
    [AvaloniaFact]
    public void BatchRenameDialogPreviewsNames()
    {
        string dir = TestEnvironment.NewFolder("batch-ui");
        for (int i = 1; i <= 4; i++) File.WriteAllText(Path.Combine(dir, $"IMG_{i:000}.JPG"), "x");
        var (main, _, _) = Boot();
        try
        {
            var items = Directory.GetFiles(dir).Order().Select(f => (f, false)).ToList();
            var task = BatchRenameDialog.Ask(main, items);
            Settle();
            var dialog = Assert.IsType<BatchRenameDialog>(TopDialog(main));
            var find = dialog.GetVisualDescendantsOfType<TextBox>();
            find.Text = "IMG_";
            var boxes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<TextBox>().ToList();
            boxes[1].Text = "Trip ";
            var caseBox = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<ComboBox>().First();
            caseBox.SelectedIndex = 1;   // lowercase
            Settle();
            var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("trip 001.jpg", texts);
            Assert.Contains(texts, t => t is not null && t.StartsWith("4 of 4 items will be renamed"));
            Snapshot(dialog, "98-batch-rename");
            dialog.Close();
            WaitFor(() => task.IsCompleted);
            Assert.Null(task.Result);
        }
        finally
        {
            main.Close();
        }
    }
}
