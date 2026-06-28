namespace RainExplorer.ViewModels;

/// <summary>A drive shown on the Home/Drives pages: usage, free space, optional file count.</summary>
public sealed class DriveVM : ObservableObject
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    public string IconKey { get; init; } = "hard-drive";
    public string TypeText { get; init; } = "";

    /// <summary>0–100 percent used (drives the usage bar).</summary>
    public double UsedPercent { get; init; }
    public string UsageText { get; init; } = "";   // "120 GB of 500 GB used"
    public string FreeText { get; init; } = "";     // "380 GB free"

    private string _countText = "";
    /// <summary>Optional recursive file count, filled in lazily when enabled.</summary>
    public string CountText { get => _countText; set => Set(ref _countText, value); }
}
