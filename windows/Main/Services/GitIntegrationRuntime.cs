using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Win32;
using RainExplorer.Models;

namespace RainExplorer.Services;

public sealed class GitIntegrationRuntime
{
    public static GitIntegrationRuntime Instance { get; } = new();

    public GitExecutableLocator ExecutableLocator { get; }
    public GitRepositoryLocator RepositoryLocator { get; }
    public GitStatusReader StatusReader { get; }
    public GitPushPreflightService PushPreflight { get; }
    public GitBranchService Branches { get; }
    public GitCloneService Clone { get; }
    public GitMutationService Mutations { get; }

    private GitIntegrationRuntime()
    {
        var runner = new GitProcessRunner();
        ExecutableLocator = new GitExecutableLocator(runner);
        var remotes = new GitRemoteService(runner, ExecutableLocator);
        RepositoryLocator = new GitRepositoryLocator(runner, ExecutableLocator);
        StatusReader = new GitStatusReader(runner, ExecutableLocator, remotes);
        PushPreflight = new GitPushPreflightService(runner, ExecutableLocator);
        Branches = new GitBranchService(runner, ExecutableLocator);
        Clone = new GitCloneService(runner, ExecutableLocator);
        Mutations = new GitMutationService(runner, ExecutableLocator);
    }
}

public sealed class GitExecutableLocator
{
    private static readonly Version MinimumVersion = new(2, 40);
    private readonly IGitProcessRunner _runner;
    private GitInstallationInfo? _cached;
    private string? _cachedSetting;

    public GitExecutableLocator(IGitProcessRunner runner) => _runner = runner;

    public async Task<GitInstallationInfo?> FindAsync(CancellationToken cancellationToken = default)
    {
        string configured = SettingsStore.Instance.Settings.GitExecutablePath.Trim();
        if (_cached is not null && string.Equals(configured, _cachedSetting, StringComparison.OrdinalIgnoreCase))
            return _cached;

        foreach (string candidate in Candidates(configured).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.IsPathFullyQualified(candidate) && !File.Exists(candidate)) continue;

            string working = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var result = await _runner.RunAsync(new GitProcessRequest(
                candidate, working, ["--version"], ReadOnly: true), cancellationToken);
            if (!result.Succeeded) continue;

            string text = result.StandardOutputText.Trim();
            Version version = ParseVersion(text);
            _cachedSetting = configured;
            return _cached = new GitInstallationInfo(
                candidate, version, version >= MinimumVersion, text);
        }

        _cachedSetting = configured;
        _cached = null;
        return null;
    }

    private static IEnumerable<string> Candidates(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        yield return "git.exe";

        string? registry = ReadRegistryPath(Registry.CurrentUser)
            ?? ReadRegistryPath(Registry.LocalMachine);
        if (!string.IsNullOrWhiteSpace(registry))
            yield return Path.Combine(registry, "cmd", "git.exe");

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0) yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) yield return Path.Combine(local, "Programs", "Git", "cmd", "git.exe");
    }

    private static string? ReadRegistryPath(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\GitForWindows");
            return key?.GetValue("InstallPath") as string;
        }
        catch { return null; }
    }

    private static Version ParseVersion(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success) return new Version(0, 0);
        int major = int.Parse(match.Groups[1].Value);
        int minor = int.Parse(match.Groups[2].Value);
        int patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(major, minor, patch);
    }
}

public sealed class GitRepositoryLocator
{
    private readonly IGitProcessRunner _runner;
    private readonly GitExecutableLocator _locator;
    private readonly ConcurrentDictionary<string, (DateTime Expires, GitRepositoryContext? Context)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public GitRepositoryLocator(IGitProcessRunner runner, GitExecutableLocator locator)
    {
        _runner = runner;
        _locator = locator;
    }

    public async Task<GitRepositoryContext?> FindAsync(
        string path, CancellationToken cancellationToken = default, bool useCache = true)
    {
        string candidate = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;
        if (!Directory.Exists(candidate)) return null;
        candidate = Path.GetFullPath(candidate);

        if (useCache && _cache.TryGetValue(candidate, out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Context;

        var installation = await _locator.FindAsync(cancellationToken);
        if (installation is null || !installation.IsSupported) return null;

        var result = await _runner.RunAsync(new GitProcessRequest(
            installation.ExecutablePath,
            candidate,
            ["-c", "core.fsmonitor=false", "-C", candidate, "rev-parse",
             "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir",
             "--is-inside-work-tree"],
            ReadOnly: true), cancellationToken);
        if (!result.Succeeded) return Cache(candidate, null);

        string[] lines = result.StandardOutputText
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 4 || !string.Equals(lines[^1], "true", StringComparison.OrdinalIgnoreCase))
            return Cache(candidate, null);

        string root = Path.GetFullPath(lines[0]);
        string gitDir = Absolute(lines[1], root);
        string commonDir = Absolute(lines[2], root);

        var superResult = await _runner.RunAsync(new GitProcessRequest(
            installation.ExecutablePath,
            root,
            ["-C", root, "rev-parse", "--show-superproject-working-tree"],
            ReadOnly: true), cancellationToken);
        string? super = superResult.Succeeded
            ? superResult.StandardOutputText.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(super)) super = null;

        var context = new GitRepositoryContext(
            root, gitDir, commonDir, super, DetectOperation(gitDir, commonDir));
        return Cache(candidate, context);
    }

