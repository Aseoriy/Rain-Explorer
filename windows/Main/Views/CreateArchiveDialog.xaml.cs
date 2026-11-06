using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

public partial class CreateArchiveDialog : Window
{
    private readonly ArchiveCreationCapabilitySnapshot _capabilities;
    private readonly IReadOnlyList<ArchiveCreationSource> _sources;
    private readonly IReadOnlyList<ArchiveCreationFormatChoice> _formatChoices;
    private bool _changingFormat;

    public CreateArchiveDialog(
        ArchiveCreationCapabilitySnapshot capabilities,
        IReadOnlyList<ArchiveCreationSource> sources,
        string suggestedDestination)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(sources);
        _capabilities = capabilities;
        _sources = sources.ToArray();
        _formatChoices = FormatChoices(capabilities, sources);
        InitializeComponent();
        SourcesList.ItemsSource = _sources.Select(source => new SourceReviewItem(
            source.Kind == ArchiveCreationSourceKind.Directory ? "Folder" : "File",
            Bound(UntrustedDisplayText.Escape(source.ArchivePath), 100),
            Bound(UntrustedDisplayText.Escape(source.SourcePath), 220))).ToArray();
        FormatInput.ItemsSource = _formatChoices;
        FormatInput.SelectedItem = _formatChoices.FirstOrDefault(choice => choice.IsCompatible);
        DestinationInput.Text = suggestedDestination;
        ApplyFormat(updateDestination: true);
        Loaded += (_, _) => FormatInput.Focus();
    }

    public ArchiveCreationRequest? Selection { get; private set; }

    private void ApplyCapabilitySummary(ArchiveCreationFormatOption format)
    {
        CapabilitySummaryText.Text = format.Format switch
        {
            ArchiveWriteFormat.Rar => "RAR uses your separately installed, verified WinRAR encoder.",
            ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2 =>
                "Compresses one file only. To include folders, choose ZIP or a TAR format.",
            ArchiveWriteFormat.Tar => "TAR bundles files and folders without compression.",
            _ => "Includes selected files and everything inside selected folders.",
        };
        int maximumEntries = _sources.Any(source => source.Kind == ArchiveCreationSourceKind.Directory)
            ? format.MaximumTreeEntries : format.MaximumEntries;
        string entries = format.Format is ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2
            ? "One file"
            : $"Up to {maximumEntries:N0} files and folders";
        CapabilityLimitText.Text =
            $"{entries} · {format.MaximumSourceBytes / (1024d * 1024 * 1024):0.##} GiB total. Existing archives are never overwritten.";
    }

    public static IReadOnlyList<ArchiveCreationFormatOption> CompatibleFormats(
        ArchiveCreationCapabilitySnapshot capabilities,
        IReadOnlyList<ArchiveCreationSource> sources)
    {
        if (!capabilities.IsReady || sources.Count == 0)
            return Array.Empty<ArchiveCreationFormatOption>();
        bool hasDirectory = sources.Any(source => source.Kind == ArchiveCreationSourceKind.Directory);
        bool rawShape = sources.Count == 1
            && sources[0].Kind == ArchiveCreationSourceKind.File;
        return capabilities.Formats.Where(option =>
                sources.Count <= option.MaximumEntries
                && (!hasDirectory || option.SupportsDirectories)
                && (option.Format is not (ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2)
                    || rawShape))
            .ToArray();
    }

    internal static IReadOnlyList<ArchiveCreationFormatChoice> FormatChoices(
        ArchiveCreationCapabilitySnapshot capabilities,
        IReadOnlyList<ArchiveCreationSource> sources)
    {
        if (!capabilities.IsReady || sources.Count == 0)
            return Array.Empty<ArchiveCreationFormatChoice>();

        IReadOnlySet<ArchiveWriteFormat> compatible = CompatibleFormats(capabilities, sources)
            .Select(option => option.Format)
            .ToHashSet();
        var choices = capabilities.Formats.Select(option =>
        {
            bool isCompatible = compatible.Contains(option.Format);
            bool singleFileOnly = option.Format is ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2;
            string availability = isCompatible
                ? "Available for this selection"
                : singleFileOnly
                    ? "Requires exactly one ordinary file"
                    : "Unavailable for this selection";
            string displayName = isCompatible
                ? option.Name
                : $"{option.Name} — {availability}";
            return new ArchiveCreationFormatChoice(option, displayName, availability, isCompatible);
        }).ToList();

        if (!capabilities.Formats.Any(option => option.Format == ArchiveWriteFormat.Rar)
            && capabilities.RarAvailability is { } rarAvailability)
        {
            string availability = rarAvailability switch
            {
                RarEncoderAvailability.UpdateRequired => "Requires WinRAR 7.23 or later",
                RarEncoderAvailability.NotInstalled => "Requires a separate WinRAR installation",
                RarEncoderAvailability.UntrustedBinary => "Installed WinRAR could not be verified",
                RarEncoderAvailability.UnsupportedPlatform => "Requires 64-bit Windows",
                RarEncoderAvailability.RestartRequired => "Restart Rain Explorer to enable RAR creation",
                _ => "Unavailable from the connected Worker",
            };
            var unavailableRar = new ArchiveCreationFormatOption(
                ArchiveWriteFormat.Rar,
                "RAR",
                ".rar",
                SupportsDirectories: true,
                Enum.GetValues<ArchiveCompressionPreset>(),
                ProtocolConstants.MaximumRarCreationEntries,
                ProtocolConstants.MaximumRarCreationSourceBytes);
            choices.Add(new ArchiveCreationFormatChoice(
                unavailableRar,
                $"RAR — {availability}",
                availability,
                IsCompatible: false));
        }

        return choices;
    }

    private ArchiveCreationFormatOption? SelectedFormat =>
        (FormatInput.SelectedItem as ArchiveCreationFormatChoice)?.Option;

    private void FormatInput_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_changingFormat) return;
        ApplyFormat(updateDestination: true);
    }

    private void ApplyFormat(bool updateDestination)
    {
        ArchiveCreationFormatOption? format = SelectedFormat;
        if (format is null)
        {
            PresetInput.ItemsSource = null;
            CapabilitySummaryText.Text = "No creation format is available for this selection.";
            CapabilityLimitText.Text = "Unavailable formats show the reason in the format list.";
            ReviewText.Text = "Choose a compatible format to continue.";
            CreateArchiveButton.IsEnabled = false;
            return;
        }
        CreateArchiveButton.IsEnabled = true;
        ApplyCapabilitySummary(format);

        ArchiveCreationFormatOption[] all = _capabilities.Formats.ToArray();
        if (updateDestination)
        {
            string current = DestinationInput.Text;
            ArchiveCreationFormatOption? previous = all
                .OrderByDescending(option => option.Extension.Length)
                .FirstOrDefault(option => current.EndsWith(
                    option.Extension, StringComparison.OrdinalIgnoreCase));
            if (previous is not null && previous.Format != format.Format)
                DestinationInput.Text = current[..^previous.Extension.Length] + format.Extension;
            else if (!current.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase))
                DestinationInput.Text = current + format.Extension;
        }

        _changingFormat = true;
        try
        {
            PresetInput.ItemsSource = format.CompressionPresets;
            PresetInput.SelectedItem = format.CompressionPresets.Contains(
                ArchiveCompressionPreset.Balanced)
                ? ArchiveCompressionPreset.Balanced
                : format.CompressionPresets.FirstOrDefault();
        }
        finally
        {
            _changingFormat = false;
        }

        int files = _sources.Count(source => source.Kind == ArchiveCreationSourceKind.File);
        int folders = _sources.Count - files;
        ReviewText.Text =
            $"{files:N0} selected file(s), {folders:N0} selected folder(s) → new {format.Name} archive. Originals stay untouched.";
        ValidationText.Text = string.Empty;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        ArchiveCreationFormatOption? format = SelectedFormat;
        if (format is null) return;
        var dialog = new SaveFileDialog
        {
            Title = $"Choose a new {format.Name} archive",
            AddExtension = true,
            CheckPathExists = true,
            CreatePrompt = false,
            DefaultExt = format.Extension,
            Filter = $"{format.Name} archive|*{format.Extension}",
            FilterIndex = 1,
            OverwritePrompt = false,
        };
        ApplyCurrentDestination(dialog);
        if (dialog.ShowDialog(this) == true) DestinationInput.Text = dialog.FileName;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ArchiveCreationFormatOption? format = SelectedFormat;
        if (format is null || PresetInput.SelectedItem is not ArchiveCompressionPreset preset)
        {
            ValidationText.Text = "Choose a Worker-advertised format and compression preset.";
            return;
        }
        var request = new ArchiveCreationRequest(
            DestinationInput.Text.Trim(),
            _sources,
            format.Format,
            preset);
        if (!ArchiveWriteValidation.TryValidateCreationRequest(
                request, format, out string canonicalDestination))
        {
            ValidationText.Text =
                "Choose a distinct, absent destination under an ordinary existing folder using the selected format extension.";
            return;
        }
        Selection = request with { DestinationPath = canonicalDestination };
        DialogResult = true;
    }

    private void ApplyCurrentDestination(SaveFileDialog dialog)
    {
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
    }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";

    private sealed record SourceReviewItem(string Kind, string ArchivePath, string SourcePath);
}

internal sealed record ArchiveCreationFormatChoice(
    ArchiveCreationFormatOption Option,
    string DisplayName,
    string Availability,
    bool IsCompatible);
