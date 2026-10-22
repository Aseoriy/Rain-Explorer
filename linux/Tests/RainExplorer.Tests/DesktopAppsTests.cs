using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Tests;

public sealed class DesktopAppsTests
{
    [Fact]
    public void UserEntriesOverrideSystemEntriesIncludingHiddenApps()
    {
        string root = TestEnvironment.NewFolder("desktop-apps");
        string user = Path.Combine(root, "user");
        string system = Path.Combine(root, "system");
        Directory.CreateDirectory(Path.Combine(user, "applications"));
        Directory.CreateDirectory(Path.Combine(system, "applications"));
        string entry = "[Desktop Entry]\nType=Application\nName=System editor\nExec=editor %f\nMimeType=text/plain;\n";
        File.WriteAllText(Path.Combine(system, "applications", "editor.desktop"), entry);
        File.WriteAllText(Path.Combine(system, "applications", "hidden.desktop"), entry);
        File.WriteAllText(Path.Combine(user, "applications", "editor.desktop"), entry.Replace("System editor", "My editor"));
        File.WriteAllText(Path.Combine(user, "applications", "hidden.desktop"), "[Desktop Entry]\nHidden=true\n");
        Directory.CreateSymbolicLink(Path.Combine(user, "applications", "loop"), Path.Combine(user, "applications"));
        File.CreateSymbolicLink(Path.Combine(user, "applications", "flatpak.desktop"), Path.Combine(system, "applications", "editor.desktop"));

        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", user);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", system);
            var apps = DesktopApps.All();
            Assert.Equal(2, apps.Count);
            Assert.Contains(apps, a => a.Id == "flatpak.desktop" && a.Name == "System editor");
            var app = Assert.Single(apps, a => a.Id == "editor.desktop");
            Assert.Equal("My editor", app.Name);
            Assert.Equal("editor.desktop", app.Id);
            Assert.Contains("text/plain", app.MimeTypes);
            Assert.Equal(Path.Combine(user, "applications", "editor.desktop"), app.DesktopFilePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
        }
    }

    [Fact]
    public void GioReceivesDesktopEntryAndFileAsSeparateLiteralArguments()
    {
        string dir = TestEnvironment.NewFolder("gio-launch");
        string captured = Path.Combine(dir, "arguments");
        string gio = Path.Combine(dir, "gio");
        File.WriteAllText(gio, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$RAIN_TEST_GIO_ARGS\"\n");
        File.SetUnixFileMode(gio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string desktop = Path.Combine(dir, "My editor.desktop");
        string file = Path.Combine(dir, "notes with spaces;$(literal).md");
        File.WriteAllText(desktop, "[Desktop Entry]\n");
        File.WriteAllText(file, "Notes");
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir);
            Environment.SetEnvironmentVariable("RAIN_TEST_GIO_ARGS", captured);
            DesktopApps.Launch(new DesktopApp("editor", "Editor", "editor %f", new[] { "text/plain" }, true, desktop), file);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(captured) && new FileInfo(captured).Length > 0, 3000));
            Assert.Equal(new[] { "launch", desktop, file }, File.ReadAllLines(captured));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("RAIN_TEST_GIO_ARGS", null);
        }
    }

    [Fact]
    public void TerminalAppWithoutGioReportsWhyItCannotLaunch()
    {
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            var error = Assert.Throws<InvalidOperationException>(() =>
                DesktopApps.Launch(new DesktopApp("editor", "Editor", "editor %f", Array.Empty<string>(), true), "/tmp/notes"));
            Assert.Contains("terminal", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }
}
