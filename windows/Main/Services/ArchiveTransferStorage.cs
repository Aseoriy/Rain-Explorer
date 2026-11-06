using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RainExplorer.Services;

internal enum ArchiveTransferCleanupOutcome
{
    NotNeeded,
    Removed,
    Preserved,
    PartiallyRemoved,
}

internal sealed record ArchiveTransferAllocation(
    string TransferRootPath,
    string DestinationPath,
    ArchiveTransferObjectIdentity TransferRootIdentity);

internal sealed record ArchiveTransferManifestEntry(
    string RelativePath,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc,
    ArchiveTransferObjectIdentity Identity,
    string? Sha256);

internal sealed record ArchiveTransferExpectedObject(
    string RelativePath,
    bool IsDirectory,
    long Length);

internal sealed record ArchiveTransferSelectionManifest(
    IReadOnlyList<ArchiveTransferExpectedObject> ExpectedObjects,
    IReadOnlyList<string> PublishedRelativePaths);

internal sealed class ArchiveTransferManifest
{
    internal ArchiveTransferManifest(
        ArchiveTransferAllocation allocation,
        ArchiveTransferObjectIdentity destinationIdentity,
        IReadOnlyList<ArchiveTransferManifestEntry> entries,
        IReadOnlyList<string> topLevelPaths,
        IReadOnlyList<string> publishedPaths,
        long totalFileBytes,
        ArchiveTransferPinSet publicationPins)
    {
        Allocation = allocation;
        DestinationIdentity = destinationIdentity;
        Entries = entries;
        TopLevelPaths = topLevelPaths;
        PublishedPaths = publishedPaths;
        TotalFileBytes = totalFileBytes;
        PublicationPins = publicationPins;
    }

    public ArchiveTransferAllocation Allocation { get; }
    public ArchiveTransferObjectIdentity DestinationIdentity { get; }
    public IReadOnlyList<ArchiveTransferManifestEntry> Entries { get; }
    public IReadOnlyList<string> TopLevelPaths { get; }
    public IReadOnlyList<string> PublishedPaths { get; }
    public long TotalFileBytes { get; }
    internal ArchiveTransferPinSet PublicationPins { get; }
}

internal readonly record struct ArchiveTransferObjectIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);

internal sealed class ArchiveTransferPathBudget
{
    private readonly int _maximumCharacters;
    private readonly int _maximumUtf8Bytes;
    private long _characters;
    private long _utf8Bytes;

    public ArchiveTransferPathBudget(
        int maximumCharacters = ArchiveTransferStorage.MaximumTransferPathCharacters,
        int maximumUtf8Bytes = ArchiveTransferStorage.MaximumTransferPathUtf8Bytes)
    {
        if (maximumCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if (maximumUtf8Bytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        _maximumCharacters = maximumCharacters;
        _maximumUtf8Bytes = maximumUtf8Bytes;
    }

    public bool TryAdd(string path)
    {
        if (path is null) return false;
        try
        {
            long characters = checked(_characters + path.Length);
            long utf8Bytes = checked(_utf8Bytes + Encoding.UTF8.GetByteCount(path));
            if (characters > _maximumCharacters || utf8Bytes > _maximumUtf8Bytes)
                return false;
            _characters = characters;
            _utf8Bytes = utf8Bytes;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return false;
        }
    }
}


/// <summary>
/// Owns only Rain Explorer's fixed per-user archive-transfer root. Publication
/// requires two identical bounded tree snapshots while each reviewed object is
/// pinned against write/delete access. Cleanup releases those read pins, pins
/// every exact manifest identity for deletion, repeats the whole review, and
/// deletes only through those handles.
/// Any uncertainty preserves the owned output for inspection.
/// </summary>
internal sealed class ArchiveTransferStorage
{
    internal const int MaximumTransferObjects = ArchiveLocationAdapter.MaximumIndexedEntries;
    internal const long MaximumTransferBytes = 64L * 1024 * 1024 * 1024;
    internal const int MaximumTransferPathCharacters = 16 * 1024 * 1024;
    internal const int MaximumTransferPathUtf8Bytes = 32 * 1024 * 1024;
    internal const int MaximumStartupChildren = 1024;
    private const int MaximumAllocationAttempts = 16;
    private readonly string _transferRootPath;
    private readonly Func<Guid> _newId;
    private readonly Func<CancellationToken, Task>? _beforeReadyRevalidation;
    private readonly Func<CancellationToken, Task>? _beforeCleanup;
    private readonly int _maximumReviewObjects;
    private readonly Action<string>? _reviewHandlePinned;

    public ArchiveTransferStorage()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RainExplorer.ArchiveTransfers.v1"))
    {
    }

    internal ArchiveTransferStorage(
        string transferRootPath,
        Func<Guid>? newId = null,
        Func<CancellationToken, Task>? beforeReadyRevalidation = null,
        Func<CancellationToken, Task>? beforeCleanup = null,
        int maximumReviewObjects = MaximumTransferObjects,
        Action<string>? reviewHandlePinned = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferRootPath);
        if (!Path.IsPathFullyQualified(transferRootPath))
            throw new ArgumentException("The archive-transfer root must be fully qualified.", nameof(transferRootPath));

        _transferRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(transferRootPath));
        _newId = newId ?? Guid.NewGuid;
        _beforeReadyRevalidation = beforeReadyRevalidation;
        _beforeCleanup = beforeCleanup;
        if (maximumReviewObjects <= 0 || maximumReviewObjects > MaximumTransferObjects)
            throw new ArgumentOutOfRangeException(nameof(maximumReviewObjects));
        _maximumReviewObjects = maximumReviewObjects;
        _reviewHandlePinned = reviewHandlePinned;
    }

