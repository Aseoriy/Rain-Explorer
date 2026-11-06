namespace RainArchives.Core;

/// <summary>
/// Keeps an opened archive stable for the lifetime of a worker session by denying
/// new write and delete sharing on the source file. Fingerprint checks remain
/// necessary for filesystems that do not fully honor Windows sharing semantics.
/// </summary>
public sealed class ArchiveSourceLease : IAsyncDisposable
{
    private readonly FileStream _stream;

    private ArchiveSourceLease(string sourcePath, FileStream stream)
    {
        SourcePath = sourcePath;
        _stream = stream;
    }

    public string SourcePath { get; }

    public static ArchiveSourceLease Acquire(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var canonicalPath = Path.GetFullPath(sourcePath);
        var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.RandomAccess);
        return new ArchiveSourceLease(canonicalPath, stream);
    }

    public static ArchiveSourceLease AdoptOwnedReadStream(string sourcePath, FileStream stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || stream.SafeFileHandle.IsInvalid || stream.SafeFileHandle.IsClosed)
        {
            throw new ArgumentException(
                "The adopted archive source must be an open readable file stream.",
                nameof(stream));
        }

        var canonicalPath = Path.GetFullPath(sourcePath);
        _ = FileSystemIdentityService.CaptureOpenFile(stream, canonicalPath);
        return new ArchiveSourceLease(canonicalPath, stream);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
