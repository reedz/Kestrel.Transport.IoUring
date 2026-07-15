using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Kestrel.Transport.IoUring.Diagnostics;
using Kestrel.Transport.IoUring.Native;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Kestrel.Transport.IoUring.Transport;



internal sealed class IoUringConnection : ConnectionContext, IValueTaskSource<int>
{
    private const ulong OpTypeMask = 0xFF;
    private const ulong GenerationMask = 0xFFFF;
    private const int GenerationShift = 8;
    private const int ConnectionIdShift = 24;

    public enum OpType : byte { Accept = 0, Recv = 1, Send = 2, Close = 3, Cancel = 4 }

    public static ulong EncodeUserData(long connectionId, ushort generation, OpType opType) =>
        ((ulong)connectionId << ConnectionIdShift) | ((ulong)generation << GenerationShift) | (byte)opType;

    public static (long ConnectionId, ushort Generation, OpType OpType) DecodeUserData(ulong userData) =>
        ((long)(userData >> ConnectionIdShift),
         (ushort)((userData >> GenerationShift) & GenerationMask),
         (OpType)(userData & OpTypeMask));

    private readonly long _connectionId;
    private readonly int _socketFd;
    private readonly int _fileIndex; // registered file index, or -1
    private readonly Ring _ring;
    private readonly ILogger _logger;
    private readonly int _receiveBufferSize;
    private readonly ReceiveBufferBudget _receiveBufferBudget;
    private readonly CancellationTokenSource _connectionCts = new();
    private readonly Pipe _inputPipe;
    private readonly Pipe _outputPipe;
    private int _disposed;
    private int _closeRequested;
    private int _abortRequested;
    private int _socketShutdown;
    private int _socketClosed;
    private int _socketCloseCount;
    private int _sendLoopStarted;
    private int _sendLoopCompleted;
    private Exception? _closeError;

    /// <summary>Generation counter — incremented on each connection using this slot.
    /// Encoded into UserData to detect stale CQEs from previous connections.</summary>
    internal ushort Generation { get; set; }

    // Callback to request a RECV resubmission from the IO loop after async flush completes.
    private Action<long, ushort>? _requestRecvResubmit;
    private Action<long, ushort>? _requestClose;
    private Action<long, ushort>? _notifySendLoopCompleted;

    // Zero-alloc send completion: connection itself is the IValueTaskSource.
    // Send loop awaits this; IO loop sets result on CQE → send loop resumes inline.
    private ManualResetValueTaskSourceCore<int> _sendTcs;

    // IValueTaskSource<int> implementation — used by send loop to await send CQE.
    int IValueTaskSource<int>.GetResult(short token) => _sendTcs.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _sendTcs.GetStatus(token);
    void IValueTaskSource<int>.OnCompleted(Action<object?> c, object? s, short t, ValueTaskSourceOnCompletedFlags f) =>
        _sendTcs.OnCompleted(c, s, t, f);

    private ValueTask<int> AwaitSendCompletion()
    {
        _sendTcs.Reset();
        return new ValueTask<int>(this, _sendTcs.Version);
    }

    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    public override IDuplexPipe Transport { get; set; }

    public int SocketFd => _socketFd;
    public long NumericConnectionId => _connectionId;

    /// <summary>Tracks whether a RECV SQE is currently in-flight for this connection.</summary>
    internal bool HasRecvInFlight { get; set; }

    /// <summary>Tracks whether a SEND SQE is currently in-flight for this connection.</summary>
    internal bool HasSendInFlight { get; set; }

    /// <summary>Set when the connection is shutting down (recv returned ≤0 or abort called).</summary>
    internal bool IsClosing { get; set; }

    /// <summary>True when using multishot recv with buffer ring (no _recvHandle to manage).</summary>
    internal bool UsingMultishotRecv { get; set; }

    /// <summary>True when an async flush is pending and will trigger a recv rearm on completion.</summary>
    internal bool RecvRearmPending { get; set; }
    internal bool SendLoopCompleted =>
        Volatile.Read(ref _sendLoopStarted) == 0 ||
        Volatile.Read(ref _sendLoopCompleted) != 0;
    internal bool AbortRequested => Volatile.Read(ref _abortRequested) != 0;

    internal const int MaxPendingRecvBytes = 256 * 1024;

    internal enum RecvWriteResult
    {
        Ready,
        Pending,
        InputCompleted,
        Closed,
    }

