using Kestrel.Transport.IoUring.Native;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>
/// Best-effort reclamation of sockets accepted by completions already in the CQ when a listener is quarantined
/// (its ring could not be drained to idle, see <see cref="IoUringConnectionListener"/>'s teardown). Normal disposal
/// does not need it: the owning thread consumes every completion until each request has ended.
/// </summary>
internal static class ListenerStoppedCompletionDrain
{
    internal readonly record struct DrainResult(int MaterializationErrno, Exception? Failure);

    internal interface ICompletionSource<TSelf>
        where TSelf : struct, ICompletionSource<TSelf>
    {
        static abstract (uint AvailableCompletions, int MaterializationErrno, bool HasNodropOverflowBacklog)
            CaptureAvailableCompletions(
                ref TSelf self,
                bool materializeNodropOverflowBacklog);

        static abstract bool TryPeekCompletion(ref TSelf self, out IoUringCqe cqe);

        static abstract void AdvanceCompletion(ref TSelf self);
    }

    internal interface IAcceptedSocketCloseRecorder<TSelf>
        where TSelf : struct, IAcceptedSocketCloseRecorder<TSelf>
    {
        static abstract void CloseAcceptedSocket(ref TSelf self, int socketFd);
    }

    internal static DrainResult Drain(Ring ring)
    {
        var completionSource = new RingCompletionSource(ring);
        var closeRecorder = default(ProductionAcceptedSocketCloseRecorder);
        return Drain(ref completionSource, ref closeRecorder);
    }

