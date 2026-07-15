using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kestrel.Transport.IoUring.Native;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Kestrel.Transport.IoUring.Transport;

internal sealed class IoUringConnectionListener : IConnectionListener
{
    private readonly Ring _ring;
    private readonly ILogger _logger;
    private readonly Socket _listenSocket;
    private readonly Channel<ConnectionContext> _acceptChannel;
    private readonly IoUringConnection?[] _connectionSlots;
    private readonly ushort[] _slotGenerations;
    // Round-7: free-list of available slot indices. Acquire a slot on accept, release
    // on RemoveConnection. Prevents the pre-R7 `connId % maxConn` collision that
    // orphaned prior connections at c=2048 under default MaxConnections=1024.
    // All access is on the single IO-loop thread, so Stack<T> (no locks) is safe.
    private readonly Stack<int> _freeSlots;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _maxConnections;
    private readonly int _receiveBufferSize;
    private readonly ReceiveBufferBudget _receiveBufferBudget;
    private readonly IoUringTransportOptions _options;

    // Connections that need RECV resubmitted (after async pipe flush completes).
    private readonly ConcurrentQueue<(long ConnectionId, ushort Generation)> _recvResubmitQueue = new();
    private readonly ConcurrentQueue<(long ConnectionId, ushort Generation)> _closeRequestQueue = new();

    // Pipe scheduler — routes output pipe reader continuations to the IO loop thread.
    private IoUringPipeScheduler? _pipeScheduler;

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
    private const int EPIPE = 32;
    private const int ECONNRESET = 104;

    private int _consecutiveErrors;
    private readonly TaskCompletionSource _ioLoopStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _listenSocketFd;
    private bool _listenSocketFdRefAdded;
    private int _ioLoopStarted;
    private int _unbound;
    private int _disposed;
    private Exception? _fatalError;
    private uint _lastOverflowCount;
    private bool _useMultishotAccept = false;
    private bool _acceptSubmissionPending;
    private bool _acceptArmed;
    private bool _eventFdReadSubmissionPending;
    private bool _eventFdReadArmed;

    // Registered file indices for fixed-fd SQEs (-1 = not registered).
    private int _listenSocketFileIndex = -1;
    private int _eventFdFileIndex = -1;

    // Diagnostic timers for SendDiagnostics + RecvDiagnostics periodic logging
    // (null when disabled). RecvDiagnostics shares the LogPoolStatsInterval flag.
    private Timer? _diagTimer;
    private Timer? _recvDiagTimer;

    // Process-wide counter of accept-channel drops (S0.1). Inspected by
    // diagnostics; reset is not supported because it would race the writer.
    internal static long s_acceptChannelDrops;
    private static long s_nextPublicConnectionId;
    internal bool AcceptSubmissionPending => _acceptSubmissionPending;
    internal bool AcceptArmed => _acceptArmed;
    internal bool EventFdReadSubmissionPending => _eventFdReadSubmissionPending;
    internal bool EventFdReadArmed => _eventFdReadArmed;
    internal int OccupiedConnectionCount =>
        _connectionSlots.Count(static connection => connection != null);
    internal int ClosingConnectionCount => _closingConnections.Count;
    internal int FreeConnectionSlotCount => _freeSlots.Count;
    internal long PendingReceiveBudgetBytes => _receiveBufferBudget.ReservedBytes;

    public EndPoint EndPoint { get; private set; }

