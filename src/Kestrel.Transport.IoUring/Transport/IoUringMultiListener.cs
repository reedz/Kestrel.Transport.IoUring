using System.Net;
using System.Net.Sockets;
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
/// and IO loop thread. By default every worker accepts from one exclusively bound socket this listener owns; with
/// <see cref="IoUringTransportOptions.EnableReusePort"/> each worker binds its own SO_REUSEPORT socket and the kernel
/// distributes connections across them.
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

    /// <summary>How long disposal waits for the workers to finish unbinding before it disposes them anyway.</summary>
    internal TimeSpan ShutdownStepTimeout { get; set; } = TimeSpan.FromSeconds(5);
    private int _acceptsClosedToApp;
    private int _mergedWriterCompleted;
    // Forwarders still running. When the last one ends on its own (every worker stopped accepting, e.g. their IO
    // loops failed) no connection can arrive any more, so the merged channel completes and AcceptAsync returns.
    private int _activeForwarders;

    private readonly record struct WorkerBuild(WorkerSet WorkerSet, int MergedCapacity);
    private readonly record struct WorkerSet(EndPoint EndPoint, IMultiListenerWorker[] Workers, Socket? SharedListenSocket);

    // The listening socket every ring accepts from (null with EnableReusePort, where each ring owns its own).
    private readonly Socket? _sharedListenSocket;

    internal IReadOnlyList<IMultiListenerWorker> WorkersForTests => _workers;

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
        : this(workerSet.EndPoint, mergedCapacity, workerSet.Workers, workerSet.SharedListenSocket)
    {
    }

    internal IoUringMultiListener(
        EndPoint endPoint,
        int mergedCapacity,
        params IMultiListenerWorker[] workers)
        : this(endPoint, mergedCapacity, workers, sharedListenSocket: null)
    {
    }

    private IoUringMultiListener(
        EndPoint endPoint,
        int mergedCapacity,
        IMultiListenerWorker[] workers,
        Socket? sharedListenSocket)
    {
        EndPoint = workers.Length == 0 ? endPoint : workers[0].EndPoint;
        _workers = workers;
        // Before the forwarders start: the last one to end may have to stop the socket listening.
        _sharedListenSocket = sharedListenSocket;
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
        int connectionShare)
    {
        // A full copy, so every behavioural setting (including those resolved from SocketTransportOptions) reaches
        // the workers. MaxConnections stays the full limit: admission is shared (ConnectionAdmission), so any
        // worker may hold up to all of it; only the buffers are sized for the worker's share.
        var worker = options.Clone();
        worker.BufferRingConnectionShare = connectionShare;
        worker.ThreadCount = 1;
        return worker;
    }

    /// <summary>The even share of <paramref name="total"/> connections one of <paramref name="workers"/> rings expects.</summary>
    internal static int GetWorkerConnectionShare(int total, int workers, int workerIndex) =>
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
        var admission = new ConnectionAdmission(options.MaxConnections);
        bool reusePort = options.EnableReusePort;
        Socket? sharedSocket = null;
        if (reusePort)
        {
            EnsurePortIsFree(endPoint);
        }
        else
        {
            // One exclusively bound socket for every ring: a second instance fails to bind, and no other process can
            // join the port later (as it could an SO_REUSEPORT group).
            sharedSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                IoUringConnectionListener.BindAndListen(sharedSocket, endPoint, options.ListenBacklog);
                publishedEndPoint = sharedSocket.LocalEndPoint ?? endPoint;
            }
            catch
            {
                sharedSocket.Dispose();
                throw;
            }
        }

        try
        {
            for (int i = 0; i < threadCount; i++)
            {
                var workerOptions = CreateWorkerOptions(
                    options,
                    GetWorkerConnectionShare(options.MaxConnections, threadCount, i));
                // Accept requests wait on a shared socket exclusively: the kernel hands each new connection to the
                // first one queued. A multishot accept stays first and would take every connection (measured:
                // 256/0 over two rings); a single-shot accept requeues behind its siblings once it completes.
                if (!reusePort)
                    workerOptions.EnableMultishotAccept = false;
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
                        logger,
                        admission,
                        sharedSocket);
                    worker.Bind(options.ListenBacklog, reusePort);
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
            ReleaseSharedListenSocket(sharedSocket, workers.Take(createdWorkers));
            throw;
        }

        return new WorkerSet(publishedEndPoint, workers, sharedSocket);
    }

    /// <summary>
    /// With <see cref="IoUringTransportOptions.EnableReusePort"/> the rings share the port through SO_REUSEPORT,
    /// which would also let another process (a second instance of the application) bind it without error and
    /// silently split the traffic. Binding an exclusive socket first fails with Kestrel's
    /// <see cref="AddressInUseException"/>, as SocketTransport does. It does not reserve the port: Linux lets any
    /// process with the same effective user ID join the SO_REUSEPORT group later (see docs/KNOWN-ISSUES.md). An
    /// ephemeral port (0) needs no probe because nobody else can be bound to the one the kernel picks.
    /// </summary>
    internal static void EnsurePortIsFree(EndPoint endPoint)
    {
        if (endPoint is not IPEndPoint { Port: not 0 } ipEndPoint)
            return;

        using var probe = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (ipEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
            probe.DualMode = true;
        try
        {
            probe.Bind(ipEndPoint);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new AddressInUseException(ex.Message, ex);
        }
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
            {
                CompleteMergedWriter();
                // Every ring stopped accepting on its own (their loops failed): nobody will accept from the shared
                // socket any more, so new clients must be refused rather than queue up. (Unbind stops it itself,
                // once the rings have cancelled their accepts.)
                if (Volatile.Read(ref _acceptsClosedToApp) == 0)
                    StopSharedListening(_sharedListenSocket);
            }
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

        // Every ring has stopped accepting from the shared socket: stop listening, so new clients are refused.
        StopSharedListening(_sharedListenSocket);

        ThrowCapturedFailures(failures);
    }

    private static void StopSharedListening(Socket? socket)
    {
        try { socket?.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Stops the shared socket listening and closes it, unless a ring still holds its reference to the handle: a
    /// ring whose loop is wedged is quarantined with the socket (it may still use the descriptor), and closing a
    /// socket whose handle is still referenced waits for that reference (SafeSocketHandle.CloseAsIs spins until
    /// it is released), which would hang disposal. Such a socket stays reachable through the quarantined ring.
    /// </summary>
    private static void ReleaseSharedListenSocket(Socket? socket, IEnumerable<IMultiListenerWorker> workers)
    {
        if (socket is null)
            return;

        StopSharedListening(socket);
        if (!workers.Any(static worker => worker is IoUringConnectionListener { HoldsListenSocketReference: true }))
            socket.Dispose();
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;

        // Bounded like the single-ring listener's unbind: a worker whose IO loop is wedged never finishes unbinding,
        // and its own DisposeAsync (bounded, and quarantining such a ring) must still run.
        Task unbindTask = EnsureUnbindTask();
        try
        {
            await unbindTask.WaitAsync(ShutdownStepTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = unbindTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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

        // Also when an unbind never finished (a wedged ring): the port must stop listening.
        ReleaseSharedListenSocket(_sharedListenSocket, _workers);

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
