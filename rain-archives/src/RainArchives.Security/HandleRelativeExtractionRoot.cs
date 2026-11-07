using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

/// <summary>
/// Pins the destination ancestry, creates every staging descendant relative to a
/// verified directory handle, and commits by handle. Path replacement cannot redirect
/// a write or the final same-volume rename.
/// </summary>
internal sealed partial class HandleRelativeExtractionRoot : IDisposable
{
    private const int StreamBufferSize = 1024 * 1024;

    private const uint FileReadDataOrListDirectory = 0x00000001;
    private const uint FileWriteDataOrAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;

    private const uint FileOpen = 0x00000001;
    private const uint FileCreate = 0x00000002;

    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileWriteThrough = 0x00000002;
    private const uint FileSequentialOnly = 0x00000004;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;

    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint ObjectDontReparse = 0x00001000;

    private const uint StatusReparsePointEncountered = 0xC000050B;

    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeNormal = 0x00000080;
    private const int FileRenameInformation = 10;
    private const int FileDispositionInformation = 13;

    private const uint TraverseDirectoryAccess = FileReadDataOrListDirectory
        | FileTraverse
        | FileReadAttributes
        | Synchronize;
    private const uint WritableDirectoryAccess = TraverseDirectoryAccess | FileAddSubdirectory;
    private const uint DirectoryShare = FileShareRead | FileShareWrite;
    private const uint AncestorDirectoryShare = DirectoryShare | FileShareDelete;
    private const uint DirectoryOptions = FileDirectoryFile
        | FileSynchronousIoNonAlert
        | FileOpenReparsePoint;

    private readonly List<SafeFileHandle> _absoluteDirectoryHandles;
    private readonly Dictionary<string, string> _directoryIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fileIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _fileProvenanceStreams =
        new(StringComparer.OrdinalIgnoreCase);
    private SafeFileHandle? _stagingHandle;
    private int _activeOutputFiles;
    private bool _stagingHandlesReleased;
    private bool _disposed;

    private HandleRelativeExtractionRoot(
        List<SafeFileHandle> absoluteDirectoryHandles,
        SafeFileHandle stagingHandle,
        string parentIdentity,
        string stagingIdentity)
    {
        _absoluteDirectoryHandles = absoluteDirectoryHandles;
        _stagingHandle = stagingHandle;
        ParentIdentity = parentIdentity;
        StagingIdentity = stagingIdentity;
        _directoryIdentities.Add(string.Empty, stagingIdentity);
    }

    public string ParentIdentity { get; }

    public string StagingIdentity { get; }

    public static HandleRelativeExtractionRoot Create(string parentPath, string stagingName) =>
        CreateCore(parentPath, stagingName, shareFinalParentDelete: false);

    internal static HandleRelativeExtractionRoot CreateInPinnedParent(
        string parentPath,
        string stagingName) =>
        CreateCore(parentPath, stagingName, shareFinalParentDelete: true);

    private static HandleRelativeExtractionRoot CreateCore(
        string parentPath,
        string stagingName,
        bool shareFinalParentDelete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ValidateSingleComponent(stagingName);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure handle-relative extraction requires Windows.");
        }

