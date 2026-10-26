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

internal sealed class IoUringConnectionListener : IConnectionListener, IMultiListenerWorker, IIoUringListenerInfo
{
    private readonly Ring _ring;
    private readonly ILogger _logger;
    private readonly Socket _listenSocket;
    // False when a multi-ring listener lent every ring the same listening socket: then only the lender may shut it
    // down or close it (doing so here would stop the sibling rings too).
    private readonly bool _ownsListenSocket;
    private readonly Channel<ConnectionContext> _acceptChannel;
    private readonly IoUringConnection?[] _connectionSlots;
    private readonly long[] _slotGenerations;
    // Round-7: free-list of available slot indices. Acquire a slot on accept, release
    // on RemoveConnection. Prevents the pre-R7 `connId % maxConn` collision that
    // orphaned prior connections at c=2048 under default MaxConnections=1024.
    // All access is on the single IO-loop thread, so Stack<T> (no locks) is safe.
    private readonly Stack<int> _freeSlots;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _maxConnections;
    private readonly ConnectionAdmission _admission;
    private readonly int _receiveBufferSize;
    private readonly IoUringTransportOptions _options;
    // Pipe memory for this listener's connections; disposed only once every connection is closed, never in quarantine.
    private readonly System.Buffers.MemoryPool<byte> _memoryPool;

    // Connections that need RECV resubmitted (after async pipe flush completes).
    private readonly ConcurrentQueue<(long ConnectionId, long Generation)> _recvResubmitQueue = new();
    private readonly ConcurrentQueue<CloseRequest> _closeRequestQueue = new();

    // Pipe scheduler — routes output pipe reader continuations to the IO loop thread.
    private IoUringPipeScheduler? _pipeScheduler;

    // Connections awaiting close after in-flight ops drain.
    private readonly Dictionary<long, IoUringConnection> _closingConnections = [];

    // Connections whose RECV failed due to SQ-full or whose RECV_CANCEL needs retry.
    private readonly ListenerRecvRetryState _recvRetryState = new();
    private ListenerEventFdReadState _eventFdReadState;
    private readonly ListenerSendRetryState _sendRetryState;

    // eventfd used to wake the IO loop for listener-owned cross-thread work.
    private readonly ListenerEventFdWakeState _eventFdWakeState = new();
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
    // Completed by the IO loop once it owns the ring (after enabling a ring created disabled for SINGLE_ISSUER).
    private readonly TaskCompletionSource _loopActivated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connectionsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _listenSocketFd;
    private bool _listenSocketFdRefAdded;
    private int _listenSocketReferenceReleased;

    /// <summary>
    /// True while this ring still holds its reference to the listening socket's handle (a wedged, quarantined ring
    /// never drops it). The lender of a shared socket must not close it then: closing waits for the reference.
    /// </summary>
    internal bool HoldsListenSocketReference =>
        _listenSocketFdRefAdded && Volatile.Read(ref _listenSocketReferenceReleased) == 0;
    private uint _lastOverflowCount;
    private bool _acceptMultishotActive;
    private bool _useMultishotAccept;
    private bool _acceptInFlight;
    private bool _acceptRetryRequired;
    private readonly Timer _acceptRetryTimer;
    private readonly Timer _recvRetryTimer;
    // NEW-14: a fixed-file unregister that failed leaves the socket referenced by the ring's table (the slot is not
    // reused). It is retried from the loop; this timer wakes an otherwise idle loop to do so.
    private readonly Timer _unregisterRetryTimer;
    private int _unregisterRetryAttempts;
    private long _unregisterRetryNotBeforeTimestamp;
    private const int UnregisterRetryWarnAttempts = 32;
    private long _acceptRetryNotBeforeTimestamp;
    private long _recvRetryNotBeforeTimestamp;
    // Set by each retry timer when it fires. Timers run on the coarse monotonic clock and can fire a few
    // milliseconds before the Stopwatch deadline the retry checks; a retry found not yet due after its timer fired
    // re-arms the timer for the remainder (RearmIfFiredEarly), or an idle loop would park with nothing left to wake it.
    private int _acceptRetryTimerFired;
    private int _recvRetryTimerFired;
    private int _unregisterRetryTimerFired;
    private int _acceptErrorBackoffMs;
    private int _recvErrorBackoffMs;
    // Consecutive ENOBUFS completions with no successful buffer receive in between.
    private int _enobufsStreak;
    private volatile bool _stopping;
    private int _ringFailed;
    // Test seam: invoked at the top of every IO-loop iteration so tests can inject loop faults. Null in production.
    internal Action? IoLoopIterationHook { get; set; }

    internal enum TeardownOutcome { NotRun, Quiesced, Quarantined }

    // Written by the thread that drained the ring (the IO loop, or the disposing thread if the loop never ran).
    private int _teardownOutcome;
    private int _teardownCancelsInFlight; // owner thread only

    /// <summary>Whether disposal proved the ring idle before releasing memory, or had to quarantine it.</summary>
    internal TeardownOutcome Teardown => (TeardownOutcome)Volatile.Read(ref _teardownOutcome);

    /// <summary>How long disposal waits for every request's final completion before quarantining the ring.</summary>
    internal TimeSpan TeardownDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // Test seam: issue no cancel requests while draining, so a request the listener does not know about never ends.
    internal bool SuppressTeardownCancels { get; set; }

    // Upper bound for each step of disposal that waits on another party (accept shutdown, connection drain, the loop
    // stopping). Settable for tests.
    internal TimeSpan ShutdownStepTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal Task IoLoopStopped => _ioLoopStopped.Task;

    /// <summary>True when connections of this listener receive through the provided-buffer ring (multishot).</summary>
    internal bool UsesBufferRing => _bufferRing != null;
    private int _unbindStarted;
    private int _disposeStarted;
    private bool _ioLoopStarted;

    // Unbind only stops accepting: the IO loop keeps serving established connections until DisposeAsync
    // (Kestrel gracefully closes them between UnbindAsync and DisposeAsync). Accept shutdown is driven by the
    // IO loop so the cancel SQE, any racing accept CQEs and the listen-socket close have a single owner.
    private readonly TaskCompletionSource _acceptStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _acceptStopRequested;   // set by UnbindAsync (any thread), consumed by the IO loop
    private bool _acceptStopBegun;      // IO loop only
    private bool _acceptCancelInFlight; // IO loop only
    // A cancel was submitted against the current accept request (unbind or pause); its ECANCELED is expected.
    private bool _acceptCancelRequested; // IO loop only
    // H-03: accepted connections waiting for room in the accept channel, while accepting is paused.
    private readonly Queue<IoUringConnection> _pendingAccepts = new(); // IO loop only
    private bool _acceptPaused; // IO loop only
    private int _acceptPausedSignal; // read by AcceptAsync, so a consumer wakes the loop to resume
    private int _forceCloseRequested;   // set by DisposeAsync (any thread), consumed by the IO loop
    private bool _forceCloseBegun;      // IO loop only
    private int _listenSocketReleased;
    // Registered file indices for fixed-fd SQEs (-1 = not registered).
    private int _listenSocketFileIndex = -1;
    private int _eventFdFileIndex = -1;

    // Diagnostic timers for SendDiagnostics + RecvDiagnostics periodic logging
    // (null when disabled). RecvDiagnostics shares the LogPoolStatsInterval flag.
    private Timer? _diagTimer;
    private Timer? _recvDiagTimer;

    private readonly record struct CloseRequest(
        long ConnectionId,
        long Generation,
        ConnectionAbortedException? Reason);

    // Process-wide count of accept pauses (accept channel full; see DeliverAccepted).
    internal static long s_acceptPauses;
    internal static long s_activeConnections;

    public EndPoint EndPoint { get; private set; }

    int IIoUringListenerInfo.RingCount => 1;

    uint IIoUringListenerInfo.SetupFlags => _ring.SetupFlags;

    bool IIoUringListenerInfo.UsesProvidedBufferRing => _bufferRing != null;

