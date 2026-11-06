using Microsoft.Win32.SafeHandles;

namespace RainArchives.Security;

internal sealed partial class HandleRelativeExtractionRoot
{
    internal sealed partial class HandleRelativeRecoveryRoot
    {
        internal RecoveryPruneLease OpenCompletedRecordForPruning(
            string operationName,
            ulong? expectedOperationFileId,
            string journalName,
            string dispositionName,
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
            try
            {
                operation = CreateRelativeHandle(
                    _handles[^1],
                    operationName,
                    RecoveryInventoryDirectoryAccess | Delete,
                    shareAccess: 0,
                    FileOpen,
                    DirectoryOptions,
                    "a completed archive-creation recovery operation");
                var operationInformation = GetInformation(operation);
                ValidateDirectory(operation, expectedIdentity: null);
                EnsureRecoveryEnumerationIdentity(
                    operationInformation,
                    expectedOperationFileId);

                var contents = EnumerateRecoveryDirectory(
                    operation,
                    maximumChildren: 3,
                    cancellationToken);
                var journalChild = RequireExactRecoveryChild(
                    contents,
                    journalName,
                    dispositionName);
                var dispositionChild = RequireExactRecoveryChild(
                    contents,
                    dispositionName,
                    journalName);

                cancellationToken.ThrowIfCancellationRequested();
                journal = OpenRecoveryPruneFile(
                    operation,
                    journalName,
                    journalChild.FileId,
                    "an archive-creation recovery journal retained for replay");
                disposition = OpenRecoveryPruneFile(
                    operation,
                    dispositionName,
                    dispositionChild.FileId,
                    "an archive-creation recovery disposition receipt retained for replay");

                var journalStream = new FileStream(
                    journal,
                    FileAccess.Read,
                    bufferSize: 4096,
                    isAsync: false);
                journal = null;
                FileStream? dispositionStream = null;
                try
                {
                    dispositionStream = new FileStream(
                        disposition,
                        FileAccess.Read,
                        bufferSize: 4096,
                        isAsync: false);
                    disposition = null;
                    var lease = new RecoveryPruneLease(
                        operation,
                        journalStream,
                        dispositionStream,
                        journalName,
                        dispositionName);
                    operation = null;
                    dispositionStream = null;
                    return lease;
                }
                catch
                {
                    dispositionStream?.Dispose();
                    journalStream.Dispose();
                    throw;
                }
            }
            finally
            {
                disposition?.Dispose();
                journal?.Dispose();
                operation?.Dispose();
            }
        }

