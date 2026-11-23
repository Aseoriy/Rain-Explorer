using Avalonia.Input;

namespace RainExplorer.Services;

/// <summary>
/// One keyboard action: its stable id (saved in settings), where it shows up in Settings → Shortcuts, its
/// default keys, and whether it may fire while a text box has focus.
/// </summary>
public sealed record ShortcutAction(string Id, string Group, string Name, string[] Defaults, bool WorksWhileTyping);

/// <summary>
/// Every keyboard shortcut in Rain, with user overrides on top (Settings → Shortcuts). Defaults match the
/// shortcuts Rain always had. Overrides live in <see cref="Models.AppSettings.ShortcutOverrides"/> as
/// id → gestures joined by "|" (an empty string turns the action off). While typing in a text box, only
/// actions marked <see cref="ShortcutAction.WorksWhileTyping"/> fire, and only with Ctrl/Alt or F-keys, so
/// typing letters, Space, Backspace or Delete never triggers anything.
/// </summary>
public static class ShortcutService
{
    public static readonly IReadOnlyList<ShortcutAction> Actions =
    [
        // Tabs & panes
        new("tab.new", "Tabs & panes", "New tab", ["Ctrl+T"], true),
        new("tab.close", "Tabs & panes", "Close tab", ["Ctrl+W"], true),
        new("tab.reopen", "Tabs & panes", "Reopen closed tab", ["Ctrl+Shift+T"], true),
        new("tab.next", "Tabs & panes", "Next tab", ["Ctrl+Tab", "Ctrl+PageDown"], true),
        new("tab.prev", "Tabs & panes", "Previous tab", ["Ctrl+Shift+Tab", "Ctrl+PageUp"], true),
        new("view.split", "Tabs & panes", "Split view", ["F3"], true),

        // Navigation
        new("nav.back", "Navigation", "Back", ["Alt+Left"], true),
        new("nav.forward", "Navigation", "Forward", ["Alt+Right"], true),
        new("nav.up", "Navigation", "Up one folder", ["Alt+Up", "Back"], true),
        new("nav.home", "Navigation", "Home folder", ["Alt+Home"], true),
        new("nav.refresh", "Navigation", "Refresh", ["F5", "Ctrl+R"], true),
        new("nav.address", "Navigation", "Edit the address", ["Ctrl+L", "F6", "Alt+D"], true),
        new("nav.search", "Navigation", "Search this folder", ["Ctrl+F"], true),

        // Files
        new("file.open", "Files", "Open", ["Enter"], false),
        new("file.copy", "Files", "Copy", ["Ctrl+C"], false),
        new("file.cut", "Files", "Cut", ["Ctrl+X"], false),
        new("file.paste", "Files", "Paste", ["Ctrl+V"], false),
        new("file.copyPath", "Files", "Copy as path", ["Ctrl+Shift+C"], false),
        new("file.selectAll", "Files", "Select all", ["Ctrl+A"], false),
        new("file.rename", "Files", "Rename", ["F2"], false),
        new("file.batchRename", "Files", "Rename several files", ["Shift+F2"], false),
        new("file.delete", "Files", "Delete", ["Delete"], false),
        new("file.deletePermanent", "Files", "Delete permanently", ["Shift+Delete"], false),
        new("file.newFolder", "Files", "New folder", ["Ctrl+Shift+N"], true),
        new("file.properties", "Files", "Properties", ["Alt+Enter"], false),
        new("file.contextMenu", "Files", "Open the right-click menu", ["Shift+F10", "Apps"], false),
        new("edit.undo", "Files", "Undo", ["Ctrl+Z"], false),
        new("edit.redo", "Files", "Redo", ["Ctrl+Y", "Ctrl+Shift+Z"], false),

        // View
        new("view.preview", "View", "Preview pane", ["Space"], false),
        new("view.hidden", "View", "Show hidden files", ["Ctrl+H"], true),
        new("view.sidebar", "View", "Sidebar", ["Ctrl+B"], true),

        // Rain
        new("app.settings", "Rain", "Settings", ["Ctrl+OemComma"], true),
        new("app.activity", "Rain", "Activity center", ["Ctrl+Shift+A"], true),
        new("app.commandBox", "Rain", "Command box", ["Ctrl+OemTilde"], true),
        new("app.terminal", "Rain", "Open terminal here", ["Shift+F4"], true),
        new("app.shortcuts", "Rain", "Keyboard shortcuts", ["F1", "Shift+OemQuestion"], true),
    ];

    private static readonly Dictionary<string, ShortcutAction> ById = Actions.ToDictionary(a => a.Id, StringComparer.Ordinal);

    /// <summary>True while Settings → Shortcuts waits for a new key; shortcuts don't fire meanwhile.</summary>
    public static bool IsRecording { get; set; }

    /// <summary>Receives the next key while recording (the window forwards every key press here).</summary>
    public static Action<KeyEventArgs>? Recorder { get; set; }

    /// <summary>Raised after any shortcut changes (menus and the shortcuts sheet refresh their text).</summary>
    public static event Action? Changed;

    private static Dictionary<string, string> Overrides => SettingsStore.Instance.Settings.ShortcutOverrides;

