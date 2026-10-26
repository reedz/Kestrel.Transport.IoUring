---- MODULE MultiListener ----
EXTENDS Naturals, Integers, Sequences, FiniteSets, TLC

\* Phase 5 multi-listener / forwarder / shutdown model.
\*
\* Traceability to the current implementation:
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringMultiListener.cs:18-184
\*     (IoUringMultiListener constructor chain, CreateWorkerBuild,
\*      CreateWorkerOptions, GetWorkerConnectionShare, CreateMergedChannel,
\*      GetSetupFlags, CreateWorkerSet, and startup forwarders)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringMultiListener.cs:186-522
\*     (ForwardAcceptsAsync, explicit in-transit handoff, shutdown-time
\*      failed-write dispose / continued draining, outer AcceptAsync, cached
\*      UnbindAsync / DisposeAsync lifecycle sharing, worker-unbind fan-out,
\*      and unread merged-channel cleanup)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:1190-1332
\*     (worker AcceptAsync / UnbindAsync / DisposeAsync listener contract,
\*      including close-request shutdown and the 5-second drain timeout)
\*   - tests/Kestrel.Transport.IoUring.Tests/Unit/ConnectionCorrectnessTests.cs:181-231
\*     (CreateWorkerOptions cloning and exact remainder distribution witnesses)
\*   - tests/Kestrel.Transport.IoUring.Tests/Unit/MultiListenerBug7RedTests.cs:12-592
\*     (runtime-only dispose-drain, app-owned handoff, start-all-unbinds,
\*      call-sharing idempotence, cancellation-sharing witnesses, and
\*      all-fault aggregation witnesses)
\*   - specs/tla/Listener.tla:1372-1397, 2602-2648, 3470-3537
\*     (phase-4 worker-listener accept/dispose contract and focused liveness style)
\*
\* Worker abstraction for this phase:
\*   1. Each worker is reduced to a bounded accept channel, admission against
\*      the listener-wide connection limit shared by all workers (H-06; a
\*      connection that finds the channel full stays live until there is room,
\*      H-03), and the phase-4 listener lifecycle boundary: it can publish
\*      accepted connections, its AcceptAsync returns the FIFO head or null after
\*      cancellation/completion, UnbindAsync only stops accepting (application-owned
\*      connections stay live until Kestrel closes them or DisposeAsync aborts the
\*      remainder), and DisposeAsync only runs after UnbindAsync.
\*   2. Internal ring SQ/CQ scheduling, slot generations, recv/send state, and
\*      worker-local close queues stay in Listener.tla. This module only keeps
\*      the ownership effects visible to IoUringMultiListener.
\*
\* Compositional note:
\*   MultiListener.tla assumes the phase-4 Listener.tla worker contract. Any
\*   combined claims across the modules are compositional, not a checked
\*   refinement proof between the worker abstraction and the full listener model.
\*
\* Scope exclusions:
\*   1. Constructor rollback on partial worker creation is excluded after BaseInit.
\*   2. Listen-socket endpoint publication, logging, and socket fallback are excluded.
\*   3. Connection tokens are abstract references, not sockets or ConnectionContext objects.
\*   4. App-side AcceptAsync cancellation is recorded as an observation only because
\*      it does not change ownership.
\*   5. FixDrainOnUnbind = TRUE models the repaired production behavior: once
\*      unbind starts,
\*      the outer AcceptAsync gate closes, CompleteMergedWriter runs before the
\*      model awaits forwarders, workers stop publishing new accepts, blocked or
\*      later merged writes abort / dispose that forwarder-owned in-transit ref,
\*      worker close requests may still flip that ref to aborted underneath the
\*      forwarder, unread merged backlog drains only after the forwarders stop,
\*      and completion no longer relies on the app continuing AcceptAsync after
\*      unbind. The dedicated late-order mutation scenario delays that merged
\*      completion step until after the forwarders and should violate
\*      quiescence.
\*   6. Exact ConnectionContext DisposeAsync-once counting for queued /
\*      in-transit / merged cleanup, UnbindAsync / DisposeAsync call-sharing
\*      idempotence, and the "start every worker unbind before awaiting
\*      completion" concurrency obligation stay outside this ownership model.
\*      They remain runtime-test obligations covered by
\*      MultiListenerBug7RedTests rather than formal claims here.
\*   7. KNOWN GAP: worker unbind is still abstracted as completing only after the
\*      worker's live connections are gone (CompleteWorkerUnbind requires
\*      WorkerLiveCount(w) = 0; UnboundHasNoLiveConnections). The repaired runtime
\*      does not abort app-owned connections in UnbindAsync: they stay live until
\*      Kestrel closes them or the worker's DisposeAsync force-aborts the remainder.
\*      The ownership claims checked here (forwarder / merged-queue / app handoff)
\*      do not depend on that timing, but the unbind-completion and
\*      "unbound => no live connections" claims describe the pre-repair worker.
\*      The unbind-alone contract is checked in Listener.tla
\*      (UnbindAloneKeepsLoopRunning) and by GracefulShutdownTests and
\*      StopUnderLoadTests.

CONSTANTS
    WorkerCount,
    TotalMaxConnections,
    WorkerAcceptCapacity,
    MergedCapacity,
    ConnectionCount,
    Scenario,
    FixDrainOnUnbind

