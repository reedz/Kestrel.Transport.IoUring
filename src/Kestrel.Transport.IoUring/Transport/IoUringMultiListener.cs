using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kestrel.Transport.IoUring.Native;
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
    private int _disposed;
    private Exception? _fatalError;

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

        // Compute setup flags once for all workers.
        uint setupFlags = 0;
        if (options.EnableSqPoll)
            setupFlags |= IoUringConstants.IORING_SETUP_SQPOLL;
        if (options.EnableCoopTaskRun)
            setupFlags |= IoUringConstants.IORING_SETUP_COOP_TASKRUN;
        if (options.EnableSingleIssuer)
            setupFlags |= IoUringConstants.IORING_SETUP_SINGLE_ISSUER;
        if (options.EnableDeferTaskRun && options.EnableSingleIssuer)
            setupFlags |= IoUringConstants.IORING_SETUP_DEFER_TASKRUN;

        _workers = new IoUringConnectionListener[threadCount];
        _forwardTasks = new Task[threadCount];

        var logger = loggerFactory.CreateLogger<IoUringConnectionListener>();

        int createdWorkers = 0;
        try
        {
            for (int i = 0; i < threadCount; i++)
            {
                var perWorkerOptions = CreateWorkerOptions(options, i);
                Ring? ring = null;
                IoUringConnectionListener? worker = null;
                try
                {
                    ring = new Ring((uint)perWorkerOptions.EffectiveRingSize, setupFlags);
                    var workerEndPoint = i == 0 ? endPoint : _workers[0].EndPoint;
                    worker = new IoUringConnectionListener(workerEndPoint, ring, perWorkerOptions, logger);
                    worker.Bind(options.ListenBacklog, reusePort: true);
                    _workers[i] = worker;

                    // Forward accepted connections from each worker to the merged channel.
                    int workerIndex = i;
                    _forwardTasks[i] = ForwardAcceptsAsync(worker, workerIndex);
                    createdWorkers++;
                }
                catch
                {
                    if (worker != null)
                        worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    else
                        ring?.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            _cts.Cancel();
            _mergedChannel.Writer.TryComplete();
            for (int i = 0; i < createdWorkers; i++)
                _workers[i].DisposeAsync().AsTask().GetAwaiter().GetResult();
            _cts.Dispose();
            throw;
        }

        EndPoint = _workers[0].EndPoint;
    }

    internal static IoUringTransportOptions CreateWorkerOptions(
        IoUringTransportOptions options,
        int workerIndex)
    {
        int baseConnections = options.MaxConnections / options.ThreadCount;
        int remainder = options.MaxConnections % options.ThreadCount;

        return new IoUringTransportOptions
        {
            RingSize = options.RingSize,
            MaxConnections = baseConnections + (workerIndex < remainder ? 1 : 0),
            ListenBacklog = options.ListenBacklog,
            AcceptQueueCapacity = options.AcceptQueueCapacity,
            LogPoolStatsInterval = options.LogPoolStatsInterval,
            ReceiveBufferSize = options.ReceiveBufferSize,
            MaxPendingReceiveBytesPerRing = options.MaxPendingReceiveBytesPerRing,
            ThreadCount = 1,
            EnableSqPoll = options.EnableSqPoll,
            EnableCoopTaskRun = options.EnableCoopTaskRun,
            EnableSingleIssuer = options.EnableSingleIssuer,
            EnableDeferTaskRun = options.EnableDeferTaskRun,
            EnableBufferRing = options.EnableBufferRing,
            BufferRingSize = options.BufferRingSize,
            UnsafeInlineScheduling = options.UnsafeInlineScheduling,
        };
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
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _fatalError, ex, null);
            _mergedChannel.Writer.TryComplete(ex);
            _cts.Cancel();
        }
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
            if (Volatile.Read(ref _fatalError) is { } fatalError)
            {
                throw new IOException(
                    "An io_uring worker stopped because its IO loop failed.",
                    fatalError);
            }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await UnbindAsync().ConfigureAwait(false);

        foreach (var worker in _workers)
            await worker.DisposeAsync().ConfigureAwait(false);

        _cts.Dispose();
    }

    internal void FailWorkerForTest(int workerIndex, Exception error) =>
        _workers[workerIndex].FailListenerForTest(error);
}
