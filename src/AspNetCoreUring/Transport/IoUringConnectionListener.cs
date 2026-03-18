using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AspNetCoreUring.Native;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace AspNetCoreUring.Transport;

internal sealed class IoUringConnectionListener : IConnectionListener
{
    private readonly Ring _ring;
    private readonly ILogger _logger;
    private readonly Socket _listenSocket;
    private readonly Channel<ConnectionContext> _acceptChannel;
    private readonly ConcurrentDictionary<long, IoUringConnection> _connections = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _maxConnections;
    private readonly int _receiveBufferSize;

    // Sends are enqueued from background output-drain tasks and dequeued on the IO-loop thread.
    private readonly ConcurrentQueue<PendingSend> _pendingSendQueue = new();

    // Per-connection in-flight send tracking — only accessed from the IO loop thread.
    private readonly Dictionary<long, PendingSend> _inFlightSends = [];

    // Connections that need RECV resubmitted (after async pipe flush completes).
    private readonly ConcurrentQueue<long> _recvResubmitQueue = new();

    // Connections awaiting close after in-flight ops drain.
    private readonly Dictionary<long, IoUringConnection> _closingConnections = [];

    // Connections whose RECV failed due to SQ-full; retry on next IO loop iteration.
    private readonly HashSet<long> _recvRetrySet = [];

    // eventfd used to wake the IO loop immediately when a send is enqueued.
    private readonly int _eventFd;
    // Pinned read buffer for the eventfd READ SQE (8 bytes per Linux eventfd spec).
    private readonly ulong[] _eventFdReadBuf;
    private readonly MemoryHandle _eventFdReadHandle;
    // Pinned write buffer for waking the eventfd (value = 1).
    private readonly ulong[] _eventFdWriteBuf;
    private readonly MemoryHandle _eventFdWriteHandle;

    // setsockopt value buffer (pinned for P/Invoke).
    private readonly int[] _sockOptBuf;
    private readonly MemoryHandle _sockOptHandle;

    private long _nextConnectionId;
    private readonly TaskCompletionSource _ioLoopStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _listenSocketFd;
    private bool _listenSocketFdRefAdded;
    private uint _lastOverflowCount;

    public EndPoint EndPoint { get; }

    public IoUringConnectionListener(EndPoint endPoint, Ring ring, IoUringTransportOptions options, ILogger logger)
    {
        EndPoint = endPoint;
        _ring = ring;
        _logger = logger;
        _maxConnections = options.MaxConnections;
        _receiveBufferSize = options.ReceiveBufferSize;
        _listenSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _acceptChannel = Channel.CreateBounded<ConnectionContext>(new BoundedChannelOptions(options.AcceptQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        // Create an eventfd for waking the IO loop when a send is enqueued.
        // EFD_NONBLOCK = 0x800 prevents writes from blocking when the counter saturates.
        _eventFd = Libc.eventfd(0, 0x800 /* EFD_NONBLOCK */);
        if (_eventFd < 0)
            throw new InvalidOperationException($"eventfd failed: {Marshal.GetLastPInvokeError()}");

        _eventFdReadBuf = GC.AllocateUninitializedArray<ulong>(1, pinned: true);
        _eventFdReadHandle = _eventFdReadBuf.AsMemory().Pin();

        _eventFdWriteBuf = GC.AllocateUninitializedArray<ulong>(1, pinned: true);
        _eventFdWriteBuf[0] = 1UL;
        _eventFdWriteHandle = _eventFdWriteBuf.AsMemory().Pin();

        // Pinned buffer for setsockopt value (TCP_NODELAY = 1).
        _sockOptBuf = GC.AllocateUninitializedArray<int>(1, pinned: true);
        _sockOptBuf[0] = 1;
        _sockOptHandle = _sockOptBuf.AsMemory().Pin();
    }

    public void Bind(int listenBacklog)
    {
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Enable dual-stack for IPv6 sockets (accept both IPv4 and IPv6 connections),
        // matching the default Kestrel socket transport behavior.
        if (_listenSocket.AddressFamily == AddressFamily.InterNetworkV6)
            _listenSocket.DualMode = true;

        _listenSocket.Bind(EndPoint);
        _listenSocket.Listen(listenBacklog);

        // Safely acquire the socket fd with proper ref counting.
        bool refAdded = false;
        _listenSocket.SafeHandle.DangerousAddRef(ref refAdded);
        _listenSocketFdRefAdded = refAdded;
        _listenSocketFd = (int)_listenSocket.SafeHandle.DangerousGetHandle();

        SubmitAccept();
        SubmitEventFdRead();
        _ring.Submit();

        var ioThread = new Thread(RunIoLoop)
        {
            IsBackground = true,
            Name = "io_uring IO Loop",
        };
        ioThread.Start();
    }

    private static int GetSocketFd(Socket socket) =>
        (int)socket.SafeHandle.DangerousGetHandle();

    private unsafe void SubmitAccept()
    {
        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_ACCEPT;
            sqe->Fd = _listenSocketFd;
            sqe->AddrOrSpliceOffIn = 0;
            sqe->OffOrAddr2 = 0;
            sqe->Len = 0;
            sqe->UserData = IoUringConnection.EncodeUserData(0, IoUringConnection.OpType.Accept);
        }
        else
        {
            _logger.LogWarning("SQ full when submitting ACCEPT — will retry on next loop iteration.");
        }
    }

    /// <summary>Submits a READ SQE on the eventfd. When output-drain tasks write to the eventfd this completes.</summary>
    private unsafe void SubmitEventFdRead()
    {
        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_READ;
            sqe->Fd = _eventFd;
            sqe->AddrOrSpliceOffIn = (ulong)(nint)Unsafe.AsPointer(ref _eventFdReadBuf[0]);
            sqe->Len = sizeof(ulong);
            sqe->UserData = IoUringConstants.EVENTFD_USER_DATA;
        }
    }

