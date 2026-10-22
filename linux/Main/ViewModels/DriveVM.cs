using RainExplorer.Services;

namespace RainExplorer.ViewModels;

/// <summary>A drive shown on the Home/Drives pages: usage, free space, optional file count.</summary>
public sealed class DriveVM : ObservableObject
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    public string IconKey { get; init; } = "hard-drive";
    public MountEntry? Drive { get; init; }
    public bool IsMounted => Drive?.IsMounted ?? true;
    public bool HasAction => Drive?.HasAction == true;
    public string ActionText => Drive?.ActionText ?? "";
    public string ActionIcon => Drive?.CanMount == true ? "plus" : "arrow-up";
    public string Tooltip => Drive is { IsMounted: false } d ? d.Device + " · Not mounted" : Path;
    public string TypeText { get; init; } = "";

    /// <summary>0–100 percent used (drives the usage bar).</summary>
    private double _usedPercent;
    public double UsedPercent { get => _usedPercent; set => Set(ref _usedPercent, value); }
    private string _usageText = "";
    public string UsageText { get => _usageText; set => Set(ref _usageText, value); }   // "120 GB of 500 GB used"
    private string _freeText = "";
    public string FreeText { get => _freeText; set => Set(ref _freeText, value); }     // "380 GB free"

    /// <summary>Compact one-line summary for small cards ("380 GB free of 500 GB").</summary>
    private string _summaryText = "";
    public string SummaryText { get => _summaryText; set => Set(ref _summaryText, value); }

    private string _countText = "";
    /// <summary>Optional recursive file count, filled in lazily when enabled.</summary>
    public string CountText { get => _countText; set => Set(ref _countText, value); }
}