    private readonly Queue<byte[]> _pendingRecvBuffers = new();
    private int _pendingRecvBytes;
    private FlushResult _completedRecvFlushResult;
    private Exception? _completedRecvFlushException;
    private int _recvFlushCompletionState;
    private bool _recvEnded;

    internal int PendingRecvBytes => _pendingRecvBytes;
    internal int SocketCloseCount => Volatile.Read(ref _socketCloseCount);

    // Pre-pinned recv buffer for the SINGLE-SHOT path. Null when the connection was
    // constructed for the buffer-ring (multishot) path — in that case the kernel
    // selects buffers from the provided buffer ring and we never copy via this array.
    // S1: skipping this 4 KB POH allocation when buffer ring is active saves
    // ~ReceiveBufferSize × MaxConnections × ThreadCount of pinned heap.
    private readonly byte[]? _pinnedRecvBuf;
    private readonly unsafe byte* _pinnedRecvPtr;

    public IoUringConnection(
        long connectionId,
        int socketFd,
        int fileIndex,
        Ring ring,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        int receiveBufferSize,
        IoUringPipeScheduler transportScheduler,
        bool unsafeInlineScheduling,
        ILogger logger,
        bool useBufferRing = false,
        string? publicConnectionId = null,
        ReceiveBufferBudget? receiveBufferBudget = null)
    {
        _connectionId = connectionId;
        _socketFd = socketFd;
        _fileIndex = fileIndex;
        _ring = ring;
        _logger = logger;
        _receiveBufferSize = receiveBufferSize;
        _receiveBufferBudget = receiveBufferBudget ?? new ReceiveBufferBudget(MaxPendingRecvBytes);
        ConnectionId = publicConnectionId ?? $"iouring:{connectionId}";
        RemoteEndPoint = remoteEndPoint;
        LocalEndPoint = localEndPoint;

        // Pre-pin recv buffer to avoid Pin()/Dispose() on every recv. Skip when the
        // listener will use multishot+buffer-ring for this connection (S1).
        if (!useBufferRing)
        {
            _pinnedRecvBuf = GC.AllocateArray<byte>(receiveBufferSize, pinned: true);
            unsafe { fixed (byte* p = _pinnedRecvBuf) _pinnedRecvPtr = p; }
        }
        else
        {
            _pinnedRecvBuf = null;
            unsafe { _pinnedRecvPtr = null; }
        }

        // When UnsafeInlineScheduling is true, Kestrel HTTP processing runs inline on
        // the IO thread (Seastar model). When false, it runs on the ThreadPool (safer).
        var appReadScheduler = unsafeInlineScheduling
            ? (PipeScheduler)transportScheduler
            : PipeScheduler.ThreadPool;

        var inputOptions = new PipeOptions(
            writerScheduler: transportScheduler,
            readerScheduler: appReadScheduler,
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 512 * 1024,
            useSynchronizationContext: false);
        var outputOptions = new PipeOptions(
            writerScheduler: PipeScheduler.ThreadPool,
            readerScheduler: transportScheduler,
            pauseWriterThreshold: 64 * 1024,
            resumeWriterThreshold: 32 * 1024,
            useSynchronizationContext: false);
        _inputPipe = new Pipe(inputOptions);
        _outputPipe = new Pipe(outputOptions);

        Transport = new DuplexPipe(_inputPipe.Reader, _outputPipe.Writer);
        Application = new DuplexPipe(_outputPipe.Reader, _inputPipe.Writer);
    }

    public IDuplexPipe Application { get; }

    public override CancellationToken ConnectionClosed => _connectionCts.Token;

    // _recvHandle is only accessed from the single IO loop thread — no lock needed.
    private MemoryHandle _recvHandle;

    /// <summary>Sets the fd on an SQE, using fixed-file index if registered.</summary>
    private unsafe void SetSqeFd(IoUringSqe* sqe)
    {
        sqe->Flags = 0; // ensure clean slate — SQEs are reused; |= below would carry stale bits
        if (_fileIndex >= 0)
        {
            sqe->Fd = _fileIndex;
            sqe->Flags |= IoUringConstants.IOSQE_FIXED_FILE;
        }
        else
        {
            sqe->Fd = _socketFd;
        }
    }

