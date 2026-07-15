using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
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
    private readonly TaskCompletionSource _blockingRequestStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseBlockingRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        int port = GetRandomPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));

        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapGet("/", () => "Hello from io_uring Kestrel!");
        _app.MapGet("/remote-ip", (HttpContext ctx) =>
            ctx.Connection.RemoteIpAddress?.ToString() ?? "missing");
        _app.MapGet("/blocking", () =>
        {
            _blockingRequestStarted.TrySetResult();
            _releaseBlockingRequest.Task.GetAwaiter().GetResult();
            return "unblocked";
        });
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
        _app.Map("/ws", async context =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            byte[] buffer = new byte[1024];
            WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, context.RequestAborted);
            await socket.SendAsync(
                buffer.AsMemory(0, received.Count),
                received.MessageType,
                endOfMessage: true,
                context.RequestAborted);
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "done",
                context.RequestAborted);
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
    public async Task ConnectionCloseResponse_Completes()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/");
        request.Headers.ConnectionClose = true;

        using HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Hello from io_uring Kestrel!");
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
    public async Task Post_ChunkedBody_EchoesCompletePayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('C', 32_000));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/echo")
        {
            Content = new UnknownLengthContent(payload),
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(payload);
    }

    [Fact]
    public async Task Post_ExpectContinue_EchoesCompletePayload()
    {
        string payload = new('E', 32_000);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/echo")
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/plain"),
        };
        request.Headers.ExpectContinue = true;

        using HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(payload);
    }

    [Fact]
    public async Task RemoteIpAddress_IsPopulated()
    {
        string remoteIp = await _client.GetStringAsync($"{_baseUrl}/remote-ip");

        remoteIp.Should().Be("127.0.0.1");
    }

    [Fact]
    public async Task BlockingRequest_DoesNotStarveAnotherConnectionByDefault()
    {
        using var blockingClient = new HttpClient();
        Task<HttpResponseMessage> blockingRequest =
            blockingClient.GetAsync($"{_baseUrl}/blocking");
        await _blockingRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using var fastClient = new HttpClient();
            HttpResponseMessage fastResponse = await fastClient
                .GetAsync($"{_baseUrl}/")
                .WaitAsync(TimeSpan.FromSeconds(5));

            fastResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            _releaseBlockingRequest.TrySetResult();
        }

        (await blockingRequest).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GracefulHostShutdown_WaitsForActiveRequest()
    {
        using var client = new HttpClient();
        Task<HttpResponseMessage> request = client.GetAsync($"{_baseUrl}/blocking");
        await _blockingRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task stop = _app.StopAsync();
        _releaseBlockingRequest.TrySetResult();

        using HttpResponseMessage response = await request.WaitAsync(TimeSpan.FromSeconds(5));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WebSocket_BidirectionalMessageAndCloseComplete()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_baseUrl.Replace("http://", "ws://") + "/ws"), default);

        byte[] payload = Encoding.UTF8.GetBytes("websocket-payload");
        await socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            default);

        byte[] response = new byte[1024];
        WebSocketReceiveResult received = await socket.ReceiveAsync(response, default);

        received.MessageType.Should().Be(WebSocketMessageType.Text);
        Encoding.UTF8.GetString(response, 0, received.Count).Should().Be("websocket-payload");

        WebSocketReceiveResult close = await socket.ReceiveAsync(response, default);
        close.MessageType.Should().Be(WebSocketMessageType.Close);
    }

    [Fact]
    public async Task ClientResetDuringLargeResponse_DoesNotStallListener()
    {
        using (var socket = new Socket(SocketType.Stream, ProtocolType.Tcp))
        {
            socket.LingerState = new LingerOption(enable: true, seconds: 0);
            await socket.ConnectAsync(IPAddress.Loopback, new Uri(_baseUrl).Port);
            await SendAll(
                socket,
                "GET /large/1024 HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray());
        }

        for (int i = 0; i < 10; i++)
        {
            using HttpResponseMessage response = await _client
                .GetAsync($"{_baseUrl}/")
                .WaitAsync(TimeSpan.FromSeconds(5));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Http11PipelinedRequests_ReturnBothResponses()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, new Uri(_baseUrl).Port);
        await SendAll(
            socket,
            Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n" +
                "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));

        using var responseBytes = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (read == 0)
                break;
            responseBytes.Write(buffer, 0, read);
        }

        string responses = Encoding.ASCII.GetString(responseBytes.ToArray());
        responses.Split("Hello from io_uring Kestrel!", StringSplitOptions.None)
            .Length.Should().Be(3);
    }

    private static async Task SendAll(Socket socket, byte[] data)
    {
        int sent = 0;
        while (sent < data.Length)
            sent += await socket.SendAsync(data.AsMemory(sent), SocketFlags.None);
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(payload.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    internal static int GetRandomPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
