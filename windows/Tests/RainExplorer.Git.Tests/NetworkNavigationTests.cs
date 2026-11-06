using System.Reflection;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class NetworkNavigationTests
{
    [Fact]
    public async Task ServerRootNavigationUsesSharesAndDisablesPhysicalCommandsAndUp()
    {
        var share = new FileItem
        {
            Name = "Public", FullPath = @"\\server\Public", IsDirectory = true,
            IsNetworkShare = true,
        };
        using var tab = NewTab((path, _, _) => [share]);

        await tab.NavigateAsync(@"\\\server\", true);

        Assert.Equal(@"\\server", tab.CurrentPath);
        Assert.Same(share, Assert.Single(tab.Items));
        Assert.False(tab.CanUsePhysicalFileCommands);
        Assert.False(tab.CanGoUp);
    }

    [Fact]
    public async Task SharePathEnablesPhysicalCommandsAndUpReturnsToItsServer()
    {
        using var tab = NewTab((path, _, _) => []);

        tab.CurrentPath = @"\\server\Public";

        Assert.Equal(@"\\server\Public", tab.CurrentPath);
        Assert.True(tab.CanUsePhysicalFileCommands);
        Assert.True(tab.CanGoUp);
        Assert.Equal(@"\\server", tab.UpCommand.CanExecute(null)
            ? GetParent(tab.CurrentPath) : null);
        var navigated = WaitForContents(tab);
        tab.UpCommand.Execute(null);
        await navigated;
        Assert.Equal(@"\\server", tab.CurrentPath);
        Assert.False(tab.CanUsePhysicalFileCommands);
    }

    [Fact]
    public async Task BackAndForwardNavigateBetweenNetworkServers()
    {
        using var tab = NewTab((path, _, _) =>
            [new FileItem { Name = path[2..], FullPath = path + @"\Share", IsDirectory = true }]);
        await tab.NavigateAsync(@"\\server-a", true);
        await tab.NavigateAsync(@"\\server-b", true);

        var back = WaitForContents(tab);
        tab.BackCommand.Execute(null);
        await back;
        Assert.Equal(@"\\server-a", tab.CurrentPath);

        var forward = WaitForContents(tab);
        tab.ForwardCommand.Execute(null);
        await forward;
        Assert.Equal(@"\\server-b", tab.CurrentPath);
    }

    [Fact]
    public async Task SlowStaleServerResultCannotOverwriteNewerNavigation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tab = NewTab((path, _, _) =>
        {
            if (path.Equals(@"\\slow", StringComparison.OrdinalIgnoreCase))
            {
                entered.SetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
            return [];
        });

        Task stale = tab.NavigateAsync(@"\\slow", true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await tab.NavigateAsync(@"\\new", true);
        }
        finally { release.TrySetResult(true); }
        await stale;

        Assert.Equal(@"\\new", tab.CurrentPath);
    }

    [Fact]
    public async Task OfflineRestoredServerRetainsItsTargetAndCanBeRefreshed()
    {
        bool offline = true;
        using var tab = NewTab((path, _, _) =>
        {
            if (offline) throw new IOException("offline");
            return [];
        });
        await tab.NavigateAsync(@"\\server", true);
        Assert.Equal(@"\\server", tab.RestoreTarget);
        Assert.False(tab.Busy);

        offline = false;
        await tab.ReloadAsync();
        Assert.Equal(@"\\server", tab.CurrentPath);
        Assert.Equal(@"\\server", tab.RestoreTarget);
    }

    [Fact]
    public async Task FailedServerNavigationPreservesCurrentSuccessfulView()
    {
        var existing = new FileItem { Name = "Keep", FullPath = @"\\good\Keep", IsDirectory = true };
        using var tab = NewTab((path, _, _) =>
        {
            if (path.Equals(@"\\bad", StringComparison.OrdinalIgnoreCase))
                throw new IOException("offline");
            return [existing];
        });
        await tab.NavigateAsync(@"\\good", true);

        await tab.NavigateAsync(@"\\bad", true);

        Assert.Equal(@"\\good", tab.CurrentPath);
        Assert.Same(existing, Assert.Single(tab.Items));
        Assert.StartsWith("⚠️", tab.Status);
    }

    [Theory]
    [InlineData(@"\\server", new[] { "server" })]
    [InlineData(@"\\server\Public\Folder", new[] { "server", "Public", "Folder" })]
    public void BreadcrumbsContainServerShareAndDeepSegments(string path, string[] labels)
    {
        Type breadcrumb = typeof(RainExplorer.Controls.BreadcrumbBar);
        MethodInfo method = breadcrumb.GetMethod("BuildCrumbs", BindingFlags.NonPublic | BindingFlags.Static)!;
        var crumbs = ((System.Collections.IEnumerable)method.Invoke(null, [path])!).Cast<object>().ToList();
        Assert.Equal(labels, crumbs.Select(c => (string)c.GetType().GetProperty("Label")!.GetValue(c)!).ToArray());
        Assert.Equal(NetworkPath.Normalize(path),
            (string)crumbs[^1].GetType().GetProperty("Path")!.GetValue(crumbs[^1])!);
    }

    [Theory]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\Public")]
    [InlineData(@"\\server\Public\Folder")]
    public void NetworkServerAndShareTargetsAreRestorable(string path)
    {
        foreach (Type type in new[] { typeof(PaneViewModel), typeof(MainViewModel) })
        {
            MethodInfo method = type.GetMethod("IsRestorableTarget",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.True((bool)method.Invoke(null, [path])!);
        }
    }

    private static TabViewModel NewTab(
        Func<string, bool, CancellationToken, IReadOnlyList<FileItem>> reader) =>
        new(new FileSystemService(reader));

    private static Task WaitForContents(TabViewModel tab)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.ContentsChanged += Complete;
        return AwaitAndDetach();

        void Complete() => completed.TrySetResult(true);
        async Task AwaitAndDetach()
        {
            try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { tab.ContentsChanged -= Complete; }
        }
    }

    private static string? GetParent(string path) => new FileSystemService().GetParent(path);
}
