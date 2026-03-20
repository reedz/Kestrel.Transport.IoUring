using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// End-to-end integration tests: HttpClient → Kestrel with io_uring transport.
/// </summary>
public class KestrelIntegrationTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        int port = GetRandomPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));

        _app = builder.Build();
        _app.MapGet("/", () => "Hello from io_uring Kestrel!");
        _app.MapPost("/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Text(body, "text/plain");
        });
        _app.MapGet("/large/{sizeKb}", (int sizeKb) =>
        {
            var data = new byte[sizeKb * 1024];
            Random.Shared.NextBytes(data);
            return Results.Bytes(data, "application/octet-stream");
        });

        await _app.StartAsync();
        _baseUrl = $"http://127.0.0.1:{port}";
        _client = new HttpClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Get_ReturnsOk()
    {
        var response = await _client.GetAsync($"{_baseUrl}/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Hello from io_uring Kestrel!");
    }

    [Fact]
    public async Task Post_EchoBody()
    {
        var content = new StringContent("Test payload", System.Text.Encoding.UTF8, "text/plain");
        var response = await _client.PostAsync($"{_baseUrl}/echo", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Test payload");
    }

    [Fact]
    public async Task Get_LargePayload_64KB()
    {
        var response = await _client.GetAsync($"{_baseUrl}/large/64");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await response.Content.ReadAsByteArrayAsync();
        data.Length.Should().Be(64 * 1024);
    }

    [Fact]
    public async Task ConcurrentRequests_50()
    {
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => _client.GetAsync($"{_baseUrl}/"))
            .ToArray();
        var responses = await Task.WhenAll(tasks);
        responses.Should().AllSatisfy(r =>
            r.StatusCode.Should().Be(HttpStatusCode.OK));
    }

    [Fact]
    public async Task SequentialRequests_KeepAlive()
    {
        for (int i = 0; i < 20; i++)
        {
            var response = await _client.GetAsync($"{_baseUrl}/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Post_LargeBody_100KB()
    {
        var payload = new string('Z', 100_000);
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "text/plain");
        var response = await _client.PostAsync($"{_baseUrl}/echo", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be(payload);
    }

    private static int GetRandomPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
