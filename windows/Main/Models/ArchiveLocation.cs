using System.IO;
using System.Text;
using System.Text.Json;
using RainArchives.Contracts;

namespace RainExplorer.Models;

/// <summary>A persistent, provider-neutral archive location. Runtime session and entry IDs never enter this value.</summary>
public sealed record ArchiveLocation(
    string SourcePath,
    ArchiveFingerprint Fingerprint,
    string FolderPath,
    int? FolderDuplicateOrdinal)
{
    public IReadOnlyList<ArchiveNestedSegment> NestedSegments { get; init; } =
        Array.Empty<ArchiveNestedSegment>();
}

/// <summary>A persistent parent/entry/child link. It contains no runtime session or entry identity.</summary>
public sealed record ArchiveNestedSegment(
    ArchiveFingerprint ParentFingerprint,
    string SourceEntryPath,
    int SourceEntryDuplicateOrdinal,
    ArchiveFingerprint ChildFingerprint);

public static class ArchiveLocationSerializer
{
    public const int MaximumEnvelopeBytes = 32 * 1024;
    public const string Prefix = "rain-archive:";

    public static bool IsArchiveTarget(string? value) =>
        value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static bool TrySerialize(ArchiveLocation location, out string target)
    {
        ArgumentNullException.ThrowIfNull(location);
        target = string.Empty;
        if (!TryValidate(location, out var canonical)) return false;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 2);
            writer.WriteString("source", canonical.SourcePath);
            WriteFingerprint(writer, canonical.Fingerprint);
            writer.WriteString("folder", canonical.FolderPath);
            if (canonical.FolderDuplicateOrdinal is int duplicate)
                writer.WriteNumber("duplicate", duplicate);
            writer.WriteStartArray("segments");
            foreach (ArchiveNestedSegment segment in canonical.NestedSegments)
            {
                writer.WriteStartObject();
                WriteFingerprint(writer, segment.ParentFingerprint, "parent");
                writer.WriteString("sourceEntry", segment.SourceEntryPath);
                writer.WriteNumber("sourceDuplicate", segment.SourceEntryDuplicateOrdinal);
                WriteFingerprint(writer, segment.ChildFingerprint, "child");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        string candidate = Prefix + Encoding.UTF8.GetString(stream.ToArray());
        if (Encoding.UTF8.GetByteCount(candidate) > MaximumEnvelopeBytes) return false;
        target = candidate;
        return true;
    }

    public static bool TryDeserialize(string? target, out ArchiveLocation location)
    {
        location = null!;
        if (!IsArchiveTarget(target)
            || target!.Length > MaximumEnvelopeBytes
            || Encoding.UTF8.GetByteCount(target!) > MaximumEnvelopeBytes)
            return false;

        byte[] json = Encoding.UTF8.GetBytes(target![Prefix.Length..]);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            int? version = null;
            string? source = null;
            long? length = null;
            string? writeUtc = null;
            string? fileId = null;
            bool fileIdSeen = false;
            string? prefixSha256 = null;
            string? folder = null;
            int? duplicate = null;
            JsonElement? segmentsElement = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return false;
                switch (property.Name)
                {
                    case "v" when property.Value.ValueKind == JsonValueKind.Number
                                      && property.Value.TryGetInt32(out int v):
                        version = v;
                        break;
                    case "source" when property.Value.ValueKind == JsonValueKind.String:
                        source = property.Value.GetString();
                        break;
                    case "length" when property.Value.ValueKind == JsonValueKind.Number
                                           && property.Value.TryGetInt64(out long l):
                        length = l;
                        break;
                    case "writeUtc" when property.Value.ValueKind == JsonValueKind.String:
                        writeUtc = property.Value.GetString();
                        break;
                    case "fileId" when property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                        fileIdSeen = true;
                        fileId = property.Value.ValueKind == JsonValueKind.Null
                            ? null
                            : property.Value.GetString();
                        break;
                    case "prefixSha256" when property.Value.ValueKind == JsonValueKind.String:
                        prefixSha256 = property.Value.GetString();
                        break;
                    case "folder" when property.Value.ValueKind == JsonValueKind.String:
                        folder = property.Value.GetString();
                        break;
                    case "duplicate" when property.Value.ValueKind == JsonValueKind.Number
                                              && property.Value.TryGetInt32(out int d):
                        duplicate = d;
                        break;
                    case "segments" when property.Value.ValueKind == JsonValueKind.Array:
                        segmentsElement = property.Value;
                        break;
                    default:
                        return false;
                }
            }