    /// <summary>
    /// Submits a RECV SQE using the pre-pinned buffer. Returns false if the SQ is full.
    /// </summary>
    public unsafe bool SubmitRecv()
    {
        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_RECV;
            SetSqeFd(sqe);
            sqe->AddrOrSpliceOffIn = (ulong)_pinnedRecvPtr;
            sqe->Len = (uint)_receiveBufferSize;
            sqe->OpFlags = 0;
            sqe->IoPrio = 0;
            sqe->UserData = EncodeUserData(_connectionId, Generation, OpType.Recv);
            HasRecvInFlight = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Submits a multishot RECV SQE with buffer selection from a provided buffer ring.
    /// The kernel will select buffers from the specified group and generate multiple CQEs.
    /// No per-recv Pin() needed — the buffer ring owns the memory.
    /// Returns true if the SQE was queued; false if the SQ was full (caller should retry).
    /// </summary>
    public unsafe bool SubmitMultishotRecv(ushort bufferGroupId)
    {
        if (!_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            // S0.2: previously this returned silently, leaving the connection without
            // an armed recv (it would never receive again). Caller must add to the
            // retry set so the IO loop reattempts on the next iteration.
            return false;
        }

        sqe->Opcode = IoUringConstants.IORING_OP_RECV;
        SetSqeFd(sqe);
        sqe->AddrOrSpliceOffIn = 0; // kernel selects buffer
        sqe->Len = 0;              // kernel determines length from buffer ring
        sqe->OpFlags = 0; // recv_flags (MSG_xxx) — none
        sqe->IoPrio = (ushort)IoUringConstants.IORING_RECV_MULTISHOT; // multishot is set in ioprio, NOT op_flags
        // Use |= to preserve any flags already set by SetSqeFd (e.g. IOSQE_FIXED_FILE).
        // Regression of commit 2cd6067 — overwriting flags here caused recv to read from
        // wrong fd when fixed-file table is registered, producing immediate -ENOTSOCK
        // (or similar) and causing every connection to close right after accept.
        sqe->Flags |= IoUringConstants.IOSQE_BUFFER_SELECT;
        sqe->BufIndexOrGroup = bufferGroupId;
        sqe->UserData = EncodeUserData(_connectionId, Generation, OpType.Recv);
        HasRecvInFlight = true;
        UsingMultishotRecv = true;
        return true;
    }

    /// <summary>Completes the input pipe writer (called by listener on recv close).</summary>
    internal void CompleteInputWriter()
    {
        try { _inputPipe.Writer.Complete(_closeError); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Called when a multishot recv CQE completes with data in a provided buffer.
    /// Copies data from the buffer ring into the pipe and flushes.
    /// Returns whether receiving can continue, must wait for back-pressure, or must close.
    /// </summary>
    internal RecvWriteResult OnRecvCompleteFromBuffer(ReadOnlySpan<byte> data)
    {
        if (RecvRearmPending)
        {
            if (data.Length > MaxPendingRecvBytes - _pendingRecvBytes)
            {
                var error = new ConnectionAbortedException(
                    $"Pending receive data exceeded the {MaxPendingRecvBytes}-byte limit.");
                FailReceive(error);
                return RecvWriteResult.Closed;
            }

            if (!_receiveBufferBudget.TryReserve(data.Length))
            {
                var error = new ConnectionAbortedException(
                    "The ring-wide pending receive budget was exhausted.");
                FailReceive(error);
                return RecvWriteResult.Closed;
            }

            byte[] copy;
            try
            {
                copy = data.ToArray();
            }
            catch
            {
                _receiveBufferBudget.Release(data.Length);
                throw;
            }
            _pendingRecvBuffers.Enqueue(copy);
            _pendingRecvBytes += copy.Length;
            return RecvWriteResult.Pending;
        }

        return WriteAndFlush(data);
    }

    // Send state — only accessed from the IO loop thread (via pipe scheduler).
    private MemoryHandle _sendHandle;
    // Diagnostic shadow state: timestamp + byte count of the current pin (0 when no pin held).
    private long _sendPinStartTs;
    private int _sendPinByteLen;

    /// <summary>
    /// Called by the IO loop when a SEND CQE completes.
    /// </summary>
    internal void CompleteSend(int bytesSent, uint cqeFlags)
    {
        bool isNotif = (cqeFlags & IoUringConstants.IORING_CQE_F_NOTIF) != 0;
        if (isNotif)
            return;

        HasSendInFlight = false;
        DisposeSendPin();

        // Signal the send loop to resume — continuation runs inline on IO thread.
        _sendTcs.SetResult(bytesSent);
    }

    /// <summary>
    /// Called on the IO loop thread when a RECV CQE completes.
    /// Copies data from the pre-pinned buffer into the pipe.
    /// Returns whether the listener should rearm, wait for a flush, or close.
    /// </summary>
    internal RecvWriteResult OnRecvComplete(int bytesRead)
    {
        HasRecvInFlight = false;

        if (bytesRead <= 0)
            return OnRecvEnd();

        return WriteAndFlush(_pinnedRecvBuf!.AsSpan(0, bytesRead));
    }

    internal RecvWriteResult OnRecvEnd()
    {
        _recvEnded = true;
        if (RecvRearmPending)
            return RecvWriteResult.Pending;

        CompleteReceive();
        return RecvWriteResult.InputCompleted;
    }

    internal RecvWriteResult ResumeRecvAfterFlush()
    {
        if (!RecvRearmPending)
            return RecvWriteResult.Ready;

        int completionState = Volatile.Read(ref _recvFlushCompletionState);
        if (completionState == 0)
            return RecvWriteResult.Pending;

        RecvRearmPending = false;
        Volatile.Write(ref _recvFlushCompletionState, 0);

        if (completionState == 2)
        {
            var error = _completedRecvFlushException!;
            _completedRecvFlushException = null;
            FailReceive(error);
            return RecvWriteResult.Closed;
        }

        var flushResult = _completedRecvFlushResult;
        if (flushResult.IsCompleted || flushResult.IsCanceled)
        {
            CompleteReceive();
            RequestClose();
            return RecvWriteResult.Closed;
        }

        while (_pendingRecvBuffers.TryDequeue(out var data))
        {
            _pendingRecvBytes -= data.Length;
            _receiveBufferBudget.Release(data.Length);
            var result = WriteAndFlush(data);
            if (result != RecvWriteResult.Ready)
                return result;
        }

        if (_recvEnded)
        {
            CompleteReceive();
            return RecvWriteResult.InputCompleted;
        }

        return RecvWriteResult.Ready;
    }

    private RecvWriteResult WriteAndFlush(ReadOnlySpan<byte> data)
    {
        try
        {
            var dest = _inputPipe.Writer.GetSpan(data.Length);
            data.CopyTo(dest);
            _inputPipe.Writer.Advance(data.Length);

            var flushTask = _inputPipe.Writer.FlushAsync();
            if (flushTask.IsCompleted)
            {
                var flushResult = flushTask.Result;
                if (flushResult.IsCompleted || flushResult.IsCanceled)
                {
                    CompleteReceive();
                    RequestClose();
                    return RecvWriteResult.Closed;
                }
                return RecvWriteResult.Ready;
            }

            RecvRearmPending = true;
            _ = WaitForFlushThenRequestRecv(flushTask, Generation);
            return RecvWriteResult.Pending;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            FailReceive(ex);
            return RecvWriteResult.Closed;
        }
    }

    private async Task WaitForFlushThenRequestRecv(ValueTask<FlushResult> flushTask, ushort generation)
    {
        try
        {
            _completedRecvFlushResult = await flushTask.ConfigureAwait(false);
            Volatile.Write(ref _recvFlushCompletionState, 1);
        }
        catch (Exception ex)
        {
            _completedRecvFlushException = ex;
            Volatile.Write(ref _recvFlushCompletionState, 2);
        }

        _requestRecvResubmit?.Invoke(_connectionId, generation);
    }

    private void CompleteReceive()
    {
        ClearPendingReceiveBuffers();
        try { _inputPipe.Writer.Complete(); } catch (InvalidOperationException) { }
    }

    private void FailReceive(Exception error)
    {
        ClearPendingReceiveBuffers();
        _logger.LogDebug(error, "Receive failed for connection {Id}", _connectionId);
        try { _inputPipe.Writer.Complete(error); } catch (InvalidOperationException) { }
        RequestClose(abortive: true);
    }

    private void ClearPendingReceiveBuffers()
    {
        _pendingRecvBuffers.Clear();
        if (_pendingRecvBytes > 0)
        {
            _receiveBufferBudget.Release(_pendingRecvBytes);
            _pendingRecvBytes = 0;
        }
    }

    internal void ReleasePendingReceiveBuffers() => ClearPendingReceiveBuffers();

    /// <summary>Pins a slice for SEND and records diagnostic counters. IO-loop thread only.</summary>
    private void PinSendSlice(ReadOnlyMemory<byte> slice)
    {
        _sendHandle = slice.Pin();
        _sendPinByteLen = slice.Length;
        _sendPinStartTs = SendDiagnostics.OnPinStart(slice.Length);
    }

    /// <summary>Disposes the current SEND pin (if any) and records diagnostic counters.</summary>
    private unsafe void DisposeSendPin()
    {
        if (_sendHandle.Pointer != null)
        {
            _sendHandle.Dispose();
            SendDiagnostics.OnPinDispose(_sendPinStartTs, _sendPinByteLen);
        }
        _sendHandle = default;
        _sendPinStartTs = 0;
        _sendPinByteLen = 0;
    }

    /// <summary>
    /// Starts the send loop. Continuations run on the IO loop thread via IoUringPipeScheduler.
    /// No drain task thread — the output pipe's readerScheduler routes continuations to the IO loop.
    /// </summary>
    public void StartSendLoop(
        Action<long, ushort> requestRecvResubmit,
        Action<long, ushort> requestClose,
        Action<long, ushort>? notifySendLoopCompleted = null)
    {
        _requestRecvResubmit = requestRecvResubmit;
        _requestClose = requestClose;
        _notifySendLoopCompleted = notifySendLoopCompleted;
        Volatile.Write(ref _sendLoopStarted, 1);
        _ = RunSendLoopAsync();
    }

    internal unsafe void SubmitSend(ReadOnlyMemory<byte> slice)
    {
        PinSendSlice(slice);
        try
        {
            if (!_ring.TryGetSqe(out IoUringSqe* sqe))
            {
                DisposeSendPin();
                _ring.Submit();
                PinSendSlice(slice);
                if (!_ring.TryGetSqe(out sqe))
                    throw new InvalidOperationException("Submission queue remained full after an immediate submit.");
            }

            sqe->Opcode = IoUringConstants.IORING_OP_SEND;
            SetSqeFd(sqe);
            sqe->AddrOrSpliceOffIn = (ulong)_sendHandle.Pointer;
            sqe->Len = (uint)slice.Length;
            sqe->OpFlags = 0;
            sqe->IoPrio = 0;
            sqe->UserData = EncodeUserData(_connectionId, Generation, OpType.Send);
            HasSendInFlight = true;
        }
        catch
        {
            if (!HasSendInFlight)
                DisposeSendPin();
            throw;
        }
    }

    /// <summary>
    /// Reads from the output pipe and submits SEND SQEs.
    /// Continuations run on the IO loop thread via IoUringPipeScheduler.
    /// Non-blocking: yields to IO loop after SQE submit, resumes on CQE.
    /// </summary>
    private async Task RunSendLoopAsync()
    {
        var reader = _outputPipe.Reader;
        var ct = _connectionCts.Token;
        try
        {
            while (true)
            {
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (result.IsCanceled)
                    break;

                var buffer = result.Buffer;
                if (buffer.IsEmpty)
                {
                    reader.AdvanceTo(buffer.End);
                    if (result.IsCompleted)
                        break;
                    continue;
                }

                // Send across all segments of the buffer (multi-segment safe).
                // Bug history: previously only buffer.First was sent and the rest
                // was silently dropped via AdvanceTo(examined: buffer.End).
                long totalConsumed = 0;
                bool aborted = false;
                foreach (var segment in buffer)
                {
                    int segOffset = 0;
                    while (segOffset < segment.Length)
                    {
                        var slice = segment.Slice(segOffset);
                        int requestedLen = slice.Length;
                        SubmitSend(slice);

                        // Opt E: do NOT call _ring.Submit() here. The send loop (with Opt B)
                        // runs on the IO thread; the IO loop's next SubmitAndWait at the top
                        // of RunIoLoop will submit this SQE along with any others. Saves one
                        // io_uring_enter syscall per send. Off-IO-thread send schedules go
                        // through IoUringPipeScheduler, which writes to eventfd → the IO loop
                        // wakes from SubmitAndWait → also submits this SQE.

                        // Yield to the IO loop — it will process other connections' recv/send.
                        int sent = await AwaitSendCompletion().ConfigureAwait(false);

                        if (sent <= 0)
                        {
                            aborted = true;
                            RequestClose(abortive: true);
                            break;
                        }
                        if (sent < requestedLen) SendDiagnostics.OnShortSendResubmit();
                        segOffset += sent;
                        totalConsumed += sent;
                    }
                    if (aborted) break;
                }

                // Tell the pipe how many bytes we consumed; examined = consumed so the
                // pipe will return immediately if more data is buffered.
                var consumedPos = buffer.GetPosition(totalConsumed, buffer.Start);
                reader.AdvanceTo(consumedPos, consumedPos);
                if (aborted || result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unhandled error in send loop for connection {Id}", _connectionId);
            RequestClose(abortive: true);
        }
        finally
        {
            try { reader.Complete(); } catch (InvalidOperationException) { }
            Volatile.Write(ref _sendLoopCompleted, 1);
            try
            {
                _connectionCts.Cancel();
            }
            catch (AggregateException ex)
            {
                _logger.LogError(ex, "ConnectionClosed callbacks failed for connection {Id}", _connectionId);
            }
            _notifySendLoopCompleted?.Invoke(_connectionId, Generation);
            _logger.LogDebug(
                "Send loop completed for connection {Id}; abortive={Abortive}.",
                _connectionId,
                AbortRequested);
        }
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _logger.LogDebug("Abort requested for connection {Id}: {Reason}", _connectionId, abortReason.Message);
        _connectionCts.Cancel();
        Interlocked.CompareExchange(ref _closeError, abortReason, null);
        try { _outputPipe.Writer.Complete(abortReason); } catch (InvalidOperationException) { }
        RequestClose(abortive: true);
    }

    public override ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return ValueTask.CompletedTask;

        _logger.LogDebug("Graceful dispose requested for connection {Id}.", _connectionId);
        // Complete the application-owned pipe ends. The listener completes the input
        // writer on the IO thread so it cannot race native receive completions.
        try { _inputPipe.Reader.Complete(); } catch (InvalidOperationException) { }
        try { _outputPipe.Writer.Complete(); } catch (InvalidOperationException) { }

        if (HasSendInFlight) SendDiagnostics.OnSendAbortWithPinned();
        RequestClose();

        // Don't dispose _connectionCts — Kestrel may still read ConnectionClosed token
        // after DisposeAsync returns. The CTS is collected by the GC.

        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the socket fd. Called by the listener after in-flight ops are drained.</summary>
    internal void CloseSocketFd()
    {
        if (Interlocked.Exchange(ref _socketClosed, 1) != 0)
            return;

        Interlocked.Increment(ref _socketCloseCount);
        if (_fileIndex >= 0 && !_ring.UnregisterFd(_fileIndex))
        {
            _logger.LogCritical(
                "Failed to remove fixed-file slot {Slot} for fd {Fd}; fixed-file registration is disabled for this ring. Errno: {Errno}",
                _fileIndex,
                _socketFd,
                Marshal.GetLastPInvokeError());
        }
        if (Libc.close(_socketFd) < 0)
            _logger.LogWarning("close(fd={Fd}) failed with errno {Errno}", _socketFd, Marshal.GetLastPInvokeError());
    }

    internal void ShutdownSocket()
    {
        if (Interlocked.Exchange(ref _socketShutdown, 1) != 0 ||
            Volatile.Read(ref _socketClosed) != 0)
        {
            return;
        }

        if (Libc.shutdown(_socketFd, IoUringConstants.SHUT_RDWR) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno != IoUringConstants.ENOTCONN && errno != IoUringConstants.EBADF)
                _logger.LogWarning("shutdown(fd={Fd}) failed with errno {Errno}", _socketFd, errno);
        }
    }

    /// <summary>Disposes any pinned recv buffer still held (e.g. during forced shutdown).</summary>
    internal void CleanupRecvHandle()
    {
        _recvHandle.Dispose();
        _recvHandle = default;
    }

    /// <summary>Releases kernel-owned pins after the ring has been shut down.</summary>
    internal void CleanupAfterRingShutdown()
    {
        HasRecvInFlight = false;
        HasSendInFlight = false;
        ClearPendingReceiveBuffers();
        DisposeSendPin();
        CompleteSendOverflowRecovery();
    }

    /// <summary>
    /// Signals the send loop with an error result after CQ overflow recovery.
    /// The send loop will see sent=-1 and break out of its loop.
    /// </summary>
    internal void CompleteSendOverflowRecovery()
    {
        try { _sendTcs.SetResult(-1); } catch { }
    }

    private void RequestClose(bool abortive = false)
    {
        bool becameAbortive =
            abortive && Interlocked.Exchange(ref _abortRequested, 1) == 0;
        bool firstRequest = Interlocked.Exchange(ref _closeRequested, 1) == 0;
        if (firstRequest || becameAbortive)
            _requestClose?.Invoke(_connectionId, Generation);
    }

    private sealed class DuplexPipe(PipeReader reader, PipeWriter writer) : IDuplexPipe
    {
        public PipeReader Input { get; } = reader;
        public PipeWriter Output { get; } = writer;
    }
}
