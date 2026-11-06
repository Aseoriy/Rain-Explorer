using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainArchives.Contracts;

namespace RainArchives.App.Services;

public sealed record ManagedCreationDefaults(
    ArchiveCreationFormat DefaultFormat,
    ArchiveCompressionPreset ZipPreset,
    ArchiveCompressionPreset TarPreset,
    ArchiveCompressionPreset TarGZipPreset,
    ArchiveCompressionPreset TarBZip2Preset,
    ArchiveCompressionPreset GZipPreset,
    ArchiveCompressionPreset BZip2Preset,
    ArchiveCompressionPreset ZipRebuildPreset)
{
    public ArchiveCompressionPreset GetPreset(ArchiveCreationFormat format) => format switch
    {
        ArchiveCreationFormat.Zip => ZipPreset,
        ArchiveCreationFormat.Tar => TarPreset,
        ArchiveCreationFormat.TarGZip => TarGZipPreset,
        ArchiveCreationFormat.TarBZip2 => TarBZip2Preset,
        ArchiveCreationFormat.GZip => GZipPreset,
        ArchiveCreationFormat.BZip2 => BZip2Preset,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

/// <summary>
/// Bounded App-only preferences. This model intentionally cannot contain paths,
/// archive text, operation data, secrets, recovery authority, or file handles.
/// </summary>
public sealed record AppUiSettings(
    ManagedCreationDefaults ManagedCreation,
    bool ShowAdvancedRebuildOptions)
{
    public static AppUiSettings Default { get; } = new(
        new ManagedCreationDefaults(
            ArchiveCreationFormat.Zip,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Store,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Balanced),
        ShowAdvancedRebuildOptions: false);
}

public enum AppSettingsLoadState
{
    Ready,
    Defaults,
    RecoveredFromBackup,
    Corrupt,
    UnsupportedVersion,
    UnsafeStorage,
    Unavailable,
}

public sealed record AppSettingsLoadResult(
    AppUiSettings Settings,
    AppSettingsLoadState State,
    bool IsLastKnownGood,
    string Status);

public enum AppSettingsSaveState
{
    Saved,
    UnsafeStorage,
    Unavailable,
}

public sealed record AppSettingsSaveResult(
    bool Succeeded,
    AppSettingsSaveState State,
    string Status);

/// <summary>
/// Versioned, fixed-field persistence for App-only UI settings. The primary and
/// backup are never repaired implicitly; a validated backup is exposed as an
/// explicit read-only last-known-good state until the user chooses Reset.
/// </summary>
public sealed class AppSettingsStore
{
    public const int SchemaVersion = 1;
    public const int MaximumFileBytes = 32 * 1024;
    public const int MaximumSchemaTokenScalars = 32;
    public const int MaximumSchemaTokenUtf8Bytes = 64;
    public const int MaximumFreeFormStrings = 0;

    private const string SailHubDirectoryName = "SailHub";
    private const string ApplicationDirectoryName = "Rain Archives";
    private const string SettingsDirectoryName = "settings";
    private const string SettingsFileName = "ui-settings.v1.json";
    private const string BackupFileName = "ui-settings.v1.json.bak";
    private const string ReadyStatus = "Settings are loaded from the current local document.";
    private const string DefaultsStatus = "No saved UI settings exist; bounded defaults are active.";
    private const string RecoveredStatus = "The current settings document was not usable; a validated backup is active without rewriting local state.";
    private const string CorruptStatus = "Saved UI settings are malformed or incomplete; the last known good settings remain active.";
    private const string UnsupportedStatus = "Saved UI settings use an unsupported schema version; the last known good settings remain active.";
    private const string UnsafeStatus = "The local settings object was refused because its filesystem state is unsafe or ambiguous.";
    private const string UnavailableStatus = "Local UI settings are unavailable; the last known good settings remain active.";
    private const string SavedStatus = "UI settings were saved atomically.";
    private const string SaveUnavailableStatus = "UI settings were not changed because local storage is unavailable.";
    private const string SaveUnsafeStatus = "UI settings were not changed because the local settings object is unsafe or ambiguous.";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _lastKnownGoodGate = new();
    private readonly string _localRoot;
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly string _backupPath;
    private readonly string _lockName;
    private readonly TimeSpan _lockTimeout;
    private AppUiSettings _lastKnownGood = AppUiSettings.Default;

    public AppSettingsStore(string? localApplicationDataRoot = null)
        : this(localApplicationDataRoot, TimeSpan.FromSeconds(30))
    {
    }

    internal AppSettingsStore(
        string? localApplicationDataRoot,
        TimeSpan lockTimeout)
    {
        if (lockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }

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
        _settingsDirectory = Path.Combine(
            _localRoot,
            SailHubDirectoryName,
            ApplicationDirectoryName,
            SettingsDirectoryName);
        _settingsPath = Path.Combine(_settingsDirectory, SettingsFileName);
        _backupPath = Path.Combine(_settingsDirectory, BackupFileName);
        _lockName = CreateLockName(_settingsDirectory);
        _lockTimeout = lockTimeout;
    }

    public AppSettingsLoadResult Load(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mutex? gate = null;
        try
        {
            gate = OpenStoreLock(cancellationToken);
            if (!LocalSettingsFileSafety.ValidateExistingDirectoryChain(
                    _localRoot,
                    SailHubDirectoryName,
                    ApplicationDirectoryName,
                    SettingsDirectoryName))
            {
                return Fallback(AppSettingsLoadState.Defaults, DefaultsStatus, useLastKnownGood: false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!LocalSettingsFileSafety.TryGetAttributes(_settingsPath, out _))
            {
                return LoadBackupOrFallback(
                    AppSettingsLoadState.Defaults,
                    DefaultsStatus,
                    cancellationToken);
            }

            try
            {
                var settings = ReadAndValidate(_settingsPath, cancellationToken);
                SetLastKnownGood(settings);
                return new AppSettingsLoadResult(settings, AppSettingsLoadState.Ready, false, ReadyStatus);
            }
            catch (UnsupportedAppSettingsVersionException)
            {
                return Fallback(
                    AppSettingsLoadState.UnsupportedVersion,
                    UnsupportedStatus,
                    useLastKnownGood: true);
            }
            catch (Exception exception) when (IsMalformedDocument(exception))
            {
                return LoadBackupOrFallback(
                    AppSettingsLoadState.Corrupt,
                    CorruptStatus,
                    cancellationToken);
            }
        }
        catch (UnsafeSettingsObjectException)
        {
            return Fallback(AppSettingsLoadState.UnsafeStorage, UnsafeStatus, useLastKnownGood: true);
        }
        catch (Exception exception) when (IsUnavailableStorage(exception))
        {
            return Fallback(AppSettingsLoadState.Unavailable, UnavailableStatus, useLastKnownGood: true);
        }
        finally
        {
            ReleaseStoreLock(gate);
        }
    }

    public AppSettingsSaveResult Save(
        AppUiSettings settings,
        CancellationToken cancellationToken = default)
    {
        var validated = Validate(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = Serialize(validated);
        Mutex? gate = null;
        string? temporaryPath = null;
        LocalSettingsFileIdentity? temporaryIdentity = null;
        try
        {
            gate = OpenStoreLock(cancellationToken);
            LocalSettingsFileSafety.CreateAndValidateDirectoryChain(
                _localRoot,
                SailHubDirectoryName,
                ApplicationDirectoryName,
                SettingsDirectoryName);
            LocalSettingsFileSafety.EnsureRegularFileIfPresent(_settingsPath);
            LocalSettingsFileSafety.EnsureRegularFileIfPresent(_backupPath);
            cancellationToken.ThrowIfCancellationRequested();

            var temporary = CreateTemporaryFile();
            temporaryPath = temporary.Path;
            temporaryIdentity = temporary.Identity;
            using (temporary.Stream)
            {
                temporary.Stream.Write(payload);
                temporary.Stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var closedIdentity = LocalSettingsFileSafety.EnsureRegularFile(temporaryPath);
            if (!temporaryIdentity.Value.SameObject(closedIdentity))
            {
                throw new UnsafeSettingsObjectException(
                    "The local settings staging object changed before commit.");
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
            SetLastKnownGood(validated);
            return new AppSettingsSaveResult(true, AppSettingsSaveState.Saved, SavedStatus);
        }
        catch (UnsafeSettingsObjectException)
        {
            return new AppSettingsSaveResult(false, AppSettingsSaveState.UnsafeStorage, SaveUnsafeStatus);
        }
        catch (Exception exception) when (IsUnavailableStorage(exception))
        {
            return new AppSettingsSaveResult(false, AppSettingsSaveState.Unavailable, SaveUnavailableStatus);
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

    public AppSettingsSaveResult Reset(CancellationToken cancellationToken = default) =>
        Save(AppUiSettings.Default, cancellationToken);

    internal string SettingsDirectoryPath => _settingsDirectory;
    internal string SettingsPath => _settingsPath;
    internal string BackupPath => _backupPath;
    internal string LockName => _lockName;

    public static AppUiSettings Validate(AppUiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.ManagedCreation);
        var defaults = settings.ManagedCreation;
        ValidateFormat(defaults.DefaultFormat);
        ValidatePreset(defaults.ZipPreset);
        ValidatePreset(defaults.TarPreset);
        ValidatePreset(defaults.TarGZipPreset);
        ValidatePreset(defaults.TarBZip2Preset);
        ValidatePreset(defaults.GZipPreset);
        ValidatePreset(defaults.BZip2Preset);
        ValidatePreset(defaults.ZipRebuildPreset);
        return settings with { ManagedCreation = defaults with { } };
    }

    private AppSettingsLoadResult LoadBackupOrFallback(
        AppSettingsLoadState fallbackState,
        string fallbackStatus,
        CancellationToken cancellationToken)
    {
        if (!LocalSettingsFileSafety.TryGetAttributes(_backupPath, out _))
        {
            return Fallback(
                fallbackState,
                fallbackStatus,
                useLastKnownGood: fallbackState != AppSettingsLoadState.Defaults);
        }

        try
        {
            var settings = ReadAndValidate(_backupPath, cancellationToken);
            SetLastKnownGood(settings);
            return new AppSettingsLoadResult(
                settings,
                AppSettingsLoadState.RecoveredFromBackup,
                true,
                RecoveredStatus);
        }
        catch (UnsupportedAppSettingsVersionException)
        {
            return Fallback(fallbackState, fallbackStatus, useLastKnownGood: true);
        }
        catch (Exception exception) when (IsMalformedDocument(exception))
        {
            return Fallback(fallbackState, fallbackStatus, useLastKnownGood: true);
        }
    }

    private AppSettingsLoadResult Fallback(
        AppSettingsLoadState state,
        string status,
        bool useLastKnownGood)
    {
        lock (_lastKnownGoodGate)
        {
            var settings = useLastKnownGood ? _lastKnownGood : AppUiSettings.Default;
            return new AppSettingsLoadResult(settings, state, useLastKnownGood, status);
        }
    }

    private void SetLastKnownGood(AppUiSettings settings)
    {
        lock (_lastKnownGoodGate)
        {
            _lastKnownGood = settings;
        }
    }

    private static byte[] Serialize(AppUiSettings settings)
    {
        var defaults = settings.ManagedCreation;
        var document = new PersistedDocument(
            SchemaVersion,
            defaults.DefaultFormat.ToString(),
            defaults.ZipPreset.ToString(),
            defaults.TarPreset.ToString(),
            defaults.TarGZipPreset.ToString(),
            defaults.TarBZip2Preset.ToString(),
            defaults.GZipPreset.ToString(),
            defaults.BZip2Preset.ToString(),
            defaults.ZipRebuildPreset.ToString(),
            settings.ShowAdvancedRebuildOptions);
        var payload = JsonSerializer.Serialize(document, JsonOptions);
        var bytes = StrictUtf8.GetBytes(payload);
        if (bytes.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The UI settings document exceeds its bounded size.");
        }

        return bytes;
    }

    private static AppUiSettings ReadDocument(ReadOnlySpan<byte> bytes)
    {
        var document = JsonSerializer.Deserialize<PersistedDocument>(bytes, JsonOptions)
            ?? throw new JsonException("The UI settings document is empty.");
        if (document.Version != SchemaVersion)
        {
            throw new UnsupportedAppSettingsVersionException();
        }

        return Validate(new AppUiSettings(
            new ManagedCreationDefaults(
                ParseFormat(document.DefaultCreationFormat),
                ParsePreset(document.DefaultZipPreset),
                ParsePreset(document.DefaultTarPreset),
                ParsePreset(document.DefaultTarGZipPreset),
                ParsePreset(document.DefaultTarBZip2Preset),
                ParsePreset(document.DefaultGZipPreset),
                ParsePreset(document.DefaultBZip2Preset),
                ParsePreset(document.DefaultZipRebuildPreset)),
            document.ShowAdvancedRebuildOptions));
    }

    private static ArchiveCreationFormat ParseFormat(string value)
    {
        ValidateSchemaToken(value);
        if (!Enum.TryParse<ArchiveCreationFormat>(value, ignoreCase: false, out var format)
            || !string.Equals(Enum.GetName(format), value, StringComparison.Ordinal))
        {
            throw new JsonException("The default archive format is invalid.");
        }

        ValidateFormat(format);
        return format;
    }

    private static ArchiveCompressionPreset ParsePreset(string value)
    {
        ValidateSchemaToken(value);
        if (!Enum.TryParse<ArchiveCompressionPreset>(value, ignoreCase: false, out var preset)
            || !string.Equals(Enum.GetName(preset), value, StringComparison.Ordinal))
        {
            throw new JsonException("A compression preset value is invalid.");
        }

        ValidatePreset(preset);
        return preset;
    }

    private static void ValidateSchemaToken(string value)
    {
        if (value.Length == 0
            || value.EnumerateRunes().Count() > MaximumSchemaTokenScalars
            || StrictUtf8.GetByteCount(value) > MaximumSchemaTokenUtf8Bytes)
        {
            throw new JsonException("A UI settings schema token exceeds its bound.");
        }
    }

    private AppUiSettings ReadAndValidate(
        string path,
        CancellationToken cancellationToken)
    {
        using var stream = LocalSettingsFileSafety.OpenVerifiedRead(
            path,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The UI settings document exceeds its bounded size.");
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
            throw new InvalidDataException("The UI settings document exceeds its bounded size.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ReadDocument(bytes.AsSpan(0, total));
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
                // Only a fresh random CreateNew sibling is retried.
            }
        }

        throw new IOException("A unique UI settings staging object could not be created.");
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
                    _lockTimeout);
                if (result == 1)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result == WaitHandle.WaitTimeout)
                {
                    throw new IOException("The local UI settings lock could not be acquired.");
                }
            }
            catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
            {
                // Ownership transfers to this caller. No partial document is
                // trusted because writes use a fresh sibling and replacement.
            }

            return mutex;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

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
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SecurityException
                or NotSupportedException)
        {
            // Never broaden cleanup beyond the exact verified staging object.
        }
    }

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
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var material = StrictUtf8.GetBytes($"{user}\0{Path.GetFullPath(settingsDirectory)}");
        var digest = Convert.ToHexString(SHA256.HashData(material));
        return $"Local\\SailHub.RainArchives.UiSettings.{digest}";
    }

    private static void ValidateFormat(ArchiveCreationFormat format)
    {
        if (format is not (ArchiveCreationFormat.Zip
            or ArchiveCreationFormat.Tar
            or ArchiveCreationFormat.TarGZip
            or ArchiveCreationFormat.TarBZip2
            or ArchiveCreationFormat.GZip
            or ArchiveCreationFormat.BZip2))
        {
            throw new ArgumentException("The default archive format is unsupported.", nameof(format));
        }
    }

    private static void ValidatePreset(ArchiveCompressionPreset preset)
    {
        if (preset is not (ArchiveCompressionPreset.Store
            or ArchiveCompressionPreset.Fast
            or ArchiveCompressionPreset.Balanced
            or ArchiveCompressionPreset.Maximum))
        {
            throw new ArgumentException("A default compression preset is unsupported.", nameof(preset));
        }
    }

    private static bool IsMalformedDocument(Exception exception) =>
        exception is JsonException
            or InvalidDataException
            or DecoderFallbackException
            or ArgumentException
            or OverflowException;

    private static bool IsUnavailableStorage(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException;

    private sealed record PersistedDocument(
        [property: JsonRequired] int Version,
        [property: JsonRequired] string DefaultCreationFormat,
        [property: JsonRequired] string DefaultZipPreset,
        [property: JsonRequired] string DefaultTarPreset,
        [property: JsonRequired] string DefaultTarGZipPreset,
        [property: JsonRequired] string DefaultTarBZip2Preset,
        [property: JsonRequired] string DefaultGZipPreset,
        [property: JsonRequired] string DefaultBZip2Preset,
        [property: JsonRequired] string DefaultZipRebuildPreset,
        [property: JsonRequired] bool ShowAdvancedRebuildOptions);

    private sealed class UnsupportedAppSettingsVersionException : Exception;
}