    public static ShortcutAction? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>The keys currently bound to an action (defaults unless overridden; empty when turned off).</summary>
    public static IReadOnlyList<KeyGesture> GesturesFor(string id)
    {
        if (!ById.TryGetValue(id, out var action)) return [];
        string[] texts = Overrides.TryGetValue(id, out string? custom)
            ? custom.Split('|', StringSplitOptions.RemoveEmptyEntries)
            : action.Defaults;
        var list = new List<KeyGesture>();
        foreach (string text in texts)
            if (TryParse(text, out var gesture)) list.Add(gesture);
        return list;
    }

    public static bool IsCustomized(string id) => Overrides.ContainsKey(id);

    /// <summary>The first key for an action, for menu hints; null when it has none.</summary>
    public static KeyGesture? PrimaryGesture(string id) => GesturesFor(id).FirstOrDefault();

    /// <summary>"Ctrl+T, Ctrl+PageDown" (or "None").</summary>
    public static string DescribeAction(string id)
    {
        var gestures = GesturesFor(id);
        return gestures.Count == 0 ? "None" : string.Join(", ", gestures.Select(Describe));
    }

    /// <summary>The action a key press triggers, or null.</summary>
    public static ShortcutAction? Match(KeyEventArgs e, bool typing)
    {
        foreach (var action in Actions)
        {
            if (typing && !action.WorksWhileTyping) continue;
            foreach (var gesture in GesturesFor(action.Id))
            {
                if (!Matches(gesture, e.Key, e.KeyModifiers)) continue;
                if (typing && !SafeWhileTyping(gesture)) continue;
                return action;
            }
        }
        return null;
    }

    internal static bool Matches(KeyGesture gesture, Key key, KeyModifiers modifiers)
    {
        var relevant = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;
        return gesture.Key == NormalizeKey(key) && (modifiers & relevant) == (gesture.KeyModifiers & relevant);
    }

    /// <summary>Typing letters, Space, Backspace, Delete or Enter must reach the text box.</summary>
    internal static bool SafeWhileTyping(KeyGesture gesture) =>
        (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0 || gesture.Key is >= Key.F1 and <= Key.F24;

    private static Key NormalizeKey(Key key) => key == Key.Return ? Key.Enter : key;

    // ---- changing shortcuts ----------------------------------------------------------------------

    /// <summary>Bind <paramref name="gesture"/> as the only key for an action (null turns it off).</summary>
    public static void Set(string id, KeyGesture? gesture)
    {
        if (!ById.ContainsKey(id)) return;
        Overrides[id] = gesture is null ? string.Empty : Format(gesture);
        Save();
    }

    public static void Reset(string id)
    {
        if (Overrides.Remove(id)) Save();
    }

    public static void ResetAll()
    {
        if (Overrides.Count == 0) return;
        Overrides.Clear();
        Save();
    }

    /// <summary>Another action already using this key, if any.</summary>
    public static ShortcutAction? ConflictFor(string id, KeyGesture gesture) =>
        Actions.FirstOrDefault(a => a.Id != id && GesturesFor(a.Id).Any(g => g.Key == gesture.Key && g.KeyModifiers == gesture.KeyModifiers));

    /// <summary>Remove one key from an action (used when it moves to another action).</summary>
    public static void RemoveGesture(string id, KeyGesture gesture)
    {
        var remaining = GesturesFor(id).Where(g => !(g.Key == gesture.Key && g.KeyModifiers == gesture.KeyModifiers)).ToList();
        Overrides[id] = string.Join("|", remaining.Select(Format));
        Save();
    }

    /// <summary>Keys that can't be used: plain letters/digits (they'd stop typing a name to jump to a file).</summary>
    public static string? Validate(KeyGesture gesture)
    {
        bool modified = (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        bool letterOrDigit = gesture.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9;
        if (letterOrDigit && !modified)
            return "Add Ctrl or Alt to letter and number keys, so typing in the file list still jumps to files.";
        if (gesture.Key == Key.Escape && !modified) return "Escape is kept for closing things.";
        if (gesture.Key == Key.Tab && !modified) return "Tab is kept for moving between controls.";
        return null;
    }

    private static void Save()
    {
        SettingsStore.Instance.Flush();
        Changed?.Invoke();
    }

    // ---- text ------------------------------------------------------------------------------------

    public static bool TryParse(string text, out KeyGesture gesture)
    {
        gesture = null!;
        try
        {
            gesture = KeyGesture.Parse(text);
            return gesture.Key != Key.None;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Saved form (Avalonia's parser format, e.g. "Ctrl+Shift+T").</summary>
    public static string Format(KeyGesture gesture) => gesture.ToString();

    /// <summary>Readable form for menus and Settings ("Ctrl+,", "Del", "Shift+F2", "Ctrl+`").</summary>
    public static string Describe(KeyGesture gesture)
    {
        string key = gesture.Key switch
        {
            Key.Delete => "Del",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemTilde => "`",
            Key.OemQuestion => "/",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemPipe => "\\",
            Key.PageUp => "Page Up",
            Key.PageDown => "Page Down",
            Key.Apps => "Menu",
            >= Key.D0 and <= Key.D9 => ((int)(gesture.Key - Key.D0)).ToString(),
            _ => gesture.Key.ToString(),
        };
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Super");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(key);
        return string.Join('+', parts);
    }
}
