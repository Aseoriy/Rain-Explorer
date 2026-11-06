using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Providers.WinRar;

internal sealed record WinRarWorkspaceMaintenanceResult(
    int CleanedWorkspaceCount,
    int PreservedWorkspaceCount,
    int SuspiciousWorkspaceCount,
    bool IsTruncated,
    bool InspectionIncomplete)
{
    internal bool RequiresAttention =>
        PreservedWorkspaceCount > 0
        || SuspiciousWorkspaceCount > 0
        || IsTruncated
        || InspectionIncomplete;
}

/// <summary>
/// Owns the private filesystem bridge required by the separately installed RAR
/// CLI. The exclusive owner claim distinguishes active work from a hard-kill
/// remnant; a later Worker removes only token-bound, non-reparse remnants.
/// </summary>
internal sealed partial class WinRarEncoderWorkspace : IDisposable
{
    private const string WorkspacePrefix = "rainarchives-rar-";
    private const string OwnerFileName = "owner.lock";
    private const string OwnerRecordPrefix = "RainArchives RAR encoder workspace v1\n";
    private const int TokenCharacters = 64;
    private const int MaximumMaintenanceEntries = 1_024;
    private const int MaximumMaintenanceObjects = 16_384;
    private const int MaximumWorkspaceObjects = 1_000_000;
    private static readonly TimeSpan MaximumMaintenanceDuration = TimeSpan.FromSeconds(2);
    private const uint GenericRead = 0x80000000;
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileDispositionInfo = 4;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorFileExists = 80;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private readonly SafeFileHandle _directoryHandle;
    private FileStream? _owner;
    private bool _disposed;

    private WinRarEncoderWorkspace(
        string directoryPath,
        SafeFileHandle directoryHandle,
        FileStream owner)
    {
        DirectoryPath = directoryPath;
        _directoryHandle = directoryHandle;
        _owner = owner;
    }

    internal string DirectoryPath { get; }

