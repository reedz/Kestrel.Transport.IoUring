using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
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
        _app.MapGet("/endpoints", (HttpContext ctx) =>
            $"{ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}|" +
            $"{ctx.Connection.LocalIpAddress}:{ctx.Connection.LocalPort}");
        _app.MapGet("/abort", (HttpContext ctx) =>
        {
            ctx.Abort();
            return Task.CompletedTask;
        });
        _app.MapPost("/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Text(body, "text/plain");
        });
        _app.MapPost("/slow-upload", async (HttpContext ctx) =>
        {
            byte[] buffer = new byte[4096];
            int total = 0;
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer)) != 0)
            {
                total += read;
                await Task.Delay(1);
            }
            return Results.Text(total.ToString());
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

    [Fact]
    public async Task ConnectionClose_DrainsFinalResponseAndSendsFin()
    {
        var uri = new Uri(_baseUrl);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, uri.Port);
        await using var stream = client.GetStream();

        byte[] request = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
                break;
            response.Write(buffer, 0, read);
        }

        string text = Encoding.ASCII.GetString(response.ToArray());
        text.Should().Contain("HTTP/1.1 200");
        text.Should().Contain("Hello from io_uring Kestrel!");
        text.Should().EndWith("0\r\n\r\n");

        for (int i = 0; i < 100; i++)
        {
            if (Volatile.Read(ref IoUringConnectionListener.s_activeConnections) == 0)
                break;
            await Task.Delay(10);
        }
        Volatile.Read(ref IoUringConnectionListener.s_activeConnections).Should().Be(0);
    }

    [Fact]
    public async Task ConnectionEndpoints_ArePopulated()
    {
        string endpoints = await _client.GetStringAsync($"{_baseUrl}/endpoints");

        endpoints.Should().MatchRegex(
            @"^127\.0\.0\.1:\d+\|127\.0\.0\.1:\d+$");
    }

    [Fact]
    public async Task UnixDomainSocket_UsesSocketTransportFallback()
    {
        string socketPath = Path.Combine(Path.GetTempPath(), $"iouring-{Guid.NewGuid():N}.sock");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath));

        await using var app = builder.Build();
        app.MapGet("/", () => "uds fallback");
        await app.StartAsync();

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(socketPath),
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        try
        {
            (await client.GetStringAsync("/")).Should().Be("uds fallback");
        }
        finally
        {
            await app.StopAsync();
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task RepeatedApplicationAborts_ReclaimConnections()
    {
        var uri = new Uri(_baseUrl);

        for (int i = 0; i < 100; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, uri.Port);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /abort HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            byte[] buffer = new byte[256];
            try
            {
                while (await stream.ReadAsync(buffer, timeout.Token) != 0)
                {
                }
            }
            catch (IOException)
            {
                // An aborted connection may terminate with EOF or RST.
            }
        }

        for (int i = 0; i < 100; i++)
        {
            if (Volatile.Read(ref IoUringConnectionListener.s_activeConnections) == 0)
                break;
            await Task.Delay(20);
        }

        Volatile.Read(ref IoUringConnectionListener.s_activeConnections).Should().Be(0);
        (await _client.GetStringAsync($"{_baseUrl}/"))
            .Should().Be("Hello from io_uring Kestrel!");
    }

    [Fact]
    public async Task SlowUpload_AppliesBackpressureWithoutStalling()
    {
        byte[] payload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        using var response = await _client.PostAsync(
            $"{_baseUrl}/slow-upload",
            new ByteArrayContent(payload));

        response.EnsureSuccessStatusCode();
        (await response.Content.ReadAsStringAsync()).Should().Be(payload.Length.ToString());
        (await _client.GetStringAsync($"{_baseUrl}/"))
            .Should().Be("Hello from io_uring Kestrel!");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PortZero_ReportsActualBoundEndpoint(int threadCount)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring(options =>
        {
            options.ThreadCount = threadCount;
            options.MaxConnections = 32;
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));

        await using var app = builder.Build();
        app.MapGet("/", () => "dynamic port");
        await app.StartAsync();

        string address = app.Urls.Single();
        var uri = new Uri(address);
        uri.Port.Should().BeGreaterThan(0);
        using var client = new HttpClient();
        (await client.GetStringAsync(uri)).Should().Be("dynamic port");

        await app.StopAsync();
    }

    [Fact]
    public async Task MinimumRingSize_BindsAndStopsWithoutHanging()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring(options =>
        {
            options.RingSize = 2;
            options.BufferRingSize = 2;
            options.MaxConnections = 1;
            options.AcceptQueueCapacity = 1;
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));

        await using var app = builder.Build();
        app.MapGet("/", () => "minimum ring");
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
