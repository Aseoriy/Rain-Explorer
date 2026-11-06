using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Providers.SevenZip;

/// <summary>
/// Holds the native-provider adoption gate. The official 7z.dll is not loaded
/// until the package/source/license and callback conformance checks have passed.
/// </summary>
public sealed class SevenZipProviderGate : IArchiveProvider
{
    private readonly string _libraryPath;

    public SevenZipProviderGate(bool gateApproved)
    {
        _libraryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "native",
            "win-x64",
            "7z.dll"));
        GateApproved = gateApproved;
    }

    public string Id => "7zip/26.02";
    public bool GateApproved { get; }
    public bool LibraryPresent => File.Exists(_libraryPath);

    public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath)
    {
        if (!GateApproved || !LibraryPresent)
        {
            return ArchiveProbeResult.NoMatch("The native 7-Zip provider gate has not been satisfied.");
        }

        var format = ArchiveSignatures.Detect(prefix);
        return format is "7z" or "rar"
            ? new ArchiveProbeResult(format, 100, "Official 7-Zip provider selected for this format.")
            : ArchiveProbeResult.NoMatch("The native provider is not authoritative for this signature.");
    }

    public ValueTask<IArchiveHandle> OpenAsync(
        string sourcePath,
        ReadOnlyMemory<byte> passwordUtf8,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<IArchiveHandle>(new ArchiveProviderException(
            ArchiveErrorCategory.UnsupportedCapability,
            "provider.7zip_gate_incomplete",
            "The 7-Zip provider is present but its audited adapter has not been enabled."));
}
