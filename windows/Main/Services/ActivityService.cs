using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using RainExplorer.Models;
using RainExplorer.ViewModels;

namespace RainExplorer.Services;

/// <summary>
/// Log of recent file actions (delete, copy, move, rename, compress…) shown in the
/// activity center. Each entry records success/failure and how long the operation
/// took. Persisted to %AppData%\RainExplorer\activity.json when the "Remember
/// activity" setting is on; otherwise it lives for the session only and resets on
/// each launch.
/// </summary>
public sealed class ActivityService : ObservableObject
{
    public static ActivityService Instance { get; } = new();
    internal const int BasicCap = 60;
    internal const int DetailedCap = 2000;
    private static int Cap => Detailed ? DetailedCap : BasicCap;
    private static bool Detailed => SettingsStore.Instance.Settings.DetailedActivityHistory;

    /// <summary>Drives the "View details" button in the activity center.</summary>
    public bool DetailedHistory => Detailed;
    private static readonly object DispatcherlessUiGate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
    private readonly string _path;
    private readonly object _persistGate = new();
    private bool _loadFailed;
    private bool _retryLoad;

    public ObservableCollection<ActivityEntry> Items { get; } = new();

    /// <summary>Requests that the owning window show the Activity flyout for a large operation.</summary>
    public event Action<Window?>? ShowRequested;

