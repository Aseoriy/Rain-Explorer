using System.IO;
using RainExplorer.ViewModels;

namespace RainExplorer.Services;

/// <summary>
/// A reversible file operation. <see cref="Invoke"/> performs the inverse and
/// returns an error (or null) plus a redo action that re-applies the original
/// (or null when redo isn't supported, e.g. for create/copy undos).
/// </summary>
public abstract class UndoAction
{
    /// <summary>Short human label, e.g. "Move", "Rename", "Copy".</summary>
    public abstract string Label { get; }

    /// <summary>Run the inverse. Returns (error, redo-action-or-null).</summary>
    public abstract (string? error, UndoAction? redo) Invoke();

    private protected static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);
}

/// <summary>Undo a move by moving each item back to its original folder.</summary>
public sealed class MoveAction : UndoAction
{
    // Each item currently sits at Cur; undo moves it into the folder of Home.
    private readonly List<(string Cur, string Home)> _items;
    public MoveAction(List<(string Cur, string Home)> items) => _items = items;

    public override string Label => _items.Count == 1 ? "Move" : $"Move ({_items.Count} items)";

    public override (string?, UndoAction?) Invoke()
    {
        var ops = new FileOperationsService();
        string? err = null;
        var redo = new List<(string Cur, string Home)>();
        foreach (var (cur, home) in _items)
        {
            if (!PathExists(cur)) continue;
            string homeParent = Path.GetDirectoryName(home.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
            if (homeParent.Length == 0) continue;
            string? e = ops.MoveInto(new[] { cur }, homeParent);
            if (e is not null) { err ??= e; continue; }
            redo.Add((Cur: home, Home: cur));   // now back at Home; redo moves it to Cur again
        }
        return (err, redo.Count > 0 ? new MoveAction(redo) : null);
    }
}

/// <summary>Undo a rename by renaming the item back to its previous name.</summary>
public sealed class RenameAction : UndoAction
{
    private readonly string _cur;    // path after the rename being undone
    private readonly string _home;   // path it should return to
    public RenameAction(string cur, string home) { _cur = cur; _home = home; }

    public override string Label => "Rename";

    public override (string?, UndoAction?) Invoke()
    {
        if (!PathExists(_cur)) return ("Item no longer exists.", null);
        var ops = new FileOperationsService();
        string? e = ops.Rename(_cur, Path.GetFileName(_home.TrimEnd(Path.DirectorySeparatorChar)));
        if (e is not null) return (e, null);
        return (null, new RenameAction(_home, _cur));
    }
}

/// <summary>Recycle a set of paths (the undo of a create/copy). Redo restores them from the bin.</summary>
public sealed class RecycleAction : UndoAction
{
    private readonly List<string> _paths;
    private readonly string _label;
    public RecycleAction(IEnumerable<string> paths, string label)
    {
        _paths = paths.ToList();
        _label = label;
    }

    public override string Label => _label;

    public override (string?, UndoAction?) Invoke()
    {
        var existing = _paths.Where(PathExists).ToList();
        if (existing.Count == 0) return (null, null);
        string? e = new FileOperationsService().Delete(existing);   // to the Recycle Bin
        if (e is not null) return (e, null);
        return (null, new RestoreFromBinAction(existing, _label));
    }
}

/// <summary>Restore a set of paths from the Recycle Bin (the undo of a delete). Redo recycles them again.</summary>
public sealed class RestoreFromBinAction : UndoAction
{
    private readonly List<string> _paths;
    private readonly string _label;
    public RestoreFromBinAction(IEnumerable<string> paths, string label)
    {
        _paths = paths.ToList();
        _label = label;
    }

    public override string Label => _label;

    public override (string?, UndoAction?) Invoke()
    {
        string? err = null;
        var restored = new List<string>();
        foreach (var p in _paths)
        {
            var (e, finalPath) = RecycleBinService.Restore(p);
            if (e is not null) { err ??= e; continue; }
            if (finalPath is not null) restored.Add(finalPath);
        }
        UndoAction? redo = restored.Count > 0 ? new RecycleAction(restored, _label) : null;
        return (err, redo);
    }
}

/// <summary>
/// Session undo/redo stack for file operations. Singleton + observable so the
/// title-bar Undo button can reflect availability and the next action's label.
/// </summary>
public sealed class UndoService : ObservableObject
{
    public static UndoService Instance { get; } = new();
    private const int Cap = 50;

    // Tail = top of stack.
    private readonly List<UndoAction> _undo = new();
    private readonly List<UndoAction> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoText => _undo.Count > 0 ? $"Undo {_undo[^1].Label} (Ctrl+Z)" : "Nothing to undo";
    public string RedoText => _redo.Count > 0 ? $"Redo {_redo[^1].Label} (Ctrl+Y)" : "Nothing to redo";

    /// <summary>Record a completed operation so it can be undone.</summary>
    public void Push(UndoAction action)
    {
        _undo.Add(action);
        if (_undo.Count > Cap) _undo.RemoveAt(0);
        _redo.Clear();
        Changed();
    }

    /// <summary>Undo the most recent operation. Returns an error message or null.</summary>
    public string? Undo() => Run(_undo, _redo);

    /// <summary>Redo the most recently undone operation. Returns an error message or null.</summary>
    public string? Redo() => Run(_redo, _undo);

    private string? Run(List<UndoAction> from, List<UndoAction> to)
    {
        if (from.Count == 0) return null;
        var action = from[^1];
        from.RemoveAt(from.Count - 1);

        var entry = ActivityService.Instance.Begin(
            ReferenceEquals(from, _undo) ? "Undo" : "Redo", action.Label, "undo");
        var (err, inverse) = action.Invoke();
        ActivityService.Instance.Complete(entry, err is null, err);

        if (inverse is not null) to.Add(inverse);
        Changed();
        return err;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
    }
}
