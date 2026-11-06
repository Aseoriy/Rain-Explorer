using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed record ExtractionRecoveryRecord(
    string RecoveryId,
    OperationId OperationId,
    string DestinationPath,
    string DestinationParentIdentity,
    string StagingDirectoryName,
    string? StagingDirectoryIdentity,
    int? FileCount,
    int? DirectoryCount,
    long? TotalBytes,
    int? NamedStreamCount,
    string? TreeRevision,
    ExtractionRecoveryPhase Phase,
    int Sequence,
    DateTimeOffset RecordedAtUtc,
    bool HasIncompleteTail);

public sealed record ExtractionRecoveryInventory(
    int ActiveOperationCount,
    int PotentialRecoveryCount,
    int SuspiciousOperationCount,
    int IncompleteJournalCount,
    bool IsTruncated,
    bool InspectionIncomplete,
    IReadOnlyList<ExtractionRecoveryRecord> RecoveryRecords);

/// <summary>
/// Holds bounded append-only extraction ownership records on a local fixed drive.
/// Startup listing reads only this local root and never probes a recorded destination.
/// </summary>
public sealed class ExtractionRecoveryStore
{
    internal const string OperationPrefix = "rainarchives-extraction-";
    internal const string JournalFileName = "journal.bin";
    internal const string ReservedFileName = "reserved.bin";
    private const string StagingPrefix = ".rainarchives-extract-";
    private const string StagingSuffix = ".staging";
    private const int TokenCharacters = 64;
    private const int MaximumCreateAttempts = 8;
    private const int JournalSchemaVersion = 1;
    private const int MaximumJournalFrames = 5;
    private const int MaximumFramePayloadBytes = 256 * 1024;
    private const int FrameHeaderBytes = 4 + sizeof(int) + SHA256.HashSizeInBytes;
    private const int MaximumJournalBytes =
        MaximumJournalFrames * (FrameHeaderBytes + MaximumFramePayloadBytes);
    private static readonly byte[] FrameMagic = "RAEJ"u8.ToArray();
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };
    private static readonly ExtractionPathPolicy JournalPathPolicy = new();

    public ExtractionRecoveryStore(string rootDirectory)
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
            "Extraction");
    }

    public ExtractionRecoveryInventory Inspect() =>
        Inspect(ProtocolConstants.MaximumExtractionRecoveryRecords);

    public ValueTask<InspectExtractionRecoveryResponse> InspectArtifactsAsync(
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        ValidateRecoveryId(recoveryId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLocalRoot)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "extraction_recovery.inventory_unavailable",
                "The local extraction recovery inventory is unavailable.");
        }

        try
        {
            using var recoveryRoot = HandleRelativeExtractionRoot
                .OpenArchiveCreationRecoveryRoot(RootDirectory);
            using var journal = recoveryRoot.OpenJournal(
                OperationPrefix + recoveryId,
                expectedOperationFileId: null,
                JournalFileName,
                ReservedFileName,
                exclusive: false,
                cancellationToken);
            if (journal.Disposition is not null
                || !TryReadJournal(journal.Journal, recoveryId, out var record)
                || record is null)
            {
                throw UnverifiedRecord();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var destination = HandleRelativeExtractionRoot
                .InspectExtractionRecoveryArtifacts(record, cancellationToken);
            var root = Path.GetPathRoot(record.DestinationPath);
            return ValueTask.FromResult(new InspectExtractionRecoveryResponse(
                ToSummary(record),
                record.DestinationPath,
                root is not null && root.StartsWith(@"\\", StringComparison.Ordinal),
                record.FileCount,
                record.DirectoryCount,
                record.TotalBytes,
                record.NamedStreamCount,
                record.TreeRevision,
                destination.DestinationParent,
                destination.StagingArtifact,
                destination.FinalArtifact,
                destination.Assessment,
                DateTimeOffset.UtcNow,
                SupportsMutation: false,
                SupportsRetry: false,
                SupportsCleanup: false));
        }
        catch (Exception exception) when (IsSharingViolation(exception))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.SourceChanged,
                "extraction_recovery.active",
                "The extraction recovery record is still owned by an active operation.",
                exception);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException
                || exception is ArchiveProviderException provider
                    && provider.Code == "destination.path_replaced")
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "extraction_recovery.not_found",
                "The extraction recovery record was not found.",
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

    internal ExtractionRecoveryInventory Inspect(int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
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
                    maximumEntries,
                    CancellationToken.None);
                var active = 0;
                var potential = 0;
                var suspicious = 0;
                var incomplete = 0;
                var attentionTruncated = false;
                var records = new List<ExtractionRecoveryRecord>();
                foreach (var operation in enumeration.Children)
                {
                    if (!TryGetToken(operation.Name, out var token))
                    {
                        AddAttention(ref suspicious, ref attentionTruncated);
                        continue;
                    }

                    if (operation.FileId is null or 0)
                    {
                        AddAttention(ref suspicious, ref attentionTruncated);
                        continue;
                    }

                    try
                    {
                        using var journal = recoveryRoot.OpenJournal(
                            operation.Name,
                            operation.FileId,
                            JournalFileName,
                            ReservedFileName,
                            exclusive: false,
                            CancellationToken.None);
                        if (journal.Disposition is not null
                            || !TryReadJournal(journal.Journal, token, out var record)
                            || record is null)
                        {
                            AddAttention(ref suspicious, ref attentionTruncated);
                            continue;
                        }

                        if (records.Count < maximumEntries)
                        {
                            records.Add(record);
                            potential++;
                            if (record.HasIncompleteTail)
                            {
                                incomplete++;
                            }
                        }
                        else
                        {
                            attentionTruncated = true;
                        }
                    }
                    catch (Exception exception) when (IsSharingViolation(exception))
                    {
                        AddAttention(ref active, ref attentionTruncated);
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
                        AddAttention(ref suspicious, ref attentionTruncated);
                    }
                }

                records.Sort(static (left, right) => string.Compare(
                    left.RecoveryId,
                    right.RecoveryId,
                    StringComparison.Ordinal));
                return new ExtractionRecoveryInventory(
                    active,
                    potential,
                    suspicious,
                    incomplete,
                    enumeration.IsTruncated || attentionTruncated,
                    InspectionIncomplete: false,
                    records.AsReadOnly());

                void AddAttention(ref int count, ref bool truncated)
                {
                    if (active + potential + suspicious < maximumEntries)
                    {
                        count++;
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException
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

    internal async ValueTask<ExtractionRecoveryTransaction> BeginAsync(
        OperationId operationId,
        string destinationPath,
        string destinationParentIdentity,
        CancellationToken cancellationToken)
    {
        if (operationId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "An extraction recovery journal requires a non-empty operation ID.",
                nameof(operationId));
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
                var transaction = new ExtractionRecoveryTransaction(
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
                            "Extraction recovery-journal creation and cleanup both failed.",
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

        throw new IOException("Could not allocate a unique extraction recovery journal.");
    }

    internal static ExtractionRecoverySummary ToSummary(ExtractionRecoveryRecord record) =>
        new(
            record.RecoveryId,
            record.Phase switch
            {
                ExtractionRecoveryPhase.Admitted or ExtractionRecoveryPhase.Writing =>
                    ExtractionRecoveryInventoryState.Incomplete,
                ExtractionRecoveryPhase.Finalized =>
                    ExtractionRecoveryInventoryState.Finalized,
                ExtractionRecoveryPhase.CommitIntent =>
                    ExtractionRecoveryInventoryState.PublicationUncertain,
                ExtractionRecoveryPhase.Committed =>
                    ExtractionRecoveryInventoryState.PublishedWithoutTerminal,
                _ => throw new InvalidOperationException("The extraction recovery phase is invalid."),
            },
            record.Phase,
            record.Sequence,
            record.RecordedAtUtc,
            record.HasIncompleteTail);

    private void EnsureRootAvailable()
    {
        if (!IsLocalRoot)
        {
            throw new IOException("Extraction recovery journals require a local fixed-drive path.");
        }

        ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(RootDirectory);
        Directory.CreateDirectory(RootDirectory);
        ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(RootDirectory);
        _ = FileSystemIdentityService.CaptureExistingDirectoryIdentity(RootDirectory);
    }

    private static bool TryReadJournal(
        FileStream journal,
        string expectedToken,
        out ExtractionRecoveryRecord? record)
    {
        record = null;
        if (journal.Length <= 0 || journal.Length > MaximumJournalBytes)
        {
            return false;
        }

        ExtractionJournalState? previous = null;
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
            var expectedHash = header.AsSpan(
                FrameMagic.Length + sizeof(int),
                SHA256.HashSizeInBytes);
            var actualHash = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                return false;
            }

            ExtractionJournalState? current;
            try
            {
                current = JsonSerializer.Deserialize<ExtractionJournalState>(
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

        record = new ExtractionRecoveryRecord(
            previous.Token,
            new OperationId(previous.OperationId),
            previous.DestinationPath,
            previous.DestinationParentIdentity,
            previous.StagingDirectoryName,
            previous.StagingDirectoryIdentity,
            previous.FileCount,
            previous.DirectoryCount,
            previous.TotalBytes,
            previous.NamedStreamCount,
            previous.TreeRevision,
            previous.Phase,
            previous.Sequence,
            previous.RecordedAtUtc,
            incompleteTail);
        return true;
    }

    private static bool IsValidState(
        ExtractionJournalState current,
        string expectedToken,
        ExtractionJournalState? previous)
    {
        if (current.SchemaVersion != JournalSchemaVersion
            || !string.Equals(current.Token, expectedToken, StringComparison.Ordinal)
            || current.OperationId == Guid.Empty
            || !IsValidJournalDestinationPath(current.DestinationPath)
            || !IsValidFileIdentity(current.DestinationParentIdentity)
            || !string.Equals(
                current.StagingDirectoryName,
                StagingPrefix + expectedToken + StagingSuffix,
                StringComparison.Ordinal)
            || current.RecordedAtUtc == default
            || current.RecordedAtUtc.Offset != TimeSpan.Zero
            || current.Sequence != (int)current.Phase + 1
            || !Enum.IsDefined(current.Phase))
        {
            return false;
        }

        if (current.Phase == ExtractionRecoveryPhase.Admitted)
        {
            return previous is null
                && current.StagingDirectoryIdentity is null
                && HasNoTreeEvidence(current);
        }

        if (previous is null
            || current.Phase != previous.Phase + 1
            || current.Sequence != previous.Sequence + 1
            || !ImmutableFieldsMatch(current, previous)
            || !IsValidFileIdentity(current.StagingDirectoryIdentity))
        {
            return false;
        }

        if (current.Phase == ExtractionRecoveryPhase.Writing)
        {
            return HasNoTreeEvidence(current);
        }

        if (!HasValidTreeEvidence(current))
        {
            return false;
        }

        if (previous.StagingDirectoryIdentity is not null
            && !string.Equals(
                current.StagingDirectoryIdentity,
                previous.StagingDirectoryIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }

        return previous.TreeRevision is null
            || TreeEvidenceMatches(current, previous);
    }

    private static bool ImmutableFieldsMatch(
        ExtractionJournalState current,
        ExtractionJournalState previous) =>
        current.SchemaVersion == previous.SchemaVersion
        && string.Equals(current.Token, previous.Token, StringComparison.Ordinal)
        && current.OperationId == previous.OperationId
        && string.Equals(current.DestinationPath, previous.DestinationPath, StringComparison.Ordinal)
        && string.Equals(
            current.DestinationParentIdentity,
            previous.DestinationParentIdentity,
            StringComparison.Ordinal)
        && string.Equals(
            current.StagingDirectoryName,
            previous.StagingDirectoryName,
            StringComparison.Ordinal);

    private static bool HasNoTreeEvidence(ExtractionJournalState state) =>
        state.FileCount is null
        && state.DirectoryCount is null
        && state.TotalBytes is null
        && state.NamedStreamCount is null
        && state.TreeRevision is null;

    private static bool HasValidTreeEvidence(ExtractionJournalState state) =>
        state.FileCount is >= 0 and <= ProtocolConstants.MaximumExtractionRecoveryObjects
        && state.DirectoryCount is >= 0 and <= ProtocolConstants.MaximumExtractionRecoveryObjects
        && (long)state.FileCount.Value + state.DirectoryCount.Value
            <= ProtocolConstants.MaximumExtractionRecoveryObjects
        && state.TotalBytes is >= 0 and <= ProtocolConstants.MaximumExtractionRecoveryBytes
        && state.NamedStreamCount is >= 0
            and <= ProtocolConstants.MaximumExtractionRecoveryObjects
        && state.NamedStreamCount <= state.FileCount
        && IsLowercaseSha256(state.TreeRevision);

    private static bool TreeEvidenceMatches(
        ExtractionJournalState current,
        ExtractionJournalState previous) =>
        current.FileCount == previous.FileCount
        && current.DirectoryCount == previous.DirectoryCount
        && current.TotalBytes == previous.TotalBytes
        && current.NamedStreamCount == previous.NamedStreamCount
        && string.Equals(current.TreeRevision, previous.TreeRevision, StringComparison.Ordinal);

    private static bool IsValidFileIdentity(string? identity)
    {
        if (identity is null || identity.Length != 25 || identity[8] != ':')
        {
            return false;
        }

        var hasNonzeroFileIdDigit = false;
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

            if (index > 8 && character != '0')
            {
                hasNonzeroFileIdDigit = true;
            }
        }

        return hasNonzeroFileIdDigit;
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

        return JournalPathPolicy.TryNormalize(relativePath, out _, out _, out _);
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
            if (offset >= path.Length || path[offset] is not ('\\' or '/'))
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

    private static void ValidateRecoveryId(string recoveryId)
    {
        if (!IsLowercaseSha256(recoveryId))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "extraction_recovery.invalid_id",
                "The extraction recovery identifier is invalid.");
        }
    }

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

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ArchiveProviderException UnverifiedRecord(Exception? innerException = null) =>
        new(
            ArchiveErrorCategory.CorruptArchive,
            "extraction_recovery.record_unverified",
            "The extraction recovery record could not be verified.",
            innerException);

    private static ExtractionRecoveryInventory EmptyInventory() =>
        new(0, 0, 0, 0, false, false, []);

    private static ExtractionRecoveryInventory IncompleteInventory() =>
        new(0, 0, 0, 0, false, true, []);

    private sealed record ExtractionJournalState(
        int SchemaVersion,
        string Token,
        Guid OperationId,
        string DestinationPath,
        string DestinationParentIdentity,
        string StagingDirectoryName,
        string? StagingDirectoryIdentity,
        int? FileCount,
        int? DirectoryCount,
        long? TotalBytes,
        int? NamedStreamCount,
        string? TreeRevision,
        ExtractionRecoveryPhase Phase,
        int Sequence,
        DateTimeOffset RecordedAtUtc);

    internal sealed class ExtractionRecoveryTransaction : IAsyncDisposable
    {
        private readonly HandleRelativeExtractionRoot _root;
        private HandleRelativeExtractionRoot.HandleRelativeOutputFile? _journal;
        private ExtractionJournalState? _state;
        private bool _disposed;

        internal ExtractionRecoveryTransaction(
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
            StagingDirectoryName = StagingPrefix + token + StagingSuffix;
        }

        internal string Token { get; }
        internal string OperationDirectoryPath { get; }
        internal OperationId OperationId { get; }
        internal string DestinationPath { get; }
        internal string DestinationParentIdentity { get; }
        internal string StagingDirectoryName { get; }

        internal ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            AppendAsync(
                ExtractionRecoveryPhase.Admitted,
                stagingDirectoryIdentity: null,
                evidence: null,
                cancellationToken);

        internal ValueTask RecordWritingAsync(
            string stagingDirectoryIdentity,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectoryIdentity);
            return AppendAsync(
                ExtractionRecoveryPhase.Writing,
                stagingDirectoryIdentity,
                evidence: null,
                cancellationToken);
        }

        internal ValueTask RecordFinalizedAsync(
            ExtractionTreeRecoveryEvidence evidence,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            return AppendAsync(
                ExtractionRecoveryPhase.Finalized,
                RequiredState().StagingDirectoryIdentity,
                evidence,
                cancellationToken);
        }

        internal ValueTask RecordCommitIntentAsync(CancellationToken cancellationToken) =>
            AppendAsync(
                ExtractionRecoveryPhase.CommitIntent,
                RequiredState().StagingDirectoryIdentity,
                RequiredEvidence(),
                cancellationToken);

        internal ValueTask RecordCommittedAsync(CancellationToken cancellationToken) =>
            AppendAsync(
                ExtractionRecoveryPhase.Committed,
                RequiredState().StagingDirectoryIdentity,
                RequiredEvidence(),
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
                        "Multiple extraction recovery-journal cleanup steps failed.",
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
                        "Multiple extraction recovery-journal preservation steps failed.",
                        failure,
                        exception);
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private async ValueTask AppendAsync(
            ExtractionRecoveryPhase phase,
            string? stagingDirectoryIdentity,
            ExtractionTreeRecoveryEvidence? evidence,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var expectedPhase = _state is null
                ? ExtractionRecoveryPhase.Admitted
                : _state.Phase + 1;
            if (phase != expectedPhase || !Enum.IsDefined(phase))
            {
                throw new InvalidOperationException(
                    $"Extraction recovery phase '{phase}' cannot follow '{_state?.Phase.ToString() ?? "none"}'.");
            }

            var next = new ExtractionJournalState(
                JournalSchemaVersion,
                Token,
                OperationId.Value,
                DestinationPath,
                DestinationParentIdentity,
                StagingDirectoryName,
                stagingDirectoryIdentity,
                evidence?.FileCount,
                evidence?.DirectoryCount,
                evidence?.TotalBytes,
                evidence?.NamedStreamCount,
                evidence?.Revision,
                phase,
                (int)phase + 1,
                DateTimeOffset.UtcNow);
            if (!IsValidState(next, Token, _state))
            {
                throw new InvalidOperationException("The extraction recovery phase data is invalid.");
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(next, JournalJsonOptions);
            if (payload.Length > MaximumFramePayloadBytes)
            {
                throw new IOException("The extraction recovery record exceeds its frame bound.");
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
                ?? throw new ObjectDisposedException(nameof(ExtractionRecoveryTransaction)));
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            _state = next;
        }

        private ExtractionJournalState RequiredState() =>
            _state ?? throw new InvalidOperationException(
                "The extraction recovery journal is not initialized.");

        private ExtractionTreeRecoveryEvidence RequiredEvidence()
        {
            var state = RequiredState();
            if (!HasValidTreeEvidence(state))
            {
                throw new InvalidOperationException(
                    "The extraction recovery tree evidence is unavailable.");
            }

            return new ExtractionTreeRecoveryEvidence(
                state.FileCount!.Value,
                state.DirectoryCount!.Value,
                state.TotalBytes!.Value,
                state.NamedStreamCount!.Value,
                state.TreeRevision!);
        }
    }
}