    private ActivityService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "activity.json");

        if (Remember) LoadPersisted();
        Prune();

        // React to the toggle: turning it on snapshots the current log; turning it
        // off resets the persisted file so nothing is restored next launch.
        SettingsStore.Instance.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.DetailedActivityHistory) or nameof(AppSettings.ActivityRetentionDays))
            {
                OnUi(() =>
                {
                    OnPropertyChanged(nameof(DetailedHistory));
                    Prune();
                    Persist();
                });
            }
            if (e.PropertyName is nameof(AppSettings.RememberActivity) or nameof(AppSettings.DetailedActivityHistory))
            {
                if (Remember) Persist();
                else
                {
                    try { File.Delete(_path); } catch { }
                    _loadFailed = false;
                    _retryLoad = false;
                }
            }
        };
    }

    // Detailed history is a saved log by definition, so it persists even with "Remember activity" off.
    private static bool Remember => SettingsStore.Instance.Settings.RememberActivity || Detailed;

    /// <summary>Drop finished entries past the retention period (detailed history) and over the cap.</summary>
    private void Prune()
    {
        PruneItems(Items, Detailed, SettingsStore.Instance.Settings.ActivityRetentionDays, DateTime.Now);
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal static void PruneItems(IList<ActivityEntry> items, bool detailed, int retentionDays, DateTime now)
    {
        if (detailed && retentionDays > 0)
        {
            DateTime cutoff = now.AddDays(-retentionDays);
            for (int i = items.Count - 1; i >= 0; i--)
                if (!items[i].IsActive && items[i].StartedAt < cutoff) items.RemoveAt(i);
        }
        int cap = detailed ? DetailedCap : BasicCap;
        while (items.Count > cap && !items[^1].IsActive) items.RemoveAt(items.Count - 1);
    }

    private void LoadPersisted()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var saved = JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(_path), JsonOpts);
            if (saved is null) return;
            foreach (var e in saved.Where(e => e is not null).Take(Cap))
            {
                // An op that was still running when the app closed can never complete now.
                if (e.Status == ActivityStatus.Running) e.Status = ActivityStatus.Failed;
                Items.Add(e);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _retryLoad = true;
        }
        catch
        {
            // Keep a recovery copy; a malformed activity file should not be silently
            // replaced by the next operation's save.
            _loadFailed = true;
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { }
        }
    }

    private void Persist()
    {
        if (!Remember || _loadFailed) return;
        lock (_persistGate)
        {
            if (_retryLoad && !TryMergeDeferredLoad()) return;

            string? tmp = null;
            try
            {
                tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    sw.Write(JsonSerializer.Serialize(Items, JsonOpts));
                    sw.Flush();
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
                tmp = null;
            }
            catch
            {
                if (tmp is not null) { try { File.Delete(tmp); } catch { } }
            }
        }
    }

    private bool TryMergeDeferredLoad()
    {
        try
        {
            if (File.Exists(_path))
            {
                var saved = JsonSerializer.Deserialize<List<ActivityEntry>>(
                    File.ReadAllText(_path), JsonOpts) ?? [];
                foreach (ActivityEntry entry in saved.Where(e => e is not null))
                {
                    if (Items.Count >= Cap) break;
                    bool duplicate = Items.Any(current =>
                        current.StartedAt == entry.StartedAt
                        && string.Equals(current.Title, entry.Title, StringComparison.Ordinal)
                        && string.Equals(current.Detail, entry.Detail, StringComparison.Ordinal));
                    if (duplicate) continue;
                    if (entry.Status == ActivityStatus.Running)
                        entry.Status = ActivityStatus.Failed;
                    Items.Add(entry);
                }
            }

            _retryLoad = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            _loadFailed = true;
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { }
            return false;
        }
    }

    private int _unseen;
    /// <summary>New entries since the flyout was last opened (drives the badge).</summary>
    public int UnseenCount
    {
        get => _unseen;
        private set { if (Set(ref _unseen, value)) OnPropertyChanged(nameof(HasUnseen)); }
    }
    public bool HasUnseen => _unseen > 0;

    public bool IsEmpty => Items.Count == 0;

    public void MarkSeen() => OnUi(() => UnseenCount = 0);

    /// <summary>Start a running activity; call <see cref="Complete"/> when the op finishes.</summary>
    public ActivityEntry Begin(string title, string detail, string iconKey)
    {
        var e = new ActivityEntry
        {
            Title = title,
            IconKey = iconKey,
            Detail = detail,
            StartedAt = DateTime.Now,
            BeginDetail = detail,
            Status = ActivityStatus.Running,
            Watch = Stopwatch.StartNew(),
        };
        Add(e);
        return e;
    }

    public void AttachControls(ActivityEntry e, Action cancel, Func<bool> togglePause) =>
        OnUi(() => e.AttachControls(cancel, togglePause));

    public void AttachCancel(ActivityEntry e, Action cancel) =>
        OnUi(() => e.AttachCancel(cancel));

    public void DetachControls(ActivityEntry e) => OnUi(e.DetachControls);

    public void RequestCancel(ActivityEntry e) => OnUi(e.RequestCancel);

    public void MarkCancellationPending(ActivityEntry e) => OnUi(e.MarkCancellationPending);

    public void RequestPauseToggle(ActivityEntry e) => OnUi(e.TogglePause);

    /// <summary>Update an active operation without writing progress to disk.</summary>
    public void ReportProgress(ActivityEntry e, double? progress, string? detail = null,
        string? progressText = null) => OnUi(() =>
    {
        if (!e.IsActive) return;
        e.Progress = progress is double value ? Math.Clamp(value, 0, 1) : -1;
        if (!string.IsNullOrWhiteSpace(detail)) e.Detail = Shorten(detail!);
        if (progressText is not null) e.ProgressText = progressText;
    });

    public void RequestShow(Window? owner) => OnUi(() => ShowRequested?.Invoke(owner));

    /// <summary>Apply only fixed, typed operation fields. Current-item text is ephemeral and never persisted.</summary>
    public void ReportOperation(ActivityEntry entry, ActivityOperationUpdate update) => OnUi(() =>
    {
        bool terminal = update.Phase is ActivityOperationPhase.Completed
            or ActivityOperationPhase.CompletedWithWarnings
            or ActivityOperationPhase.Cancelled
            or ActivityOperationPhase.Failed;
        if (!entry.IsActive && !terminal) return;

        entry.Progress = CalculateProgress(update, terminal);
        entry.ProgressText = BuildOperationProgressText(update);
        entry.Detail = entry.IsCancellationPending && !terminal
            ? "Cancellation requested; waiting for the final result."
            : FormatOperationDetail(update);

        if (!terminal) return;
        entry.Watch?.Stop();
        entry.FinishedAt = DateTime.Now;
        entry.DurationText = FormatDuration(update.Elapsed ?? entry.Watch?.Elapsed ?? TimeSpan.Zero);
        entry.IsPaused = false;
        entry.Status = update.Phase switch
        {
            ActivityOperationPhase.Completed or ActivityOperationPhase.CompletedWithWarnings =>
                ActivityStatus.Success,
            ActivityOperationPhase.Cancelled => ActivityStatus.Canceled,
            _ => ActivityStatus.Failed,
        };
        entry.DetachControls();
        KeepActiveAtTop();
        Persist();
        if (entry.Status == ActivityStatus.Failed) ShowIfFailuresShouldOpen();
    });

    public void Complete(ActivityEntry e, bool ok, string? error = null)
    {
        e.Watch?.Stop();
        OnUi(() =>
        {
            e.DurationText = FormatDuration(e.Watch?.Elapsed ?? TimeSpan.Zero);
            e.FinishedAt = DateTime.Now;
            if (!ok && !string.IsNullOrWhiteSpace(error))
            {
                e.Detail = Shorten(error!);
                if (Detailed) e.FullDetail = error!.Trim();
            }
            if (ok) e.Progress = 1;
            e.ProgressText = string.Empty;
            e.IsPaused = false;
            e.Status = ok ? ActivityStatus.Success : ActivityStatus.Failed;
            e.DetachControls();
            KeepActiveAtTop();
            Persist();
            if (!ok) ShowIfFailuresShouldOpen();
        });
    }

    private void ShowIfFailuresShouldOpen()
    {
        if (SettingsStore.Instance.Settings.OpenActivityOnFailure) ShowRequested?.Invoke(null);
    }

    /// <summary>
    /// Record something that failed outside a file operation, such as opening a folder. Only kept with
    /// detailed history on; otherwise the status bar message is enough.
    /// </summary>
    public void LogFailure(string title, string detail, string error, string iconKey)
    {
        if (!Detailed) return;
        var e = new ActivityEntry
        {
            Title = title,
            IconKey = iconKey,
            Detail = Shorten(error),
            BeginDetail = detail,
            FullDetail = error.Trim(),
            StartedAt = DateTime.Now,
            FinishedAt = DateTime.Now,
            Status = ActivityStatus.Failed,
        };
        Add(e);
        OnUi(ShowIfFailuresShouldOpen);
    }

    /// <summary>The whole history as readable text (Settings → Export activity history).</summary>
    public string ExportText()
    {
        var parts = new List<string>
        {
            $"Rain Explorer activity history — exported {DateTime.Now:yyyy-MM-dd HH:mm}",
            new string('=', 60),
        };
        foreach (ActivityEntry entry in Items.ToList())
        {
            parts.Add(ActivityExplainer.Describe(entry));
            parts.Add(new string('-', 60));
        }
        return string.Join(Environment.NewLine, parts) + Environment.NewLine;
    }

    /// <summary>Mark a running activity as cancelled (the user backed out of a confirm dialog,
    /// so nothing actually happened). Keeps it in the log so the outcome is visible.</summary>
    public void Cancel(ActivityEntry e, string? detail = null) => OnUi(() =>
    {
        e.Watch?.Stop();
        e.DurationText = FormatDuration(e.Watch?.Elapsed ?? TimeSpan.Zero);
        e.FinishedAt = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(detail)) e.Detail = Shorten(detail!);
        e.ProgressText = string.Empty;
        e.IsPaused = false;
        e.Status = ActivityStatus.Canceled;
        e.DetachControls();
        KeepActiveAtTop();
        Persist();
    });

    public void Clear() => OnUi(() =>
    {
        var active = Items.Where(e => e.IsActive).ToList();
        Items.Clear();
        foreach (var e in active) Items.Add(e);
        _loadFailed = false;
        _retryLoad = false;
        UnseenCount = 0;
        OnPropertyChanged(nameof(IsEmpty));
        Persist();
    });

    private void Add(ActivityEntry e) => OnUi(() =>
    {
        int insertAt = 0;
        while (insertAt < Items.Count && Items[insertAt].IsActive) insertAt++;
        Items.Insert(insertAt, e);
        Prune();
        UnseenCount++;
        OnPropertyChanged(nameof(IsEmpty));
        Persist();
    });

    private void KeepActiveAtTop()
    {
        int target = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            if (!Items[i].IsActive) continue;
            if (i != target) Items.Move(i, target);
            target++;
        }
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null)
        {
            lock (DispatcherlessUiGate) a();
        }
        else if (d.CheckAccess()) a();
        else d.Invoke(a);
    }

    private static string Shorten(string s) => s.Length > 140 ? s[..140] + "…" : s;

    private static double CalculateProgress(ActivityOperationUpdate update, bool terminal)
    {
        if (update.TotalBytes is > 0)
            return Math.Clamp((double)update.BytesProcessed / update.TotalBytes.Value, 0, 1);
        if (update.TotalEntries is > 0)
            return Math.Clamp((double)update.EntriesProcessed / update.TotalEntries.Value, 0, 1);
        return terminal && update.Phase is (ActivityOperationPhase.Completed
            or ActivityOperationPhase.CompletedWithWarnings) ? 1 : -1;
    }

    private static string BuildOperationProgressText(ActivityOperationUpdate update)
    {
        var parts = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(update.CurrentItem)) parts.Add(Shorten(update.CurrentItem!));
        if (update.TotalBytes is not null || update.BytesProcessed > 0)
            parts.Add(update.TotalBytes is > 0
                ? $"{FormatBytes(update.BytesProcessed)} / {FormatBytes(update.TotalBytes.Value)}"
                : FormatBytes(update.BytesProcessed));
        if (update.TotalEntries is not null || update.EntriesProcessed > 0)
            parts.Add(update.TotalEntries is > 0
                ? $"{Math.Max(0, update.EntriesProcessed):N0} / {update.TotalEntries.Value:N0} items"
                : $"{Math.Max(0, update.EntriesProcessed):N0} items");
        if (update.Elapsed is { } elapsed) parts.Add(FormatDuration(elapsed));
        if (update.BytesPerSecond is double throughput
            && double.IsFinite(throughput) && throughput > 0)
            parts.Add($"{FormatBytes((long)Math.Min(throughput, long.MaxValue))}/s");
        if (update.CompressionRatio is double ratio
            && double.IsFinite(ratio) && ratio >= 0)
            parts.Add($"ratio {ratio:0.###}");
        if (update.WarningCount > 0)
            parts.Add($"{update.WarningCount:N0} warning{(update.WarningCount == 1 ? string.Empty : "s")}");
        return string.Join(" · ", parts);
    }

    internal static string FormatOperationDetail(ActivityOperationUpdate update) => update.Phase switch
    {
        ActivityOperationPhase.Queued => "Queued by the archive worker.",
        ActivityOperationPhase.Validating => "Validating the current archive operation.",
        ActivityOperationPhase.WaitingForInput => "Waiting for input; no continuation control is available.",
        ActivityOperationPhase.Running => "Archive operation in progress.",
        ActivityOperationPhase.Finalizing => "Finalizing the archive operation.",
        ActivityOperationPhase.Completed => "Completed successfully.",
        ActivityOperationPhase.CompletedWithWarnings =>
            $"Completed with {update.WarningCount:N0} warning{(update.WarningCount == 1 ? string.Empty : "s")}.",
        ActivityOperationPhase.Cancelled => "Cancelled; no retry was started.",
        ActivityOperationPhase.Failed when update.Kind == ActivityOperationKind.ArchiveCreation
            && ArchiveCreationFailureText.Describe(update.Failure, update.FailureLimit) is { } detail => detail,
        ActivityOperationPhase.Failed when update.Failure == ActivityOperationFailure.DestinationConflict
            && update.Kind is ActivityOperationKind.Extraction or ActivityOperationKind.TransferOut =>
            ArchiveDestinationConflictText.Describe(update.DestinationConflictReason),
        ActivityOperationPhase.Failed => $"Failed ({FormatFailure(update.Failure)}); no retry was started.",
        _ => "Archive operation in progress.",
    };

    private static string FormatFailure(ActivityOperationFailure failure) => failure switch
    {
        ActivityOperationFailure.InvalidRequest => "invalid request",
        ActivityOperationFailure.Unsupported => "unsupported operation",
        ActivityOperationFailure.InvalidArchive => "invalid archive",
        ActivityOperationFailure.CorruptArchive => "corrupt archive",
        ActivityOperationFailure.PasswordRequired => "password required",
        ActivityOperationFailure.IncorrectPassword => "incorrect password",
        ActivityOperationFailure.UnsafePath => "unsafe archive path",
        ActivityOperationFailure.PathCollision => "archive path collision",
        ActivityOperationFailure.ResourceLimit => "resource limit",
        ActivityOperationFailure.SourceChanged => "source changed",
        ActivityOperationFailure.DestinationConflict => "destination conflict",
        ActivityOperationFailure.PermissionDenied => "permission denied",
        ActivityOperationFailure.InsufficientSpace => "insufficient space",
        ActivityOperationFailure.ProviderFailure => "archive provider failure",
        ActivityOperationFailure.WorkerUnavailable => "archive worker unavailable",
        ActivityOperationFailure.ProtocolMismatch => "protocol mismatch",
        ActivityOperationFailure.UnsafeOutput => "unsafe transfer output",
        _ => "unknown error",
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0} ms"
        : t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0} s"
        : $"{(int)t.TotalMinutes}m {t.Seconds}s";
}
