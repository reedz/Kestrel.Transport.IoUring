using Kestrel.Transport.IoUring.Benchmarks;

if (args.Length > 0 && args[0] == "quick")
{
    await QuickBench.RunAsync();
    return;
}

BenchmarkDotNet.Running.BenchmarkRunner.Run<HttpBenchmark>();
