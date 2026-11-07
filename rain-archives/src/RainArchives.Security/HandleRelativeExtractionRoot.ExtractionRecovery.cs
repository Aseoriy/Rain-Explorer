using System.Buffers.Binary;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

internal sealed partial class HandleRelativeExtractionRoot
{
    internal ExtractionTreeRecoveryEvidence CaptureExtractionRecoveryEvidence(
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        if (_activeOutputFiles != 0)
        {
            throw new InvalidOperationException(
                "Output files are still active during extraction recovery accounting.");
        }

        var staging = _stagingHandle
            ?? throw new InvalidOperationException("The staging root is no longer available.");
        var expectedDirectories = _directoryIdentities
            .Where(pair => pair.Key.Length != 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var expectedFiles = _fileIdentities
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var expectedProvenance = _fileProvenanceStreams
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        try
        {
            return InspectExtractionTree(
                staging,
                StagingIdentity,
                ProtocolConstants.MaximumExtractionRecoveryObjects,
                ProtocolConstants.MaximumExtractionRecoveryBytes,
                expectedDirectories,
                expectedFiles,
                expectedProvenance,
                cancellationToken,
                tolerateExtraLinks: true);
        }
        catch (ExtractionTreeObservationException exception)
        {
            throw DestinationChanged(
                "destination.staging_shape_changed",
                exception.Message);
        }
    }

    internal static ExtractionRecoveryDestinationInspection InspectExtractionRecoveryArtifacts(
        ExtractionRecoveryRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var parentPath = Path.GetDirectoryName(record.DestinationPath)
            ?? throw new InvalidDataException(
                "The extraction recovery destination has no parent directory.");
        var destinationName = Path.GetFileName(record.DestinationPath);
        ValidateSingleComponent(destinationName);
        ValidateSingleComponent(record.StagingDirectoryName);

        List<SafeFileHandle>? parentHandles = null;
        try
        {
            parentHandles = OpenAbsoluteDirectoryChainForRecoveryInspection(parentPath);
            var parent = parentHandles[^1];
            var parentIdentity = ValidateDirectory(parent, expectedIdentity: null);
            if (!string.Equals(
                    parentIdentity,
                    record.DestinationParentIdentity,
                    StringComparison.Ordinal))
            {
                return ExtractionRecoveryDestinationInspection.ParentOnly(
                    ExtractionRecoveryParentState.IdentityMismatch);
            }

            var staging = ObserveExtractionRecoveryDirectory(
                parent,
                record.StagingDirectoryName,
                record,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var final = ObserveExtractionRecoveryDirectory(
                parent,
                destinationName,
                record,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            List<SafeFileHandle>? verificationHandles = null;
            try
            {
                verificationHandles = OpenAbsoluteDirectoryChainForRecoveryInspection(parentPath);
                var verificationIdentity = ValidateDirectory(
                    verificationHandles[^1],
                    expectedIdentity: null);
                if (!string.Equals(
                        verificationIdentity,
                        record.DestinationParentIdentity,
                        StringComparison.Ordinal))
                {
                    return ExtractionRecoveryDestinationInspection.ParentOnly(
                        ExtractionRecoveryParentState.IdentityMismatch);
                }
            }
            catch (Exception exception) when (IsExpectedInspectionFailure(exception))
            {
                return ExtractionRecoveryDestinationInspection.ParentOnly(
                    ClassifyExtractionParentFailure(exception));
            }
            finally
            {
                if (verificationHandles is not null)
                {
                    DisposeHandles(verificationHandles);
                }
            }

            return new ExtractionRecoveryDestinationInspection(
                ExtractionRecoveryParentState.MatchesJournal,
                staging,
                final,
                record.HasIncompleteTail
                    ? ExtractionRecoveryAssessment.InspectionIncomplete
                    : AssessExtractionRecoveryArtifacts(record, staging.State, final.State));
        }
        catch (Exception exception) when (IsExpectedInspectionFailure(exception))
        {
            return ExtractionRecoveryDestinationInspection.ParentOnly(
                ClassifyExtractionParentFailure(exception));
        }
        finally
        {
            if (parentHandles is not null)
            {
                DisposeHandles(parentHandles);
            }
        }
    }

    private static ExtractionRecoveryArtifactInspection ObserveExtractionRecoveryDirectory(
        SafeFileHandle parent,
        string directoryName,
        ExtractionRecoveryRecord record,
        CancellationToken cancellationToken)
    {
        SafeFileHandle? directory = null;
        try
        {
            directory = CreateRelativeHandle(
                parent,
                directoryName,
                RecoveryInventoryDirectoryAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                DirectoryOptions,
                "an extraction recovery directory");
            var information = GetInformation(directory);
            var attributes = (FileAttributes)information.FileAttributes;
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.ReparsePoint);
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                return new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.WrongKind);
            }

            var observedIdentity = FormatIdentity(information);
            if (record.StagingDirectoryIdentity is null)
            {
                return new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.NotBoundByJournal);
            }

            if (!string.Equals(
                    observedIdentity,
                    record.StagingDirectoryIdentity,
                    StringComparison.Ordinal))
            {
                return new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.IdentityMismatch);
            }

