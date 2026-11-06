using System.Reflection;
using BenchmarkDotNet.Running;
using RainArchives.Benchmarks;

if (args.Length > 0
    && string.Equals(args[0], "--foundation-operation-probe", StringComparison.Ordinal))
{
    Environment.ExitCode = await FoundationOperationProbe.RunAsync(args[1..]);
    return;
}

if (args.Length > 0
    && string.Equals(args[0], "--physical-large-file-probe", StringComparison.Ordinal))
{
    Environment.ExitCode = await PhysicalLargeFileProbe.RunAsync(args[1..]);
    return;
}

if (args.Length > 0
    && string.Equals(args[0], "--creation-operation-probe", StringComparison.Ordinal))
{
    Environment.ExitCode = await CreationOperationProbe.RunAsync(args[1..]);
    return;
}

BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
