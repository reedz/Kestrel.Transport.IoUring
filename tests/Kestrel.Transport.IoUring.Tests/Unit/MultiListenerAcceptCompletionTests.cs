using System.Net;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// NEW-09: when every worker stops accepting on its own (for example their IO loops failed), no connection can
/// arrive any more, so the merged accept channel must complete and AcceptAsync must return instead of waiting
/// forever. One worker stopping must not end accepts while another is still accepting.
/// </summary>
public class MultiListenerAcceptCompletionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AcceptAsync_ReturnsNull_OnceEveryWorkerStoppedAccepting()
    {
        var first = new StoppableWorker();
        var second = new StoppableWorker();
        var listener = new IoUringMultiListener(new IPEndPoint(IPAddress.Loopback, 0), 4, first, second);
        try
        {
            Task<ConnectionContext?> accept = listener.AcceptAsync().AsTask();

            first.StopAccepting();
            await Task.Delay(100);
            accept.IsCompleted.Should().BeFalse("the second worker can still deliver connections");

            second.StopAccepting();
            (await accept.WaitAsync(Limit)).Should().BeNull();
            (await listener.AcceptAsync().AsTask().WaitAsync(Limit)).Should().BeNull();
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task AcceptAsync_StillDeliversConnectionsQueuedBeforeTheLastWorkerStopped()
    {
        var worker = new StoppableWorker();
        var listener = new IoUringMultiListener(new IPEndPoint(IPAddress.Loopback, 0), 4, worker);
        var connection = new DefaultConnectionContext("queued");
        try
        {
            worker.Deliver(connection);
            worker.StopAccepting();

            (await listener.AcceptAsync().AsTask().WaitAsync(Limit)).Should().BeSameAs(connection);
            (await listener.AcceptAsync().AsTask().WaitAsync(Limit)).Should().BeNull();
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    private sealed class StoppableWorker : IMultiListenerWorker
    {
        private readonly System.Threading.Channels.Channel<ConnectionContext> _accepts =
            System.Threading.Channels.Channel.CreateUnbounded<ConnectionContext>();

        public EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);

        public void Deliver(ConnectionContext connection) => _accepts.Writer.TryWrite(connection);

        public void StopAccepting() => _accepts.Writer.TryComplete();

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _accepts.Reader.ReadAsync(cancellationToken);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            StopAccepting();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
