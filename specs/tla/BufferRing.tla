---- MODULE BufferRing ----
EXTENDS Naturals, Sequences, FiniteSets, TLC

\* Phase 3 traceability / scope.
\*
\* Init models the post-constructor state after successful provided-buffer-ring
\* registration and initial fill in:
\*   - src/Kestrel.Transport.IoUring/ProvidedBufferRing.cs:31-87
\*     (ProvidedBufferRing ctor, AddBuffer, CommitTail)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:939-955
\*     (initial SubmitBufferRingRecv from accept path)
\*
\* ArmRecv and RetryArmRecv abstract successful provided-buffer recv submits from:
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnection.cs:238-258
\*     (SubmitBufferRingRecv)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:520-548
\*     (DrainRecvResubmitQueue)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:570-605
\*     (RetryFailedRecvs)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:1019-1025,
\*     1075-1082, 1127-1135
\*     (HandleRecv and HandleCancel rearm call sites)
\*
\* KernelSelectAvailableBuffer, KernelCompleteSelected, PublishCompletion,
\* LoseCompletionToOverflow, and StartTransportProcessing abstract the recv CQE
\* path:
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:690-732
\*     (ProcessCompletions, CQ overflow fail-safe)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:746-790
\*     (DispatchCompletion)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:976-1083
\*     (HandleRecv, F_BUFFER / bid extraction and processing)
\*   - src/Kestrel.Transport.IoUring/Native/IoUringConstants.cs:88-93
\*     (IORING_CQE_F_BUFFER, IORING_CQE_BUFFER_SHIFT)
\*
\* FinishOpenDataContinue / Deferred / Close, FinishOpenTerminal, and
\* FinishClosingOrStale abstract:
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnection.cs:285-364
\*     (OnRecvCompleteFromBuffer, WriteAndFlushRecvData, ResumeReceiveAfterFlush)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:976-1083
\*     (open, terminal, closing, and stale provided-buffer recv handling)
\*
\* ObserveEnobufs, ObserveRecvCanceled, HandleCancelCompletion,
\* ObserveTerminalNoBufferCompletion, and ResolveDeferred* abstract the
\* control-only completion / wakeup paths that do not carry a provided-buffer
\* bid:
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:520-548
\*     (DrainRecvResubmitQueue)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:570-605
\*     (RetryFailedRecvs)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:1005-1027
\*     (ENOBUFS / ECANCELED handling)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:1121-1138
\*     (HandleCancel)
\*
\* RequestClose, FinalizeClose, CloseRingFd, KernelTeardownComplete, and
\* UnmapBufferRing abstract close and shutdown cleanup:
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:1164-1198
\*     (BeginCloseConnection, TryFinalizeClose, ShutdownSocket)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:1268-1302
\*     (DisposeAsync)
\*   - src/Kestrel.Transport.IoUring/Ring.cs:396-406
\*     (Ring.Dispose closes the ring fd)
\*   - src/Kestrel.Transport.IoUring/ProvidedBufferRing.cs:121-141
\*     (Dispose, DisposeAfterRingClosed, unregister-vs-unmap split)
\*
\* Model assumptions and exclusions:
\*   1. One buffer-ring-enabled connection is modeled. Accept, send,
\*      registered-files churn, and accept-channel backpressure are excluded.
\*   2. The model starts after successful ring registration/fill. mmap/register
\*      failures and the single-shot fallback are excluded once Init holds.
\*   3. Buffer-bearing CQEs always use bids from BufferUniverse. Invalid
\*      F_BUFFER / bid values are excluded by scope rather than proved.
\*   4. BufferUniverse cardinality is assumed to be a power of two so modulo
\*      head/tail cursors faithfully abstract `_tail & _mask` in
\*      ProvidedBufferRing.AddBuffer. Raw ushort wrap is still excluded.
\*   5. ringAvailable is a per-bid multiset, not a plain set, so duplicate
\*      recycle bugs mechanically violate AllBufferConservation. Exact FIFO slot
\*      order inside the full ring remains abstracted away.
\*   6. Retry timers, repeated SQ-full failures on direct rearm/cancel submits,
\*      and the separate cancel-retry set are excluded. recvRetryPending only
\*      abstracts listener `_recvRetrySet`; direct ArmRecv does not clear it,
\*      RetryArmRecv models its eventual successful retry, and RetryDropStale
\*      abstracts RetryFailedRecvs removing stale entries after close or after a
\*      direct rearm already restored recvInFlight.
\*   7. IoUringRing.tla already models CQ overflow observation and fail-fast.
\*      This phase keeps only the single-connection ownership effect of a
\*      dropped buffer-bearing CQE via cqOverflowLost until the ring itself is
\*      closed.
\*   8. CloseRingFd only models Ring.Dispose closing the main ring fd. It stops
\*      new buffer-ring kernel actions, but it does not itself reclaim bids
\*      still parked in selected/completionBacklog/cqOwned/cqOverflowLost and
\*      may occur even if the connection still looks open or recv bookkeeping
\*      is still live after the graceful-drain timeout.
\*   9. KernelTeardownComplete is an explicit environment assumption, not a
\*      proof about Linux semantics. It models eventual post-close teardown that
\*      makes lingering kernel/completion-owned bids reclaimable and stops recv
\*      before DisposeAfterRingClosed unmaps the ring memory.
\*
\* Compositional note:
\*   Cross-module use of BufferRing.tla with IoUringRing.tla or Listener.tla is
\*   by assumption, not a checked refinement proof. The CQ-overflow story here
\*   is a favorable single-connection over-approx of Listener.tla's
\*   all-connections FailRing path: cqOverflowLost records only the lost-bid
\*   ownership effect while the listener model fails the whole ring.