ASSUME
    /\ WorkerCount \in Nat
    /\ WorkerCount > 0
    /\ TotalMaxConnections \in Nat
    /\ TotalMaxConnections > 0
    /\ WorkerCount <= TotalMaxConnections
    /\ WorkerAcceptCapacity \in Nat
    /\ WorkerAcceptCapacity > 0
    /\ MergedCapacity \in Nat
    /\ MergedCapacity > 0
    /\ ConnectionCount \in Nat
    /\ ConnectionCount > 0
    /\ Scenario \in {"safety", "forward-progress", "shutdown-drain",
                     "shutdown-late-merged-complete"}
    /\ FixDrainOnUnbind \in BOOLEAN

VARIABLES
    workerState,
    workerQueueCompleted,
    workerLive,
    workerQueue,
    inTransit,
    mergedQueue,
    mergedCompleted,
    ctsCancelled,
    forwarderState,
    connState,
    connWorker,
    forwardHistory,
    appOwned,
    closedRefs,
    droppedRefs,
    listenerPhase,
    disposeRequested,
    appCancelObserved

Workers == 0..(WorkerCount - 1)
Connections == 0..(ConnectionCount - 1)
NoWorker == WorkerCount
NoConn == ConnectionCount

ConnStates == {"fresh", "live", "closed", "aborted"}
WorkerStates == {"running", "unbinding", "unbound", "disposed"}
ForwarderStates == {"running", "done"}
ListenerStates == {"running", "unbinding", "unbound", "disposed"}

WorkerCap(w) ==
    TotalMaxConnections \div WorkerCount +
    (IF w < TotalMaxConnections % WorkerCount THEN 1 ELSE 0)

RECURSIVE SumSeq(_)
SumSeq(s) == IF Len(s) = 0 THEN 0 ELSE s[1] + SumSeq(Tail(s))

WorkerCapSeq == [i \in 1..WorkerCount |-> WorkerCap(i - 1)]
TotalWorkerCapacity == SumSeq(WorkerCapSeq)

BoolToNat(p) == IF p THEN 1 ELSE 0

UniqueSeq(s) ==
    \A i, j \in 1..Len(s) : i /= j => s[i] /= s[j]

SeqToSet(s) == { s[i] : i \in 1..Len(s) }

TailOrEmpty(s) == IF Len(s) = 0 THEN << >> ELSE Tail(s)

MergedRefs == SeqToSet(mergedQueue)
QueuedRefs == { c \in Connections : \E w \in Workers : c \in SeqToSet(workerQueue[w]) }
TransitRefs == { c \in Connections : \E w \in Workers : inTransit[w] = c }

LiveOwnersOf(c) == { w \in Workers : c \in workerLive[w] }
QueueOwnersOf(c) == { w \in Workers : c \in SeqToSet(workerQueue[w]) }
TransitOwnersOf(c) == { w \in Workers : inTransit[w] = c }

ConnInWorkerLive(c) == LiveOwnersOf(c) # {}
ConnInWorkerQueue(c) == QueueOwnersOf(c) # {}
ConnInTransit(c) == TransitOwnersOf(c) # {}
ConnInMerged(c) == c \in MergedRefs
ConnInApp(c) == c \in appOwned
ConnInClosed(c) == c \in closedRefs
\* droppedRefs records silent-discard history: references that left modeled
\* ownership without a close / merged / app handoff. It witnesses conservation
\* bugs without over-claiming a literal fd leak in the legacy pre-fix
\* behavior.
ConnInDropped(c) == c \in droppedRefs

ConnReferenced(c) ==
    \/ ConnInWorkerLive(c)
    \/ ConnInWorkerQueue(c)
    \/ ConnInTransit(c)
    \/ ConnInMerged(c)
    \/ ConnInApp(c)
    \/ ConnInClosed(c)
    \/ ConnInDropped(c)

ExplicitMembershipCount(c) ==
    Cardinality(LiveOwnersOf(c))
    + Cardinality(QueueOwnersOf(c))
    + Cardinality(TransitOwnersOf(c))
    + BoolToNat(ConnInMerged(c))
    + BoolToNat(ConnInApp(c))
    + BoolToNat(ConnInClosed(c))
    + BoolToNat(ConnInDropped(c))

WorkerLiveCount(w) ==
    Cardinality({ c \in Connections : connWorker[c] = w /\ connState[c] = "live" })

TotalLiveCount ==
    Cardinality({ c \in Connections : connState[c] = "live" })

AllForwardersDone ==
    \A w \in Workers : forwarderState[w] = "done"

AllWorkersUnbound ==
    \A w \in Workers : workerState[w] \in {"unbound", "disposed"}

ListenerOwnedLocationsEmpty ==
    /\ \A w \in Workers : workerLive[w] = {}
    /\ \A w \in Workers : workerQueue[w] = << >>
    /\ \A w \in Workers : inTransit[w] = NoConn
    /\ mergedQueue = << >>

NoLiveConnections ==
    \A c \in Connections : connState[c] # "live"

AllConnectionLocationsQuiesced ==
    /\ ListenerOwnedLocationsEmpty
    /\ \A c \in appOwned : connState[c] # "live"

LateMergedCompletionMutation ==
    Scenario = "shutdown-late-merged-complete"

EarlyMergedCompletionOnUnbind ==
    FixDrainOnUnbind /\ ~LateMergedCompletionMutation

AppAcceptsClosed ==
    disposeRequested /\ FixDrainOnUnbind

InTransitWitness ==
    \E w \in Workers : inTransit[w] # NoConn

MergedWitness == MergedRefs # {}

ShutdownBlockedTransitWitness ==
    /\ Len(mergedQueue) = MergedCapacity
    /\ \E cm \in MergedRefs, cq \in QueuedRefs, ct \in TransitRefs :
        /\ cm # cq
        /\ cm # ct
        /\ cq # ct
        /\ connState[cm] = "live"
        /\ connState[cq] = "live"
        /\ connState[ct] = "live"

