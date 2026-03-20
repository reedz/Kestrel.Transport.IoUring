using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>
/// Multiplexed connection listener that distributes connections across multiple
/// <see cref="IoUringConnectionListener"/> workers, each with its own io_uring ring
/// and IO loop thread. Uses SO_REUSEPORT so the kernel distributes incoming
/// connections across listen sockets.
/// </summary>
internal sealed class IoUringMultiListener : IConnectionListener
{
    private readonly IoUringConnectionListener[] _workers;
    private readonly Channel<ConnectionContext> _mergedChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _forwardTasks;

    public EndPoint EndPoint { get; }

    public IoUringMultiListener(
        EndPoint endPoint,
        IoUringTransportOptions options,
        ILoggerFactory loggerFactory)
    {
        EndPoint = endPoint;
        int threadCount = options.ThreadCount;

        _mergedChannel = Channel.CreateBounded<ConnectionContext>(
            new BoundedChannelOptions(options.AcceptQueueCapacity * threadCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        // Per-worker ring size: divide MaxConnections across workers.
        var perWorkerOptions = new IoUringTransportOptions
        {
            RingSize = options.RingSize,
            MaxConnections = Math.Max(1, options.MaxConnections / threadCount),
            ListenBacklog = options.ListenBacklog,
            AcceptQueueCapacity = options.AcceptQueueCapacity,
            ReceiveBufferSize = options.ReceiveBufferSize,
            ThreadCount = 1, // each worker is single-threaded
        };

        _workers = new IoUringConnectionListener[threadCount];
        _forwardTasks = new Task[threadCount];

        var logger = loggerFactory.CreateLogger<IoUringConnectionListener>();

        for (int i = 0; i < threadCount; i++)
        {
            var ring = new Ring((uint)perWorkerOptions.EffectiveRingSize);
            var worker = new IoUringConnectionListener(endPoint, ring, perWorkerOptions, logger);
            worker.Bind(options.ListenBacklog, reusePort: true);
            _workers[i] = worker;

            // Forward accepted connections from each worker to the merged channel.
            int workerIndex = i;
            _forwardTasks[i] = ForwardAcceptsAsync(worker, workerIndex);
        }
    }

    private async Task ForwardAcceptsAsync(IoUringConnectionListener worker, int workerIndex)
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var conn = await worker.AcceptAsync(token).ConfigureAwait(false);
                if (conn == null) break;
                await _mergedChannel.Writer.WriteAsync(conn, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _mergedChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _cts.Cancel();
        _mergedChannel.Writer.TryComplete();

        foreach (var worker in _workers)
            await worker.UnbindAsync(cancellationToken).ConfigureAwait(false);

        await Task.WhenAll(_forwardTasks).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await UnbindAsync().ConfigureAwait(false);

        foreach (var worker in _workers)
            await worker.DisposeAsync().ConfigureAwait(false);

        _cts.Dispose();
    }
}