    public IoUringConnectionListener(EndPoint endPoint, Ring ring, IoUringTransportOptions options, ILogger logger)
    {
        EndPoint = endPoint;
        _ring = ring;
        _logger = logger;
        _maxConnections = options.MaxConnections;
        _receiveBufferSize = options.ReceiveBufferSize;
        _receiveBufferBudget = new ReceiveBufferBudget(options.MaxPendingReceiveBytesPerRing);
        _options = options;
        _connectionSlots = new IoUringConnection?[options.MaxConnections];
        _slotGenerations = new ushort[options.MaxConnections];
        _freeSlots = new Stack<int>(options.MaxConnections);
        // Pre-populate free-list with all slot indices. Push highest first so Pop()
        // returns 0 first — stable small connIds under light load for easier debugging.
        for (int i = options.MaxConnections - 1; i >= 0; i--) _freeSlots.Push(i);
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
        EndPoint = _listenSocket.LocalEndPoint!;

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

        // Provided buffer ring for multishot recv — eliminates per-recv Pin()/Dispose().
        if (_options.EnableBufferRing)
        {
            try
            {
                _bufferRing = new ProvidedBufferRing(
                    _ring.Fd, RECV_BUF_GROUP_ID,
                    _options.BufferRingSize, _receiveBufferSize);
                _useMultishotAccept = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register buffer ring — falling back to single-shot recv.");
                _bufferRing = null;
            }
        }
        else
        {
            _bufferRing = null;
        }

        RequestAcceptSubmission();
        RequestEventFdReadSubmission();

        // Submit pending SQEs to the kernel. NOTE: when EnableSingleIssuer is true, this
        // would bind the issuer task to the constructor thread (wrong); the IO loop's
        // first SubmitAndWait would also try to bind and fail. SINGLE_ISSUER is therefore
        // disabled by default until submissions are routed exclusively through the IO
        // loop. With SINGLE_ISSUER off, this Submit is fine on any thread.
        if (!_options.EnableSingleIssuer)
        {
            _ring.Submit();
        }

        _pipeScheduler = new IoUringPipeScheduler(WakeIoLoop);

        var ioThread = new Thread(RunIoLoop)
        {
            IsBackground = true,
            Name = "io_uring IO Loop",
        };
        Volatile.Write(ref _ioLoopStarted, 1);
        ioThread.Start();

        // Opt-in periodic diagnostic logging (Round-3, Opt G validation).
        // Read interval from option OR env var (env var allows debug without source changes
        // in benchmark fork that still references published v2.1.0 NuGet metadata).
        int statsInterval = _options.LogPoolStatsInterval;
        if (statsInterval <= 0 &&
            int.TryParse(Environment.GetEnvironmentVariable("IOURING_LOG_POOL_STATS_INTERVAL"), out var envInt) &&
            envInt > 0)
        {
            statsInterval = envInt;
        }
        if (statsInterval > 0)
        {
            _diagTimer = Diagnostics.SendDiagnostics.StartPeriodicLogger(_logger, statsInterval);
            _recvDiagTimer = Diagnostics.RecvDiagnostics.StartPeriodicLogger(_logger, statsInterval);
            _logger.LogInformation("[io_uring diag] periodic send+recv counters enabled (interval={Interval}s)",
                statsInterval);
        }
    }

    internal static string CreatePublicConnectionId() =>
        $"iouring:{Interlocked.Increment(ref s_nextPublicConnectionId)}";

    internal static (EndPoint? RemoteEndPoint, EndPoint? LocalEndPoint) GetSocketEndpoints(int socketFd)
    {
        using var handle = new SafeSocketHandle((nint)socketFd, ownsHandle: false);
        using var socket = new Socket(handle);
        return (socket.RemoteEndPoint, socket.LocalEndPoint);
    }

    // Connection slot helpers — IO loop is the sole accessor, no synchronization needed.
    // Round-7: connectionId passed throughout the IO loop is now the SLOT INDEX
    // (0..MaxConnections-1), acquired from _freeSlots on accept and returned on close.
    // Prior behavior was `slot = connId % maxConn` with a monotonic connId, which
    // recycled slots based on connId wrap-around and silently overwrote live
    // connections' slots under load (the Round-6 c=2048 mass-error root cause).
    private IoUringConnection? GetConnection(long slot) =>
        (uint)slot < (uint)_maxConnections ? _connectionSlots[slot] : null;

    private void SetConnection(long slot, IoUringConnection conn)
    {
        int s = (int)slot;
        _slotGenerations[s]++;
        conn.Generation = _slotGenerations[s];
        _connectionSlots[s] = conn;
    }

    internal void SetConnectionForTest(long slot, IoUringConnection conn) =>
        SetConnection(slot, conn);

    private void RemoveConnection(long slot)
    {
        int s = (int)slot;
        if (_connectionSlots[s] != null)
        {
            _connectionSlots[s] = null;
            _freeSlots.Push(s);
        }
    }