    internal static string GetDefaultRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SailHub",
        "Rain Archives",
        "Rar Encoder Workspaces");

    internal static WinRarEncoderWorkspace Create(string? rootDirectory = null)
    {
        var parent = Path.GetFullPath(rootDirectory ?? GetDefaultRootDirectory());
        EnsurePrivateRoot(parent);

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var workspaceName = WorkspacePrefix + token;
            var directoryPath = Path.Combine(parent, workspaceName);
            if (!CreateDirectory(directoryPath, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ErrorAlreadyExists or ErrorFileExists)
                {
                    continue;
                }

                throw new Win32Exception(error, "The private RAR encoder workspace could not be created.");
            }

            SafeFileHandle? directory = null;
            FileStream? owner = null;
            try
            {
                directory = OpenDirectory(directoryPath, shareDelete: false);
                EnsureDirectoryHandleIsOrdinary(directory);
                var ownerPath = Path.Combine(directoryPath, OwnerFileName);
                owner = new FileStream(
                    ownerPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 256,
                    FileOptions.WriteThrough);
                var record = CreateOwnerRecord(workspaceName);
                try
                {
                    owner.Write(record);
                    owner.Flush(flushToDisk: true);
                    owner.Position = 0;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(record);
                }

                var result = new WinRarEncoderWorkspace(directoryPath, directory, owner);
                directory = null;
                owner = null;
                return result;
            }
            catch
            {
                owner?.Dispose();
                if (directory is not null)
                {
                    TryDeleteWorkspace(directoryPath, directory, workspaceName, requireOwner: false);
                }

                throw;
            }
            finally
            {
                owner?.Dispose();
                directory?.Dispose();
            }
        }

        throw new IOException("A unique private RAR encoder workspace could not be allocated.");
    }

    internal static WinRarWorkspaceMaintenanceResult PerformStartupMaintenance(
        string? rootDirectory = null,
        int maximumEntries = MaximumMaintenanceEntries,
        int maximumObjects = MaximumMaintenanceObjects,
        TimeSpan? maximumDuration = null)
    {
        if (maximumEntries <= 0 || maximumEntries > MaximumMaintenanceEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        if (maximumObjects <= 0 || maximumObjects > MaximumWorkspaceObjects)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumObjects));
        }

        var duration = maximumDuration ?? MaximumMaintenanceDuration;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        var parent = Path.GetFullPath(rootDirectory ?? GetDefaultRootDirectory());
        try
        {
            EnsurePrivateRoot(parent);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArchiveProviderException)
        {
            return new WinRarWorkspaceMaintenanceResult(0, 0, 0, false, true);
        }

        var cleaned = 0;
        var preserved = 0;
        var suspicious = 0;
        var truncated = false;
        var incomplete = false;
        var budget = new MaintenanceBudget(maximumObjects, duration);
        try
        {
            var examined = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         parent,
                         WorkspacePrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (examined == maximumEntries || !budget.TryCheckTime())
                {
                    truncated = true;
                    break;
                }

                examined++;
                var workspaceName = Path.GetFileName(path);
                if (!TryGetToken(workspaceName, out _))
                {
                    suspicious++;
                    continue;
                }

                SafeFileHandle? directory = null;
                try
                {
                    directory = OpenDirectory(path, shareDelete: false);
                    EnsureDirectoryHandleIsOrdinary(directory);
                    if (!OwnerRecordMatches(Path.Combine(path, OwnerFileName), workspaceName))
                    {
                        suspicious++;
                        continue;
                    }

                    TryDeleteWorkspace(
                        path,
                        directory,
                        workspaceName,
                        requireOwner: true,
                        budget);
                    directory = null;
                    cleaned++;
                }
                catch (MaintenanceBudgetExceededException)
                {
                    truncated = true;
                    break;
                }
                catch (Win32Exception exception) when (
                    exception.NativeErrorCode is ErrorSharingViolation or ErrorLockViolation)
                {
                    // A live Worker holds the workspace directory and owner claim
                    // without delete sharing. Active work is neither stale nor suspicious.
                }
                catch (IOException exception) when (IsSharingViolation(exception))
                {
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or ArchiveProviderException
                        or Win32Exception)
                {
                    preserved++;
                }
                finally
                {
                    directory?.Dispose();
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            incomplete = true;
        }

        return new WinRarWorkspaceMaintenanceResult(
            cleaned,
            preserved,
            suspicious,
            truncated,
            incomplete);
    }

    internal void Cleanup()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner?.Dispose();
        _owner = null;
        TryDeleteWorkspace(
            DirectoryPath,
            _directoryHandle,
            Path.GetFileName(DirectoryPath),
            requireOwner: true);
        _disposed = true;
    }

    internal void AbandonForTest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner?.Dispose();
        _owner = null;
        _directoryHandle.Dispose();
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Cleanup();
        }
        catch
        {
            // A failed proof is intentionally preserved for bounded startup
            // maintenance instead of following or recursively deleting it blindly.
            _disposed = true;
            _owner?.Dispose();
            _owner = null;
            _directoryHandle.Dispose();
        }
    }

    private static void TryDeleteWorkspace(
        string path,
        SafeFileHandle directory,
        string workspaceName,
        bool requireOwner,
        MaintenanceBudget? maintenanceBudget = null)
    {
        EnsureDirectoryHandleIsOrdinary(directory);
        if (requireOwner && !OwnerRecordMatches(Path.Combine(path, OwnerFileName), workspaceName))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "rar.workspace_owner_changed",
                "The private RAR encoder workspace ownership claim changed.");
        }

        var tree = InspectWorkspaceTree(path, maintenanceBudget);
        DeleteTreeContents(path, tree);
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (SetFileInformationByHandle(
                directory,
                FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The empty private RAR encoder workspace could not be removed by handle.");
        }

        directory.Dispose();
    }

    private static WorkspaceTree InspectWorkspaceTree(
        string root,
        MaintenanceBudget? maintenanceBudget)
    {
        var pending = new Stack<string>();
        var files = new List<string>();
        var directories = new List<string>();
        pending.Push(root);
        var objectCount = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                maintenanceBudget?.ConsumeObject();
                if (++objectCount > MaximumWorkspaceObjects)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.ResourceLimit,
                        "rar.workspace_object_limit",
                        "The private RAR encoder workspace exceeded its bounded object count.");
                }

                var attributes = File.GetAttributes(child);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.UnsafePath,
                        "rar.workspace_reparse_detected",
                        "The private RAR encoder workspace acquired a reparse point.");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    directories.Add(child);
                    pending.Push(child);
                }
                else
                {
                    files.Add(child);
                }
            }
        }

        return new WorkspaceTree(files, directories);
    }

    private static void DeleteTreeContents(string root, WorkspaceTree tree)
    {
        var ownerPath = Path.Combine(root, OwnerFileName);
        foreach (var file in tree.Files)
        {
            if (!string.Equals(file, ownerPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }

        foreach (var directory in tree.Directories
                     .OrderByDescending(path => path.Length)
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            Directory.Delete(directory, recursive: false);
        }

        if (tree.Files.Any(path =>
                string.Equals(path, ownerPath, StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(ownerPath);
        }
    }

    private static bool OwnerRecordMatches(string ownerPath, string workspaceName)
    {
        byte[]? actual = null;
        var expected = CreateOwnerRecord(workspaceName);
        try
        {
            using var ownerHandle = OpenOwnerFile(ownerPath);
            EnsureOwnerHandleIsOrdinary(ownerHandle);
            using var owner = new FileStream(
                ownerHandle,
                FileAccess.Read,
                bufferSize: 256,
                isAsync: false);
            if (owner.Length != expected.Length)
            {
                return false;
            }

            actual = new byte[expected.Length];
            owner.ReadExactly(actual);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            if (actual is not null)
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    private static byte[] CreateOwnerRecord(string workspaceName)
    {
        if (!TryGetToken(workspaceName, out var token))
        {
            throw new ArgumentException("The RAR workspace name is invalid.", nameof(workspaceName));
        }

        return Encoding.ASCII.GetBytes(OwnerRecordPrefix + token + "\n");
    }

    private static bool TryGetToken(string? workspaceName, out string token)
    {
        token = string.Empty;
        if (workspaceName is null
            || workspaceName.Length != WorkspacePrefix.Length + TokenCharacters
            || !workspaceName.StartsWith(WorkspacePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = workspaceName.AsSpan(WorkspacePrefix.Length);
        foreach (var character in candidate)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        token = candidate.ToString();
        return true;
    }

    private static SafeFileHandle OpenDirectory(string path, bool shareDelete)
    {
        var share = FileShareRead | FileShareWrite | (shareDelete ? 0x00000004u : 0u);
        var handle = CreateFile(
            ToExtendedLengthPath(path),
            GenericRead | Delete | Synchronize,
            share,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The private RAR encoder workspace could not be pinned.");
        }

        return handle;
    }

    private static SafeFileHandle OpenOwnerFile(string path)
    {
        var handle = CreateFile(
            ToExtendedLengthPath(path),
            GenericRead,
            0,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "The private RAR encoder workspace ownership claim could not be opened.");
        }

        return handle;
    }

    private static void EnsureDirectoryHandleIsOrdinary(SafeFileHandle handle)
    {
        if (GetFileInformationByHandle(handle, out var information) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The private RAR encoder workspace identity could not be read.");
        }

        var attributes = (FileAttributes)information.FileAttributes;
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "rar.workspace_ancestry_unsafe",
                "The private RAR encoder workspace is not an ordinary directory.");
        }
    }

    private static void EnsureOwnerHandleIsOrdinary(SafeFileHandle handle)
    {
        if (GetFileInformationByHandle(handle, out var information) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The private RAR encoder workspace ownership identity could not be read.");
        }

        var attributes = (FileAttributes)information.FileAttributes;
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint)
            || information.NumberOfLinks != 1)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "rar.workspace_owner_unsafe",
                "The private RAR encoder workspace ownership claim is not an ordinary single-link file.");
        }
    }

    private static void EnsurePrivateRoot(string path)
    {
        var missing = new Stack<string>();
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (!current.Exists)
        {
            missing.Push(current.FullName);
            current = current.Parent
                ?? throw new DirectoryNotFoundException(
                    "The private RAR encoder workspace root has no existing ancestor.");
        }

        EnsureExistingDirectoryHasNoReparsePoints(current.FullName);
        while (missing.TryPop(out var directory))
        {
            if (!CreateDirectory(directory, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is not ErrorAlreadyExists and not ErrorFileExists)
                {
                    throw new Win32Exception(
                        error,
                        "The private RAR encoder workspace root could not be created.");
                }
            }

            using var handle = OpenDirectory(directory, shareDelete: true);
            EnsureDirectoryHandleIsOrdinary(handle);
        }

        EnsureExistingDirectoryHasNoReparsePoints(path);
    }

    private static void EnsureExistingDirectoryHasNoReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (!current.Exists || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsafePath,
                    "rar.workspace_ancestry_unsafe",
                    "The private RAR encoder workspace ancestry is unavailable or contains a reparse point.");
            }

            current = current.Parent;
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }

    private sealed record WorkspaceTree(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Directories);

    private sealed class MaintenanceBudget
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly TimeSpan _maximumDuration;
        private int _remainingObjects;

        internal MaintenanceBudget(int maximumObjects, TimeSpan maximumDuration)
        {
            _remainingObjects = maximumObjects;
            _maximumDuration = maximumDuration;
        }

        internal bool TryCheckTime() => _stopwatch.Elapsed < _maximumDuration;

        internal void ConsumeObject()
        {
            if (_remainingObjects-- <= 0 || !TryCheckTime())
            {
                throw new MaintenanceBudgetExceededException();
            }
        }
    }

    private sealed class MaintenanceBudgetExceededException : Exception
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public int DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // These are internal, canonical workspace paths, not new source-path authority.
    // Explicit extended paths work even when the Worker host does not opt into
    // longPathAware or the machine's LongPathsEnabled setting is off.
    internal static string ToExtendedLengthPath(string path)
    {
        var canonical = Path.GetFullPath(path);
        if (canonical.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return canonical;
        }

        return canonical.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + canonical[2..]
            : @"\\?\" + canonical;
    }

    private static bool CreateDirectory(string path, IntPtr securityAttributes) =>
        CreateDirectoryNative(ToExtendedLengthPath(path), securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryNative(string path, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}
