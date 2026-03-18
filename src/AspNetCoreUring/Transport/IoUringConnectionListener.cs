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

    // Connections that need RECV resubmitted (after async pipe flush completes).
    private readonly ConcurrentQueue<long> _recvResubmitQueue = new();

    // Connections awaiting close after in-flight ops drain.
    private readonly Dictionary<long, IoUringConnection> _closingConnections = [];

    // Connections whose RECV failed due to SQ-full; retry on next IO loop iteration.
    private readonly HashSet<long> _recvRetrySet = [];

    // eventfd used to wake the IO loop when recv resubmission is needed.
    private readonly int _eventFd;
    private readonly ulong[] _eventFdReadBuf;
    private readonly MemoryHandle _eventFdReadHandle;
    private readonly ulong[] _eventFdWriteBuf;
    private readonly MemoryHandle _eventFdWriteHandle;

    // setsockopt value buffer (pinned for P/Invoke).
    private readonly int[] _sockOptBuf;
    private readonly MemoryHandle _sockOptHandle;

    // Provided buffer ring for multishot recv — kernel picks buffers from this pool.
    private ProvidedBufferRing? _bufferRing;
    private const ushort RECV_BUF_GROUP_ID = 0;
    private const int ENOBUFS = 105; // errno for buffer ring exhaustion

    private long _nextConnectionId;
    private readonly TaskCompletionSource _ioLoopStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _listenSocketFd;
    private bool _listenSocketFdRefAdded;
    private uint _lastOverflowCount;
    private bool _acceptMultishotActive;
    private bool _useMultishotAccept = false;

    // Registered file indices for fixed-fd SQEs (-1 = not registered).
    private int _listenSocketFileIndex = -1;
    private int _eventFdFileIndex = -1;

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

    public void Bind(int listenBacklog, bool reusePort = false)
    {
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (reusePort)
            SetSocketOption(_listenSocket, IoUringConstants.SOL_SOCKET, IoUringConstants.SO_REUSEPORT);

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

        // Register fixed file table for IOSQE_FIXED_FILE optimization.
        // Table size: listen socket + eventfd + maxConnections.
        if (_ring.InitFileTable(_maxConnections + 2))
        {
            _listenSocketFileIndex = _ring.RegisterFd(_listenSocketFd);
            _eventFdFileIndex = _ring.RegisterFd(_eventFd);
        }

        // Multishot recv + buffer ring: works for sequential and burst requests, but
        // stalls some concurrent keep-alive connections. The ENOBUFS and rearm race fixes
        // are in place; the remaining issue is likely in the multishot CQE sequencing
        // when multiple connections share the buffer ring under concurrent load.
        // Disabled until the concurrent keep-alive stall is debugged.
        _bufferRing = null;

        lock (_ring.SubmitLock)
        {
            SubmitAccept();
            SubmitEventFdRead();
        }
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
            sqe->AddrOrSpliceOffIn = 0;
            sqe->OffOrAddr2 = 0;
            sqe->Len = 0;
            sqe->UserData = IoUringConnection.EncodeUserData(0, IoUringConnection.OpType.Accept);
            if (_useMultishotAccept)
                sqe->OpFlags = IoUringConstants.IORING_ACCEPT_MULTISHOT;
            if (_listenSocketFileIndex >= 0)
            {
                sqe->Fd = _listenSocketFileIndex;
                sqe->Flags |= IoUringConstants.IOSQE_FIXED_FILE;
            }
            else
            {
                sqe->Fd = _listenSocketFd;
            }
            _acceptMultishotActive = _useMultishotAccept;
        }
        else
        {
            _logger.LogWarning("SQ full when submitting ACCEPT — will retry on next loop iteration.");
        }
    }

    /// <summary>Submits a READ SQE on the eventfd.</summary>
    private unsafe void SubmitEventFdRead()
    {
        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_READ;
            sqe->AddrOrSpliceOffIn = (ulong)(nint)Unsafe.AsPointer(ref _eventFdReadBuf[0]);
            sqe->Len = sizeof(ulong);
            sqe->UserData = IoUringConstants.EVENTFD_USER_DATA;
            if (_eventFdFileIndex >= 0)
            {
                sqe->Fd = _eventFdFileIndex;
                sqe->Flags |= IoUringConstants.IOSQE_FIXED_FILE;
            }
            else
            {
                sqe->Fd = _eventFd;
            }
        }
    }

    /// <summary>
    /// Wakes the IO loop immediately by writing to the eventfd.
    /// Called from drain task threads when recv resubmission is needed.
    /// </summary>
    private unsafe void WakeIoLoop()
    {
        Libc.write(_eventFd, Unsafe.AsPointer(ref _eventFdWriteBuf[0]), sizeof(ulong));
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

    /// <summary>Sets a socket option on a managed Socket using raw setsockopt.</summary>
    private unsafe void SetSocketOption(Socket socket, int level, int optname)
    {
        int fd = (int)socket.SafeHandle.DangerousGetHandle();
        Libc.setsockopt(fd, level, optname,
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

    /// <summary>Resubmits RECV SQEs for connections that had async flush (back-pressure resolved).</summary>
    private void DrainRecvResubmitQueue()
    {
        while (_recvResubmitQueue.TryDequeue(out long connId))
        {
            if (_connections.TryGetValue(connId, out var conn) && !conn.IsClosing && !conn.HasRecvInFlight)
            {
                if (conn.UsingMultishotRecv && _bufferRing != null)
                    conn.SubmitMultishotRecv(RECV_BUF_GROUP_ID);
                else if (!conn.SubmitRecv())
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
                bool ok;
                if (conn.UsingMultishotRecv && _bufferRing != null)
                {
                    conn.SubmitMultishotRecv(RECV_BUF_GROUP_ID);
                    ok = true;
                }
                else
                {
                    ok = conn.SubmitRecv();
                }
                if (ok) retried.Add(connId);
            }
            else
            {
                retried.Add(connId);
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
                // Eventfd fired — process recv resubmit requests and re-arm.
                lock (_ring.SubmitLock)
                {
                    DrainRecvResubmitQueue();
                    SubmitEventFdRead();
                }
                needsSubmit = true;
                continue;
            }

            var (connectionId, opType) = IoUringConnection.DecodeUserData(cqe.UserData);

            switch (opType)
            {
                case IoUringConnection.OpType.Accept:
                    lock (_ring.SubmitLock) { HandleAccept(cqe.Res, cqe.Flags); }
                    needsSubmit = true;
                    break;
                case IoUringConnection.OpType.Recv:
                    bool recvSubmitted;
                    lock (_ring.SubmitLock) { recvSubmitted = HandleRecv(connectionId, cqe.Res, cqe.Flags); }
                    if (recvSubmitted) needsSubmit = true;
                    break;
                case IoUringConnection.OpType.Send:
                    HandleSend(connectionId, cqe.Res);
                    break;
                case IoUringConnection.OpType.Close:
                    HandleClose(connectionId);
                    break;
                case IoUringConnection.OpType.Cancel:
                    break;
            }
        }

        lock (_ring.SubmitLock) { RetryFailedRecvs(); }

        if (needsSubmit)
            _ring.Submit();
    }

    private unsafe void HandleAccept(int result, uint cqeFlags)
    {
        bool more = (cqeFlags & IoUringConstants.IORING_CQE_F_MORE) != 0;

        if (result < 0)
        {
            int errno = -result;
            _acceptMultishotActive = more;

            // EINVAL means multishot accept is not supported — fall back to single-shot.
            if (errno == 22 /* EINVAL */ && _useMultishotAccept)
            {
                _useMultishotAccept = false;
                _acceptMultishotActive = false;
                _logger.LogInformation("Multishot accept not supported; using single-shot accept.");
                if (!_cts.IsCancellationRequested)
                    SubmitAccept();
                return;
            }

            if (!_cts.IsCancellationRequested)
                _logger.LogWarning("Accept failed with errno {Errno}", errno);
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
                SetTcpNoDelay(socketFd);
                long connId = Interlocked.Increment(ref _nextConnectionId);

                // Register the accepted socket fd for IOSQE_FIXED_FILE.
                int fileIndex = _ring.HasRegisteredFiles ? _ring.RegisterFd(socketFd) : -1;

                var conn = new IoUringConnection(
                    connId,
                    socketFd,
                    fileIndex,
                    _ring,
                    remoteEndPoint: null,
                    EndPoint,
                    _receiveBufferSize,
                    _logger);

                _connections[connId] = conn;
                conn.StartOutputDrain(RequestRecvResubmit);

                // Submit multishot recv if buffer ring is available; otherwise single-shot.
                if (_bufferRing != null)
                    conn.SubmitMultishotRecv(RECV_BUF_GROUP_ID);
                else if (!conn.SubmitRecv())
                    _recvRetrySet.Add(connId);

                _acceptChannel.Writer.TryWrite(conn);
            }

            _acceptMultishotActive = more;
        }

        // If multishot ended (F_MORE not set), rearm.
        if (!more && !_cts.IsCancellationRequested)
            SubmitAccept();
    }

    /// <summary>Returns true if a submission was queued (caller should call Submit).</summary>
    private bool HandleRecv(long connectionId, int result, uint cqeFlags)
    {
        bool more = (cqeFlags & IoUringConstants.IORING_CQE_F_MORE) != 0;
        bool hasBuffer = (cqeFlags & IoUringConstants.IORING_CQE_F_BUFFER) != 0;
        ushort bufferId = (ushort)(cqeFlags >> IoUringConstants.IORING_CQE_BUFFER_SHIFT);

        // Check closing connections first.
        if (_closingConnections.TryGetValue(connectionId, out var closingConn))
        {
            if (!more) closingConn.HasRecvInFlight = false;
            if (hasBuffer && _bufferRing != null)
                _bufferRing.RecycleBuffer(bufferId);
            TryFinalizeClose(connectionId, closingConn);
            return false;
        }

        if (!_connections.TryGetValue(connectionId, out var conn))
        {
            if (hasBuffer && _bufferRing != null)
                _bufferRing.RecycleBuffer(bufferId);
            return false;
        }

        // ── Multishot recv with buffer ring ──
        if (conn.UsingMultishotRecv)
        {
            if (!more) conn.HasRecvInFlight = false;

            if (result <= 0)
            {
                if (hasBuffer && _bufferRing != null)
                    _bufferRing.RecycleBuffer(bufferId);

                // ENOBUFS: buffer ring empty — transient, rearm later.
                if (result == -ENOBUFS)
                {
                    _recvRetrySet.Add(connectionId);
                    return false;
                }

                _inputPipeComplete(conn);
                BeginCloseConnection(connectionId, conn);
                return false;
            }

            if (hasBuffer && _bufferRing != null)
            {
                var bufSpan = _bufferRing.GetBuffer(bufferId).Slice(0, result);
                bool flushOk = conn.OnRecvCompleteFromBuffer(bufSpan);
                _bufferRing.RecycleBuffer(bufferId);

                // Rearm if multishot ended, flush was sync-ok, and no async rearm pending.
                if (!more && flushOk && !conn.RecvRearmPending)
                {
                    conn.SubmitMultishotRecv(RECV_BUF_GROUP_ID);
                    return true;
                }
            }
            return false;
        }

        // ── Single-shot recv (fallback) ──
        bool resubmit = conn.OnRecvComplete(result);

        if (result <= 0)
        {
            BeginCloseConnection(connectionId, conn);
            return false;
        }

        if (resubmit)
        {
            if (!conn.SubmitRecv())
                _recvRetrySet.Add(connectionId);
            return true;
        }
        return false;
    }

    private static void _inputPipeComplete(IoUringConnection conn)
    {
        // Signal the pipe that no more data will arrive.
        try { conn.CompleteInputWriter(); } catch { }
    }

    private void HandleSend(long connectionId, int result)
    {
        // Check closing connections first.
        if (_closingConnections.TryGetValue(connectionId, out var closingConn))
        {
            closingConn.CompleteSend(-1);
            TryFinalizeClose(connectionId, closingConn);
            return;
        }

        if (_connections.TryGetValue(connectionId, out var conn))
            conn.CompleteSend(result);
    }

    private void HandleClose(long connectionId)
    {
        if (_closingConnections.Remove(connectionId, out var conn))
            conn.CloseSocketFd();
        _connections.TryRemove(connectionId, out _);
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

        _closingConnections.Remove(connectionId);
        conn.CloseSocketFd();
        // Kestrel will call DisposeAsync when it finishes the HTTP pipeline.
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
        _bufferRing?.Dispose();
        Libc.close(_eventFd);

        if (_listenSocketFdRefAdded)
            _listenSocket.SafeHandle.DangerousRelease();
        _listenSocket.Dispose();

        _ring.Dispose();
        _cts.Dispose();
    }
}
