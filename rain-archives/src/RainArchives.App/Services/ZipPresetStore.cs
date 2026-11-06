using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainArchives.Contracts;

namespace RainArchives.App.Services;

/// <summary>
/// One immutable ZIP preset that can be selected by either App archive dialog.
/// Presets intentionally contain no archive, source, destination, or recovery data.
/// </summary>
public sealed class ZipPreset
{
    private ZipPreset(
        string name,
        ArchiveCompressionPreset compressionPreset,
        ZipPresetMetadataDefaults metadataDefaults,
        bool isBuiltIn)
    {
        Name = name;
        CompressionPreset = compressionPreset;
        MetadataDefaults = metadataDefaults;
        IsBuiltIn = isBuiltIn;
    }

    public string Name { get; }
    public ArchiveCompressionPreset CompressionPreset { get; }
    public ZipPresetMetadataDefaults MetadataDefaults { get; }
    public bool IsBuiltIn { get; }

    public static ZipPreset CreateUser(
        string name,
        ArchiveCompressionPreset compressionPreset,
        ZipPresetMetadataDefaults? metadataDefaults = null)
    {
        var normalizedName = ZipPresetValidation.NormalizeName(name);
        ZipPresetValidation.ValidateCompressionPreset(compressionPreset);
        return new(
            normalizedName,
            compressionPreset,
            metadataDefaults ?? new ZipPresetMetadataDefaults(),
            isBuiltIn: false);
    }

    internal static ZipPreset CreateBuiltIn(
        string name,
        ArchiveCompressionPreset compressionPreset) =>
        new(
            name,
            compressionPreset,
            new ZipPresetMetadataDefaults(),
            isBuiltIn: true);
}

/// <summary>
/// The only metadata defaults a user preset may carry. Null leaves the rebuild's
/// existing/derived value unchanged; these defaults apply only to mapped entries.
/// </summary>
public sealed class ZipPresetMetadataDefaults
{
    public ZipPresetMetadataDefaults(
        ArchiveDosLocalTimestamp? lastWriteTime = null,
        ushort? attributesLowWord = null)
    {
        if (lastWriteTime is not null)
        {
            ZipPresetValidation.ValidateTimestamp(lastWriteTime);
        }

        if (attributesLowWord is not null)
        {
            ZipPresetValidation.ValidateAttributes(attributesLowWord.Value);
        }

        LastWriteTime = lastWriteTime;
        AttributesLowWord = attributesLowWord;
    }

    public ArchiveDosLocalTimestamp? LastWriteTime { get; }
    public ushort? AttributesLowWord { get; }
}

public enum ZipPresetLoadState
{
    Ready,
    Missing,
    UnavailableOrInvalid,
}

public sealed record ZipPresetLoadResult(
    IReadOnlyList<ZipPreset> Presets,
    string? Warning,
    ZipPresetLoadState State = ZipPresetLoadState.Ready)
{
    public bool UsedBuiltIns => State != ZipPresetLoadState.Ready;
}

public sealed record ZipPresetSaveResult(
    bool Succeeded,
    string? Warning);

/// <summary>
/// App-owned, settings-only persistence for reusable ZIP presets. It has no
/// archive-operation or engine filesystem authority.
/// </summary>
public sealed class ZipPresetStore
{
    public const int SchemaVersion = 1;
    public const int MaximumFileBytes = 128 * 1024;
    public const int MaximumUserPresets = 64;
    public const int MaximumNameScalars = 64;
    public const int MaximumNameUtf8Bytes = 256;

    private const string SettingsDirectoryName = "settings";
    private const string SettingsFileName = "zip-presets.v1.json";
    private const string BackupFileName = "zip-presets.v1.json.bak";
    private const string WarningText =
        "ZIP presets could not be loaded or saved safely; built-in presets remain available.";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly IReadOnlyList<ZipPreset> BuiltInPresets =
        Array.AsReadOnly(
        [
            ZipPreset.CreateBuiltIn("Store", ArchiveCompressionPreset.Store),
            ZipPreset.CreateBuiltIn("Fast", ArchiveCompressionPreset.Fast),
            ZipPreset.CreateBuiltIn("Balanced", ArchiveCompressionPreset.Balanced),
            ZipPreset.CreateBuiltIn("Maximum", ArchiveCompressionPreset.Maximum),
        ]);

    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly string _backupPath;
    private readonly string _lockName;
    private readonly string _localRoot;