ShutdownDisposeWitness ==
    /\ disposeRequested
    /\ listenerPhase = "unbinding"
    /\ ShutdownBlockedTransitWitness

WitnessConn == 0
WitnessConnInTransit == \E w \in Workers : inTransit[w] = WitnessConn
WitnessConnAtApp == WitnessConn \in appOwned

LocationVars ==
    << workerLive, workerQueue, inTransit, mergedQueue, appOwned, closedRefs,
       droppedRefs >>

ControlVars ==
    << workerState, workerQueueCompleted, mergedCompleted, ctsCancelled,
       forwarderState, listenerPhase, disposeRequested, appCancelObserved >>

ConnVars ==
    << connState, connWorker, forwardHistory >>

Vars ==
    << LocationVars, ControlVars, ConnVars >>

BaseInit ==
    /\ workerState = [w \in Workers |-> "running"]
    /\ workerQueueCompleted = [w \in Workers |-> FALSE]
    /\ workerLive = [w \in Workers |-> {}]
    /\ workerQueue = [w \in Workers |-> << >>]
    /\ inTransit = [w \in Workers |-> NoConn]
    /\ mergedQueue = << >>
    /\ mergedCompleted = FALSE
    /\ ctsCancelled = FALSE
    /\ forwarderState = [w \in Workers |-> "running"]
    /\ connState = [c \in Connections |-> "fresh"]
    /\ connWorker = [c \in Connections |-> NoWorker]
    /\ forwardHistory = << >>
    /\ appOwned = {}
    /\ closedRefs = {}
    /\ droppedRefs = {}
    /\ listenerPhase = "running"
    /\ disposeRequested = FALSE
    /\ appCancelObserved = FALSE

\* Core ownership moves used by both safety and focused liveness actions.
CreateCore(w, c) ==
    /\ workerLive' = [workerLive EXCEPT ![w] = @ \cup {c}]
    /\ connState' = [connState EXCEPT ![c] = "live"]
    /\ connWorker' = [connWorker EXCEPT ![c] = w]
    /\ UNCHANGED << workerState, workerQueueCompleted, workerQueue, inTransit,
                    mergedQueue, mergedCompleted, ctsCancelled, forwarderState,
                    forwardHistory, appOwned, closedRefs, droppedRefs,
                    listenerPhase, disposeRequested, appCancelObserved >>

QueueCore(w, c) ==
    /\ workerLive' = [workerLive EXCEPT ![w] = @ \ {c}]
    /\ workerQueue' = [workerQueue EXCEPT ![w] = Append(@, c)]
    /\ UNCHANGED << workerState, workerQueueCompleted, inTransit, mergedQueue,
                    mergedCompleted, ctsCancelled, forwarderState, connState,
                    connWorker, forwardHistory, appOwned, closedRefs,
                    droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

TakeTransitCore(w, c) ==
    /\ workerQueue' = [workerQueue EXCEPT ![w] = TailOrEmpty(@)]
    /\ inTransit' = [inTransit EXCEPT ![w] = c]
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, mergedQueue,
                    mergedCompleted, ctsCancelled, forwarderState, connState,
                    connWorker, forwardHistory, appOwned, closedRefs,
                    droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

EnqueueMergedCore(w, c) ==
    /\ inTransit' = [inTransit EXCEPT ![w] = NoConn]
    /\ mergedQueue' = Append(mergedQueue, c)
    /\ forwardHistory' = Append(forwardHistory, c)
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    mergedCompleted, ctsCancelled, forwarderState, connState,
                    connWorker, appOwned, closedRefs, droppedRefs,
                    listenerPhase, disposeRequested, appCancelObserved >>

AppAcceptMergedCore(c) ==
    /\ mergedQueue' = TailOrEmpty(mergedQueue)
    /\ appOwned' = appOwned \cup {c}
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedCompleted, ctsCancelled, forwarderState,
                    connState, connWorker, forwardHistory, closedRefs,
                    droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

AppCloseCore(c) ==
    /\ connState' = [connState EXCEPT ![c] = "closed"]
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, mergedCompleted, ctsCancelled,
                    forwarderState, connWorker, forwardHistory, appOwned,
                    closedRefs, droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

AppReleaseCore(c) ==
    /\ appOwned' = appOwned \ {c}
    /\ closedRefs' = closedRefs \cup {c}
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, mergedCompleted, ctsCancelled,
                    forwarderState, connState, connWorker, forwardHistory,
                    droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

RequestDisposeCore ==
    /\ listenerPhase' = "unbinding"
    /\ disposeRequested' = TRUE
    /\ ctsCancelled' = IF FixDrainOnUnbind THEN FALSE ELSE TRUE
    /\ mergedCompleted' =
        IF FixDrainOnUnbind
            THEN EarlyMergedCompletionOnUnbind
            ELSE TRUE
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, forwarderState, connState,
                    connWorker, forwardHistory, appOwned, closedRefs,
                    droppedRefs, appCancelObserved >>

BeginWorkerUnbindCore(w) ==
    /\ workerState' = [workerState EXCEPT ![w] = "unbinding"]
    /\ workerQueueCompleted' = [workerQueueCompleted EXCEPT ![w] = TRUE]
    /\ UNCHANGED << workerLive, workerQueue, inTransit, mergedQueue,
                    mergedCompleted, ctsCancelled, forwarderState, connState,
                    connWorker, forwardHistory, appOwned, closedRefs,
                    droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

