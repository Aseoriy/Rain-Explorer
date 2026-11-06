using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;

namespace RainArchives.Security;

internal sealed partial class HandleRelativeExtractionRoot
{
    private const int FileStreamInfoClass = 7;
    private const int FileStreamInfoBufferBytes = 64 * 1024;
    private const int FileStreamInfoNameOffset = 24;
    private const int ErrorHandleEof = 38;
    private const int ErrorMoreData = 234;

    internal static HandleRelativeRecoveryRoot OpenNestedWorkspaceRecoveryRoot(
        string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Nested-workspace recovery requires Windows.");
        }

        var handles = OpenAbsoluteDirectoryChainForRecoveryInspection(
            rootPath,
            shareFinalDelete: false,
            leafAccess: RecoveryInventoryDirectoryAccess,
            exactCase: true);
        return new HandleRelativeRecoveryRoot(handles);
    }

    internal sealed partial class HandleRelativeRecoveryRoot
    {
        internal NestedWorkspaceRecoveryLease OpenNestedWorkspaceRecovery(
            string workspaceName,
            bool forMutation,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OwnedNestedTemporaryWorkspace.TryGetToken(workspaceName, out _))
            {
                throw new ArgumentException(
                    "The nested-workspace recovery name is invalid.",
                    nameof(workspaceName));
            }

            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle? workspace = null;
            FileStream? owner = null;
            var sources = new List<NestedWorkspaceRecoverySourceLease>();
            try
            {
                workspace = CreateRelativeHandle(
                    _handles[^1],
                    workspaceName,
                    RecoveryInventoryDirectoryAccess | (forMutation ? Delete : 0),
                    shareAccess: 0,
                    FileOpen,
                    DirectoryOptions,
                    "a nested-workspace recovery directory",
                    caseInsensitive: false);
                var workspaceInformation = GetInformation(workspace);
                var workspaceIdentity = ValidateDirectory(workspace, expectedIdentity: null);
                _ = RequireNonzeroRecoveryFileId(
                    workspaceInformation,
                    "The nested-workspace directory has no stable filesystem identity.");
                EnsureNoNamedDataStreams(workspace, requireDefaultStream: false);

                var contents = EnumerateRecoveryDirectory(
                    workspace,
                    ProtocolConstants.MaximumOpenSessions + 2,
                    cancellationToken);
                if (contents.IsTruncated)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "The nested-workspace recovery contains too many direct children.");
                }

                if (contents.Children.Count == 0)
                {
                    var emptyLease = new NestedWorkspaceRecoveryLease(
                        workspaceName,
                        workspace,
                        workspaceIdentity,
                        owner: null,
                        ownerIdentity: null,
                        sources,
                        forMutation);
                    workspace = null;
                    return emptyLease;
                }

                var ownerChild = RequireOneExactChild(
                    contents,
                    OwnedNestedTemporaryWorkspace.OwnerFileName,
                    "The nested-workspace recovery has a missing or ambiguous owner claim.");
                var sourceChildren = contents.Children
                    .Where(child => !string.Equals(
                        child.Name,
                        OwnedNestedTemporaryWorkspace.OwnerFileName,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(child => child.Name, StringComparer.Ordinal)
                    .ToArray();
                if (sourceChildren.Length > ProtocolConstants.MaximumOpenSessions
                    || sourceChildren.Any(child =>
                        !OwnedTemporaryArchiveSource.IsValidSourceDirectoryName(child.Name)))
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "The nested-workspace recovery has unexpected source directories.");
                }

                var ownerHandle = OpenNestedRecoveryFile(
                    workspace,
                    OwnedNestedTemporaryWorkspace.OwnerFileName,
                    ownerChild.FileId,
                    includeDeleteAccess: forMutation,
                    "the nested-workspace owner claim");
                var ownerInformation = GetInformation(ownerHandle);
                var ownerIdentity = FormatIdentity(ownerInformation);
                try
                {
                    owner = new FileStream(
                        ownerHandle,
                        FileAccess.Read,
                        bufferSize: 256,
                        isAsync: false);
                    ownerHandle = null;
                    if (!OwnedNestedTemporaryWorkspace.OwnerRecordMatches(owner, workspaceName))
                    {
                        throw new NestedWorkspaceRecoveryUnsafeException(
                            "The nested-workspace owner claim does not match its recovery identifier.");
                    }

                    foreach (var child in sourceChildren)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sources.Add(OpenNestedRecoverySource(
                            workspace,
                            child,
                            forMutation,
                            cancellationToken));
                    }

                    var aggregateBytes = sources.Aggregate(
                        0L,
                        static (total, source) => checked(total + source.Length));
                    if (aggregateBytes > ProtocolConstants.MaximumActiveNestedArchiveBytes)
                    {
                        throw new NestedWorkspaceRecoveryUnsafeException(
                            "The nested-workspace recovery exceeds the aggregate byte limit.");
                    }

                    var lease = new NestedWorkspaceRecoveryLease(
                        workspaceName,
                        workspace,
                        workspaceIdentity,
                        owner,
                        ownerIdentity,
                        sources,
                        forMutation);
                    workspace = null;
                    owner = null;
                    sources = [];
                    lease.RevalidateExactTree(cancellationToken);
                    return lease;
                }
                finally
                {
                    ownerHandle?.Dispose();
                }
            }
            catch (OverflowException exception)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "The nested-workspace recovery byte total overflowed.",
                    exception);
            }
            finally
            {
                foreach (var source in sources)
                {
                    source.Dispose();
                }

                owner?.Dispose();
                workspace?.Dispose();
            }
        }

        private static NestedWorkspaceRecoverySourceLease OpenNestedRecoverySource(
            SafeFileHandle workspace,
            RecoveryDirectoryChild sourceChild,
            bool forMutation,
            CancellationToken cancellationToken)
        {
            if (sourceChild.FileId is null)
            {
                throw new NestedWorkspaceRecoveryIdentityUnavailableException(
                    "A nested source directory has no enumeration identity.");
            }

            SafeFileHandle? directory = null;
            SafeFileHandle? source = null;
            try
            {
                directory = CreateRelativeHandle(
                    workspace,
                    sourceChild.Name,
                    RecoveryInventoryDirectoryAccess | (forMutation ? Delete : 0),
                    shareAccess: 0,
                    FileOpen,
                    DirectoryOptions,
                    "a nested-workspace source directory",
                    caseInsensitive: false);
                var directoryInformation = GetInformation(directory);
                var directoryIdentity = ValidateDirectory(directory, expectedIdentity: null);
                EnsureRecoveryEnumerationIdentity(directoryInformation, sourceChild.FileId);
                _ = RequireNonzeroRecoveryFileId(
                    directoryInformation,
                    "A nested source directory has no stable filesystem identity.");
                EnsureNoNamedDataStreams(directory, requireDefaultStream: false);

                var contents = EnumerateRecoveryDirectory(
                    directory,
                    maximumChildren: 2,
                    cancellationToken);
                var sourceFileChild = RequireOneExactChild(
                    contents,
                    OwnedTemporaryArchiveSource.SourceFileName,
                    "A nested source directory is not an exact single-file tree.");
                if (contents.IsTruncated || contents.Children.Count != 1)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "A nested source directory contains unexpected content.");
                }

                source = OpenNestedRecoveryFile(
                    directory,
                    OwnedTemporaryArchiveSource.SourceFileName,
                    sourceFileChild.FileId,
                    includeDeleteAccess: forMutation,
                    "a nested-workspace archive source");
                var sourceInformation = GetInformation(source);
                var sourceIdentity = FormatIdentity(sourceInformation);
                var length = GetBoundedRecoveryLength(sourceInformation);
                if (length > ProtocolConstants.MaximumNestedArchiveBytes)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "A nested-workspace source exceeds the per-source byte limit.");
                }

                var result = new NestedWorkspaceRecoverySourceLease(
                    sourceChild.Name,
                    directory,
                    directoryIdentity,
                    sourceChild.FileId.Value,
                    source,
                    sourceIdentity,
                    sourceFileChild.FileId!.Value,
                    length);
                directory = null;
                source = null;
                return result;
            }
            finally
            {
                source?.Dispose();
                directory?.Dispose();
            }
        }

        private static SafeFileHandle OpenNestedRecoveryFile(
            SafeFileHandle parent,
            string fileName,
            ulong? expectedFileId,
            bool includeDeleteAccess,
            string description)
        {
            if (expectedFileId is null)
            {
                throw new NestedWorkspaceRecoveryIdentityUnavailableException(
                    $"{description} has no enumeration identity.");
            }

            SafeFileHandle? file = null;
            try
            {
                file = CreateRelativeHandle(
                    parent,
                    fileName,
                    FileReadDataOrListDirectory
                        | FileReadAttributes
                        | Synchronize
                        | (includeDeleteAccess ? Delete : 0),
                    shareAccess: 0,
                    FileOpen,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                    description,
                    caseInsensitive: false);
                var information = GetInformation(file);
                ValidateFile(file, expectedIdentity: null);
                EnsureRecoveryEnumerationIdentity(information, expectedFileId);
                _ = RequireNonzeroRecoveryFileId(
                    information,
                    $"{description} has no stable filesystem identity.");
                if (information.NumberOfLinks != 1)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        $"{description} has unexpected hard links.");
                }

                EnsureNoNamedDataStreams(file, requireDefaultStream: true);
                var result = file;
                file = null;
                return result;
            }
            finally
            {
                file?.Dispose();
            }
        }
    }

    internal sealed class NestedWorkspaceRecoveryLease : IDisposable
    {
        private readonly string _workspaceName;
        private readonly string _workspaceIdentity;
        private readonly bool _forMutation;
        private SafeFileHandle? _workspace;
        private FileStream? _owner;
        private readonly string? _ownerIdentity;
        private readonly List<NestedWorkspaceRecoverySourceLease> _sources;
        private bool _disposed;

        internal NestedWorkspaceRecoveryLease(
            string workspaceName,
            SafeFileHandle workspace,
            string workspaceIdentity,
            FileStream? owner,
            string? ownerIdentity,
            List<NestedWorkspaceRecoverySourceLease> sources,
            bool forMutation)
        {
            _workspaceName = workspaceName;
            _workspace = workspace;
            _workspaceIdentity = workspaceIdentity;
            _owner = owner;
            _ownerIdentity = ownerIdentity;
            _sources = sources;
            _forMutation = forMutation;
            SourceCount = sources.Count;
            TotalSourceBytes = sources.Aggregate(
                0L,
                static (total, source) => checked(total + source.Length));
            Revision = ComputeRevision();
        }

        internal int SourceCount { get; }

        internal long TotalSourceBytes { get; }

        internal string Revision { get; }

        internal async ValueTask<NestedWorkspaceRecoveryReopenEvidence> CreateReopenEvidenceAsync(
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RevalidateExactTree(cancellationToken);
            var sources = new List<NestedWorkspaceRecoverySourceEvidence>(_sources.Count);
            foreach (var source in _sources.OrderBy(
                         source => source.DirectoryName,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var contentSha256 = await source
                    .ComputeContentSha256Async(cancellationToken)
                    .ConfigureAwait(false);
                sources.Add(new NestedWorkspaceRecoverySourceEvidence(
                    source.SourceId,
                    source.Length,
                    contentSha256));
            }

            RevalidateExactTree(cancellationToken);
            var evidence = new StringBuilder(1024)
                .Append("RainArchives nested workspace independent reopen revision v1\n")
                .Append(Revision).Append('\n');
            foreach (var source in sources)
            {
                evidence
                    .Append(source.SourceId).Append('\n')
                    .Append(source.Length).Append('\n')
                    .Append(source.ContentSha256).Append('\n');
            }

            var bytes = Encoding.UTF8.GetBytes(evidence.ToString());
            try
            {
                return new NestedWorkspaceRecoveryReopenEvidence(
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    sources.AsReadOnly());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        internal async ValueTask CopySourceToAsync(
            string sourceId,
            Stream destination,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(destination);
            var source = _sources.SingleOrDefault(candidate => string.Equals(
                candidate.SourceId,
                sourceId,
                StringComparison.Ordinal));
            if (source is null)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "The selected nested-workspace source is no longer present.");
            }

            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            RevalidateExactTree(cancellationToken);
        }

        internal void RevalidateExactTree(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var workspace = _workspace
                ?? throw new ObjectDisposedException(nameof(NestedWorkspaceRecoveryLease));
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDirectory(workspace, _workspaceIdentity);
            EnsureNoNamedDataStreams(workspace, requireDefaultStream: false);

            var contents = EnumerateRecoveryDirectory(
                workspace,
                ProtocolConstants.MaximumOpenSessions + 2,
                cancellationToken);
            var expectedChildren = _sources.Count + (_owner is null ? 0 : 1);
            if (contents.IsTruncated || contents.Children.Count != expectedChildren)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "The nested-workspace recovery changed after inspection.");
            }

            if (_owner is null)
            {
                if (_sources.Count != 0)
                {
                    throw new InvalidOperationException(
                        "An ownerless nested-workspace lease cannot contain sources.");
                }
            }
            else
            {
                var ownerChild = RequireOneExactChild(
                    contents,
                    OwnedNestedTemporaryWorkspace.OwnerFileName,
                    "The nested-workspace owner claim changed after inspection.");
                RevalidateRecoveryFile(
                    _owner.SafeFileHandle,
                    ownerChild.FileId,
                    _ownerIdentity!,
                    requireDefaultStream: true,
                    "The nested-workspace owner claim changed after inspection.");
                _owner.Position = 0;
                if (!OwnedNestedTemporaryWorkspace.OwnerRecordMatches(_owner, _workspaceName))
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "The nested-workspace owner claim changed after inspection.");
                }
            }

            foreach (var source in _sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directoryChild = RequireOneExactChild(
                    contents,
                    source.DirectoryName,
                    "A nested source directory changed after inspection.");
                source.Revalidate(directoryChild.FileId, cancellationToken);
            }

            if (!string.Equals(Revision, ComputeRevision(), StringComparison.Ordinal))
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "The nested-workspace recovery revision changed after inspection.");
            }
        }

        internal void DeleteExactTree(
            string expectedRevision,
            int expectedSourceCount,
            long expectedTotalSourceBytes,
            Action<NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint>?
                checkpoint,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_forMutation)
            {
                throw new InvalidOperationException(
                    "The nested-workspace recovery was not opened for mutation.");
            }

            if (!string.Equals(Revision, expectedRevision, StringComparison.Ordinal)
                || SourceCount != expectedSourceCount
                || TotalSourceBytes != expectedTotalSourceBytes)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "The nested-workspace recovery no longer matches the inspected revision.");
            }

            RevalidateExactTree(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Cancellation is intentionally ignored after the first exact-handle
            // deletion. A crash or I/O failure may leave a smaller suspicious tree,
            // which later inventory preserves for another explicit review.
            var checkpointSignaled = false;
            foreach (var source in _sources)
            {
                source.DeleteSourceFile();
                SignalFirstMutationCheckpoint(checkpoint, ref checkpointSignaled);
            }

            foreach (var source in _sources)
            {
                source.DeleteEmptyDirectory();
            }

            if (_owner is not null)
            {
                MarkForDeletion(
                    _owner.SafeFileHandle,
                    "the nested-workspace recovery owner claim");
                _owner.Dispose();
                _owner = null;
                SignalFirstMutationCheckpoint(checkpoint, ref checkpointSignaled);
            }

            var workspace = _workspace
                ?? throw new ObjectDisposedException(nameof(NestedWorkspaceRecoveryLease));
            var remaining = EnumerateRecoveryDirectory(
                workspace,
                maximumChildren: 1,
                CancellationToken.None);
            if (remaining.IsTruncated || remaining.Children.Count != 0)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "The nested-workspace recovery changed during deletion.");
            }

            ValidateDirectory(workspace, _workspaceIdentity);
            EnsureNoNamedDataStreams(workspace, requireDefaultStream: false);
            MarkForDeletion(workspace, "the empty nested-workspace recovery directory");
            SignalFirstMutationCheckpoint(checkpoint, ref checkpointSignaled);
            workspace.Dispose();
            _workspace = null;
        }

        private static void SignalFirstMutationCheckpoint(
            Action<NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint>?
                checkpoint,
            ref bool checkpointSignaled)
        {
            if (checkpoint is null || checkpointSignaled)
            {
                return;
            }

            checkpointSignaled = true;
            checkpoint(
                NestedTemporaryWorkspaceRecoveryStore
                    .NestedWorkspaceRecoveryDiscardCheckpoint
                    .FirstArtifactMutationApplied);
        }

        private string ComputeRevision()
        {
            var evidence = new StringBuilder(1024)
                .Append("RainArchives nested workspace recovery revision v1\n")
                .Append(_workspaceName).Append('\n')
                .Append(_workspaceIdentity).Append('\n')
                .Append(_owner is null ? "owner=absent\n" : "owner=present\n");
            if (_ownerIdentity is not null)
            {
                evidence.Append(_ownerIdentity).Append('\n');
            }

            foreach (var source in _sources.OrderBy(
                         source => source.DirectoryName,
                         StringComparer.Ordinal))
            {
                evidence
                    .Append(source.DirectoryName).Append('\n')
                    .Append(source.DirectoryIdentity).Append('\n')
                    .Append(source.SourceIdentity).Append('\n')
                    .Append(source.Length).Append('\n');
            }

            var bytes = Encoding.UTF8.GetBytes(evidence.ToString());
            try
            {
                return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var source in _sources)
            {
                source.Dispose();
            }

            _owner?.Dispose();
            _owner = null;
            _workspace?.Dispose();
            _workspace = null;
        }
    }

    internal sealed class NestedWorkspaceRecoverySourceLease : IDisposable
    {
        private SafeFileHandle? _directory;
        private SafeFileHandle? _source;
        private readonly ulong _directoryFileId;
        private readonly ulong _sourceFileId;
        private bool _disposed;

        internal NestedWorkspaceRecoverySourceLease(
            string directoryName,
            SafeFileHandle directory,
            string directoryIdentity,
            ulong directoryFileId,
            SafeFileHandle source,
            string sourceIdentity,
            ulong sourceFileId,
            long length)
        {
            DirectoryName = directoryName;
            _directory = directory;
            DirectoryIdentity = directoryIdentity;
            _directoryFileId = directoryFileId;
            _source = source;
            SourceIdentity = sourceIdentity;
            _sourceFileId = sourceFileId;
            Length = length;
        }

        internal string DirectoryName { get; }

        internal string SourceId => DirectoryName[OwnedTemporaryArchiveSource.SourceDirectoryPrefix.Length..];

        internal string DirectoryIdentity { get; }

        internal string SourceIdentity { get; }

        internal long Length { get; }

        internal async ValueTask<string> ComputeContentSha256Async(
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var source = _source
                ?? throw new ObjectDisposedException(nameof(NestedWorkspaceRecoverySourceLease));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long offset = 0;
            try
            {
                while (offset < Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(buffer.Length, Length - offset);
                    var read = await RandomAccess.ReadAsync(
                            source,
                            buffer.AsMemory(0, requested),
                            offset,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        throw new NestedWorkspaceRecoveryUnsafeException(
                            "A nested archive source ended before its inspected length.");
                    }

                    hash.AppendData(buffer, 0, read);
                    offset = checked(offset + read);
                }

                return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        internal async ValueTask CopyToAsync(
            Stream destination,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(destination);
            var source = _source
                ?? throw new ObjectDisposedException(nameof(NestedWorkspaceRecoverySourceLease));
            var buffer = new byte[64 * 1024];
            long offset = 0;
            try
            {
                while (offset < Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(buffer.Length, Length - offset);
                    var read = await RandomAccess.ReadAsync(
                            source,
                            buffer.AsMemory(0, requested),
                            offset,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        throw new NestedWorkspaceRecoveryUnsafeException(
                            "A nested archive source ended before its inspected length.");
                    }

                    await destination
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    offset = checked(offset + read);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        internal void Revalidate(
            ulong? expectedDirectoryFileId,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var directory = _directory
                ?? throw new ObjectDisposedException(nameof(NestedWorkspaceRecoverySourceLease));
            var source = _source
                ?? throw new ObjectDisposedException(nameof(NestedWorkspaceRecoverySourceLease));
            var directoryInformation = GetInformation(directory);
            ValidateDirectory(directory, DirectoryIdentity);
            EnsureRecoveryEnumerationIdentity(directoryInformation, expectedDirectoryFileId);
            if (RequireNonzeroRecoveryFileId(
                    directoryInformation,
                    "A nested source directory lost its stable identity.") != _directoryFileId)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "A nested source directory changed after inspection.");
            }

            EnsureNoNamedDataStreams(directory, requireDefaultStream: false);
            var contents = EnumerateRecoveryDirectory(
                directory,
                maximumChildren: 2,
                cancellationToken);
            var sourceChild = RequireOneExactChild(
                contents,
                OwnedTemporaryArchiveSource.SourceFileName,
                "A nested archive source changed after inspection.");
            if (contents.IsTruncated || contents.Children.Count != 1)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "A nested source directory changed after inspection.");
            }

            RevalidateRecoveryFile(
                source,
                sourceChild.FileId,
                SourceIdentity,
                requireDefaultStream: true,
                "A nested archive source changed after inspection.");
            var sourceInformation = GetInformation(source);
            if (RequireNonzeroRecoveryFileId(
                    sourceInformation,
                    "A nested archive source lost its stable identity.") != _sourceFileId
                || GetBoundedRecoveryLength(sourceInformation) != Length)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "A nested archive source changed after inspection.");
            }
        }

        internal void DeleteSourceFile()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var source = _source
                ?? throw new ObjectDisposedException(nameof(NestedWorkspaceRecoverySourceLease));
            MarkForDeletion(source, "a nested-workspace archive source");
            source.Dispose();
            _source = null;
        }

        internal void DeleteEmptyDirectory()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var directory = _directory
                ?? throw new ObjectDisposedException(nameof(NestedWorkspaceRecoverySourceLease));
            var remaining = EnumerateRecoveryDirectory(
                directory,
                maximumChildren: 1,
                CancellationToken.None);
            if (remaining.IsTruncated || remaining.Children.Count != 0)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "A nested source directory changed during deletion.");
            }

            ValidateDirectory(directory, DirectoryIdentity);
            EnsureNoNamedDataStreams(directory, requireDefaultStream: false);
            MarkForDeletion(directory, "an empty nested-workspace source directory");
            directory.Dispose();
            _directory = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source?.Dispose();
            _source = null;
            _directory?.Dispose();
            _directory = null;
        }
    }

    private static RecoveryDirectoryChild RequireOneExactChild(
        RecoveryDirectoryEnumeration contents,
        string requiredName,
        string message)
    {
        var caseInsensitiveMatches = contents.Children
            .Where(child => string.Equals(
                child.Name,
                requiredName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (caseInsensitiveMatches.Length != 1
            || !string.Equals(
                caseInsensitiveMatches[0].Name,
                requiredName,
                StringComparison.Ordinal))
        {
            throw new NestedWorkspaceRecoveryUnsafeException(message);
        }

        if (caseInsensitiveMatches[0].FileId is null)
        {
            throw new NestedWorkspaceRecoveryIdentityUnavailableException(
                $"{requiredName} has no enumeration identity.");
        }

        return caseInsensitiveMatches[0];
    }

    private static void RevalidateRecoveryFile(
        SafeFileHandle file,
        ulong? expectedEnumerationFileId,
        string expectedIdentity,
        bool requireDefaultStream,
        string changedMessage)
    {
        if (expectedEnumerationFileId is null)
        {
            throw new NestedWorkspaceRecoveryIdentityUnavailableException(
                "A nested-workspace recovery file lost its enumeration identity.");
        }

        var information = GetInformation(file);
        ValidateFile(file, expectedIdentity);
        EnsureRecoveryEnumerationIdentity(information, expectedEnumerationFileId);
        if (information.NumberOfLinks != 1)
        {
            throw new NestedWorkspaceRecoveryUnsafeException(changedMessage);
        }

        EnsureNoNamedDataStreams(file, requireDefaultStream);
    }

    private static ulong RequireNonzeroRecoveryFileId(
        NativeFileInformation information,
        string message)
    {
        var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        if (fileId == 0)
        {
            throw new NestedWorkspaceRecoveryIdentityUnavailableException(message);
        }

        return fileId;
    }

    private static long GetBoundedRecoveryLength(NativeFileInformation information)
    {
        var unsignedLength = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (unsignedLength > long.MaxValue)
        {
            throw new NestedWorkspaceRecoveryUnsafeException(
                "A nested-workspace source reported an unrepresentable length.");
        }

        return (long)unsignedLength;
    }

    private static unsafe void EnsureNoNamedDataStreams(
        SafeFileHandle handle,
        bool requireDefaultStream)
    {
        var buffer = (byte*)NativeMemory.AllocZeroed(FileStreamInfoBufferBytes);
        if (buffer is null)
        {
            throw new OutOfMemoryException(
                "Could not allocate the bounded stream-inspection buffer.");
        }

        try
        {
            if (!NativeMethods.GetFileInformationByHandleEx(
                    handle,
                    FileStreamInfoClass,
                    buffer,
                    FileStreamInfoBufferBytes))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is ErrorHandleEof or ErrorNoMoreFiles)
                {
                    if (requireDefaultStream)
                    {
                        throw new NestedWorkspaceRecoveryUnsafeException(
                            "A nested-workspace recovery file has no unnamed data stream.");
                    }

                    return;
                }

                if (error is 1 or 50 or 87)
                {
                    throw new NestedWorkspaceRecoveryIdentityUnavailableException(
                        "The filesystem cannot prove the absence of named data streams.");
                }

                if (error == ErrorMoreData)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "A nested-workspace recovery object has excessive stream metadata.");
                }

                throw new Win32Exception(
                    error,
                    "Could not inspect nested-workspace data streams by handle.");
            }

            var offset = 0;
            var observedDefaultStream = false;
            while (true)
            {
                if (offset < 0 || offset > FileStreamInfoBufferBytes - FileStreamInfoNameOffset)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "Windows returned malformed nested-workspace stream metadata.");
                }

                var entry = buffer + offset;
                var nextOffset = *(uint*)entry;
                var nameLength = *(uint*)(entry + sizeof(uint));
                if ((nameLength & 1) != 0
                    || nameLength > FileStreamInfoBufferBytes - offset - FileStreamInfoNameOffset)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "Windows returned malformed nested-workspace stream metadata.");
                }

                var streamName = new string(
                    (char*)(entry + FileStreamInfoNameOffset),
                    0,
                    checked((int)(nameLength / sizeof(char))));
                if (!string.Equals(streamName, "::$DATA", StringComparison.OrdinalIgnoreCase)
                    || observedDefaultStream)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "A nested-workspace recovery object has a named or duplicate data stream.");
                }

                observedDefaultStream = true;
                if (nextOffset == 0)
                {
                    break;
                }

                if ((nextOffset & 7) != 0
                    || nextOffset < FileStreamInfoNameOffset + nameLength
                    || nextOffset > FileStreamInfoBufferBytes - offset)
                {
                    throw new NestedWorkspaceRecoveryUnsafeException(
                        "Windows returned malformed nested-workspace stream metadata.");
                }

                offset = checked(offset + (int)nextOffset);
            }

            if (requireDefaultStream && !observedDefaultStream)
            {
                throw new NestedWorkspaceRecoveryUnsafeException(
                    "A nested-workspace recovery file has no unnamed data stream.");
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }
}

internal sealed record NestedWorkspaceRecoveryReopenEvidence(
    string Revision,
    IReadOnlyList<NestedWorkspaceRecoverySourceEvidence> Sources);

internal sealed class NestedWorkspaceRecoveryIdentityUnavailableException : IOException
{
    internal NestedWorkspaceRecoveryIdentityUnavailableException(string message)
        : base(message)
    {
    }
}

internal sealed class NestedWorkspaceRecoveryUnsafeException : IOException
{
    internal NestedWorkspaceRecoveryUnsafeException(string message)
        : base(message)
    {
    }

    internal NestedWorkspaceRecoveryUnsafeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
