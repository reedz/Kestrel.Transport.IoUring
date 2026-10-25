using System.Net;
using System.Runtime.ExceptionServices;
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
internal sealed class IoUringMultiListener : IConnectionListener, IIoUringListenerInfo
{
    private readonly IMultiListenerWorker[] _workers;
    private readonly Channel<ConnectionContext> _mergedChannel;
    private readonly Task[] _forwardTasks;
    private readonly object _lifecycleLock = new();
    // Serializes app AcceptAsync reads with the internal shutdown drain so the
    // listener never races itself while reclaiming unread merged accepts.
    private readonly SemaphoreSlim _acceptReadLock = new(1, 1);
    private Task? _unbindTask;
    private Task? _disposeTask;
    private int _acceptsClosedToApp;
    private int _mergedWriterCompleted;
    // Forwarders still running. When the last one ends on its own (every worker stopped accepting, e.g. their IO
    // loops failed) no connection can arrive any more, so the merged channel completes and AcceptAsync returns.
    private int _activeForwarders;

    private readonly record struct WorkerBuild(WorkerSet WorkerSet, int MergedCapacity);
    private readonly record struct WorkerSet(EndPoint EndPoint, IMultiListenerWorker[] Workers);

    public EndPoint EndPoint { get; private set; }

    int IIoUringListenerInfo.RingCount => _workers.Sum(
        static worker => worker is IIoUringListenerInfo info ? info.RingCount : 1);

    uint IIoUringListenerInfo.SetupFlags =>
        _workers.Length > 0 && _workers[0] is IIoUringListenerInfo info ? info.SetupFlags : 0;

    bool IIoUringListenerInfo.UsesProvidedBufferRing =>
        _workers.Length > 0 &&
        _workers.All(static worker => worker is IIoUringListenerInfo { UsesProvidedBufferRing: true });

    public IoUringMultiListener(
        EndPoint endPoint,
        IoUringTransportOptions options,
        ILoggerFactory loggerFactory)
        : this(CreateWorkerBuild(endPoint, options, loggerFactory))
    {
    }

    private IoUringMultiListener(WorkerBuild workerBuild)
        : this(workerBuild.WorkerSet, workerBuild.MergedCapacity)
    {
    }

    private IoUringMultiListener(WorkerSet workerSet, int mergedCapacity)
        : this(workerSet.EndPoint, mergedCapacity, workerSet.Workers)
    {
    }

    internal IoUringMultiListener(
        EndPoint endPoint,
        int mergedCapacity,
        params IMultiListenerWorker[] workers)
    {
        EndPoint = workers.Length == 0 ? endPoint : workers[0].EndPoint;
        _workers = workers;
        _mergedChannel = CreateMergedChannel(mergedCapacity);
        _forwardTasks = new Task[workers.Length];
        _activeForwarders = workers.Length;
        if (workers.Length == 0)
            CompleteMergedWriter();

        for (int i = 0; i < workers.Length; i++)
            _forwardTasks[i] = ForwardAcceptsAsync(workers[i]);
    }

    internal static IoUringTransportOptions CreateWorkerOptions(
        IoUringTransportOptions options,
        int maxConnections)
    {
        // A full copy, so every behavioural setting (including those resolved from SocketTransportOptions) reaches
        // the workers.
        var worker = options.Clone();
        worker.MaxConnections = maxConnections;
        worker.ThreadCount = 1;
        return worker;
    }

    internal static int GetWorkerMaxConnections(int total, int workers, int workerIndex) =>
        total / workers + (workerIndex < total % workers ? 1 : 0);

    private static Channel<ConnectionContext> CreateMergedChannel(int mergedCapacity) =>
        Channel.CreateBounded<ConnectionContext>(
            new BoundedChannelOptions(mergedCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

    private static uint GetSetupFlags(IoUringTransportOptions options) => options.SetupFlags;

    private static WorkerBuild CreateWorkerBuild(
        EndPoint endPoint,
        IoUringTransportOptions options,
        ILoggerFactory loggerFactory)
    {
        int threadCount = options.ThreadCount;
        return new WorkerBuild(
            CreateWorkerSet(endPoint, options, loggerFactory, threadCount),
            options.AcceptQueueCapacity * threadCount);
    }

    private static WorkerSet CreateWorkerSet(
        EndPoint endPoint,
        IoUringTransportOptions options,
        ILoggerFactory loggerFactory,
        int threadCount)
    {
        var workers = new IMultiListenerWorker[threadCount];
        var logger = loggerFactory.CreateLogger<IoUringConnectionListener>();
        EndPoint publishedEndPoint = endPoint;
        int createdWorkers = 0;
        uint setupFlags = GetSetupFlags(options);

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
                    EndPoint workerEndPoint = i == 0 ? endPoint : publishedEndPoint;
                    worker = new IoUringConnectionListener(
                        workerEndPoint,
                        ring,
                        workerOptions,
                        logger);
                    worker.Bind(options.ListenBacklog, reusePort: true);
                    if (i == 0)
                        publishedEndPoint = worker.EndPoint;
                    ring = null; // listener owns the ring
                    workers[i] = worker;
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
            for (int i = createdWorkers - 1; i >= 0; i--)
                workers[i].DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        return new WorkerSet(publishedEndPoint, workers);
    }

    private async Task ForwardAcceptsAsync(IMultiListenerWorker worker)
    {
        List<Exception>? failures = null;
        try
        {
            while (true)
            {
                ConnectionContext? connection;
                try
                {
                    connection = await worker.AcceptAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    CaptureFailure(ref failures, ex);
                    break;
                }

                if (connection is null)
                    break;

                try
                {
                    await ForwardOrDisposeConnectionAsync(connection).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CaptureFailure(ref failures, ex);
                }
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeForwarders) == 0)
                CompleteMergedWriter();
            ThrowCapturedFailures(failures);
        }
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _acceptsClosedToApp) != 0)
            return null;