    internal string TransferRootPath => _transferRootPath;
    internal static int FileIdInformationNativeSize => Marshal.SizeOf<FileIdInformation>();

    internal void ValidateSelectionForAdmission(ArchiveTransferSelectionManifest selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _ = ValidateSelectionManifest(selection);
    }

    internal static bool FitsAggregatePathBudget(
        IEnumerable<string> paths,
        int maximumCharacters = MaximumTransferPathCharacters,
        int maximumUtf8Bytes = MaximumTransferPathUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (maximumCharacters < 0 || maximumUtf8Bytes < 0) return false;
        var budget = new ArchiveTransferPathBudget(maximumCharacters, maximumUtf8Bytes);
        foreach (string path in paths)
        {
            if (!budget.TryAdd(path)) return false;
        }
        return true;
    }

    public ArchiveTransferAllocation Allocate()
    {
        ArchiveTransferObjectIdentity rootIdentity = EnsureTransferRoot();
        for (int attempt = 0; attempt < MaximumAllocationAttempts; attempt++)
        {
            string destination = Path.Combine(_transferRootPath, "transfer-" + _newId().ToString("N"));
            EnsureDirectChild(destination);
            if (ProbePath(destination) == PathProbe.Absent)
            {
                EnsureUnchangedDirectory(_transferRootPath, rootIdentity, requireNoStreams: true);
                return new ArchiveTransferAllocation(_transferRootPath, destination, rootIdentity);
            }
        }

        throw new IOException("Rain Explorer could not allocate a fresh archive-transfer destination.");
    }

    public async Task<ArchiveTransferManifest> ReviewCompletedAsync(
        ArchiveTransferAllocation allocation,
        ArchiveTransferSelectionManifest expectedSelection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(expectedSelection);
        ValidateAllocation(allocation);
        ValidatedSelectionManifest expected = ValidateSelectionManifest(expectedSelection);
        ArchiveTransferTreeSnapshot first = await SnapshotTreeAsync(
                allocation, expected.ExpectedByPath, cancellationToken)
            .ConfigureAwait(false);

        ArchiveTransferPinSet? pins = null;
        try
        {
            pins = PinForPublication(allocation, first);
            ArchiveTransferTreeSnapshot second = await SnapshotTreeAsync(
                    allocation, expected.ExpectedByPath, cancellationToken)
                .ConfigureAwait(false);
            if (!TreeMatches(first, second))
                throw new InvalidDataException("The archive-transfer tree changed during terminal review.");

            if (!OutputMatchesSelection(expected, second))
                throw new InvalidDataException("The completed archive transfer did not match its exact selected-object manifest.");

            EnsureUnchangedDirectory(allocation.TransferRootPath, allocation.TransferRootIdentity, requireNoStreams: true);
            string[] topLevelPaths = second.Entries
                .Where(entry => !entry.RelativePath.Contains('/'))
                .Select(entry => entry.FullPath)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (topLevelPaths.Length == 0)
                throw new InvalidDataException("The completed archive transfer has no top-level objects.");
            var actualByRelativePath = second.Entries.ToDictionary(
                entry => entry.RelativePath,
                StringComparer.Ordinal);
            string[] publishedPaths = expected.PublishedRelativePaths
                .Select(path => actualByRelativePath[path].FullPath)
                .ToArray();

            var manifest = new ArchiveTransferManifest(
                allocation,
                second.DestinationIdentity,
                second.Entries,
                topLevelPaths,
                publishedPaths,
                second.TotalFileBytes,
                pins);
            pins = null;
            return manifest;
        }
        finally
        {
            pins?.Dispose();
        }
    }

