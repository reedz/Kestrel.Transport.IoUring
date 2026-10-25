using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Tests.Unit;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// The process-wide active-connection gauge must not drift when the IO loop dies with live connections; later
/// zero-assertions would otherwise fail forever. The dying loop drains its ring, which closes those connections
/// through the normal release path.
/// </summary>
[Collection(ProcessWideResourcesCollection.Name)]
public class ActiveConnectionGaugeTests
{
    [Fact]
    public async Task TheLoopDyingWithLiveConnections_RestoresTheProcessWideGauge()
    {
        if (!Ring.IsSupported) return;

        long before = Volatile.Read(ref IoUringConnectionListener.s_activeConnections);
        using var fail = new ManualResetEventSlim(false);
        var listener = ListenerLoopFailureTests.CreateListener(() =>
        {
            if (fail.IsSet)
                throw new InvalidOperationException("injected io loop fault");
        });

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            var connection = await listener.AcceptAsync();
            connection.Should().NotBeNull();
            Volatile.Read(ref IoUringConnectionListener.s_activeConnections).Should().Be(before + 1);

            fail.Set();
            await client.GetStream().WriteAsync(new byte[] { 1 }); // wakes the parked loop
            await listener.IoLoopStopped.WaitAsync(TimeSpan.FromSeconds(20));

            listener.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.Quiesced);
            Volatile.Read(ref IoUringConnectionListener.s_activeConnections)
                .Should().Be(before, "the dying loop drains its ring, which releases the connection");
        }
        finally
        {
            await listener.DisposeAsync();
        }

        Volatile.Read(ref IoUringConnectionListener.s_activeConnections).Should().Be(before);
    }
}
