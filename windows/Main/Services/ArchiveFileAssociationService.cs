using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RainExplorer.Models;

namespace RainExplorer.Services;

internal enum ArchiveAssociationRegistrationState
{
    NotRegistered,
    Registered,
    NeedsRepair,
    Conflict,
    Unavailable,
}

internal enum ArchiveAssociationRegistrationOutcome
{
    Completed,
    Conflict,
    Failed,
}

internal readonly record struct ArchiveAssociationRegistrationResult(
    ArchiveAssociationRegistrationOutcome Outcome,
    ArchiveAssociationRegistrationState State);

internal interface IArchiveAssociationRegistry
{
    bool KeyExists(string path);
    bool ValueExists(string path, string? name);
    string? GetString(string path, string? name);
    void SetString(string path, string? name, string value);
    void SetNone(string path, string name);
}

/// <summary>
/// Registers Rain Explorer as a candidate archive handler for the current user. Windows
/// remains the sole authority for the actual default: this service never writes UserChoice,
/// never replaces an extension's default value, and never edits another application's keys.
/// </summary>
internal static class ArchiveFileAssociationService
{
    internal const string ProgId = "SailHub.RainExplorer.Archive.2";
    internal const string ProgIdPath = @"Software\Classes\SailHub.RainExplorer.Archive.2";
    internal const string CapabilitiesPath =
        @"Software\SailHub\RainExplorer\Capabilities\ArchiveAssociationsV2";
    internal const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
    internal const string RegisteredApplicationName = "Rain Explorer Archives";
    internal const string OwnerValueName = "RainExplorerOwner";
    internal const string OwnerValue = "SailHub.RainExplorer.ArchiveAssociations.v2";
    internal const string ArchiveIconRelativePath = @"Assets\RainArchive.ico";

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        nint item1,
        nint item2);

    internal static ArchiveAssociationRegistrationState GetRegistrationState()
    {
        try
        {
            return GetRegistrationState(
                new CurrentUserArchiveAssociationRegistry(),
                ShellIntegrationService.ExePath);
        }
        catch
        {
            return ArchiveAssociationRegistrationState.Unavailable;
        }
    }

    internal static ArchiveAssociationRegistrationResult RegisterForCurrentUser()
    {
        try
        {
            var result = RegisterForCurrentUser(
                new CurrentUserArchiveAssociationRegistry(),
                ShellIntegrationService.ExePath);
            if (result.Outcome == ArchiveAssociationRegistrationOutcome.Completed)
            {
                try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, 0, 0); }
                catch { /* registration is durable even if Explorer cannot refresh immediately */ }
            }
            return result;
        }
        catch
        {
            return new(
                ArchiveAssociationRegistrationOutcome.Failed,
                ArchiveAssociationRegistrationState.Unavailable);
        }
    }

    internal static ArchiveAssociationRegistrationState GetRegistrationState(
        IArchiveAssociationRegistry registry,
        string executablePath)
    {
        string exe = NormalizeExecutablePath(executablePath);
        string archiveIcon = NormalizeArchiveIconPath(exe);
        bool progIdExists = registry.KeyExists(ProgIdPath);
        bool capabilitiesExist = registry.KeyExists(CapabilitiesPath);
        string? registeredPath = registry.GetString(
            RegisteredApplicationsPath,
            RegisteredApplicationName);

        if ((progIdExists && !HasOwner(registry, ProgIdPath))
            || (capabilitiesExist && !HasOwner(registry, CapabilitiesPath))
            || (registeredPath is not null
                && !string.Equals(registeredPath, CapabilitiesPath, StringComparison.Ordinal)))
        {
            return ArchiveAssociationRegistrationState.Conflict;
        }

        if (!progIdExists && !capabilitiesExist && registeredPath is null)
            return ArchiveAssociationRegistrationState.NotRegistered;

        if (!progIdExists || !capabilitiesExist
            || !string.Equals(registeredPath, CapabilitiesPath, StringComparison.Ordinal)
            || !MatchesExpectedRegistration(registry, exe, archiveIcon))
        {
            return ArchiveAssociationRegistrationState.NeedsRepair;
        }

        return ArchiveAssociationRegistrationState.Registered;
    }

    internal static ArchiveAssociationRegistrationResult RegisterForCurrentUser(
        IArchiveAssociationRegistry registry,
        string executablePath)
    {
        string exe;
        ArchiveAssociationRegistrationState state;
        try
        {
            exe = NormalizeExecutablePath(executablePath);
            _ = NormalizeArchiveIconPath(exe);
            state = GetRegistrationState(registry, exe);
        }
        catch
        {
            return new(
                ArchiveAssociationRegistrationOutcome.Failed,
                ArchiveAssociationRegistrationState.Unavailable);
        }

        if (state == ArchiveAssociationRegistrationState.Conflict)
        {
            return new(
                ArchiveAssociationRegistrationOutcome.Conflict,
                ArchiveAssociationRegistrationState.Conflict);
        }

        try
        {
            string archiveIcon = NormalizeArchiveIconPath(exe);
            // Ownership markers are written first. Any later partial registration remains
            // repairable, but cannot silently claim a foreign pre-existing identity.
            registry.SetString(ProgIdPath, OwnerValueName, OwnerValue);
            registry.SetString(CapabilitiesPath, OwnerValueName, OwnerValue);

            registry.SetString(ProgIdPath, null, "Rain Explorer archive");
            registry.SetString(ProgIdPath, "FriendlyTypeName", "Rain Explorer archive");
            registry.SetString(
                $@"{ProgIdPath}\DefaultIcon",
                null,
                $"\"{archiveIcon}\",0");
            registry.SetString(
                $@"{ProgIdPath}\shell\open\command",
                null,
                $"\"{exe}\" \"%1\"");

            registry.SetString(CapabilitiesPath, "ApplicationName", "Rain Explorer");
            registry.SetString(
                CapabilitiesPath,
                "ApplicationDescription",
                "Browse supported archives in Rain Explorer using the isolated Rain Archives worker.");
            registry.SetString(CapabilitiesPath, "ApplicationIcon", $"\"{exe}\",0");

            string associationsPath = $@"{CapabilitiesPath}\FileAssociations";
            foreach (string extension in ArchiveFileTypes.AssociationExtensions)
            {
                registry.SetString(associationsPath, extension, ProgId);
                registry.SetNone($@"Software\Classes\{extension}\OpenWithProgids", ProgId);
            }

            // Publish the Default Apps candidate last. Even an interruption before this
            // point changes no current default and leaves only Rain-owned repairable data.
            registry.SetString(
                RegisteredApplicationsPath,
                RegisteredApplicationName,
                CapabilitiesPath);

            ArchiveAssociationRegistrationState finalState =
                GetRegistrationState(registry, exe);
            return finalState == ArchiveAssociationRegistrationState.Registered
                ? new(
                    ArchiveAssociationRegistrationOutcome.Completed,
                    ArchiveAssociationRegistrationState.Registered)
                : new(
                    ArchiveAssociationRegistrationOutcome.Failed,
                    finalState);
        }
        catch
        {
            return new(
                ArchiveAssociationRegistrationOutcome.Failed,
                ArchiveAssociationRegistrationState.NeedsRepair);
        }
    }

    internal static bool OpenWindowsDefaultAppsPage()
    {
        bool useRegisteredAppFilter = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        string preferred = BuildDefaultAppsSettingsUri(useRegisteredAppFilter);
        if (TryOpenSettings(preferred)) return true;
        return string.Equals(preferred, "ms-settings:defaultapps", StringComparison.Ordinal)
            || TryOpenSettings("ms-settings:defaultapps");
    }

    internal static string BuildDefaultAppsSettingsUri(bool useRegisteredAppFilter) =>
        useRegisteredAppFilter
            ? "ms-settings:defaultapps?registeredAppUser="
                + Uri.EscapeDataString(RegisteredApplicationName)
            : "ms-settings:defaultapps";

    private static bool TryOpenSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasOwner(IArchiveAssociationRegistry registry, string path) =>
        string.Equals(
            registry.GetString(path, OwnerValueName),
            OwnerValue,
            StringComparison.Ordinal);

    private static bool MatchesExpectedRegistration(
        IArchiveAssociationRegistry registry,
        string exe,
        string archiveIcon)
    {
        if (!string.Equals(
                registry.GetString($@"{ProgIdPath}\shell\open\command", null),
                $"\"{exe}\" \"%1\"",
                StringComparison.Ordinal)
            || !string.Equals(
                registry.GetString($@"{ProgIdPath}\DefaultIcon", null),
                $"\"{archiveIcon}\",0",
                StringComparison.Ordinal)
            || !string.Equals(
                registry.GetString(CapabilitiesPath, "ApplicationIcon"),
                $"\"{exe}\",0",
                StringComparison.Ordinal))
        {
            return false;
        }

        string associationsPath = $@"{CapabilitiesPath}\FileAssociations";
        foreach (string extension in ArchiveFileTypes.AssociationExtensions)
        {
            if (!string.Equals(
                    registry.GetString(associationsPath, extension),
                    ProgId,
                    StringComparison.Ordinal)
                || !registry.ValueExists(
                    $@"Software\Classes\{extension}\OpenWithProgids",
                    ProgId))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"'))
            throw new ArgumentException("The executable path is invalid.", nameof(executablePath));
        string fullPath = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(fullPath) || !File.Exists(fullPath))
            throw new FileNotFoundException("The Rain Explorer executable was not found.", fullPath);
        return fullPath;
    }

    internal static string NormalizeArchiveIconPath(string executablePath)
    {
        string directory = Path.GetDirectoryName(executablePath)
            ?? throw new ArgumentException(
                "The executable directory is invalid.",
                nameof(executablePath));
        string iconPath = Path.GetFullPath(Path.Combine(directory, ArchiveIconRelativePath));
        if (!File.Exists(iconPath))
            throw new FileNotFoundException("The Rain Explorer archive icon was not found.", iconPath);
        return iconPath;
    }

    private sealed class CurrentUserArchiveAssociationRegistry : IArchiveAssociationRegistry
    {
        public bool KeyExists(string path)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key is not null;
        }

        public bool ValueExists(string path, string? name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValueNames().Contains(name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                == true;
        }

        public string? GetString(string path, string? name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValue(
                name,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        public void SetString(string path, string? name, string value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public void SetNone(string path, string name)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue(name, Array.Empty<byte>(), RegistryValueKind.None);
        }
    }
}
