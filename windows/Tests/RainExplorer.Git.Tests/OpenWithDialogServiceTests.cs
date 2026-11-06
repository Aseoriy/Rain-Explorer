using RainExplorer.Services;
using System.Runtime.InteropServices;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class OpenWithDialogServiceTests
{
    [Fact]
    public void SupportedWindowsExportsTheDocumentedOpenWithDialogEntryPoint()
    {
        Assert.True(NativeLibrary.TryLoad("shell32.dll", out IntPtr shell32));
        try
        {
            Assert.True(NativeLibrary.TryGetExport(shell32, "SHOpenWithDialog", out IntPtr entryPoint));
            Assert.NotEqual(IntPtr.Zero, entryPoint);
        }
        finally
        {
            NativeLibrary.Free(shell32);
        }
    }

    [Fact]
    public void ExistingUnicodeFileIsCanonicalizedWithoutChangingRainWorkingDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"Rain Explorer open-with path 雨 {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "archive with spaces.zip");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        string priorWorkingDirectory = Environment.CurrentDirectory;

        try
        {
            Assert.True(OpenWithDialogService.TryNormalizeExistingFile(path, out string canonical));
            Assert.Equal(Path.GetFullPath(path), canonical);
            Assert.Equal(priorWorkingDirectory, Environment.CurrentDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingRelativeAndDirectoryTargetsAreRefusedBeforeOpeningWindowsUi()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"Rain Explorer open-with refusal {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(OpenWithDialogService.TryNormalizeExistingFile("relative.zip", out _));
            Assert.False(OpenWithDialogService.TryNormalizeExistingFile(
                Path.Combine(root, "missing.zip"), out _));
            Assert.False(OpenWithDialogService.TryNormalizeExistingFile(root, out _));
        }
        finally
        {
            Directory.Delete(root);
        }
    }
}
