using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

internal enum StagedProvenancePreparationStatus
{
    NotRequested,
    Prepared,
    Unsupported,
}

internal sealed partial class HandleRelativeExtractionRoot
{
    internal static HandleRelativeStagedFile CreateStagedFile(
        string parentPath,
        string stagingName,
        bool prepareProvenanceStream = false,
        bool prepareReadback = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ValidateSingleComponent(stagingName);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Secure handle-relative archive creation requires Windows.");
        }

        var handles = OpenAbsoluteDirectoryChain(parentPath, shareFinalParentDelete: false);
        SafeFileHandle? file = null;
        SafeFileHandle? provenanceHandle = null;
        FileStream? provenanceStream = null;
        var deletionDispositionEstablished = false;
        var provenancePreparation = prepareProvenanceStream
            ? StagedProvenancePreparationStatus.Unsupported
            : StagedProvenancePreparationStatus.NotRequested;
        try
        {
            var parent = handles[^1];
            var parentIdentity = ValidateDirectory(parent, expectedIdentity: null);
            file = CreateRelativeHandle(
                parent,
                stagingName,
                FileWriteDataOrAddFile
                    | FileReadAttributes
                    | FileWriteAttributes
                    | Delete
                    | Synchronize
                    | (prepareReadback ? FileReadDataOrListDirectory : 0),
                0,
                FileCreate,
                FileNonDirectoryFile
                    | FileWriteThrough
                    | (prepareReadback ? 0 : FileSequentialOnly)
                    | FileOpenReparsePoint,
                "the operation-owned archive staging file");
            var fileIdentity = ValidateFile(file, expectedIdentity: null);

            if (prepareProvenanceStream)
            {
                try
                {
                    provenanceHandle = CreateRelativeHandle(
                        parent,
                        $"{stagingName}:Zone.Identifier",
                        FileReadDataOrListDirectory
                            | FileWriteDataOrAddFile
                            | FileReadAttributes
                            | Delete
                            | Synchronize,
                        FileShareDelete,
                        FileCreate,
                        FileNonDirectoryFile
                            | FileSequentialOnly
                            | FileWriteThrough
                            | FileOpenReparsePoint,
                        "the staged archive download-provenance stream");
                }
                catch (Win32Exception exception)
                    when (IsOptionalProvenanceFailure(exception.NativeErrorCode))
                {
                    provenanceHandle?.Dispose();
                    provenanceHandle = null;
                }
            }

            try
            {
                MarkForDeletion(file, "the operation-owned archive staging file");
            }
            catch (Win32Exception exception)
                when (provenanceHandle is not null
                    && IsOptionalProvenanceFailure(exception.NativeErrorCode))
            {
                MarkForDeletion(
                    provenanceHandle,
                    "the unsupported staged archive download-provenance stream");
                provenanceHandle.Dispose();
                provenanceHandle = null;
                provenancePreparation = StagedProvenancePreparationStatus.Unsupported;
                MarkForDeletion(file, "the operation-owned archive staging file");
            }

            deletionDispositionEstablished = true;
            if (provenanceHandle is not null)
            {
                ValidateFile(provenanceHandle, fileIdentity);
                provenanceStream = new FileStream(
                    provenanceHandle,
                    FileAccess.ReadWrite,
                    DownloadProvenanceService.MaximumZoneIdentifierBytes,
                    isAsync: true);
                provenanceHandle = null;
                provenancePreparation = StagedProvenancePreparationStatus.Prepared;
            }

            var stream = new FileStream(
                file,
                prepareReadback ? FileAccess.ReadWrite : FileAccess.Write,
                StreamBufferSize,
                isAsync: true);
            file = null;
            return new HandleRelativeStagedFile(
                handles,
                stream,
                parentIdentity,
                fileIdentity,
                provenanceStream,
                provenancePreparation);
        }
        catch (Exception operationFailure)
        {
            Exception? cleanupFailure = null;
            try
            {
                provenanceHandle?.Dispose();
                provenanceStream?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            if (file is not null)
            {
                if (!deletionDispositionEstablished)
                {
                    try
                    {
                        MarkForDeletion(file, "the failed archive staging file");
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure = cleanupFailure is null
                            ? exception
                            : new AggregateException(
                                "Multiple staged-archive setup cleanup steps failed.",
                                cleanupFailure,
                                exception);
                    }
                }

                file.Dispose();
            }

            try
            {
                DisposeHandles(handles);
            }
            catch (Exception exception)
            {
                cleanupFailure = cleanupFailure is null
                    ? exception
                    : new AggregateException(
                        "Multiple staged-archive setup cleanup steps failed.",
                        cleanupFailure,
                        exception);
            }

            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Archive staging setup and cleanup both failed.",
                    operationFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
            throw;
        }
    }

