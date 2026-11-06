using System.ComponentModel;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

/// <summary>
/// Provides path-free, explicit recovery review for worker-owned nested archive
/// copies. Nothing is deleted during inventory or inspection.
/// </summary>
public sealed class NestedTemporaryWorkspaceRecoveryStore
{
    private readonly string _rootPath;

    public NestedTemporaryWorkspaceRecoveryStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(_rootPath))
        {
            throw new DirectoryNotFoundException(
                "The nested-workspace recovery root does not exist.");
        }
    }

    public ListNestedWorkspaceRecoveriesResponse List(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enumeration = EnumerateCandidateNames(
            ProtocolConstants.MaximumNestedWorkspaceRecoveryRecords,
            cancellationToken);
        var active = 0;
        var suspicious = enumeration.InvalidNameCount;
        var incomplete = enumeration.InspectionIncomplete;
        var recoveries = new List<NestedWorkspaceRecoverySummary>();

        foreach (var group in enumeration.ValidNames
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var names = group.ToArray();
            if (names.Length != 1)
            {
                suspicious = checked(suspicious + names.Length);
                continue;
            }

            var recoveryId = GetRecoveryId(names[0]);
            var inspection = InspectLocated(recoveryId, names[0], cancellationToken);
            switch (inspection.Assessment)
            {
                case NestedWorkspaceRecoveryAssessment.VerifiedDisposable:
                    recoveries.Add(new NestedWorkspaceRecoverySummary(recoveryId));
                    break;
                case NestedWorkspaceRecoveryAssessment.Active:
                    active = checked(active + 1);
                    break;
                case NestedWorkspaceRecoveryAssessment.InspectionIncomplete:
                    suspicious = checked(suspicious + 1);
                    incomplete = true;
                    break;
                case NestedWorkspaceRecoveryAssessment.IdentityUnavailable:
                case NestedWorkspaceRecoveryAssessment.UnexpectedContents:
                    suspicious = checked(suspicious + 1);
                    break;
                default:
                    throw new InvalidOperationException(
                        "The nested-workspace recovery assessment is invalid.");
            }
        }

        recoveries.Sort(static (left, right) => string.Compare(
            left.RecoveryId,
            right.RecoveryId,
            StringComparison.Ordinal));
        return new ListNestedWorkspaceRecoveriesResponse(
            recoveries.AsReadOnly(),
            active,
            suspicious,
            enumeration.IsTruncated,
            incomplete);
    }

    public InspectNestedWorkspaceRecoveryResponse Inspect(
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        ValidateRecoveryId(recoveryId);
        cancellationToken.ThrowIfCancellationRequested();
        var location = LocateRecovery(recoveryId, cancellationToken);
        return location.State switch
        {
            RecoveryLocationState.Missing => throw RecoveryNotFound(),
            RecoveryLocationState.Ambiguous => AssessmentOnly(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.UnexpectedContents),
            _ => InspectLocated(recoveryId, location.WorkspaceName!, cancellationToken),
        };
    }

    public async Task<InspectNestedWorkspaceReopenResponse> InspectForReopenAsync(
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        ValidateRecoveryId(recoveryId);
        cancellationToken.ThrowIfCancellationRequested();
        var location = LocateRecovery(recoveryId, cancellationToken);
        if (location.State == RecoveryLocationState.Missing)
        {
            throw RecoveryNotFound();
        }

        if (location.State == RecoveryLocationState.Ambiguous)
        {
            return ReopenAssessmentOnly(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.UnexpectedContents);
        }

        try
        {
            using var root = HandleRelativeExtractionRoot.OpenNestedWorkspaceRecoveryRoot(_rootPath);
            using var lease = root.OpenNestedWorkspaceRecovery(
                location.WorkspaceName!,
                forMutation: false,
                cancellationToken);
            var evidence = await lease
                .CreateReopenEvidenceAsync(cancellationToken)
                .ConfigureAwait(false);
            return new InspectNestedWorkspaceReopenResponse(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
                lease.SourceCount,
                lease.TotalSourceBytes,
                evidence.Revision,
                evidence.Sources,
                DateTimeOffset.UtcNow,
                SupportsIndependentReopen: evidence.Sources.Any(source => source.Length > 0));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception exception) when (IsSharingViolation(exception))
        {
            return ReopenAssessmentOnly(recoveryId, NestedWorkspaceRecoveryAssessment.Active);
        }
        catch (NestedWorkspaceRecoveryIdentityUnavailableException)
        {
            return ReopenAssessmentOnly(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.IdentityUnavailable);
        }
        catch (Exception exception) when (
            exception is NestedWorkspaceRecoveryUnsafeException
                or ArchiveProviderException)
        {
            return ReopenAssessmentOnly(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.UnexpectedContents);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or PlatformNotSupportedException)
        {
            return ReopenAssessmentOnly(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.InspectionIncomplete);
        }
    }

    public async Task CopySourceForIndependentReopenAsync(
        string recoveryId,
        string expectedRevision,
        int expectedSourceCount,
        long expectedTotalSourceBytes,
        string sourceId,
        long expectedSourceLength,
        string expectedContentSha256,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ValidateRecoveryId(recoveryId);
        ValidateRecoveryId(expectedRevision);
        ValidateRecoveryId(sourceId);
        ValidateRecoveryId(expectedContentSha256);
        ArgumentNullException.ThrowIfNull(destination);
        if (expectedSourceCount is <= 0 or > ProtocolConstants.MaximumOpenSessions
            || expectedTotalSourceBytes is <= 0
                or > ProtocolConstants.MaximumActiveNestedArchiveBytes
            || expectedSourceLength is <= 0 or > ProtocolConstants.MaximumNestedArchiveBytes
            || expectedSourceLength > expectedTotalSourceBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSourceLength),
                "The nested-workspace reopen evidence is outside the bounded limits.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var location = LocateRecovery(recoveryId, cancellationToken);
        if (location.State != RecoveryLocationState.Exact)
        {
            throw RecoveryChanged(
                "The nested-workspace recovery is missing or ambiguous and was preserved.");
        }

        try
        {
            using var root = HandleRelativeExtractionRoot.OpenNestedWorkspaceRecoveryRoot(_rootPath);
            using var lease = root.OpenNestedWorkspaceRecovery(
                location.WorkspaceName!,
                forMutation: false,
                cancellationToken);
            var evidence = await lease
                .CreateReopenEvidenceAsync(cancellationToken)
                .ConfigureAwait(false);
            var source = evidence.Sources.SingleOrDefault(candidate => string.Equals(
                candidate.SourceId,
                sourceId,
                StringComparison.Ordinal));
            if (!string.Equals(evidence.Revision, expectedRevision, StringComparison.Ordinal)
                || lease.SourceCount != expectedSourceCount
                || lease.TotalSourceBytes != expectedTotalSourceBytes
                || source is null
                || source.Length != expectedSourceLength
                || !string.Equals(
                    source.ContentSha256,
                    expectedContentSha256,
                    StringComparison.Ordinal))
            {
                throw RecoveryChanged(
                    "The nested-workspace recovery changed after it was inspected and was preserved.");
            }

            await lease
                .CopySourceToAsync(sourceId, destination, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (Win32Exception exception) when (IsSharingViolation(exception))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "nested_recovery.active",
                "The nested-workspace recovery is active or held open and was preserved.",
                exception);
        }
        catch (NestedWorkspaceRecoveryIdentityUnavailableException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "nested_recovery.identity_unavailable",
                "The filesystem could not provide all identity evidence required for safe reopen.",
                exception);
        }
        catch (NestedWorkspaceRecoveryUnsafeException exception)
        {
            throw RecoveryChanged(
                "The nested-workspace recovery no longer has the exact inspected structure and was preserved.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "nested_recovery.reopen_io_failed",
                "The nested-workspace source could not be safely copied for independent reopen.",
                exception);
        }
    }

    public DiscardNestedWorkspaceRecoveryResponse Discard(
        string recoveryId,
        string expectedRevision,
        int expectedSourceCount,
        long expectedTotalSourceBytes,
        CancellationToken cancellationToken = default) =>
        DiscardCore(
            recoveryId,
            expectedRevision,
            expectedSourceCount,
            expectedTotalSourceBytes,
            checkpoint: null,
            cancellationToken);

    internal DiscardNestedWorkspaceRecoveryResponse Discard(
        string recoveryId,
        string expectedRevision,
        int expectedSourceCount,
        long expectedTotalSourceBytes,
        Action<NestedWorkspaceRecoveryDiscardCheckpoint> checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return DiscardCore(
            recoveryId,
            expectedRevision,
            expectedSourceCount,
            expectedTotalSourceBytes,
            checkpoint,
            cancellationToken);
    }

    private DiscardNestedWorkspaceRecoveryResponse DiscardCore(
        string recoveryId,
        string expectedRevision,
        int expectedSourceCount,
        long expectedTotalSourceBytes,
        Action<NestedWorkspaceRecoveryDiscardCheckpoint>? checkpoint,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryId(recoveryId);
        if (!IsLowercaseSha256(expectedRevision))
        {
            throw new ArgumentException(
                "The nested-workspace recovery revision must be 64 lowercase hexadecimal characters.",
                nameof(expectedRevision));
        }

        if (expectedSourceCount is < 0 or > ProtocolConstants.MaximumOpenSessions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSourceCount),
                "The nested-workspace source count is outside the recovery bound.");
        }

        if (expectedTotalSourceBytes is < 0
            or > ProtocolConstants.MaximumActiveNestedArchiveBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedTotalSourceBytes),
                "The nested-workspace byte total is outside the recovery bound.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var location = LocateRecovery(recoveryId, cancellationToken);
        if (location.State == RecoveryLocationState.Missing)
        {
            return new DiscardNestedWorkspaceRecoveryResponse(
                recoveryId,
                NestedWorkspaceRecoveryDiscardResult.AlreadyAbsent,
                DateTimeOffset.UtcNow);
        }

        if (location.State == RecoveryLocationState.Ambiguous)
        {
            throw RecoveryChanged(
                "The nested-workspace recovery name is ambiguous and was preserved.");
        }

        try
        {
            using var root = HandleRelativeExtractionRoot.OpenNestedWorkspaceRecoveryRoot(_rootPath);
            using var lease = root.OpenNestedWorkspaceRecovery(
                location.WorkspaceName!,
                forMutation: true,
                cancellationToken);
            if (!string.Equals(lease.Revision, expectedRevision, StringComparison.Ordinal)
                || lease.SourceCount != expectedSourceCount
                || lease.TotalSourceBytes != expectedTotalSourceBytes)
            {
                throw RecoveryChanged(
                    "The nested-workspace recovery changed after it was inspected and was preserved.");
            }

            lease.DeleteExactTree(
                expectedRevision,
                expectedSourceCount,
                expectedTotalSourceBytes,
                checkpoint,
                cancellationToken);
            return new DiscardNestedWorkspaceRecoveryResponse(
                recoveryId,
                NestedWorkspaceRecoveryDiscardResult.Discarded,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (Win32Exception exception) when (IsSharingViolation(exception))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.DestinationConflict,
                "nested_recovery.active",
                "The nested-workspace recovery is active or held open and was preserved.",
                exception);
        }
        catch (NestedWorkspaceRecoveryIdentityUnavailableException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "nested_recovery.identity_unavailable",
                "The filesystem could not provide all identity evidence required for safe deletion.",
                exception);
        }
        catch (NestedWorkspaceRecoveryUnsafeException exception)
        {
            throw RecoveryChanged(
                "The nested-workspace recovery no longer has the exact inspected structure and was preserved.",
                exception);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 5 or 1314)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.PermissionDenied,
                "nested_recovery.permission_denied",
                "Windows denied access while Rain Archives was validating the nested-workspace recovery.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "nested_recovery.io_failed",
                "The nested-workspace recovery could not be safely validated or deleted.",
                exception);
        }
    }

    private InspectNestedWorkspaceRecoveryResponse InspectLocated(
        string recoveryId,
        string workspaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var root = HandleRelativeExtractionRoot.OpenNestedWorkspaceRecoveryRoot(_rootPath);
            using var lease = root.OpenNestedWorkspaceRecovery(
                workspaceName,
                forMutation: false,
                cancellationToken);
            return new InspectNestedWorkspaceRecoveryResponse(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
                lease.SourceCount,
                lease.TotalSourceBytes,
                lease.Revision,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception exception) when (IsSharingViolation(exception))
        {
            return AssessmentOnly(recoveryId, NestedWorkspaceRecoveryAssessment.Active);
        }
        catch (NestedWorkspaceRecoveryIdentityUnavailableException)
        {
            return AssessmentOnly(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.IdentityUnavailable);
        }
        catch (Exception exception) when (
            exception is NestedWorkspaceRecoveryUnsafeException
                or ArchiveProviderException)
        {
            return AssessmentOnly(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.UnexpectedContents);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or PlatformNotSupportedException)
        {
            return AssessmentOnly(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.InspectionIncomplete);
        }
    }

    private CandidateNameEnumeration EnumerateCandidateNames(
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        var names = new List<string>(maximumEntries);
        var invalid = 0;
        var truncated = false;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         _rootPath,
                         OwnedNestedTemporaryWorkspace.WorkspacePrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (names.Count + invalid == maximumEntries)
                {
                    truncated = true;
                    break;
                }

                var name = Path.GetFileName(path);
                if (OwnedNestedTemporaryWorkspace.TryGetToken(name, out _))
                {
                    names.Add(name);
                }
                else
                {
                    invalid = checked(invalid + 1);
                }
            }

            return new CandidateNameEnumeration(
                names.AsReadOnly(),
                invalid,
                truncated,
                InspectionIncomplete: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new CandidateNameEnumeration(
                names.AsReadOnly(),
                invalid,
                truncated,
                InspectionIncomplete: true);
        }
    }

    private RecoveryLocation LocateRecovery(
        string recoveryId,
        CancellationToken cancellationToken)
    {
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        try
        {
            var matches = new List<string>(2);
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         _rootPath,
                         workspaceName,
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                matches.Add(Path.GetFileName(path));
                if (matches.Count == 2)
                {
                    break;
                }
            }

            var exactCount = matches.Count(name => string.Equals(
                name,
                workspaceName,
                StringComparison.Ordinal));
            var insensitiveCount = matches.Count(name => string.Equals(
                name,
                workspaceName,
                StringComparison.OrdinalIgnoreCase));
            if (exactCount == 0)
            {
                return new RecoveryLocation(RecoveryLocationState.Missing, null);
            }

            return exactCount == 1 && insensitiveCount == 1
                ? new RecoveryLocation(RecoveryLocationState.Exact, workspaceName)
                : new RecoveryLocation(RecoveryLocationState.Ambiguous, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "nested_recovery.inventory_failed",
                "The nested-workspace recovery inventory could not be read.",
                exception);
        }
    }

    private static InspectNestedWorkspaceRecoveryResponse AssessmentOnly(
        string recoveryId,
        NestedWorkspaceRecoveryAssessment assessment) =>
        new(
            recoveryId,
            assessment,
            SourceCount: null,
            TotalSourceBytes: null,
            Revision: null,
            DateTimeOffset.UtcNow);

    private static InspectNestedWorkspaceReopenResponse ReopenAssessmentOnly(
        string recoveryId,
        NestedWorkspaceRecoveryAssessment assessment) =>
        new(
            recoveryId,
            assessment,
            SourceCount: null,
            TotalSourceBytes: null,
            Revision: null,
            Sources: null,
            DateTimeOffset.UtcNow,
            SupportsIndependentReopen: false);

    private static string GetRecoveryId(string workspaceName)
    {
        if (!OwnedNestedTemporaryWorkspace.TryGetToken(workspaceName, out var recoveryId))
        {
            throw new InvalidOperationException(
                "A validated nested-workspace name lost its recovery identifier.");
        }

        return recoveryId;
    }

    private static void ValidateRecoveryId(string recoveryId)
    {
        if (!IsLowercaseSha256(recoveryId))
        {
            throw new ArgumentException(
                "The nested-workspace recovery identifier must be 64 lowercase hexadecimal characters.",
                nameof(recoveryId));
        }
    }

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static bool IsSharingViolation(Win32Exception exception) =>
        exception.NativeErrorCode is 32 or 33;

    private static ArchiveProviderException RecoveryNotFound() =>
        new(
            ArchiveErrorCategory.InvalidRequest,
            "nested_recovery.not_found",
            "The nested-workspace recovery identifier was not found.");

    private static ArchiveProviderException RecoveryChanged(
        string message,
        Exception? innerException = null) =>
        new(
            ArchiveErrorCategory.SourceChanged,
            "nested_recovery.changed",
            message,
            innerException);

    private sealed record CandidateNameEnumeration(
        IReadOnlyList<string> ValidNames,
        int InvalidNameCount,
        bool IsTruncated,
        bool InspectionIncomplete);

    private sealed record RecoveryLocation(
        RecoveryLocationState State,
        string? WorkspaceName);

    private enum RecoveryLocationState
    {
        Missing,
        Exact,
        Ambiguous,
    }

    internal enum NestedWorkspaceRecoveryDiscardCheckpoint
    {
        FirstArtifactMutationApplied,
        IndependentReopenSourceCopiedAndFlushed,
    }
}
