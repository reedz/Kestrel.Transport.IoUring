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
/// H-06: MaxConnections is one limit shared by every ring of a listener (with SO_REUSEPORT a per-ring split refused
/// connections on one ring while another had room). H-03: a full accept channel pauses accepting instead of
/// aborting connections the kernel already accepted. Port sharing: a multi-ring listener refuses a port someone
/// already holds, like SocketTransport.
/// </summary>
public class AcceptAdmissionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void Admission_ReservesUpToTheLimit_AndReleasesExactly()
    {
        var admission = new ConnectionAdmission(2);

        admission.TryReserve().Should().BeTrue();
        admission.TryReserve().Should().BeTrue();
        admission.TryReserve().Should().BeFalse();
        admission.Release();
        admission.TryReserve().Should().BeTrue();
        admission.Release(2);
        admission.Count.Should().Be(0);
    }

    [Fact]
    public async Task SharedAdmission_LetsOneRingTakeTheWholeLimit_AndCapsTheTotal()
    {
        if (!Ring.IsSupported) return;

        // Two rings standing in for the workers of one multi-ring listener; their own tables hold the whole limit.
        var admission = new ConnectionAdmission(2);
        var first = CreateListener(maxConnections: 2, admission);
        var second = CreateListener(maxConnections: 2, admission);
        var clients = new List<TcpClient>();
        try
        {
            var a = await AcceptFromAsync(first, clients);
            var b = await AcceptFromAsync(first, clients);
            admission.Count.Should().Be(2, "one ring may hold every connection the listener allows");

            clients.Add(await ConnectAsync(second));
            var refused = second.AcceptAsync().AsTask();
            (await Task.WhenAny(refused, Task.Delay(300))).Should().NotBeSameAs(refused,
                "the shared limit is reached, whichever ring the connection lands on");

            await a.DisposeAsync().AsTask().WaitAsync(Limit);
            admission.Count.Should().Be(1, "disposal releases the reservation exactly once");

            clients.Add(await ConnectAsync(second));
            var accepted = await refused.WaitAsync(Limit);
            accepted.Should().NotBeNull();
            await accepted!.DisposeAsync().AsTask().WaitAsync(Limit);
            await b.DisposeAsync().AsTask().WaitAsync(Limit);
        }
        finally
        {
            await first.DisposeAsync().AsTask().WaitAsync(Limit);
            await second.DisposeAsync().AsTask().WaitAsync(Limit);
            foreach (var client in clients)
                client.Dispose();
        }

        admission.Count.Should().Be(0);
    }

    [Fact]
    public async Task FullAcceptChannel_PausesAccepting_InsteadOfAbortingAcceptedConnections()
    {
        if (!Ring.IsSupported) return;

        var options = TestTransport.Apply(new IoUringTransportOptions { ThreadCount = 1, AcceptQueueCapacity = 1 });
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new Ring((uint)options.EffectiveRingSize),
            options,
            NullLogger<IoUringConnectionListener>.Instance);
        listener.Bind(options.ListenBacklog);
        long pausesBefore = Interlocked.Read(ref IoUringConnectionListener.s_acceptPauses);
        var clients = new List<TcpClient>();
        try
        {
            // Nobody calls AcceptAsync while they connect: the channel (capacity 1) fills immediately.
            for (int i = 0; i < 6; i++)
                clients.Add(await ConnectAsync(listener));
            await Task.Delay(200);
            Interlocked.Read(ref IoUringConnectionListener.s_acceptPauses).Should().BeGreaterThan(pausesBefore);

            for (int i = 0; i < 6; i++)
                await ExchangeAsync(listener, clients[i]);

            // Accepting resumed once the backlog was delivered.
            clients.Add(await ConnectAsync(listener));
            await ExchangeAsync(listener, clients[^1]);
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
            foreach (var client in clients)
                client.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiRingListener_RefusesAPortAlreadyBound(bool reusePort)
    {
        if (!Ring.IsSupported) return;

        var holder = await CreateFactory(threadCount: 2, reusePort).BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)holder.EndPoint).Port;
        try
        {
            Func<Task> secondMultiRing = async () =>
                await CreateFactory(threadCount: 2, reusePort).BindAsync(new IPEndPoint(IPAddress.Loopback, port));
            await secondMultiRing.Should().ThrowAsync<AddressInUseException>(
                "a second instance must not silently split the traffic");

            Func<Task> singleRing = async () =>
                await CreateFactory(threadCount: 1).BindAsync(new IPEndPoint(IPAddress.Loopback, port));
            await singleRing.Should().ThrowAsync<AddressInUseException>();
        }
        finally
        {
            await holder.DisposeAsync();
        }

        // Free again once the holder is gone.
        var rebound = await CreateFactory(threadCount: 2, reusePort).BindAsync(new IPEndPoint(IPAddress.Loopback, port));
        await rebound.DisposeAsync();
    }

    private static IoUringConnectionListener CreateListener(int maxConnections, ConnectionAdmission admission)
    {
        var options = TestTransport.Apply(new IoUringTransportOptions { ThreadCount = 1, MaxConnections = maxConnections });
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new Ring((uint)options.EffectiveRingSize),
            options,
            NullLogger<IoUringConnectionListener>.Instance,
            admission);
        listener.Bind(options.ListenBacklog);
        return listener;
    }

    private static IoUringTransportFactory CreateFactory(int threadCount, bool reusePort = false) =>
        new(
            Options.Create(TestTransport.Apply(new IoUringTransportOptions
            {
                RequireIoUring = true,
                ThreadCount = threadCount,
                MaxConnections = 16,
                EnableReusePort = reusePort,
            })),
            Options.Create(new SocketTransportOptions()),
            NullLoggerFactory.Instance);

    private static async Task<TcpClient> ConnectAsync(IConnectionListener listener)
    {
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.EndPoint);
        return client;
    }

    private static async Task<ConnectionContext> AcceptFromAsync(IoUringConnectionListener listener, List<TcpClient> clients)
    {
        clients.Add(await ConnectAsync(listener));
        var connection = await listener.AcceptAsync().AsTask().WaitAsync(Limit);
        connection.Should().NotBeNull();
        return connection!;
    }

    private static async Task ExchangeAsync(IoUringConnectionListener listener, TcpClient client)
    {
        var connection = await listener.AcceptAsync().AsTask().WaitAsync(Limit);
        connection.Should().NotBeNull("an accepted connection must be delivered, not aborted");
        await client.GetStream().WriteAsync("x"u8.ToArray());
        var read = await connection!.Transport.Input.ReadAsync().AsTask().WaitAsync(Limit);
        read.Buffer.Length.Should().BeGreaterThan(0);
        connection.Transport.Input.AdvanceTo(read.Buffer.End);
        await connection.DisposeAsync().AsTask().WaitAsync(Limit);
    }
}
