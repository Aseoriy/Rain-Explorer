using System.Text.Json.Serialization;
using RainExplorer.ViewModels;

namespace RainExplorer.Models;

public enum ActivityStatus { Running, Success, Failed }

/// <summary>One logged file action shown in the activity center (top-right flyout).</summary>
public sealed class ActivityEntry : ObservableObject
{
    public required string Title { get; init; }
    public required string IconKey { get; init; }
    public DateTime StartedAt { get; init; }

    private string _detail = string.Empty;
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private ActivityStatus _status;
    public ActivityStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    private string _durationText = string.Empty;
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }

    [JsonIgnore]
    public string StatusText => _status switch
    {
        ActivityStatus.Running => "Working…",
        ActivityStatus.Success => "Done",
        _ => "Failed",
    };

    [JsonIgnore]
    public string TimeText => StartedAt.ToString("h:mm tt");

    /// <summary>Wall-clock timer; not data-bound, not persisted.</summary>
    [JsonIgnore]
    internal System.Diagnostics.Stopwatch? Watch { get; set; }
}
