using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

internal sealed partial class HandleRelativeExtractionRoot
{
    private const uint RecoveryInspectionDirectoryAccess =
        FileTraverse | FileReadAttributes | Synchronize;
    private const uint RecoveryInventoryDirectoryAccess =
        RecoveryInspectionDirectoryAccess | FileReadDataOrListDirectory;
    private const uint RecoveryMutationDirectoryAccess =
        RecoveryInventoryDirectoryAccess | FileWriteDataOrAddFile;

    internal static ArchiveCreationRecoveryDestinationInspection InspectRecoveryArtifacts(
        ArchiveCreationRecoveryRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var parentPath = Path.GetDirectoryName(record.DestinationPath)
            ?? throw new InvalidDataException(
                "The archive-creation recovery destination has no parent directory.");
        var destinationName = Path.GetFileName(record.DestinationPath);
        ValidateSingleComponent(destinationName);
        ValidateSingleComponent(record.StagingFileName);

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
                return ParentOnly(ArchiveCreationRecoveryParentState.IdentityMismatch);
            }

            using var staging = ObserveRecoveryArtifact(
                parent,
                record.StagingFileName,
                record.StagingFileIdentity,
                record.StagingLength);
            cancellationToken.ThrowIfCancellationRequested();
            using var destination = ObserveRecoveryArtifact(
                parent,
                destinationName,
                record.StagingFileIdentity,
                record.StagingLength);
            cancellationToken.ThrowIfCancellationRequested();

