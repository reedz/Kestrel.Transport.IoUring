using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// When the IO loop dies (fatal ring error, repeated loop faults) nothing will ever service lifecycle commands
/// again. UnbindAsync and DisposeAsync must still complete instead of waiting for an accept shutdown that can no
/// longer happen, and a fatal error raised after unbind began must still abort the connections.
/// </summary>
public class ListenerLoopFailureTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    internal static IoUringConnectionListener CreateListener(Action? loopHook = null)
    {
        var options = new IoUringTransportOptions { ThreadCount = 1 };
        var listener = new IoUringConnectionListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new Ring((uint)options.EffectiveRingSize),
            options,
            NullLogger<IoUringConnectionListener>.Instance)
        {
            IoLoopIterationHook = loopHook,
        };
        listener.Bind(options.ListenBacklog);
        return listener;
    }

    private static async Task WithinLimit(Task task, string what)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(Limit));
        winner.Should().BeSameAs(task, what);
        await task;
    }

    [Fact]
    public async Task UnbindAndDispose_AfterTheIoLoopDied_Complete()
    {
        if (!Ring.IsSupported) return;

        using var fail = new ManualResetEventSlim(false);
        var listener = CreateListener(() =>
        {
            if (fail.IsSet)
                throw new InvalidOperationException("injected io loop fault");
        });

        try
        {
            fail.Set();
            await WakeLoopAsync((IPEndPoint)listener.EndPoint);
            await listener.IoLoopStopped.WaitAsync(Limit);

            await WithinLimit(listener.UnbindAsync().AsTask(), "UnbindAsync must not wait for a dead IO loop");
        }
        finally
        {
            await WithinLimit(listener.DisposeAsync().AsTask(), "DisposeAsync must not wait for a dead IO loop");
        }
    }

    // A connection attempt wakes the parked IO loop so it runs the armed fault; six consecutive faults make it give
    // up and exit. The loop can fail and shut the listener down before the attempt completes, so a refusal is fine.
    private static async Task WakeLoopAsync(IPEndPoint endpoint)
    {
        try
        {
            using var waker = new TcpClient();
            await waker.ConnectAsync(endpoint);
        }
        catch (SocketException)
        {
        }
    }

    [Fact]
    public async Task AfterTheIoLoopDied_TheListenPortStopsAccepting()
    {
        if (!Ring.IsSupported) return;

        using var fail = new ManualResetEventSlim(false);
        var listener = CreateListener(() =>
        {
            if (fail.IsSet)
                throw new InvalidOperationException("injected io loop fault");
        });

        try
        {
            var endpoint = (IPEndPoint)listener.EndPoint;
            fail.Set();
            await WakeLoopAsync(endpoint);
            await listener.IoLoopStopped.WaitAsync(Limit);

            await WithinLimit(listener.UnbindAsync().AsTask(), "UnbindAsync after the loop died");

            // The kernel would otherwise keep completing connects into the listen backlog of a socket nobody serves.
            using var late = new TcpClient();
            Func<Task> connect = async () => await late.ConnectAsync(endpoint);
            await connect.Should().ThrowAsync<SocketException>("a dead listener must not keep accepting connections");
        }
        finally
        {
            await WithinLimit(listener.DisposeAsync().AsTask(), "DisposeAsync after loop death");
        }
    }

    [Fact]
    public async Task FatalErrorAfterUnbind_StillAbortsAppOwnedConnections()
    {
        if (!Ring.IsSupported) return;

        using var fail = new ManualResetEventSlim(false);
        var listener = CreateListener(() =>
        {
            if (fail.IsSet)
                throw new InvalidOperationException("injected io loop fault");
        });

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.EndPoint);
            var connection = await listener.AcceptAsync();
            connection.Should().NotBeNull();

            await WithinLimit(listener.UnbindAsync().AsTask(), "UnbindAsync with a live loop");

            fail.Set();
            await client.GetStream().WriteAsync(new byte[] { 1 }); // wakes the parked IO loop

            // Unbind alone leaves the connection open, but a fatal loop error afterwards must abort it.
            var closed = new TaskCompletionSource();
            using var registration = connection!.ConnectionClosed.Register(() => closed.TrySetResult());
            await WithinLimit(closed.Task, "a fatal error after unbind must abort the connection");
        }
        finally
        {
            await WithinLimit(listener.DisposeAsync().AsTask(), "DisposeAsync after a fatal error");
        }
    }
}
