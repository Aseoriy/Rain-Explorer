using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Git.Tests;

[Collection("WPF application resources")]
public sealed class ArchivePasswordDialogTests
{
    private static readonly Lazy<StaTestHost> StaHost = new(() => new StaTestHost());

    [Theory]
    [InlineData(560, 480)]
    [InlineData(660, 560)]
    public void CreationReviewKeepsSourcesAndActionsVisibleWithScrollableBody(double width, double height)
    {
        RunSta(() =>
        {
            var capabilities = new ArchiveCreationCapabilitySnapshot(
                ArchiveWriteCapabilityState.Ready,
                [new ArchiveCreationFormatOption(ArchiveWriteFormat.Zip, "ZIP", ".zip", true,
                    [ArchiveCompressionPreset.Store, ArchiveCompressionPreset.Balanced], 4096,
                    68719476736, 65536)]);
            var dialog = new CreateArchiveDialog(capabilities,
                [new ArchiveCreationSource(ArchiveCreationSourceKind.Directory,
                    @"C:\Example\Project", "Project")], @"C:\Example\Project.zip");
            try
            {
                var surface = Assert.IsType<Border>(dialog.Content);
                surface.Measure(new Size(width, height));
                surface.Arrange(new Rect(0, 0, width, height));
                surface.UpdateLayout();

                var sources = Assert.IsType<ListView>(dialog.FindName("SourcesList"));
                var body = Assert.IsType<ScrollViewer>(dialog.FindName("CreationBody"));
                var create = Assert.IsType<Button>(dialog.FindName("CreateArchiveButton"));
                Assert.Single(sources.Items);
                Assert.InRange(sources.ActualHeight, 140, 160);
                Assert.True(sources.IsHitTestVisible);
                Assert.True(body.ViewportHeight > 0);
                Assert.True(body.ExtentHeight > body.ViewportHeight);
                Point buttonPosition = create.TransformToAncestor(surface).Transform(new Point(0, 0));
                Assert.InRange(buttonPosition.Y + create.ActualHeight, 1, height);
                Assert.True(create.IsEnabled);
                Assert.True(create.IsDefault);
                Assert.Contains("65", Assert.IsType<TextBlock>(dialog.FindName("CapabilityLimitText")).Text);
                var columns = Assert.IsType<GridView>(sources.View).Columns;
                Assert.Equal(new[] { "Item kind", "Name in archive", "Current location" },
                    columns.Select(column => column.Header?.ToString()));

                body.ScrollToBottom();
                surface.UpdateLayout();
                Assert.True(body.VerticalOffset > 0);
                Assert.InRange(create.TransformToAncestor(surface).Transform(new Point()).Y
                    + create.ActualHeight, 1, height);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void AcceptedPasswordSurvivesModalCloseUntilTakenAndThenCanBeCleared()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            const string expected = "RainExplorer-Test-Password-2026!";
            PasswordBox? input = null;
            var dialog = new ArchivePasswordDialog("encrypted\r\narchive.zip");
            dialog.Loaded += (_, _) =>
            {
                input = Assert.IsType<PasswordBox>(dialog.FindName("PasswordInput"));
                input.Password = expected;
                FindVisual<Button>(dialog,
                    button => AutomationProperties.GetName(button)
                        == "Continue with archive password")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };

            Assert.True(dialog.ShowDialog() == true);
            Assert.Equal(ProtocolConstants.MaximumSecretBytes / 4, input?.MaxLength);
            Assert.Equal(string.Empty, input?.Password);

            var captured = dialog.TakePassword();
            try
            {
                Assert.Equal(expected, new string(captured));
            }
            finally
            {
                Array.Clear(captured);
            }

            Assert.Empty(dialog.TakePassword());
            dialog.ClearCapturedPassword();
            Assert.Empty(dialog.TakePassword());
        });
    }

    [Fact]
    public void ClearCapturedPasswordRemovesAcceptedPasswordWhenCallerDoesNotTakeIt()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var dialog = ShowAcceptedDialog("unused-capture");

            dialog.ClearCapturedPassword();

            Assert.Empty(dialog.TakePassword());
        });
    }

    [Fact]
    public void CancelledPromptClearsVisiblePasswordAndHasNoCapturedPassword()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            PasswordBox? input = null;
            var dialog = new ArchivePasswordDialog("cancelled-prompt");
            dialog.Loaded += (_, _) =>
            {
                input = Assert.IsType<PasswordBox>(dialog.FindName("PasswordInput"));
                input.Password = "must-not-survive-close";
                dialog.Close();
            };

            Assert.False(dialog.ShowDialog() == true);
            Assert.Equal(string.Empty, input?.Password);
            Assert.Empty(dialog.TakePassword());
        });
    }

    [Fact]
    public void FailedNonModalAcceptanceClearsTransferredPasswordOwnership()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var dialog = new ArchivePasswordDialog("failed-transfer");
            var input = Assert.IsType<PasswordBox>(dialog.FindName("PasswordInput"));
            input.Password = "must-not-survive-failed-acceptance";
            Button continueButton = FindLogical<Button>(dialog,
                button => AutomationProperties.GetName(button)
                    == "Continue with archive password");

            Assert.Throws<InvalidOperationException>(() =>
                continueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Equal(string.Empty, input.Password);
            Assert.Empty(dialog.TakePassword());
        });
    }

    [Fact]
    public void PasswordCaptureSourceKeepsBothOwnershipFailurePathsExplicit()
    {
        string root = FindRepositoryRoot();
        string passwordDialogSource = File.ReadAllText(
            Path.Combine(root, "Main", "Views", "ArchivePasswordDialog.xaml.cs"));

        Assert.Contains("if (!transferred && characters is not null)",
            passwordDialogSource, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(characters)", passwordDialogSource, StringComparison.Ordinal);
        Assert.Contains("else if (transferred && !accepted)",
            passwordDialogSource, StringComparison.Ordinal);
        Assert.Contains("ClearCapturedPassword()", passwordDialogSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveOperationMenuUsesExpectedAcceleratorsAndCancellationOnlyActivity()
    {
        string root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "Main", "Controls", "PaneView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement archiveIntegrity = document.Descendants(presentation + "MenuItem")
            .Single(element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "MenuArchiveIntegrity", StringComparison.Ordinal));
        XElement archiveExtract = document.Descendants(presentation + "MenuItem")
            .Single(element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "MenuArchiveExtract", StringComparison.Ordinal));

        Assert.Equal("Ctrl+Shift+I", (string?)archiveIntegrity.Attribute("InputGestureText"));
        Assert.Equal("Ctrl+Shift+E", (string?)archiveExtract.Attribute("InputGestureText"));
        Assert.Equal("Collapsed", (string?)archiveIntegrity.Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)archiveExtract.Attribute("Visibility"));
        Assert.Equal("ArchiveIntegrity_Click", (string?)archiveIntegrity.Attribute("Click"));
        Assert.Equal("ArchiveExtract_Click", (string?)archiveExtract.Attribute("Click"));
        Assert.Contains("Test archive integrity", archiveIntegrity.ToString(), StringComparison.Ordinal);
        Assert.Contains("Extract selected archive entries", archiveExtract.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Pause", archiveIntegrity.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pause", archiveExtract.ToString(), StringComparison.OrdinalIgnoreCase);

        string adapterSource = File.ReadAllText(
            Path.Combine(root, "Main", "Services", "ArchiveLocationAdapter.cs"));
        int operationStart = adapterSource.IndexOf(
            "private async Task<ArchiveOperationResult> RunArchiveOperationAsync",
            StringComparison.Ordinal);
        int operationEnd = adapterSource.IndexOf(
            "private async Task<ArchiveOperationResult> ExecuteArchiveOperationAsync",
            StringComparison.Ordinal);
        Assert.True(operationStart >= 0 && operationEnd > operationStart);
        string operationSource = adapterSource[operationStart..operationEnd];
        Assert.Contains("operation.Activity.AttachCancel", operationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("operation.Activity.AttachControls", operationSource, StringComparison.Ordinal);
    }

    private static ArchivePasswordDialog ShowAcceptedDialog(string archiveName)
    {
        var dialog = new ArchivePasswordDialog(archiveName);
        dialog.Loaded += (_, _) =>
        {
            var input = Assert.IsType<PasswordBox>(dialog.FindName("PasswordInput"));
            input.Password = "accepted-but-not-taken";
            FindVisual<Button>(dialog,
                button => AutomationProperties.GetName(button)
                    == "Continue with archive password")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        Assert.True(dialog.ShowDialog() == true);
        return dialog;
    }

    private static void EnsureApplicationResources()
    {
        if (Application.Current is null)
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        if (Application.Current!.TryFindResource("Text") is null)
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/RainExplorer;component/Themes/Dark.xaml",
                    UriKind.Absolute),
            });
        }
    }

    private static T FindVisual<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && predicate(typed)) return typed;
            try { return FindVisual(child, predicate); }
            catch (InvalidOperationException) { }
        }

        throw new InvalidOperationException($"A matching {typeof(T).Name} was not found.");
    }

    private static T FindLogical<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T typed && predicate(typed)) return typed;
            if (child is not DependencyObject dependency) continue;
            try { return FindLogical(dependency, predicate); }
            catch (InvalidOperationException) { }
        }

        throw new InvalidOperationException($"A matching {typeof(T).Name} was not found.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Main", "Controls", "PaneView.xaml")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Rain Explorer repository root was not found.");
    }

    internal static void RunSta(Action action) => StaHost.Value.Invoke(action);

    private sealed class StaTestHost
    {
        private readonly ManualResetEventSlim _ready = new();
        private Dispatcher? _dispatcher;

        public StaTestHost()
        {
            var thread = new Thread(Run) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _ready.Wait();
        }

        public void Invoke(Action action)
        {
            _dispatcher!.Invoke(action);
        }

        private void Run()
        {
            EnsureApplicationResources();
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        }
    }
}
