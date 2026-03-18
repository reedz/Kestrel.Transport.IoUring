using System.Collections.Concurrent;
using System.Threading.Tasks.Sources;

namespace AspNetCoreUring.Transport;

/// <summary>
/// Pooled <see cref="IValueTaskSource{TResult}"/> for send completions.
/// Avoids allocating a <see cref="TaskCompletionSource{TResult}"/> per send operation.
/// Automatically returned to the pool when the result is consumed.
/// </summary>
internal sealed class PooledSendCompletion : IValueTaskSource<int>
{
    private static readonly ConcurrentQueue<PooledSendCompletion> Pool = new();

    private ManualResetValueTaskSourceCore<int> _core;

    private PooledSendCompletion()
    {
        _core.RunContinuationsAsynchronously = true;
    }

    /// <summary>Gets a completion source from the pool (or creates a new one).</summary>
    public static PooledSendCompletion Rent()
    {
        if (Pool.TryDequeue(out var item))
        {
            item._core.Reset();
            return item;
        }
        return new PooledSendCompletion();
    }

    /// <summary>Creates a <see cref="ValueTask{Int32}"/> that completes when <see cref="SetResult"/> is called.</summary>
    public ValueTask<int> AsValueTask() => new(this, _core.Version);

    /// <summary>The current version token — pass this to cancel or check status.</summary>
    public short Version => _core.Version;

    /// <summary>Completes the value task with the given result.</summary>
    public void SetResult(int result) => _core.SetResult(result);

    // IValueTaskSource<int> implementation — called by the awaiter.
    int IValueTaskSource<int>.GetResult(short token)
    {
        var result = _core.GetResult(token);
        Pool.Enqueue(this); // return to pool after result consumed
        return result;
    }

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) =>
        _core.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
