using System.Reflection;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveRebuildCopyTests
{
    [Fact]
    public async Task EligibleZipRebuildUsesExactCurrentAuthorityAndEmptyCopyEdits()
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("source archive.zip", "original bytes");
        string destination = files.PathFor("rebuilt copy.zip");
        byte[] sourceBefore = File.ReadAllBytes(source);
        var fake = RebuildClient.Create();
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);

        await adapter.OpenAsync(source);
        Assert.True(adapter.CanRebuildCopy);
        ArchiveRebuildCapabilitySnapshot authority = await adapter.LoadRebuildCapabilitiesAsync();
        Assert.True(authority.IsReady);

        ArchiveOperationResult result = await adapter.RebuildCopyAsync(new ArchiveRebuildRequest(
            authority, destination, ArchiveCompressionPreset.Maximum));

        Assert.Equal(ArchiveOperationOutcome.Completed, result.Outcome);
        Assert.Equal(1, fake.RebuildStartCount);
        Assert.Equal(authority.SessionId, fake.LastRebuildSession);
        Assert.Equal(destination, fake.LastDestination);
        Assert.Empty(fake.LastDeletes);
        Assert.Empty(fake.LastFiles);
        Assert.Equal(ArchiveCompressionPreset.Maximum, fake.LastPreset);
        Assert.Equal(0, fake.SecretCreateCount);
        Assert.Equal(sourceBefore, File.ReadAllBytes(source));
        Assert.Contains(sink.Updates, update =>
            update.Kind == ActivityOperationKind.ArchiveRebuild
            && update.Phase == ActivityOperationPhase.Running
            && update.BytesProcessed == 5
            && update.TotalBytes == 10
            && update.CurrentItem == "payload.txt");
        Assert.Equal(ActivityOperationPhase.Completed, sink.Updates[^1].Phase);
    }

    [Fact]
    public async Task RebuildDestinationRacePreservesSentinelAndDoesNotRetryOrDelete()
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("original.zip", "original");
        string destination = files.PathFor("race output.zip");
        var fake = RebuildClient.Create();
        fake.RejectExistingDestination = true;
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(
            runtime,
            static (_, _, _, _, _) => throw new NotSupportedException(),
            activitySink: sink,
            rebuildBeforeStart: () =>
            {
                File.WriteAllText(destination, "sentinel owned by another process");
                return Task.CompletedTask;
            });

        await adapter.OpenAsync(source);
        ArchiveRebuildCapabilitySnapshot authority = await adapter.LoadRebuildCapabilitiesAsync();
        ArchiveOperationResult result = await adapter.RebuildCopyAsync(new ArchiveRebuildRequest(
            authority, destination, ArchiveCompressionPreset.Balanced));

        Assert.Equal(ArchiveOperationOutcome.DestinationConflict, result.Outcome);
        Assert.Equal(1, fake.RebuildStartCount);
        Assert.Equal("sentinel owned by another process", File.ReadAllText(destination));
        Assert.Equal(ActivityOperationFailure.DestinationConflict, sink.Updates[^1].Failure);
    }

    [Fact]
    public async Task CallerCancellationDuringStartWaitsForIdThenCancelsThatExactOperation()
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("cancel source.zip", "source");
        string destination = files.PathFor("cancel destination.zip");
        var fake = RebuildClient.Create();
        fake.BlockStart = true;
        fake.BlockTerminal = true;
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(source);
        ArchiveRebuildCapabilitySnapshot authority = await adapter.LoadRebuildCapabilitiesAsync();
        using var cancellation = new CancellationTokenSource();

        Task<ArchiveOperationResult> pending = adapter.RebuildCopyAsync(new ArchiveRebuildRequest(
            authority, destination, ArchiveCompressionPreset.Balanced), cancellation.Token);
        await fake.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        Assert.False(fake.LastStartToken.IsCancellationRequested);
        fake.ReleaseStart.TrySetResult();
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fake.CompleteTerminal(OperationPhase.Cancelled);
        ArchiveOperationResult result = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ArchiveOperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, fake.RebuildStartCount);
        Assert.Equal(1, fake.CancelCallCount);
        Assert.Equal(fake.StartedOperationId, fake.CancelledOperationId);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task UnexpectedWaitingPhaseIsCancelledAndReportedAsUnsupported()
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("waiting source.zip", "source");
        var fake = RebuildClient.Create();
        fake.EmitUnexpectedWaiting = true;
        fake.BlockTerminal = true;
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(source);
        ArchiveRebuildCapabilitySnapshot authority = await adapter.LoadRebuildCapabilitiesAsync();

        Task<ArchiveOperationResult> pending = adapter.RebuildCopyAsync(new ArchiveRebuildRequest(
            authority, files.PathFor("waiting output.zip"), ArchiveCompressionPreset.Balanced));
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fake.CompleteTerminal(OperationPhase.Cancelled);

        Assert.Equal(ArchiveOperationOutcome.Unsupported,
            (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
        Assert.Equal(1, fake.CancelCallCount);
        Assert.Equal(fake.StartedOperationId, fake.CancelledOperationId);
        Assert.Equal(ActivityOperationFailure.Unsupported, sink.Updates[^1].Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignInteractivePhaseBeforeOrAfterOperationIdDoesNotCancelRebuild(
        bool beforeOperationId)
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("foreign source.zip", "source");
        var fake = RebuildClient.Create();
        fake.BlockStart = true;
        fake.BlockTerminal = true;
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(source);
        ArchiveRebuildCapabilitySnapshot authority = await adapter.LoadRebuildCapabilitiesAsync();

        Task<ArchiveOperationResult> pending = adapter.RebuildCopyAsync(new ArchiveRebuildRequest(
            authority,
            files.PathFor("foreign output.zip"),
            ArchiveCompressionPreset.Balanced));
        await fake.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        if (!beforeOperationId)
        {
            fake.ReleaseStart.TrySetResult();
            await fake.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        OperationId foreignOperationId = OperationId.New();
        Assert.NotEqual(fake.StartedOperationId, foreignOperationId);
        fake.Emit(new OperationEvent(
            foreignOperationId,
            2,
            OperationEventKind.StateChanged,
            OperationPhase.AwaitingConflict));
        Assert.Equal(0, fake.CancelCallCount);

        if (beforeOperationId)
        {
            fake.ReleaseStart.TrySetResult();
            await fake.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        fake.CompleteTerminal(OperationPhase.Completed);

        Assert.Equal(
            ArchiveOperationOutcome.Completed,
            (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
        Assert.Equal(0, fake.CancelCallCount);
    }

    [Fact]
    public async Task NonfatalCancellationRaceKeepsSharedRuntimeAvailable()
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("cancel race source.zip", "source");
        var fake = RebuildClient.Create();
        fake.BlockStart = true;
        fake.BlockTerminal = true;
        fake.CancelErrorCategory = ArchiveErrorCategory.InvalidRequest;
        int factoryCalls = 0;
        await using var runtime = new ArchiveClientRuntime(_ =>
        {
            factoryCalls++;
            return Task.FromResult(fake.Client);
        });
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(source);
        ArchiveRebuildCapabilitySnapshot authority = await adapter.LoadRebuildCapabilitiesAsync();
        using var cancellation = new CancellationTokenSource();

        Task<ArchiveOperationResult> pending = adapter.RebuildCopyAsync(new ArchiveRebuildRequest(
            authority,
            files.PathFor("cancel race output.zip"),
            ArchiveCompressionPreset.Balanced), cancellation.Token);
        await fake.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        fake.ReleaseStart.TrySetResult();
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fake.CompleteTerminal(OperationPhase.Cancelled);

        Assert.Equal(
            ArchiveOperationOutcome.Cancelled,
            (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
        Assert.Same(fake.Client, await runtime.GetClientAsync(CancellationToken.None));
        Assert.Equal(1, factoryCalls);
        Assert.Equal(0, fake.DisposeCalls);
    }

    [Fact]
    public async Task RebuildAuthorityFromAnOlderSessionIsRejectedBeforeStart()
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("stale.zip", "source");
        var fake = RebuildClient.Create();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(source);
        ArchiveRebuildCapabilitySnapshot stale = await adapter.LoadRebuildCapabilitiesAsync();

        await adapter.OpenAsync(source);
        ArchiveOperationResult result = await adapter.RebuildCopyAsync(new ArchiveRebuildRequest(
            stale, files.PathFor("stale copy.zip"), ArchiveCompressionPreset.Balanced));

        Assert.Equal(ArchiveOperationOutcome.Stale, result.Outcome);
        Assert.Equal(0, fake.RebuildStartCount);
    }

    [Fact]
    public async Task SourceChangedCapabilityResponseTransitionsTheOpenTabToStale()
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("changed.zip", "source");
        var fake = RebuildClient.Create();
        fake.CapabilityFailure = ArchiveErrorCategory.SourceChanged;
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(source);

        ArchiveRebuildCapabilitySnapshot capability = await adapter.LoadRebuildCapabilitiesAsync();

        Assert.Equal(ArchiveWriteCapabilityState.Stale, capability.State);
        Assert.Equal(ArchiveLocationState.Stale, adapter.State);
        Assert.False(adapter.CanRebuildCopy);
        Assert.Equal(1, fake.CloseCount);
    }

    [Fact]
    public async Task UnsupportedSessionShapesNeverExposeRebuild()
    {
        using var files = new TestDirectory();
        string source = files.CreateFile("not eligible.zip", "source");
        var variants = new[]
        {
            RebuildClient.Create(format: "TAR"),
            RebuildClient.Create(hasEncryptedEntries: true),
            RebuildClient.Create(isMultipart: true),
            RebuildClient.Create(volumeSetKind: ArchiveVolumeSetKind.Unknown),
            RebuildClient.Create(capabilities: ArchiveCapability.List),
            RebuildClient.Create(missingFeature: ProtocolFeatures.ZipRebuildCopy),
        };

        foreach (RebuildClient fake in variants)
        {
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            await using var adapter = new ArchiveLocationAdapter(runtime);
            await adapter.OpenAsync(source);

            Assert.False(adapter.CanRebuildCopy);
            Assert.Equal(ArchiveWriteCapabilityState.Unavailable,
                (await adapter.LoadRebuildCapabilitiesAsync()).State);
            Assert.Equal(0, fake.RebuildStartCount);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Rain Explorer M6F rebuild " + Guid.NewGuid().ToString("N"));

        public TestDirectory() => Directory.CreateDirectory(_path);

        public string PathFor(string name) => System.IO.Path.Combine(_path, name);

        public string CreateFile(string name, string contents)
        {
            string path = PathFor(name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class RecordingActivitySink : IArchiveActivitySink
    {
        public List<ActivityOperationUpdate> Updates { get; } = [];

        public IArchiveActivityHandle Begin(ActivityOperationKind kind) =>
            new RecordingActivityHandle(this);

        private sealed class RecordingActivityHandle(RecordingActivitySink owner)
            : IArchiveActivityHandle
        {
            private Action? _cancel;

            public void AttachCancel(Action cancel) => _cancel = cancel;
            public void MarkCancellationPending() { }
            public void Report(ActivityOperationUpdate update) => owner.Updates.Add(update);
            public void Cancel() => _cancel?.Invoke();
        }
    }

    public class RebuildClient : DispatchProxy
    {
        private EventHandler<OperationEvent>? _events;
        private ArchiveSessionId _session;
        private readonly EntryId _entryId = EntryId.New();
        private string _format = "ZIP";
        private ArchiveCapability _capabilities = ArchiveCapability.List | ArchiveCapability.UpdateByRewrite;
        private ArchiveVolumeSetKind _volumeSetKind = ArchiveVolumeSetKind.Single;
        private bool _hasEncryptedEntries;
        private bool _isMultipart;
        private string? _missingFeature;
        private readonly TaskCompletionSource<OperationEvent> _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IArchiveClient Client { get; private set; } = null!;
        public ArchiveErrorCategory? CapabilityFailure { get; set; }
        public bool RejectExistingDestination { get; set; }
        public bool BlockStart { get; set; }
        public bool BlockTerminal { get; set; }
        public bool EmitUnexpectedWaiting { get; set; }
        public ArchiveErrorCategory? CancelErrorCategory { get; set; }
        public int CloseCount { get; private set; }
        public int RebuildStartCount { get; private set; }
        public int SecretCreateCount { get; private set; }
        public int CancelCallCount { get; private set; }
        public int DisposeCalls { get; private set; }
        public ArchiveSessionId LastRebuildSession { get; private set; }
        public string? LastDestination { get; private set; }
        public IReadOnlyList<ArchiveEntryKey> LastDeletes { get; private set; } = [];
        public IReadOnlyList<ArchiveRebuildCopyFile> LastFiles { get; private set; } = [];
        public ArchiveCompressionPreset LastPreset { get; private set; }
        public CancellationToken LastStartToken { get; private set; }
        public OperationId StartedOperationId { get; private set; }
        public OperationId? CancelledOperationId { get; private set; }
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static RebuildClient Create(
            string format = "ZIP",
            ArchiveCapability capabilities = ArchiveCapability.List | ArchiveCapability.UpdateByRewrite,
            ArchiveVolumeSetKind volumeSetKind = ArchiveVolumeSetKind.Single,
            bool hasEncryptedEntries = false,
            bool isMultipart = false,
            string? missingFeature = null)
        {
            IArchiveClient client = DispatchProxy.Create<IArchiveClient, RebuildClient>();
            var fake = (RebuildClient)(object)client;
            fake.Client = client;
            fake._format = format;
            fake._capabilities = capabilities;
            fake._volumeSetKind = volumeSetKind;
            fake._hasEncryptedEntries = hasEncryptedEntries;
            fake._isMultipart = isMultipart;
            fake._missingFeature = missingFeature;
            return fake;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            CancellationToken token = args.OfType<CancellationToken>().LastOrDefault();
            token.ThrowIfCancellationRequested();
            switch (targetMethod.Name)
            {
                case "add_OperationEventReceived":
                    _events += (EventHandler<OperationEvent>)args[0]!;
                    return null;
                case "remove_OperationEventReceived":
                    _events -= (EventHandler<OperationEvent>)args[0]!;
                    return null;
                case nameof(IArchiveClient.SupportsFeature):
                    return !string.Equals((string)args[0]!, _missingFeature, StringComparison.Ordinal);
                case nameof(IArchiveClient.OpenArchiveAsync):
                    _session = ArchiveSessionId.New();
                    return Task.FromResult(new OpenArchiveResponse(Descriptor()));
                case nameof(IArchiveClient.ListEntriesAsync):
                    return Task.FromResult(new ListEntriesResponse([Entry()], 0, 1, null));
                case nameof(IArchiveClient.CloseArchiveAsync):
                    CloseCount++;
                    return Task.FromResult(new CloseArchiveResponse(true));
                case nameof(IArchiveClient.GetZipWriteCapabilitiesAsync):
                    if (CapabilityFailure is ArchiveErrorCategory category)
                        return Task.FromException<GetZipWriteCapabilitiesResponse>(ClientError(category));
                    return Task.FromResult(Capabilities());
                case nameof(IArchiveClient.StartZipRebuildCopyAsync):
                    return StartRebuildAsync(args);
                case nameof(IArchiveClient.CancelOperationAsync):
                    CancelCallCount++;
                    CancelledOperationId = (OperationId)args[0]!;
                    CancelCalled.TrySetResult();
                    if (CancelErrorCategory is ArchiveErrorCategory cancelCategory)
                        return Task.FromException<CancelOperationResponse>(ClientError(cancelCategory));
                    return Task.FromResult(new CancelOperationResponse(true));
                case nameof(IArchiveClient.WaitForTerminalAsync):
                    WaitEntered.TrySetResult();
                    return BlockTerminal
                        ? _terminal.Task.WaitAsync(token)
                        : Task.FromResult(Terminal(OperationPhase.Completed));
                case nameof(IArchiveClient.GetOperationActivityAsync):
                    return Task.FromResult(new GetOperationActivityResponse(
                        DateTimeOffset.UtcNow, 8, 1, OperationQueueOrdering.Unspecified,
                        32, false, false, false, false, false, []));
                case nameof(IArchiveClient.CreateSecretAsync):
                    SecretCreateCount++;
                    throw new InvalidOperationException("Rebuild must remain password-free.");
                case nameof(IAsyncDisposable.DisposeAsync):
                    DisposeCalls++;
                    return ValueTask.CompletedTask;
                default:
                    throw new NotSupportedException($"Fake operation not configured: {targetMethod.Name}");
            }
        }

        public void CompleteTerminal(OperationPhase phase) =>
            _terminal.TrySetResult(Terminal(phase));

        public void Emit(OperationEvent operationEvent) =>
            _events?.Invoke(Client, operationEvent);

        private async Task<StartOperationResponse> StartRebuildAsync(object?[] args)
        {
            RebuildStartCount++;
            LastRebuildSession = (ArchiveSessionId)args[0]!;
            LastDestination = (string)args[1]!;
            LastDeletes = (IReadOnlyList<ArchiveEntryKey>)args[2]!;
            LastFiles = (IReadOnlyList<ArchiveRebuildCopyFile>)args[3]!;
            LastPreset = (ArchiveCompressionPreset)args[4]!;
            LastStartToken = (CancellationToken)args[5]!;
            if (RejectExistingDestination && File.Exists(LastDestination))
                throw ClientError(ArchiveErrorCategory.DestinationConflict);

            StartedOperationId = OperationId.New();
            StartEntered.TrySetResult();
            if (BlockStart) await ReleaseStart.Task.ConfigureAwait(false);
            _events?.Invoke(Client, new OperationEvent(
                StartedOperationId,
                1,
                EmitUnexpectedWaiting ? OperationEventKind.StateChanged : OperationEventKind.Progress,
                EmitUnexpectedWaiting ? OperationPhase.AwaitingConflict : OperationPhase.Running,
                new OperationProgress(
                    5, 10, 1, 1, _entryId, "untrusted provider name", TimeSpan.FromSeconds(1), 5, 0.5)));
            return new StartOperationResponse(StartedOperationId);
        }

        private ArchiveDescriptor Descriptor() => new(
            _session,
            _format,
            "test-provider",
            _capabilities,
            Fingerprint(),
            1,
            IsSolid: false,
            HasEncryptedEntries: _hasEncryptedEntries,
            IsMultipart: _isMultipart,
            IsReadOnly: true,
            Warnings: [],
            VolumeSetKind: _volumeSetKind);

        private ArchiveEntryDescriptor Entry() => new(
            _entryId,
            null,
            0,
            "payload.txt",
            "payload.txt",
            ArchiveEntryKind.File,
            10,
            5,
            null,
            null,
            null,
            null,
            false,
            false,
            null,
            EntrySafetyStatus.Safe,
            null,
            "payload.txt",
            0);

        private static ArchiveFingerprint Fingerprint() => new(
            14,
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            "volume:file",
            new string('A', 64));

        private static GetZipWriteCapabilitiesResponse Capabilities() => new(
            CanCreate: true,
            CanRebuild: true,
            SupportsAdd: true,
            SupportsReplace: true,
            SupportsDelete: true,
            SupportsFileRename: true,
            SupportsTreeEdit: true,
            SupportsMetadata: true,
            CompressionPresets:
            [
                ArchiveCompressionPreset.Store,
                ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Balanced,
                ArchiveCompressionPreset.Maximum,
            ],
            MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumZipCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

        private OperationEvent Terminal(OperationPhase phase) => new(
            StartedOperationId,
            3,
            OperationEventKind.Terminal,
            phase);

        private static ArchiveClientException ClientError(ArchiveErrorCategory category) => new(
            new ArchiveError("test.failure", category, "provider-controlled detail"));
    }
}
