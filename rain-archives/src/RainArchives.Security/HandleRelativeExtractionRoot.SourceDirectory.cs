using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

internal sealed partial class HandleRelativeExtractionRoot
{
    private const int SourceDirectoryBufferBytes = 64 * 1024;
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int FileIdBothDirectoryNameOffset = 104;
    private const int ErrorNoMoreFiles = 18;

    internal static HandleRelativeSourceDirectory OpenStableSourceDirectory(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Stable handle-relative archive creation directories require Windows.");
        }

        var canonicalPath = Path.GetFullPath(sourcePath);
        List<SafeFileHandle>? handles = null;
        try
        {
            handles = OpenAbsoluteSourceDirectoryChain(canonicalPath);
            var snapshot = CaptureStableSourceDirectorySnapshot(handles[^1]);
            return new HandleRelativeSourceDirectory(canonicalPath, handles, snapshot);
        }
        catch (Exception exception)
        {
            if (handles is not null)
            {
                DisposeHandles(handles);
            }

            if (exception is ArchiveProviderException provider
                && provider.Code.StartsWith("destination.", StringComparison.Ordinal))
            {
                throw new ArchiveProviderException(
                    provider.Code.Contains("reparse", StringComparison.Ordinal)
                        ? ArchiveErrorCategory.UnsafePath
                        : ArchiveErrorCategory.SourceChanged,
                    provider.Code.Contains("reparse", StringComparison.Ordinal)
                        ? "source.reparse_point"
                        : "source.directory_changed",
                    provider.Code.Contains("reparse", StringComparison.Ordinal)
                        ? "Archive creation source directories cannot contain reparse points."
                        : "An archive creation source directory changed while it was being opened.",
                    provider);
            }

            throw;
        }
    }

    private static List<SafeFileHandle> OpenAbsoluteSourceDirectoryChain(string directoryPath)
    {
        var rootPath = Path.GetPathRoot(directoryPath);
        if (string.IsNullOrEmpty(rootPath))
        {
            throw new IOException("The archive creation source directory must have a filesystem root.");
        }

        var relativePath = directoryPath[rootPath.Length..]
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var components = relativePath.Length == 0
            ? []
            : relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        var rootShare = components.Length == 0 ? DirectoryShare : AncestorDirectoryShare;
        var root = NativeMethods.CreateFile(
            rootPath,
            TraverseDirectoryAccess,
            rootShare,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (root.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            root.Dispose();
            throw new Win32Exception(error, "Could not pin the archive creation source volume root.");
        }

        var handles = new List<SafeFileHandle>(components.Length + 1) { root };
        try
        {
            ValidateDirectory(root, expectedIdentity: null);
            var current = root;
            for (var index = 0; index < components.Length; index++)
            {
                ValidateSingleComponent(components[index]);
                var share = index == components.Length - 1
                    ? DirectoryShare
                    : AncestorDirectoryShare;
                current = CreateRelativeHandle(
                    current,
                    components[index],
                    TraverseDirectoryAccess,
                    share,
                    FileOpen,
                    DirectoryOptions,
                    $"archive creation source directory component {index + 1}");
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

    private static OpenDirectorySnapshot CaptureStableSourceDirectorySnapshot(SafeFileHandle directory)
    {
        var information = GetInformation(directory);
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "source.directories_only",
                "A ZIP directory source must identify a directory.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "source.reparse_point",
                "Archive creation source directories cannot be reparse points.");
        }

        var unsignedFileTime = ((ulong)information.LastWriteTime.High << 32)
            | information.LastWriteTime.Low;
        if (unsignedFileTime > long.MaxValue)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "source.timestamp_invalid",
                "An archive creation source directory has an invalid timestamp.");
        }

        return new OpenDirectorySnapshot(
            DateTimeOffset.FromFileTime((long)unsignedFileTime).ToUniversalTime(),
            FormatIdentity(information));
    }

    internal sealed record SourceDirectoryChild(
        string Name,
        FileAttributes Attributes,
        ulong? FileId);

    internal sealed class HandleRelativeSourceDirectory : IDisposable
    {
        private readonly List<SafeFileHandle> _absoluteDirectoryHandles;
        private bool _enumerated;
        private bool _disposed;

        internal HandleRelativeSourceDirectory(
            string sourcePath,
            List<SafeFileHandle> absoluteDirectoryHandles,
            OpenDirectorySnapshot snapshot)
        {
            SourcePath = sourcePath;
            _absoluteDirectoryHandles = absoluteDirectoryHandles;
            Snapshot = snapshot;
        }

        public string SourcePath { get; }

        public OpenDirectorySnapshot Snapshot { get; }

        public unsafe IReadOnlyList<SourceDirectoryChild> EnumerateChildren(
            int maximumChildren,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (maximumChildren < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumChildren));
            }

            if (_enumerated)
            {
                throw new InvalidOperationException(
                    "A pinned source-directory handle can be enumerated only once.");
            }

            _enumerated = true;
            EnsureMatches(Snapshot);
            var children = new List<SourceDirectoryChild>(Math.Min(maximumChildren, 64));
            var buffer = (byte*)NativeMemory.Alloc(SourceDirectoryBufferBytes);
            if (buffer is null)
            {
                throw new OutOfMemoryException("Could not allocate the bounded source-directory buffer.");
            }

            try
            {
                var informationClass = FileIdBothDirectoryRestartInfo;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NativeMemory.Clear(buffer, SourceDirectoryBufferBytes);
                    if (!NativeMethods.GetFileInformationByHandleEx(
                            _absoluteDirectoryHandles[^1],
                            informationClass,
                            buffer,
                            SourceDirectoryBufferBytes))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error == ErrorNoMoreFiles)
                        {
                            break;
                        }

                        throw new Win32Exception(
                            error,
                            "Could not enumerate an archive creation source directory by handle.");
                    }

                    informationClass = FileIdBothDirectoryInfo;
                    var offset = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (offset < 0 || offset > SourceDirectoryBufferBytes - FileIdBothDirectoryNameOffset)
                        {
                            throw InvalidDirectoryBuffer();
                        }

                        var entry = buffer + offset;
                        var nextOffset = *(uint*)entry;
                        var attributes = *(uint*)(entry + 56);
                        var fileNameLength = *(uint*)(entry + 60);
                        var fileId = *(ulong*)(entry + 96);
                        if ((fileNameLength & 1) != 0
                            || fileNameLength > SourceDirectoryBufferBytes - offset - FileIdBothDirectoryNameOffset)
                        {
                            throw InvalidDirectoryBuffer();
                        }

                        var fileName = new string(
                            (char*)(entry + FileIdBothDirectoryNameOffset),
                            0,
                            checked((int)(fileNameLength / sizeof(char))));
                        if (fileName is not "." and not "..")
                        {
                            ValidateSingleComponent(fileName);
                            if (children.Count >= maximumChildren)
                            {
                                throw new ArchiveProviderException(
                                    ArchiveErrorCategory.ResourceLimit,
                                    "creation.entry_count_limit",
                                    "The source directory exceeds the remaining negotiated creation-tree entry budget.");
                            }

                            children.Add(new SourceDirectoryChild(
                                fileName,
                                (FileAttributes)attributes,
                                fileId == 0 ? null : fileId));
                        }

                        if (nextOffset == 0)
                        {
                            break;
                        }

                        if ((nextOffset & 7) != 0
                            || nextOffset < FileIdBothDirectoryNameOffset + fileNameLength
                            || nextOffset > SourceDirectoryBufferBytes - offset)
                        {
                            throw InvalidDirectoryBuffer();
                        }

                        offset = checked(offset + (int)nextOffset);
                    }
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
            }

            children.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.Name, right.Name));
            EnsureMatches(Snapshot);
            return children;
        }

        public HandleRelativeSourceDirectory OpenChildDirectory(
            string name,
            ulong? expectedFileId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateSingleComponent(name);
            SafeFileHandle? child = null;
            try
            {
                child = CreateRelativeHandle(
                    _absoluteDirectoryHandles[^1],
                    name,
                    TraverseDirectoryAccess,
                    DirectoryShare,
                    FileOpen,
                    DirectoryOptions,
                    "an archive creation source child directory");
                var snapshot = CaptureStableSourceDirectorySnapshot(child);
                EnsureEnumeratedFileId(snapshot.Identity, expectedFileId);
                var handles = new List<SafeFileHandle>(1) { child };
                child = null;
                return new HandleRelativeSourceDirectory(
                    Path.Combine(SourcePath, name),
                    handles,
                    snapshot);
            }
            catch (Exception exception)
            {
                child?.Dispose();
                throw TranslateSourceTreeException(exception);
            }
        }

        public HandleRelativeSourceFile OpenChildFile(
            string name,
            ulong? expectedFileId,
            bool requireSingleLink = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateSingleComponent(name);
            SafeFileHandle? file = null;
            try
            {
                file = CreateRelativeHandle(
                    _absoluteDirectoryHandles[^1],
                    name,
                    FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
                    FileShareRead,
                    FileOpen,
                    FileNonDirectoryFile | FileSequentialOnly | FileOpenReparsePoint,
                    "an archive creation source child file");
                var snapshot = CaptureStableSourceSnapshot(file, requireSingleLink);
                EnsureEnumeratedFileId(snapshot.Identity, expectedFileId);
                var stream = new FileStream(
                    file,
                    FileAccess.Read,
                    StreamBufferSize,
                    isAsync: true);
                file = null;
                return new HandleRelativeSourceFile(
                    Path.Combine(SourcePath, name),
                    [],
                    stream,
                    snapshot);
            }
            catch (Exception exception)
            {
                file?.Dispose();
                throw TranslateSourceTreeException(exception);
            }
        }

        public void EnsureMatches(OpenDirectorySnapshot expected)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CaptureStableSourceDirectorySnapshot(_absoluteDirectoryHandles[^1]) != expected)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.SourceChanged,
                    "source.directory_changed",
                    "An archive creation source directory changed after validation.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeHandles(_absoluteDirectoryHandles);
        }

        private static ArchiveProviderException InvalidDirectoryBuffer() =>
            new(
                ArchiveErrorCategory.ProviderFailure,
                "source.directory_enumeration_invalid",
                "Windows returned malformed source-directory enumeration data.");

        private static void EnsureEnumeratedFileId(
            string? openedIdentity,
            ulong? expectedFileId)
        {
            if (expectedFileId is null)
            {
                return;
            }

            var separator = openedIdentity?.IndexOf(':') ?? -1;
            if (separator < 0
                || !ulong.TryParse(
                    openedIdentity![(separator + 1)..],
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var openedFileId)
                || openedFileId != expectedFileId.Value)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.SourceChanged,
                    "source.directory_changed",
                    "A source-tree child was replaced between enumeration and its handle-relative open.");
            }
        }

        private static Exception TranslateSourceTreeException(Exception exception)
        {
            if (exception is not ArchiveProviderException provider
                || !provider.Code.StartsWith("destination.", StringComparison.Ordinal))
            {
                return exception;
            }

            return new ArchiveProviderException(
                provider.Code.Contains("reparse", StringComparison.Ordinal)
                    ? ArchiveErrorCategory.UnsafePath
                    : ArchiveErrorCategory.SourceChanged,
                provider.Code.Contains("reparse", StringComparison.Ordinal)
                    ? "source.reparse_point"
                    : "source.directory_changed",
                provider.Code.Contains("reparse", StringComparison.Ordinal)
                    ? "Archive creation source trees cannot contain reparse points."
                    : "An archive creation source tree changed during traversal.",
                provider);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static unsafe partial bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int fileInformationClass,
            void* fileInformation,
            uint bufferSize);
    }
}
