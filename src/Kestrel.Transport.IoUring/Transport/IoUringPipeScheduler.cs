using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;

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
    // 0 = no signal pending, 1 = eventfd write already issued and not yet drained.
    private int _signalPending;

    [ThreadStatic]
    private static bool t_isIoThread;

    /// <summary>Marks the calling thread as the IO loop thread; calls to Schedule from this
    /// thread will enqueue work but will NOT write the eventfd (the outer loop drains inline).</summary>
    public static void MarkIoThread() => t_isIoThread = true;

    /// <summary>Last drain count — diagnostic only, exposed to logs.</summary>
    public int LastDrainedCount;

    /// <summary>True if the queue has work waiting and we have NOT signalled yet (IO thread can avoid parking).</summary>
    public bool HasWork => !_queue.IsEmpty;

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

        // OPT B: if we're already on the IO thread, the outer loop will drain
        // before re-parking. No need to write the eventfd at all.
        if (t_isIoThread)
            return;

        // OPT A: strict CAS coalesce. Only one off-loop producer wins the race
        // to write the eventfd between drains.
        if (Interlocked.Exchange(ref _signalPending, 1) == 0)
        {
            _wakeIoLoop();
        }
    }

    /// <summary>
    /// Drains all queued work items. Called by the IO loop on each iteration.
    /// </summary>
    public void DrainWorkItems()
    {
        // Clear BEFORE draining so any producer enqueuing during/after the drain
        // still races to publish a wake (correctness: we never miss a signal).
        Volatile.Write(ref _signalPending, 0);
        int drained = 0;
        while (_queue.TryDequeue(out var work))
        {
            work.Callback(work.State);
            drained++;
        }
        LastDrainedCount = drained;
    }
}
