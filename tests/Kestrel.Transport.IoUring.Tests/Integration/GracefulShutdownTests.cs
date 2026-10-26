using System.Net;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
using static Kestrel.Transport.IoUring.Tests.Integration.TestHost;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// Kestrel stops a transport in this order: UnbindAsync (stop accepting), graceful close of established
/// connections, abort after the shutdown timeout, DisposeAsync. Unbind must therefore not touch live connections.
/// </summary>
public class GracefulShutdownTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InFlightRequest_CompletesWhenHostStops(int threadCount)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = BuildApp(o => o.ThreadCount = threadCount, a =>
            a.MapGet("/slow", async ctx =>
            {
                started.TrySetResult();
                await Task.Delay(500);
                await ctx.Response.WriteAsync("done");
            }));
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Task<string> request = client.GetStringAsync("/slow");
        await started.Task.WaitAsync(Short);

        Task stop = app.StopAsync();

        (await request.WaitAsync(Short)).Should().Be("done");
        await stop.WaitAsync(Short);
    }

    [Fact]
    public async Task Dispose_WithoutStop_DoesNotThrow()
    {
        // Host.DisposeAsync calls StopAsync with an already-cancelled token when the host was never stopped.
        // SocketTransport tolerates that; the io_uring listener must too.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring(o =>
        {
            o.RequireIoUring = Ring.IsSupported;
            TestTransport.ApplyLane(o);
        });
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();

        Func<Task> dispose = async () => await app.DisposeAsync();

        await dispose.Should().NotThrowAsync();
    }
}