        var handles = OpenAbsoluteDirectoryChain(parentPath, shareFinalParentDelete);
        SafeFileHandle? staging = null;
        try
        {
            var parent = handles[^1];
            staging = CreateRelativeHandle(
                parent,
                stagingName,
                WritableDirectoryAccess | Delete,
                DirectoryShare,
                FileCreate,
                DirectoryOptions,
                "the operation staging directory");
            var parentIdentity = ValidateDirectory(parent, expectedIdentity: null);
            var stagingIdentity = ValidateDirectory(staging, expectedIdentity: null);
            return new HandleRelativeExtractionRoot(
                handles,
                staging,
                parentIdentity,
                stagingIdentity);
        }
        catch
        {
            staging?.Dispose();
            DisposeHandles(handles);
            throw;
        }
    }

    public void EnsureDirectory(string relativePath)
    {
        ThrowIfUnavailable();
        using var chain = OpenOrCreateDirectoryChain(relativePath);
    }

    public HandleRelativeOutputFile CreateFile(string relativePath)
    {
        ThrowIfUnavailable();
        var components = SplitRelativePath(relativePath);
        if (components.Length == 0)
        {
            throw new ArgumentException("An output file path must contain a filename.", nameof(relativePath));
        }

        var fileName = components[^1];
        var parentPath = components.Length == 1
            ? string.Empty
            : string.Join(Path.DirectorySeparatorChar, components, 0, components.Length - 1);
        var parentChain = OpenOrCreateDirectoryChain(parentPath);
        SafeFileHandle? fileHandle = null;
        try
        {
            fileHandle = CreateRelativeHandle(
                parentChain.Leaf,
                fileName,
                FileWriteDataOrAddFile | FileReadAttributes | FileWriteAttributes | Synchronize,
                0,
                FileCreate,
                FileNonDirectoryFile | FileSequentialOnly | FileWriteThrough | FileOpenReparsePoint,
                "an extracted file");
            var identity = ValidateFile(fileHandle, expectedIdentity: null);
            if (!_fileIdentities.TryAdd(relativePath, identity))
            {
                throw DestinationChanged(
                    "destination.path_appeared",
                    "An extracted file path was created more than once.");
            }

            _fileProvenanceStreams.Add(relativePath, false);

            var stream = new FileStream(fileHandle, FileAccess.Write, StreamBufferSize, isAsync: true);
            fileHandle = null;
            checked
            {
                _activeOutputFiles++;
            }

            return new HandleRelativeOutputFile(
                this,
                parentChain,
                relativePath,
                fileName,
                identity,
                stream);
        }
        catch
        {
            fileHandle?.Dispose();
            parentChain.Dispose();
            throw;
        }
    }

    public void CommitAs(string destinationName)
    {
        ThrowIfUnavailable();
        ValidateSingleComponent(destinationName);
        if (_activeOutputFiles != 0)
        {
            throw new InvalidOperationException("Output files are still active at commit time.");
        }

        var staging = _stagingHandle
            ?? throw new InvalidOperationException("The staging root is no longer available.");
        var parent = _absoluteDirectoryHandles[^1];
        ValidateDirectory(parent, ParentIdentity);
        ValidateDirectory(staging, StagingIdentity);

        RenameRelativeHandle(staging, parent, destinationName);
        _stagingHandlesReleased = true;
    }

    public StagingTreeManifest ValidateKnownTree()
    {
        ThrowIfUnavailable();
        if (_activeOutputFiles != 0)
        {
            throw new InvalidOperationException("Output files are still active during staging validation.");
        }

        var staging = _stagingHandle
            ?? throw new InvalidOperationException("The staging root is no longer available.");
        ValidateDirectory(staging, StagingIdentity);

        foreach (var pair in _directoryIdentities)
        {
            if (pair.Key.Length == 0)
            {
                continue;
            }

            using var chain = OpenExistingDirectoryChain(pair.Key);
            ValidateDirectory(chain.Leaf, pair.Value);
        }

        foreach (var pair in _fileIdentities)
        {
            var components = SplitRelativePath(pair.Key);
            var fileName = components[^1];
            var parentPath = components.Length == 1
                ? string.Empty
                : string.Join(Path.DirectorySeparatorChar, components, 0, components.Length - 1);
            using var parentChain = OpenExistingDirectoryChain(parentPath);
            using var file = CreateRelativeHandle(
                parentChain.Leaf,
                fileName,
                FileReadAttributes | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                "an extracted file during final staging validation");
            var information = GetInformation(file);
            ValidateFile(file, pair.Value);

            // Extra hard links are tolerated here. Every staged file was created new by this
            // operation and ValidateFile has just proven it is still that exact file, so a link
            // added afterwards cannot redirect the write to some other file. Sync clients such
            // as Google Drive for desktop routinely add short-lived links (for example under
            // ".tmp.driveupload") to files that appear inside a synced folder, and rejecting
            // them made extraction into synced folders fail.

            try
            {
                RequireStableExtractionRecoveryIdentity(
                    information,
                    "An extracted file has no stable filesystem identity.");
                EnsureAllowedExtractionDataStreams(
                    file,
                    requireDefaultStream: true,
                    requireZoneIdentifier: _fileProvenanceStreams[pair.Key]);
            }
            catch (ExtractionTreeObservationException exception)
            {
                throw DestinationChanged(
                    "destination.staging_shape_changed",
                    exception.Message);
            }
        }

        return new StagingTreeManifest(
            _directoryIdentities.Keys.Where(path => path.Length != 0).ToArray(),
            _fileIdentities.Keys.ToArray());
    }

    public ArchiveSourceLease OpenSingleFileSourceLease(
        string relativePath,
        string absolutePath)
    {
        ThrowIfUnavailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (_activeOutputFiles != 0)
        {
            throw new InvalidOperationException("Output files are still active while opening the temporary archive source.");
        }

        if (_directoryIdentities.Count != 1
            || _fileIdentities.Count != 1
            || !_fileIdentities.TryGetValue(relativePath, out var expectedIdentity))
        {
            throw new InvalidOperationException("The temporary archive source tree is not an exact single-file tree.");
        }

        var components = SplitRelativePath(relativePath);
        if (components.Length != 1)
        {
            throw new ArgumentException("The temporary archive source must be a direct child file.", nameof(relativePath));
        }

        SafeFileHandle? file = null;
        try
        {
            var staging = _stagingHandle
                ?? throw new InvalidOperationException("The staging root is no longer available.");
            file = CreateRelativeHandle(
                staging,
                components[0],
                FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
                FileShareRead,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                "the completed temporary archive source");
            var information = GetInformation(file);
            ValidateFile(file, expectedIdentity);
            if (information.NumberOfLinks != 1)
            {
                throw DestinationChanged(
                    "destination.hard_link_appeared",
                    "The temporary archive source acquired an unexpected hard link.");
            }

            var stream = new FileStream(file, FileAccess.Read, bufferSize: 1, isAsync: false);
            file = null;
            return ArchiveSourceLease.AdoptOwnedReadStream(absolutePath, stream);
        }
        finally
        {
            file?.Dispose();
        }
    }

    public void DeleteSingleFileTree(string relativePath)
    {
        ThrowIfUnavailable();
        if (_activeOutputFiles != 0)
        {
            throw new InvalidOperationException("Output files are still active during temporary archive cleanup.");
        }

        if (_directoryIdentities.Count != 1
            || _fileIdentities.Count != 1
            || !_fileIdentities.TryGetValue(relativePath, out var expectedIdentity))
        {
            throw new InvalidOperationException("The temporary archive source tree is not an exact single-file tree.");
        }

        var components = SplitRelativePath(relativePath);
        if (components.Length != 1)
        {
            throw new ArgumentException("The temporary archive source must be a direct child file.", nameof(relativePath));
        }

        var staging = _stagingHandle
            ?? throw new InvalidOperationException("The staging root is no longer available.");
        ValidateDirectory(staging, StagingIdentity);
        using (var file = CreateRelativeHandle(
                   staging,
                   components[0],
                   Delete | FileReadAttributes | Synchronize,
                   FileShareRead | FileShareWrite | FileShareDelete,
                   FileOpen,
                   FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                   "the temporary archive source during cleanup"))
        {
            var information = GetInformation(file);
            ValidateFile(file, expectedIdentity);
            if (information.NumberOfLinks != 1)
            {
                throw DestinationChanged(
                    "destination.hard_link_appeared",
                    "The temporary archive source acquired an unexpected hard link before cleanup.");
            }

            MarkForDeletion(file, "the temporary archive source");
        }

        MarkForDeletion(staging, "the temporary archive directory");
        _stagingHandlesReleased = true;
    }

    public void DeleteEmptyTree()
    {
        ThrowIfUnavailable();
        if (_activeOutputFiles != 0)
        {
            throw new InvalidOperationException("Output files are still active during empty temporary-directory cleanup.");
        }

        if (_directoryIdentities.Count != 1 || _fileIdentities.Count != 0)
        {
            throw new InvalidOperationException("The temporary directory is not an exact empty tree.");
        }

        var staging = _stagingHandle
            ?? throw new InvalidOperationException("The staging root is no longer available.");
        ValidateDirectory(staging, StagingIdentity);
        MarkForDeletion(staging, "the empty temporary directory");
        _stagingHandlesReleased = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stagingHandle?.Dispose();
        _stagingHandle = null;
        DisposeHandles(_absoluteDirectoryHandles);
    }

    private static List<SafeFileHandle> OpenAbsoluteDirectoryChain(
        string directoryPath,
        bool shareFinalParentDelete)
    {
        var canonicalPath = Path.GetFullPath(directoryPath);
        var rootPath = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrEmpty(rootPath))
        {
            throw new IOException("The extraction destination parent must have a filesystem root.");
        }

        var relativeParent = canonicalPath[rootPath.Length..]
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var components = relativeParent.Length == 0
            ? []
            : relativeParent.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        var rootAccess = components.Length == 0 ? WritableDirectoryAccess : TraverseDirectoryAccess;
        var rootShare = components.Length == 0 && !shareFinalParentDelete
            ? DirectoryShare
            : AncestorDirectoryShare;
        var root = NativeMethods.CreateFile(
            rootPath,
            rootAccess,
            rootShare,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (root.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            root.Dispose();
            throw new Win32Exception(error, "Could not pin the extraction destination volume root.");
        }

        var handles = new List<SafeFileHandle>(components.Length + 1) { root };
        try
        {
            ValidateDirectory(root, expectedIdentity: null);
            var current = root;
            for (var index = 0; index < components.Length; index++)
            {
                ValidateSingleComponent(components[index]);
                var access = index == components.Length - 1
                    ? WritableDirectoryAccess
                    : TraverseDirectoryAccess;
                var share = index == components.Length - 1 && !shareFinalParentDelete
                    ? DirectoryShare
                    : AncestorDirectoryShare;
                current = CreateRelativeHandle(
                    current,
                    components[index],
                    access,
                    share,
                    FileOpen,
                    DirectoryOptions,
                    $"extraction destination ancestor component {index + 1}");
                handles.Add(current);
                ValidateDirectory(current, expectedIdentity: null);
            }

            return handles;
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    private PinnedDirectoryChain OpenOrCreateDirectoryChain(string relativePath)
    {
        var staging = _stagingHandle
            ?? throw new InvalidOperationException("The staging root is no longer available.");
        var components = SplitRelativePath(relativePath);
        var openedHandles = new List<SafeFileHandle>(components.Length);
        var current = staging;
        var currentKey = string.Empty;
        try
        {
            foreach (var component in components)
            {
                currentKey = currentKey.Length == 0
                    ? component
                    : $"{currentKey}{Path.DirectorySeparatorChar}{component}";
                var isKnown = _directoryIdentities.TryGetValue(currentKey, out var expectedIdentity);
                current = CreateRelativeHandle(
                    current,
                    component,
                    WritableDirectoryAccess,
                    DirectoryShare,
                    isKnown ? FileOpen : FileCreate,
                    DirectoryOptions,
                    isKnown ? "an operation-owned extraction directory" : "a new extraction directory");
                openedHandles.Add(current);
                var identity = ValidateDirectory(current, expectedIdentity);
                if (!isKnown)
                {
                    _directoryIdentities.Add(currentKey, identity);
                }
            }

            return new PinnedDirectoryChain(staging, openedHandles);
        }
        catch
        {
            DisposeHandles(openedHandles);
            throw;
        }
    }

    private PinnedDirectoryChain OpenExistingDirectoryChain(string relativePath)
    {
        var staging = _stagingHandle
            ?? throw new InvalidOperationException("The staging root is no longer available.");
        var components = SplitRelativePath(relativePath);
        var openedHandles = new List<SafeFileHandle>(components.Length);
        var current = staging;
        var currentKey = string.Empty;
        try
        {
            foreach (var component in components)
            {
                currentKey = currentKey.Length == 0
                    ? component
                    : $"{currentKey}{Path.DirectorySeparatorChar}{component}";
                if (!_directoryIdentities.TryGetValue(currentKey, out var expectedIdentity))
                {
                    throw DestinationChanged(
                        "destination.path_appeared",
                        "An unexpected directory appeared in the operation staging tree.");
                }

                current = CreateRelativeHandle(
                    current,
                    component,
                    TraverseDirectoryAccess,
                    DirectoryShare,
                    FileOpen,
                    DirectoryOptions,
                    "an operation-owned directory during final staging validation");
                openedHandles.Add(current);
                ValidateDirectory(current, expectedIdentity);
            }

            return new PinnedDirectoryChain(staging, openedHandles);
        }
        catch
        {
            DisposeHandles(openedHandles);
            throw;
        }
    }

    private async ValueTask<bool> TryApplyProvenanceAsync(
        SafeFileHandle parent,
        string fileName,
        string expectedFileIdentity,
        DownloadProvenance provenance,
        CancellationToken cancellationToken)
    {
        if (provenance.ZoneId is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(provenance));
        }

        using var baseFile = CreateRelativeHandle(
            parent,
            fileName,
            FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite,
            FileOpen,
            FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
            "an extracted file awaiting provenance");
        ValidateFile(baseFile, expectedFileIdentity);

        try
        {
            using var alternateStreamHandle = CreateRelativeHandle(
                parent,
                $"{fileName}:Zone.Identifier",
                FileWriteDataOrAddFile | FileReadAttributes | Synchronize,
                FileShareRead,
                FileCreate,
                FileNonDirectoryFile | FileSequentialOnly | FileWriteThrough | FileOpenReparsePoint,
                "a download-provenance stream");
            ValidateFile(alternateStreamHandle, expectedFileIdentity);
            await using var stream = new FileStream(
                alternateStreamHandle,
                FileAccess.Write,
                4096,
                isAsync: true);
            var payload = Encoding.ASCII.GetBytes(
                $"[ZoneTransfer]\r\nZoneId={provenance.ZoneId.ToString(CultureInfo.InvariantCulture)}\r\n");
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Win32Exception exception) when (IsOptionalProvenanceFailure(exception.NativeErrorCode))
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (IOException exception) when (IsOptionalProvenanceFailure(exception.HResult & 0xFFFF))
        {
            return false;
        }
    }

    private static bool IsOptionalProvenanceFailure(int win32Code) =>
        win32Code is 1 or 3 or 5 or 50 or 87 or 123 or 282;

    private static void SetLastWriteTimeUtc(SafeFileHandle file, DateTimeOffset modifiedTime)
    {
        var fileTime = modifiedTime.UtcDateTime.ToFileTimeUtc();
        var nativeTime = new NativeFileTime
        {
            Low = unchecked((uint)fileTime),
            High = unchecked((uint)(fileTime >> 32)),
        };
        if (!NativeMethods.SetFileTime(file, IntPtr.Zero, IntPtr.Zero, in nativeTime))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not apply an archive entry timestamp to its pinned output file.");
        }
    }

    private static string ValidateDirectory(SafeFileHandle handle, string? expectedIdentity)
    {
        var information = GetInformation(handle);
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("A path component expected to be a directory is not a directory.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw DestinationChanged(
                "destination.reparse_point_encountered",
                "A reparse point was encountered while opening an extraction directory by handle.");
        }

        return ValidateIdentity(information, expectedIdentity);
    }

    private static string ValidateFile(SafeFileHandle handle, string? expectedIdentity)
    {
        var information = GetInformation(handle);
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new IOException("A path component expected to be a file is a directory.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw DestinationChanged(
                "destination.reparse_point_encountered",
                "A reparse point was encountered while opening an extracted file by handle.");
        }

        return ValidateIdentity(information, expectedIdentity);
    }

    private static string ValidateIdentity(NativeFileInformation information, string? expectedIdentity)
    {
        var identity = FormatIdentity(information);
        if (expectedIdentity is not null
            && !string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
        {
            throw DestinationChanged(
                "destination.identity_changed",
                "An operation-owned extraction path was replaced during the operation.");
        }

        return identity;
    }

    private static NativeFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not inspect a pinned extraction filesystem object.");
        }

        return information;
    }

    private static string FormatIdentity(NativeFileInformation information) =>
        $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";

    private static unsafe SafeFileHandle CreateRelativeHandle(
        SafeFileHandle root,
        string objectName,
        uint desiredAccess,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        string description,
        bool caseInsensitive = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(objectName);
        if (objectName.Length > ushort.MaxValue / sizeof(char))
        {
            throw new PathTooLongException("A native relative path component is too long.");
        }

        var rootReferenceAdded = false;
        IntPtr rawHandle = IntPtr.Zero;
        try
        {
            root.DangerousAddRef(ref rootReferenceAdded);
            fixed (char* nameBuffer = objectName)
            {
                var unicodeName = new NativeUnicodeString
                {
                    Length = checked((ushort)(objectName.Length * sizeof(char))),
                    MaximumLength = checked((ushort)(objectName.Length * sizeof(char))),
                    Buffer = (IntPtr)nameBuffer,
                };
                var attributes = new NativeObjectAttributes
                {
                    Length = checked((uint)Marshal.SizeOf<NativeObjectAttributes>()),
                    RootDirectory = root.DangerousGetHandle(),
                    ObjectName = (IntPtr)(&unicodeName),
                    Attributes = ObjectDontReparse
                        | (caseInsensitive ? ObjectCaseInsensitive : 0),
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero,
                };
                var status = NativeMethods.NtCreateFile(
                    out rawHandle,
                    desiredAccess,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    FileAttributeNormal,
                    shareAccess,
                    createDisposition,
                    createOptions,
                    IntPtr.Zero,
                    0);
                if (status < 0)
                {
                    var error = checked((int)NativeMethods.RtlNtStatusToDosError(status));
                    var unsignedStatus = unchecked((uint)status);
                    if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                    {
                        using var unexpectedHandle = new SafeFileHandle(rawHandle, ownsHandle: true);
                        rawHandle = IntPtr.Zero;
                    }

                    if (unsignedStatus == StatusReparsePointEncountered)
                    {
                        throw DestinationChanged(
                            "destination.reparse_point_encountered",
                            $"A reparse point appeared while Rain Archives was opening {description}.");
                    }

                    if (error is 80 or 183)
                    {
                        throw DestinationChanged(
                            "destination.path_appeared",
                            $"Another filesystem object appeared where Rain Archives was creating {description}.");
                    }

                    if (createDisposition == FileOpen && error is 2 or 3 or 21 or 267)
                    {
                        throw DestinationChanged(
                            "destination.path_replaced",
                            $"An operation-owned filesystem object disappeared or changed type while opening {description}.");
                    }

                    throw new Win32Exception(
                        error,
                        $"Could not open {description} safely by handle (NTSTATUS 0x{unchecked((uint)status):X8}).");
                }
            }

            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            {
                throw new IOException($"Windows returned an invalid handle for {description}.");
            }

            var result = new SafeFileHandle(rawHandle, ownsHandle: true);
            rawHandle = IntPtr.Zero;
            return result;
        }
        finally
        {
            if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
            {
                using var unexpectedHandle = new SafeFileHandle(rawHandle, ownsHandle: true);
            }

            if (rootReferenceAdded)
            {
                root.DangerousRelease();
            }
        }
    }

    private static unsafe void RenameRelativeHandle(
        SafeFileHandle source,
        SafeFileHandle destinationParent,
        string destinationName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationParent);
        ValidateSingleComponent(destinationName);

        var nameLength = checked(destinationName.Length * sizeof(char));
        var rootDirectoryOffset = IntPtr.Size == sizeof(long) ? 8 : 4;
        var nameLengthOffset = checked(rootDirectoryOffset + IntPtr.Size);
        var nameOffset = checked(nameLengthOffset + sizeof(uint));
        var structureSize = checked(
            (nameOffset + sizeof(char) + IntPtr.Size - 1) & ~(IntPtr.Size - 1));
        var bufferLength = checked(structureSize + nameLength);
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)bufferLength);
        if (buffer is null)
        {
            throw new OutOfMemoryException("Could not allocate the native rename buffer.");
        }

        var parentReferenceAdded = false;
        try
        {
            destinationParent.DangerousAddRef(ref parentReferenceAdded);
            *(IntPtr*)(buffer + rootDirectoryOffset) = destinationParent.DangerousGetHandle();
            *(uint*)(buffer + nameLengthOffset) = checked((uint)nameLength);
            destinationName.AsSpan().CopyTo(
                new Span<char>((char*)(buffer + nameOffset), destinationName.Length));

            var status = NativeMethods.NtSetInformationFile(
                source,
                out _,
                (IntPtr)buffer,
                checked((uint)bufferLength),
                FileRenameInformation);
            if (status < 0)
            {
                var error = checked((int)NativeMethods.RtlNtStatusToDosError(status));
                if (error is 80 or 183)
                {
                    throw DestinationChanged(
                        "destination.appeared_before_commit",
                        "The destination appeared before the staged result could be committed.");
                }

                throw new Win32Exception(
                    error,
                    $"Could not atomically commit the staged result relative to its pinned parent handle (NTSTATUS 0x{unchecked((uint)status):X8}).");
            }
        }
        finally
        {
            if (parentReferenceAdded)
            {
                destinationParent.DangerousRelease();
            }

            NativeMemory.Free(buffer);
        }
    }

    private static unsafe void MarkForDeletion(SafeFileHandle handle, string description)
    {
        byte delete = 1;
        var status = NativeMethods.NtSetInformationFile(
            handle,
            out _,
            (IntPtr)(&delete),
            1,
            FileDispositionInformation);
        if (status < 0)
        {
            var error = checked((int)NativeMethods.RtlNtStatusToDosError(status));
            throw new Win32Exception(
                error,
                $"Could not safely delete {description} by handle (NTSTATUS 0x{unchecked((uint)status):X8}).");
        }
    }

    private static string[] SplitRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length == 0)
        {
            return [];
        }

        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        foreach (var component in components)
        {
            ValidateSingleComponent(component);
        }

        return components;
    }

    private static void ValidateSingleComponent(string component)
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        if (component is "." or ".."
            || component.Contains(Path.DirectorySeparatorChar)
            || component.Contains(Path.AltDirectorySeparatorChar)
            || component.Contains('\0'))
        {
            throw new ArgumentException("A native relative path must contain exactly one safe component.");
        }
    }

    private static ArchiveProviderException DestinationChanged(string code, string message) =>
        new(ArchiveErrorCategory.DestinationConflict, code, message);

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }

        handles.Clear();
    }

    private void ReleaseOutputFile()
    {
        if (_activeOutputFiles <= 0)
        {
            throw new InvalidOperationException("The extraction output-file count is inconsistent.");
        }

        _activeOutputFiles--;
    }

    private void MarkProvenanceApplied(string relativePath, string expectedIdentity)
    {
        if (!_fileIdentities.TryGetValue(relativePath, out var identity)
            || !string.Equals(identity, expectedIdentity, StringComparison.Ordinal)
            || !_fileProvenanceStreams.TryGetValue(relativePath, out var applied)
            || applied)
        {
            throw new InvalidOperationException(
                "The extraction provenance state is inconsistent with the owned output file.");
        }

        _fileProvenanceStreams[relativePath] = true;
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (_stagingHandlesReleased)
        {
            throw new InvalidOperationException("The staging handles were released for final commit.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal sealed class HandleRelativeOutputFile : IAsyncDisposable
    {
        private readonly HandleRelativeExtractionRoot _owner;
        private readonly PinnedDirectoryChain _parentChain;
        private readonly string _relativePath;
        private readonly string _fileName;
        private readonly string _fileIdentity;
        private FileStream? _stream;
        private bool _disposed;

        internal HandleRelativeOutputFile(
            HandleRelativeExtractionRoot owner,
            PinnedDirectoryChain parentChain,
            string relativePath,
            string fileName,
            string fileIdentity,
            FileStream stream)
        {
            _owner = owner;
            _parentChain = parentChain;
            _relativePath = relativePath;
            _fileName = fileName;
            _fileIdentity = fileIdentity;
            _stream = stream;
        }

        public Stream Stream => _stream
            ?? throw new InvalidOperationException("The output data stream has already been closed.");

        public void SetLastWriteTimeUtc(DateTimeOffset modifiedTime)
        {
            var stream = _stream
                ?? throw new InvalidOperationException("The output data stream has already been closed.");
            HandleRelativeExtractionRoot.SetLastWriteTimeUtc(stream.SafeFileHandle, modifiedTime);
        }

        public async ValueTask CloseDataAsync()
        {
            if (_stream is null)
            {
                return;
            }

            var stream = _stream;
            _stream = null;
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask<bool> TryApplyProvenanceAsync(
            DownloadProvenance provenance,
            CancellationToken cancellationToken)
        {
            if (_stream is not null)
            {
                throw new InvalidOperationException("Close the output data stream before applying provenance.");
            }

            var applied = await _owner.TryApplyProvenanceAsync(
                _parentChain.Leaf,
                _fileName,
                _fileIdentity,
                provenance,
                cancellationToken).ConfigureAwait(false);
            if (applied)
            {
                _owner.MarkProvenanceApplied(_relativePath, _fileIdentity);
            }

            return applied;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await CloseDataAsync().ConfigureAwait(false);
            }
            finally
            {
                _parentChain.Dispose();
                _owner.ReleaseOutputFile();
            }
        }
    }

    internal sealed class PinnedDirectoryChain : IDisposable
    {
        private readonly List<SafeFileHandle> _ownedHandles;
        private bool _disposed;

        public PinnedDirectoryChain(SafeFileHandle stagingRoot, List<SafeFileHandle> ownedHandles)
        {
            _ownedHandles = ownedHandles;
            Leaf = ownedHandles.Count == 0 ? stagingRoot : ownedHandles[^1];
        }

        public SafeFileHandle Leaf { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeHandles(_ownedHandles);
        }
    }

    internal sealed record StagingTreeManifest(
        IReadOnlyList<string> DirectoryPaths,
        IReadOnlyList<string> FilePaths);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIoStatusBlock
    {
        public IntPtr StatusOrPointer;
        public UIntPtr Information;
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

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetFileTime(
            SafeFileHandle file,
            IntPtr creationTime,
            IntPtr lastAccessTime,
            in NativeFileTime lastWriteTime);

        [LibraryImport("ntdll.dll")]
        public static partial int NtCreateFile(
            out IntPtr fileHandle,
            uint desiredAccess,
            ref NativeObjectAttributes objectAttributes,
            out NativeIoStatusBlock ioStatusBlock,
            IntPtr allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);

        [LibraryImport("ntdll.dll")]
        public static partial int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out NativeIoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

        [LibraryImport("ntdll.dll")]
        public static partial uint RtlNtStatusToDosError(int status);
    }
}