    public void Invalidate(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (string key in _cache.Keys.Where(key =>
                     IsSameOrDescendant(key, full) || IsSameOrDescendant(full, key)))
            _cache.TryRemove(key, out _);
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private GitRepositoryContext? Cache(string candidate, GitRepositoryContext? context)
    {
        _cache[candidate] = (DateTime.UtcNow.AddSeconds(3), context);
        return context;
    }

    private static string Absolute(string path, string root) =>
        Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, root);

    private static GitRepositoryOperationState DetectOperation(string gitDir, string commonDir)
    {
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return GitRepositoryOperationState.Merge;
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge"))
            || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            return GitRepositoryOperationState.Rebase;
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            return GitRepositoryOperationState.CherryPick;
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
            return GitRepositoryOperationState.Revert;
        if (File.Exists(Path.Combine(commonDir, "BISECT_LOG")))
            return GitRepositoryOperationState.Bisect;
        return GitRepositoryOperationState.Normal;
    }
}

public sealed class GitStatusReader
{
    public const int MaxRows = 25_000;

    private readonly IGitProcessRunner _runner;
    private readonly GitExecutableLocator _locator;
    private readonly GitRemoteService _remoteService;

    public GitStatusReader(
        IGitProcessRunner runner,
        GitExecutableLocator locator,
        GitRemoteService remoteService)
    {
        _runner = runner;
        _locator = locator;
        _remoteService = remoteService;
    }

    public async Task<GitRepositoryStatus> ReadAsync(
        GitRepositoryContext repository, CancellationToken cancellationToken = default)
    {
        var installation = await _locator.FindAsync(cancellationToken)
            ?? throw new InvalidOperationException("Git for Windows could not be found.");

        var result = await _runner.RunAsync(new GitProcessRequest(
            installation.ExecutablePath,
            repository.WorkTreeRoot,
            ["-c", "core.fsmonitor=false", "-C", repository.WorkTreeRoot, "status",
             "--porcelain=v2", "-z", "--branch", "--show-stash",
             "--untracked-files=all", "--ignored=matching"],
            ReadOnly: true), cancellationToken);
        if (result.OutcomeUnknown)
            throw new InvalidOperationException(FriendlyError(result, "Git status outcome is unknown."));
        if (result.WasCanceled) throw new OperationCanceledException(cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(FriendlyError(result, "Git status failed."));

        GitStatusParseResult parsed = GitStatusParser.Parse(result.StandardOutput, MaxRows);
        IReadOnlyList<GitRemoteInfo> remotes =
            await _remoteService.ReadAsync(repository, cancellationToken);
        string? preferred = await _remoteService.PreferredPushRemoteAsync(
            repository, parsed.Branch, remotes, cancellationToken);

        string? tree = null;
        if (parsed.Files.Any(file => file.IsStaged) && !parsed.Files.Any(file => file.IsConflict))
        {
            var treeResult = await _runner.RunAsync(new GitProcessRequest(
                installation.ExecutablePath,
                repository.WorkTreeRoot,
                ["-C", repository.WorkTreeRoot, "write-tree"],
                ReadOnly: true), cancellationToken);
            if (treeResult.OutcomeUnknown)
                throw new InvalidOperationException(FriendlyError(treeResult, "Git index snapshot outcome is unknown."));
            if (treeResult.WasCanceled) throw new OperationCanceledException(cancellationToken);
            if (treeResult.Succeeded) tree = treeResult.StandardOutputText.Trim();
        }

        return new GitRepositoryStatus
        {
            Repository = repository with
            {
                OperationState = DetectCurrentOperation(repository)
            },
            Branch = parsed.Branch,
            Files = parsed.Files,
            Remotes = remotes,
            PreferredPushRemote = preferred,
            IndexTreeId = tree,
            StashCount = parsed.StashCount,
            IsTruncated = parsed.IsTruncated,
        };
    }

    private static GitRepositoryOperationState DetectCurrentOperation(GitRepositoryContext repository)
    {
        if (File.Exists(Path.Combine(repository.GitDirectory, "MERGE_HEAD")))
            return GitRepositoryOperationState.Merge;
        if (Directory.Exists(Path.Combine(repository.GitDirectory, "rebase-merge"))
            || Directory.Exists(Path.Combine(repository.GitDirectory, "rebase-apply")))
            return GitRepositoryOperationState.Rebase;
        if (File.Exists(Path.Combine(repository.GitDirectory, "CHERRY_PICK_HEAD")))
            return GitRepositoryOperationState.CherryPick;
        if (File.Exists(Path.Combine(repository.GitDirectory, "REVERT_HEAD")))
            return GitRepositoryOperationState.Revert;
        if (File.Exists(Path.Combine(repository.CommonDirectory, "BISECT_LOG")))
            return GitRepositoryOperationState.Bisect;
        return GitRepositoryOperationState.Normal;
    }

    internal static string FriendlyError(GitProcessResult result, string fallback) =>
        string.IsNullOrWhiteSpace(result.StandardError) ? fallback : result.StandardError;
}

public sealed record GitStatusParseResult(
    GitBranchState Branch,
    IReadOnlyList<GitFileStatus> Files,
    int StashCount,
    bool IsTruncated);

public static class GitStatusParser
{
    public static GitStatusParseResult Parse(byte[] bytes, int maxRows = GitStatusReader.MaxRows)
    {
        string text = Encoding.UTF8.GetString(bytes);
        string? oid = null, head = null, upstream = null;
        int ahead = 0, behind = 0, stash = 0;
        bool truncated = false;
        var files = new List<GitFileStatus>();
        string[] records = text.Split('\0');

        for (int index = 0; index < records.Length; index++)
        {
            string record = records[index];
            if (record.Length == 0) continue;

            if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                oid = record[13..];
                continue;
            }
            if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                head = record[14..];
                continue;
            }
            if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = record[18..];
                continue;
            }
            if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                foreach (string part in record[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith('+')) int.TryParse(part[1..], out ahead);
                    else if (part.StartsWith('-')) int.TryParse(part[1..], out behind);
                }
                continue;
            }
            if (record.StartsWith("# stash ", StringComparison.Ordinal))
            {
                int.TryParse(record[8..], out stash);
                continue;
            }

            GitFileStatus? file = null;
            if (record.StartsWith("1 ", StringComparison.Ordinal))
            {
                string[] fields = record.Split(' ', 9);
                if (fields.Length == 9)
                    file = Tracked(fields[8], null, fields[1], fields[2], conflict: false);
            }
            else if (record.StartsWith("2 ", StringComparison.Ordinal))
            {
                string[] fields = record.Split(' ', 10);
                string? original = index + 1 < records.Length ? records[++index] : null;
                if (fields.Length == 10)
                    file = Tracked(fields[9], original, fields[1], fields[2], conflict: false);
            }
            else if (record.StartsWith("u ", StringComparison.Ordinal))
            {
                string[] fields = record.Split(' ', 11);
                if (fields.Length == 11)
                    file = Tracked(fields[10], null, fields[1], fields[2], conflict: true);
            }
            else if (record.StartsWith("? ", StringComparison.Ordinal))
            {
                file = new GitFileStatus { Path = record[2..], IsUntracked = true, WorkTreeCode = '?' };
            }
            else if (record.StartsWith("! ", StringComparison.Ordinal))
            {
                file = new GitFileStatus { Path = record[2..], IsIgnored = true, WorkTreeCode = '!' };
            }

            if (file is null) continue;
            if (files.Count >= maxRows) { truncated = true; continue; }
            files.Add(file);
        }

        bool unborn = string.Equals(oid, "(initial)", StringComparison.Ordinal);
        bool detached = string.Equals(head, "(detached)", StringComparison.Ordinal);
        return new GitStatusParseResult(
            new GitBranchState(
                detached ? null : head,
                unborn ? null : oid,
                upstream,
                ahead,
                behind,
                unborn,
                detached),
            files,
            stash,
            truncated);
    }

    private static GitFileStatus Tracked(
        string path, string? original, string xy, string submodule, bool conflict)
    {
        char index = xy.Length > 0 ? xy[0] : '.';
        char worktree = xy.Length > 1 ? xy[1] : '.';
        return new GitFileStatus
        {
            Path = path,
            OriginalPath = original,
            IndexCode = index,
            WorkTreeCode = worktree,
            SubmoduleCode = submodule,
            IsConflict = conflict,
        };
    }
}

