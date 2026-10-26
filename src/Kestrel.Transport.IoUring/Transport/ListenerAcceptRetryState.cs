namespace Kestrel.Transport.IoUring.Transport;

/// <summary>Shared listener accept-retry due-now policy.</summary>
internal static class ListenerAcceptRetryState
{
    internal static bool IsDueNow(
        bool retryRequired,
        bool acceptInFlight,
        bool stopping,
        bool cancellationRequested,
        long now,
        long retryNotBeforeTimestamp) =>
        retryRequired &&
        !acceptInFlight &&
        !stopping &&
        !cancellationRequested &&
        now >= retryNotBeforeTimestamp;
}
