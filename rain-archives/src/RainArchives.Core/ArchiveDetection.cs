namespace RainArchives.Core;

public static class ArchiveSignatures
{
    public const int PrefixBytesRequired = 512;

    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmptyHeader = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] ZipSpannedHeader = [0x50, 0x4B, 0x07, 0x08];
    private static readonly byte[] SevenZipHeader = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] RarHeader = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];
    private static readonly byte[] GZipHeader = [0x1F, 0x8B];
    private static readonly byte[] BZip2Header = [0x42, 0x5A, 0x68];
    private static readonly byte[] XzHeader = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    public static string? Detect(ReadOnlySpan<byte> prefix)
    {
        if (prefix.StartsWith(ZipLocalHeader)
            || prefix.StartsWith(ZipEmptyHeader)
            || prefix.StartsWith(ZipSpannedHeader))
        {
            return "zip";
        }

        if (prefix.StartsWith(SevenZipHeader))
        {
            return "7z";
        }

        if (prefix.StartsWith(RarHeader))
        {
            return "rar";
        }

        if (prefix.StartsWith(GZipHeader))
        {
            return "gzip";
        }

        if (prefix.StartsWith(BZip2Header))
        {
            return "bzip2";
        }

        if (prefix.StartsWith(XzHeader))
        {
            return "xz";
        }

        if (prefix.Length >= 262
            && prefix.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            return "tar";
        }

        return null;
    }
}

public sealed class ArchiveProviderRegistry
{
    private readonly IReadOnlyList<IArchiveProvider> _providers;

    public ArchiveProviderRegistry(IEnumerable<IArchiveProvider> providers)
    {
        _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        if (_providers.Count == 0)
        {
            throw new ArgumentException("At least one archive provider is required.", nameof(providers));
        }
    }

    public IReadOnlyList<IArchiveProvider> Providers => _providers;

    public IArchiveProvider Select(ReadOnlySpan<byte> prefix, string sourcePath)
    {
        IArchiveProvider? selected = null;
        var selectedConfidence = 0;
        foreach (var provider in _providers)
        {
            var probe = provider.Probe(prefix, sourcePath);
            if (probe.Confidence > selectedConfidence)
            {
                selected = provider;
                selectedConfidence = probe.Confidence;
            }
        }

        return selected ?? throw new ArchiveProviderException(
            Contracts.ArchiveErrorCategory.UnsupportedFormat,
            "format.not_detected",
            "The file content did not match a supported archive format.");
    }
}
