using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using RainArchives.Client;
using RainArchives.Contracts;

if (args.Length != 3)
{
    Console.Error.WriteLine(
        "Usage: RainArchives.ProtocolCompatibilityHarness <worker.exe> <source.zip> <empty-output-root>");
    return 2;
}

var workerPath = Path.GetFullPath(args[0]);
var sourceArchive = Path.GetFullPath(args[1]);
var outputRoot = Path.GetFullPath(args[2]);
if (!File.Exists(workerPath) || !File.Exists(sourceArchive))
{
    Console.Error.WriteLine("The worker and source archive must both exist.");
    return 2;
}

if (!Directory.Exists(outputRoot)
    || (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0
    || Directory.EnumerateFileSystemEntries(outputRoot).Any())
{
    Console.Error.WriteLine("The output root must be an existing, empty, non-reparse directory.");
    return 2;
}

var originalHash = await HashFileAsync(sourceArchive);
var mappedSource = Path.Combine(outputRoot, "compat-source.txt");
var createdArchive = Path.Combine(outputRoot, "compat-created.zip");
var createdTarArchive = Path.Combine(outputRoot, "compat-created.tar");
var rebuiltArchive = Path.Combine(outputRoot, "compat-rebuilt.zip");
var metadataArchive = Path.Combine(outputRoot, "compat-metadata.zip");
await File.WriteAllTextAsync(
    mappedSource,
    $"Rain Archives protocol 1.{ProtocolConstants.MinorVersion} compatibility payload.\n");

await using var client = await ArchiveClient.StartAsync(workerPath);
foreach (var feature in ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion))
{
    if (!client.SupportsFeature(feature))
    {
        throw new InvalidOperationException(
            $"The retained 1.{ProtocolConstants.MinorVersion} client did not negotiate {feature}.");
    }
}

GetZipWriteCapabilitiesResponse? globalWriteCapabilities = null;
if (client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities))
{
    globalWriteCapabilities = await client.GetZipWriteCapabilitiesAsync();
    RequireExpectedWriteCapabilities(globalWriteCapabilities, canRebuild: false);
}

GetTarWriteCapabilitiesResponse? tarWriteCapabilities = null;
if (client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities))
{
    tarWriteCapabilities = await client.GetTarWriteCapabilitiesAsync();
    RequireExpectedTarWriteCapabilities(tarWriteCapabilities);
}

var opened = await client.OpenArchiveAsync(sourceArchive);
if (!string.Equals(opened.Archive.Format, "zip", StringComparison.OrdinalIgnoreCase)
    || opened.Archive.VolumeSetKind != ArchiveVolumeSetKind.Single
    || !opened.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite))
{
    throw new InvalidOperationException("The compatibility source did not open as an eligible ZIP.");
}

if (globalWriteCapabilities is not null)
{
    RequireExpectedWriteCapabilities(
        await client.GetZipWriteCapabilitiesAsync(opened.Archive.SessionId),
        canRebuild: true);
}

var listed = await client.ListEntriesAsync(
    opened.Archive.SessionId,
    limit: ProtocolConstants.MaximumPageSize);
if (listed.TotalCount <= 0 || listed.Entries.Count != listed.TotalCount)
{
    throw new InvalidOperationException("The compatibility source did not produce one complete page.");
}

var integrity = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
RequireCompleted(await client.WaitForTerminalAsync(integrity.OperationId), "integrity");

var creation = await client.StartZipCreationAsync(
    createdArchive,
    [new ArchiveCreationFile(mappedSource, "compat-source.txt")],
    ArchiveCompressionPreset.Balanced);
RequireCompleted(await client.WaitForTerminalAsync(creation.OperationId), "creation");

