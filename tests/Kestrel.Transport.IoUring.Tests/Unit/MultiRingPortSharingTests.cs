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
/// By default (EnableReusePort) each ring of a multi-ring listener binds its own SO_REUSEPORT socket, which any
/// process of the same user can join at any time. With EnableReusePort=false the rings accept from one exclusively
/// bound socket, so no other process can join the port; each ring keeps one single-shot accept queued on it, which
/// spreads connections over the rings.
/// </summary>
[Collection(Integration.ProcessWideResourcesCollection.Name)] // WorkerCreatedForTests is process-wide
public class MultiRingPortSharingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Shared_RingsShareOneExclusiveSocket_SoNoOtherSocketCanJoinThePort()
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
    public async Task Shared_SpreadsConnectionsOverTheRings()
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
            // How evenly depends on scheduling (a ring whose thread runs less re-arms less often: 121/7 was seen on a
            // loaded 2-CPU machine). What must hold is that no ring is starved, as a multishot accept on the shared
            // socket starved the others (256/0): a queued accept reaches the head once a sibling requeues behind it.
            perRing.Should().OnlyContain(
                count => count > 0,
                $"every ring must get connections (got {string.Join("/", perRing)})");
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
    public async Task Shared_UnbindStopsListening_AndDisposeFreesThePort()
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
    public async Task Shared_ARingWhoseLoopDies_LeavesTheSharedSocketToItsSibling()
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
    public async Task Shared_DisposeWithAWedgedRing_DoesNotHang_AndStopsListening()
    {
        if (!Ring.IsSupported) return;

        var listener = await BindAsync(reusePort: false);
        listener.ShutdownStepTimeout = TimeSpan.FromMilliseconds(200);
        var endPoint = (IPEndPoint)listener.EndPoint;
        var wedgedRing = Workers(listener)[0];
        wedgedRing.ShutdownStepTimeout = TimeSpan.FromMilliseconds(200);
        wedgedRing.TeardownDrainTimeout = TimeSpan.FromMilliseconds(200);
        using var wedge = new ManualResetEventSlim(false);
        using var wedged = new ManualResetEventSlim(false);
        wedgedRing.IoLoopIterationHook = () =>
        {
            wedged.Set();
            wedge.Wait();
        };
        try
        {
            wedgedRing.WakeIoLoopForTests();
            wedged.Wait(Limit).Should().BeTrue();

            // Closing the shared socket would wait for the wedged ring's handle reference forever.
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);

            IoUringQuarantine.Contains(wedgedRing).Should().BeTrue();
            wedgedRing.HoldsListenSocketReference.Should().BeTrue();
            using var late = new TcpClient();
            Func<Task> connect = () => late.ConnectAsync(endPoint);
            (await connect.Should().ThrowAsync<SocketException>())
                .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused);
        }
        finally
        {
            wedge.Set();
        }
    }

    [Fact]
    public async Task Shared_WhenEveryRingDies_ThePortStopsListening()
    {
        if (!Ring.IsSupported) return;

        var listener = await BindAsync(reusePort: false);
        var endPoint = (IPEndPoint)listener.EndPoint;
        try
        {
            foreach (var ring in Workers(listener))
            {
                ring.IoLoopIterationHook = static () => throw new InvalidOperationException("injected loop failure");
                ring.WakeIoLoopForTests();
            }
            foreach (var ring in Workers(listener))
                await ring.IoLoopStopped.WaitAsync(Limit);
            (await listener.AcceptAsync().AsTask().WaitAsync(Limit)).Should().BeNull();

            using var late = new TcpClient();
            Func<Task> connect = () => late.ConnectAsync(endPoint);
            (await connect.Should().ThrowAsync<SocketException>())
                .Which.SocketErrorCode.Should().Be(
                    SocketError.ConnectionRefused,
                    "no ring accepts any more, so clients must not queue up on the socket");
        }
        finally
        {
            try { await listener.DisposeAsync().AsTask().WaitAsync(Limit); }
            catch (Exception ex) when (ex is not TimeoutException) { }
        }
    }

    [Fact]
    public async Task Shared_BuildRollbackWithAWedgedRingWhoseBindFailed_DoesNotHang_AndStopsListening()
    {
        if (!Ring.IsSupported) return;

        using var wedge = new ManualResetEventSlim(false);
        using var wedged = new ManualResetEventSlim(false);
        IoUringConnectionListener? failedRing = null;
        IPEndPoint? endPoint = null;
        IoUringMultiListener.WorkerCreatedForTests = ring =>
        {
            if (Interlocked.CompareExchange(ref failedRing, ring, null) != null)
                return;
            ring.ShutdownStepTimeout = TimeSpan.FromMilliseconds(200);
            ring.TeardownDrainTimeout = TimeSpan.FromMilliseconds(200);
            int wedging = 0;
            ring.IoLoopIterationHook = () =>
            {
                if (Volatile.Read(ref wedging) == 1)
                {
                    wedged.Set();
                    wedge.Wait();
                }
            };
            // Bind fails after the ring took its reference to the shared socket, with the loop wedged, so its
            // disposal quarantines it with that reference still held.
            ring.LoopActivatedInBindForTests = () =>
            {
                endPoint = (IPEndPoint)ring.EndPoint;
                Volatile.Write(ref wedging, 1);
                ring.WakeIoLoopForTests();
                wedged.Wait(Limit).Should().BeTrue();
                throw new InvalidOperationException("injected bind failure");
            };
        };
        try
        {
            // Off the test thread: the rollback used to spin forever closing a socket the failed ring referenced.
            Func<Task> bind = () => Task.Run(() => BindAsync(reusePort: false)).WaitAsync(Limit);
            (await bind.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<TimeoutException>();

            failedRing!.HoldsListenSocketReference.Should().BeTrue();
            IoUringQuarantine.Contains(failedRing).Should().BeTrue();
            using var late = new TcpClient();
            Func<Task> connect = () => late.ConnectAsync(endPoint!);
            (await connect.Should().ThrowAsync<SocketException>())
                .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused, "a failed bind leaves nothing listening");
        }
        finally
        {
            IoUringMultiListener.WorkerCreatedForTests = null;
            wedge.Set();
        }
    }

    [Fact]
    public async Task ReusePortDefault_GivesEachRingItsOwnSocket_WhichOtherSocketsOfTheUserCanJoin()
    {
        if (!Ring.IsSupported) return;

        new IoUringTransportOptions().EnableReusePort.Should().BeTrue();
        var listener = await BindAsync(reusePort: null);
        try
        {
            foreach (var worker in Workers(listener))
            {
                worker.OwnsListenSocket.Should().BeTrue();
                worker.UsesMultishotAcceptForTests.Should().BeTrue();
            }

            using var joiner = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            joiner.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            joiner.Bind(listener.EndPoint); // what the option documents: the group admits same-user sockets
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    private static async Task<IoUringMultiListener> BindAsync(bool? reusePort, int maxConnections = 64)
    {
        var options = new IoUringTransportOptions
        {
            RequireIoUring = true,
            ThreadCount = 2,
            MaxConnections = maxConnections,
        };
        if (reusePort is { } value)
            options.EnableReusePort = value;
        var factory = new IoUringTransportFactory(
            Options.Create(TestTransport.Apply(options)),
            Options.Create(new SocketTransportOptions()),
            NullLoggerFactory.Instance);
        return (IoUringMultiListener)await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    private static IoUringConnectionListener[] Workers(IoUringMultiListener listener) =>
        listener.WorkersForTests.Cast<IoUringConnectionListener>().ToArray();
}