public sealed class GitRemoteService
{
    private readonly IGitProcessRunner _runner;
    private readonly GitExecutableLocator _locator;

    public GitRemoteService(IGitProcessRunner runner, GitExecutableLocator locator)
    {
        _runner = runner;
        _locator = locator;
    }

    public async Task<IReadOnlyList<GitRemoteInfo>> ReadAsync(
        GitRepositoryContext repository, CancellationToken cancellationToken)
    {
        var installation = await _locator.FindAsync(cancellationToken);
        if (installation is null) return [];
        var listResult = await Run(repository, installation, ["remote"], cancellationToken);
        if (!listResult.Succeeded) return [];

        var remotes = new List<GitRemoteInfo>();
        foreach (string name in listResult.StandardOutputText
                     .Replace("\r", "")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fetch = await Run(repository, installation, ["remote", "get-url", name], cancellationToken);
            var push = await Run(repository, installation, ["remote", "get-url", "--push", name], cancellationToken);
            if (!fetch.Succeeded) continue;
            string fetchUrl = fetch.StandardOutputText.Trim();
            string pushUrl = push.Succeeded ? push.StandardOutputText.Trim() : fetchUrl;
            remotes.Add(GitRemoteClassifier.Create(name, fetchUrl, pushUrl));
        }
        return remotes;
    }

    public async Task<string?> PreferredPushRemoteAsync(
        GitRepositoryContext repository,
        GitBranchState branch,
        IReadOnlyList<GitRemoteInfo> remotes,
        CancellationToken cancellationToken)
    {
        if (branch.Name is { Length: > 0 })
        {
            string? pushRemote = await Config(repository, $"branch.{branch.Name}.pushRemote", cancellationToken);
            if (remotes.Any(r => r.Name == pushRemote)) return pushRemote;
            string? branchRemote = await Config(repository, $"branch.{branch.Name}.remote", cancellationToken);
            if (remotes.Any(r => r.Name == branchRemote)) return branchRemote;
        }

        string? defaultRemote = await Config(repository, "remote.pushDefault", cancellationToken);
        if (remotes.Any(r => r.Name == defaultRemote)) return defaultRemote;
        if (remotes.Count == 1) return remotes[0].Name;
        if (remotes.Any(r => r.Name == "origin")) return "origin";
        return null;
    }

    private async Task<string?> Config(
        GitRepositoryContext repository, string key, CancellationToken cancellationToken)
    {
        var installation = await _locator.FindAsync(cancellationToken);
        if (installation is null) return null;
        var result = await Run(repository, installation, ["config", "--get", key], cancellationToken);
        return result.Succeeded ? result.StandardOutputText.Trim() : null;
    }