if (client.SupportsFeature(ProtocolFeatures.TarCreation))
{
    var tarCreation = await client.StartTarCreationAsync(
        createdTarArchive,
        [new ArchiveCreationFile(mappedSource, "compat-source.txt")]);
    RequireCompleted(await client.WaitForTerminalAsync(tarCreation.OperationId), "TAR creation");

    await using var tarStream = new FileStream(
        createdTarArchive,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var tarReader = new TarReader(tarStream, leaveOpen: true);
    var tarEntry = await tarReader.GetNextEntryAsync();
    if (tarEntry is null
        || tarEntry.EntryType is not TarEntryType.RegularFile
        || !string.Equals(tarEntry.Name, "compat-source.txt", StringComparison.Ordinal)
        || await tarReader.GetNextEntryAsync() is not null)
    {
        throw new InvalidOperationException("The TAR compatibility manifest is unexpected.");
    }
}

var rebuild = await client.StartZipRebuildCopyAsync(
    opened.Archive.SessionId,
    rebuiltArchive,
    [],
    [new ArchiveRebuildCopyFile(mappedSource, "compat-added.txt")],
    ArchiveCompressionPreset.Balanced);
RequireCompleted(await client.WaitForTerminalAsync(rebuild.OperationId), "rebuild");

var metadataSource = listed.Entries.First(entry =>
    entry.Kind == ArchiveEntryKind.File
    && entry.NormalizedPath is { Length: > 0 });
var metadataTimestamp = new ArchiveDosLocalTimestamp(2026, 8, 25, 12, 34, 56);
var metadata = await client.StartZipRebuildMetadataAsync(
    opened.Archive.SessionId,
    metadataArchive,
    [],
    [],
    [],
    [],
    [],
    [new ArchiveRebuildMetadataDirective(
        new ArchiveRebuildMetadataTarget(
            ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
            new ArchiveEntryKey(
                metadataSource.NormalizedPath!,
                metadataSource.DuplicateOrdinal)),
        metadataTimestamp,
        0x0021)],
    ArchiveCompressionPreset.Balanced);
RequireCompleted(await client.WaitForTerminalAsync(metadata.OperationId), "metadata rebuild");

using (var archive = ZipFile.OpenRead(metadataArchive))
{
    var entry = archive.GetEntry(metadataSource.NormalizedPath!)
        ?? throw new InvalidOperationException("The metadata compatibility ZIP lost its target entry.");
    var expectedTimestamp = new DateTime(
        metadataTimestamp.Year,
        metadataTimestamp.Month,
        metadataTimestamp.Day,
        metadataTimestamp.Hour,
        metadataTimestamp.Minute,
        metadataTimestamp.Second,
        DateTimeKind.Unspecified);
    if (entry.LastWriteTime.DateTime != expectedTimestamp
        || entry.ExternalAttributes != 0x0021)
    {
        throw new InvalidOperationException("The metadata compatibility ZIP failed exact DOS readback.");
    }
}

var originalHashAfter = await HashFileAsync(sourceArchive);
if (!CryptographicOperations.FixedTimeEquals(originalHash, originalHashAfter))
{
    throw new InvalidOperationException("The original archive changed during compatibility probing.");
}

var created = await client.OpenArchiveAsync(createdArchive);
var createdEntries = await client.ListEntriesAsync(created.Archive.SessionId);
if (createdEntries.Entries.Count(entry => entry.Kind == ArchiveEntryKind.File) != 1)
{
    throw new InvalidOperationException("The created compatibility ZIP manifest is unexpected.");
}

var rebuilt = await client.OpenArchiveAsync(rebuiltArchive);
var rebuiltEntries = await client.ListEntriesAsync(
    rebuilt.Archive.SessionId,
    limit: ProtocolConstants.MaximumPageSize);
if (!rebuiltEntries.Entries.Any(entry =>
        entry.Kind == ArchiveEntryKind.File
        && string.Equals(entry.NormalizedPath, "compat-added.txt", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("The rebuilt compatibility ZIP is missing compat-added.txt.");
}

var metadataRebuilt = await client.OpenArchiveAsync(metadataArchive);
var metadataEntries = await client.ListEntriesAsync(
    metadataRebuilt.Archive.SessionId,
    limit: ProtocolConstants.MaximumPageSize);
var metadataListed = metadataEntries.Entries.Single(entry =>
    entry.Kind == ArchiveEntryKind.File
    && string.Equals(
        entry.NormalizedPath,
        metadataSource.NormalizedPath,
        StringComparison.Ordinal));
if ((metadataListed.WindowsAttributes & 0x0037) != 0x0021)
{
    throw new InvalidOperationException("Rain did not reopen the edited compatibility attributes.");
}

var tarEntries = 0;
if (File.Exists(createdTarArchive))
{
    var createdTar = await client.OpenArchiveAsync(createdTarArchive);
    var listedTar = await client.ListEntriesAsync(
        createdTar.Archive.SessionId,
        limit: ProtocolConstants.MaximumPageSize);
    tarEntries = listedTar.TotalCount;
    if (listedTar.Entries.Count(entry =>
            entry.Kind == ArchiveEntryKind.File
            && string.Equals(entry.NormalizedPath, "compat-source.txt", StringComparison.Ordinal)) != 1)
    {
        throw new InvalidOperationException("Rain did not reopen the created compatibility TAR.");
    }

    await client.CloseArchiveAsync(createdTar.Archive.SessionId);
}

await client.CloseArchiveAsync(metadataRebuilt.Archive.SessionId);
await client.CloseArchiveAsync(rebuilt.Archive.SessionId);
await client.CloseArchiveAsync(created.Archive.SessionId);
await client.CloseArchiveAsync(opened.Archive.SessionId);
Console.WriteLine(
    $"PASS protocol=1.{ProtocolConstants.MinorVersion} sourceEntries={listed.TotalCount} rebuiltEntries={rebuiltEntries.TotalCount} metadataEntries={metadataEntries.TotalCount} tarEntries={tarEntries} capabilities={(globalWriteCapabilities is null ? "legacy" : "managed-zip-v1")} tarCapabilities={(tarWriteCapabilities is null ? "unavailable" : "managed-tar-v1")} originalSha256={Convert.ToHexString(originalHash)}");
return 0;

static void RequireExpectedWriteCapabilities(
    GetZipWriteCapabilitiesResponse response,
    bool canRebuild)
{
    if (!response.CanCreate
        || response.CanRebuild != canRebuild
        || !response.SupportsAdd
        || !response.SupportsReplace
        || !response.SupportsDelete
        || !response.SupportsFileRename
        || !response.SupportsTreeEdit
        || !response.SupportsMetadata
        || !Enum.GetValues<ArchiveCompressionPreset>().SequenceEqual(response.CompressionPresets)
        || response.MaximumEntries != ProtocolConstants.MaximumZipCreationEntries
        || response.MaximumSourceBytes != ProtocolConstants.MaximumZipCreationSourceBytes
        || response.SupportsEncryption
        || response.SupportsFilenameEncryption
        || response.SupportsSplitOutput
        || response.SupportsExistingDestinationReplacement
        || response.SupportsInPlaceUpdate)
    {
        throw new InvalidOperationException("The worker returned unexpected ZIP write capabilities.");
    }
}

static void RequireExpectedTarWriteCapabilities(GetTarWriteCapabilitiesResponse response)
{
    if (!response.CanCreate
        || response.CanRebuild
        || !response.SupportsTreeCreation
        || !new[] { ArchiveCompressionPreset.Store }.SequenceEqual(response.CompressionPresets)
        || response.MaximumEntries != ProtocolConstants.MaximumTarCreationEntries
        || response.MaximumSourceBytes != ProtocolConstants.MaximumTarCreationSourceBytes
        || response.SupportsEncryption
        || response.SupportsFilenameEncryption
        || response.SupportsSplitOutput
        || response.SupportsExistingDestinationReplacement
        || response.SupportsInPlaceUpdate)
    {
        throw new InvalidOperationException("The worker returned unexpected TAR write capabilities.");
    }
}

static void RequireCompleted(OperationEvent terminal, string operation)
{
    if (terminal.Phase is not OperationPhase.Completed and not OperationPhase.CompletedWithWarnings)
    {
        throw new InvalidOperationException(
            $"The {operation} compatibility operation ended in {terminal.Phase}: {terminal.Error?.Code}");
    }
}

static async Task<byte[]> HashFileAsync(string path)
{
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    return await SHA256.HashDataAsync(stream);
}
