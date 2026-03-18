using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AspNetCoreUring.Native;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace AspNetCoreUring.Transport;

/// <summary>A pending send operation queued from the output-drain task to the IO-loop thread.</summary>
internal readonly struct PendingSend
{
    public readonly long ConnectionId;
    public readonly MemoryHandle Handle;
    public readonly nint Pointer;
    public readonly uint Length;
    public readonly PooledSendCompletion Completion;

    public PendingSend(long connectionId, MemoryHandle handle, nint pointer, uint length, PooledSendCompletion completion)
    {
        ConnectionId = connectionId;
        Handle = handle;
        Pointer = pointer;
        Length = length;
        Completion = completion;
    }
}

internal sealed class IoUringConnection : ConnectionContext
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

    public IoUringConnection(
        long connectionId,
        int socketFd,
        int fileIndex,
        Ring ring,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        int receiveBufferSize,
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

        var inputOptions = new PipeOptions(useSynchronizationContext: false);
        var outputOptions = new PipeOptions(useSynchronizationContext: false);
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
            SetSqeFd(sqe);
        }
    }

    /// <summary>
    /// Submits a RECV SQE. Returns false if the SQ is full (caller should retry later).
    /// </summary>
    public unsafe bool SubmitRecv()
    {
        Memory<byte> buffer = _inputPipe.Writer.GetMemory(_receiveBufferSize);
        _recvHandle = buffer.Pin();

        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_RECV;
            SetSqeFd(sqe);
            sqe->AddrOrSpliceOffIn = (ulong)_recvHandle.Pointer;
            sqe->Len = (uint)buffer.Length;
            sqe->UserData = EncodeUserData(_connectionId, OpType.Recv);
            HasRecvInFlight = true;
            return true;
        }

        _recvHandle.Dispose();
        _recvHandle = default;
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
            sqe->Flags |= IoUringConstants.IOSQE_BUFFER_SELECT;
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

    // Send state — written by drain task, read by IO loop after CQE arrives.
    // Safe because io_uring_enter syscall acts as a memory barrier between the two.
    private MemoryHandle _sendHandle;
    private PooledSendCompletion? _sendCompletion;

    public unsafe bool SubmitSend(in PendingSend pending)
    {
        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_SEND;
            SetSqeFd(sqe);
            sqe->AddrOrSpliceOffIn = (ulong)pending.Pointer;
            sqe->Len = pending.Length;
            sqe->UserData = EncodeUserData(_connectionId, OpType.Send);
            HasSendInFlight = true;
            return true;
        }

        // Ring full — complete with 0 so the drain task retries.
        pending.Handle.Dispose();
        pending.Completion.SetResult(0);
        return false;
    }

    /// <summary>
    /// Submits a SEND SQE directly from the calling thread (output drain task).
    /// Acquires the ring lock, fills the SQE, flushes, and submits via io_uring_enter.
    /// Returns a ValueTask that completes when the kernel finishes the SEND.
    /// </summary>
    private unsafe ValueTask<int> SubmitSendDirect(ReadOnlyMemory<byte> data)
    {
        var completion = PooledSendCompletion.Rent();
        var handle = data.Pin();

        bool submitted;
        lock (_ring.SubmitLock)
        {
            if (_ring.TryGetSqe(out IoUringSqe* sqe))
            {
                sqe->Opcode = IoUringConstants.IORING_OP_SEND;
                SetSqeFd(sqe);
                sqe->AddrOrSpliceOffIn = (ulong)handle.Pointer;
                sqe->Len = (uint)data.Length;
                sqe->UserData = EncodeUserData(_connectionId, OpType.Send);

                _sendHandle = handle;
                _sendCompletion = completion;
                HasSendInFlight = true;
                submitted = true;
            }
            else
            {
                submitted = false;
            }
        }

        if (submitted)
        {
            // Submit outside lock — io_uring_enter is kernel-safe for concurrent calls.
            _ring.Submit();
            return completion.AsValueTask();
        }

        // Ring full — caller should retry.
        handle.Dispose();
        completion.SetResult(0);
        return completion.AsValueTask();
    }

    /// <summary>
    /// Called by the IO loop when a SEND CQE completes.
    /// Signals the drain task that submitted this send.
    /// </summary>
    internal void CompleteSend(int bytesSent)
    {
        HasSendInFlight = false;
        _sendHandle.Dispose();
        _sendHandle = default;
        var completion = _sendCompletion;
        _sendCompletion = null;
        completion?.SetResult(bytesSent);
    }

    /// <summary>
    /// Called on the IO loop thread when a RECV CQE completes.
    /// Returns true if a new RECV should be immediately resubmitted.
    /// </summary>
    public bool OnRecvComplete(int bytesRead)
    {
        HasRecvInFlight = false;
        _recvHandle.Dispose();
        _recvHandle = default;

        if (bytesRead <= 0)
        {
            _inputPipe.Writer.Complete();
            return false;
        }

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
    /// Starts the background output-drain loop that reads from the application's output pipe
    /// and submits SEND SQEs directly via the ring (no cross-thread queue).
    /// </summary>
    public void StartOutputDrain(Action<long> requestRecvResubmit)
    {
        _requestRecvResubmit = requestRecvResubmit;
        _ = RunOutputDrainAsync();
    }

    private async Task RunOutputDrainAsync()
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
                SequencePosition consumed = buffer.Start;
                bool error = false;

                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    if (segment.IsEmpty) continue;

                    // Handle partial sends by retrying the remainder of each segment.
                    int offset = 0;
                    int remaining = segment.Length;

                    while (remaining > 0)
                    {
                        var slice = segment.Slice(offset, remaining);

                        // Submit SEND directly from this thread — no eventfd round-trip.
                        int sent;
                        try
                        {
                            sent = await SubmitSendDirect(slice).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            error = true;
                            break;
                        }

                        if (sent <= 0)
                        {
                            error = true;
                            break;
                        }

                        offset += sent;
                        remaining -= sent;
                    }

                    if (error) break;
                    consumed = buffer.GetPosition(segment.Length, consumed);
                }

                try
                {
                    reader.AdvanceTo(consumed, buffer.End);
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                if (error) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unhandled error in output drain for connection {Id}", _connectionId);
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

        // If a send is still in flight, complete its awaiter so the drain task unblocks.
        if (HasSendInFlight)
        {
            HasSendInFlight = false;
            _sendHandle.Dispose();
            _sendHandle = default;
            var completion = _sendCompletion;
            _sendCompletion = null;
            completion?.SetResult(-1);
        }

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
