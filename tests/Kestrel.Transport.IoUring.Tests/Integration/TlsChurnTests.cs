using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Integration;

public class TlsChurnTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private X509Certificate2 _certificate = null!;
    private RSA _certificateKey = null!;
    private string _baseUrl = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        (_certificate, _certificateKey) = CreateCertificate();
        _port = KestrelIntegrationTests.GetRandomPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseIoUring(options =>
        {
            options.MaxConnections = 256;
            options.RingSize = 256;
        });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, _port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(_certificate);
            }));

        _app = builder.Build();
        _app.MapGet("/", () => "tls-ok");
        _app.MapGet("/large/{sizeKb}", (int sizeKb) =>
            Results.Bytes(new byte[sizeKb * 1024], "application/octet-stream"));
        await _app.StartAsync();
        _baseUrl = $"https://127.0.0.1:{_port}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _certificate.Dispose();
        _certificateKey.Dispose();
    }

    [Fact]
    public async Task RepeatedConnectionCloseTlsHandshakes_DoNotStallIoLoop()
    {
        for (int i = 0; i < 64; i++)
            await SendSingleConnectionRequest();
    }

    [Fact]
    public async Task ConcurrentTlsHandshakeChurn_CompletesWithoutErrors()
    {
        Task[] requests = Enumerable.Range(0, 32)
            .Select(_ => SendSingleConnectionRequest())
            .ToArray();

        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task TlsKeepAliveResponse_Completes()
    {
        await SendSingleConnectionRequest(connectionClose: false);
    }

    [Fact]
    public async Task Tls12Response_Completes()
    {
        await SendSingleConnectionRequest(
            connectionClose: false,
            sslProtocols: SslProtocols.Tls12);
    }

    [Fact]
    public async Task Tls13Response_Completes()
    {
        await SendSingleConnectionRequest(
            connectionClose: false,
            sslProtocols: SslProtocols.Tls13);
    }

    [Fact]
    public async Task Http2MultiplexedRequests_CompleteOnOneConnection()
    {
        using var handler = CreateHandler();
        handler.EnableMultipleHttp2Connections = false;
        handler.MaxConnectionsPerServer = 1;
        using var client = new HttpClient(handler);

        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 32)
            .Select(_ =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/")
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                return client.SendAsync(request);
            })
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(requests)
            .WaitAsync(TimeSpan.FromSeconds(15));

        foreach (HttpResponseMessage response in responses)
        {
            using (response)
            {
                response.Version.Should().Be(HttpVersion.Version20);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                (await response.Content.ReadAsStringAsync()).Should().Be("tls-ok");
            }
        }
    }

    [Fact]
    public async Task Http2StreamReset_DoesNotPoisonTheConnection()
    {
        using var handler = CreateHandler();
        handler.EnableMultipleHttp2Connections = false;
        handler.MaxConnectionsPerServer = 1;
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/large/4096")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using (HttpResponseMessage large = await client.SendAsync(
                   request,
                   HttpCompletionOption.ResponseHeadersRead))
        {
            byte[] prefix = new byte[1024];
            int read = await large.Content.ReadAsStream().ReadAsync(prefix);
            read.Should().BeGreaterThan(0);
        }

        using var followUp = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage response = await client
            .SendAsync(followUp)
            .WaitAsync(TimeSpan.FromSeconds(5));

        response.Version.Should().Be(HttpVersion.Version20);
        (await response.Content.ReadAsStringAsync()).Should().Be("tls-ok");
    }

    [Fact]
    public async Task InvalidTlsHandshakes_DoNotPoisonTheListener()
    {
        for (int i = 0; i < 16; i++)
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                LingerState = new LingerOption(enable: true, seconds: 0),
            };
            await socket.ConnectAsync(IPAddress.Loopback, _port);
            await socket.SendAsync("not-a-tls-client"u8.ToArray(), SocketFlags.None);
        }

        await SendSingleConnectionRequest(connectionClose: false);
    }

    private async Task SendSingleConnectionRequest(
        bool connectionClose = true,
        SslProtocols sslProtocols = SslProtocols.None)
    {
        using var handler = CreateHandler();
        handler.SslOptions.EnabledSslProtocols = sslProtocols;
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.ConnectionClose = connectionClose;

        using HttpResponseMessage response = await client
            .SendAsync(request)
            .WaitAsync(TimeSpan.FromSeconds(5));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("tls-ok");
    }

    private static SocketsHttpHandler CreateHandler() =>
        new()
        {
            MaxConnectionsPerServer = 1,
            SslOptions =
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
        };

    private static (X509Certificate2 Certificate, RSA Key) CreateCertificate()
    {
        RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return (certificate, rsa);
    }
}
