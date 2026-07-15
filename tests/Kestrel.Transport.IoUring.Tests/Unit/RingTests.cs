using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Kestrel.Transport.IoUring;
using Kestrel.Transport.IoUring.Native;
using Xunit;
using Xunit.Abstractions;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// Low-level tests for the io_uring Ring and buffer ring after the
/// IOSQE_BUFFER_SELECT fix (1<<3 → 1<<5).
/// </summary>
public class RingTests
{
    private readonly ITestOutputHelper _output;
    public RingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Ring_IsSupported()
    {
        Ring.IsSupported.Should().BeTrue();
    }

    [Fact]
    public void IOSQE_BUFFER_SELECT_IsCorrectValue()
    {
        // The fix: was 1<<3 (0x08), should be 1<<5 (0x20).
        IoUringConstants.IOSQE_BUFFER_SELECT.Should().Be(0x20,
            "IOSQE_BUFFER_SELECT must be 1<<5 = 0x20 to match kernel definition");
    }

    [Fact]
    public unsafe void BufferRing_Registration_Succeeds()
    {
        using var ring = new Ring(32);
        using var bufRing = new ProvidedBufferRing(ring.Fd, bgid: 0, ringEntries: 16, bufferSize: 4096);
        bufRing.GroupId.Should().Be(0);
        bufRing.BufferSize.Should().Be(4096);
    }

    [Fact]
    public unsafe void BufferRing_SingleShot_Recv_WithCorrectFlag()
    {
        // Create connected socket pair.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        clientSocket.Connect(IPAddress.Loopback, port);
        var serverSocket = listener.AcceptSocket();
        listener.Stop();

        int clientFd = (int)clientSocket.Handle;
        serverSocket.Send("BUFRING_TEST"u8.ToArray());

        using var ring = new Ring(32);
        using var bufRing = new ProvidedBufferRing(ring.Fd, bgid: 0, ringEntries: 16, bufferSize: 4096);

        // Submit RECV with buffer selection (uses the fixed IOSQE_BUFFER_SELECT = 0x20).
        lock (ring.SubmitLock)
        {
            ring.TryGetSqe(out IoUringSqe* sqe);
            sqe->Opcode = IoUringConstants.IORING_OP_RECV;
            sqe->Fd = clientFd;
            sqe->Len = 4096;
            sqe->Flags = IoUringConstants.IOSQE_BUFFER_SELECT;
            sqe->BufIndexOrGroup = 0;
            sqe->UserData = 42;
        }

        ring.SubmitAndWait(1);
        ring.TryPeekCompletion(out var cqe);
        ring.AdvanceCompletion();

        _output.WriteLine($"CQE: res={cqe.Res} flags=0x{cqe.Flags:X}");

        cqe.Res.Should().BeGreaterThan(0, $"RECV should succeed, got errno {-cqe.Res}");

        bool hasBuffer = (cqe.Flags & IoUringConstants.IORING_CQE_F_BUFFER) != 0;
        hasBuffer.Should().BeTrue("CQE should have buffer selection flag");

        ushort bufferId = (ushort)(cqe.Flags >> IoUringConstants.IORING_CQE_BUFFER_SHIFT);
        var data = bufRing.GetBuffer(bufferId).Slice(0, cqe.Res);
        var str = System.Text.Encoding.UTF8.GetString(data);
        _output.WriteLine($"Received: '{str}'");
        str.Should().Be("BUFRING_TEST");

        bufRing.RecycleBuffer(bufferId);
        clientSocket.Close();
        serverSocket.Close();
    }

    [Fact]
    public void FailedFileUnregister_DisablesFixedFileReuse()
    {
        using var ring = new Ring(8);
        ring.InitFileTable(4).Should().BeTrue();
        int socketFd = Libc.socket(2 /* AF_INET */, 1 /* SOCK_STREAM */, 0);
        socketFd.Should().BeGreaterThanOrEqualTo(0);

        int slot = ring.RegisterFd(socketFd);
        slot.Should().BeGreaterThanOrEqualTo(0);

        ring.CompleteFileUnregisterForTest(slot, updateResult: -1).Should().BeFalse();
        ring.HasRegisteredFiles.Should().BeFalse(
            "an unconfirmed kernel table update must quarantine all future fixed-file registrations");
        ring.RegisterFd(socketFd).Should().Be(-1);

        Libc.close(socketFd).Should().Be(0);
    }

    [Fact]
    public void NativeStructSizes_MatchLinuxIoUringAbi()
    {
        Unsafe.SizeOf<IoUringSqe>().Should().Be(64);
        Unsafe.SizeOf<IoUringCqe>().Should().Be(16);
        Unsafe.SizeOf<IoUringSqRingOffsets>().Should().Be(40);
        Unsafe.SizeOf<IoUringCqRingOffsets>().Should().Be(40);
        Unsafe.SizeOf<IoUringParams>().Should().Be(120);
        Unsafe.SizeOf<IoUringBuf>().Should().Be(16);
        Unsafe.SizeOf<IoUringBufReg>().Should().Be(40);
        Unsafe.SizeOf<IoUringFilesUpdate>().Should().Be(16);
    }
}
