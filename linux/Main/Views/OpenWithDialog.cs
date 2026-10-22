using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>One installed application from a freedesktop <c>.desktop</c> entry.</summary>
public sealed record DesktopApp(string Id, string Name, string Exec, IReadOnlyList<string> MimeTypes, bool Terminal,
    string? DesktopFilePath = null)
{
    public override string ToString() => Name;
}

/// <summary>Reads installed applications (XDG application dirs) and launches one with files.</summary>
public static class DesktopApps
{
    public static IReadOnlyList<DesktopApp> All()
    {
        var dirs = new List<string>
        {
            Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home
                ? home : Path.Combine(XdgDirs.Home, ".local/share"), "applications"),
        };
        string dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d
            ? d : "/usr/local/share:/usr/share";
        dirs.AddRange(dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries).Select(x => Path.Combine(x, "applications")));

        var apps = new Dictionary<string, DesktopApp>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string dir in dirs.Where(Directory.Exists))
        {
            foreach (string file in ApplicationFiles(dir))
            {
                string id = Path.GetRelativePath(dir, file).Replace('/', '-');
                if (!seen.Add(id)) continue;   // hidden user entries also mask system entries
                if (Parse(id, file) is { } app) apps[id] = app;
            }
        }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<string> ApplicationFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string[] files, children;
            try
            {
                files = Directory.GetFiles(dir, "*.desktop");
                children = new DirectoryInfo(dir).GetDirectories()
                    .Where(d => d.LinkTarget is null).Select(d => d.FullName).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            // Flatpak exports link desktop manifests. Read those entries, but never
            // recurse into directory links when discovering installed applications.
            foreach (string file in files) yield return file;
            foreach (string child in children) pending.Push(child);
        }
    }

    private static DesktopApp? Parse(string id, string file)
    {
        try
        {
            bool inEntry = false;
            string? name = null, exec = null, type = null;
            bool hidden = false, terminal = false;
            var mimes = new List<string>();
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    if (inEntry) break;
                    inEntry = line == "[Desktop Entry]";
                    continue;
                }
                if (!inEntry || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "Name": name = value; break;
                    case "Exec": exec = value; break;
                    case "Type": type = value; break;
                    case "NoDisplay" or "Hidden": hidden |= value == "true"; break;
                    case "Terminal": terminal = value == "true"; break;
                    case "MimeType": mimes.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries)); break;
                }
            }
            if (hidden || type != "Application" || name is null || exec is null) return null;
            return new DesktopApp(id, name, exec, mimes, terminal, file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Start <paramref name="app"/> with one file, expanding the Exec field codes.</summary>
    public static void Launch(DesktopApp app, string path)
    {
        // GIO implements desktop-entry quoting, field codes and Terminal=true, including
        // Flatpak launchers. Keep files as arguments rather than interpreting Exec as shell code.
        if (app.DesktopFilePath is { } desktopFile && ProcessLauncher.FindExecutable("gio") is { } gio)
        {
            var launch = new ProcessStartInfo(gio) { UseShellExecute = false };
            launch.ArgumentList.Add("launch");
            launch.ArgumentList.Add(desktopFile);
            launch.ArgumentList.Add(path);
            ProcessLauncher.StartDetached(launch);
            return;
        }
        if (app.Terminal)
            throw new InvalidOperationException("This app needs a terminal. Install GIO (glib utilities) to launch it.");

        var args = new List<string>();
        bool placed = false;
        foreach (string token in Tokenize(app.Exec))
        {
            switch (token)
            {
                case "%f" or "%F" or "%u" or "%U":
                    args.Add(path);
                    placed = true;
                    break;
                case "%i" or "%c" or "%k":
                    break;
                default:
                    args.Add(token.Replace("%%", "%"));
                    break;
            }
        }
        if (args.Count == 0) throw new InvalidOperationException($"{app.Name} has no command to run.");
        if (!placed) args.Add(path);

        var start = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path) ?? XdgDirs.Home,
        };
        foreach (string arg in args.Skip(1)) start.ArgumentList.Add(arg);
        ProcessLauncher.StartDetached(start);
    }

    /// <summary>Split an Exec value on spaces, honouring double quotes and backslash escapes.</summary>
    private static IEnumerable<string> Tokenize(string exec)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char ch = exec[i];
            if (quoted && ch == '\\' && i + 1 < exec.Length) { current.Append(exec[++i]); continue; }
            if (ch == '"') { quoted = !quoted; any = true; continue; }
            if (ch == ' ' && !quoted)
            {
                if (current.Length > 0 || any) yield return current.ToString();
                current.Clear();
                any = false;
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0 || any) yield return current.ToString();
    }
}

/// <summary>Rain-styled "Open with" picker: apps for this file type first, then every app.</summary>
public sealed class OpenWithDialog : DialogWindow
{
    private readonly ListBox _list;
    private readonly string _path;

    private OpenWithDialog(string path) : base("Open with", "external-link", width: 440)
    {
        _path = path;
        string mime = MimeService.ForFileName(Path.GetFileName(path));
        var family = MimeService.SelfAndParents(mime).ToHashSet(StringComparer.Ordinal);
        var all = DesktopApps.All();
        var recommended = all.Where(a => a.MimeTypes.Any(family.Contains)).ToList();
        var items = recommended.Concat(all.Except(recommended)).ToList();

        _list = new ListBox
        {
            Margin = new Thickness(0, 14, 0, 0),
            Height = 320,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemsSource = items,
            SelectedIndex = items.Count > 0 ? 0 : -1,
            ItemTemplate = new FuncDataTemplate<DesktopApp>((app, _) => new TextBlock
            {
                Text = app is null ? "" : recommended.Contains(app) ? app.Name : $"{app.Name}",
                FontWeight = app is not null && recommended.Contains(app) ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 2),
            }),
        };
        ApplyTheme(_list, "NavItem", container: true);
        _list.DoubleTapped += (_, _) => Accept();

        var panel = new StackPanel();
        panel.Children.Add(Message(recommended.Count > 0
            ? $"Apps for {MimeService.Comment(mime) ?? mime} are listed first."
            : "No app says it handles this file type. Pick any app to try it."));
        panel.Children.Add(_list);
        Body = panel;

        AddButton("Cancel", () => Finish(null));
        AddButton("Open", Accept, DialogButtonKind.Primary, isDefault: true);
    }

    private string? _error;

    private void Accept()
    {
        if (_list.SelectedItem is not DesktopApp app) return;
        try { DesktopApps.Launch(app, _path); _error = null; }
        catch (Exception ex) { _error = $"Couldn't open with {app.Name}: {ex.Message}"; }
        Finish(true);
    }

    /// <summary>Returns an error message, or null when an app was started or the user cancelled.</summary>
    public static async Task<string?> ShowFor(Window? owner, string path)
    {
        var dialog = new OpenWithDialog(path);
        await dialog.ShowFor<bool>(owner);
        return dialog._error;
    }
}
