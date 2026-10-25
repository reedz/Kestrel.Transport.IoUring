namespace Kestrel.Transport.IoUring.Transport;

/// <summary>
/// Listener-owned receive retry state, including the cancel origins needed to
/// target the same live receive arm on a retry pass.
/// </summary>
internal sealed class ListenerRecvRetryState
{
    internal enum RecvRetryProcessResult
    {
        RetryPending,
        Armed,
        Drop,
    }

    internal enum RecvCancelRetryProcessResult
    {
        RetryPending,
        Selected,
        Drop,
    }

    internal readonly struct RecvRetryConnectionState(
        ushort generation,
        ulong recvEpoch,
        bool isClosing,
        bool hasRecvInFlight,
        bool recvCancelPending,
        bool usingBufferRing,
        bool recvRearmPending = false)
    {
        internal ushort Generation { get; } = generation;
        internal ulong RecvEpoch { get; } = recvEpoch;
        internal bool IsClosing { get; } = isClosing;
        internal bool HasRecvInFlight { get; } = hasRecvInFlight;
        internal bool RecvCancelPending { get; } = recvCancelPending;
        internal bool UsingBufferRing { get; } = usingBufferRing;
        internal bool RecvRearmPending { get; } = recvRearmPending;
    }

    internal readonly struct RecvCancelRetryOrigin(ushort generation, ulong recvEpoch)
    {
        internal ushort Generation { get; } = generation;
        internal ulong RecvEpoch { get; } = recvEpoch;
    }

    internal interface IRecvRetryPassAdapter<TSelf>
        where TSelf : struct, IRecvRetryPassAdapter<TSelf>
    {
        static abstract bool TryGetRecvState(
            ref TSelf self,
            long connectionId,
            out RecvRetryConnectionState connectionState);

        static abstract bool SubmitRecv(
            ref TSelf self,
            long connectionId,
            in RecvRetryConnectionState connectionState);

        static abstract bool SubmitBufferRingRecv(
            ref TSelf self,
            long connectionId,
            in RecvRetryConnectionState connectionState);

        static abstract bool SubmitRecvCancel(
            ref TSelf self,
            long connectionId,
            in RecvRetryConnectionState connectionState,
            in RecvCancelRetryOrigin origin);
    }

    private readonly HashSet<long> _recvRetrySet = [];
    // FIFO order of retry arms so a connection that keeps losing a race for buffers cannot starve the
    // others. Entries may be stale (removed from the set); they are skipped when dequeued.
    private readonly Queue<long> _recvRetryOrder = new();
    private readonly HashSet<long> _recvCancelRetrySet = [];
    private readonly Dictionary<long, RecvCancelRetryOrigin> _recvCancelRetryOrigins = [];
    private readonly List<long> _recvRetryScratch = [];
    // True only when the most recent due retry pass actually tried to queue
    // recv/cancel work and lost to SQ-full.
    private bool _blockedOnSq;
    // True when the most recent pass stopped at its arm budget with receive retries still queued.
    private bool _budgetLimited;

    internal int RecvRetryCount => _recvRetrySet.Count;
    internal int RecvCancelRetryCount => _recvCancelRetrySet.Count;
    internal bool HasPendingRetryPassWork => _recvRetrySet.Count != 0 || _recvCancelRetrySet.Count != 0;
    internal bool BlockedOnSq => _blockedOnSq;
    internal bool BudgetLimited => _budgetLimited;

    internal void AddRecvRetry(long connectionId)
    {
        if (_recvRetrySet.Add(connectionId))
            _recvRetryOrder.Enqueue(connectionId);
    }

    internal void AddRecvCancelRetry(long connectionId, in RecvCancelRetryOrigin origin)
    {
        _recvCancelRetrySet.Add(connectionId);
        _recvCancelRetryOrigins[connectionId] = origin;
    }

    internal bool TryGetRecvCancelRetryOrigin(
        long connectionId,
        out RecvCancelRetryOrigin origin) =>
        _recvCancelRetryOrigins.TryGetValue(connectionId, out origin);

    internal void OnConnectionClosing(long connectionId)
    {
        _recvRetrySet.Remove(connectionId);
        if (!HasPendingRetryPassWork)
            _blockedOnSq = false;
    }

