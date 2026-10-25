using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Kestrel.Transport.IoUring.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Xunit;
using static Kestrel.Transport.IoUring.Tests.Integration.TestHost;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// NEW-13: the receive-retry gauge is the live number of entries summed over every listener: it rises while
/// connections wait for buffers and returns to its baseline when they are served or the listener goes away
/// (previously it kept whatever depth was last reported).
/// </summary>
[Collection(ProcessWideResourcesCollection.Name)]
public class RecvRetryGaugeTests
{
    [Fact]
    public async Task Gauge_RisesUnderBufferExhaustion_AndReturnsToBaseline()
    {
        if (!Ring.IsSupported || TestTransport.SingleShotLane) return;

        long baseline = RecvDiagnostics.RecvRetryDepth;
        RecvDiagnostics.ResetRecvRetryHighWaterForTests();

        const int connections = 300;
        await using (var app = BuildApp(
            o =>
            {
                o.BufferRingSize = 8;
                o.RingSize = 64;
                o.MaxConnections = connections + 16;
            },
            a => a.MapGet("/", () => "Hello")))
        {
            await app.StartAsync();
            IPEndPoint endpoint = EndpointOf(app);
            var sockets = new List<Socket>(connections);
            for (int i = 0; i < connections; i++)
                sockets.Add(await ConnectAsync(endpoint));
            byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

            await Task.WhenAll(sockets.Select(s => s.SendAsync(request, SocketFlags.None)));
            await Task.WhenAll(sockets.Select(s => ReadToEndAsync(s, TimeSpan.FromSeconds(15))));
            foreach (Socket s in sockets)
                s.Dispose();
        }

        RecvDiagnostics.RecvRetryDepthHighWater.Should().BeGreaterThan(baseline,
            "eight provided buffers for 300 simultaneous requests force receives into the retry set");
        RecvDiagnostics.RecvRetryDepth.Should().Be(baseline, "no entry outlives its listener");
    }
}
