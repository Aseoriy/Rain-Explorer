using System.Collections.ObjectModel;

namespace RainExplorer.Models;

public sealed record GitInstallationInfo(
    string ExecutablePath,
    Version Version,
    bool IsSupported,
    string DisplayVersion);

public sealed record GitRepositoryContext(
    string WorkTreeRoot,
    string GitDirectory,
    string CommonDirectory,
    string? SuperprojectRoot,
    GitRepositoryOperationState OperationState);

public enum GitRepositoryOperationState
{
    Normal,
    Merge,
    Rebase,
    CherryPick,
    Revert,
    Bisect,
}

public sealed record GitBranchState(
    string? Name,
    string? ObjectId,
    string? Upstream,
    int Ahead,
    int Behind,
    bool IsUnborn,
    bool IsDetached)
{
    public string DisplayName => IsUnborn ? (Name ?? "Unborn branch")
        : IsDetached ? "Detached HEAD"
        : Name ?? "Unknown branch";
}

public sealed record GitRemoteInfo(
    string Name,
    string FetchUrl,
    string PushUrl,
    bool IsGitHub,
    string? WebUrl)
{
    public string DisplayUrl => GitRemoteClassifier.SanitizeUrl(PushUrl);
}

public sealed class GitFileStatus
{
    public required string Path { get; init; }
    public string? OriginalPath { get; init; }
    public char IndexCode { get; init; } = '.';
    public char WorkTreeCode { get; init; } = '.';
    public string SubmoduleCode { get; init; } = "N...";
    public bool IsUntracked { get; init; }
    public bool IsIgnored { get; init; }
    public bool IsConflict { get; init; }

    public bool IsStaged => !IsIgnored && !IsUntracked && IndexCode != '.';
    public bool IsUnstaged => !IsIgnored && (IsUntracked || WorkTreeCode != '.');

    public string Name
    {
        get
        {
            string normalized = Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
            return System.IO.Path.GetFileName(normalized.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }
    }

    public string ChangeText
    {
        get
        {
            if (IsConflict) return "Conflict";
            if (IsIgnored) return "Ignored";
            if (IsUntracked) return "Untracked";
            return Describe(IsStaged ? IndexCode : WorkTreeCode);
        }
    }

    private static string Describe(char code) => code switch
    {
        'A' => "Added",
        'D' => "Deleted",
        'M' => "Modified",
        'R' => "Renamed",
        'C' => "Copied",
        'T' => "Type changed",
        'U' => "Conflict",
        _ => "Changed",
    };
}

public sealed class GitRepositoryStatus
{
    public required GitRepositoryContext Repository { get; init; }
    public required GitBranchState Branch { get; init; }
    public required IReadOnlyList<GitFileStatus> Files { get; init; }
    public required IReadOnlyList<GitRemoteInfo> Remotes { get; init; }
    public string? PreferredPushRemote { get; init; }
    public string? IndexTreeId { get; init; }
    public int StashCount { get; init; }
    public bool IsTruncated { get; init; }

    public bool HasConflicts => Files.Any(file => file.IsConflict);
    public bool HasStagedChanges => Files.Any(file => file.IsStaged);
}

public enum GitOperationOutcome
{
    Success,
    Failed,
    Canceled,
    StateChanged,
    OutcomeUnknown,
}

public sealed record GitOperationResult(
    GitOperationOutcome Outcome,
    string? Message = null,
    string? BeforeObjectId = null,
    string? AfterObjectId = null)
{
    public bool Succeeded => Outcome == GitOperationOutcome.Success;
    public static GitOperationResult Success(string? message = null) =>
        new(GitOperationOutcome.Success, message);
    public static GitOperationResult Fail(string message) =>
        new(GitOperationOutcome.Failed, message);
}

public sealed record GitPushPreview(
    int CommitCount,
    IReadOnlyList<string> SensitivePaths,
    IReadOnlyList<string> LargePaths);

public sealed record GitBranchInfo(string Name, bool IsCurrent)
{
    public string DisplayName => IsCurrent ? $"{Name}  (current)" : Name;
}

public sealed record GitCloneResult(
    GitOperationOutcome Outcome,
    string? DestinationPath = null,
    string? Message = null)
{
    public bool Succeeded => Outcome == GitOperationOutcome.Success;
}

public sealed class GitHubAccountMetadata
{
    public long AccountId { get; set; }
    public string Login { get; set; } = string.Empty;
    public string Host { get; set; } = "github.com";
    public string AvatarUrl { get; set; } = string.Empty;
    public string AccessCredentialTarget { get; set; } = string.Empty;
    public string RefreshCredentialTarget { get; set; } = string.Empty;
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
}

public static class GitRemoteClassifier
{
    public static GitRemoteInfo Create(string name, string fetchUrl, string pushUrl)
    {
        bool github = TryGetGitHubWebUrl(pushUrl, out string? web)
            || TryGetGitHubWebUrl(fetchUrl, out web);
        return new GitRemoteInfo(name, fetchUrl, pushUrl, github, web);
    }

    public static bool TryGetGitHubWebUrl(string remoteUrl, out string? webUrl)
    {
        webUrl = null;
        if (string.IsNullOrWhiteSpace(remoteUrl)) return false;

        string value = remoteUrl.Trim();
        string? host = null;
        string? path = null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            int colon = value.IndexOf(':');
            int at = value.LastIndexOf('@', colon >= 0 ? colon : value.Length - 1);
            if (colon > 0 && at >= 0)
            {
                host = value[(at + 1)..colon];
                path = value[(colon + 1)..];
            }
        }

        if (!string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(path))
            return false;

        string repoPath = path.Trim('/').Replace('\\', '/');
        if (repoPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoPath = repoPath[..^4];
        string[] segments = repoPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;
        webUrl = $"https://github.com/{segments[0]}/{segments[1]}";
        return true;
    }

    public static string SanitizeUrl(string remoteUrl)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return remoteUrl;
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        return builder.Uri.ToString();
    }
}
