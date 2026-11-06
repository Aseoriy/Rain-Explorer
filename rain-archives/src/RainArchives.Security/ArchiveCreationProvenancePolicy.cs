using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

internal sealed record PlannedArchiveCreationSourceProvenance(
    string SourcePath,
    OpenFileSnapshot SourceSnapshot,
    DownloadProvenanceState State);

internal sealed record ArchiveCreationProvenancePlan(
    IReadOnlyList<PlannedArchiveCreationSourceProvenance> Sources,
    DownloadProvenance? ProvenanceToApply,
    ArchiveWarning? Warning);

internal sealed class ArchiveCreationProvenancePolicy
{
    internal async ValueTask<ArchiveCreationProvenancePlan> CaptureAsync(
        IReadOnlyList<PlannedArchiveCreationFile> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var sources = new List<PlannedArchiveCreationSourceProvenance>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = HandleRelativeExtractionRoot.OpenStableSourceFile(
                file.SourcePath);
            source.EnsureMatches(file.SourceSnapshot);
            var state = await source
                .ReadProvenanceStateAsync(cancellationToken)
                .ConfigureAwait(false);
            source.EnsureMatches(file.SourceSnapshot);
            sources.Add(new PlannedArchiveCreationSourceProvenance(
                file.SourcePath,
                file.SourceSnapshot,
                state));
        }

        return CreatePlan(sources);
    }

    internal async ValueTask ValidateAsync(
        ArchiveCreationProvenancePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var expected in plan.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = HandleRelativeExtractionRoot.OpenStableSourceFile(
                expected.SourcePath);
            source.EnsureMatches(expected.SourceSnapshot);
            var actual = await source
                .ReadProvenanceStateAsync(cancellationToken)
                .ConfigureAwait(false);
            source.EnsureMatches(expected.SourceSnapshot);
            if (actual != expected.State)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.SourceChanged,
                    "source.provenance_changed",
                    "A source file's Windows download-security provenance changed during archive creation.");
            }
        }
    }

    private static ArchiveCreationProvenancePlan CreatePlan(
        IReadOnlyList<PlannedArchiveCreationSourceProvenance> sources)
    {
        if (sources.Count == 0)
        {
            return new ArchiveCreationProvenancePlan(sources, null, null);
        }

        var first = sources[0].State;
        if (first is
            {
                Kind: DownloadProvenanceStateKind.Present,
                Provenance: { } firstProvenance,
            }
            && sources.All(source =>
                source.State is
                {
                    Kind: DownloadProvenanceStateKind.Present,
                    Provenance: { } provenance,
                }
                && provenance.ZoneId == firstProvenance.ZoneId))
        {
            return new ArchiveCreationProvenancePlan(
                sources,
                new DownloadProvenance(firstProvenance.ZoneId),
                null);
        }

        if (sources.All(source =>
                source.State.Kind is DownloadProvenanceStateKind.Absent
                    or DownloadProvenanceStateKind.Unsupported))
        {
            return new ArchiveCreationProvenancePlan(sources, null, null);
        }

        return new ArchiveCreationProvenancePlan(
            sources,
            null,
            new ArchiveWarning(
                "provenance.source_ambiguous",
                "Windows download-security provenance was not copied because the selected sources did not have one unambiguous shared security zone."));
    }
}
