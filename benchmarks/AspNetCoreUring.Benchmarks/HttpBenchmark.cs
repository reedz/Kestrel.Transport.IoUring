using System.Net.Http;
using System.Threading.Tasks;
using AspNetCoreUring;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AspNetCoreUring.Benchmarks;

[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
public class HttpBenchmark : IDisposable
{
    private WebApplication? _socketApp;
    private WebApplication? _iouringApp;
    private HttpClient _socketClient = null!;
    private HttpClient _iouringClient = null!;
    private const int SocketPort = 15080;
    private const int IoUringPort = 15081;

    [GlobalSetup]
    public async Task Setup()
    {
        _socketApp = BuildSocketApp();
        await _socketApp.StartAsync();

        if (Ring.IsSupported)
        {
            _iouringApp = BuildIoUringApp();
            await _iouringApp.StartAsync();
        }

        _socketClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{SocketPort}") };
        _iouringClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{IoUringPort}") };

        // warmup
        await _socketClient.GetAsync("/");
        if (Ring.IsSupported)
            await _iouringClient.GetAsync("/");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _socketClient?.Dispose();
        _iouringClient?.Dispose();
        if (_socketApp != null) await _socketApp.StopAsync();
        if (_iouringApp != null) await _iouringApp.StopAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<string> SocketTransport()
    {
        var response = await _socketClient.GetAsync("/");
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> IoUringTransport()
    {
        if (!Ring.IsSupported)
            return await SocketTransport();

        var response = await _iouringClient.GetAsync("/");
        return await response.Content.ReadAsStringAsync();
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

    public void Dispose()
    {
        _socketApp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _iouringApp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