        internal static RecoveryDirectoryChild RequireExactRecoveryChild(
            RecoveryDirectoryEnumeration contents,
            string requiredName,
            string otherRequiredName)
        {
            var required = contents.Children
                .Where(child => string.Equals(
                    child.Name,
                    requiredName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var otherCount = contents.Children.Count(child => string.Equals(
                child.Name,
                otherRequiredName,
                StringComparison.OrdinalIgnoreCase));
            if (contents.IsTruncated
                || contents.Children.Count != 2
                || required.Length != 1
                || otherCount != 1)
            {
                throw new InvalidDataException(
                    "A completed archive-creation recovery operation has unexpected content.");
            }

            return required[0];
        }

        private static SafeFileHandle OpenRecoveryPruneFile(
            SafeFileHandle operation,
            string fileName,
            ulong? expectedFileId,
            string description)
        {
            SafeFileHandle? file = null;
            try
            {
                file = CreateRelativeHandle(
                    operation,
                    fileName,
                    FileReadDataOrListDirectory | FileReadAttributes | Delete | Synchronize,
                    shareAccess: 0,
                    FileOpen,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                    description);
                var information = GetInformation(file);
                ValidateFile(file, expectedIdentity: null);
                EnsureRecoveryEnumerationIdentity(information, expectedFileId);
                if (information.NumberOfLinks != 1)
                {
                    throw new InvalidDataException(
                        "A completed archive-creation recovery file has unexpected hard links.");
                }

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

    internal sealed class RecoveryPruneLease : IDisposable
    {
        private readonly SafeFileHandle _operation;
        private readonly string _journalName;
        private readonly string _dispositionName;
        private FileStream? _journal;
        private FileStream? _disposition;
        private bool _disposed;

        internal RecoveryPruneLease(
            SafeFileHandle operation,
            FileStream journal,
            FileStream disposition,
            string journalName,
            string dispositionName)
        {
            _operation = operation;
            _journal = journal;
            _disposition = disposition;
            _journalName = journalName;
            _dispositionName = dispositionName;
        }

        internal FileStream Journal => _journal
            ?? throw new ObjectDisposedException(nameof(RecoveryPruneLease));

        internal FileStream Disposition => _disposition
            ?? throw new ObjectDisposedException(nameof(RecoveryPruneLease));

        internal void DeleteExactTree(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var journal = _journal
                ?? throw new ObjectDisposedException(nameof(RecoveryPruneLease));
            var disposition = _disposition
                ?? throw new ObjectDisposedException(nameof(RecoveryPruneLease));

            ValidateDirectory(_operation, expectedIdentity: null);
            var contents = EnumerateRecoveryDirectory(
                _operation,
                maximumChildren: 3,
                cancellationToken);
            var journalChild = HandleRelativeRecoveryRoot.RequireExactRecoveryChild(
                contents,
                _journalName,
                _dispositionName);
            var dispositionChild = HandleRelativeRecoveryRoot.RequireExactRecoveryChild(
                contents,
                _dispositionName,
                _journalName);
            RevalidatePruneFile(journal, journalChild.FileId);
            RevalidatePruneFile(disposition, dispositionChild.FileId);
            cancellationToken.ThrowIfCancellationRequested();

            // Cancellation is intentionally not observed after the first delete
            // disposition. This bounded sequence either removes the exact metadata
            // tree or leaves a smaller, visibly suspicious tree for later review.
            MarkForDeletion(
                disposition.SafeFileHandle,
                "the completed archive-creation recovery disposition receipt");
            disposition.Dispose();
            _disposition = null;

            MarkForDeletion(
                journal.SafeFileHandle,
                "the completed archive-creation recovery journal");
            journal.Dispose();
            _journal = null;

            var remaining = EnumerateRecoveryDirectory(
                _operation,
                maximumChildren: 1,
                CancellationToken.None);
            if (remaining.IsTruncated || remaining.Children.Count != 0)
            {
                throw new InvalidDataException(
                    "The completed archive-creation recovery operation changed during pruning.");
            }

            ValidateDirectory(_operation, expectedIdentity: null);
            MarkForDeletion(
                _operation,
                "the empty completed archive-creation recovery operation directory");
        }

        private static void RevalidatePruneFile(FileStream stream, ulong? expectedFileId)
        {
            var information = GetInformation(stream.SafeFileHandle);
            ValidateFile(stream.SafeFileHandle, expectedIdentity: null);
            EnsureRecoveryEnumerationIdentity(information, expectedFileId);
            if (information.NumberOfLinks != 1)
            {
                throw new InvalidDataException(
                    "A completed archive-creation recovery file acquired an unexpected hard link.");
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
            DisposeStream(ref _disposition, ref failure);
            DisposeStream(ref _journal, ref failure);
            try
            {
                _operation.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(
                        "Completed recovery-receipt cleanup failed.",
                        failure,
                        exception);
            }

            if (failure is not null)
            {
                throw failure;
            }
        }

        private static void DisposeStream(
            ref FileStream? stream,
            ref Exception? failure)
        {
            if (stream is null)
            {
                return;
            }

            try
            {
                stream.Dispose();
                stream = null;
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(
                        "Completed recovery-receipt cleanup failed.",
                        failure,
                        exception);
            }
        }
    }
}