    private void RequestAcceptSubmission()
    {
        if (_acceptArmed || Volatile.Read(ref _unbound) != 0)
            return;

        _acceptSubmissionPending = true;
        TrySubmitAccept();
    }

    private unsafe bool TrySubmitAccept()
    {
        if (Volatile.Read(ref _unbound) != 0)
        {
            _acceptSubmissionPending = false;
            return true;
        }

        if (!_acceptSubmissionPending || _acceptArmed)
            return true;

        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_ACCEPT;
            sqe->Flags = 0;
            sqe->AddrOrSpliceOffIn = 0;
            sqe->OffOrAddr2 = 0;
            sqe->Len = 0;
            sqe->OpFlags = 0;
            sqe->IoPrio = 0;
            sqe->UserData = IoUringConnection.EncodeUserData(0, 0, IoUringConnection.OpType.Accept);
            if (_useMultishotAccept)
                sqe->IoPrio = (ushort)IoUringConstants.IORING_ACCEPT_MULTISHOT; // multishot is set in ioprio, NOT op_flags
            if (_listenSocketFileIndex >= 0)
            {
                sqe->Fd = _listenSocketFileIndex;
                sqe->Flags |= IoUringConstants.IOSQE_FIXED_FILE;
            }
            else
            {
                sqe->Fd = _listenSocketFd;
            }
            _acceptArmed = true;
            _acceptSubmissionPending = false;
            return true;
        }

