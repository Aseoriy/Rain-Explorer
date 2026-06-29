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
    private const int Cap = 60;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;

    public ObservableCollection<ActivityEntry> Items { get; } = new();

    private ActivityService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "activity.json");

        if (SettingsStore.Instance.Settings.RememberActivity) LoadPersisted();

        // React to the toggle: turning it on snapshots the current log; turning it
        // off resets the persisted file so nothing is restored next launch.
        SettingsStore.Instance.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.RememberActivity))
            {
                if (SettingsStore.Instance.Settings.RememberActivity) Persist();
                else { try { File.Delete(_path); } catch { } }
            }
        };
    }

    private bool Remember => SettingsStore.Instance.Settings.RememberActivity;

    private void LoadPersisted()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var saved = JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(_path), JsonOpts);
            if (saved is null) return;
            foreach (var e in saved.Take(Cap))
            {
                // An op that was still running when the app closed can never complete now.
                if (e.Status == ActivityStatus.Running) e.Status = ActivityStatus.Failed;
                Items.Add(e);
            }
        }
        catch { /* corrupt — start with an empty log */ }
    }

    private void Persist()
    {
        if (!Remember) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Items, JsonOpts)); }
        catch { /* best-effort */ }
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
            Persist();
        });
    }

    public void Clear() => OnUi(() =>
    {
        Items.Clear();
        UnseenCount = 0;
        OnPropertyChanged(nameof(IsEmpty));
        Persist();
    });

    private void Add(ActivityEntry e) => OnUi(() =>
    {
        Items.Insert(0, e);
        while (Items.Count > Cap) Items.RemoveAt(Items.Count - 1);
        UnseenCount++;
        OnPropertyChanged(nameof(IsEmpty));
        Persist();
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
