using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Integration;

public class MultiRingIntegrationTests
{
    [Fact]
    public async Task ConcurrentRequests_CompleteAcrossReusePortWorkers()
    {
        int port = KestrelIntegrationTests.GetRandomPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring(options =>
        {
            options.RingSize = 64;
            options.MaxConnections = 128;
            options.ThreadCount = 2;
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port));

        await using WebApplication app = builder.Build();
        app.MapGet("/", () => "multi-ring-ok");
        await app.StartAsync();

        try
        {
            using var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 100,
            };
            using var client = new HttpClient(handler);
            Task<string>[] requests = Enumerable.Range(0, 100)
                .Select(_ => client.GetStringAsync($"http://127.0.0.1:{port}/"))
                .ToArray();

            string[] responses = await Task.WhenAll(requests)
                .WaitAsync(TimeSpan.FromSeconds(15));

            responses.Should().OnlyContain(response => response == "multi-ring-ok");
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
