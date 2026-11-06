using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App;
using RainArchives.App.Services;
using RainArchives.App.Views;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

internal static class WpfRebuildCopyAssertions
{
    public static void Run(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var button = FindVisualDescendant<Button>(
            window,
            candidate => AutomationProperties.GetName(candidate) == "Rebuild ZIP copy");
        Assert.IsNotNull(button);
        Assert.IsFalse(button.IsEnabled);
        StringAssert.Contains(
            AutomationProperties.GetHelpText(button),
            "Open an eligible ZIP archive");

        AssertRebuildDialog(window);
    }

    private static void AssertRebuildDialog(MainWindow owner)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.WpfRebuildCopyTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RebuildArchiveDialog? dialog = null;
        try
        {
            var source = Path.Combine(root, "source.zip");
            var replacement = Path.Combine(root, "replacement.txt");
            var addition = Path.Combine(root, "addition.bin");
            var destination = Path.Combine(root, "rebuilt.zip");
            File.WriteAllText(source, "source");
            File.WriteAllText(replacement, "replacement");
            File.WriteAllText(addition, "addition");
            var entry = new ArchiveEntryDescriptor(
                EntryId.New(),
                null,
                0,
                "tree/file.txt",
                "file.txt",
                ArchiveEntryKind.File,
                120,
                80,
                null,
                null,
                "Deflate",
                null,
                false,
                false,
                null,
                EntrySafetyStatus.Safe,
                null,
                "tree/file.txt",
                0);
            var directory = new ArchiveEntryDescriptor(
                EntryId.New(),
                null,
                1,
                "folder",
                "folder",
                ArchiveEntryKind.Directory,
                0,
                0,
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                EntrySafetyStatus.Safe,
                null,
                "folder/",
                0);
            var retained = new ArchiveEntryDescriptor(
                EntryId.New(),
                null,
                2,
                "keep.txt",
                "keep.txt",
                ArchiveEntryKind.File,
                42,
                30,
                null,
                null,
                "Deflate",
                null,
                false,
                false,
                null,
                EntrySafetyStatus.Safe,
                null,
                "keep.txt",
                0);
            var treePlan = new[]
            {
                new ZipRebuildTreePlanNode(
                    "tree",
                    true,
                    12,
                    9,
                    new string('a', 64)),
            };
            dialog = new RebuildArchiveDialog(source, [entry, directory, retained], treePlan)
            {
                Owner = owner,
            };
            dialog.Show();
            dialog.UpdateLayout();

            var entriesGrid = Assert.IsInstanceOfType<DataGrid>(dialog.FindName("EntriesGrid"));
            Assert.AreEqual("Persistent archive entries", AutomationProperties.GetName(entriesGrid));
            StringAssert.Contains(AutomationProperties.GetHelpText(entriesGrid), "duplicate ordinal");
            Assert.IsTrue(entriesGrid.Columns.Any(column => column.Header?.ToString() == "Rename to"));
            var filesGrid = Assert.IsInstanceOfType<DataGrid>(dialog.FindName("FilesGrid"));
            Assert.AreEqual("Rebuild ZIP file inputs", AutomationProperties.GetName(filesGrid));
            var treePlanGrid = Assert.IsInstanceOfType<DataGrid>(dialog.FindName("TreePlanGrid"));
            Assert.AreEqual("Worker returned ZIP tree plan", AutomationProperties.GetName(treePlanGrid));
            Assert.IsTrue(treePlanGrid.Columns.Any(column => column.Header?.ToString() == "Descendants"));
            Assert.IsTrue(treePlanGrid.Columns.Any(column => column.Header?.ToString() == "Material"));
            var folderAddsGrid = Assert.IsInstanceOfType<DataGrid>(dialog.FindName("FolderAddsGrid"));
            Assert.IsTrue(folderAddsGrid.Columns.Any(column => column.Header?.ToString() == "Bounded review"));
            var metadataGrid = Assert.IsInstanceOfType<DataGrid>(dialog.FindName("MetadataGrid"));
            Assert.AreEqual("ZIP metadata current and final review", AutomationProperties.GetName(metadataGrid));
            Assert.IsTrue(metadataGrid.Columns.Any(column => column.Header?.ToString() == "Current time"));
            Assert.IsTrue(metadataGrid.Columns.Any(column => column.Header?.ToString() == "Final time"));
            Assert.IsTrue(metadataGrid.Columns.Any(column => column.Header?.ToString() == "Current attributes"));
            Assert.IsTrue(metadataGrid.Columns.Any(column => column.Header?.ToString() == "Final attributes"));
            foreach (var reviewGrid in new[]
                     {
                         entriesGrid,
                         filesGrid,
                         treePlanGrid,
                         folderAddsGrid,
                         metadataGrid,
                     })
            {
                AssertRecyclingGrid(reviewGrid);
            }

            CollectionAssert.AreEqual(
                new[] { 0, 4, 7, 8, 18 },
                new[]
                {
                    KeyboardNavigation.GetTabIndex(entriesGrid),
                    KeyboardNavigation.GetTabIndex(filesGrid),
                    KeyboardNavigation.GetTabIndex(treePlanGrid),
                    KeyboardNavigation.GetTabIndex(folderAddsGrid),
                    KeyboardNavigation.GetTabIndex(metadataGrid),
                });
            var rebuildContentScroll = Assert.IsInstanceOfType<ScrollViewer>(
                dialog.FindName("RebuildContentScroll"));
            var cancelSetupButton = Assert.IsInstanceOfType<Button>(
                dialog.FindName("CancelSetupButton"));
            var rebuildCopyButton = Assert.IsInstanceOfType<Button>(
                dialog.FindName("RebuildCopyButton"));
            Assert.AreEqual(19, KeyboardNavigation.GetTabIndex(cancelSetupButton));
            Assert.AreEqual(20, KeyboardNavigation.GetTabIndex(rebuildCopyButton));
            Assert.IsTrue(cancelSetupButton.IsCancel);
            Assert.IsTrue(rebuildCopyButton.IsDefault);
            dialog.Width = dialog.MinWidth;
            dialog.Height = dialog.MinHeight;
            dialog.UpdateLayout();
            Assert.IsTrue(rebuildContentScroll.ActualHeight > 0);
            Assert.IsTrue(rebuildCopyButton.IsVisible);
            Assert.IsTrue(rebuildCopyButton.ActualHeight > 0);
            dialog.Width = 980;
            dialog.Height = 760;
            dialog.UpdateLayout();
            var metadataLabel = Assert.IsInstanceOfType<Expander>(
                dialog.FindName("AdvancedMetadataExpander"));
            metadataLabel.IsExpanded = true;
            dialog.UpdateLayout();
            Assert.IsTrue(metadataGrid.IsVisible);
            Assert.AreEqual(150d, metadataGrid.Height);
            var metadataScroll = Assert.IsInstanceOfType<ScrollViewer>(metadataLabel.Content);
            Assert.AreEqual(260d, metadataScroll.MaxHeight);
            var metadataHelp = FindVisualDescendant<TextBlock>(
                metadataLabel,
                text => text.Text.Contains("timezone unavailable", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(metadataHelp);
            Assert.IsTrue(
                FindVisualDescendant<TextBlock>(
                    metadataLabel,
                    text => text.Text == "Archive time (timezone unavailable)") is not null);
            StringAssert.Contains(
                AutomationProperties.GetHelpText(metadataGrid),
                "Rain's derived rebuild value");
            Assert.IsFalse(
                AutomationProperties.GetHelpText(metadataGrid).Contains(
                    "keep the current value",
                    StringComparison.OrdinalIgnoreCase));
            var untrustedEntry = new RebuildCopyEntryItem(entry with
            {
                NormalizedPath = "tree\nfile.txt",
            });
            Assert.AreEqual("tree\\u000Afile.txt", untrustedEntry.DisplayPath);
            Assert.AreEqual("tree\nfile.txt", untrustedEntry.TargetPath);
            var untrustedInput = new RebuildCopyFileItem(
                RebuildCopyFileMode.Add,
                "C:\\source.txt",
                "tree\nfile.txt");
            Assert.AreEqual("tree\\u000Afile.txt", untrustedInput.DisplayArchivePath);
            var untrustedTree = new RebuildTreePlanItem(new ZipRebuildTreePlanNode(
                "tree\n",
                true,
                1,
                1,
                "revision"));
            Assert.AreEqual("tree\\u000A", untrustedTree.DisplayPath);
            Assert.AreEqual(
                "unavailable (raw ZIP DOS time not exposed)",
                dialog.Metadata.Single(item => item.Entry?.TargetPath == "keep.txt").CurrentTimeText);
            Assert.AreEqual(12, dialog.TreePlan.Single().DescendantCount);
            Assert.AreEqual(9, dialog.TreePlan.Single().MaterialEntryCount);
            dialog.TreePlan.Single().MovePrefix = "MovedTree/";
            var nonexistentFolder = Path.Combine(root, "selection-only-folder");
            var addedFolder = new RebuildFolderAddItem(nonexistentFolder, "Imported/", 7, 3);
            dialog.FolderAdds.Add(addedFolder);
            var addedFolderMetadata = RebuildMetadataItem.ForMappedDirectory(addedFolder);
            dialog.Metadata.Add(addedFolderMetadata);
            addedFolder.ArchiveRoot = "ImportedRenamed/";
            addedFolderMetadata.FinalAttributesText = "archive";
            Assert.AreEqual(3, dialog.FolderAdds.Single().FileCount);
            StringAssert.Contains(dialog.FolderAdds.Single().CountText, "7 descendants");
            Assert.AreEqual("ImportedRenamed", addedFolderMetadata.Target.MappedFinalArchivePath);
            var preset = Assert.IsInstanceOfType<ComboBox>(dialog.FindName("PresetInput"));
            Assert.AreEqual(4, preset.Items.Count);
            Assert.AreEqual(ArchiveCompressionPreset.Balanced, ((CompressionPresetOption)preset.SelectedItem!).Value);

            var replaceButton = Assert.IsInstanceOfType<Button>(dialog.FindName("ReplaceFileButton"));
            Assert.IsFalse(replaceButton.IsEnabled);
            entriesGrid.SelectedItem = dialog.PersistentEntries[0];
            entriesGrid.UpdateLayout();
            Assert.IsTrue(replaceButton.IsEnabled);
            Assert.IsTrue(dialog.TryReplaceSelectedFile(replacement));
            Assert.AreEqual("Replace", dialog.Files.Single().Mode);
            Assert.AreEqual("tree/file.txt", dialog.Files.Single().ArchivePath);
            var mappedReplacementMetadata = dialog.Metadata.Single(item =>
                item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
                && item.Target.MappedKind == ArchiveEntryKind.File
                && item.Target.MappedFinalArchivePath == "tree/file.txt");
            Assert.AreEqual("unavailable", mappedReplacementMetadata.CurrentTimeText);
            Assert.IsFalse(dialog.Metadata.Any(item => item.Entry?.TargetPath == "tree/file.txt"));
            var removeInputButton = Assert.IsInstanceOfType<Button>(dialog.FindName("RemoveFileButton"));
            filesGrid.SelectedItem = dialog.Files.Single();
            removeInputButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsEmpty(dialog.Files);
            Assert.IsTrue(dialog.Metadata.Any(item => item.Entry?.TargetPath == "tree/file.txt"));
            entriesGrid.SelectedItem = dialog.PersistentEntries[0];
            Assert.IsTrue(dialog.TryReplaceSelectedFile(replacement));
            mappedReplacementMetadata = dialog.Metadata.Single(item =>
                item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
                && item.Target.MappedKind == ArchiveEntryKind.File
                && item.Target.MappedFinalArchivePath == "tree/file.txt");
            mappedReplacementMetadata.FinalAttributesText = "archive";
            entriesGrid.SelectedItem = dialog.PersistentEntries[2];
            Assert.IsTrue(dialog.TryRenameSelectedEntry("Moved/keep.txt"));

            Assert.IsTrue(dialog.TryAddFiles([addition]));
            Assert.AreEqual(2, dialog.Files.Count);
            dialog.Files[1].ArchivePath = "tree/cafe\u0301.bin";
            var mappedAdditionMetadata = dialog.Metadata.Single(item =>
                item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
                && item.Target.MappedKind == ArchiveEntryKind.File
                && item.Target.MappedFinalArchivePath == "tree/cafe\u0301.bin");
            mappedAdditionMetadata.FinalAttributesText = "archive";
            var destinationInput = Assert.IsInstanceOfType<TextBox>(dialog.FindName("DestinationInput"));
            destinationInput.Text = destination;
            dialog.Files[1].ArchivePath = "bad*.txt";
            Assert.IsFalse(dialog.TryAddFiles([addition]));
            StringAssert.Contains(
                Assert.IsInstanceOfType<TextBlock>(dialog.FindName("ValidationMessage")).Text,
                "Fix invalid");
            Assert.AreEqual(2, dialog.Files.Count);
            Assert.IsFalse(dialog.TryBuildSelection(out _, out var unsafePathMessage));
            StringAssert.Contains(unsafePathMessage, "must be safe");
            dialog.Files[1].ArchivePath = "tree/cafe\u0301.bin";
            dialog.PersistentEntries[1].IsMarkedForDeletion = true;
            dialog.Metadata.Single(item => item.Entry?.TargetPath == "folder/").FinalAttributesText = "archive";
            Assert.IsFalse(dialog.TryBuildSelection(out _, out var deletedMetadata));
            StringAssert.Contains(deletedMetadata, "deleted from the final archive");
            dialog.Metadata.Single(item => item.Entry?.TargetPath == "folder/").FinalAttributesText = string.Empty;
            dialog.Metadata.Single(item => item.Entry?.TargetPath == "keep.txt").FinalTimeText = "2026-01-01T00:00:02";
            dialog.Metadata.Single(item => item.Entry?.TargetPath == "keep.txt").FinalAttributesText = "ro,archive";
            Assert.IsTrue(dialog.TryBuildSelection(out var selection, out var validationMessage), validationMessage);
            Assert.IsNotNull(selection);
            Assert.AreEqual(destination, selection!.DestinationPath);
            CollectionAssert.AreEqual(
                new[] { new ArchiveEntryKey("folder/", 0) },
                selection.EntriesToDelete.ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    new ArchiveRebuildCopyFile(replacement, "tree/file.txt"),
                    new ArchiveRebuildCopyFile(addition, "tree/café.bin"),
                },
                selection.Files.ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    new ArchiveRebuildRename(
                        new ArchiveEntryKey("keep.txt", 0),
                        "Moved/keep.txt"),
                },
                selection.Renames!.ToArray());
            CollectionAssert.AreEqual(
                new[] { new ArchiveCreationDirectory(nonexistentFolder, "ImportedRenamed") },
                selection.Directories!.ToArray(),
                "WPF selection must not enumerate or require the chosen source folder; Security remains filesystem authority.");
            CollectionAssert.AreEqual(
                new[]
                {
                    new ArchiveRebuildTreeAction(
                        "tree",
                        new string('a', 64),
                        ArchiveRebuildTreeActionKind.Move,
                        "MovedTree"),
                },
                selection.TreeActions!.ToArray());
            Assert.HasCount(4, selection.Metadata!);
            var retainedMetadata = selection.Metadata!.Single(item =>
                item.Target.Kind == ArchiveRebuildMetadataTargetKind.RetainedEntryKey);
            Assert.AreEqual(
                new ArchiveDosLocalTimestamp(2026, 1, 1, 0, 0, 2),
                retainedMetadata.LastWriteTime);
            Assert.AreEqual((ushort)0x0021, retainedMetadata.AttributesLowWord);
            var mappedMetadata = selection.Metadata!.Single(item =>
                item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
                && item.Target.MappedFinalArchivePath == "MovedTree/file.txt");
            Assert.AreEqual("MovedTree/file.txt", mappedMetadata.Target.MappedFinalArchivePath);
            Assert.AreEqual((ushort)0x0020, mappedMetadata.AttributesLowWord);
            var mappedAddition = selection.Metadata!.Single(item =>
                item.Target.MappedFinalArchivePath == "MovedTree/café.bin");
            Assert.AreEqual((ushort)0x0020, mappedAddition.AttributesLowWord);
            var mappedDirectory = selection.Metadata!.Single(item =>
                item.Target.MappedKind == ArchiveEntryKind.Directory);
            Assert.AreEqual("ImportedRenamed", mappedDirectory.Target.MappedFinalArchivePath);
            var removeFolderButton = Assert.IsInstanceOfType<Button>(dialog.FindName("RemoveFolderButton"));
            var folderAddsGridForRemoval = Assert.IsInstanceOfType<DataGrid>(dialog.FindName("FolderAddsGrid"));
            folderAddsGridForRemoval.SelectedItem = addedFolder;
            removeFolderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(dialog.FolderAdds.Contains(addedFolder));
            Assert.IsFalse(dialog.Metadata.Contains(addedFolderMetadata));
            dialog.Metadata.Single(item => item.Entry?.TargetPath == "keep.txt").FinalTimeText = "2026-01-01T00:00:01";
            Assert.IsFalse(dialog.TryBuildSelection(out _, out var invalidMetadata));
            StringAssert.Contains(invalidMetadata, "even seconds");
            var safetyText = Assert.IsInstanceOfType<TextBlock>(dialog.FindName("OriginalUntouchedText"));
            StringAssert.Contains(safetyText.Text, "original archive remains untouched");
            StringAssert.Contains(safetyText.Text, "Rename is distinct from Replace");

            var presetRoot = Path.Combine(root, "preset-root");
            var presetStore = new ZipPresetStore(presetRoot);
            var presetTime = new ArchiveDosLocalTimestamp(2027, 2, 2, 4, 6, 8);
            Assert.IsTrue(presetStore.Save(
                [ZipPreset.CreateUser(
                    "Mapped defaults",
                    ArchiveCompressionPreset.Fast,
                    new ZipPresetMetadataDefaults(presetTime, 0x0020))]).Succeeded);
            var presetDialog = new RebuildArchiveDialog(
                source,
                [entry, retained],
                [],
                client: null,
                sessionId: null,
                presetStore: presetStore)
            {
                Owner = owner,
            };
            presetDialog.Show();
            presetDialog.UpdateLayout();
            var presetInput = Assert.IsInstanceOfType<ComboBox>(presetDialog.FindName("PresetInput"));
            presetInput.SelectedItem = presetInput.Items
                .OfType<CompressionPresetOption>()
                .Single(option => option.Name == "Mapped defaults");
            Assert.IsTrue(presetDialog.TryAddFiles([addition, replacement]));
            var explicitMapped = presetDialog.Metadata.Single(item =>
                item.Target.MappedFinalArchivePath == "addition.bin");
            explicitMapped.FinalAttributesText = "hidden";
            var presetDestination = Path.Combine(root, "preset-rebuild.zip");
            var presetDestinationInput = Assert.IsInstanceOfType<TextBox>(presetDialog.FindName("DestinationInput"));
            presetDestinationInput.Text = presetDestination;
            Assert.IsTrue(presetDialog.TryBuildSelection(out var presetSelection, out var presetError), presetError);
            Assert.IsNotNull(presetSelection);
            var explicitDirective = presetSelection!.Metadata!.Single(item =>
                item.Target.MappedFinalArchivePath == "addition.bin");
            Assert.AreEqual((ushort)0x0002, explicitDirective.AttributesLowWord);
            var defaultDirective = presetSelection.Metadata!.Single(item =>
                item.Target.MappedFinalArchivePath == "replacement.txt");
            Assert.AreEqual(presetTime, defaultDirective.LastWriteTime);
            Assert.AreEqual((ushort)0x0020, defaultDirective.AttributesLowWord);
            Assert.IsFalse(presetSelection.Metadata!.Any(item => item.Target.Kind == ArchiveRebuildMetadataTargetKind.RetainedEntryKey));
            presetDialog.Close();

            var rebuildSettings = AppUiSettings.Default with
            {
                ManagedCreation = AppUiSettings.Default.ManagedCreation with
                {
                    ZipRebuildPreset = ArchiveCompressionPreset.Maximum,
                },
                ShowAdvancedRebuildOptions = true,
            };
            var rebuildCapabilities = new GetZipWriteCapabilitiesResponse(
                CanCreate: true,
                CanRebuild: true,
                SupportsAdd: true,
                SupportsReplace: true,
                SupportsDelete: true,
                SupportsFileRename: true,
                SupportsTreeEdit: true,
                SupportsMetadata: true,
                CompressionPresets:
                [
                    ArchiveCompressionPreset.Fast,
                    ArchiveCompressionPreset.Maximum,
                ],
                MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
                MaximumSourceBytes: ProtocolConstants.MaximumZipCreationSourceBytes,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false);
            var settingsDialog = new RebuildArchiveDialog(
                source,
                [entry, retained],
                [],
                client: null,
                sessionId: null,
                presetStore: null,
                capabilities: rebuildCapabilities,
                uiSettings: rebuildSettings,
                settingsStatus: "Local UI settings are available.")
            {
                Owner = owner,
            };
            settingsDialog.Show();
            settingsDialog.UpdateLayout();
            Assert.AreEqual(
                ArchiveCompressionPreset.Maximum,
                Assert.IsInstanceOfType<CompressionPresetOption>(
                    Assert.IsInstanceOfType<ComboBox>(settingsDialog.FindName("PresetInput")).SelectedItem).Value);
            Assert.IsTrue(
                Assert.IsInstanceOfType<Expander>(settingsDialog.FindName("AdvancedMetadataExpander")).IsExpanded);
            StringAssert.Contains(
                Assert.IsInstanceOfType<TextBlock>(settingsDialog.FindName("ReviewSummaryText")).Text,
                "full guarded ZIP rebuild-to-copy");
            var settingsPolicy = Assert.IsInstanceOfType<TextBlock>(settingsDialog.FindName("DestinationPolicyText"));
            StringAssert.Contains(settingsPolicy.Text, "original archive and every source remain untouched");
            StringAssert.Contains(settingsPolicy.Text, "existing or raced targets are preserved");
            Assert.AreEqual(
                "Local UI settings are available.",
                Assert.IsInstanceOfType<TextBlock>(settingsDialog.FindName("PresetWarningText")).Text);

            var existingRebuild = Path.Combine(root, "existing-rebuild.zip");
            File.WriteAllText(existingRebuild, "preserve-rebuild");
            Assert.IsInstanceOfType<TextBox>(settingsDialog.FindName("DestinationInput")).Text = existingRebuild;
            Assert.IsFalse(settingsDialog.TryBuildSelection(out _, out var existingDestinationMessage));
            StringAssert.Contains(existingDestinationMessage, "new and absent");
            Assert.AreEqual("preserve-rebuild", File.ReadAllText(existingRebuild));
            Assert.AreEqual("source", File.ReadAllText(source));
            settingsDialog.Close();

            var refusedCapabilities = new GetZipWriteCapabilitiesResponse(
                CanCreate: false,
                CanRebuild: false,
                SupportsAdd: false,
                SupportsReplace: false,
                SupportsDelete: false,
                SupportsFileRename: false,
                SupportsTreeEdit: false,
                SupportsMetadata: false,
                CompressionPresets: [],
                MaximumEntries: 0,
                MaximumSourceBytes: 0,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false);
            var refusedCreate = new CreateArchiveDialog(
                supportsDirectoryCreation: false,
                presetStore: null,
                capabilities: refusedCapabilities)
            {
                Owner = owner,
            };
            refusedCreate.Show();
            refusedCreate.UpdateLayout();
            Assert.IsFalse(Assert.IsInstanceOfType<Button>(refusedCreate.FindName("AddFilesButton")).IsEnabled);
            Assert.IsFalse(Assert.IsInstanceOfType<Button>(refusedCreate.FindName("CreateArchiveButton")).IsEnabled);
            Assert.IsFalse(refusedCreate.TryAddFiles([addition]));
            refusedCreate.Close();

            var refusedRebuild = new RebuildArchiveDialog(
                source,
                [entry, retained],
                [],
                client: null,
                sessionId: null,
                presetStore: null,
                capabilities: refusedCapabilities)
            {
                Owner = owner,
            };
            refusedRebuild.Show();
            refusedRebuild.UpdateLayout();
            Assert.IsFalse(Assert.IsInstanceOfType<Button>(refusedRebuild.FindName("AddFilesButton")).IsEnabled);
            Assert.IsFalse(Assert.IsInstanceOfType<Button>(refusedRebuild.FindName("RebuildCopyButton")).IsEnabled);
            Assert.IsFalse(refusedRebuild.TryBuildSelection(out _, out var refusedMessage));
            StringAssert.Contains(refusedMessage, "does not support ZIP rebuild");
            refusedRebuild.Close();

        }
        finally
        {
            if (dialog?.IsVisible == true)
            {
                dialog.Close();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static T? FindVisualDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && predicate(typed))
            {
                return typed;
            }

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void AssertRecyclingGrid(DataGrid grid)
    {
        Assert.IsTrue(grid.EnableRowVirtualization);
        Assert.IsTrue(grid.EnableColumnVirtualization);
        Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(grid));
        Assert.AreEqual(
            VirtualizationMode.Recycling,
            VirtualizingPanel.GetVirtualizationMode(grid));
        Assert.IsTrue(ScrollViewer.GetCanContentScroll(grid));
    }
}
