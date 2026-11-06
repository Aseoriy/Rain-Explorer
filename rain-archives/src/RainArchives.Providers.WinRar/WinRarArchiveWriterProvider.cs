using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RainArchives.Contracts;
using RainArchives.Core;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace RainArchives.Providers.WinRar;

internal sealed class WinRarEncoderContainmentException : Exception
{
    internal WinRarEncoderContainmentException(Exception? innerException = null)
        : base(
            "The separately installed RAR encoder could not be proven stopped.",
            innerException)
    {
    }
}

/// <summary>
/// Optional RAR5 writer backed by the user's separately installed WinRAR CLI.
/// No WinRAR binary is copied, redistributed, or searched for through PATH.
/// </summary>
public sealed class WinRarArchiveWriterProvider :
    IArchiveWriterProvider,
    IArchiveStagedOutputValidator
{
    private static readonly IReadOnlySet<ArchiveCompressionPreset> SupportedPresets =
        Enum.GetValues<ArchiveCompressionPreset>().ToHashSet();
    private readonly string _executablePath;
    private readonly string? _workspaceRoot;

    private WinRarArchiveWriterProvider(string executablePath, string? workspaceRoot = null)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _workspaceRoot = workspaceRoot is null ? null : Path.GetFullPath(workspaceRoot);
    }

    public string Id => "winrar/rar-cli/7.x";
    public string Format => "rar";
    public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => SupportedPresets;

    internal RarEncoderAvailability GetCurrentAvailability() =>
        WinRarEncoderProbe.ProbeVerifiedExecutable(_executablePath);

    internal static WinRarWorkspaceMaintenanceResult PerformStartupMaintenance(
        string? rootDirectory = null,
        int maximumEntries = 1_024,
        int maximumObjects = 16_384,
        TimeSpan? maximumDuration = null) =>
        WinRarEncoderWorkspace.PerformStartupMaintenance(
            rootDirectory,
            maximumEntries,
            maximumObjects,
            maximumDuration);

    public static bool TryCreateInstalled(
        out WinRarArchiveWriterProvider? provider,
        out RarEncoderAvailability availability)
    {
        availability = WinRarEncoderProbe.ProbeInstalled();
        provider = availability == RarEncoderAvailability.Available
            ? new WinRarArchiveWriterProvider(WinRarEncoderProbe.InstalledExecutablePath)
            : null;
        return provider is not null;
    }

    internal static bool TryCreateVerifiedExecutable(
        string executablePath,
        out WinRarArchiveWriterProvider? provider,
        out RarEncoderAvailability availability)
    {
        availability = WinRarEncoderProbe.ProbeVerifiedExecutable(executablePath);
        provider = availability == RarEncoderAvailability.Available
            ? new WinRarArchiveWriterProvider(executablePath)
            : null;
        return provider is not null;
    }

    internal static bool TryCreateVerifiedExecutable(
        string executablePath,
        string workspaceRoot,
        out WinRarArchiveWriterProvider? provider,
        out RarEncoderAvailability availability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        availability = WinRarEncoderProbe.ProbeVerifiedExecutable(executablePath);
        provider = availability == RarEncoderAvailability.Available
            ? new WinRarArchiveWriterProvider(executablePath, workspaceRoot)
            : null;
        return provider is not null;
    }

    internal static void TerminateEncoderProcessForTest(
        Process process,
        Action<Process> kill,
        Func<Process, int, bool> waitForExit) =>
        WriterHandle.TerminateEncoderProcess(process, kill, waitForExit);

    public ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The RAR destination stream must be writable.", nameof(destination));
        }

        if (!SupportedPresets.Contains(compressionPreset))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "rar.compression_preset_unsupported",
                "The selected RAR compression preset is not supported by the qualified encoder.");
        }

        FileStream? executableLease = null;
        try
        {
            executableLease = new FileStream(
                _executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            if (WinRarEncoderProbe.ProbeVerifiedExecutable(_executablePath)
                != RarEncoderAvailability.Available)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "rar.encoder_unavailable",
                    "The separately installed RAR encoder is no longer available or no longer passes its identity gate.");
            }

            var writer = new WriterHandle(
                _executablePath,
                executableLease,
                destination,
                compressionPreset,
                _workspaceRoot);
            executableLease = null;
            return ValueTask.FromResult<IArchiveWriterHandle>(writer);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "rar.encoder_unavailable",
                "The separately installed RAR encoder is no longer available or no longer passes its identity gate.",
                exception);
        }
        finally
        {
            executableLease?.Dispose();
        }
    }

    public async ValueTask ValidateAsync(
        Stream archive,
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(expected);
        if (!archive.CanRead || !archive.CanSeek)
        {
            throw new ArgumentException(
                "RAR staged-output validation requires a readable, seekable stream.",
                nameof(archive));
        }

        var originalPosition = archive.Position;
        try
        {
            var expectedByKey = ValidateExpectedManifest(expected);
            archive.Position = 0;
            using var opened = ArchiveFactory.OpenArchive(
                archive,
                new ReaderOptions { LeaveStreamOpen = true });
            if (opened.Type != ArchiveType.Rar)
            {
                throw ManifestMismatch();
            }

            var actualKeys = new HashSet<string>(StringComparer.Ordinal);
            var actualCount = 0;
            foreach (var entry in opened.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                actualCount++;
                if (actualCount > expected.Count)
                {
                    throw ManifestMismatch();
                }

                if (string.IsNullOrEmpty(entry.Key))
                {
                    throw ManifestMismatch();
                }

                var actualPath = NormalizeArchivePath(entry.Key, entry.IsDirectory);
                var collisionKey = ArchivePathRules.GetCollisionKey(actualPath);
                if (!actualKeys.Add(collisionKey)
                    || !expectedByKey.TryGetValue(collisionKey, out var expectedEntry)
                    || !string.Equals(actualPath, expectedEntry.ArchivePath, StringComparison.Ordinal)
                    || entry.IsDirectory != (expectedEntry.Kind == ArchiveEntryKind.Directory)
                    || TryGetLinkTarget(entry) is not null
                    || TryGet(() => entry.IsEncrypted, fallback: false)
                    || TryGet(() => entry.IsSolid, fallback: false))
                {
                    throw ManifestMismatch();
                }

                if (expectedEntry.Kind == ArchiveEntryKind.Directory)
                {
                    if (TryGet(() => entry.Size, fallback: -1L) is not (0 or -1)
                        || !TimestampMatches(entry.LastModifiedTime, expectedEntry.LastWriteTime))
                    {
                        throw ManifestMismatch();
                    }

                    continue;
                }

                if (TryGet(() => entry.Size, fallback: -1L) != expectedEntry.Length
                    || !TimestampMatches(entry.LastModifiedTime, expectedEntry.LastWriteTime))
                {
                    throw ManifestMismatch();
                }

                await using var content = entry.OpenEntryStream();
                var actualDigest = await SHA256.HashDataAsync(content, cancellationToken)
                    .ConfigureAwait(false);
                var expectedDigest = Convert.FromHexString(expectedEntry.ContentSha256!);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
                    {
                        throw ManifestMismatch();
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(actualDigest);
                    CryptographicOperations.ZeroMemory(expectedDigest);
                }
            }

            if (actualCount != expected.Count)
            {
                throw ManifestMismatch();
            }
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or NotSupportedException
                or ArgumentException
                or System.Security.Cryptography.CryptographicException)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "rar.output_invalid",
                "The staged RAR output could not be reopened and validated.",
                exception);
        }
        finally
        {
            archive.Position = originalPosition;
        }
    }

    private static IReadOnlyDictionary<string, ArchiveStagedOutputManifestEntry>
        ValidateExpectedManifest(IReadOnlyList<ArchiveStagedOutputManifestEntry> expected)
    {
        var byKey = new Dictionary<string, ArchiveStagedOutputManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in expected)
        {
            if (entry is null
                || entry.Kind is not ArchiveEntryKind.File and not ArchiveEntryKind.Directory
                || (entry.Kind == ArchiveEntryKind.File
                    && (entry.Length is null or < 0 || !IsSha256(entry.ContentSha256)))
                || (entry.Kind == ArchiveEntryKind.Directory
                    && (entry.Length is not null || entry.ContentSha256 is not null)))
            {
                throw InvalidManifest();
            }

            var normalized = NormalizeArchivePath(
                entry.ArchivePath,
                entry.Kind == ArchiveEntryKind.Directory);
            var key = ArchivePathRules.GetCollisionKey(normalized);
            if (!string.Equals(normalized, entry.ArchivePath, StringComparison.Ordinal)
                || !byKey.TryAdd(key, entry))
            {
                throw InvalidManifest();
            }
        }

        return byKey;
    }

    private static string NormalizeArchivePath(string path, bool directory)
    {
        var withoutTrailingSeparators = directory
            ? path.TrimEnd('/', '\\')
            : path;
        if (!ArchivePathRules.TryNormalize(
                withoutTrailingSeparators,
                out var normalized,
                out _,
                out _))
        {
            throw ManifestMismatch();
        }

        return normalized.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool TimestampMatches(DateTime? actual, DateTimeOffset expected)
    {
        if (actual is null)
        {
            return false;
        }

        var actualUtc = actual.Value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(actual.Value),
            DateTimeKind.Local => new DateTimeOffset(actual.Value).ToUniversalTime(),
            _ => new DateTimeOffset(DateTime.SpecifyKind(actual.Value, DateTimeKind.Local))
                .ToUniversalTime(),
        };
        return (actualUtc - expected.ToUniversalTime()).Duration() <= TimeSpan.FromSeconds(2);
    }

    private static string? TryGetLinkTarget(IArchiveEntry entry) =>
        TryGet(() => entry.LinkTarget, fallback: null);

    private static T TryGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch (Exception exception) when (
            exception is NotImplementedException or ArgumentNullException or InvalidFormatException)
        {
            return fallback;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static ArchiveProviderException ManifestMismatch() =>
        new(
            ArchiveErrorCategory.ProviderFailure,
            "rar.output_manifest_mismatch",
            "The staged RAR output did not match the exact validated output manifest.");

    private static ArchiveProviderException InvalidManifest() =>
        new(
            ArchiveErrorCategory.ProviderFailure,
            "rar.output_manifest_invalid",
            "RAR staged-output validation requires only files and directories, with an exact SHA-256 digest for every file.");

    private sealed class WriterHandle : IArchiveWriterHandle
    {
        private const long WorkspaceReserveBytes = 512L * 1024 * 1024;
        private const int MaximumWorkspaceObjects = 1_000_000;
        private static readonly TimeSpan EncoderTimeout = TimeSpan.FromHours(2);
        private readonly string _executablePath;
        private readonly FileStream _executableLease;
        private readonly Stream _destination;
        private readonly ArchiveCompressionPreset _compressionPreset;
        private readonly WinRarEncoderWorkspace _workspace;
        private readonly string _workspaceRoot;
        private readonly string _inputRoot;
        private readonly string _directoryRoot;
        private readonly string _workRoot;
        private readonly string _candidatePath;
        private readonly string _fileListPath;
        private readonly string _directoryListPath;
        private readonly List<string> _fileListEntries = [];
        private readonly List<string> _directoryListEntries = [];
        private readonly List<(string Path, DateTimeOffset Timestamp)> _directoryTimestamps = [];
        private readonly HashSet<string> _claims = new(StringComparer.OrdinalIgnoreCase);
        private long _materializedBytes;
        // owner.lock plus the three fixed bridge directories. Count newly created
        // path components/files once; final scans also account for encoder output.
        private int _materializedObjectCount = 4;
        private int _state;
        private int _cleanupStarted;

        public WriterHandle(
            string executablePath,
            FileStream executableLease,
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            string? workspaceRoot)
        {
            _executablePath = executablePath;
            _executableLease = executableLease;
            _destination = destination;
            _compressionPreset = compressionPreset;
            _workspace = WinRarEncoderWorkspace.Create(workspaceRoot);
            _workspaceRoot = _workspace.DirectoryPath;
            _inputRoot = Path.Combine(_workspaceRoot, "input");
            _directoryRoot = Path.Combine(_workspaceRoot, "directories");
            _workRoot = Path.Combine(_workspaceRoot, "work");
            _candidatePath = Path.Combine(_workspaceRoot, "candidate.rar");
            _fileListPath = Path.Combine(_workspaceRoot, "files.utf8.lst");
            _directoryListPath = Path.Combine(_workspaceRoot, "directories.utf8.lst");
            try
            {
                Directory.CreateDirectory(_inputRoot);
                Directory.CreateDirectory(_directoryRoot);
                Directory.CreateDirectory(_workRoot);
                EnsureWorkspaceTreeHasNoReparsePoints();
            }
            catch
            {
                _workspace.Dispose();
                throw;
            }
        }

        public ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePlatformMetadata(entry.WindowsAttributes);
            var relative = ValidateAndClaimPath(entry.ArchivePath, directory: true);
            var physical = GetPhysicalPath(_directoryRoot, relative);
            using var directoryLease = _workspace.PrepareMaterializationDirectory(
                physical, ref _materializedObjectCount, MaximumWorkspaceObjects, cancellationToken);
            _directoryTimestamps.Add((physical, entry.LastWriteTime.ToUniversalTime()));
            _directoryListEntries.Add(relative.Replace('/', '\\'));
            return ValueTask.CompletedTask;
        }

        public async ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(reportEntryBytes);
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.CanRead || entry.Length < 0)
            {
                throw new ArgumentException("The RAR entry source and length are invalid.", nameof(source));
            }

            ValidatePlatformMetadata(entry.WindowsAttributes);
            var relative = ValidateAndClaimPath(entry.ArchivePath, directory: false);
            var physical = GetPhysicalPath(_inputRoot, relative);
            var parent = Path.GetDirectoryName(physical)
                ?? throw new InvalidDataException("The RAR materialization path has no parent.");
            using var directoryLease = _workspace.PrepareMaterializationDirectory(
                parent, ref _materializedObjectCount, MaximumWorkspaceObjects, cancellationToken);
            WinRarEncoderWorkspace.EnsureMaterializationObjectAvailable(
                _materializedObjectCount, MaximumWorkspaceObjects);
            EnsureWorkspaceFreeSpace(entry.Length);

            var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
            long copied = 0;
            try
            {
                await using var output = new FileStream(
                    physical,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 80 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                _materializedObjectCount++;
                while (copied < entry.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, entry.Length - copied);
                    var read = await source
                        .ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw SourceChanged();
                    }

                    await output
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    copied = checked(copied + read);
                    await reportEntryBytes(copied).ConfigureAwait(false);
                }

                if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                    .ConfigureAwait(false) != 0)
                {
                    throw SourceChanged();
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            catch
            {
                Interlocked.Exchange(ref _state, 2);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            File.SetLastWriteTimeUtc(physical, entry.LastWriteTime.UtcDateTime);
            _materializedBytes = checked(_materializedBytes + entry.Length);
            _fileListEntries.Add(relative.Replace('/', '\\'));
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (_fileListEntries.Count == 0 && _directoryListEntries.Count == 0)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    "rar.empty_archive_unsupported",
                    "RAR creation requires at least one file or directory source.");
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new ObjectDisposedException(nameof(WriterHandle));
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (WinRarEncoderProbe.ProbeVerifiedExecutable(_executablePath)
                    != RarEncoderAvailability.Available)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.UnsupportedCapability,
                        "rar.encoder_identity_changed",
                        "The separately installed RAR encoder changed before use.");
                }

                EnsureWorkspaceTreeHasNoReparsePoints(cancellationToken);
                foreach (var directory in _directoryTimestamps
                    .OrderByDescending(item => item.Path.Count(character =>
                        character == Path.DirectorySeparatorChar)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.SetLastWriteTimeUtc(directory.Path, directory.Timestamp.UtcDateTime);
                }

                EnsureWorkspaceFreeSpace(EstimateCandidateBytes());
                var utf8 = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true);
                if (_directoryListEntries.Count > 0)
                {
                    await File.WriteAllLinesAsync(
                            _directoryListPath,
                            GetOrderedDirectoryEntries(),
                            utf8,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (_fileListEntries.Count > 0)
                {
                    await File.WriteAllLinesAsync(
                            _fileListPath,
                            _fileListEntries,
                            utf8,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                EnsureWorkspaceTreeHasNoReparsePoints(cancellationToken);

                if (_directoryListEntries.Count > 0)
                {
                    await RunEncoderAsync(
                        CreateDirectoryArguments(),
                        _directoryRoot,
                        cancellationToken).ConfigureAwait(false);
                }

                if (_fileListEntries.Count > 0)
                {
                    await RunEncoderAsync(
                        CreateFileArguments(),
                        _inputRoot,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!File.Exists(_candidatePath)
                    || File.GetAttributes(_candidatePath).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw EncoderFailure("rar.encoder_output_missing");
                }

                await RunEncoderAsync(
                    CreateTestArguments(),
                    _workspaceRoot,
                    cancellationToken).ConfigureAwait(false);
                EnsureWorkspaceTreeHasNoReparsePoints(cancellationToken);
                await using (var candidate = new FileStream(
                    _candidatePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 80 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await candidate.CopyToAsync(_destination, 80 * 1024, cancellationToken)
                        .ConfigureAwait(false);
                }

                CleanupWorkspaceOrThrow();
            }
            catch
            {
                Interlocked.Exchange(ref _state, 2);
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.CompareExchange(ref _state, 2, 0);
            try
            {
                CleanupWorkspaceOrThrow();
            }
            finally
            {
                _workspace.Dispose();
                _executableLease.Dispose();
            }
            return ValueTask.CompletedTask;
        }

        private IReadOnlyList<string> GetOrderedDirectoryEntries()
        {
            return _directoryListEntries
                .OrderBy(entry => entry.Count(character => character == '\\'))
                .ThenBy(entry => entry, StringComparer.Ordinal)
                .ToArray();
        }

        private IReadOnlyList<string> CreateFileArguments() =>
        [
            "a",
            "-cfg-",
            "-y",
            "-t",
            "-idq",
            "-ierr",
            "-r-",
            "-s=-",
            "-ma5",
            "-htb",
            "-scfl",
            $"-w{_workRoot}",
            CompressionSwitch(_compressionPreset),
            _candidatePath,
            $"@{_fileListPath}",
        ];

        private IReadOnlyList<string> CreateDirectoryArguments() =>
        [
            "a",
            "-cfg-",
            "-y",
            "-t",
            "-idq",
            "-ierr",
            "-r-",
            "-s=-",
            "-ma5",
            "-htb",
            "-scfl",
            $"-w{_workRoot}",
            CompressionSwitch(_compressionPreset),
            _candidatePath,
            $"@{_directoryListPath}",
        ];

        private IReadOnlyList<string> CreateTestArguments() =>
        [
            "t",
            "-cfg-",
            "-y",
            "-idq",
            "-ierr",
            $"-w{_workRoot}",
            _candidatePath,
        ];

        private async Task RunEncoderAsync(
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.Environment.Remove("RARINISWITCHES");
            startInfo.Environment["TEMP"] = _workRoot;
            startInfo.Environment["TMP"] = _workRoot;
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw EncoderFailure("rar.encoder_start_failed");
            process.StandardInput.Close();
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(EncoderTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TerminateEncoderProcess(process);
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "rar.encoder_timeout",
                    "The separately installed RAR encoder exceeded the bounded operation time.");
            }
            catch
            {
                TerminateEncoderProcess(process);
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProviderFailure,
                    "rar.encoder_failed",
                    $"The separately installed RAR encoder failed with exit code {process.ExitCode}.");
            }
        }

        private static async Task DrainAsync(StreamReader reader)
        {
            var buffer = ArrayPool<char>.Shared.Rent(4096);
            try
            {
                while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0)
                {
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer, clearArray: true);
            }
        }

        internal static void TerminateEncoderProcess(
            Process process,
            Action<Process>? kill = null,
            Func<Process, int, bool>? waitForExit = null)
        {
            ArgumentNullException.ThrowIfNull(process);
            kill ??= static target => target.Kill(entireProcessTree: true);
            waitForExit ??= static (target, milliseconds) => target.WaitForExit(milliseconds);
            Exception? terminationFailure = null;
            try
            {
                if (!process.HasExited)
                {
                    kill(process);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
            {
                terminationFailure = exception;
            }

            try
            {
                if (process.HasExited || waitForExit(process, 5_000))
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
            {
                terminationFailure ??= exception;
            }

            throw new WinRarEncoderContainmentException(terminationFailure);
        }

        private string ValidateAndClaimPath(string archivePath, bool directory)
        {
            var candidate = directory ? archivePath.TrimEnd('/', '\\') : archivePath;
            if (!ArchivePathRules.TryNormalize(
                    candidate,
                    out var normalized,
                    out var collisionKey,
                    out var reason)
                || !_claims.Add(collisionKey))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsafePath,
                    "rar.materialization_path_invalid",
                    $"The validated RAR entry cannot be materialized safely: {reason}");
            }

            return normalized.Replace(Path.DirectorySeparatorChar, '/');
        }

        private string GetPhysicalPath(string root, string relativePath)
        {
            var physical = Path.GetFullPath(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!physical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsafePath,
                    "rar.materialization_path_escape",
                    "The validated RAR entry escaped its private materialization root.");
            }

            return physical;
        }

        private void EnsureWorkspaceFreeSpace(long additionalBytes)
        {
            try
            {
                var root = Path.GetPathRoot(_workspaceRoot)
                    ?? throw new IOException("The RAR workspace has no volume root.");
                var available = new DriveInfo(root).AvailableFreeSpace;
                if (available < checked(additionalBytes + WorkspaceReserveBytes))
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.ResourceLimit,
                        "rar.workspace_insufficient_space",
                        "The private RAR encoder workspace does not have enough free space.");
                }
            }
            catch (ArchiveProviderException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProviderFailure,
                    "rar.workspace_space_unavailable",
                    "The private RAR encoder workspace free space could not be verified.",
                    exception);
            }
        }

        private long EstimateCandidateBytes()
        {
            try
            {
                return checked(
                    _materializedBytes
                    + (_materializedBytes / 8)
                    + (1L * 1024 * 1024)
                    + ((long)(_fileListEntries.Count + _directoryListEntries.Count) * 4096));
            }
            catch (OverflowException exception)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "rar.workspace_estimate_overflow",
                    "The private RAR encoder workspace estimate overflowed.",
                    exception);
            }
        }

        private void EnsureWorkspaceTreeHasNoReparsePoints(CancellationToken cancellationToken = default)
        {
            var pending = new Stack<string>();
            pending.Push(_workspaceRoot);
            var objectCount = 0;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                foreach (var path in Directory.EnumerateFileSystemEntries(
                    current,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++objectCount > MaximumWorkspaceObjects)
                    {
                        throw new ArchiveProviderException(
                            ArchiveErrorCategory.ResourceLimit,
                            "rar.workspace_object_limit",
                            "The private RAR encoder workspace exceeded its bounded object count.");
                    }

                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        throw new ArchiveProviderException(
                            ArchiveErrorCategory.UnsafePath,
                            "rar.workspace_reparse_detected",
                            "The private RAR encoder workspace acquired a reparse point.");
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(path);
                    }
                }
            }
        }

        private void CleanupWorkspaceOrThrow()
        {
            if (Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) != 0)
            {
                return;
            }

            try
            {
                EnsureWorkspaceTreeHasNoReparsePoints();
                _workspace.Cleanup();
                Interlocked.Exchange(ref _cleanupStarted, 2);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArchiveProviderException
                    or System.ComponentModel.Win32Exception)
            {
                Interlocked.Exchange(ref _cleanupStarted, 0);
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProviderFailure,
                    "rar.workspace_cleanup_failed",
                    "The private RAR encoder workspace could not be proven clean.",
                    exception);
            }
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref _state) != 0)
            {
                throw new ObjectDisposedException(nameof(WriterHandle));
            }
        }

        private static void ValidatePlatformMetadata(int? windowsAttributes)
        {
            if (windowsAttributes is not null)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "rar.platform_metadata_unsupported",
                    "RAR creation does not accept caller-supplied platform metadata.");
            }
        }

        private static string CompressionSwitch(ArchiveCompressionPreset preset) => preset switch
        {
            ArchiveCompressionPreset.Store => "-m0",
            ArchiveCompressionPreset.Fast => "-m1",
            ArchiveCompressionPreset.Balanced => "-m3",
            ArchiveCompressionPreset.Maximum => "-m5",
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        private static ArchiveProviderException SourceChanged() =>
            new(
                ArchiveErrorCategory.SourceChanged,
                "source.changed_during_creation",
                "A source stream changed while it was being materialized for RAR creation.");

        private static ArchiveProviderException EncoderFailure(string code) =>
            new(
                ArchiveErrorCategory.ProviderFailure,
                code,
                "The separately installed RAR encoder could not produce a qualified output archive.");
    }
}
