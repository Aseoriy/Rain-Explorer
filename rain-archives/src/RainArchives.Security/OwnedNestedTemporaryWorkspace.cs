using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

namespace RainArchives.Security;

public sealed record NestedTemporaryWorkspaceInventory(
    int ActiveWorkspaceCount,
    int PotentialStaleWorkspaceCount,
    int SuspiciousWorkspaceCount,
    bool IsTruncated,
    bool InspectionIncomplete)
{
    public bool RequiresAttention =>
        PotentialStaleWorkspaceCount > 0
        || SuspiciousWorkspaceCount > 0
        || IsTruncated
        || InspectionIncomplete;
}

/// <summary>
/// Owns one worker-specific parent for nested archive sources. The retained owner
/// file is an exclusive liveness claim; after a hard process exit, later workers
/// can inventory the durable token-bound claim without deleting it.
/// </summary>
public sealed class OwnedNestedTemporaryWorkspace : IAsyncDisposable
{
    internal const string WorkspacePrefix = "rainarchives-worker-";
    internal const string OwnerFileName = "owner.lock";
    private const string OwnerRecordPrefix = "RainArchives nested workspace v1\n";
    private const int TokenCharacters = 64;
    private const int DefaultMaximumInventoryEntries = 1024;
    private readonly HandleRelativeExtractionRoot _root;
    private HandleRelativeExtractionRoot.HandleRelativeOutputFile? _ownerFile;
    private bool _disposed;

    private OwnedNestedTemporaryWorkspace(
        string directoryPath,
        HandleRelativeExtractionRoot root,
        HandleRelativeExtractionRoot.HandleRelativeOutputFile ownerFile)
    {
        DirectoryPath = directoryPath;
        _root = root;
        _ownerFile = ownerFile;
    }

    public string DirectoryPath { get; }

    public static OwnedNestedTemporaryWorkspace Create(string parentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var canonicalParent = Path.GetFullPath(parentDirectory);
        if (!Directory.Exists(canonicalParent))
        {
            throw new DirectoryNotFoundException("The nested temporary workspace parent does not exist.");
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var workspaceName = WorkspacePrefix + token;
        var directoryPath = Path.Combine(canonicalParent, workspaceName);
        var root = HandleRelativeExtractionRoot.Create(canonicalParent, workspaceName);
        HandleRelativeExtractionRoot.HandleRelativeOutputFile? ownerFile = null;
        try
        {
            ownerFile = root.CreateFile(OwnerFileName);
            var record = CreateOwnerRecord(workspaceName);
            try
            {
                ownerFile.Stream.Write(record);
                ((FileStream)ownerFile.Stream).Flush(flushToDisk: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(record);
            }

            return new OwnedNestedTemporaryWorkspace(directoryPath, root, ownerFile);
        }
        catch
        {
            try
            {
                if (ownerFile is not null)
                {
                    ownerFile.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    root.DeleteSingleFileTree(OwnerFileName);
                }
                else
                {
                    root.DeleteEmptyTree();
                }
            }
            catch
            {
                // Preserve the creation failure when exact cleanup cannot be proven.
            }
            finally
            {
                root.Dispose();
            }

            throw;
        }
    }

    public static NestedTemporaryWorkspaceInventory Inspect(string parentDirectory) =>
        Inspect(parentDirectory, DefaultMaximumInventoryEntries);

    internal static NestedTemporaryWorkspaceInventory Inspect(
        string parentDirectory,
        int maximumEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        var canonicalParent = Path.GetFullPath(parentDirectory);
        if (!Directory.Exists(canonicalParent))
        {
            return new NestedTemporaryWorkspaceInventory(0, 0, 0, false, true);
        }

        var active = 0;
        var stale = 0;
        var suspicious = 0;
        var truncated = false;
        var incomplete = false;
        try
        {
            var examined = 0;
            foreach (var directoryPath in Directory.EnumerateDirectories(
                         canonicalParent,
                         WorkspacePrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (examined == maximumEntries)
                {
                    truncated = true;
                    break;
                }

                examined++;
                var workspaceName = Path.GetFileName(directoryPath);
                if (!TryGetToken(workspaceName, out _))
                {
                    suspicious++;
                    continue;
                }

                try
                {
                    if (File.GetAttributes(directoryPath).HasFlag(FileAttributes.ReparsePoint))
                    {
                        suspicious++;
                        continue;
                    }

                    var ownerPath = Path.Combine(directoryPath, OwnerFileName);
                    if (File.GetAttributes(ownerPath).HasFlag(FileAttributes.ReparsePoint))
                    {
                        suspicious++;
                        continue;
                    }

                    using var owner = new FileStream(
                        ownerPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None,
                        bufferSize: 256,
                        FileOptions.SequentialScan);
                    if (OwnerRecordMatches(owner, workspaceName))
                    {
                        stale++;
                    }
                    else
                    {
                        suspicious++;
                    }
                }
                catch (IOException exception) when (IsSharingViolation(exception))
                {
                    active++;
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException)
                {
                    suspicious++;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            incomplete = true;
        }

        return new NestedTemporaryWorkspaceInventory(
            active,
            stale,
            suspicious,
            truncated,
            incomplete);
    }

    public OwnedTemporaryArchiveSource CreateSource()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return OwnedTemporaryArchiveSource.CreateInPinnedParent(DirectoryPath);
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
            if (_ownerFile is not null)
            {
                await _ownerFile.DisposeAsync().ConfigureAwait(false);
                _ownerFile = null;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _ = _root.ValidateKnownTree();
            _root.DeleteSingleFileTree(OwnerFileName);
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException("Multiple nested workspace cleanup steps failed.", failure, exception);
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

    internal static byte[] CreateOwnerRecord(string workspaceName)
    {
        if (!TryGetToken(workspaceName, out var token))
        {
            throw new ArgumentException("The nested workspace name is invalid.", nameof(workspaceName));
        }

        return Encoding.ASCII.GetBytes(OwnerRecordPrefix + token + "\n");
    }

    internal static bool OwnerRecordMatches(FileStream owner, string workspaceName)
    {
        var expected = CreateOwnerRecord(workspaceName);
        byte[]? actual = null;
        try
        {
            if (owner.Length != expected.Length)
            {
                return false;
            }

            actual = new byte[expected.Length];
            owner.ReadExactly(actual);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            if (actual is not null)
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    internal static bool TryGetToken(string? workspaceName, out string token)
    {
        token = string.Empty;
        if (workspaceName is null
            || workspaceName.Length != WorkspacePrefix.Length + TokenCharacters
            || !workspaceName.StartsWith(WorkspacePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = workspaceName.AsSpan(WorkspacePrefix.Length);
        foreach (var character in candidate)
        {
            if (character is not (>= '0' and <= '9')
                && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        token = candidate.ToString();
        return true;
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var win32Code = exception.HResult & 0xFFFF;
        return win32Code is 32 or 33;
    }
}