            // Reopen the named parent while both child handles remain pinned. This
            // detects a path-to-parent replacement that happened after the first open.
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
                    return ParentOnly(ArchiveCreationRecoveryParentState.IdentityMismatch);
                }
            }
            catch (Exception exception) when (IsExpectedInspectionFailure(exception))
            {
                return ParentOnly(ClassifyParentFailure(exception));
            }
            finally
            {
                if (verificationHandles is not null)
                {
                    DisposeHandles(verificationHandles);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var assessment = AssessRecoveryArtifacts(
                record,
                staging.Inspection.State,
                destination.Inspection.State);
            return new ArchiveCreationRecoveryDestinationInspection(
                ArchiveCreationRecoveryParentState.MatchesJournal,
                staging.Inspection,
                destination.Inspection,
                record.HasIncompleteTail
                    ? ArchiveCreationRecoveryAssessment.InspectionIncomplete
                    : assessment);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedInspectionFailure(exception))
        {
            return ParentOnly(ClassifyParentFailure(exception));
        }
        finally
        {
            if (parentHandles is not null)
            {
                DisposeHandles(parentHandles);
            }
        }
    }

    internal static RecoveryDispositionLease PrepareRecoveryDisposition(
        ArchiveCreationRecoveryRecord record,
        ArchiveCreationRecoveryDispositionAction action,
        bool allowAlreadyApplied,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parentPath = Path.GetDirectoryName(record.DestinationPath)
            ?? throw new InvalidDataException(
                "The archive-creation recovery destination has no parent directory.");
        var destinationName = Path.GetFileName(record.DestinationPath);
        ValidateSingleComponent(destinationName);
        ValidateSingleComponent(record.StagingFileName);

        List<SafeFileHandle>? parentHandles = null;
        SafeFileHandle? stagingMutation = null;
        try
        {
            parentHandles = OpenAbsoluteDirectoryChainForRecoveryInspection(
                parentPath,
                shareFinalDelete: false,
                leafAccess: RecoveryMutationDirectoryAccess);
            var parent = parentHandles[^1];
            var parentIdentity = ValidateDirectory(parent, expectedIdentity: null);
            if (!string.Equals(
                    parentIdentity,
                    record.DestinationParentIdentity,
                    StringComparison.Ordinal))
            {
                throw RecoveryDispositionRefused(
                    "recovery.disposition_parent_changed",
                    "The recovery destination parent no longer matches the journal.");
            }

            var inspection = InspectPinnedRecoveryArtifacts(record, parent, cancellationToken);
            var alreadyApplied = false;
            switch (action)
            {
                case ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging:
                    if (record.Phase != ArchiveCreationRecoveryPhase.CommitIntent)
                    {
                        throw RecoveryDispositionRefused(
                            "recovery.disposition_phase_mismatch",
                            "The recovery journal phase does not permit committing its staging artifact.");
                    }

                    if (inspection.StagingArtifact.State
                            == ArchiveCreationRecoveryArtifactState.ExactMatch
                        && inspection.FinalArtifact.State
                            == ArchiveCreationRecoveryArtifactState.Absent
                        && inspection.Assessment
                            == ArchiveCreationRecoveryAssessment.OwnedStagingObserved)
                    {
                        stagingMutation = OpenOwnedRecoveryArtifactForMutation(
                            parent,
                            record.StagingFileName,
                            record.StagingFileIdentity,
                            record.StagingLength,
                            requireBoundLength: true);
                    }
                    else if (allowAlreadyApplied
                        && inspection.StagingArtifact.State
                            == ArchiveCreationRecoveryArtifactState.Absent
                        && inspection.FinalArtifact.State
                            == ArchiveCreationRecoveryArtifactState.ExactMatch
                        && inspection.Assessment
                            == ArchiveCreationRecoveryAssessment.OwnedFinalObserved)
                    {
                        alreadyApplied = true;
                    }
                    else
                    {
                        throw RecoveryDispositionRefused(
                            "recovery.disposition_not_applicable",
                            "The freshly inspected recovery artifacts do not permit this commit action.");
                    }

                    break;

                case ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging:
                    if (inspection.Assessment
                            == ArchiveCreationRecoveryAssessment.OwnedStagingObserved
                        && inspection.FinalArtifact.State
                            == ArchiveCreationRecoveryArtifactState.Absent
                        && inspection.StagingArtifact.State is
                            ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound
                            or ArchiveCreationRecoveryArtifactState.ExactMatch)
                    {
                        stagingMutation = OpenOwnedRecoveryArtifactForMutation(
                            parent,
                            record.StagingFileName,
                            record.StagingFileIdentity,
                            record.StagingLength,
                            requireBoundLength: false);
                    }
                    else if (allowAlreadyApplied
                        && inspection.StagingArtifact.State
                            == ArchiveCreationRecoveryArtifactState.Absent
                        && inspection.FinalArtifact.State
                            == ArchiveCreationRecoveryArtifactState.Absent
                        && inspection.Assessment
                            == ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved)
                    {
                        alreadyApplied = true;
                    }
                    else
                    {
                        throw RecoveryDispositionRefused(
                            "recovery.disposition_not_applicable",
                            "The freshly inspected recovery artifacts do not permit discarding a staging artifact.");
                    }

                    break;

                case ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal:
                    if (inspection.StagingArtifact.State
                            != ArchiveCreationRecoveryArtifactState.Absent
                        || inspection.FinalArtifact.State
                            != ArchiveCreationRecoveryArtifactState.ExactMatch
                        || inspection.Assessment
                            != ArchiveCreationRecoveryAssessment.OwnedFinalObserved)
                    {
                        throw RecoveryDispositionRefused(
                            "recovery.disposition_not_applicable",
                            "The freshly inspected recovery artifacts do not contain one exact published final archive.");
                    }

                    break;

                case ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact:
                    if (inspection.StagingArtifact.State
                            != ArchiveCreationRecoveryArtifactState.Absent
                        || inspection.FinalArtifact.State
                            != ArchiveCreationRecoveryArtifactState.Absent
                        || inspection.Assessment
                            != ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved)
                    {
                        throw RecoveryDispositionRefused(
                            "recovery.disposition_not_applicable",
                            "The freshly inspected recovery record still has an artifact or conflicting evidence.");
                    }

                    break;
            }

            VerifyRecoveryParentPath(parentPath, record.DestinationParentIdentity);
            var lease = new RecoveryDispositionLease(
                parentHandles,
                stagingMutation,
                record,
                action,
                destinationName,
                alreadyApplied);
            parentHandles = null;
            stagingMutation = null;
            return lease;
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedInspectionFailure(exception))
        {
            throw RecoveryDispositionRefused(
                "recovery.disposition_inspection_failed",
                "The recovery artifacts could not be revalidated for disposition.",
                exception);
        }
        finally
        {
            stagingMutation?.Dispose();
            if (parentHandles is not null)
            {
                DisposeHandles(parentHandles);
            }
        }
    }

    private static ArchiveCreationRecoveryDestinationInspection InspectPinnedRecoveryArtifacts(
        ArchiveCreationRecoveryRecord record,
        SafeFileHandle parent,
        CancellationToken cancellationToken)
    {
        using var staging = ObserveRecoveryArtifact(
            parent,
            record.StagingFileName,
            record.StagingFileIdentity,
            record.StagingLength);
        cancellationToken.ThrowIfCancellationRequested();
        using var destination = ObserveRecoveryArtifact(
            parent,
            Path.GetFileName(record.DestinationPath),
            record.StagingFileIdentity,
            record.StagingLength);
        cancellationToken.ThrowIfCancellationRequested();
        return new ArchiveCreationRecoveryDestinationInspection(
            ArchiveCreationRecoveryParentState.MatchesJournal,
            staging.Inspection,
            destination.Inspection,
            AssessRecoveryArtifacts(
                record,
                staging.Inspection.State,
                destination.Inspection.State));
    }

    private static SafeFileHandle OpenOwnedRecoveryArtifactForMutation(
        SafeFileHandle parent,
        string fileName,
        string? expectedIdentity,
        long? expectedLength,
        bool requireBoundLength)
    {
        if (expectedIdentity is null || requireBoundLength && expectedLength is null)
        {
            throw RecoveryDispositionRefused(
                "recovery.disposition_unbound_artifact",
                "The recovery journal does not bind the artifact strongly enough for this action.");
        }

        SafeFileHandle? handle = null;
        try
        {
            handle = CreateRelativeHandle(
                parent,
                fileName,
                Delete | FileReadAttributes | Synchronize,
                FileShareRead,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                "an archive-creation recovery artifact for disposition");
            RevalidateOwnedRecoveryArtifactForMutation(
                handle,
                expectedIdentity,
                expectedLength,
                requireBoundLength);

            var result = handle;
            handle = null;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static void RevalidateOwnedRecoveryArtifactForMutation(
        SafeFileHandle handle,
        string expectedIdentity,
        long? expectedLength,
        bool requireBoundLength)
    {
        if (requireBoundLength && expectedLength is null)
        {
            throw RecoveryDispositionRefused(
                "recovery.disposition_unbound_artifact",
                "The recovery journal does not bind the artifact strongly enough for this action.");
        }

        var information = GetInformation(handle);
        ValidateFile(handle, expectedIdentity);
        if (information.NumberOfLinks != 1)
        {
            throw RecoveryDispositionRefused(
                "recovery.disposition_unexpected_hard_links",
                "The recovery artifact has an unexpected hard-link count.");
        }

        var unsignedLength = ((ulong)information.FileSizeHigh << 32)
            | information.FileSizeLow;
        if (unsignedLength > long.MaxValue
            || expectedLength is { } requiredLength
                && (long)unsignedLength != requiredLength)
        {
            throw RecoveryDispositionRefused(
                "recovery.disposition_length_changed",
                "The recovery artifact length no longer matches the journal.");
        }
    }

    private static void VerifyRecoveryParentPath(
        string parentPath,
        string expectedIdentity)
    {
        List<SafeFileHandle>? verification = null;
        try
        {
            verification = OpenAbsoluteDirectoryChainForRecoveryInspection(parentPath);
            var observedIdentity = ValidateDirectory(verification[^1], expectedIdentity: null);
            if (!string.Equals(observedIdentity, expectedIdentity, StringComparison.Ordinal))
            {
                throw RecoveryDispositionRefused(
                    "recovery.disposition_parent_changed",
                    "The recovery destination parent changed during disposition preparation.");
            }
        }
        finally
        {
            if (verification is not null)
            {
                DisposeHandles(verification);
            }
        }
    }

    private static ArchiveProviderException RecoveryDispositionRefused(
        string code,
        string message,
        Exception? innerException = null) =>
        new(
            ArchiveErrorCategory.SourceChanged,
            code,
            message,
            innerException);

    internal static HandleRelativeRecoveryRoot OpenArchiveCreationRecoveryRoot(
        string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Archive-creation recovery inspection requires Windows.");
        }

        var handles = OpenAbsoluteDirectoryChainForRecoveryInspection(
            rootPath,
            shareFinalDelete: false,
            leafAccess: RecoveryInventoryDirectoryAccess);
        return new HandleRelativeRecoveryRoot(handles);
    }

    private static List<SafeFileHandle> OpenAbsoluteDirectoryChainForRecoveryInspection(
        string directoryPath,
        bool shareFinalDelete = true,
        uint leafAccess = RecoveryInspectionDirectoryAccess,
        bool exactCase = false)
    {
        var canonicalPath = Path.GetFullPath(directoryPath);
        var rootPath = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrEmpty(rootPath))
        {
            throw new DirectoryNotFoundException(
                "The archive-creation recovery destination parent has no filesystem root.");
        }

        var relativeParent = canonicalPath[rootPath.Length..]
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var components = relativeParent.Length == 0
            ? []
            : relativeParent.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        var rootShare = components.Length == 0 && !shareFinalDelete
            ? DirectoryShare
            : AncestorDirectoryShare;
        var rootAccess = components.Length == 0
            ? leafAccess
            : RecoveryInspectionDirectoryAccess;
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
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            root.Dispose();
            throw new Win32Exception(
                error,
                "Could not pin the archive-creation recovery destination volume root.");
        }

        var handles = new List<SafeFileHandle>(components.Length + 1) { root };
        try
        {
            ValidateDirectory(root, expectedIdentity: null);
            var current = root;
            for (var index = 0; index < components.Length; index++)
            {
                ValidateSingleComponent(components[index]);
                var share = index == components.Length - 1 && !shareFinalDelete
                    ? DirectoryShare
                    : AncestorDirectoryShare;
                var access = index == components.Length - 1
                    ? leafAccess
                    : RecoveryInspectionDirectoryAccess;
                current = CreateRelativeHandle(
                    current,
                    components[index],
                    access,
                    share,
                    FileOpen,
                    DirectoryOptions,
                    $"archive-creation recovery destination ancestor component {index + 1}",
                    caseInsensitive: !exactCase);
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

    private static unsafe RecoveryDirectoryEnumeration EnumerateRecoveryDirectory(
        SafeFileHandle directory,
        int maximumChildren,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumChildren);
        var children = new List<RecoveryDirectoryChild>(Math.Min(maximumChildren, 64));
        var buffer = (byte*)NativeMemory.Alloc(SourceDirectoryBufferBytes);
        if (buffer is null)
        {
            throw new OutOfMemoryException(
                "Could not allocate the bounded recovery-directory buffer.");
        }

        try
        {
            var informationClass = FileIdBothDirectoryRestartInfo;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeMemory.Clear(buffer, SourceDirectoryBufferBytes);
                if (!NativeMethods.GetFileInformationByHandleEx(
                        directory,
                        informationClass,
                        buffer,
                        SourceDirectoryBufferBytes))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreFiles)
                    {
                        return new RecoveryDirectoryEnumeration(children.AsReadOnly(), false);
                    }

                    throw new Win32Exception(
                        error,
                        "Could not enumerate an archive-creation recovery directory by handle.");
                }

                informationClass = FileIdBothDirectoryInfo;
                var offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (offset < 0
                        || offset > SourceDirectoryBufferBytes - FileIdBothDirectoryNameOffset)
                    {
                        throw InvalidRecoveryDirectoryBuffer();
                    }

                    var entry = buffer + offset;
                    var nextOffset = *(uint*)entry;
                    var fileNameLength = *(uint*)(entry + 60);
                    var fileId = *(ulong*)(entry + 96);
                    if ((fileNameLength & 1) != 0
                        || fileNameLength
                            > SourceDirectoryBufferBytes - offset - FileIdBothDirectoryNameOffset)
                    {
                        throw InvalidRecoveryDirectoryBuffer();
                    }

                    var fileName = new string(
                        (char*)(entry + FileIdBothDirectoryNameOffset),
                        0,
                        checked((int)(fileNameLength / sizeof(char))));
                    if (fileName is not "." and not "..")
                    {
                        if (children.Count == maximumChildren)
                        {
                            return new RecoveryDirectoryEnumeration(children.AsReadOnly(), true);
                        }

                        var attributes = (FileAttributes)(*(uint*)(entry + 56));
                        children.Add(new RecoveryDirectoryChild(
                            fileName,
                            fileId == 0 ? null : fileId,
                            attributes));
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }

                    if ((nextOffset & 7) != 0
                        || nextOffset < FileIdBothDirectoryNameOffset + fileNameLength
                        || nextOffset > SourceDirectoryBufferBytes - offset)
                    {
                        throw InvalidRecoveryDirectoryBuffer();
                    }

                    offset = checked(offset + (int)nextOffset);
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static InvalidDataException InvalidRecoveryDirectoryBuffer() =>
        new("Windows returned malformed recovery-directory enumeration data.");

    private static void EnsureRecoveryEnumerationIdentity(
        NativeFileInformation information,
        ulong? expectedFileId)
    {
        var observedFileId = ((ulong)information.FileIndexHigh << 32)
            | information.FileIndexLow;
        if (observedFileId == 0
            || expectedFileId is not null && observedFileId != expectedFileId.Value)
        {
            throw new InvalidDataException(
                "An archive-creation recovery object changed during inspection.");
        }
    }

    private static ObservedRecoveryArtifact ObserveRecoveryArtifact(
        SafeFileHandle parent,
        string fileName,
        string? expectedIdentity,
        long? expectedLength)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateRelativeHandle(
                parent,
                fileName,
                FileReadAttributes | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen,
                FileSynchronousIoNonAlert | FileOpenReparsePoint,
                "an archive-creation recovery artifact");
            var information = GetInformation(handle);
            var attributes = (FileAttributes)information.FileAttributes;
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new ObservedRecoveryArtifact(
                    handle,
                    new ArchiveCreationRecoveryArtifactInspection(
                        ArchiveCreationRecoveryArtifactState.ReparsePoint));
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                return new ObservedRecoveryArtifact(
                    handle,
                    new ArchiveCreationRecoveryArtifactInspection(
                        ArchiveCreationRecoveryArtifactState.WrongKind));
            }

            var unsignedLength = ((ulong)information.FileSizeHigh << 32)
                | information.FileSizeLow;
            if (unsignedLength > long.MaxValue)
            {
                return new ObservedRecoveryArtifact(
                    handle,
                    new ArchiveCreationRecoveryArtifactInspection(
                        ArchiveCreationRecoveryArtifactState.Unavailable));
            }

            var length = (long)unsignedLength;
            var linkCount = (long)information.NumberOfLinks;
            var observedIdentity = FormatIdentity(information);
            ArchiveCreationRecoveryArtifactState state;
            if (information.NumberOfLinks != 1)
            {
                state = ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks;
            }
            else if (expectedIdentity is null)
            {
                state = ArchiveCreationRecoveryArtifactState.NotBoundByJournal;
            }
            else if (!string.Equals(observedIdentity, expectedIdentity, StringComparison.Ordinal))
            {
                state = ArchiveCreationRecoveryArtifactState.IdentityMismatch;
            }
            else if (expectedLength is null)
            {
                state = ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound;
            }
            else if (length != expectedLength.Value)
            {
                state = ArchiveCreationRecoveryArtifactState.LengthMismatch;
            }
            else
            {
                state = ArchiveCreationRecoveryArtifactState.ExactMatch;
            }

            return new ObservedRecoveryArtifact(
                handle,
                new ArchiveCreationRecoveryArtifactInspection(state, length, linkCount));
        }
        catch (ArchiveProviderException exception)
            when (exception.Code == "destination.path_replaced")
        {
            handle?.Dispose();
            return ObservedRecoveryArtifact.WithoutHandle(
                ArchiveCreationRecoveryArtifactState.Absent);
        }
        catch (ArchiveProviderException exception)
            when (exception.Code == "destination.reparse_point_encountered")
        {
            handle?.Dispose();
            return ObservedRecoveryArtifact.WithoutHandle(
                ArchiveCreationRecoveryArtifactState.ReparsePoint);
        }
        catch (Win32Exception exception) when (IsAccessDenied(exception.NativeErrorCode))
        {
            handle?.Dispose();
            return ObservedRecoveryArtifact.WithoutHandle(
                ArchiveCreationRecoveryArtifactState.AccessDenied);
        }
        catch (Exception exception) when (IsExpectedInspectionFailure(exception))
        {
            handle?.Dispose();
            return ObservedRecoveryArtifact.WithoutHandle(
                ArchiveCreationRecoveryArtifactState.Unavailable);
        }
    }

    private static ArchiveCreationRecoveryAssessment AssessRecoveryArtifacts(
        ArchiveCreationRecoveryRecord record,
        ArchiveCreationRecoveryArtifactState staging,
        ArchiveCreationRecoveryArtifactState destination)
    {
        if (IsIncompleteArtifactState(staging) || IsIncompleteArtifactState(destination))
        {
            return ArchiveCreationRecoveryAssessment.InspectionIncomplete;
        }

        if (IsConflictingArtifactState(staging) || IsConflictingArtifactState(destination))
        {
            return ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts;
        }

        var stagingOwned = IsOwnedArtifactState(staging);
        var destinationOwned = IsOwnedArtifactState(destination);
        if (stagingOwned && destinationOwned)
        {
            return ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts;
        }

        if (stagingOwned)
        {
            return record.Phase == ArchiveCreationRecoveryPhase.Committed
                ? ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts
                : ArchiveCreationRecoveryAssessment.OwnedStagingObserved;
        }

        if (destinationOwned)
        {
            return record.Phase is ArchiveCreationRecoveryPhase.CommitIntent
                or ArchiveCreationRecoveryPhase.Committed
                ? ArchiveCreationRecoveryAssessment.OwnedFinalObserved
                : ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts;
        }

        return ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved;
    }

    private static bool IsOwnedArtifactState(ArchiveCreationRecoveryArtifactState state) =>
        state is ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound
            or ArchiveCreationRecoveryArtifactState.ExactMatch;

    private static bool IsConflictingArtifactState(ArchiveCreationRecoveryArtifactState state) =>
        state is ArchiveCreationRecoveryArtifactState.NotBoundByJournal
            or ArchiveCreationRecoveryArtifactState.IdentityMismatch
            or ArchiveCreationRecoveryArtifactState.LengthMismatch
            or ArchiveCreationRecoveryArtifactState.WrongKind
            or ArchiveCreationRecoveryArtifactState.ReparsePoint
            or ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks;

    private static bool IsIncompleteArtifactState(ArchiveCreationRecoveryArtifactState state) =>
        state is ArchiveCreationRecoveryArtifactState.NotInspected
            or ArchiveCreationRecoveryArtifactState.AccessDenied
            or ArchiveCreationRecoveryArtifactState.Unavailable;

    private static ArchiveCreationRecoveryDestinationInspection ParentOnly(
        ArchiveCreationRecoveryParentState parentState) =>
        new(
            parentState,
            new ArchiveCreationRecoveryArtifactInspection(
                ArchiveCreationRecoveryArtifactState.NotInspected),
            new ArchiveCreationRecoveryArtifactInspection(
                ArchiveCreationRecoveryArtifactState.NotInspected),
            parentState is ArchiveCreationRecoveryParentState.AccessDenied
                or ArchiveCreationRecoveryParentState.Unavailable
                ? ArchiveCreationRecoveryAssessment.InspectionIncomplete
                : ArchiveCreationRecoveryAssessment.ParentChanged);

    private static ArchiveCreationRecoveryParentState ClassifyParentFailure(Exception exception) =>
        exception switch
        {
            DirectoryNotFoundException or FileNotFoundException =>
                ArchiveCreationRecoveryParentState.Missing,
            ArchiveProviderException provider
                when provider.Code == "destination.reparse_point_encountered" =>
                ArchiveCreationRecoveryParentState.ReparsePointOrWrongKind,
            ArchiveProviderException provider
                when provider.Code == "destination.path_replaced" =>
                ArchiveCreationRecoveryParentState.Missing,
            UnauthorizedAccessException => ArchiveCreationRecoveryParentState.AccessDenied,
            Win32Exception native when IsAccessDenied(native.NativeErrorCode) =>
                ArchiveCreationRecoveryParentState.AccessDenied,
            Win32Exception native when native.NativeErrorCode is 2 or 3 or 21 or 267 =>
                ArchiveCreationRecoveryParentState.Missing,
            IOException io when IsNotFound(io.HResult & 0xFFFF) =>
                ArchiveCreationRecoveryParentState.Missing,
            IOException => ArchiveCreationRecoveryParentState.ReparsePointOrWrongKind,
            _ => ArchiveCreationRecoveryParentState.Unavailable,
        };

    private static bool IsExpectedInspectionFailure(Exception exception) =>
        exception is ArchiveProviderException
            or Win32Exception
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException;

    private static bool IsAccessDenied(int error) => error is 5 or 65 or 1314;

    private static bool IsNotFound(int error) => error is 2 or 3 or 21 or 267;

    private sealed class ObservedRecoveryArtifact : IDisposable
    {
        private readonly SafeFileHandle? _handle;

        internal ObservedRecoveryArtifact(
            SafeFileHandle? handle,
            ArchiveCreationRecoveryArtifactInspection inspection)
        {
            _handle = handle;
            Inspection = inspection;
        }

        internal ArchiveCreationRecoveryArtifactInspection Inspection { get; }

        internal static ObservedRecoveryArtifact WithoutHandle(
            ArchiveCreationRecoveryArtifactState state) =>
            new(null, new ArchiveCreationRecoveryArtifactInspection(state));

        public void Dispose() => _handle?.Dispose();
    }

    internal sealed partial class HandleRelativeRecoveryRoot : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;
        private bool _disposed;

        internal HandleRelativeRecoveryRoot(List<SafeFileHandle> handles)
        {
            _handles = handles;
        }

        internal RecoveryDirectoryEnumeration EnumerateOperations(
            int maximumOperations,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EnumerateRecoveryDirectory(
                _handles[^1],
                maximumOperations,
                cancellationToken);
        }

        internal RecoveryJournalLease OpenJournal(
            string operationName,
            ulong? expectedOperationFileId,
            string journalName,
            string dispositionName,
            bool exclusive,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateSingleComponent(operationName);
            ValidateSingleComponent(journalName);
            ValidateSingleComponent(dispositionName);
            cancellationToken.ThrowIfCancellationRequested();

            SafeFileHandle? operation = null;
            SafeFileHandle? journal = null;
            SafeFileHandle? disposition = null;
            FileStream? journalStream = null;
            FileStream? dispositionStream = null;
            try
            {
                operation = CreateRelativeHandle(
                    _handles[^1],
                    operationName,
                    exclusive
                        ? RecoveryMutationDirectoryAccess
                        : RecoveryInventoryDirectoryAccess,
                    DirectoryShare,
                    FileOpen,
                    DirectoryOptions,
                    "an archive-creation recovery operation");
                var operationInformation = GetInformation(operation);
                ValidateDirectory(operation, expectedIdentity: null);
                EnsureRecoveryEnumerationIdentity(
                    operationInformation,
                    expectedOperationFileId);

                var contents = EnumerateRecoveryDirectory(
                    operation,
                    maximumChildren: 3,
                    cancellationToken);
                var journalChildren = contents.Children
                    .Where(child => string.Equals(
                        child.Name,
                        journalName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var dispositionChildren = contents.Children
                    .Where(child => string.Equals(
                        child.Name,
                        dispositionName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (contents.IsTruncated
                    || journalChildren.Length != 1
                    || dispositionChildren.Length > 1
                    || contents.Children.Count
                        != journalChildren.Length + dispositionChildren.Length)
                {
                    throw new InvalidDataException(
                        "An archive-creation recovery operation has unexpected content.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                journal = CreateRelativeHandle(
                    operation,
                    journalName,
                    FileReadDataOrListDirectory | FileReadAttributes | Synchronize,
                    exclusive ? 0 : FileShareRead,
                    FileOpen,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                    "an archive-creation recovery journal");
                var journalInformation = GetInformation(journal);
                ValidateFile(journal, expectedIdentity: null);
                EnsureRecoveryEnumerationIdentity(
                    journalInformation,
                    journalChildren[0].FileId);
                if (journalInformation.NumberOfLinks != 1)
                {
                    throw new InvalidDataException(
                        "An archive-creation recovery journal has unexpected hard links.");
                }

                journalStream = new FileStream(
                    journal,
                    FileAccess.Read,
                    bufferSize: 4096,
                    isAsync: false);
                journal = null;
                if (dispositionChildren.Length == 1)
                {
                    disposition = CreateRelativeHandle(
                        operation,
                        dispositionName,
                        FileReadDataOrListDirectory
                            | (exclusive ? FileWriteDataOrAddFile : 0)
                            | FileReadAttributes
                            | Synchronize,
                        exclusive ? 0 : FileShareRead,
                        FileOpen,
                        FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                        "an archive-creation recovery disposition receipt");
                    var dispositionInformation = GetInformation(disposition);
                    ValidateFile(disposition, expectedIdentity: null);
                    EnsureRecoveryEnumerationIdentity(
                        dispositionInformation,
                        dispositionChildren[0].FileId);
                    if (dispositionInformation.NumberOfLinks != 1)
                    {
                        throw new InvalidDataException(
                            "An archive-creation recovery disposition receipt has unexpected hard links.");
                    }

                    dispositionStream = new FileStream(
                        disposition,
                        exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                        bufferSize: 4096,
                        isAsync: false);
                    disposition = null;
                }

                var lease = new RecoveryJournalLease(
                    operation,
                    journalStream,
                    dispositionStream,
                    dispositionName,
                    exclusive);
                operation = null;
                journalStream = null;
                dispositionStream = null;
                return lease;
            }
            finally
            {
                dispositionStream?.Dispose();
                journalStream?.Dispose();
                disposition?.Dispose();
                journal?.Dispose();
                operation?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeHandles(_handles);
        }
    }

    internal sealed class RecoveryJournalLease : IDisposable
    {
        private readonly SafeFileHandle _operation;
        private readonly string _dispositionName;
        private readonly bool _allowDispositionCreation;
        private bool _disposed;

        internal RecoveryJournalLease(
            SafeFileHandle operation,
            FileStream journal,
            FileStream? disposition,
            string dispositionName,
            bool allowDispositionCreation)
        {
            _operation = operation;
            _dispositionName = dispositionName;
            _allowDispositionCreation = allowDispositionCreation;
            Journal = journal;
            Disposition = disposition;
        }

        internal FileStream Journal { get; }
        internal FileStream? Disposition { get; private set; }

        internal FileStream CreateDisposition()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_allowDispositionCreation)
            {
                throw new InvalidOperationException(
                    "The recovery record was not opened for disposition.");
            }

            if (Disposition is not null)
            {
                return Disposition;
            }

            SafeFileHandle? disposition = null;
            try
            {
                disposition = CreateRelativeHandle(
                    _operation,
                    _dispositionName,
                    FileReadDataOrListDirectory
                        | FileWriteDataOrAddFile
                        | FileReadAttributes
                        | Synchronize,
                    0,
                    FileCreate,
                    FileNonDirectoryFile
                        | FileSynchronousIoNonAlert
                        | FileWriteThrough
                        | FileOpenReparsePoint,
                    "an archive-creation recovery disposition receipt");
                var information = GetInformation(disposition);
                ValidateFile(disposition, expectedIdentity: null);
                if (information.NumberOfLinks != 1)
                {
                    throw new InvalidDataException(
                        "An archive-creation recovery disposition receipt has unexpected hard links.");
                }

                Disposition = new FileStream(
                    disposition,
                    FileAccess.ReadWrite,
                    bufferSize: 4096,
                    isAsync: false);
                disposition = null;
                return Disposition;
            }
            finally
            {
                disposition?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Exception? failure = null;
            try
            {
                Disposition?.Dispose();
                Disposition = null;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                Journal.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException("Recovery-record cleanup failed.", failure, exception);
            }

            try
            {
                _operation.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException("Recovery-record cleanup failed.", failure, exception);
            }

            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    internal sealed class RecoveryDispositionLease : IDisposable
    {
        private List<SafeFileHandle>? _parentHandles;
        private SafeFileHandle? _stagingMutation;
        private readonly ArchiveCreationRecoveryRecord _record;
        private readonly ArchiveCreationRecoveryDispositionAction _action;
        private readonly string _destinationName;
        private bool _disposed;

        internal RecoveryDispositionLease(
            List<SafeFileHandle> parentHandles,
            SafeFileHandle? stagingMutation,
            ArchiveCreationRecoveryRecord record,
            ArchiveCreationRecoveryDispositionAction action,
            string destinationName,
            bool alreadyApplied)
        {
            _parentHandles = parentHandles;
            _stagingMutation = stagingMutation;
            _record = record;
            _action = action;
            _destinationName = destinationName;
            AlreadyApplied = alreadyApplied;
        }

        internal bool AlreadyApplied { get; }

        internal ArchiveCreationRecoveryAssessment Apply(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var parentHandles = _parentHandles
                ?? throw new ObjectDisposedException(nameof(RecoveryDispositionLease));
            var parent = parentHandles[^1];
            ValidateDirectory(parent, _record.DestinationParentIdentity);
            var parentPath = Path.GetDirectoryName(_record.DestinationPath)
                ?? throw new InvalidDataException(
                    "The archive-creation recovery destination has no parent directory.");
            VerifyRecoveryParentPath(parentPath, _record.DestinationParentIdentity);
            cancellationToken.ThrowIfCancellationRequested();

            if (!AlreadyApplied)
            {
                switch (_action)
                {
                    case ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging:
                        var commitSource = _stagingMutation
                            ?? throw new InvalidOperationException(
                                "The recovery commit source is unavailable.");
                        RevalidateOwnedRecoveryArtifactForMutation(
                            commitSource,
                            _record.StagingFileIdentity!,
                            _record.StagingLength,
                            requireBoundLength: true);
                        using (var destination = ObserveRecoveryArtifact(
                                   parent,
                                   _destinationName,
                                   _record.StagingFileIdentity,
                                   _record.StagingLength))
                        {
                            if (destination.Inspection.State
                                != ArchiveCreationRecoveryArtifactState.Absent)
                            {
                                throw RecoveryDispositionRefused(
                                    "recovery.disposition_destination_appeared",
                                    "A destination appeared before the recovery commit.");
                            }
                        }

                        try
                        {
                            RenameRelativeHandle(commitSource, parent, _destinationName);
                        }
                        catch (ArchiveProviderException exception) when (
                            exception.Code == "destination.appeared_before_commit")
                        {
                            throw RecoveryDispositionRefused(
                                "recovery.disposition_destination_appeared",
                                "A destination appeared before the recovery commit.",
                                exception);
                        }

                        break;

                    case ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging:
                        var discardSource = _stagingMutation
                            ?? throw new InvalidOperationException(
                                "The recovery discard source is unavailable.");
                        RevalidateOwnedRecoveryArtifactForMutation(
                            discardSource,
                            _record.StagingFileIdentity!,
                            _record.StagingLength,
                            requireBoundLength: false);
                        MarkForDeletion(discardSource, "the archive-creation recovery staging artifact");
                        discardSource.Dispose();
                        _stagingMutation = null;
                        break;

                    case ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal:
                    case ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact:
                        break;
                }
            }

            var finalInspection = InspectPinnedRecoveryArtifacts(
                _record,
                parent,
                CancellationToken.None);
            var expectedAssessment = _action is
                ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging
                or ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal
                    ? ArchiveCreationRecoveryAssessment.OwnedFinalObserved
                    : ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved;
            if (finalInspection.Assessment != expectedAssessment)
            {
                throw RecoveryDispositionRefused(
                    "recovery.disposition_postcondition_failed",
                    "The recovery action did not produce its required final artifact state.");
            }

            VerifyRecoveryParentPath(parentPath, _record.DestinationParentIdentity);

            return expectedAssessment;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stagingMutation?.Dispose();
            _stagingMutation = null;
            if (_parentHandles is not null)
            {
                DisposeHandles(_parentHandles);
                _parentHandles = null;
            }
        }
    }

    internal sealed record RecoveryDirectoryChild(
        string Name,
        ulong? FileId,
        FileAttributes Attributes);

    internal sealed record RecoveryDirectoryEnumeration(
        IReadOnlyList<RecoveryDirectoryChild> Children,
        bool IsTruncated);
}

internal sealed record ArchiveCreationRecoveryDestinationInspection(
    ArchiveCreationRecoveryParentState DestinationParent,
    ArchiveCreationRecoveryArtifactInspection StagingArtifact,
    ArchiveCreationRecoveryArtifactInspection FinalArtifact,
    ArchiveCreationRecoveryAssessment Assessment);
