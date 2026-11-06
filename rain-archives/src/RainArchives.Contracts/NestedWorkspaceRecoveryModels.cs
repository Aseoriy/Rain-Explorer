using System.Text.Json.Serialization;

namespace RainArchives.Contracts;

public sealed record NestedWorkspaceRecoverySummary(
    [property: JsonRequired] string RecoveryId);

public sealed record ListNestedWorkspaceRecoveriesRequest();

public sealed record ListNestedWorkspaceRecoveriesResponse(
    [property: JsonRequired] IReadOnlyList<NestedWorkspaceRecoverySummary> Recoveries,
    [property: JsonRequired] int ActiveWorkspaceCount,
    [property: JsonRequired] int SuspiciousWorkspaceCount,
    [property: JsonRequired] bool IsTruncated,
    [property: JsonRequired] bool InspectionIncomplete);

public sealed record InspectNestedWorkspaceRecoveryRequest(
    [property: JsonRequired] string RecoveryId);

public enum NestedWorkspaceRecoveryAssessment
{
    VerifiedDisposable,
    Active,
    IdentityUnavailable,
    UnexpectedContents,
    InspectionIncomplete,
}

public sealed record InspectNestedWorkspaceRecoveryResponse(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] NestedWorkspaceRecoveryAssessment Assessment,
    int? SourceCount,
    long? TotalSourceBytes,
    string? Revision,
    [property: JsonRequired] DateTimeOffset InspectedAtUtc);

public sealed record DiscardNestedWorkspaceRecoveryRequest(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] int ExpectedSourceCount,
    [property: JsonRequired] long ExpectedTotalSourceBytes);

public enum NestedWorkspaceRecoveryDiscardResult
{
    Discarded,
    AlreadyAbsent,
}

public sealed record DiscardNestedWorkspaceRecoveryResponse(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] NestedWorkspaceRecoveryDiscardResult Result,
    [property: JsonRequired] DateTimeOffset ResolvedAtUtc);

public sealed record InspectNestedWorkspaceReopenRequest(
    [property: JsonRequired] string RecoveryId);

public sealed record NestedWorkspaceRecoverySourceEvidence(
    [property: JsonRequired] string SourceId,
    [property: JsonRequired] long Length,
    [property: JsonRequired] string ContentSha256);

public sealed record InspectNestedWorkspaceReopenResponse(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] NestedWorkspaceRecoveryAssessment Assessment,
    int? SourceCount,
    long? TotalSourceBytes,
    string? Revision,
    IReadOnlyList<NestedWorkspaceRecoverySourceEvidence>? Sources,
    [property: JsonRequired] DateTimeOffset InspectedAtUtc,
    [property: JsonRequired] bool SupportsIndependentReopen);

public enum NestedWorkspaceRecoveryReopenMode
{
    IndependentRoot,
}

public sealed record ReopenNestedWorkspaceRecoveryRequest(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] int ExpectedSourceCount,
    [property: JsonRequired] long ExpectedTotalSourceBytes,
    [property: JsonRequired] string SourceId,
    [property: JsonRequired] long ExpectedSourceLength,
    [property: JsonRequired] string ExpectedContentSha256,
    [property: JsonRequired] NestedWorkspaceRecoveryReopenMode Mode,
    Guid? PasswordSlotId = null);

public sealed record ReopenNestedWorkspaceRecoveryResponse(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] string SourceId,
    [property: JsonRequired] NestedWorkspaceRecoveryReopenMode Mode,
    [property: JsonRequired] ArchiveDescriptor Archive,
    [property: JsonRequired] long MaterializedBytes,
    [property: JsonRequired] int Depth,
    [property: JsonRequired] DateTimeOffset ReopenedAtUtc);
