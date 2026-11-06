using System.Runtime.ExceptionServices;
using System.Text;
using System.Xml.Linq;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveEntryPreviewTests
{
    [Fact]
    public void StrictDecoderAcceptsOnlyUtf8OrBomProvenUnicodeAndEscapesDisplayControls()
    {
        const string text = "hello 雨";
        foreach ((Encoding encoding, ArchiveEntryPreviewEncoding expected) in new[]
        {
            ((Encoding)new UTF8Encoding(true, true), ArchiveEntryPreviewEncoding.Utf8),
            (new UnicodeEncoding(false, true, true), ArchiveEntryPreviewEncoding.Utf16LittleEndian),
            (new UnicodeEncoding(true, true, true), ArchiveEntryPreviewEncoding.Utf16BigEndian),
            (new UTF32Encoding(false, true, true), ArchiveEntryPreviewEncoding.Utf32LittleEndian),
            (new UTF32Encoding(true, true, true), ArchiveEntryPreviewEncoding.Utf32BigEndian),
        })
        {
            byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            ArchiveEntryPreviewResult result = ArchiveEntryPreviewReader.Decode(bytes);

            Assert.Equal(ArchiveEntryPreviewState.Text, result.State);
            Assert.Equal(text, result.Text);
            Assert.Equal(expected, result.Encoding);
            Assert.Equal(bytes.Length, result.ByteLength);
        }

        ArchiveEntryPreviewResult utf8 = ArchiveEntryPreviewReader.Decode(
            Encoding.UTF8.GetBytes("plain UTF-8"));
        Assert.Equal(ArchiveEntryPreviewState.Text, utf8.State);
        Assert.Equal(ArchiveEntryPreviewEncoding.Utf8, utf8.Encoding);

        Assert.Equal(ArchiveEntryPreviewState.Binary,
            ArchiveEntryPreviewReader.Decode([0xC3, 0x28]).State);
        Assert.Equal(ArchiveEntryPreviewState.Binary,
            ArchiveEntryPreviewReader.Decode(Encoding.Unicode.GetBytes("not BOM-proven")).State);
        Assert.Equal(ArchiveEntryPreviewState.Binary,
            ArchiveEntryPreviewReader.Decode(Encoding.UTF8.GetBytes(new string('\u0001', 10))).State);

        ArchiveEntryPreviewResult escaped = ArchiveEntryPreviewReader.Decode(
            Encoding.UTF8.GetBytes("left\u202Eright\\tail"));
        Assert.Equal(ArchiveEntryPreviewState.Text, escaped.State);
        Assert.Equal("left\\u202Eright\\\\tail", escaped.Text);
    }

    [Fact]
    public async Task ReaderHandlesShortReadsAndEnforcesTheExactOneMibPlusOneLimit()
    {
        byte[] shortReadText = Encoding.UTF8.GetBytes("short reads still form one result 雨");
        await using var shortLease = new TestLease(
            ArchiveSessionId.New(), EntryId.New(), new ChunkedReadStream(shortReadText, 3));

        ArchiveEntryPreviewResult shortResult = await ArchiveEntryPreviewReader.ReadAsync(
            shortLease, CancellationToken.None);

        Assert.Equal(ArchiveEntryPreviewState.Text, shortResult.State);
        Assert.Equal("short reads still form one result 雨", shortResult.Text);

        byte[] exact = Enumerable.Repeat((byte)'a', ArchiveEntryPreviewReader.MaximumTextBytes).ToArray();
        Assert.Equal(ArchiveEntryPreviewState.Text, ArchiveEntryPreviewReader.Decode(exact).State);

        byte[] plusOne = Enumerable.Repeat((byte)'a', ArchiveEntryPreviewReader.StreamLimitBytes).ToArray();
        await using var oversizeLease = new TestLease(
            ArchiveSessionId.New(), EntryId.New(), new ChunkedReadStream(plusOne, 997));
        Assert.Equal(ArchiveEntryPreviewState.Oversize,
            (await ArchiveEntryPreviewReader.ReadAsync(oversizeLease, CancellationToken.None)).State);

        var unread = new CountingReadStream();
        await using var declaredOversize = new TestLease(
            ArchiveSessionId.New(), EntryId.New(), unread,
            ArchiveEntryPreviewReader.MaximumTextBytes + 1L);
        Assert.Equal(ArchiveEntryPreviewState.Oversize,
            (await ArchiveEntryPreviewReader.ReadAsync(declaredOversize, CancellationToken.None)).State);
        Assert.Equal(0, unread.ReadCount);
    }

    [Fact]
    public async Task ReplacementKeepsOnlyNewestPendingAndDisposesBeforeSuccessorAdmission()
    {
        EntryId entryId = EntryId.New();
        var fake = PreviewClient(entryId);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var blocking = new BlockingReadStream();
        var releaseFirstDispose = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TestLease? firstLease = null;
        int factoryCalls = 0;
        int liveLeases = 0;
        int maximumLiveLeases = 0;
        bool successorOpenedBeforeCleanup = false;

        Task<IArchiveEntryPreviewLease> Factory(
            IArchiveClient _, ArchiveSessionId sessionId, EntryId openedEntry,
            long maximumBytes, CancellationToken __)
        {
            Assert.Equal(ArchiveEntryPreviewReader.StreamLimitBytes, maximumBytes);
            int call = Interlocked.Increment(ref factoryCalls);
            int live = Interlocked.Increment(ref liveLeases);
            maximumLiveLeases = Math.Max(maximumLiveLeases, live);
            if (call == 1)
            {
                firstLease = new TestLease(sessionId, openedEntry, blocking, onDispose: async () =>
                {
                    await releaseFirstDispose.Task;
                    Interlocked.Decrement(ref liveLeases);
                });
                return Task.FromResult<IArchiveEntryPreviewLease>(firstLease);
            }

            successorOpenedBeforeCleanup = firstLease?.DisposeCount != 1 || live != 1;
            return Task.FromResult<IArchiveEntryPreviewLease>(new TestLease(
                sessionId, openedEntry,
                new MemoryStream(Encoding.UTF8.GetBytes("newest"), writable: false),
                onDispose: () =>
                {
                    Interlocked.Decrement(ref liveLeases);
                    return ValueTask.CompletedTask;
                }));
        }

        await using var adapter = new ArchiveLocationAdapter(runtime, Factory);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "preview queue.zip"));
        FileItem item = Assert.Single(adapter.Items);

        Task<ArchiveEntryPreviewResult> first = adapter.LoadPreviewAsync(item);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ArchiveEntryPreviewResult> superseded = adapter.LoadPreviewAsync(item);
        await firstLease!.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ArchiveEntryPreviewResult> newest = adapter.LoadPreviewAsync(item);

        Assert.Equal(ArchiveEntryPreviewState.Cancelled,
            (await superseded.WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.False(newest.IsCompleted);
        releaseFirstDispose.TrySetResult();

        Assert.Equal(ArchiveEntryPreviewState.Cancelled,
            (await first.WaitAsync(TimeSpan.FromSeconds(2))).State);
        ArchiveEntryPreviewResult newestResult = await newest.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ArchiveEntryPreviewState.Text, newestResult.State);
        Assert.Equal("newest", newestResult.Text);
        Assert.Equal(2, factoryCalls);
        Assert.Equal(1, maximumLiveLeases);
        Assert.False(successorOpenedBeforeCleanup);
        Assert.Equal(0, liveLeases);
        Assert.Equal(1, firstLease.DisposeCount);
    }

    [Fact]
    public async Task ReplacementAtCompletionBoundaryCannotPublishOldTextOrRaceDisposedCancellation()
    {
        EntryId entryId = EntryId.New();
        var fake = PreviewClient(entryId);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var completionReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int leases = 0;
        int completionHooks = 0;

        Task<IArchiveEntryPreviewLease> Factory(
            IArchiveClient _, ArchiveSessionId sessionId, EntryId openedEntry,
            long __, CancellationToken ___)
        {
            int leaseNumber = Interlocked.Increment(ref leases);
            return Task.FromResult<IArchiveEntryPreviewLease>(new TestLease(
                sessionId,
                openedEntry,
                new MemoryStream(Encoding.UTF8.GetBytes(
                    leaseNumber == 1 ? "old text" : "new text"), writable: false)));
        }

        async Task BeforeCompletion()
        {
            if (Interlocked.Increment(ref completionHooks) != 1) return;
            completionReached.TrySetResult();
            await releaseCompletion.Task;
        }

        await using var adapter = new ArchiveLocationAdapter(runtime, Factory, BeforeCompletion);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "completion boundary.zip"));
        FileItem item = Assert.Single(adapter.Items);

        Task<ArchiveEntryPreviewResult> old = adapter.LoadPreviewAsync(item);
        await completionReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ArchiveEntryPreviewResult> newest = adapter.LoadPreviewAsync(item);
        Assert.False(old.IsCompleted);
        releaseCompletion.TrySetResult();

        ArchiveEntryPreviewResult oldResult = await old.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ArchiveEntryPreviewState.Cancelled, oldResult.State);
        Assert.Empty(oldResult.Text);
        ArchiveEntryPreviewResult newestResult = await newest.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ArchiveEntryPreviewState.Text, newestResult.State);
        Assert.Equal("new text", newestResult.Text);

        Task<ArchiveEntryPreviewResult>[] stress = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => adapter.LoadPreviewAsync(item)))
            .ToArray();
        ArchiveEntryPreviewResult[] stressResults = await Task.WhenAll(stress);
        Assert.All(stressResults, result => Assert.Contains(result.State,
            new[] { ArchiveEntryPreviewState.Text, ArchiveEntryPreviewState.Cancelled }));
    }

    [Fact]
    public async Task NavigationAndWorkerLossSettleActiveLeaseWithFixedStates()
    {
        EntryId entryId = EntryId.New();
        var fake = PreviewClient(entryId);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var firstStream = new BlockingReadStream();
        var secondStream = new BlockingReadStream();
        var releaseWorkerLossCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        TestLease? firstLease = null;
        TestLease? secondLease = null;

        Task<IArchiveEntryPreviewLease> Factory(
            IArchiveClient _, ArchiveSessionId sessionId, EntryId openedEntry,
            long __, CancellationToken ___)
        {
            if (Interlocked.Increment(ref calls) == 1)
                return Task.FromResult<IArchiveEntryPreviewLease>(
                    firstLease = new TestLease(sessionId, openedEntry, firstStream));
            return Task.FromResult<IArchiveEntryPreviewLease>(
                secondLease = new TestLease(
                    sessionId,
                    openedEntry,
                    secondStream,
                    onDispose: async () => await releaseWorkerLossCleanup.Task));
        }

        await using var adapter = new ArchiveLocationAdapter(runtime, Factory);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "preview lifecycle.zip"));
        int changedCount = 0;
        adapter.Changed += (_, _) => Interlocked.Increment(ref changedCount);
        FileItem item = Assert.Single(adapter.Items);

        Task<ArchiveEntryPreviewResult> navigationPreview = adapter.LoadPreviewAsync(item);
        await firstStream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        adapter.SetSearch("missing", recursive: false);
        Assert.Equal(ArchiveEntryPreviewState.Cancelled,
            (await navigationPreview.WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.Equal(1, firstLease!.DisposeCount);

        Assert.Equal(ArchiveEntryPreviewState.Stale,
            (await adapter.LoadPreviewAsync(item).WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.Equal(1, calls);

        adapter.SetSearch(string.Empty, recursive: false);
        item = Assert.Single(adapter.Items);
        Task<ArchiveEntryPreviewResult> workerLossPreview = adapter.LoadPreviewAsync(item);
        await secondStream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.InvalidateAsync(fake.Client);

        await secondLease!.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(workerLossPreview.IsCompleted);
        Assert.Equal(ArchiveLocationState.Ready, adapter.State);
        Assert.True(adapter.IsOpen);
        Assert.Single(adapter.Items);
        releaseWorkerLossCleanup.TrySetResult();

        ArchiveEntryPreviewResult workerLossResult =
            await workerLossPreview.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ArchiveEntryPreviewState.Offline, workerLossResult.State);
        Assert.Empty(workerLossResult.Text);
        await WaitUntilAsync(() => adapter.State == ArchiveLocationState.Offline);
        Assert.Equal(1, secondLease.DisposeCount);
        Assert.Equal(ArchiveLocationState.Offline, adapter.State);
        Assert.Empty(adapter.Items);
        Assert.False(adapter.IsOpen);
        Assert.Equal(0, fake.CloseCount);
        Assert.Equal(1, fake.DisposeClientCount);

        await adapter.DisposeAsync();
        int changesAfterDispose = changedCount;
        await Task.Delay(50);
        Assert.Equal(changesAfterDispose, changedCount);
        Assert.Equal(0, fake.CloseCount);
        Assert.Equal(1, secondLease.DisposeCount);
    }

    [Fact]
    public async Task TabDisposalDrainsPreviewBeforeClosingItsArchiveSession()
    {
        EntryId entryId = EntryId.New();
        var fake = PreviewClient(entryId);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var stream = new BlockingReadStream();
        TestLease? lease = null;
        Task<IArchiveEntryPreviewLease> Factory(
            IArchiveClient _, ArchiveSessionId sessionId, EntryId openedEntry,
            long __, CancellationToken ___) => Task.FromResult<IArchiveEntryPreviewLease>(
                lease = new TestLease(sessionId, openedEntry, stream));
        var tab = new TabViewModel(new FileSystemService(), runtime, Factory);
        var location = new ArchiveLocation(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tab preview.zip")),
            Fingerprint(), string.Empty, null);
        Assert.True(ArchiveLocationSerializer.TrySerialize(location, out string target));
        await tab.NavigateAsync(target, pushHistory: true);
        await WaitUntilAsync(() => tab.IsArchiveView && !tab.Busy);

        Task<ArchiveEntryPreviewResult> preview = tab.LoadArchivePreviewAsync(Assert.Single(tab.Items));
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await tab.DisposeAsync();

        Assert.Equal(ArchiveEntryPreviewState.Cancelled,
            (await preview.WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.Equal(1, lease!.DisposeCount);
        Assert.Equal(1, fake.CloseCount);
        Assert.Single(fake.ClosedSessions);
    }

    [Fact]
    public async Task ClientFailuresAndMismatchedLeaseIdentityMapToFixedProductStates()
    {
        foreach ((string code, ArchiveErrorCategory category, ArchiveEntryPreviewState expected) in new[]
        {
            ("provider.raw.code", ArchiveErrorCategory.PasswordRequired, ArchiveEntryPreviewState.PasswordRequired),
            ("provider.raw.code", ArchiveErrorCategory.SourceChanged, ArchiveEntryPreviewState.Stale),
            ("provider.raw.code", ArchiveErrorCategory.WorkerFailure, ArchiveEntryPreviewState.Offline),
            ("provider.raw.code", ArchiveErrorCategory.UnsupportedCapability, ArchiveEntryPreviewState.Unsupported),
            ("provider.raw.code", ArchiveErrorCategory.InvalidArchive, ArchiveEntryPreviewState.Error),
            ("entry.stream_byte_overflow", ArchiveErrorCategory.ResourceLimit, ArchiveEntryPreviewState.Oversize),
        })
        {
            EntryId entryId = EntryId.New();
            var fake = PreviewClient(entryId);
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            await using var adapter = new ArchiveLocationAdapter(runtime, (
                IArchiveClient _, ArchiveSessionId __, EntryId ___, long ____, CancellationToken _____) =>
                Task.FromException<IArchiveEntryPreviewLease>(new ArchiveClientException(
                    new ArchiveError(code, category, "provider raw failure text"))));
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "preview errors.zip"));

            ArchiveEntryPreviewResult result = await adapter.LoadPreviewAsync(Assert.Single(adapter.Items));

            Assert.Equal(expected, result.State);
            Assert.Equal(ArchiveEntryPreviewResult.FixedStatus(expected), result.Status);
            Assert.DoesNotContain("provider", result.Status, StringComparison.OrdinalIgnoreCase);
        }

        EntryId mismatchEntry = EntryId.New();
        var mismatchClient = PreviewClient(mismatchEntry);
        await using var mismatchRuntime = new ArchiveClientRuntime(
            _ => Task.FromResult(mismatchClient.Client));
        TestLease? mismatchedLease = null;
        await using var mismatchAdapter = new ArchiveLocationAdapter(mismatchRuntime, (
            IArchiveClient _, ArchiveSessionId __, EntryId entryId, long ___, CancellationToken ____) =>
            Task.FromResult<IArchiveEntryPreviewLease>(mismatchedLease = new TestLease(
                ArchiveSessionId.New(), entryId,
                new MemoryStream(Encoding.UTF8.GetBytes("must not publish"), writable: false))));
        await mismatchAdapter.OpenAsync(Path.Combine(Path.GetTempPath(), "mismatched session.zip"));

        ArchiveEntryPreviewResult mismatch = await mismatchAdapter.LoadPreviewAsync(
            Assert.Single(mismatchAdapter.Items));

        Assert.Equal(ArchiveEntryPreviewState.Stale, mismatch.State);
        Assert.Equal(string.Empty, mismatch.Text);
        Assert.Equal(1, mismatchedLease!.DisposeCount);
    }

    [Fact]
    public async Task SourceChangedAfterStreamAdmissionDisposesLeaseAndFailsSessionClosed()
    {
        EntryId entryId = EntryId.New();
        var fake = PreviewClient(entryId);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        TestLease? lease = null;
        await using var adapter = new ArchiveLocationAdapter(runtime, (
            IArchiveClient _, ArchiveSessionId sessionId, EntryId openedEntry,
            long __, CancellationToken ___) => Task.FromResult<IArchiveEntryPreviewLease>(
                lease = new TestLease(
                    sessionId,
                    openedEntry,
                    new ThrowingReadStream(new ArchiveClientException(new ArchiveError(
                        "provider.raw.source", ArchiveErrorCategory.SourceChanged,
                        "provider raw source text"))))));
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "source changed preview.zip"));

        ArchiveEntryPreviewResult result = await adapter.LoadPreviewAsync(Assert.Single(adapter.Items));

        Assert.Equal(ArchiveEntryPreviewState.Stale, result.State);
        Assert.Equal(ArchiveEntryPreviewResult.FixedStatus(ArchiveEntryPreviewState.Stale), result.Status);
        Assert.Empty(result.Text);
        Assert.Equal(1, lease!.DisposeCount);
        Assert.Equal(ArchiveLocationState.Stale, adapter.State);
        Assert.Empty(adapter.Items);
        Assert.False(adapter.IsOpen);
        Assert.Equal(1, fake.CloseCount);

        await adapter.DisposeAsync();
        Assert.Equal(1, fake.CloseCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task TransportFailureInvalidatesExactClientAndExplicitReopenUsesFreshClient()
    {
        EntryId firstEntry = EntryId.New();
        EntryId secondEntry = EntryId.New();
        var first = PreviewClient(firstEntry);
        var second = PreviewClient(secondEntry);
        int connections = 0;
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(
            Interlocked.Increment(ref connections) == 1 ? first.Client : second.Client));
        TestLease? freshLease = null;

        Task<IArchiveEntryPreviewLease> Factory(
            IArchiveClient client, ArchiveSessionId sessionId, EntryId openedEntry,
            long __, CancellationToken ___)
        {
            if (ReferenceEquals(client, first.Client))
                return Task.FromException<IArchiveEntryPreviewLease>(
                    new IOException("transport detail must not escape"));
            return Task.FromResult<IArchiveEntryPreviewLease>(freshLease = new TestLease(
                sessionId,
                openedEntry,
                new MemoryStream(Encoding.UTF8.GetBytes("fresh client"), writable: false)));
        }

        await using var adapter = new ArchiveLocationAdapter(runtime, Factory);
        string source = Path.Combine(Path.GetTempPath(), "transport preview.zip");
        await adapter.OpenAsync(source);

        ArchiveEntryPreviewResult failed = await adapter.LoadPreviewAsync(Assert.Single(adapter.Items));
        Assert.Equal(ArchiveEntryPreviewState.Offline, failed.State);
        Assert.Equal(ArchiveEntryPreviewResult.FixedStatus(ArchiveEntryPreviewState.Offline), failed.Status);
        Assert.Empty(failed.Text);
        await WaitUntilAsync(() => adapter.State == ArchiveLocationState.Offline);
        Assert.Empty(adapter.Items);
        Assert.False(adapter.IsOpen);
        Assert.Equal(1, first.DisposeClientCount);
        Assert.Equal(0, first.CloseCount);

        await adapter.OpenAsync(source);
        Assert.Equal(ArchiveLocationState.Ready, adapter.State);
        ArchiveEntryPreviewResult reopened = await adapter.LoadPreviewAsync(Assert.Single(adapter.Items));
        Assert.Equal(ArchiveEntryPreviewState.Text, reopened.State);
        Assert.Equal("fresh client", reopened.Text);
        Assert.Equal(2, connections);
        Assert.Equal(1, freshLease!.DisposeCount);

        await adapter.DisposeAsync();
        Assert.Equal(1, second.CloseCount);
    }

    [Fact]
    public void PreviewSurfaceKeepsArchiveContentInertAccessibleAndOutOfPhysicalHandlers()
    {
        RunSta(() =>
        {
            string root = FindRepositoryRoot();
            string previewSource = File.ReadAllText(
                Path.Combine(root, "Main", "Controls", "PreviewPane.xaml.cs"));
            string paneSource = File.ReadAllText(
                Path.Combine(root, "Main", "Controls", "PaneView.xaml.cs"));
            string xaml = File.ReadAllText(
                Path.Combine(root, "Main", "Controls", "PreviewPane.xaml"));
            string archiveMethod = previewSource[
                previewSource.IndexOf("public async void ShowArchiveItem", StringComparison.Ordinal)..previewSource.IndexOf("// ---- Audio / video playback", StringComparison.Ordinal)];

            Assert.Contains("Preview.ShowArchiveItem(_tab, previewItem)", paneSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain("FullPath", archiveMethod, StringComparison.Ordinal);
            Assert.DoesNotContain("FileStream", archiveMethod, StringComparison.Ordinal);
            Assert.DoesNotContain("new Uri", archiveMethod, StringComparison.Ordinal);
            Assert.DoesNotContain(".Focus(", archiveMethod, StringComparison.Ordinal);
            Assert.Contains("_archivePreviewGate.WaitAsync(cts.Token)", archiveMethod,
                StringComparison.Ordinal);
            Assert.Contains("AutomationProperties.Name=\"Archive or file text preview\"", xaml,
                StringComparison.Ordinal);
            Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml,
                StringComparison.Ordinal);

            Assert.Contains("TryShowImage(item, cts.Token)", previewSource, StringComparison.Ordinal);
            Assert.Contains("TryShowPdf(item, cts.Token)", previewSource, StringComparison.Ordinal);
            Assert.Contains("TryShowText(item, cts.Token)", previewSource, StringComparison.Ordinal);
            Assert.Contains("TryShowThumbnail(item, cts.Token)", previewSource, StringComparison.Ordinal);

            string serviceSource = File.ReadAllText(
                Path.Combine(root, "Main", "Services", "ArchiveEntryPreview.cs"));
            string adapterSource = File.ReadAllText(
                Path.Combine(root, "Main", "Services", "ArchiveLocationAdapter.cs"));
            Assert.DoesNotContain("ArchivePreviewCache", serviceSource + adapterSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Path.GetTempPath", serviceSource, StringComparison.Ordinal);

            string projectPath = Path.Combine(root, "Main", "RainExplorer.csproj");
            string[] projectReferences = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => include is not null)
                .Cast<string>()
                .ToArray();
            Assert.Equal(2, projectReferences.Length);
            Assert.Contains(projectReferences,
                include => include.EndsWith(
                    @"RainArchives.Contracts\RainArchives.Contracts.csproj",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(projectReferences,
                include => include.EndsWith(
                    @"RainArchives.Client\RainArchives.Client.csproj",
                    StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void StateSurfaceIsFixedAndContainsNoExceptionOrProviderText()
    {
        ArchiveEntryPreviewState[] states = Enum.GetValues<ArchiveEntryPreviewState>();
        Assert.Equal(10, states.Length);
        foreach (ArchiveEntryPreviewState state in states)
        {
            string status = ArchiveEntryPreviewResult.FixedStatus(state);
            Assert.NotEmpty(status);
            Assert.DoesNotContain("provider", status, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exception", status, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ArchiveLocationAdapterTests.FakeArchiveClient PreviewClient(EntryId entryId) =>
        ArchiveLocationAdapterTests.FakeArchiveClient.Create(
            [Entry(entryId)],
            capabilities: ArchiveCapability.List | ArchiveCapability.OpenEntryStream);

    private static ArchiveEntryDescriptor Entry(EntryId id) => new(
        id,
        ParentEntryId: null,
        ProviderOrdinal: 0,
        RawPath: "preview.txt",
        DisplayName: "preview.txt",
        Kind: ArchiveEntryKind.File,
        OriginalSize: 10,
        CompressedSize: null,
        ModifiedTimeUtc: null,
        WindowsAttributes: null,
        CompressionMethod: null,
        IntegrityValue: null,
        IsEncrypted: false,
        IsSolid: false,
        LinkTarget: null,
        SafetyStatus: EntrySafetyStatus.Safe,
        SafetyReason: null,
        NormalizedPath: "preview.txt");

    private static ArchiveFingerprint Fingerprint() => new(
        123,
        new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
        "volume:file",
        new string('A', 64));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Main", "Controls", "PreviewPane.xaml")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Rain Explorer repository root was not found.");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TestLease : IArchiveEntryPreviewLease
    {
        private readonly Func<ValueTask>? _onDispose;

        public TestLease(
            ArchiveSessionId sessionId,
            EntryId entryId,
            Stream content,
            long? declaredLength = null,
            Func<ValueTask>? onDispose = null)
        {
            SessionId = sessionId;
            EntryId = entryId;
            Content = content;
            DeclaredLength = declaredLength;
            _onDispose = onDispose;
        }

        public ArchiveSessionId SessionId { get; }
        public EntryId EntryId { get; }
        public long? DeclaredLength { get; }
        public Stream Content { get; }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            if (_onDispose is not null) await _onDispose();
            await Content.DisposeAsync();
        }
    }

    private sealed class ChunkedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CountingReadStream : Stream
    {
        public int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult(0);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingReadStream(Exception failure) : Stream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
