using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerSendRetryStateTests
{
    [Fact]
    public void RequestSendRetry_QueuesDistinctMarkers_AndRequestsWakeExactlyOncePerRequest()
    {
        int wakeCount = 0;
        var state = new ListenerSendRetryState(() => wakeCount++);
        const long firstSlot = 0;
        const long secondSlot = 1;
        const ushort firstGeneration = 1;
        const ushort secondGeneration = 2;

        state.RequestSendRetry(firstSlot, firstGeneration);
        state.RequestSendRetry(secondSlot, secondGeneration);

        state.SendRetryCount.Should().Be(2,
            "each distinct send retry request should add its own listener-owned retry marker");
        state.ContainsSendRetry(firstSlot, firstGeneration).Should().BeTrue(
            "the first queued send retry must remain addressable by slot and generation");
        state.ContainsSendRetry(secondSlot, secondGeneration).Should().BeTrue(
            "the second queued send retry must remain addressable by slot and generation");
        wakeCount.Should().Be(2,
            "each send retry request must publish exactly one listener wake after recording its marker");
    }

    [Fact]
    public void RequestSendRetry_DuplicateMarker_KeepsSingleMarker_AndStillRequestsWakePerCall()
    {
        int wakeCount = 0;
        var state = new ListenerSendRetryState(() => wakeCount++);

        state.RequestSendRetry(connectionId: 0, generation: 1);
        state.RequestSendRetry(connectionId: 0, generation: 1);

        state.SendRetryCount.Should().Be(1,
            "duplicate retries for the same slot generation should coalesce to one queued marker");
        state.ContainsSendRetry(connectionId: 0, generation: 1).Should().BeTrue(
            "coalescing must keep the queued marker addressable by slot and generation");
        wakeCount.Should().Be(2,
            "each request should still publish one wake even when the marker already exists");
    }

    [Fact]
    public void RunResumePass_ResumesOnlyMatchingGeneration()
    {
        var state = new ListenerSendRetryState(static () => { });
        var liveSlots = new[]
        {
            CreateLiveSlot(generation: 1),
            CreateLiveSlot(generation: 2),
        };
        var resumed = new List<ResumeSelection>();
        var adapter = new FakeSendRetryResumePassAdapter(liveSlots, resumed);

        state.RequestSendRetry(connectionId: 0, generation: 1);
        state.RequestSendRetry(connectionId: 1, generation: 1);

        state.RunResumePass(ref adapter);

        resumed.Should().ContainSingle().Which.Should().Be(new ResumeSelection(Slot: 0, Generation: 1),
            "only the queued retry whose generation still matches the live slot should resume");
        state.SendRetryCount.Should().Be(0,
            "the resume pass should clear both matched and stale markers after selection");
    }

    [Fact]
    public void RunResumePass_DropsQueuedRetryBeforeReusedSlotGenerationCanResume()
    {
        var state = new ListenerSendRetryState(static () => { });
        var liveSlots = new[]
        {
            CreateLiveSlot(generation: 2),
        };
        var resumed = new List<ResumeSelection>();
        var adapter = new FakeSendRetryResumePassAdapter(liveSlots, resumed);

        state.RequestSendRetry(connectionId: 0, generation: 1);

        state.RunResumePass(ref adapter);

        resumed.Should().BeEmpty(
            "queued send retries from a released slot generation must not resume the next occupant of that slot");
        state.SendRetryCount.Should().Be(0,
            "stale send retry markers should be cleared after the resume pass");
    }

    [Fact]
    public void RunResumePass_ReentrantRetryRequest_StaysQueuedForNextPass()
    {
        var state = new ListenerSendRetryState(static () => { });
        var liveSlots = new[]
        {
            CreateLiveSlot(generation: 1),
        };
        var firstPassResumed = new List<ResumeSelection>();
        var reentrantAdapter = new ReentrantSendRetryResumePassAdapter(
            liveSlots,
            firstPassResumed,
            state,
            reentrantConnectionId: 0,
            reentrantGeneration: 1);

        state.RequestSendRetry(connectionId: 0, generation: 1);

        state.RunResumePass(ref reentrantAdapter);

        firstPassResumed.Should().ContainSingle().Which.Should().Be(new ResumeSelection(Slot: 0, Generation: 1),
            "the first pass should resume only the marker that was already queued when the pass began");
        state.SendRetryCount.Should().Be(1,
            "a reentrant retry request should stay queued for the next pass instead of being lost");
        state.ContainsSendRetry(connectionId: 0, generation: 1).Should().BeTrue(
            "the reentrant retry request should remain addressable by slot and generation");

        var secondPassResumed = new List<ResumeSelection>();
        var secondPassAdapter = new FakeSendRetryResumePassAdapter(liveSlots, secondPassResumed);

        state.RunResumePass(ref secondPassAdapter);

        secondPassResumed.Should().ContainSingle().Which.Should().Be(new ResumeSelection(Slot: 0, Generation: 1),
            "the reentrant marker should resume on the next pass once the listener revisits the retry set");
        state.SendRetryCount.Should().Be(0,
            "the second pass should clear the reentrant retry marker after it resumes");
    }

    private static FakeConnectionSlot CreateLiveSlot(ushort generation) =>
        new()
        {
            IsLive = true,
            Generation = generation,
        };

    private static bool TryGetConnectionState(
        FakeConnectionSlot[] liveSlots,
        long connectionId,
        out ListenerSendRetryState.SendRetryConnectionState connectionState)
    {
        if ((uint)connectionId >= (uint)liveSlots.Length)
        {
            connectionState = default;
            return false;
        }

        ref var liveSlot = ref liveSlots[(int)connectionId];
        if (!liveSlot.IsLive)
        {
            connectionState = default;
            return false;
        }

        connectionState = new ListenerSendRetryState.SendRetryConnectionState(liveSlot.Generation);
        return true;
    }

    private readonly record struct ResumeSelection(long Slot, long Generation);

    private struct FakeConnectionSlot
    {
        internal bool IsLive;
        internal ushort Generation;
    }

    private struct FakeSendRetryResumePassAdapter(
        FakeConnectionSlot[] liveSlots,
        List<ResumeSelection> resumed)
        : ListenerSendRetryState.ISendRetryResumePassAdapter<FakeSendRetryResumePassAdapter>
    {
        private readonly FakeConnectionSlot[] _liveSlots = liveSlots;
        private readonly List<ResumeSelection> _resumed = resumed;

        public static bool TryGetConnectionState(
            ref FakeSendRetryResumePassAdapter self,
            long connectionId,
            out ListenerSendRetryState.SendRetryConnectionState connectionState)
        {
            return ListenerSendRetryStateTests.TryGetConnectionState(self._liveSlots, connectionId, out connectionState);
        }

        public static void ResumeSendRetry(
            ref FakeSendRetryResumePassAdapter self,
            long connectionId,
            in ListenerSendRetryState.SendRetryConnectionState connectionState)
        {
            self._resumed.Add(new ResumeSelection(connectionId, connectionState.Generation));
        }
    }

    private struct ReentrantSendRetryResumePassAdapter(
        FakeConnectionSlot[] liveSlots,
        List<ResumeSelection> resumed,
        ListenerSendRetryState state,
        long reentrantConnectionId,
        ushort reentrantGeneration)
        : ListenerSendRetryState.ISendRetryResumePassAdapter<ReentrantSendRetryResumePassAdapter>
    {
        private readonly FakeConnectionSlot[] _liveSlots = liveSlots;
        private readonly List<ResumeSelection> _resumed = resumed;
        private readonly ListenerSendRetryState _state = state;
        private readonly long _reentrantConnectionId = reentrantConnectionId;
        private readonly ushort _reentrantGeneration = reentrantGeneration;
        private bool _requestedRetry;

        public static bool TryGetConnectionState(
            ref ReentrantSendRetryResumePassAdapter self,
            long connectionId,
            out ListenerSendRetryState.SendRetryConnectionState connectionState)
        {
            return ListenerSendRetryStateTests.TryGetConnectionState(self._liveSlots, connectionId, out connectionState);
        }

        public static void ResumeSendRetry(
            ref ReentrantSendRetryResumePassAdapter self,
            long connectionId,
            in ListenerSendRetryState.SendRetryConnectionState connectionState)
        {
            self._resumed.Add(new ResumeSelection(connectionId, connectionState.Generation));
            if (!self._requestedRetry)
            {
                self._requestedRetry = true;
                self._state.RequestSendRetry(self._reentrantConnectionId, self._reentrantGeneration);
            }
        }
    }
}
