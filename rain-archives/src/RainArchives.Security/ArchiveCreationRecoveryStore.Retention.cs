using System.ComponentModel;
using System.Text.Json;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed partial class ArchiveCreationRecoveryStore
{
    internal const int MaximumRetainedCompletedDispositionReceipts = 256;
    internal const int MaximumCompletedDispositionPrunesPerStartup = 256;
    private const int MaximumCompletedDispositionPrunesPerResolution = 1;
    internal const int CompletedDispositionInspectionOverhead =
        MaximumRetainedCompletedDispositionReceipts
        + MaximumCompletedDispositionPrunesPerStartup
        + 1;
    internal static readonly TimeSpan CompletedDispositionReceiptRetention =
        TimeSpan.FromDays(30);

    /// <summary>
    /// Performs one bounded, local-only retention pass over verified completed
    /// disposition receipts, then returns the ordinary read-only startup inventory.
    /// Recorded archive destinations are never contacted by this maintenance pass.
    /// </summary>
    public ArchiveCreationRecoveryInventory InspectForStartup() =>
        InspectForStartup(DateTimeOffset.UtcNow, CancellationToken.None);

    internal ArchiveCreationRecoveryInventory InspectForStartup(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = PruneCompletedDispositionReceipts(
                utcNow,
                checked(
                    ProtocolConstants.MaximumArchiveCreationRecoveryRecords
                    + CompletedDispositionInspectionOverhead),
                MaximumRetainedCompletedDispositionReceipts,
                MaximumCompletedDispositionPrunesPerStartup,
                cancellationToken);
        }
        catch (Exception exception) when (IsExpectedRetentionFailure(exception))
        {
            // Startup inventory remains authoritative. A maintenance failure cannot
            // convert local metadata into deletion authority or prevent inspection.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Inspect();
    }

    internal int PruneCompletedDispositionReceipts(
        DateTimeOffset utcNow,
        int maximumOperations,
        int maximumRetainedReceipts,
        int maximumPrunes,
        CancellationToken cancellationToken)
    {
        if (utcNow == default || utcNow.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Completed-receipt retention requires a non-default UTC time.",
                nameof(utcNow));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOperations);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedReceipts);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPrunes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLocalRoot || maximumPrunes == 0)
        {
            return 0;
        }

        try
        {
            using var recoveryRoot = HandleRelativeExtractionRoot
                .OpenArchiveCreationRecoveryRoot(RootDirectory);
            var enumeration = recoveryRoot.EnumerateOperations(
                maximumOperations,
                cancellationToken);
            var completed = new List<CompletedDispositionCandidate>();
            foreach (var operation in enumeration.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetToken(operation.Name, out var token))
                {
                    continue;
                }

                try
                {
                    using var journal = recoveryRoot.OpenJournal(
                        operation.Name,
                        operation.FileId,
                        JournalFileName,
                        DispositionFileName,
                        exclusive: false,
                        cancellationToken);
                    if (!TryReadJournal(journal.Journal, token, out var record)
                        || record is null
                        || journal.Disposition is null
                        || !TryReadDispositionReceipt(
                            journal.Disposition,
                            record,
                            out var receipt,
                            out var hasIncompleteTail,
                            out _)
                        || hasIncompleteTail
                        || receipt?.Phase
                            != ArchiveCreationRecoveryDispositionReceiptPhase.Completed
                        || receipt.ResolvedAtUtc is not { } resolvedAtUtc)
                    {
                        continue;
                    }

                    completed.Add(new CompletedDispositionCandidate(
                        operation.Name,
                        operation.FileId,
                        record.RecoveryId,
                        resolvedAtUtc));
                }
                catch (Exception exception) when (IsExpectedRetentionFailure(exception))
                {
                    // Active, malformed, inaccessible, or structurally unexpected
                    // records are retained without becoming pruning candidates.
                }
            }

            completed.Sort(static (left, right) =>
            {
                var timeComparison = right.ResolvedAtUtc.CompareTo(left.ResolvedAtUtc);
                return timeComparison != 0
                    ? timeComparison
                    : string.Compare(
                        right.RecoveryId,
                        left.RecoveryId,
                        StringComparison.Ordinal);
            });

            var expirationCutoff = utcNow - CompletedDispositionReceiptRetention;
            var pruned = 0;
            for (var index = 0;
                 index < completed.Count && pruned < maximumPrunes;
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = completed[index];
                var expired = candidate.ResolvedAtUtc <= expirationCutoff;
                var exceedsCount = !enumeration.IsTruncated
                    && index >= maximumRetainedReceipts;
                if (!expired && !exceedsCount)
                {
                    continue;
                }

                try
                {
                    using var prune = recoveryRoot.OpenCompletedRecordForPruning(
                        candidate.OperationName,
                        candidate.OperationFileId,
                        JournalFileName,
                        DispositionFileName,
                        cancellationToken);
                    if (!TryReadJournal(prune.Journal, candidate.RecoveryId, out var record)
                        || record is null
                        || !TryReadDispositionReceipt(
                            prune.Disposition,
                            record,
                            out var receipt,
                            out var hasIncompleteTail,
                            out _)
                        || hasIncompleteTail
                        || receipt?.Phase
                            != ArchiveCreationRecoveryDispositionReceiptPhase.Completed
                        || receipt.ResolvedAtUtc != candidate.ResolvedAtUtc)
                    {
                        continue;
                    }

                    prune.DeleteExactTree(cancellationToken);
                    pruned++;
                }
                catch (Exception exception) when (IsExpectedRetentionFailure(exception))
                {
                    // Failure preserves whatever state could not be deleted exactly.
                    // A partial metadata-only prune becomes suspicious inventory.
                }
            }

            return pruned;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException
                || exception is ArchiveProviderException provider
                    && provider.Code == "destination.path_replaced")
        {
            return 0;
        }
    }

    private void PruneCompletedDispositionReceiptsAfterResolution()
    {
        try
        {
            _ = PruneCompletedDispositionReceipts(
                DateTimeOffset.UtcNow,
                checked(
                    ProtocolConstants.MaximumArchiveCreationRecoveryRecords
                    + CompletedDispositionInspectionOverhead),
                MaximumRetainedCompletedDispositionReceipts,
                MaximumCompletedDispositionPrunesPerResolution,
                CancellationToken.None);
        }
        catch (Exception exception) when (IsExpectedRetentionFailure(exception))
        {
            // The recovery action and its completion receipt are already durable.
            // Best-effort metadata retention must not change that authoritative result.
        }
    }

    private static bool IsExpectedRetentionFailure(Exception exception) =>
        exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or Win32Exception
            or JsonException
            or NotSupportedException
            or ArchiveProviderException;

    private sealed record CompletedDispositionCandidate(
        string OperationName,
        ulong? OperationFileId,
        string RecoveryId,
        DateTimeOffset ResolvedAtUtc);
}
