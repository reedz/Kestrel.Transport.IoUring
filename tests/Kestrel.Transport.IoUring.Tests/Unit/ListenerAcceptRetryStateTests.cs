using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerAcceptRetryStateTests
{
    [Fact]
    public void IsDueNow_RetryNotRequired_IsNotDue()
    {
        ListenerAcceptRetryState.IsDueNow(
            retryRequired: false,
            acceptInFlight: false,
            stopping: false,
            cancellationRequested: false,
            now: 10,
            retryNotBeforeTimestamp: 10).Should().BeFalse(
            "without a queued retry request, the accept-retry seam must stay dormant regardless of the timestamp");
    }

    [Fact]
    public void IsDueNow_RequiredRetryWithAcceptAlreadyInFlight_IsNotDue()
    {
        ListenerAcceptRetryState.IsDueNow(
            retryRequired: true,
            acceptInFlight: true,
            stopping: false,
            cancellationRequested: false,
            now: 10,
            retryNotBeforeTimestamp: 10).Should().BeFalse(
            "the accept-retry seam must not claim due-now work while an accept is already in flight");
    }

    [Fact]
    public void IsDueNow_RequiredRetryWithoutAcceptInFlightAndDueTimestamp_IsDue()
    {
        ListenerAcceptRetryState.IsDueNow(
            retryRequired: true,
            acceptInFlight: false,
            stopping: false,
            cancellationRequested: false,
            now: 10,
            retryNotBeforeTimestamp: 10).Should().BeTrue(
            "once the retry is required, no accept is in flight, and the backoff has expired, production should see the accept retry as due-now");
    }

    [Fact]
    public void IsDueNow_FutureRetryTimestamp_IsNotDue()
    {
        ListenerAcceptRetryState.IsDueNow(
            retryRequired: true,
            acceptInFlight: false,
            stopping: false,
            cancellationRequested: false,
            now: 9,
            retryNotBeforeTimestamp: 10).Should().BeFalse(
            "accept retries must stay dormant until their backoff timestamp is actually due");
    }

    [Fact]
    public void GetMinComplete_FutureRetryBackoffWithoutOtherWork_BlocksForCompletion()
    {
        bool acceptRetryDueNow = ListenerAcceptRetryState.IsDueNow(
            retryRequired: true,
            acceptInFlight: false,
            stopping: false,
            cancellationRequested: false,
            now: 9,
            retryNotBeforeTimestamp: 10);

        acceptRetryDueNow.Should().BeFalse(
            "future accept backoff must keep the wait-policy input dormant until the retry timestamp is actually due");
        ListenerIoLoopWaitPolicy.GetMinComplete(
            schedulerHasWork: false,
            eventFdRetryPendingWithoutReadInFlight: false,
            acceptRetryDueNow: acceptRetryDueNow,
            recvRetryPassBlockedOnSq: false).Should().Be(1u,
            "a future accept backoff is not immediate IO-loop work, so the listener should still block for one completion");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IsDueNow_StoppingOrCancelled_IsNotDue(bool stopping, bool cancellationRequested)
    {
        ListenerAcceptRetryState.IsDueNow(
            retryRequired: true,
            acceptInFlight: false,
            stopping: stopping,
            cancellationRequested: cancellationRequested,
            now: 10,
            retryNotBeforeTimestamp: 10).Should().BeFalse(
            "stop/cancel paths must suppress accept retry work even when the retry would otherwise be due");
    }
}