    internal static HandleRelativeSourceFile OpenStableSourceFile(
        string sourcePath,
        bool requireSingleLink = false,
        bool retainAncestry = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Stable handle-relative archive creation sources require Windows.");
        }

        var canonicalPath = Path.GetFullPath(sourcePath);
        var parentPath = Path.GetDirectoryName(canonicalPath)
            ?? throw new IOException("The archive creation source must have a parent directory.");
        var fileName = Path.GetFileName(canonicalPath);
        ValidateSingleComponent(fileName);

        List<SafeFileHandle>? handles = null;
        SafeFileHandle? file = null;
        FileStream? stream = null;
        try
        {
            handles = OpenAbsoluteDirectoryChain(parentPath, shareFinalParentDelete: false);
            file = CreateRelativeHandle(
                handles[^1],
                fileName,
                FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
                FileShareRead,
                FileOpen,
                FileNonDirectoryFile | FileSequentialOnly | FileOpenReparsePoint,
                "an archive creation source file");
            var snapshot = CaptureStableSourceSnapshot(file, requireSingleLink);
            stream = new FileStream(
                file,
                FileAccess.Read,
                StreamBufferSize,
                isAsync: true);
            file = null;
            if (!retainAncestry)
            {
                DisposeHandles(handles);
            }

            var result = new HandleRelativeSourceFile(canonicalPath, handles, stream, snapshot);
            stream = null;
            return result;
        }
        catch (Exception exception)
        {
            stream?.Dispose();
            file?.Dispose();
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
                        : "source.changed_before_creation",
                    provider.Code.Contains("reparse", StringComparison.Ordinal)
                        ? "Archive creation source paths cannot contain reparse points."
                        : "An archive creation source changed before it could be opened safely.",
                    provider);
            }

            throw;
        }
    }

    private static OpenFileSnapshot CaptureStableSourceSnapshot(
        SafeFileHandle file,
        bool requireSingleLink = false)
    {
        var information = GetInformation(file);
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "source.regular_files_only",
                "This ZIP creation slice accepts explicit regular files only.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "source.reparse_point",
                "Archive creation source files cannot be reparse points.");
        }

        if (requireSingleLink && information.NumberOfLinks != 1)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "source.hard_link_refused",
                "ZIP Rebuild Copy source files must have exactly one filesystem link.");
        }

        var unsignedLength = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (unsignedLength > long.MaxValue)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "source.length_overflow",
                "An archive creation source length exceeds the supported range.");
        }

        var unsignedFileTime = ((ulong)information.LastWriteTime.High << 32)
            | information.LastWriteTime.Low;
        if (unsignedFileTime > long.MaxValue)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "source.timestamp_invalid",
                "An archive creation source has an invalid timestamp.");
        }

        return new OpenFileSnapshot(
            (long)unsignedLength,
            DateTimeOffset.FromFileTime((long)unsignedFileTime).ToUniversalTime(),
            FormatIdentity(information));
    }

    private static unsafe void ClearDeletionDisposition(SafeFileHandle handle)
    {
        byte delete = 0;
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
                $"Could not prepare the staged archive for commit (NTSTATUS 0x{unchecked((uint)status):X8}).");
        }
    }

    internal sealed class HandleRelativeStagedFile : IAsyncDisposable
    {
        private readonly List<SafeFileHandle> _absoluteDirectoryHandles;
        private FileStream? _stream;
        private FileStream? _provenanceStream;
        private readonly string _fileIdentity;
        private bool _writingCompleted;
        private bool _deleteOnCloseCleared;
        private bool _committed;
        private bool _disposed;

        internal HandleRelativeStagedFile(
            List<SafeFileHandle> absoluteDirectoryHandles,
            FileStream stream,
            string parentIdentity,
            string fileIdentity,
            FileStream? provenanceStream,
            StagedProvenancePreparationStatus provenancePreparation)
        {
            _absoluteDirectoryHandles = absoluteDirectoryHandles;
            _stream = stream;
            _provenanceStream = provenanceStream;
            ParentIdentity = parentIdentity;
            _fileIdentity = fileIdentity;
            ProvenancePreparation = provenancePreparation;
        }

        public string ParentIdentity { get; }

        internal string FileIdentity => _fileIdentity;

        internal long Length
        {
            get
            {
                ThrowIfDisposed();
                return _stream!.Length;
            }
        }

        internal StagedProvenancePreparationStatus ProvenancePreparation { get; }

        public Stream Stream => _stream
            ?? throw new ObjectDisposedException(nameof(HandleRelativeStagedFile));

        public async ValueTask CompleteWritingAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var stream = _stream!;
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            _writingCompleted = true;
        }

        internal Stream GetReadbackStream()
        {
            ThrowIfDisposed();
            if (!_writingCompleted)
            {
                throw new InvalidOperationException(
                    "The staged archive must be durably flushed before validation.");
            }

            var stream = _stream!;
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw new InvalidOperationException(
                    "This staged archive was not prepared for final readback validation.");
            }

            stream.Position = 0;
            return stream;
        }

        internal async ValueTask ApplyPreparedProvenanceAsync(
            DownloadProvenance provenance,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!_writingCompleted)
            {
                throw new InvalidOperationException(
                    "The staged archive must be durably flushed before provenance is applied.");
            }

            var provenanceStream = _provenanceStream
                ?? throw new InvalidOperationException(
                    "No download-provenance stream was prepared for this staged archive.");
            var payload = DownloadProvenanceService.CreateCanonicalPayload(provenance);
            cancellationToken.ThrowIfCancellationRequested();
            provenanceStream.Position = 0;
            provenanceStream.SetLength(0);
            await provenanceStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await provenanceStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            provenanceStream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();

            provenanceStream.Position = 0;
            var readback = new byte[payload.Length];
            await provenanceStream.ReadExactlyAsync(readback, cancellationToken).ConfigureAwait(false);
            if (provenanceStream.Position != provenanceStream.Length
                || !readback.AsSpan().SequenceEqual(payload))
            {
                throw new IOException(
                    "The staged archive download-provenance stream could not be verified.");
            }

            await provenanceStream.DisposeAsync().ConfigureAwait(false);
            _provenanceStream = null;
            ValidateFile(_stream!.SafeFileHandle, _fileIdentity);
        }

        public void CommitAs(string destinationName)
        {
            ThrowIfDisposed();
            ValidateSingleComponent(destinationName);
            if (!_writingCompleted)
            {
                throw new InvalidOperationException(
                    "The staged archive must be durably flushed before commit.");
            }

            if (_provenanceStream is not null)
            {
                throw new InvalidOperationException(
                    "Prepared archive provenance must be applied and verified before commit.");
            }

            var stream = _stream!;
            var parent = _absoluteDirectoryHandles[^1];
            ValidateDirectory(parent, ParentIdentity);
            var information = GetInformation(stream.SafeFileHandle);
            ValidateFile(stream.SafeFileHandle, _fileIdentity);
            if (information.NumberOfLinks > 1)
            {
                throw DestinationChanged(
                    "destination.hard_link_appeared",
                    "The staged archive acquired an unexpected hard link before commit.");
            }

            ClearDeletionDisposition(stream.SafeFileHandle);
            _deleteOnCloseCleared = true;
            try
            {
                information = GetInformation(stream.SafeFileHandle);
                ValidateFile(stream.SafeFileHandle, _fileIdentity);
                if (information.NumberOfLinks != 1)
                {
                    throw DestinationChanged(
                        "destination.hard_link_appeared",
                        "The staged archive did not return to one link after its cleanup disposition was cleared.");
                }

                RenameRelativeHandle(stream.SafeFileHandle, parent, destinationName);
                _committed = true;
            }
            catch (Exception commitFailure)
            {
                try
                {
                    MarkForDeletion(stream.SafeFileHandle, "the uncommitted archive staging file");
                    _deleteOnCloseCleared = false;
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Archive commit and staging cleanup both failed.",
                        commitFailure,
                        cleanupFailure);
                }

                ExceptionDispatchInfo.Capture(commitFailure).Throw();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Exception? failure = null;
            try
            {
                if (_provenanceStream is not null)
                {
                    await _provenanceStream.DisposeAsync().ConfigureAwait(false);
                    _provenanceStream = null;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (!_committed && _deleteOnCloseCleared && _stream is not null)
            {
                try
                {
                    MarkForDeletion(_stream.SafeFileHandle, "the uncommitted archive staging file");
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            try
            {
                if (_stream is not null)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    _stream = null;
                }
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException("Multiple staged-archive cleanup steps failed.", failure, exception);
            }

            try
            {
                DisposeHandles(_absoluteDirectoryHandles);
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException("Multiple staged-archive cleanup steps failed.", failure, exception);
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed || _stream is null, this);
    }

    internal sealed class HandleRelativeSourceFile : IDisposable, IAsyncDisposable
    {
        private readonly List<SafeFileHandle> _absoluteDirectoryHandles;
        private FileStream? _stream;
        private bool _disposed;

        internal HandleRelativeSourceFile(
            string sourcePath,
            List<SafeFileHandle> absoluteDirectoryHandles,
            FileStream stream,
            OpenFileSnapshot snapshot)
        {
            SourcePath = sourcePath;
            _absoluteDirectoryHandles = absoluteDirectoryHandles;
            _stream = stream;
            Snapshot = snapshot;
        }

        public string SourcePath { get; }
        public OpenFileSnapshot Snapshot { get; }
        public Stream Stream => _stream
            ?? throw new ObjectDisposedException(nameof(HandleRelativeSourceFile));

        public void EnsureMatches(OpenFileSnapshot expected)
        {
            ArgumentNullException.ThrowIfNull(expected);
            var stream = _stream
                ?? throw new ObjectDisposedException(nameof(HandleRelativeSourceFile));
            if (CaptureStableSourceSnapshot(stream.SafeFileHandle) != expected)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.SourceChanged,
                    "source.changed_during_creation",
                    "An archive creation source changed after validation.");
            }
        }

        internal async ValueTask<DownloadProvenanceState> ReadProvenanceStateAsync(
            CancellationToken cancellationToken)
        {
            var stream = _stream
                ?? throw new ObjectDisposedException(nameof(HandleRelativeSourceFile));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureMatches(Snapshot);

            SafeFileHandle? provenanceHandle = null;
            try
            {
                try
                {
                    provenanceHandle = CreateRelativeHandle(
                        _absoluteDirectoryHandles[^1],
                        $"{Path.GetFileName(SourcePath)}:Zone.Identifier",
                        FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
                        FileShareRead | FileShareDelete,
                        FileOpen,
                        FileNonDirectoryFile
                            | FileSequentialOnly
                            | FileOpenReparsePoint,
                        "an archive creation source download-provenance stream");
                }
                catch (ArchiveProviderException exception)
                    when (exception.Code == "destination.path_replaced")
                {
                    EnsureMatches(Snapshot);
                    return DownloadProvenanceState.Absent;
                }
                catch (Win32Exception exception)
                    when (IsUnsupportedSourceProvenanceError(exception.NativeErrorCode))
                {
                    EnsureMatches(Snapshot);
                    return DownloadProvenanceState.Unsupported;
                }
                catch (Exception exception) when (
                    exception is Win32Exception
                        or UnauthorizedAccessException
                        or IOException
                        or NotSupportedException)
                {
                    EnsureMatches(Snapshot);
                    return DownloadProvenanceState.Inaccessible;
                }

                try
                {
                    ValidateFile(
                        provenanceHandle,
                        Snapshot.Identity
                            ?? throw new IOException(
                                "Windows did not return a stable source-file identity."));
                }
                catch (ArchiveProviderException exception)
                {
                    throw SourceProvenanceChanged(exception);
                }

                var information = GetInformation(provenanceHandle);
                var unsignedLength = ((ulong)information.FileSizeHigh << 32)
                    | information.FileSizeLow;
                if (unsignedLength > long.MaxValue)
                {
                    throw SourceProvenanceChanged();
                }

                var length = (long)unsignedLength;
                await using var provenanceStream = new FileStream(
                    provenanceHandle,
                    FileAccess.Read,
                    DownloadProvenanceService.MaximumZoneIdentifierBytes,
                    isAsync: true);
                provenanceHandle = null;
                var bytesToInspect = checked((int)Math.Min(
                    length,
                    DownloadProvenanceService.MaximumZoneIdentifierBytes));
                var payload = new byte[bytesToInspect];
                await provenanceStream
                    .ReadExactlyAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
                ValidateFile(
                    provenanceStream.SafeFileHandle,
                    Snapshot.Identity
                        ?? throw new IOException(
                            "Windows did not return a stable source-file identity."));
                EnsureMatches(Snapshot);
                if (length > DownloadProvenanceService.MaximumZoneIdentifierBytes)
                {
                    return new DownloadProvenanceState(
                        DownloadProvenanceStateKind.Malformed,
                        StreamLength: length,
                        HashedBytes: payload.Length,
                        ContentSha256: Convert.ToHexString(SHA256.HashData(payload)));
                }

                return DownloadProvenanceService.InspectPayload(payload);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ArchiveProviderException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is Win32Exception
                    or UnauthorizedAccessException
                    or IOException
                    or NotSupportedException)
            {
                EnsureMatches(Snapshot);
                return DownloadProvenanceState.Inaccessible;
            }
            finally
            {
                provenanceHandle?.Dispose();
            }
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
                if (_stream is not null)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    _stream = null;
                }
            }
            finally
            {
                DisposeHandles(_absoluteDirectoryHandles);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _stream?.Dispose();
                _stream = null;
            }
            finally
            {
                DisposeHandles(_absoluteDirectoryHandles);
            }
        }

        private static bool IsUnsupportedSourceProvenanceError(int win32Code) =>
            win32Code is 1 or 50 or 87 or 123 or 282;

        private static ArchiveProviderException SourceProvenanceChanged(
            Exception? innerException = null) =>
            new(
                ArchiveErrorCategory.SourceChanged,
                "source.provenance_changed",
                "A source file's Windows download-security provenance changed during archive creation.",
                innerException);
    }
}