CONSTANTS
    BufferUniverse,
    CqEntries,
    MutateDoubleRecycle,
    MutateLoseRecycle,
    MutateUnmapBeforeRingClose,
    MutateUnmapBeforeTeardown

RingEntries == Cardinality(BufferUniverse)

ASSUME
    /\ BufferUniverse # {}
    /\ \E k \in 0..RingEntries : RingEntries = 2^k
    /\ CqEntries \in Nat
    /\ CqEntries > 0
    /\ MutateDoubleRecycle \in BOOLEAN
    /\ MutateLoseRecycle \in BOOLEAN
    /\ MutateUnmapBeforeRingClose \in BOOLEAN
    /\ MutateUnmapBeforeTeardown \in BOOLEAN

VARIABLES
    ringRegistered,
    ringFdClosed,
    kernelTeardownDone,
    ringUnmapped,
    headCursor,
    tailCursor,
    ringOccupancy,
    ringAvailable,
    selected,
    completionBacklog,
    cqOwned,
    cqOverflowLost,
    processing,
    disposed,
    compKind,
    compMore,
    recvInFlight,
    recvRearmPending,
    recvCancelPending,
    recvCancelIssued,
    recvRetryPending,
    connState

CompletionKinds == {"data", "terminal"}
ConnStates == {"open", "closing", "closed"}
NoBuffer == "no-buffer"
BufferSymmetry == Permutations(BufferUniverse)

Inc(cursor) == (cursor + 1) % RingEntries
IncBy(cursor, amount) == (cursor + amount) % RingEntries
BoolToNat(p) == IF p THEN 1 ELSE 0
ZeroCounts == [b \in BufferUniverse |-> 0]

RECURSIVE SumCounts(_, _)
SumCounts(S, counts) ==
    IF S = {}
        THEN 0
        ELSE LET b == CHOOSE x \in S : TRUE
             IN counts[b] + SumCounts(S \ {b}, counts)

RingAvailableCount == SumCounts(BufferUniverse, ringAvailable)
RingAvailableIds == { b \in BufferUniverse : ringAvailable[b] > 0 }
ProcessingSet == IF processing = NoBuffer THEN {} ELSE {processing}

