using System.Windows.Input;

namespace RainExplorer.Services;

/// <summary>A key plus modifiers. Rain's own type: WPF's KeyGesture refuses keys like a bare Delete or Space.</summary>
public readonly record struct Shortcut(Key Key, ModifierKeys Modifiers);

/// <summary>
/// One keyboard action: its stable id (saved in settings), where it shows up in Settings → Shortcuts, its
/// default keys, and whether it may fire while a text box has focus.
/// </summary>
public sealed record ShortcutAction(string Id, string Group, string Name, string[] Defaults, bool WorksWhileTyping);

/// <summary>
/// Every keyboard shortcut in Rain, with user overrides on top (Settings → Shortcuts). Defaults match the
/// shortcuts Rain always had. Overrides live in <see cref="Models.AppSettings.ShortcutOverrides"/> as
/// id → keys joined by "|" (an empty string turns the action off). While typing in a text box, only actions
/// marked <see cref="ShortcutAction.WorksWhileTyping"/> fire, and only with Ctrl/Alt or F-keys, so typing
/// letters, Space, Backspace or Delete never triggers anything. Port of the Linux ShortcutService.
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
        new("nav.up", "Navigation", "Up one folder", ["Alt+Up", "Backspace"], true),
        new("nav.home", "Navigation", "Home page", ["Alt+Home"], true),
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
        new("app.settings", "Rain", "Settings", ["Ctrl+,"], true),
        new("app.activity", "Rain", "Activity center", ["Ctrl+Shift+A"], true),
        new("app.commandBox", "Rain", "Command box", ["Ctrl+`"], true),
        new("app.terminal", "Rain", "Open terminal here", ["Shift+F4"], true),
        new("app.shortcuts", "Rain", "Keyboard shortcuts", ["F1", "Shift+/"], true),
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
    public static IReadOnlyList<Shortcut> GesturesFor(string id)
    {
        if (!ById.TryGetValue(id, out var action)) return [];
        string[] texts = Overrides.TryGetValue(id, out string? custom)
            ? custom.Split('|', StringSplitOptions.RemoveEmptyEntries)
            : action.Defaults;
        var list = new List<Shortcut>();
        foreach (string text in texts)
            if (TryParse(text, out var gesture)) list.Add(gesture);
        return list;
    }

    public static bool IsCustomized(string id) => Overrides.ContainsKey(id);

    /// <summary>The first key for an action, for menu hints; null when it has none.</summary>
    public static Shortcut? PrimaryGesture(string id) => GesturesFor(id) is [var first, ..] ? first : null;

    /// <summary>"Ctrl+T, Ctrl+Page Down" (or "None").</summary>
    public static string DescribeAction(string id)
    {
        var gestures = GesturesFor(id);
        return gestures.Count == 0 ? "None" : string.Join(", ", gestures.Select(Describe));
    }

    /// <summary>Menu text for an action's first key ("" when it has none).</summary>
    public static string Hint(string id) => PrimaryGesture(id) is { } g ? Describe(g) : string.Empty;

    /// <summary>The key a press really is: Alt turns keys into Key.System, an IME into Key.ImeProcessed.</summary>
    public static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    /// <summary>The action a key press triggers, or null.</summary>
    public static ShortcutAction? Match(KeyEventArgs e, bool typing) => Match(RealKey(e), Keyboard.Modifiers, typing);

    internal static ShortcutAction? Match(Key key, ModifierKeys modifiers, bool typing)
    {
        foreach (var action in Actions)
        {
            if (typing && !action.WorksWhileTyping) continue;
            foreach (var gesture in GesturesFor(action.Id))
            {
                if (!Matches(gesture, key, modifiers)) continue;
                if (typing && !SafeWhileTyping(gesture)) continue;
                return action;
            }
        }
        return null;
    }

    internal static bool Matches(Shortcut gesture, Key key, ModifierKeys modifiers)
    {
        const ModifierKeys relevant = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows;
        return gesture.Key == key && (modifiers & relevant) == (gesture.Modifiers & relevant);
    }

    /// <summary>Typing letters, Space, Backspace, Delete or Enter must reach the text box.</summary>
    internal static bool SafeWhileTyping(Shortcut gesture) =>
        (gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 || gesture.Key is >= Key.F1 and <= Key.F24;

    /// <summary>Keys that are only modifiers: recording waits for the real key after them.</summary>
    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift
        or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None or Key.System;

    // ---- changing shortcuts ----------------------------------------------------------------------

    /// <summary>Bind <paramref name="gesture"/> as the only key for an action (null turns it off).</summary>
    public static void Set(string id, Shortcut? gesture)
    {
        if (!ById.ContainsKey(id)) return;
        Overrides[id] = gesture is { } g ? Format(g) : string.Empty;
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
    public static ShortcutAction? ConflictFor(string id, Shortcut gesture) =>
        Actions.FirstOrDefault(a => a.Id != id && GesturesFor(a.Id).Contains(gesture));

    /// <summary>Remove one key from an action (used when it moves to another action).</summary>
    public static void RemoveGesture(string id, Shortcut gesture)
    {
        var remaining = GesturesFor(id).Where(g => g != gesture).ToList();
        Overrides[id] = string.Join("|", remaining.Select(Format));
        Save();
    }

    /// <summary>Keys that can't be used: plain letters/digits (they'd stop typing a name to jump to a file).</summary>
    public static string? Validate(Shortcut gesture)
    {
        bool modified = (gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
        bool letterOrDigit = gesture.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9;
        if (letterOrDigit && !modified)
            return "Add Ctrl or Alt to letter and number keys, so typing in the file list still jumps to files.";
        if (gesture.Key == Key.Escape && !modified) return "Escape is kept for closing things.";
        if (gesture.Key == Key.Tab && !modified) return "Tab is kept for moving between controls.";
        if ((gesture.Modifiers & ModifierKeys.Windows) != 0) return "Windows keeps shortcuts with the Windows key for itself.";
        return null;
    }

    private static void Save()
    {
        SettingsStore.Instance.Flush();
        Changed?.Invoke();
    }

    // ---- text ------------------------------------------------------------------------------------

    // Keys with readable names; also accepted when parsing. Everything else uses the Key enum name.
    private static readonly (Key Key, string Text)[] KeyNames =
    [
        (Key.Delete, "Delete"), (Key.Enter, "Enter"), (Key.Back, "Backspace"), (Key.Escape, "Esc"),
        (Key.OemComma, ","), (Key.OemPeriod, "."), (Key.Oem3, "`"), (Key.Oem2, "/"), (Key.OemMinus, "-"),
        (Key.OemPlus, "="), (Key.Oem4, "["), (Key.Oem6, "]"), (Key.Oem1, ";"), (Key.Oem7, "'"),
        (Key.Oem5, "\\"), (Key.PageUp, "PageUp"), (Key.PageDown, "PageDown"), (Key.Apps, "Menu"),
        (Key.Space, "Space"), (Key.Insert, "Insert"), (Key.CapsLock, "CapsLock"), (Key.PrintScreen, "PrintScreen"),
    ];

    public static bool TryParse(string text, out Shortcut gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        string[] parts = text.Split('+');
        // "Ctrl++" means the "+" key; a lone trailing "+" ("Ctrl+") has no key at all.
        string keyText = text.Length > 1 && text.EndsWith("++", StringComparison.Ordinal) ? "+" : parts[^1];
        var modifiers = ModifierKeys.None;
        foreach (string part in parts[..^1])
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "win" or "windows" or "meta" or "super": modifiers |= ModifierKeys.Windows; break;
                case "": break;
                default: return false;
            }
        }
        if (!TryParseKey(keyText.Trim(), out Key key)) return false;
        gesture = new Shortcut(key, modifiers);
        return true;
    }

    private static bool TryParseKey(string text, out Key key)
    {
        foreach (var (k, name) in KeyNames)
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) { key = k; return true; }
        }
        if (text.Length == 1 && char.IsAsciiDigit(text[0])) { key = Key.D0 + (text[0] - '0'); return true; }
        if (text == "+") { key = Key.OemPlus; return true; }
        if (Enum.TryParse(text, ignoreCase: true, out key) && Enum.IsDefined(key) && key != Key.None) return true;
        key = Key.None;
        return false;
    }

    /// <summary>Saved form ("Ctrl+Shift+T", "Ctrl+,", "Shift+Delete").</summary>
    public static string Format(Shortcut gesture) => Describe(gesture);

    /// <summary>Readable form for menus and Settings ("Ctrl+,", "Delete", "Shift+F2", "Ctrl+`").</summary>
    public static string Describe(Shortcut gesture)
    {
        string? key = null;
        foreach (var (k, name) in KeyNames)
        {
            if (k == gesture.Key) { key = name; break; }
        }
        key ??= gesture.Key is >= Key.D0 and <= Key.D9
            ? ((int)(gesture.Key - Key.D0)).ToString()
            : gesture.Key.ToString();
        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(key);
        return string.Join('+', parts);
    }
}
