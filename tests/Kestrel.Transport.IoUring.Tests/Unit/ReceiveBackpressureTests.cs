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
        List<ConnectionAbortedException?> closeRequests,
        Action<long, ushort>? requestRecvResubmit = null,
        IoUringPipeScheduler? scheduler = null)
    {
        var connection = new IoUringConnection(
            connectionId: 0,
            socketFd: 42,
            fileIndex: -1,
            ring,
            remoteEndPoint: null,
            localEndPoint: null,
            receiveBufferSize: ReceiveBufferSize,
            transportScheduler: scheduler ?? new IoUringPipeScheduler(static () => { }),
            unsafeInlineScheduling: false,
            logger: NullLogger.Instance,
            useBufferRing: true)
        {
            Generation = 1,
        };
        connection.InitializeIoCallbacks(
            requestRecvResubmit ?? (static (_, _) => { }),
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
    public void EofWithNoBackpressure_IsNotDeferred()
    {
        if (!Ring.IsSupported) return;
        using var ring = new Ring(64);
        var connection = CreateConnection(ring, []);
        try
        {
            connection.TryDeferReceiveEof().Should().BeFalse();
            connection.ReceiveEofPending.Should().BeFalse();
        }
        finally
        {
            connection.ForceCleanupAfterRingClosed();
        }
    }

    [Fact]
    public async Task EofBehindQueuedData_IsDeferredUntilTheDataIsDelivered()
    {
        if (!Ring.IsSupported) return;
        using var ring = new Ring(64);
        var resubmit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new IoUringPipeScheduler(static () => { });
        var connection = CreateConnection(ring, [], (_, _) => resubmit.TrySetResult(), scheduler);
        try
        {
            DriveIntoBackpressure(connection);
            const int QueuedTail = 3 * ReceiveBufferSize;
            for (int i = 0; i < 3; i++)
            {
                connection.OnRecvCompleteFromBuffer(new byte[ReceiveBufferSize])
                    .Should().Be(IoUringConnection.RecvOutcome.Deferred);
            }

            connection.TryDeferReceiveEof().Should().BeTrue("data is still queued behind the paused pipe");
            connection.ReceiveEofPending.Should().BeTrue();

            // The application drains the pipe, which completes the pending flush and requests the resubmit.
            var input = connection.Transport.Input;
            var first = await input.ReadAsync();
            long delivered = first.Buffer.Length;
            input.AdvanceTo(first.Buffer.End);
            // Stands in for the IO loop, which runs the pipe's continuations through the scheduler.
            scheduler.DrainWorkItems();
            await resubmit.Task.WaitAsync(TimeSpan.FromSeconds(10));

            connection.ResumeReceiveAfterFlush().Should().Be(IoUringConnection.RecvOutcome.Continue);

            var second = await input.ReadAsync();
            delivered += second.Buffer.Length;
            input.AdvanceTo(second.Buffer.End);

            delivered.Should().Be(PauseThreshold + QueuedTail, "everything received before the EOF must reach the application");
            connection.ReceiveEofPending.Should().BeTrue("the listener completes the input only now, after the data");
        }
        finally
        {
            connection.ForceCleanupAfterRingClosed();
        }
    }

    [Fact]
    public void ConnectionIds_AreUniqueEvenWhenSlotAndGenerationRepeat()
    {
        if (!Ring.IsSupported) return;
        using var ring = new Ring(64);
        var first = CreateConnection(ring, []);
        var second = CreateConnection(ring, []);
        try
        {
            // Both use connectionId 0 / generation 1, as two workers (or listeners) do for their first connection.
            first.ConnectionId.Should().NotBe(second.ConnectionId);
            first.ConnectionId.Should().StartWith("iouring:");
        }
        finally
        {
            first.ForceCleanupAfterRingClosed();
            second.ForceCleanupAfterRingClosed();
        }
    }

    [Fact]
    public void PendingLimit_DoesNotWrapNearIntMaxValue()
    {
        IoUringConnection.ExceedsPendingRecvLimit(0, 1024, 8 * 1024 * 1024).Should().BeFalse();
        IoUringConnection.ExceedsPendingRecvLimit(8 * 1024 * 1024 - 1024, 1024, 8 * 1024 * 1024).Should().BeFalse();
        IoUringConnection.ExceedsPendingRecvLimit(8 * 1024 * 1024, 1, 8 * 1024 * 1024).Should().BeTrue();

        // An int sum would wrap negative here and be accepted even though it exceeds the cap.
        IoUringConnection.ExceedsPendingRecvLimit(int.MaxValue - 10, 100, int.MaxValue).Should().BeTrue();
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
