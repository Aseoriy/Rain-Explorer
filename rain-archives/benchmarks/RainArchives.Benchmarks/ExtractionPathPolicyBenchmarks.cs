using BenchmarkDotNet.Attributes;
using RainArchives.Security;

namespace RainArchives.Benchmarks;

[MemoryDiagnoser]
public class ExtractionPathPolicyBenchmarks
{
    private readonly ExtractionPathPolicy _policy = new();

    [Params(100, 100_000)]
    public int EntryCount { get; set; }

    [Benchmark]
    public int NormalizePaths()
    {
        var accepted = 0;
        for (var index = 0; index < EntryCount; index++)
        {
            if (_policy.TryNormalize(
                    $"folder-{index / 1000}/file-{index}.bin",
                    out _,
                    out _,
                    out _))
            {
                accepted++;
            }
        }

        return accepted;
    }
}
