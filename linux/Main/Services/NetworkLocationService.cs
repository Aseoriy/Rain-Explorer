using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RainExplorer.Services;

/// <summary>Credentials typed into Rain's sign-in dialog. Used once for <c>gio mount</c>, never stored.</summary>
public sealed record NetworkCredentials(string User, string Domain, string Password, bool Anonymous);

public enum NetworkConnectOutcome { Connected, NeedsCredentials, Failed }

public sealed record NetworkConnectResult(NetworkConnectOutcome Outcome, string? LocalPath = null, string? Error = null,
    string? DefaultUser = null, string? DefaultDomain = null, bool AsksDomain = false);

/// <summary>
/// Network shares through GVfs, the desktop's own network layer (what Nautilus, Nemo and the GTK file pickers
/// use). The Windows build browses UNC paths; here the address bar takes <c>smb://</c>, <c>sftp://</c>,
/// <c>ftp://</c>, <c>dav://</c>… URIs, mounts them with <c>gio mount</c> and opens the FUSE path under
/// <c>/run/user/UID/gvfs</c>, so every file operation keeps working on a normal local path. Sign-in prompts
/// from gio are answered from Rain's dialog over stdin, so a password never appears on a command line.
/// </summary>
public static class NetworkLocationService
{
    private static readonly string[] Schemes = ["smb", "sftp", "ssh", "ftp", "ftps", "dav", "davs", "webdav", "webdavs", "nfs", "afp"];
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex UserPrompt = new(@"User \[(?<d>[^\]]*)\]: $", RegexOptions.CultureInvariant);
    private static readonly Regex DomainPrompt = new(@"Domain \[(?<d>[^\]]*)\]: $", RegexOptions.CultureInvariant);

    public static bool IsAvailable => ToolRunner.IsAvailable("gio");

    /// <summary>True for text like <c>smb://server/share</c> that GVfs can mount.</summary>
    public static bool IsNetworkUri(string text)
    {
        int colon = text.IndexOf("://", StringComparison.Ordinal);
        if (colon <= 0) return false;
        string scheme = text[..colon].ToLowerInvariant();
        return Schemes.Contains(scheme) && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0;
    }