    public async Task<ArchiveTransferCleanupOutcome> CleanupAsync(
        ArchiveTransferManifest? manifest,
        CancellationToken cancellationToken)
    {
        if (manifest is null) return ArchiveTransferCleanupOutcome.NotNeeded;

        ArchiveTransferPinSet? deletionPins = null;
        bool mutationStarted = false;
        try
        {
            if (_beforeCleanup is not null)
                await _beforeCleanup(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAllocation(manifest.Allocation);
            IReadOnlyDictionary<string, ArchiveTransferExpectedObject> expected =
                BuildManifestExpectations(manifest);
            ArchiveTransferTreeSnapshot whilePinned = await SnapshotTreeAsync(
                    manifest.Allocation, expected, cancellationToken)
                .ConfigureAwait(false);
            if (!ManifestMatches(manifest, whilePinned))
                return ArchiveTransferCleanupOutcome.Preserved;

            manifest.PublicationPins.Dispose();
            deletionPins = PinForDeletion(manifest);
            ArchiveTransferTreeSnapshot current = await SnapshotTreeAsync(
                    manifest.Allocation, expected, cancellationToken, accommodateDeletionPins: true)
                .ConfigureAwait(false);
            if (!ManifestMatches(manifest, current))
                return ArchiveTransferCleanupOutcome.Preserved;

            foreach (ArchiveTransferManifestEntry entry in current.Entries
                         .Where(entry => !entry.IsDirectory)
                         .OrderByDescending(entry => PathDepth(entry.RelativePath))
                         .ThenByDescending(entry => entry.RelativePath, StringComparer.Ordinal))
            {
                DeletePinned(entry.FullPath, deletionPins, () => mutationStarted = true);
            }

            foreach (ArchiveTransferManifestEntry entry in current.Entries
                         .Where(entry => entry.IsDirectory)
                         .OrderByDescending(entry => PathDepth(entry.RelativePath))
                         .ThenByDescending(entry => entry.RelativePath, StringComparer.Ordinal))
            {
                DeletePinned(entry.FullPath, deletionPins, () => mutationStarted = true);
            }

            DeletePinned(
                manifest.Allocation.DestinationPath,
                deletionPins,
                () => mutationStarted = true);
            EnsureUnchangedDirectory(
                manifest.Allocation.TransferRootPath,
                manifest.Allocation.TransferRootIdentity,
                requireNoStreams: true);
            return ArchiveTransferCleanupOutcome.Removed;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or OperationCanceledException
                                              or Win32Exception)
        {
            return mutationStarted
                ? ArchiveTransferCleanupOutcome.PartiallyRemoved
                : ArchiveTransferCleanupOutcome.Preserved;
        }
        finally
        {
            deletionPins?.Dispose();
            manifest.PublicationPins.Dispose();
        }
    }

    public async Task<bool> RevalidateReadyAsync(
        ArchiveTransferManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_beforeReadyRevalidation is not null)
            await _beforeReadyRevalidation(cancellationToken).ConfigureAwait(false);
        ValidateAllocation(manifest.Allocation);
        IReadOnlyDictionary<string, ArchiveTransferExpectedObject> expected =
            BuildManifestExpectations(manifest);
        ArchiveTransferTreeSnapshot current = await SnapshotTreeAsync(
                manifest.Allocation, expected, cancellationToken)
            .ConfigureAwait(false);
        return ManifestMatches(manifest, current);
    }

