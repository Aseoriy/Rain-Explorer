namespace RainExplorer.Services;

/// <summary>Per-user activation, written only when the user enables the integration.</summary>
public static class FileManagerActivation
{
    public const string Marker = "# Rain Explorer FileManager1 integration";
    public static string ServicePath => Path.Combine(XdgDirs.DataHome, "dbus-1", "services", FileManagerService.BusName + ".service");

    internal static string CreateContents(IReadOnlyList<string> command)
    {
        if (command.Count == 0 || command.Any(arg => arg.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new ArgumentException("The Rain executable path cannot contain line breaks.");
        string exec = string.Join(' ', command.Select(arg => "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
        return $"{Marker}\n[D-BUS Service]\nName={FileManagerService.BusName}\nExec={exec} --file-manager-service\n";
    }

    internal static IReadOnlyList<string> LaunchCommand()
    {
        string assembly = typeof(FileManagerActivation).Assembly.Location;
        string appHost = Path.Combine(Path.GetDirectoryName(assembly)!, "RainExplorer");
        if (File.Exists(appHost)) return [appHost];
        string? dotnet = Path.GetFileName(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath
            : ProcessLauncher.FindExecutable("dotnet");
        return dotnet is not null ? [dotnet, assembly]
            : throw new IOException("Install a release build of Rain to enable desktop activation.");
    }

    public static void Enable()
    {
        string path = ServicePath;
        ValidateEnable();
        string contents = CreateContents(LaunchCommand());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void ValidateEnable()
    {
        string path = ServicePath;
        if (new FileInfo(path).LinkTarget is not null || File.Exists(path) && !IsManaged(path))
            throw new IOException("Another app has a user Show in folder service. Rain has left it unchanged.");
    }

    public static bool Disable()
    {
        string path = ServicePath;
        if (new FileInfo(path).LinkTarget is not null || !IsManaged(path)) return false;
        File.Delete(path);
        return true;
    }

    private static bool IsManaged(string path)
    {
        if (!File.Exists(path)) return false;
        using var reader = File.OpenText(path);
        return reader.ReadLine() == Marker;
    }
}
