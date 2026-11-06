using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Cli;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class CliZipWriteCapabilitiesTests
{
    [TestMethod]
    public void CapabilitiesDiagnosticAcceptsOnlyWorkerOptionAndNoArchivePath()
    {
        var parsed = CliArguments.TryParse(
            ["capabilities", "--worker", @"C:\worker\RainArchives.Worker.exe"],
            out var options,
            out var error);

        Assert.IsTrue(parsed, error);
        Assert.AreEqual(CliCommand.Capabilities, options.Command);
        Assert.AreEqual(string.Empty, options.ArchivePath);
        Assert.AreEqual(0, options.CreationFiles.Count);
    }

    [TestMethod]
    public void CapabilitiesDiagnosticRejectsPositionalArchiveAndPreset()
    {
        Assert.IsFalse(CliArguments.TryParse(
            ["capabilities", @"C:\archive.zip"],
            out _,
            out var pathError));
        StringAssert.Contains(pathError, "does not accept a positional path");

        Assert.IsFalse(CliArguments.TryParse(
            ["capabilities", "--preset", "balanced"],
            out _,
            out var presetError));
        StringAssert.Contains(presetError, "valid only for archive creation or rebuild-zip");
    }

    [TestMethod]
    public void RebuildCapabilityPreflightRefusesBeforeAnySourceOrPasswordPhase()
    {
        Assert.IsTrue(CliArguments.TryParse(
            ["rebuild-zip", @"C:\source.zip", @"C:\destination.zip", "--confirm-rebuild-copy"],
            out var options,
            out var parseError), parseError);
        var refused = new GetZipWriteCapabilitiesResponse(
            CanCreate: false,
            CanRebuild: false,
            SupportsAdd: false,
            SupportsReplace: false,
            SupportsDelete: false,
            SupportsFileRename: false,
            SupportsTreeEdit: false,
            SupportsMetadata: false,
            CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumZipCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

        var sourceOpened = false;
        var passwordRead = false;
        if (CliProgram.TryValidateRebuildCapabilities(
                refused,
                options,
                requireRebuildEligibility: false,
                out var error))
        {
            sourceOpened = true;
            passwordRead = true;
        }

        Assert.IsFalse(sourceOpened);
        Assert.IsFalse(passwordRead);
        StringAssert.Contains(error, "managed ZIP writing");
    }
}
