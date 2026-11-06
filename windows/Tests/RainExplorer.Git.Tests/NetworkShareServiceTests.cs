using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.ComponentModel;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class NetworkShareServiceTests
{
    private sealed class Counter { public int Value; }
    private static readonly ConcurrentDictionary<IntPtr, int> PageLengths = new();
    [Fact]
    public void ReadShares_PagesDiskSharesFiltersSpecialsAndDeduplicatesNames()
    {
        var pages = new[]
        {
            new[]
            {
                Share("Public", 0), Share("PUBLIC", 0), Share("Printer", 1),
                Share("IPC$", 3), Share("ADMIN$", 0x80000000), Share("bad/name", 0),
            },
            new[] { Share("C$", 0x80000000), Share("Media", 0), Share("..", 0) },
        };
        int call = 0;
        var freed = new List<IntPtr>();
        IReadOnlyList<FileItem> result = NetworkShareService.ReadShares(
            @"\\server\", showHidden: false, CancellationToken.None,
            (string server, uint level, out IntPtr buffer, uint preferred, out uint read,
                out uint total, ref uint resume) =>
            {
                Assert.Equal(@"\\server", server);
                Assert.Equal(1u, level);
                Assert.True(preferred > 0);
                buffer = AllocatePage(pages[call]);
                read = (uint)pages[call].Length;
                total = 3;
                resume = call++ == 0 ? 42u : 0u;
                return call == 1 ? 234 : 0;
            }, FreePage(freed));

        Assert.Equal(["Public", "Media"], result.Select(item => item.Name));
        Assert.All(result, item =>
        {
            Assert.Equal(@"\\server\" + item.Name, item.FullPath);
            Assert.True(item.IsDirectory);
            Assert.True(item.IsNetworkShare);
        });
        Assert.Equal(2, freed.Count);
    }

    [Fact]
    public void ReadShares_ShowHiddenIncludesSpecialDiskSharesButStillExcludesPrintersAndIpc()
    {
        var shares = new[]
        {
            Share("Public", 0), Share("ADMIN$", 0x80000000), Share("C$", 0x80000000),
            Share("Printer", 1), Share("IPC$", 3),
        };
        var freed = new Counter();
        IReadOnlyList<FileItem> result = ReadSingle(shares, showHidden: true, freed);

        Assert.Equal(["Public", "ADMIN$", "C$"], result.Select(item => item.Name));
        Assert.Equal(1, freed.Value);
    }

    [Fact]
    public void ReadShares_FreesBufferWhenEnumeratorReturnsAnError()
    {
        var freed = new Counter();
        var error = Assert.Throws<Win32Exception>(() => NetworkShareService.ReadShares(
            @"\\server", false, CancellationToken.None,
            (string _, uint _, out IntPtr buffer, uint _, out uint read, out uint total, ref uint resume) =>
            {
                buffer = AllocatePage([Share("Public", 0)]);
                read = total = 1;
                return 5;
            }, FreePage(freed)));

        Assert.Contains("Couldn't list shared folders", error.Message);
        Assert.Equal(1, freed.Value);
    }

    [Fact]
    public void ReadShares_FreesBufferOnCancellationAfterEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        var freed = new Counter();
        Assert.Throws<OperationCanceledException>(() => NetworkShareService.ReadShares(
            @"\\server", false, cancellation.Token,
            (string _, uint _, out IntPtr buffer, uint _, out uint read, out uint total, ref uint resume) =>
            {
                buffer = AllocatePage([Share("Public", 0)]);
                read = total = 1;
                cancellation.Cancel();
                return 0;
            }, FreePage(freed)));

        Assert.Equal(1, freed.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadShares_RejectsNonProgressingMoreData(bool repeatedResume)
    {
        var freed = new Counter();
        Assert.Throws<IOException>(() => NetworkShareService.ReadShares(
            @"\\server", false, CancellationToken.None,
            (string _, uint _, out IntPtr buffer, uint _, out uint read, out uint total, ref uint resume) =>
            {
                buffer = AllocatePage(repeatedResume
                    ? [Share("Public", 0)]
                    : Array.Empty<NetworkShareService.ShareInfo>());
                read = repeatedResume ? 1u : 0u;
                total = 2;
                resume = repeatedResume ? 7u : 0u;
                return 234;
            }, FreePage(freed)));
        Assert.True(freed.Value >= 1);
    }

    private static IReadOnlyList<FileItem> ReadSingle(
        NetworkShareService.ShareInfo[] shares, bool showHidden, Counter freed)
    {
        bool called = false;
        return NetworkShareService.ReadShares(@"\\server", showHidden, CancellationToken.None,
            (string _, uint _, out IntPtr buffer, uint _, out uint read, out uint total, ref uint resume) =>
            {
                Assert.False(called);
                called = true;
                buffer = AllocatePage(shares);
                read = total = (uint)shares.Length;
                resume = 0;
                return 0;
            }, FreePage(freed));
    }

    private static NetworkShareService.ShareInfo Share(string name, uint type) => new()
    {
        Name = name,
        Type = type,
        Remark = null,
    };

    private static IntPtr AllocatePage(IReadOnlyList<NetworkShareService.ShareInfo> shares)
    {
        int size = Marshal.SizeOf<NetworkShareService.ShareInfo>();
        IntPtr page = Marshal.AllocHGlobal(Math.Max(1, size * shares.Count));
        PageLengths[page] = shares.Count;
        for (int i = 0; i < shares.Count; i++)
            Marshal.StructureToPtr(shares[i], IntPtr.Add(page, i * size), fDeleteOld: false);
        return page;
    }

    private static Action<IntPtr> FreePage(List<IntPtr> freed) => page =>
    {
        freed.Add(page);
        DestroyPage(page);
    };

    private static Action<IntPtr> FreePage(Counter freed) => page =>
    {
        freed.Value++;
        DestroyPage(page);
    };

    private static void DestroyPage(IntPtr page)
    {
        int size = Marshal.SizeOf<NetworkShareService.ShareInfo>();
        if (PageLengths.TryRemove(page, out int count))
            for (int i = 0; i < count; i++)
                Marshal.DestroyStructure<NetworkShareService.ShareInfo>(IntPtr.Add(page, i * size));
        Marshal.FreeHGlobal(page);
    }
}