    internal void OnConnectionReleased(long connectionId)
    {
        _recvRetrySet.Remove(connectionId);
        RemoveRecvCancelRetry(connectionId);
        if (!HasPendingRetryPassWork)
            _blockedOnSq = false;
    }

    internal void ClearBlockedOnSq() => _blockedOnSq = false;

    internal void RetryFailedRecvs(
        IoUringConnection?[] connectionSlots,
        ProvidedBufferRing? bufferRing,
        ushort recvBufferGroupId,
        int maxArms = int.MaxValue)
    {
        var adapter = new ProductionPassAdapter(connectionSlots, bufferRing, recvBufferGroupId);
        RunRetryPass(ref adapter, maxArms);
    }

    /// <summary>
    /// Runs one retry pass. <paramref name="maxArms"/> bounds how many receives are re-armed per pass: with a
    /// provided buffer ring, re-arming more sockets than there are free buffers just produces another round of
    /// ENOBUFS, so the pass leaves the rest queued (in FIFO order) for the next one.
    /// </summary>
    internal void RunRetryPass<TAdapter>(ref TAdapter adapter, int maxArms = int.MaxValue)
        where TAdapter : struct, IRecvRetryPassAdapter<TAdapter>
    {
        // A retry pass must still run for cancel-only work; the per-slot stale
        // origin and in-flight gates below decide whether each marker issues or drops.
        if (!HasPendingRetryPassWork)
        {
            _blockedOnSq = false;
            _budgetLimited = false;
            _recvRetryOrder.Clear();
            return;
        }

        bool blockedOnSq = false;
        bool budgetLimited = false;
        int armed = 0;
        int queued = _recvRetryOrder.Count;
        for (int i = 0; i < queued; i++)
        {
            if (armed >= maxArms)
            {
                budgetLimited = true;
                break;
            }

            if (!_recvRetryOrder.TryDequeue(out long connectionId))
                break;
            if (!_recvRetrySet.Contains(connectionId))
                continue;

            switch (ProcessRecvRetry(ref adapter, connectionId, out bool retryBlockedOnSq))
            {
                case RecvRetryProcessResult.Armed:
                    _recvRetrySet.Remove(connectionId);
                    armed++;
                    break;
                case RecvRetryProcessResult.Drop:
                    _recvRetrySet.Remove(connectionId);
                    break;
                default:
                    _recvRetryOrder.Enqueue(connectionId);
                    break;
            }

            blockedOnSq |= retryBlockedOnSq;
        }

        _recvRetryScratch.Clear();
        foreach (long connectionId in _recvCancelRetrySet)
        {
            var origin = _recvCancelRetryOrigins.TryGetValue(connectionId, out var shadowOrigin)
                ? shadowOrigin
                : default;

            switch (ProcessRecvCancelRetry(ref adapter, connectionId, origin, out bool cancelBlockedOnSq))
            {
                case RecvCancelRetryProcessResult.Selected:
                case RecvCancelRetryProcessResult.Drop:
                    _recvRetryScratch.Add(connectionId);
                    break;
            }

            blockedOnSq |= cancelBlockedOnSq;
        }

        foreach (long connectionId in _recvRetryScratch)
            RemoveRecvCancelRetry(connectionId);

        _blockedOnSq = blockedOnSq;
        _budgetLimited = budgetLimited && _recvRetrySet.Count != 0;
    }

    private void RemoveRecvCancelRetry(long connectionId)
    {
        _recvCancelRetrySet.Remove(connectionId);
        _recvCancelRetryOrigins.Remove(connectionId);
        if (!HasPendingRetryPassWork)
            _blockedOnSq = false;
    }

    private static IoUringConnection? GetConnection(IoUringConnection?[] connectionSlots, long connectionId) =>
        (uint)connectionId < (uint)connectionSlots.Length ? connectionSlots[(int)connectionId] : null;