    private Task<GitProcessResult> Run(
        GitRepositoryContext repository,
        GitInstallationInfo installation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var all = new List<string> { "-C", repository.WorkTreeRoot };
        all.AddRange(arguments);
        return _runner.RunAsync(new GitProcessRequest(
            installation.ExecutablePath, repository.WorkTreeRoot, all, ReadOnly: true),
            cancellationToken);
    }
}

public sealed class GitPushPreflightService
{
    private const long LargeFileWarningBytes = 50L * 1024 * 1024;
    private readonly IGitProcessRunner _runner;
    private readonly GitExecutableLocator _locator;

    public GitPushPreflightService(IGitProcessRunner runner, GitExecutableLocator locator)
    {
        _runner = runner;
        _locator = locator;
    }

    public async Task<GitPushPreview> ReadAsync(
        GitRepositoryContext repository,
        GitBranchState branch,
        CancellationToken cancellationToken)
    {
        var installation = await _locator.FindAsync(cancellationToken);
        if (installation is null || branch.IsUnborn)
            return new GitPushPreview(0, [], []);

        string range = branch.Upstream is { Length: > 0 }
            ? $"{branch.Upstream}..HEAD"
            : "HEAD";
        GitProcessResult countResult = await Run(repository, installation,
            ["rev-list", "--count", range], cancellationToken);
        int commitCount = countResult.Succeeded
            && int.TryParse(countResult.StandardOutputText.Trim(), out int count)
            ? count
            : Math.Max(0, branch.Ahead);

        GitProcessResult treeResult = await Run(repository, installation,
            ["ls-tree", "-rl", "-z", "HEAD"], cancellationToken);
        if (!treeResult.Succeeded)
            return new GitPushPreview(commitCount, [], []);

        var sensitive = new List<string>();
        var large = new List<string>();
        foreach (string record in treeResult.StandardOutputText.Split(
                     '\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = record.IndexOf('\t');
            if (tab < 0) continue;
            string metadata = record[..tab];
            string path = record[(tab + 1)..];
            if (IsSensitivePath(path)) sensitive.Add(path);

            string[] fields = metadata.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 4
                && long.TryParse(fields[3], out long size)
                && size >= LargeFileWarningBytes)
                large.Add(path);
        }
        return new GitPushPreview(commitCount, sensitive, large);
    }

    public static bool IsSensitivePath(string path)
    {
        string name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
    }

    private Task<GitProcessResult> Run(
        GitRepositoryContext repository,
        GitInstallationInfo installation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var all = new List<string> { "-C", repository.WorkTreeRoot };
        all.AddRange(arguments);
        return _runner.RunAsync(new GitProcessRequest(
            installation.ExecutablePath, repository.WorkTreeRoot, all, ReadOnly: true),
            cancellationToken);
    }
}

public sealed class GitBranchService
{
    private readonly IGitProcessRunner _runner;
    private readonly GitExecutableLocator _locator;

    public GitBranchService(IGitProcessRunner runner, GitExecutableLocator locator)
    {
        _runner = runner;
        _locator = locator;
    }

