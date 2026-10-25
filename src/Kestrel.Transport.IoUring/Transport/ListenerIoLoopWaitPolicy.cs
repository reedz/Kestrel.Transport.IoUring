namespace Kestrel.Transport.IoUring.Transport;

/// <summary>Shared listener IO-loop wait policy.</summary>
internal static class ListenerIoLoopWaitPolicy
{
    internal static uint GetMinComplete(
        bool schedulerHasWork,
        bool eventFdRetryPendingWithoutReadInFlight,
        bool acceptRetryDueNow,
        bool recvRetryPassBlockedOnSq)
        => (schedulerHasWork ||
            eventFdRetryPendingWithoutReadInFlight ||
            acceptRetryDueNow ||
            recvRetryPassBlockedOnSq)
            ? 0u
            : 1u;
}
