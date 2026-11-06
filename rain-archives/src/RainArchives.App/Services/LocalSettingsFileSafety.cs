using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RainArchives.App.Services;

/// <summary>
/// Narrow Windows object checks for App-owned settings files. This helper does
/// not grant archive or destination filesystem authority.
/// </summary>
internal static class LocalSettingsFileSafety
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint OpenExisting = 3;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorHandleEof = 38;
    private const int ErrorNoMoreFiles = 18;
    private static readonly IntPtr InvalidFindHandle = new(-1);

    public static void CreateAndValidateDirectoryChain(
        string localRoot,
        params string[] relativeSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        ArgumentNullException.ThrowIfNull(relativeSegments);
        var current = Path.GetFullPath(localRoot);
        if (!Directory.Exists(current))
        {
            Directory.CreateDirectory(current);
        }

        EnsureRegularDirectory(current);
        foreach (var segment in relativeSegments)
        {
            ValidateSegment(segment);
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            EnsureRegularDirectory(current);
        }
    }

    public static bool ValidateExistingDirectoryChain(
        string localRoot,
        params string[] relativeSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        ArgumentNullException.ThrowIfNull(relativeSegments);
        var current = Path.GetFullPath(localRoot);
        if (!TryGetAttributes(current, out _))
        {
            return false;
        }

        EnsureRegularDirectory(current);
        foreach (var segment in relativeSegments)
        {
            ValidateSegment(segment);
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out _))
            {
                return false;
            }

            EnsureRegularDirectory(current);
        }

        return true;
    }

    public static void EnsureRegularDirectory(string path)
    {
        var identity = ReadNoFollowIdentity(path);
        if (!identity.IsDirectory
            || identity.IsReparsePoint)
        {
            throw new UnsafeSettingsObjectException(
                "The local settings directory is not a regular directory.");
        }

        EnsureNoAlternateDataStreams(path);
    }

    public static LocalSettingsFileIdentity EnsureRegularFile(string path)
    {
        var identity = ReadNoFollowIdentity(path);
        if (identity.IsDirectory
            || identity.IsReparsePoint
            || identity.NumberOfLinks != 1)
        {
            throw new UnsafeSettingsObjectException(
                "The local settings file is not an exclusive regular file.");
        }

        EnsureNoAlternateDataStreams(path);
        return identity;
    }

    public static void EnsureRegularFileIfPresent(string path)
    {
        if (TryGetAttributes(path, out _))
        {
            _ = EnsureRegularFile(path);
        }
    }

    public static FileStream OpenVerifiedRead(
        string path,
        int bufferSize,
        FileOptions options)
    {
        var expected = EnsureRegularFile(path);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                options);
            var actual = ReadIdentity(stream.SafeFileHandle);
            if (!expected.SameObject(actual)
                || actual.IsDirectory
                || actual.IsReparsePoint
                || actual.NumberOfLinks != 1)
            {
                throw new UnsafeSettingsObjectException(
                    "The local settings file changed while it was being opened.");
            }

            EnsureNoAlternateDataStreams(path);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public static LocalSettingsFileIdentity EnsureRegularFileHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var identity = ReadIdentity(handle);
        if (identity.IsDirectory
            || identity.IsReparsePoint
            || identity.NumberOfLinks != 1)
        {
            throw new UnsafeSettingsObjectException(
                "The local settings handle is not an exclusive regular file.");
        }

        return identity;
    }

    public static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void ValidateSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment is "." or ".."
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || segment.Contains(Path.DirectorySeparatorChar)
            || segment.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("A local settings directory segment is invalid.", nameof(segment));
        }
    }

    private static LocalSettingsFileIdentity ReadNoFollowIdentity(string path)
    {
        using var handle = NativeMethods.CreateFile(
            path,
            FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                "The local settings object could not be opened safely.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return ReadIdentity(handle);
    }

    private static LocalSettingsFileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                "The local settings object identity could not be read.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new LocalSettingsFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks,
            (information.FileAttributes & FileAttributes.Directory) != 0,
            (information.FileAttributes & FileAttributes.ReparsePoint) != 0);
    }

    private static void EnsureNoAlternateDataStreams(string path)
    {
        var find = NativeMethods.FindFirstStream(
            path,
            StreamInfoLevels.FindStreamInfoStandard,
            out var data,
            0);
        if (find == InvalidFindHandle)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound
                or ErrorPathNotFound
                or ErrorHandleEof
                or ErrorNoMoreFiles)
            {
                return;
            }

            throw new IOException(
                "The local settings object streams could not be inspected.",
                new Win32Exception(error));
        }

        try
        {
            do
            {
                if (!string.Equals(data.StreamName, "::$DATA", StringComparison.Ordinal))
                {
                    throw new UnsafeSettingsObjectException(
                        "The local settings object has an alternate data stream.");
                }
            }
            while (NativeMethods.FindNextStream(find, out data));

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorHandleEof && error != ErrorNoMoreFiles)
            {
                throw new IOException(
                    "The local settings object streams could not be inspected.",
                    new Win32Exception(error));
            }
        }
        finally
        {
            _ = NativeMethods.FindClose(find);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private enum StreamInfoLevels
    {
        FindStreamInfoStandard,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindFirstStream(
            string fileName,
            StreamInfoLevels infoLevel,
            out Win32FindStreamData findStreamData,
            uint flags);

        [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextStream(
            IntPtr findStream,
            out Win32FindStreamData findStreamData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr findFile);
    }
}

internal readonly record struct LocalSettingsFileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex,
    uint NumberOfLinks,
    bool IsDirectory,
    bool IsReparsePoint)
{
    public bool SameObject(LocalSettingsFileIdentity other) =>
        VolumeSerialNumber == other.VolumeSerialNumber
        && FileIndex == other.FileIndex;
}

internal sealed class UnsafeSettingsObjectException : IOException
{
    public UnsafeSettingsObjectException(string message)
        : base(message)
    {
    }
}