    public async Task<IReadOnlyList<GitBranchInfo>> ReadAsync(
        GitRepositoryContext repository,
        CancellationToken cancellationToken)
    {
        var installation = await _locator.FindAsync(cancellationToken);
        if (installation is null) return [];
        GitProcessResult result = await Run(repository, installation,
            ["branch", "--format=%(HEAD)%09%(refname:short)"], readOnly: true, cancellationToken);
        var branches = new List<GitBranchInfo>();
        if (result.Succeeded)
        {
            foreach (string line in result.StandardOutputText.Replace("\r", "")
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf('\t');
                if (separator < 0) continue;
                branches.Add(new GitBranchInfo(
                    line[(separator + 1)..],
                    line[..separator].Contains('*')));
            }
        }

        if (branches.Count == 0)
        {
            GitProcessResult symbolic = await Run(repository, installation,
                ["symbolic-ref", "--quiet", "--short", "HEAD"], readOnly: true, cancellationToken);
            string name = symbolic.Succeeded ? symbolic.StandardOutputText.Trim() : string.Empty;
            if (name.Length > 0) branches.Add(new GitBranchInfo(name, true));
        }
        return branches.OrderByDescending(branch => branch.IsCurrent)
            .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<GitOperationResult> CreateAndSwitchAsync(
        GitRepositoryContext repository,
        string name,
        CancellationToken cancellationToken) =>
        GitOperationCoordinator.RunAsync(repository.CommonDirectory, async () =>
        {
            var installation = await _locator.FindAsync(cancellationToken);
            if (installation is null) return GitOperationResult.Fail("Git for Windows could not be found.");
            name = name.Trim();
            if (name.Length == 0) return GitOperationResult.Fail("Enter a branch name.");

            GitProcessResult validation = await Run(repository, installation,
                ["check-ref-format", "--branch", name], readOnly: true, cancellationToken);
            if (!validation.Succeeded)
                return GitOperationResult.Fail("That branch name is not valid.");
            GitOperationResult? safety = EnsureOperationIsNormal(repository);
            if (safety is not null) return safety;

            GitProcessResult result = await Run(repository, installation,
                ["switch", "-c", name], readOnly: false, cancellationToken);
            return result.Succeeded
                ? GitOperationResult.Success($"Created and switched to {name}.")
                : GitOperationResult.Fail(
                    GitStatusReader.FriendlyError(result, "The branch could not be created."));
        }, cancellationToken);

    public Task<GitOperationResult> SwitchAsync(
        GitRepositoryContext repository,
        string name,
        CancellationToken cancellationToken) =>
        GitOperationCoordinator.RunAsync(repository.CommonDirectory, async () =>
        {
            var installation = await _locator.FindAsync(cancellationToken);
            if (installation is null) return GitOperationResult.Fail("Git for Windows could not be found.");
            GitOperationResult? safety = await EnsureSafeToSwitch(
                repository, installation, cancellationToken);
            if (safety is not null) return safety;

            GitProcessResult result = await Run(repository, installation,
                ["switch", "--", name], readOnly: false, cancellationToken);
            return result.Succeeded
                ? GitOperationResult.Success($"Switched to {name}.")
                : GitOperationResult.Fail(
                    GitStatusReader.FriendlyError(result, "The branch could not be switched."));
        }, cancellationToken);

    public Task<GitOperationResult> DeleteAsync(
        GitRepositoryContext repository,
        string name,
        CancellationToken cancellationToken) =>
        GitOperationCoordinator.RunAsync(repository.CommonDirectory, async () =>
        {
            var installation = await _locator.FindAsync(cancellationToken);
            if (installation is null) return GitOperationResult.Fail("Git for Windows could not be found.");
            GitOperationResult? safety = EnsureOperationIsNormal(repository);
            if (safety is not null) return safety;
            GitProcessResult current = await Run(repository, installation,
                ["symbolic-ref", "--quiet", "--short", "HEAD"], readOnly: true, cancellationToken);
            if (current.Succeeded
                && string.Equals(current.StandardOutputText.Trim(), name, StringComparison.Ordinal))
                return GitOperationResult.Fail("Switch to another branch before deleting this one.");

            GitProcessResult result = await Run(repository, installation,
                ["branch", "-d", "--", name], readOnly: false, cancellationToken);
            return result.Succeeded
                ? GitOperationResult.Success($"Deleted local branch {name}.")
                : GitOperationResult.Fail(
                    GitStatusReader.FriendlyError(
                        result,
                        "The branch was not deleted. Rain only deletes branches Git considers fully merged."));
        }, cancellationToken);

    private async Task<GitOperationResult?> EnsureSafeToSwitch(
        GitRepositoryContext repository,
        GitInstallationInfo installation,
        CancellationToken cancellationToken)
    {
        GitOperationResult? operationSafety = EnsureOperationIsNormal(repository);
        if (operationSafety is not null) return operationSafety;
        GitProcessResult status = await Run(repository, installation,
            ["status", "--porcelain=v2", "-z", "--untracked-files=all"],
            readOnly: true, cancellationToken);
        if (!status.Succeeded)
            return GitOperationResult.Fail(
                GitStatusReader.FriendlyError(status, "Repository status could not be checked."));
        return status.StandardOutput.Length == 0
            ? null
            : GitOperationResult.Fail(
                "Commit or otherwise resolve all file changes before changing branches.");
    }

    private static GitOperationResult? EnsureOperationIsNormal(
        GitRepositoryContext repository) =>
        CurrentOperation(repository) == GitRepositoryOperationState.Normal
            ? null
            : GitOperationResult.Fail(
                "Finish or recover the current Git operation before changing branches.");

    private static GitRepositoryOperationState CurrentOperation(GitRepositoryContext repository)
    {
        if (File.Exists(Path.Combine(repository.GitDirectory, "MERGE_HEAD")))
            return GitRepositoryOperationState.Merge;
        if (Directory.Exists(Path.Combine(repository.GitDirectory, "rebase-merge"))
            || Directory.Exists(Path.Combine(repository.GitDirectory, "rebase-apply")))
            return GitRepositoryOperationState.Rebase;
        if (File.Exists(Path.Combine(repository.GitDirectory, "CHERRY_PICK_HEAD")))
            return GitRepositoryOperationState.CherryPick;
        if (File.Exists(Path.Combine(repository.GitDirectory, "REVERT_HEAD")))
            return GitRepositoryOperationState.Revert;
        if (File.Exists(Path.Combine(repository.CommonDirectory, "BISECT_LOG")))
            return GitRepositoryOperationState.Bisect;
        return GitRepositoryOperationState.Normal;
    }

    private Task<GitProcessResult> Run(
        GitRepositoryContext repository,
        GitInstallationInfo installation,
        IReadOnlyList<string> arguments,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        var all = new List<string> { "-C", repository.WorkTreeRoot };
        all.AddRange(arguments);
        return _runner.RunAsync(new GitProcessRequest(
            installation.ExecutablePath, repository.WorkTreeRoot, all, ReadOnly: readOnly),
            cancellationToken);
    }
}

public sealed class GitCloneService
{
    private readonly IGitProcessRunner _runner;
    private readonly GitExecutableLocator _locator;

    public GitCloneService(IGitProcessRunner runner, GitExecutableLocator locator)
    {
        _runner = runner;
        _locator = locator;
    }

