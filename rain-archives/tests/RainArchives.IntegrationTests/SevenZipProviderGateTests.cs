using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.SevenZip;
using RainArchives.Providers.SharpCompress;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class SevenZipProviderGateTests
{
    [TestMethod]
    public void DisabledNativeGateReturnsNoMatchForSevenZipAndRar()
    {
        var provider = new SevenZipProviderGate(gateApproved: false);

        Assert.IsFalse(
            provider.LibraryPresent,
            "The managed-only test output must not carry the gated native 7z.dll asset.");

        var sevenZip = provider.Probe(
            [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C],
            "fixture.7z");
        var rar = provider.Probe(
            [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07],
            "fixture.rar");

        Assert.AreEqual("unknown", sevenZip.Format);
        Assert.AreEqual(0, sevenZip.Confidence);
        Assert.AreEqual("unknown", rar.Format);
        Assert.AreEqual(0, rar.Confidence);
    }

    [TestMethod]
    public async Task DisabledNativeGateOpenRefusesWithStableStructuredCode()
    {
        var provider = new SevenZipProviderGate(gateApproved: false);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await provider.OpenAsync(
                "fixture.7z",
                ReadOnlyMemory<byte>.Empty,
                CancellationToken.None));

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, exception.Category);
        Assert.AreEqual("provider.7zip_gate_incomplete", exception.Code);
    }

    [TestMethod]
    public void RegistrySelectsManagedProviderWhenNativeGateIsDisabled()
    {
        var nativeGate = new SevenZipProviderGate(gateApproved: false);
        var managedProvider = new SharpCompressArchiveProvider();
        var registry = new ArchiveProviderRegistry([nativeGate, managedProvider]);

        var selectedSevenZip = registry.Select(
            [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C],
            "fixture.7z");
        var selectedRar = registry.Select(
            [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07],
            "fixture.rar");

        Assert.AreSame(managedProvider, selectedSevenZip);
        Assert.AreSame(managedProvider, selectedRar);
        Assert.AreEqual("sharpcompress/0.50.4", selectedSevenZip.Id);
        Assert.AreEqual("sharpcompress/0.50.4", selectedRar.Id);
    }
}
