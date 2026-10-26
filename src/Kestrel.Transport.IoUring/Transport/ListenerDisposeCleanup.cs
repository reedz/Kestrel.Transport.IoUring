using System.Runtime.ExceptionServices;

namespace Kestrel.Transport.IoUring.Transport;

internal static class ListenerDisposeCleanup
{
    internal static void RunIsolatedPerItem<TItem>(
        IEnumerable<TItem> items,
        string stepName,
        Action<string, Action> runCleanupStep,
        Action<TItem> action)
    {
        foreach (var item in items)
            runCleanupStep(stepName, () => action(item));
    }

    internal static ValueTask DisposeRetryTimerAsync(Timer timer)
    {
        return timer.DisposeAsync();
    }

    internal static ValueTask DisposeRetryTimersThenRunEventFdTeardownAsync(
        Timer acceptRetryTimer,
        Timer recvRetryTimer,
        Func<string, Func<ValueTask>, ValueTask> runCleanupStepAsync,
        Func<ValueTask> eventFdTeardownAsync)
    {
        return RunAfterRetryTimersAsync(
            runCleanupStepAsync("accept retry timer dispose", () => DisposeRetryTimerAsync(acceptRetryTimer)),
            runCleanupStepAsync("recv retry timer dispose", () => DisposeRetryTimerAsync(recvRetryTimer)),
            eventFdTeardownAsync);
    }

    internal static async ValueTask RunAfterRetryTimersAsync(
        ValueTask acceptRetryTimerCleanup,
        ValueTask recvRetryTimerCleanup,
        Func<ValueTask> eventFdTeardownAsync)
    {
        List<Exception>? failures = null;

        await RunCleanupAsync(acceptRetryTimerCleanup).ConfigureAwait(false);
        await RunCleanupAsync(recvRetryTimerCleanup).ConfigureAwait(false);

        try
        {
            await eventFdTeardownAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, ex);
        }

        if (failures is null)
            return;

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
            return;
        }

        throw new AggregateException(failures);

        async ValueTask RunCleanupAsync(ValueTask cleanup)
        {
            try
            {
                await cleanup.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CaptureFailure(ref failures, ex);
            }
        }
    }

    private static void CaptureFailure(ref List<Exception>? failures, Exception failure)
    {
        if (failure is AggregateException aggregateException)
        {
            foreach (Exception innerException in aggregateException.Flatten().InnerExceptions)
                (failures ??= []).Add(innerException);

            return;
        }

        (failures ??= []).Add(failure);
    }
}
