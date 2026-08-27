using System.Diagnostics;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ProcessLauncherTests
{
    [Fact]
    public void DirectExecutableUsesItsContainingDirectoryWithoutChangingRainCwd()
    {
        string rainWorkingDirectory = Environment.CurrentDirectory;
        string executable = Path.Combine(Path.GetTempPath(), "Rain Explorer launch test", "Probe App.exe");

        ProcessStartInfo start = ProcessLauncher.CreateShellStartInfo(executable);

        Assert.True(start.UseShellExecute);
        Assert.Equal(executable, start.FileName);
        Assert.Equal(Path.GetDirectoryName(executable), start.WorkingDirectory);
        Assert.Equal(rainWorkingDirectory, Environment.CurrentDirectory);
    }

    [Theory]
    [InlineData("Document.txt")]
    [InlineData("Shortcut.lnk")]
    [InlineData("Package.msi")]
    public void ShellAssociatedFilesKeepTheirNativeWorkingDirectoryBehavior(string fileName)
    {
        string path = Path.Combine(Path.GetTempPath(), "Rain Explorer launch test", fileName);

        ProcessStartInfo start = ProcessLauncher.CreateShellStartInfo(path);

        Assert.True(start.UseShellExecute);
        Assert.Equal(string.Empty, start.WorkingDirectory);
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("runas", true)]
    [InlineData("edit", false)]
    [InlineData(null, false)]
    public void NativeShellOnlyGetsExecutableDirectoryForOpenAndRunAs(
        string? canonicalVerb, bool expectsDirectory)
    {
        string executable = Path.Combine(Path.GetTempPath(), "Rain Explorer launch test", "Probe App.exe");

        string? workingDirectory = ProcessLauncher.GetShellVerbWorkingDirectory(
            executable, canonicalVerb);

        Assert.Equal(expectsDirectory ? Path.GetDirectoryName(executable) : null, workingDirectory);
        Assert.Null(ProcessLauncher.GetShellVerbWorkingDirectory(
            Path.ChangeExtension(executable, ".lnk"), canonicalVerb));
    }

    [Fact]
    public void OpenWithKeepsAPathContainingSpacesAsOneArgument()
    {
        string path = @"E:\Some Folder\Probe App.exe";

        ProcessStartInfo start = ProcessLauncher.CreateOpenWithStartInfo(path);

        Assert.True(start.UseShellExecute);
        Assert.Equal("rundll32.exe", start.FileName);
        Assert.Equal(["shell32.dll,OpenAs_RunDLL", path], start.ArgumentList);
    }

    [Fact]
    public void LaunchedExecutableReceivesItsContainingDirectoryIncludingSpaces()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Rain Explorer launch test {Guid.NewGuid():N}");
        string executable = Path.Combine(root, "Probe App.exe");
        string report = Path.Combine(root, "reported cwd.txt");
        Directory.CreateDirectory(root);

        try
        {
            File.Copy(Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
                executable);
            ProcessStartInfo start = ProcessLauncher.CreateShellStartInfo(executable);
            start.Arguments = "/d /c cd > \"reported cwd.txt\"";

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("The working-directory probe did not start.");
            Assert.True(process.WaitForExit(10_000), "The working-directory probe did not exit.");
            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(report), "The probe did not write its report beside the executable.");

            string actual = File.ReadAllText(report).Trim();
            Assert.Equal(Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(actual), ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
