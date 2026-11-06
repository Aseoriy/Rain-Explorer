using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveOleTransferGateTests
{
    [Fact]
    public void WpfDelegatesOnlyGetDataButLacksNativeAsyncCapabilityAndGateStaysOff()
    {
        RunSta(() =>
        {
            var source = new CountingDataObject();
            var wrapper = new DataObject((System.Windows.IDataObject)source);

            Assert.Contains(DataFormats.FileDrop, wrapper.GetFormats(autoConvert: true));
            Assert.True(wrapper.GetDataPresent(DataFormats.FileDrop, autoConvert: true));
            Assert.Equal(0, source.GetDataCount);
            Assert.Equal(["C:\\not-published.txt"],
                Assert.IsType<string[]>(wrapper.GetData(DataFormats.FileDrop, autoConvert: true)));
            Assert.Equal(1, source.GetDataCount);
            Assert.Equal(unchecked((int)0x80004002),
                ArchiveOleTransferGate.QueryAsyncCapability(wrapper));
            Assert.False(ArchiveOleTransferGate.IsEnabled);
            Assert.False(ArchiveOleTransferGate.HasWorkerPublicationIdentity);
            Assert.Contains("protocol 1.22", ArchiveOleTransferGate.DisabledReason);

            using var temporary = new TemporaryDirectory();
            int extractionCount = 0;
            var coordinator = new ArchiveTransferCoordinator(
                (_, _, _, _, _) =>
                {
                    extractionCount++;
                    return Task.FromResult(new ArchiveOperationResult(ArchiveOperationOutcome.Failed));
                },
                new ArchiveTransferStorage(Path.Combine(temporary.Path, "transfer-root")));
            ArchiveTransferOwner owner = Assert.IsType<ArchiveTransferOwner>(
                coordinator.BeginAsync().GetAwaiter().GetResult());

            Assert.False(ArchiveOleTransferGate.TryCreateDataObject(owner, out DataObject? disabled));
            Assert.Null(disabled);
            Assert.False(owner.HasStartedRendering);
            Assert.Equal(0, extractionCount);

            owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetRefused)
                .GetAwaiter().GetResult();
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The STA/OLE proof did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class CountingDataObject : System.Windows.IDataObject
    {
        public int GetDataCount { get; private set; }

        public object GetData(string format, bool autoConvert)
        {
            GetDataCount++;
            return new[] { "C:\\not-published.txt" };
        }

        public object GetData(string format) => GetData(format, autoConvert: true);
        public object GetData(Type format) => GetData(format.FullName ?? format.Name);
        public bool GetDataPresent(string format, bool autoConvert) =>
            string.Equals(format, DataFormats.FileDrop, StringComparison.Ordinal);
        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
        public bool GetDataPresent(Type format) => false;
        public string[] GetFormats(bool autoConvert) => [DataFormats.FileDrop];
        public string[] GetFormats() => GetFormats(autoConvert: true);
        public void SetData(string format, object data, bool autoConvert) =>
            throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainExplorer-ArchiveOle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