        return false;
    }

    /// <summary>Submits a READ SQE on the eventfd.</summary>
    private void RequestEventFdReadSubmission()
    {
        if (_eventFdReadArmed)
            return;

        _eventFdReadSubmissionPending = true;
        TrySubmitEventFdRead();
    }

    private unsafe bool TrySubmitEventFdRead()
    {
        if (!_eventFdReadSubmissionPending || _eventFdReadArmed)
            return true;

        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_READ;
            sqe->Flags = 0;
            sqe->AddrOrSpliceOffIn = (ulong)(nint)Unsafe.AsPointer(ref _eventFdReadBuf[0]);
            sqe->Len = sizeof(ulong);
            sqe->OpFlags = 0;
            sqe->IoPrio = 0;
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
            _eventFdReadArmed = true;
            _eventFdReadSubmissionPending = false;
            return true;
        }

        return false;
    }

    private void RetryControlSubmissions()
    {
        if (_acceptSubmissionPending)
            TrySubmitAccept();
        if (_eventFdReadSubmissionPending)
            TrySubmitEventFdRead();

        if (!_acceptSubmissionPending && !_eventFdReadSubmissionPending)
            return;

        // Make queued SQEs visible to the kernel so their slots can be consumed, then
        // immediately retry the control operations before the IO loop considers parking.
        _ring.Submit();
        if (_acceptSubmissionPending)
            TrySubmitAccept();
        if (_eventFdReadSubmissionPending)
            TrySubmitEventFdRead();
    }

    internal void RetryControlSubmissionsForTest() => RetryControlSubmissions();
    internal void RequestControlSubmissionsForTest()
    {
        RequestAcceptSubmission();
        RequestEventFdReadSubmission();
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
    private void RequestRecvResubmit(long connectionId, ushort generation)
    {
        _recvResubmitQueue.Enqueue((connectionId, generation));
        WakeIoLoop();
    }

    private void RequestConnectionClose(long connectionId, ushort generation)
    {
        _closeRequestQueue.Enqueue((connectionId, generation));
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
        // OPT B: mark this thread so PipeScheduler.Schedule called on the IO loop
        // (e.g. via SetResult-inlined async continuations during ProcessCompletions)
        // skips the eventfd write — the outer loop will drain on next iteration.
        IoUringPipeScheduler.MarkIoThread();

        // OPT C: spin budget before parking on SubmitAndWait. Configurable via
        // env var IOURING_SPIN_COUNT. 0 disables (legacy behaviour: always block).
        // When >0, after a productive iteration we spin briefly polling for new
        // CQEs / pipe-scheduler work before re-blocking — reduces wakeup latency
        // under sustained load (epoll-style busy poll).
        int spinBudget = 0;
        var spinEnv = Environment.GetEnvironmentVariable("IOURING_SPIN_COUNT");
        if (!string.IsNullOrEmpty(spinEnv) && int.TryParse(spinEnv, out var parsed) && parsed > 0)
            spinBudget = parsed;

        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Drain pipe scheduler work items (send loop continuations).
                    _pipeScheduler?.DrainWorkItems();
                    DrainCloseRequestQueue();
                    DrainRecvResubmitQueue();
                    RetryControlSubmissions();

                    bool didWork = false;
                    if (spinBudget > 0)
                    {
                        // Submit any pending SQEs (no wait). If CQEs are already there OR
                        // the pipe scheduler has work, process them without parking.
                        _ring.Submit();
                        for (int i = 0; i < spinBudget; i++)
                        {
                            if (_ring.TryPeekCompletion(out _))
                            {
                                ProcessCompletions();
                                didWork = true;
                                break;
                            }
                            if (_pipeScheduler != null && _pipeScheduler.HasWork)
                            {
                                _pipeScheduler.DrainWorkItems();
                                didWork = true;
                                break;
                            }
                        }
                    }

                    if (!didWork)
                    {
                        // OPT F: dynamic min_complete. If the pipe scheduler already has
                        // queued work (e.g. another connection's send-loop staged an SQE
                        // and bumped the eventfd), don't park waiting for a CQE — submit
                        // and return immediately so we drain that work next iteration.
                        bool controlSubmissionPending =
                            _acceptSubmissionPending || _eventFdReadSubmissionPending;
                        uint minComplete =
                            (_pipeScheduler != null && _pipeScheduler.HasWork) || controlSubmissionPending
                                ? 0u
                                : 1u;
                        _ring.SubmitAndWait(minComplete);
                        ProcessCompletions();
                    }
                    _consecutiveErrors = 0;

                    // Drain work items again — send completions during ProcessCompletions
                    // may have resumed send loops that filled new SQEs via PipeScheduler.
                    _pipeScheduler?.DrainWorkItems();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;
                    _logger.LogError(ex, "Error in IO loop (consecutive: {Count})", _consecutiveErrors);
                    if (_consecutiveErrors > 5)
                    {
                        var fatalError = new InvalidOperationException(
                            "The io_uring IO loop entered an unrecoverable error state.",
                            ex);
                        _logger.LogCritical(
                            fatalError,
                            "Too many consecutive IO loop errors; stopping the listener.");
                        FailListener(fatalError);
                        break;
                    }
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
        while (_recvResubmitQueue.TryDequeue(out var request))
        {
            var conn = GetConnection(request.ConnectionId);
            if (conn == null ||
                conn.Generation != request.Generation)
            {
                continue;
            }

            var flushState = conn.ResumeRecvAfterFlush();
            if (conn.IsClosing)
            {
                TryFinalizeClose(request.ConnectionId, conn);
                continue;
            }

            if (flushState == IoUringConnection.RecvWriteResult.Closed)
            {
                BeginCloseConnection(request.ConnectionId, conn);
                continue;
            }
            if (flushState == IoUringConnection.RecvWriteResult.InputCompleted)
                continue;
            if (flushState == IoUringConnection.RecvWriteResult.Pending ||
                conn.HasRecvInFlight)
            {
                continue;
            }

            if (conn.UsingMultishotRecv && _bufferRing != null)
            {
                if (!conn.SubmitMultishotRecv(RECV_BUF_GROUP_ID))
                    _recvRetrySet.Add(request.ConnectionId);
            }
            else if (!conn.SubmitRecv())
                _recvRetrySet.Add(request.ConnectionId);
        }
    }

    internal void EnqueueRecvResubmitForTest(long connectionId, ushort generation) =>
        _recvResubmitQueue.Enqueue((connectionId, generation));

    internal void DrainRecvResubmitQueueForTest() => DrainRecvResubmitQueue();

    private void DrainCloseRequestQueue()
    {
        while (_closeRequestQueue.TryDequeue(out var request))
            ProcessCloseRequest(request.ConnectionId, request.Generation);
    }

    private bool ProcessCloseRequest(long connectionId, ushort generation)
    {
        var conn = GetConnection(connectionId);
        if (conn == null || conn.Generation != generation)
            return false;

        conn.CompleteInputWriter();
        if (conn.AbortRequested || conn.SendLoopCompleted)
            conn.ShutdownSocket();
        _logger.LogDebug(
            "Close progress for connection {Id}: abortive={Abortive}, sendLoopCompleted={SendLoopCompleted}, recvInFlight={RecvInFlight}, sendInFlight={SendInFlight}.",
            connectionId,
            conn.AbortRequested,
            conn.SendLoopCompleted,
            conn.HasRecvInFlight,
            conn.HasSendInFlight);
        BeginCloseConnection(connectionId, conn);
        return true;
    }

    internal bool ProcessCloseRequestForTest(long connectionId, ushort generation) =>
        ProcessCloseRequest(connectionId, generation);

    /// <summary>Retries RECV submissions that previously failed due to SQ-full.</summary>
    private void RetryFailedRecvs()
    {
        if (_recvRetrySet.Count == 0) return;

        var retried = new List<long>();
        foreach (long connId in _recvRetrySet)
        {
            var conn = GetConnection(connId);
            if (conn != null && !conn.IsClosing && !conn.HasRecvInFlight)
            {
                bool ok = conn.UsingMultishotRecv && _bufferRing != null
                    ? conn.SubmitMultishotRecv(RECV_BUF_GROUP_ID)
                    : conn.SubmitRecv();
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
        // NODROP rings defer CQEs internally when the mapped CQ is full. Without NODROP,
        // the owner of a dropped CQE is unknowable; mutating per-connection in-flight state
        // could release a buffer still owned by the kernel or submit a duplicate operation.
        uint overflow = _ring.CqOverflowCount;
        if (overflow != _lastOverflowCount)
        {
            uint overflowed = overflow - _lastOverflowCount;
            _lastOverflowCount = overflow;

            if (IsCqOverflowFatal(_ring.Features))
            {
                var error = new IOException(
                    $"io_uring dropped {overflowed} completion(s); connection ownership is no longer recoverable.");
                _logger.LogCritical(
                    error,
                    "io_uring CQ overflow is fatal because the ring does not report IORING_FEAT_NODROP.");
                FailListener(error);
                return;
            }

            _logger.LogWarning(
                "io_uring CQ overflowed by {Count} completion(s); the kernel retained them via IORING_FEAT_NODROP.",
                overflowed);
        }

        while (_ring.TryPeekCompletion(out var cqe))
        {
            _ring.AdvanceCompletion();

            if (cqe.UserData == IoUringConstants.EVENTFD_USER_DATA)
            {
                // Eventfd fired — drain pipe scheduler work items and recv resubmits, re-arm.
                _eventFdReadArmed = false;
                _pipeScheduler?.DrainWorkItems();
                DrainCloseRequestQueue();
                DrainRecvResubmitQueue();
                RequestEventFdReadSubmission();
                continue;
            }

            var (connectionId, generation, opType) = IoUringConnection.DecodeUserData(cqe.UserData);

            // Validate generation to detect stale CQEs from previous connections
            // that occupied the same slot.
            if (opType != IoUringConnection.OpType.Accept)
            {
                var conn = GetConnection(connectionId);
                if (conn != null && conn.Generation != generation)
                {
                    // Stale CQE from a previous connection — discard silently.
                    continue;
                }
            }

            switch (opType)
            {
                case IoUringConnection.OpType.Accept:
                    HandleAccept(cqe.Res, cqe.Flags);
                    break;
                case IoUringConnection.OpType.Recv:
                    HandleRecv(connectionId, cqe.Res, cqe.Flags);
                    break;
                case IoUringConnection.OpType.Send:
                    HandleSend(connectionId, cqe.Res, cqe.Flags);
                    break;
                case IoUringConnection.OpType.Close:
                    HandleClose(connectionId);
                    break;
                case IoUringConnection.OpType.Cancel:
                    break;
            }
        }

        RetryFailedRecvs();
        RetryControlSubmissions();

        // Single batched submit for all pending SQEs (accepts, recvs, sends).
        _ring.Submit();
    }

    internal static bool IsCqOverflowFatal(uint ringFeatures) =>
        (ringFeatures & IoUringConstants.IORING_FEAT_NODROP) == 0;

    private void FailListener(Exception error)
    {
        Interlocked.CompareExchange(ref _fatalError, error, null);
        _acceptChannel.Writer.TryComplete(error);
        _cts.Cancel();
    }

    internal void FailListenerForTest(Exception error) => FailListener(error);

    private unsafe void HandleAccept(int result, uint cqeFlags)
    {
        bool more = (cqeFlags & IoUringConstants.IORING_CQE_F_MORE) != 0;
        _acceptArmed = more;

        if (result < 0)
        {
            int errno = -result;
            // EINVAL means multishot accept is not supported — fall back to single-shot.
            if (errno == 22 /* EINVAL */ && _useMultishotAccept)
            {
                _useMultishotAccept = false;
                _logger.LogInformation("Multishot accept not supported; using single-shot accept.");
                if (!_cts.IsCancellationRequested)
                    RequestAcceptSubmission();
                return;
            }

            if (!_cts.IsCancellationRequested && Volatile.Read(ref _unbound) == 0)
                _logger.LogWarning("Accept failed with errno {Errno}", errno);
        }
        else
        {
            int socketFd = result;

            if (Volatile.Read(ref _unbound) != 0)
            {
                Libc.close(socketFd);
            }
            // Round-7: acquire a slot from the free-list. If none available, close the
            // accepted fd cleanly instead of orphaning a live connection by slot-reuse.
            else if (!_freeSlots.TryPop(out int slot))
            {
                _logger.LogWarning("Connection limit ({Limit}) reached; rejecting new connection.", _maxConnections);
                Libc.close(socketFd);
            }
            else
            {
                SetTcpNoDelay(socketFd);
                // connId now IS the slot index (no monotonic counter). Logging uses
                // $"iouring:{slot}" which is fine — slot reuse collides only after a
                // connection has fully closed and returned its slot.
                long connId = slot;

                // Register the accepted socket fd for IOSQE_FIXED_FILE.
                int fileIndex = _ring.HasRegisteredFiles ? _ring.RegisterFd(socketFd) : -1;
                EndPoint? remoteEndPoint = null;
                EndPoint? localEndPoint = EndPoint;
                try
                {
                    (remoteEndPoint, localEndPoint) = GetSocketEndpoints(socketFd);
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to resolve endpoints for accepted socket fd {Fd}.",
                        socketFd);
                }

                var conn = new IoUringConnection(
                    connId,
                    socketFd,
                    fileIndex,
                    _ring,
                    remoteEndPoint,
                    localEndPoint,
                    _receiveBufferSize,
                    _pipeScheduler!,
                    _options.UnsafeInlineScheduling,
                    _logger,
                    useBufferRing: _bufferRing != null,
                    publicConnectionId: CreatePublicConnectionId(),
                    receiveBufferBudget: _receiveBufferBudget);

                SetConnection(connId, conn);
                conn.StartSendLoop(
                    RequestRecvResubmit,
                    RequestConnectionClose,
                    RequestConnectionClose);

                // Submit multishot recv if buffer ring is available; otherwise single-shot.
                if (_bufferRing != null)
                {
                    if (!conn.SubmitMultishotRecv(RECV_BUF_GROUP_ID))
                    {
                        Diagnostics.RecvDiagnostics.OnMultishotRearmSqFull();
                        _recvRetrySet.Add(connId);
                        Diagnostics.RecvDiagnostics.OnRecvRetryDepth(_recvRetrySet.Count);
                    }
                }
                else if (!conn.SubmitRecv())
                {
                    Diagnostics.RecvDiagnostics.OnSingleshotRearmSqFull();
                    _recvRetrySet.Add(connId);
                    Diagnostics.RecvDiagnostics.OnRecvRetryDepth(_recvRetrySet.Count);
                }

                // S0.1: bounded accept channel (capacity AcceptQueueCapacity, default 128).
                // If full under burst, the accepted connection would previously be leaked
                // (slot allocated, send loop running, recv armed, but never observed by
                // Kestrel). Tear it down cleanly so the slot is freed.
                if (!_acceptChannel.Writer.TryWrite(conn))
                {
                    Interlocked.Increment(ref s_acceptChannelDrops);
                    Diagnostics.RecvDiagnostics.OnAcceptChannelDrop();
                    // Cancel CTS + complete pipes so the send loop exits and any in-flight
                    // recv (multishot or single-shot) is observed as a close. Then route
                    // through the standard close path which frees the slot, drains
                    // in-flight ops, and closes the socket fd.
                    try { conn.Abort(new ConnectionAbortedException("Accept channel full")); } catch { }
                    BeginCloseConnection(connId, conn);
                }
            }
        }

        // If multishot ended (F_MORE not set), rearm.
        if (!more &&
            !_cts.IsCancellationRequested &&
            Volatile.Read(ref _unbound) == 0)
            RequestAcceptSubmission();
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

        var conn = GetConnection(connectionId);
        if (conn == null)
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
                    Diagnostics.RecvDiagnostics.OnRecvEnobufs();
                    _recvRetrySet.Add(connectionId);
                    Diagnostics.RecvDiagnostics.OnRecvRetryDepth(_recvRetrySet.Count);
                    return false;
                }

                if (result == 0) Diagnostics.RecvDiagnostics.OnRecvCleanClose();
                else if (result == -EPIPE) Diagnostics.RecvDiagnostics.OnRecvEpipe();
                else if (result == -ECONNRESET) Diagnostics.RecvDiagnostics.OnRecvEconnreset();
                else Diagnostics.RecvDiagnostics.OnRecvOtherError();

                var endState = conn.OnRecvEnd();
                if (result < 0)
                {
                    conn.Abort(new ConnectionAbortedException(
                        $"Receive failed with errno {-result}."));
                    BeginCloseConnection(connectionId, conn);
                }
                else if (endState == IoUringConnection.RecvWriteResult.Closed)
                {
                    BeginCloseConnection(connectionId, conn);
                }
                return false;
            }

            if (hasBuffer && _bufferRing != null)
            {
                var bufSpan = _bufferRing.GetBuffer(bufferId).Slice(0, result);
                var flushState = conn.OnRecvCompleteFromBuffer(bufSpan);
                _bufferRing.RecycleBuffer(bufferId);

                if (flushState == IoUringConnection.RecvWriteResult.Closed)
                {
                    BeginCloseConnection(connectionId, conn);
                    return false;
                }

                bool flushOk = flushState == IoUringConnection.RecvWriteResult.Ready;
                if (!flushOk)
                    Diagnostics.RecvDiagnostics.OnAsyncFlushPending();

                // Rearm if multishot ended, flush was sync-ok, and no async rearm pending.
                if (!more && flushOk && !conn.RecvRearmPending)
                {
                    if (!conn.SubmitMultishotRecv(RECV_BUF_GROUP_ID))
                    {
                        Diagnostics.RecvDiagnostics.OnMultishotRearmSqFull();
                        _recvRetrySet.Add(connectionId);
                        Diagnostics.RecvDiagnostics.OnRecvRetryDepth(_recvRetrySet.Count);
                    }
                    return true;
                }
            }
            return false;
        }

        // ── Single-shot recv (fallback) ──
        var recvState = conn.OnRecvComplete(result);

        if (result < 0)
        {
            if (result == -EPIPE) Diagnostics.RecvDiagnostics.OnRecvEpipe();
            else if (result == -ECONNRESET) Diagnostics.RecvDiagnostics.OnRecvEconnreset();
            else Diagnostics.RecvDiagnostics.OnRecvOtherError();
            conn.Abort(new ConnectionAbortedException(
                $"Receive failed with errno {-result}."));
            BeginCloseConnection(connectionId, conn);
            return false;
        }
        if (result == 0)
        {
            Diagnostics.RecvDiagnostics.OnRecvCleanClose();
            return false;
        }

        if (recvState == IoUringConnection.RecvWriteResult.Closed)
        {
            BeginCloseConnection(connectionId, conn);
            return false;
        }

        if (recvState == IoUringConnection.RecvWriteResult.Ready)
        {
            if (!conn.SubmitRecv())
            {
                Diagnostics.RecvDiagnostics.OnSingleshotRearmSqFull();
                _recvRetrySet.Add(connectionId);
                Diagnostics.RecvDiagnostics.OnRecvRetryDepth(_recvRetrySet.Count);
            }
            return true;
        }
        return false;
    }

    private void HandleSend(long connectionId, int result, uint cqeFlags)
    {
        if (_closingConnections.TryGetValue(connectionId, out var closingConn))
        {
            closingConn.CompleteSend(
                closingConn.AbortRequested ? -1 : result,
                cqeFlags);
            TryFinalizeClose(connectionId, closingConn);
            return;
        }
        var conn = GetConnection(connectionId);
        if (conn != null)
            conn.CompleteSend(result, cqeFlags);
    }

    private void HandleClose(long connectionId)
    {
        if (_closingConnections.Remove(connectionId, out var conn))
            conn.CloseSocketFd();
        RemoveConnection(connectionId);
        _recvRetrySet.Remove(connectionId);
    }

    /// <summary>
    /// Begins closing a connection: marks it as closing, moves it to the closing set,
    /// and waits for in-flight ops to drain before closing the socket.
    /// </summary>
    private void BeginCloseConnection(long connectionId, IoUringConnection conn)
    {
        conn.IsClosing = true;
        // Round-7: do NOT release the slot here. Keep the connection parked in
        // _connectionSlots[slot] until in-flight CQEs drain (TryFinalizeClose).
        // Otherwise an immediate new accept could reuse the slot and route the
        // closing conn's residual recv/send completions to the new conn's lookup,
        // leaking HasRecvInFlight=true forever on the closing conn.
        _recvRetrySet.Remove(connectionId);
        _closingConnections[connectionId] = conn;

        TryFinalizeClose(connectionId, conn);
    }

    /// <summary>
    /// If no in-flight ops remain for the connection, closes its socket and releases its slot.
    /// </summary>
    private void TryFinalizeClose(long connectionId, IoUringConnection conn)
    {
        if (conn.HasRecvInFlight || conn.HasSendInFlight || !conn.SendLoopCompleted)
            return;

        _closingConnections.Remove(connectionId);
        conn.ReleasePendingReceiveBuffers();
        conn.CloseSocketFd();
        // Round-7: release the slot now that all in-flight ops have drained.
        RemoveConnection(connectionId);
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
            if (Volatile.Read(ref _fatalError) is { } fatalError)
            {
                throw new IOException(
                    "The io_uring listener stopped because its IO loop failed.",
                    fatalError);
            }
            return null;
        }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _unbound, 1) != 0)
            return ValueTask.CompletedTask;

        WakeIoLoop();
        _acceptChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    internal static Task WaitForIoLoopShutdownAsync(
        Task ioLoopStopped,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ioLoopStopped.WaitAsync(timeout, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await UnbindAsync().ConfigureAwait(false);
            _cts.Cancel();
            WakeIoLoop();
            if (Volatile.Read(ref _ioLoopStarted) != 0)
            {
                await WaitForIoLoopShutdownAsync(
                        _ioLoopStopped.Task,
                        TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // No native memory may be unmapped until the IO loop is confirmed stopped.
            Volatile.Write(ref _disposed, 0);
            throw;
        }

        var connections = new HashSet<IoUringConnection>();
        foreach (var conn in _connectionSlots)
        {
            if (conn != null)
                connections.Add(conn);
        }
        foreach (var conn in _closingConnections.Values)
            connections.Add(conn);

        foreach (var conn in connections)
        {
            conn.CompleteInputWriter();
            conn.ShutdownSocket();
            conn.CloseSocketFd();
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        Array.Clear(_connectionSlots);
        _closingConnections.Clear();

        _diagTimer?.Dispose();
        _recvDiagTimer?.Dispose();

        if (_listenSocketFdRefAdded)
            _listenSocket.SafeHandle.DangerousRelease();
        _listenSocket.Dispose();

        _ring.Dispose();
        _bufferRing?.Dispose();

        // Closing the ring is the terminal ownership boundary for any CQEs that could
        // not be drained during shutdown. Only now is it safe to release SEND pins.
        foreach (var conn in connections)
            conn.CleanupAfterRingShutdown();

        _eventFdReadHandle.Dispose();
        _eventFdWriteHandle.Dispose();
        _sockOptHandle.Dispose();
        Libc.close(_eventFd);
        _cts.Dispose();
    }
}