KernelOrCompletionOwned ==
    selected \cup completionBacklog \cup cqOwned \cup cqOverflowLost

NonRingOwnershipSets ==
    << selected,
       completionBacklog,
       cqOwned,
       cqOverflowLost,
       ProcessingSet,
       disposed >>

PairwiseDisjoint(sets) ==
    \A i, j \in 1..Len(sets) : i < j => sets[i] \cap sets[j] = {}

OwnedCount(b) ==
    ringAvailable[b]
    + BoolToNat(b \in selected)
    + BoolToNat(b \in completionBacklog)
    + BoolToNat(b \in cqOwned)
    + BoolToNat(b \in cqOverflowLost)
    + BoolToNat(processing = b)
    + BoolToNat(b \in disposed)

CanArmRecv ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ connState = "open"
    /\ ~recvInFlight
    /\ ~recvRearmPending
    /\ ~recvCancelPending

CanRetryArmRecv ==
    /\ recvRetryPending
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ connState = "open"
    /\ ~recvInFlight

Vars ==
    << ringRegistered,
       ringFdClosed,
       kernelTeardownDone,
       ringUnmapped,
       headCursor,
       tailCursor,
       ringOccupancy,
       ringAvailable,
       selected,
       completionBacklog,
       cqOwned,
       cqOverflowLost,
       processing,
       disposed,
       compKind,
       compMore,
       recvInFlight,
       recvRearmPending,
       recvCancelPending,
       recvCancelIssued,
       recvRetryPending,
       connState >>

Init ==
    /\ ringRegistered = TRUE
    /\ ringFdClosed = FALSE
    /\ kernelTeardownDone = FALSE
    /\ ringUnmapped = FALSE
    /\ headCursor = 0
    /\ tailCursor = 0
    /\ ringOccupancy = RingEntries
    /\ ringAvailable = [b \in BufferUniverse |-> 1]
    /\ selected = {}
    /\ completionBacklog = {}
    /\ cqOwned = {}
    /\ cqOverflowLost = {}
    /\ processing = NoBuffer
    /\ disposed = {}
    /\ compKind = [b \in BufferUniverse |-> "data"]
    /\ compMore = [b \in BufferUniverse |-> FALSE]
    /\ recvInFlight = FALSE
    /\ recvRearmPending = FALSE
    /\ recvCancelPending = FALSE
    /\ recvCancelIssued = FALSE
    /\ recvRetryPending = FALSE
    /\ connState = "open"

\* Successful direct submit from HandleRecv/HandleCancel/DrainRecvResubmitQueue.
\* A stale retry-set membership can survive this direct rearm until a later
\* RetryDropStale sweep removes it.
ArmRecv ==
    /\ CanArmRecv
    /\ recvInFlight' = TRUE
    /\ recvCancelIssued' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvRearmPending,
           recvCancelPending,
           recvRetryPending,
           connState >>

\* Successful retry from listener _recvRetrySet. This deliberately ignores
\* recvRearmPending and recvCancelPending to match RetryFailedRecvs.
RetryArmRecv ==
    /\ CanRetryArmRecv
    /\ recvInFlight' = TRUE
    /\ recvCancelIssued' = FALSE
    /\ recvRetryPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvRearmPending,
           recvCancelPending,
           connState >>

