namespace RainExplorer.Services;

/// <summary>What the Git window can offer next to an explained error.</summary>
public enum GitErrorFix { None, SetIdentity, OpenTerminal, Refresh }

/// <summary>A Git error in plain words: what happened, what to do, and the original text for reference.</summary>
public sealed record GitErrorHelp(string Title, string Explanation, GitErrorFix Fix, string Details);

/// <summary>
/// Turns Git's own error output into a short explanation. Matching is on Git's stable English messages
/// (Rain runs git with LC_ALL=C). Unknown errors return null and the raw text is shown as before.
/// </summary>
public static class GitErrorExplainer
{
    private sealed record Rule(string[] AnyOf, string Title, string Explanation, GitErrorFix Fix);

    private static readonly Rule[] Rules =
    [
        new(["Author identity unknown", "Please tell me who you are", "unable to auto-detect email address"],
            "Git doesn't know who you are yet",
            "Every commit records an author name and email. Git hasn't been told yours on this computer, so it "
            + "refused to commit. Set them once and commits will work in every repository. On GitHub, use the "
            + "email on your account (or your GitHub \"noreply\" address) so commits link to your profile.",
            GitErrorFix.SetIdentity),

        new(["could not read Username", "terminal prompts disabled", "Authentication failed", "Invalid username or password",
             "Password authentication is not supported"],
            "GitHub needs you to sign in",
            "The push was refused because Git has no saved login for this remote, and GitHub no longer accepts "
            + "passwords. The easiest fix is the GitHub CLI: run \"gh auth login\" once in a terminal and choose "
            + "HTTPS. You can also set up an SSH key and use the git@github.com: address.",
            GitErrorFix.OpenTerminal),

        new(["Permission denied (publickey)"],
            "Your SSH key wasn't accepted",
            "This remote uses SSH, and the server didn't accept any key on this computer. Create a key with "
            + "\"ssh-keygen -t ed25519\" and add the .pub file to your GitHub account (Settings → SSH keys), "
            + "or switch the remote to its https:// address and sign in with \"gh auth login\".",
            GitErrorFix.OpenTerminal),

        new(["Host key verification failed"],
            "The server's identity hasn't been confirmed",
            "This is the first SSH connection to this server, and Rain won't accept its key on your behalf. "
            + "Run \"ssh -T git@github.com\" (or the server you use) in a terminal, check the fingerprint and type "
            + "yes. Then try again.",
            GitErrorFix.OpenTerminal),

        new(["[rejected]", "non-fast-forward", "fetch first", "Updates were rejected"],
            "The remote has commits you don't have",
            "Someone (or you, from another computer) pushed to this branch first, so Git won't overwrite it. "
            + "Bring their changes in with \"git pull\" in a terminal, fix any conflicts, then push again.",
            GitErrorFix.OpenTerminal),

        new(["Could not resolve host", "Failed to connect to", "Connection timed out", "Network is unreachable",
             "Could not read from remote repository"],
            "Couldn't reach the remote",
            "Git couldn't connect to the server. Check your internet connection and the remote address, then "
            + "try again. If you're on a VPN or proxy, it may be blocking the connection.",
            GitErrorFix.None),

        new(["index.lock", "Another git process seems to be running"],
            "Another Git program is busy with this repository",
            "Git keeps a lock file while it works. Either another Git command (an editor, IDE or terminal) is "
            + "still running, or one crashed and left the lock behind. Close other Git tools and try again. If "
            + "nothing is running, delete .git/index.lock.",
            GitErrorFix.Refresh),

        new(["detected dubious ownership"],
            "Git doesn't trust this folder's owner",
            "This repository belongs to a different user account (common on external drives and shared "
            + "folders), so Git stops for safety. If you trust it, run the \"git config --global --add "
            + "safe.directory …\" command from the details below in a terminal.",
            GitErrorFix.OpenTerminal),

        new(["Please commit your changes or stash them", "would be overwritten by checkout"],
            "Switching branches would lose your changes",
            "Some of your uncommitted changes conflict with the branch you picked. Commit them first, or stash "
            + "them with \"git stash\" in a terminal, then switch.",
            GitErrorFix.OpenTerminal),

        new(["is not fully merged"],
            "That branch has work that isn't merged",
            "Deleting it would lose commits that exist only on that branch, so Rain asked Git to refuse. Merge "
            + "it first, or delete it in a terminal with \"git branch -D <name>\" if you're sure.",
            GitErrorFix.None),

        new(["src refspec", "does not match any"],
            "There's nothing to push yet",
            "This branch has no commits. Create a local commit first, then push.",
            GitErrorFix.None),

        new(["GH001", "Large files detected", "exceeds GitHub's file size limit"],
            "A file is too big for GitHub",
            "GitHub rejects files over 100 MB. Remove the file from your commits (or track it with Git LFS) "
            + "and push again. The details below name the file.",
            GitErrorFix.OpenTerminal),

        new(["GH013", "push protection", "Push cannot contain secrets"],
            "GitHub blocked a secret in your commits",
            "GitHub found something that looks like a password or API key and refused the push to protect it. "
            + "Remove it from the commit (and change that key if it was real), then push again.",
            GitErrorFix.OpenTerminal),

        new(["CONFLICT", "unmerged files", "Committing is not possible because you have unmerged files"],
            "There are merge conflicts to resolve",
            "Some files have conflicting changes. Open them, keep the parts you want (remove the <<<<<<< and "
            + ">>>>>>> markers), stage them, then commit.",
            GitErrorFix.None),

        new(["nothing to commit", "no changes added to commit"],
            "There's nothing staged to commit",
            "Stage the files you want in this commit first, then commit.",
            GitErrorFix.None),
    ];

    /// <summary>Plain-language help for a Git error, or null when the text isn't a recognized error.</summary>
    public static GitErrorHelp? Explain(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        foreach (Rule rule in Rules)
            if (rule.AnyOf.Any(needle => message.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                return new GitErrorHelp(rule.Title, rule.Explanation, rule.Fix, message.Trim());
        return null;
    }
}