            ExtractionTreeRecoveryEvidence evidence;
            try
            {
                evidence = InspectExtractionTree(
                    directory,
                    record.StagingDirectoryIdentity,
                    ProtocolConstants.MaximumExtractionRecoveryObjects,
                    ProtocolConstants.MaximumExtractionRecoveryBytes,
                    expectedDirectories: null,
                    expectedFiles: null,
                    expectedProvenance: null,
                    cancellationToken);
            }
            catch (ExtractionTreeObservationException exception)
            {
                return new ExtractionRecoveryArtifactInspection(exception.State);
            }

            var state = record.TreeRevision is null
                ? ExtractionRecoveryArtifactState.IdentityMatchRevisionUnbound
                : evidence.FileCount != record.FileCount
                    || evidence.DirectoryCount != record.DirectoryCount
                    || evidence.TotalBytes != record.TotalBytes
                    || evidence.NamedStreamCount != record.NamedStreamCount
                    || !string.Equals(
                        evidence.Revision,
                        record.TreeRevision,
                        StringComparison.Ordinal)
                        ? ExtractionRecoveryArtifactState.RevisionMismatch
                        : ExtractionRecoveryArtifactState.ExactMatch;
            return new ExtractionRecoveryArtifactInspection(
                state,
                evidence.FileCount,
                evidence.DirectoryCount,
                evidence.TotalBytes,
                evidence.NamedStreamCount,
                evidence.Revision);
        }
        catch (ArchiveProviderException exception)
            when (exception.Code == "destination.path_replaced")
        {
            return new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.Absent);
        }
        catch (ArchiveProviderException exception)
            when (exception.Code == "destination.reparse_point_encountered")
        {
            return new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.ReparsePoint);
        }
        catch (Win32Exception exception) when (IsAccessDenied(exception.NativeErrorCode))
        {
            return new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.AccessDenied);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or NotSupportedException
                or PlatformNotSupportedException)
        {
            return new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.Unavailable);
        }
        finally
        {
            directory?.Dispose();
        }
    }

    private static ExtractionTreeRecoveryEvidence InspectExtractionTree(
        SafeFileHandle root,
        string expectedRootIdentity,
        int maximumObjects,
        long maximumBytes,
        IReadOnlyDictionary<string, string>? expectedDirectories,
        IReadOnlyDictionary<string, string>? expectedFiles,
        IReadOnlyDictionary<string, bool>? expectedProvenance,
        CancellationToken cancellationToken,
        bool tolerateExtraLinks = false)
    {
        // tolerateExtraLinks is only set for the live pre-commit check of files this operation
        // just created and identity-validated. Sync clients (Google Drive, OneDrive) can add
        // temporary hard links to them; recovery inspection after a crash stays strict.
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRootIdentity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumObjects);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDirectory(root, expectedRootIdentity);
        RequireStableExtractionRecoveryIdentity(
            GetInformation(root),
            "The extraction recovery root has no stable filesystem identity.");
        EnsureAllowedExtractionDataStreams(
            root,
            requireDefaultStream: false,
            requireZoneIdentifier: false);

        using var revision = new ExtractionTreeRevisionBuilder(expectedRootIdentity);
        var fileCount = 0;
        var directoryCount = 0;
        var namedStreamCount = 0;
        long totalBytes = 0;
        var seenDirectories = expectedDirectories is null
            ? null
            : new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = expectedFiles is null
            ? null
            : new HashSet<string>(StringComparer.Ordinal);

        InspectDirectory(root, string.Empty, depth: 0);
        if (seenDirectories is not null
            && seenFiles is not null
            && (seenDirectories.Count != expectedDirectories!.Count
                || seenFiles.Count != expectedFiles!.Count))
        {
            throw new ExtractionTreeObservationException(
                ExtractionRecoveryArtifactState.RevisionMismatch,
                "An operation-owned extraction path disappeared before recovery accounting.");
        }

        return new ExtractionTreeRecoveryEvidence(
            fileCount,
            directoryCount,
            totalBytes,
            namedStreamCount,
            revision.Complete());

        void InspectDirectory(SafeFileHandle directory, string relativePath, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 256)
            {
                throw new ExtractionTreeObservationException(
                    ExtractionRecoveryArtifactState.Excessive,
                    "The extraction recovery tree exceeds the path-depth bound.");
            }

            var remaining = maximumObjects - fileCount - directoryCount;
            if (remaining <= 0)
            {
                var one = EnumerateRecoveryDirectory(directory, 1, cancellationToken);
                if (one.Children.Count != 0 || one.IsTruncated)
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.Excessive,
                        "The extraction recovery tree exceeds the object-count bound.");
                }

                return;
            }

            var contents = EnumerateRecoveryDirectory(
                directory,
                checked(remaining + 1),
                cancellationToken);
            if (contents.IsTruncated || contents.Children.Count > remaining)
            {
                throw new ExtractionTreeObservationException(
                    ExtractionRecoveryArtifactState.Excessive,
                    "The extraction recovery tree exceeds the object-count bound.");
            }

            var children = contents.Children
                .OrderBy(child => child.Name, StringComparer.Ordinal)
                .ToArray();
            var collisionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!collisionNames.Add(child.Name))
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.RevisionMismatch,
                        "The extraction recovery tree contains a Windows name collision.");
                }

                ValidateSingleComponent(child.Name);
                if (child.FileId is null or 0)
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.Unavailable,
                        "An extraction recovery object has no stable enumeration identity.");
                }

                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.ReparsePoint,
                        "The extraction recovery tree contains a reparse point.");
                }

                var childPath = relativePath.Length == 0
                    ? child.Name
                    : $"{relativePath}\\{child.Name}";
                var isDirectory = child.Attributes.HasFlag(FileAttributes.Directory);
                using var handle = CreateRelativeHandle(
                    directory,
                    child.Name,
                    isDirectory
                        ? RecoveryInventoryDirectoryAccess
                        : FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
                    isDirectory
                        ? FileShareRead | FileShareWrite | FileShareDelete
                        : FileShareRead,
                    FileOpen,
                    isDirectory
                        ? DirectoryOptions
                        : FileNonDirectoryFile
                            | FileSynchronousIoNonAlert
                            | FileOpenReparsePoint,
                    "an extraction recovery tree object",
                    caseInsensitive: false);
                var information = GetInformation(handle);
                EnsureRecoveryEnumerationIdentity(information, child.FileId);
                RequireStableExtractionRecoveryIdentity(
                    information,
                    "An extraction recovery object has no stable filesystem identity.");
                var observedAttributes = (FileAttributes)information.FileAttributes;
                if (observedAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.ReparsePoint,
                        "The extraction recovery tree contains a reparse point.");
                }

                if (observedAttributes.HasFlag(FileAttributes.Directory) != isDirectory)
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.WrongKind,
                        "An extraction recovery object changed type during inspection.");
                }

                var identity = FormatIdentity(information);
                if (isDirectory)
                {
                    ValidateDirectory(handle, identity);
                    if (expectedDirectories is not null
                        && (!expectedDirectories.TryGetValue(childPath, out var expectedDirectoryIdentity)
                            || !string.Equals(
                                expectedDirectoryIdentity,
                                identity,
                                StringComparison.Ordinal)))
                    {
                        throw new ExtractionTreeObservationException(
                            ExtractionRecoveryArtifactState.RevisionMismatch,
                            "An extraction recovery directory does not match the owned manifest.");
                    }

                    EnsureAllowedExtractionDataStreams(
                        handle,
                        requireDefaultStream: false,
                        requireZoneIdentifier: false);
                    seenDirectories?.Add(childPath);
                    directoryCount = checked(directoryCount + 1);
                    revision.AppendDirectory(childPath, identity);
                    InspectDirectory(handle, childPath, checked(depth + 1));
                    continue;
                }

                ValidateFile(handle, identity);
                if (information.NumberOfLinks != 1 && !tolerateExtraLinks)
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.UnexpectedHardLinks,
                        "An extraction recovery file has unexpected hard links.");
                }

                if (expectedFiles is not null
                    && (!expectedFiles.TryGetValue(childPath, out var expectedFileIdentity)
                        || !string.Equals(
                            expectedFileIdentity,
                            identity,
                            StringComparison.Ordinal)))
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.RevisionMismatch,
                        "An extraction recovery file does not match the owned manifest.");
                }

                var length = GetBoundedRecoveryLength(information);
                totalBytes = AddBoundedExtractionRecoveryBytes(
                    totalBytes,
                    length,
                    maximumBytes);

                var requiresZone = expectedProvenance is not null
                    && expectedProvenance.TryGetValue(childPath, out var expectedZone)
                    && expectedZone;
                var streamShape = ReadAllowedExtractionDataStreams(
                    handle,
                    requireDefaultStream: true,
                    requireZoneIdentifier: requiresZone,
                    allowOptionalZoneIdentifier: expectedProvenance is null);
                namedStreamCount = checked(namedStreamCount + streamShape.NamedStreamCount);
                string? zoneIdentifierHash = null;
                if (streamShape.ZoneIdentifierLength is { } zoneIdentifierLength)
                {
                    totalBytes = AddBoundedExtractionRecoveryBytes(
                        totalBytes,
                        zoneIdentifierLength,
                        maximumBytes);
                    zoneIdentifierHash = ComputeExtractionNamedStreamHash(
                        directory,
                        child.Name,
                        identity,
                        zoneIdentifierLength,
                        cancellationToken,
                        tolerateExtraLinks);
                }

                var contentHash = ComputeExtractionContentHash(
                    handle,
                    length,
                    cancellationToken);
                var revalidated = GetInformation(handle);
                ValidateFile(handle, identity);
                if ((revalidated.NumberOfLinks != 1 && !tolerateExtraLinks)
                    || GetBoundedRecoveryLength(revalidated) != length)
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.RevisionMismatch,
                        "An extraction recovery file changed while its content revision was computed.");
                }

                seenFiles?.Add(childPath);
                fileCount = checked(fileCount + 1);
                revision.AppendFile(
                    childPath,
                    identity,
                    length,
                    streamShape.ZoneIdentifierLength,
                    zoneIdentifierHash,
                    contentHash);
            }
        }
    }

    private static long AddBoundedExtractionRecoveryBytes(
        long current,
        long additional,
        long maximum)
    {
        if (additional < 0 || current > maximum - additional)
        {
            throw new ExtractionTreeObservationException(
                ExtractionRecoveryArtifactState.Excessive,
                "The extraction recovery tree exceeds the byte bound.");
        }

        return current + additional;
    }

    private static void RequireStableExtractionRecoveryIdentity(
        NativeFileInformation information,
        string message)
    {
        var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        if (fileId == 0)
        {
            throw new ExtractionTreeObservationException(
                ExtractionRecoveryArtifactState.Unavailable,
                message);
        }
    }

    private static string ComputeExtractionNamedStreamHash(
        SafeFileHandle parent,
        string fileName,
        string expectedIdentity,
        long expectedLength,
        CancellationToken cancellationToken,
        bool tolerateExtraLinks = false)
    {
        SafeFileHandle? stream = null;
        try
        {
            stream = CreateRelativeHandle(
                parent,
                $"{fileName}:Zone.Identifier",
                FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
                FileShareRead,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                "an extraction recovery provenance stream",
                caseInsensitive: false);
            var information = GetInformation(stream);
            ValidateFile(stream, expectedIdentity);
            RequireStableExtractionRecoveryIdentity(
                information,
                "An extraction recovery provenance stream has no stable filesystem identity.");
            if ((information.NumberOfLinks != 1 && !tolerateExtraLinks)
                || GetBoundedRecoveryLength(information) != expectedLength)
            {
                throw UnexpectedStreams(
                    "The extraction provenance stream changed during inspection.");
            }

            return ComputeExtractionContentHash(stream, expectedLength, cancellationToken);
        }
        catch (ExtractionTreeObservationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or NotSupportedException
                or ArchiveProviderException)
        {
            throw new ExtractionTreeObservationException(
                ExtractionRecoveryArtifactState.UnexpectedDataStreams,
                "The extraction provenance stream could not be stably inspected.");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static string ComputeExtractionContentHash(
        SafeFileHandle handle,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < expectedLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, expectedLength - offset);
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
                if (read <= 0)
                {
                    throw new ExtractionTreeObservationException(
                        ExtractionRecoveryArtifactState.RevisionMismatch,
                        "An extraction recovery file ended before its recorded length.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                offset = checked(offset + read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (RandomAccess.Read(handle, buffer.AsSpan(0, 1), expectedLength) != 0)
            {
                throw new ExtractionTreeObservationException(
                    ExtractionRecoveryArtifactState.RevisionMismatch,
                    "An extraction recovery file exceeded its recorded length.");
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureAllowedExtractionDataStreams(
        SafeFileHandle handle,
        bool requireDefaultStream,
        bool requireZoneIdentifier) =>
        _ = ReadAllowedExtractionDataStreams(
            handle,
            requireDefaultStream,
            requireZoneIdentifier,
            allowOptionalZoneIdentifier: false);

    private static unsafe ExtractionDataStreamShape ReadAllowedExtractionDataStreams(
        SafeFileHandle handle,
        bool requireDefaultStream,
        bool requireZoneIdentifier,
        bool allowOptionalZoneIdentifier)
    {
        var buffer = (byte*)NativeMemory.AllocZeroed(FileStreamInfoBufferBytes);
        if (buffer is null)
        {
            throw new OutOfMemoryException(
                "Could not allocate the bounded extraction stream-inspection buffer.");
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
                    if (requireDefaultStream || requireZoneIdentifier)
                    {
                        throw UnexpectedStreams(
                            "An expected extraction data stream is absent.");
                    }

                    return new ExtractionDataStreamShape(0, null);
                }

                if (error == ErrorMoreData)
                {
                    throw UnexpectedStreams(
                        "An extraction recovery object has excessive stream metadata.");
                }

                throw new Win32Exception(
                    error,
                    "Could not inspect extraction recovery data streams by handle.");
            }

            var offset = 0;
            var observedDefault = false;
            long? zoneLength = null;
            while (true)
            {
                if (offset < 0 || offset > FileStreamInfoBufferBytes - FileStreamInfoNameOffset)
                {
                    throw UnexpectedStreams(
                        "Windows returned malformed extraction stream metadata.");
                }

                var entry = buffer + offset;
                var nextOffset = *(uint*)entry;
                var nameLength = *(uint*)(entry + sizeof(uint));
                var streamLength = *(long*)(entry + 8);
                if ((nameLength & 1) != 0
                    || nameLength > FileStreamInfoBufferBytes - offset - FileStreamInfoNameOffset
                    || streamLength < 0)
                {
                    throw UnexpectedStreams(
                        "Windows returned malformed extraction stream metadata.");
                }

                var streamName = new string(
                    (char*)(entry + FileStreamInfoNameOffset),
                    0,
                    checked((int)(nameLength / sizeof(char))));
                if (string.Equals(streamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
                {
                    if (observedDefault)
                    {
                        throw UnexpectedStreams(
                            "An extraction recovery object has duplicate unnamed streams.");
                    }

                    observedDefault = true;
                }
                else if (string.Equals(
                             streamName,
                             ":Zone.Identifier:$DATA",
                             StringComparison.OrdinalIgnoreCase))
                {
                    if (zoneLength is not null)
                    {
                        throw UnexpectedStreams(
                            "An extraction recovery object has duplicate provenance streams.");
                    }

                    zoneLength = streamLength;
                }
                else
                {
                    throw UnexpectedStreams(
                        "An extraction recovery object has an unexpected named data stream.");
                }

                if (nextOffset == 0)
                {
                    break;
                }

                if ((nextOffset & 7) != 0
                    || nextOffset < FileStreamInfoNameOffset + nameLength
                    || nextOffset > FileStreamInfoBufferBytes - offset)
                {
                    throw UnexpectedStreams(
                        "Windows returned malformed extraction stream metadata.");
                }

                offset = checked(offset + (int)nextOffset);
            }

            if (requireDefaultStream && !observedDefault)
            {
                throw UnexpectedStreams(
                    "An extraction recovery file has no unnamed data stream.");
            }

            if (requireZoneIdentifier != (zoneLength is not null)
                && !(allowOptionalZoneIdentifier && zoneLength is not null))
            {
                throw UnexpectedStreams(
                    "The extraction provenance-stream shape does not match its owner record.");
            }

            return new ExtractionDataStreamShape(zoneLength is null ? 0 : 1, zoneLength);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static ExtractionTreeObservationException UnexpectedStreams(string message) =>
        new(ExtractionRecoveryArtifactState.UnexpectedDataStreams, message);

    private static ExtractionRecoveryAssessment AssessExtractionRecoveryArtifacts(
        ExtractionRecoveryRecord record,
        ExtractionRecoveryArtifactState staging,
        ExtractionRecoveryArtifactState final)
    {
        var stagingOwned = IsOwnedExtractionArtifact(staging);
        var finalOwned = IsOwnedExtractionArtifact(final);
        if (stagingOwned && finalOwned)
        {
            return ExtractionRecoveryAssessment.MultipleOrConflictingArtifacts;
        }

        if (IsExcessiveExtractionArtifact(staging) || IsExcessiveExtractionArtifact(final))
        {
            return ExtractionRecoveryAssessment.Excessive;
        }

        if (IsIncompleteExtractionArtifact(staging) || IsIncompleteExtractionArtifact(final))
        {
            return ExtractionRecoveryAssessment.InspectionIncomplete;
        }

        if (IsConflictingExtractionArtifact(staging) || IsConflictingExtractionArtifact(final))
        {
            return ExtractionRecoveryAssessment.ChangedOrSuspicious;
        }

        if (stagingOwned)
        {
            return record.TreeRevision is null
                ? ExtractionRecoveryAssessment.OwnedIncompleteStagingObserved
                : ExtractionRecoveryAssessment.OwnedFinalizedStagingObserved;
        }

        if (finalOwned)
        {
            return ExtractionRecoveryAssessment.OwnedFinalObserved;
        }

        return ExtractionRecoveryAssessment.NoOwnedArtifactObserved;
    }

    private static bool IsOwnedExtractionArtifact(ExtractionRecoveryArtifactState state) =>
        state is ExtractionRecoveryArtifactState.IdentityMatchRevisionUnbound
            or ExtractionRecoveryArtifactState.ExactMatch;

    private static bool IsConflictingExtractionArtifact(ExtractionRecoveryArtifactState state) =>
        state is ExtractionRecoveryArtifactState.NotBoundByJournal
            or ExtractionRecoveryArtifactState.IdentityMismatch
            or ExtractionRecoveryArtifactState.RevisionMismatch
            or ExtractionRecoveryArtifactState.WrongKind
            or ExtractionRecoveryArtifactState.ReparsePoint
            or ExtractionRecoveryArtifactState.UnexpectedHardLinks
            or ExtractionRecoveryArtifactState.UnexpectedDataStreams;

    private static bool IsIncompleteExtractionArtifact(ExtractionRecoveryArtifactState state) =>
        state is ExtractionRecoveryArtifactState.NotInspected
            or ExtractionRecoveryArtifactState.AccessDenied
            or ExtractionRecoveryArtifactState.Unavailable;

    private static bool IsExcessiveExtractionArtifact(ExtractionRecoveryArtifactState state) =>
        state == ExtractionRecoveryArtifactState.Excessive;

    private static ExtractionRecoveryParentState ClassifyExtractionParentFailure(
        Exception exception) => exception switch
        {
            ArchiveProviderException { Code: "destination.reparse_point_encountered" } =>
                ExtractionRecoveryParentState.ReparsePointOrWrongKind,
            ArchiveProviderException { Code: "destination.path_replaced" } =>
                ExtractionRecoveryParentState.Missing,
            Win32Exception native when IsAccessDenied(native.NativeErrorCode) =>
                ExtractionRecoveryParentState.AccessDenied,
            Win32Exception native when IsNotFound(native.NativeErrorCode) =>
                ExtractionRecoveryParentState.Missing,
            UnauthorizedAccessException => ExtractionRecoveryParentState.AccessDenied,
            DirectoryNotFoundException or FileNotFoundException =>
                ExtractionRecoveryParentState.Missing,
            _ => ExtractionRecoveryParentState.Unavailable,
        };

    private sealed class ExtractionTreeRevisionBuilder : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _completed;

        internal ExtractionTreeRevisionBuilder(string rootIdentity)
        {
            AppendString("RainArchives extraction staging revision v2");
            AppendString(rootIdentity);
        }

        internal void AppendDirectory(string path, string identity)
        {
            AppendByte((byte)'D');
            AppendString(path);
            AppendString(identity);
        }

        internal void AppendFile(
            string path,
            string identity,
            long length,
            long? zoneIdentifierLength,
            string? zoneIdentifierHash,
            string contentHash)
        {
            AppendByte((byte)'F');
            AppendString(path);
            AppendString(identity);
            AppendInt64(length);
            AppendInt64(zoneIdentifierLength ?? -1);
            AppendString(zoneIdentifierHash ?? string.Empty);
            AppendString(contentHash);
        }

        internal string Complete()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The extraction revision is already complete.");
            }

            _completed = true;
            return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }

        public void Dispose() => _hash.Dispose();

        private void AppendString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                Span<byte> length = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                _hash.AppendData(length);
                _hash.AppendData(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private void AppendByte(byte value)
        {
            Span<byte> buffer = stackalloc byte[1];
            buffer[0] = value;
            _hash.AppendData(buffer);
        }

        private void AppendInt64(long value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            _hash.AppendData(buffer);
        }
    }

    private sealed class ExtractionTreeObservationException : IOException
    {
        internal ExtractionTreeObservationException(
            ExtractionRecoveryArtifactState state,
            string message)
            : base(message)
        {
            State = state;
        }

        internal ExtractionRecoveryArtifactState State { get; }
    }

    private sealed record ExtractionDataStreamShape(
        int NamedStreamCount,
        long? ZoneIdentifierLength);
}

internal sealed record ExtractionTreeRecoveryEvidence(
    int FileCount,
    int DirectoryCount,
    long TotalBytes,
    int NamedStreamCount,
    string Revision);

internal sealed record ExtractionRecoveryDestinationInspection(
    ExtractionRecoveryParentState DestinationParent,
    ExtractionRecoveryArtifactInspection StagingArtifact,
    ExtractionRecoveryArtifactInspection FinalArtifact,
    ExtractionRecoveryAssessment Assessment)
{
    internal static ExtractionRecoveryDestinationInspection ParentOnly(
        ExtractionRecoveryParentState state) =>
        new(
            state,
            new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.NotInspected),
            new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.NotInspected),
            state == ExtractionRecoveryParentState.IdentityMismatch
                ? ExtractionRecoveryAssessment.ParentChanged
                : ExtractionRecoveryAssessment.InspectionIncomplete);
}
