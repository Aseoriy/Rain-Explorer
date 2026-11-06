using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Worker;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class WorkerLaunchOptionsTests
{
    private const string PipeName =
        "rainarchives-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void RecoveryRootIsOptional()
    {
        var options = WorkerLaunchOptions.Parse(
            ["--pipe", PipeName, "--bootstrap-handle", "1234"]);

        Assert.AreEqual(PipeName, options.PipeName);
        Assert.AreEqual("1234", options.BootstrapHandle);
        Assert.IsNull(options.CreationRecoveryRoot);
        Assert.IsNull(options.ExtractionRecoveryRoot);
    }

    [TestMethod]
    public void FullyQualifiedRecoveryRootIsCanonicalized()
    {
        var recoveryRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.WorkerLaunchOptionsTests",
            "recovery");
        var extractionRecoveryRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.WorkerLaunchOptionsTests",
            "extraction-recovery");

        var options = WorkerLaunchOptions.Parse(
            [
                "--pipe",
                PipeName,
                "--bootstrap-handle",
                "1234",
                "--creation-recovery-root",
                recoveryRoot,
                "--extraction-recovery-root",
                extractionRecoveryRoot,
            ]);

        Assert.AreEqual(Path.GetFullPath(recoveryRoot), options.CreationRecoveryRoot);
        Assert.AreEqual(
            Path.GetFullPath(extractionRecoveryRoot),
            options.ExtractionRecoveryRoot);
    }

    [TestMethod]
    public void RelativeOrDuplicateRecoveryRootIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WorkerLaunchOptions.Parse(
            [
                "--pipe",
                PipeName,
                "--bootstrap-handle",
                "1234",
                "--creation-recovery-root",
                "relative-recovery",
            ]));

        var recoveryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "recovery"));
        Assert.ThrowsExactly<ArgumentException>(() => WorkerLaunchOptions.Parse(
            [
                "--pipe",
                PipeName,
                "--bootstrap-handle",
                "1234",
                "--creation-recovery-root",
                recoveryRoot,
                "--creation-recovery-root",
                recoveryRoot,
            ]));

        Assert.ThrowsExactly<ArgumentException>(() => WorkerLaunchOptions.Parse(
            [
                "--pipe",
                PipeName,
                "--bootstrap-handle",
                "1234",
                "--extraction-recovery-root",
                "relative-extraction-recovery",
            ]));

        var extractionRecoveryRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "extraction-recovery"));
        Assert.ThrowsExactly<ArgumentException>(() => WorkerLaunchOptions.Parse(
            [
                "--pipe",
                PipeName,
                "--bootstrap-handle",
                "1234",
                "--extraction-recovery-root",
                extractionRecoveryRoot,
                "--extraction-recovery-root",
                extractionRecoveryRoot,
            ]));
    }

    [TestMethod]
    public void TestOwnedCreationRootDerivesBackwardCompatibleExtractionRoot()
    {
        var temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rain-worker-temp"));
        var creationRoot = Path.Combine(
            temporaryRoot,
            "rainarchives-creation-recovery");
        var options = WorkerLaunchOptions.Parse(
            [
                "--pipe",
                PipeName,
                "--bootstrap-handle",
                "1234",
                "--creation-recovery-root",
                creationRoot,
            ]);

        Assert.AreEqual(
            Path.Combine(temporaryRoot, "rainarchives-extraction-recovery"),
            WorkerProgram.ResolveExtractionRecoveryRoot(options));
    }
}
