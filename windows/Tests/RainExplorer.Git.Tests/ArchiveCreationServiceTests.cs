using System.Reflection;
using System.Text;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveCreationServiceTests
{
    public static TheoryData<ArchiveWriteFormat, bool, ArchiveCompressionPreset, string> DispatchCases =>
        new()
        {
            { ArchiveWriteFormat.Zip, false, ArchiveCompressionPreset.Maximum, nameof(IArchiveClient.StartZipCreationAsync) },
            { ArchiveWriteFormat.Zip, true, ArchiveCompressionPreset.Fast, nameof(IArchiveClient.StartZipTreeCreationAsync) },
            { ArchiveWriteFormat.Tar, false, ArchiveCompressionPreset.Store, nameof(IArchiveClient.StartTarCreationAsync) },
            { ArchiveWriteFormat.Tar, true, ArchiveCompressionPreset.Store, nameof(IArchiveClient.StartTarTreeCreationAsync) },
            { ArchiveWriteFormat.Rar, false, ArchiveCompressionPreset.Maximum, nameof(IArchiveClient.StartRarCreationAsync) },
            { ArchiveWriteFormat.Rar, true, ArchiveCompressionPreset.Fast, nameof(IArchiveClient.StartRarTreeCreationAsync) },
            { ArchiveWriteFormat.TarGZip, false, ArchiveCompressionPreset.Balanced, nameof(IArchiveClient.StartTarGZipCreationAsync) },
            { ArchiveWriteFormat.TarGZip, true, ArchiveCompressionPreset.Maximum, nameof(IArchiveClient.StartTarGZipTreeCreationAsync) },
            { ArchiveWriteFormat.TarBZip2, false, ArchiveCompressionPreset.Fast, nameof(IArchiveClient.StartTarBZip2CreationAsync) },
            { ArchiveWriteFormat.TarBZip2, true, ArchiveCompressionPreset.Balanced, nameof(IArchiveClient.StartTarBZip2TreeCreationAsync) },
            { ArchiveWriteFormat.GZip, false, ArchiveCompressionPreset.Maximum, nameof(IArchiveClient.StartGZipCreationAsync) },
            { ArchiveWriteFormat.BZip2, false, ArchiveCompressionPreset.Fast, nameof(IArchiveClient.StartBZip2CreationAsync) },
        };

    [Fact]
    public async Task AdvertisedCapabilitiesMapOnlyTheSevenExactSafeFormats()
    {
        var fake = FakeArchiveClient.Create();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, new RecordingActivitySink());

        ArchiveCreationCapabilitySnapshot snapshot = await service.LoadCapabilitiesAsync();

        Assert.Equal(ArchiveWriteCapabilityState.Ready, snapshot.State);
        Assert.Equal(
            [
                ArchiveWriteFormat.Zip,
                ArchiveWriteFormat.Tar,
                ArchiveWriteFormat.TarGZip,
                ArchiveWriteFormat.TarBZip2,
                ArchiveWriteFormat.GZip,
                ArchiveWriteFormat.BZip2,
                ArchiveWriteFormat.Rar,
            ],
            snapshot.Formats.Select(option => option.Format));
        Assert.Collection(
            snapshot.Formats,
            option => AssertOption(option, ".zip", true,
                ArchiveCompressionPreset.Store, ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Balanced, ArchiveCompressionPreset.Maximum),
            option => AssertOption(option, ".tar", true, ArchiveCompressionPreset.Store),
            option => AssertOption(option, ".tar.gz", true,
                ArchiveCompressionPreset.Fast, ArchiveCompressionPreset.Balanced,
                ArchiveCompressionPreset.Maximum),
            option => AssertOption(option, ".tar.bz2", true,
                ArchiveCompressionPreset.Fast, ArchiveCompressionPreset.Balanced,
                ArchiveCompressionPreset.Maximum),
            option => AssertOption(option, ".gz", false,
                ArchiveCompressionPreset.Balanced, ArchiveCompressionPreset.Maximum),
            option => AssertOption(option, ".bz2", false,
                ArchiveCompressionPreset.Fast, ArchiveCompressionPreset.Balanced),
            option => AssertOption(option, ".rar", true,
                ArchiveCompressionPreset.Store, ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Balanced, ArchiveCompressionPreset.Maximum));
        Assert.Equal(RarEncoderAvailability.Available, snapshot.RarAvailability);
        Assert.Equal(1, fake.ZipCapabilityCalls);
        Assert.Equal(1, fake.TarCapabilityCalls);
        Assert.Equal(1, fake.ManagedCapabilityCalls);
        Assert.Equal(1, fake.RarCapabilityCalls);
    }

    [Fact]
    public async Task FormatPickerShowsAllSevenWriterFormatsAndExplainsSelectionIncompatibility()
    {
        using var temporary = new TemporaryDirectory();
        string file = temporary.CreateFile("single.bin", "single");
        string directory = temporary.CreateDirectory("folder");
        var fake = FakeArchiveClient.Create();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, new RecordingActivitySink());
        ArchiveCreationCapabilitySnapshot snapshot = await service.LoadCapabilitiesAsync();

        IReadOnlyList<ArchiveCreationFormatChoice> folderChoices =
            CreateArchiveDialog.FormatChoices(snapshot, BuildSources([(directory, true)]));

        Assert.Equal(7, folderChoices.Count);
        Assert.Equal(5, folderChoices.Count(choice => choice.IsCompatible));
        Assert.All(
            folderChoices.Where(choice => choice.Option.Format is ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2),
            choice =>
            {
                Assert.False(choice.IsCompatible);
                Assert.Contains("exactly one ordinary file", choice.Availability, StringComparison.Ordinal);
                Assert.Contains(choice.Availability, choice.DisplayName, StringComparison.Ordinal);
            });

        IReadOnlyList<ArchiveCreationFormatChoice> fileChoices =
            CreateArchiveDialog.FormatChoices(snapshot, BuildSources([(file, false)]));
        Assert.Equal(7, fileChoices.Count);
        Assert.All(fileChoices, choice => Assert.True(choice.IsCompatible));
    }

    [Fact]
    public async Task FormatPickerKeepsUnavailableRarVisibleWithTheExactUpdateReason()
    {
        using var temporary = new TemporaryDirectory();
        string file = temporary.CreateFile("single.bin", "single");
        var fake = FakeArchiveClient.Create();
        fake.RarCapabilities = SafeRarCapabilities() with
        {
            CanCreate = false,
            SupportsTreeCreation = false,
            Availability = RarEncoderAvailability.UpdateRequired,
        };
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, new RecordingActivitySink());

        ArchiveCreationCapabilitySnapshot snapshot = await service.LoadCapabilitiesAsync();
        Assert.Equal(RarEncoderAvailability.UpdateRequired, snapshot.RarAvailability);
        Assert.DoesNotContain(snapshot.Formats, option => option.Format == ArchiveWriteFormat.Rar);

        IReadOnlyList<ArchiveCreationFormatChoice> choices =
            CreateArchiveDialog.FormatChoices(snapshot, BuildSources([(file, false)]));
        ArchiveCreationFormatChoice rar = Assert.Single(
            choices,
            choice => choice.Option.Format == ArchiveWriteFormat.Rar);
        Assert.False(rar.IsCompatible);
        Assert.Equal("Requires WinRAR 7.23 or later", rar.Availability);
        Assert.Contains(rar.Availability, rar.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProtocolOrFamilyFeatureDowngradesWithoutInventingSupport()
    {
        var protocolDowngrade = FakeArchiveClient.Create();
        protocolDowngrade.Features.Remove(ProtocolFeatures.OperationActivity);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(protocolDowngrade.Client)))
        await using (var service = new ArchiveCreationService(runtime, new RecordingActivitySink()))
        {
            ArchiveCreationCapabilitySnapshot snapshot = await service.LoadCapabilitiesAsync();
            Assert.Equal(ArchiveWriteCapabilityState.Unavailable, snapshot.State);
            Assert.Empty(snapshot.Formats);
            Assert.Equal(0, protocolDowngrade.TotalCapabilityCalls);
        }

        var familyDowngrade = FakeArchiveClient.Create();
        familyDowngrade.Features.Remove(ProtocolFeatures.GZipCreation);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(familyDowngrade.Client)))
        await using (var service = new ArchiveCreationService(runtime, new RecordingActivitySink()))
        {
            ArchiveCreationCapabilitySnapshot snapshot = await service.LoadCapabilitiesAsync();
            Assert.Equal(ArchiveWriteCapabilityState.Ready, snapshot.State);
            Assert.DoesNotContain(snapshot.Formats, option => option.Format == ArchiveWriteFormat.GZip);
            Assert.Equal(6, snapshot.Formats.Count);
        }

        var unsafeAdvertisement = FakeArchiveClient.Create();
        unsafeAdvertisement.ZipCapabilities = SafeZipCapabilities() with { SupportsEncryption = true };
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(unsafeAdvertisement.Client)))
        await using (var service = new ArchiveCreationService(runtime, new RecordingActivitySink()))
        {
            ArchiveCreationCapabilitySnapshot snapshot = await service.LoadCapabilitiesAsync();
            Assert.DoesNotContain(snapshot.Formats, option => option.Format == ArchiveWriteFormat.Zip);
            Assert.Equal(6, snapshot.Formats.Count);
        }
    }

    [Theory]
    [MemberData(nameof(DispatchCases))]
    public async Task DispatchUsesExactAdvertisedShapePresetAndPasswordFreeCall(
        ArchiveWriteFormat format,
        bool withDirectory,
        ArchiveCompressionPreset preset,
        string expectedMethod)
    {
        using var temporary = new TemporaryDirectory();
        string file = temporary.CreateFile("plain file 雨.txt", "ordinary source");
        string directory = temporary.CreateDirectory("folder with space");
        IReadOnlyList<ArchiveCreationSource> sources = BuildSources(
            withDirectory ? [(file, false), (directory, true)] : [(file, false)]);
        string destination = Path.Combine(temporary.Path, "created archive" + Extension(format));
        var fake = FakeArchiveClient.Create();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, new RecordingActivitySink());

        ArchiveOperationResult result = await service.CreateAsync(
            new ArchiveCreationRequest(destination, sources, format, preset));

        Assert.Equal(ArchiveOperationOutcome.Completed, result.Outcome);
        CreationStart invocation = Assert.Single(fake.Starts);
        Assert.Equal(expectedMethod, invocation.Method);
        Assert.Equal(Path.GetFullPath(destination), invocation.DestinationPath);
        Assert.Equal(preset, invocation.CompressionPreset);
        if (format is ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2)
        {
            Assert.Equal(file, invocation.RawSourcePath);
            Assert.Empty(invocation.Files);
        }
        else
        {
            ArchiveCreationFile selectedFile = Assert.Single(invocation.Files);
            Assert.Equal(file, selectedFile.SourcePath);
            Assert.Equal(Path.GetFileName(file), selectedFile.ArchivePath);
        }
        if (withDirectory)
        {
            ArchiveCreationDirectory selectedDirectory = Assert.Single(invocation.Directories);
            Assert.Equal(directory, selectedDirectory.SourcePath);
            Assert.Equal(Path.GetFileName(directory), selectedDirectory.ArchivePath);
        }
        else
        {
            Assert.Empty(invocation.Directories);
        }
        Assert.False(invocation.StartToken.IsCancellationRequested);
        Assert.Equal(0, fake.CreateSecretCalls);
        Assert.Equal(0, fake.DiscardSecretCalls);
        Assert.Equal(invocation.OperationId, fake.WaitedOperationId);
    }

    [Fact]
    public void ValidationRechecksMissingWrongKindAndReparseRootsWithoutEnumeratingDirectories()
    {
        using var temporary = new TemporaryDirectory();
        string missing = Path.Combine(temporary.Path, "missing source.bin");
        Assert.True(ArchiveWriteValidation.TryBuildSources(
            [(missing, false)], out IReadOnlyList<ArchiveCreationSource> missingSources));
        Assert.False(ArchiveWriteValidation.TryValidateSourceRoots(missingSources));

        string file = temporary.CreateFile("declared folder.txt", "source");
        var wrongKind = new ArchiveCreationSource(
            ArchiveCreationSourceKind.Directory, file, Path.GetFileName(file));
        Assert.True(ArchiveWriteValidation.TryValidateCreationShape(
            new ArchiveCreationRequest(
                Path.Combine(temporary.Path, "wrong-kind.zip"),
                [wrongKind],
                ArchiveWriteFormat.Zip,
                ArchiveCompressionPreset.Balanced),
            option: null));
        Assert.False(ArchiveWriteValidation.TryValidateSourceRoots([wrongKind]));

        string target = temporary.CreateDirectory("real directory");
        temporary.CreateFile(Path.Combine("real directory", "nested.txt"), "must not be enumerated");
        string link = Path.Combine(temporary.Path, "linked directory");
        Directory.CreateSymbolicLink(link, target);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        var reparse = new ArchiveCreationSource(
            ArchiveCreationSourceKind.Directory, link, Path.GetFileName(link));
        Assert.False(ArchiveWriteValidation.TryValidateSourceRoots([reparse]));
    }

    [Fact]
    public async Task StaleSourceRootsFailBeforeClientDispatch()
    {
        using var temporary = new TemporaryDirectory();
        var cases = new[]
        {
            new ArchiveCreationSource(
                ArchiveCreationSourceKind.File,
                Path.Combine(temporary.Path, "missing.bin"),
                "missing.bin"),
            new ArchiveCreationSource(
                ArchiveCreationSourceKind.Directory,
                temporary.CreateFile("ordinary.bin", "data"),
                "ordinary.bin"),
        };

        foreach (ArchiveCreationSource source in cases)
        {
            int factoryCalls = 0;
            var fake = FakeArchiveClient.Create();
            await using var runtime = new ArchiveClientRuntime(_ =>
            {
                factoryCalls++;
                return Task.FromResult(fake.Client);
            });
            var sink = new RecordingActivitySink();
            await using var service = new ArchiveCreationService(runtime, sink);
            string destination = Path.Combine(temporary.Path, Guid.NewGuid().ToString("N") + ".zip");

            ArchiveOperationResult result = await service.CreateAsync(new ArchiveCreationRequest(
                destination, [source], ArchiveWriteFormat.Zip, ArchiveCompressionPreset.Balanced));

            Assert.Equal(ArchiveOperationOutcome.Stale, result.Outcome);
            Assert.Equal(0, factoryCalls);
            Assert.Empty(fake.Starts);
            Assert.Equal(ActivityOperationFailure.SourceChanged, sink.LastUpdate!.Failure);
        }
    }

    [Fact]
    public async Task ExistingDestinationIsAConflictBeforeClientDispatch()
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = temporary.CreateFile("already exists.zip", "sentinel");
        int factoryCalls = 0;
        var fake = FakeArchiveClient.Create();
        await using var runtime = new ArchiveClientRuntime(_ =>
        {
            factoryCalls++;
            return Task.FromResult(fake.Client);
        });
        var sink = new RecordingActivitySink();
        await using var service = new ArchiveCreationService(runtime, sink);

        ArchiveOperationResult result = await service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced));

        Assert.Equal(ArchiveOperationOutcome.DestinationConflict, result.Outcome);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(fake.Starts);
        Assert.Equal("sentinel", File.ReadAllText(destination));
        Assert.Equal(ActivityOperationFailure.DestinationConflict, sink.LastUpdate!.Failure);
    }

    [Fact]
    public async Task WorkerWinsDestinationRaceAndExplorerPreservesSentinelWithoutRetryOrDelete()
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = Path.Combine(temporary.Path, "raced destination.zip");
        byte[] sentinel = Encoding.UTF8.GetBytes("race-winner-sentinel");
        var fake = FakeArchiveClient.Create();
        fake.StartErrorCategory = ArchiveErrorCategory.DestinationConflict;
        fake.RequireDestinationAtStart = true;
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var sink = new RecordingActivitySink();
        await using var service = new ArchiveCreationService(
            runtime,
            sink,
            () => File.WriteAllBytesAsync(destination, sentinel));

        ArchiveOperationResult result = await service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced));

        Assert.Equal(ArchiveOperationOutcome.DestinationConflict, result.Outcome);
        Assert.Single(fake.Starts);
        Assert.Equal(1, fake.StartAttempts);
        Assert.True(File.Exists(destination));
        Assert.Equal(sentinel, File.ReadAllBytes(destination));
        Assert.Equal(0, fake.CancelCalls);
        Assert.Equal(ActivityOperationFailure.DestinationConflict, sink.LastUpdate!.Failure);
    }

    [Fact]
    public async Task SourceKindRaceImmediatelyBeforeStartReturnsStaleWithoutDispatch()
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("raced source.bin", "ordinary file");
        string destination = Path.Combine(temporary.Path, "source-race.zip");
        var fake = FakeArchiveClient.Create();
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(
            runtime,
            sink,
            () =>
            {
                File.Delete(source);
                Directory.CreateDirectory(source);
                return Task.CompletedTask;
            });

        ArchiveOperationResult result = await service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced));

        Assert.Equal(ArchiveOperationOutcome.Stale, result.Outcome);
        Assert.Equal(0, fake.StartAttempts);
        Assert.Empty(fake.Starts);
        Assert.Equal(ActivityOperationFailure.SourceChanged, sink.LastUpdate!.Failure);
    }

    [Fact]
    public async Task CancellationDuringStartKeepsStartTokenAliveThenCancelsExactOperationAndDrainsTerminal()
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = Path.Combine(temporary.Path, "cancelled.zip");
        var fake = FakeArchiveClient.Create();
        fake.BlockStart = true;
        fake.BlockTerminal = true;
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var sink = new RecordingActivitySink();
        await using var service = new ArchiveCreationService(runtime, sink);
        using var cancellation = new CancellationTokenSource();

        Task<ArchiveOperationResult> operation = service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced), cancellation.Token);
        await fake.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        cancellation.Cancel();
        Assert.NotNull(fake.CapturedStartToken);
        Assert.False(fake.CapturedStartToken!.Value.IsCancellationRequested);
        Assert.Equal(0, fake.CancelCalls);
        Assert.False(operation.IsCompleted);

        fake.ReleaseStart.TrySetResult();
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(fake.StartTokenWasCancellationRequested);
        Assert.Equal(fake.StartedOperationId, fake.CancelledOperationId);
        Assert.Equal(1, fake.CancelCalls);
        Assert.False(operation.IsCompleted);
        Assert.True(sink.Handle!.CancellationPending);

        fake.CompleteTerminal(OperationPhase.Cancelled);
        ArchiveOperationResult result = await operation.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ArchiveOperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(fake.StartedOperationId, fake.WaitedOperationId);
        Assert.Equal(ActivityOperationPhase.Cancelled, sink.LastUpdate!.Phase);
        Assert.Equal(0, fake.CreateSecretCalls);
        Assert.Equal(0, fake.DiscardSecretCalls);
    }

    [Theory]
    [InlineData(OperationPhase.AwaitingPassword)]
    [InlineData(OperationPhase.AwaitingConflict)]
    [InlineData(OperationPhase.AwaitingRiskApproval)]
    public async Task UnexpectedInteractivePhaseCancelsExactOperationAndFailsClosed(
        OperationPhase unexpectedPhase)
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = Path.Combine(temporary.Path, "unexpected-wait.zip");
        var fake = FakeArchiveClient.Create();
        fake.BlockTerminal = true;
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, sink);

        Task<ArchiveOperationResult> operation = service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced));
        await fake.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        fake.Emit(new OperationEvent(
            fake.StartedOperationId,
            1,
            OperationEventKind.StateChanged,
            unexpectedPhase));
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, fake.CancelCalls);
        Assert.Equal(fake.StartedOperationId, fake.CancelledOperationId);
        Assert.False(operation.IsCompleted);
        Assert.True(sink.Handle!.CancellationPending);
        fake.CompleteTerminal(OperationPhase.Cancelled);

        ArchiveOperationResult result = await operation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ArchiveOperationOutcome.Unsupported, result.Outcome);
        Assert.Equal(ActivityOperationPhase.Failed, sink.LastUpdate!.Phase);
        Assert.Equal(ActivityOperationFailure.Unsupported, sink.LastUpdate.Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignInteractivePhaseBeforeOrAfterOperationIdDoesNotCancelCreation(
        bool beforeOperationId)
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("foreign source.txt", "source");
        string destination = Path.Combine(temporary.Path, "foreign-wait.zip");
        var fake = FakeArchiveClient.Create();
        fake.BlockStart = true;
        fake.BlockTerminal = true;
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(
            runtime,
            new RecordingActivitySink());

        Task<ArchiveOperationResult> operation = service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced));
        await fake.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        if (!beforeOperationId)
        {
            fake.ReleaseStart.TrySetResult();
            await WaitUntilAsync(() => fake.WaitedOperationId == fake.StartedOperationId);
        }

        OperationId foreignOperationId = OperationId.New();
        Assert.NotEqual(fake.StartedOperationId, foreignOperationId);
        fake.Emit(new OperationEvent(
            foreignOperationId,
            1,
            OperationEventKind.StateChanged,
            OperationPhase.AwaitingConflict));
        Assert.Equal(0, fake.CancelCalls);

        if (beforeOperationId)
        {
            fake.ReleaseStart.TrySetResult();
            await WaitUntilAsync(() => fake.WaitedOperationId == fake.StartedOperationId);
        }
        fake.CompleteTerminal(OperationPhase.Completed);

        Assert.Equal(
            ArchiveOperationOutcome.Completed,
            (await operation.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
        Assert.Equal(0, fake.CancelCalls);
    }

    [Theory]
    [InlineData(ArchiveErrorCategory.InvalidRequest, false)]
    [InlineData(ArchiveErrorCategory.WorkerFailure, true)]
    [InlineData(ArchiveErrorCategory.ProtocolMismatch, true)]
    public async Task CancellationFailureInvalidatesOnlyForFatalClientCategories(
        ArchiveErrorCategory category,
        bool shouldInvalidate)
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = Path.Combine(temporary.Path, "cancel-failure.zip");
        var fake = FakeArchiveClient.Create();
        fake.BlockTerminal = true;
        fake.CancelErrorCategory = category;
        int factoryCalls = 0;
        await using var runtime = new ArchiveClientRuntime(_ =>
        {
            factoryCalls++;
            return Task.FromResult(fake.Client);
        });
        await using var service = new ArchiveCreationService(runtime, new RecordingActivitySink());
        using var cancellation = new CancellationTokenSource();

        Task<ArchiveOperationResult> operation = service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced), cancellation.Token);
        await fake.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        cancellation.Cancel();
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(fake.StartedOperationId, fake.CancelledOperationId);
        if (shouldInvalidate)
            await WaitUntilAsync(() => fake.DisposeCalls == 1);
        Assert.Equal(shouldInvalidate ? 1 : 0, fake.DisposeCalls);

        fake.CompleteTerminal(OperationPhase.Cancelled);
        Assert.Equal(
            ArchiveOperationOutcome.Cancelled,
            (await operation.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);

        IArchiveClient current = await runtime.GetClientAsync(CancellationToken.None);
        Assert.Same(fake.Client, current);
        Assert.Equal(shouldInvalidate ? 2 : 1, factoryCalls);
    }

    [Fact]
    public async Task ActivityMapsEscapedBoundedProgressSnapshotWarningsAndTerminalFields()
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = Path.Combine(temporary.Path, "activity.zip");
        var fake = FakeArchiveClient.Create();
        fake.BlockTerminal = true;
        fake.TerminalPhase = OperationPhase.CompletedWithWarnings;
        fake.SnapshotProgress = new OperationActivityProgress(
            18, 20, 2, 2, TimeSpan.FromSeconds(4), 5.5, 0.4);
        fake.SnapshotWarningCount = 3;
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, sink);

        Task<ArchiveOperationResult> operation = service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced));
        await fake.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        string untrusted = "visible-" + string.Concat(Enumerable.Repeat("\u202E\n", 100));
        fake.Emit(new OperationEvent(
            fake.StartedOperationId,
            1,
            OperationEventKind.Progress,
            OperationPhase.Running,
            new OperationProgress(
                10, 20, 1, 2, null, untrusted,
                TimeSpan.FromSeconds(2), 4.5, 0.5)));
        fake.Emit(new OperationEvent(
            fake.StartedOperationId,
            2,
            OperationEventKind.StateChanged,
            OperationPhase.Finalizing));
        fake.Emit(new OperationEvent(
            fake.StartedOperationId,
            3,
            OperationEventKind.Warning,
            OperationPhase.Finalizing,
            Warning: new ArchiveWarning("test.warning", "provider-controlled warning")));
        fake.CompleteTerminal(OperationPhase.CompletedWithWarnings);

        ArchiveOperationResult result = await operation.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ArchiveOperationOutcome.CompletedWithWarnings, result.Outcome);
        Assert.Equal(ActivityOperationKind.ArchiveCreation, sink.Handle!.Kind);
        Assert.Contains(sink.Updates, update => update.Phase == ActivityOperationPhase.Validating);
        Assert.Contains(sink.Updates, update => update.Phase == ActivityOperationPhase.Queued);
        ActivityOperationUpdate running = Assert.Single(
            sink.Updates, update => update.Phase == ActivityOperationPhase.Running);
        Assert.Equal(10, running.BytesProcessed);
        Assert.Equal(20, running.TotalBytes);
        Assert.Equal(1, running.EntriesProcessed);
        Assert.Equal(2, running.TotalEntries);
        Assert.Equal(TimeSpan.FromSeconds(2), running.Elapsed);
        Assert.Equal(4.5, running.BytesPerSecond);
        Assert.Equal(0.5, running.CompressionRatio);
        Assert.NotNull(running.CurrentItem);
        Assert.StartsWith("visible-", running.CurrentItem, StringComparison.Ordinal);
        Assert.True(running.CurrentItem!.Length <= ActivityOperationUpdate.MaximumCurrentItemCharacters);
        Assert.DoesNotContain('\u202E', running.CurrentItem);
        Assert.DoesNotContain('\n', running.CurrentItem);
        Assert.Contains(sink.Updates, update => update.Phase == ActivityOperationPhase.Finalizing);

        ActivityOperationUpdate terminal = sink.LastUpdate!;
        Assert.Equal(ActivityOperationPhase.CompletedWithWarnings, terminal.Phase);
        Assert.Equal(18, terminal.BytesProcessed);
        Assert.Equal(20, terminal.TotalBytes);
        Assert.Equal(2, terminal.EntriesProcessed);
        Assert.Equal(2, terminal.TotalEntries);
        Assert.Equal(TimeSpan.FromSeconds(4), terminal.Elapsed);
        Assert.Equal(5.5, terminal.BytesPerSecond);
        Assert.Equal(0.4, terminal.CompressionRatio);
        Assert.Null(terminal.CurrentItem);
        Assert.Equal(3, terminal.WarningCount);
        Assert.Equal(ActivityOperationFailure.None, terminal.Failure);
    }

    [Theory]
    [InlineData(ArchiveErrorCategory.SourceChanged, ArchiveOperationOutcome.Stale, ActivityOperationFailure.SourceChanged)]
    [InlineData(ArchiveErrorCategory.DestinationConflict, ArchiveOperationOutcome.DestinationConflict, ActivityOperationFailure.DestinationConflict)]
    [InlineData(ArchiveErrorCategory.PermissionDenied, ArchiveOperationOutcome.Failed, ActivityOperationFailure.PermissionDenied)]
    public async Task TerminalErrorsMapToFixedPathFreeResultsAndActivity(
        ArchiveErrorCategory category,
        ArchiveOperationOutcome expectedOutcome,
        ActivityOperationFailure expectedFailure)
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = Path.Combine(temporary.Path, "failed.zip");
        var fake = FakeArchiveClient.Create();
        fake.TerminalPhase = OperationPhase.Failed;
        fake.TerminalErrorCategory = category;
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, sink);

        ArchiveOperationResult result = await service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Zip,
            ArchiveCompressionPreset.Balanced));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(ActivityOperationPhase.Failed, sink.LastUpdate!.Phase);
        Assert.Equal(expectedFailure, sink.LastUpdate.Failure);
        Assert.DoesNotContain(destination, sink.LastUpdate.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreationShapeRequiresAdvertisedPresetAndRawSingleOrdinaryFile()
    {
        using var temporary = new TemporaryDirectory();
        string first = temporary.CreateFile("first.bin", "first");
        string second = temporary.CreateFile("second.bin", "second");
        string directory = temporary.CreateDirectory("folder");
        ArchiveCreationFormatOption raw = new(
            ArchiveWriteFormat.GZip,
            "GZIP",
            ".gz",
            SupportsDirectories: false,
            [ArchiveCompressionPreset.Balanced],
            MaximumEntries: 1,
            MaximumSourceBytes: ProtocolConstants.MaximumRawCreationSourceBytes);

        Assert.True(ArchiveWriteValidation.TryValidateCreationShape(
            new ArchiveCreationRequest(
                Path.Combine(temporary.Path, "single.gz"),
                BuildSources([(first, false)]),
                ArchiveWriteFormat.GZip,
                ArchiveCompressionPreset.Balanced),
            raw));
        Assert.False(ArchiveWriteValidation.TryValidateCreationShape(
            new ArchiveCreationRequest(
                Path.Combine(temporary.Path, "many.gz"),
                BuildSources([(first, false), (second, false)]),
                ArchiveWriteFormat.GZip,
                ArchiveCompressionPreset.Balanced),
            raw));
        Assert.False(ArchiveWriteValidation.TryValidateCreationShape(
            new ArchiveCreationRequest(
                Path.Combine(temporary.Path, "folder.gz"),
                BuildSources([(directory, true)]),
                ArchiveWriteFormat.GZip,
                ArchiveCompressionPreset.Balanced),
            raw));
        Assert.False(ArchiveWriteValidation.TryValidateCreationShape(
            new ArchiveCreationRequest(
                Path.Combine(temporary.Path, "preset.gz"),
                BuildSources([(first, false)]),
                ArchiveWriteFormat.GZip,
                ArchiveCompressionPreset.Maximum),
            raw));
    }

    [Theory]
    [InlineData(false, 4096)]
    [InlineData(true, 65536)]
    public async Task ExpandedFolderBudgetDoesNotExpandExplicitRootsOrRawFormats(bool negotiated, int expected)
    {
        var fake = FakeArchiveClient.Create();
        if (negotiated) fake.Features.Add(ProtocolFeatures.ExpandedCreationTreeBudget);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, new RecordingActivitySink());

        ArchiveCreationCapabilitySnapshot snapshot = await service.LoadCapabilitiesAsync();

        foreach (ArchiveCreationFormatOption option in snapshot.Formats)
        {
            Assert.Equal(option.SupportsDirectories ? expected : 1, option.MaximumTreeEntries);
            Assert.Equal(option.SupportsDirectories ? 4096 : 1, option.MaximumEntries);
        }
    }

    [Theory]
    [InlineData("creation.entry_count_limit", ActivityOperationFailure.CreationEntryLimit, 65536L)]
    [InlineData("creation.source_byte_limit", ActivityOperationFailure.CreationSourceSizeLimit, 68719476736L)]
    [InlineData("creation.metadata_budget", ActivityOperationFailure.CreationMetadataLimit, null)]
    [InlineData("creation.source_path_limit", ActivityOperationFailure.CreationPathLimit, null)]
    [InlineData("creation.output_limit", ActivityOperationFailure.CreationOutputLimit, null)]
    [InlineData("creation.output_overflow", ActivityOperationFailure.CreationOutputLimit, null)]
    [InlineData("provider-controlled-unknown-code", ActivityOperationFailure.ResourceLimit, null)]
    public async Task CreationLimitsKeepTheirExactFixedReasonAndNegotiatedBudget(
        string code, ActivityOperationFailure expected, long? expectedLimit)
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateDirectory("source-folder");
        string destination = Path.Combine(temporary.Path, "limited.zip");
        var fake = FakeArchiveClient.Create();
        fake.Features.Add(ProtocolFeatures.ExpandedCreationTreeBudget);
        fake.TerminalPhase = OperationPhase.Failed;
        fake.TerminalErrorCategory = ArchiveErrorCategory.ResourceLimit;
        fake.TerminalErrorCode = code;
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, sink);

        ArchiveOperationResult result = await service.CreateAsync(new ArchiveCreationRequest(
            destination, BuildSources([(source, true)]), ArchiveWriteFormat.Zip, ArchiveCompressionPreset.Balanced));

        Assert.Equal(ArchiveOperationOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(expectedLimit, result.FailureLimit);
        Assert.Equal(expected, sink.LastUpdate!.Failure);
        Assert.Equal(expectedLimit, sink.LastUpdate.FailureLimit);
        string? detail = ArchiveCreationFailureText.Describe(result.Failure, result.FailureLimit);
        if (expected == ActivityOperationFailure.ResourceLimit) Assert.Null(detail);
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(detail));
            Assert.DoesNotContain("provider-controlled", detail, StringComparison.Ordinal);
            Assert.DoesNotContain(source, detail, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(File.Exists(destination));
        Assert.Equal(1, fake.StartAttempts);
    }

    [Theory]
    [InlineData(
        "rar.workspace_path_too_long",
        ActivityOperationFailure.CreationRarWorkspacePathTooLong,
        "A file or folder name is too long for RAR preparation. Shorten nested names or select a smaller subfolder.")]
    [InlineData(
        "rar.workspace_io_failure",
        ActivityOperationFailure.CreationRarWorkspaceIoFailure,
        "Rain could not prepare the temporary files for RAR creation. Check available disk space and folder permissions.")]
    public async Task RarWorkspaceProviderCodesMapToFixedActionableTextWithoutProviderDetails(
        string code, ActivityOperationFailure expected, string expectedText)
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = Path.Combine(temporary.Path, "failed.rar");
        const string untrustedMessage = "provider message includes C:\\untrusted\\source.txt and a secret token";
        var fake = FakeArchiveClient.Create();
        fake.TerminalPhase = OperationPhase.Failed;
        fake.TerminalErrorCategory = ArchiveErrorCategory.ProviderFailure;
        fake.TerminalErrorCode = code;
        fake.TerminalErrorMessage = untrustedMessage;
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, sink);

        ArchiveOperationResult result = await service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Rar,
            ArchiveCompressionPreset.Balanced));

        Assert.Equal(ArchiveOperationOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.Failure);
        ActivityOperationUpdate terminal = sink.LastUpdate!;
        Assert.Equal(expected, terminal.Failure);
        string detail = ArchiveCreationFailureText.Describe(result.Failure)!;
        Assert.Equal(expectedText, detail);
        Assert.DoesNotContain(untrustedMessage, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(source, detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(destination, detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(untrustedMessage, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(untrustedMessage, terminal.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.CancelCalls);
    }

    [Theory]
    [InlineData(
        ArchiveErrorCategory.ProviderFailure,
        "filesystem.native_failure",
        ArchiveOperationOutcome.Failed,
        ActivityOperationFailure.ProviderFailure)]
    [InlineData(
        ArchiveErrorCategory.InvalidRequest,
        "filesystem.path_too_long",
        ArchiveOperationOutcome.InvalidRequest,
        ActivityOperationFailure.InvalidRequest)]
    [InlineData(
        ArchiveErrorCategory.ProviderFailure,
        "rar.workspace_path_too_long.extra",
        ArchiveOperationOutcome.Failed,
        ActivityOperationFailure.ProviderFailure)]
    [InlineData(
        ArchiveErrorCategory.ProviderFailure,
        "provider-controlled-unknown-code",
        ArchiveOperationOutcome.Failed,
        ActivityOperationFailure.ProviderFailure)]
    public async Task NonAllowlistedCreationCodesKeepTheExistingGenericFailureBehavior(
        ArchiveErrorCategory category,
        string code,
        ArchiveOperationOutcome expectedOutcome,
        ActivityOperationFailure expectedFailure)
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.CreateFile("source.txt", "source");
        string destination = Path.Combine(temporary.Path, "failed.rar");
        var fake = FakeArchiveClient.Create();
        fake.TerminalPhase = OperationPhase.Failed;
        fake.TerminalErrorCategory = category;
        fake.TerminalErrorCode = code;
        fake.TerminalErrorMessage = "untrusted provider message with C:\\private\\path.txt";
        var sink = new RecordingActivitySink();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var service = new ArchiveCreationService(runtime, sink);

        ArchiveOperationResult result = await service.CreateAsync(new ArchiveCreationRequest(
            destination,
            BuildSources([(source, false)]),
            ArchiveWriteFormat.Rar,
            ArchiveCompressionPreset.Balanced));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedFailure, result.Failure);
        Assert.Equal(expectedFailure, sink.LastUpdate!.Failure);
        Assert.Null(ArchiveCreationFailureText.Describe(result.Failure));
    }

    private static IReadOnlyList<ArchiveCreationSource> BuildSources(
        IEnumerable<(string SourcePath, bool IsDirectory)> selected)
    {
        Assert.True(ArchiveWriteValidation.TryBuildSources(selected, out var sources));
        return sources;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static void AssertOption(
        ArchiveCreationFormatOption option,
        string extension,
        bool supportsDirectories,
        params ArchiveCompressionPreset[] presets)
    {
        Assert.Equal(extension, option.Extension);
        Assert.Equal(supportsDirectories, option.SupportsDirectories);
        Assert.Equal(presets, option.CompressionPresets);
    }

    private static string Extension(ArchiveWriteFormat format) => format switch
    {
        ArchiveWriteFormat.Zip => ".zip",
        ArchiveWriteFormat.Tar => ".tar",
        ArchiveWriteFormat.TarGZip => ".tar.gz",
        ArchiveWriteFormat.TarBZip2 => ".tar.bz2",
        ArchiveWriteFormat.GZip => ".gz",
        ArchiveWriteFormat.BZip2 => ".bz2",
        ArchiveWriteFormat.Rar => ".rar",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static GetZipWriteCapabilitiesResponse SafeZipCapabilities() => new(
        CanCreate: true,
        CanRebuild: false,
        SupportsAdd: true,
        SupportsReplace: false,
        SupportsDelete: false,
        SupportsFileRename: false,
        SupportsTreeEdit: false,
        SupportsMetadata: false,
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

    private static GetTarWriteCapabilitiesResponse SafeTarCapabilities() => new(
        CanCreate: true,
        CanRebuild: false,
        SupportsTreeCreation: true,
        CompressionPresets: [ArchiveCompressionPreset.Store],
        MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
        MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
        SupportsEncryption: false,
        SupportsFilenameEncryption: false,
        SupportsSplitOutput: false,
        SupportsExistingDestinationReplacement: false,
        SupportsInPlaceUpdate: false);

    private static GetRarWriteCapabilitiesResponse SafeRarCapabilities() => new(
        CanCreate: true,
        CanRebuild: false,
        SupportsTreeCreation: true,
        CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
        MaximumEntries: ProtocolConstants.MaximumRarCreationEntries,
        MaximumSourceBytes: ProtocolConstants.MaximumRarCreationSourceBytes,
        Availability: RarEncoderAvailability.Available,
        SupportsEncryption: false,
        SupportsFilenameEncryption: false,
        SupportsSplitOutput: false,
        SupportsExistingDestinationReplacement: false,
        SupportsInPlaceUpdate: false);

    private static GetManagedWriteCapabilitiesResponse SafeManagedCapabilities() => new(
    [
        ManagedCapability(
            ManagedArchiveCreationFormat.TarGZip,
            true,
            ArchiveCompressionPreset.Fast,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Maximum),
        ManagedCapability(
            ManagedArchiveCreationFormat.TarBZip2,
            true,
            ArchiveCompressionPreset.Fast,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Maximum),
        ManagedCapability(
            ManagedArchiveCreationFormat.GZip,
            false,
            ArchiveCompressionPreset.Balanced,
            ArchiveCompressionPreset.Maximum),
        ManagedCapability(
            ManagedArchiveCreationFormat.BZip2,
            false,
            ArchiveCompressionPreset.Fast,
            ArchiveCompressionPreset.Balanced),
    ]);

    private static ManagedWriteCapability ManagedCapability(
        ManagedArchiveCreationFormat format,
        bool supportsTree,
        params ArchiveCompressionPreset[] presets) => new(
            format,
            CanCreate: true,
            SupportsTreeCreation: supportsTree,
            CanRebuild: false,
            CompressionPresets: presets,
            MaximumEntries: format is ManagedArchiveCreationFormat.GZip
                or ManagedArchiveCreationFormat.BZip2
                ? ProtocolConstants.MaximumRawCreationEntries
                : ProtocolConstants.MaximumTarCreationEntries,
            MaximumSourceBytes: format is ManagedArchiveCreationFormat.GZip
                or ManagedArchiveCreationFormat.BZip2
                ? ProtocolConstants.MaximumRawCreationSourceBytes
                : ProtocolConstants.MaximumTarCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

    private sealed class RecordingActivitySink : IArchiveActivitySink
    {
        private readonly object _sync = new();
        private readonly List<ActivityOperationUpdate> _updates = [];

        public RecordingActivityHandle? Handle { get; private set; }
        public IReadOnlyList<ActivityOperationUpdate> Updates
        {
            get { lock (_sync) return _updates.ToArray(); }
        }
        public ActivityOperationUpdate? LastUpdate => Updates.LastOrDefault();

        public IArchiveActivityHandle Begin(ActivityOperationKind kind)
        {
            Handle = new RecordingActivityHandle(this, kind);
            return Handle;
        }

        private void Add(ActivityOperationUpdate update)
        {
            lock (_sync) _updates.Add(update);
        }

        public sealed class RecordingActivityHandle(
            RecordingActivitySink owner,
            ActivityOperationKind kind) : IArchiveActivityHandle
        {
            private Action? _cancel;

            public ActivityOperationKind Kind { get; } = kind;
            public bool CancellationPending { get; private set; }

            public void AttachCancel(Action cancel) => _cancel = cancel;
            public void Cancel() => _cancel?.Invoke();
            public void MarkCancellationPending() => CancellationPending = true;
            public void Report(ActivityOperationUpdate update) => owner.Add(update);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Rain Explorer M6F Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string relativePath)
        {
            string path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(string relativePath, string contents)
        {
            string path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    public sealed record CreationStart(
        string Method,
        string DestinationPath,
        IReadOnlyList<ArchiveCreationFile> Files,
        IReadOnlyList<ArchiveCreationDirectory> Directories,
        string? RawSourcePath,
        ArchiveCompressionPreset CompressionPreset,
        CancellationToken StartToken,
        OperationId OperationId);

    public class FakeArchiveClient : DispatchProxy
    {
        private EventHandler<OperationEvent>? _operationEventReceived;
        private readonly TaskCompletionSource<OperationEvent> _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IArchiveClient Client { get; private set; } = null!;
        public HashSet<string> Features { get; } = new(StringComparer.Ordinal);
        public GetZipWriteCapabilitiesResponse ZipCapabilities { get; set; } = SafeZipCapabilities();
        public GetTarWriteCapabilitiesResponse TarCapabilities { get; set; } = SafeTarCapabilities();
        public GetRarWriteCapabilitiesResponse RarCapabilities { get; set; } = SafeRarCapabilities();
        public GetManagedWriteCapabilitiesResponse ManagedCapabilities { get; set; } =
            SafeManagedCapabilities();
        public List<CreationStart> Starts { get; } = [];
        public int ZipCapabilityCalls { get; private set; }
        public int TarCapabilityCalls { get; private set; }
        public int RarCapabilityCalls { get; private set; }
        public int ManagedCapabilityCalls { get; private set; }
        public int TotalCapabilityCalls =>
            ZipCapabilityCalls + TarCapabilityCalls + RarCapabilityCalls + ManagedCapabilityCalls;
        public int CreateSecretCalls { get; private set; }
        public int DiscardSecretCalls { get; private set; }
        public int StartAttempts { get; private set; }
        public int CancelCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool BlockStart { get; set; }
        public bool BlockTerminal { get; set; }
        public bool RequireDestinationAtStart { get; set; }
        public ArchiveErrorCategory? StartErrorCategory { get; set; }
        public ArchiveErrorCategory? CancelErrorCategory { get; set; }
        public OperationPhase TerminalPhase { get; set; } = OperationPhase.Completed;
        public ArchiveErrorCategory? TerminalErrorCategory { get; set; }
        public string TerminalErrorCode { get; set; } = "test.terminal.failure";
        public string TerminalErrorMessage { get; set; } = "provider-controlled failure";
        public OperationActivityProgress? SnapshotProgress { get; set; }
        public int SnapshotWarningCount { get; set; }
        public OperationId StartedOperationId { get; private set; }
        public OperationId? CancelledOperationId { get; private set; }
        public OperationId? WaitedOperationId { get; private set; }
        public CancellationToken? CapturedStartToken { get; private set; }
        public bool StartTokenWasCancellationRequested { get; private set; }
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static FakeArchiveClient Create()
        {
            IArchiveClient client = DispatchProxy.Create<IArchiveClient, FakeArchiveClient>();
            var proxy = (FakeArchiveClient)(object)client;
            proxy.Client = client;
            proxy.Features.UnionWith(
            [
                ProtocolFeatures.OperationEvents,
                ProtocolFeatures.RequestCancellation,
                ProtocolFeatures.OperationActivity,
                ProtocolFeatures.ZipCreation,
                ProtocolFeatures.ZipDirectoryCreation,
                ProtocolFeatures.ZipWriteCapabilities,
                ProtocolFeatures.TarCreation,
                ProtocolFeatures.TarDirectoryCreation,
                ProtocolFeatures.TarWriteCapabilities,
                ProtocolFeatures.RarCreation,
                ProtocolFeatures.RarDirectoryCreation,
                ProtocolFeatures.RarWriteCapabilities,
                ProtocolFeatures.TarGZipCreation,
                ProtocolFeatures.TarGZipDirectoryCreation,
                ProtocolFeatures.TarBZip2Creation,
                ProtocolFeatures.TarBZip2DirectoryCreation,
                ProtocolFeatures.GZipCreation,
                ProtocolFeatures.BZip2Creation,
                ProtocolFeatures.ManagedWriteCapabilities,
            ]);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= Array.Empty<object>();
            switch (targetMethod.Name)
            {
                case "add_OperationEventReceived":
                    _operationEventReceived += (EventHandler<OperationEvent>)args[0]!;
                    return null;
                case "remove_OperationEventReceived":
                    _operationEventReceived -= (EventHandler<OperationEvent>)args[0]!;
                    return null;
                case nameof(IArchiveClient.SupportsFeature):
                    return Features.Contains((string)args[0]!);
                case "get_MaximumCreationTreeEntries":
                    return Features.Contains(ProtocolFeatures.ExpandedCreationTreeBudget) ? 65536 : 4096;
                case nameof(IArchiveClient.GetZipWriteCapabilitiesAsync):
                    ZipCapabilityCalls++;
                    return Task.FromResult(ZipCapabilities);
                case nameof(IArchiveClient.GetTarWriteCapabilitiesAsync):
                    TarCapabilityCalls++;
                    return Task.FromResult(TarCapabilities);
                case nameof(IArchiveClient.GetRarWriteCapabilitiesAsync):
                    RarCapabilityCalls++;
                    return Task.FromResult(RarCapabilities);
                case nameof(IArchiveClient.GetManagedWriteCapabilitiesAsync):
                    ManagedCapabilityCalls++;
                    return Task.FromResult(ManagedCapabilities);
                case nameof(IArchiveClient.StartZipCreationAsync):
                case nameof(IArchiveClient.StartZipTreeCreationAsync):
                case nameof(IArchiveClient.StartTarCreationAsync):
                case nameof(IArchiveClient.StartTarTreeCreationAsync):
                case nameof(IArchiveClient.StartRarCreationAsync):
                case nameof(IArchiveClient.StartRarTreeCreationAsync):
                case nameof(IArchiveClient.StartTarGZipCreationAsync):
                case nameof(IArchiveClient.StartTarGZipTreeCreationAsync):
                case nameof(IArchiveClient.StartTarBZip2CreationAsync):
                case nameof(IArchiveClient.StartTarBZip2TreeCreationAsync):
                case nameof(IArchiveClient.StartGZipCreationAsync):
                case nameof(IArchiveClient.StartBZip2CreationAsync):
                    return StartAsync(targetMethod.Name, args);
                case nameof(IArchiveClient.CreateSecretAsync):
                    CreateSecretCalls++;
                    return Task.FromResult(Guid.NewGuid());
                case nameof(IArchiveClient.DiscardSecretAsync):
                    DiscardSecretCalls++;
                    return Task.FromResult(true);
                case nameof(IArchiveClient.CancelOperationAsync):
                    CancelCalls++;
                    CancelledOperationId = (OperationId)args[0]!;
                    CancelCalled.TrySetResult();
                    if (CancelErrorCategory is ArchiveErrorCategory cancelCategory)
                    {
                        return Task.FromException<CancelOperationResponse>(
                            new ArchiveClientException(new ArchiveError(
                                "test.cancel.failure",
                                cancelCategory,
                                "provider-controlled cancellation failure")));
                    }
                    return Task.FromResult(new CancelOperationResponse(true));
                case nameof(IArchiveClient.WaitForTerminalAsync):
                    WaitedOperationId = (OperationId)args[0]!;
                    CancellationToken waitToken = (CancellationToken)args[1]!;
                    return BlockTerminal
                        ? _terminal.Task.WaitAsync(waitToken)
                        : Task.FromResult(CreateTerminal(TerminalPhase));
                case nameof(IArchiveClient.GetOperationActivityAsync):
                    return Task.FromResult(CreateActivityResponse());
                case nameof(IAsyncDisposable.DisposeAsync):
                    DisposeCalls++;
                    return ValueTask.CompletedTask;
                default:
                    throw new NotSupportedException($"Fake operation not configured: {targetMethod.Name}");
            }
        }

        public void Emit(OperationEvent operationEvent) =>
            _operationEventReceived?.Invoke(Client, operationEvent);

        public void CompleteTerminal(OperationPhase phase) =>
            _terminal.TrySetResult(CreateTerminal(phase));

        private async Task<StartOperationResponse> StartAsync(string method, object?[] args)
        {
            StartAttempts++;
            StartedOperationId = OperationId.New();
            CreationStart start = CaptureStart(method, args, StartedOperationId);
            Starts.Add(start);
            CapturedStartToken = start.StartToken;
            StartEntered.TrySetResult();
            if (BlockStart) await ReleaseStart.Task.ConfigureAwait(false);
            StartTokenWasCancellationRequested = start.StartToken.IsCancellationRequested;
            if (RequireDestinationAtStart && !File.Exists(start.DestinationPath))
                throw new InvalidOperationException("The race sentinel was not present at Worker dispatch.");
            if (StartErrorCategory is ArchiveErrorCategory category)
                throw new ArchiveClientException(new ArchiveError(
                    "test.start.failure", category, "provider-controlled start failure"));
            return new StartOperationResponse(StartedOperationId);
        }

        private CreationStart CaptureStart(string method, object?[] args, OperationId operationId)
        {
            string destination = (string)args[0]!;
            return method switch
            {
                nameof(IArchiveClient.StartZipCreationAsync)
                    or nameof(IArchiveClient.StartRarCreationAsync)
                    or nameof(IArchiveClient.StartTarGZipCreationAsync)
                    or nameof(IArchiveClient.StartTarBZip2CreationAsync) => new CreationStart(
                        method,
                        destination,
                        ((IReadOnlyList<ArchiveCreationFile>)args[1]!).ToArray(),
                        [],
                        null,
                        (ArchiveCompressionPreset)args[2]!,
                        (CancellationToken)args[3]!,
                        operationId),
                nameof(IArchiveClient.StartZipTreeCreationAsync)
                    or nameof(IArchiveClient.StartRarTreeCreationAsync)
                    or nameof(IArchiveClient.StartTarGZipTreeCreationAsync)
                    or nameof(IArchiveClient.StartTarBZip2TreeCreationAsync) => new CreationStart(
                        method,
                        destination,
                        ((IReadOnlyList<ArchiveCreationFile>)args[1]!).ToArray(),
                        ((IReadOnlyList<ArchiveCreationDirectory>)args[2]!).ToArray(),
                        null,
                        (ArchiveCompressionPreset)args[3]!,
                        (CancellationToken)args[4]!,
                        operationId),
                nameof(IArchiveClient.StartTarCreationAsync) => new CreationStart(
                    method,
                    destination,
                    ((IReadOnlyList<ArchiveCreationFile>)args[1]!).ToArray(),
                    [],
                    null,
                    ArchiveCompressionPreset.Store,
                    (CancellationToken)args[2]!,
                    operationId),
                nameof(IArchiveClient.StartTarTreeCreationAsync) => new CreationStart(
                    method,
                    destination,
                    ((IReadOnlyList<ArchiveCreationFile>)args[1]!).ToArray(),
                    ((IReadOnlyList<ArchiveCreationDirectory>)args[2]!).ToArray(),
                    null,
                    ArchiveCompressionPreset.Store,
                    (CancellationToken)args[3]!,
                    operationId),
                nameof(IArchiveClient.StartGZipCreationAsync)
                    or nameof(IArchiveClient.StartBZip2CreationAsync) => new CreationStart(
                        method,
                        destination,
                        [],
                        [],
                        (string)args[1]!,
                        (ArchiveCompressionPreset)args[2]!,
                        (CancellationToken)args[3]!,
                        operationId),
                _ => throw new NotSupportedException(method),
            };
        }

        private OperationEvent CreateTerminal(OperationPhase phase) => new(
            StartedOperationId,
            4,
            OperationEventKind.Terminal,
            phase,
            Error: TerminalErrorCategory is ArchiveErrorCategory category
                ? new ArchiveError(TerminalErrorCode, category, TerminalErrorMessage)
                : null);

        private GetOperationActivityResponse CreateActivityResponse()
        {
            OperationActivitySnapshot[] operations = SnapshotProgress is null
                ? []
                :
                [
                    new OperationActivitySnapshot(
                        StartedOperationId,
                        OperationKind.CreateArchive,
                        TerminalPhase,
                        LastEventSequence: 3,
                        CreatedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
                        UpdatedAtUtc: DateTimeOffset.UtcNow,
                        CompletedAtUtc: DateTimeOffset.UtcNow,
                        SnapshotProgress,
                        SnapshotWarningCount,
                        CanCancel: false),
                ];
            return new GetOperationActivityResponse(
                DateTimeOffset.UtcNow,
                ProtocolConstants.MaximumQueuedOperations,
                ProtocolConstants.MaximumConcurrentOperations,
                OperationQueueOrdering.Unspecified,
                ProtocolConstants.MaximumRetainedOperationActivity,
                SupportsReordering: false,
                SupportsPause: false,
                SupportsRetry: false,
                PersistsAcrossWorkerRestart: false,
                HistoryTruncated: false,
                operations);
        }
    }
}
