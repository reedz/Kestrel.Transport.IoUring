using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;
using Xunit.Abstractions;
using static Kestrel.Transport.IoUring.Tests.Integration.TestHost;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// Large responses reach the transport as many pipe segments. They must be delivered byte-for-byte, in order,
/// including when the peer reads slowly so the kernel accepts only part of each send.
/// </summary>
public class LargeResponseTests(ITestOutputHelper output)
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Theory]
    [InlineData(118_047)]       // the PTS test.html size
    [InlineData(1_000_003)]     // odd size, spans hundreds of pipe segments
    [InlineData(8 * 1024 * 1024)]
    public async Task Response_IsDeliveredByteForByte(int length)
    {
        byte[] payload = RandomBytes(length, seed: length);
        await using var app = BuildApp(null, a => a.MapGet("/big", () => Results.Bytes(payload)));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        byte[] received = await client.GetByteArrayAsync("/big");

        received.Length.Should().Be(length);
        SHA256.HashData(received).Should().Equal(SHA256.HashData(payload));
    }

    [Fact]
    public async Task ManyConcurrentLargeResponses_AreEachDeliveredIntact()
    {
        byte[] payload = RandomBytes(118_047, seed: 7);
        byte[] expectedHash = SHA256.HashData(payload);
        await using var app = BuildApp(o => o.RingSize = 64, a => a.MapGet("/big", () => Results.Bytes(payload)));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        const int clients = 64;
        byte[][] results = await Task.WhenAll(
            Enumerable.Range(0, clients).Select(_ => client.GetByteArrayAsync("/big")));

        results.Should().OnlyContain(r => SHA256.HashData(r).SequenceEqual(expectedHash));
    }

    [Fact]
    public async Task SlowReader_WithTinyReceiveBuffer_ReceivesEveryByteInOrder()
    {
        // A small receive window makes the kernel accept only part of each send, exercising the
        // partial-send resubmission path with a multi-segment response.
        byte[] payload = RandomBytes(4 * 1024 * 1024, seed: 99);
        await using var app = BuildApp(null, a => a.MapGet("/big", () => Results.Bytes(payload)));
        await app.StartAsync();

        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = 4096,
        };
        await socket.ConnectAsync(EndpointOf(app));
        await socket.SendAsync(
            Encoding.ASCII.GetBytes("GET /big HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"), SocketFlags.None);

        using var received = new MemoryStream();
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int reads = 0;
        while (true)
        {
            int n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            if (n == 0)
                break;
            received.Write(buffer, 0, n);
            if (++reads % 64 == 0)
                await Task.Delay(1, cts.Token);
        }

        byte[] all = received.ToArray();
        int headerEnd = IndexOf(all, "\r\n\r\n"u8) + 4;
        headerEnd.Should().BeGreaterThan(4);
        byte[] body = all[headerEnd..];
        output.WriteLine($"received {body.Length} body bytes in {reads} reads");
        body.Length.Should().Be(payload.Length);
        SHA256.HashData(body).Should().Equal(SHA256.HashData(payload));
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) =>
        data.AsSpan().IndexOf(pattern);
}
