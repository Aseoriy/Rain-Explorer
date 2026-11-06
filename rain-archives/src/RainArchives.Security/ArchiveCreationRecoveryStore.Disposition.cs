using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed partial class ArchiveCreationRecoveryStore
{
    private const int DispositionSchemaVersion = 1;
    private const int MaximumDispositionFrames = 2;
    private const int MaximumDispositionFramePayloadBytes = 64 * 1024;
    private const int DispositionFrameHeaderBytes =
        4 + sizeof(int) + SHA256.HashSizeInBytes;
    private const int MaximumDispositionBytes =
        MaximumDispositionFrames
        * (DispositionFrameHeaderBytes + MaximumDispositionFramePayloadBytes);
    private static readonly byte[] DispositionFrameMagic = "RACD"u8.ToArray();

    public ValueTask<ApplyArchiveCreationRecoveryDispositionResponse> ApplyDispositionAsync(
        string recoveryId,
        ArchiveCreationRecoveryPhase expectedPhase,
        int expectedSequence,
        DateTimeOffset expectedRecordedAtUtc,
        ArchiveCreationRecoveryDispositionAction action,
        CancellationToken cancellationToken = default) =>
        ApplyDispositionCoreAsync(
            recoveryId,
            expectedPhase,
            expectedSequence,
            expectedRecordedAtUtc,
            action,
            checkpoint: null,
            cancellationToken);

    internal ValueTask<ApplyArchiveCreationRecoveryDispositionResponse> ApplyDispositionAsync(
        string recoveryId,
        ArchiveCreationRecoveryPhase expectedPhase,
        int expectedSequence,
        DateTimeOffset expectedRecordedAtUtc,
        ArchiveCreationRecoveryDispositionAction action,
        Action<ArchiveCreationRecoveryDispositionCheckpoint> checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return ApplyDispositionCoreAsync(
            recoveryId,
            expectedPhase,
            expectedSequence,
            expectedRecordedAtUtc,
            action,
            checkpoint,
            cancellationToken);
    }

    private ValueTask<ApplyArchiveCreationRecoveryDispositionResponse> ApplyDispositionCoreAsync(
        string recoveryId,
        ArchiveCreationRecoveryPhase expectedPhase,
        int expectedSequence,
        DateTimeOffset expectedRecordedAtUtc,
        ArchiveCreationRecoveryDispositionAction action,
        Action<ArchiveCreationRecoveryDispositionCheckpoint>? checkpoint,
        CancellationToken cancellationToken)
    {
        if (!TryGetToken(OperationPrefix + recoveryId, out var token)
            || !string.Equals(token, recoveryId, StringComparison.Ordinal))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "recovery.invalid_id",
                "The archive-creation recovery identifier is invalid.");
        }

        if (!Enum.IsDefined(expectedPhase)
            || expectedSequence <= 0
            || expectedSequence != (int)expectedPhase + 1
            || expectedRecordedAtUtc == default
            || expectedRecordedAtUtc.Offset != TimeSpan.Zero
            || !Enum.IsDefined(action))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "recovery.disposition_invalid_request",
                "The archive-creation recovery disposition request is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLocalRoot)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "recovery.inventory_unavailable",
                "The local archive-creation recovery inventory is unavailable.");
        }

        ApplyArchiveCreationRecoveryDispositionResponse completedResponse;
        try
        {
            using var recoveryRoot = HandleRelativeExtractionRoot
                .OpenArchiveCreationRecoveryRoot(RootDirectory);
            using var recovery = recoveryRoot.OpenJournal(
                OperationPrefix + token,
                expectedOperationFileId: null,
                JournalFileName,
                DispositionFileName,
                exclusive: true,
                cancellationToken);
            if (!TryReadJournal(recovery.Journal, token, out var record)
                || record is null)
            {
                throw UnverifiedRecord();
            }

            if (record.HasIncompleteTail)
            {
                throw DispositionRefused(
                    "recovery.disposition_incomplete_journal",
                    "A recovery journal with an incomplete tail cannot be changed.");
            }

            if (record.Phase != expectedPhase
                || record.Sequence != expectedSequence
                || record.RecordedAtUtc != expectedRecordedAtUtc)
            {
                throw DispositionRefused(
                    "recovery.disposition_stale_revision",
                    "The recovery journal changed after it was inspected.");
            }

            ArchiveCreationRecoveryDispositionReceipt? receipt = null;
            var receiptHasIncompleteTail = false;
            var validReceiptLength = 0L;
            if (recovery.Disposition is not null
                && !TryReadDispositionReceipt(
                    recovery.Disposition,
                    record,
                    out receipt,
                    out receiptHasIncompleteTail,
                    out validReceiptLength))
            {
                throw UnverifiedRecord();
            }

            if (receipt is not null)
            {
                EnsureReceiptMatchesRequest(
                    receipt,
                    record,
                    expectedPhase,
                    expectedSequence,
                    expectedRecordedAtUtc,
                    action);
                if (receipt.Phase == ArchiveCreationRecoveryDispositionReceiptPhase.Completed)
                {
                    return ValueTask.FromResult(
                        CreateDispositionResponse(
                            record.RecoveryId,
                            action,
                            ArchiveCreationRecoveryDispositionResult.AlreadyCompleted,
                            receipt.FinalAssessment
                                ?? throw new InvalidDataException(
                                    "A completed recovery disposition has no final assessment."),
                            receipt.ResolvedAtUtc
                                ?? throw new InvalidDataException(
                                    "A completed recovery disposition has no completion time.")));
                }
            }

            using var prepared = HandleRelativeExtractionRoot.PrepareRecoveryDisposition(
                record,
                action,
                allowAlreadyApplied: receipt is not null,
                cancellationToken);

            var disposition = recovery.Disposition ?? recovery.CreateDisposition();
            if (receiptHasIncompleteTail)
            {
                disposition.SetLength(validReceiptLength);
                disposition.Flush(flushToDisk: true);
            }

            if (receipt is null)
            {
                var admittedAtUtc = DateTimeOffset.UtcNow;
                var intent = new ArchiveCreationRecoveryDispositionReceipt(
                    DispositionSchemaVersion,
                    record.RecoveryId,
                    record.OperationId.Value,
                    action,
                    expectedPhase,
                    expectedSequence,
                    expectedRecordedAtUtc,
                    ArchiveCreationRecoveryDispositionReceiptPhase.Intent,
                    Sequence: 1,
                    FinalAssessment: null,
                    ResolvedAtUtc: null,
                    RecordedAtUtc: admittedAtUtc);
                AppendDispositionFrame(disposition, intent);
                receipt = intent;
                validReceiptLength = disposition.Length;
            }

            checkpoint?.Invoke(ArchiveCreationRecoveryDispositionCheckpoint.IntentDurable);
            var finalAssessment = prepared.Apply(cancellationToken);
            checkpoint?.Invoke(ArchiveCreationRecoveryDispositionCheckpoint.ArtifactMutationApplied);
            var resolvedAtUtc = DateTimeOffset.UtcNow;
            var completed = receipt with
            {
                Phase = ArchiveCreationRecoveryDispositionReceiptPhase.Completed,
                Sequence = 2,
                FinalAssessment = finalAssessment,
                ResolvedAtUtc = resolvedAtUtc,
                RecordedAtUtc = resolvedAtUtc,
            };
            AppendDispositionFrame(disposition, completed);
            completedResponse = CreateDispositionResponse(
                record.RecoveryId,
                action,
                ArchiveCreationRecoveryDispositionResult.Completed,
                finalAssessment,
                resolvedAtUtc);
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
        catch (ArchiveProviderException exception) when (
            exception.Code.StartsWith("recovery.disposition_", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or UnauthorizedAccessException
                or Win32Exception
                or IOException
                or NotSupportedException
                or ArchiveProviderException)
        {
            throw UnverifiedRecord(exception);
        }

        PruneCompletedDispositionReceiptsAfterResolution();
        return ValueTask.FromResult(completedResponse);
    }

    private static ApplyArchiveCreationRecoveryDispositionResponse CreateDispositionResponse(
        string recoveryId,
        ArchiveCreationRecoveryDispositionAction action,
        ArchiveCreationRecoveryDispositionResult result,
        ArchiveCreationRecoveryAssessment finalAssessment,
        DateTimeOffset resolvedAtUtc) =>
        new(
            recoveryId,
            action,
            result,
            finalAssessment,
            resolvedAtUtc);

    private static void EnsureReceiptMatchesRequest(
        ArchiveCreationRecoveryDispositionReceipt receipt,
        ArchiveCreationRecoveryRecord record,
        ArchiveCreationRecoveryPhase expectedPhase,
        int expectedSequence,
        DateTimeOffset expectedRecordedAtUtc,
        ArchiveCreationRecoveryDispositionAction action)
    {
        if (!string.Equals(receipt.RecoveryId, record.RecoveryId, StringComparison.Ordinal)
            || receipt.OperationId != record.OperationId.Value
            || receipt.ExpectedPhase != expectedPhase
            || receipt.ExpectedSequence != expectedSequence
            || receipt.ExpectedRecordedAtUtc != expectedRecordedAtUtc
            || receipt.Action != action)
        {
            throw DispositionRefused(
                "recovery.disposition_conflict",
                "A different recovery disposition is already recorded for this recovery identifier.");
        }
    }

    private static bool TryReadDispositionReceipt(
        FileStream disposition,
        ArchiveCreationRecoveryRecord record,
        out ArchiveCreationRecoveryDispositionReceipt? receipt,
        out bool hasIncompleteTail,
        out long validLength)
    {
        receipt = null;
        hasIncompleteTail = false;
        validLength = 0;
        disposition.Position = 0;
        if (disposition.Length > MaximumDispositionBytes)
        {
            return false;
        }

        if (disposition.Length == 0)
        {
            hasIncompleteTail = true;
            return true;
        }

        ArchiveCreationRecoveryDispositionReceipt? previous = null;
        var frameCount = 0;
        while (disposition.Position < disposition.Length)
        {
            if (frameCount == MaximumDispositionFrames)
            {
                return false;
            }

            var frameStart = disposition.Position;
            var remaining = disposition.Length - frameStart;
            if (remaining < DispositionFrameHeaderBytes)
            {
                if (previous is null
                    && !IsValidIncompleteDispositionFramePrefix(disposition, remaining))
                {
                    return false;
                }

                hasIncompleteTail = true;
                break;
            }

            var header = new byte[DispositionFrameHeaderBytes];
            disposition.ReadExactly(header);
            if (!header.AsSpan(0, DispositionFrameMagic.Length)
                .SequenceEqual(DispositionFrameMagic))
            {
                return false;
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
                header.AsSpan(DispositionFrameMagic.Length, sizeof(int)));
            if (payloadLength <= 0
                || payloadLength > MaximumDispositionFramePayloadBytes)
            {
                return false;
            }

            if (disposition.Length - disposition.Position < payloadLength)
            {
                hasIncompleteTail = true;
                break;
            }

            var payload = new byte[payloadLength];
            disposition.ReadExactly(payload);
            var expectedHash = header.AsSpan(
                DispositionFrameMagic.Length + sizeof(int),
                SHA256.HashSizeInBytes);
            var actualHash = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                return false;
            }

            ArchiveCreationRecoveryDispositionReceipt? current;
            try
            {
                current = JsonSerializer.Deserialize<ArchiveCreationRecoveryDispositionReceipt>(
                    payload,
                    JournalJsonOptions);
            }
            catch (JsonException)
            {
                return false;
            }

            if (current is null
                || !IsValidDispositionReceipt(current, record, previous))
            {
                return false;
            }

            previous = current;
            validLength = disposition.Position;
            frameCount++;
        }

        receipt = previous;
        return receipt is not null || hasIncompleteTail;
    }

    private static bool IsValidIncompleteDispositionFramePrefix(
        FileStream disposition,
        long remaining)
    {
        if (remaining <= 0 || remaining >= DispositionFrameHeaderBytes)
        {
            return remaining == 0;
        }

        var prefix = new byte[checked((int)remaining)];
        disposition.ReadExactly(prefix);
        var magicBytes = Math.Min(prefix.Length, DispositionFrameMagic.Length);
        if (!prefix.AsSpan(0, magicBytes)
            .SequenceEqual(DispositionFrameMagic.AsSpan(0, magicBytes)))
        {
            return false;
        }

        if (prefix.Length < DispositionFrameMagic.Length + sizeof(int))
        {
            return true;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            prefix.AsSpan(DispositionFrameMagic.Length, sizeof(int)));
        return payloadLength is > 0 and <= MaximumDispositionFramePayloadBytes;
    }

    private static bool IsValidDispositionReceipt(
        ArchiveCreationRecoveryDispositionReceipt current,
        ArchiveCreationRecoveryRecord record,
        ArchiveCreationRecoveryDispositionReceipt? previous)
    {
        if (current.SchemaVersion != DispositionSchemaVersion
            || !string.Equals(current.RecoveryId, record.RecoveryId, StringComparison.Ordinal)
            || current.OperationId != record.OperationId.Value
            || current.ExpectedPhase != record.Phase
            || current.ExpectedSequence != record.Sequence
            || current.ExpectedRecordedAtUtc != record.RecordedAtUtc
            || !Enum.IsDefined(current.Action)
            || !Enum.IsDefined(current.Phase)
            || current.RecordedAtUtc == default
            || current.RecordedAtUtc.Offset != TimeSpan.Zero
            || current.Sequence != (int)current.Phase)
        {
            return false;
        }

        if (current.Phase == ArchiveCreationRecoveryDispositionReceiptPhase.Intent)
        {
            return previous is null
                && current.FinalAssessment is null
                && current.ResolvedAtUtc is null;
        }

        return previous is not null
            && previous.Phase == ArchiveCreationRecoveryDispositionReceiptPhase.Intent
            && current.Sequence == previous.Sequence + 1
            && ReceiptImmutableFieldsMatch(current, previous)
            && current.FinalAssessment == ExpectedFinalAssessment(current.Action)
            && current.ResolvedAtUtc is { } resolvedAtUtc
            && resolvedAtUtc != default
            && resolvedAtUtc.Offset == TimeSpan.Zero
            && current.RecordedAtUtc == resolvedAtUtc;
    }

    private static bool ReceiptImmutableFieldsMatch(
        ArchiveCreationRecoveryDispositionReceipt current,
        ArchiveCreationRecoveryDispositionReceipt previous) =>
        current.SchemaVersion == previous.SchemaVersion
        && string.Equals(current.RecoveryId, previous.RecoveryId, StringComparison.Ordinal)
        && current.OperationId == previous.OperationId
        && current.Action == previous.Action
        && current.ExpectedPhase == previous.ExpectedPhase
        && current.ExpectedSequence == previous.ExpectedSequence
        && current.ExpectedRecordedAtUtc == previous.ExpectedRecordedAtUtc;

    private static ArchiveCreationRecoveryAssessment ExpectedFinalAssessment(
        ArchiveCreationRecoveryDispositionAction action) =>
        action is ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging
            or ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal
            ? ArchiveCreationRecoveryAssessment.OwnedFinalObserved
            : ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved;

    private static void AppendDispositionFrame(
        FileStream disposition,
        ArchiveCreationRecoveryDispositionReceipt state)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, JournalJsonOptions);
        if (payload.Length <= 0 || payload.Length > MaximumDispositionFramePayloadBytes)
        {
            throw new IOException(
                "The archive recovery disposition exceeds its bounded frame size.");
        }

        var header = new byte[DispositionFrameHeaderBytes];
        DispositionFrameMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(DispositionFrameMagic.Length, sizeof(int)),
            payload.Length);
        SHA256.HashData(payload).CopyTo(
            header,
            DispositionFrameMagic.Length + sizeof(int));
        disposition.Position = disposition.Length;
        disposition.Write(header);
        disposition.Write(payload);
        disposition.Flush(flushToDisk: true);
    }

    private static ArchiveProviderException DispositionRefused(
        string code,
        string message) =>
        new(
            ArchiveErrorCategory.SourceChanged,
            code,
            message);

    private enum ArchiveCreationRecoveryDispositionReceiptPhase
    {
        Intent = 1,
        Completed = 2,
    }

    private sealed record ArchiveCreationRecoveryDispositionReceipt(
        int SchemaVersion,
        string RecoveryId,
        Guid OperationId,
        ArchiveCreationRecoveryDispositionAction Action,
        ArchiveCreationRecoveryPhase ExpectedPhase,
        int ExpectedSequence,
        DateTimeOffset ExpectedRecordedAtUtc,
        ArchiveCreationRecoveryDispositionReceiptPhase Phase,
        int Sequence,
        ArchiveCreationRecoveryAssessment? FinalAssessment,
        DateTimeOffset? ResolvedAtUtc,
        DateTimeOffset RecordedAtUtc);

    internal enum ArchiveCreationRecoveryDispositionCheckpoint
    {
        IntentDurable,
        ArtifactMutationApplied,
    }
}
