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
    public readonly int ConnectionId;
    public readonly MemoryHandle Handle;
    public readonly uint Length;
    public readonly TaskCompletionSource<int> Completion;

    public PendingSend(int connectionId, MemoryHandle handle, uint length, TaskCompletionSource<int> completion)
    {
        ConnectionId = connectionId;
        Handle = handle;
        Length = length;
        Completion = completion;
    }
}

internal sealed class IoUringConnection : ConnectionContext
{
    private const ulong OpTypeMask = 0xFF;
    private const int ConnectionIdShift = 8;

    public enum OpType : byte { Accept = 0, Recv = 1, Send = 2, Close = 3 }

    public static ulong EncodeUserData(int connectionId, OpType opType) =>
        ((ulong)connectionId << ConnectionIdShift) | (byte)opType;

    public static (int ConnectionId, OpType OpType) DecodeUserData(ulong userData) =>
        ((int)(userData >> ConnectionIdShift), (OpType)(userData & OpTypeMask));

    private readonly int _connectionId;
    private readonly int _socketFd;
    private readonly Ring _ring;
    private readonly ILogger _logger;
    private readonly int _receiveBufferSize;
    private readonly CancellationTokenSource _connectionCts = new();
    private readonly Pipe _inputPipe;
    private readonly Pipe _outputPipe;
    private int _disposed;

    // Callback registered by the listener so the output drain task can enqueue sends.
    private Action<PendingSend>? _enqueueSend;

    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    public override IDuplexPipe Transport { get; set; }

    public int SocketFd => _socketFd;
    public int NumericConnectionId => _connectionId;

    public IoUringConnection(
        int connectionId,
        int socketFd,
        Ring ring,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        int receiveBufferSize,
        ILogger logger)
    {
        _connectionId = connectionId;
        _socketFd = socketFd;
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

    public unsafe void SubmitRecv()
    {
        Memory<byte> buffer = _inputPipe.Writer.GetMemory(_receiveBufferSize);
        _recvHandle = buffer.Pin();

        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_RECV;
            sqe->Fd = _socketFd;
            sqe->AddrOrSpliceOffIn = (ulong)_recvHandle.Pointer;
            sqe->Len = (uint)buffer.Length;
            sqe->UserData = EncodeUserData(_connectionId, OpType.Recv);
        }
        else
        {
            _recvHandle.Dispose();
            _recvHandle = default;
        }
    }

    // Called from IO loop thread only.
    private PendingSend _inFlightSend;
    private bool _hasSendInFlight;

    public unsafe void SubmitSend(in PendingSend pending)
    {
        _inFlightSend = pending;
        _hasSendInFlight = true;

        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_SEND;
            sqe->Fd = _socketFd;
            sqe->AddrOrSpliceOffIn = (ulong)pending.Handle.Pointer;
            sqe->Len = pending.Length;
            sqe->UserData = EncodeUserData(_connectionId, OpType.Send);
        }
        else
        {
            // Ring full — complete with 0 so the drain task retries.
            _hasSendInFlight = false;
            pending.Handle.Dispose();
            pending.Completion.TrySetResult(0);
        }
    }

    public bool HasSendInFlight => _hasSendInFlight;

    public void OnRecvComplete(int bytesRead)
    {
        _recvHandle.Dispose();
        _recvHandle = default;

        if (bytesRead <= 0)
        {
            _inputPipe.Writer.Complete();
            return;
        }

        _inputPipe.Writer.Advance(bytesRead);
        var flushResult = _inputPipe.Writer.FlushAsync();
        if (!flushResult.IsCompleted)
        {
            // We're on the IO loop thread — synchronously wait for back-pressure.
            flushResult.AsTask().GetAwaiter().GetResult();
        }
        else if (flushResult.Result.IsCompleted || flushResult.Result.IsCanceled)
        {
            _inputPipe.Writer.Complete();
        }
    }

    public void OnSendComplete(int bytesSent)
    {
        _hasSendInFlight = false;
        var pending = _inFlightSend;
        _inFlightSend = default;
        pending.Handle.Dispose();
        pending.Completion.TrySetResult(bytesSent);
    }

    /// <summary>
    /// Starts the background output-drain loop that reads from the application's output pipe
    /// and submits SEND SQEs via <paramref name="enqueueSend"/>.
    /// </summary>
    public void StartOutputDrain(Action<PendingSend> enqueueSend)
    {
        _enqueueSend = enqueueSend;
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

                // Submit each contiguous segment as a separate SEND and await its completion.
                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    if (segment.IsEmpty) continue;

                    var sendCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var handle = segment.Pin();
                    _enqueueSend!(new PendingSend(_connectionId, handle, (uint)segment.Length, sendCompletion));

                    int sent;
                    try
                    {
                        // WaitAsync ensures a cancellation from DisposeAsync unblocks us.
                        sent = await sendCompletion.Task.WaitAsync(ct).ConfigureAwait(false);
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
                    consumed = buffer.GetPosition(sent, consumed);
                }

                try
                {
                    reader.AdvanceTo(consumed, buffer.End);
                }
                catch (InvalidOperationException)
                {
                    // Reader was completed concurrently (connection disposed). Exit cleanly.
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

    public override ValueTask DisposeAsync()
    {
        // Atomically check-and-set to prevent double-dispose from concurrent callers.
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return ValueTask.CompletedTask;

        _connectionCts.Cancel();
        _inputPipe.Reader.Complete();
        _inputPipe.Writer.Complete();
        // NOTE: Do NOT complete _outputPipe.Reader here — RunOutputDrainAsync owns it and
        // completes it in its finally block after the cancellation token fires.
        _outputPipe.Writer.Complete();

        // If a send is still in flight, complete its TCS so the drain task unblocks.
        if (_hasSendInFlight)
        {
            _hasSendInFlight = false;
            _inFlightSend.Handle.Dispose();
            _inFlightSend.Completion.TrySetResult(-1);
            _inFlightSend = default;
        }

        if (Libc.close(_socketFd) < 0)
            _logger.LogWarning("close(fd={Fd}) failed with errno {Errno}", _socketFd, Marshal.GetLastPInvokeError());
        _connectionCts.Dispose();

        return ValueTask.CompletedTask;
    }

    private sealed class DuplexPipe(PipeReader reader, PipeWriter writer) : IDuplexPipe
    {
        public PipeReader Input { get; } = reader;
        public PipeWriter Output { get; } = writer;
    }
}
