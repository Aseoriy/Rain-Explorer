using System.Text.RegularExpressions;
using Microsoft.Win32;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveFileAssociationServiceTests
{
    [Theory]
    [InlineData("archive.zip", true, "archive")]
    [InlineData("ARCHIVE.7Z", true, "ARCHIVE")]
    [InlineData("archive.rar", true, "archive")]
    [InlineData("archive.tar.gz", true, "archive")]
    [InlineData("archive.tar.bz2", true, "archive")]
    [InlineData("archive.tar.xz", true, "archive")]
    [InlineData("archive.gzip", true, "archive")]
    [InlineData("archive.bzip2", true, "archive")]
    [InlineData("archive.tgz", true, "archive")]
    [InlineData("archive.tbz2", true, "archive")]
    [InlineData("archive.txz", true, "archive")]
    [InlineData("archive.zip.exe", false, "archive.zip.exe")]
    [InlineData("archive.001", false, "archive.001")]
    [InlineData("", false, "")]
    public void ArchiveSuffixRecognitionAndRemovalAreConsistent(
        string fileName,
        bool expected,
        string expectedStem)
    {
        Assert.Equal(expected, ArchiveFileTypes.IsSupportedPath(fileName));
        Assert.Equal(expectedStem, ArchiveFileTypes.RemoveSupportedSuffix(fileName));
    }

    [Fact]
    public void AssociationExtensionsAreTerminalAndDoNotClaimGenericSplitVolumes()
    {
        Assert.Equal(
            [
                ".zip", ".7z", ".rar", ".tar", ".tgz", ".tbz2", ".txz",
                ".gzip", ".bzip2", ".gz", ".bz2", ".xz",
            ],
            ArchiveFileTypes.AssociationExtensions);
        Assert.DoesNotContain(".001", ArchiveFileTypes.AssociationExtensions);
        Assert.DoesNotContain(".tar.gz", ArchiveFileTypes.AssociationExtensions);
    }

    [Fact]
    public void RegistrationPreservesExistingDefaultsAndWinRarIntegration()
    {
        using var executable = TestExecutable.Create();
        var registry = new FakeArchiveAssociationRegistry();
        registry.SetInitialString(@"Software\Classes\.zip", null, "WinRAR.ZIP");
        registry.SetInitialString(
            @"Software\Classes\WinRAR.ZIP\shell\open\command",
            null,
            "\"C:\\Program Files\\WinRAR\\WinRAR.exe\" \"%1\"");
        registry.ClearWrites();

        ArchiveAssociationRegistrationResult result =
            ArchiveFileAssociationService.RegisterForCurrentUser(registry, executable.Path);

        Assert.Equal(ArchiveAssociationRegistrationOutcome.Completed, result.Outcome);
        Assert.Equal(
            ArchiveAssociationRegistrationState.Registered,
            ArchiveFileAssociationService.GetRegistrationState(registry, executable.Path));
        Assert.Equal("WinRAR.ZIP", registry.GetString(@"Software\Classes\.zip", null));
        Assert.Equal(
            "\"C:\\Program Files\\WinRAR\\WinRAR.exe\" \"%1\"",
            registry.GetString(@"Software\Classes\WinRAR.ZIP\shell\open\command", null));
        Assert.DoesNotContain(registry.Writes, write =>
            write.Path.Contains("UserChoice", StringComparison.OrdinalIgnoreCase)
            || write.Path.Contains("WinRAR", StringComparison.OrdinalIgnoreCase)
            || (write.Name?.Contains("UserChoice", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.Equal(
            $"\"{executable.Path}\" \"%1\"",
            registry.GetString(
                $@"{ArchiveFileAssociationService.ProgIdPath}\shell\open\command",
                null));
        Assert.Equal(
            $"\"{executable.ArchiveIconPath}\",0",
            registry.GetString(
                $@"{ArchiveFileAssociationService.ProgIdPath}\DefaultIcon",
                null));
        Assert.Equal(
            $"\"{executable.Path}\",0",
            registry.GetString(
                ArchiveFileAssociationService.CapabilitiesPath,
                "ApplicationIcon"));

        string associations = $@"{ArchiveFileAssociationService.CapabilitiesPath}\FileAssociations";
        foreach (string extension in ArchiveFileTypes.AssociationExtensions)
        {
            Assert.Equal(
                ArchiveFileAssociationService.ProgId,
                registry.GetString(associations, extension));
            Assert.True(registry.ValueExists(
                $@"Software\Classes\{extension}\OpenWithProgids",
                ArchiveFileAssociationService.ProgId));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForeignOwnedIdentityOrRegisteredApplicationFailsBeforeAnyWrite(bool progIdConflict)
    {
        using var executable = TestExecutable.Create();
        var registry = new FakeArchiveAssociationRegistry();
        if (progIdConflict)
        {
            registry.SetInitialString(
                ArchiveFileAssociationService.ProgIdPath,
                ArchiveFileAssociationService.OwnerValueName,
                "Another product");
        }
        else
        {
            registry.SetInitialString(
                ArchiveFileAssociationService.RegisteredApplicationsPath,
                ArchiveFileAssociationService.RegisteredApplicationName,
                @"Software\AnotherProduct\Capabilities");
        }
        registry.ClearWrites();

        ArchiveAssociationRegistrationResult result =
            ArchiveFileAssociationService.RegisterForCurrentUser(registry, executable.Path);

        Assert.Equal(ArchiveAssociationRegistrationOutcome.Conflict, result.Outcome);
        Assert.Equal(ArchiveAssociationRegistrationState.Conflict, result.State);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void ChangedExecutableCommandIsReportedAsRepairable()
    {
        using var executable = TestExecutable.Create();
        var registry = new FakeArchiveAssociationRegistry();
        Assert.Equal(
            ArchiveAssociationRegistrationOutcome.Completed,
            ArchiveFileAssociationService.RegisterForCurrentUser(registry, executable.Path).Outcome);
        registry.SetInitialString(
            $@"{ArchiveFileAssociationService.ProgIdPath}\shell\open\command",
            null,
            "\"C:\\Moved\\RainExplorer.exe\" \"%1\"");

        Assert.Equal(
            ArchiveAssociationRegistrationState.NeedsRepair,
            ArchiveFileAssociationService.GetRegistrationState(registry, executable.Path));
    }

    [Fact]
    public void WindowsSettingsUriUsesRegisteredApplicationOnlyOnSupportedSystems()
    {
        Assert.Equal(
            "ms-settings:defaultapps?registeredAppUser=Rain%20Explorer%20Archives",
            ArchiveFileAssociationService.BuildDefaultAppsSettingsUri(true));
        Assert.Equal(
            "ms-settings:defaultapps",
            ArchiveFileAssociationService.BuildDefaultAppsSettingsUri(false));
    }

    [Fact]
    public void PhysicalLaunchTargetsOpenArchivesDirectlyAndRevealOtherFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "RainExplorer.AssociationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "sample.tar.gz");
            string ordinary = Path.Combine(root, "notes.txt");
            File.WriteAllText(archive, "archive fixture");
            File.WriteAllText(ordinary, "ordinary fixture");

            Assert.True(App.TryResolvePhysicalLaunchTarget(archive, out string? target, out string? select));
            Assert.Equal(archive, target);
            Assert.Null(select);

            Assert.True(App.TryResolvePhysicalLaunchTarget(ordinary, out target, out select));
            Assert.Equal(root, target);
            Assert.Equal(ordinary, select);

            Assert.True(App.TryResolvePhysicalLaunchTarget(root, out target, out select));
            Assert.Equal(root, target);
            Assert.Null(select);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EveryIconResourceReferenceExists()
    {
        string root = FindRepositoryRoot();
        string iconSource = File.ReadAllText(Path.Combine(root, "Main", "Themes", "Icons.xaml"));
        var definitions = Regex.Matches(iconSource, "x:Key=\"(Ic\\.[^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var missing = Directory.EnumerateFiles(Path.Combine(root, "Main"), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "\\{(?:Static|Dynamic)Resource\\s+(Ic\\.[^}\\s]+)\\}")
                .Select(match => (Path: path, Key: match.Groups[1].Value)))
            .Where(reference => !definitions.Contains(reference.Key))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Missing icon resources: " + string.Join(", ", missing.Select(item => $"{item.Key} in {item.Path}")));
    }

    [Fact]
    public void MissingArchiveIconFailsBeforeAnyRegistryWrite()
    {
        using var executable = TestExecutable.Create();
        File.Delete(executable.ArchiveIconPath);
        var registry = new FakeArchiveAssociationRegistry();

        ArchiveAssociationRegistrationResult result =
            ArchiveFileAssociationService.RegisterForCurrentUser(registry, executable.Path);

        Assert.Equal(ArchiveAssociationRegistrationOutcome.Failed, result.Outcome);
        Assert.Equal(ArchiveAssociationRegistrationState.Unavailable, result.State);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void SettingsUsesOneRainButtonAndOneRequiredWindowsConfirmation()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "Main", "Controls", "SettingsView.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "Main", "Controls", "SettingsView.xaml.cs"));

        Assert.Contains("Content=\"Set as default…\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Press Set default there once to confirm.", xaml, StringComparison.Ordinal);
        Assert.Contains("WinRAR right-click and Open with commands stay available", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Manage defaults…\"", xaml, StringComparison.Ordinal);

        int registration = code.IndexOf(
            "ArchiveFileAssociationService.RegisterForCurrentUser()",
            StringComparison.Ordinal);
        int windowsSettings = code.IndexOf(
            "ArchiveFileAssociationService.OpenWindowsDefaultAppsPage()",
            StringComparison.Ordinal);
        Assert.True(registration >= 0 && windowsSettings > registration);
        Assert.Contains("press Set default once", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductVersionSurfacesAreTwoDotZeroBetaOne()
    {
        string root = FindRepositoryRoot();
        Assert.Contains(
            "<Version>2.0.0-beta-1</Version>",
            File.ReadAllText(Path.Combine(root, "Main", "RainExplorer.csproj")),
            StringComparison.Ordinal);
        Assert.Contains(
            "<AssemblyVersion>2.0.0.0</AssemblyVersion>",
            File.ReadAllText(Path.Combine(root, "Main", "RainExplorer.csproj")),
            StringComparison.Ordinal);
        Assert.Contains(
            "<FileVersion>2.0.0.0</FileVersion>",
            File.ReadAllText(Path.Combine(root, "Main", "RainExplorer.csproj")),
            StringComparison.Ordinal);
        Assert.Contains(
            "assemblyIdentity version=\"2.0.0.0\"",
            File.ReadAllText(Path.Combine(root, "Main", "app.manifest")),
            StringComparison.Ordinal);
        Assert.Contains(
            "#define MyAppVersion \"2.0.0-beta-1\"",
            File.ReadAllText(Path.Combine(root, "Installer", "RainExplorer.iss")),
            StringComparison.Ordinal);
        Assert.Contains(
            "v is null ? \"v2.0.0-beta-1\"",
            File.ReadAllText(Path.Combine(root, "Main", "Controls", "SettingsView.xaml.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "v is null ? \"2.0.0-beta-1\"",
            File.ReadAllText(Path.Combine(root, "Main", "Services", "UpdateService.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveIconContainsCompleteMultiSizePngFramesAndIsPackaged()
    {
        string root = FindRepositoryRoot();
        string iconPath = Path.Combine(root, "Main", "Assets", "RainArchive.ico");
        byte[] icon = File.ReadAllBytes(iconPath);

        Assert.True(icon.Length > 6);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(icon, 0));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(icon, 2));
        Assert.Equal((ushort)9, BitConverter.ToUInt16(icon, 4));

        int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        for (int index = 0; index < expectedSizes.Length; index++)
        {
            int entryOffset = 6 + (16 * index);
            int width = icon[entryOffset] == 0 ? 256 : icon[entryOffset];
            int height = icon[entryOffset + 1] == 0 ? 256 : icon[entryOffset + 1];
            ushort bitDepth = BitConverter.ToUInt16(icon, entryOffset + 6);
            uint imageLength = BitConverter.ToUInt32(icon, entryOffset + 8);
            uint imageOffset = BitConverter.ToUInt32(icon, entryOffset + 12);

            Assert.Equal(expectedSizes[index], width);
            Assert.Equal(expectedSizes[index], height);
            Assert.Equal((ushort)32, bitDepth);
            Assert.InRange(imageOffset, 6u + (16u * 9u), (uint)icon.Length);
            Assert.InRange(imageLength, 8u, (uint)icon.Length);
            Assert.True(imageOffset + imageLength <= icon.Length);
            Assert.Equal(
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                icon.AsSpan((int)imageOffset, 8).ToArray());
        }

        string project = File.ReadAllText(Path.Combine(root, "Main", "RainExplorer.csproj"));
        Assert.Contains("<None Update=\"Assets\\RainArchive.ico\">", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>", project, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Main", "Controls", "PaneView.xaml")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Rain Explorer repository root was not found.");
    }

    private sealed class TestExecutable : IDisposable
    {
        private readonly string _root;
        internal string Path { get; }
        internal string ArchiveIconPath { get; }

        private TestExecutable(string root, string path, string archiveIconPath)
        {
            _root = root;
            Path = path;
            ArchiveIconPath = archiveIconPath;
        }

        internal static TestExecutable Create()
        {
            string root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainExplorer.AssociationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = System.IO.Path.Combine(root, "Rain Explorer.exe");
            File.WriteAllText(path, "executable fixture");
            string archiveIconPath = System.IO.Path.Combine(
                root,
                ArchiveFileAssociationService.ArchiveIconRelativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(archiveIconPath)!);
            File.WriteAllBytes(archiveIconPath, [0, 0, 1, 0]);
            return new(root, path, archiveIconPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record RegistryWrite(string Path, string? Name, object Value, RegistryValueKind Kind);

    private sealed class FakeArchiveAssociationRegistry : IArchiveAssociationRegistry
    {
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Path, string Name), object> _values = new();
        internal List<RegistryWrite> Writes { get; } = [];

        public bool KeyExists(string path) => _keys.Contains(path);

        public bool ValueExists(string path, string? name) =>
            _values.ContainsKey(Key(path, name));

        public string? GetString(string path, string? name) =>
            _values.TryGetValue(Key(path, name), out object? value) ? value as string : null;

        public void SetString(string path, string? name, string value)
        {
            Set(path, name, value, RegistryValueKind.String, recordWrite: true);
        }

        public void SetNone(string path, string name)
        {
            Set(path, name, Array.Empty<byte>(), RegistryValueKind.None, recordWrite: true);
        }

        internal void SetInitialString(string path, string? name, string value)
        {
            Set(path, name, value, RegistryValueKind.String, recordWrite: false);
        }

        internal void ClearWrites() => Writes.Clear();

        private void Set(
            string path,
            string? name,
            object value,
            RegistryValueKind kind,
            bool recordWrite)
        {
            _keys.Add(path);
            _values[Key(path, name)] = value;
            if (recordWrite) Writes.Add(new(path, name, value, kind));
        }

        private static (string Path, string Name) Key(string path, string? name) =>
            (path.ToUpperInvariant(), (name ?? string.Empty).ToUpperInvariant());
    }
}
