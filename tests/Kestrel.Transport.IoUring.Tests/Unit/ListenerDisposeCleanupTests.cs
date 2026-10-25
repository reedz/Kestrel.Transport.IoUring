using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerDisposeCleanupTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void RunIsolatedPerItem_WhenFirstCloseThrows_StillRunsLaterCloseAndRemainingCleanupInOrder()
    {
        List<string> trace = [];
        List<string> failures = [];

        void RunCleanupStep(string stepName, Action action)
        {
            trace.Add($"start:{stepName}");
            try
            {
                action();
                trace.Add($"success:{stepName}");
            }
            catch (Exception ex)
            {
                failures.Add($"{stepName}:{ex.Message}");
                trace.Add($"failure:{stepName}:{ex.Message}");
            }
        }

        ListenerDisposeCleanup.RunIsolatedPerItem(
            ["first", "second"],
            "connection close",
            RunCleanupStep,
            connection =>
            {
                trace.Add($"close:{connection}");
                if (connection == "first")
                    throw new InvalidOperationException("simulated first close failure");
            });

        RunCleanupStep("final completion snapshot drain", () => trace.Add("drain"));
        RunCleanupStep("ring dispose", () => trace.Add("ring"));
        ListenerDisposeCleanup.RunIsolatedPerItem(
            ["first", "second"],
            "connection force cleanup",
            RunCleanupStep,
            connection => trace.Add($"force:{connection}"));
        RunCleanupStep("listener cancellation source dispose", () => trace.Add("cts"));

        failures.Should().Equal(
            [
                "connection close:simulated first close failure",
            ]);
        trace.Should().Equal(
            [
                "start:connection close",
                "close:first",
                "failure:connection close:simulated first close failure",
                "start:connection close",
                "close:second",
                "success:connection close",
                "start:final completion snapshot drain",
                "drain",
                "success:final completion snapshot drain",
                "start:ring dispose",
                "ring",
                "success:ring dispose",
                "start:connection force cleanup",
                "force:first",
                "success:connection force cleanup",
                "start:connection force cleanup",
                "force:second",
                "success:connection force cleanup",
                "start:listener cancellation source dispose",
                "cts",
                "success:listener cancellation source dispose",
            ],
            because: "a throwing ConnectionClosed callback during the first socket close must not stop later per-connection close attempts or later dispose cleanup steps");
    }

    [Fact, Trait("Category", "Red")]
    public async Task DisposeRetryTimerAsync_WhenCallbackIsInFlight_DoesNotReportDisposeCompleteUntilCallbackReturns()
    {
        var trace = new ConcurrentQueue<string>();
        var callbackStarted = CreateSignal();
        var callbackExited = CreateSignal();
        using var allowCallbackExit = new ManualResetEvent(false);
        using var timer = new Timer(
            static state =>
            {
                var callbackState = (BlockingTimerCallbackState)state!;
                callbackState.Started.TrySetResult(true);
                callbackState.AllowExit.WaitOne();
                callbackState.Trace.Enqueue("callback-exit");
                callbackState.Exited.TrySetResult(true);
            },
            new BlockingTimerCallbackState(trace, callbackStarted, callbackExited, allowCallbackExit),
            Timeout.Infinite,
            Timeout.Infinite);

        timer.Change(0, Timeout.Infinite);
        await callbackStarted.Task.WaitAsync(TestTimeout);

        bool cleanupStayedIncomplete = false;
        Task cleanupTask = Task.CompletedTask;
        Task cleanupCompleteMarkerTask = Task.CompletedTask;
        try
        {
            cleanupTask = ListenerDisposeCleanup.DisposeRetryTimerAsync(timer).AsTask();
            cleanupCompleteMarkerTask = cleanupTask.ContinueWith(
                static (_, state) => ((ConcurrentQueue<string>)state!).Enqueue("cleanup-complete"),
                trace,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            cleanupStayedIncomplete = !cleanupTask.IsCompleted;

            allowCallbackExit.Set();
            await cleanupTask.WaitAsync(TestTimeout);
            await callbackExited.Task.WaitAsync(TestTimeout);
            await cleanupCompleteMarkerTask.WaitAsync(TestTimeout);
        }
        finally
        {
            allowCallbackExit.Set();

            if (callbackStarted.Task.IsCompleted)
                await callbackExited.Task.WaitAsync(TestTimeout);

            await cleanupTask.WaitAsync(TestTimeout);
            await cleanupCompleteMarkerTask.WaitAsync(TestTimeout);
        }

        using var assertions = new AssertionScope();

        cleanupStayedIncomplete.Should().BeTrue(
            because: "Timer.Dispose must not report completion while a retry-timer callback is still running before eventfd teardown continues");
        trace.ToArray().Should().Equal(
            [
                "callback-exit",
                "cleanup-complete",
            ],
            because: "the callback return must be observed before retry-timer cleanup completion, not merely sometime later");
    }

    [Fact]
    public async Task DisposeRetryTimerAsync_WhenNoCallbackIsInFlight_CompletesSynchronously()
    {
        using var timer = new Timer(static _ => { }, null, Timeout.Infinite, Timeout.Infinite);

        Task cleanupTask = ListenerDisposeCleanup.DisposeRetryTimerAsync(timer).AsTask();

        cleanupTask.IsCompletedSuccessfully.Should().BeTrue(
            because: "a retry timer with no active callback has nothing left to quiesce");
        await cleanupTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task DisposeRetryTimerAsync_WhenCalledAgainAfterCompletion_FollowsTimerDisposeContract()
    {
        using var timer = new Timer(static _ => { }, null, Timeout.Infinite, Timeout.Infinite);

        Task firstCleanup = ListenerDisposeCleanup.DisposeRetryTimerAsync(timer).AsTask();
        await firstCleanup.WaitAsync(TestTimeout);

        Task secondCleanup = ListenerDisposeCleanup.DisposeRetryTimerAsync(timer).AsTask();

        secondCleanup.IsCompletedSuccessfully.Should().BeTrue(
            because: "Timer.Dispose permits repeated cleanup calls after the timer is already disposed");
        await secondCleanup.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task DisposeRetryTimersThenRunEventFdTeardownAsync_WaitsForBothTimerCleanupAwaitablesBeforeStartingEventFdTeardown()
    {
        var trace = new ConcurrentQueue<string>();
        var acceptCleanupRelease = CreateSignal();
        var acceptCleanupCompleted = CreateSignal();
        var recvCleanupRelease = CreateSignal();
        var recvCleanupCompleted = CreateSignal();
        var eventFdTeardownStarted = CreateSignal();
        var allowEventFdTeardownExit = CreateSignal();
        using var acceptRetryTimer = new Timer(static _ => { }, null, Timeout.Infinite, Timeout.Infinite);
        using var recvRetryTimer = new Timer(static _ => { }, null, Timeout.Infinite, Timeout.Infinite);

        Task cleanupTask = ListenerDisposeCleanup.DisposeRetryTimersThenRunEventFdTeardownAsync(
            acceptRetryTimer,
            recvRetryTimer,
            async (stepName, cleanupAsync) =>
            {
                string prefix;
                TaskCompletionSource<bool> release;
                TaskCompletionSource<bool> completed;

                if (stepName == "accept retry timer dispose")
                {
                    prefix = "accept";
                    release = acceptCleanupRelease;
                    completed = acceptCleanupCompleted;
                }
                else if (stepName == "recv retry timer dispose")
                {
                    prefix = "recv";
                    release = recvCleanupRelease;
                    completed = recvCleanupCompleted;
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected cleanup step: {stepName}");
                }

                trace.Enqueue($"{prefix}-timer-dispose-enter");
                await cleanupAsync().ConfigureAwait(false);
                trace.Enqueue($"{prefix}-timer-dispose-return");
                await release.Task.ConfigureAwait(false);
                trace.Enqueue($"{prefix}-timer-cleanup-complete");
                completed.TrySetResult(true);
            },
            async () =>
            {
                trace.Enqueue("eventfd-teardown-enter");
                eventFdTeardownStarted.TrySetResult(true);
                await allowEventFdTeardownExit.Task.ConfigureAwait(false);
                trace.Enqueue("eventfd-teardown-exit");
            }).AsTask();

        cleanupTask.IsCompleted.Should().BeFalse(
            because: "eventfd teardown must stay behind both retry-timer cleanup awaitables");
        trace.ToArray().Should().Equal(
            [
                "accept-timer-dispose-enter",
                "accept-timer-dispose-return",
                "recv-timer-dispose-enter",
                "recv-timer-dispose-return",
            ],
            because: "the production helper should issue both timer-dispose steps before either cleanup awaitable completes");

        acceptCleanupRelease.TrySetResult(true);
        await acceptCleanupCompleted.Task.WaitAsync(TestTimeout);

        cleanupTask.IsCompleted.Should().BeFalse(
            because: "eventfd teardown must still wait for the recv retry timer cleanup awaitable");
        eventFdTeardownStarted.Task.IsCompleted.Should().BeFalse(
            because: "starting eventfd teardown after only one timer cleanup would reorder the syscalls");

        recvCleanupRelease.TrySetResult(true);
        await recvCleanupCompleted.Task.WaitAsync(TestTimeout);
        await eventFdTeardownStarted.Task.WaitAsync(TestTimeout);

        cleanupTask.IsCompleted.Should().BeFalse(
            because: "the composed helper must still await the deferred eventfd teardown callback after both timer cleanups complete");

        allowEventFdTeardownExit.TrySetResult(true);
        await cleanupTask.WaitAsync(TestTimeout);

        trace.ToArray().Should().Equal(
            [
                "accept-timer-dispose-enter",
                "accept-timer-dispose-return",
                "recv-timer-dispose-enter",
                "recv-timer-dispose-return",
                "accept-timer-cleanup-complete",
                "recv-timer-cleanup-complete",
                "eventfd-teardown-enter",
                "eventfd-teardown-exit",
            ],
            because: "eventfd teardown must begin only after both retry-timer cleanup awaitables finish");
    }

    [Fact, Trait("Category", "Red")]
    public async Task RunAfterRetryTimersAsync_WhenFirstCleanupFails_StillAwaitsSecondCleanupAndEventFdTeardownBeforeSurfacingFailure()
    {
        var trace = new ConcurrentQueue<string>();
        var allowAcceptFailure = CreateSignal();
        var allowRecvCleanupExit = CreateSignal();
        var recvCleanupCompleted = CreateSignal();
        var eventFdTeardownStarted = CreateSignal();
        var allowEventFdTeardownExit = CreateSignal();
        var eventFdTeardownCompleted = CreateSignal();

        Task acceptCleanupTask = AcceptCleanupAsync();
        Task recvCleanupTask = RecvCleanupAsync();
        Task cleanupTask = ListenerDisposeCleanup.RunAfterRetryTimersAsync(
            new ValueTask(acceptCleanupTask),
            new ValueTask(recvCleanupTask),
            EventFdTeardownAsync).AsTask();

        try
        {
            cleanupTask.IsCompleted.Should().BeFalse(
                because: "the composed helper must stay pending until it has awaited both timer cleanup tasks and the deferred eventfd teardown");
            trace.ToArray().Should().Equal(
                [
                    "accept-timer-cleanup-enter",
                    "recv-timer-cleanup-enter",
                ],
                because: "both timer cleanup tasks are created before the composed helper begins awaiting them");

            allowAcceptFailure.TrySetResult(true);
            var acceptFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => acceptCleanupTask.WaitAsync(TestTimeout));
            acceptFailure.Message.Should().Be("simulated accept retry timer cleanup failure");

            cleanupTask.IsCompleted.Should().BeFalse(
                because: "the first timer cleanup failure must stay pending until the second cleanup and eventfd teardown mandatory ordering complete");
            eventFdTeardownStarted.Task.IsCompleted.Should().BeFalse(
                because: "eventfd teardown must still stay behind the second timer cleanup even when the first cleanup fails");

            allowRecvCleanupExit.TrySetResult(true);
            await recvCleanupCompleted.Task.WaitAsync(TestTimeout);
            await eventFdTeardownStarted.Task.WaitAsync(TestTimeout);

            cleanupTask.IsCompleted.Should().BeFalse(
                because: "surfacing the first cleanup failure must still wait for the deferred eventfd teardown callback to return");

            allowEventFdTeardownExit.TrySetResult(true);
            await eventFdTeardownCompleted.Task.WaitAsync(TestTimeout);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => cleanupTask.WaitAsync(TestTimeout));
            failure.Message.Should().Be("simulated accept retry timer cleanup failure");

            trace.ToArray().Should().Equal(
                [
                    "accept-timer-cleanup-enter",
                    "recv-timer-cleanup-enter",
                    "accept-timer-cleanup-throw",
                    "recv-timer-cleanup-exit",
                    "eventfd-teardown-enter",
                    "eventfd-teardown-exit",
                ],
                because: "the second timer cleanup and eventfd teardown must still run before the first cleanup failure is surfaced");
        }
        finally
        {
            allowAcceptFailure.TrySetResult(true);
            allowRecvCleanupExit.TrySetResult(true);
            allowEventFdTeardownExit.TrySetResult(true);

            try
            {
                await acceptCleanupTask.WaitAsync(TestTimeout);
            }
            catch
            {
            }

            await recvCleanupTask.WaitAsync(TestTimeout);

            if (eventFdTeardownStarted.Task.IsCompleted)
                await eventFdTeardownCompleted.Task.WaitAsync(TestTimeout);

            try
            {
                await cleanupTask.WaitAsync(TestTimeout);
            }
            catch
            {
            }
        }

        async Task AcceptCleanupAsync()
        {
            trace.Enqueue("accept-timer-cleanup-enter");
            await allowAcceptFailure.Task.ConfigureAwait(false);
            trace.Enqueue("accept-timer-cleanup-throw");
            throw new InvalidOperationException("simulated accept retry timer cleanup failure");
        }

        async Task RecvCleanupAsync()
        {
            trace.Enqueue("recv-timer-cleanup-enter");
            await allowRecvCleanupExit.Task.ConfigureAwait(false);
            trace.Enqueue("recv-timer-cleanup-exit");
            recvCleanupCompleted.TrySetResult(true);
        }

        async ValueTask EventFdTeardownAsync()
        {
            trace.Enqueue("eventfd-teardown-enter");
            eventFdTeardownStarted.TrySetResult(true);
            await allowEventFdTeardownExit.Task.ConfigureAwait(false);
            trace.Enqueue("eventfd-teardown-exit");
            eventFdTeardownCompleted.TrySetResult(true);
        }
    }

    [Fact, Trait("Category", "Red")]
    public async Task RunAfterRetryTimersAsync_WhenSecondCleanupFails_StillRunsEventFdTeardownBeforeSurfacingFailure()
    {
        var trace = new ConcurrentQueue<string>();
        var allowRecvFailure = CreateSignal();
        var eventFdTeardownStarted = CreateSignal();
        var allowEventFdTeardownExit = CreateSignal();
        var eventFdTeardownCompleted = CreateSignal();

        Task recvCleanupTask = RecvCleanupAsync();
        Task cleanupTask = ListenerDisposeCleanup.RunAfterRetryTimersAsync(
            ValueTask.CompletedTask,
            new ValueTask(recvCleanupTask),
            EventFdTeardownAsync).AsTask();

        try
        {
            allowRecvFailure.TrySetResult(true);
            var recvFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => recvCleanupTask.WaitAsync(TestTimeout));
            recvFailure.Message.Should().Be("simulated recv retry timer cleanup failure");

            Task firstCompletion = await Task.WhenAny(
                eventFdTeardownStarted.Task,
                cleanupTask).WaitAsync(TestTimeout);
            firstCompletion.Should().BeSameAs(
                eventFdTeardownStarted.Task,
                because: "a failure from the second timer cleanup must not skip mandatory eventfd teardown");
            cleanupTask.IsCompleted.Should().BeFalse(
                because: "the timer cleanup failure must stay pending until eventfd teardown returns");

            allowEventFdTeardownExit.TrySetResult(true);
            await eventFdTeardownCompleted.Task.WaitAsync(TestTimeout);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => cleanupTask.WaitAsync(TestTimeout));
            failure.Message.Should().Be("simulated recv retry timer cleanup failure");
            trace.ToArray().Should().Equal(
                [
                    "recv-timer-cleanup-enter",
                    "recv-timer-cleanup-throw",
                    "eventfd-teardown-enter",
                    "eventfd-teardown-exit",
                ]);
        }
        finally
        {
            allowRecvFailure.TrySetResult(true);
            allowEventFdTeardownExit.TrySetResult(true);

            try
            {
                await recvCleanupTask.WaitAsync(TestTimeout);
            }
            catch
            {
            }

            if (eventFdTeardownStarted.Task.IsCompleted)
                await eventFdTeardownCompleted.Task.WaitAsync(TestTimeout);

            try
            {
                await cleanupTask.WaitAsync(TestTimeout);
            }
            catch
            {
            }
        }

        async Task RecvCleanupAsync()
        {
            trace.Enqueue("recv-timer-cleanup-enter");
            await allowRecvFailure.Task.ConfigureAwait(false);
            trace.Enqueue("recv-timer-cleanup-throw");
            throw new InvalidOperationException("simulated recv retry timer cleanup failure");
        }

        async ValueTask EventFdTeardownAsync()
        {
            trace.Enqueue("eventfd-teardown-enter");
            eventFdTeardownStarted.TrySetResult(true);
            await allowEventFdTeardownExit.Task.ConfigureAwait(false);
            trace.Enqueue("eventfd-teardown-exit");
            eventFdTeardownCompleted.TrySetResult(true);
        }
    }

    [Fact, Trait("Category", "Red")]
    public async Task RunAfterRetryTimersAsync_WhenBothCleanupsFail_AwaitsBothAndEventFdTeardownThenSurfacesFlatAggregate()
    {
        var trace = new ConcurrentQueue<string>();
        var allowAcceptFailure = CreateSignal();
        var allowRecvFailure = CreateSignal();
        var eventFdTeardownStarted = CreateSignal();
        var allowEventFdTeardownExit = CreateSignal();
        var eventFdTeardownCompleted = CreateSignal();

        Task acceptCleanupTask = CleanupAsync(
            "accept",
            allowAcceptFailure,
            "simulated accept retry timer cleanup failure");
        Task recvCleanupTask = CleanupAsync(
            "recv",
            allowRecvFailure,
            "simulated recv retry timer cleanup failure");
        Task cleanupTask = ListenerDisposeCleanup.RunAfterRetryTimersAsync(
            new ValueTask(acceptCleanupTask),
            new ValueTask(recvCleanupTask),
            EventFdTeardownAsync).AsTask();

        try
        {
            allowAcceptFailure.TrySetResult(true);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => acceptCleanupTask.WaitAsync(TestTimeout));

            cleanupTask.IsCompleted.Should().BeFalse(
                because: "the first timer cleanup failure must not skip awaiting the second timer cleanup");

            allowRecvFailure.TrySetResult(true);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => recvCleanupTask.WaitAsync(TestTimeout));

            Task firstCompletion = await Task.WhenAny(
                eventFdTeardownStarted.Task,
                cleanupTask).WaitAsync(TestTimeout);
            firstCompletion.Should().BeSameAs(
                eventFdTeardownStarted.Task,
                because: "both timer cleanup failures must be retained without skipping mandatory eventfd teardown");
            cleanupTask.IsCompleted.Should().BeFalse(
                because: "the aggregate failure must stay pending until eventfd teardown returns");

            allowEventFdTeardownExit.TrySetResult(true);
            await eventFdTeardownCompleted.Task.WaitAsync(TestTimeout);

            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
                () => cleanupTask.WaitAsync(TestTimeout));
            failure.Flatten().InnerExceptions.Select(static exception => exception.Message).Should().Equal(
                [
                    "simulated accept retry timer cleanup failure",
                    "simulated recv retry timer cleanup failure",
                ]);
            failure.InnerExceptions.Should().HaveCount(2,
                because: "the composed cleanup should surface one flat aggregate rather than nesting timer failures");
            trace.ToArray().Should().Equal(
                [
                    "accept-timer-cleanup-enter",
                    "recv-timer-cleanup-enter",
                    "accept-timer-cleanup-throw",
                    "recv-timer-cleanup-throw",
                    "eventfd-teardown-enter",
                    "eventfd-teardown-exit",
                ]);
        }
        finally
        {
            allowAcceptFailure.TrySetResult(true);
            allowRecvFailure.TrySetResult(true);
            allowEventFdTeardownExit.TrySetResult(true);

            foreach (Task timerCleanupTask in new[] { acceptCleanupTask, recvCleanupTask })
            {
                try
                {
                    await timerCleanupTask.WaitAsync(TestTimeout);
                }
                catch
                {
                }
            }

            if (eventFdTeardownStarted.Task.IsCompleted)
                await eventFdTeardownCompleted.Task.WaitAsync(TestTimeout);

            try
            {
                await cleanupTask.WaitAsync(TestTimeout);
            }
            catch
            {
            }
        }

        async Task CleanupAsync(
            string timerName,
            TaskCompletionSource<bool> allowFailure,
            string failureMessage)
        {
            trace.Enqueue($"{timerName}-timer-cleanup-enter");
            await allowFailure.Task.ConfigureAwait(false);
            trace.Enqueue($"{timerName}-timer-cleanup-throw");
            throw new InvalidOperationException(failureMessage);
        }

        async ValueTask EventFdTeardownAsync()
        {
            trace.Enqueue("eventfd-teardown-enter");
            eventFdTeardownStarted.TrySetResult(true);
            await allowEventFdTeardownExit.Task.ConfigureAwait(false);
            trace.Enqueue("eventfd-teardown-exit");
            eventFdTeardownCompleted.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class BlockingTimerCallbackState(
        ConcurrentQueue<string> trace,
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> exited,
        ManualResetEvent allowExit)
    {
        public ConcurrentQueue<string> Trace { get; } = trace;

        public TaskCompletionSource<bool> Started { get; } = started;

        public TaskCompletionSource<bool> Exited { get; } = exited;

        public ManualResetEvent AllowExit { get; } = allowExit;
    }
}
