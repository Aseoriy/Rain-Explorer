using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace RainExplorer.Services;

/// <summary>How this copy of Rain was installed, which decides what "update" means.</summary>
public enum InstallKind
{
    /// <summary>An AppImage: the file is replaced in place and Rain restarts.</summary>
    AppImage,
    /// <summary>The .deb in /opt/rain-explorer: the new package is handed to the system installer.</summary>
    Deb,
    /// <summary>Tarball install (~/.local) or a development run: the release page is opened.</summary>
    Manual,
}

/// <summary>Details of a release that's newer than the running build.</summary>
public sealed record UpdateInfo(
    string Version, string Tag, bool IsPrerelease, string Notes, string HtmlUrl,
    string? DownloadUrl, string? AssetName, string? ChecksumUrl);

/// <summary>
/// Checks GitHub Releases for a newer Rain Explorer, ported from the Windows build's UpdateService (same tag
/// rules: plain releases outrank pre-releases, only "-beta" pre-releases are offered and only with the beta
/// toggle on). Windows and Linux share release tags from 2.0.0 on, so a release is only offered here when it
/// carries a Linux package for this architecture and install kind. Downloads are verified against the
/// release's .sha256 file when there is one.
/// </summary>
public static class UpdateService
{
    private const string Owner = "Aseoriy";
    private const string Repo = "Rain-Explorer";
    private static readonly string ReleasesApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub requires a User-Agent; without one the API returns 403.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RainExplorer-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The running build's version (e.g. "2.0.0-beta-2"), without "+hash" metadata.</summary>
    public static string CurrentVersionString
    {
        get
        {
            string? info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static InstallKind CurrentInstallKind =>
        DetectInstallKind(Environment.GetEnvironmentVariable("APPIMAGE"), AppContext.BaseDirectory);

    internal static InstallKind DetectInstallKind(string? appImage, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage)) return InstallKind.AppImage;
        string dir = baseDirectory.TrimEnd('/') + "/";
        return dir.StartsWith("/opt/rain-explorer/", StringComparison.Ordinal) ? InstallKind.Deb : InstallKind.Manual;
    }

    private static string ArchSuffix => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    private static string DebArch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

    /// <summary>
    /// The newest release strictly newer than this build that has a Linux package, or null when up to date,
    /// offline or the API answer is unusable.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(bool includeBeta, CancellationToken ct = default)
    {
        string json;
        try { json = await Http.GetStringAsync(ReleasesApi, ct).ConfigureAwait(false); }
        catch { return null; }
        return PickUpdate(json, CurrentVersionString, includeBeta, CurrentInstallKind, ArchSuffix, DebArch);
    }

