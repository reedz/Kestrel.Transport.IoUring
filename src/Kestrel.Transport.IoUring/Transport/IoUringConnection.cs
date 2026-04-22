using System.Buffers;
using System.Collections.Concurrent;
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
    private readonly CancellationTokenSource _connectionCts = new();
    private readonly Pipe _inputPipe;
    private readonly Pipe _outputPipe;
    private int _disposed;
    private volatile bool _disposing;

    /// <summary>Generation counter — incremented on each connection using this slot.
    /// Encoded into UserData to detect stale CQEs from previous connections.</summary>
    internal ushort Generation { get; set; }

    // Callback to request a RECV resubmission from the IO loop after async flush completes.
    private Action<long>? _requestRecvResubmit;

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
        bool useBufferRing = false)
    {
        _connectionId = connectionId;
        _socketFd = socketFd;
        _fileIndex = fileIndex;
        _ring = ring;
        _logger = logger;
        _receiveBufferSize = receiveBufferSize;
        ConnectionId = $"iouring:{connectionId}";
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
        try { _inputPipe.Writer.Complete(); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Called when a multishot recv CQE completes with data in a provided buffer.
    /// Copies data from the buffer ring into the pipe and flushes.
    /// Returns true if flush completed synchronously (ok to continue receiving).
    /// </summary>
    public bool OnRecvCompleteFromBuffer(ReadOnlySpan<byte> data)
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
                _inputPipe.Writer.Complete();
                return false;
            }
            return true;
        }

        // Async flush (back-pressure). Set flag so the !more path doesn't double-rearm.
        RecvRearmPending = true;
        _ = WaitForFlushThenRequestRecv(flushTask);
        return false;
    }

    // Send state — only accessed from the IO loop thread (via pipe scheduler).
    private MemoryHandle _sendHandle;
    private MemoryHandle _sendZcPendingHandle;
    // Diagnostic shadow state: timestamp + byte count of the current pin (0 when no pin held).
    private long _sendPinStartTs;
    private int _sendPinByteLen;
    internal bool SendZcNotifPending { get; set; }

    /// <summary>
    /// Called by the IO loop when a SEND CQE completes.
    /// </summary>
    internal void CompleteSend(int bytesSent, uint cqeFlags)
    {
        // Check if connection is being disposed from another thread — skip if so.
        if (_disposing) return;

        bool isNotif = (cqeFlags & IoUringConstants.IORING_CQE_F_NOTIF) != 0;
        if (isNotif)
        {
            HasSendInFlight = false;
            _sendZcPendingHandle.Dispose();
            _sendZcPendingHandle = default;
            SendZcNotifPending = false;
            return;
        }
        bool hasMore = (cqeFlags & IoUringConstants.IORING_CQE_F_MORE) != 0;
        if (!hasMore)
        {
            HasSendInFlight = false;
        }
        DisposeSendPin();
        // Signal the send loop to resume — continuation runs inline on IO thread.
        _sendTcs.SetResult(bytesSent);
    }

    /// <summary>
    /// Called on the IO loop thread when a RECV CQE completes.
    /// Copies data from the pre-pinned buffer into the pipe.
    /// Returns true if a new RECV should be immediately resubmitted.
    /// </summary>
    public bool OnRecvComplete(int bytesRead)
    {
        HasRecvInFlight = false;

        if (bytesRead <= 0)
        {
            _inputPipe.Writer.Complete();
            return false;
        }

        // Copy from pre-pinned recv buffer into the pipe.
        var dest = _inputPipe.Writer.GetSpan(bytesRead);
        _pinnedRecvBuf.AsSpan(0, bytesRead).CopyTo(dest);
        _inputPipe.Writer.Advance(bytesRead);
        var flushTask = _inputPipe.Writer.FlushAsync();

        if (flushTask.IsCompleted)
        {
            var flushResult = flushTask.Result;
            if (flushResult.IsCompleted || flushResult.IsCanceled)
            {
                _inputPipe.Writer.Complete();
                return false;
            }
            return true; // Resubmit RECV immediately.
        }

        // Flush is async (back-pressure). Don't block the IO loop — defer recv resubmission.
        _ = WaitForFlushThenRequestRecv(flushTask);
        return false;
    }

    private async Task WaitForFlushThenRequestRecv(ValueTask<FlushResult> flushTask)
    {
        try
        {
            var result = await flushTask.ConfigureAwait(false);
            RecvRearmPending = false;
            if (result.IsCompleted || result.IsCanceled)
            {
                _inputPipe.Writer.Complete();
                return;
            }

            // Request the IO loop to resubmit RECV for this connection.
            _requestRecvResubmit?.Invoke(_connectionId);
        }
        catch (Exception ex)
        {
            RecvRearmPending = false;
            _logger.LogDebug(ex, "Flush failed for connection {Id}", _connectionId);
            _inputPipe.Writer.Complete(ex);
        }
    }

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
    public void StartSendLoop(Action<long> requestRecvResubmit)
    {
        _requestRecvResubmit = requestRecvResubmit;
        _ = RunSendLoopAsync();
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

                if (result.IsCompleted || result.IsCanceled)
                    break;

                var buffer = result.Buffer;
                if (buffer.IsEmpty)
                {
                    reader.AdvanceTo(buffer.End);
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
                        PinSendSlice(slice);
                        int requestedLen = slice.Length;

                        bool submitted = false;
                        try
                        {
                            unsafe
                            {
                                if (!_ring.TryGetSqe(out IoUringSqe* sqe))
                                {
                                    DisposeSendPin();
                                    break; // Ring full — yield and retry next iteration.
                                }
                                sqe->Opcode = IoUringConstants.IORING_OP_SEND;
                                SetSqeFd(sqe);
                                sqe->AddrOrSpliceOffIn = (ulong)_sendHandle.Pointer;
                                sqe->Len = (uint)slice.Length;
                                sqe->OpFlags = 0;
                                sqe->IoPrio = 0;
                                sqe->UserData = EncodeUserData(_connectionId, Generation, OpType.Send);
                                HasSendInFlight = true;
                                submitted = true;
                            }
                        }
                        catch
                        {
                            DisposeSendPin();
                            throw;
                        }

                        if (!submitted) break;

                        // Opt E: do NOT call _ring.Submit() here. The send loop (with Opt B)
                        // runs on the IO thread; the IO loop's next SubmitAndWait at the top
                        // of RunIoLoop will submit this SQE along with any others. Saves one
                        // io_uring_enter syscall per send. Off-IO-thread send schedules go
                        // through IoUringPipeScheduler, which writes to eventfd → the IO loop
                        // wakes from SubmitAndWait → also submits this SQE.

                        // Yield to the IO loop — it will process other connections' recv/send.
                        int sent = await AwaitSendCompletion().ConfigureAwait(false);

                        if (sent <= 0) { aborted = true; break; }
                        if (sent < requestedLen) SendDiagnostics.OnShortSendResubmit();
                        segOffset += sent;
                        totalConsumed += sent;
                    }
                    if (aborted) break;
                    if (segOffset < segment.Length) break; // SQ-full; resume next ReadAsync.
                }

                // Tell the pipe how many bytes we consumed; examined = consumed so the
                // pipe will return immediately if more data is buffered.
                var consumedPos = buffer.GetPosition(totalConsumed, buffer.Start);
                reader.AdvanceTo(consumedPos, consumedPos);
                if (aborted) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unhandled error in send loop for connection {Id}", _connectionId);
        }
        finally
        {
            try { reader.Complete(); } catch (InvalidOperationException) { }
        }
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _connectionCts.Cancel();
        _inputPipe.Writer.Complete(abortReason);
        _outputPipe.Writer.Complete(abortReason);
    }

    public override unsafe ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return ValueTask.CompletedTask;

        _disposing = true;
        _connectionCts.Cancel();
        // Complete both sides of both pipes. By the time Kestrel calls DisposeAsync,
        // it has finished reading from the input pipe.
        try { _inputPipe.Reader.Complete(); } catch (InvalidOperationException) { }
        try { _inputPipe.Writer.Complete(); } catch (InvalidOperationException) { }
        try { _outputPipe.Writer.Complete(); } catch (InvalidOperationException) { }

        // Always clean up the send handle — covers the case where Pin() was called
        // but HasSendInFlight wasn't set yet (abort between Pin and SQE submit).
        if (HasSendInFlight || _sendHandle.Pointer != null)
        {
            // Diagnostic: surface aborts that race with an in-flight send. If this fires,
            // the kernel may still touch the buffer after we've freed it (use-after-free).
            // The current per-send Pin() approach is correct only because MemoryHandle
            // back-references the GC pin to the MemoryPool block, which keeps the block
            // alive until Dispose. Once Opt G lands, slot lifecycle will be tied to CQE
            // arrival instead of to the connection — this counter validates that.
            if (HasSendInFlight) SendDiagnostics.OnSendAbortWithPinned();

            HasSendInFlight = false;
            DisposeSendPin();
        }

        if (SendZcNotifPending) { _sendZcPendingHandle.Dispose(); SendZcNotifPending = false; }

        // Don't dispose _connectionCts — Kestrel may still read ConnectionClosed token
        // after DisposeAsync returns. The CTS is collected by the GC.

        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the socket fd. Called by the listener after in-flight ops are drained.</summary>
    internal void CloseSocketFd()
    {
        if (_fileIndex >= 0)
            _ring.UnregisterFd(_fileIndex);
        if (Libc.close(_socketFd) < 0)
            _logger.LogWarning("close(fd={Fd}) failed with errno {Errno}", _socketFd, Marshal.GetLastPInvokeError());
    }

    /// <summary>Disposes any pinned recv buffer still held (e.g. during forced shutdown).</summary>
    internal void CleanupRecvHandle()
    {
        _recvHandle.Dispose();
        _recvHandle = default;
    }

    /// <summary>Disposes any pinned send handle (for CQ overflow recovery).</summary>
    internal void CleanupSendHandle()
    {
        DisposeSendPin();
    }

    /// <summary>
    /// Signals the send loop with an error result after CQ overflow recovery.
    /// The send loop will see sent=-1 and break out of its loop.
    /// </summary>
    internal void CompleteSendOverflowRecovery()
    {
        try { _sendTcs.SetResult(-1); } catch { }
    }

    private sealed class DuplexPipe(PipeReader reader, PipeWriter writer) : IDuplexPipe
    {
        public PipeReader Input { get; } = reader;
        public PipeWriter Output { get; } = writer;
    }
}
