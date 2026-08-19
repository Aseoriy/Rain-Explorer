using System.Text.Json.Serialization;
using RainExplorer.ViewModels;

namespace RainExplorer.Models;

public enum ActivityStatus { Running, Success, Failed, Canceled }

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
        set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(ShowProgress));
            OnPropertyChanged(nameof(ShowDeterminateProgress));
            OnPropertyChanged(nameof(ShowIndeterminateProgress));
        }
    }

    private string _durationText = string.Empty;
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }

    private double _progress = -1;
    /// <summary>Ephemeral progress from 0 to 1; -1 means indeterminate/not applicable.</summary>
    [JsonIgnore]
    public double Progress
    {
        get => _progress;
        set
        {
            if (!Set(ref _progress, value)) return;
            OnPropertyChanged(nameof(HasProgress));
            OnPropertyChanged(nameof(ShowDeterminateProgress));
            OnPropertyChanged(nameof(ShowIndeterminateProgress));
        }
    }

    private bool _isPaused;
    [JsonIgnore]
    public bool IsPaused
    {
        get => _isPaused;
        internal set
        {
            if (!Set(ref _isPaused, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PauseButtonText));
        }
    }

    [JsonIgnore]
    public bool IsActive => _status == ActivityStatus.Running;

    /// <summary>Running activities stay at the top of the activity flyout.</summary>
    [JsonIgnore]
    public bool IsPinned => IsActive;

    [JsonIgnore]
    public bool HasProgress => _progress >= 0;

    [JsonIgnore]
    public bool ShowProgress => IsActive;

    [JsonIgnore]
    public bool ShowDeterminateProgress => ShowProgress && HasProgress;

    [JsonIgnore]
    public bool ShowIndeterminateProgress => ShowProgress && !HasProgress;

    [JsonIgnore]
    public bool CanCancel => IsActive && !_cancelRequested && _cancelAction is not null;

    [JsonIgnore]
    public bool CanPause => IsActive && !_cancelRequested && _togglePauseAction is not null;

    [JsonIgnore]
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    [JsonIgnore]
    private Action? _cancelAction;

    [JsonIgnore]
    private Func<bool>? _togglePauseAction;

    [JsonIgnore]
    private bool _cancelRequested;

    [JsonIgnore]
    public string StatusText => _status switch
    {
        ActivityStatus.Running when IsPaused => "Paused",
        ActivityStatus.Running => "Working…",
        ActivityStatus.Success => "Done",
        ActivityStatus.Canceled => "Cancelled",
        _ => "Failed",
    };

    [JsonIgnore]
    public string TimeText => StartedAt.ToString("h:mm tt");

    /// <summary>Wall-clock timer; not data-bound, not persisted.</summary>
    [JsonIgnore]
    internal System.Diagnostics.Stopwatch? Watch { get; set; }

    internal void AttachControls(Action cancel, Func<bool> togglePause)
    {
        _cancelAction = cancel;
        _togglePauseAction = togglePause;
        _cancelRequested = false;
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanPause));
    }

    internal void DetachControls()
    {
        _cancelAction = null;
        _togglePauseAction = null;
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanPause));
    }

    internal void RequestCancel()
    {
        if (!CanCancel) return;
        _cancelRequested = true;
        Detail = "Canceling…";
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanPause));
        _cancelAction?.Invoke();
    }

    internal void TogglePause()
    {
        if (!CanPause || _togglePauseAction is null) return;
        IsPaused = _togglePauseAction();
    }
}
