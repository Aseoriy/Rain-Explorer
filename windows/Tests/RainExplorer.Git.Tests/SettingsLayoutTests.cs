using System.IO;
using System.Xml.Linq;
using Xunit;

namespace RainExplorer.Git.Tests;

// Windows counterpart of the Linux SettingsRowTextNeverRunsUnderItsControl test. Settings rows used to put
// the control on top of the right side of the text, so long hints ran underneath dropdowns and buttons.
public sealed class SettingsLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void EverySettingsRowGivesItsControlItsOwnColumn()
    {
        var xaml = XDocument.Load(Path.Combine(FindWindowsRoot(), "Main", "Controls", "SettingsView.xaml"));
        var offenders = new List<string>();
        int rows = 0;

        foreach (XElement grid in xaml.Descendants(P + "Grid"))
        {
            var children = grid.Elements().Where(e => !e.Name.LocalName.StartsWith("Grid.", StringComparison.Ordinal)).ToList();
            XElement? control = children.FirstOrDefault(e => (string?)e.Attribute("HorizontalAlignment") == "Right");
            if (control is null || children.Count != 2) continue;
            XElement text = children[0];
            bool isSettingsRow = text.DescendantsAndSelf(P + "TextBlock").Any(t =>
                ((string?)t.Attribute("Style"))?.Contains("RowTitle") == true
                || ((string?)t.Attribute("Style"))?.Contains("RowHint") == true);
            if (!isSettingsRow) continue;
            string title = text.Descendants(P + "TextBlock").Select(t => (string?)t.Attribute("Text")).FirstOrDefault()
                           ?? (string?)text.Attribute("Text") ?? "?";
            rows++;

            bool ownColumn = grid.Element(P + "Grid.ColumnDefinitions") is not null
                             && (string?)control.Attribute("Grid.Column") == "1";
            // The font card keeps fixed margins instead of columns; accept a right margin that clears the control.
            bool clearedByMargin = ((string?)text.Attribute("Margin"))?.Split(',') is [_, _, var right, _]
                                   && double.TryParse(right, out double gap) && gap >= 120;
            if (!ownColumn && !clearedByMargin) offenders.Add(title);
        }

        Assert.True(rows > 40, $"Only {rows} settings rows found; did the layout change?");
        Assert.True(offenders.Count == 0, "Text can run under the control in: " + string.Join(", ", offenders));
    }

    private static string FindWindowsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Main", "RainExplorer.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("windows/ folder not found");
    }
}
