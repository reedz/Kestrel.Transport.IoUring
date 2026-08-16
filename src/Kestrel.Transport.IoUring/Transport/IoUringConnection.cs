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
    internal enum RecvOutcome { Continue, Deferred, Closed }

    private const ulong OpTypeMask = 0xFF;
    private const ulong GenerationMask = 0xFFFF;
    private const int GenerationShift = 8;
    private const int ConnectionIdShift = 24;

    public enum OpType : byte { Accept = 0, Recv = 1, Send = 2, Cancel = 3 }

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

    /// <summary>Generation counter — incremented on each connection using this slot.
    /// Encoded into UserData to detect stale CQEs from previous connections.</summary>
    internal ushort Generation { get; set; }

    // Callback to request a RECV resubmission from the IO loop after async flush completes.
    private Action<long, ushort>? _requestRecvResubmit;
    private Action<long, ushort>? _requestSendRetry;
    private Action<long, ushort, ConnectionAbortedException?>? _requestClose;
    private TaskCompletionSource? _sendRetryCompletion;
    private int _closeRequested;
    private int _abortRequested;
    private int _socketClosed;
    private int _transportAborted;
    private int _socketShutdown;

    // Zero-alloc send completion: connection itself is the IValueTaskSource.
    // Send loop awaits this; IO loop sets result on CQE → send loop resumes inline.
    private ManualResetValueTaskSourceCore<int> _sendTcs;

    // IValueTaskSource<int> implementation — used by send loop to await send CQE.
    int IValueTaskSource<int>.GetResult(short token)
    {
        try
        {
            return _sendTcs.GetResult(token);
        }
        finally
        {
            Volatile.Write(ref _sendAwaiting, 0);
        }
    }
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _sendTcs.GetStatus(token);
    void IValueTaskSource<int>.OnCompleted(Action<object?> c, object? s, short t, ValueTaskSourceOnCompletedFlags f) =>
        _sendTcs.OnCompleted(c, s, t, f);

    private ValueTask<int> AwaitSendCompletion()
    {
        _sendTcs.Reset();
        Volatile.Write(ref _sendAwaiting, 1);
        return new ValueTask<int>(this, _sendTcs.Version);
    }

    private int _sendAwaiting;

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

    /// <summary>True when receive buffers are selected from the provided buffer ring.</summary>
    internal bool UsingBufferRing { get; }

    /// <summary>True when an async flush is pending and will trigger a recv rearm on completion.</summary>
    internal bool RecvRearmPending { get; set; }
    internal bool RecvCancelPending { get; set; }
    private Queue<byte[]>? _pendingRecvData;
    private int _pendingRecvBytes;
    private const int MaxPendingRecvBytes = 1024 * 1024;

    /// <summary>True after the output-pipe send loop has stopped and cannot submit more sends.</summary>
    internal bool SendLoopCompleted { get; private set; } = true;

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
        UsingBufferRing = useBufferRing;
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
            ? PipeScheduler.Inline
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
    /// Backpressure cancels the multishot request before it is rearmed.
    /// Returns true if the SQE was queued; false if the SQ was full (caller should retry).
    /// </summary>
    public unsafe bool SubmitBufferRingRecv(ushort bufferGroupId)
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
        sqe->Len = 0;
        sqe->OpFlags = 0; // recv_flags (MSG_xxx) — none
        sqe->IoPrio = (ushort)IoUringConstants.IORING_RECV_MULTISHOT;
        sqe->Flags |= IoUringConstants.IOSQE_BUFFER_SELECT;
        sqe->BufIndexOrGroup = bufferGroupId;
        sqe->UserData = EncodeUserData(_connectionId, Generation, OpType.Recv);
        HasRecvInFlight = true;
        return true;
    }

    public unsafe bool SubmitRecvCancel()
    {
        if (!_ring.TryGetSqe(out IoUringSqe* sqe))
            return false;

        sqe->Opcode = IoUringConstants.IORING_OP_ASYNC_CANCEL;
        sqe->Fd = -1;
        sqe->AddrOrSpliceOffIn = EncodeUserData(_connectionId, Generation, OpType.Recv);
        sqe->UserData = EncodeUserData(_connectionId, Generation, OpType.Cancel);
        RecvCancelPending = true;
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
    public RecvOutcome OnRecvCompleteFromBuffer(ReadOnlySpan<byte> data)
    {
        if (RecvRearmPending)
        {
            if (_pendingRecvBytes + data.Length > MaxPendingRecvBytes)
            {
                _requestClose?.Invoke(
                    _connectionId,
                    Generation,
                    new ConnectionAbortedException("Receive backpressure buffer limit exceeded."));
                return RecvOutcome.Closed;
            }

            (_pendingRecvData ??= new Queue<byte[]>()).Enqueue(data.ToArray());
            _pendingRecvBytes += data.Length;
            return RecvOutcome.Deferred;
        }

        return WriteAndFlushRecvData(data);
    }

    private RecvOutcome WriteAndFlushRecvData(ReadOnlySpan<byte> data)
    {
        ValueTask<FlushResult> flushTask;
        try
        {
            var dest = _inputPipe.Writer.GetSpan(data.Length);
            data.CopyTo(dest);
            _inputPipe.Writer.Advance(data.Length);
            flushTask = _inputPipe.Writer.FlushAsync();
        }
        catch (Exception ex)
        {
            _requestClose?.Invoke(
                _connectionId,
                Generation,
                new ConnectionAbortedException("Receive pipe write failed.", ex));
            return RecvOutcome.Closed;
        }

        if (flushTask.IsCompleted)
        {
            FlushResult flushResult;
            try
            {
                flushResult = flushTask.Result;
            }
            catch (Exception ex)
            {
                _requestClose?.Invoke(
                    _connectionId,
                    Generation,
                    new ConnectionAbortedException("Receive pipe flush failed.", ex));
                return RecvOutcome.Closed;
            }
            if (flushResult.IsCompleted || flushResult.IsCanceled)
            {
                _inputPipe.Writer.Complete();
                return RecvOutcome.Closed;
            }
            return RecvOutcome.Continue;
        }

        RecvRearmPending = true;
        _ = WaitForFlushThenRequestRecv(flushTask);
        return RecvOutcome.Deferred;
    }

    internal RecvOutcome ResumeReceiveAfterFlush()
    {
        RecvRearmPending = false;
        while (_pendingRecvData is { Count: > 0 })
        {
            byte[] data = _pendingRecvData.Dequeue();
            _pendingRecvBytes -= data.Length;
            var outcome = WriteAndFlushRecvData(data);
            if (outcome != RecvOutcome.Continue)
                return outcome;
        }
        return RecvOutcome.Continue;
    }

    // Send state — only accessed from the IO loop thread (via pipe scheduler).
    private MemoryHandle _sendHandle;
    private const int SmallSendBufferSize = 4096;
    private byte[]? _smallSendBuffer;
    private nint _smallSendBasePtr;
    private nint _sendPtr;
    // Diagnostic shadow state: timestamp + byte count of the current pin (0 when no pin held).
    private long _sendPinStartTs;
    private int _sendPinByteLen;
    /// <summary>
    /// Called by the IO loop when a SEND CQE completes.
    /// </summary>
    internal void CompleteSend(int bytesSent, uint cqeFlags)
    {
        HasSendInFlight = false;
        DisposeSendPin();
        if (Volatile.Read(ref _sendAwaiting) != 0)
            _sendTcs.SetResult(bytesSent);
    }

    /// <summary>
    /// Called on the IO loop thread when a RECV CQE completes.
    /// Copies data from the pre-pinned buffer into the pipe.
    /// Returns true if a new RECV should be immediately resubmitted.
    /// </summary>
    public RecvOutcome OnRecvComplete(int bytesRead)
    {
        HasRecvInFlight = false;

        if (bytesRead <= 0)
        {
            _inputPipe.Writer.Complete();
            return RecvOutcome.Closed;
        }

        ValueTask<FlushResult> flushTask;
        try
        {
            var dest = _inputPipe.Writer.GetSpan(bytesRead);
            _pinnedRecvBuf.AsSpan(0, bytesRead).CopyTo(dest);
            _inputPipe.Writer.Advance(bytesRead);
            flushTask = _inputPipe.Writer.FlushAsync();
        }
        catch (Exception ex)
        {
            _requestClose?.Invoke(
                _connectionId,
                Generation,
                new ConnectionAbortedException("Receive pipe write failed.", ex));
            return RecvOutcome.Closed;
        }

        if (flushTask.IsCompleted)
        {
            FlushResult flushResult;
            try
            {
                flushResult = flushTask.Result;
            }
            catch (Exception ex)
            {
                _requestClose?.Invoke(
                    _connectionId,
                    Generation,
                    new ConnectionAbortedException("Receive pipe flush failed.", ex));
                return RecvOutcome.Closed;
            }
            if (flushResult.IsCompleted || flushResult.IsCanceled)
            {
                _inputPipe.Writer.Complete();
                return RecvOutcome.Closed;
            }
            return RecvOutcome.Continue;
        }

        // Flush is async (back-pressure). Don't block the IO loop — defer recv resubmission.
        RecvRearmPending = true;
        _ = WaitForFlushThenRequestRecv(flushTask);
        return RecvOutcome.Deferred;
    }

    private async Task WaitForFlushThenRequestRecv(ValueTask<FlushResult> flushTask)
    {
        try
        {
            var result = await flushTask.ConfigureAwait(false);
            if (result.IsCompleted || result.IsCanceled)
            {
                _inputPipe.Writer.Complete();
                _requestClose?.Invoke(_connectionId, Generation, null);
                return;
            }

            // Request the IO loop to resubmit RECV for this connection.
            _requestRecvResubmit?.Invoke(_connectionId, Generation);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Flush failed for connection {Id}", _connectionId);
            _inputPipe.Writer.Complete(ex);
            _requestClose?.Invoke(
                _connectionId,
                Generation,
                new ConnectionAbortedException("Receive pipe flush failed.", ex));
        }
    }

    /// <summary>Pins a slice for SEND and records diagnostic counters. IO-loop thread only.</summary>
    private unsafe void PrepareSendSlice(ReadOnlyMemory<byte> slice)
    {
        if (slice.Length <= SmallSendBufferSize)
        {
            EnsureSmallSendBuffer();
            slice.Span.CopyTo(_smallSendBuffer);
            _sendPtr = _smallSendBasePtr;
            return;
        }

        _sendHandle = slice.Pin();
        _sendPtr = (nint)_sendHandle.Pointer;
        _sendPinByteLen = slice.Length;
        _sendPinStartTs = SendDiagnostics.OnPinStart(slice.Length);
    }

    private unsafe void EnsureSmallSendBuffer()
    {
        if (_smallSendBuffer != null)
            return;

        _smallSendBuffer = GC.AllocateUninitializedArray<byte>(
            SmallSendBufferSize,
            pinned: true);
        fixed (byte* pointer = _smallSendBuffer)
            _smallSendBasePtr = (nint)pointer;
    }

    private unsafe bool TrySubmitPreparedSend(int length)
    {
        if (!_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            _ring.Submit();
            if (!_ring.TryGetSqe(out sqe))
                return false;
        }

        sqe->Opcode = IoUringConstants.IORING_OP_SEND;
        SetSqeFd(sqe);
        sqe->AddrOrSpliceOffIn = (ulong)_sendPtr;
        sqe->Len = (uint)length;
        sqe->OpFlags = 0;
        sqe->IoPrio = 0;
        sqe->UserData = EncodeUserData(_connectionId, Generation, OpType.Send);
        HasSendInFlight = true;
        return true;
    }

    private Task WaitForSendRetry()
    {
        var completion = new TaskCompletionSource();
        if (Interlocked.CompareExchange(
                ref _sendRetryCompletion,
                completion,
                null) != null)
        {
            throw new InvalidOperationException("A send retry is already pending.");
        }
        _requestSendRetry?.Invoke(_connectionId, Generation);
        return completion.Task;
    }

    internal void ResumeSendRetry()
    {
        Interlocked.Exchange(ref _sendRetryCompletion, null)?.TrySetResult();
    }

    private void CancelSendRetry()
    {
        Interlocked.Exchange(ref _sendRetryCompletion, null)?
            .TrySetCanceled(_connectionCts.Token);
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
        _sendPtr = nint.Zero;
        _sendPinStartTs = 0;
        _sendPinByteLen = 0;
    }

    /// <summary>
    /// Starts the send loop. Continuations run on the IO loop thread via IoUringPipeScheduler.
    /// No drain task thread — the output pipe's readerScheduler routes continuations to the IO loop.
    /// </summary>
    public void InitializeIoCallbacks(
        Action<long, ushort> requestRecvResubmit,
        Action<long, ushort> requestSendRetry,
        Action<long, ushort, ConnectionAbortedException?> requestClose)
    {
        _requestRecvResubmit = requestRecvResubmit;
        _requestSendRetry = requestSendRetry;
        _requestClose = requestClose;
    }

    public void StartSendLoop()
    {
        SendLoopCompleted = false;
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
                        PrepareSendSlice(slice);
                        int requestedLen = slice.Length;
                        try
                        {
                            while (!TrySubmitPreparedSend(requestedLen))
                                await WaitForSendRetry().ConfigureAwait(false);
                        }
                        catch
                        {
                            DisposeSendPin();
                            throw;
                        }

                        int sent = await AwaitSendCompletion().ConfigureAwait(false);
                        if (sent <= 0) { aborted = true; break; }
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
                if (aborted) break;
                if (result.IsCompleted && totalConsumed == buffer.Length)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unhandled error in send loop for connection {Id}", _connectionId);
        }
        finally
        {
            try { reader.Complete(); } catch (InvalidOperationException) { }
            SendLoopCompleted = true;
            _requestClose?.Invoke(_connectionId, Generation, null);
        }
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        RequestClose(abortReason);
    }

    public override ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return ValueTask.CompletedTask;

        RequestClose(null);
        return ValueTask.CompletedTask;
    }

    private void RequestClose(ConnectionAbortedException? reason)
    {
        if (reason != null)
        {
            if (Interlocked.Exchange(ref _abortRequested, 1) == 0)
                _requestClose?.Invoke(_connectionId, Generation, reason);
            return;
        }

        if (Interlocked.Exchange(ref _closeRequested, 1) == 0)
            _requestClose?.Invoke(_connectionId, Generation, reason);
    }

    internal void BeginGracefulClose()
    {
        IsClosing = true;
    }

    /// <summary>Completes application output after Kestrel disposes the connection.</summary>
    internal void BeginApplicationDispose()
    {
        IsClosing = true;
        try { _outputPipe.Writer.Complete(); } catch (InvalidOperationException) { }
    }

    /// <summary>Aborts transport IO. Must run on the owning IO loop.</summary>
    internal void BeginTransportAbort(ConnectionAbortedException reason)
    {
        IsClosing = true;
        if (Interlocked.Exchange(ref _transportAborted, 1) != 0)
            return;

        _connectionCts.Cancel();
        CancelSendRetry();
        try
        {
            _inputPipe.Writer.Complete(reason);
        }
        catch (InvalidOperationException) { }
        _outputPipe.Reader.CancelPendingRead();

        ShutdownSocket();
    }

    internal void ShutdownSocket()
    {
        if (Interlocked.Exchange(ref _socketShutdown, 1) != 0)
            return;

        if (Libc.shutdown(_socketFd, IoUringConstants.SHUT_RDWR) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno != IoUringConstants.ENOTCONN)
                _logger.LogDebug("shutdown(fd={Fd}) failed with errno {Errno}", _socketFd, errno);
        }
    }

    /// <summary>Closes the socket fd. Called by the listener after in-flight ops are drained.</summary>
    internal void CloseSocketFd()
    {
        if (Interlocked.Exchange(ref _socketClosed, 1) != 0)
            return;

        _connectionCts.Cancel();
        if (_fileIndex >= 0 && !_ring.UnregisterFd(_fileIndex))
            _logger.LogWarning("Failed to unregister fixed-file slot {Slot}.", _fileIndex);
        if (Libc.close(_socketFd) < 0)
            _logger.LogWarning("close(fd={Fd}) failed with errno {Errno}", _socketFd, Marshal.GetLastPInvokeError());
    }

    /// <summary>Releases managed transport resources after the ring has stopped and closed.</summary>
    internal void ForceCleanupAfterRingClosed()
    {
        DisposeSendPin();
        try { _inputPipe.Reader.Complete(); } catch (InvalidOperationException) { }
        try { _inputPipe.Writer.Complete(); } catch (InvalidOperationException) { }
        try { _outputPipe.Reader.Complete(); } catch (InvalidOperationException) { }
        _pendingRecvData?.Clear();
        _pendingRecvBytes = 0;
    }

    private sealed class DuplexPipe(PipeReader reader, PipeWriter writer) : IDuplexPipe
    {
        public PipeReader Input { get; } = reader;
        public PipeWriter Output { get; } = writer;
    }
}
