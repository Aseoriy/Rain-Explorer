using System.Text;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class GitRealRepositoryTests
{
    [Fact]
    public async Task InitializesStagesCommitsAndReadsRealRepositoryState()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"RainExplorer-GitTests-{Guid.NewGuid():N}");
        string remoteFolder = folder + "-remote.git";
        Directory.CreateDirectory(folder);
        try
        {
            var runner = new GitProcessRunner();
            var locator = new GitExecutableLocator(runner);
            GitInstallationInfo? installation = await locator.FindAsync();
            Assert.NotNull(installation);
            Assert.True(installation.IsSupported);

            var mutations = new GitMutationService(runner, locator);
            GitOperationResult initialized = await mutations.InitializeAsync(folder, CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Message);

            await RunGit(runner, installation.ExecutablePath, folder,
                "config", "user.name", "Rain Explorer Tests");
            await RunGit(runner, installation.ExecutablePath, folder,
                "config", "user.email", "rain-explorer-tests@example.invalid");

            await File.WriteAllTextAsync(Path.Combine(folder, "-leading dash.txt"), "one");
            await File.WriteAllTextAsync(Path.Combine(folder, "unicodé.txt"), "two");
            await File.WriteAllTextAsync(Path.Combine(folder, ".env"), "NOT_A_REAL_SECRET=test");

            var repositoryLocator = new GitRepositoryLocator(runner, locator);
            GitRepositoryContext repository =
                Assert.IsType<GitRepositoryContext>(await repositoryLocator.FindAsync(folder));
            var remotes = new GitRemoteService(runner, locator);
            var statusReader = new GitStatusReader(runner, locator, remotes);

            GitRepositoryStatus untracked = await statusReader.ReadAsync(repository);
            Assert.True(untracked.Branch.IsUnborn);
            Assert.Equal(3, untracked.Files.Count(file => file.IsUntracked));

            GitOperationResult staged = await mutations.StageAsync(repository,
                ["-leading dash.txt", "unicodé.txt", ".env"], CancellationToken.None);
            Assert.True(staged.Succeeded, staged.Message);
            GitRepositoryStatus stagedStatus = await statusReader.ReadAsync(repository);
            Assert.Equal(3, stagedStatus.Files.Count(file => file.IsStaged));

            GitOperationResult unstaged = await mutations.UnstageAsync(repository,
                ["-leading dash.txt"], hasHead: false, CancellationToken.None);
            Assert.True(unstaged.Succeeded, unstaged.Message);
            GitOperationResult restaged = await mutations.StageAsync(repository,
                ["-leading dash.txt"], CancellationToken.None);
            Assert.True(restaged.Succeeded, restaged.Message);
            stagedStatus = await statusReader.ReadAsync(repository);

            GitOperationResult committed = await mutations.CommitAsync(
                repository,
                "Test local commit",
                "Created by the temporary-repository integration test.",
                stagedStatus.IndexTreeId,
                CancellationToken.None);
            Assert.True(committed.Succeeded, committed.Message);
            Assert.Contains("Nothing has been uploaded", committed.Message);

            var preflight = new GitPushPreflightService(runner, locator);
            GitPushPreview preview = await preflight.ReadAsync(
                repository, (await statusReader.ReadAsync(repository)).Branch, CancellationToken.None);
            Assert.Equal(1, preview.CommitCount);
            Assert.Contains(".env", preview.SensitivePaths);

            GitRepositoryStatus clean = await statusReader.ReadAsync(repository);
            Assert.False(clean.Branch.IsUnborn);
            Assert.DoesNotContain(clean.Files, file => !file.IsIgnored);

            var branchService = new GitBranchService(runner, locator);
            string originalBranch = clean.Branch.Name!;
            string branchWork = Path.Combine(folder, "branch work.txt");
            await File.WriteAllTextAsync(branchWork, "move this work onto the new branch");
            GitOperationResult createdBranch = await branchService.CreateAndSwitchAsync(
                repository, "feature/test-branch", CancellationToken.None);
            Assert.True(createdBranch.Succeeded, createdBranch.Message);
            Assert.Contains(await branchService.ReadAsync(repository, CancellationToken.None),
                branch => branch.Name == "feature/test-branch" && branch.IsCurrent);
            Assert.True(File.Exists(branchWork));
            File.Delete(branchWork);
            GitOperationResult switchedBranch = await branchService.SwitchAsync(
                repository, originalBranch, CancellationToken.None);
            Assert.True(switchedBranch.Succeeded, switchedBranch.Message);
            GitOperationResult deletedBranch = await branchService.DeleteAsync(
                repository, "feature/test-branch", CancellationToken.None);
            Assert.True(deletedBranch.Succeeded, deletedBranch.Message);

            await RunGit(runner, installation.ExecutablePath, folder,
                "init", "--bare", remoteFolder);
            await RunGit(runner, installation.ExecutablePath, folder,
                "remote", "add", "origin", remoteFolder);
            GitOperationResult pushed = await mutations.PushAsync(
                repository, clean.Branch.Name!, "origin", setUpstream: true, CancellationToken.None);
            Assert.True(pushed.Succeeded, pushed.Message);

            await RunGit(runner, installation.ExecutablePath, folder, "checkout", "--detach");
            GitOperationResult detachedPush = await mutations.PushAsync(
                repository, clean.Branch.Name!, "origin", setUpstream: false, CancellationToken.None);
            Assert.False(detachedPush.Succeeded);
            Assert.Contains("detached", detachedPush.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryRepository(folder);
            DeleteTemporaryRepository(remoteFolder);
        }
    }

    [Fact]
    public async Task CloneUsesTemporaryDirectoryAndCreatesANewChildFolder()
    {
        string source = Path.Combine(Path.GetTempPath(), $"RainExplorer-CloneSource-{Guid.NewGuid():N}.git");
        string destinationParent = Path.Combine(
            Path.GetTempPath(), $"RainExplorer-CloneParent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationParent);
        try
        {
            var runner = new GitProcessRunner();
            var locator = new GitExecutableLocator(runner);
            GitInstallationInfo installation =
                Assert.IsType<GitInstallationInfo>(await locator.FindAsync());
            await RunGit(runner, installation.ExecutablePath, destinationParent,
                "init", "--bare", source);

            var cloneService = new GitCloneService(runner, locator);
            GitCloneResult result = await cloneService.CloneIntoNewChildAsync(
                destinationParent, source, CancellationToken.None);

            Assert.True(result.Succeeded, result.Message);
            Assert.NotNull(result.DestinationPath);
            Assert.True(Directory.Exists(result.DestinationPath));
            Assert.True(Directory.Exists(Path.Combine(result.DestinationPath, ".git")));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(destinationParent),
                path => Path.GetFileName(path).StartsWith(".rain-clone-", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryRepository(destinationParent);
            DeleteTemporaryRepository(source);
        }
    }

    private static async Task RunGit(
        IGitProcessRunner runner,
        string executable,
        string folder,
        params string[] arguments)
    {
        GitProcessResult result = await runner.RunAsync(
            new GitProcessRequest(executable, folder, arguments), CancellationToken.None);
        Assert.True(result.Succeeded, result.StandardError);
    }

    private static void DeleteTemporaryRepository(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }
}