        try
        {
            await _acceptReadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (Volatile.Read(ref _acceptsClosedToApp) != 0)
                return null;

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
        finally
        {
            _acceptReadLock.Release();
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        Task unbindTask = EnsureUnbindTask();
        try
        {
            await unbindTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Best effort, like SocketTransport: Kestrel passes an already-cancelled token once its shutdown
            // timeout expires and must still dispose the workers. The shared unbind keeps running. If it
            // finished in the meantime, report its real outcome (a worker failure) rather than the cancellation.
            if (unbindTask.IsCompleted)
                await unbindTask.ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() =>
        new(EnsureDisposeTask());

    private Task EnsureUnbindTask()
    {
        Task? cachedTask = Volatile.Read(ref _unbindTask);
        if (cachedTask is not null)
            return cachedTask;

        lock (_lifecycleLock)
            return _unbindTask ??= UnbindCoreAsync();
    }

    private Task EnsureDisposeTask()
    {
        Task? cachedTask = Volatile.Read(ref _disposeTask);
        if (cachedTask is not null)
            return cachedTask;

        lock (_lifecycleLock)
            return _disposeTask ??= DisposeCoreAsync();
    }

    private async Task UnbindCoreAsync()
    {
        Interlocked.Exchange(ref _acceptsClosedToApp, 1);
        Task[] workerUnbindTasks = StartWorkerOperations(static worker => worker.UnbindAsync(), _workers);
        // Make merged delivery unavailable before we await the forwarders so any
        // blocked WriteAsync hands its in-transit ownership back to the forwarder
        // for explicit abort/dispose, then the forwarder can keep draining.
        CompleteMergedWriter();

        List<Exception>? failures = null;
        Task allTask = Task.WhenAll(_forwardTasks);

        try
        {
            await allTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, allTask.Exception ?? ex);
        }

        try
        {
            await DrainMergedConnectionsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, ex);
        }

        allTask = Task.WhenAll(workerUnbindTasks);
        try
        {
            await allTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, allTask.Exception ?? ex);
        }

        ThrowCapturedFailures(failures);
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;

        try
        {
            await EnsureUnbindTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, ex);
        }

        Task[] workerDisposeTasks = StartWorkerOperations(static worker => worker.DisposeAsync(), _workers);
        Task allTask = Task.WhenAll(workerDisposeTasks);

        try
        {
            await allTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, allTask.Exception ?? ex);
        }

        CompleteMergedWriter();

        try
        {
            await DrainMergedConnectionsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, ex);
        }

        ThrowCapturedFailures(failures);
    }

    private async Task ForwardOrDisposeConnectionAsync(ConnectionContext connection)
    {
        try
        {
            await _mergedChannel.Writer.WriteAsync(connection).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            await AbortAndDisposeConnectionAsync(
                connection,
                new ConnectionAbortedException("Transport is shutting down."))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            List<Exception>? failures = null;
            CaptureFailure(ref failures, ex);
            try
            {
                await AbortAndDisposeConnectionAsync(
                    connection,
                    new ConnectionAbortedException("Merged accept delivery failed.", ex))
                    .ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                CaptureFailure(ref failures, cleanupEx);
            }

            ThrowCapturedFailures(failures);
        }
    }

    private async Task DrainMergedConnectionsAsync()
    {
        List<Exception>? failures = null;
        await _acceptReadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            while (_mergedChannel.Reader.TryRead(out var connection))
            {
                try
                {
                    await AbortAndDisposeConnectionAsync(
                        connection,
                        new ConnectionAbortedException("Transport is shutting down."))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CaptureFailure(ref failures, ex);
                }
            }
        }
        finally
        {
            _acceptReadLock.Release();
        }

        ThrowCapturedFailures(failures);
    }

    private void CompleteMergedWriter()
    {
        if (Interlocked.Exchange(ref _mergedWriterCompleted, 1) == 0)
            _mergedChannel.Writer.TryComplete();
    }

    private static Task[] StartWorkerOperations(
        Func<IMultiListenerWorker, ValueTask> operation,
        IMultiListenerWorker[] workers)
    {
        var tasks = new Task[workers.Length];
        for (int i = 0; i < workers.Length; i++)
            tasks[i] = StartWorkerOperation(operation, workers[i]);

        return tasks;
    }

    private static Task StartWorkerOperation(
        Func<IMultiListenerWorker, ValueTask> operation,
        IMultiListenerWorker worker)
    {
        try
        {
            return operation(worker).AsTask();
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private static async Task AbortAndDisposeConnectionAsync(
        ConnectionContext connection,
        ConnectionAbortedException abortReason)
    {
        List<Exception>? failures = null;

        try
        {
            connection.Abort(abortReason);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, ex);
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, ex);
        }

        ThrowCapturedFailures(failures);
    }

    private static void CaptureFailure(ref List<Exception>? failures, Exception failure)
    {
        if (failure is AggregateException aggregateException)
        {
            foreach (var inner in aggregateException.Flatten().InnerExceptions)
                (failures ??= []).Add(inner);

            return;
        }

        (failures ??= []).Add(failure);
    }

    private static void ThrowCapturedFailures(List<Exception>? failures)
    {
        if (failures is null || failures.Count == 0)
            return;

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
            return;
        }

        throw new AggregateException(failures);
    }
}