    /// <summary>
    /// Wakes the IO loop immediately by writing to the eventfd.
    /// Called from output-drain task threads (concurrent with IO loop).
    /// </summary>
    private unsafe void WakeIoLoop()
    {
        Libc.write(_eventFd, Unsafe.AsPointer(ref _eventFdWriteBuf[0]), sizeof(ulong));
    }

    /// <summary>Enqueues a pending send and wakes the IO loop.</summary>
    private void EnqueueSend(PendingSend pending)
    {
        _pendingSendQueue.Enqueue(pending);
        WakeIoLoop();
    }

    /// <summary>Enqueues a RECV resubmission request and wakes the IO loop.</summary>
    private void RequestRecvResubmit(long connectionId)
    {
        _recvResubmitQueue.Enqueue(connectionId);
        WakeIoLoop();
    }

    /// <summary>Sets TCP_NODELAY on a socket fd to disable Nagle's algorithm.</summary>
    private unsafe void SetTcpNoDelay(int socketFd)
    {
        Libc.setsockopt(socketFd,
            IoUringConstants.IPPROTO_TCP,
            IoUringConstants.TCP_NODELAY,
            (nint)Unsafe.AsPointer(ref _sockOptBuf[0]),
            sizeof(int));
    }

    private void RunIoLoop()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _ring.SubmitAndWait(1);
                    ProcessCompletions();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in IO loop");
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            _ioLoopStopped.TrySetResult();
        }
    }

    /// <summary>Submits SEND SQEs for connections that have data and no in-flight send.</summary>
    private void DrainPendingSendQueue()
    {
        int count = _pendingSendQueue.Count;
        for (int i = 0; i < count; i++)
        {
            if (!_pendingSendQueue.TryDequeue(out var pending))
                break;

            // Skip connections with an in-flight send — re-enqueue for later.
            if (_inFlightSends.ContainsKey(pending.ConnectionId))
            {
                _pendingSendQueue.Enqueue(pending);
                continue;
            }

            if (_connections.TryGetValue(pending.ConnectionId, out var conn) && !conn.IsClosing)
            {
                if (conn.SubmitSend(in pending))
                    _inFlightSends[pending.ConnectionId] = pending;
            }
            else
            {
                // Connection gone — cancel the send.
                pending.Handle.Dispose();
                pending.Completion.TrySetResult(-1);
            }
        }
    }

    /// <summary>Resubmits RECV SQEs for connections that had async flush (back-pressure resolved).</summary>
    private void DrainRecvResubmitQueue()
    {
        while (_recvResubmitQueue.TryDequeue(out long connId))
        {
            if (_connections.TryGetValue(connId, out var conn) && !conn.IsClosing && !conn.HasRecvInFlight)
            {
                if (!conn.SubmitRecv())
                    _recvRetrySet.Add(connId);
            }
        }
    }

    /// <summary>Retries RECV submissions that previously failed due to SQ-full.</summary>
    private void RetryFailedRecvs()
    {
        if (_recvRetrySet.Count == 0) return;

        var retried = new List<long>();
        foreach (long connId in _recvRetrySet)
        {
            if (_connections.TryGetValue(connId, out var conn) && !conn.IsClosing && !conn.HasRecvInFlight)
            {
                if (conn.SubmitRecv())
                    retried.Add(connId);
            }
            else
            {
                retried.Add(connId); // Connection gone, remove from retry set.
            }
        }
        foreach (long id in retried)
            _recvRetrySet.Remove(id);
    }

    private void ProcessCompletions()
    {
        // Check for CQ overflow — indicates completions were lost by the kernel.
        uint overflow = _ring.CqOverflowCount;
        if (overflow != _lastOverflowCount)
        {
            _logger.LogCritical(
                "io_uring CQ overflow detected ({Count} completions lost). " +
                "Increase RingSize to prevent resource leaks.",
                overflow - _lastOverflowCount);
            _lastOverflowCount = overflow;
        }

        bool needsSubmit = false;

        while (_ring.TryPeekCompletion(out var cqe))
        {
            _ring.AdvanceCompletion();

            if (cqe.UserData == IoUringConstants.EVENTFD_USER_DATA)
            {
                DrainPendingSendQueue();
                DrainRecvResubmitQueue();
                SubmitEventFdRead();
                needsSubmit = true;
                continue;
            }

            var (connectionId, opType) = IoUringConnection.DecodeUserData(cqe.UserData);

            switch (opType)
            {
                case IoUringConnection.OpType.Accept:
                    HandleAccept(cqe.Res);
                    needsSubmit = true;
                    break;
                case IoUringConnection.OpType.Recv:
                    if (HandleRecv(connectionId, cqe.Res))
                        needsSubmit = true;
                    break;
                case IoUringConnection.OpType.Send:
                    HandleSend(connectionId, cqe.Res);
                    needsSubmit = true;
                    break;
                case IoUringConnection.OpType.Close:
                    HandleClose(connectionId);
                    break;
                case IoUringConnection.OpType.Cancel:
                    // Cancel completion — no action needed, the original op will also complete.
                    break;
            }
        }

        RetryFailedRecvs();

        if (needsSubmit)
            _ring.Submit();
    }

    private unsafe void HandleAccept(int result)
    {
        if (result < 0)
        {
            if (!_cts.IsCancellationRequested)
                _logger.LogWarning("Accept failed with errno {Errno}", -result);
        }
        else
        {
            int socketFd = result;

            if (_connections.Count >= _maxConnections)
            {
                _logger.LogWarning("Connection limit ({Limit}) reached; rejecting new connection.", _maxConnections);
                Libc.close(socketFd);
            }
            else
            {
                // Set TCP_NODELAY to disable Nagle's algorithm for low-latency responses.
                SetTcpNoDelay(socketFd);

                long connId = Interlocked.Increment(ref _nextConnectionId);

                // Attempt to resolve remote endpoint from the socket fd.
                EndPoint? remoteEndPoint = null;
                // Note: We could use accept4 with sockaddr to capture the remote endpoint,
                // but that requires additional io_uring flags. For now, remoteEndPoint is null.

                var conn = new IoUringConnection(
                    connId,
                    socketFd,
                    _ring,
                    remoteEndPoint,
                    EndPoint,
                    _receiveBufferSize,
                    _logger);

                _connections[connId] = conn;
                conn.StartOutputDrain(EnqueueSend, RequestRecvResubmit);

                if (!conn.SubmitRecv())
                    _recvRetrySet.Add(connId);

                _acceptChannel.Writer.TryWrite(conn);
            }
        }

        if (!_cts.IsCancellationRequested)
            SubmitAccept();
    }

    /// <summary>Returns true if a submission was queued (caller should call Submit).</summary>
    private bool HandleRecv(long connectionId, int result)
    {
        // Check closing connections first (this is a completion for an op that was in-flight when close started).
        if (_closingConnections.TryGetValue(connectionId, out var closingConn))
        {
            closingConn.HasRecvInFlight = false;
            TryFinalizeClose(connectionId, closingConn);
            return false;
        }

        if (!_connections.TryGetValue(connectionId, out var conn))
            return false;

        bool resubmit = conn.OnRecvComplete(result);

        if (result <= 0)
        {
            // Graceful close or error — initiate connection teardown.
            BeginCloseConnection(connectionId, conn);
            return false;
        }

        if (resubmit)
        {
            if (!conn.SubmitRecv())
                _recvRetrySet.Add(connectionId);
            return true;
        }

        // If OnRecvComplete returned false but result > 0, an async flush is in progress.
        // The recv will be resubmitted when the flush completes (via _recvResubmitQueue).
        return false;
    }

    private void HandleSend(long connectionId, int result)
    {
        if (_inFlightSends.Remove(connectionId, out var pending))
        {
            // Check closing connections first.
            if (_closingConnections.TryGetValue(connectionId, out var closingConn))
            {
                closingConn.HasSendInFlight = false;
                pending.Handle.Dispose();
                pending.Completion.TrySetResult(-1);
                TryFinalizeClose(connectionId, closingConn);
                return;
            }

            if (_connections.TryGetValue(connectionId, out var conn))
                conn.OnSendComplete(result, pending);
        }

        // Try to submit the next queued send for any connection.
        if (_pendingSendQueue.Count > 0)
            DrainPendingSendQueue();
    }

    private void HandleClose(long connectionId)
    {
        if (_closingConnections.Remove(connectionId, out var conn))
        {
            conn.CloseSocketFd();
            _ = conn.DisposeAsync();
        }
        _connections.TryRemove(connectionId, out _);
        if (_inFlightSends.Remove(connectionId, out var pending))
        {
            pending.Handle.Dispose();
            pending.Completion.TrySetResult(-1);
        }
        _recvRetrySet.Remove(connectionId);
    }

    /// <summary>
    /// Begins closing a connection: marks it as closing, moves it to the closing set,
    /// and waits for in-flight ops to drain before issuing CLOSE.
    /// </summary>
    private void BeginCloseConnection(long connectionId, IoUringConnection conn)
    {
        conn.IsClosing = true;
        _connections.TryRemove(connectionId, out _);
        _recvRetrySet.Remove(connectionId);
        _closingConnections[connectionId] = conn;

        TryFinalizeClose(connectionId, conn);
    }

    /// <summary>
    /// If no in-flight ops remain for the connection, submits a CLOSE SQE.
    /// </summary>
    private unsafe void TryFinalizeClose(long connectionId, IoUringConnection conn)
    {
        if (conn.HasRecvInFlight || conn.HasSendInFlight)
            return;

        // Cancel any pending sends for this connection.
        if (_inFlightSends.Remove(connectionId, out var pending))
        {
            pending.Handle.Dispose();
            pending.Completion.TrySetResult(-1);
        }

        // All io_uring ops are drained — safe to close the fd.
        _closingConnections.Remove(connectionId);
        conn.CloseSocketFd();
        _ = conn.DisposeAsync();
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _acceptChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
        WakeIoLoop();
        _acceptChannel.Writer.TryComplete();

        try
        {
            await _ioLoopStopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("IO loop did not complete gracefully within the timeout period.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnbindAsync().ConfigureAwait(false);

        // Cancel any pending sends that were never submitted.
        while (_pendingSendQueue.TryDequeue(out var pending))
        {
            pending.Handle.Dispose();
            pending.Completion.TrySetResult(-1);
        }

        // Dispose in-flight send handles (IO loop has exited, CQEs won't be reaped).
        foreach (var (_, inflight) in _inFlightSends)
        {
            inflight.Handle.Dispose();
            inflight.Completion.TrySetResult(-1);
        }
        _inFlightSends.Clear();

        foreach (var conn in _connections.Values)
        {
            if (conn.HasRecvInFlight)
                conn.CleanupRecvHandle();
            conn.CloseSocketFd();
            await conn.DisposeAsync().ConfigureAwait(false);
        }
        _connections.Clear();

        foreach (var conn in _closingConnections.Values)
        {
            if (conn.HasRecvInFlight)
                conn.CleanupRecvHandle();
            conn.CloseSocketFd();
            await conn.DisposeAsync().ConfigureAwait(false);
        }
        _closingConnections.Clear();

        _eventFdReadHandle.Dispose();
        _eventFdWriteHandle.Dispose();
        _sockOptHandle.Dispose();
        Libc.close(_eventFd);

        if (_listenSocketFdRefAdded)
            _listenSocket.SafeHandle.DangerousRelease();
        _listenSocket.Dispose();

        _ring.Dispose();
        _cts.Dispose();
    }
}
