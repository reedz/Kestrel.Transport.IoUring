using System.Diagnostics;
using System.Net.Http;
using AspNetCoreUring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace AspNetCoreUring.Benchmarks;

/// <summary>
/// Quick throughput benchmark for iterating on performance changes.
/// Usage: dotnet run -c Release -- quick
/// </summary>
public static class QuickBench
{
    private const int SocketPort = 16080;
    private const int IoUringPort = 16081;
    private const int WarmupRequests = 500;
    private const int MeasuredRequests = 10000;
    private const int Concurrency = 32;

    public static async Task RunAsync()
    {
        Console.WriteLine($"io_uring supported: {Ring.IsSupported}");
        Console.WriteLine($"Warmup: {WarmupRequests} requests, Measured: {MeasuredRequests} requests, Concurrency: {Concurrency}");
        Console.WriteLine();

        // Start servers
        var socketApp = BuildSocketApp();
        await socketApp.StartAsync();

        WebApplication? iouringApp = null;
        if (Ring.IsSupported)
        {
            iouringApp = BuildIoUringApp();
            await iouringApp.StartAsync();
        }

        using var socketClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{SocketPort}") };
        using var iouringClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{IoUringPort}") };

        try
        {
            // Socket transport
            var socketResult = await BenchmarkTransport("Socket", socketClient, WarmupRequests, MeasuredRequests, Concurrency);
            PrintResult("Socket    ", socketResult);

            // io_uring transport
            if (Ring.IsSupported)
            {
                var iouringResult = await BenchmarkTransport("io_uring", iouringClient, WarmupRequests, MeasuredRequests, Concurrency);
                PrintResult("io_uring  ", iouringResult);

                Console.WriteLine();
                double ratio = iouringResult.MeanUs / socketResult.MeanUs;
                Console.WriteLine($"  io_uring / Socket ratio: {ratio:F3}x (< 1.0 = io_uring faster)");
            }
            else
            {
                Console.WriteLine("  io_uring: SKIPPED (not supported)");
            }
        }
        finally
        {
            if (iouringApp != null) await iouringApp.StopAsync();
            await socketApp.StopAsync();
        }
    }

    private static async Task<BenchResult> BenchmarkTransport(
        string name, HttpClient client, int warmup, int measured, int concurrency)
    {
        // Warmup
        for (int i = 0; i < warmup; i++)
            await client.GetAsync("/");

        // Force GC before measurement
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        long gen0Before = GC.CollectionCount(0);
        long gen1Before = GC.CollectionCount(1);

        // Measured phase — concurrent requests
        var sw = Stopwatch.StartNew();
        var semaphore = new SemaphoreSlim(concurrency);
        var tasks = new Task[measured];
        for (int i = 0; i < measured; i++)
        {
            await semaphore.WaitAsync();
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var resp = await client.GetAsync("/");
                    resp.EnsureSuccessStatusCode();
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }
        await Task.WhenAll(tasks);
        sw.Stop();

        long gen0After = GC.CollectionCount(0);
        long gen1After = GC.CollectionCount(1);

        return new BenchResult(
            measured,
            sw.Elapsed.TotalMilliseconds,
            gen0After - gen0Before,
            gen1After - gen1Before);
    }

    private static void PrintResult(string label, BenchResult r)
    {
        Console.WriteLine($"  {label}: {r.MeanUs,8:F1} µs/req | {r.ReqPerSec,8:F0} req/s | Total: {r.TotalMs,8:F1} ms | GC gen0: {r.Gen0Collects}, gen1: {r.Gen1Collects}");
    }

    private static WebApplication BuildSocketApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{SocketPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.MapGet("/", () => "Hello from Socket transport!");
        return app;
    }

    private static WebApplication BuildIoUringApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost
            .UseUrls($"http://localhost:{IoUringPort}")
            .UseIoUring(opts => { opts.RingSize = 256; });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.MapGet("/", () => "Hello from io_uring transport!");
        return app;
    }

    private record BenchResult(int RequestCount, double TotalMs, long Gen0Collects, long Gen1Collects)
    {
        public double MeanUs => TotalMs * 1000.0 / RequestCount;
        public double ReqPerSec => RequestCount / (TotalMs / 1000.0);
    }
}
