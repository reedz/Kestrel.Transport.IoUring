using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kestrel.Transport.IoUring.Native;
using Microsoft.AspNetCore.Connections;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;

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
    private readonly IoUringTransportOptions _options;

    // Connections that need RECV resubmitted (after async pipe flush completes).
    private readonly ConcurrentQueue<(long ConnectionId, ushort Generation)> _recvResubmitQueue = new();
    private readonly ConcurrentQueue<CloseRequest> _closeRequestQueue = new();

    // Pipe scheduler — routes output pipe reader continuations to the IO loop thread.
    private IoUringPipeScheduler? _pipeScheduler;

    // Connections awaiting close after in-flight ops drain.
    private readonly Dictionary<long, IoUringConnection> _closingConnections = [];

    // Connections whose RECV failed due to SQ-full; retry on next IO loop iteration.
    private readonly HashSet<long> _recvRetrySet = [];
    private readonly HashSet<long> _recvCancelRetrySet = [];
    private readonly HashSet<(long ConnectionId, ushort Generation)> _sendRetrySet = [];
    private readonly List<long> _recvRetryScratch = [];

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

    private int _activeConnectionCount;
    private int _consecutiveErrors;
    private readonly TaskCompletionSource _ioLoopStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connectionsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _listenSocketFd;
    private bool _listenSocketFdRefAdded;
    private uint _lastOverflowCount;
    private bool _acceptMultishotActive;
    private bool _useMultishotAccept;
    private bool _acceptInFlight;
    private bool _acceptRetryRequired;
    private bool _eventFdReadInFlight;
    private bool _eventFdReadRetryRequired;
    private readonly Timer _acceptRetryTimer;
    private readonly Timer _recvRetryTimer;
    private long _acceptRetryNotBeforeTimestamp;
    private long _recvRetryNotBeforeTimestamp;
    private int _acceptErrorBackoffMs;
    private int _recvErrorBackoffMs;
    private volatile bool _stopping;
    private int _unbindStarted;
    private int _disposeStarted;
    private bool _ioLoopStarted;

    // Registered file indices for fixed-fd SQEs (-1 = not registered).
    private int _listenSocketFileIndex = -1;
    private int _eventFdFileIndex = -1;

    // Diagnostic timers for SendDiagnostics + RecvDiagnostics periodic logging
    // (null when disabled). RecvDiagnostics shares the LogPoolStatsInterval flag.
    private Timer? _diagTimer;
    private Timer? _recvDiagTimer;

    private readonly record struct CloseRequest(
        long ConnectionId,
        ushort Generation,
        ConnectionAbortedException? Reason);

    // Process-wide counter of accept-channel drops (S0.1). Inspected by
    // diagnostics; reset is not supported because it would race the writer.
    internal static long s_acceptChannelDrops;
    internal static long s_activeConnections;

    public EndPoint EndPoint { get; private set; }

    public IoUringConnectionListener(EndPoint endPoint, Ring ring, IoUringTransportOptions options, ILogger logger)
    {
        EndPoint = endPoint;
        _ring = ring;
        _logger = logger;
        _maxConnections = options.MaxConnections;
        _receiveBufferSize = options.ReceiveBufferSize;
        _options = options;
        _useMultishotAccept = options.EnableMultishotAccept;
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
        _eventFd = Libc.eventfd(0, IoUringConstants.EFD_NONBLOCK);
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
        _acceptRetryTimer = new Timer(
            static state => ((IoUringConnectionListener)state!).WakeIoLoop(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
        _recvRetryTimer = new Timer(
            static state => ((IoUringConnectionListener)state!).WakeIoLoop(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
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
        EndPoint = _listenSocket.LocalEndPoint ?? EndPoint;
        _listenSocket.Listen(listenBacklog);

        // Safely acquire the socket fd with proper ref counting.
        bool refAdded = false;
        _listenSocket.SafeHandle.DangerousAddRef(ref refAdded);
        _listenSocketFdRefAdded = refAdded;
        _listenSocketFd = (int)_listenSocket.SafeHandle.DangerousGetHandle();

        // Register fixed file table for IOSQE_FIXED_FILE optimization.
        // Table size: listen socket + eventfd + maxConnections.
        if (_options.EnableRegisteredFiles && _ring.InitFileTable(_maxConnections + 2))
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

        SubmitAccept();
        SubmitEventFdRead();

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
        ioThread.Start();
        _ioLoopStarted = true;

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
        conn.ConnectionId = $"iouring:{s}:{conn.Generation}";
        _connectionSlots[s] = conn;
        _activeConnectionCount++;
        Interlocked.Increment(ref s_activeConnections);
    }

    private void RemoveConnection(long slot)
    {
        int s = (int)slot;
        if (_connectionSlots[s] != null)
        {
            _connectionSlots[s] = null;
            _activeConnectionCount--;
            Interlocked.Decrement(ref s_activeConnections);
            _freeSlots.Push(s);
            if (_stopping && _activeConnectionCount == 0)
                _connectionsDrained.TrySetResult();
        }
    }

    private unsafe bool SubmitAccept()
    {
        if (_acceptInFlight)
            return true;

        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_ACCEPT;
            sqe->Flags = 0;
            sqe->AddrOrSpliceOffIn = 0;
            sqe->OffOrAddr2 = 0;
            sqe->Len = 0;
            sqe->OpFlags = IoUringConstants.SOCK_NONBLOCK | IoUringConstants.SOCK_CLOEXEC;
            sqe->IoPrio = 0;
            sqe->UserData = IoUringConstants.ACCEPT_USER_DATA;
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
            _acceptMultishotActive = _useMultishotAccept;
            _acceptInFlight = true;
            _acceptRetryRequired = false;
            return true;
        }

        _acceptRetryRequired = true;
        return false;
    }

    /// <summary>Submits a READ SQE on the eventfd.</summary>
    private unsafe bool SubmitEventFdRead()
    {
        if (_eventFdReadInFlight)
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
            _eventFdReadInFlight = true;
            _eventFdReadRetryRequired = false;
            return true;
        }

        _eventFdReadRetryRequired = true;
        return false;
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

    private void RequestClose(long connectionId, ushort generation, ConnectionAbortedException? reason)
    {
        _closeRequestQueue.Enqueue(new CloseRequest(connectionId, generation, reason));
        WakeIoLoop();
    }

    private void RequestSendRetry(long connectionId, ushort generation)
    {
        _sendRetrySet.Add((connectionId, generation));
    }

    /// <summary>Sets TCP_NODELAY on a socket fd to disable Nagle's algorithm.</summary>
    private unsafe bool SetTcpNoDelay(int socketFd)
    {
        return Libc.setsockopt(socketFd,
            IoUringConstants.IPPROTO_TCP,
            IoUringConstants.TCP_NODELAY,
            (nint)Unsafe.AsPointer(ref _sockOptBuf[0]),
            sizeof(int)) == 0;
    }

    private static (EndPoint? Remote, EndPoint? Local) GetSocketEndpoints(int socketFd)
    {
        try
        {
            using var handle = new SafeSocketHandle((nint)socketFd, ownsHandle: false);
            using var socket = new Socket(handle);
            return (socket.RemoteEndPoint, socket.LocalEndPoint);
        }
        catch (SocketException)
        {
            return (null, null);
        }
    }

    /// <summary>Sets a socket option on a managed Socket using raw setsockopt.</summary>
    private unsafe void SetSocketOption(Socket socket, int level, int optname)
    {
        int fd = (int)socket.SafeHandle.DangerousGetHandle();
        if (Libc.setsockopt(fd, level, optname,
            (nint)Unsafe.AsPointer(ref _sockOptBuf[0]),
            sizeof(int)) < 0)
        {
            throw new SocketException(Marshal.GetLastPInvokeError());
        }
    }

    private void RunIoLoop()
    {
        // OPT B: mark this thread so PipeScheduler.Schedule called on the IO loop
        // (e.g. via SetResult-inlined async continuations during ProcessCompletions)
        // skips the eventfd write — the outer loop will drain on next iteration.
        _pipeScheduler!.MarkIoThread();

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
                    RetryControlOperations();

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
                        uint minComplete = (_pipeScheduler != null && _pipeScheduler.HasWork) ? 0u : 1u;
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
                        FailRing(new InvalidOperationException(
                            "The io_uring loop exceeded its consecutive error limit.",
                            ex));
                        break;
                    }
                    Thread.Yield();
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
            if (conn != null &&
                conn.Generation == request.Generation &&
                !conn.IsClosing)
            {
                var outcome = conn.ResumeReceiveAfterFlush();
                if (outcome == IoUringConnection.RecvOutcome.Closed)
                {
                    BeginCloseConnection(request.ConnectionId, conn);
                    continue;
                }
                if (outcome == IoUringConnection.RecvOutcome.Deferred ||
                    conn.HasRecvInFlight ||
                    conn.RecvCancelPending)
                {
                    continue;
                }

                if (conn.UsingBufferRing && _bufferRing != null)
                {
                    if (!conn.SubmitBufferRingRecv(RECV_BUF_GROUP_ID))
                        _recvRetrySet.Add(request.ConnectionId);
                }
                else if (!conn.SubmitRecv())
                    _recvRetrySet.Add(request.ConnectionId);
            }
        }
    }

    private void DrainCloseRequestQueue()
    {
        while (_closeRequestQueue.TryDequeue(out var request))
        {
            var conn = GetConnection(request.ConnectionId);
            if (conn == null || conn.Generation != request.Generation)
                continue;

            if (request.Reason == null)
                conn.BeginApplicationDispose();
            else
                conn.BeginTransportAbort(request.Reason);
            BeginCloseConnection(request.ConnectionId, conn);
        }
    }

    /// <summary>Retries RECV submissions that previously failed due to SQ-full.</summary>
    private void RetryFailedRecvs()
    {
        if (_recvRetrySet.Count == 0) return;
        if (Stopwatch.GetTimestamp() < Volatile.Read(ref _recvRetryNotBeforeTimestamp))
            return;

        _recvRetryScratch.Clear();
        foreach (long connId in _recvRetrySet)
        {
            var conn = GetConnection(connId);
            if (conn != null && !conn.IsClosing && !conn.HasRecvInFlight)
            {
                bool ok = conn.UsingBufferRing && _bufferRing != null
                    ? conn.SubmitBufferRingRecv(RECV_BUF_GROUP_ID)
                    : conn.SubmitRecv();
                if (ok) _recvRetryScratch.Add(connId);
            }
            else
            {
                _recvRetryScratch.Add(connId);
            }
        }
        foreach (long id in _recvRetryScratch)
            _recvRetrySet.Remove(id);

        _recvRetryScratch.Clear();
        foreach (long connId in _recvCancelRetrySet)
        {
            var conn = GetConnection(connId);
            if (conn == null || conn.IsClosing || !conn.HasRecvInFlight)
            {
                _recvRetryScratch.Add(connId);
            }
            else if (!conn.RecvCancelPending && conn.SubmitRecvCancel())
            {
                _recvRetryScratch.Add(connId);
            }
        }
        foreach (long id in _recvRetryScratch)
            _recvCancelRetrySet.Remove(id);
    }

    private void ScheduleRecvRetry()
    {
        _recvErrorBackoffMs = Math.Min(
            _recvErrorBackoffMs == 0 ? 10 : _recvErrorBackoffMs * 2,
            1000);
        Volatile.Write(
            ref _recvRetryNotBeforeTimestamp,
            Stopwatch.GetTimestamp() +
            (long)(_recvErrorBackoffMs * (double)Stopwatch.Frequency / 1000));
        _recvRetryTimer.Change(_recvErrorBackoffMs, Timeout.Infinite);
    }

    private void RetryControlOperations()
    {
        if (_acceptRetryRequired &&
            !_stopping &&
            !_cts.IsCancellationRequested &&
            Stopwatch.GetTimestamp() >= Volatile.Read(ref _acceptRetryNotBeforeTimestamp))
        {
            SubmitAccept();
        }
        if (_eventFdReadRetryRequired && !_cts.IsCancellationRequested)
            SubmitEventFdRead();
    }

    private void ResumeSendRetries()
    {
        if (_sendRetrySet.Count == 0)
            return;

        var retries = _sendRetrySet.ToArray();
        _sendRetrySet.Clear();
        foreach (var (connectionId, generation) in retries)
        {
            var conn = GetConnection(connectionId);
            if (conn != null && conn.Generation == generation)
                conn.ResumeSendRetry();
        }
    }

    private void ScheduleAcceptRetry(int errno)
    {
        bool resourceExhausted =
            errno is IoUringConstants.EMFILE or
                IoUringConstants.ENFILE or
                IoUringConstants.ENOMEM or
                ENOBUFS;
        _acceptErrorBackoffMs = resourceExhausted
            ? Math.Min(_acceptErrorBackoffMs == 0 ? 10 : _acceptErrorBackoffMs * 2, 1000)
            : 10;
        _acceptRetryRequired = true;
        Volatile.Write(
            ref _acceptRetryNotBeforeTimestamp,
            Stopwatch.GetTimestamp() +
            (long)(_acceptErrorBackoffMs * (double)Stopwatch.Frequency / 1000));
        _acceptRetryTimer.Change(_acceptErrorBackoffMs, Timeout.Infinite);
    }

    private void FailRing(Exception error)
    {
        if (_stopping)
            return;

        _stopping = true;
        _logger.LogCritical(error, "The io_uring listener is stopping after a fatal ring error.");
        _acceptChannel.Writer.TryComplete(error);
        foreach (var conn in _connectionSlots)
        {
            if (conn != null)
                conn.BeginTransportAbort(new ConnectionAbortedException(
                    "The io_uring completion queue lost an event.",
                    error));
        }
        _connectionsDrained.TrySetResult();
        _cts.Cancel();
    }


    private void ProcessCompletions()
    {
        const int CompletionFairnessBatch = 32;
        int processedCompletions = 0;

        // A non-zero overflow delta means a CQE was genuinely dropped. Operation
        // ownership is no longer knowable, so fail the listener instead of guessing.
        uint overflow = _ring.CqOverflowCount;
        if (overflow != _lastOverflowCount)
        {
            uint lost = overflow - _lastOverflowCount;
            _lastOverflowCount = overflow;
            FailRing(new InvalidOperationException(
                $"io_uring dropped {lost} completion queue event(s)."));
            return;
        }

        while (_ring.TryPeekCompletion(out var cqe))
        {
            try
            {
                DispatchCompletion(cqe);
            }
            catch (Exception ex)
            {
                HandleCompletionError(cqe, ex);
            }
            finally
            {
                _ring.AdvanceCompletion();
            }

            if (++processedCompletions % CompletionFairnessBatch == 0)
            {
                _pipeScheduler?.DrainWorkItems();
                DrainCloseRequestQueue();
                DrainRecvResubmitQueue();
                if (_pipeScheduler?.LastDrainedCount > 0)
                    _ring.Submit();
            }
        }

        RetryFailedRecvs();
        RetryControlOperations();
        ResumeSendRetries();
    }

    private void DispatchCompletion(IoUringCqe cqe)
    {
        if (cqe.UserData == IoUringConstants.EVENTFD_USER_DATA)
        {
            _eventFdReadInFlight = false;
            if (cqe.Res < 0 && !_cts.IsCancellationRequested)
                _logger.LogWarning("eventfd read failed with errno {Errno}; rearming.", -cqe.Res);
            _pipeScheduler?.DrainWorkItems();
            DrainCloseRequestQueue();
            DrainRecvResubmitQueue();
            if (!_cts.IsCancellationRequested)
                SubmitEventFdRead();
            return;
        }

        if (cqe.UserData == IoUringConstants.ACCEPT_USER_DATA)
        {
            HandleAccept(cqe.Res, cqe.Flags);
            return;
        }

        var (connectionId, generation, opType) =
            IoUringConnection.DecodeUserData(cqe.UserData);
        var conn = GetConnection(connectionId);
        if (conn == null || conn.Generation != generation)
        {
            if (opType == IoUringConnection.OpType.Recv &&
                (cqe.Flags & IoUringConstants.IORING_CQE_F_BUFFER) != 0 &&
                _bufferRing != null)
            {
                ushort staleBufferId =
                    (ushort)(cqe.Flags >> IoUringConstants.IORING_CQE_BUFFER_SHIFT);
                _bufferRing.RecycleBuffer(staleBufferId);
            }
            return;
        }

        switch (opType)
        {
            case IoUringConnection.OpType.Recv:
                HandleRecv(connectionId, cqe.Res, cqe.Flags);
                break;
            case IoUringConnection.OpType.Send:
                HandleSend(connectionId, cqe.Res, cqe.Flags);
                break;
            case IoUringConnection.OpType.Cancel:
                HandleCancel(connectionId);
                break;
            default:
                _logger.LogWarning(
                    "Ignoring CQE with unknown operation type {OperationType}.",
                    (byte)opType);
                break;
        }
    }

    private void HandleCompletionError(IoUringCqe cqe, Exception error)
    {
        _logger.LogError(
            error,
            "Failed to dispatch io_uring completion 0x{UserData:x}.",
            cqe.UserData);

        if (cqe.UserData is IoUringConstants.EVENTFD_USER_DATA or
            IoUringConstants.ACCEPT_USER_DATA)
        {
            if (cqe.UserData == IoUringConstants.EVENTFD_USER_DATA)
            {
                _eventFdReadInFlight = false;
                _eventFdReadRetryRequired = true;
            }
            else
            {
                _acceptInFlight = false;
                _acceptRetryRequired = true;
            }
            return;
        }

        var (connectionId, generation, _) =
            IoUringConnection.DecodeUserData(cqe.UserData);
        var conn = GetConnection(connectionId);
        if (conn == null || conn.Generation != generation)
            return;

        conn.BeginTransportAbort(new ConnectionAbortedException(
            "The transport failed to process an io_uring completion.",
            error));
        BeginCloseConnection(connectionId, conn);
    }

    private unsafe void HandleAccept(int result, uint cqeFlags)
    {
        bool more = (cqeFlags & IoUringConstants.IORING_CQE_F_MORE) != 0;
        _acceptInFlight = more;
        bool delayedRetry = false;

        if (result < 0)
        {
            int errno = -result;
            _acceptMultishotActive = more;

            // EINVAL means multishot accept is not supported — fall back to single-shot.
            if (errno == IoUringConstants.EINVAL && _useMultishotAccept)
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
            if (errno is not IoUringConstants.EAGAIN and
                not IoUringConstants.EINTR and
                not IoUringConstants.ECONNABORTED)
            {
                ScheduleAcceptRetry(errno);
                delayedRetry = true;
            }
        }
        else
        {
            int socketFd = result;
            _acceptErrorBackoffMs = 0;
            Volatile.Write(ref _acceptRetryNotBeforeTimestamp, 0);

            if (_stopping)
            {
                Libc.close(socketFd);
                return;
            }

            // Round-7: acquire a slot from the free-list. If none available, close the
            // accepted fd cleanly instead of orphaning a live connection by slot-reuse.
            if (!_freeSlots.TryPop(out int slot))
            {
                _logger.LogWarning("Connection limit ({Limit}) reached; rejecting new connection.", _maxConnections);
                Libc.close(socketFd);
            }
            else
            {
                if (!SetTcpNoDelay(socketFd))
                {
                    _logger.LogWarning(
                        "TCP_NODELAY failed for accepted fd {Fd} with errno {Errno}.",
                        socketFd,
                        Marshal.GetLastPInvokeError());
                    _freeSlots.Push(slot);
                    Libc.close(socketFd);
                    goto AcceptCompleted;
                }

                var (remoteEndPoint, localEndPoint) = GetSocketEndpoints(socketFd);
                // connId now IS the slot index (no monotonic counter). Logging uses
                // $"iouring:{slot}" which is fine — slot reuse collides only after a
                // connection has fully closed and returned its slot.
                long connId = slot;

                // Register the accepted socket fd for IOSQE_FIXED_FILE.
                int fileIndex = _ring.HasRegisteredFiles ? _ring.RegisterFd(socketFd) : -1;

                IoUringConnection conn;
                try
                {
                    conn = new IoUringConnection(
                        connId,
                        socketFd,
                        fileIndex,
                        _ring,
                        remoteEndPoint,
                        localEndPoint ?? EndPoint,
                        _receiveBufferSize,
                        _pipeScheduler!,
                        _options.UnsafeInlineScheduling,
                        _logger,
                        useBufferRing: _bufferRing != null);
                }
                catch
                {
                    if (fileIndex >= 0)
                        _ring.UnregisterFd(fileIndex);
                    _freeSlots.Push(slot);
                    Libc.close(socketFd);
                    throw;
                }

                SetConnection(connId, conn);
                conn.InitializeIoCallbacks(
                    RequestRecvResubmit,
                    RequestSendRetry,
                    RequestClose);

                // S0.1: bounded accept channel (capacity AcceptQueueCapacity, default 128).
                // If full under burst, the accepted connection would previously be leaked
                // (slot allocated, send loop running, recv armed, but never observed by
                // Kestrel). Tear it down cleanly so the slot is freed.
                if (!_acceptChannel.Writer.TryWrite(conn))
                {
                    Interlocked.Increment(ref s_acceptChannelDrops);
                    Diagnostics.RecvDiagnostics.OnAcceptChannelDrop();
                    conn.BeginTransportAbort(new ConnectionAbortedException("Accept channel full"));
                    BeginCloseConnection(connId, conn);
                }
                else
                {
                    conn.StartSendLoop();

                    // Submit multishot recv if buffer ring is available; otherwise single-shot.
                    if (_bufferRing != null)
                    {
                        if (!conn.SubmitBufferRingRecv(RECV_BUF_GROUP_ID))
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
                }
            }

        AcceptCompleted:
            _acceptMultishotActive = more;
        }

        // If multishot ended (F_MORE not set), rearm.
        if (!more && !delayedRetry && !_stopping && !_cts.IsCancellationRequested)
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

        var conn = GetConnection(connectionId);
        if (conn == null)
        {
            if (hasBuffer && _bufferRing != null)
                _bufferRing.RecycleBuffer(bufferId);
            return false;
        }

        // ── Provided-buffer recv ──
        if (conn.UsingBufferRing)
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
                    ScheduleRecvRetry();
                    Diagnostics.RecvDiagnostics.OnRecvRetryDepth(_recvRetrySet.Count);
                    return false;
                }

                if (result == -IoUringConstants.ECANCELED && !conn.IsClosing)
                {
                    if (!conn.RecvRearmPending && !conn.RecvCancelPending)
                    {
                        if (!conn.SubmitBufferRingRecv(RECV_BUF_GROUP_ID))
                            _recvRetrySet.Add(connectionId);
                    }
                    return false;
                }

                if (result == 0) Diagnostics.RecvDiagnostics.OnRecvCleanClose();
                else if (result == -EPIPE) Diagnostics.RecvDiagnostics.OnRecvEpipe();
                else if (result == -ECONNRESET) Diagnostics.RecvDiagnostics.OnRecvEconnreset();
                else Diagnostics.RecvDiagnostics.OnRecvOtherError();

                _inputPipeComplete(conn);
                BeginCloseConnection(connectionId, conn);
                return false;
            }

            if (!hasBuffer || _bufferRing == null)
            {
                _logger.LogError(
                    "Provided-buffer recv completed without a valid buffer for connection {Id}.",
                    connectionId);
                BeginCloseConnection(connectionId, conn);
                return false;
            }

            if (hasBuffer && _bufferRing != null)
            {
                _recvErrorBackoffMs = 0;
                Volatile.Write(ref _recvRetryNotBeforeTimestamp, 0);
                IoUringConnection.RecvOutcome bufferOutcome;
                try
                {
                    var bufSpan = _bufferRing.GetBuffer(bufferId).Slice(0, result);
                    bufferOutcome = conn.OnRecvCompleteFromBuffer(bufSpan);
                }
                finally
                {
                    _bufferRing.RecycleBuffer(bufferId);
                }

                if (bufferOutcome == IoUringConnection.RecvOutcome.Deferred)
                {
                    Diagnostics.RecvDiagnostics.OnAsyncFlushPending();
                    if (more && !conn.RecvCancelPending && !conn.SubmitRecvCancel())
                        _recvCancelRetrySet.Add(connectionId);
                }
                else if (bufferOutcome == IoUringConnection.RecvOutcome.Closed)
                {
                    BeginCloseConnection(connectionId, conn);
                    return false;
                }
                else if (!more)
                {
                    if (!conn.SubmitBufferRingRecv(RECV_BUF_GROUP_ID))
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
        var outcome = conn.OnRecvComplete(result);

        if (result <= 0)
        {
            if (result == 0) Diagnostics.RecvDiagnostics.OnRecvCleanClose();
            else if (result == -EPIPE) Diagnostics.RecvDiagnostics.OnRecvEpipe();
            else if (result == -ECONNRESET) Diagnostics.RecvDiagnostics.OnRecvEconnreset();
            else Diagnostics.RecvDiagnostics.OnRecvOtherError();
            BeginCloseConnection(connectionId, conn);
            return false;
        }

        if (outcome == IoUringConnection.RecvOutcome.Closed)
        {
            BeginCloseConnection(connectionId, conn);
            return false;
        }

        if (outcome == IoUringConnection.RecvOutcome.Continue)
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

    private void HandleCancel(long connectionId)
    {
        var conn = GetConnection(connectionId);
        if (conn == null)
            return;

        conn.RecvCancelPending = false;
        if (!conn.HasRecvInFlight &&
            !conn.RecvRearmPending &&
            !conn.IsClosing &&
            conn.UsingBufferRing &&
            _bufferRing != null)
        {
            if (!conn.SubmitBufferRingRecv(RECV_BUF_GROUP_ID))
                _recvRetrySet.Add(connectionId);
        }
        if (conn.IsClosing)
            TryFinalizeClose(connectionId, conn);
    }

    private static void _inputPipeComplete(IoUringConnection conn)
    {
        // Signal the pipe that no more data will arrive.
        try { conn.CompleteInputWriter(); } catch { }
    }

    private void HandleSend(long connectionId, int result, uint cqeFlags)
    {
        if (_closingConnections.TryGetValue(connectionId, out var closingConn))
        {
            closingConn.CompleteSend(result, cqeFlags);
            TryFinalizeClose(connectionId, closingConn);
            return;
        }
        var conn = GetConnection(connectionId);
        if (conn != null)
            conn.CompleteSend(result, cqeFlags);
    }

    /// <summary>
    /// Begins closing a connection: marks it as closing, moves it to the closing set,
    /// and waits for in-flight ops to drain before issuing CLOSE.
    /// </summary>
    private void BeginCloseConnection(long connectionId, IoUringConnection conn)
    {
        conn.BeginGracefulClose();
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
    /// If no in-flight ops remain for the connection, submits a CLOSE SQE.
    /// </summary>
    private unsafe void TryFinalizeClose(long connectionId, IoUringConnection conn)
    {
        if (!conn.SendLoopCompleted ||
            conn.HasSendInFlight ||
            conn.RecvCancelPending)
            return;

        if (conn.HasRecvInFlight)
        {
            conn.ShutdownSocket();
            return;
        }

        _closingConnections.Remove(connectionId);
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
            return null;
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _unbindStarted, 1) == 0)
        {
            _stopping = true;
            _acceptChannel.Writer.TryComplete();

            if (!_ioLoopStarted)
            {
                _connectionsDrained.TrySetResult();
                _cts.Cancel();
                _ioLoopStopped.TrySetResult();
            }

            foreach (var conn in _connectionSlots)
            {
                if (conn == null)
                    continue;
                _closeRequestQueue.Enqueue(new CloseRequest(
                    conn.NumericConnectionId,
                    conn.Generation,
                    new ConnectionAbortedException("Transport is shutting down.")));
            }

            if (_activeConnectionCount == 0)
                _connectionsDrained.TrySetResult();

            if (_ioLoopStarted)
                WakeIoLoop();

            try
            {
                await _connectionsDrained.Task
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Connections did not drain gracefully within the timeout period.");
            }
            finally
            {
                _cts.Cancel();
                if (_ioLoopStarted)
                    WakeIoLoop();
            }
        }

        await _ioLoopStopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        await UnbindAsync().ConfigureAwait(false);

        var connections = new HashSet<IoUringConnection>();
        foreach (var conn in _connectionSlots)
        {
            if (conn != null)
                connections.Add(conn);
        }
        foreach (var conn in _closingConnections.Values)
            connections.Add(conn);

        foreach (var conn in connections)
            conn.CloseSocketFd();

        _ring.Dispose();

        foreach (var conn in connections)
            conn.ForceCleanupAfterRingClosed();

        Array.Clear(_connectionSlots);
        _closingConnections.Clear();

        _diagTimer?.Dispose();
        _recvDiagTimer?.Dispose();
        _acceptRetryTimer.Dispose();
        _recvRetryTimer.Dispose();
        _eventFdReadHandle.Dispose();
        _eventFdWriteHandle.Dispose();
        _sockOptHandle.Dispose();
        _bufferRing?.DisposeAfterRingClosed();
        Libc.close(_eventFd);

        if (_listenSocketFdRefAdded)
            _listenSocket.SafeHandle.DangerousRelease();
        _listenSocket.Dispose();

        _cts.Dispose();
    }
}
