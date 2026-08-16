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

    public EndPoint EndPoint { get; private set; }

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
                var workerOptions = CreateWorkerOptions(
                    options,
                    GetWorkerMaxConnections(options.MaxConnections, threadCount, i));
                Ring? ring = null;
                IoUringConnectionListener? worker = null;
                try
                {
                    ring = new Ring((uint)workerOptions.EffectiveRingSize, setupFlags);
                    EndPoint workerEndPoint = i == 0 ? endPoint : EndPoint;
                    worker = new IoUringConnectionListener(
                        workerEndPoint,
                        ring,
                        workerOptions,
                        logger);
                    worker.Bind(options.ListenBacklog, reusePort: true);
                    if (i == 0)
                        EndPoint = worker.EndPoint;
                    ring = null; // listener owns the ring
                    _workers[i] = worker;
                    _forwardTasks[i] = ForwardAcceptsAsync(worker);
                    createdWorkers++;
                }
                finally
                {
                    if (ring != null && worker != null)
                        worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    else
                        ring?.Dispose();
                }
            }
        }
        catch
        {
            _cts.Cancel();
            _mergedChannel.Writer.TryComplete();
            for (int i = createdWorkers - 1; i >= 0; i--)
                _workers[i].DisposeAsync().AsTask().GetAwaiter().GetResult();
            _cts.Dispose();
            throw;
        }
    }

    internal static IoUringTransportOptions CreateWorkerOptions(
        IoUringTransportOptions options,
        int maxConnections) =>
        new()
        {
            RingSize = options.RingSize,
            MaxConnections = maxConnections,
            ListenBacklog = options.ListenBacklog,
            AcceptQueueCapacity = options.AcceptQueueCapacity,
            LogPoolStatsInterval = options.LogPoolStatsInterval,
            ReceiveBufferSize = options.ReceiveBufferSize,
            ThreadCount = 1,
            EnableSqPoll = options.EnableSqPoll,
            EnableBufferRing = options.EnableBufferRing,
            EnableMultishotAccept = options.EnableMultishotAccept,
            EnableRegisteredFiles = options.EnableRegisteredFiles,
            BufferRingSize = options.BufferRingSize,
            EnableCoopTaskRun = options.EnableCoopTaskRun,
            EnableSingleIssuer = options.EnableSingleIssuer,
            EnableDeferTaskRun = options.EnableDeferTaskRun,
            UnsafeInlineScheduling = options.UnsafeInlineScheduling,
        };

    internal static int GetWorkerMaxConnections(int total, int workers, int workerIndex) =>
        total / workers + (workerIndex < total % workers ? 1 : 0);

    private async Task ForwardAcceptsAsync(IoUringConnectionListener worker)
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
