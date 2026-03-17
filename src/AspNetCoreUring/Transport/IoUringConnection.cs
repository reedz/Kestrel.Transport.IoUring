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
    private readonly CancellationTokenSource _connectionCts = new();
    private readonly Pipe _inputPipe;
    private readonly Pipe _outputPipe;
    private bool _disposed;

    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    public override IDuplexPipe Transport { get; set; }

    public int SocketFd => _socketFd;
    public int NumericConnectionId => _connectionId;

    public IoUringConnection(int connectionId, int socketFd, Ring ring, EndPoint? remoteEndPoint, EndPoint? localEndPoint, ILogger logger)
    {
        _connectionId = connectionId;
        _socketFd = socketFd;
        _ring = ring;
        _logger = logger;
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

    private MemoryHandle _recvHandle;

    public unsafe void SubmitRecv()
    {
        Memory<byte> buffer = _inputPipe.Writer.GetMemory(4096);
        _recvHandle = buffer.Pin();

        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_RECV;
            sqe->Fd = _socketFd;
            sqe->AddrOrSpliceOffIn = (ulong)_recvHandle.Pointer;
            sqe->Len = (uint)buffer.Length;
            sqe->UserData = EncodeUserData(_connectionId, OpType.Recv);
        }
    }

    private MemoryHandle _sendHandle;

    public unsafe void SubmitSend(ReadOnlyMemory<byte> data)
    {
        _sendHandle = data.Pin();

        if (_ring.TryGetSqe(out IoUringSqe* sqe))
        {
            sqe->Opcode = IoUringConstants.IORING_OP_SEND;
            sqe->Fd = _socketFd;
            sqe->AddrOrSpliceOffIn = (ulong)_sendHandle.Pointer;
            sqe->Len = (uint)data.Length;
            sqe->UserData = EncodeUserData(_connectionId, OpType.Send);
        }
    }

    public void OnRecvComplete(int bytesRead)
    {
        _recvHandle.Dispose();

        if (bytesRead <= 0)
        {
            _inputPipe.Writer.Complete();
            return;
        }

        _inputPipe.Writer.Advance(bytesRead);
        var result = _inputPipe.Writer.FlushAsync();
        if (!result.IsCompleted)
            result.AsTask().GetAwaiter().GetResult();
    }

    public void OnSendComplete(int bytesSent)
    {
        _sendHandle.Dispose();

        if (bytesSent < 0)
        {
            _outputPipe.Reader.Complete();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _connectionCts.Cancel();
        _inputPipe.Reader.Complete();
        _inputPipe.Writer.Complete();
        _outputPipe.Reader.Complete();
        _outputPipe.Writer.Complete();
        Libc.close(_socketFd);
        _connectionCts.Dispose();
    }

    private sealed class DuplexPipe(PipeReader reader, PipeWriter writer) : IDuplexPipe
    {
        public PipeReader Input { get; } = reader;
        public PipeWriter Output { get; } = writer;
    }
}
