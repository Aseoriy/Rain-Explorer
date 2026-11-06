using System.IO.Pipes;
using RainArchives.Security;

namespace RainArchives.Worker;

public static class WorkerProgram
{
    public static Task<int> RunAsync(string[] args) =>
        RunCoreAsync(
            args,
            recoveryDispositionCheckpoint: null,
            nestedRecoveryDiscardCheckpoint: null);

    internal static Task<int> RunAsync(
        string[] args,
        Action<ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint>
            recoveryDispositionCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(recoveryDispositionCheckpoint);
        return RunCoreAsync(
            args,
            recoveryDispositionCheckpoint,
            nestedRecoveryDiscardCheckpoint: null);
    }

    internal static Task<int> RunWithNestedRecoveryCheckpointAsync(
        string[] args,
        Action<NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint>
            nestedRecoveryDiscardCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(nestedRecoveryDiscardCheckpoint);
        return RunCoreAsync(
            args,
            recoveryDispositionCheckpoint: null,
            nestedRecoveryDiscardCheckpoint);
    }

    private static async Task<int> RunCoreAsync(
        string[] args,
        Action<ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint>?
            recoveryDispositionCheckpoint,
        Action<NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint>?
            nestedRecoveryDiscardCheckpoint)
    {
        try
        {
            var options = WorkerLaunchOptions.Parse(args);
            var extractionRecoveryRoot = ResolveExtractionRecoveryRoot(options);
            using var bootstrapPipe = new AnonymousPipeClientStream(PipeDirection.In, options.BootstrapHandle);
            using var bootstrapReader = new StreamReader(bootstrapPipe, leaveOpen: false);
            using var bootstrapTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var nonce = await ReadBootstrapNonceAsync(bootstrapReader, bootstrapTimeout.Token).ConfigureAwait(false);
            if (nonce is null)
            {
                return 2;
            }

            using var controlPipe = new NamedPipeClientStream(
                ".",
                options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await controlPipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            await using var host = recoveryDispositionCheckpoint is not null
                ? new WorkerHost(
                    controlPipe,
                    nonce,
                    options.CreationRecoveryRoot,
                    extractionRecoveryRoot,
                    recoveryDispositionCheckpoint)
                : nestedRecoveryDiscardCheckpoint is not null
                    ? new WorkerHost(
                        controlPipe,
                        nonce,
                        options.CreationRecoveryRoot,
                        extractionRecoveryRoot,
                        nestedRecoveryDiscardCheckpoint)
                    : new WorkerHost(
                        controlPipe,
                        nonce,
                        options.CreationRecoveryRoot,
                        extractionRecoveryRoot);
            await host.RunAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 3;
        }
        catch
        {
            return 1;
        }
    }

    internal static string? ResolveExtractionRecoveryRoot(WorkerLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExtractionRecoveryRoot is not null)
        {
            return options.ExtractionRecoveryRoot;
        }

        if (options.CreationRecoveryRoot is null
            || !string.Equals(
                Path.GetFileName(options.CreationRecoveryRoot),
                "rainarchives-creation-recovery",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(options.CreationRecoveryRoot);
        return parent is null
            ? null
            : Path.Combine(parent, "rainarchives-extraction-recovery");
    }

    private static async Task<string?> ReadBootstrapNonceAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        const int nonceLength = 64;
        var nonceCharacters = new char[nonceLength];
        var singleCharacter = new char[1];
        var count = 0;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(singleCharacter.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0 || singleCharacter[0] == '\n')
                {
                    break;
                }

                if (singleCharacter[0] == '\r')
                {
                    var lineFeed = await reader.ReadAsync(singleCharacter.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (lineFeed != 1 || singleCharacter[0] != '\n')
                    {
                        return null;
                    }

                    break;
                }

                if (count == nonceLength)
                {
                    return null;
                }

                nonceCharacters[count++] = singleCharacter[0];
            }

            if (count != nonceLength
                || nonceCharacters.Any(character =>
                    character is not (>= '0' and <= '9')
                    && character is not (>= 'a' and <= 'f')))
            {
                return null;
            }

            return new string(nonceCharacters);
        }
        finally
        {
            Array.Clear(nonceCharacters);
            Array.Clear(singleCharacter);
        }
    }
}

public sealed record WorkerLaunchOptions(
    string PipeName,
    string BootstrapHandle,
    string? CreationRecoveryRoot,
    string? ExtractionRecoveryRoot)
{
    public static WorkerLaunchOptions Parse(string[] args)
    {
        string? pipe = null;
        string? bootstrap = null;
        string? creationRecoveryRoot = null;
        string? extractionRecoveryRoot = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pipe" when pipe is null && index + 1 < args.Length:
                    pipe = args[++index];
                    break;
                case "--bootstrap-handle" when bootstrap is null && index + 1 < args.Length:
                    bootstrap = args[++index];
                    break;
                case "--creation-recovery-root" when creationRecoveryRoot is null && index + 1 < args.Length:
                    creationRecoveryRoot = args[++index];
                    break;
                case "--extraction-recovery-root" when extractionRecoveryRoot is null && index + 1 < args.Length:
                    extractionRecoveryRoot = args[++index];
                    break;
                default:
                    throw new ArgumentException("Worker launch arguments were invalid.");
            }
        }

        if (!IsValidPipeName(pipe)
            || string.IsNullOrWhiteSpace(bootstrap)
            || !ulong.TryParse(bootstrap, out _))
        {
            throw new ArgumentException("Worker launch arguments were incomplete.");
        }

        if (creationRecoveryRoot is not null)
        {
            if (!Path.IsPathFullyQualified(creationRecoveryRoot))
            {
                throw new ArgumentException("The archive-creation recovery root must be fully qualified.");
            }

            creationRecoveryRoot = Path.GetFullPath(creationRecoveryRoot);
        }

        if (extractionRecoveryRoot is not null)
        {
            if (!Path.IsPathFullyQualified(extractionRecoveryRoot))
            {
                throw new ArgumentException("The extraction recovery root must be fully qualified.");
            }

            extractionRecoveryRoot = Path.GetFullPath(extractionRecoveryRoot);
        }

        return new WorkerLaunchOptions(
            pipe!,
            bootstrap,
            creationRecoveryRoot,
            extractionRecoveryRoot);
    }

    private static bool IsValidPipeName(string? value)
    {
        const string prefix = "rainarchives-";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 64)
        {
            return false;
        }

        foreach (var character in value.AsSpan(prefix.Length))
        {
            var decimalDigit = character >= '0' && character <= '9';
            var lowerHexLetter = character >= 'a' && character <= 'f';
            if (!decimalDigit && !lowerHexLetter)
            {
                return false;
            }
        }

        return true;
    }
}
