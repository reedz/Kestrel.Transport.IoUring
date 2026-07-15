using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Integration;

public class ConnectionLimitIntegrationTests
{
    [Fact]
    public async Task RejectedConnection_DoesNotPreventSlotReuseAfterActiveRequestCompletes()
    {
        if (!Ring.IsSupported)
            return;

        int port = KestrelIntegrationTests.GetRandomPort();
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring(options =>
        {
            options.RingSize = 8;
            options.MaxConnections = 1;
            options.EnableBufferRing = false;
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port));

        await using WebApplication app = builder.Build();
        app.MapGet("/hold", async () =>
        {
            requestStarted.TrySetResult();
            await releaseRequest.Task;
            return "released";
        });
        app.MapGet("/", () => "available");
        await app.StartAsync();

        try
        {
            using var firstClient = new HttpClient();
            using var firstRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://127.0.0.1:{port}/hold");
            firstRequest.Headers.ConnectionClose = true;
            Task<HttpResponseMessage> active = firstClient.SendAsync(firstRequest);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var rejectedClient = new HttpClient();
            Func<Task> rejected = async () =>
                await rejectedClient.GetStringAsync($"http://127.0.0.1:{port}/");
            await rejected.Should().ThrowAsync<HttpRequestException>();

            releaseRequest.TrySetResult();
            using HttpResponseMessage activeResponse =
                await active.WaitAsync(TimeSpan.FromSeconds(5));
            activeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            activeResponse.Dispose();
            firstClient.Dispose();

            string? available = null;
            for (int attempt = 0; attempt < 100 && available == null; attempt++)
            {
                using var nextClient = new HttpClient();
                try
                {
                    available = await nextClient
                        .GetStringAsync($"http://127.0.0.1:{port}/")
                        .WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(10);
                }
            }
            available.Should().Be("available");
        }
        finally
        {
            releaseRequest.TrySetResult();
            await app.StopAsync();
        }
    }
}
