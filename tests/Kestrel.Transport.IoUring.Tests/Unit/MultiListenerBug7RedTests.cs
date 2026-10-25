using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using FluentAssertions;
using Kestrel.Transport.IoUring.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Kestrel.Transport.IoUring.Tests.Unit;

public class MultiListenerBug7RedTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DisposeAsync_DisposesAbortedInTransitConnection_WhenMergedWriteIsCancelled()
    {
        var mergedConnection = new TrackedConnectionContext("merged");
        var inTransitConnection = new TrackedConnectionContext("in-transit");
        var worker1 = new FakeWorker();
        var worker2 = new FakeWorker();

        worker1.Publish(mergedConnection);
        worker1.CompleteAccepts();
        worker2.QueuePending(inTransitConnection);

        var listener = CreateListener(mergedCapacity: 1, worker1, worker2);

        await WithTimeout(worker1.SecondAcceptStarted);
        worker2.PublishPending();
        await WithTimeout(worker2.FirstConnectionReturned);

        await WithTimeout(listener.DisposeAsync());

        inTransitConnection.AbortCount.Should().BeGreaterThanOrEqualTo(1);
        inTransitConnection.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_DrainsMergedAndQueuedWorkerConnections_WithoutFurtherAccepts()
    {
        var mergedConnection = new TrackedConnectionContext("merged");
        var queuedConnection = new TrackedConnectionContext("queued");
        var worker1 = new FakeWorker();
        var worker2 = new FakeWorker(publishPendingOnUnbind: true);

        worker1.Publish(mergedConnection);
        worker1.CompleteAccepts();
        worker2.QueuePending(queuedConnection);

        var listener = CreateListener(mergedCapacity: 1, worker1, worker2);

        await WithTimeout(worker1.SecondAcceptStarted);
        await WithTimeout(worker2.FirstAcceptStarted);

        await WithTimeout(listener.DisposeAsync());

        mergedConnection.AbortCount.Should().BeGreaterThanOrEqualTo(1);
        mergedConnection.DisposeCount.Should().Be(1);
        queuedConnection.AbortCount.Should().BeGreaterThanOrEqualTo(1);
        queuedConnection.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task UnbindAsync_DrainsMergedBufferedConnection_WithoutDisposeAsync()
    {
        var mergedConnection = new TrackedConnectionContext("merged");
        var worker = new FakeWorker();

        worker.Publish(mergedConnection);
        worker.CompleteAccepts();

        var listener = CreateListener(mergedCapacity: 1, worker);
        var listenerDisposed = false;

        try
        {
            await WithTimeout(worker.SecondAcceptStarted);

            await WithTimeout(listener.UnbindAsync());

            mergedConnection.AbortCount.Should().BeGreaterThanOrEqualTo(1);
            mergedConnection.DisposeCount.Should().Be(1);
            worker.UnbindCallCount.Should().Be(1);
            worker.DisposeCallCount.Should().Be(0);

            await WithTimeout(listener.DisposeAsync());
            listenerDisposed = true;

            mergedConnection.DisposeCount.Should().Be(1);
            worker.DisposeCallCount.Should().Be(1);
        }
        finally
        {
            if (!listenerDisposed)
                await SafeDisposeAsync(listener);
        }
    }

    [Fact]
    public async Task AcceptAsync_ReturnsNullAfterUnbindStarts_EvenWhenMergedConnectionIsBuffered()
    {
        var mergedConnection = new TrackedConnectionContext("merged");
        var inTransitConnection = new TrackedConnectionContext(
            "in-transit",
            releaseDisposeByDefault: false);
        var worker1 = new FakeWorker();
        var worker2 = new FakeWorker();

        worker1.Publish(mergedConnection);
        worker1.CompleteAccepts();
        worker2.QueuePending(inTransitConnection);

        var listener = CreateListener(mergedCapacity: 1, worker1, worker2);
        var listenerDisposed = false;

        try
        {
            await WithTimeout(worker1.SecondAcceptStarted);

            worker2.PublishPending();
            await WithTimeout(worker2.FirstConnectionReturned);

            Task unbindTask = listener.UnbindAsync().AsTask();
            await WithTimeout(inTransitConnection.DisposeStarted);

            mergedConnection.DisposeCount.Should().Be(0);

            ConnectionContext? appConnection = await WithTimeout(listener.AcceptAsync());
            appConnection.Should().BeNull();

            inTransitConnection.ReleaseDispose();
            await WithTimeout(unbindTask);

            mergedConnection.AbortCount.Should().BeGreaterThanOrEqualTo(1);
            mergedConnection.DisposeCount.Should().Be(1);
            worker1.DisposeCallCount.Should().Be(0);
            worker2.DisposeCallCount.Should().Be(0);

            await WithTimeout(listener.DisposeAsync());
            listenerDisposed = true;

            mergedConnection.DisposeCount.Should().Be(1);
            worker1.DisposeCallCount.Should().Be(1);
            worker2.DisposeCallCount.Should().Be(1);
        }
        finally
        {
            inTransitConnection.ReleaseDispose();
            if (!listenerDisposed)
                await SafeDisposeAsync(listener);
        }
    }

    [Fact]
    public async Task UnbindAsync_StartsEveryWorkerUnbind_BeforeAwaitingCompletion()
    {
        var worker1 = new FakeWorker(releaseUnbindByDefault: false);
        var worker2 = new FakeWorker(releaseUnbindByDefault: false);
        var listener = CreateListener(mergedCapacity: 1, worker1, worker2);
        var unbindTask = listener.UnbindAsync().AsTask();

        try
        {
            await WithTimeout(worker1.UnbindStarted);
            worker2.UnbindStarted.IsCompleted.Should().BeTrue();
        }
        finally
        {
            worker1.ReleaseUnbind();
            await WithTimeout(worker2.UnbindStarted);
            worker2.ReleaseUnbind();

            try
            {
                await WithTimeout(unbindTask);
            }
            catch
            {
            }

            await SafeDisposeAsync(listener);
        }
    }

    [Fact]
    public async Task UnbindAsync_IsIdempotent()
    {
        var worker = new FakeWorker();
        var listener = CreateListener(mergedCapacity: 1, worker);

        try
        {
            Func<Task> act = async () =>
            {
                await WithTimeout(listener.UnbindAsync());
                await WithTimeout(listener.UnbindAsync());
            };

            await WithTimeout(act.Should().NotThrowAsync());
            worker.UnbindCallCount.Should().Be(1);
        }
        finally
        {
            await SafeDisposeAsync(listener);
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var worker = new FakeWorker();
        var listener = CreateListener(mergedCapacity: 1, worker);

        Func<Task> act = async () =>
        {
            await WithTimeout(listener.DisposeAsync());
            await WithTimeout(listener.DisposeAsync());
        };

        await WithTimeout(act.Should().NotThrowAsync());
        worker.UnbindCallCount.Should().Be(1);
        worker.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotDisposeConnectionAlreadyAcceptedByApp()
    {
        var acceptedConnection = new TrackedConnectionContext("app-owned");
        var worker = new FakeWorker();
        worker.Publish(acceptedConnection);
        worker.CompleteAccepts();

        var listener = CreateListener(mergedCapacity: 1, worker);
        ConnectionContext? appConnection = await WithTimeout(listener.AcceptAsync());

        appConnection.Should().BeSameAs(acceptedConnection);

        await WithTimeout(listener.DisposeAsync());

        acceptedConnection.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_AwaitsSharedUnbindAfterFirstCallerCancellation()
    {
        var worker = new FakeWorker(releaseUnbindByDefault: false);
        var listener = CreateListener(mergedCapacity: 1, worker);
        using var cancellationTokenSource = new CancellationTokenSource();
        Task cancelledUnbind = listener.UnbindAsync(cancellationTokenSource.Token).AsTask();

        try
        {
            await WithTimeout(worker.UnbindStarted);

            cancellationTokenSource.Cancel();

            await WithTimeout(Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledUnbind));

            worker.ReleaseUnbind();

            await WithTimeout(listener.DisposeAsync());

            worker.UnbindCallCount.Should().Be(1);
            worker.DisposeCallCount.Should().Be(1);
        }
        finally
        {
            worker.ReleaseUnbind();
            await SafeDisposeAsync(listener);
        }
    }

    [Fact]
    public async Task DisposeAsync_ContinuesCleanupWhenAWorkerUnbindFails()
    {
        var mergedConnection = new TrackedConnectionContext("merged");
        var queuedConnection = new TrackedConnectionContext("queued");
        var failingWorker = new FakeWorker(
            unbindException: new InvalidOperationException("simulated worker unbind failure"));
        var drainingWorker = new FakeWorker(publishPendingOnUnbind: true);

        drainingWorker.Publish(mergedConnection);
        drainingWorker.QueuePending(queuedConnection);

        var listener = CreateListener(mergedCapacity: 1, failingWorker, drainingWorker);

        try
        {
            await WithTimeout(drainingWorker.SecondAcceptStarted);

            Func<Task> act = async () => await WithTimeout(listener.DisposeAsync());

            await WithTimeout(act.Should().ThrowAsync<InvalidOperationException>()
                .Where(ex => ex.Message == "simulated worker unbind failure"));

            mergedConnection.DisposeCount.Should().Be(1);
            queuedConnection.DisposeCount.Should().Be(1);
            failingWorker.DisposeCallCount.Should().Be(1);
            drainingWorker.DisposeCallCount.Should().Be(1);
        }
        finally
        {
            await SafeDisposeAsync(listener);
        }
    }

    [Fact]
    public async Task DisposeAsync_AggregatesAllForwarderUnbindAndDisposeFailures_AfterCleanup()
    {
        var forwarderFailure1 = new InvalidOperationException("simulated forwarder dispose failure 1");
        var forwarderFailure2 = new InvalidOperationException("simulated forwarder dispose failure 2");
        var unbindFailure1 = new InvalidOperationException("simulated worker unbind failure 1");
        var unbindFailure2 = new InvalidOperationException("simulated worker unbind failure 2");
        var disposeFailure1 = new InvalidOperationException("simulated worker dispose failure 1");
        var disposeFailure2 = new InvalidOperationException("simulated worker dispose failure 2");
        var connection1 = new TrackedConnectionContext("worker-1", disposeException: forwarderFailure1);
        var connection2 = new TrackedConnectionContext("worker-2", disposeException: forwarderFailure2);
        var worker1 = new FakeWorker(
            publishPendingOnUnbind: true,
            unbindException: unbindFailure1,
            disposeException: disposeFailure1);
        var worker2 = new FakeWorker(
            publishPendingOnUnbind: true,
            unbindException: unbindFailure2,
            disposeException: disposeFailure2);

        worker1.QueuePending(connection1);
        worker2.QueuePending(connection2);

        var listener = CreateListener(mergedCapacity: 1, worker1, worker2);

        try
        {
            AggregateException failure = await WithTimeout(Assert.ThrowsAsync<AggregateException>(
                async () => await WithTimeout(listener.DisposeAsync())));

            failure.Flatten().InnerExceptions.Should().HaveCount(6);
            failure.Flatten().InnerExceptions.Select(static ex => ex.Message).Should().BeEquivalentTo(
                [
                    forwarderFailure1.Message,
                    forwarderFailure2.Message,
                    unbindFailure1.Message,
                    unbindFailure2.Message,
                    disposeFailure1.Message,
                    disposeFailure2.Message,
                ]);

            connection1.AbortCount.Should().BeGreaterThanOrEqualTo(1);
            connection1.DisposeCount.Should().Be(1);
            connection2.AbortCount.Should().BeGreaterThanOrEqualTo(1);
            connection2.DisposeCount.Should().Be(1);
            worker1.UnbindCallCount.Should().Be(1);
            worker2.UnbindCallCount.Should().Be(1);
            worker1.DisposeCallCount.Should().Be(1);
            worker2.DisposeCallCount.Should().Be(1);
        }
        finally
        {
            await SafeDisposeAsync(listener);
        }
    }

    private static IoUringMultiListener CreateListener(int mergedCapacity, params FakeWorker[] workers) =>
        new(new IPEndPoint(IPAddress.Loopback, 0), mergedCapacity, workers);

    private static async Task SafeDisposeAsync(IoUringMultiListener listener)
    {
        try
        {
            await WithTimeout(listener.DisposeAsync());
        }
        catch
        {
        }
    }

    private static Task WithTimeout(Task task) =>
        task.WaitAsync(TestTimeout);

    private static Task<T> WithTimeout<T>(Task<T> task) =>
        task.WaitAsync(TestTimeout);

    private static Task WithTimeout(ValueTask task) =>
        task.AsTask().WaitAsync(TestTimeout);

    private static Task<T> WithTimeout<T>(ValueTask<T> task) =>
        task.AsTask().WaitAsync(TestTimeout);

    private sealed class FakeWorker : IMultiListenerWorker
    {
        private readonly Channel<ConnectionContext> _accepts = Channel.CreateUnbounded<ConnectionContext>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        private readonly Queue<TrackedConnectionContext> _pending = new();
        private readonly List<TrackedConnectionContext> _tracked = [];
        private readonly TaskCompletionSource<bool> _firstAcceptStarted = CreateSignal();
        private readonly TaskCompletionSource<bool> _secondAcceptStarted = CreateSignal();
        private readonly TaskCompletionSource<bool> _firstConnectionReturned = CreateSignal();
        private readonly TaskCompletionSource<bool> _unbindStarted = CreateSignal();
        private readonly TaskCompletionSource<bool> _allowUnbind = CreateSignal();
        private readonly bool _publishPendingOnUnbind;
        private readonly Exception? _unbindException;
        private readonly Exception? _disposeException;

        private int _acceptCallCount;
        private int _unbindCallCount;
        private int _disposeCallCount;

        public FakeWorker(
            bool publishPendingOnUnbind = false,
            bool releaseUnbindByDefault = true,
            Exception? unbindException = null,
            Exception? disposeException = null)
        {
            _publishPendingOnUnbind = publishPendingOnUnbind;
            _unbindException = unbindException;
            _disposeException = disposeException;
            if (releaseUnbindByDefault)
                _allowUnbind.TrySetResult(true);
        }

        public EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);

        public Task FirstAcceptStarted => _firstAcceptStarted.Task;

        public Task SecondAcceptStarted => _secondAcceptStarted.Task;

        public Task FirstConnectionReturned => _firstConnectionReturned.Task;

        public Task UnbindStarted => _unbindStarted.Task;

        public int UnbindCallCount => Volatile.Read(ref _unbindCallCount);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public void Publish(TrackedConnectionContext connection)
        {
            _tracked.Add(connection);
            _accepts.Writer.TryWrite(connection);
        }

        public void QueuePending(TrackedConnectionContext connection)
        {
            _tracked.Add(connection);
            _pending.Enqueue(connection);
        }

        public void PublishPending()
        {
            while (_pending.Count > 0)
                _accepts.Writer.TryWrite(_pending.Dequeue());
        }

        public void CompleteAccepts() => _accepts.Writer.TryComplete();

        public void ReleaseUnbind() => _allowUnbind.TrySetResult(true);

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            int acceptCall = Interlocked.Increment(ref _acceptCallCount);
            if (acceptCall == 1)
                _firstAcceptStarted.TrySetResult(true);
            else if (acceptCall == 2)
                _secondAcceptStarted.TrySetResult(true);

            try
            {
                var connection = await _accepts.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _firstConnectionReturned.TrySetResult(true);
                return connection;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _unbindCallCount);
            _unbindStarted.TrySetResult(true);

            foreach (var connection in _tracked)
                connection.Abort(new ConnectionAbortedException("Transport is shutting down."));

            if (_publishPendingOnUnbind)
                PublishPending();

            _accepts.Writer.TryComplete();
            await _allowUnbind.Task.WaitAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
            if (_unbindException is not null)
                throw _unbindException;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            return _disposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_disposeException);
        }

        private static TaskCompletionSource<bool> CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TrackedConnectionContext : ConnectionContext
    {
        private readonly CancellationTokenSource _closed = new();
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly Exception? _disposeException;
        private readonly TaskCompletionSource<bool> _disposeStarted = CreateSignal();
        private readonly TaskCompletionSource<bool> _allowDispose = CreateSignal();
        private int _abortCount;
        private int _disposeCount;

        public TrackedConnectionContext(
            string connectionId,
            Exception? disposeException = null,
            bool releaseDisposeByDefault = true)
        {
            ConnectionId = connectionId;
            Transport = new TestDuplexPipe(_input.Reader, _output.Writer);
            _disposeException = disposeException;
            if (releaseDisposeByDefault)
                _allowDispose.TrySetResult(true);
        }

        public int AbortCount => Volatile.Read(ref _abortCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task DisposeStarted => _disposeStarted.Task;

        public override string ConnectionId { get; set; }

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

        public override IDuplexPipe Transport { get; set; }

        public override CancellationToken ConnectionClosed => _closed.Token;

        public override void Abort(ConnectionAbortedException abortReason)
        {
            Interlocked.Increment(ref _abortCount);
            _closed.Cancel();
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposeStarted.TrySetResult(true);
            _closed.Cancel();
            try { _input.Reader.Complete(); } catch (InvalidOperationException) { }
            try { _input.Writer.Complete(); } catch (InvalidOperationException) { }
            try { _output.Reader.Complete(); } catch (InvalidOperationException) { }
            try { _output.Writer.Complete(); } catch (InvalidOperationException) { }
            return DisposeCoreAsync();
        }

        public void ReleaseDispose() => _allowDispose.TrySetResult(true);

        private async ValueTask DisposeCoreAsync()
        {
            await _allowDispose.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            if (_disposeException is not null)
                throw _disposeException;
        }

        private static TaskCompletionSource<bool> CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
