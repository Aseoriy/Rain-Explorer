using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

public partial class RebuildArchiveDialog : Window
{
    private readonly ArchiveRebuildCapabilitySnapshot _capabilities;

    public RebuildArchiveDialog(
        ArchiveRebuildCapabilitySnapshot capabilities,
        string suggestedDestination)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
        InitializeComponent();
        PresetInput.ItemsSource = capabilities.CompressionPresets;
        PresetInput.SelectedItem = capabilities.CompressionPresets.Contains(
            ArchiveCompressionPreset.Balanced)
            ? ArchiveCompressionPreset.Balanced
            : capabilities.CompressionPresets.FirstOrDefault();
        DestinationInput.Text = suggestedDestination;
        Loaded += (_, _) => PresetInput.Focus();
    }

    public ArchiveRebuildRequest? Selection { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose a distinct ZIP rebuild copy",
            AddExtension = true,
            CheckPathExists = true,
            CreatePrompt = false,
            DefaultExt = ".zip",
            Filter = "ZIP archive|*.zip",
            FilterIndex = 1,
            OverwritePrompt = false,
        };
        try
        {
            string current = Path.GetFullPath(DestinationInput.Text.Trim());
            string? parent = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                dialog.InitialDirectory = parent;
            dialog.FileName = Path.GetFileName(current);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
        }
        if (dialog.ShowDialog(this) == true) DestinationInput.Text = dialog.FileName;
    }

    private void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        if (!_capabilities.IsReady
            || PresetInput.SelectedItem is not ArchiveCompressionPreset preset
            || !_capabilities.CompressionPresets.Contains(preset)
            || !ArchiveWriteValidation.TryValidateOutputDestination(
                DestinationInput.Text.Trim(),
                ".zip",
                [new ArchiveCreationSource(
                    ArchiveCreationSourceKind.File,
                    _capabilities.SourcePath,
                    Path.GetFileName(_capabilities.SourcePath))],
                out string canonical,
                out _))
        {
            ValidationText.Text =
                "Choose an advertised preset and a distinct, absent .zip destination under an ordinary existing folder.";
            return;
        }
        Selection = new ArchiveRebuildRequest(_capabilities, canonical, preset);
        DialogResult = true;
    }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
