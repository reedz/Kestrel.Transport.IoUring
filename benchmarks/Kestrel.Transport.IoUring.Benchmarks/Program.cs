using Kestrel.Transport.IoUring.Benchmarks;

if (args.Length > 0 && args[0] == "quick")
{
    await QuickBench.RunAsync(args[1..]);
    return;
}

if (args.Length == 5 && args[0] == "server")
{
    await QuickBench.RunServerAsync(
        args[1],
        int.Parse(args[2]),
        int.Parse(args[3]),
        int.Parse(args[4]));
    return;
}

BenchmarkDotNet.Running.BenchmarkRunner.Run<HttpBenchmark>();
