using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.Views;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

internal static class ManagedCreationSettingsWpfAssertions
{
    public static void Run()
    {
        var zip = ZipCapabilities();
        var tar = new GetTarWriteCapabilitiesResponse(
            CanCreate: true,
            CanRebuild: false,
            SupportsTreeCreation: true,
            CompressionPresets: [ArchiveCompressionPreset.Store],
            MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
        var managed = new GetManagedWriteCapabilitiesResponse(
        [
            Capability(
                ManagedArchiveCreationFormat.TarGZip,
                supportsTree: true,
                ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Maximum),
            Capability(
                ManagedArchiveCreationFormat.TarBZip2,
                supportsTree: true,
                ArchiveCompressionPreset.Balanced),
            Capability(
                ManagedArchiveCreationFormat.GZip,
                supportsTree: false,
                ArchiveCompressionPreset.Fast),
            Capability(
                ManagedArchiveCreationFormat.BZip2,
                supportsTree: false,
                ArchiveCompressionPreset.Maximum),
        ]);
        var cases = new[]
        {
            new FormatCase(ArchiveCreationFormat.Zip, ArchiveCompressionPreset.Maximum, "ZIP", SupportsDirectories: true),
            new FormatCase(ArchiveCreationFormat.Tar, ArchiveCompressionPreset.Store, "TAR", SupportsDirectories: true),
            new FormatCase(ArchiveCreationFormat.TarGZip, ArchiveCompressionPreset.Maximum, "TAR.GZ", SupportsDirectories: true),
            new FormatCase(ArchiveCreationFormat.TarBZip2, ArchiveCompressionPreset.Balanced, "TAR.BZ2", SupportsDirectories: true),
            new FormatCase(ArchiveCreationFormat.GZip, ArchiveCompressionPreset.Fast, "GZIP", SupportsDirectories: false),
            new FormatCase(ArchiveCreationFormat.BZip2, ArchiveCompressionPreset.Maximum, "BZIP2", SupportsDirectories: false),
        };

        foreach (var item in cases)
        {
            var dialog = new CreateArchiveDialog(
                supportsDirectoryCreation: true,
                presetStore: null,
                capabilities: zip,
                tarCapabilities: tar,
                managedCapabilities: managed,
                uiSettings: SettingsFor(item.Format, item.Preset),
                settingsStatus: "Local UI settings are available.");
            try
            {
                var selectedFormat = Assert.IsInstanceOfType<CreationFormatOption>(
                    Assert.IsInstanceOfType<ComboBox>(dialog.FindName("FormatInput")).SelectedItem);
                var selectedPreset = Assert.IsInstanceOfType<CompressionPresetOption>(
                    Assert.IsInstanceOfType<ComboBox>(dialog.FindName("PresetInput")).SelectedItem);
                Assert.AreEqual(item.Format, selectedFormat.Value);
                Assert.AreEqual(item.Preset, selectedPreset.Value);
                Assert.AreEqual(item.SupportsDirectories,
                    Assert.IsInstanceOfType<Button>(dialog.FindName("AddFolderButton")).IsEnabled);

                var review = Assert.IsInstanceOfType<TextBlock>(dialog.FindName("ReviewSummaryText"));
                StringAssert.Contains(review.Text, item.DisplayName);
                StringAssert.Contains(review.Text, selectedPreset.Name);
                var destinationPolicy = Assert.IsInstanceOfType<TextBlock>(dialog.FindName("DestinationPolicyText"));
                StringAssert.Contains(destinationPolicy.Text, "distinct, and absent");
                StringAssert.Contains(destinationPolicy.Text, "Existing or raced targets are preserved");
                if (!item.SupportsDirectories)
                {
                    StringAssert.Contains(
                        Assert.IsInstanceOfType<TextBlock>(dialog.FindName("LimitationsText")).Text,
                        "exactly one ordinary file");
                }
            }
            finally
            {
                dialog.Close();
            }
        }

        var clamped = new CreateArchiveDialog(
            supportsDirectoryCreation: true,
            presetStore: null,
            capabilities: zip,
            tarCapabilities: tar,
            managedCapabilities: managed,
            uiSettings: SettingsFor(ArchiveCreationFormat.GZip, ArchiveCompressionPreset.Maximum));
        try
        {
            var clampedPreset = Assert.IsInstanceOfType<CompressionPresetOption>(
                Assert.IsInstanceOfType<ComboBox>(clamped.FindName("PresetInput")).SelectedItem);
            Assert.AreEqual(ArchiveCompressionPreset.Fast, clampedPreset.Value,
                "A saved hint must be clamped to the current authenticated capability response.");
        }
        finally
        {
            clamped.Close();
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.ManagedCreationSettingsWpfTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.txt");
        var existingDestination = Path.Combine(root, "existing.zip");
        File.WriteAllText(source, "source");
        File.WriteAllText(existingDestination, "preserve-me");
        var refusal = new CreateArchiveDialog(
            supportsDirectoryCreation: true,
            presetStore: null,
            capabilities: zip,
            tarCapabilities: tar,
            managedCapabilities: managed,
            uiSettings: SettingsFor(ArchiveCreationFormat.Zip, ArchiveCompressionPreset.Balanced));
        try
        {
            Assert.IsTrue(refusal.TryAddFiles([source]));
            Assert.IsInstanceOfType<TextBox>(refusal.FindName("DestinationInput")).Text = existingDestination;
            refusal.Show();
            refusal.UpdateLayout();
            Assert.IsInstanceOfType<Button>(refusal.FindName("CreateArchiveButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StringAssert.Contains(
                Assert.IsInstanceOfType<TextBlock>(refusal.FindName("ValidationMessage")).Text,
                "new and absent");
            Assert.AreEqual("preserve-me", File.ReadAllText(existingDestination));
            Assert.IsNull(refusal.Selection);
        }
        finally
        {
            refusal.Close();
            Directory.Delete(root, recursive: true);
        }
    }

    private static AppUiSettings SettingsFor(
        ArchiveCreationFormat format,
        ArchiveCompressionPreset preset)
    {
        var defaults = AppUiSettings.Default.ManagedCreation with
        {
            DefaultFormat = format,
            ZipPreset = format == ArchiveCreationFormat.Zip ? preset : AppUiSettings.Default.ManagedCreation.ZipPreset,
            TarPreset = format == ArchiveCreationFormat.Tar ? preset : AppUiSettings.Default.ManagedCreation.TarPreset,
            TarGZipPreset = format == ArchiveCreationFormat.TarGZip ? preset : AppUiSettings.Default.ManagedCreation.TarGZipPreset,
            TarBZip2Preset = format == ArchiveCreationFormat.TarBZip2 ? preset : AppUiSettings.Default.ManagedCreation.TarBZip2Preset,
            GZipPreset = format == ArchiveCreationFormat.GZip ? preset : AppUiSettings.Default.ManagedCreation.GZipPreset,
            BZip2Preset = format == ArchiveCreationFormat.BZip2 ? preset : AppUiSettings.Default.ManagedCreation.BZip2Preset,
        };
        return AppUiSettings.Default with { ManagedCreation = defaults };
    }

    private static GetZipWriteCapabilitiesResponse ZipCapabilities() => new(
        CanCreate: true,
        CanRebuild: true,
        SupportsAdd: true,
        SupportsReplace: true,
        SupportsDelete: true,
        SupportsFileRename: true,
        SupportsTreeEdit: true,
        SupportsMetadata: true,
        CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
        MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
        MaximumSourceBytes: ProtocolConstants.MaximumZipCreationSourceBytes,
        SupportsEncryption: false,
        SupportsFilenameEncryption: false,
        SupportsSplitOutput: false,
        SupportsExistingDestinationReplacement: false,
        SupportsInPlaceUpdate: false);

    private static ManagedWriteCapability Capability(
        ManagedArchiveCreationFormat format,
        bool supportsTree,
        params ArchiveCompressionPreset[] presets) => new(
            format,
            CanCreate: true,
            SupportsTreeCreation: supportsTree,
            CanRebuild: false,
            CompressionPresets: presets,
            MaximumEntries: 4096,
            MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

    private sealed record FormatCase(
        ArchiveCreationFormat Format,
        ArchiveCompressionPreset Preset,
        string DisplayName,
        bool SupportsDirectories);
}
