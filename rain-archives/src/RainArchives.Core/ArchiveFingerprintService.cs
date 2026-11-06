using System.Security.Cryptography;
using RainArchives.Contracts;

namespace RainArchives.Core;

public static class ArchiveFingerprintService
{
    private const int PrefixLength = 64 * 1024;

    public static async ValueTask<ArchiveFingerprint> CreateAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Archive source was not found.", sourcePath);
        }

        await using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            PrefixLength,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var before = FileSystemIdentityService.CaptureOpenFile(stream, sourcePath);
        var prefix = new byte[Math.Min(PrefixLength, checked((int)Math.Min(before.Length, PrefixLength)))];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var after = FileSystemIdentityService.CaptureOpenFile(stream, sourcePath);
        if (before != after)
        {
            throw SourceChanged();
        }

        var hash = Convert.ToHexString(SHA256.HashData(prefix));

        return new ArchiveFingerprint(
            before.Length,
            before.LastWriteTimeUtc,
            before.Identity,
            hash);
    }

    public static async ValueTask EnsureUnchangedAsync(
        string sourcePath,
        ArchiveFingerprint expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArchiveFingerprint current;
        try
        {
            current = await CreateAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            throw SourceChanged(exception);
        }

        if (!Matches(expected, current))
        {
            throw SourceChanged();
        }
    }

    public static bool Matches(ArchiveFingerprint expected, ArchiveFingerprint current)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);
        return expected.Length == current.Length
            && expected.LastWriteTimeUtc == current.LastWriteTimeUtc
            && string.Equals(expected.FileIdentity, current.FileIdentity, StringComparison.Ordinal)
            && string.Equals(expected.PrefixSha256, current.PrefixSha256, StringComparison.Ordinal);
    }

    public static async ValueTask<byte[]> ReadDetectionPrefixAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            ArchiveSignatures.PrefixBytesRequired,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var before = FileSystemIdentityService.CaptureOpenFile(stream, sourcePath);
        var prefix = new byte[Math.Min(
            ArchiveSignatures.PrefixBytesRequired,
            checked((int)Math.Min(before.Length, ArchiveSignatures.PrefixBytesRequired)))];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var after = FileSystemIdentityService.CaptureOpenFile(stream, sourcePath);
        if (before != after)
        {
            throw SourceChanged();
        }

        return prefix;
    }

    private static ArchiveProviderException SourceChanged(Exception? innerException = null) =>
        new(
            ArchiveErrorCategory.SourceChanged,
            "archive.source_changed",
            "The archive changed after the session was opened. Reopen it before continuing.",
            innerException);
}