    public async Task<ArchiveTransferCleanupOutcome> CleanupConfirmedOutputAsync(
        ArchiveTransferAllocation allocation,
        ArchiveTransferSelectionManifest selection,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAllocation(allocation);
            if (ProbePath(allocation.DestinationPath) == PathProbe.Absent)
            {
                EnsureUnchangedDirectory(
                    allocation.TransferRootPath,
                    allocation.TransferRootIdentity,
                    requireNoStreams: true);
                return ArchiveTransferCleanupOutcome.NotNeeded;
            }

            ArchiveTransferManifest manifest = await ReviewCompletedAsync(
                    allocation, selection, cancellationToken)
                .ConfigureAwait(false);
            return await CleanupAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return ArchiveTransferCleanupOutcome.Preserved;
        }
    }

    public ArchiveTransferCleanupOutcome InspectUncertainOutput(
        ArchiveTransferAllocation allocation)
    {
        try
        {
            ValidateAllocation(allocation);
            if (ProbePath(allocation.DestinationPath) == PathProbe.Present)
                return ArchiveTransferCleanupOutcome.Preserved;
            EnsureUnchangedDirectory(
                allocation.TransferRootPath,
                allocation.TransferRootIdentity,
                requireNoStreams: true);
            return ArchiveTransferCleanupOutcome.NotNeeded;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return ArchiveTransferCleanupOutcome.Preserved;
        }
    }

    public ArchiveTransferRootInspection InspectRoot()
    {
        try
        {
            ArchiveTransferObjectIdentity identity = EnsureTransferRoot();
            int observedChildren = 0;
            bool truncated = false;
            bool unsafeObject = false;
            foreach (string child in Directory.EnumerateFileSystemEntries(_transferRootPath))
            {
                if (observedChildren >= MaximumStartupChildren)
                {
                    truncated = true;
                    break;
                }

                observedChildren++;
                try
                {
                    _ = CaptureObject(child, requireDirectory: null, requireNoStreams: true);
                }
                catch
                {
                    unsafeObject = true;
                }
            }

            EnsureUnchangedDirectory(_transferRootPath, identity, requireNoStreams: true);
            return new ArchiveTransferRootInspection(true, observedChildren, truncated, unsafeObject);
        }
        catch
        {
            return new ArchiveTransferRootInspection(false, 0, false, true);
        }
    }

    private ArchiveTransferObjectIdentity EnsureTransferRoot()
    {
        string? parent = Directory.GetParent(_transferRootPath)?.FullName;
        if (parent is null) throw new IOException("The archive-transfer root has no parent directory.");
        EnsureExistingAncestryIsOrdinary(parent);
        ObjectSnapshot parentBefore = CaptureObject(parent, requireDirectory: true, requireNoStreams: false);

        if (ProbePath(_transferRootPath) == PathProbe.Absent)
            Directory.CreateDirectory(_transferRootPath);

        ObjectSnapshot root = CaptureObject(_transferRootPath, requireDirectory: true, requireNoStreams: true);
        ObjectSnapshot parentAfter = CaptureObject(parent, requireDirectory: true, requireNoStreams: false);
        if (parentBefore.Identity != parentAfter.Identity
            || !parentBefore.IsDirectory
            || !parentAfter.IsDirectory)
            throw new InvalidDataException("The archive-transfer parent changed during allocation.");
        return root.Identity;
    }

    private static void EnsureExistingAncestryIsOrdinary(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) throw new IOException("The archive-transfer root is invalid.");

        string current = root;
        string remainder = fullPath[root.Length..];
        foreach (string component in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (ProbePath(current) == PathProbe.Absent)
                throw new DirectoryNotFoundException("The archive-transfer parent does not exist.");
            _ = CaptureObject(current, requireDirectory: true, requireNoStreams: false);
        }
    }

    private async Task<ArchiveTransferTreeSnapshot> SnapshotTreeAsync(
        ArchiveTransferAllocation allocation,
        IReadOnlyDictionary<string, ArchiveTransferExpectedObject> expected,
        CancellationToken cancellationToken,
        bool accommodateDeletionPins = false)
    {
        EnsureUnchangedDirectory(allocation.TransferRootPath, allocation.TransferRootIdentity, requireNoStreams: true);
        EnsureDirectChild(allocation.DestinationPath);
        ObjectSnapshot destination = CaptureObject(
            allocation.DestinationPath, requireDirectory: true, requireNoStreams: true);

        var entries = new List<ArchiveTransferManifestEntry>();
        var pending = new Stack<string>();
        pending.Push(allocation.DestinationPath);
        var pathBudget = new ArchiveTransferPathBudget();
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            using SafeFileHandle directoryHandle = OpenReviewHandle(
                directory, accommodateDeletionPins);
            ObjectSnapshot before = CaptureObject(
                directoryHandle, requireDirectory: true, requireNoStreams: true);
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= _maximumReviewObjects)
                    throw new InvalidDataException("The archive transfer exceeds the object-review limit.");

                string fullChild = Path.GetFullPath(child);
                EnsureUnderDestination(allocation.DestinationPath, fullChild);
                string relativePath = Path.GetRelativePath(allocation.DestinationPath, fullChild)
                    .Replace(Path.DirectorySeparatorChar, '/');
                ValidateRelativePath(relativePath);
                if (!pathBudget.TryAdd(relativePath))
                    throw new InvalidDataException("The archive transfer exceeds the path-review limit.");
                if (!expected.TryGetValue(relativePath, out ArchiveTransferExpectedObject? expectedObject))
                    throw new InvalidDataException("The archive transfer contains an unexpected object.");

                using SafeFileHandle reviewHandle = OpenReviewHandle(
                    fullChild, accommodateDeletionPins);
                ObjectSnapshot initial = CaptureObject(
                    reviewHandle, requireDirectory: null, requireNoStreams: true);
                _reviewHandlePinned?.Invoke(fullChild);
                if (initial.IsDirectory != expectedObject.IsDirectory
                    || initial.Length != expectedObject.Length)
                    throw new InvalidDataException("An archive-transfer object did not match its declared type or length.");
                string? sha256 = null;
                if (!initial.IsDirectory)
                {
                    totalBytes = checked(totalBytes + expectedObject.Length);
                    if (totalBytes > MaximumTransferBytes)
                        throw new InvalidDataException("The archive transfer exceeds the byte-review limit.");
                    sha256 = await HashFileAsync(
                            reviewHandle, expectedObject.Length, cancellationToken)
                        .ConfigureAwait(false);
                }

                ObjectSnapshot final = CaptureObject(
                    reviewHandle, requireDirectory: initial.IsDirectory, requireNoStreams: true);
                if (!SnapshotMatches(initial, final))
                    throw new InvalidDataException("An archive-transfer object changed during review.");

                entries.Add(new ArchiveTransferManifestEntry(
                    relativePath,
                    fullChild,
                    final.IsDirectory,
                    final.Length,
                    final.LastWriteTimeUtc,
                    final.Identity,
                    sha256));
                if (final.IsDirectory) pending.Push(fullChild);
            }

            ObjectSnapshot after = CaptureObject(
                directoryHandle, requireDirectory: true, requireNoStreams: true);
            if (!SnapshotMatches(before, after))
                throw new InvalidDataException("An archive-transfer directory changed during review.");
        }

        if (entries.Count == 0)
            throw new InvalidDataException("The completed archive transfer is empty.");
        if (entries.Count != expected.Count)
            throw new InvalidDataException("The completed archive transfer omitted one or more exact objects.");
        ObjectSnapshot destinationAfter = CaptureObject(
            allocation.DestinationPath, requireDirectory: true, requireNoStreams: true);
        if (!SnapshotMatches(destination, destinationAfter))
            throw new InvalidDataException("The archive-transfer root changed during review.");

        return new ArchiveTransferTreeSnapshot(
            destination.Identity,
            entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray(),
            totalBytes);
    }

    private ArchiveTransferPinSet PinForPublication(
        ArchiveTransferAllocation allocation,
        ArchiveTransferTreeSnapshot snapshot)
    {
        var pins = new ArchiveTransferPinSet();
        try
        {
            pins.Add(OpenPin(
                allocation.TransferRootPath,
                requireDirectory: true,
                allocation.TransferRootIdentity,
                includeDeleteAccess: false,
                FileShare.Read));
            pins.Add(OpenPin(
                allocation.DestinationPath,
                requireDirectory: true,
                snapshot.DestinationIdentity,
                includeDeleteAccess: false,
                FileShare.Read));
            foreach (ArchiveTransferManifestEntry entry in snapshot.Entries
                         .OrderBy(entry => PathDepth(entry.RelativePath))
                         .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal))
            {
                pins.Add(OpenPin(
                    entry.FullPath,
                    entry.IsDirectory,
                    entry.Identity,
                    includeDeleteAccess: false,
                    FileShare.Read));
            }
            return pins;
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    private ArchiveTransferPinSet PinForDeletion(ArchiveTransferManifest manifest)
    {
        var pins = new ArchiveTransferPinSet();
        try
        {
            pins.Add(OpenPin(
                manifest.Allocation.TransferRootPath,
                requireDirectory: true,
                manifest.Allocation.TransferRootIdentity,
                includeDeleteAccess: false,
                FileShare.Read));
            pins.Add(OpenPin(
                manifest.Allocation.DestinationPath,
                requireDirectory: true,
                manifest.DestinationIdentity,
                includeDeleteAccess: true,
                FileShare.Read));
            foreach (ArchiveTransferManifestEntry entry in manifest.Entries
                         .OrderBy(entry => PathDepth(entry.RelativePath))
                         .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal))
            {
                pins.Add(OpenPin(
                    entry.FullPath,
                    entry.IsDirectory,
                    entry.Identity,
                    includeDeleteAccess: true,
                    FileShare.Read));
            }
            return pins;
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    private static ArchiveTransferPinnedObject OpenPin(
        string path,
        bool requireDirectory,
        ArchiveTransferObjectIdentity? expectedIdentity,
        bool includeDeleteAccess,
        FileShare share)
    {
        uint desiredAccess = FileReadAttributes;
        if (requireDirectory) desiredAccess |= FileListDirectory;
        if (includeDeleteAccess) desiredAccess |= DeleteAccess;
        SafeFileHandle handle = CreateFileW(
            path,
            desiredAccess,
            share,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "An archive-transfer object could not be pinned safely.");
        }

        try
        {
            ObjectSnapshot snapshot = CaptureObject(handle, requireDirectory, requireNoStreams: true);
            if (expectedIdentity is ArchiveTransferObjectIdentity expected
                && snapshot.Identity != expected)
                throw new InvalidDataException("An archive-transfer object changed identity before it was pinned.");
            return new ArchiveTransferPinnedObject(path, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void DeletePinned(
        string path,
        ArchiveTransferPinSet deletionPins,
        Action dispositionApplied)
    {
        using ArchiveTransferPinnedObject deletionPin = deletionPins.Take(path);
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                deletionPin.Handle,
                FileDispositionInfoClass,
                in disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "An exact archive-transfer object could not be removed.");

        dispositionApplied();
        deletionPin.Dispose();
        if (ProbePath(path) != PathProbe.Absent)
            throw new IOException("An exact archive-transfer object remained after cleanup.");
    }

    private static SafeFileHandle OpenReviewHandle(
        string path,
        bool accommodateDeletionPins)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            FileReadData | FileReadAttributes,
            accommodateDeletionPins ? FileShare.Read | FileShare.Delete : FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagOverlapped,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "An archive-transfer object could not be opened for exact review.");
    }

    private static async Task<string> HashFileAsync(
        SafeFileHandle handle,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        byte[] buffer = GC.AllocateUninitializedArray<byte>(bufferSize);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < declaredLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, declaredLength - offset);
            int read = await RandomAccess.ReadAsync(
                    handle, buffer.AsMemory(0, requested), offset, cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
                throw new InvalidDataException("An archive-transfer file ended before its declared length.");
            hash.AppendData(buffer, 0, read);
            offset = checked(offset + read);
        }

        int beyondDeclaredLength = await RandomAccess.ReadAsync(
                handle, buffer.AsMemory(0, 1), offset, cancellationToken)
            .ConfigureAwait(false);
        if (beyondDeclaredLength != 0)
            throw new InvalidDataException("An archive-transfer file grew beyond its declared length.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IReadOnlyDictionary<string, ArchiveTransferExpectedObject> BuildManifestExpectations(
        ArchiveTransferManifest manifest) => manifest.Entries.ToDictionary(
        entry => entry.RelativePath,
        entry => new ArchiveTransferExpectedObject(entry.RelativePath, entry.IsDirectory, entry.Length),
        StringComparer.Ordinal);

    private static ValidatedSelectionManifest ValidateSelectionManifest(
        ArchiveTransferSelectionManifest selection)
    {
        if (selection.ExpectedObjects.Count == 0
            || selection.ExpectedObjects.Count > MaximumTransferObjects
            || selection.PublishedRelativePaths.Count == 0
            || selection.PublishedRelativePaths.Count > MaximumTransferObjects)
            throw new InvalidDataException("The selected archive transfer has no bounded exact manifest.");
        if (!FitsAggregatePathBudget(
                selection.ExpectedObjects.Select(item => item.RelativePath)
                    .Concat(selection.PublishedRelativePaths)))
            throw new InvalidDataException("The selected archive transfer exceeds the aggregate path budget.");

        var exact = new Dictionary<string, ArchiveTransferExpectedObject>(StringComparer.Ordinal);
        var windowsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        foreach (ArchiveTransferExpectedObject expected in selection.ExpectedObjects)
        {
            ValidateRelativePath(expected.RelativePath);
            ValidateWindowsRelativePath(expected.RelativePath);
            if (expected.Length < 0
                || expected.IsDirectory && expected.Length != 0
                || !exact.TryAdd(expected.RelativePath, expected)
                || !windowsPaths.Add(expected.RelativePath))
                throw new InvalidDataException("The selected archive transfer has an ambiguous exact manifest.");
            if (!expected.IsDirectory)
            {
                declaredBytes = checked(declaredBytes + expected.Length);
                if (declaredBytes > MaximumTransferBytes)
                    throw new InvalidDataException("The selected archive transfer exceeds the byte-review limit.");
            }
        }

        foreach (ArchiveTransferExpectedObject expected in exact.Values)
        {
            int separator = expected.RelativePath.LastIndexOf('/');
            if (separator < 0) continue;
            string parent = expected.RelativePath[..separator];
            if (!exact.TryGetValue(parent, out ArchiveTransferExpectedObject? parentObject)
                || !parentObject.IsDirectory)
                throw new InvalidDataException("The selected archive transfer has an incomplete parent manifest.");
        }

        var published = new HashSet<string>(StringComparer.Ordinal);
        var publishedWindowsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in selection.PublishedRelativePaths)
        {
            ValidateRelativePath(relativePath);
            if (!exact.ContainsKey(relativePath)
                || !published.Add(relativePath)
                || !publishedWindowsPaths.Add(relativePath))
                throw new InvalidDataException("The selected publication path is not an exact manifest object.");
        }

        return new ValidatedSelectionManifest(
            exact.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray(),
            exact,
            selection.PublishedRelativePaths.ToArray());
    }

    private static bool OutputMatchesSelection(
        ValidatedSelectionManifest expected,
        ArchiveTransferTreeSnapshot actual)
    {
        if (expected.ExpectedObjects.Count != actual.Entries.Count) return false;
        for (int index = 0; index < expected.ExpectedObjects.Count; index++)
        {
            ArchiveTransferExpectedObject expectedObject = expected.ExpectedObjects[index];
            ArchiveTransferManifestEntry actualObject = actual.Entries[index];
            if (!string.Equals(expectedObject.RelativePath, actualObject.RelativePath, StringComparison.Ordinal)
                || expectedObject.IsDirectory != actualObject.IsDirectory
                || expectedObject.Length != actualObject.Length)
                return false;
        }
        return true;
    }

    private static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath is "." or ".."
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Contains('\\')
            || relativePath.Split('/').Any(component => component.Length == 0 || component is "." or ".."))
            throw new InvalidDataException("An archive-transfer path escaped its owned root.");
    }

    private static void ValidateWindowsRelativePath(string relativePath)
    {
        foreach (string component in relativePath.Split('/'))
        {
            if (component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || component.EndsWith(' ')
                || component.EndsWith('.'))
                throw new InvalidDataException("The selected archive transfer has a non-materializable path.");
        }
    }

    private void ValidateAllocation(ArchiveTransferAllocation allocation)
    {
        if (!string.Equals(allocation.TransferRootPath, _transferRootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive-transfer allocation belongs to another root.");
        EnsureDirectChild(allocation.DestinationPath);
        EnsureUnchangedDirectory(allocation.TransferRootPath, allocation.TransferRootIdentity, requireNoStreams: true);
    }

    private void EnsureDirectChild(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? parent = Directory.GetParent(fullPath)?.FullName;
        string name = Path.GetFileName(fullPath);
        string suffix = name.StartsWith("transfer-", StringComparison.Ordinal)
            ? name["transfer-".Length..]
            : string.Empty;
        if (!string.Equals(parent, _transferRootPath, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(suffix, "N", out _))
            throw new InvalidDataException("The archive-transfer destination is not a direct owned child.");
    }

    private static void EnsureUnderDestination(string destinationPath, string childPath)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath))
            + Path.DirectorySeparatorChar;
        if (!childPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An archive-transfer object escaped its owned destination.");
    }

    private static bool TreeMatches(
        ArchiveTransferTreeSnapshot expected,
        ArchiveTransferTreeSnapshot actual) =>
        expected.DestinationIdentity == actual.DestinationIdentity
        && expected.TotalFileBytes == actual.TotalFileBytes
        && expected.Entries.SequenceEqual(actual.Entries);

    private static bool ManifestMatches(
        ArchiveTransferManifest expected,
        ArchiveTransferTreeSnapshot actual) =>
        expected.DestinationIdentity == actual.DestinationIdentity
        && expected.TotalFileBytes == actual.TotalFileBytes
        && expected.Entries.SequenceEqual(actual.Entries);

    private static bool SnapshotMatches(ObjectSnapshot left, ObjectSnapshot right) =>
        left.IsDirectory == right.IsDirectory
        && left.Length == right.Length
        && left.LastWriteTimeUtc == right.LastWriteTimeUtc
        && left.Identity == right.Identity
        && left.LinkCount == right.LinkCount
        && left.NamedStreamCount == right.NamedStreamCount;

    private static void EnsureUnchangedDirectory(
        string path,
        ArchiveTransferObjectIdentity identity,
        bool requireNoStreams)
    {
        ObjectSnapshot current = CaptureObject(path, requireDirectory: true, requireNoStreams);
        if (current.Identity != identity)
            throw new InvalidDataException("The archive-transfer root changed identity.");
    }

    private static ObjectSnapshot CaptureObject(
        string path,
        bool? requireDirectory,
        bool requireNoStreams)
    {
        using SafeFileHandle handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "An archive-transfer object could not be opened safely.");
        return CaptureObject(handle, requireDirectory, requireNoStreams);
    }

    private static ObjectSnapshot CaptureObject(
        SafeFileHandle handle,
        bool? requireDirectory,
        bool requireNoStreams)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "An archive-transfer object could not be identified.");
        if (!GetFileIdInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out FileIdInformation fileIdInformation,
                (uint)Marshal.SizeOf<FileIdInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "An archive-transfer object identity could not be read.");

        FileAttributes attributes = (FileAttributes)information.FileAttributes;
        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (requireDirectory is bool expectedDirectory && isDirectory != expectedDirectory)
            throw new InvalidDataException("An archive-transfer object changed type.");
        const FileAttributes disallowed = FileAttributes.ReparsePoint
            | FileAttributes.Device
            | FileAttributes.Encrypted
            | FileAttributes.Offline
            | FileAttributes.SparseFile
            | FileAttributes.Temporary
            | FileAttributes.IntegrityStream
            | FileAttributes.NoScrubData;
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        FileAttributes effectiveDisallowed = disallowed | recallOnOpen | recallOnDataAccess;
        if (requireNoStreams) effectiveDisallowed |= FileAttributes.ReadOnly;
        if ((attributes & effectiveDisallowed) != 0)
            throw new InvalidDataException("An archive-transfer object is not an ordinary file or directory.");
        if (information.NumberOfLinks != 1)
            throw new InvalidDataException("An archive-transfer object has an unsafe link count.");

        int namedStreams = requireNoStreams ? CountNamedStreams(handle) : 0;
        if (namedStreams != 0)
            throw new InvalidDataException("An archive-transfer object contains a named data stream.");
        long length = isDirectory
            ? 0
            : checked((long)(((ulong)information.FileSizeHigh << 32) | information.FileSizeLow));
        long lastWrite = ((long)information.LastWriteTime.High << 32) | information.LastWriteTime.Low;
        return new ObjectSnapshot(
            isDirectory,
            length,
            DateTime.FromFileTimeUtc(lastWrite),
            new ArchiveTransferObjectIdentity(
                fileIdInformation.VolumeSerialNumber,
                fileIdInformation.FileId.Low,
                fileIdInformation.FileId.High),
            information.NumberOfLinks,
            namedStreams);
    }

    private static int CountNamedStreams(SafeFileHandle handle)
    {
        const int maximumStreamInformationBytes = 64 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(maximumStreamInformationBytes);
        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileStreamInfoClass,
                    buffer,
                    maximumStreamInformationBytes))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is ErrorHandleEof or ErrorFileNotFound) return 0;
                if (error is ErrorInsufficientBuffer or ErrorMoreData)
                    throw new InvalidDataException("Archive-transfer stream metadata exceeds the review bound.");
                throw new Win32Exception(error, "Archive-transfer named streams could not be reviewed.");
            }

            int named = 0;
            int offset = 0;
            while (true)
            {
                if (offset < 0 || offset > maximumStreamInformationBytes - FileStreamInformationHeaderBytes)
                    throw new InvalidDataException("Archive-transfer stream metadata is malformed.");
                IntPtr entry = IntPtr.Add(buffer, offset);
                int nextOffset = Marshal.ReadInt32(entry, 0);
                int nameLength = Marshal.ReadInt32(entry, 4);
                if (nameLength < 0 || (nameLength & 1) != 0
                    || nameLength > maximumStreamInformationBytes - offset - FileStreamInformationHeaderBytes)
                    throw new InvalidDataException("Archive-transfer stream metadata is malformed.");
                string name = Marshal.PtrToStringUni(
                    IntPtr.Add(entry, FileStreamInformationHeaderBytes),
                    nameLength / 2) ?? string.Empty;
                if (!string.Equals(name, "::$DATA", StringComparison.Ordinal)) named++;
                if (nextOffset == 0) return named;
                if (nextOffset < FileStreamInformationHeaderBytes
                    || nextOffset > maximumStreamInformationBytes - offset)
                    throw new InvalidDataException("Archive-transfer stream metadata is malformed.");
                offset += nextOffset;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PathProbe ProbePath(string path)
    {
        if (GetFileAttributesW(path) != uint.MaxValue) return PathProbe.Present;
        int error = Marshal.GetLastWin32Error();
        if (error is ErrorFileNotFound or ErrorPathNotFound) return PathProbe.Absent;
        throw new Win32Exception(error, "An archive-transfer path could not be inspected safely.");
    }

    private static int PathDepth(string relativePath) => relativePath.Count(character => character == '/');

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or AccessViolationException;

    private sealed record ArchiveTransferTreeSnapshot(
        ArchiveTransferObjectIdentity DestinationIdentity,
        IReadOnlyList<ArchiveTransferManifestEntry> Entries,
        long TotalFileBytes);

    private sealed record ValidatedSelectionManifest(
        IReadOnlyList<ArchiveTransferExpectedObject> ExpectedObjects,
        IReadOnlyDictionary<string, ArchiveTransferExpectedObject> ExpectedByPath,
        IReadOnlyList<string> PublishedRelativePaths);

    private sealed record ObjectSnapshot(
        bool IsDirectory,
        long Length,
        DateTime LastWriteTimeUtc,
        ArchiveTransferObjectIdentity Identity,
        uint LinkCount,
        int NamedStreamCount);

    private enum PathProbe
    {
        Absent,
        Present,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    private const uint FileListDirectory = 0x0001;
    private const uint FileReadData = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int FileDispositionInfoClass = 4;
    private const int FileStreamInfoClass = 7;
    private const int FileIdInfoClass = 18;
    private const int FileStreamInformationHeaderBytes = 24;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorHandleEof = 38;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        in FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string fileName);

}

