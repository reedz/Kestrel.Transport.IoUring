namespace Kestrel.Transport.IoUring.Transport;

/// <summary>
/// Listener-owned send retry state. The queued wake callback is wired at
/// construction time so send retry requests can publish the listener eventfd
/// wake without reshaping the request/resume plumbing.
/// </summary>
internal sealed class ListenerSendRetryState
{
    internal readonly struct SendRetryConnectionState(long generation)
    {
        internal long Generation { get; } = generation;
    }

    internal interface ISendRetryResumePassAdapter<TSelf>
        where TSelf : struct, ISendRetryResumePassAdapter<TSelf>
    {
        static abstract bool TryGetConnectionState(
            ref TSelf self,
            long connectionId,
            out SendRetryConnectionState connectionState);

        static abstract void ResumeSendRetry(
            ref TSelf self,
            long connectionId,
            in SendRetryConnectionState connectionState);
    }

    // Keyed by slot and the occupant's 64-bit generation, so a retry queued by an earlier occupant can never resume
    // a later one (see IoUringConnection.Generation).
    private readonly HashSet<(long ConnectionId, long Generation)> _sendRetrySet = [];
    private readonly List<(long ConnectionId, long Generation)> _sendRetryScratch = [];
    private readonly Action _wakeIoLoop;

    internal ListenerSendRetryState(Action wakeIoLoop)
    {
        _wakeIoLoop = wakeIoLoop;
    }

    internal int SendRetryCount => _sendRetrySet.Count;

    internal bool ContainsSendRetry(long connectionId, long generation) =>
        _sendRetrySet.Contains((connectionId, generation));

    internal void RequestSendRetry(long connectionId, long generation)
    {
        _sendRetrySet.Add((connectionId, generation));
        _wakeIoLoop();
    }

    internal void ResumeSendRetries(IoUringConnection?[] connectionSlots)
    {
        var adapter = new ProductionResumePassAdapter(connectionSlots);
        RunResumePass(ref adapter);
    }

    internal void RunResumePass<TAdapter>(ref TAdapter adapter)
        where TAdapter : struct, ISendRetryResumePassAdapter<TAdapter>
    {
        if (_sendRetrySet.Count == 0)
            return;

        _sendRetryScratch.Clear();
        foreach (var request in _sendRetrySet)
            _sendRetryScratch.Add(request);

        _sendRetrySet.Clear();
        foreach (var (connectionId, generation) in _sendRetryScratch)
            ProcessSendRetry(ref adapter, connectionId, generation);
    }

    private static IoUringConnection? GetConnection(IoUringConnection?[] connectionSlots, long connectionId) =>
        (uint)connectionId < (uint)connectionSlots.Length ? connectionSlots[(int)connectionId] : null;

    private static void ProcessSendRetry<TAdapter>(
        ref TAdapter adapter,
        long connectionId,
        long generation)
        where TAdapter : struct, ISendRetryResumePassAdapter<TAdapter>
    {
        if (!TAdapter.TryGetConnectionState(ref adapter, connectionId, out var connectionState))
            return;

        if (connectionState.Generation != generation)
            return;

        TAdapter.ResumeSendRetry(ref adapter, connectionId, connectionState);
    }

    private readonly struct ProductionResumePassAdapter(IoUringConnection?[] connectionSlots)
        : ISendRetryResumePassAdapter<ProductionResumePassAdapter>
    {
        private readonly IoUringConnection?[] _connectionSlots = connectionSlots;

        public static bool TryGetConnectionState(
            ref ProductionResumePassAdapter self,
            long connectionId,
            out SendRetryConnectionState connectionState)
        {
            var conn = GetConnection(self._connectionSlots, connectionId);
            if (conn == null)
            {
                connectionState = default;
                return false;
            }

            connectionState = new SendRetryConnectionState(conn.Generation);
            return true;
        }

        public static void ResumeSendRetry(
            ref ProductionResumePassAdapter self,
            long connectionId,
            in SendRetryConnectionState connectionState)
        {
            GetConnection(self._connectionSlots, connectionId)?.ResumeSendRetry();
        }
    }
}
