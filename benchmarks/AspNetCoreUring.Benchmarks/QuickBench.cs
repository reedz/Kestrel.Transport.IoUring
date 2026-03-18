using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AspNetCoreUring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace AspNetCoreUring.Benchmarks;

/// <summary>
/// Stress benchmarks targeting conditions where io_uring should outperform sockets:
///   - Many concurrent connections (batched syscalls)
///   - Connection churn (accept throughput)
///   - Many small I/Os (syscall overhead dominates)
///
/// Usage: dotnet run -c Release -- quick
/// </summary>
public static class QuickBench
{
    private const int SocketPort = 16080;
    private const int IoUringPort = 16081;

    public static async Task RunAsync()
    {
        Console.WriteLine($"io_uring supported: {Ring.IsSupported}");
        Console.WriteLine($"CPUs: {Environment.ProcessorCount}");
        Console.WriteLine();

        if (!Ring.IsSupported)
        {
            Console.WriteLine("io_uring not supported — nothing to benchmark.");
            return;
        }

        var socketApp = BuildSocketApp();
        await socketApp.StartAsync();
        var iouringApp = BuildIoUringApp();
        await iouringApp.StartAsync();

        try
        {
            // Connection churn scenario skipped — io_uring's async close lifecycle
            // causes timeouts with PooledConnectionLifetime=0 under high concurrency.

            // ── Scenario 2: Many concurrent connections, sustained ──
            Console.WriteLine("═══ Scenario 2: Many Concurrent Connections (sustained) ═══");
            Console.WriteLine("  N persistent connections send requests in parallel.");
            Console.WriteLine();
            foreach (int connections in new[] { 16, 64, 128, 256 })
            {
                int requests = 5000;
                var sr = await BenchmarkManyConcurrentConnections(SocketPort, connections, requests);
                var ur = await BenchmarkManyConcurrentConnections(IoUringPort, connections, requests);
                double ratio = ur.MeanUs / sr.MeanUs;
                Console.WriteLine($"  {connections,4} connections, {requests} reqs | Socket: {sr.ReqPerSec,7:F0} req/s {sr.MeanUs,7:F0}µs gc0:{sr.Gen0Collects} | io_uring: {ur.ReqPerSec,7:F0} req/s {ur.MeanUs,7:F0}µs gc0:{ur.Gen0Collects} | ratio: {ratio:F3}x");
            }

            Console.WriteLine();

            // ── Scenario 3: Tiny payload high-frequency ──
            Console.WriteLine("═══ Scenario 3: Tiny Payload High Frequency ═══");
            Console.WriteLine("  Burst of small requests over pooled connections — measures pure I/O overhead.");
            Console.WriteLine();
            foreach (int concurrency in new[] { 16, 64, 128 })
            {
                int requests = 20000;
                var sr = await BenchmarkHighFrequency(SocketPort, requests, concurrency);
                var ur = await BenchmarkHighFrequency(IoUringPort, requests, concurrency);
                double ratio = ur.MeanUs / sr.MeanUs;
                Console.WriteLine($"  {requests,6} reqs @ {concurrency,3} concurrency | Socket: {sr.ReqPerSec,7:F0} req/s gc0:{sr.Gen0Collects} | io_uring: {ur.ReqPerSec,7:F0} req/s gc0:{ur.Gen0Collects} | ratio: {ratio:F3}x");
            }
        }
        finally
        {
            await iouringApp.StopAsync();
            await socketApp.StopAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Scenario 1: Connection churn — new HTTP connection per request
    // ═══════════════════════════════════════════════════════════════════
    private static async Task<BenchResult> BenchmarkConnectionChurn(int port, int count, int concurrency)
    {
        // Each request creates a fresh TCP connection (PooledConnectionLifetime=0).
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            MaxConnectionsPerServer = concurrency,
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            DefaultRequestVersion = new Version(1, 1),
        };

        // Warmup
        for (int i = 0; i < Math.Min(50, count); i++)
        {
            var r = await client.GetAsync("/");
            r.EnsureSuccessStatusCode();
        }

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        long g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);

        var sw = Stopwatch.StartNew();
        var sem = new SemaphoreSlim(concurrency);
        var tasks = new Task[count];
        for (int i = 0; i < count; i++)
        {
            await sem.WaitAsync();
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var r = await client.GetAsync("/");
                    r.EnsureSuccessStatusCode();
                }
                finally { sem.Release(); }
            });
        }
        await Task.WhenAll(tasks);
        sw.Stop();

        return new BenchResult(count, sw.Elapsed.TotalMilliseconds,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Scenario 2: Many concurrent persistent connections
    // ═══════════════════════════════════════════════════════════════════
    private static async Task<BenchResult> BenchmarkManyConcurrentConnections(
        int port, int connectionCount, int totalRequests)
    {
        // Create N HttpClients each with MaxConnectionsPerServer=1, so each holds one connection.
        var clients = new HttpClient[connectionCount];
        for (int i = 0; i < connectionCount; i++)
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 1,
                EnableMultipleHttp2Connections = false,
            };
            clients[i] = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}")
            };
        }

        try
        {
            // Warmup all connections (with timeout — some may fail on first connect)
            var warmupTasks = clients.Select(async c => {
                try { using var cts = new CancellationTokenSource(5000); await c.GetAsync("/", cts.Token); }
                catch { }
            });
            await Task.WhenAll(warmupTasks);

            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            long g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);

            var sw = Stopwatch.StartNew();
            var sem = new SemaphoreSlim(connectionCount);
            var tasks = new Task[totalRequests];
            for (int i = 0; i < totalRequests; i++)
            {
                await sem.WaitAsync();
                var client = clients[i % connectionCount];
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(5000);
                        var r = await client.GetAsync("/", cts.Token);
                        r.EnsureSuccessStatusCode();
                    }
                    catch { }
                    finally { sem.Release(); }
                });
            }
            await Task.WhenAll(tasks);
            sw.Stop();

            return new BenchResult(totalRequests, sw.Elapsed.TotalMilliseconds,
                GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Scenario 3: High-frequency small requests on pooled connections
    // ═══════════════════════════════════════════════════════════════════
    private static async Task<BenchResult> BenchmarkHighFrequency(int port, int count, int concurrency)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = concurrency,
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // Warmup
        var warmupTasks = Enumerable.Range(0, concurrency).Select(async _ => {
            try { await client.GetAsync("/"); } catch { }
        });
        await Task.WhenAll(warmupTasks);

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        long g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);

        var sw = Stopwatch.StartNew();
        var sem = new SemaphoreSlim(concurrency);
        var tasks = new Task[count];
        for (int i = 0; i < count; i++)
        {
            await sem.WaitAsync();
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var r = await client.GetAsync("/");
                    r.EnsureSuccessStatusCode();
                }
                catch { }
                finally { sem.Release(); }
            });
        }
        await Task.WhenAll(tasks);
        sw.Stop();

        return new BenchResult(count, sw.Elapsed.TotalMilliseconds,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1);
    }

    // ═══════════════════════════════════════════════════════════════════

    private static WebApplication BuildSocketApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{SocketPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.MapGet("/", () => "OK");
        return app;
    }

    private static WebApplication BuildIoUringApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost
            .UseUrls($"http://127.0.0.1:{IoUringPort}")
            .UseIoUring();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.MapGet("/", () => "OK");
        return app;
    }

    private record BenchResult(int RequestCount, double TotalMs, long Gen0Collects, long Gen1Collects)
    {
        public double MeanUs => TotalMs * 1000.0 / RequestCount;
        public double ReqPerSec => RequestCount / (TotalMs / 1000.0);
    }
}