    public IoUringConnectionListener(
        EndPoint endPoint,
        Ring ring,
        IoUringTransportOptions options,
        ILogger logger,
        ConnectionAdmission? admission = null,
        Socket? sharedListenSocket = null)
    {
        EndPoint = endPoint;
        _ring = ring;
        _logger = logger;
        // A multi-ring listener passes one admission shared by all of its rings.
        _admission = admission ?? new ConnectionAdmission(options.MaxConnections);
        _maxConnections = options.MaxConnections;
        _receiveBufferSize = options.ReceiveBufferSize;
        _options = options;
        // First, before anything that needs releasing: the factory may throw.
        _memoryPool = options.MemoryPoolFactory?.Invoke() ?? new PinnedBlockMemoryPool();
        _eventFd = -1;
        try
        {
            _useMultishotAccept = options.EnableMultishotAccept;
            _connectionSlots = new IoUringConnection?[options.MaxConnections];
            _slotGenerations = new long[options.MaxConnections];
            _freeSlots = new Stack<int>(options.MaxConnections);
            // Pre-populate free-list with all slot indices. Push highest first so Pop()
            // returns 0 first — stable small connIds under light load for easier debugging.
            for (int i = options.MaxConnections - 1; i >= 0; i--) _freeSlots.Push(i);
            // A multi-ring listener can hand every ring the same bound, listening socket; it stays the owner's.
            _ownsListenSocket = sharedListenSocket is null;
            _listenSocket = sharedListenSocket ??
                new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _acceptChannel = Channel.CreateBounded<ConnectionContext>(
                new BoundedChannelOptions(options.AcceptQueueCapacity)
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
            _sendRetryState = new(WakeIoLoop);
            _acceptRetryTimer = new Timer(
                static state =>
                {
                    var listener = (IoUringConnectionListener)state!;
                    Volatile.Write(ref listener._acceptRetryTimerFired, 1);
                    listener.WakeIoLoop();
                },
                this,
                Timeout.Infinite,
                Timeout.Infinite);
            _recvRetryTimer = new Timer(
                static state =>
                {
                    var listener = (IoUringConnectionListener)state!;
                    Volatile.Write(ref listener._recvRetryTimerFired, 1);
                    listener.WakeIoLoop();
                },
                this,
                Timeout.Infinite,
                Timeout.Infinite);
            _unregisterRetryTimer = new Timer(
                static state =>
                {
                    var listener = (IoUringConnectionListener)state!;
                    Volatile.Write(ref listener._unregisterRetryTimerFired, 1);
                    listener.WakeIoLoop();
                },
                this,
                Timeout.Infinite,
                Timeout.Infinite);
        }
        catch
        {
            // There is no listener to dispose: release what this constructor acquired.
            _unregisterRetryTimer?.Dispose();
            _recvRetryTimer?.Dispose();
            _acceptRetryTimer?.Dispose();
            _sockOptHandle.Dispose();
            _eventFdWriteHandle.Dispose();
            _eventFdReadHandle.Dispose();
            if (_eventFd >= 0)
                Libc.close(_eventFd);
            if (_ownsListenSocket)
                _listenSocket?.Dispose();
            _memoryPool.Dispose();
            throw;
        }
    }

    private void BindOwnedListenSocket(int listenBacklog, bool reusePort)
    {
        // Only the per-ring sockets of a multi-ring listener with EnableReusePort share a port. Setting ReuseAddress
        // unconditionally (as before) is both unnecessary (plain sockets rebind fine through TIME_WAIT) and harmful:
        // .NET also enables SO_REUSEPORT for it on Linux, so a second instance of the application would bind the same
        // port without error and silently split the traffic, unlike stock Kestrel, which fails with "address already
        // in use".
        if (reusePort)
        {
            _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            SetSocketOption(_listenSocket, IoUringConstants.SOL_SOCKET, IoUringConstants.SO_REUSEPORT);
        }

        BindAndListen(_listenSocket, EndPoint, listenBacklog);
        EndPoint = _listenSocket.LocalEndPoint ?? EndPoint;
    }

    /// <summary>
    /// Binds and listens like Kestrel's socket transport: dual-stack for IPv6, and a port conflict raised as the
    /// <see cref="AddressInUseException"/> Kestrel's address binder expects (it produces its standard "address
    /// already in use" error and tolerates it for localhost dual binding).
    /// </summary>
    internal static void BindAndListen(Socket socket, EndPoint endPoint, int listenBacklog)
    {
        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            socket.DualMode = true;

        try
        {
            socket.Bind(endPoint);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new AddressInUseException(ex.Message, ex);
        }
        socket.Listen(listenBacklog);
    }

    public void Bind(int listenBacklog, bool reusePort = false)
    {
        if (!_ownsListenSocket)
        {
            // A lent socket is already bound and listening; this ring only accepts from it.
            if (reusePort)
                throw new InvalidOperationException("A shared listening socket cannot also use SO_REUSEPORT.");
            EndPoint = _listenSocket.LocalEndPoint ?? EndPoint;
        }
        else
        {
            BindOwnedListenSocket(listenBacklog, reusePort);
        }

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
        Version? kernel = IoUringKernelFeatures.CurrentKernel();
        if (_options.EnableBufferRing && !IoUringKernelFeatures.ShouldUseBufferRing(true, kernel))
        {
            _logger.LogInformation(
                "Kernel {Kernel} has no multishot receive (needs {Minimum}+); using single-shot receive.",
                kernel?.ToString() ?? "unknown",
                IoUringKernelFeatures.MultishotRecvMinimum);
            _bufferRing = null;
        }
        else if (_options.EnableBufferRing)
        {
            try
            {
                _bufferRing = new ProvidedBufferRing(
                    _ring.Fd, RECV_BUF_GROUP_ID,
                    _options.EffectiveBufferRingSize, _receiveBufferSize);
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

        // A disabled ring (SINGLE_ISSUER) is enabled by the IO loop thread, which thereby becomes its only issuer;
        // the requests prepared above are submitted from there.
        if (_options.EnableSingleIssuer &&
            (_ring.SetupFlags & IoUringConstants.IORING_SETUP_SINGLE_ISSUER) == 0)
        {
            _logger.LogInformation("IORING_SETUP_SINGLE_ISSUER is not supported by this kernel; continuing without it.");
        }
        if (!_ring.IsDisabled)
        {
            _ring.Submit();
        }

        _pipeScheduler = new IoUringPipeScheduler(WakeIoLoop);

        var ioThread = new Thread(RunIoLoop)
        {
            IsBackground = true,
            Name = "io_uring IO Loop",
        };
        _ioLoopStarted = true;
        try
        {
            ioThread.Start();
        }
        catch
        {
            _ioLoopStarted = false;
            throw;
        }

        // The loop owns the ring from here on; wait until it has taken it over (enabled a disabled ring).
        if (!_loopActivated.Task.Wait(ShutdownStepTimeout))
            throw new InvalidOperationException("The io_uring loop did not start.");
        _loopActivated.Task.GetAwaiter().GetResult();
        LoopActivatedInBindForTests?.Invoke();

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
        _connectionSlots[s] = conn;
        _activeConnectionCount++;
        Interlocked.Increment(ref s_activeConnections);
    }

    // Test seam: positions a free slot's generation counter (the next occupant gets generation + 1), for example so
    // that its 16-bit wire value repeats an earlier occupant's. Call only while the slot is free and the loop idle.
    internal void SetSlotGenerationForTests(int slot, long generation) => _slotGenerations[slot] = generation;

    internal void WakeIoLoopForTests() => WakeIoLoop();

    internal System.Buffers.MemoryPool<byte> MemoryPoolForTests => _memoryPool;

    // Test seams: the IO loop owns the count; tests read it once the ring is idle.
    internal int ActiveConnectionCountForTests => Volatile.Read(ref _activeConnectionCount);

    internal bool OwnsListenSocket => _ownsListenSocket;

    // Test seam: runs in Bind once the IO loop has taken over the ring (a throw makes Bind fail at that point).
    internal Action? LoopActivatedInBindForTests { get; set; }

    internal bool UsesMultishotAcceptForTests => _useMultishotAccept;

    /// <summary>
    /// Returns the admission reservations and gauge share of connections still counted. Only the ring's owner (or
    /// disposal once no loop runs) may call it; it zeroes the count, so it releases each reservation once.
    /// </summary>
    private void ReleaseResidualAccounting()
    {
        int residual = _activeConnectionCount;
        if (residual == 0)
            return;

        _activeConnectionCount = 0;
        Interlocked.Add(ref s_activeConnections, -residual);
        _admission.Release(residual);
    }

    private void RemoveConnection(long slot)
    {
        int s = (int)slot;
        if (_connectionSlots[s] != null)
        {
            _recvRetryState.OnConnectionReleased(slot);
            _connectionSlots[s] = null;
            _activeConnectionCount--;
            Interlocked.Decrement(ref s_activeConnections);
            _admission.Release();
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
            _acceptCancelRequested = false;
            return true;
        }

        _acceptRetryRequired = true;
        return false;
    }

    /// <summary>Submits a READ SQE on the eventfd.</summary>
    private unsafe bool SubmitEventFdRead()
    {
        var adapter = new EventFdReadSubmitAdapter(this);
        return _eventFdReadState.TrySubmitEventFdRead(ref adapter);
    }

    private readonly unsafe struct EventFdReadSubmitAdapter(IoUringConnectionListener listener)
        : ListenerEventFdReadState.IEventFdReadSubmitAdapter<EventFdReadSubmitAdapter>
    {
        private readonly IoUringConnectionListener _listener = listener;

        public static bool TrySubmitEventFdRead(ref EventFdReadSubmitAdapter self)
        {
            if (!self._listener._ring.TryGetSqe(out IoUringSqe* sqe))
                return false;

            sqe->Opcode = IoUringConstants.IORING_OP_READ;
            sqe->Flags = 0;
            sqe->AddrOrSpliceOffIn = (ulong)(nint)Unsafe.AsPointer(ref self._listener._eventFdReadBuf[0]);
            sqe->Len = sizeof(ulong);
            sqe->OpFlags = 0;
            sqe->IoPrio = 0;
            sqe->UserData = IoUringConstants.EVENTFD_USER_DATA;
            if (self._listener._eventFdFileIndex >= 0)
            {
                sqe->Fd = self._listener._eventFdFileIndex;
                sqe->Flags |= IoUringConstants.IOSQE_FIXED_FILE;
            }
            else
            {
                sqe->Fd = self._listener._eventFd;
            }

            return true;
        }
    }

    /// <summary>
    /// Wakes the IO loop immediately by writing to the eventfd.
    /// Called from drain task threads when recv resubmission is needed.
    /// </summary>
    private unsafe void WakeIoLoop()
    {
        var adapter = new EventFdWakeAdapter(this);
        _eventFdWakeState.Wake(ref adapter);
    }

    private readonly unsafe struct EventFdWakeAdapter(IoUringConnectionListener listener)
        : ListenerEventFdWakeState.IEventFdWakeAdapter<EventFdWakeAdapter>
    {
        private readonly IoUringConnectionListener _listener = listener;

        public static void Write(ref EventFdWakeAdapter self)
        {
            Libc.write(
                self._listener._eventFd,
                Unsafe.AsPointer(ref self._listener._eventFdWriteBuf[0]),
                sizeof(ulong));
        }

        public static ValueTask CloseAsync(ref EventFdWakeAdapter self)
        {
            if (Libc.close(self._listener._eventFd) < 0)
            {
                self._listener._logger.LogWarning(
                    "close(fd={Fd}) failed with errno {Errno}",
                    self._listener._eventFd,
                    Marshal.GetLastPInvokeError());
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Enqueues a RECV resubmission request and wakes the IO loop.</summary>
    private void RequestRecvResubmit(long connectionId, long generation)
    {
        _recvResubmitQueue.Enqueue((connectionId, generation));
        WakeIoLoop();
    }

    private void RequestClose(long connectionId, long generation, ConnectionAbortedException? reason)
    {
        _closeRequestQueue.Enqueue(new CloseRequest(connectionId, generation, reason));
        WakeIoLoop();
    }

    private void RequestSendRetry(long connectionId, long generation)
    {
        _sendRetryState.RequestSendRetry(connectionId, generation);
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
            try
            {
                _ring.EnableRings();
                _loopActivated.TrySetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Enabling the io_uring ring on its IO loop failed.");
                _loopActivated.TrySetException(ex);
                return;
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    IoLoopIterationHook?.Invoke();
                    ProcessLifecycleCommands();
                    FlushPendingAccepts();
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
                        // OPT F: dynamic min_complete. Queued pipe-scheduler work,
                        // an eventfd rearm retry with no read armed, a due-now
                        // accept retry, or a due recv retry pass that already lost
                        // a real SQ attempt must all keep the loop nonblocking until
                        // control work stages the SQE or the kernel frees SQ space.
                        bool schedulerHasWork = _pipeScheduler != null && _pipeScheduler.HasWork;
                        long now = Stopwatch.GetTimestamp();
                        bool acceptRetryDueNow = ListenerAcceptRetryState.IsDueNow(
                            _acceptRetryRequired,
                            _acceptInFlight,
                            _stopping || _acceptPaused || _acceptCancelInFlight,
                            _cts.IsCancellationRequested,
                            now,
                            Volatile.Read(ref _acceptRetryNotBeforeTimestamp));
                        bool recvRetryPassDueNow =
                            _recvRetryState.HasPendingRetryPassWork &&
                            now >= Volatile.Read(ref _recvRetryNotBeforeTimestamp);
                        if (!recvRetryPassDueNow)
                            _recvRetryState.ClearBlockedOnSq();
                        _ring.SubmitAndWait(
                            ListenerIoLoopWaitPolicy.GetMinComplete(
                                schedulerHasWork,
                                _eventFdReadState.RetryPendingWithoutReadInFlight,
                                acceptRetryDueNow,
                                recvRetryPassDueNow &&
                                    (_recvRetryState.BlockedOnSq || _recvRetryState.BudgetLimited)));
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
            // A dead loop can no longer cancel its accept request, so stop the port accepting first (no-op once
            // unbind has released the listening socket).
            StopAcceptingAfterLoopExit();

            TeardownOutcome outcome;
            try
            {
                outcome = DrainKernelOperations() ? TeardownOutcome.Quiesced : TeardownOutcome.Quarantined;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Draining the io_uring ring during shutdown failed.");
                outcome = TeardownOutcome.Quarantined;
            }
            Volatile.Write(ref _teardownOutcome, (int)outcome);
            // Nothing will retry receives for this listener any more.
            ReportRecvRetryDepth(loopEnded: true);
            // Nor release connections through the normal close path: return their admission (shared with sibling
            // rings, which must not be starved by a ring that stopped or was quarantined) and gauge share now.
            ReleaseResidualAccounting();

            // The loop can make no further progress. Release anything still awaiting finalization so a
            // Kestrel connection DisposeAsync cannot hang; the sockets are closed later by DisposeAsync.
            foreach (var conn in _connectionSlots)
                conn?.MarkFinalized();
            foreach (var conn in _closingConnections.Values)
                conn.MarkFinalized();
            // Nothing will service lifecycle commands again: an unbind or dispose waiting for the IO loop to
            // stop accepting or to drain connections would otherwise wait forever. The listen socket is
            // released by DisposeAsync.
            _acceptStopped.TrySetResult();
            _connectionsDrained.TrySetResult();
            _ioLoopStopped.TrySetResult();
        }
    }

    /// <summary>
    /// Ends every request the kernel still holds, so that the memory they reference can be released. Closing the
    /// ring fd is not enough: the kernel cancels requests asynchronously after close() returns, and a receive or
    /// send it is still running can touch its buffer after that. Must run on the thread that owns the ring.
    /// Aborts the remaining connections, shuts their sockets down, cancels every request and consumes completions
    /// (closing accepted sockets and finalizing connections as usual) until every submitted request has posted
    /// its final completion. Returns false if that did not happen before <see cref="TeardownDrainTimeout"/>.
    /// </summary>
    private bool DrainKernelOperations()
    {
        _stopping = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(TeardownDrainTimeout.TotalSeconds * Stopwatch.Frequency);
        long cancelInterval = Stopwatch.Frequency / 20;
        long nextCancel = 0;
        int consecutiveFailures = 0;

        AbortRemainingConnections();

        while (_ring.OutstandingOperations > 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= deadline)
                break;

            bool progressed = false;
            try
            {
                // Re-issued while requests remain: a cancel can race a request that is not cancellable yet.
                if (_teardownCancelsInFlight == 0 && now >= nextCancel)
                {
                    SubmitTeardownCancels();
                    nextCancel = now + cancelInterval;
                }

                _ring.Submit();
                _ring.EnterForTeardown();
                progressed = DrainTeardownCompletions();
                _pipeScheduler?.DrainWorkItems();
                DrainCloseRequestQueue();
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An io_uring teardown drain step failed.");
                if (++consecutiveFailures > 5)
                    break;
            }

            if (!progressed && _ring.OutstandingOperations > 0)
                Thread.Sleep(1);
        }

        long outstanding = _ring.OutstandingOperations;
        if (outstanding == 0)
        {
            _ring.SealSubmissions();
            // Unregistering fixed files is reserved to the issuer on newer SINGLE_ISSUER kernels, so the remaining
            // sockets are closed here, on the thread that owns the ring.
            CloseRemainingSockets();
            return true;
        }

        _logger.LogError(
            "{Outstanding} io_uring request(s) did not complete within {Timeout}; the ring and every buffer it may " +
            "still use are kept alive for the rest of the process instead of being released.",
            outstanding,
            TeardownDrainTimeout);
        return false;
    }

    private void CloseRemainingSockets()
    {
        var connections = new HashSet<IoUringConnection>(_closingConnections.Values);
        foreach (var conn in _connectionSlots)
        {
            if (conn != null)
                connections.Add(conn);
        }

        ListenerDisposeCleanup.RunIsolatedPerItem(
            connections,
            "connection close",
            RunDisposeCleanupStep,
            static conn => conn.CloseSocketFd());
    }

    private void AbortRemainingConnections()
    {
        foreach (var conn in _connectionSlots)
        {
            if (conn == null)
                continue;

            try
            {
                conn.BeginTransportAbort(new ConnectionAbortedException("Transport is shutting down."));
                if (!_closingConnections.ContainsKey(conn.NumericConnectionId))
                    BeginCloseConnection(conn.NumericConnectionId, conn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Aborting connection {Id} during shutdown failed.", conn.NumericConnectionId);
            }
        }

        // Completes the receive and send requests still parked on the sockets (graceful closes included).
        foreach (var conn in _closingConnections.Values.ToArray())
            conn.ShutdownSocket();
    }

    private void SubmitTeardownCancels()
    {
        if (SuppressTeardownCancels)
            return;

        // One request ends everything on Linux 5.19+. Older kernels fail it with EINVAL and rely on the targeted
        // cancels below and on the socket shutdowns, which complete the connections' receives and sends.
        TrySubmitTeardownCancel(
            0,
            IoUringConstants.IORING_ASYNC_CANCEL_ALL | IoUringConstants.IORING_ASYNC_CANCEL_ANY);
        if (_acceptInFlight)
            TrySubmitTeardownCancel(IoUringConstants.ACCEPT_USER_DATA, 0);
        if (_eventFdReadState.ReadInFlight)
            TrySubmitTeardownCancel(IoUringConstants.EVENTFD_USER_DATA, 0);
    }

    private unsafe void TrySubmitTeardownCancel(ulong targetUserData, uint cancelFlags)
    {
        if (!_ring.TryGetSqe(out IoUringSqe* sqe))
            return;

        sqe->Opcode = IoUringConstants.IORING_OP_ASYNC_CANCEL;
        sqe->Fd = -1;
        sqe->AddrOrSpliceOffIn = targetUserData;
        sqe->OpFlags = cancelFlags;
        sqe->UserData = IoUringConstants.TEARDOWN_CANCEL_USER_DATA;
        _teardownCancelsInFlight++;
    }

    private bool DrainTeardownCompletions()
    {
        bool drained = false;
        while (_ring.TryPeekCompletion(out var cqe))
        {
            drained = true;
            try
            {
                DispatchCompletion(cqe);
            }
            catch (Exception ex)
            {
                try
                {
                    HandleCompletionError(cqe, ex);
                }
                catch (Exception inner)
                {
                    _logger.LogWarning(inner, "Handling a failed completion during teardown failed.");
                }
            }
            finally
            {
                _ring.AdvanceCompletion();
            }
        }

        return drained;
    }

    /// <summary>
    /// A dead IO loop can no longer cancel its outstanding accept, and the kernel keeps accepting through that
    /// request (it holds its own reference to the listen socket, so closing our handle would not stop it).
    /// Shutting the listening socket down makes new clients get refused and fails the accept request, so the port
    /// stops accepting before unbind is acknowledged.
    /// </summary>
    private void StopAcceptingAfterLoopExit()
    {
        // A lent socket must keep serving the sibling rings; the drain cancels this ring's accept instead.
        if (!_ownsListenSocket || Volatile.Read(ref _listenSocketReleased) != 0)
            return;

        try
        {
            _listenSocket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already shut down or disposed: nothing left to stop.
        }
    }

    /// <summary>
    /// Runs IO-loop-owned lifecycle commands requested from other threads by <see cref="UnbindAsync"/>
    /// and <see cref="DisposeAsync"/>.
    /// </summary>
    private void ProcessLifecycleCommands()
    {
        if (Volatile.Read(ref _acceptStopRequested) != 0)
        {
            if (!_acceptStopBegun)
            {
                _acceptStopBegun = true;
                TryCompleteAcceptStop();
            }
            // Re-attempts the cancel while the accept request is still live (SQ was full, or the
            // cancel raced the accept's start and reported ENOENT).
            TrySubmitAcceptCancel();
        }

        if (Volatile.Read(ref _forceCloseRequested) != 0 && !_forceCloseBegun)
        {
            _forceCloseBegun = true;
            foreach (var conn in _connectionSlots)
            {
                // Connections the application has disposed keep draining their final response; everything else is
                // aborted, including connections already closing because the peer ended its side, whose send loop
                // would otherwise wait for an application that is not going to complete it.
                if (conn != null && !conn.ApplicationDisposed)
                {
                    _closeRequestQueue.Enqueue(new CloseRequest(
                        conn.NumericConnectionId,
                        conn.Generation,
                        new ConnectionAbortedException("Transport is shutting down.")));
                }
            }

            if (_activeConnectionCount == 0)
                _connectionsDrained.TrySetResult();
        }
    }

    private unsafe void TrySubmitAcceptCancel()
    {
        if (!_acceptInFlight || _acceptCancelInFlight)
            return;

        if (!_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            // SQ full: the next loop iteration flushes it and retries.
            WakeIoLoop();
            return;
        }

        sqe->Opcode = IoUringConstants.IORING_OP_ASYNC_CANCEL;
        sqe->Fd = -1;
        sqe->AddrOrSpliceOffIn = IoUringConstants.ACCEPT_USER_DATA;
        sqe->UserData = IoUringConstants.ACCEPT_CANCEL_USER_DATA;
        _acceptCancelInFlight = true;
        _acceptCancelRequested = true;
    }

    /// <summary>
    /// Completes accept shutdown once no accept or cancel request is outstanding: closes the listen socket
    /// (new clients are refused from here on) and releases <see cref="UnbindAsync"/>.
    /// </summary>
    private void TryCompleteAcceptStop()
    {
        if (!_stopping || !_acceptStopBegun || _acceptInFlight || _acceptCancelInFlight)
            return;

        ReleaseListenSocket(unregisterFixedFile: true);
        _acceptStopped.TrySetResult();
    }

    private void ReleaseListenSocket(bool unregisterFixedFile)
    {
        if (Interlocked.Exchange(ref _listenSocketReleased, 1) != 0)
            return;

        // Stop listening before anything else: closing our descriptor alone does not, while a fixed-file slot whose
        // unregister failed, or a ring that is kept (quarantine), still references the socket.
        StopListening();

        // Only valid while the ring is open (fd numbers can be reused after it closes).
        if (unregisterFixedFile && _listenSocketFileIndex >= 0)
        {
            try { _ring.UnregisterFd(_listenSocketFileIndex); } catch { }
            _listenSocketFileIndex = -1;
        }

        RunDisposeCleanupStep("listen socket handle release", () =>
        {
            if (_listenSocketFdRefAdded)
            {
                _listenSocket.SafeHandle.DangerousRelease();
                Volatile.Write(ref _listenSocketReferenceReleased, 1);
            }
        });
        // A lent socket is shut down and closed by its owner once every ring has stopped accepting.
        if (_ownsListenSocket)
            RunDisposeCleanupStep("listen socket dispose", _listenSocket.Dispose);
    }

    /// <summary>
    /// shutdown(2) on a listening TCP socket stops it listening (the kernel unhashes it, so new clients are refused
    /// and pending accepts fail) whoever else still holds a reference to it. Best effort and thread-safe (the
    /// socket's handle is reference-counted); a socket that never listened just reports ENOTCONN. Not for a lent
    /// socket: that would stop the sibling rings too.
    /// </summary>
    private void StopListening()
    {
        if (!_ownsListenSocket)
            return;

        try { _listenSocket.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
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
                if (outcome == IoUringConnection.RecvOutcome.Continue && conn.ReceiveEofPending)
                {
                    // Everything queued ahead of the peer's EOF has been delivered; complete it now.
                    _inputPipeComplete(conn);
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
                        _recvRetryState.AddRecvRetry(request.ConnectionId);
                }
                else if (!conn.SubmitRecv())
                    _recvRetryState.AddRecvRetry(request.ConnectionId);
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

    /// <summary>Retries RECV submissions that previously failed (SQ full, or the buffer pool ran dry).</summary>
    private void RetryFailedRecvs()
    {
        long now = Stopwatch.GetTimestamp();
        long notBefore = Volatile.Read(ref _recvRetryNotBeforeTimestamp);
        if (now < notBefore)
        {
            RearmIfFiredEarly(ref _recvRetryTimerFired, _recvRetryTimer, notBefore, now);
            return;
        }

        // Re-arming more sockets than there are free provided buffers only produces another round of
        // ENOBUFS. All buffers are free right after the CQ drains, so half the ring bounds the number of
        // sockets that can complete before their buffers are recycled even if every one of them has data.
        int maxArms = _bufferRing != null
            ? Math.Max(1, _options.EffectiveBufferRingSize / 2)
            : int.MaxValue;
        _recvRetryState.RetryFailedRecvs(_connectionSlots, _bufferRing, RECV_BUF_GROUP_ID, maxArms);
    }

    /// <summary>
    /// Provided-buffer exhaustion is resolved by progress, not time: the completions that hold buffers are
    /// processed and recycled, then the retry pass at the end of the same CQ drain re-arms the victims.
    /// Only a long streak with no successful receive in between means buffers are not coming back
    /// (or the peer is flooding faster than we can drain), and then we back off instead of spinning.
    /// </summary>
    private void OnBufferExhausted()
    {
        int streakLimit = Math.Max(1024, _options.EffectiveBufferRingSize * 8);
        if (++_enobufsStreak > streakLimit)
            ScheduleRecvRetry();
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
        Volatile.Write(ref _recvRetryTimerFired, 0);
        _recvRetryTimer.Change(_recvErrorBackoffMs, Timeout.Infinite);
    }

    private static void RearmIfFiredEarly(ref int fired, Timer timer, long notBeforeTimestamp, long now)
    {
        if (Interlocked.Exchange(ref fired, 0) == 0)
            return;

        long remainingMs = (notBeforeTimestamp - now) * 1000 / Stopwatch.Frequency + 1;
        timer.Change(remainingMs, Timeout.Infinite);
    }

    private void RetryControlOperations()
    {
        long now = Stopwatch.GetTimestamp();
        long acceptNotBefore = Volatile.Read(ref _acceptRetryNotBeforeTimestamp);
        if (ListenerAcceptRetryState.IsDueNow(
            _acceptRetryRequired,
            _acceptInFlight,
            _stopping || _acceptPaused || _acceptCancelInFlight,
            _cts.IsCancellationRequested,
            now,
            acceptNotBefore))
        {
            SubmitAccept();
        }
        else if (_acceptRetryRequired && !_acceptInFlight && now < acceptNotBefore)
        {
            RearmIfFiredEarly(ref _acceptRetryTimerFired, _acceptRetryTimer, acceptNotBefore, now);
        }
        if (_eventFdReadState.RetryRequired && !_cts.IsCancellationRequested)
        {
            var eventFdReadAdapter = new EventFdReadSubmitAdapter(this);
            _eventFdReadState.RetryPendingEventFdRead(ref eventFdReadAdapter);
        }
        RetryFailedFixedFileUnregisters();
    }

    private void RetryFailedFixedFileUnregisters()
    {
        if (_ring.FailedUnregisterCount == 0)
            return;

        long now = Stopwatch.GetTimestamp();
        long notBefore = Volatile.Read(ref _unregisterRetryNotBeforeTimestamp);
        if (now < notBefore)
        {
            RearmIfFiredEarly(ref _unregisterRetryTimerFired, _unregisterRetryTimer, notBefore, now);
            return;
        }

        if (_ring.RetryFailedUnregisters() == 0)
        {
            _unregisterRetryAttempts = 0;
            return;
        }

        // The slots stay reserved (never reused) and their sockets were shut down when they closed, so the cost of
        // failing for good is only the kernel's reference until the ring closes. Keep retrying, slowly.
        if (++_unregisterRetryAttempts == UnregisterRetryWarnAttempts)
        {
            _logger.LogError(
                "{Count} fixed-file slot(s) could not be unregistered after {Attempts} attempts; retrying every second.",
                _ring.FailedUnregisterCount,
                _unregisterRetryAttempts);
        }

        int delayMs = Math.Min(10 << Math.Min(_unregisterRetryAttempts, 7), 1000);
        Volatile.Write(
            ref _unregisterRetryNotBeforeTimestamp,
            now + (long)(delayMs * (double)Stopwatch.Frequency / 1000));
        Volatile.Write(ref _unregisterRetryTimerFired, 0);
        _unregisterRetryTimer.Change(delayMs, Timeout.Infinite);
    }

    private void ResumeSendRetries()
    {
        _sendRetryState.ResumeSendRetries(_connectionSlots);
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
        Volatile.Write(ref _acceptRetryTimerFired, 0);
        _acceptRetryTimer.Change(_acceptErrorBackoffMs, Timeout.Infinite);
    }

    private void FailRing(Exception error)
    {
        // Not _stopping: unbind sets that while the loop keeps serving app-owned connections, and a fatal ring
        // error during that drain must still abort them and stop the loop.
        if (Interlocked.Exchange(ref _ringFailed, 1) != 0)
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

        // The listener treats any overflow delta as fail-safe evidence that CQ
        // ownership may now be ambiguous, so it stops instead of guessing.
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
                // A CQ that is never empty must not starve an unbind or dispose waiting for the loop, nor parked
                // accepts waiting for room in the accept channel.
                ProcessLifecycleCommands();
                FlushPendingAccepts();
                // Whatever this batch staged (sends, re-arms, accept pause/resume, cancels), and anything an earlier
                // short or EAGAIN enter left in the SQ ring, reaches the kernel now rather than after the CQ drains.
                if (_ring.HasUnsubmittedSqes)
                    _ring.Submit();
            }
        }

        RetryFailedRecvs();
        RetryControlOperations();
        ResumeSendRetries();
        FlushPendingAccepts();
        // Retry passes and closes remove entries; keep the process-wide gauge live.
        ReportRecvRetryDepth();
    }

    private int _reportedRecvRetryDepth; // IO loop only

    /// <summary>Publishes this listener's change in receive-retry entries to the process-wide gauge (NEW-13).</summary>
    private void ReportRecvRetryDepth(bool loopEnded = false)
    {
        int depth = loopEnded ? 0 : _recvRetryState.RecvRetryCount;
        int delta = depth - _reportedRecvRetryDepth;
        if (delta == 0)
            return;

        _reportedRecvRetryDepth = depth;
        Diagnostics.RecvDiagnostics.OnRecvRetryDepthChanged(delta);
    }

    private void DispatchCompletion(IoUringCqe cqe)
    {
        if (cqe.UserData == IoUringConstants.TEARDOWN_CANCEL_USER_DATA)
        {
            // Result is informational: the cancelled requests' own final completions are what end them.
            _teardownCancelsInFlight--;
            return;
        }

        if (cqe.UserData == IoUringConstants.EVENTFD_USER_DATA)
        {
            _eventFdReadState.OnEventFdReadCompletion();
            if (cqe.Res < 0 && !_cts.IsCancellationRequested)
                _logger.LogWarning("eventfd read failed with errno {Errno}; rearming.", -cqe.Res);
            _pipeScheduler?.DrainWorkItems();
            DrainCloseRequestQueue();
            DrainRecvResubmitQueue();
            // AcceptAsync wakes the loop through the eventfd when it makes room for parked accepts.
            FlushPendingAccepts();
            if (!_cts.IsCancellationRequested)
            {
                var eventFdReadAdapter = new EventFdReadSubmitAdapter(this);
                _eventFdReadState.TrySubmitEventFdRead(ref eventFdReadAdapter);
            }
            return;
        }

        if (cqe.UserData == IoUringConstants.ACCEPT_USER_DATA)
        {
            HandleAccept(cqe.Res, cqe.Flags);
            return;
        }

        if (cqe.UserData == IoUringConstants.ACCEPT_CANCEL_USER_DATA)
        {
            // Result is informational (0 cancelled, -ENOENT/-EALREADY raced the accept's own completion).
            // The accept request's terminal CQE is what ends it; ProcessLifecycleCommands re-issues the
            // cancel if the accept is somehow still live.
            _acceptCancelInFlight = false;
            TryCompleteAcceptStop();
            ResumeAcceptIfIdle();
            return;
        }

        var (connectionId, wireGeneration, opType) =
            IoUringConnection.DecodeUserData(cqe.UserData);
        var conn = GetConnection(connectionId);
        // user_data carries only the low 16 bits of the generation; see IoUringConnection.Generation.
        if (conn == null || (ushort)conn.Generation != wireGeneration)
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
            IoUringConstants.ACCEPT_USER_DATA or
            IoUringConstants.ACCEPT_CANCEL_USER_DATA)
        {
            if (cqe.UserData == IoUringConstants.EVENTFD_USER_DATA)
            {
                _eventFdReadState.OnEventFdReadCompletionDispatchError();
            }
            else if (cqe.UserData == IoUringConstants.ACCEPT_CANCEL_USER_DATA)
            {
                _acceptCancelInFlight = false;
            }
            else
            {
                // A multishot accept that posted F_MORE is still armed; only a final completion ends it.
                bool stillArmed = (cqe.Flags & IoUringConstants.IORING_CQE_F_MORE) != 0;
                _acceptInFlight = stillArmed;
                _acceptRetryRequired = !stillArmed;
            }
            return;
        }

        var (connectionId, wireGeneration, _) =
            IoUringConnection.DecodeUserData(cqe.UserData);
        var conn = GetConnection(connectionId);
        if (conn == null || (ushort)conn.Generation != wireGeneration)
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

        if (_stopping)
        {
            // Accept is shutting down (unbind or fatal error): reclaim any connection that raced the
            // cancel instead of surfacing it, and finish accept shutdown if nothing else is outstanding.
            if (result >= 0)
                Libc.close(result);
            TryCompleteAcceptStop();
            return;
        }

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

            if (errno == IoUringConstants.ECANCELED && _acceptCancelRequested)
            {
                // Our own cancel (accept paused because the accept channel is full). It is re-armed when the
                // backlog is delivered (FlushPendingAccepts) or when the cancel completes, whichever is last.
            }
            else
            {
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

            // Admission first (shared by every ring of the listener), before any connection resource exists. It is
            // released exactly once: here on any failure before SetConnection, else by RemoveConnection or disposal.
            if (!_admission.TryReserve())
            {
                _logger.LogWarning(
                    "Connection limit ({Limit}) reached; rejecting new connection.",
                    _admission.Limit);
                Libc.close(socketFd);
            }
            else if (!_freeSlots.TryPop(out int slot))
            {
                _admission.Release();
                _logger.LogWarning("No free connection slot ({Limit}); rejecting new connection.", _maxConnections);
                Libc.close(socketFd);
            }
            else
            {
                // SocketTransportOptions.NoDelay (default true). Accepted sockets otherwise inherit the listening
                // socket's setting, which is Nagle on.
                if (_options.NoDelay && !SetTcpNoDelay(socketFd))
                {
                    _logger.LogWarning(
                        "TCP_NODELAY failed for accepted fd {Fd} with errno {Errno}.",
                        socketFd,
                        Marshal.GetLastPInvokeError());
                    _freeSlots.Push(slot);
                    _admission.Release();
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
                        useBufferRing: _bufferRing != null,
                        maxReadBufferSize: _options.MaxReadBufferSize,
                        maxWriteBufferSize: _options.MaxWriteBufferSize,
                        memoryPool: _memoryPool);
                }
                catch
                {
                    if (fileIndex >= 0)
                        _ring.UnregisterFd(fileIndex);
                    _freeSlots.Push(slot);
                    _admission.Release();
                    Libc.close(socketFd);
                    throw;
                }

                SetConnection(connId, conn);
                conn.InitializeIoCallbacks(
                    RequestRecvResubmit,
                    RequestSendRetry,
                    RequestClose);

                conn.StartSendLoop();

                // Submit multishot recv if buffer ring is available; otherwise single-shot.
                if (_bufferRing != null)
                {
                    if (!conn.SubmitBufferRingRecv(RECV_BUF_GROUP_ID))
                    {
                        Diagnostics.RecvDiagnostics.OnMultishotRearmSqFull();
                        _recvRetryState.AddRecvRetry(connId);
                        ReportRecvRetryDepth();
                    }
                }
                else if (!conn.SubmitRecv())
                {
                    Diagnostics.RecvDiagnostics.OnSingleshotRearmSqFull();
                    _recvRetryState.AddRecvRetry(connId);
                    ReportRecvRetryDepth();
                }

                DeliverAccepted(conn);
            }

        AcceptCompleted:
            _acceptMultishotActive = more;
        }

        // If the accept request ended (F_MORE not set), rearm, unless it is paused or a cancel of it is still
        // outstanding (ResumeAcceptIfIdle runs when that completes).
        if (!more && !delayedRetry)
            ResumeAcceptIfIdle();
    }

    /// <summary>
    /// H-03: hands an accepted connection to <see cref="AcceptAsync"/>. When the accept channel is full the
    /// connection is parked (IO-loop owned, in arrival order) and accepting pauses: the multishot accept is
    /// cancelled, so further clients wait in the kernel's listen backlog (TCP backpressure, like SocketTransport
    /// when Kestrel stops calling AcceptAsync) instead of being accepted and then aborted. Connections the kernel
    /// accepted before the cancel landed are parked too. The admission limit bounds the parked set.
    /// </summary>
    private void DeliverAccepted(IoUringConnection conn)
    {
        if (_pendingAccepts.Count == 0 && _acceptChannel.Writer.TryWrite(conn))
            return;

        _pendingAccepts.Enqueue(conn);
        if (_acceptPaused)
            return;

        _acceptPaused = true;
        Volatile.Write(ref _acceptPausedSignal, 1);
        Interlocked.Increment(ref s_acceptPauses);
        Diagnostics.RecvDiagnostics.OnAcceptPaused();
        if (_acceptInFlight && _acceptMultishotActive)
            TrySubmitAcceptCancel();
    }

    /// <summary>Moves parked connections into the accept channel as it drains; resumes accepting once all are delivered.</summary>
    private void FlushPendingAccepts()
    {
        if (!_acceptPaused)
            return;

        while (_pendingAccepts.Count > 0)
        {
            if (!_acceptChannel.Writer.TryWrite(_pendingAccepts.Peek()))
            {
                // Still full. A pause cancel that raced the accept's start (ENOENT) leaves it accepting: re-issue.
                if (_acceptInFlight && _acceptMultishotActive && !_acceptCancelInFlight)
                    TrySubmitAcceptCancel();
                return;
            }
            _pendingAccepts.Dequeue();
        }

        _acceptPaused = false;
        Volatile.Write(ref _acceptPausedSignal, 0);
        ResumeAcceptIfIdle();
    }

    /// <summary>Re-arms accepting once nothing is paused and neither the accept nor a cancel of it is outstanding.</summary>
    private void ResumeAcceptIfIdle()
    {
        if (_acceptPaused || _acceptInFlight || _acceptCancelInFlight || _stopping || _cts.IsCancellationRequested)
            return;

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

                // ENOBUFS: the buffer pool was empty when this socket became readable, which terminated
                // its multishot receive. Retried by progress once the CQ drains and buffers are recycled.
                if (result == -ENOBUFS)
                {
                    Diagnostics.RecvDiagnostics.OnRecvEnobufs();
                    _recvRetryState.AddRecvRetry(connectionId);
                    OnBufferExhausted();
                    ReportRecvRetryDepth();
                    return false;
                }

                if (result == -IoUringConstants.ECANCELED && !conn.IsClosing)
                {
                    if (!conn.RecvRearmPending && !conn.RecvCancelPending)
                    {
                        if (!conn.SubmitBufferRingRecv(RECV_BUF_GROUP_ID))
                            _recvRetryState.AddRecvRetry(connectionId);
                    }
                    return false;
                }

                if (result == 0) Diagnostics.RecvDiagnostics.OnRecvCleanClose();
                else if (result == -EPIPE) Diagnostics.RecvDiagnostics.OnRecvEpipe();
                else if (result == -ECONNRESET) Diagnostics.RecvDiagnostics.OnRecvEconnreset();
                else Diagnostics.RecvDiagnostics.OnRecvOtherError();

                // A clean EOF can overtake data still queued behind pipe backpressure (the multishot receive
                // keeps posting until its cancel lands). Deliver that data first; a reset discards it anyway.
                // Like SocketTransport, whose receive loop awaits the paused flush before it can read the FIN,
                // the closure is not observable until the application drains the input.
                if (result == 0 && conn.TryDeferReceiveEof())
                    return false;

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
                _enobufsStreak = 0;
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
                        _recvRetryState.AddRecvCancelRetry(
                            connectionId,
                            new(conn.Generation, conn.RecvEpoch));
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
                        _recvRetryState.AddRecvRetry(connectionId);
                        ReportRecvRetryDepth();
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
                _recvRetryState.AddRecvRetry(connectionId);
                ReportRecvRetryDepth();
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
                _recvRetryState.AddRecvRetry(connectionId);
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
        _recvRetryState.OnConnectionClosing(connectionId);
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

        var finalizationAdapter = new ProductionCloseFinalizationAdapter(this, conn);
        ListenerCloseFinalization.Run(connectionId, conn.HasRecvInFlight, ref finalizationAdapter);
        // Kestrel will call DisposeAsync when it finishes the HTTP pipeline.
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _acceptChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            // Room was made in the accept channel: let the loop deliver what it parked and resume accepting.
            if (Volatile.Read(ref _acceptPausedSignal) != 0)
                WakeIoLoop();
            return connection;
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

    /// <summary>
    /// Stops accepting new connections. Established connections are untouched: Kestrel closes them gracefully
    /// (and aborts stragglers) before disposing the listener, so the IO loop keeps running until
    /// <see cref="DisposeAsync"/>. Never throws for a cancelled token; accept shutdown continues on the IO loop.
    /// </summary>
    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _unbindStarted, 1) == 0)
        {
            _stopping = true;
            _acceptChannel.Writer.TryComplete();

            if (_ioLoopStarted)
            {
                Volatile.Write(ref _acceptStopRequested, 1);
                WakeIoLoop();
            }
            else
            {
                ReleaseListenSocket(unregisterFixedFile: false);
                _acceptStopped.TrySetResult();
            }
        }

        try
        {
            await _acceptStopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best effort: the caller stopped waiting, accept shutdown still completes on the IO loop.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        // Every wait below is bounded: an accept shutdown or a drain that cannot finish must not hang the host.
        using (var unbindTimeout = new CancellationTokenSource(ShutdownStepTimeout))
            await UnbindAsync(unbindTimeout.Token).ConfigureAwait(false);

        bool loopStopped;
        if (_ioLoopStarted)
        {
            // Anything still open here was not closed by Kestrel (direct disposal or a fatal error).
            // Abort it so disposal cannot leak sockets or hang.
            Volatile.Write(ref _forceCloseRequested, 1);
            WakeIoLoop();
            try
            {
                await _connectionsDrained.Task
                    .WaitAsync(ShutdownStepTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Connections did not drain within the timeout period.");
            }
            finally
            {
                // The IO loop exits and drains the ring (DrainKernelOperations) before it reports stopping.
                _cts.Cancel();
                WakeIoLoop();
            }

            try
            {
                await _ioLoopStopped.Task
                    .WaitAsync(TeardownDrainTimeout + ShutdownStepTimeout)
                    .ConfigureAwait(false);
                loopStopped = true;
            }
            catch (TimeoutException)
            {
                // The loop is wedged and still owns the ring: nothing it references may be touched.
                _logger.LogError("The io_uring loop did not stop; its ring is quarantined.");
                loopStopped = false;
            }
        }
        else
        {
            // No loop ever owned the ring, so this thread does (requests may have been prepared by Bind).
            _connectionsDrained.TrySetResult();
            TeardownOutcome outcome;
            try
            {
                outcome = DrainKernelOperations() ? TeardownOutcome.Quiesced : TeardownOutcome.Quarantined;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Draining the io_uring ring during shutdown failed.");
                outcome = TeardownOutcome.Quarantined;
            }
            Volatile.Write(ref _teardownOutcome, (int)outcome);
            _ioLoopStopped.TrySetResult();
            loopStopped = true;
        }

        if (!loopStopped)
        {
            // The owner still runs: its collections, pipes and sockets are not ours to touch.
            QuarantineWedgedLoop();
            return;
        }

        var connections = new HashSet<IoUringConnection>();
        foreach (var conn in _connectionSlots)
        {
            if (conn != null)
                connections.Add(conn);
        }
        foreach (var conn in _closingConnections.Values)
            connections.Add(conn);

        if (Teardown != TeardownOutcome.Quiesced)
        {
            Quarantine(connections);
            return;
        }

        ListenerDisposeCleanup.RunIsolatedPerItem(
            connections,
            "connection close",
            RunDisposeCleanupStep,
            static conn => conn.CloseSocketFd());

        // Every request has posted its final completion (DrainKernelOperations), so the kernel references none of
        // the memory released from here on and closing the ring cannot race a request.
        RunDisposeCleanupStep("ring dispose", _ring.Dispose);

        ListenerDisposeCleanup.RunIsolatedPerItem(
            connections,
            "connection force cleanup",
            RunDisposeCleanupStep,
            static conn => conn.ForceCleanupAfterRingClosed());

        Array.Clear(_connectionSlots);
        _closingConnections.Clear();
        // Connections still counted were never released through the normal close path (only reachable here when no
        // IO loop ever ran; a loop returns its residue when it ends).
        ReleaseResidualAccounting();

        RunDisposeCleanupStep("send diagnostics timer dispose", () => _diagTimer?.Dispose());
        RunDisposeCleanupStep("recv diagnostics timer dispose", () => _recvDiagTimer?.Dispose());
        // Like the retry timers below, its callback writes the eventfd, so it must be gone before that closes.
        await RunDisposeCleanupStepAsync(
            "unregister retry timer dispose",
            () => _unregisterRetryTimer.DisposeAsync()).ConfigureAwait(false);

        await ListenerDisposeCleanup.DisposeRetryTimersThenRunEventFdTeardownAsync(
            _acceptRetryTimer,
            _recvRetryTimer,
            RunDisposeCleanupStepAsync,
            EventFdTeardownAsync).ConfigureAwait(false);

        // Normally released on the IO loop during unbind; this covers a loop that never ran or failed.
        ReleaseListenSocket(unregisterFixedFile: false);

        // Every connection is closed (CloseSocketFd and ForceCleanupAfterRingClosed above), as the socket transport
        // has it when it disposes its pool.
        RunDisposeCleanupStep("memory pool dispose", _memoryPool.Dispose);

        RunDisposeCleanupStep("listener cancellation source dispose", _cts.Dispose);

        async ValueTask EventFdTeardownAsync()
        {
            RunDisposeCleanupStep("eventfd read handle dispose", _eventFdReadHandle.Dispose);
            RunDisposeCleanupStep("eventfd write handle dispose", _eventFdWriteHandle.Dispose);
            RunDisposeCleanupStep("socket option handle dispose", _sockOptHandle.Dispose);
            RunDisposeCleanupStep("buffer ring cleanup", () => _bufferRing?.DisposeAfterRingClosed());
            await RunDisposeCleanupStepAsync("eventfd close", CloseEventFdAsync).ConfigureAwait(false);
        }

        ValueTask CloseEventFdAsync()
        {
            var adapter = new EventFdWakeAdapter(this);
            return _eventFdWakeState.CloseAsync(ref adapter);
        }
    }

    /// <summary>
    /// The IO loop did not stop in time: it still owns the ring, its collections, the connections' pipes and their
    /// sockets, so none of them is touched here. Root everything for the rest of the process and only complete the
    /// thread-safe finalization signals (their continuations run asynchronously) so Kestrel's connection disposal
    /// cannot hang.
    /// </summary>
    private void QuarantineWedgedLoop()
    {
        IoUringQuarantine.Add(this, 0, 0);
        _logger.LogError(
            "io_uring listener {EndPoint}: the IO loop did not stop; the listener, its ring and every buffer it may " +
            "still use are quarantined for the rest of the process.",
            EndPoint);

        // Array element reads are safe while the owner runs; MarkFinalized only completes a task.
        foreach (var conn in _connectionSlots)
            conn?.MarkFinalized();

        // The loop cannot serve anyone: stop the kernel from accepting (a multishot accept keeps taking connections)
        // and refuse new clients. Shutdown goes through the socket's reference-counted handle, so it is safe beside
        // the owner; the socket itself stays the owner's to release. (A lent socket is stopped by its lender.)
        StopListening();

        RunDisposeCleanupStep("send diagnostics timer dispose", () => _diagTimer?.Dispose());
        RunDisposeCleanupStep("recv diagnostics timer dispose", () => _recvDiagTimer?.Dispose());
        RunDisposeCleanupStep("accept retry timer dispose", _acceptRetryTimer.Dispose);
        RunDisposeCleanupStep("recv retry timer dispose", _recvRetryTimer.Dispose);
        RunDisposeCleanupStep("unregister retry timer dispose", _unregisterRetryTimer.Dispose);
    }

    /// <summary>
    /// The loop has stopped but could not prove the ring idle (<see cref="DrainKernelOperations"/> timed out or a
    /// completion was lost). Releases only what the kernel cannot be using and parks the listener, and with it the
    /// ring, its mappings, the provided buffers and every connection's receive buffer and send pins, in
    /// <see cref="IoUringQuarantine"/> for the rest of the process.
    /// </summary>
    private void Quarantine(HashSet<IoUringConnection> connections)
    {
        long outstanding = _ring.OutstandingOperations;
        IoUringQuarantine.Add(this, connections.Count, outstanding);
        _logger.LogError(
            "io_uring listener {EndPoint} could not prove its ring idle during shutdown ({Outstanding} request(s) " +
            "outstanding); the ring and the memory of {Connections} connection(s) are quarantined for the rest of " +
            "the process.",
            EndPoint,
            outstanding,
            connections.Count);

        // Nothing else consumes the queue now: close sockets accepted by completions already posted.
        RunDisposeCleanupStep("quarantine completion snapshot drain", () =>
        {
            var result = ListenerStoppedCompletionDrain.Drain(_ring);
            if (result.Failure is not null)
                throw result.Failure;
        });

        // Finalize first: closing a socket cancels ConnectionClosed, whose callbacks run inline.
        ListenerDisposeCleanup.RunIsolatedPerItem(
            connections,
            "connection abandon",
            RunDisposeCleanupStep,
            static conn => conn.AbandonAfterFailedTeardown());

        // A kernel request holds its own reference to the socket, so closing our descriptor is memory-safe.
        ListenerDisposeCleanup.RunIsolatedPerItem(
            connections,
            "connection close",
            RunDisposeCleanupStep,
            static conn => conn.CloseSocketFd());
        ReleaseListenSocket(unregisterFixedFile: false);

        // Normally already returned by the loop when it ended; covers a ring whose loop never ran.
        ReleaseResidualAccounting();

        RunDisposeCleanupStep("send diagnostics timer dispose", () => _diagTimer?.Dispose());
        RunDisposeCleanupStep("recv diagnostics timer dispose", () => _recvDiagTimer?.Dispose());
        RunDisposeCleanupStep("accept retry timer dispose", _acceptRetryTimer.Dispose);
        RunDisposeCleanupStep("recv retry timer dispose", _recvRetryTimer.Dispose);
        RunDisposeCleanupStep("unregister retry timer dispose", _unregisterRetryTimer.Dispose);
    }

    private void RunDisposeCleanupStep(string stepName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync cleanup step '{StepName}' failed; continuing cleanup.", stepName);
        }
    }

    private async ValueTask RunDisposeCleanupStepAsync(string stepName, Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync cleanup step '{StepName}' failed; continuing cleanup.", stepName);
        }
    }

    private readonly struct ProductionCloseFinalizationAdapter(
        IoUringConnectionListener listener,
        IoUringConnection connection)
        : ListenerCloseFinalization.IFinalizationAdapter<ProductionCloseFinalizationAdapter>
    {
        private readonly IoUringConnectionListener _listener = listener;
        private readonly IoUringConnection _connection = connection;

        public static void ShutdownSocket(ref ProductionCloseFinalizationAdapter self) =>
            self._connection.ShutdownSocket();

        public static void RemoveClosingConnection(
            ref ProductionCloseFinalizationAdapter self,
            long connectionId) =>
            self._listener._closingConnections.Remove(connectionId);

        public static void CloseSocketFd(ref ProductionCloseFinalizationAdapter self) =>
            self._connection.CloseSocketFd();

        public static void ReleaseConnection(
            ref ProductionCloseFinalizationAdapter self,
            long connectionId)
        {
            self._listener.RemoveConnection(connectionId);
            // The socket is closed and the slot released: Kestrel's DisposeAsync on this connection may complete.
            self._connection.MarkFinalized();
        }

        public static void LogCloseFinalizationFailure(
            ref ProductionCloseFinalizationAdapter self,
            long connectionId,
            Exception exception) =>
            self._listener._logger.LogWarning(
                exception,
                "Final close cleanup for connection {ConnectionId} failed; continuing teardown.",
                connectionId);
    }
}