CompleteWorkerUnbindCore(w) ==
    /\ workerState' = [workerState EXCEPT ![w] = "unbound"]
    /\ UNCHANGED << workerQueueCompleted, workerLive, workerQueue, inTransit,
                    mergedQueue, mergedCompleted, ctsCancelled, forwarderState,
                    connState, connWorker, forwardHistory, appOwned,
                    closedRefs, droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

ForwarderCompleteNaturalCore(w) ==
    /\ forwarderState' = [forwarderState EXCEPT ![w] = "done"]
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, mergedCompleted, ctsCancelled,
                    connState, connWorker, forwardHistory, appOwned,
                    closedRefs, droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

ForwarderObserveCancelCore(w) ==
    /\ forwarderState' = [forwarderState EXCEPT ![w] = "done"]
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, mergedCompleted, ctsCancelled,
                    connState, connWorker, forwardHistory, appOwned,
                    closedRefs, droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

ForwardCancelTransitDropCore(w, c) ==
    /\ inTransit' = [inTransit EXCEPT ![w] = NoConn]
    /\ forwarderState' = [forwarderState EXCEPT ![w] = "done"]
    /\ droppedRefs' = droppedRefs \cup {c}
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    mergedQueue, mergedCompleted, ctsCancelled, connState,
                    connWorker, forwardHistory, appOwned, closedRefs,
                    listenerPhase, disposeRequested, appCancelObserved >>

ForwardCancelTransitDisposeCore(w, c) ==
    /\ inTransit' = [inTransit EXCEPT ![w] = NoConn]
    /\ connState' = [connState EXCEPT ![c] = "aborted"]
    /\ closedRefs' = closedRefs \cup {c}
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    mergedQueue, mergedCompleted, ctsCancelled, forwarderState,
                    connWorker, forwardHistory, appOwned, droppedRefs,
                    listenerPhase, disposeRequested, appCancelObserved >>

WorkerAbortWorkerLiveCore(w, c) ==
    /\ workerLive' = [workerLive EXCEPT ![w] = @ \ {c}]
    /\ connState' = [connState EXCEPT ![c] = "aborted"]
    /\ closedRefs' = closedRefs \cup {c}
    /\ UNCHANGED << workerState, workerQueueCompleted, workerQueue, inTransit,
                    mergedQueue, mergedCompleted, ctsCancelled, forwarderState,
                    connWorker, forwardHistory, appOwned, droppedRefs,
                    listenerPhase, disposeRequested, appCancelObserved >>

WorkerAbortExposedCore(c) ==
    /\ connState' = [connState EXCEPT ![c] = "aborted"]
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, mergedCompleted, ctsCancelled,
                    forwarderState, connWorker, forwardHistory, appOwned,
                    closedRefs, droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

WorkerAbortQueuedHeadCore(w, c) ==
    /\ workerQueue' = [workerQueue EXCEPT ![w] = TailOrEmpty(@)]
    /\ connState' = [connState EXCEPT ![c] = "aborted"]
    /\ closedRefs' = closedRefs \cup {c}
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, inTransit,
                    mergedQueue, mergedCompleted, ctsCancelled, forwarderState,
                    connWorker, forwardHistory, appOwned, droppedRefs,
                    listenerPhase, disposeRequested, appCancelObserved >>

WorkerAbortMergedDrainCore(c) ==
    /\ mergedQueue' = TailOrEmpty(mergedQueue)
    /\ connState' = [connState EXCEPT ![c] = "aborted"]
    /\ closedRefs' = closedRefs \cup {c}
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedCompleted, ctsCancelled, forwarderState,
                    connWorker, forwardHistory, appOwned, droppedRefs,
                    listenerPhase, disposeRequested, appCancelObserved >>

CompleteMergedCore ==
    /\ mergedCompleted' = TRUE
    /\ ctsCancelled' = TRUE
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, forwarderState, connState,
                    connWorker, forwardHistory, appOwned, closedRefs,
                    droppedRefs, listenerPhase, disposeRequested,
                    appCancelObserved >>

CompleteUnbindCore ==
    /\ listenerPhase' = "unbound"
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, mergedCompleted, ctsCancelled,
                    forwarderState, connState, connWorker, forwardHistory,
                    appOwned, closedRefs, droppedRefs, disposeRequested,
                    appCancelObserved >>

DisposeCore ==
    /\ workerState' = [w \in Workers |-> "disposed"]
    /\ listenerPhase' = "disposed"
    /\ UNCHANGED << workerQueueCompleted, workerLive, workerQueue, inTransit,
                    mergedQueue, mergedCompleted, ctsCancelled, forwarderState,
                    connState, connWorker, forwardHistory, appOwned,
                    closedRefs, droppedRefs, disposeRequested,
                    appCancelObserved >>

\* Running-phase worker behavior abstracts the worker listener's accept channel:
\* a fresh connection becomes live and is queued when the channel has room. The
\* connection limit is one admission shared by every worker (H-06), so a worker
\* may hold any number of live connections as long as the total stays within it.
WorkerCreateAny(w, c) ==
    /\ Scenario \in {"safety", "forward-progress", "shutdown-drain",
                     "shutdown-late-merged-complete"}
    /\ listenerPhase = "running"
    /\ workerState[w] = "running"
    /\ ~workerQueueCompleted[w]
    /\ connState[c] = "fresh"
    /\ TotalLiveCount < TotalMaxConnections
    /\ CreateCore(w, c)

