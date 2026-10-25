using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Tests.Unit;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Integration;

/// <summary>
/// The process-wide active-connection gauge must not drift when a listener is disposed with connections that
/// were never released through the normal close path; later zero-assertions would otherwise fail forever.
/// </summary>
[Collection(ProcessWideResourcesCollection.Name)]
public class ActiveConnectionGaugeTests
{
    [Fact]
    public async Task DisposingAfterTheLoopDiedWithLiveConnections_RestoresTheProcessWideGauge()
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

            // Kill the loop while the client is still connected: the connection can never be released normally.
            fail.Set();
            await client.GetStream().WriteAsync(new byte[] { 1 }); // wakes the parked loop
            await listener.IoLoopStopped.WaitAsync(TimeSpan.FromSeconds(20));

            Volatile.Read(ref IoUringConnectionListener.s_activeConnections)
                .Should().Be(before + 1, "a dead loop cannot release the connection, so dispose must");
        }
        finally
        {
            await listener.DisposeAsync();
        }

        Volatile.Read(ref IoUringConnectionListener.s_activeConnections).Should().Be(before);
    }
}