    /// <summary>An SMB URI that names only a server (no share), which can be browsed for its shares.</summary>
    public static bool IsSmbServerRoot(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri? u)
        && u.Scheme.Equals("smb", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.Trim('/').Length == 0;

    /// <summary>
    /// Mount the location (if needed) and return its local path. Without <paramref name="credentials"/> a
    /// sign-in request comes back as <see cref="NetworkConnectOutcome.NeedsCredentials"/>.
    /// </summary>
    public static async Task<NetworkConnectResult> ConnectAsync(string uri, NetworkCredentials? credentials, CancellationToken ct)
    {
        if (!IsAvailable)
            return new(NetworkConnectOutcome.Failed, Error: "Network locations need GVfs (the gio command, package gvfs-bin or glib2).");

        if (await LocalPathAsync(uri, ct).ConfigureAwait(false) is { } existing)
            return new(NetworkConnectOutcome.Connected, existing);

        var mount = await MountAsync(uri, credentials, ct).ConfigureAwait(false);
        if (mount.Outcome != NetworkConnectOutcome.Connected) return mount;
        // A server address (smb://nas) mounts GVfs's share list, which has no folder on disk; the caller
        // lists its shares and connects to the one picked.
        if (IsSmbServerRoot(uri)) return new(NetworkConnectOutcome.Connected);

        string? local = await LocalPathAsync(uri, ct).ConfigureAwait(false);
        return local is null
            ? new(NetworkConnectOutcome.Failed, Error: "Connected, but this location has no folder Rain can open. If it is a server, add a share name (smb://server/share).")
            : new(NetworkConnectOutcome.Connected, local);
    }

    /// <summary>The share names on an SMB server (after it was mounted for browsing).</summary>
    public static async Task<IReadOnlyList<string>> ListSharesAsync(string serverUri, CancellationToken ct)
    {
        var result = await ToolRunner.RunAsync("gio", ["list", serverUri.TrimEnd('/') + "/"], QueryTimeout, ct)
            .ConfigureAwait(false);
        if (!result.Succeeded) return [];
        return result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !name.EndsWith('$'))   // administrative shares
            .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Unmount the GVfs location that <paramref name="localPath"/> belongs to.</summary>
    public static async Task<string?> DisconnectAsync(string localPath, CancellationToken ct = default)
    {
        string? uri = await UriForLocalPathAsync(localPath, ct).ConfigureAwait(false);
        if (uri is null) return "Couldn't find the network location for that folder.";
        var result = await ToolRunner.RunAsync("gio", ["mount", "-u", uri], QueryTimeout, ct).ConfigureAwait(false);
        return result.Succeeded ? null : FirstLine(result.Error) ?? "Couldn't disconnect the network location.";
    }

    /// <summary>GVfs mounts under /run/user/UID/gvfs, as drive entries.</summary>
    public static IReadOnlyList<MountEntry> GvfsMounts()
    {
        string root = GvfsRoot;
        var list = new List<MountEntry>();
        try
        {
            if (!Directory.Exists(root)) return list;
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (DescribeGvfsMount(name) is not { } label) continue;
                list.Add(new MountEntry(dir, name, "gvfs", label, IsRoot: false, IsRemovable: false, IsNetwork: true)
                {
                    IsGvfs = true,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>The GVfs location containing <paramref name="path"/>, or null for ordinary folders.</summary>
    public static MountEntry? FindGvfsMount(string path)
    {
        string root = GvfsRoot;
        if (!PathUtil.IsSameOrInside(root, path) || PathUtil.Same(root, path)) return null;
        string relative = Path.GetRelativePath(root, path);
        string first = relative.Split('/', 2)[0];
        string mountPoint = Path.Combine(root, first);
        return DescribeGvfsMount(first) is { } label
            ? new MountEntry(mountPoint, first, "gvfs", label, IsRoot: false, IsRemovable: false, IsNetwork: true) { IsGvfs = true }
            : null;
    }

    internal static string GvfsRoot
    {
        get
        {
            string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r
                ? r : $"/run/user/{TrashService.CurrentUid.ToString(CultureInfo.InvariantCulture)}";
            return Path.Combine(runtime, "gvfs");
        }
    }

    /// <summary>"share on server" for a GVfs mount folder name like <c>smb-share:server=nas,share=media</c>.</summary>
    internal static string? DescribeGvfsMount(string folderName)
    {
        int colon = folderName.IndexOf(':');
        if (colon <= 0) return null;
        string kind = folderName[..colon];
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in folderName[(colon + 1)..].Split(','))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) parts[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        string? host = parts.GetValueOrDefault("server") ?? parts.GetValueOrDefault("host");
        if (host is null) return null;
        if (parts.TryGetValue("share", out string? share)) return $"{share} on {host}";
        string scheme = kind switch
        {
            "sftp" => "SFTP",
            "ftp" => "FTP",
            "ftps" => "FTPS",
            "dav" => parts.GetValueOrDefault("ssl") == "true" ? "WebDAV (secure)" : "WebDAV",
            "smb-server" => "Windows shares",
            _ => kind,
        };
        return parts.TryGetValue("user", out string? user) ? $"{user}@{host} ({scheme})" : $"{host} ({scheme})";
    }

    // ---- gio plumbing -----------------------------------------------------------------------------

    private static async Task<string?> LocalPathAsync(string uri, CancellationToken ct)
    {
        var result = await ToolRunner.RunAsync("gio", ["info", "-a", "standard::type", uri], QueryTimeout, ct)
            .ConfigureAwait(false);
        if (!result.Succeeded) return null;
        string? local = ReadField(result.Text, "local path");
        return local is not null && Directory.Exists(local) ? local : null;
    }

    private static async Task<string?> UriForLocalPathAsync(string localPath, CancellationToken ct)
    {
        var result = await ToolRunner.RunAsync("gio", ["info", "-a", "standard::type", localPath], QueryTimeout, ct)
            .ConfigureAwait(false);
        return result.Succeeded ? ReadField(result.Text, "uri") : null;
    }

    internal static string? ReadField(string gioInfo, string field)
    {
        string prefix = field + ": ";
        foreach (string line in gioInfo.Split('\n'))
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..].Trim();
        return null;
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>
    /// Run <c>gio mount</c>, answering its User / Domain / Password prompts. Without credentials the first
    /// prompt ends the attempt and reports which fields are wanted.
    /// </summary>
    private static async Task<NetworkConnectResult> MountAsync(string uri, NetworkCredentials? credentials, CancellationToken ct)
    {
        string? gio = ProcessLauncher.FindExecutable("gio");
        if (gio is null) return new(NetworkConnectOutcome.Failed, Error: "The gio command is not installed.");

        var start = new ProcessStartInfo(gio)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = XdgDirs.Home,
        };
        start.ArgumentList.Add("mount");
        if (credentials?.Anonymous == true) start.ArgumentList.Add("-a");
        start.ArgumentList.Add(uri);
        start.Environment["LC_ALL"] = "C";   // the prompts are matched in English

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) { return new(NetworkConnectOutcome.Failed, Error: ex.Message); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MountTimeout);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        var output = new StringBuilder();
        char[] buffer = new char[512];
        bool askedUser = false, askedDomain = false, askedPassword = false;
        string? defaultUser = null, defaultDomain = null;
        try
        {
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                output.Append(buffer, 0, read);
                string text = output.ToString();
                string? answer = null;
                bool repeated = false;
                if (UserPrompt.Match(text) is { Success: true } user)
                {
                    repeated = askedUser;
                    askedUser = true;
                    defaultUser = user.Groups["d"].Value;
                    answer = credentials?.User;
                }
                else if (DomainPrompt.Match(text) is { Success: true } domain)
                {
                    repeated = askedDomain;
                    askedDomain = true;
                    defaultDomain = domain.Groups["d"].Value;
                    answer = credentials?.Domain;
                }
                else if (text.EndsWith("Password: ", StringComparison.Ordinal))
                {
                    repeated = askedPassword;
                    askedPassword = true;
                    answer = credentials?.Password;
                }
                else if (Regex.IsMatch(text, @"\[\d+\] [^\n]*\n\s*$|Choice: $"))
                {
                    // A question such as an unknown SSH host key. Rain doesn't answer it on the user's behalf.
                    Kill(process);
                    return new(NetworkConnectOutcome.Failed,
                        Error: "The server asked a question Rain can't answer here (for example a new SSH host key). "
                               + "Connect once with ssh or your desktop's file manager, then try again.");
                }
                else continue;

                if (repeated && credentials is not null)
                {
                    // Asked again: the server rejected what was entered.
                    Kill(process);
                    return new(NetworkConnectOutcome.Failed, Error: "Sign-in failed. Check the user name and password.");
                }
                if (credentials is null)
                {
                    // Gather which fields are asked for, then stop: the dialog collects them.
                    await process.StandardInput.WriteLineAsync(string.Empty.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (askedPassword) { Kill(process); break; }
                    continue;
                }
                await process.StandardInput.WriteLineAsync((answer ?? string.Empty).AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                output.Clear();
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            ct.ThrowIfCancellationRequested();
            return new(NetworkConnectOutcome.Failed, Error: "The server took too long to respond.");
        }

        string error = FirstLine(await SafeAsync(stderr).ConfigureAwait(false)) ?? string.Empty;
        bool exited = process.HasExited;
        if (exited && process.ExitCode == 0 && !(credentials is null && (askedUser || askedPassword)))
            return new(NetworkConnectOutcome.Connected);
        if (credentials is null && (askedUser || askedPassword))
            return new(NetworkConnectOutcome.NeedsCredentials, DefaultUser: defaultUser, DefaultDomain: defaultDomain,
                AsksDomain: askedDomain);
        if (error.Contains("already mounted", StringComparison.OrdinalIgnoreCase))
            return new(NetworkConnectOutcome.Connected);
        return new(NetworkConnectOutcome.Failed,
            Error: CleanError(error) ?? (credentials is not null ? "Sign-in failed. Check the user name and password." : "Couldn't connect."));
    }

    private static string? CleanError(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        // "gio: smb://x/: Failed to mount Windows share: …" → the reason only.
        int idx = error.LastIndexOf(": ", StringComparison.Ordinal);
        return idx > 0 && idx < error.Length - 2 ? error[(idx + 2)..] : error;
    }

    private static async Task<string> SafeAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