WorkerPublishAny(w, c) ==
    /\ Scenario \in {"safety", "forward-progress", "shutdown-drain",
                     "shutdown-late-merged-complete"}
    /\ listenerPhase = "running"
    /\ workerState[w] = "running"
    /\ ~workerQueueCompleted[w]
    /\ c \in workerLive[w]
    /\ Len(workerQueue[w]) < WorkerAcceptCapacity
    /\ QueueCore(w, c)

\* A live connection that finds its worker's accept channel full is not aborted
\* (H-03): it stays live and unpublished (the worker parks it and pauses
\* accepting) until WorkerPublishAny finds room.

\* ForwardTake / ForwardEnqueue refine ForwardAcceptsAsync and its explicit
\* local variable handoff before WriteAsync into _mergedChannel.
ForwardTake(w) ==
    /\ forwarderState[w] = "running"
    /\ ~ctsCancelled
    /\ inTransit[w] = NoConn
    /\ workerQueue[w] # << >>
    /\ LET c == workerQueue[w][1] IN TakeTransitCore(w, c)

\* While Len(mergedQueue) < MergedCapacity the merged channel behaves like an
\* immediate write source; otherwise the connection stays in inTransit and the
\* forwarder is blocked exactly as BoundedChannelFullMode.Wait would be. The
\* forwarder still owns that reference even if the worker aborts the transport
\* beneath it during shutdown.
ForwardEnqueue(w) ==
    /\ forwarderState[w] = "running"
    /\ inTransit[w] # NoConn
    /\ connState[inTransit[w]] \in {"live", "aborted"}
    /\ ~mergedCompleted
    /\ Len(mergedQueue) < MergedCapacity
    /\ EnqueueMergedCore(w, inTransit[w])

ForwarderCompleteNatural(w) ==
    /\ forwarderState[w] = "running"
    /\ ~ctsCancelled
    /\ workerQueueCompleted[w]
    /\ workerQueue[w] = << >>
    /\ inTransit[w] = NoConn
    /\ workerState[w] # "running"
    /\ ForwarderCompleteNaturalCore(w)

ForwarderObserveCancel(w) ==
    /\ ~FixDrainOnUnbind
    /\ forwarderState[w] = "running"
    /\ ctsCancelled
    /\ inTransit[w] = NoConn
    /\ ForwarderObserveCancelCore(w)

\* A shutdown-failed merged-channel write can strand a reference in the
\* forwarder's local inTransit slot under either flag. The legacy pre-fix
\* behavior still drops that owned ref on the floor; the repaired production
\* behavior instead aborts / disposes it explicitly, then leaves the forwarder
\* running so later queued accepts drain under the same closed-writer guard.
\* That handoff only happens once shutdown has actually completed the merged
\* writer (or the legacy cancellation path has fired); channel fullness alone
\* keeps the write blocked.
ForwardCancelTransit(w) ==
    /\ forwarderState[w] = "running"
    /\ listenerPhase = "unbinding"
    /\ inTransit[w] # NoConn
    /\ connState[inTransit[w]] \in {"live", "aborted"}
    /\ \/ mergedCompleted
       \/ ctsCancelled
    /\ LET c == inTransit[w] IN
        IF FixDrainOnUnbind
            THEN ForwardCancelTransitDisposeCore(w, c)
            ELSE ForwardCancelTransitDropCore(w, c)

\* Outer application reads from the merged channel, mirroring
\* IoUringMultiListener.AcceptAsync. Under FixDrainOnUnbind the outer accept
\* gate closes as soon as unbind starts, so post-unbind quiescence cannot rely
\* on the app draining buffered merged entries.
AppAcceptMerged ==
    /\ listenerPhase # "disposed"
    /\ ~AppAcceptsClosed
    /\ mergedQueue # << >>
    /\ LET c == mergedQueue[1] IN AppAcceptMergedCore(c)

AppCloseOwned(c) ==
    /\ c \in appOwned
    /\ connState[c] = "live"
    /\ AppCloseCore(c)

AppReleaseTerminated(c) ==
    /\ c \in appOwned
    /\ connState[c] \in {"closed", "aborted"}
    /\ AppReleaseCore(c)

AppCancelAccept ==
    /\ listenerPhase # "disposed"
    /\ ~appCancelObserved
    /\ appCancelObserved' = TRUE
    /\ UNCHANGED << workerState, workerQueueCompleted, workerLive, workerQueue,
                    inTransit, mergedQueue, mergedCompleted, ctsCancelled,
                    forwarderState, connState, connWorker, forwardHistory,
                    appOwned, closedRefs, droppedRefs, listenerPhase,
                    disposeRequested >>

\* Shutdown actions refine IoUringMultiListener.UnbindAsync / DisposeAsync and
\* the worker listener's UnbindAsync / DisposeAsync boundaries.
RequestDispose ==
    /\ listenerPhase = "running"
    /\ IF Scenario \in {"shutdown-drain", "shutdown-late-merged-complete"}
          THEN ShutdownBlockedTransitWitness
          ELSE TRUE
    /\ RequestDisposeCore

BeginWorkerUnbind(w) ==
    /\ listenerPhase = "unbinding"
    /\ workerState[w] = "running"
    /\ BeginWorkerUnbindCore(w)

CompleteWorkerUnbind(w) ==
    /\ workerState[w] = "unbinding"
    /\ WorkerLiveCount(w) = 0
    /\ CompleteWorkerUnbindCore(w)

WorkerAbortWorkerLive(w, c) ==
    /\ workerState[w] = "unbinding"
    /\ c \in workerLive[w]
    /\ connState[c] = "live"
    /\ WorkerAbortWorkerLiveCore(w, c)

