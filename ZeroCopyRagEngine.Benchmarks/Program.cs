using BenchmarkDotNet.Running;

namespace ModernAiAssistant.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        var summary1 = BenchmarkRunner.Run<ChunkerBenchmarks>();
        var summary2 = BenchmarkRunner.Run<VectorSearchBenchmarks>();
    }
}
