using System.Text;
using RainArchives.Providers.WinRar;
using RainArchives.Security;
using RainArchives.Worker;

namespace RainArchives.RecoveryCrashProbe;

internal static class Program
{
    private const string CheckpointConfigurationFileName =
        ".rainarchives-recovery-disposition-checkpoint";
    private const string CheckpointReadyFileName =
        ".rainarchives-recovery-disposition-checkpoint.ready";
    private const string NestedCheckpointConfigurationFileName =
        ".rainarchives-nested-recovery-discard-checkpoint";
    private const string NestedCheckpointReadyFileName =
        ".rainarchives-nested-recovery-discard-checkpoint.ready";
    private const string RarWorkspaceCheckpointSwitch =
        "--rar-workspace-checkpoint";

    public static Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(2);
        }

        if (TryReadRarWorkspaceCheckpointArguments(
                args,
                out var rarWorkspaceRoot,
                out var rarWorkspaceNonce,
                out var rarWorkspaceReadyPath))
        {
            return RunRarWorkspaceCrashProbe(
                rarWorkspaceRoot,
                rarWorkspaceNonce,
                rarWorkspaceReadyPath);
        }

        if (TryReadCreationCheckpointConfiguration(
                out var creationCheckpoint,
                out var creationReadyPath))
        {
            var signaled = 0;
            return WorkerProgram.RunAsync(
                args,
                checkpoint => SignalCheckpoint(
                    checkpoint,
                    creationCheckpoint,
                    creationReadyPath,
                    ref signaled));
        }

        if (TryReadNestedCheckpointConfiguration(
                out var nestedCheckpoint,
                out var nestedReadyPath))
        {
            var signaled = 0;
            return WorkerProgram.RunWithNestedRecoveryCheckpointAsync(
                args,
                checkpoint => SignalCheckpoint(
                    checkpoint,
                    nestedCheckpoint,
                    nestedReadyPath,
                    ref signaled));
        }

        return Task.FromResult(2);
    }

    private static bool TryReadRarWorkspaceCheckpointArguments(
        string[] args,
        out string workspaceRoot,
        out string nonce,
        out string readyPath)
    {
        workspaceRoot = string.Empty;
        nonce = string.Empty;
        readyPath = string.Empty;
        try
        {
            if (args.Length != 4
                || !string.Equals(args[0], RarWorkspaceCheckpointSwitch, StringComparison.Ordinal)
                || !Path.IsPathFullyQualified(args[1])
                || !Path.IsPathFullyQualified(args[2]))
            {
                return false;
            }

            workspaceRoot = Path.GetFullPath(args[1]);
            readyPath = Path.GetFullPath(args[2]);
            nonce = args[3];
            return Directory.Exists(workspaceRoot)
                && string.Equals(
                    Path.GetDirectoryName(readyPath),
                    Path.GetDirectoryName(workspaceRoot),
                    StringComparison.OrdinalIgnoreCase)
                && nonce.Length == 64
                && nonce.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                && !File.Exists(readyPath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            workspaceRoot = string.Empty;
            nonce = string.Empty;
            readyPath = string.Empty;
            return false;
        }
    }

    private static Task<int> RunRarWorkspaceCrashProbe(
        string workspaceRoot,
        string nonce,
        string readyPath)
    {
        using var workspace = WinRarEncoderWorkspace.Create(workspaceRoot);
        var input = Path.Combine(workspace.DirectoryPath, "input");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(
            Path.Combine(input, "materialized-source.bin"),
            Enumerable.Range(0, 4097).Select(value => (byte)value).ToArray());

        using (var signal = new FileStream(
                   readyPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(
                   signal,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   leaveOpen: true))
        {
            writer.WriteLine($"READY:{nonce}");
            writer.Write(workspace.DirectoryPath);
            writer.Flush();
            signal.Flush(flushToDisk: true);
        }

        Thread.Sleep(Timeout.Infinite);
        return Task.FromResult(0);
    }

    private static bool TryReadCreationCheckpointConfiguration(
        out ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint checkpoint,
        out string readyPath) =>
        TryReadCheckpointConfiguration(
            CheckpointConfigurationFileName,
            CheckpointReadyFileName,
            out checkpoint,
            out readyPath);

    private static bool TryReadNestedCheckpointConfiguration(
        out NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint checkpoint,
        out string readyPath) =>
        TryReadCheckpointConfiguration(
            NestedCheckpointConfigurationFileName,
            NestedCheckpointReadyFileName,
            out checkpoint,
            out readyPath);

    private static bool TryReadCheckpointConfiguration<TCheckpoint>(
        string configurationFileName,
        string readyFileName,
        out TCheckpoint checkpoint,
        out string readyPath)
        where TCheckpoint : struct, Enum
    {
        checkpoint = default;
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var configurationPath = Path.Combine(temporaryRoot, configurationFileName);
        readyPath = Path.Combine(temporaryRoot, readyFileName);

        try
        {
            if (File.Exists(readyPath))
            {
                return false;
            }

            var value = File.ReadAllText(configurationPath, Encoding.UTF8);
            return Enum.TryParse(value, ignoreCase: false, out checkpoint)
                && Enum.IsDefined(checkpoint)
                && string.Equals(value, checkpoint.ToString(), StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static void SignalCheckpoint<TCheckpoint>(
        TCheckpoint checkpoint,
        TCheckpoint expectedCheckpoint,
        string readyPath,
        ref int signaled)
        where TCheckpoint : struct, Enum
    {
        if (!EqualityComparer<TCheckpoint>.Default.Equals(checkpoint, expectedCheckpoint)
            || Interlocked.Exchange(ref signaled, 1) != 0)
        {
            return;
        }

        using (var signal = new FileStream(
                   readyPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(
                   signal,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   leaveOpen: true))
        {
            writer.Write($"READY:{checkpoint}");
            writer.Flush();
            signal.Flush(flushToDisk: true);
        }

        Thread.Sleep(Timeout.Infinite);
    }
}
