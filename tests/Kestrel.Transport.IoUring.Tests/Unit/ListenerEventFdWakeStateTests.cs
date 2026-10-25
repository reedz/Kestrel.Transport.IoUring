using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Kestrel.Transport.IoUring.Transport;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class ListenerEventFdWakeStateTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact, Trait("Category", "Red")]
    public async Task CloseAsync_WhenWakeWriteIsInFlight_WaitsForWriteToReturnBeforeIssuingClose_AndSuppressesLaterWrites()
    {
        var state = new ListenerEventFdWakeState();
        using var callbacks = new RecordingWakeCallbacks(blockFirstWrite: true);

        Task blockedWake = Task.Run(() => InvokeWake(state, new RecordingWakeAdapter(callbacks)));
        Task closeTask = Task.CompletedTask;

        try
        {
            await callbacks.FirstWriteStarted.Task.WaitAsync(TestTimeout);

            var closeAdapter = new RecordingWakeAdapter(callbacks);
            closeTask = state.CloseAsync(ref closeAdapter).AsTask();

            closeTask.IsCompleted.Should().BeFalse(
                because: "close(eventfd) must stay pending while write(eventfd) is still inside the wake adapter");
            callbacks.CloseCount.Should().Be(0,
                because: "close(eventfd) must not be issued before the in-flight write(eventfd) returns");
            callbacks.TraceSnapshot().Should().Equal(
                [
                    "write-enter",
                ],
                because: "the blocked wake should still be sitting inside write(eventfd) with no close(eventfd) yet issued");

            callbacks.AllowFirstWriteExit.Set();
            await AwaitAllAsync(blockedWake, closeTask);

            int writesBeforeClosedWake = callbacks.WriteCount;
            InvokeWake(state, new RecordingWakeAdapter(callbacks));

            callbacks.WriteCount.Should().Be(writesBeforeClosedWake,
                because: "after close(eventfd) returns, later Wake calls must not issue another write(eventfd)");
            callbacks.TraceSnapshot().Should().Equal(
                [
                    "write-enter",
                    "write-exit",
                    "close-enter",
                    "close-exit",
                ],
                because: "the listener must let write(eventfd) return before it issues close(eventfd)");
        }
        finally
        {
            callbacks.AllowFirstWriteExit.Set();
            await AwaitCleanupAsync(blockedWake, closeTask);
        }
    }

    [Fact, Trait("Category", "Red")]
    public async Task CloseAsync_WhenTwoWakeWritesAreInFlight_WaitsForBothWritesBeforeIssuingClose()
    {
        var state = new ListenerEventFdWakeState();
        using var callbacks = new RecordingWakeCallbacks(blockFirstWrite: true, blockSecondWrite: true);

        Task firstWake = Task.Run(() => InvokeWake(state, new RecordingWakeAdapter(callbacks)));
        Task secondWake = Task.CompletedTask;
        Task closeTask = Task.CompletedTask;

        try
        {
            await callbacks.FirstWriteStarted.Task.WaitAsync(TestTimeout);

            secondWake = Task.Run(() => InvokeWake(state, new RecordingWakeAdapter(callbacks)));
            await callbacks.SecondWriteStarted.Task.WaitAsync(TestTimeout);

            var closeAdapter = new RecordingWakeAdapter(callbacks);
            closeTask = state.CloseAsync(ref closeAdapter).AsTask();

            using (new AssertionScope())
            {
                closeTask.IsCompleted.Should().BeFalse(
                    because: "close(eventfd) must wait while either participating writer is still in the adapter");
                callbacks.CloseCount.Should().Be(0,
                    because: "close(eventfd) must not be issued while both writes are in flight");
            }

            callbacks.AllowFirstWriteExit.Set();
            await firstWake.WaitAsync(TestTimeout);

            using (new AssertionScope())
            {
                secondWake.IsCompleted.Should().BeFalse(
                    because: "the independently gated second writer must still be in flight");
                closeTask.IsCompleted.Should().BeFalse(
                    because: "one writer returning must not release close(eventfd) while another writer remains");
                callbacks.CloseCount.Should().Be(0,
                    because: "a single in-flight boolean cannot safely represent multiple concurrent writers");
            }

            callbacks.AllowSecondWriteExit.Set();
            await AwaitAllAsync(secondWake, closeTask);

            callbacks.TraceSnapshot().Should().Equal(
                [
                    "write-enter",
                    "write-enter",
                    "write-exit",
                    "write-exit",
                    "close-enter",
                    "close-exit",
                ],
                because: "close(eventfd) must follow the return of every writer that acquired participation before closing");
        }
        finally
        {
            callbacks.AllowFirstWriteExit.Set();
            callbacks.AllowSecondWriteExit.Set();
            await AwaitCleanupAsync(firstWake, secondWake, closeTask);
        }
    }

    [Fact, Trait("Category", "Red")]
    public async Task CloseAsync_WhenCalledTwiceDuringWakeWrite_BothCallersWaitForTheSameSingleClose()
    {
        var state = new ListenerEventFdWakeState();
        using var callbacks = new RecordingWakeCallbacks(blockFirstWrite: true, deferCloseCompletion: true);

        Task blockedWake = Task.Run(() => InvokeWake(state, new RecordingWakeAdapter(callbacks)));
        Task firstCloseTask = Task.CompletedTask;
        Task secondCloseTask = Task.CompletedTask;

        try
        {
            await callbacks.FirstWriteStarted.Task.WaitAsync(TestTimeout);

            var firstCloseAdapter = new RecordingWakeAdapter(callbacks);
            firstCloseTask = state.CloseAsync(ref firstCloseAdapter).AsTask();

            var secondCloseAdapter = new RecordingWakeAdapter(callbacks);
            secondCloseTask = state.CloseAsync(ref secondCloseAdapter).AsTask();

            using (new AssertionScope())
            {
                firstCloseTask.IsCompleted.Should().BeFalse(
                    because: "the first close(eventfd) caller must stay pending until write(eventfd) returns");
                secondCloseTask.IsCompleted.Should().BeFalse(
                    because: "a concurrent second close(eventfd) caller must join the same pending close");
                callbacks.CloseCount.Should().Be(0,
                    because: "neither caller may issue close(eventfd) before the blocked write(eventfd) returns");
            }

            callbacks.AllowFirstWriteExit.Set();
            await blockedWake.WaitAsync(TestTimeout);
            await callbacks.CloseStarted.Task.WaitAsync(TestTimeout);

            using (new AssertionScope())
            {
                firstCloseTask.IsCompleted.Should().BeFalse(
                    because: "the single close(eventfd) callback is still deferred by the test adapter");
                secondCloseTask.IsCompleted.Should().BeFalse(
                    because: "both CloseAsync callers must complete from that same single close(eventfd)");
                callbacks.CloseCount.Should().Be(1,
                    because: "the eventfd must be closed exactly once for both callers");
            }

            callbacks.AllowCloseExit.TrySetResult(true);
            await AwaitAllAsync(firstCloseTask, secondCloseTask);

            int writesBeforeClosedWake = callbacks.WriteCount;
            InvokeWake(state, new RecordingWakeAdapter(callbacks));

            callbacks.WriteCount.Should().Be(writesBeforeClosedWake,
                because: "after the shared close(eventfd) returns, later Wake calls must stay suppressed");
            callbacks.TraceSnapshot().Should().Equal(
                [
                    "write-enter",
                    "write-exit",
                    "close-enter",
                    "close-exit",
                ],
                because: "both CloseAsync callers should observe one close(eventfd) issued after write(eventfd) returns");
        }
        finally
        {
            callbacks.AllowFirstWriteExit.Set();
            callbacks.AllowCloseExit.TrySetResult(true);
            await AwaitCleanupAsync(blockedWake, firstCloseTask, secondCloseTask);
        }
    }

    [Fact, Trait("Category", "Red")]
    public async Task CloseAsync_WhenWakeArrivesAfterCloseStarts_SuppressesTheNewWriteUntilTheBlockedWriteReturns_AndStillClosesOnce()
    {
        var state = new ListenerEventFdWakeState();
        using var callbacks = new RecordingWakeCallbacks(blockFirstWrite: true);

        Task firstWakeTask = Task.Run(() => InvokeWake(state, new RecordingWakeAdapter(callbacks)));
        Task secondWakeTask = Task.CompletedTask;
        Task closeTask = Task.CompletedTask;

        try
        {
            await callbacks.FirstWriteStarted.Task.WaitAsync(TestTimeout);

            var closeAdapter = new RecordingWakeAdapter(callbacks);
            closeTask = state.CloseAsync(ref closeAdapter).AsTask();

            closeTask.IsCompleted.Should().BeFalse(
                because: "close(eventfd) must stay pending while the original write(eventfd) is still inside the wake adapter");
            callbacks.CloseCount.Should().Be(0,
                because: "close(eventfd) must not issue until the original write(eventfd) returns safely");

            secondWakeTask = Task.Run(() => InvokeWake(state, new RecordingWakeAdapter(callbacks)));
            await secondWakeTask.WaitAsync(TestTimeout);

            using (new AssertionScope())
            {
                callbacks.WriteCount.Should().Be(1,
                    because: "once close(eventfd) initiation wins the gate, a later Wake call must stay suppressed instead of issuing a second write(eventfd)");
                callbacks.CloseCount.Should().Be(0,
                    because: "close(eventfd) must still wait for the blocked original write(eventfd) to return");
                closeTask.IsCompleted.Should().BeFalse(
                    because: "the first write(eventfd) is still in flight even though the second wake attempt has already returned");
                callbacks.TraceSnapshot().Should().Equal(
                    [
                        "write-enter",
                    ],
                    because: "the second wake attempt must not add another write(eventfd) while close(eventfd) is pending behind the original writer");
            }

            callbacks.AllowFirstWriteExit.Set();
            await AwaitAllAsync(firstWakeTask, secondWakeTask, closeTask);

            callbacks.CloseCount.Should().Be(1,
                because: "closing after the blocked write returns should still issue exactly one close(eventfd)");
            callbacks.TraceSnapshot().Should().Equal(
                [
                    "write-enter",
                    "write-exit",
                    "close-enter",
                    "close-exit",
                ],
                because: "the blocked write(eventfd) must return before the single close(eventfd), and the second wake attempt must stay fully suppressed");
        }
        finally
        {
            callbacks.AllowFirstWriteExit.Set();
            await AwaitCleanupAsync(firstWakeTask, secondWakeTask, closeTask);
        }
    }

    [Fact, Trait("Category", "Red")]
    public async Task CloseAsync_WhenCalledAgainWhileAdapterCloseIsStillPending_BothCallersAwaitTheSameSingleCloseCompletion()
    {
        var state = new ListenerEventFdWakeState();
        using var callbacks = new RecordingWakeCallbacks(blockFirstWrite: false, deferCloseCompletion: true);
        Task firstCloseTask = Task.CompletedTask;
        Task secondCloseTask = Task.CompletedTask;

        try
        {
            var firstCloseAdapter = new RecordingWakeAdapter(callbacks);
            firstCloseTask = state.CloseAsync(ref firstCloseAdapter).AsTask();
            await callbacks.CloseStarted.Task.WaitAsync(TestTimeout);

            var secondCloseAdapter = new RecordingWakeAdapter(callbacks);
            secondCloseTask = state.CloseAsync(ref secondCloseAdapter).AsTask();

            using (new AssertionScope())
            {
                firstCloseTask.IsCompleted.Should().BeFalse(
                    because: "the first CloseAsync caller is still waiting on the adapter-provided close completion gate");
                secondCloseTask.IsCompleted.Should().BeFalse(
                    because: "a second CloseAsync caller must join the same pending close completion instead of observing teardown as already done");
                callbacks.CloseCount.Should().Be(1,
                    because: "the eventfd must still be closed exactly once while both callers await the same underlying close");
                callbacks.TraceSnapshot().Should().Equal(
                    [
                        "close-enter",
                    ],
                    because: "with no writers left to drain, the only pending operation should be the single gated close(eventfd)");
            }

            callbacks.AllowCloseExit.TrySetResult(true);
            await AwaitAllAsync(firstCloseTask, secondCloseTask);

            callbacks.TraceSnapshot().Should().Equal(
                [
                    "close-enter",
                    "close-exit",
                ],
                because: "both close callers should complete from that one gated close(eventfd) callback");
        }
        finally
        {
            callbacks.AllowCloseExit.TrySetResult(true);
            await AwaitCleanupAsync(firstCloseTask, secondCloseTask);
        }
    }

    [Fact]
    public async Task CloseAsync_WhenInFlightWakeThrows_ReleasesWriterParticipationBeforeClosing()
    {
        var state = new ListenerEventFdWakeState();
        var writeFailure = new InvalidOperationException("simulated eventfd write failure");
        using var callbacks = new RecordingWakeCallbacks(
            blockFirstWrite: true,
            writeException: writeFailure);

        Task wakeTask = Task.Run(() => InvokeWake(state, new RecordingWakeAdapter(callbacks)));
        Task closeTask = Task.CompletedTask;

        try
        {
            await callbacks.FirstWriteStarted.Task.WaitAsync(TestTimeout);

            var closeAdapter = new RecordingWakeAdapter(callbacks);
            closeTask = state.CloseAsync(ref closeAdapter).AsTask();

            callbacks.AllowFirstWriteExit.Set();

            var observedWriteFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => wakeTask.WaitAsync(TestTimeout));
            observedWriteFailure.Should().BeSameAs(writeFailure);
            await closeTask.WaitAsync(TestTimeout);

            callbacks.TraceSnapshot().Should().Equal(
                [
                    "write-enter",
                    "write-exit",
                    "close-enter",
                    "close-exit",
                ],
                because: "a throwing writer must relinquish participation before the close adapter runs");
        }
        finally
        {
            callbacks.AllowFirstWriteExit.Set();
            await AwaitCleanupAsync(wakeTask, closeTask);
        }
    }

    [Fact]
    public async Task CloseAsync_WhenSynchronousAdapterCloseThrows_SharesTheSameSingleFailure()
    {
        var state = new ListenerEventFdWakeState();
        var closeFailure = new InvalidOperationException("simulated synchronous eventfd close failure");
        using var callbacks = new RecordingWakeCallbacks(
            blockFirstWrite: false,
            closeException: closeFailure);

        var firstCloseAdapter = new RecordingWakeAdapter(callbacks);
        Task firstCloseTask = state.CloseAsync(ref firstCloseAdapter).AsTask();
        var secondCloseAdapter = new RecordingWakeAdapter(callbacks);
        Task secondCloseTask = state.CloseAsync(ref secondCloseAdapter).AsTask();

        firstCloseTask.Should().BeSameAs(
            secondCloseTask,
            because: "all callers must observe the same shared close completion");
        var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstCloseTask.WaitAsync(TestTimeout));
        var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondCloseTask.WaitAsync(TestTimeout));

        firstFailure.Should().BeSameAs(closeFailure);
        secondFailure.Should().BeSameAs(closeFailure);
        callbacks.CloseCount.Should().Be(1);
    }

    [Fact]
    public async Task CloseAsync_WhenAsynchronousAdapterCloseThrows_SharesTheFailureAfterAdapterCompletion()
    {
        var state = new ListenerEventFdWakeState();
        var closeFailure = new InvalidOperationException("simulated asynchronous eventfd close failure");
        using var callbacks = new RecordingWakeCallbacks(
            blockFirstWrite: false,
            deferCloseCompletion: true,
            closeException: closeFailure);

        var firstCloseAdapter = new RecordingWakeAdapter(callbacks);
        Task firstCloseTask = state.CloseAsync(ref firstCloseAdapter).AsTask();
        await callbacks.CloseStarted.Task.WaitAsync(TestTimeout);

        var secondCloseAdapter = new RecordingWakeAdapter(callbacks);
        Task secondCloseTask = state.CloseAsync(ref secondCloseAdapter).AsTask();
        callbacks.AllowCloseExit.TrySetResult(true);

        var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstCloseTask.WaitAsync(TestTimeout));
        var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondCloseTask.WaitAsync(TestTimeout));

        firstFailure.Should().BeSameAs(closeFailure);
        secondFailure.Should().BeSameAs(closeFailure);
        firstCloseTask.Should().BeSameAs(secondCloseTask);
        callbacks.CloseCount.Should().Be(1);
    }

    [Fact]
    public async Task CloseAsync_WhenAdapterCloseThrowsAfterWriterDrain_CompletesTheSharedCloseWithThatFailure()
    {
        var state = new ListenerEventFdWakeState();
        var closeFailure = new InvalidOperationException("simulated post-drain eventfd close failure");
        using var callbacks = new RecordingWakeCallbacks(
            blockFirstWrite: true,
            closeException: closeFailure);

        Task wakeTask = Task.Run(() => InvokeWake(state, new RecordingWakeAdapter(callbacks)));
        await callbacks.FirstWriteStarted.Task.WaitAsync(TestTimeout);

        var firstCloseAdapter = new RecordingWakeAdapter(callbacks);
        Task firstCloseTask = state.CloseAsync(ref firstCloseAdapter).AsTask();
        var secondCloseAdapter = new RecordingWakeAdapter(callbacks);
        Task secondCloseTask = state.CloseAsync(ref secondCloseAdapter).AsTask();

        callbacks.AllowFirstWriteExit.Set();
        await wakeTask.WaitAsync(TestTimeout);

        var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstCloseTask.WaitAsync(TestTimeout));
        var secondFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondCloseTask.WaitAsync(TestTimeout));

        firstFailure.Should().BeSameAs(closeFailure);
        secondFailure.Should().BeSameAs(closeFailure);
        firstCloseTask.Should().BeSameAs(secondCloseTask);
        callbacks.CloseCount.Should().Be(1);
    }

    [Fact]
    public void Wake_WhenOpen_InvokesWriteAdapter()
    {
        var state = new ListenerEventFdWakeState();
        using var callbacks = new RecordingWakeCallbacks(blockFirstWrite: false);

        InvokeWake(state, new RecordingWakeAdapter(callbacks));

        callbacks.WriteCount.Should().Be(1,
            because: "an open listener wake should publish exactly one eventfd write");
        callbacks.CloseCount.Should().Be(0,
            because: "a plain wake must not close the eventfd");
    }

    [Fact]
    public async Task CloseAsync_WhenNoWakeIsInFlight_CompletesSynchronously()
    {
        var state = new ListenerEventFdWakeState();
        using var callbacks = new RecordingWakeCallbacks(blockFirstWrite: false);
        var closeAdapter = new RecordingWakeAdapter(callbacks);

        Task closeTask = state.CloseAsync(ref closeAdapter).AsTask();

        closeTask.IsCompletedSuccessfully.Should().BeTrue(
            because: "with no active wake write, the current eventfd close path is synchronous");
        await closeTask.WaitAsync(TestTimeout);
        callbacks.CloseCount.Should().Be(1);
    }

    [Fact]
    public async Task CloseAsync_WhenCalledAgainAfterCompletion_IsIdempotent()
    {
        var state = new ListenerEventFdWakeState();
        using var callbacks = new RecordingWakeCallbacks(blockFirstWrite: false);
        var firstCloseAdapter = new RecordingWakeAdapter(callbacks);

        await state.CloseAsync(ref firstCloseAdapter).AsTask().WaitAsync(TestTimeout);

        var secondCloseAdapter = new RecordingWakeAdapter(callbacks);
        Task secondCloseTask = state.CloseAsync(ref secondCloseAdapter).AsTask();

        secondCloseTask.IsCompletedSuccessfully.Should().BeTrue(
            because: "repeated eventfd teardown should not re-run the close callback after the first completion");
        await secondCloseTask.WaitAsync(TestTimeout);
        callbacks.CloseCount.Should().Be(1);
    }

    private static void InvokeWake(ListenerEventFdWakeState state, RecordingWakeAdapter adapter) =>
        state.Wake(ref adapter);

    private static Task AwaitAllAsync(params Task[] tasks) =>
        Task.WhenAll(tasks).WaitAsync(TestTimeout);

    private static async Task AwaitCleanupAsync(params Task[] tasks)
    {
        try
        {
            await AwaitAllAsync(tasks);
        }
        catch
        {
        }
    }

    private sealed class RecordingWakeCallbacks(
        bool blockFirstWrite,
        bool deferCloseCompletion = false,
        bool blockSecondWrite = false,
        Exception? writeException = null,
        Exception? closeException = null) : IDisposable
    {
        private readonly bool _blockFirstWrite = blockFirstWrite;
        private readonly bool _blockSecondWrite = blockSecondWrite;
        private readonly bool _deferCloseCompletion = deferCloseCompletion;
        private readonly Exception? _writeException = writeException;
        private readonly Exception? _closeException = closeException;
        private readonly ConcurrentQueue<string> _trace = new();
        private int _writeCount;
        private int _closeCount;

        internal TaskCompletionSource<bool> FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> SecondWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> CloseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim AllowFirstWriteExit { get; } = new(false);

        internal ManualResetEventSlim AllowSecondWriteExit { get; } = new(false);

        internal TaskCompletionSource<bool> AllowCloseExit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int WriteCount => Volatile.Read(ref _writeCount);
        internal int CloseCount => Volatile.Read(ref _closeCount);

        internal string[] TraceSnapshot() => _trace.ToArray();

        internal void RecordWrite()
        {
            _trace.Enqueue("write-enter");
            int writeCount = Interlocked.Increment(ref _writeCount);
            if (writeCount == 1)
            {
                FirstWriteStarted.TrySetResult(true);
                if (_blockFirstWrite)
                    AllowFirstWriteExit.Wait();
            }
            else if (writeCount == 2)
            {
                SecondWriteStarted.TrySetResult(true);
                if (_blockSecondWrite)
                    AllowSecondWriteExit.Wait();
            }

            _trace.Enqueue("write-exit");
            if (_writeException is not null)
                throw _writeException;
        }

        internal ValueTask RecordCloseAsync()
        {
            _trace.Enqueue("close-enter");
            Interlocked.Increment(ref _closeCount);
            CloseStarted.TrySetResult(true);

            if (!_deferCloseCompletion)
            {
                _trace.Enqueue("close-exit");
                if (_closeException is not null)
                    throw _closeException;

                return ValueTask.CompletedTask;
            }

            return AwaitCloseExitAsync(this);

            static async ValueTask AwaitCloseExitAsync(RecordingWakeCallbacks callbacks)
            {
                await callbacks.AllowCloseExit.Task.ConfigureAwait(false);
                callbacks._trace.Enqueue("close-exit");
                if (callbacks._closeException is not null)
                    throw callbacks._closeException;
            }
        }

        public void Dispose()
        {
            AllowFirstWriteExit.Set();
            AllowSecondWriteExit.Set();
            AllowCloseExit.TrySetResult(true);
        }
    }

    private struct RecordingWakeAdapter(RecordingWakeCallbacks callbacks)
        : ListenerEventFdWakeState.IEventFdWakeAdapter<RecordingWakeAdapter>
    {
        private readonly RecordingWakeCallbacks _callbacks = callbacks;

        public static void Write(ref RecordingWakeAdapter self) =>
            self._callbacks.RecordWrite();

        public static ValueTask CloseAsync(ref RecordingWakeAdapter self) =>
            self._callbacks.RecordCloseAsync();
    }
}
