using FluentAssertions;
using Kestrel.Transport.IoUring;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerRecvRetryStateTests
{
    [Fact]
    public void OnConnectionReleased_PurgesQueuedRecvAndCancelRetryState()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;

        state.AddRecvRetry(slot);
        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 1));
        state.OnConnectionReleased(slot);

        state.RecvRetryCount.Should().Be(0,
            "releasing a slot should purge any queued recv retry marker");
        state.RecvCancelRetryCount.Should().Be(0,
            "releasing a slot should purge any queued recv-cancel retry marker");
        state.TryGetRecvCancelRetryOrigin(slot, out _).Should().BeFalse(
            "releasing a slot should also drop the queued recv-cancel retry origin");
    }

    [Fact]
    public void HasPendingRetryPassWork_CancelOnlyRetryCountsAsPending()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;

        state.HasPendingRetryPassWork.Should().BeFalse();

        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 1));

        state.HasPendingRetryPassWork.Should().BeTrue(
            "cancel-only retry markers must keep the listener's retry pass due-now state live even when no recv rearm marker is queued");
    }

    [Fact]
    public void RunRetryPass_FakePassAdapter_IssuesMatchingCancelRetryWithoutRecvRetryWork()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(generation: 1, recvEpoch: 1, hasRecvInFlight: true, usingBufferRing: true),
        };
        var cancelTargets = new List<CancelSelection>();
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, cancelTargets);

        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 1));

        state.RunRetryPass(ref adapter);

        cancelTargets.Should().ContainSingle().Which.Should().Be(
            new CancelSelection(
                Slot: slot,
                OriginGeneration: 1,
                OriginRecvEpoch: 1,
                LiveGeneration: 1,
                LiveRecvEpoch: 1),
            "matching generation and recv epoch should still submit the queued cancel retry");
        state.RecvCancelRetryCount.Should().Be(0,
            "a selected cancel-only retry should clear its queued marker");
        state.TryGetRecvCancelRetryOrigin(slot, out _).Should().BeFalse(
            "a selected cancel-only retry should also remove its stored origin");
        adapter.GetLiveState(slot).RecvCancelPending.Should().BeTrue();
    }

    [Fact]
    public void AddRecvCancelRetry_RefreshesOriginForLatestReceiveArm()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(generation: 1, recvEpoch: 2, hasRecvInFlight: true, usingBufferRing: true),
        };
        var cancelTargets = new List<CancelSelection>();
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, cancelTargets);

        state.AddRecvRetry(slot);
        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 1));
        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 2));

        state.RunRetryPass(ref adapter);

        cancelTargets.Should().ContainSingle().Which.OriginRecvEpoch.Should().Be(2,
            "a later retry marker for the same slot must replace the obsolete receive origin");
    }

    [Fact]
    public void RetryFailedRecvs_ProductionPassAdapter_IssuesMatchingCancelRetryWithoutRecvRetryWork()
    {
        if (!Ring.IsSupported)
            return;

        using var ring = new Ring(8);
        var scheduler = new IoUringPipeScheduler(static () => { });
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var connectionSlots = new IoUringConnection?[1];
        var connection = CreateConnection(slot, ring, scheduler);
        connectionSlots[(int)slot] = connection;

        connection.SubmitRecv().Should().BeTrue(
            "the focused Bug 3 witness needs a live in-flight recv before the cancel-only retry pass runs");
        connection.HasRecvInFlight.Should().BeTrue();
        connection.RecvEpoch.Should().Be(1);

        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(connection.Generation, connection.RecvEpoch));

        state.RetryFailedRecvs(connectionSlots, bufferRing: null, recvBufferGroupId: 0);

        connection.RecvCancelPending.Should().BeTrue(
            "a matching cancel-only retry should still issue through the production retry-pass adapter");
        state.RecvCancelRetryCount.Should().Be(0,
            "a selected cancel-only retry should clear the queued marker after issue");
    }

    [Fact]
    public void RetryFailedRecvs_ProductionPassAdapter_WhenRecvRearmSubmitHitsSqFull_SetsBlockedOnSq_AndRequestsNonBlockingWait()
    {
        if (!Ring.IsSupported)
            return;

        using var ring = new Ring(8);
        var scheduler = new IoUringPipeScheduler(static () => { });
        var state = new ListenerRecvRetryState();
        const long targetId = 1;
        var connectionSlots = new IoUringConnection?[2];
        var sqBlocker = CreateConnection(connectionId: 0, ring, scheduler);
        var target = CreateConnection(connectionId: targetId, ring, scheduler);
        connectionSlots[0] = sqBlocker;
        connectionSlots[(int)targetId] = target;

        int sqeCount = 0;
        while (sqBlocker.SubmitRecv())
            sqeCount++;

        sqeCount.Should().BePositive(
            "the RED wait witness needs a real queued SQE to exhaust the submission queue before the retry pass runs");
        target.HasRecvInFlight.Should().BeFalse(
            "the retry-pass rearm target must not already have a recv in flight");

        state.AddRecvRetry(targetId);
        state.RetryFailedRecvs(connectionSlots, bufferRing: null, recvBufferGroupId: 0);

        state.BlockedOnSq.Should().BeTrue(
            "a due retry pass that actually tries to queue a recv and loses to SQ-full should surface that blocked-on-SQ state for the shared wait seam");
        state.RecvRetryCount.Should().Be(1,
            "the failed rearm should remain queued for a later retry pass once SQ space returns");
        ListenerIoLoopWaitPolicy.GetMinComplete(
            schedulerHasWork: false,
            eventFdRetryPendingWithoutReadInFlight: false,
            acceptRetryDueNow: false,
            recvRetryPassBlockedOnSq: state.BlockedOnSq).Should().Be(0u,
            "a due retry pass that already lost a real SQ attempt must keep the listener on SubmitAndWait(0) until SQ space returns");
    }

    [Fact]
    public void RunRetryPass_CancelMarkerWithoutNewSqAttempt_DoesNotSetBlockedOnSq_OrRequestNonBlockingWait()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(
                generation: 1,
                recvEpoch: 1,
                hasRecvInFlight: true,
                usingBufferRing: true,
                recvCancelPending: true),
        };
        var cancelTargets = new List<CancelSelection>();
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, cancelTargets);

        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 1));

        state.RunRetryPass(ref adapter);

        state.HasPendingRetryPassWork.Should().BeTrue(
            "a queued cancel marker may remain pending when the matching cancel is already in flight");
        state.BlockedOnSq.Should().BeFalse(
            "retry-pass blocked-on-SQ must reflect an actual failed SQ submission attempt, not just a lingering marker or RecvCancelPending state");
        cancelTargets.Should().BeEmpty(
            "no new cancel SQE should be attempted while the connection already has a matching cancel in flight");
        ListenerIoLoopWaitPolicy.GetMinComplete(
            schedulerHasWork: false,
            eventFdRetryPendingWithoutReadInFlight: false,
            acceptRetryDueNow: false,
            recvRetryPassBlockedOnSq: state.BlockedOnSq).Should().Be(1u,
            "the wait policy must not spin merely because a cancel-only retry marker remains pending without a fresh SQ-full attempt");
    }

    [Fact]
    public void RunRetryPass_SuccessfulRecvRearmClearsBlockedOnSq()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(
                generation: 1,
                recvEpoch: 1,
                hasRecvInFlight: false,
                usingBufferRing: false,
                submitRecvSucceeds: false),
        };
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, new List<CancelSelection>());

        state.AddRecvRetry(slot);
        state.RunRetryPass(ref adapter);

        state.BlockedOnSq.Should().BeTrue(
            "the first pass should remember that a real recv rearm attempt failed with the SQ full");

        liveSlots[0].SubmitRecvSucceeds = true;
        state.RunRetryPass(ref adapter);

        state.BlockedOnSq.Should().BeFalse(
            "once a later due pass stages the recv successfully, the blocked-on-SQ signal must clear");
        state.RecvRetryCount.Should().Be(0,
            "a successfully staged recv retry should consume the queued marker");
        adapter.GetLiveState(slot).HasRecvInFlight.Should().BeTrue(
            "the successful retry pass should rearm receive work on the connection");
    }

    [Fact]
    public void RunRetryPass_DroppedLastRecvRetryMarkerClearsBlockedOnSq()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(
                generation: 1,
                recvEpoch: 1,
                hasRecvInFlight: false,
                usingBufferRing: false,
                submitRecvSucceeds: false),
        };
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, new List<CancelSelection>());

        state.AddRecvRetry(slot);
        state.RunRetryPass(ref adapter);
        state.BlockedOnSq.Should().BeTrue();

        liveSlots[0].IsLive = false;
        state.RunRetryPass(ref adapter);

        state.BlockedOnSq.Should().BeFalse(
            "dropping the final queued retry marker must clear the shared blocked-on-SQ signal");
        state.RecvRetryCount.Should().Be(0);
    }

    [Fact]
    public void OnConnectionClosing_LastRecvRetryMarkerClearsBlockedOnSq()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(
                generation: 1,
                recvEpoch: 1,
                hasRecvInFlight: false,
                usingBufferRing: false,
                submitRecvSucceeds: false),
        };
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, new List<CancelSelection>());

        state.AddRecvRetry(slot);
        state.RunRetryPass(ref adapter);
        state.BlockedOnSq.Should().BeTrue();

        state.OnConnectionClosing(slot);

        state.BlockedOnSq.Should().BeFalse(
            "closing a slot should clear blocked-on-SQ once that closing transition removes the last queued recv retry marker");
        state.RecvRetryCount.Should().Be(0);
    }

    [Fact]
    public void OnConnectionReleased_LastCancelRetryMarkerClearsBlockedOnSq()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(
                generation: 1,
                recvEpoch: 1,
                hasRecvInFlight: true,
                usingBufferRing: true,
                submitRecvCancelSucceeds: false),
        };
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, new List<CancelSelection>());

        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 1));
        state.RunRetryPass(ref adapter);
        state.BlockedOnSq.Should().BeTrue();

        state.OnConnectionReleased(slot);

        state.BlockedOnSq.Should().BeFalse(
            "releasing a slot should clear blocked-on-SQ once the final queued cancel retry marker is purged");
        state.RecvCancelRetryCount.Should().Be(0);
    }

    [Fact]
    public void RunRetryPass_WithoutPendingWork_ClearsBlockedOnSq()
    {
        var state = new ListenerRecvRetryState();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var blockedField = typeof(ListenerRecvRetryState).GetField("_blockedOnSq", flags);
        blockedField.Should().NotBeNull();
        blockedField!.SetValue(state, true);
        var adapter = new FakeRecvRetryPassAdapter([], new List<CancelSelection>());

        state.RunRetryPass(ref adapter);

        state.BlockedOnSq.Should().BeFalse(
            "an empty retry pass must clear any leftover blocked-on-SQ signal before the next wait decision");
    }

    [Fact]
    public void ClearBlockedOnSq_ExplicitlyClearsSignal()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(
                generation: 1,
                recvEpoch: 1,
                hasRecvInFlight: false,
                usingBufferRing: false,
                submitRecvSucceeds: false),
        };
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, new List<CancelSelection>());

        state.AddRecvRetry(slot);
        state.RunRetryPass(ref adapter);
        state.BlockedOnSq.Should().BeTrue();

        state.ClearBlockedOnSq();

        state.BlockedOnSq.Should().BeFalse(
            "the explicit listener wait seam needs a way to clear stale blocked-on-SQ state once a retry pass is no longer due");
    }

    [Fact]
    public void RunRetryPass_DropsQueuedCancelRetryBeforeReusedSlotGenerationCanBeSelected()
    {
        var state = new ListenerRecvRetryState();
        const long reusedSlot = 0;
        const long gateSlot = 1;
        var liveSlots = new[]
        {
            CreateLiveSlot(generation: 2, recvEpoch: 1, hasRecvInFlight: true, usingBufferRing: true),
            CreateLiveSlot(
                generation: 9,
                recvEpoch: 1,
                hasRecvInFlight: false,
                usingBufferRing: true,
                submitBufferRingRecvSucceeds: true),
        };
        var cancelTargets = new List<CancelSelection>();
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, cancelTargets);

        state.OnConnectionClosing(reusedSlot);
        state.OnConnectionReleased(reusedSlot);
        state.AddRecvCancelRetry(
            reusedSlot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 1));
        state.AddRecvRetry(gateSlot);

        state.RunRetryPass(ref adapter);

        cancelTargets.Should().BeEmpty(
            "queued cancel retries from a released slot generation must not target the next occupant of that slot");
    }

    [Fact]
    public void RunRetryPass_DropsQueuedCancelRetryBeforeLaterSameGenerationRecvEpochCanBeSelected()
    {
        var state = new ListenerRecvRetryState();
        const long slot = 0;
        var liveSlots = new[]
        {
            CreateLiveSlot(
                generation: 1,
                recvEpoch: 1,
                hasRecvInFlight: false,
                usingBufferRing: true,
                submitBufferRingRecvSucceeds: true),
        };
        var cancelTargets = new List<CancelSelection>();
        var adapter = new FakeRecvRetryPassAdapter(liveSlots, cancelTargets);

        state.AddRecvRetry(slot);
        state.AddRecvCancelRetry(
            slot,
            new ListenerRecvRetryState.RecvCancelRetryOrigin(generation: 1, recvEpoch: 1));

        state.RunRetryPass(ref adapter);

        adapter.GetLiveState(slot).RecvEpoch.Should().Be(2UL);

        cancelTargets.Should().BeEmpty(
            "queued cancel retries must not target a later receive arm in the same slot generation");
    }

    private static FakeConnectionSlot CreateLiveSlot(
        ushort generation,
        ulong recvEpoch,
        bool hasRecvInFlight,
        bool usingBufferRing,
        bool isClosing = false,
        bool recvCancelPending = false,
        bool submitRecvSucceeds = true,
        bool submitBufferRingRecvSucceeds = true,
        bool submitRecvCancelSucceeds = true) =>
        new()
        {
            IsLive = true,
            Generation = generation,
            RecvEpoch = recvEpoch,
            IsClosing = isClosing,
            HasRecvInFlight = hasRecvInFlight,
            RecvCancelPending = recvCancelPending,
            UsingBufferRing = usingBufferRing,
            SubmitRecvSucceeds = submitRecvSucceeds,
            SubmitBufferRingRecvSucceeds = submitBufferRingRecvSucceeds,
            SubmitRecvCancelSucceeds = submitRecvCancelSucceeds,
        };

    private readonly record struct CancelSelection(
        long Slot,
        ushort OriginGeneration,
        ulong OriginRecvEpoch,
        ushort LiveGeneration,
        ulong LiveRecvEpoch);

    private static IoUringConnection CreateConnection(
        long connectionId,
        Ring ring,
        IoUringPipeScheduler scheduler) =>
        new(
            connectionId: connectionId,
            socketFd: 42 + (int)connectionId,
            fileIndex: -1,
            ring,
            remoteEndPoint: null,
            localEndPoint: null,
            receiveBufferSize: 1,
            transportScheduler: scheduler,
            unsafeInlineScheduling: false,
            logger: NullLogger.Instance,
            useBufferRing: false)
        {
            Generation = 1,
        };

    private struct FakeConnectionSlot
    {
        internal bool IsLive;
        internal ushort Generation;
        internal ulong RecvEpoch;
        internal bool IsClosing;
        internal bool HasRecvInFlight;
        internal bool RecvCancelPending;
        internal bool UsingBufferRing;
        internal bool SubmitRecvSucceeds;
        internal bool SubmitBufferRingRecvSucceeds;
        internal bool SubmitRecvCancelSucceeds;

        internal ListenerRecvRetryState.RecvRetryConnectionState ToLiveState() =>
            new(
                Generation,
                RecvEpoch,
                IsClosing,
                HasRecvInFlight,
                RecvCancelPending,
                UsingBufferRing);
    }

    private struct FakeRecvRetryPassAdapter(
        FakeConnectionSlot[] liveSlots,
        List<CancelSelection> cancelSelections)
        : ListenerRecvRetryState.IRecvRetryPassAdapter<FakeRecvRetryPassAdapter>
    {
        private readonly FakeConnectionSlot[] _liveSlots = liveSlots;
        private readonly List<CancelSelection> _cancelSelections = cancelSelections;

        internal ListenerRecvRetryState.RecvRetryConnectionState GetLiveState(long connectionId) =>
            _liveSlots[(int)connectionId].ToLiveState();

        public static bool TryGetRecvState(
            ref FakeRecvRetryPassAdapter self,
            long connectionId,
            out ListenerRecvRetryState.RecvRetryConnectionState connectionState)
        {
            if ((uint)connectionId >= (uint)self._liveSlots.Length)
            {
                connectionState = default;
                return false;
            }

            ref var liveSlot = ref self._liveSlots[(int)connectionId];
            if (!liveSlot.IsLive)
            {
                connectionState = default;
                return false;
            }

            connectionState = liveSlot.ToLiveState();
            return true;
        }

        public static bool SubmitRecv(
            ref FakeRecvRetryPassAdapter self,
            long connectionId,
            in ListenerRecvRetryState.RecvRetryConnectionState connectionState) =>
            TryArmRecv(ref self._liveSlots[(int)connectionId], useBufferRingPath: false);

        public static bool SubmitBufferRingRecv(
            ref FakeRecvRetryPassAdapter self,
            long connectionId,
            in ListenerRecvRetryState.RecvRetryConnectionState connectionState) =>
            TryArmRecv(ref self._liveSlots[(int)connectionId], useBufferRingPath: true);

        public static bool SubmitRecvCancel(
            ref FakeRecvRetryPassAdapter self,
            long connectionId,
            in ListenerRecvRetryState.RecvRetryConnectionState connectionState,
            in ListenerRecvRetryState.RecvCancelRetryOrigin origin)
        {
            ref var liveSlot = ref self._liveSlots[(int)connectionId];
            if (!liveSlot.SubmitRecvCancelSucceeds)
                return false;

            liveSlot.RecvCancelPending = true;
            self._cancelSelections.Add(
                new CancelSelection(
                    connectionId,
                    origin.Generation,
                    origin.RecvEpoch,
                    connectionState.Generation,
                    connectionState.RecvEpoch));
            return true;
        }

        private static bool TryArmRecv(ref FakeConnectionSlot liveSlot, bool useBufferRingPath)
        {
            bool succeeds = useBufferRingPath
                ? liveSlot.SubmitBufferRingRecvSucceeds
                : liveSlot.SubmitRecvSucceeds;
            if (!succeeds)
                return false;

            liveSlot.HasRecvInFlight = true;
            liveSlot.RecvEpoch++;
            return true;
        }
    }
}