    public async Task<GitCloneResult> CloneIntoNewChildAsync(
        string destinationParent,
        string repositoryUrl,
        CancellationToken cancellationToken)
    {
        var installation = await _locator.FindAsync(cancellationToken);
        if (installation is null)
            return new GitCloneResult(
                GitOperationOutcome.Failed, Message: "Git for Windows could not be found.");
        if (!installation.IsSupported)
            return new GitCloneResult(
                GitOperationOutcome.Failed, Message: "Rain Explorer requires Git 2.40 or newer.");
        if (!Directory.Exists(destinationParent))
            return new GitCloneResult(
                GitOperationOutcome.Failed, Message: "The destination folder no longer exists.");

        repositoryUrl = repositoryUrl.Trim();
        string? folderName = RepositoryFolderName(repositoryUrl);
        if (folderName is null)
            return new GitCloneResult(
                GitOperationOutcome.Failed, Message: "The repository URL is not valid.");

        string parent = Path.GetFullPath(destinationParent);
        string final = Path.GetFullPath(Path.Combine(parent, folderName));
        if (Directory.Exists(final) || File.Exists(final))
            return new GitCloneResult(
                GitOperationOutcome.Failed,
                Message: $"A file or folder named {folderName} already exists here.");

        string temporary = Path.GetFullPath(Path.Combine(
            parent, $".rain-clone-{Guid.NewGuid():N}"));
        return await GitOperationCoordinator.RunAsync(final, async () =>
        {
            GitProcessResult result = await _runner.RunAsync(new GitProcessRequest(
                installation.ExecutablePath,
                parent,
                ["clone", "--progress", "--", repositoryUrl, temporary]),
                cancellationToken);
            if (result.OutcomeUnknown)
                return new GitCloneResult(
                    GitOperationOutcome.OutcomeUnknown,
                    temporary,
                    "Clone cancellation is still being cleaned up. Its temporary folder was left in place; refresh before retrying.");
            if (result.WasCanceled)
            {
                string? cleanup = CleanupTemporaryClone(parent, temporary);
                return new GitCloneResult(
                    GitOperationOutcome.Canceled,
                    Message: cleanup ?? "Clone canceled. Temporary files were removed.");
            }
            if (!result.Succeeded)
            {
                string? cleanup = CleanupTemporaryClone(parent, temporary);
                string error = GitStatusReader.FriendlyError(result, "The repository could not be cloned.");
                return new GitCloneResult(
                    GitOperationOutcome.Failed,
                    Message: cleanup is null ? error : $"{error} {cleanup}");
            }

            try
            {
                if (Directory.Exists(final) || File.Exists(final))
                    return new GitCloneResult(
                        GitOperationOutcome.StateChanged,
                        Message: $"A file or folder named {folderName} appeared while cloning. "
                                 + $"The completed clone remains at {temporary}.");
                Directory.Move(temporary, final);
                return new GitCloneResult(
                    GitOperationOutcome.Success,
                    final,
                    $"Cloned into {final}.");
            }
            catch (Exception ex)
            {
                return new GitCloneResult(
                    GitOperationOutcome.OutcomeUnknown,
                    Message: $"The clone completed at {temporary}, but Rain could not move it into place: "
                             + GitSecurity.Redact(ex.Message));
            }
        }, cancellationToken);
    }

