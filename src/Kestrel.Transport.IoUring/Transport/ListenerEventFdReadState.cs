namespace Kestrel.Transport.IoUring.Transport;

/// <summary>Listener-owned eventfd read rearm state.</summary>
internal struct ListenerEventFdReadState
{
    internal interface IEventFdReadSubmitAdapter<TSelf>
        where TSelf : struct, IEventFdReadSubmitAdapter<TSelf>
    {
        static abstract bool TrySubmitEventFdRead(ref TSelf self);
    }

    private bool _readInFlight;
    private bool _retryRequired;

    internal bool ReadInFlight => _readInFlight;
    internal bool RetryRequired => _retryRequired;
    internal bool RetryPendingWithoutReadInFlight => _retryRequired && !_readInFlight;

    internal void OnEventFdReadCompletion()
    {
        _readInFlight = false;
    }

    internal void OnEventFdReadCompletionDispatchError()
    {
        _readInFlight = false;
        _retryRequired = true;
    }

    internal bool TrySubmitEventFdRead<TAdapter>(ref TAdapter adapter)
        where TAdapter : struct, IEventFdReadSubmitAdapter<TAdapter>
    {
        if (_readInFlight)
            return true;

        if (TAdapter.TrySubmitEventFdRead(ref adapter))
        {
            _readInFlight = true;
            _retryRequired = false;
            return true;
        }

        _retryRequired = true;
        return false;
    }

    internal void RetryPendingEventFdRead<TAdapter>(ref TAdapter adapter)
        where TAdapter : struct, IEventFdReadSubmitAdapter<TAdapter>
    {
        if (_retryRequired)
            TrySubmitEventFdRead(ref adapter);
    }
}
