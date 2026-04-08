using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Kestrel.Transport.IoUring.Native;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>A pending send operation queued from the output-drain task to the IO-loop thread.</summary>
internal readonly struct PendingSend
{
    public readonly long ConnectionId;
    public readonly MemoryHandle Handle;
    public readonly nint Pointer;
    public readonly uint Length;
    public readonly PooledSendCompletion Completion;
    public readonly bool UseZeroCopy;

    public PendingSend(long connectionId, MemoryHandle handle, nint pointer, uint length, PooledSendCompletion completion, bool useZeroCopy = false)
    {
        ConnectionId = connectionId;
        Handle = handle;
        Pointer = pointer;
        Length = length;
        Completion = completion;
        UseZeroCopy = useZeroCopy;
    }
}

internal sealed class IoUringConnection : ConnectionContext, IValueTaskSource<int>
{
    private const ulong OpTypeMask = 0xFF;
    private const int ConnectionIdShift = 8;

    public enum OpType : byte { Accept = 0, Recv = 1, Send = 2, Close = 3, Cancel = 4 }

    public static ulong EncodeUserData(long connectionId, OpType opType) =>
        ((ulong)connectionId << ConnectionIdShift) | (byte)opType;

    public static (long ConnectionId, OpType OpType) DecodeUserData(ulong userData) =>
        ((long)(userData >> ConnectionIdShift), (OpType)(userData & OpTypeMask));

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

    // Pre-pinned recv buffer — eliminates Pin()/Dispose() per recv.
    private readonly byte[] _pinnedRecvBuf;
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
        ILogger logger)
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

        // Pre-pin recv buffer to avoid Pin()/Dispose() on every recv.
        _pinnedRecvBuf = GC.AllocateArray<byte>(receiveBufferSize, pinned: true);
        unsafe { fixed (byte* p = _pinnedRecvBuf) _pinnedRecvPtr = p; }

        // PipeOptions: inline mode — ALL continuations run on the IO loop thread.
        // Kestrel HTTP processing runs inline on the IO thread (like UnsafePreferInlineScheduling).
        // This eliminates cross-thread hops: recv → HTTP → send all on one thread.
        var inputOptions = new PipeOptions(
            writerScheduler: transportScheduler,
            readerScheduler: transportScheduler,  // Kestrel reads inline on IO thread
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
            sqe->UserData = EncodeUserData(_connectionId, OpType.Recv);
            HasRecvInFlight = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Submits a multishot RECV SQE with buffer selection from a provided buffer ring.
    /// The kernel will select buffers from the specified group and generate multiple CQEs.
    /// No per-recv Pin() needed — the buffer ring owns the memory.
    /// </summary>
    public unsafe void SubmitMultishotRecv(ushort bufferGroupId)
    {
        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_RECV;
            SetSqeFd(sqe);
            sqe->AddrOrSpliceOffIn = 0; // kernel selects buffer
            sqe->Len = 0;              // kernel determines length from buffer ring
            sqe->OpFlags = IoUringConstants.IORING_RECV_MULTISHOT;
            sqe->Flags = IoUringConstants.IOSQE_BUFFER_SELECT;
            sqe->BufIndexOrGroup = bufferGroupId;
            sqe->UserData = EncodeUserData(_connectionId, OpType.Recv);
            HasRecvInFlight = true;
            UsingMultishotRecv = true;
        }
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
    internal bool SendZcNotifPending { get; set; }

    /// <summary>
    /// Called by the IO loop when a SEND CQE completes.
    /// </summary>
    internal void CompleteSend(int bytesSent, uint cqeFlags)
    {
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
        _sendHandle.Dispose();
        _sendHandle = default;
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

    public void OnSendComplete(int bytesSent, PendingSend pending)
    {
        HasSendInFlight = false;
        pending.Handle.Dispose();
        pending.Completion.SetResult(bytesSent);
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

                // Send directly from pipe buffer — no copy needed.
                // Pin the first segment and submit a SEND SQE.
                var first = buffer.First;
                _sendHandle = first.Pin();

                unsafe
                {
                    if (!_ring.TryGetSqe(out IoUringSqe* sqe))
                    {
                        _sendHandle.Dispose();
                        _sendHandle = default;
                        break; // Ring full
                    }
                    sqe->Opcode = IoUringConstants.IORING_OP_SEND;
                    SetSqeFd(sqe);
                    sqe->AddrOrSpliceOffIn = (ulong)_sendHandle.Pointer;
                    sqe->Len = (uint)first.Length;
                    sqe->UserData = EncodeUserData(_connectionId, OpType.Send);
                    HasSendInFlight = true;
                }

                _ring.Submit();

                // Yield to the IO loop — it will process other connections' recv/send.
                // When the send CQE arrives, CompleteSend → SetResult → we resume inline.
                int sent = await AwaitSendCompletion().ConfigureAwait(false);

                if (sent <= 0)
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    break;
                }

                reader.AdvanceTo(buffer.GetPosition(sent));
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

    public override ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return ValueTask.CompletedTask;

        _connectionCts.Cancel();
        // Complete both sides of both pipes. By the time Kestrel calls DisposeAsync,
        // it has finished reading from the input pipe.
        try { _inputPipe.Reader.Complete(); } catch (InvalidOperationException) { }
        try { _inputPipe.Writer.Complete(); } catch (InvalidOperationException) { }
        try { _outputPipe.Writer.Complete(); } catch (InvalidOperationException) { }

        // If a send is still in flight, clean up the handle.
        if (HasSendInFlight)
        {
            HasSendInFlight = false;
            _sendHandle.Dispose();
            _sendHandle = default;
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

    private sealed class DuplexPipe(PipeReader reader, PipeWriter writer) : IDuplexPipe
    {
        public PipeReader Input { get; } = reader;
        public PipeWriter Output { get; } = writer;
    }
}