    public static string? RepositoryFolderName(string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl)) return null;
        string value = repositoryUrl.Trim().TrimEnd('/', '\\');
        int separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        int colon = value.LastIndexOf(':');
        if (colon > separator && !Path.IsPathFullyQualified(value)) separator = colon;
        string name = separator >= 0 ? value[(separator + 1)..] : value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name;
    }

    private static string? CleanupTemporaryClone(string parent, string temporary)
    {
        try
        {
            string relative = Path.GetRelativePath(parent, temporary);
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || !Path.GetFileName(temporary).StartsWith(".rain-clone-", StringComparison.Ordinal))
                return $"Temporary clone cleanup was skipped for safety: {temporary}";
            if (!Directory.Exists(temporary)) return null;
            foreach (string file in Directory.EnumerateFiles(
                         temporary, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(temporary, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"Temporary clone cleanup failed at {temporary}: {GitSecurity.Redact(ex.Message)}";
        }
    }
}

public sealed class GitMutationService
{
    private readonly IGitProcessRunner _runner;
    private readonly GitExecutableLocator _locator;

    public GitMutationService(IGitProcessRunner runner, GitExecutableLocator locator)
    {
        _runner = runner;
        _locator = locator;
    }

    public Task<GitOperationResult> StageAsync(
        GitRepositoryContext repository,
        IReadOnlyCollection<string> repositoryPaths,
        CancellationToken cancellationToken) =>
        RunLocked(repository, async installation =>
        {
            if (repositoryPaths.Count == 0) return GitOperationResult.Fail("No files were selected.");
            var result = await RunPaths(repository, installation,
                ["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"],
                repositoryPaths, cancellationToken);
            return ToOperation(result, "The selected files could not be staged.");
        }, cancellationToken);

    public Task<GitOperationResult> UnstageAsync(
        GitRepositoryContext repository,
        IReadOnlyCollection<string> repositoryPaths,
        bool hasHead,
        CancellationToken cancellationToken) =>
        RunLocked(repository, async installation =>
        {
            if (repositoryPaths.Count == 0) return GitOperationResult.Fail("No files were selected.");
            IReadOnlyList<string> arguments = hasHead
                ? ["restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"]
                : ["rm", "--cached", "-r", "--ignore-unmatch",
                   "--pathspec-from-file=-", "--pathspec-file-nul"];
            var result = await RunPaths(repository, installation, arguments, repositoryPaths, cancellationToken);
            return ToOperation(result, "The selected files could not be unstaged.");
        }, cancellationToken);

    public Task<GitOperationResult> CommitAsync(
        GitRepositoryContext repository,
        string summary,
        string description,
        string? reviewedIndexTree,
        CancellationToken cancellationToken) =>
        RunLocked(repository, async installation =>
        {
            summary = summary.Trim();
            if (summary.Length == 0) return GitOperationResult.Fail("Enter a commit summary.");
            GitOperationResult? preflight = await MutationPreflight(
                repository, installation, expectedBranch: null, cancellationToken);
            if (preflight is not null) return preflight;

            string? before = await ReadHead(repository, installation, cancellationToken);
            var tree = await Run(repository, installation, ["write-tree"], null, true, cancellationToken);
            if (!tree.Succeeded)
                return ToOperation(tree, "The staged changes could not be reviewed.");
            string currentTree = tree.StandardOutputText.Trim();
            if (!string.IsNullOrEmpty(reviewedIndexTree)
                && !string.Equals(currentTree, reviewedIndexTree, StringComparison.Ordinal))
                return new GitOperationResult(
                    GitOperationOutcome.StateChanged,
                    "The staged changes changed since they were reviewed. Refresh and check them again.",
                    before,
                    before);

            string message = description.Trim().Length == 0
                ? summary
                : $"{summary}\n\n{description.Trim()}";
            var result = await Run(repository, installation,
                ["commit", "-F", "-"], Encoding.UTF8.GetBytes(message), false, cancellationToken);
            if (result.OutcomeUnknown)
                return new GitOperationResult(
                    GitOperationOutcome.OutcomeUnknown,
                    "Git is still shutting down. The commit outcome is unknown; refresh before retrying.",
                    before,
                    before);
            string? after = await ReadHead(repository, installation, CancellationToken.None);
            if (result.WasCanceled)
            {
                bool committed = !string.Equals(before, after, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(after);
                return new GitOperationResult(
                    committed ? GitOperationOutcome.Success : GitOperationOutcome.Canceled,
                    committed
                        ? "The commit completed before cancellation. It is still local."
                        : "Commit canceled. Staged changes were preserved.",
                    before,
                    after);
            }
            if (!result.Succeeded)
                return new GitOperationResult(
                    GitOperationOutcome.Failed,
                    GitStatusReader.FriendlyError(result, "The local commit failed."),
                    before,
                    after);
            return new GitOperationResult(
                GitOperationOutcome.Success,
                "Committed locally. Nothing has been uploaded.",
                before,
                after);
        }, cancellationToken);

    public Task<GitOperationResult> PushAsync(
        GitRepositoryContext repository,
        string branch,
        string remote,
        bool setUpstream,
        CancellationToken cancellationToken) =>
        RunLocked(repository, async installation =>
        {
            if (string.IsNullOrWhiteSpace(branch))
                return GitOperationResult.Fail("A detached HEAD cannot be pushed from the MVP.");
            if (string.IsNullOrWhiteSpace(remote))
                return GitOperationResult.Fail("Choose a push remote.");
            GitOperationResult? preflight = await MutationPreflight(
                repository, installation, branch, cancellationToken);
            if (preflight is not null) return preflight;

            string? before = await ReadHead(repository, installation, cancellationToken);
            var args = new List<string> { "push", "--porcelain" };
            if (setUpstream) args.Add("--set-upstream");
            args.Add("--");
            args.Add(remote);
            args.Add(branch);
            var result = await Run(repository, installation, args, null, false, cancellationToken);
            if (result.OutcomeUnknown)
                return new GitOperationResult(
                    GitOperationOutcome.OutcomeUnknown,
                    "Git is still shutting down. The remote outcome is unknown; refresh before retrying.",
                    before,
                    before);
            if (result.WasCanceled)
                return new GitOperationResult(
                    GitOperationOutcome.OutcomeUnknown,
                    "Push canceled. The remote outcome is unknown; refresh before retrying.",
                    before,
                    before);
            return result.Succeeded
                ? new GitOperationResult(
                    GitOperationOutcome.Success,
                    $"Pushed {branch} to {remote}.",
                    before,
                    before)
                : new GitOperationResult(
                    GitOperationOutcome.Failed,
                    GitStatusReader.FriendlyError(result, "Push failed. The local commit is unchanged."),
                    before,
                    before);
        }, cancellationToken);

    public async Task<GitOperationResult> InitializeAsync(
        string folder, CancellationToken cancellationToken)
    {
        var installation = await _locator.FindAsync(cancellationToken);
        if (installation is null) return GitOperationResult.Fail("Git for Windows could not be found.");
        if (!installation.IsSupported)
            return GitOperationResult.Fail("Rain Explorer requires Git for Windows 2.40 or newer.");
        if (!Directory.Exists(folder)) return GitOperationResult.Fail("The selected folder no longer exists.");

        return await GitOperationCoordinator.RunAsync(folder, async () =>
        {
            var branchResult = await _runner.RunAsync(new GitProcessRequest(
                installation.ExecutablePath,
                folder,
                ["config", "--get", "init.defaultBranch"],
                ReadOnly: true), cancellationToken);
            string branch = branchResult.Succeeded
                ? branchResult.StandardOutputText.Trim()
                : "main";
            if (string.IsNullOrWhiteSpace(branch)) branch = "main";

            var result = await _runner.RunAsync(new GitProcessRequest(
                installation.ExecutablePath,
                folder,
                ["-C", folder, "init", "-b", branch]), cancellationToken);
            return ToOperation(result, "The repository could not be initialized.");
        }, cancellationToken);
    }

    private async Task<GitOperationResult> RunLocked(
        GitRepositoryContext repository,
        Func<GitInstallationInfo, Task<GitOperationResult>> action,
        CancellationToken cancellationToken)
    {
        var installation = await _locator.FindAsync(cancellationToken);
        if (installation is null) return GitOperationResult.Fail("Git for Windows could not be found.");
        if (!installation.IsSupported)
            return GitOperationResult.Fail("Rain Explorer requires Git for Windows 2.40 or newer.");
        return await GitOperationCoordinator.RunAsync(repository.CommonDirectory,
            () => action(installation), cancellationToken);
    }

    private Task<GitProcessResult> RunPaths(
        GitRepositoryContext repository,
        GitInstallationInfo installation,
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        var all = new List<string> { "--literal-pathspecs", "-C", repository.WorkTreeRoot };
        all.AddRange(arguments);
        byte[] input = Encoding.UTF8.GetBytes(string.Join('\0', paths) + '\0');
        return _runner.RunAsync(new GitProcessRequest(
            installation.ExecutablePath, repository.WorkTreeRoot, all, input), cancellationToken);
    }

    private Task<GitProcessResult> Run(
        GitRepositoryContext repository,
        GitInstallationInfo installation,
        IReadOnlyList<string> arguments,
        byte[]? input,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        var all = new List<string> { "-C", repository.WorkTreeRoot };
        all.AddRange(arguments);
        return _runner.RunAsync(new GitProcessRequest(
            installation.ExecutablePath, repository.WorkTreeRoot, all, input, readOnly),
            cancellationToken);
    }

    private async Task<string?> ReadHead(
        GitRepositoryContext repository,
        GitInstallationInfo installation,
        CancellationToken cancellationToken)
    {
        var result = await Run(repository, installation,
            ["rev-parse", "--verify", "HEAD"], null, true, cancellationToken);
        return result.Succeeded ? result.StandardOutputText.Trim() : null;
    }

    private async Task<GitOperationResult?> MutationPreflight(
        GitRepositoryContext repository,
        GitInstallationInfo installation,
        string? expectedBranch,
        CancellationToken cancellationToken)
    {
        GitRepositoryOperationState state = CurrentOperation(repository);
        if (state != GitRepositoryOperationState.Normal)
            return GitOperationResult.Fail(
                $"{state} is in progress. Finish or recover that operation before committing or pushing.");

        var branchResult = await Run(repository, installation,
            ["symbolic-ref", "--quiet", "--short", "HEAD"], null, true, cancellationToken);
        if (!branchResult.Succeeded)
            return GitOperationResult.Fail(
                "HEAD is detached. Check out a branch before committing or pushing.");
        string currentBranch = branchResult.StandardOutputText.Trim();
        if (expectedBranch is { Length: > 0 }
            && !string.Equals(currentBranch, expectedBranch, StringComparison.Ordinal))
            return new GitOperationResult(
                GitOperationOutcome.StateChanged,
                $"The active branch changed from {expectedBranch} to {currentBranch}. Refresh before pushing.");

        var conflicts = await Run(repository, installation,
            ["diff", "--name-only", "--diff-filter=U", "-z"], null, true, cancellationToken);
        if (!conflicts.Succeeded)
            return GitOperationResult.Fail(
                GitStatusReader.FriendlyError(conflicts, "Repository conflict state could not be checked."));
        if (conflicts.StandardOutput.Length > 0)
            return GitOperationResult.Fail("Resolve and stage all conflicts before committing or pushing.");
        return null;
    }

    private static GitRepositoryOperationState CurrentOperation(GitRepositoryContext repository)
    {
        if (File.Exists(Path.Combine(repository.GitDirectory, "MERGE_HEAD")))
            return GitRepositoryOperationState.Merge;
        if (Directory.Exists(Path.Combine(repository.GitDirectory, "rebase-merge"))
            || Directory.Exists(Path.Combine(repository.GitDirectory, "rebase-apply")))
            return GitRepositoryOperationState.Rebase;
        if (File.Exists(Path.Combine(repository.GitDirectory, "CHERRY_PICK_HEAD")))
            return GitRepositoryOperationState.CherryPick;
        if (File.Exists(Path.Combine(repository.GitDirectory, "REVERT_HEAD")))
            return GitRepositoryOperationState.Revert;
        if (File.Exists(Path.Combine(repository.CommonDirectory, "BISECT_LOG")))
            return GitRepositoryOperationState.Bisect;
        return GitRepositoryOperationState.Normal;
    }

    private static GitOperationResult ToOperation(GitProcessResult result, string fallback)
    {
        if (result.OutcomeUnknown)
            return new GitOperationResult(
                GitOperationOutcome.OutcomeUnknown,
                GitStatusReader.FriendlyError(result, fallback));
        if (result.WasCanceled)
            return new GitOperationResult(GitOperationOutcome.Canceled, "Operation canceled.");
        return result.Succeeded
            ? GitOperationResult.Success()
            : GitOperationResult.Fail(GitStatusReader.FriendlyError(result, fallback));
    }
}

public static class GitOperationCoordinator
{
    private static readonly ConcurrentDictionary<string, GateEntry> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<T> RunAsync<T>(
        string repositoryKey,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        string key = Path.GetFullPath(repositoryKey);
        GateEntry entry;
        while (true)
        {
            entry = Gates.GetOrAdd(key, _ => new GateEntry());
            lock (entry.Sync)
            {
                if (entry.Retired) continue;
                entry.References++;
                break;
            }
        }
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            try { return await action(); }
            finally { entry.Semaphore.Release(); }
        }
        finally
        {
            lock (entry.Sync)
            {
                if (--entry.References == 0)
                {
                    entry.Retired = true;
                    Gates.TryRemove(new KeyValuePair<string, GateEntry>(key, entry));
                    entry.Semaphore.Dispose();
                }
            }
        }
    }

    private sealed class GateEntry
    {
        public readonly object Sync = new();
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
        public bool Retired;
    }
}
