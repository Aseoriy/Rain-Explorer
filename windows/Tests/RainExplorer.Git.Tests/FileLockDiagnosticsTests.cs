using System.IO;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class FileLockDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rain-lock-" + Guid.NewGuid().ToString("N"));

    public FileLockDiagnosticsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void LockedFolderMoveNamesTheFolderInsteadOfTheBareWindowsMessage()
    {
        // The exact error Windows gave when a project folder was some program's
        // working folder (ERROR_SHARING_VIOLATION).
        string folder = Path.Combine(_root, "Project");
        Directory.CreateDirectory(folder);
        var failure = new IOException(
            "The process cannot access the file because it is being used by another process.",
            unchecked((int)0x80070020));

        Assert.True(FileLockDiagnostics.IsInUse(failure));
        Assert.StartsWith("\"Project\" or something inside it is open in another program",
            FileLockDiagnostics.Describe(failure, folder));
    }

    [Fact]
    public void AccessDeniedFolderWithNoNamedHolderKeepsTheWindowsMessage()
    {
        // Our own open handle makes the folder move fail, but Restart Manager leaves Rain's
        // own process out of the holders, so there is nothing to name and nothing to reword.
        string folder = Path.Combine(_root, "Project");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "open.txt");
        File.WriteAllText(file, "x");
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        // Windows reports this as an IOException carrying ERROR_ACCESS_DENIED.
        Exception failure = Assert.ThrowsAny<Exception>(() =>
            Directory.Move(folder, Path.Combine(_root, "Moved")));

        Assert.Equal(failure.Message, FileLockDiagnostics.Describe(failure, folder));
    }

    [Fact]
    public void LockedFileMoveNamesTheFile()
    {
        string file = Path.Combine(_root, "report.docx");
        File.WriteAllText(file, "x");
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var failure = Assert.ThrowsAny<IOException>(() =>
            File.Move(file, Path.Combine(_root, "elsewhere.docx")));

        Assert.True(FileLockDiagnostics.IsInUse(failure));
        Assert.Equal("\"report.docx\" is open in another program. Close it and try again.",
            FileLockDiagnostics.Describe(failure, file));
    }

    [Fact]
    public void OtherErrorsKeepTheirOriginalMessage()
    {
        var other = new IOException("Disk full.");
        Assert.Equal("Disk full.", FileLockDiagnostics.Describe(other, @"C:\x"));
    }

    [Theory]
    [InlineData(@"C:\Projects\Sail", @"C:\Projects\Sail", true)]
    [InlineData(@"C:\Projects\Sail\src", @"C:\Projects\Sail", true)]
    [InlineData(@"C:\projects\sail\src\", @"C:\Projects\Sail\", true)]
    [InlineData(@"C:\Projects\SailBeta", @"C:\Projects\Sail", false)]
    [InlineData(@"C:\Projects", @"C:\Projects\Sail", false)]
    public void WatcherReleaseMatchesTheMovedFolderAndItsInsides(string watched, string moving, bool expected) =>
        Assert.Equal(expected, TabViewModel.IsSameOrInside(watched, moving));
}
