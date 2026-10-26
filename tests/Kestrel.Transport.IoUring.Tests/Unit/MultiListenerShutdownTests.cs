using System.Net;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// Disposing a multi-ring listener must reach every worker's own (bounded, quarantining) disposal even when a worker
/// never finishes unbinding, for example because its IO loop is wedged; it used to wait for the unbind forever.
/// </summary>
public class MultiListenerShutdownTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Dispose_WhenAWorkerNeverFinishesUnbinding_StillDisposesEveryWorker()
    {
        var healthy = new Worker(unbindCompletes: true);
        var wedged = new Worker(unbindCompletes: false);
        var listener = new IoUringMultiListener(new IPEndPoint(IPAddress.Loopback, 0), 4, healthy, wedged)
        {
            ShutdownStepTimeout = TimeSpan.FromMilliseconds(200),
        };

        await listener.DisposeAsync().AsTask().WaitAsync(Limit);

        healthy.Disposed.Should().BeTrue();
        wedged.Disposed.Should().BeTrue("its own disposal is what bounds and quarantines a wedged ring");
        (await listener.AcceptAsync().AsTask().WaitAsync(Limit)).Should().BeNull();
    }

    private sealed class Worker(bool unbindCompletes) : IMultiListenerWorker
    {
        private readonly TaskCompletionSource _unbound = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Threading.Channels.Channel<ConnectionContext> _accepts =
            System.Threading.Channels.Channel.CreateUnbounded<ConnectionContext>();

        public bool Disposed { get; private set; }

        public EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);

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
            // Like IoUringConnectionListener: accepting stops at once, the unbind itself waits for the IO loop.
            _accepts.Writer.TryComplete();
            if (unbindCompletes)
                _unbound.TrySetResult();
            return new ValueTask(_unbound.Task.WaitAsync(cancellationToken));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _unbound.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
