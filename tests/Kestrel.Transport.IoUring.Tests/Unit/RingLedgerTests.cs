using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Kestrel.Transport.IoUring.Native;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// The ring's operation ledger (SQEs handed out minus final completions consumed) is what teardown relies on to
/// know the kernel no longer touches request memory. A multishot request stays outstanding until the completion
/// without IORING_CQE_F_MORE.
/// </summary>
public class RingLedgerTests
{
    [Fact]
    public unsafe void SingleShotRequest_IsOutstandingUntilItsCompletionIsConsumed()
    {
        if (!Ring.IsSupported) return;

        using var ring = new Ring(8);
        ring.OutstandingOperations.Should().Be(0);

        ring.TryGetSqe(out IoUringSqe* sqe).Should().BeTrue();
        sqe->Opcode = IoUringConstants.IORING_OP_NOP;
        sqe->UserData = 1;
        ring.OutstandingOperations.Should().Be(1, "a prepared SQE will reach the kernel on the next submit");

        ring.SubmitAndWait(1);
        ring.TryPeekCompletion(out var cqe).Should().BeTrue();
        cqe.UserData.Should().Be(1UL);
        ring.OutstandingOperations.Should().Be(1, "peeking does not consume the completion");

        ring.AdvanceCompletion();
        ring.OutstandingOperations.Should().Be(0);
    }

    [Fact]
    public unsafe void MultishotAccept_StaysOutstandingUntilItsFinalCompletion()
    {
        if (!Ring.IsSupported) return;
        if (IoUringKernelFeatures.CurrentKernel() is not { } kernel || kernel < new Version(5, 19)) return;

        using var listen = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listen.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listen.Listen(16);
        using var ring = new Ring(8);
        const ulong acceptUserData = 7;

        ring.TryGetSqe(out IoUringSqe* sqe).Should().BeTrue();
        sqe->Opcode = IoUringConstants.IORING_OP_ACCEPT;
        sqe->Fd = (int)listen.Handle;
        sqe->IoPrio = (ushort)IoUringConstants.IORING_ACCEPT_MULTISHOT;
        sqe->OpFlags = IoUringConstants.SOCK_CLOEXEC;
        sqe->UserData = acceptUserData;
        ring.Submit();

        var clients = new List<Socket>();
        var accepted = new List<int>();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                clients.Add(client);
                client.Connect(listen.LocalEndPoint!);
                ring.SubmitAndWait(1);
                ring.TryPeekCompletion(out var cqe).Should().BeTrue();
                cqe.Res.Should().BeGreaterThanOrEqualTo(0);
                (cqe.Flags & IoUringConstants.IORING_CQE_F_MORE).Should().NotBe(0u);
                accepted.Add(cqe.Res);
                ring.AdvanceCompletion();
                ring.OutstandingOperations.Should().Be(1, "the multishot accept is still armed");
            }

            ring.TryGetSqe(out sqe).Should().BeTrue();
            sqe->Opcode = IoUringConstants.IORING_OP_ASYNC_CANCEL;
            sqe->Fd = -1;
            sqe->AddrOrSpliceOffIn = acceptUserData;
            sqe->UserData = 8;
            ring.OutstandingOperations.Should().Be(2);

            int consumed = 0;
            while (consumed < 2)
            {
                ring.SubmitAndWait(1);
                while (ring.TryPeekCompletion(out var cqe))
                {
                    if (cqe.UserData == acceptUserData && cqe.Res >= 0)
                        accepted.Add(cqe.Res); // raced the cancel; still ours to close
                    else
                        consumed++;
                    ring.AdvanceCompletion();
                }
            }

            ring.OutstandingOperations.Should().Be(0, "the accept ended and the cancel completed");
        }
        finally
        {
            foreach (int fd in accepted)
                Libc.close(fd);
            foreach (var client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public unsafe void SealedRing_HandsOutNoMoreSqes()
    {
        if (!Ring.IsSupported) return;

        using var ring = new Ring(8);
        ring.SealSubmissions();

        ring.TryGetSqe(out IoUringSqe* sqe).Should().BeFalse();
        ((nint)sqe).Should().Be(nint.Zero);
        ring.OutstandingOperations.Should().Be(0);
    }
}