\* A worker close request can target a connection whose ConnectionContext is
\* already sitting in the forwarder's local inTransit slot. The transport becomes
\* aborted immediately, but the forwarder still owns the reference until it
\* forwards or drains it.
WorkerAbortTransit(w) ==
    /\ workerState[w] = "unbinding"
    /\ inTransit[w] # NoConn
    /\ connState[inTransit[w]] = "live"
    /\ LET c == inTransit[w] IN WorkerAbortExposedCore(c)

\* The worker's close-request queue can target arbitrary queued accepts. The
\* legacy pre-fix behavior consumes that accept-channel head immediately. The
\* repaired fake-worker witness instead aborts the queued connection first,
\* then leaves queue ownership intact so the forwarder can publish or drain the
\* same reference.
\* The runtime implementation also disposes that drained ConnectionContext
\* exactly once, but that exact count stays a runtime obligation rather than a
\* modeled ownership claim here. Under both flags, the shutdown queue head may
\* already be transport-aborted; only the ownership effect differs.
WorkerAbortQueuedHead(w) ==
    /\ workerState[w] = "unbinding"
    /\ workerQueue[w] # << >>
    /\ LET c == workerQueue[w][1] IN
        /\ connWorker[c] = w
        /\ connState[c] \in {"live", "aborted"}
        /\ IF FixDrainOnUnbind
              THEN WorkerAbortExposedCore(c)
              ELSE WorkerAbortQueuedHeadCore(w, c)

\* A worker close request can still abort the transport beneath a buffered
\* merged ref while leaving the app-visible merged entry intact. The later
\* owner-side drain is modeled separately because the runtime only runs
\* DrainMergedConnectionsAsync after the forwarders stop.
WorkerAbortMergedHead(w) ==
    /\ workerState[w] = "unbinding"
    /\ mergedQueue # << >>
    /\ LET c == mergedQueue[1] IN
        /\ connWorker[c] = w
        /\ connState[c] = "live"
        /\ WorkerAbortExposedCore(c)

\* DrainMergedHead refines IoUringMultiListener.DrainMergedConnectionsAsync.
\* In the repaired production behavior it only runs after CompleteMergedWriter
\* has already prevented any further ForwardEnqueue and after every forwarder
\* has exited, so buffered merged refs cannot be refilled behind the drain.
\* Those refs may already be transport-aborted by WorkerAbortMergedHead.
DrainMergedHead(w) ==
    /\ listenerPhase = "unbinding"
    /\ FixDrainOnUnbind
    /\ mergedCompleted
    /\ AllForwardersDone
    /\ workerState[w] \in {"unbinding", "unbound"}
    /\ mergedQueue # << >>
    /\ LET c == mergedQueue[1] IN
        /\ connWorker[c] = w
        /\ connState[c] \in {"live", "aborted"}
        /\ WorkerAbortMergedDrainCore(c)

WorkerAbortApp(w, c) ==
    /\ workerState[w] = "unbinding"
    /\ c \in appOwned
    /\ connWorker[c] = w
    /\ connState[c] = "live"
    /\ WorkerAbortExposedCore(c)

WorkerAbortDropped(w, c) ==
    /\ workerState[w] = "unbinding"
    /\ c \in droppedRefs
    /\ connWorker[c] = w
    /\ connState[c] = "live"
    /\ WorkerAbortExposedCore(c)

\* The repaired runtime completes the merged writer inside UnbindAsync before it
\* awaits forwarders. This late action remains only for the focused mutation
\* that delays that completion until after the forwarders have drained worker
\* accepts; buffered merged entries may still remain for later DrainMergedHead.
CompleteMerged ==
    /\ listenerPhase = "unbinding"
    /\ FixDrainOnUnbind
    /\ LateMergedCompletionMutation
    /\ ~mergedCompleted
    /\ AllForwardersDone
    /\ \A w \in Workers : workerQueue[w] = << >>
    /\ \A w \in Workers : inTransit[w] = NoConn
    /\ CompleteMergedCore

CompleteUnbind ==
    /\ listenerPhase = "unbinding"
    /\ AllWorkersUnbound
    /\ AllForwardersDone
    /\ IF FixDrainOnUnbind
          THEN /\ mergedCompleted
               /\ ListenerOwnedLocationsEmpty
               /\ droppedRefs = {}
          ELSE TRUE
    /\ CompleteUnbindCore

DisposeNow ==
    /\ listenerPhase = "unbound"
    /\ disposeRequested
    /\ DisposeCore

AnyCreate ==
    \E w \in Workers, c \in Connections : WorkerCreateAny(w, c)

AnyQueue ==
    \E w \in Workers, c \in Connections : WorkerPublishAny(w, c)

AnyForwardTake ==
    \E w \in Workers : ForwardTake(w)

AnyForwardEnqueue ==
    \E w \in Workers : ForwardEnqueue(w)

AnyForwarderCompleteNatural ==
    \E w \in Workers : ForwarderCompleteNatural(w)

AnyForwarderObserveCancel ==
    \E w \in Workers : ForwarderObserveCancel(w)

AnyForwardCancelTransit ==
    \E w \in Workers : ForwardCancelTransit(w)

AnyBeginWorkerUnbind ==
    \E w \in Workers : BeginWorkerUnbind(w)

AnyCompleteWorkerUnbind ==
    \E w \in Workers : CompleteWorkerUnbind(w)

AnyAbortWorkerLive ==
    \E w \in Workers, c \in Connections : WorkerAbortWorkerLive(w, c)

AnyAbortTransit ==
    \E w \in Workers : WorkerAbortTransit(w)

