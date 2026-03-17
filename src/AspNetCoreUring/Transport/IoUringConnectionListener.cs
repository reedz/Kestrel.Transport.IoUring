using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
    private int _nextConnectionId;
    private Task? _ioLoopTask;
    private int _listenSocketFd;

    public EndPoint EndPoint { get; }

    public IoUringConnectionListener(EndPoint endPoint, Ring ring, ILogger logger)
    {
        EndPoint = endPoint;
        _ring = ring;
        _logger = logger;
        _listenSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _acceptChannel = Channel.CreateBounded<ConnectionContext>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public void Bind()
    {
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenSocket.Bind(EndPoint);
        _listenSocket.Listen(512);

        _listenSocketFd = GetSocketFd(_listenSocket);
        SubmitAccept();
        _ring.Submit();
        _ioLoopTask = Task.Run(RunIoLoopAsync);
    }

    private static int GetSocketFd(Socket socket)
    {
        return (int)socket.SafeHandle.DangerousGetHandle();
    }

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

    private async Task RunIoLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
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

    private void ProcessCompletions()
    {
        while (_ring.TryPeekCompletion(out var cqe))
        {
            _ring.AdvanceCompletion();
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
    }

    private void HandleAccept(int result)
    {
        if (result < 0)
        {
            if (!_cts.IsCancellationRequested)
                _logger.LogWarning("Accept failed with errno {Errno}", -result);
        }
        else
        {
            int socketFd = result;
            int connId = Interlocked.Increment(ref _nextConnectionId);
            var conn = new IoUringConnection(
                connId,
                socketFd,
                _ring,
                remoteEndPoint: null,
                localEndPoint: EndPoint,
                _logger);

            _connections[connId] = conn;
            conn.SubmitRecv();
            _acceptChannel.Writer.TryWrite(conn);
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
        if (_connections.TryGetValue(connectionId, out var conn))
        {
            conn.OnSendComplete(result);
        }
    }

    private void HandleClose(int connectionId)
    {
        _connections.TryRemove(connectionId, out _);
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
        _acceptChannel.Writer.TryComplete();

        if (_ioLoopTask != null)
        {
            try
            {
                await _ioLoopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnbindAsync().ConfigureAwait(false);
        foreach (var conn in _connections.Values)
            await conn.DisposeAsync().ConfigureAwait(false);
        _connections.Clear();
        _listenSocket.Dispose();
        _ring.Dispose();
        _cts.Dispose();
    }
}
