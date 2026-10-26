using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Xunit;
using Xunit.Abstractions;
using static Kestrel.Transport.IoUring.Tests.Integration.TestHost;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// With multishot receive and a provided buffer ring, a burst of readable connections can consume buffers
/// faster than the IO loop recycles them. Exhaustion must delay a connection, never strand it.
/// </summary>
public class BufferExhaustionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(300, 8)]
    public async Task ManyConcurrentConnections_TinyBufferRing_AllRequestsComplete(int connections, int bufferRingSize)
    {
        await using var app = BuildApp(
            o =>
            {
                o.BufferRingSize = bufferRingSize;
                o.RingSize = 64;
                o.MaxConnections = connections + 16;
            },
            a => a.MapGet("/", () => "Hello"));
        await app.StartAsync();
        IPEndPoint endpoint = EndpointOf(app);

        var sockets = new List<Socket>(connections);
        for (int i = 0; i < connections; i++)
            sockets.Add(await ConnectAsync(endpoint));
        byte[] request = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        // Send from all connections at once so completions pile up faster than buffers are recycled.
        await Task.WhenAll(sockets.Select(s => s.SendAsync(request, SocketFlags.None)));
        string[] responses = await Task.WhenAll(
            sockets.Select(s => ReadToEndAsync(s, TimeSpan.FromSeconds(15))));
        watch.Stop();
        output.WriteLine($"{connections} connections, bufferRing={bufferRingSize}: {watch.ElapsedMilliseconds} ms");

        responses.Should().OnlyContain(r => r.StartsWith("HTTP/1.1 200"));
        foreach (Socket s in sockets)
            s.Dispose();
    }
}
