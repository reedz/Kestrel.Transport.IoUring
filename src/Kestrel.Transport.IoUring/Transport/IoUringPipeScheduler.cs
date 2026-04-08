using System.Collections.Concurrent;
using System.IO.Pipelines;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>
/// A <see cref="PipeScheduler"/> that schedules work on the IO loop thread.
/// When the application writes to the output pipe and flushes, the pipe's reader
/// continuation is enqueued here and executed on the IO loop thread — not the ThreadPool.
/// This matches the architecture of Kestrel's socket transport IOQueue.
/// </summary>
internal sealed class IoUringPipeScheduler : PipeScheduler
{
    private readonly ConcurrentQueue<Work> _queue = new();
    private readonly Action _wakeIoLoop;
    private volatile bool _signalPending;

    private readonly struct Work
    {
        public readonly Action<object?> Callback;
        public readonly object? State;

        public Work(Action<object?> callback, object? state)
        {
            Callback = callback;
            State = state;
        }
    }

    public IoUringPipeScheduler(Action wakeIoLoop)
    {
        _wakeIoLoop = wakeIoLoop;
    }

    public override void Schedule(Action<object?> action, object? state)
    {
        _queue.Enqueue(new Work(action, state));
        // Coalesce wakeups: only write eventfd if no signal is already pending.
        if (!_signalPending)
        {
            _signalPending = true;
            _wakeIoLoop();
        }
    }

    /// <summary>
    /// Drains all queued work items. Called by the IO loop on each iteration.
    /// </summary>
    public void DrainWorkItems()
    {
        _signalPending = false;
        while (_queue.TryDequeue(out var work))
        {
            work.Callback(work.State);
        }
    }
}
