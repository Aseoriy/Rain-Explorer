using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using RainArchives.Core;

namespace RainArchives.Security;

/// <summary>
/// Owns one immutable, handle-relative temporary archive source. The source lease
/// denies write/delete sharing after materialization, and cleanup deletes only the
/// exact file and directory identities created by this instance.
/// </summary>
public sealed class OwnedTemporaryArchiveSource : IAsyncDisposable
{
    internal const string SourceDirectoryPrefix = "rainarchives-nested-";
    internal const string SourceFileName = "source.archive";
    private const int TokenCharacters = 64;
    private readonly HandleRelativeExtractionRoot _root;
    private HandleRelativeExtractionRoot.HandleRelativeOutputFile? _output;
    private ArchiveSourceLease? _sourceLease;
    private bool _completed;
    private bool _disposed;

    private OwnedTemporaryArchiveSource(
        string directoryPath,
        HandleRelativeExtractionRoot root,
        HandleRelativeExtractionRoot.HandleRelativeOutputFile output)
    {
        DirectoryPath = directoryPath;
        SourcePath = Path.Combine(directoryPath, SourceFileName);
        _root = root;
        _output = output;
    }

    public string DirectoryPath { get; }

    public string SourcePath { get; }

    public Stream Output => _output?.Stream
        ?? throw new InvalidOperationException("The temporary archive output is no longer writable.");

    public static OwnedTemporaryArchiveSource Create(string parentDirectory) =>
        Create(parentDirectory, parentIsPinned: false);

    internal static OwnedTemporaryArchiveSource CreateInPinnedParent(string parentDirectory) =>
        Create(parentDirectory, parentIsPinned: true);

    private static OwnedTemporaryArchiveSource Create(
        string parentDirectory,
        bool parentIsPinned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var canonicalParent = Path.GetFullPath(parentDirectory);
        if (!Directory.Exists(canonicalParent))
        {
            throw new DirectoryNotFoundException("The temporary archive parent directory does not exist.");
        }

        var directoryName = SourceDirectoryPrefix
            + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var directoryPath = Path.Combine(canonicalParent, directoryName);
        var root = parentIsPinned
            ? HandleRelativeExtractionRoot.CreateInPinnedParent(canonicalParent, directoryName)
            : HandleRelativeExtractionRoot.Create(canonicalParent, directoryName);
        try
        {
            var output = root.CreateFile(SourceFileName);
            return new OwnedTemporaryArchiveSource(directoryPath, root, output);
        }
        catch
        {
            try
            {
                root.DeleteEmptyTree();
            }
            catch
            {
                // Preserve the original creation failure if the exact empty-tree cleanup cannot be proven.
            }
            finally
            {
                root.Dispose();
            }

            throw;
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The temporary archive source was already completed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var output = _output
            ?? throw new InvalidOperationException("The temporary archive output is unavailable.");
        await output.CloseDataAsync().ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);
        _output = null;
        cancellationToken.ThrowIfCancellationRequested();
        _ = _root.ValidateKnownTree();
        _sourceLease = _root.OpenSingleFileSourceLease(SourceFileName, SourcePath);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Exception? failure = null;
        try
        {
            if (_output is not null)
            {
                await _output.DisposeAsync().ConfigureAwait(false);
                _output = null;
            }

            if (_sourceLease is not null)
            {
                await _sourceLease.DisposeAsync().ConfigureAwait(false);
                _sourceLease = null;
            }

            _root.DeleteSingleFileTree(SourceFileName);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _root.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    internal static bool IsValidSourceDirectoryName(string? directoryName)
    {
        if (directoryName is null
            || directoryName.Length != SourceDirectoryPrefix.Length + TokenCharacters
            || !directoryName.StartsWith(SourceDirectoryPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in directoryName.AsSpan(SourceDirectoryPrefix.Length))
        {
            if (character is not (>= '0' and <= '9')
                && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
