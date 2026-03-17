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
    private readonly ConcurrentDictionary<int, IoUringConnection> _connections = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _maxConnections;
    private readonly int _receiveBufferSize;

    // Sends are enqueued from background output-drain tasks and dequeued on the IO-loop thread.
    private readonly ConcurrentQueue<PendingSend> _pendingSendQueue = new();

    // Per-connection in-flight send tracking — only accessed from the IO loop thread.
    private readonly Dictionary<int, PendingSend> _inFlightSends = [];

    // eventfd used to wake the IO loop immediately when a send is enqueued.
    // The drain task writes to it; the IO loop has a persistent IORING_OP_READ SQE on it.
    private readonly int _eventFd;
    // Pinned read buffer for the eventfd READ SQE (8 bytes per Linux eventfd spec).
    private readonly ulong[] _eventFdReadBuf;
    private readonly MemoryHandle _eventFdReadHandle;
    // Pinned write buffer for waking the eventfd (value = 1).
    private readonly ulong[] _eventFdWriteBuf;
    private readonly MemoryHandle _eventFdWriteHandle;

    private int _nextConnectionId;
    private Task? _ioLoopTask;
    private int _listenSocketFd;

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
        _eventFdWriteBuf[0] = 1UL; // increment eventfd counter by 1 on each write
        _eventFdWriteHandle = _eventFdWriteBuf.AsMemory().Pin();
    }

    public void Bind(int listenBacklog)
    {
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenSocket.Bind(EndPoint);
        _listenSocket.Listen(listenBacklog);

        _listenSocketFd = GetSocketFd(_listenSocket);
        SubmitAccept();
        SubmitEventFdRead(); // arm the persistent eventfd wakeup
        _ring.Submit();
        _ioLoopTask = Task.Run(RunIoLoopAsync);
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
    }

    /// <summary>Submits a READ SQE on the eventfd. When output-drain tasks write to the eventfd this completes.</summary>
    private unsafe void SubmitEventFdRead()
    {
        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_READ;
            sqe->Fd = _eventFd;
            sqe->AddrOrSpliceOffIn = (ulong)(nint)Unsafe.AsPointer(ref _eventFdReadBuf[0]);
            sqe->Len = sizeof(ulong); // eventfd always produces exactly 8 bytes
            sqe->UserData = IoUringConstants.EVENTFD_USER_DATA;
        }
    }

    /// <summary>
    /// Wakes the IO loop immediately by writing to the eventfd.
    /// Called from output-drain task threads (concurrent with IO loop).
    /// </summary>
    private unsafe void WakeIoLoop()
    {
        // Non-blocking write: increments eventfd counter by 1. The IO loop's READ SQE fires.
        Libc.write(_eventFd, Unsafe.AsPointer(ref _eventFdWriteBuf[0]), sizeof(ulong));
    }

    /// <summary>Enqueues a pending send and wakes the IO loop.</summary>
    private void EnqueueSend(PendingSend pending)
    {
        _pendingSendQueue.Enqueue(pending);
        WakeIoLoop();
    }

    private async Task RunIoLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // SubmitAndWait(1) blocks until at least one CQE arrives
                // (the eventfd READ fires as soon as a drain task enqueues a send).
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
                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Submits SEND SQEs for connections that have data and no in-flight send.</summary>
    private void DrainPendingSendQueue()
    {
        while (_pendingSendQueue.TryDequeue(out var pending))
        {
            // Only one in-flight SEND per connection at a time.
            if (_inFlightSends.TryGetValue(pending.ConnectionId, out _))
            {
                // Re-queue and stop — will be retried after current send completes.
                _pendingSendQueue.Enqueue(pending);
                break;
            }

            if (_connections.TryGetValue(pending.ConnectionId, out var conn))
            {
                conn.SubmitSend(in pending);
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

    private void ProcessCompletions()
    {
        bool submittedSends = false;

        while (_ring.TryPeekCompletion(out var cqe))
        {
            _ring.AdvanceCompletion();

            if (cqe.UserData == IoUringConstants.EVENTFD_USER_DATA)
            {
                // Eventfd read fired — drain queued sends and re-arm.
                DrainPendingSendQueue();
                SubmitEventFdRead();
                submittedSends = true;
                continue;
            }

            var (connectionId, opType) = IoUringConnection.DecodeUserData(cqe.UserData);

            switch (opType)
            {
                case IoUringConnection.OpType.Accept:
                    HandleAccept(cqe.Res);
                    break;
                case IoUringConnection.OpType.Recv:
                    HandleRecv(connectionId, cqe.Res);
                    break;
                case IoUringConnection.OpType.Send:
                    HandleSend(connectionId, cqe.Res);
                    break;
                case IoUringConnection.OpType.Close:
                    HandleClose(connectionId);
                    break;
            }
        }

        if (submittedSends)
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
                int connId = Interlocked.Increment(ref _nextConnectionId);
                var conn = new IoUringConnection(
                    connId,
                    socketFd,
                    _ring,
                    remoteEndPoint: null,
                    localEndPoint: EndPoint,
                    _receiveBufferSize,
                    _logger);

                _connections[connId] = conn;
                conn.StartOutputDrain(EnqueueSend);
                conn.SubmitRecv();
                _acceptChannel.Writer.TryWrite(conn);
            }
        }

        if (!_cts.IsCancellationRequested)
        {
            SubmitAccept();
            _ring.Submit();
        }
    }

    private void HandleRecv(int connectionId, int result)
    {
        if (_connections.TryGetValue(connectionId, out var conn))
        {
            conn.OnRecvComplete(result);
            if (result > 0)
            {
                conn.SubmitRecv();
                _ring.Submit();
            }
            else
            {
                _connections.TryRemove(connectionId, out _);
            }
        }
    }

    private void HandleSend(int connectionId, int result)
    {
        _inFlightSends.Remove(connectionId);

        if (_connections.TryGetValue(connectionId, out var conn))
            conn.OnSendComplete(result);

        // Immediately try to submit the next queued send for this connection.
        if (_pendingSendQueue.Count > 0)
        {
            DrainPendingSendQueue();
            _ring.Submit();
        }
    }

    private void HandleClose(int connectionId)
    {
        _connections.TryRemove(connectionId, out _);
        _inFlightSends.Remove(connectionId);
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
        // Wake the IO loop so it sees the cancellation and exits.
        WakeIoLoop();
        _acceptChannel.Writer.TryComplete();

        if (_ioLoopTask != null)
        {
            try
            {
                await _ioLoopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("IO loop did not complete gracefully within the timeout period.");
            }
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

        foreach (var conn in _connections.Values)
            await conn.DisposeAsync().ConfigureAwait(false);
        _connections.Clear();

        _eventFdReadHandle.Dispose();
        _eventFdWriteHandle.Dispose();
        Libc.close(_eventFd);
        _listenSocket.Dispose();
        _ring.Dispose();
        _cts.Dispose();
    }
}
