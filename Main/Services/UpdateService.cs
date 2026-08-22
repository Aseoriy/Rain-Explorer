using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace RainExplorer.Services;

/// <summary>Details of a release that's newer than the running build.</summary>
public sealed record UpdateInfo(
    string Version, string Tag, bool IsPrerelease,
    string Notes, string? DownloadUrl, string HtmlUrl, string? AssetName);

/// <summary>
/// Checks GitHub Releases for a newer version of Rain Explorer and (optionally) downloads
/// and launches the installer — a lightweight auto-updater in the spirit of Sail Launcher.
///
/// Version comparison understands semantic-ish tags including pre-release suffixes
/// ("1.2.0", "1.2.0-beta", "5.0.0-beta-2"): a plain release outranks a pre-release of the
/// same core version, and numbered pre-releases order by their trailing number. Only tags
/// whose suffix is specifically "beta" are ever offered as an update (gated by the "beta
/// updates" toggle); other suffixes (e.g. "-Pre") are internal dev tags and are always
/// skipped, since they're not meant to reach end users through the updater.
/// </summary>
public static class UpdateService
{
    private const string Owner = "Aseoriy";
    private const string Repo = "Rain-Explorer";
    private static readonly string ReleasesApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub requires a User-Agent; without one the API returns 403.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RainExplorer-Updater/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>The running build's version string (e.g. "1.1.0"), stripped of any "+hash".</summary>
    public static string CurrentVersionString
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.2.1" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Query GitHub and return the newest release that's strictly newer than the running
    /// build, or null if we're up to date (or the check failed / found nothing usable).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(bool includeBeta, CancellationToken ct = default)
    {
        string json;
        try { json = await Http.GetStringAsync(ReleasesApi, ct); }
        catch { return null; }   // offline / rate-limited / API change — fail silently

        UpdateInfo? best = null;
        var bestVer = Parse(CurrentVersionString);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                bool githubPrerelease = rel.TryGetProperty("prerelease", out var p)
                    && p.ValueKind == JsonValueKind.True;

                string tag = Str(rel, "tag_name");
                if (string.IsNullOrWhiteSpace(tag)) continue;

                var ver = Parse(tag);
                bool hasBetaTag = ver.HasPre
                    && ver.PreLabel.Equals("beta", StringComparison.OrdinalIgnoreCase);
                bool isPrerelease = githubPrerelease || ver.HasPre;

                // Only the "-beta" channel is ever offered as an update. Other pre-release
                // suffixes (e.g. "-Pre") are internal dev/test tags, not public builds — never
                // surface them regardless of the Beta updates toggle.
                if (ver.HasPre && !hasBetaTag) continue;
                if (isPrerelease && !includeBeta) continue;

                if (Compare(ver, bestVer) <= 0) continue;   // not newer than current best

                (string? url, string? assetName) = PickInstallerAsset(rel);
                best = new UpdateInfo(
                    Version: Normalize(tag),
                    Tag: tag,
                    IsPrerelease: isPrerelease,
                    Notes: Str(rel, "body"),
                    DownloadUrl: url,
                    HtmlUrl: Str(rel, "html_url"),
                    AssetName: assetName);
                bestVer = ver;
            }
        }
        catch { return null; }

        return best;
    }

    /// <summary>Download the update's installer to a temp file, reporting 0..1 progress.
    /// Returns the local path, or null on failure/cancel.</summary>
    public static async Task<string?> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out Uri? downloadUri)
            || !IsTrustedReleaseUrl(downloadUri, allowAssetHost: false))
            return null;

        string? partial = null;
        try
        {
            string name = SafeAssetName(info.AssetName)
                ?? $"RainExplorer-Setup-{SafeVersionName(info.Version)}.exe";
            string dir = Path.Combine(Path.GetTempPath(), "RainExplorerUpdate");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, name);
            string partialPath = $"{dest}.{Guid.NewGuid():N}.download";
            partial = partialPath;

            using (HttpResponseMessage resp = await Http.GetAsync(
                downloadUri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (resp.RequestMessage?.RequestUri is not Uri responseUri
                    || !IsTrustedReleaseUrl(responseUri, allowAssetHost: true))
                    return null;

                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;

                await using (Stream src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var fs = new FileStream(
                    partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                        read += n;
                        if (total is > 0) progress?.Report((double)read / total.Value);
                    }
                }
            }

            // The staged stream is closed before replacing the destination. This is
            // required on Windows because the file is opened with FileShare.None.
            File.Move(partialPath, dest, overwrite: true);
            partial = null;
            progress?.Report(1.0);
            return dest;
        }
        catch
        {
            // Never leave a truncated installer behind for a later launch to pick up.
            if (partial is not null)
            {
                try { File.Delete(partial); } catch { }
            }
            return null;
        }
    }

    /// <summary>Launch the downloaded installer and close this instance so it can replace files.</summary>
    public static void RunInstallerAndExit(string installerPath)
    {
        try { Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true }); }
        catch { return; }
        Application.Current?.Shutdown();
    }

    /// <summary>Open a URL (e.g. the release page) in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ===================== JSON helpers =====================
    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // Prefer the .exe installer asset (its name usually contains "Setup").
    private static (string? url, string? name) PickInstallerAsset(JsonElement rel)
    {
        if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        string? firstExe = null, firstExeName = null;
        foreach (var a in assets.EnumerateArray())
        {
            string name = Str(a, "name");
            string url = Str(a, "browser_download_url");
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || url.Length == 0) continue;
            if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) return (url, name);
            firstExe ??= url;
            firstExeName ??= name;
        }
        return (firstExe, firstExeName);
    }

    private static string? SafeAssetName(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return null;
        string name = assetName.Trim();
        if (name is "." or ".."
            || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return name;
    }

    private static string SafeVersionName(string version)
    {
        string name = Normalize(version);
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "latest" : name;
    }

    private static bool IsTrustedReleaseUrl(Uri uri, bool allowAssetHost)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps) return false;

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            string expectedPrefix = $"/{Owner}/{Repo}/releases/download/";
            return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        if (!allowAssetHost) return false;
        return uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    // ===================== Version parsing / comparison =====================

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
            // Split the pre-release into a leading label and a trailing number, allowing
            // separators like "beta-2", "beta.2" or "beta2".
            int i = pre.Length;
            while (i > 0 && char.IsDigit(pre[i - 1])) i--;
            if (i < pre.Length && int.TryParse(pre.AsSpan(i), out num)) hasNum = true;
            label = pre[..i].TrimEnd('-', '.', '_');
        }
        return new Ver(major, minor, patch, label, num, hasPre, hasNum);
    }

    private static int PartInt(string[] parts, int i) =>
        i < parts.Length && int.TryParse(parts[i], out int v) ? v : 0;

    /// <summary>Returns &gt;0 if a is newer than b, &lt;0 if older, 0 if equal.</summary>
    private static int Compare(Ver a, Ver b)
    {
        int c = a.Major.CompareTo(b.Major); if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor); if (c != 0) return c;
        c = a.Patch.CompareTo(b.Patch); if (c != 0) return c;

        // A full release outranks a pre-release of the same core version.
        if (a.HasPre != b.HasPre) return a.HasPre ? -1 : 1;
        if (!a.HasPre) return 0;

        c = string.Compare(a.PreLabel, b.PreLabel, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;

        // Same label: a numbered pre-release (beta-2) outranks an unnumbered one (beta).
        if (a.HasPreNum != b.HasPreNum) return a.HasPreNum ? 1 : -1;
        return a.PreNum.CompareTo(b.PreNum);
    }
}