    public ZipPresetStore(string? localApplicationDataRoot = null)
    {
        var localRoot = localApplicationDataRoot;
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            localRoot = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
        }

        if (string.IsNullOrWhiteSpace(localRoot))
        {
            throw new InvalidOperationException(
                "Windows did not provide a local application-data directory.");
        }

        _localRoot = Path.GetFullPath(localRoot);
        var applicationRoot = Path.Combine(_localRoot, "SailHub", "Rain Archives");
        _settingsDirectory = Path.Combine(applicationRoot, SettingsDirectoryName);
        _settingsPath = Path.Combine(_settingsDirectory, SettingsFileName);
        _backupPath = Path.Combine(_settingsDirectory, BackupFileName);
        _lockName = CreateLockName(_settingsDirectory);
    }

    public static IReadOnlyList<ZipPreset> BuiltIns => BuiltInPresets;

    public ZipPresetLoadResult Load() => Load(CancellationToken.None);

    public ZipPresetLoadResult Load(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mutex? gate = null;
        try
        {
            gate = OpenStoreLock(cancellationToken);
            if (!LocalSettingsFileSafety.ValidateExistingDirectoryChain(
                    _localRoot,
                    "SailHub",
                    "Rain Archives",
                    SettingsDirectoryName))
            {
                return Fallback(ZipPresetLoadState.Missing);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!LocalSettingsFileSafety.TryGetAttributes(_settingsPath, out _))
            {
                return Fallback(ZipPresetLoadState.Missing);
            }

            var document = ReadDocument(cancellationToken);
            var userPresets = ValidateDocument(document);
            return new ZipPresetLoadResult(CombineWithBuiltIns(userPresets), null);
        }
        catch (Exception exception) when (IsRecoverableStorageFailure(exception))
        {
            return Fallback(ZipPresetLoadState.UnavailableOrInvalid);
        }
        finally
        {
            ReleaseStoreLock(gate);
        }
    }

    public ZipPresetSaveResult Save(IReadOnlyList<ZipPreset> userPresets) =>
        Save(userPresets, CancellationToken.None);

    public ZipPresetSaveResult Save(
        IReadOnlyList<ZipPreset> userPresets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userPresets);
        var validated = ValidateUserPresets(userPresets);
        return SaveCore(validated, allowInvalidExistingDocument: false, cancellationToken);
    }

    public ZipPresetSaveResult Reset(CancellationToken cancellationToken = default) =>
        SaveCore(Array.Empty<ZipPreset>(), allowInvalidExistingDocument: true, cancellationToken);

    private ZipPresetSaveResult SaveCore(
        IReadOnlyList<ZipPreset> validated,
        bool allowInvalidExistingDocument,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mutex? gate = null;
        string? temporaryPath = null;
        LocalSettingsFileIdentity? temporaryIdentity = null;
        try
        {
            gate = OpenStoreLock(cancellationToken);
            LocalSettingsFileSafety.CreateAndValidateDirectoryChain(
                _localRoot,
                "SailHub",
                "Rain Archives",
                SettingsDirectoryName);
            LocalSettingsFileSafety.EnsureRegularFileIfPresent(_settingsPath);
            LocalSettingsFileSafety.EnsureRegularFileIfPresent(_backupPath);
            if (!allowInvalidExistingDocument
                && LocalSettingsFileSafety.TryGetAttributes(_settingsPath, out _))
            {
                _ = ValidateDocument(ReadDocument(cancellationToken));
            }

            var payload = JsonSerializer.Serialize(
                new PersistedDocument(
                    SchemaVersion,
                    validated.Select(static preset => new PersistedPreset(
                        preset.Name,
                        preset.CompressionPreset,
                        new PersistedMetadataDefaults(
                            preset.MetadataDefaults.LastWriteTime,
                            preset.MetadataDefaults.AttributesLowWord))).ToArray()),
                JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(payload);
            if (bytes.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("The ZIP preset settings file exceeds its bounded size.");
            }

            var temporary = CreateTemporaryFile();
            temporaryPath = temporary.Path;
            temporaryIdentity = temporary.Identity;
            using (temporary.Stream)
            {
                temporary.Stream.Write(bytes);
                temporary.Stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var closedIdentity = LocalSettingsFileSafety.EnsureRegularFile(temporaryPath);
            if (!temporaryIdentity.Value.SameObject(closedIdentity))
            {
                throw new UnsafeSettingsObjectException(
                    "The ZIP preset staging object changed before commit.");
            }

            LocalSettingsFileSafety.EnsureRegularFileIfPresent(_settingsPath);
            LocalSettingsFileSafety.EnsureRegularFileIfPresent(_backupPath);
            if (LocalSettingsFileSafety.TryGetAttributes(_settingsPath, out _))
            {
                File.Replace(
                    temporaryPath,
                    _settingsPath,
                    _backupPath,
                    ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(temporaryPath, _settingsPath);
            }

            temporaryPath = null;
            _ = LocalSettingsFileSafety.EnsureRegularFile(_settingsPath);
            LocalSettingsFileSafety.EnsureRegularFileIfPresent(_backupPath);
            return new ZipPresetSaveResult(true, null);
        }
        catch (Exception exception) when (IsRecoverableStorageFailure(exception))
        {
            return new ZipPresetSaveResult(false, WarningText);
        }
        finally
        {
            if (temporaryPath is not null && temporaryIdentity is not null)
            {
                TryDeleteExactTemporary(temporaryPath, temporaryIdentity.Value);
            }

            ReleaseStoreLock(gate);
        }
    }

    internal string SettingsDirectoryPath => _settingsDirectory;
    internal string SettingsPath => _settingsPath;
    internal string BackupPath => _backupPath;
    internal string LockName => _lockName;

    private static JsonSerializerOptions CreateJsonOptions() =>
        new(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            WriteIndented = false,
        };

    private static string CreateLockName(string settingsDirectory)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value
            ?? Environment.UserName;
        var material = Encoding.UTF8.GetBytes($"{user}\0{Path.GetFullPath(settingsDirectory)}");
        var digest = Convert.ToHexString(SHA256.HashData(material));
        return $"Local\\SailHub.RainArchives.ZipPresets.{digest}";
    }

    private Mutex OpenStoreLock(CancellationToken cancellationToken)
    {
        var mutex = new Mutex(false, _lockName);
        try
        {
            try
            {
                var result = WaitHandle.WaitAny(
                    [mutex, cancellationToken.WaitHandle],
                    TimeSpan.FromSeconds(30));
                if (result == 1)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result == WaitHandle.WaitTimeout)
                {
                    throw new IOException("The ZIP preset settings lock could not be acquired.");
                }
            }
            catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
            {
                // Ownership transfers to this caller; the abandoned state is safe
                // because all writes use a fresh sibling and atomic replacement.
            }

            return mutex;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private (string Path, FileStream Stream, LocalSettingsFileIdentity Identity) CreateTemporaryFile()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Path.Combine(
                _settingsDirectory,
                $"{SettingsFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                var stream = new FileStream(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.WriteThrough);
                try
                {
                    var identity = LocalSettingsFileSafety.EnsureRegularFileHandle(stream.SafeFileHandle);
                    return (candidate, stream, identity);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException) when (attempt < 7)
            {
                // A random sibling collision is retried without touching any other file.
            }
        }

        throw new IOException("A unique ZIP preset temporary sibling could not be created.");
    }

    private PersistedDocument ReadDocument(CancellationToken cancellationToken)
    {
        using var stream = LocalSettingsFileSafety.OpenVerifiedRead(
            _settingsPath,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The ZIP preset settings file exceeds its bounded size.");
        }

        var bytes = new byte[MaximumFileBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
        }

        if (total > MaximumFileBytes)
        {
            throw new InvalidDataException("The ZIP preset settings file exceeds its bounded size.");
        }

        return JsonSerializer.Deserialize<PersistedDocument>(bytes.AsSpan(0, total), JsonOptions)
            ?? throw new JsonException("The ZIP preset settings document was empty.");
    }

    private static IReadOnlyList<ZipPreset> ValidateDocument(PersistedDocument? document)
    {
        if (document is null
            || document.Version != SchemaVersion
            || document.UserPresets is null)
        {
            throw new JsonException("The ZIP preset settings document schema is unsupported.");
        }

        var userPresets = new List<ZipPreset>(document.UserPresets.Count);
        foreach (var persisted in document.UserPresets)
        {
            if (persisted is null || persisted.MetadataDefaults is null)
            {
                throw new JsonException("The ZIP preset settings document is incomplete.");
            }

            userPresets.Add(ZipPreset.CreateUser(
                persisted.Name ?? throw new JsonException("A ZIP preset name is missing."),
                persisted.CompressionPreset,
                new ZipPresetMetadataDefaults(
                    persisted.MetadataDefaults.LastWriteTime,
                    persisted.MetadataDefaults.AttributesLowWord)));
        }

        return ValidateUserPresets(userPresets);
    }

    private static IReadOnlyList<ZipPreset> ValidateUserPresets(
        IReadOnlyList<ZipPreset> userPresets)
    {
        if (userPresets.Count > MaximumUserPresets)
        {
            throw new ArgumentException(
                $"A maximum of {MaximumUserPresets} user ZIP presets is supported.",
                nameof(userPresets));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in userPresets)
        {
            ArgumentNullException.ThrowIfNull(preset);
            if (preset.IsBuiltIn)
            {
                throw new ArgumentException("Built-in ZIP presets are immutable.", nameof(userPresets));
            }

            var normalizedName = ZipPresetValidation.NormalizeName(preset.Name);
            if (!names.Add(normalizedName)
                || BuiltInPresets.Any(builtIn =>
                    string.Equals(builtIn.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    "ZIP preset names must be unique and may not shadow a built-in preset.",
                    nameof(userPresets));
            }

            ZipPresetValidation.ValidateCompressionPreset(preset.CompressionPreset);
            ArgumentNullException.ThrowIfNull(preset.MetadataDefaults);
        }

        return userPresets.ToArray();
    }

    private static IReadOnlyList<ZipPreset> CombineWithBuiltIns(
        IReadOnlyList<ZipPreset> userPresets)
    {
        var all = new List<ZipPreset>(BuiltInPresets.Count + userPresets.Count);
        all.AddRange(BuiltInPresets);
        all.AddRange(userPresets);
        return all.AsReadOnly();
    }

    private ZipPresetLoadResult Fallback(ZipPresetLoadState state) =>
        new(BuiltInPresets, WarningText, state);

    private static void ReleaseStoreLock(Mutex? mutex)
    {
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }

    private static void TryDeleteExactTemporary(
        string path,
        LocalSettingsFileIdentity expectedIdentity)
    {
        try
        {
            if (!LocalSettingsFileSafety.TryGetAttributes(path, out _))
            {
                return;
            }

            var actual = LocalSettingsFileSafety.EnsureRegularFile(path);
            if (expectedIdentity.SameObject(actual))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The exact operation-owned sibling is best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Never broaden cleanup to another path after a failed save.
        }
    }

    private static bool IsRecoverableStorageFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidDataException
            or DecoderFallbackException;

    private sealed record PersistedDocument(
        [property: JsonRequired] int Version,
        [property: JsonRequired] IReadOnlyList<PersistedPreset> UserPresets);

    private sealed record PersistedPreset(
        [property: JsonRequired] string Name,
        [property: JsonRequired] ArchiveCompressionPreset CompressionPreset,
        [property: JsonRequired] PersistedMetadataDefaults MetadataDefaults);

    private sealed record PersistedMetadataDefaults(
        ArchiveDosLocalTimestamp? LastWriteTime,
        ushort? AttributesLowWord);
}

internal static class ZipPresetValidation
{
    private const ushort AllowedAttributes = 0x0001 | 0x0002 | 0x0004 | 0x0020;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A ZIP preset name is required.", nameof(name));
        }

        string normalized;
        try
        {
            normalized = name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("A ZIP preset name contains invalid Unicode.", nameof(name), exception);
        }

        var scalarCount = 0;
        for (var index = 0; index < normalized.Length;)
        {
            UnicodeCategory category;
            if (char.IsHighSurrogate(normalized[index]))
            {
                if (index + 1 >= normalized.Length || !char.IsLowSurrogate(normalized[index + 1]))
                {
                    throw new ArgumentException("A ZIP preset name contains invalid Unicode.", nameof(name));
                }

                category = CharUnicodeInfo.GetUnicodeCategory(normalized, index);
                index += 2;
            }
            else
            {
                if (char.IsLowSurrogate(normalized[index]))
                {
                    throw new ArgumentException("A ZIP preset name contains invalid Unicode.", nameof(name));
                }

                category = CharUnicodeInfo.GetUnicodeCategory(normalized, index);
                index++;
            }

            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                throw new ArgumentException(
                    "A ZIP preset name contains a control or bidi character.",
                    nameof(name));
            }

            scalarCount = checked(scalarCount + 1);
        }

        if (scalarCount > ZipPresetStore.MaximumNameScalars)
        {
            throw new ArgumentException(
                $"A ZIP preset name may contain at most {ZipPresetStore.MaximumNameScalars} Unicode scalars.",
                nameof(name));
        }

        try
        {
            if (StrictUtf8.GetByteCount(normalized) > ZipPresetStore.MaximumNameUtf8Bytes)
            {
                throw new ArgumentException(
                    $"A ZIP preset name may contain at most {ZipPresetStore.MaximumNameUtf8Bytes} UTF-8 bytes.",
                    nameof(name));
            }

            return normalized;
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("A ZIP preset name contains invalid Unicode.", nameof(name), exception);
        }
        catch (ArgumentException exception) when (exception.ParamName is null)
        {
            throw new ArgumentException("A ZIP preset name contains invalid Unicode.", nameof(name), exception);
        }
    }

    public static void ValidateCompressionPreset(ArchiveCompressionPreset preset)
    {
        if (preset is not (ArchiveCompressionPreset.Store
            or ArchiveCompressionPreset.Fast
            or ArchiveCompressionPreset.Balanced
            or ArchiveCompressionPreset.Maximum))
        {
            throw new ArgumentException("The ZIP compression preset is unsupported.", nameof(preset));
        }
    }

    public static void ValidateTimestamp(ArchiveDosLocalTimestamp timestamp)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        if (timestamp.Year is < 1980 or > 2107
            || timestamp.Month is < 1 or > 12
            || timestamp.Hour is < 0 or > 23
            || timestamp.Minute is < 0 or > 59
            || timestamp.Second is < 0 or > 58
            || (timestamp.Second & 1) != 0)
        {
            throw new ArgumentException("The ZIP preset timestamp is not exactly DOS-representable.", nameof(timestamp));
        }

        try
        {
            _ = new DateTime(
                timestamp.Year,
                timestamp.Month,
                timestamp.Day,
                timestamp.Hour,
                timestamp.Minute,
                timestamp.Second,
                DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("The ZIP preset timestamp is not a valid calendar value.", nameof(timestamp), exception);
        }
    }

    public static void ValidateAttributes(ushort attributes)
    {
        if ((attributes & ~AllowedAttributes) != 0)
        {
            throw new ArgumentException("The ZIP preset attributes contain an unsupported bit.", nameof(attributes));
        }
    }
}