    private static RecvRetryProcessResult ProcessRecvRetry<TAdapter>(
        ref TAdapter adapter,
        long connectionId,
        out bool blockedOnSq)
        where TAdapter : struct, IRecvRetryPassAdapter<TAdapter>
    {
        blockedOnSq = false;

        if (!TAdapter.TryGetRecvState(ref adapter, connectionId, out var connectionState))
            return RecvRetryProcessResult.Drop;

        if (connectionState.IsClosing || connectionState.HasRecvInFlight)
            return RecvRetryProcessResult.Drop;

        // A pending cancel or a pipe flush that has not drained already has its own re-arm path
        // (HandleCancel / DrainRecvResubmitQueue). Re-arming here would bypass receive backpressure
        // and, for a cancel, leave two receives armed for one connection.
        if (connectionState.RecvCancelPending || connectionState.RecvRearmPending)
            return RecvRetryProcessResult.Drop;

        bool armed = connectionState.UsingBufferRing
            ? TAdapter.SubmitBufferRingRecv(ref adapter, connectionId, connectionState)
            : TAdapter.SubmitRecv(ref adapter, connectionId, connectionState);

        if (armed)
            return RecvRetryProcessResult.Armed;

        blockedOnSq = true;
        return RecvRetryProcessResult.RetryPending;
    }

    private static RecvCancelRetryProcessResult ProcessRecvCancelRetry<TAdapter>(
        ref TAdapter adapter,
        long connectionId,
        in RecvCancelRetryOrigin origin,
        out bool blockedOnSq)
        where TAdapter : struct, IRecvRetryPassAdapter<TAdapter>
    {
        blockedOnSq = false;

        if (!TAdapter.TryGetRecvState(ref adapter, connectionId, out var connectionState))
            return RecvCancelRetryProcessResult.Drop;

        if (connectionState.Generation != origin.Generation ||
            connectionState.RecvEpoch != origin.RecvEpoch)
        {
            return RecvCancelRetryProcessResult.Drop;
        }

        if (connectionState.IsClosing || !connectionState.HasRecvInFlight)
            return RecvCancelRetryProcessResult.Drop;

        if (!connectionState.RecvCancelPending &&
            TAdapter.SubmitRecvCancel(ref adapter, connectionId, connectionState, origin))
        {
            return RecvCancelRetryProcessResult.Selected;
        }

        blockedOnSq = !connectionState.RecvCancelPending;
        return RecvCancelRetryProcessResult.RetryPending;
    }

    private readonly struct ProductionPassAdapter(
        IoUringConnection?[] connectionSlots,
        ProvidedBufferRing? bufferRing,
        ushort recvBufferGroupId)
        : IRecvRetryPassAdapter<ProductionPassAdapter>
    {
        private readonly IoUringConnection?[] _connectionSlots = connectionSlots;
        private readonly ProvidedBufferRing? _bufferRing = bufferRing;
        private readonly ushort _recvBufferGroupId = recvBufferGroupId;

        public static bool TryGetRecvState(
            ref ProductionPassAdapter self,
            long connectionId,
            out RecvRetryConnectionState connectionState)
        {
            var conn = GetConnection(self._connectionSlots, connectionId);
            if (conn == null)
            {
                connectionState = default;
                return false;
            }

            connectionState = new RecvRetryConnectionState(
                conn.Generation,
                conn.RecvEpoch,
                conn.IsClosing,
                conn.HasRecvInFlight,
                conn.RecvCancelPending,
                conn.UsingBufferRing && self._bufferRing != null,
                conn.RecvRearmPending);
            return true;
        }

        public static bool SubmitRecv(
            ref ProductionPassAdapter self,
            long connectionId,
            in RecvRetryConnectionState connectionState)
        {
            var conn = GetConnection(self._connectionSlots, connectionId);
            return conn != null && conn.SubmitRecv();
        }

        public static bool SubmitBufferRingRecv(
            ref ProductionPassAdapter self,
            long connectionId,
            in RecvRetryConnectionState connectionState)
        {
            var conn = GetConnection(self._connectionSlots, connectionId);
            return conn != null &&
                self._bufferRing != null &&
                conn.SubmitBufferRingRecv(self._recvBufferGroupId);
        }

        public static bool SubmitRecvCancel(
            ref ProductionPassAdapter self,
            long connectionId,
            in RecvRetryConnectionState connectionState,
            in RecvCancelRetryOrigin origin)
        {
            var conn = GetConnection(self._connectionSlots, connectionId);
            return conn != null && conn.SubmitRecvCancel();
        }
    }
}