AnyAbortQueuedHead ==
    \E w \in Workers : WorkerAbortQueuedHead(w)

AnyAbortMergedHead ==
    \E w \in Workers : WorkerAbortMergedHead(w)

AnyDrainMergedHead ==
    \E w \in Workers : DrainMergedHead(w)

AnyAbortApp ==
    \E w \in Workers, c \in Connections : WorkerAbortApp(w, c)

AnyAbortDropped ==
    \E w \in Workers, c \in Connections : WorkerAbortDropped(w, c)

AnyWorkerAbort ==
    \/ AnyAbortWorkerLive
    \/ AnyAbortTransit
    \/ AnyAbortQueuedHead
    \/ AnyAbortMergedHead
    \/ AnyAbortApp
    \/ AnyAbortDropped

AnyAppClose ==
    \E c \in Connections : AppCloseOwned(c)

AnyAppRelease ==
    \E c \in Connections : AppReleaseTerminated(c)

SafetyBodyStep ==
    \/ AnyCreate
    \/ AnyQueue
    \/ AnyForwardTake
    \/ AnyForwardEnqueue
    \/ AnyForwarderCompleteNatural
    \/ AnyForwarderObserveCancel
    \/ AnyForwardCancelTransit
    \/ AppAcceptMerged
    \/ AnyAppClose
    \/ AnyAppRelease
    \/ AppCancelAccept
    \/ RequestDispose
    \/ AnyBeginWorkerUnbind
    \/ AnyWorkerAbort
    \/ AnyDrainMergedHead
    \/ AnyCompleteWorkerUnbind
    \/ CompleteMerged
    \/ CompleteUnbind
    \/ DisposeNow

ProgressBodyStep ==
    \/ AnyCreate
    \/ AnyQueue
    \/ AnyForwardTake
    \/ AnyForwardEnqueue
    \/ AppAcceptMerged

ShutdownBuildStep ==
    /\ ~ShutdownBlockedTransitWitness
    /\ \/ AnyCreate
       \/ AnyQueue
       \/ AnyForwardTake
       \/ AnyForwardEnqueue

ShutdownBodyStep ==
    \/ AnyBeginWorkerUnbind
    \/ AnyForwardTake
    \/ AnyForwardEnqueue
    \/ AnyForwarderCompleteNatural
    \/ AnyForwarderObserveCancel
    \/ AnyForwardCancelTransit
    \/ AnyWorkerAbort
    \/ AnyDrainMergedHead
    \/ AnyCompleteWorkerUnbind
    \/ CompleteMerged
    \/ AppAcceptMerged
    \/ AnyAppRelease
    \/ CompleteUnbind
    \/ DisposeNow

SafetyNext ==
    /\ Scenario = "safety"
    /\ SafetyBodyStep

ProgressNext ==
    /\ Scenario = "forward-progress"
    /\ ProgressBodyStep

ShutdownNext ==
    /\ Scenario \in {"shutdown-drain", "shutdown-late-merged-complete"}
    /\ \/ /\ listenerPhase = "running"
          /\ (ShutdownBuildStep \/ RequestDispose)
       \/ /\ listenerPhase # "running"
          /\ ShutdownBodyStep

Next ==
    \/ SafetyNext
    \/ ProgressNext
    \/ ShutdownNext

Spec ==
    /\ BaseInit
    /\ [][Next]_Vars

SafetySpec ==
    /\ BaseInit
    /\ [][SafetyNext]_Vars

ProgressSpec ==
    /\ BaseInit
    /\ [][ProgressNext]_Vars

ShutdownSpec ==
    /\ BaseInit
    /\ [][ShutdownNext]_Vars

ProgressLiveSpec ==
    /\ ProgressSpec
    /\ WF_Vars(AnyCreate)
    /\ WF_Vars(AnyQueue)
    /\ \A w \in Workers : WF_Vars(ForwardTake(w))
    /\ \A w \in Workers : WF_Vars(ForwardEnqueue(w))
    /\ WF_Vars(AppAcceptMerged)

\* Focused shutdown fairness stays on the environment / forwarder scheduling
\* needed to build the blocked-transit witness and to request shutdown. The
\* owner-side merged drain / dispose actions remain ordinary reachable steps
\* checked by safety invariants; this witness does not justify extra fairness
\* on those synthetic cleanup moves.
ShutdownLiveSpec ==
    /\ ShutdownSpec
    /\ WF_Vars(AnyCreate)
    /\ WF_Vars(AnyQueue)
    /\ WF_Vars(RequestDispose)
    /\ \A w \in Workers : WF_Vars(BeginWorkerUnbind(w))
    /\ \A w \in Workers : WF_Vars(ForwardTake(w))
    /\ \A w \in Workers : WF_Vars(ForwardEnqueue(w))
    /\ \A w \in Workers : WF_Vars(ForwarderCompleteNatural(w))
    /\ \A w \in Workers : WF_Vars(ForwarderObserveCancel(w))
    /\ \A w \in Workers : WF_Vars(ForwardCancelTransit(w))