\* RetryFailedRecvs also drops stale retry-set entries when the connection is
\* already closing/closed or a direct rearm has already restored recvInFlight.
RetryDropStale ==
    /\ recvRetryPending
    /\ (connState # "open" \/ recvInFlight)
    /\ recvRetryPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           connState >>

\* The kernel may own at most one not-yet-completed selected bid at a time, but
\* it may continue to publish earlier completed CQEs later and in any order.
KernelSelectAvailableBuffer(b) ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ connState \in {"open", "closing"}
    /\ recvInFlight
    /\ selected = {}
    /\ ringAvailable[b] > 0
    /\ ringAvailable' = [ringAvailable EXCEPT ![b] = @ - 1]
    /\ selected' = {b}
    /\ headCursor' = Inc(headCursor)
    /\ ringOccupancy' = ringOccupancy - 1
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           tailCursor,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* A selected buffer becomes a buffer-bearing recv completion. kind="terminal"
\* abstracts result<=0 completions that still carry F_BUFFER/bid. Terminal
\* completions never advertise F_MORE.
KernelCompleteSelected(b) ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ b \in selected
    /\ \E kind \in CompletionKinds, more \in BOOLEAN :
        /\ kind = "data" \/ ~more
        /\ selected' = {}
        /\ completionBacklog' = completionBacklog \cup {b}
        /\ compKind' = [compKind EXCEPT ![b] = kind]
        /\ compMore' = [compMore EXCEPT ![b] = more]
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* Kernel-private completion backlog publishes any eligible buffer-bearing CQE
\* into the bounded user-visible CQ.
PublishCompletion(b) ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ b \in completionBacklog
    /\ Cardinality(cqOwned) < CqEntries
    /\ completionBacklog' = completionBacklog \ {b}
    /\ cqOwned' = cqOwned \cup {b}
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* A buffer-bearing CQE can be dropped under CQ overflow/OOM. Phase 1-2 models
\* the fail-fast control flow; this phase retains the lost bid until ring close.
LoseCompletionToOverflow(b) ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ b \in completionBacklog
    /\ completionBacklog' = completionBacklog \ {b}
    /\ cqOverflowLost' = cqOverflowLost \cup {b}
    /\ connState' = IF connState = "open" THEN "closing" ELSE connState
    /\ recvRetryPending' = IF connState = "open" THEN FALSE ELSE recvRetryPending
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           cqOwned,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued >>

\* Control-only ENOBUFS completion: no F_BUFFER / bid, but the armed multishot
\* recv stops and listener retry state becomes pending.
ObserveEnobufs ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ connState = "open"
    /\ recvInFlight
    /\ selected = {}
    /\ RingAvailableCount = 0
    /\ recvInFlight' = FALSE
    /\ recvCancelIssued' = FALSE
    /\ recvRetryPending' = TRUE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvRearmPending,
           recvCancelPending,
           connState >>

\* Listener dequeues one published CQE and begins HandleRecv/DispatchCompletion.
\* A !F_MORE CQE clears HasRecvInFlight when transport first observes it.
StartTransportProcessing(b) ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ b \in cqOwned
    /\ processing = NoBuffer
    /\ cqOwned' = cqOwned \ {b}
    /\ processing' = b
    /\ recvInFlight' = IF compMore[b] THEN recvInFlight ELSE FALSE
    /\ recvCancelIssued' = IF compMore[b] THEN recvCancelIssued ELSE FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOverflowLost,
           disposed,
           compKind,
           compMore,
           recvRearmPending,
           recvCancelPending,
           recvRetryPending,
           connState >>

\* Open connection, positive recv, synchronous flush or immediate continue path.
FinishOpenDataContinue(b) ==
    /\ processing = b
    /\ connState = "open"
    /\ compKind[b] = "data"
    /\ ringAvailable' = [ringAvailable EXCEPT ![b] = @ + 1]
    /\ ringOccupancy' = ringOccupancy + 1
    /\ tailCursor' = Inc(tailCursor)
    /\ processing' = NoBuffer
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* Open connection, positive recv, async flush / queued copy path. Buffer
\* ownership returns immediately to the ring; deferred state is pure control flow.
FinishOpenDataDeferred(b) ==
    /\ processing = b
    /\ connState = "open"
    /\ compKind[b] = "data"
    /\ ringAvailable' = [ringAvailable EXCEPT ![b] = @ + 1]
    /\ ringOccupancy' = ringOccupancy + 1
    /\ tailCursor' = Inc(tailCursor)
    /\ processing' = NoBuffer
    /\ recvRearmPending' = TRUE
    /\ recvCancelPending' = (recvCancelPending \/ compMore[b])
    /\ recvCancelIssued' = (recvCancelIssued \/ compMore[b])
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRetryPending,
           connState >>

\* Open connection, positive recv, but pipe write/flush/backpressure closes the
\* transport after the buffer is recycled in the finally block.
FinishOpenDataClose(b) ==
    /\ processing = b
    /\ connState = "open"
    /\ compKind[b] = "data"
    /\ ringAvailable' = [ringAvailable EXCEPT ![b] = @ + 1]
    /\ ringOccupancy' = ringOccupancy + 1
    /\ tailCursor' = Inc(tailCursor)
    /\ processing' = NoBuffer
    /\ connState' = "closing"
    /\ recvRetryPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued >>

\* Provided-buffer recv completed with result<=0, so the connection starts close
\* after returning the buffer.
FinishOpenTerminal(b) ==
    /\ processing = b
    /\ connState = "open"
    /\ compKind[b] = "terminal"
    /\ ringAvailable' = [ringAvailable EXCEPT ![b] = @ + 1]
    /\ ringOccupancy' = ringOccupancy + 1
    /\ tailCursor' = Inc(tailCursor)
    /\ processing' = NoBuffer
    /\ connState' = "closing"
    /\ recvRetryPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued >>

\* Closing or already-removed connection: a stale/closing F_BUFFER CQE still
\* recycles its bid once, but transport processing is otherwise terminal.
FinishClosingOrStale(b) ==
    /\ processing = b
    /\ connState \in {"closing", "closed"}
    /\ ringAvailable' = [ringAvailable EXCEPT ![b] = @ + 1]
    /\ ringOccupancy' = ringOccupancy + 1
    /\ tailCursor' = Inc(tailCursor)
    /\ processing' = NoBuffer
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* Model-only mutation: recycle the same processing bid twice, creating a
\* duplicate ring slot that AllBufferConservation can now falsify mechanically.
DoubleRecycleBug(b) ==
    /\ MutateDoubleRecycle
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ processing = b
    /\ ringAvailable' = [ringAvailable EXCEPT ![b] = @ + 2]
    /\ ringOccupancy' = ringOccupancy + 2
    /\ tailCursor' = IncBy(tailCursor, 2)
    /\ processing' = NoBuffer
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* Model-only mutation: drop a provided buffer after processing instead of
\* recycling it in the finally block, violating ownership conservation.
LoseRecycleBug(b) ==
    /\ MutateLoseRecycle
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ processing = b
    /\ processing' = NoBuffer
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* WaitForFlushThenRequestRecv / DrainRecvResubmitQueue continuation. Any
\* follow-up ArmRecv/RetryArmRecv happens as a separate action.
ResolveDeferredContinue ==
    /\ connState = "open"
    /\ recvRearmPending
    /\ recvRearmPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* Deferred flush can also fail and request close after the buffer was already
\* recycled. Closing immediately removes the connection from recv-retry state.
ResolveDeferredClose ==
    /\ connState = "open"
    /\ recvRearmPending
    /\ recvRearmPending' = FALSE
    /\ connState' = "closing"
    /\ recvRetryPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvCancelPending,
           recvCancelIssued >>

\* HandleRecv saw ECANCELED for the live multishot recv. The separate cancel CQE
\* may have arrived earlier or may still be pending.
ObserveRecvCanceled ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ connState = "open"
    /\ recvInFlight
    /\ recvCancelIssued
    /\ selected = {}
    /\ recvInFlight' = FALSE
    /\ recvCancelIssued' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvRearmPending,
           recvCancelPending,
           recvRetryPending,
           connState >>

\* Separate cancel CQE clears RecvCancelPending. It may arrive before or after
\* the target recv actually terminates.
HandleCancelCompletion ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ recvCancelPending
    /\ recvCancelPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* External or transport-triggered close request enters the closing state and
\* removes the connection from listener recv-retry state.
RequestClose ==
    /\ connState = "open"
    /\ connState' = "closing"
    /\ recvRetryPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued >>

\* Control-only terminal completion with no F_BUFFER. For an open connection
\* this corresponds to result<=0 without a provided buffer. For a closing
\* connection it covers the final no-buffer CQE that stops HasRecvInFlight.
ObserveTerminalNoBufferCompletion ==
    /\ ringRegistered
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ recvInFlight
    /\ selected = {}
    /\ connState \in {"open", "closing"}
    /\ recvInFlight' = FALSE
    /\ recvCancelIssued' = FALSE
    /\ connState' = IF connState = "open" THEN "closing" ELSE connState
    /\ recvRetryPending' = IF connState = "open" THEN FALSE ELSE recvRetryPending
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvRearmPending,
           recvCancelPending >>

\* TryFinalizeClose removes the live connection even if stale CQEs with F_BUFFER
\* may still exist in completionBacklog/cqOwned and be recycled later.
FinalizeClose ==
    /\ connState = "closing"
    /\ ~recvInFlight
    /\ ~recvCancelPending
    /\ processing = NoBuffer
    /\ connState' = "closed"
    /\ recvRetryPending' = FALSE
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued >>

\* Listener DisposeAsync closes the main ring before it asks ProvidedBufferRing
\* to munmap without unregistering. The close stops new kernel actions, but any
\* still-kernel-owned or completion-owned bid remains live until the separate
\* KernelTeardownComplete environment step models eventual teardown.
CloseRingFd ==
    /\ ~ringFdClosed
    /\ ~ringUnmapped
    /\ processing = NoBuffer
    /\ ringRegistered' = FALSE
    /\ ringFdClosed' = TRUE
    /\ UNCHANGED
        << kernelTeardownDone,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           disposed,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* Environment assumption: after the ring fd is closed, the kernel eventually
\* finishes tearing down any lingering recv state and releases every
\* selected/backlogged/published/lost bid back to process-owned disposal.
KernelTeardownComplete ==
    /\ ringFdClosed
    /\ ~kernelTeardownDone
    /\ ~ringUnmapped
    /\ processing = NoBuffer
    /\ kernelTeardownDone' = TRUE
    /\ selected' = {}
    /\ completionBacklog' = {}
    /\ cqOwned' = {}
    /\ cqOverflowLost' = {}
    /\ disposed' = disposed \cup KernelOrCompletionOwned
    /\ recvInFlight' = FALSE
    /\ recvRearmPending' = FALSE
    /\ recvCancelPending' = FALSE
    /\ recvCancelIssued' = FALSE
    /\ recvRetryPending' = FALSE
    /\ connState' = "closed"
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           ringUnmapped,
           headCursor,
           tailCursor,
           ringOccupancy,
           ringAvailable,
           processing,
           compKind,
           compMore >>

\* ProvidedBufferRing.DisposeAfterRingClosed only unmaps the buffer ring memory;
\* by this point the model requires explicit kernel teardown completion, so
\* every live bid must already be reclaimable by the process.
UnmapBufferRing ==
    /\ ~ringUnmapped
    /\ ringFdClosed
    /\ kernelTeardownDone
    /\ processing = NoBuffer
    /\ ringUnmapped' = TRUE
    /\ headCursor' = tailCursor
    /\ ringOccupancy' = 0
    /\ ringAvailable' = ZeroCounts
    /\ disposed' = BufferUniverse
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           tailCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* Model-only mutation: ring fd is already closed, but userspace unmaps the
\* buffer-ring memory before the kernel teardown step reclaimed every hidden or
\* published bid. This should violate the post-unmap ownership safety checks.
UnmapBeforeTeardownBug ==
    /\ MutateUnmapBeforeTeardown
    /\ ~ringUnmapped
    /\ ringFdClosed
    /\ ~kernelTeardownDone
    /\ processing = NoBuffer
    /\ KernelOrCompletionOwned # {}
    /\ ringUnmapped' = TRUE
    /\ headCursor' = tailCursor
    /\ ringOccupancy' = 0
    /\ ringAvailable' = ZeroCounts
    /\ disposed' = disposed \cup RingAvailableIds
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           tailCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

\* Model-only mutation: unmap the provided-buffer ring before the main ring is
\* closed. This violates the source-mandated ordering even if no bid is live.
UnmapBeforeRingCloseBug ==
    /\ MutateUnmapBeforeRingClose
    /\ ~ringUnmapped
    /\ ~ringFdClosed
    /\ processing = NoBuffer
    /\ ringUnmapped' = TRUE
    /\ headCursor' = tailCursor
    /\ ringOccupancy' = 0
    /\ ringAvailable' = ZeroCounts
    /\ disposed' = disposed \cup RingAvailableIds
    /\ UNCHANGED
        << ringRegistered,
           ringFdClosed,
           kernelTeardownDone,
           tailCursor,
           selected,
           completionBacklog,
           cqOwned,
           cqOverflowLost,
           processing,
           compKind,
           compMore,
           recvInFlight,
           recvRearmPending,
           recvCancelPending,
           recvCancelIssued,
           recvRetryPending,
           connState >>

KernelSelectAny ==
    \E b \in BufferUniverse : KernelSelectAvailableBuffer(b)

KernelCompleteAny ==
    \E b \in BufferUniverse : KernelCompleteSelected(b)

PublishAny ==
    \E b \in BufferUniverse : PublishCompletion(b)

LoseCompletionAny ==
    \E b \in BufferUniverse : LoseCompletionToOverflow(b)

StartProcessingAny ==
    \E b \in BufferUniverse : StartTransportProcessing(b)

FinishProcessing(b) ==
    \/ FinishOpenDataContinue(b)
    \/ FinishOpenDataDeferred(b)
    \/ FinishOpenDataClose(b)
    \/ FinishOpenTerminal(b)
    \/ FinishClosingOrStale(b)

FinishAny ==
    \E b \in BufferUniverse : FinishProcessing(b)

DoubleRecycleAny ==
    \E b \in BufferUniverse : DoubleRecycleBug(b)

LoseRecycleAny ==
    \E b \in BufferUniverse : LoseRecycleBug(b)

ResolveDeferredAny ==
    \/ ResolveDeferredContinue
    \/ ResolveDeferredClose

Next ==
    \/ ArmRecv
    \/ RetryArmRecv
    \/ RetryDropStale
    \/ KernelSelectAny
    \/ KernelCompleteAny
    \/ PublishAny
    \/ LoseCompletionAny
    \/ ObserveEnobufs
    \/ StartProcessingAny
    \/ FinishAny
    \/ DoubleRecycleAny
    \/ LoseRecycleAny
    \/ ResolveDeferredContinue
    \/ ResolveDeferredClose
    \/ ObserveRecvCanceled
    \/ HandleCancelCompletion
    \/ RequestClose
    \/ ObserveTerminalNoBufferCompletion
    \/ FinalizeClose
    \/ CloseRingFd
    \/ KernelTeardownComplete
    \/ UnmapBufferRing
    \/ UnmapBeforeTeardownBug
    \/ UnmapBeforeRingCloseBug

Spec ==
    Init /\ [][Next]_Vars

\* TLC deadlock checking is disabled in the configs because quiescent close /
\* cleanup endpoints are intentional terminal states, and Spec already permits
\* stuttering there. Liveness therefore speaks only about explicit properties.
\* WF on KernelTeardownComplete is an environment assumption about eventual
\* post-close teardown after ringFdClosed; it cannot rescue ordinary pre-close
\* transport bugs because the action is disabled until shutdown already began.
LiveSpec ==
    /\ Spec
    /\ WF_Vars(ResolveDeferredAny)
    /\ WF_Vars(HandleCancelCompletion)
    /\ WF_Vars(KernelTeardownComplete)

TypeOK ==
    /\ ringRegistered \in BOOLEAN
    /\ ringFdClosed \in BOOLEAN
    /\ kernelTeardownDone \in BOOLEAN
    /\ ringUnmapped \in BOOLEAN
    /\ headCursor \in 0..(RingEntries - 1)
    /\ tailCursor \in 0..(RingEntries - 1)
    /\ ringOccupancy \in 0..(RingEntries + 2)
    /\ ringAvailable \in [BufferUniverse -> 0..(RingEntries + 2)]
    /\ selected \subseteq BufferUniverse
    /\ completionBacklog \subseteq BufferUniverse
    /\ cqOwned \subseteq BufferUniverse
    /\ cqOverflowLost \subseteq BufferUniverse
    /\ processing \in BufferUniverse \cup {NoBuffer}
    /\ disposed \subseteq BufferUniverse
    /\ compKind \in [BufferUniverse -> CompletionKinds]
    /\ compMore \in [BufferUniverse -> BOOLEAN]
    /\ recvInFlight \in BOOLEAN
    /\ recvRearmPending \in BOOLEAN
    /\ recvCancelPending \in BOOLEAN
    /\ recvCancelIssued \in BOOLEAN
    /\ recvRetryPending \in BOOLEAN
    /\ connState \in ConnStates

ExclusiveNonRingOwners ==
    PairwiseDisjoint(NonRingOwnershipSets)

AllBufferConservation ==
    \A b \in BufferUniverse : OwnedCount(b) = 1

ValidCompletionSemantics ==
    \A b \in completionBacklog \cup cqOwned \cup cqOverflowLost \cup ProcessingSet :
        compKind[b] = "terminal" => ~compMore[b]

RingAvailabilityAccounting ==
    ringOccupancy = RingAvailableCount

RingCapacityAndTail ==
    /\ ringOccupancy <= RingEntries
    /\ tailCursor = (headCursor + ringOccupancy) % RingEntries

CqCapacity ==
    Cardinality(cqOwned) <= CqEntries

KernelTeardownReclaimsKernelOwners ==
    kernelTeardownDone =>
        /\ ringRegistered = FALSE
        /\ ringFdClosed
        /\ selected = {}
        /\ completionBacklog = {}
        /\ cqOwned = {}
        /\ cqOverflowLost = {}

KernelTeardownStopsRecv ==
    kernelTeardownDone =>
        /\ recvInFlight = FALSE
        /\ ~recvRearmPending
        /\ ~recvCancelPending
        /\ ~recvCancelIssued
        /\ ~recvRetryPending
        /\ connState = "closed"

NoLiveBuffersAfterUnmap ==
    ringUnmapped =>
        /\ ringAvailable = ZeroCounts
        /\ selected = {}
        /\ completionBacklog = {}
        /\ cqOwned = {}
        /\ cqOverflowLost = {}
        /\ processing = NoBuffer
        /\ disposed = BufferUniverse

NoUnmapBeforeRingClosed ==
    ringUnmapped => ringFdClosed

NoUnmapBeforeKernelTeardown ==
    ringUnmapped => kernelTeardownDone

\* Environment liveness only ensures that, once an open connection has no live
\* recv in flight, deferred-flush and cancel bookkeeping eventually resolve into
\* a state where the listener can rearm directly, rearm via retry, or close.
\* No fairness is assumed for ArmRecv or RetryArmRecv themselves.
ReceiveEventuallyRearmable ==
    [](
        (connState = "open"
         /\ ~recvInFlight
         /\ (recvRearmPending \/ recvCancelPending))
            => <>(connState # "open" \/ CanArmRecv \/ recvRetryPending)
    )

====