internal sealed class ArchiveTransferPinSet : IDisposable
{
    private readonly Dictionary<string, ArchiveTransferPinnedObject> _pins =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void Add(ArchiveTransferPinnedObject pin)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ArchiveTransferPinSet));
        if (!_pins.TryAdd(pin.Path, pin))
        {
            pin.Dispose();
            throw new InvalidDataException("An archive-transfer pin was duplicated.");
        }
    }

    public ArchiveTransferPinnedObject Take(string path)
    {
        if (_disposed || !_pins.Remove(path, out ArchiveTransferPinnedObject? pin))
            throw new InvalidDataException("An exact archive-transfer pin is unavailable.");
        return pin;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (ArchiveTransferPinnedObject pin in _pins.Values) pin.Dispose();
        _pins.Clear();
    }
}

internal sealed class ArchiveTransferPinnedObject : IDisposable
{
    private SafeFileHandle? _handle;

    public ArchiveTransferPinnedObject(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
    }

    public string Path { get; }
    public SafeFileHandle Handle => _handle
        ?? throw new ObjectDisposedException(nameof(ArchiveTransferPinnedObject));

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

internal sealed record ArchiveTransferRootInspection(
    bool RootIsSafe,
    int ObservedChildren,
    bool IsTruncated,
    bool ContainsUnsafeObject);
