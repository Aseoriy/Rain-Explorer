using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed record ArchiveCreationRecoveryRecord(
    string RecoveryId,
    OperationId OperationId,
    string DestinationPath,
    string DestinationParentIdentity,
    string StagingFileName,
    string? StagingFileIdentity,
    long? StagingLength,
    ArchiveCreationRecoveryPhase Phase,
    int Sequence,
    DateTimeOffset RecordedAtUtc,
    bool HasIncompleteTail);

public sealed record ArchiveCreationRecoveryInventory(
    int ActiveOperationCount,
    int PotentialRecoveryCount,
    int SuspiciousOperationCount,
    int IncompleteJournalCount,
    bool IsTruncated,
    bool InspectionIncomplete,
    IReadOnlyList<ArchiveCreationRecoveryRecord> RecoveryRecords)
{
    public bool RequiresAttention =>
        PotentialRecoveryCount > 0
        || SuspiciousOperationCount > 0
        || IncompleteJournalCount > 0
        || IsTruncated
        || InspectionIncomplete;
}

/// <summary>
/// Maintains a local, append-only account of ZIP publication phases. Startup
/// inventory reads only this local root; it never follows or probes a recorded
/// destination, which keeps an unavailable network destination off the startup path.
/// </summary>
public sealed partial class ArchiveCreationRecoveryStore
{
    internal const string OperationPrefix = "rainarchives-creation-";
    internal const string JournalFileName = "journal.bin";
    internal const string DispositionFileName = "disposition.bin";
    private const int TokenCharacters = 64;
    private const int MaximumCreateAttempts = 8;
    private const int JournalSchemaVersion = 1;
    private const int MaximumJournalFrames = 5;
    private const int MaximumFramePayloadBytes = 256 * 1024;
    private const int FrameHeaderBytes = 4 + sizeof(int) + SHA256.HashSizeInBytes;
    private const int MaximumJournalBytes =
        MaximumJournalFrames * (FrameHeaderBytes + MaximumFramePayloadBytes);
    private static readonly byte[] FrameMagic = "RACJ"u8.ToArray();
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
    };
    private static readonly ExtractionPathPolicy JournalPathPolicy = new();

    public ArchiveCreationRecoveryStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        IsLocalRoot = IsLocalFixedDrivePath(RootDirectory);
    }

    public string RootDirectory { get; }

    private bool IsLocalRoot { get; }

    public static string GetDefaultRootDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new DirectoryNotFoundException(
                "The current Windows profile does not expose a local application-data directory.");
        }

        return Path.Combine(
            localApplicationData,
            "SailHub",
            "Rain Archives",
            "Recovery",
            "ArchiveCreation");
    }

    public ArchiveCreationRecoveryInventory Inspect() =>
        Inspect(ProtocolConstants.MaximumArchiveCreationRecoveryRecords);

    public ValueTask<InspectArchiveCreationRecoveryResponse> InspectArtifactsAsync(
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetToken(OperationPrefix + recoveryId, out var token)
            || !string.Equals(token, recoveryId, StringComparison.Ordinal))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "recovery.invalid_id",
                "The archive-creation recovery identifier is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLocalRoot)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "recovery.inventory_unavailable",
                "The local archive-creation recovery inventory is unavailable.");
        }

        try
        {
            using var recoveryRoot = HandleRelativeExtractionRoot
                .OpenArchiveCreationRecoveryRoot(RootDirectory);
            using var journal = recoveryRoot.OpenJournal(
                OperationPrefix + token,
                expectedOperationFileId: null,
                JournalFileName,
                DispositionFileName,
                exclusive: false,
                cancellationToken);
            if (!TryReadJournal(journal.Journal, token, out var record) || record is null)
            {
                throw UnverifiedRecord();
            }

            if (journal.Disposition is not null
                && !TryReadDispositionReceipt(
                    journal.Disposition,
                    record,
                    out _,
                    out _,
                    out _))
            {
                throw UnverifiedRecord();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var destination = HandleRelativeExtractionRoot.InspectRecoveryArtifacts(
                record,
                cancellationToken);
            var summary = new ArchiveCreationRecoverySummary(
                record.RecoveryId,
                record.OperationId,
                record.Phase,
                record.Sequence,
                record.RecordedAtUtc,
                record.HasIncompleteTail);
            var root = Path.GetPathRoot(record.DestinationPath);
            var response = new InspectArchiveCreationRecoveryResponse(
                summary,
                record.DestinationPath,
                record.StagingFileName,
                record.StagingLength,
                root is not null && root.StartsWith(@"\\", StringComparison.Ordinal),
                destination.DestinationParent,
                destination.StagingArtifact,
                destination.FinalArtifact,
                destination.Assessment,
                DateTimeOffset.UtcNow);
            return ValueTask.FromResult(response);
        }
        catch (Exception exception) when (IsSharingViolation(exception))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.SourceChanged,
                "recovery.active",
                "The archive-creation recovery record is still owned by an active operation.",
                exception);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException
                || exception is ArchiveProviderException provider
                    && provider.Code == "destination.path_replaced")
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "recovery.not_found",
                "The archive-creation recovery record was not found.",
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or UnauthorizedAccessException
                or Win32Exception
                or NotSupportedException
                or ArchiveProviderException)
        {
            throw UnverifiedRecord(exception);
        }
    }

    internal ArchiveCreationRecoveryInventory Inspect(int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        if (maximumEntries > int.MaxValue - CompletedDispositionInspectionOverhead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                "The recovery inventory bound is too large.");
        }

        if (!IsLocalRoot)
        {
            return IncompleteInventory();
        }

        try
        {
            try
            {
                using var recoveryRoot = HandleRelativeExtractionRoot
                    .OpenArchiveCreationRecoveryRoot(RootDirectory);
                var enumeration = recoveryRoot.EnumerateOperations(
                    maximumEntries + CompletedDispositionInspectionOverhead,
                    CancellationToken.None);
                var active = 0;
                var potential = 0;
                var suspicious = 0;
                var incompleteJournals = 0;
                var attentionTruncated = false;
                var records = new List<ArchiveCreationRecoveryRecord>();
                foreach (var operation in enumeration.Children)
                {
                    var operationName = operation.Name;
                    if (!TryGetToken(operationName, out var token))
                    {
                        if (active + potential + suspicious < maximumEntries)
                        {
                            suspicious++;
                        }
                        else
                        {
                            attentionTruncated = true;
                        }

                        continue;
                    }

                    try
                    {
                        using var journal = recoveryRoot.OpenJournal(
                            operationName,
                            operation.FileId,
                            JournalFileName,
                            DispositionFileName,
                            exclusive: false,
                            CancellationToken.None);
                        if (!TryReadJournal(journal.Journal, token, out var record))
                        {
                            suspicious++;
                            continue;
                        }

                        ArchiveCreationRecoveryDispositionReceipt? receipt = null;
                        if (journal.Disposition is not null
                            && !TryReadDispositionReceipt(
                                journal.Disposition,
                                record!,
                                out receipt,
                                out _,
                                out _))
                        {
                            suspicious++;
                            continue;
                        }

                        if (receipt?.Phase
                            == ArchiveCreationRecoveryDispositionReceiptPhase.Completed)
                        {
                            continue;
                        }

                        if (active + potential + suspicious < maximumEntries)
                        {
                            potential++;
                            records.Add(record!);
                            if (record!.HasIncompleteTail)
                            {
                                incompleteJournals++;
                            }
                        }
                        else
                        {
                            attentionTruncated = true;
                        }
                    }
                    catch (Exception exception) when (IsSharingViolation(exception))
                    {
                        if (active + potential + suspicious < maximumEntries)
                        {
                            active++;
                        }
                        else
                        {
                            attentionTruncated = true;
                        }
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException
                            or IOException
                            or UnauthorizedAccessException
                            or Win32Exception
                            or JsonException
                            or NotSupportedException
                            or ArchiveProviderException)
                    {
                        if (active + potential + suspicious < maximumEntries)
                        {
                            suspicious++;
                        }
                        else
                        {
                            attentionTruncated = true;
                        }
                    }
                }

                records.Sort(static (left, right) =>
                    string.Compare(left.RecoveryId, right.RecoveryId, StringComparison.Ordinal));
                return new ArchiveCreationRecoveryInventory(
                    active,
                    potential,
                    suspicious,
                    incompleteJournals,
                    enumeration.IsTruncated || attentionTruncated,
                    InspectionIncomplete: false,
                    records.AsReadOnly());
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or DirectoryNotFoundException
                    || exception is ArchiveProviderException provider
                        && provider.Code == "destination.path_replaced")
            {
                return EmptyInventory();
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or Win32Exception
                or ArchiveProviderException
                or NotSupportedException)
        {
            return IncompleteInventory();
        }
    }

    internal async ValueTask<ArchiveCreationRecoveryTransaction> BeginAsync(
        OperationId operationId,
        string destinationPath,
        string destinationParentIdentity,
        CancellationToken cancellationToken)
    {
        if (operationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A recovery journal requires a non-empty operation ID.", nameof(operationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationParentIdentity);
        var canonicalDestination = Path.GetFullPath(destinationPath);
        EnsureRootAvailable();

        for (var attempt = 0; attempt < MaximumCreateAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = Convert
                .ToHexString(RandomNumberGenerator.GetBytes(TokenCharacters / 2))
                .ToLowerInvariant();
            var operationName = OperationPrefix + token;
            HandleRelativeExtractionRoot? root = null;
            HandleRelativeExtractionRoot.HandleRelativeOutputFile? journal = null;
            try
            {
                root = HandleRelativeExtractionRoot.Create(RootDirectory, operationName);
                journal = root.CreateFile(JournalFileName);
                var transaction = new ArchiveCreationRecoveryTransaction(
                    root,
                    journal,
                    Path.Combine(RootDirectory, operationName),
                    token,
                    operationId,
                    canonicalDestination,
                    destinationParentIdentity);
                root = null;
                journal = null;
                try
                {
                    await transaction.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    return transaction;
                }
                catch (Exception operationFailure)
                {
                    try
                    {
                        await transaction.DeleteAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException(
                            "Archive recovery-journal creation and cleanup both failed.",
                            operationFailure,
                            cleanupFailure);
                    }

                    ExceptionDispatchInfo.Capture(operationFailure).Throw();
                    throw;
                }
            }
            catch (ArchiveProviderException exception)
                when (exception.Code == "destination.path_appeared")
            {
                journal?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                root?.Dispose();
            }
            catch
            {
                journal?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                root?.Dispose();
                throw;
            }
        }

        throw new IOException("Could not allocate a unique archive-creation recovery journal.");
    }

    private void EnsureRootAvailable()
    {
        if (!IsLocalRoot)
        {
            throw new IOException(
                "Archive-creation recovery journals require a local fixed-drive path.");
        }

        ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(RootDirectory);
        Directory.CreateDirectory(RootDirectory);
        ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(RootDirectory);
        _ = FileSystemIdentityService.CaptureExistingDirectoryIdentity(RootDirectory);
    }

    private static bool TryReadJournal(
        FileStream journal,
        string expectedToken,
        out ArchiveCreationRecoveryRecord? record)
    {
        record = null;
        if (journal.Length <= 0 || journal.Length > MaximumJournalBytes)
        {
            return false;
        }

        ArchiveCreationJournalState? previous = null;
        var incompleteTail = false;
        var frameCount = 0;
        while (journal.Position < journal.Length)
        {
            if (frameCount == MaximumJournalFrames)
            {
                return false;
            }

            var remaining = journal.Length - journal.Position;
            if (remaining < FrameHeaderBytes)
            {
                if (previous is null)
                {
                    return false;
                }

                incompleteTail = true;
                break;
            }

            var header = new byte[FrameHeaderBytes];
            journal.ReadExactly(header);
            if (!header.AsSpan(0, FrameMagic.Length).SequenceEqual(FrameMagic))
            {
                return false;
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
                header.AsSpan(FrameMagic.Length, sizeof(int)));
            if (payloadLength <= 0 || payloadLength > MaximumFramePayloadBytes)
            {
                return false;
            }

            if (journal.Length - journal.Position < payloadLength)
            {
                if (previous is null)
                {
                    return false;
                }

                incompleteTail = true;
                break;
            }

            var payload = new byte[payloadLength];
            journal.ReadExactly(payload);
            var expectedHash = header.AsSpan(FrameMagic.Length + sizeof(int), SHA256.HashSizeInBytes);
            var actualHash = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                return false;
            }

            ArchiveCreationJournalState? current;
            try
            {
                current = JsonSerializer.Deserialize<ArchiveCreationJournalState>(
                    payload,
                    JournalJsonOptions);
            }
            catch (JsonException)
            {
                return false;
            }

            if (current is null || !IsValidState(current, expectedToken, previous))
            {
                return false;
            }

            previous = current;
            frameCount++;
        }

        if (previous is null)
        {
            return false;
        }

        record = new ArchiveCreationRecoveryRecord(
            previous.Token,
            new OperationId(previous.OperationId),
            previous.DestinationPath,
            previous.DestinationParentIdentity,
            previous.StagingFileName,
            previous.StagingFileIdentity,
            previous.StagingLength,
            previous.Phase,
            previous.Sequence,
            previous.RecordedAtUtc,
            incompleteTail);
        return true;
    }

    private static bool IsValidState(
        ArchiveCreationJournalState current,
        string expectedToken,
        ArchiveCreationJournalState? previous)
    {
        if (current.SchemaVersion != JournalSchemaVersion
            || !string.Equals(current.Token, expectedToken, StringComparison.Ordinal)
            || current.OperationId == Guid.Empty
            || !IsValidJournalDestinationPath(current.DestinationPath)
            || !IsValidFileIdentity(current.DestinationParentIdentity)
            || !string.Equals(
                current.StagingFileName,
                $".rainarchives-{expectedToken}.zip.staging",
                StringComparison.Ordinal)
            || current.RecordedAtUtc == default
            || current.RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        var expectedSequence = (int)current.Phase + 1;
        if (current.Sequence != expectedSequence)
        {
            return false;
        }

        if (current.Phase == ArchiveCreationRecoveryPhase.Admitted)
        {
            if (previous is not null
                || current.StagingFileIdentity is not null
                || current.StagingLength is not null)
            {
                return false;
            }
        }
        else
        {
            if (previous is null
                || current.Phase != previous.Phase + 1
                || current.Sequence != previous.Sequence + 1
                || !ImmutableFieldsMatch(current, previous)
                || !IsValidFileIdentity(current.StagingFileIdentity))
            {
                return false;
            }

            if (current.Phase == ArchiveCreationRecoveryPhase.Writing)
            {
                if (current.StagingLength is not null)
                {
                    return false;
                }
            }
            else if (current.StagingLength is null or < 0)
            {
                return false;
            }

            if (previous.StagingFileIdentity is not null
                && !string.Equals(
                    current.StagingFileIdentity,
                    previous.StagingFileIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (previous.StagingLength is not null
                && current.StagingLength != previous.StagingLength)
            {
                return false;
            }
        }

        return Enum.IsDefined(current.Phase);
    }

    private static bool ImmutableFieldsMatch(
        ArchiveCreationJournalState current,
        ArchiveCreationJournalState previous) =>
        current.SchemaVersion == previous.SchemaVersion
        && string.Equals(current.Token, previous.Token, StringComparison.Ordinal)
        && current.OperationId == previous.OperationId
        && string.Equals(current.DestinationPath, previous.DestinationPath, StringComparison.Ordinal)
        && string.Equals(
            current.DestinationParentIdentity,
            previous.DestinationParentIdentity,
            StringComparison.Ordinal)
        && string.Equals(current.StagingFileName, previous.StagingFileName, StringComparison.Ordinal);

    private static bool IsValidFileIdentity(string? identity)
    {
        if (identity is null || identity.Length != 25 || identity[8] != ':')
        {
            return false;
        }

        for (var index = 0; index < identity.Length; index++)
        {
            if (index == 8)
            {
                continue;
            }

            var character = identity[index];
            if (character is not (>= '0' and <= '9')
                && character is not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidJournalDestinationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith("//?/", StringComparison.Ordinal)
            || path.StartsWith("//./", StringComparison.Ordinal))
        {
            return false;
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!string.Equals(canonical, path, StringComparison.Ordinal)
            || !TryGetRelativeFilePath(canonical, out var relativePath))
        {
            return false;
        }

        return JournalPathPolicy.TryNormalize(
            relativePath,
            out _,
            out _,
            out _);
    }

    private static bool TryGetRelativeFilePath(string path, out string relativePath)
    {
        relativePath = string.Empty;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var offset = root.Length;
        if (!Path.EndsInDirectorySeparator(root))
        {
            if (offset >= path.Length
                || path[offset] is not ('\\' or '/'))
            {
                return false;
            }

            offset++;
        }

        if (offset >= path.Length || path[offset] is '\\' or '/')
        {
            return false;
        }

        relativePath = path[offset..];
        return true;
    }

    private static ArchiveProviderException UnverifiedRecord(Exception? innerException = null) =>
        new(
            ArchiveErrorCategory.CorruptArchive,
            "recovery.record_unverified",
            "The archive-creation recovery record could not be verified.",
            innerException);

    private static bool TryGetToken(string? operationName, out string token)
    {
        token = string.Empty;
        if (operationName is null
            || operationName.Length != OperationPrefix.Length + TokenCharacters
            || !operationName.StartsWith(OperationPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = operationName.AsSpan(OperationPrefix.Length);
        foreach (var character in candidate)
        {
            if (character is not (>= '0' and <= '9')
                && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        token = candidate.ToString();
        return true;
    }

    private static bool IsSharingViolation(Exception exception)
    {
        var win32Code = exception switch
        {
            Win32Exception native => native.NativeErrorCode,
            IOException io => io.HResult & 0xFFFF,
            _ => 0,
        };
        return win32Code is 32 or 33;
    }

    private static bool IsLocalFixedDrivePath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not { Length: 3 }
            || root[1] != Path.VolumeSeparatorChar
            || !Path.EndsInDirectorySeparator(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ArchiveCreationRecoveryInventory EmptyInventory() =>
        new(0, 0, 0, 0, false, false, []);

    private static ArchiveCreationRecoveryInventory IncompleteInventory() =>
        new(0, 0, 0, 0, false, true, []);

    private sealed record ArchiveCreationJournalState(
        int SchemaVersion,
        string Token,
        Guid OperationId,
        string DestinationPath,
        string DestinationParentIdentity,
        string StagingFileName,
        string? StagingFileIdentity,
        long? StagingLength,
        ArchiveCreationRecoveryPhase Phase,
        int Sequence,
        DateTimeOffset RecordedAtUtc);

    internal sealed class ArchiveCreationRecoveryTransaction : IAsyncDisposable
    {
        private readonly HandleRelativeExtractionRoot _root;
        private HandleRelativeExtractionRoot.HandleRelativeOutputFile? _journal;
        private ArchiveCreationJournalState? _state;
        private bool _disposed;

        internal ArchiveCreationRecoveryTransaction(
            HandleRelativeExtractionRoot root,
            HandleRelativeExtractionRoot.HandleRelativeOutputFile journal,
            string operationDirectoryPath,
            string token,
            OperationId operationId,
            string destinationPath,
            string destinationParentIdentity)
        {
            _root = root;
            _journal = journal;
            OperationDirectoryPath = operationDirectoryPath;
            Token = token;
            OperationId = operationId;
            DestinationPath = destinationPath;
            DestinationParentIdentity = destinationParentIdentity;
            StagingFileName = $".rainarchives-{token}.zip.staging";
        }

        internal string OperationDirectoryPath { get; }
        internal string Token { get; }
        internal OperationId OperationId { get; }
        internal string DestinationPath { get; }
        internal string DestinationParentIdentity { get; }
        internal string StagingFileName { get; }

        internal ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            AppendAsync(
                ArchiveCreationRecoveryPhase.Admitted,
                stagingFileIdentity: null,
                stagingLength: null,
                cancellationToken);

        internal ValueTask RecordWritingAsync(
            string stagingFileIdentity,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stagingFileIdentity);
            return AppendAsync(
                ArchiveCreationRecoveryPhase.Writing,
                stagingFileIdentity,
                stagingLength: null,
                cancellationToken);
        }

        internal ValueTask RecordFinalizedAsync(
            long stagingLength,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(stagingLength);
            return AppendAsync(
                ArchiveCreationRecoveryPhase.Finalized,
                RequiredState().StagingFileIdentity,
                stagingLength,
                cancellationToken);
        }

        internal ValueTask RecordCommitIntentAsync(CancellationToken cancellationToken) =>
            AppendAsync(
                ArchiveCreationRecoveryPhase.CommitIntent,
                RequiredState().StagingFileIdentity,
                RequiredState().StagingLength,
                cancellationToken);

        internal ValueTask RecordCommittedAsync(CancellationToken cancellationToken) =>
            AppendAsync(
                ArchiveCreationRecoveryPhase.Committed,
                RequiredState().StagingFileIdentity,
                RequiredState().StagingLength,
                cancellationToken);

        internal async ValueTask DeleteAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Exception? failure = null;
            try
            {
                if (_journal is not null)
                {
                    await _journal.DisposeAsync().ConfigureAwait(false);
                    _journal = null;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure is null)
            {
                try
                {
                    _ = _root.ValidateKnownTree();
                    _root.DeleteSingleFileTree(JournalFileName);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            try
            {
                _root.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(
                        "Multiple archive recovery-journal cleanup steps failed.",
                        failure,
                        exception);
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
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
                if (_journal is not null)
                {
                    await _journal.DisposeAsync().ConfigureAwait(false);
                    _journal = null;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                _root.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(
                        "Multiple archive recovery-journal preservation steps failed.",
                        failure,
                        exception);
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private async ValueTask AppendAsync(
            ArchiveCreationRecoveryPhase phase,
            string? stagingFileIdentity,
            long? stagingLength,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var expectedPhase = _state is null
                ? ArchiveCreationRecoveryPhase.Admitted
                : _state.Phase + 1;
            if (phase != expectedPhase || !Enum.IsDefined(phase))
            {
                throw new InvalidOperationException(
                    $"Archive recovery phase '{phase}' cannot follow '{_state?.Phase.ToString() ?? "none"}'.");
            }

            var next = new ArchiveCreationJournalState(
                JournalSchemaVersion,
                Token,
                OperationId.Value,
                DestinationPath,
                DestinationParentIdentity,
                StagingFileName,
                stagingFileIdentity,
                stagingLength,
                phase,
                (int)phase + 1,
                DateTimeOffset.UtcNow);
            if (!IsValidState(next, Token, _state))
            {
                throw new InvalidOperationException("The archive recovery phase data is invalid.");
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(next, JournalJsonOptions);
            if (payload.Length > MaximumFramePayloadBytes)
            {
                throw new IOException("The archive recovery record exceeds its bounded frame size.");
            }

            var header = new byte[FrameHeaderBytes];
            FrameMagic.CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(
                header.AsSpan(FrameMagic.Length, sizeof(int)),
                payload.Length);
            SHA256.HashData(
                payload,
                header.AsSpan(FrameMagic.Length + sizeof(int), SHA256.HashSizeInBytes));

            var stream = (FileStream)(_journal?.Stream
                ?? throw new ObjectDisposedException(nameof(ArchiveCreationRecoveryTransaction)));
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            _state = next;
        }

        private ArchiveCreationJournalState RequiredState() =>
            _state ?? throw new InvalidOperationException("The archive recovery journal is not initialized.");
    }
}
