using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerEventFdReadStateTests
{
    [Fact]
    public void GetMinComplete_SuccessfulArmedIdleState_BlocksForCompletion()
    {
        var state = new ListenerEventFdReadState();
        var adapter = new FakeEventFdReadAdapter(canSubmitEventFdRead: true);

        state.TrySubmitEventFdRead(ref adapter).Should().BeTrue(
            "the listener should arm its eventfd read when submission succeeds");
        state.ReadInFlight.Should().BeTrue();
        state.RetryRequired.Should().BeFalse();

        ListenerIoLoopWaitPolicy.GetMinComplete(
            schedulerHasWork: false,
            eventFdRetryPendingWithoutReadInFlight: state.RetryPendingWithoutReadInFlight,
            acceptRetryDueNow: false,
            recvRetryPassBlockedOnSq: false).Should().Be(1u,
            "a scheduler-idle loop with an armed listener eventfd read should block for one completion instead of spinning");
    }

    [Fact]
    public void GetMinComplete_SchedulerWork_StaysNonBlocking()
    {
        ListenerIoLoopWaitPolicy.GetMinComplete(
            schedulerHasWork: true,
            eventFdRetryPendingWithoutReadInFlight: false,
            acceptRetryDueNow: false,
            recvRetryPassBlockedOnSq: false).Should().Be(0u,
            "queued scheduler work is immediate IO-loop work and must not block behind SubmitAndWait(1)");
    }

    [Fact]
    public void RetryPendingEventFdRead_WhenSqSpaceReturns_ExplicitRetryRearmsAndClearsRetry()
    {
        var state = new ListenerEventFdReadState();
        var adapter = new FakeEventFdReadAdapter(canSubmitEventFdRead: true);

        state.TrySubmitEventFdRead(ref adapter).Should().BeTrue(
            "the initial listener eventfd read should arm when SQ space is available");
        state.ReadInFlight.Should().BeTrue();

        state.OnEventFdReadCompletion();

        adapter.CanSubmitEventFdRead = false;

        state.TrySubmitEventFdRead(ref adapter).Should().BeFalse(
            "the post-completion rearm should stay pending when the SQ is full");
        state.ReadInFlight.Should().BeFalse();
        state.RetryRequired.Should().BeTrue();

        adapter.CanSubmitEventFdRead = true;

        state.RetryPendingEventFdRead(ref adapter);

        state.ReadInFlight.Should().BeTrue(
            "once SQ space returns, the top-of-loop control retry should be able to rearm the listener eventfd read");
        state.RetryRequired.Should().BeFalse(
            "a successful explicit retry should clear the queued eventfd rearm requirement");
        ListenerIoLoopWaitPolicy.GetMinComplete(
            schedulerHasWork: false,
            eventFdRetryPendingWithoutReadInFlight: state.RetryPendingWithoutReadInFlight,
            acceptRetryDueNow: false,
            recvRetryPassBlockedOnSq: false).Should().Be(1u,
            "after the retry successfully rearms the listener eventfd read, scheduler-idle waits should return to SubmitAndWait(1)");
    }

    [Fact]
    public void GetMinComplete_RetryRequiredWithoutReadInFlight_StaysNonBlocking()
    {
        var state = new ListenerEventFdReadState();
        var adapter = new FakeEventFdReadAdapter(canSubmitEventFdRead: true);

        state.TrySubmitEventFdRead(ref adapter).Should().BeTrue(
            "the focused Bug 5 witness starts from the real listener eventfd read being armed");

        state.OnEventFdReadCompletion();

        // Model the essential full-SQ state: the retry would still fail here, so
        // SubmitAndWait(0) is the enter that can free SQ space.
        adapter.CanSubmitEventFdRead = false;

        state.TrySubmitEventFdRead(ref adapter).Should().BeFalse(
            "handling the eventfd CQE should record a retry requirement when the immediate rearm finds the SQ full");
        state.ReadInFlight.Should().BeFalse(
            "a failed rearm leaves no eventfd read armed before the next wait decision");
        state.RetryRequired.Should().BeTrue(
            "a failed rearm must stay pending for the next control retry or wait decision");
        ListenerIoLoopWaitPolicy.GetMinComplete(
            schedulerHasWork: false,
            eventFdRetryPendingWithoutReadInFlight: state.RetryPendingWithoutReadInFlight,
            acceptRetryDueNow: false,
            recvRetryPassBlockedOnSq: false).Should().Be(0u,
            "once the listener has no eventfd read in flight but still owes a retry, scheduler-idle waits must stay on SubmitAndWait(0) until the top-of-loop control retry succeeds");
    }

    [Fact]
    public void GetMinComplete_AcceptRetryDueNowWithoutOtherWork_StaysNonBlocking()
    {
        ListenerIoLoopWaitPolicy.GetMinComplete(
            schedulerHasWork: false,
            eventFdRetryPendingWithoutReadInFlight: false,
            acceptRetryDueNow: true,
            recvRetryPassBlockedOnSq: false).Should().Be(0u,
            "once a real accept retry is due now, the listener must stay on SubmitAndWait(0) until that accept is staged");
    }

    private struct FakeEventFdReadAdapter(bool canSubmitEventFdRead)
        : ListenerEventFdReadState.IEventFdReadSubmitAdapter<FakeEventFdReadAdapter>
    {
        internal bool CanSubmitEventFdRead = canSubmitEventFdRead;

        public static bool TrySubmitEventFdRead(ref FakeEventFdReadAdapter self) =>
            self.CanSubmitEventFdRead;
    }
}