    internal static UpdateInfo? PickUpdate(string json, string currentVersion, bool includeBeta, InstallKind kind,
        string archSuffix, string debArch)
    {
        UpdateInfo? best = null;
        Ver bestVer = Parse(currentVersion);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                bool githubPrerelease = rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
                string tag = Str(rel, "tag_name");
                if (string.IsNullOrWhiteSpace(tag)) continue;

                Ver ver = Parse(tag);
                bool hasBetaTag = ver.HasPre && ver.PreLabel.Equals("beta", StringComparison.OrdinalIgnoreCase);
                bool isPrerelease = githubPrerelease || ver.HasPre;
                // Only the "-beta" channel is ever offered; other suffixes ("-Pre", "-linux-alpha") are not.
                if (ver.HasPre && !hasBetaTag) continue;
                if (isPrerelease && !includeBeta) continue;
                if (Compare(ver, bestVer) <= 0) continue;

                var (url, name, checksum) = PickLinuxAsset(rel, kind, archSuffix, debArch);
                if (url is null) continue;   // a Windows-only release: nothing to install here
                best = new UpdateInfo(Normalize(tag), tag, isPrerelease, Str(rel, "body"), Str(rel, "html_url"),
                    kind == InstallKind.Manual ? null : url, name, checksum);
                bestVer = ver;
            }
        }
        catch { return null; }
        return best;
    }

    private static (string? Url, string? Name, string? Checksum) PickLinuxAsset(
        JsonElement rel, InstallKind kind, string archSuffix, string debArch)
    {
        if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null, null);
        string? appImage = null, appImageName = null, deb = null, debName = null, tar = null, tarName = null, sums = null;
        foreach (var a in assets.EnumerateArray())
        {
            string name = Str(a, "name");
            string url = Str(a, "browser_download_url");
            if (url.Length == 0) continue;
            if (name.EndsWith($"-linux-{archSuffix}.AppImage", StringComparison.Ordinal)) (appImage, appImageName) = (url, name);
            else if (name.EndsWith($"_{debArch}.deb", StringComparison.Ordinal)) (deb, debName) = (url, name);
            else if (name.EndsWith($"-linux-{archSuffix}.tar.gz", StringComparison.Ordinal)) (tar, tarName) = (url, name);
            else if (name.EndsWith($"-linux-{archSuffix}.sha256", StringComparison.Ordinal)) sums = url;
        }
        return kind switch
        {
            InstallKind.AppImage => (appImage, appImageName, sums),
            InstallKind.Deb => (deb, debName, sums),
            // Manual installs just need to know a Linux build exists; the release page is opened.
            _ => (appImage ?? deb ?? tar, appImageName ?? debName ?? tarName, sums),
        };
    }

    /// <summary>
    /// Download the update next to <paramref name="directory"/> (staged, then renamed), verify its checksum
    /// and return the final path. Null on failure, cancellation or checksum mismatch.
    /// </summary>
    public static async Task<string?> DownloadAsync(UpdateInfo info, string directory, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out Uri? uri) || !IsTrustedReleaseUrl(uri, allowAssetHost: false))
            return null;
        string? name = SafeAssetName(info.AssetName);
        if (name is null) return null;

        string dest = Path.Combine(directory, name);
        string partial = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.download");
        try
        {
            Directory.CreateDirectory(directory);
            using (HttpResponseMessage resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (resp.RequestMessage?.RequestUri is not Uri final || !IsTrustedReleaseUrl(final, allowAssetHost: true))
                    return null;
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total is > 0) progress?.Report((double)read / total.Value);
                }
            }

            if (!await ChecksumMatchesAsync(info, partial, name, ct).ConfigureAwait(false))
            {
                File.Delete(partial);
                return null;
            }
            File.Move(partial, dest, overwrite: true);
            progress?.Report(1.0);
            return dest;
        }
        catch
        {
            // Never leave a truncated package behind.
            try { File.Delete(partial); } catch { }
            return null;
        }
    }

    /// <summary>True when the release has no checksum file, or the file lists this asset with a matching hash.</summary>
    private static async Task<bool> ChecksumMatchesAsync(UpdateInfo info, string file, string assetName, CancellationToken ct)
    {
        if (!Uri.TryCreate(info.ChecksumUrl, UriKind.Absolute, out Uri? uri) || !IsTrustedReleaseUrl(uri, allowAssetHost: false))
            return true;
        string sums = await Http.GetStringAsync(uri, ct).ConfigureAwait(false);
        string? expected = ExpectedHash(sums, assetName);
        if (expected is null) return true;
        await using var stream = File.OpenRead(file);
        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hash for <paramref name="assetName"/> in sha256sum output. GitHub renames "~" in uploaded file
    /// names to ".", so both spellings match.
    /// </summary>
    internal static string? ExpectedHash(string sums, string assetName)
    {
        static string Canon(string s) => s.Replace('~', '.');
        foreach (string raw in sums.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space != 64) continue;
            string fileName = line[space..].TrimStart(' ', '*');
            if (Canon(fileName) == Canon(assetName)) return line[..64].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Replace the running AppImage with the downloaded one (same folder, atomic rename) and keep the
    /// executable bit. Returns an error message, or null on success.
    /// </summary>
    public static string? ReplaceAppImage(string downloaded, string appImage)
    {
        try
        {
            File.SetUnixFileMode(downloaded, File.GetUnixFileMode(appImage) | UnixFileMode.UserExecute);
            File.Move(downloaded, appImage, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Start the (replaced) AppImage; the caller then shuts this instance down.</summary>
    public static void Relaunch(string appImage)
    {
        var start = new ProcessStartInfo(appImage) { UseShellExecute = false, WorkingDirectory = XdgDirs.Home };
        ProcessLauncher.StartDetached(start);
    }

    public static void OpenUrl(string url)
    {
        try { ProcessLauncher.OpenDefault(url); } catch { }
    }

    // ===================== helpers (shared with the Windows build) =====================

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? SafeAssetName(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return null;
        string name = assetName.Trim();
        if (name is "." or ".." || name.StartsWith('.') || Path.GetFileName(name) != name || name.Contains('\0'))
            return null;
        return name;
    }

    private static bool IsTrustedReleaseUrl(Uri uri, bool allowAssetHost)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);
        if (!allowAssetHost) return false;
        return uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Ver(int Major, int Minor, int Patch, string PreLabel, int PreNum, bool HasPre, bool HasPreNum);

    private static string Normalize(string tag) => tag.TrimStart('v', 'V').Trim();

    private static Ver Parse(string tag)
    {
        string s = Normalize(tag);
        string core = s, pre = "";
        int dash = s.IndexOf('-');
        if (dash >= 0) { core = s[..dash]; pre = s[(dash + 1)..]; }
        var parts = core.Split('.');
        int major = PartInt(parts, 0), minor = PartInt(parts, 1), patch = PartInt(parts, 2);
        bool hasPre = pre.Length > 0;
        string label = pre;
        int num = 0;
        bool hasNum = false;
        if (hasPre)
        {
            // "beta-2", "beta.2" and "beta2" all mean label "beta", number 2.
            int i = pre.Length;
            while (i > 0 && char.IsDigit(pre[i - 1])) i--;
            if (i < pre.Length && int.TryParse(pre.AsSpan(i), out num)) hasNum = true;
            label = pre[..i].TrimEnd('-', '.', '_');
        }
        return new Ver(major, minor, patch, label, num, hasPre, hasNum);
    }

    private static int PartInt(string[] parts, int i) => i < parts.Length && int.TryParse(parts[i], out int v) ? v : 0;

    /// <summary>&gt;0 if a is newer than b, &lt;0 if older, 0 if equal.</summary>
    private static int Compare(Ver a, Ver b)
    {
        int c = a.Major.CompareTo(b.Major); if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor); if (c != 0) return c;
        c = a.Patch.CompareTo(b.Patch); if (c != 0) return c;
        if (a.HasPre != b.HasPre) return a.HasPre ? -1 : 1;   // a release outranks its pre-releases
        if (!a.HasPre) return 0;
        c = string.Compare(a.PreLabel, b.PreLabel, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        if (a.HasPreNum != b.HasPreNum) return a.HasPreNum ? 1 : -1;
        return a.PreNum.CompareTo(b.PreNum);
    }
}
