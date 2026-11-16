using System.IO;
using System.Runtime.InteropServices;

namespace RainExplorer.Services;

/// <summary>
/// Turns Windows' bare "being used by another process" error into a message that names the
/// item and, when Windows can tell, the program holding it. Restart Manager can name the
/// holder of an open file; a folder that is some program's working folder (a terminal, an
/// editor, a build tool) is invisible to it, so folders get a hint instead.
/// </summary>
public static class FileLockDiagnostics
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int MaxFilesToProbe = 200;

    public static bool IsInUse(Exception exception) =>
        exception is IOException
        && (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    /// <summary>The message to show for <paramref name="exception"/> while working on
    /// <paramref name="path"/>; the original message when it is not an in-use error.</summary>
    public static string Describe(Exception exception, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return exception.Message;
        bool inUse = IsInUse(exception);
        bool isDirectory = Directory.Exists(path);
        // Moving a folder while a file inside it is open fails as "access denied" rather
        // than "in use". Only reword that when a holder is actually found, since access
        // denied can also be a real permissions problem.
        bool deniedFolder = isDirectory && (exception is UnauthorizedAccessException
            || exception is IOException && (exception.HResult & 0xFFFF) == ErrorAccessDenied);
        if (!inUse && !deniedFolder) return exception.Message;

        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = path;

        IReadOnlyList<string> holders = Array.Empty<string>();
        try { holders = FindHolders(isDirectory ? FilesUnder(path) : new[] { path }); }
        catch { /* Diagnostics only; fall back to the hint below. */ }

        if (holders.Count > 0)
            return $"\"{name}\" is in use by {JoinNames(holders)}. Close it there and try again.";
        if (!inUse) return exception.Message;

        return isDirectory
            ? $"\"{name}\" or something inside it is open in another program, for example a terminal, editor or app whose working folder is inside it. Close it and try again."
            : $"\"{name}\" is open in another program. Close it and try again.";
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    private static string[] FilesUnder(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).Take(MaxFilesToProbe).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Program names (deduplicated) that Restart Manager reports as holding any of
    /// <paramref name="files"/>.</summary>
    internal static IReadOnlyList<string> FindHolders(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return Array.Empty<string>();
        if (RmStartSession(out uint session, 0, new System.Text.StringBuilder(33)) != 0)
            return Array.Empty<string>();
        try
        {
            string[] array = files.ToArray();
            if (RmRegisterResources(session, (uint)array.Length, array, 0, null, 0, null) != 0)
                return Array.Empty<string>();

            uint needed = 0, count = 0, reasons = 0;
            int result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result != ErrorMoreData || needed == 0) return Array.Empty<string>();

            var infos = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
                return Array.Empty<string>();

            int ownId = Environment.ProcessId;
            return infos.Take((int)count)
                .Where(info => info.Process.ProcessId != ownId)
                .Select(info => string.IsNullOrWhiteSpace(info.AppName) ? info.ServiceShortName : info.AppName)
                .Where(appName => !string.IsNullOrWhiteSpace(appName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally { RmEndSession(session); }
    }

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, System.Text.StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] fileNames,
        uint applicationCount, RmUniqueProcess[]? applications, uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded, ref uint procInfo,
        [In, Out] RmProcessInfo[]? affectedApps, ref uint rebootReasons);
}