    internal static DrainResult Drain<TCompletionSource, TAcceptedSocketCloseRecorder>(
        ref TCompletionSource completionSource,
        ref TAcceptedSocketCloseRecorder acceptedSocketCloseRecorder)
        where TCompletionSource : struct, ICompletionSource<TCompletionSource>
        where TAcceptedSocketCloseRecorder : struct, IAcceptedSocketCloseRecorder<TAcceptedSocketCloseRecorder>
    {
        // After the IO loop has stopped, drain the visible CQ snapshot at that
        // boundary. Only when the shared SQ overflow flag says hidden NODROP
        // overflow-list work still remains do we take another bounded nonblocking
        // materialization step and drain exactly that next snapshot. Ordinary
        // visible post-snapshot CQEs alone never extend the teardown drain.
        int materializationErrno = 0;
        FailureAccumulator failure = default;

        try
        {
            var capture = TCompletionSource.CaptureAvailableCompletions(
                ref completionSource,
                materializeNodropOverflowBacklog: false);
            materializationErrno = capture.MaterializationErrno;
            DrainCapturedSnapshot(
                ref completionSource,
                ref acceptedSocketCloseRecorder,
                capture.AvailableCompletions,
                ref failure);

            if (materializationErrno == 0)
            {
                bool hasNodropOverflowBacklog = capture.HasNodropOverflowBacklog;
                for (int overflowRound = 0;
                     overflowRound < Ring.TeardownOverflowDrainRoundLimit && hasNodropOverflowBacklog;
                     overflowRound++)
                {
                    capture = TCompletionSource.CaptureAvailableCompletions(
                        ref completionSource,
                        materializeNodropOverflowBacklog: true);

                    if (materializationErrno == 0)
                        materializationErrno = capture.MaterializationErrno;

                    uint drainedCompletions = DrainCapturedSnapshot(
                        ref completionSource,
                        ref acceptedSocketCloseRecorder,
                        capture.AvailableCompletions,
                        ref failure);

                    hasNodropOverflowBacklog = capture.HasNodropOverflowBacklog;
                    if (capture.MaterializationErrno != 0 || drainedCompletions == 0)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            failure.Capture(ex);
        }

        return new(materializationErrno, failure.ToException());
    }

    private static uint DrainCapturedSnapshot<TCompletionSource, TAcceptedSocketCloseRecorder>(
        ref TCompletionSource completionSource,
        ref TAcceptedSocketCloseRecorder acceptedSocketCloseRecorder,
        uint availableCompletions,
        ref FailureAccumulator failure)
        where TCompletionSource : struct, ICompletionSource<TCompletionSource>
        where TAcceptedSocketCloseRecorder : struct, IAcceptedSocketCloseRecorder<TAcceptedSocketCloseRecorder>
    {
        uint drained = 0;
        for (; drained < availableCompletions; drained++)
        {
            if (!TCompletionSource.TryPeekCompletion(ref completionSource, out var cqe))
                break;

            try
            {
                DrainCompletion(ref acceptedSocketCloseRecorder, cqe);
            }
            catch (Exception ex)
            {
                failure.Capture(ex);
            }
            finally
            {
                TCompletionSource.AdvanceCompletion(ref completionSource);
            }
        }

        return drained;
    }

    private static void DrainCompletion<TAcceptedSocketCloseRecorder>(
        ref TAcceptedSocketCloseRecorder acceptedSocketCloseRecorder,
        in IoUringCqe cqe)
        where TAcceptedSocketCloseRecorder : struct, IAcceptedSocketCloseRecorder<TAcceptedSocketCloseRecorder>
    {
        if (cqe.UserData != IoUringConstants.ACCEPT_USER_DATA || cqe.Res < 0)
            return;

        TAcceptedSocketCloseRecorder.CloseAcceptedSocket(ref acceptedSocketCloseRecorder, cqe.Res);
    }

    private struct FailureAccumulator
    {
        private Exception? _failure;
        private List<Exception>? _failures;

        internal void Capture(Exception failure)
        {
            failure = FlattenFailure(failure);

            if (_failures is not null)
            {
                AddFailureLeaves(_failures, failure);
                return;
            }

            if (_failure is null)
            {
                _failure = failure;
                return;
            }

            _failures = new(GetFailureLeafCount(_failure) + GetFailureLeafCount(failure));
            AddFailureLeaves(_failures, _failure);
            AddFailureLeaves(_failures, failure);
            _failure = null;
        }

        internal Exception? ToException()
        {
            if (_failures is null)
                return _failure;

            return _failures.Count == 1 ? _failures[0] : new AggregateException(_failures);
        }

        private static Exception FlattenFailure(Exception failure)
        {
            if (failure is not AggregateException aggregateException)
                return failure;

            AggregateException flattened = aggregateException.Flatten();
            return flattened.InnerExceptions.Count == 1
                ? flattened.InnerExceptions[0]
                : flattened;
        }

        private static int GetFailureLeafCount(Exception failure) =>
            failure is AggregateException aggregateException
                ? aggregateException.InnerExceptions.Count
                : 1;

        private static void AddFailureLeaves(List<Exception> failures, Exception failure)
        {
            if (failure is AggregateException aggregateException)
            {
                failures.AddRange(aggregateException.InnerExceptions);
                return;
            }

            failures.Add(failure);
        }
    }

    internal readonly struct RingCompletionSource(Ring ring)
        : ICompletionSource<RingCompletionSource>
    {
        private readonly Ring _ring = ring;

        public static (uint AvailableCompletions, int MaterializationErrno, bool HasNodropOverflowBacklog)
            CaptureAvailableCompletions(
                ref RingCompletionSource self,
                bool materializeNodropOverflowBacklog)
        {
            Ring.TeardownCompletionSnapshot snapshot = self._ring.CaptureCompletionSnapshotForTeardown(
                materializeNodropOverflowBacklog);
            return (
                snapshot.AvailableCompletions,
                snapshot.MaterializationErrno,
                snapshot.HasNodropOverflowBacklog);
        }

        public static bool TryPeekCompletion(ref RingCompletionSource self, out IoUringCqe cqe) =>
            self._ring.TryPeekCompletion(out cqe);

        public static void AdvanceCompletion(ref RingCompletionSource self) =>
            self._ring.AdvanceCompletion();
    }

    internal readonly struct ProductionAcceptedSocketCloseRecorder
        : IAcceptedSocketCloseRecorder<ProductionAcceptedSocketCloseRecorder>
    {
        public static void CloseAcceptedSocket(
            ref ProductionAcceptedSocketCloseRecorder self,
            int socketFd)
        {
            Libc.close(socketFd);
        }
    }
}
