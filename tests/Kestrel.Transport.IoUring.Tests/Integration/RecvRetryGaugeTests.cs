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
        using var parked = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(true);
        int parkRequested = 0;
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
            IoLoopIterationHook = () =>
            {
                if (Volatile.Read(ref parkRequested) == 1)
                {
                    parked.Set();
                    release.Wait();
                }
            },
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

            // Park the loop at the top of an iteration (explicit wake, then wait for the hook to acknowledge) so no
            // completion is processed and no buffer recycled while every client sends. Whenever the kernel posts
            // those receives (task work while parked, or the loop's next enter), only eight find a buffer; the rest
            // end with ENOBUFS and wait in the retry set once the loop runs again.
            release.Reset();
            Volatile.Write(ref parkRequested, 1);
            listener.WakeIoLoopForTests();
            parked.Wait(Limit).Should().BeTrue("the IO loop parks at the top of its next iteration");
            foreach (var client in clients)
                await client.GetStream().WriteAsync("x"u8.ToArray());
            // Loopback delivery is immediate; this only lets softirq processing catch up. More than eight readable
            // sockets in the first batch is enough to exhaust the ring.
            await Task.Delay(300);
            Volatile.Write(ref parkRequested, 0);
            release.Set();

            while (RecvDiagnostics.RecvRetryDepthHighWater <= baseline && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            RecvDiagnostics.RecvRetryDepthHighWater.Should().BeGreaterThan(baseline);
        }
        finally
        {
            Volatile.Write(ref parkRequested, 0);
            release.Set();
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
            foreach (var client in clients)
                client.Dispose();
        }

        RecvDiagnostics.RecvRetryDepth.Should().Be(baseline, "no entry outlives its listener");
    }
}
