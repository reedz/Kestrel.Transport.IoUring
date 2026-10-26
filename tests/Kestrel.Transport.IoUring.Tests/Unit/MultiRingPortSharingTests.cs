using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// By default the rings of a multi-ring listener accept from one exclusively bound socket, so no other process can
/// join the port (an SO_REUSEPORT group admits any process of the same user at any time). Each ring keeps one
/// single-shot accept queued on it, which spreads connections over the rings; EnableReusePort restores per-ring
/// SO_REUSEPORT sockets.
/// </summary>
public class MultiRingPortSharingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Default_RingsShareOneExclusiveSocket_SoNoOtherSocketCanJoinThePort()
    {
        if (!Ring.IsSupported) return;

        var listener = await BindAsync(reusePort: false);
        try
        {
            foreach (var worker in Workers(listener))
            {
                worker.OwnsListenSocket.Should().BeFalse();
                worker.UsesMultishotAcceptForTests.Should().BeFalse(
                    "a multishot accept stays first in the socket's exclusive wait queue and takes every connection");
            }

            using var intruder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // On Linux .NET sets SO_REUSEPORT together with SO_REUSEADDR.
            intruder.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            Action join = () => intruder.Bind(listener.EndPoint);
            join.Should().Throw<SocketException>()
                .Which.SocketErrorCode.Should().Be(SocketError.AddressAlreadyInUse);
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Default_SpreadsConnectionsOverTheRings()
    {
        if (!Ring.IsSupported) return;

        const int total = 128;
        var listener = await BindAsync(reusePort: false, maxConnections: 512);
        var clients = new List<TcpClient>();
        var accepted = new List<ConnectionContext>();
        try
        {
            var acceptAll = Task.Run(async () =>
            {
                for (int i = 0; i < total; i++)
                    accepted.Add((await listener.AcceptAsync())!);
            });
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                for (int i = 0; i < total / 8; i++)
                {
                    var client = new TcpClient();
                    lock (clients)
                        clients.Add(client);
                    await client.ConnectAsync((IPEndPoint)listener.EndPoint);
                }
            }));
            await acceptAll.WaitAsync(Limit);

            int[] perRing = Workers(listener).Select(static w => w.ActiveConnectionCountForTests).ToArray();
            perRing.Sum().Should().Be(total);
            perRing.Should().OnlyContain(
                count => count >= total / 8,
                $"connections must be spread over the rings (got {string.Join("/", perRing)})");
        }
        finally
        {
            foreach (var connection in accepted)
                await connection.DisposeAsync().AsTask().WaitAsync(Limit);
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
            foreach (var client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task Default_UnbindStopsListening_AndDisposeFreesThePort()
    {
        if (!Ring.IsSupported) return;

        var listener = await BindAsync(reusePort: false);
        var endPoint = (IPEndPoint)listener.EndPoint;

        await listener.UnbindAsync().AsTask().WaitAsync(Limit);
        using (var late = new TcpClient())
        {
            Func<Task> connect = () => late.ConnectAsync(endPoint);
            (await connect.Should().ThrowAsync<SocketException>())
                .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused);
        }

        await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        using var rebind = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        rebind.Bind(endPoint);
    }

    [Fact]
    public async Task Default_ARingWhoseLoopDies_LeavesTheSharedSocketToItsSibling()
    {
        if (!Ring.IsSupported) return;

        var listener = await BindAsync(reusePort: false);
        var clients = new List<TcpClient>();
        try
        {
            var dying = Workers(listener)[0];
            var survivor = Workers(listener)[1];
            dying.IoLoopIterationHook = static () => throw new InvalidOperationException("injected loop failure");
            dying.WakeIoLoopForTests();
            await dying.IoLoopStopped.WaitAsync(Limit);

            for (int i = 0; i < 8; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync((IPEndPoint)listener.EndPoint).WaitAsync(Limit);
                var connection = await listener.AcceptAsync().AsTask().WaitAsync(Limit);
                connection.Should().NotBeNull("the surviving ring still accepts from the shared socket");
                await connection!.DisposeAsync().AsTask().WaitAsync(Limit);
            }
            survivor.Teardown.Should().Be(IoUringConnectionListener.TeardownOutcome.NotRun);
        }
        finally
        {
            // The dead ring's failure is reported by unbind and disposal.
            try { await listener.DisposeAsync().AsTask().WaitAsync(Limit); }
            catch (Exception ex) when (ex is not TimeoutException) { }
            foreach (var client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task ReusePortOptIn_GivesEachRingItsOwnSocket_WhichOtherSocketsOfTheUserCanJoin()
    {
        if (!Ring.IsSupported) return;

        var listener = await BindAsync(reusePort: true);
        try
        {
            foreach (var worker in Workers(listener))
                worker.OwnsListenSocket.Should().BeTrue();

            using var joiner = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            joiner.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            joiner.Bind(listener.EndPoint); // what the option documents: the group admits same-user sockets
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    private static async Task<IoUringMultiListener> BindAsync(bool reusePort, int maxConnections = 64)
    {
        var factory = new IoUringTransportFactory(
            Options.Create(TestTransport.Apply(new IoUringTransportOptions
            {
                RequireIoUring = true,
                ThreadCount = 2,
                MaxConnections = maxConnections,
                EnableReusePort = reusePort,
            })),
            Options.Create(new SocketTransportOptions()),
            NullLoggerFactory.Instance);
        return (IoUringMultiListener)await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    private static IoUringConnectionListener[] Workers(IoUringMultiListener listener) =>
        listener.WorkersForTests.Cast<IoUringConnectionListener>().ToArray();
}
