using System.Threading;

namespace Kestrel.Transport.IoUring.Transport;

internal sealed class ListenerEventFdWakeState
{
    private const int ClosingMask = int.MinValue;
    private const int WriterCountMask = int.MaxValue;

    internal interface IEventFdWakeAdapter<TSelf>
        where TSelf : struct, IEventFdWakeAdapter<TSelf>
    {
        static abstract void Write(ref TSelf self);

        static abstract ValueTask CloseAsync(ref TSelf self);
    }

    private int _state;
    private TaskCompletionSource<bool>? _closeCompletion;
    private TaskCompletionSource<bool>? _writersDrained;

    internal void Wake<TAdapter>(ref TAdapter adapter)
        where TAdapter : struct, IEventFdWakeAdapter<TAdapter>
    {
        var spinner = new SpinWait();
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if ((state & ClosingMask) != 0)
                return;

            if ((state & WriterCountMask) == WriterCountMask)
                throw new InvalidOperationException("Too many concurrent eventfd wake writers.");

            // Writer acquisition and the closing transition share one word, so a writer
            // cannot acquire participation from a stale pre-close observation.
            if (Interlocked.CompareExchange(ref _state, state + 1, state) == state)
                break;

            spinner.SpinOnce();
        }

        try
        {
            TAdapter.Write(ref adapter);
        }
        finally
        {
            int state = Interlocked.Decrement(ref _state);
            if ((state & ClosingMask) != 0 && (state & WriterCountMask) == 0)
                Volatile.Read(ref _writersDrained)!.TrySetResult(true);
        }
    }

    internal ValueTask CloseAsync<TAdapter>(ref TAdapter adapter)
        where TAdapter : struct, IEventFdWakeAdapter<TAdapter>
    {
        TaskCompletionSource<bool>? completion = Volatile.Read(ref _closeCompletion);
        if (completion is null)
        {
            var createdCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion = Interlocked.CompareExchange(
                ref _closeCompletion,
                createdCompletion,
                comparand: null);

            if (completion is null)
            {
                completion = createdCompletion;
                TAdapter adapterCopy = adapter;
                BeginClose(adapterCopy, completion);
            }
        }

        return new ValueTask(completion.Task);
    }

    private void BeginClose<TAdapter>(
        TAdapter adapter,
        TaskCompletionSource<bool> closeCompletion)
        where TAdapter : struct, IEventFdWakeAdapter<TAdapter>
    {
        var writersDrained = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _writersDrained, writersDrained);

        int previousState = Interlocked.Or(ref _state, ClosingMask);
        if ((previousState & WriterCountMask) == 0)
            writersDrained.TrySetResult(true);

        if (writersDrained.Task.IsCompletedSuccessfully)
        {
            StartAdapterClose(adapter, closeCompletion);
            return;
        }

        _ = CloseAfterWritersDrainAsync(writersDrained.Task, adapter, closeCompletion);
    }

    private static void StartAdapterClose<TAdapter>(
        TAdapter adapter,
        TaskCompletionSource<bool> closeCompletion)
        where TAdapter : struct, IEventFdWakeAdapter<TAdapter>
    {
        try
        {
            ValueTask close = TAdapter.CloseAsync(ref adapter);
            if (close.IsCompletedSuccessfully)
            {
                close.GetAwaiter().GetResult();
                closeCompletion.TrySetResult(true);
                return;
            }

            _ = CompleteAdapterCloseAsync(close, closeCompletion);
        }
        catch (Exception ex)
        {
            closeCompletion.TrySetException(ex);
        }
    }

    private static async Task CloseAfterWritersDrainAsync<TAdapter>(
        Task writersDrained,
        TAdapter adapter,
        TaskCompletionSource<bool> closeCompletion)
        where TAdapter : struct, IEventFdWakeAdapter<TAdapter>
    {
        try
        {
            await writersDrained.ConfigureAwait(false);
            await TAdapter.CloseAsync(ref adapter).ConfigureAwait(false);
            closeCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            closeCompletion.TrySetException(ex);
        }
    }

    private static async Task CompleteAdapterCloseAsync(
        ValueTask adapterClose,
        TaskCompletionSource<bool> closeCompletion)
    {
        try
        {
            await adapterClose.ConfigureAwait(false);
            closeCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            closeCompletion.TrySetException(ex);
        }
    }
}
