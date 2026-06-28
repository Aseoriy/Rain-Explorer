using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using RainExplorer.Models;
using RainExplorer.ViewModels;

namespace RainExplorer.Services;

/// <summary>
/// In-memory log of recent file actions (delete, copy, move, rename, compress…)
/// shown in the activity center. Each entry records success/failure and how long
/// the operation took. Lives for the session only.
/// </summary>
public sealed class ActivityService : ObservableObject
{
    public static ActivityService Instance { get; } = new();
    private const int Cap = 60;

    public ObservableCollection<ActivityEntry> Items { get; } = new();

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
            Status = ActivityStatus.Running,
            Watch = Stopwatch.StartNew(),
        };
        Add(e);
        return e;
    }

    public void Complete(ActivityEntry e, bool ok, string? error = null)
    {
        e.Watch?.Stop();
        OnUi(() =>
        {
            e.DurationText = FormatDuration(e.Watch?.Elapsed ?? TimeSpan.Zero);
            if (!ok && !string.IsNullOrWhiteSpace(error)) e.Detail = Shorten(error!);
            e.Status = ok ? ActivityStatus.Success : ActivityStatus.Failed;
        });
    }

    public void Clear() => OnUi(() =>
    {
        Items.Clear();
        UnseenCount = 0;
        OnPropertyChanged(nameof(IsEmpty));
    });

    private void Add(ActivityEntry e) => OnUi(() =>
    {
        Items.Insert(0, e);
        while (Items.Count > Cap) Items.RemoveAt(Items.Count - 1);
        UnseenCount++;
        OnPropertyChanged(nameof(IsEmpty));
    });

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.Invoke(a);
    }

    private static string Shorten(string s) => s.Length > 140 ? s[..140] + "…" : s;

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0} ms"
        : t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0} s"
        : $"{(int)t.TotalMinutes}m {t.Seconds}s";
}
