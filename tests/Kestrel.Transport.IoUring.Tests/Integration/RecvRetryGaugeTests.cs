using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Diagnostics;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// NEW-13: the receive-retry gauge is the live number of entries summed over every listener: it rises while
/// connections wait for buffers and returns to its baseline when the listener goes away (previously it kept whatever
/// depth was last reported).
/// </summary>
[Collection(ProcessWideResourcesCollection.Name)]
public class RecvRetryGaugeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Gauge_RisesUnderBufferExhaustion_AndReturnsToBaseline()
    {
        if (!Ring.IsSupported) return;

        const int connections = 300;
        using var hold = new ManualResetEventSlim(true);
        var options = TestTransport.Apply(new IoUringTransportOptions
        {
            ThreadCount = 1,
            BufferRingSize = 8,
            MaxConnections = connections + 16,
        });
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new Ring((uint)options.EffectiveRingSize),
            options,
            NullLogger<IoUringConnectionListener>.Instance)
        {
            IoLoopIterationHook = () => hold.Wait(),
        };
        listener.Bind(options.ListenBacklog);
        // Only the provided-buffer ring can run out of buffers (multishot receive: Linux 6.0+, not the single-shot lane).
        if (!listener.UsesBufferRing)
        {
            await listener.DisposeAsync();
            return;
        }

        long baseline = RecvDiagnostics.RecvRetryDepth;
        long activeBefore = Volatile.Read(ref IoUringConnectionListener.s_activeConnections);
        RecvDiagnostics.ResetRecvRetryHighWaterForTests();
        var clients = new List<TcpClient>(connections);
        try
        {
            for (int i = 0; i < connections; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            }
            var deadline = DateTime.UtcNow + Limit;
            while (Volatile.Read(ref IoUringConnectionListener.s_activeConnections) - activeBefore < connections &&
                   DateTime.UtcNow < deadline)
                await Task.Delay(20);

            // With the loop parked, every receive armed on a readable socket completes into the eight buffers before
            // any of them can be recycled: the rest end with ENOBUFS and wait in the retry set.
            hold.Reset();
            await Task.Delay(50);
            foreach (var client in clients)
                await client.GetStream().WriteAsync("x"u8.ToArray());
            await Task.Delay(300);
            hold.Set();

            while (RecvDiagnostics.RecvRetryDepthHighWater <= baseline && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            RecvDiagnostics.RecvRetryDepthHighWater.Should().BeGreaterThan(baseline);
        }
        finally
        {
            hold.Set();
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
            foreach (var client in clients)
                client.Dispose();
        }

        RecvDiagnostics.RecvRetryDepth.Should().Be(baseline, "no entry outlives its listener");
    }
}
