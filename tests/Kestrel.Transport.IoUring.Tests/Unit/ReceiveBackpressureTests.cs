using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

/// <summary>
/// When the application reads slowly, the input pipe pauses its writer and the multishot receive is cancelled.
/// Completions already posted before the cancel takes effect must still be accepted: a fast peer can have
/// several MiB queued in the completion queue by then, and aborting would kill a perfectly valid upload.
/// </summary>
public class ReceiveBackpressureTests
{
    private const int ReceiveBufferSize = 2048;
    private const int PauseThreshold = 1024 * 1024 + 1;

    private static IoUringConnection CreateConnection(
        Ring ring,
        List<ConnectionAbortedException?> closeRequests)
    {
        var connection = new IoUringConnection(
            connectionId: 0,
            socketFd: 42,
            fileIndex: -1,
            ring,
            remoteEndPoint: null,
            localEndPoint: null,
            receiveBufferSize: ReceiveBufferSize,
            transportScheduler: new IoUringPipeScheduler(static () => { }),
            unsafeInlineScheduling: false,
            logger: NullLogger.Instance,
            useBufferRing: true)
        {
            Generation = 1,
        };
        connection.InitializeIoCallbacks(
            static (_, _) => { },
            static (_, _) => { },
            (_, _, reason) => closeRequests.Add(reason));
        return connection;
    }

    private static void DriveIntoBackpressure(IoUringConnection connection)
    {
        // The application has not read anything, so the first chunk over the pause threshold leaves the flush pending.
        connection.OnRecvCompleteFromBuffer(new byte[PauseThreshold])
            .Should().Be(IoUringConnection.RecvOutcome.Deferred);
        connection.RecvRearmPending.Should().BeTrue();
    }

    [Fact]
    public void CompletionsPostedBeforeTheCancelTakesEffect_AreQueuedNotAborted()
    {
        if (!Ring.IsSupported) return;
        using var ring = new Ring(64);
        var closes = new List<ConnectionAbortedException?>();
        var connection = CreateConnection(ring, closes);
        try
        {
            DriveIntoBackpressure(connection);

            // 3 MiB already sitting in the CQ: more than the old 1 MiB limit.
            var chunk = new byte[ReceiveBufferSize];
            for (int i = 0; i < 1536; i++)
            {
                connection.OnRecvCompleteFromBuffer(chunk)
                    .Should().Be(IoUringConnection.RecvOutcome.Deferred);
            }

            closes.Should().BeEmpty("the data was already received from the peer and must not be thrown away");
        }
        finally
        {
            connection.ForceCleanupAfterRingClosed();
        }
    }

    [Fact]
    public void RunawayPeer_IsStillAbortedOnceTheQueueLimitIsExceeded()
    {
        if (!Ring.IsSupported) return;
        using var ring = new Ring(64);
        var closes = new List<ConnectionAbortedException?>();
        var connection = CreateConnection(ring, closes);
        try
        {
            DriveIntoBackpressure(connection);

            var chunk = new byte[ReceiveBufferSize];
            IoUringConnection.RecvOutcome outcome = IoUringConnection.RecvOutcome.Deferred;
            for (int i = 0; i < 64 * 1024 && outcome == IoUringConnection.RecvOutcome.Deferred; i++)
                outcome = connection.OnRecvCompleteFromBuffer(chunk);

            outcome.Should().Be(IoUringConnection.RecvOutcome.Closed);
            closes.Should().ContainSingle().Which.Should().NotBeNull();
        }
        finally
        {
            connection.ForceCleanupAfterRingClosed();
        }
    }

    [Fact]
    public void Ring_ReportsItsCompletionQueueSize()
    {
        if (!Ring.IsSupported) return;
        using var ring = new Ring(64);

        // The kernel sizes the CQ at twice the SQ by default; it must never be smaller.
        ring.CqEntries.Should().BeGreaterThanOrEqualTo(64u);
    }
}