\* Repaired-production-only quiescence proof over the same shutdown witness.
\* Beyond the shared environment / forwarder fairness above, assume the
\* owner-side merged drain, app release, and lifecycle-completion actions that
\* stay continuously enabled are eventually scheduled. This is stronger than
\* the fairness-free safety twins and is only used to show that the repaired
\* production behavior can reach full quiescence without relying on
\* post-unbind app reads.
ShutdownQuiescenceLiveSpec ==
    /\ ShutdownSpec
    /\ FixDrainOnUnbind
    /\ WF_Vars(AnyCreate)
    /\ WF_Vars(AnyQueue)
    /\ WF_Vars(RequestDispose)
    /\ \A w \in Workers : WF_Vars(BeginWorkerUnbind(w))
    /\ \A w \in Workers : WF_Vars(ForwardTake(w))
    /\ \A w \in Workers : WF_Vars(ForwardEnqueue(w))
    /\ \A w \in Workers : WF_Vars(ForwarderCompleteNatural(w))
    /\ \A w \in Workers : WF_Vars(ForwardCancelTransit(w))
    /\ WF_Vars(AnyAbortWorkerLive)
    /\ \A w \in Workers : WF_Vars(WorkerAbortTransit(w))
    /\ \A w \in Workers : WF_Vars(WorkerAbortQueuedHead(w))
    /\ \A w \in Workers : WF_Vars(DrainMergedHead(w))
    /\ WF_Vars(AnyAbortApp)
    /\ WF_Vars(AnyAppRelease)
    /\ \A w \in Workers : WF_Vars(CompleteWorkerUnbind(w))
    /\ WF_Vars(CompleteMerged)
    /\ WF_Vars(CompleteUnbind)
    /\ WF_Vars(DisposeNow)

TypeOK ==
    /\ workerState \in [Workers -> WorkerStates]
    /\ workerQueueCompleted \in [Workers -> BOOLEAN]
    /\ workerLive \in [Workers -> SUBSET Connections]
    /\ workerQueue \in [Workers -> Seq(Connections)]
    /\ \A w \in Workers : UniqueSeq(workerQueue[w])
    /\ inTransit \in [Workers -> Connections \cup {NoConn}]
    /\ mergedQueue \in Seq(Connections)
    /\ UniqueSeq(mergedQueue)
    /\ mergedCompleted \in BOOLEAN
    /\ ctsCancelled \in BOOLEAN
    /\ forwarderState \in [Workers -> ForwarderStates]
    /\ connState \in [Connections -> ConnStates]
    /\ connWorker \in [Connections -> Workers \cup {NoWorker}]
    /\ forwardHistory \in Seq(Connections)
    /\ appOwned \subseteq Connections
    /\ closedRefs \subseteq Connections
    /\ droppedRefs \subseteq Connections
    /\ listenerPhase \in ListenerStates
    /\ disposeRequested \in BOOLEAN
    /\ appCancelObserved \in BOOLEAN

OwnershipPartition ==
    /\ \A c \in Connections :
        IF connState[c] = "fresh"
            THEN /\ ExplicitMembershipCount(c) = 0
                 /\ connWorker[c] = NoWorker
            ELSE /\ ExplicitMembershipCount(c) = 1
                 /\ connWorker[c] \in Workers
    /\ \A w \in Workers :
        /\ \A c \in workerLive[w] : connWorker[c] = w
        /\ \A c \in SeqToSet(workerQueue[w]) : connWorker[c] = w
        /\ inTransit[w] = NoConn \/ connWorker[inTransit[w]] = w

BoundedChannels ==
    /\ \A w \in Workers : Len(workerQueue[w]) <= WorkerAcceptCapacity
    /\ Len(mergedQueue) <= MergedCapacity

\* H-06: one admission for all workers; any worker may hold the whole limit.
SharedAdmissionBound ==
    TotalLiveCount <= TotalMaxConnections

\* WorkerCap is now each worker's buffer-sizing share (GetWorkerConnectionShare),
\* not a per-worker connection limit; the shares still split the total exactly.
TotalCapacityExact ==
    TotalWorkerCapacity = TotalMaxConnections

RemainderAllocation ==
    /\ \A w \in Workers :
        WorkerCap(w) \in
            { TotalMaxConnections \div WorkerCount,
              TotalMaxConnections \div WorkerCount + 1 }
    /\ \A i, j \in Workers : i < j => WorkerCap(i) >= WorkerCap(j)

MergedCapacityWithinSourceBound ==
    MergedCapacity <= WorkerCount * WorkerAcceptCapacity

UniqueForwardHistory ==
    /\ UniqueSeq(forwardHistory)
    /\ appOwned \subseteq SeqToSet(forwardHistory)
    /\ \A w \in Workers :
        inTransit[w] = NoConn \/ inTransit[w] \notin SeqToSet(forwardHistory)

ClosedRefsTerminal ==
    \A c \in closedRefs : connState[c] \in {"closed", "aborted"}

DroppedRefsSeparate ==
    droppedRefs \cap closedRefs = {}

DroppedRefsNeverMerged ==
    droppedRefs \cap SeqToSet(forwardHistory) = {}

UnboundHasNoLiveConnections ==
    listenerPhase \in {"unbound", "disposed"} => NoLiveConnections

DisposedQuiescesListenerLocations ==
    listenerPhase = "disposed" => AllConnectionLocationsQuiesced

DisposedHasNoDroppedRefs ==
    listenerPhase = "disposed" => droppedRefs = {}

EventuallyInTransitWitness == <> InTransitWitness
EventuallyMergedWitness == <> MergedWitness
EventuallyShutdownDisposeWitness == <> ShutdownDisposeWitness

TransitEventuallyAccepted ==
    WitnessConnInTransit ~> WitnessConnAtApp

UnbindEventuallyCompletes ==
    (listenerPhase = "unbinding") ~> (listenerPhase \in {"unbound", "disposed"})

DisposeEventuallyQuiesces ==
    disposeRequested ~>
        (listenerPhase = "disposed" /\ AllConnectionLocationsQuiesced /\ droppedRefs = {})

=============================================================================
