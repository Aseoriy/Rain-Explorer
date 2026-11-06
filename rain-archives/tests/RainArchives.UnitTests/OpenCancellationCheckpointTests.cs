using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class OpenCancellationCheckpointTests
{
    [TestMethod]
    public void MetadataIndexAndPathPlanningHonorCancellation()
    {
        var providerEntries = new[]
        {
            new ProviderArchiveEntry(
                0,
                "folder/file.txt",
                ArchiveEntryKind.File,
                1,
                1,
                null,
                null,
                "Store",
                null,
                false,
                false,
                null),
        };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            ArchiveMetadataGuard.Validate(
                providerEntries,
                cancellationToken: cancelled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            ArchiveIndex.Create(
                providerEntries,
                cancellationToken: cancelled.Token));

        var index = ArchiveIndex.Create(providerEntries);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new ExtractionPathPolicy().Plan(index.Entries, cancelled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            index.WithSafetyAnnotations(
                new Dictionary<EntryId, EntrySafetyAnnotation>(),
                cancelled.Token));
    }
}
