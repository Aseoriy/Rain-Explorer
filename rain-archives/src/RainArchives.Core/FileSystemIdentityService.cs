using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RainArchives.Core;

public sealed record OpenFileSnapshot(
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? Identity);

public sealed record OpenDirectorySnapshot(
    DateTimeOffset LastWriteTimeUtc,
    string Identity);

public static partial class FileSystemIdentityService
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint InvalidFileAttributes = uint.MaxValue;

    public static OpenFileSnapshot CaptureOpenFile(FileStream stream, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!OperatingSystem.IsWindows())
        {
            var information = new FileInfo(sourcePath);
            information.Refresh();
            return new OpenFileSnapshot(
                stream.Length,
                new DateTimeOffset(information.LastWriteTimeUtc, TimeSpan.Zero),
                null);
        }

        var native = GetInformation(stream.SafeFileHandle);
        return new OpenFileSnapshot(
            GetLength(native),
            GetLastWriteTime(native),
            FormatIdentity(native));
    }

    public static string CaptureExistingDirectoryIdentity(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Operation-owned directory identity checks require Windows.");
        }

        var canonicalPath = Path.GetFullPath(directoryPath);
        using var handle = NativeMethods.CreateFile(
            ToExtendedLengthPath(canonicalPath),
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not open directory identity for '{canonicalPath}'.");
        }

        var native = GetInformation(handle);
        var attributes = (FileAttributes)native.FileAttributes;
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("The path expected to be a directory is not a directory.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Operation paths cannot be reparse points.");
        }

        return FormatIdentity(native);
    }

    public static bool PathEntryExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists(canonicalPath) || Directory.Exists(canonicalPath);
        }

        var attributes = NativeMethods.GetFileAttributes(ToExtendedLengthPath(canonicalPath));
        if (attributes != InvalidFileAttributes)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is 2 or 3)
        {
            return false;
        }

        throw new Win32Exception(error, $"Could not inspect destination path '{canonicalPath}'.");
    }

    private static NativeFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not query filesystem object identity.");
        }

        return information;
    }

    private static long GetLength(NativeFileInformation information)
    {
        var unsignedLength = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (unsignedLength > long.MaxValue)
        {
            throw new IOException("The filesystem object length exceeds the supported range.");
        }

        return (long)unsignedLength;
    }

    private static DateTimeOffset GetLastWriteTime(NativeFileInformation information)
    {
        var unsignedFileTime = ((ulong)information.LastWriteTime.High << 32)
            | information.LastWriteTime.Low;
        if (unsignedFileTime > long.MaxValue)
        {
            throw new IOException("The filesystem timestamp exceeds the supported range.");
        }

        return DateTimeOffset.FromFileTime((long)unsignedFileTime).ToUniversalTime();
    }

    private static string FormatIdentity(NativeFileInformation information) =>
        $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";

    private static string ToExtendedLengthPath(string canonicalPath)
    {
        if (canonicalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return canonicalPath;
        }

        return canonicalPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + canonicalPath[2..]
            : @"\\?\" + canonicalPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetFileInformationByHandle(
            SafeFileHandle file,
            out NativeFileInformation information);

        [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint GetFileAttributes(string fileName);
    }
}