            if (version is not (1 or 2) || source is null || length is null || writeUtc is null
                || !fileIdSeen || prefixSha256 is null || folder is null
                || version == 1 && segmentsElement is not null
                || version == 2 && segmentsElement is null
                || !DateTimeOffset.TryParseExact(writeUtc, "O", null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var written))
                return false;

            IReadOnlyList<ArchiveNestedSegment> segments = Array.Empty<ArchiveNestedSegment>();
            if (segmentsElement is JsonElement element)
            {
                if (!TryReadSegments(element, out segments)) return false;
            }

            var parsed = new ArchiveLocation(
                source,
                new ArchiveFingerprint(length.Value, written, fileId, prefixSha256),
                folder,
                duplicate)
            {
                NestedSegments = segments,
            };
            if (!TryValidate(parsed, out var canonical)
                || !string.Equals(source, canonical.SourcePath, StringComparison.Ordinal))
                return false;

            location = canonical;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryValidate(ArchiveLocation location, out ArchiveLocation canonical)
    {
        canonical = null!;
        if (string.IsNullOrWhiteSpace(location.SourcePath)
            || !IsValidFingerprint(location.Fingerprint, requirePositiveLength: false)
            || location.FolderDuplicateOrdinal is < 0)
            return false;

        string source;
        try
        {
            source = Path.GetFullPath(location.SourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        string folder = location.FolderPath;
        if (folder.Length > 0)
        {
            if (!IsValidNormalizedArchivePath(folder)) return false;
        }
        else if (location.FolderDuplicateOrdinal is not null)
        {
            return false;
        }

        if (location.NestedSegments is null
            || location.NestedSegments.Count > ProtocolConstants.MaximumNestedArchiveDepth)
            return false;

        ArchiveNestedSegment[] segments = location.NestedSegments.Count == 0
            ? Array.Empty<ArchiveNestedSegment>()
            : new ArchiveNestedSegment[location.NestedSegments.Count];
        ArchiveFingerprint expectedParent = location.Fingerprint;
        for (int index = 0; index < location.NestedSegments.Count; index++)
        {
            ArchiveNestedSegment? segment = location.NestedSegments[index];
            if (segment is null
                || !FingerprintsEqual(segment.ParentFingerprint, expectedParent)
                || !IsValidFingerprint(segment.ParentFingerprint, requirePositiveLength: index > 0)
                || !IsValidFingerprint(segment.ChildFingerprint, requirePositiveLength: true)
                || !IsValidNormalizedArchivePath(segment.SourceEntryPath)
                || segment.SourceEntryDuplicateOrdinal < 0)
                return false;
            segments[index] = segment;
            expectedParent = segment.ChildFingerprint;
        }

        canonical = location with { SourcePath = source, NestedSegments = segments };
        return true;
    }

    private static void WriteFingerprint(
        Utf8JsonWriter writer,
        ArchiveFingerprint fingerprint,
        string prefix = "")
    {
        string lengthName = prefix.Length == 0 ? "length" : prefix + "Length";
        string writeName = prefix.Length == 0 ? "writeUtc" : prefix + "WriteUtc";
        string fileIdName = prefix.Length == 0 ? "fileId" : prefix + "FileId";
        string hashName = prefix.Length == 0 ? "prefixSha256" : prefix + "PrefixSha256";
        writer.WriteNumber(lengthName, fingerprint.Length);
        writer.WriteString(writeName, fingerprint.LastWriteTimeUtc.ToString("O"));
        if (fingerprint.FileIdentity is null) writer.WriteNull(fileIdName);
        else writer.WriteString(fileIdName, fingerprint.FileIdentity);
        writer.WriteString(hashName, fingerprint.PrefixSha256);
    }

    private static bool TryReadSegments(
        JsonElement element,
        out IReadOnlyList<ArchiveNestedSegment> segments)
    {
        segments = Array.Empty<ArchiveNestedSegment>();
        int count = element.GetArrayLength();
        if (count > ProtocolConstants.MaximumNestedArchiveDepth) return false;

        var parsed = new ArchiveNestedSegment[count];
        int index = 0;
        foreach (JsonElement segmentElement in element.EnumerateArray())
        {
            if (segmentElement.ValueKind != JsonValueKind.Object
                || !TryReadSegment(segmentElement, out ArchiveNestedSegment segment))
                return false;
            parsed[index++] = segment;
        }
        segments = parsed;
        return true;
    }

    private static bool TryReadSegment(JsonElement element, out ArchiveNestedSegment segment)
    {
        segment = null!;
        long? parentLength = null;
        string? parentWriteUtc = null;
        string? parentFileId = null;
        bool parentFileIdSeen = false;
        string? parentPrefixSha256 = null;
        string? sourceEntry = null;
        int? sourceDuplicate = null;
        long? childLength = null;
        string? childWriteUtc = null;
        string? childFileId = null;
        bool childFileIdSeen = false;
        string? childPrefixSha256 = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return false;
            switch (property.Name)
            {
                case "parentLength" when property.Value.ValueKind == JsonValueKind.Number
                                         && property.Value.TryGetInt64(out long parsedParentLength):
                    parentLength = parsedParentLength;
                    break;
                case "parentWriteUtc" when property.Value.ValueKind == JsonValueKind.String:
                    parentWriteUtc = property.Value.GetString();
                    break;
                case "parentFileId" when property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    parentFileIdSeen = true;
                    parentFileId = property.Value.ValueKind == JsonValueKind.Null
                        ? null : property.Value.GetString();
                    break;
                case "parentPrefixSha256" when property.Value.ValueKind == JsonValueKind.String:
                    parentPrefixSha256 = property.Value.GetString();
                    break;
                case "sourceEntry" when property.Value.ValueKind == JsonValueKind.String:
                    sourceEntry = property.Value.GetString();
                    break;
                case "sourceDuplicate" when property.Value.ValueKind == JsonValueKind.Number
                                            && property.Value.TryGetInt32(out int parsedDuplicate):
                    sourceDuplicate = parsedDuplicate;
                    break;
                case "childLength" when property.Value.ValueKind == JsonValueKind.Number
                                        && property.Value.TryGetInt64(out long parsedChildLength):
                    childLength = parsedChildLength;
                    break;
                case "childWriteUtc" when property.Value.ValueKind == JsonValueKind.String:
                    childWriteUtc = property.Value.GetString();
                    break;
                case "childFileId" when property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    childFileIdSeen = true;
                    childFileId = property.Value.ValueKind == JsonValueKind.Null
                        ? null : property.Value.GetString();
                    break;
                case "childPrefixSha256" when property.Value.ValueKind == JsonValueKind.String:
                    childPrefixSha256 = property.Value.GetString();
                    break;
                default:
                    return false;
            }
        }

        if (parentLength is null || parentWriteUtc is null || !parentFileIdSeen
            || parentPrefixSha256 is null || sourceEntry is null || sourceDuplicate is null
            || childLength is null || childWriteUtc is null || !childFileIdSeen
            || childPrefixSha256 is null
            || !DateTimeOffset.TryParseExact(parentWriteUtc, "O", null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parentWritten)
            || !DateTimeOffset.TryParseExact(childWriteUtc, "O", null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset childWritten))
            return false;

        segment = new ArchiveNestedSegment(
            new ArchiveFingerprint(parentLength.Value, parentWritten, parentFileId, parentPrefixSha256),
            sourceEntry,
            sourceDuplicate.Value,
            new ArchiveFingerprint(childLength.Value, childWritten, childFileId, childPrefixSha256));
        return true;
    }

    private static bool IsValidFingerprint(ArchiveFingerprint? fingerprint, bool requirePositiveLength) =>
        fingerprint is not null
        && (requirePositiveLength ? fingerprint.Length > 0 : fingerprint.Length >= 0)
        && fingerprint.LastWriteTimeUtc.Offset == TimeSpan.Zero
        && (fingerprint.FileIdentity is null || fingerprint.FileIdentity.Length <= 1024)
        && IsSha256(fingerprint.PrefixSha256);

    private static bool FingerprintsEqual(ArchiveFingerprint left, ArchiveFingerprint right) =>
        left.Length == right.Length
        && left.LastWriteTimeUtc == right.LastWriteTimeUtc
        && string.Equals(left.FileIdentity, right.FileIdentity, StringComparison.Ordinal)
        && string.Equals(left.PrefixSha256, right.PrefixSha256, StringComparison.Ordinal);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static bool IsValidNormalizedArchivePath(string path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Length > ArchivePathRules.MaximumNormalizedPathLength
            || path[0] == '/' || path[^1] == '/' || path.Contains('\\'))
            return false;
        if (!ArchivePathRules.TryNormalize(path, out string normalized, out _, out _)) return false;
        return string.Equals(normalized.Replace(Path.DirectorySeparatorChar, '/'), path,
            StringComparison.Ordinal);
    }
}
