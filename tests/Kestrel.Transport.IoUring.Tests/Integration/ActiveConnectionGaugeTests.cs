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
    public async Task DisposingWithLiveConnections_RestoresTheProcessWideGauge()
    {
        if (!Ring.IsSupported) return;

        long before = Volatile.Read(ref IoUringConnectionListener.s_activeConnections);
        var listener = ListenerLoopFailureTests.CreateListener();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            var connection = await listener.AcceptAsync();
            connection.Should().NotBeNull();

            Volatile.Read(ref IoUringConnectionListener.s_activeConnections).Should().Be(before + 1);
        }
        finally
        {
            await listener.DisposeAsync();
        }

        Volatile.Read(ref IoUringConnectionListener.s_activeConnections).Should().Be(before);
    }
}
