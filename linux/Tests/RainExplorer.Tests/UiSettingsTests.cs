using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    private static readonly string[] SettingsSections =
        ["PaneAppearance", "PaneBrowsing", "PaneFiles", "PaneShortcuts", "PaneAdvanced", "PaneAbout"];

    [AvaloniaFact]
    public async Task EverySettingsSectionCanScrollItsLastCardFullyIntoView()
    {
        var (window, vm, _) = Boot();
        try
        {
            vm.IsSettingsOpen = true;
            var settings = window.FindControl<SettingsView>("SettingsOverlay")!;
            var nav = settings.FindControl<ListBox>("SectionList")!;
            foreach (var size in new[] { new Size(1400, 780), new Size(760, 480) })
            {
                window.Width = size.Width; window.Height = size.Height;
                for (int i = 0; i < SettingsSections.Length; i++)
                {
                    nav.SelectedIndex = i;
                    await RenderFor(250);
                    var scroll = settings.FindControl<ScrollViewer>(SettingsSections[i])!;
                    scroll.Offset = new Vector(0, double.MaxValue);
                    await RenderFor(60);
                    // The last card anywhere on the page (Shortcuts builds its cards inside a list panel).
                    var last = ((StackPanel)scroll.Content!).GetVisualDescendants().OfType<Border>()
                        .Last(b => b.Classes.Contains("card") && b.IsEffectivelyVisible);
                    var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>()
                        .First(p => ReferenceEquals(p.TemplatedParent, scroll));
                    var origin = last.TranslatePoint(default, presenter)!.Value;
                    double bottom = origin.Y + last.Bounds.Height;
                    Assert.True(origin.Y >= 0, $"{SettingsSections[i]}: last card starts above the viewport at {size}.");
                    Assert.True(bottom <= presenter.Bounds.Height - 39,
                        $"{SettingsSections[i]} at {size}: last card bottom {bottom}, viewport {presenter.Bounds.Height}; bottom spacing is clipped.");
                    if (i == 0) Snapshot(window, size.Width > 1000 ? "40-settings-bottom" : "41-settings-bottom-minimum-window");
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingsRowTextNeverRunsUnderItsControl()
    {
        var (window, vm, _) = Boot();
        try
        {
            vm.IsSettingsOpen = true;
            var settings = window.FindControl<SettingsView>("SettingsOverlay")!;
            var nav = settings.FindControl<ListBox>("SectionList")!;
            int checkedRows = 0;
            foreach (var size in new[] { new Size(1400, 780), new Size(760, 480) })
            {
                window.Width = size.Width; window.Height = size.Height;
                for (int i = 0; i < SettingsSections.Length; i++)
                {
                    nav.SelectedIndex = i;
                    await RenderFor(250);
                    var pane = settings.FindControl<ScrollViewer>(SettingsSections[i])!;
                    foreach (var row in pane.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("row") && g.IsEffectivelyVisible))
                    {
                        var text = row.Children[0];
                        var control = row.Children[1];
                        if (!control.IsVisible) continue;
                        double textRight = text.TranslatePoint(new Point(text.Bounds.Width, 0), row)!.Value.X;
                        double controlLeft = control.TranslatePoint(default, row)!.Value.X;
                        string title = text.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "?";
                        Assert.True(textRight <= controlLeft + 0.5,
                            $"{SettingsSections[i]} at {size}: “{title}” text ends at {textRight}, its control starts at {controlLeft}.");
                        checkedRows++;
                    }
                    if (size.Width < 1000)
                    {
                        Snapshot(window, $"70-settings-{i}-narrow");
                        var scroll = pane;
                        scroll.Offset = new Vector(0, scroll.Extent.Height / 2);
                        await RenderFor(60);
                        Snapshot(window, $"70-settings-{i}-narrow-mid");
                        scroll.Offset = new Vector(0, double.MaxValue);
                        await RenderFor(60);
                        Snapshot(window, $"70-settings-{i}-narrow-end");
                        scroll.Offset = default;
                    }
                }
            }
            Assert.True(checkedRows > 60, $"only {checkedRows} rows checked");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingsAnimateOpeningAndSectionsAndPreserveScrollInBothPerformanceModes()
    {
        var preferences = SettingsStore.Instance.Settings;
        bool oldMode = preferences.PerformanceMode;
        string oldTheme = preferences.Theme;
        var (window, vm, _) = Boot();
        try
        {
            var settings = window.FindControl<SettingsView>("SettingsOverlay")!;
            var surface = settings.FindControl<Grid>("SettingsSurface")!;
            var nav = settings.FindControl<ListBox>("SectionList")!;
            var appearance = settings.FindControl<ScrollViewer>("PaneAppearance")!;
            var browsing = settings.FindControl<ScrollViewer>("PaneBrowsing")!;
            foreach (bool performance in new[] { false, true })
            {
                preferences.PerformanceMode = performance;
                preferences.Theme = performance ? "Light" : "Violet";
                vm.IsSettingsOpen = true;
                await RenderFor(30);
                AssertSettingsArrival(surface);
                await RenderFor(250);
                AssertSettingsAtRest(surface);
                appearance.Offset = new Vector(0, double.MaxValue);
                await RenderFor(30);
                var offset = appearance.Offset;
                Assert.True(offset.Y > 0);

                nav.SelectedIndex = 1;
                await RenderFor(30);
                AssertSettingsArrival((Control)browsing.Content!);
                AssertSettingsAtRest(surface);
                AssertSettingsAtRest(nav);
                Assert.False(appearance.IsVisible);
                Assert.True(browsing.IsVisible);
                await RenderFor(200);
                AssertSettingsAtRest((Control)browsing.Content!);

                nav.SelectedIndex = 0;
                await RenderFor(200);
                Assert.Equal(offset, appearance.Offset);
                AssertSettingsAtRest((Control)appearance.Content!);
                if (performance) Snapshot(window, "42-settings-bottom-light-performance");
                vm.IsSettingsOpen = false;
            }
        }
        finally
        {
            window.Close();
            preferences.PerformanceMode = oldMode;
            preferences.Theme = oldTheme;
        }
    }

    [AvaloniaFact]
    public async Task SettingsMotionSurvivesRapidSectionChangesClosingAndReopening()
    {
        var (window, vm, _) = Boot();
        var settings = window.FindControl<SettingsView>("SettingsOverlay")!;
        var surface = settings.FindControl<Grid>("SettingsSurface")!;
        var nav = settings.FindControl<ListBox>("SectionList")!;
        var panes = SettingsSections.Select(name => settings.FindControl<ScrollViewer>(name)!).ToArray();
        try
        {
            // Replace pending reveals before the render dispatcher has run.
            vm.IsSettingsOpen = true;
            vm.IsSettingsOpen = false;
            vm.IsSettingsOpen = true;
            nav.SelectedIndex = 1;
            nav.SelectedIndex = 2;
            await RenderFor(30);
            AssertSettingsArrival(surface);
            AssertSettingsArrival((Control)panes[2].Content!);
            AssertSettingsAtRest((Control)panes[1].Content!);

            nav.Focus();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(vm.IsSettingsOpen);
            await RenderFor(250);
            AssertSettingsAtRest(surface);
            foreach (var pane in panes) AssertSettingsAtRest((Control)pane.Content!);

            vm.IsSettingsOpen = true;
            nav.SelectedIndex = 4;
            await RenderFor(30);
            nav.SelectedIndex = 3;
            nav.SelectedIndex = 0;
            await RenderFor(250);
            AssertSettingsAtRest(surface);
            Assert.True(panes[0].IsVisible);
            Assert.All(panes.Skip(1), pane => Assert.False(pane.IsVisible));
            foreach (var pane in panes) AssertSettingsAtRest((Control)pane.Content!);

            nav.SelectedIndex = 1;
            await RenderFor(30);
            AssertSettingsArrival((Control)panes[1].Content!);
            window.Close();
            await RenderFor(200);
            AssertSettingsAtRest(surface);
            foreach (var pane in panes) AssertSettingsAtRest((Control)pane.Content!);
        }
        finally { window.Close(); }
    }

    private static void AssertSettingsArrival(Control surface)
    {
        Assert.InRange(surface.Opacity, 0.001, 0.999);
        Assert.NotNull(surface.RenderTransform);
        Assert.True(surface.RenderTransform!.Value.M32 > 0.01, "Settings should slide gently into place.");
        Assert.Equal(1, surface.RenderTransform.Value.M11);
    }

    private static void AssertSettingsAtRest(Control surface)
    {
        Assert.Equal(1, surface.Opacity);
        Assert.Null(surface.RenderTransform);
    }
}

public sealed partial class UiTests
{
    [AvaloniaFact]
    public void SettingsSearchFindsSettingsOnEveryPageAndJumpsToThem()
    {
        var (window, vm, _) = Boot();
        try
        {
            vm.IsSettingsOpen = true;
            var settings = window.FindControl<SettingsView>("SettingsOverlay")!;
            var search = settings.FindControl<TextBox>("SettingsSearch")!;
            var results = settings.FindControl<StackPanel>("SearchResults")!;

            search.Text = "smooth";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(settings.FindControl<ScrollViewer>("PaneSearch")!.IsVisible);
            var titles = results.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Smooth scrolling", titles);
            Assert.Contains("Always smooth animations", titles);
            Snapshot(window, "97-settings-search");

            search.Text = "ctrl+t";            // shortcuts are searchable by key too
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains("New tab", results.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

            search.Text = "delete";            // jump to a result on the Files page
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var hit = results.Children.OfType<Button>().First(b =>
                b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "When deleting items"));
            hit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.Equal("", search.Text);
            Assert.True(settings.FindControl<ScrollViewer>("PaneFiles")!.IsVisible);
            Assert.False(settings.FindControl<ScrollViewer>("PaneSearch")!.IsVisible);
            Assert.Contains(settings.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("found"));

            search.Text = "zzzz nothing";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(settings.FindControl<TextBlock>("SearchEmpty")!.IsVisible);

            // Escape clears the search first, then closes Settings.
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle();
            Assert.Equal("", search.Text);
            Assert.True(vm.IsSettingsOpen);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle();
            Assert.False(vm.IsSettingsOpen);
        }
        finally
        {
            vm.IsSettingsOpen = false;
            window.Close();
        }
    }
}
