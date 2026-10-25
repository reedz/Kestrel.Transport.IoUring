---- MODULE Listener ----
EXTENDS Naturals, Sequences, FiniteSets, TLC

\* Phase 4 listener / connection / scheduler model.
\*
\* Traceability to the current implementation:
\*   - Listener slot allocation, generation tagging, accept, completion
\*     dispatch, retry sets, eventfd wake/rearm, close/finalize, unbind, and
\*     dispose are abstracted from
\*     src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:
\*       constructor, Bind, SetConnection, RemoveConnection, SubmitAccept,
\*       SubmitEventFdRead, WakeIoLoop, RequestRecvResubmit, RequestClose,
\*       RequestSendRetry, RunIoLoop, DrainRecvResubmitQueue,
\*       DrainCloseRequestQueue, RetryFailedRecvs, RetryControlOperations,
\*       ResumeSendRetries, FailRing, ProcessCompletions,
\*       DispatchCompletion, HandleAccept, HandleRecv, HandleCancel,
\*       HandleSend, BeginCloseConnection, TryFinalizeClose, UnbindAsync,
\*       and DisposeAsync. Shared retry-timer cleanup / eventfd teardown
\*       ordering helpers are factored in
\*       src/Kestrel.Transport.IoUring/Transport/ListenerDisposeCleanup.cs
\*       via DisposeRetryTimerAsync,
\*       DisposeRetryTimersThenRunEventFdTeardownAsync, and
\*       RunAfterRetryTimersAsync. Listener-owned eventfd wake publication /
\*       close wiring is factored in
\*       src/Kestrel.Transport.IoUring/Transport/ListenerEventFdWakeState.cs
\*       via Wake and CloseAsync. Timer-callback quiescence and eventfd
\*       publisher teardown remain runtime obligations rather than formal step
\*       relations here. Close-finalization cleanup sequencing is factored in
\*       src/Kestrel.Transport.IoUring/Transport/ListenerCloseFinalization.cs
\*       via Run.
\*   - Listener-owned recv retry / cancel-retry slot state is abstracted from
\*     src/Kestrel.Transport.IoUring/Transport/ListenerRecvRetryState.cs and
\*     src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:
\*       queue/origin bookkeeping, recv-retry-gated cancel-pass entry,
\*       ProcessRecvCancelRetry's live generation+recv-epoch gate, and
\*       OnConnectionReleased's final-release purge (ListenerRecvRetryState.cs
\*       7-215; IoUringConnectionListener.cs 569-575).
\*   - Listener-owned send retry state is abstracted from
\*     src/Kestrel.Transport.IoUring/Transport/ListenerSendRetryState.cs and
\*     src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:
\*       request-time queue/wake wiring and current-generation resume selection
\*       (ListenerSendRetryState.cs 1-72; IoUringConnectionListener.cs 383-625).
\*   - Listener-owned eventfd read rearm / retry / wait state is abstracted from
\*     src/Kestrel.Transport.IoUring/Transport/ListenerEventFdReadState.cs and
\*     src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs:
\*       eventfd read in-flight/retry tracking, explicit retry submission, and
\*       the scheduler-work / retry-without-read-in-flight LoopWaitDecision
\*       min_complete gate
\*       (ListenerEventFdReadState.cs 1-59; IoUringConnectionListener.cs
\*       328-364, 482-489, 602-605, 701-712, 765-770).
\*   - Connection-side recv/send/close state is abstracted from
\*     src/Kestrel.Transport.IoUring/Transport/IoUringConnection.cs:
\*       user-data encoding and state fields (19-121), SubmitRecv /
\*       SubmitBufferRingRecv / SubmitRecvCancel (215-272),
\*       OnRecvCompleteFromBuffer / WriteAndFlushRecvData /
\*       ResumeReceiveAfterFlush / OnRecvComplete /
\*       WaitForFlushThenRequestRecv (280-472), send preparation / retry /
\*       completion / send loop (474-665), close / abort handoff,
\*       shutdown, and ring-closed force cleanup (703-795);
\*       callback-safe abort cleanup is factored in
\*       src/Kestrel.Transport.IoUring/Transport/ConnectionTransportAbort.cs
\*       (1-52), and callback-safe socket close / unregister cleanup is
\*       factored in
\*       src/Kestrel.Transport.IoUring/Transport/ConnectionSocketClose.cs
\*       (1-129).
\*   - Pipe-scheduler signal coalescing is abstracted from
\*     src/Kestrel.Transport.IoUring/Transport/IoUringPipeScheduler.cs:13-79.
\*   - Fixed-file slot allocation / reuse is abstracted from
\*     src/Kestrel.Transport.IoUring/Ring.cs:225-311.
\*
\* Assumptions / exclusions / scope bounds:
\*   1. This module does not re-model the full ring internals from
\*      IoUringRing.tla. Instead, sqAvail is an abstract bounded SQ capacity:
\*      a successful transport submit consumes one abstract slot, and the fair
\*      environment action KernelMakesSqSpace returns abstract SQ capacity later.
\*      That is enough to model SQ-full retry parking without duplicating Ring.
\*   2. Each recv token has unit size; each send token has either one part or
\*      two parts according to LargeSendTokens. This preserves pending-data caps,
\*      partial sends, FIFO ordering, and pin lifetime while excluding byte-level
\*      buffer arithmetic.
\*   3. Provided-buffer bid ownership is intentionally left to BufferRing.tla.
\*      This phase only keeps the listener-visible effect of provided-buffer recv
\*      CQEs: stale generation dispatch is ignored safely, and positive tokens
\*      preserve write/pending order.
\*   4. The accept channel stores generation-tagged slot references rather than
\*      managed object identities. A stale dequeue therefore drops silently; the
\*      downstream HTTP behavior of already-closed ConnectionContext instances is
\*      outside this phase.
\*   5. Send continuations that resume inline from CompleteSend are abstracted as
\*      sendReady work on the next IO-loop scheduler step. The model preserves
\*      single-threaded ordering, SQ-full retry parking, and send-pin lifetime,
\*      but not exact intra-method callback timing.
\*   6. recvEpoch is explicitly bounded by Cardinality(RecvTokenUniverse)+1.
\*      That is a state-space bound for TLC, not a production limit claim.
\*   7. Focused Scenario values disable unrelated environment branches to keep
\*      specific bug witnesses and subsystem coverage runs tractable while
\*      reusing the same shared source-like actions from BaseInit.
\*   8. The focused configs avoid direct state-setting bootstraps. When a
\*      scenario uses a narrowed setup action, the action must refine a bounded
\*      source prefix and preserve the same ownership invariants as each
\*      intermediate source step.
\*   9. Focused config CONSTRAINT entries are TLC search-scope bounds only.
\*      They keep witnesses on the intended branch; they are not production
\*      queue-depth or parallelism limits.
\*  10. After DisposeDrainBeforeRingClose marks disposeDrainCompleted, the model
\*      freezes KernelCompleteAny. This bounds the final snapshot to CQEs that
\*      are already visible or that become visible inside the current bounded
\*      materialization step; a later accepted-fd completion stays outside this
\*      proof and is not claimed as ring-close reclamation. The focused hidden
\*      overflow-list mutation below additionally models a bounded backlog that
\*      is not yet visible in cqQueue and may require extra bounded
\*      materialize/drain rounds, but it still does not admit any unrelated
\*      visible-only completion bursts to extend disposal.
\*  11. CloseSocketFd's cancellation callback dispatch, fixed-file unregister,
\*      socket close, listener finalize-slot release, BeginTransportAbort's
\*      cancellation callback / cleanup sequence, and all retry-timer /
\*      eventfd-publisher teardown ordering around
\*      ListenerDisposeCleanup.DisposeRetryTimersThenRunEventFdTeardownAsync
\*      and ListenerEventFdWakeState.Wake / CloseAsync stay entirely outside
\*      this model. TLC does not model callback exceptions, logger throws, or
\*      runtime coordination between timer callbacks, eventfd publishers, and
\*      eventfd close; those runtime obligations are discharged only after
\*      GREEN by ConnectionSocketCloseTests, ListenerCloseFinalizationTests,
\*      ConnectionTransportAbortTests, ListenerDisposeCleanupTests, and
\*      ListenerEventFdWakeStateTests.
\*
\* Compositional note:
\*   Listener.tla does not mechanically refine IoUringRing.tla or BufferRing.tla.
\*   Combining results across these modules is a compositional assumption. In
\*   particular, the remaining ring-side obligation is the real Ring.Enter call
\*   that executes outside SubmitLock, while provided-buffer overflow ownership
\*   is delegated to BufferRing.tla's focused single-connection abstraction.

CONSTANTS
    SlotCount,
    AcceptQueueCapacity,
    SqCapacity,
    PendingRecvCap,
    ConnFdUniverse,
    RecvTokenUniverse,
    SendTokenUniverse,
    LargeSendTokens,
    UseBufferRing,
    UseRegisteredFiles,
    Scenario,
    FixCancelRetryGate,
    FixCancelRetryPassGate,
    FixAcceptRetryPark,
    FixRetryPassPark,
    FixSendRetryPark,
    FixEventFdRearmPark,
    FixStaleCancelSetOnClose,
    FixDisposeAcceptedCqeLeak,
    FixDisposeOverflowDrain,
    DisposeOverflowGuaranteedProgress

ASSUME
    /\ SlotCount \in Nat
    /\ SlotCount > 0
    /\ AcceptQueueCapacity \in Nat
    /\ AcceptQueueCapacity > 0
    /\ SqCapacity \in Nat
    /\ SqCapacity > 0
    /\ PendingRecvCap \in Nat
    /\ PendingRecvCap > 0
    /\ ConnFdUniverse # {}
    /\ RecvTokenUniverse # {}
    /\ SendTokenUniverse # {}
    /\ LargeSendTokens \subseteq SendTokenUniverse
    /\ UseBufferRing \in BOOLEAN
    /\ UseRegisteredFiles \in BOOLEAN
    /\ Scenario \in {"accept-focused",
                     "recv-focused",
                     "send-focused",
                     "dispose-accepted-cqe-focused",
                     "dispose-accepted-overflow-focused",
                     "lifecycle-focused",
                     "structural-safety",
                     "send-retry",
                     "eventfd-wake",
                     "cancel-retry",
                     "stale-close",
                     "receive-liveness",
                     "close-release",
                     "wakeup-shutdown"}
    /\ FixCancelRetryGate \in BOOLEAN
    /\ FixCancelRetryPassGate \in BOOLEAN
    /\ FixAcceptRetryPark \in BOOLEAN
    /\ FixRetryPassPark \in BOOLEAN
    /\ FixSendRetryPark \in BOOLEAN
    /\ FixEventFdRearmPark \in BOOLEAN
    /\ FixStaleCancelSetOnClose \in BOOLEAN
    /\ FixDisposeAcceptedCqeLeak \in BOOLEAN
    /\ FixDisposeOverflowDrain \in BOOLEAN
    /\ DisposeOverflowGuaranteedProgress \in BOOLEAN

Slots == 0..(SlotCount - 1)
ConnStates == {"free", "open", "closing"}
OpStates == {"none", "staged", "armed"}
CloseKinds == {"app", "abort"}
LoopPhases == {"top", "wait", "process", "after", "parked", "stopped"}
NoToken == SlotCount + 2
NoFile == SlotCount + 3
NoSlot == SlotCount + 4
ListenRawFd == SlotCount + 20
EventRawFd == SlotCount + 21
NoFd == SlotCount + 22

OwnerFree == [kind |-> "free", slot |-> NoSlot, gen |-> 0]
OwnerListen == [kind |-> "listen", slot |-> NoSlot, gen |-> 0]
OwnerEvent == [kind |-> "eventfd", slot |-> NoSlot, gen |-> 0]
ConnOwner(slot, gen) == [kind |-> "conn", slot |-> slot, gen |-> gen]

ConnFileSlots == 2..(SlotCount + 1)
AllFileSlots == 0..(SlotCount + 1)
MaxConnGen == Cardinality(ConnFdUniverse)
MaxRecvEpoch == Cardinality(RecvTokenUniverse) + 1
\* Must match Ring.TeardownOverflowDrainRoundLimit.
DisposeOverflowDrainRoundLimit == 4
\* Focused overflow witness allows one documented hidden residual beyond the
\* bounded round cap for the separate cap-exhaustion termination config.
DisposeOverflowHiddenBacklogModelLimit == DisposeOverflowDrainRoundLimit + 1

ScenarioAcceptFocused == Scenario = "accept-focused"
ScenarioRecvFocused == Scenario = "recv-focused"
ScenarioSendFocused == Scenario = "send-focused"
ScenarioDisposeAcceptedCqeFocused == Scenario = "dispose-accepted-cqe-focused"
ScenarioDisposeAcceptedOverflowFocused == Scenario = "dispose-accepted-overflow-focused"
ScenarioLifecycleFocused == Scenario = "lifecycle-focused"
ScenarioStructuralSafety == Scenario = "structural-safety"
ScenarioSendRetry == Scenario = "send-retry"
ScenarioEventFdWake == Scenario = "eventfd-wake"
ScenarioCancelRetry == Scenario = "cancel-retry"
ScenarioStaleClose == Scenario = "stale-close"
ScenarioReceiveLiveness == Scenario = "receive-liveness"
ScenarioCloseRelease == Scenario = "close-release"
ScenarioWakeupShutdown == Scenario = "wakeup-shutdown"

AcceptScenario ==
    \/ ScenarioAcceptFocused
    \/ ScenarioRecvFocused
    \/ ScenarioSendFocused
    \/ ScenarioLifecycleFocused
    \/ ScenarioStructuralSafety
    \/ ScenarioSendRetry
    \/ ScenarioEventFdWake
    \/ ScenarioStaleClose
    \/ ScenarioCloseRelease

RecvScenario ==
    \/ ScenarioRecvFocused
    \/ ScenarioStructuralSafety
    \/ ScenarioCancelRetry
    \/ ScenarioStaleClose
    \/ ScenarioReceiveLiveness
    \/ ScenarioCloseRelease

SendScenario ==
    \/ ScenarioSendFocused
    \/ ScenarioSendRetry
    \/ ScenarioEventFdWake

LifecycleScenario ==
    \/ ScenarioDisposeAcceptedCqeFocused
    \/ ScenarioDisposeAcceptedOverflowFocused
    \/ ScenarioLifecycleFocused
    \/ ScenarioWakeupShutdown

CloseScenario ==
    \/ ScenarioLifecycleFocused
    \/ ScenarioStructuralSafety
    \/ ScenarioStaleClose
    \/ ScenarioCloseRelease
    \/ ScenarioWakeupShutdown

AcceptRetryScenario == ScenarioAcceptFocused
RecvRetryScenario == ScenarioRecvFocused \/ ScenarioCancelRetry \/ ScenarioReceiveLiveness
EventfdScenario ==
    \/ ScenarioAcceptFocused
    \/ ScenarioSendRetry
    \/ ScenarioSendFocused
    \/ ScenarioEventFdWake
    \/ ScenarioLifecycleFocused
    \/ ScenarioWakeupShutdown
SqSpaceScenario ==
    \/ ScenarioAcceptFocused
    \/ ScenarioRecvFocused
    \/ ScenarioSendFocused
    \/ ScenarioLifecycleFocused
    \/ ScenarioStructuralSafety
    \/ ScenarioSendRetry
    \/ ScenarioEventFdWake
    \/ ScenarioCancelRetry
    \/ ScenarioReceiveLiveness
    \/ ScenarioStaleClose
    \/ ScenarioCloseRelease
AcceptAppScenario ==
    \/ ScenarioAcceptFocused
    \/ ScenarioSendFocused
    \/ ScenarioSendRetry
    \/ ScenarioEventFdWake
LifecycleAppScenario ==
    \/ ScenarioLifecycleFocused
    \/ ScenarioCloseRelease
RecvBranchesEnabled == RecvScenario
SendBranchesEnabled == SendScenario
MgmtBranchesEnabled == CloseScenario

WitnessAcceptMore(more) ==
    IF ScenarioAcceptFocused
        THEN TRUE
        ELSE ~more

SendParts(tok) == IF tok \in LargeSendTokens THEN 2 ELSE 1
BoolToNat(p) == IF p THEN 1 ELSE 0
UniqueSeq(s) == \A i, j \in 1..Len(s) : i /= j => s[i] /= s[j]
SeqToSet(s) == { s[i] : i \in 1..Len(s) }
TailOrEmpty(s) == IF Len(s) = 0 THEN << >> ELSE Tail(s)
DistinctNonEmptySeqs(fdPool, maxLen) ==
    { seq \in UNION { [1..n -> fdPool] : n \in 1..maxLen } : UniqueSeq(seq) }
PopLast(s) == IF Len(s) = 0 THEN << >> ELSE SubSeq(s, 1, Len(s) - 1)
LastOf(s) == s[Len(s)]
PendingRecvBytes(q) == Len(q)
AllowedCqeKinds == {"accept", "eventfd", "recv", "send", "cancel"}
CqeOutcomes == {"fd", "einval", "delay", "retry", "ok", "sync", "deferred", "closed", "enobufs", "terminal", "ecanceled", "positive", "abort", "done"}
ConnRef(slot, gen) == [slot |-> slot, gen |-> gen]
CloseReq(slot, gen, kind) == [slot |-> slot, gen |-> gen, kind |-> kind]
IsConnRef(ref) == ref \in [slot : Slots, gen : 0..MaxConnGen]
IsCloseReq(req) == req \in [slot : Slots, gen : 0..MaxConnGen, kind : CloseKinds]
IsConnOwner(owner) ==
    \/ owner = OwnerFree
    \/ owner = OwnerListen
    \/ owner = OwnerEvent
    \/ owner \in [kind : {"conn"}, slot : Slots, gen : 0..MaxConnGen]
IsCqe(cqe) ==
    cqe \in [kind : AllowedCqeKinds,
             slot : Slots \cup {NoSlot},
             gen : 0..MaxConnGen,
             epoch : 0..MaxRecvEpoch,
             outcome : CqeOutcomes,
             more : BOOLEAN,
             token : ConnFdUniverse \cup RecvTokenUniverse \cup SendTokenUniverse \cup {NoToken},
             parts : 0..2]
LiveConnFdSet(slotState, slotFd) == { fd \in ConnFdUniverse : \E s \in Slots : slotState[s] # "free" /\ slotFd[s] = fd }
AcceptCqeFdSet(cqQueue) == { fd \in ConnFdUniverse : \E i \in 1..Len(cqQueue) : cqQueue[i].kind = "accept" /\ cqQueue[i].outcome = "fd" /\ cqQueue[i].token = fd }
DispatchKinds == {"none", "eventfd", "accept", "recv", "send", "cancel"}

ListenFdRef == IF UseRegisteredFiles THEN 0 ELSE ListenRawFd
EventFdRef == IF UseRegisteredFiles THEN 1 ELSE EventRawFd
ConnFdRef(slot, slotFd, slotFile) == IF slotFile[slot] # NoFile THEN slotFile[slot] ELSE slotFd[slot]

Cqe(kind, slot, gen, epoch, outcome, more, token, parts) ==
    [kind |-> kind,
     slot |-> slot,
     gen |-> gen,
     epoch |-> epoch,
     outcome |-> outcome,
     more |-> more,
     token |-> token,
     parts |-> parts]
AcceptCqeSeq(fdSeq) ==
    [i \in 1..Len(fdSeq) |-> Cqe("accept", NoSlot, 0, 0, "fd", FALSE, fdSeq[i], 0)]

InitFreeSlots == [i \in 1..SlotCount |-> SlotCount - i]

VARIABLES
    freeSlots,
    slotState,
    slotGen,
    slotAccepted,
    slotFd,
    slotFile,
    outputCompleted,
    sendLoopCompleted,
    recvEpoch,
    recvArmed,
    recvFdRef,
    recvRearmPending,
    pendingRecv,
    recvSeen,
    recvDelivered,
    sendInFlight,
    sendOpToken,
    sendOpParts,
    sendOpFd,
    sendWaitingRetry,
    sendApp,
    sendPending,
    sendHeadRemaining,
    sendSent,
    sendPin,
    sendReady,
    cancelInFlight,
    cancelTargetEpoch,
    cancelFd,
    appCloseIssued,
    abortIssued,
    transportAborted,
    socketShutdown,
    socketClosed,
    acceptArmed,
    acceptArmedMultishot,
    eventfdReadArmed,
    cqQueue,
    sqAvail,
    recvRetrySet,
    recvRetryDue,
    recvRetryTimerArmed,
    recvRetryPassActive,
    recvRetryPassBlockedOnSq,
    recvCancelRetrySet,
    cancelRetryOriginGen,
    cancelRetryOriginEpoch,
    sendRetrySet,
    recvResubmitQueue,
    closeRequestQueue,
    acceptChannel,
    schedulerQueue,
    signalPending,
    eventfdPending,
    useMultishotAccept,
    acceptRetryRequired,
    acceptRetryDue,
    acceptRetryTimerArmed,
    eventfdReadRetryRequired,
    loopPhase,
    stopping,
    ringFailed,
    unbindStarted,
    disposeStarted,
    unbindTimeoutElapsed,
    ringDisposed,
    availableConnFds,
    availableRecvTokens,
    availableSendTokens,
    releasedConnFds,
    acceptChannelDrops,
    fileSlotOwner,
    nextFileSlot,
    freeFileSlots,
    disposeDrainCompleted,
    disposeOverflowBacklog,
    disposeOverflowTrackedFds,
    disposeOverflowPreDrainEnterCount,
    dispatchLastKind,
    dispatchLastMatched,
    dispatchLastApplied

Vars ==
    << freeSlots,
       slotState,
       slotGen,
       slotAccepted,
       slotFd,
       slotFile,
       outputCompleted,
       sendLoopCompleted,
       recvEpoch,
       recvArmed,
       recvFdRef,
       recvRearmPending,
       pendingRecv,
       recvSeen,
       recvDelivered,
       sendInFlight,
       sendOpToken,
       sendOpParts,
       sendOpFd,
       sendWaitingRetry,
       sendApp,
       sendPending,
       sendHeadRemaining,
       sendSent,
       sendPin,
       sendReady,
       cancelInFlight,
       cancelTargetEpoch,
       cancelFd,
       appCloseIssued,
       abortIssued,
       transportAborted,
       socketShutdown,
       socketClosed,
       acceptArmed,
       acceptArmedMultishot,
       eventfdReadArmed,
       cqQueue,
       sqAvail,
       recvRetrySet,
       recvRetryDue,
       recvRetryTimerArmed,
       recvRetryPassActive,
       recvRetryPassBlockedOnSq,
       recvCancelRetrySet,
       cancelRetryOriginGen,
       cancelRetryOriginEpoch,
       sendRetrySet,
       recvResubmitQueue,
       closeRequestQueue,
       acceptChannel,
       schedulerQueue,
       signalPending,
       eventfdPending,
       useMultishotAccept,
       acceptRetryRequired,
       acceptRetryDue,
       acceptRetryTimerArmed,
       eventfdReadRetryRequired,
       loopPhase,
       stopping,
       ringFailed,
       unbindStarted,
       disposeStarted,
       unbindTimeoutElapsed,
       ringDisposed,
       availableConnFds,
       availableRecvTokens,
       availableSendTokens,
       releasedConnFds,
       acceptChannelDrops,
       fileSlotOwner,
       nextFileSlot,
       freeFileSlots,
       disposeDrainCompleted,
       disposeOverflowBacklog,
       disposeOverflowTrackedFds,
       disposeOverflowPreDrainEnterCount,
       dispatchLastKind,
       dispatchLastMatched,
       dispatchLastApplied >>

AllSlotsFree == \A s \in Slots : slotState[s] = "free"
FocusedSetupDone == \E s \in Slots : slotGen[s] > 0
DisposeAcceptedCqeFocusedSetupDone ==
    \/ cqQueue # << >>
    \/ disposeStarted
    \/ ringDisposed
DisposeAcceptedOverflowFocusedSetupDone ==
    \/ cqQueue # << >>
    \/ disposeOverflowBacklog # << >>
    \/ disposeStarted
    \/ ringDisposed
AcceptRetryParkSetupDone ==
    \/ cqQueue # << >>
    \/ acceptRetryRequired
    \/ \E s \in Slots : slotState[s] # "free"

SqUsed ==
    BoolToNat(acceptArmed = "staged")
    + BoolToNat(eventfdReadArmed = "staged")
    + Cardinality({ s \in Slots : recvArmed[s] = "staged" })
    + Cardinality({ s \in Slots : sendInFlight[s] = "staged" })
    + Cardinality({ s \in Slots : cancelInFlight[s] = "staged" })

HasStagedOps == SqUsed > 0
ArmOpState(op) == IF op = "staged" THEN "armed" ELSE op
ArmPerSlot(opState) == [s \in Slots |-> ArmOpState(opState[s])]

TopWorkDrained ==
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ closeRequestQueue = << >>
    /\ recvResubmitQueue = << >>
    /\ ~\E s \in Slots :
            /\ slotState[s] # "free"
            /\ ~sendLoopCompleted[s]
            /\ outputCompleted[s]
            /\ sendPending[s] = << >>
            /\ sendInFlight[s] = "none"
            /\ ~sendWaitingRetry[s]
    /\ ~\E s \in Slots :
            /\ slotState[s] = "closing"
            /\ sendLoopCompleted[s]
            /\ sendInFlight[s] = "none"
            /\ cancelInFlight[s] = "none"
    /\ ~(unbindStarted /\ \E s \in Slots : slotState[s] # "free" /\ ~abortIssued[s])

AcceptRearmEnabled ==
    \/ ScenarioAcceptFocused
    \/ ScenarioRecvFocused
    \/ ScenarioStructuralSafety
    \/ ScenarioStaleClose
    \/ ScenarioCloseRelease

\* Source traceability: ListenerRecvRetryState.ProcessRecvCancelRetry resolves
\* live recv state and drops queued cancel retries unless the recorded origin
\* still matches that live generation/epoch before SubmitRecvCancel. Rearm steps
\* in this abstraction may clear same-generation markers early; once recvEpoch is
\* incremented they can no longer submit safely in the source either.
ClearSameGenCancelRetry(slot) ==
    IF FixCancelRetryGate /\ slotGen[slot] = cancelRetryOriginGen[slot]
        THEN recvCancelRetrySet \ {slot}
        ELSE recvCancelRetrySet

CancelRetryRecvWitness(slot, more, flush) ==
    /\ recvRetrySet = {}
    /\ (\/ /\ ~recvRearmPending[slot]
          /\ recvSeen[slot] = << >>
          /\ more
          /\ flush = "deferred"
       \/ /\ recvRearmPending[slot]
          /\ Len(recvSeen[slot]) = 1
          /\ ~more
          /\ flush = "sync")

ReceiveLivenessRecvWitness(slot, more, flush) ==
    /\ recvRetrySet = {}
    /\ ~recvRearmPending[slot]
    /\ recvSeen[slot] = << >>
    /\ more
    /\ flush = "deferred"

StaleCloseRecvWitness(slot, more, flush) ==
    /\ ~recvRearmPending[slot]
    /\ recvSeen[slot] = << >>
    /\ more
    /\ flush = "deferred"

WitnessRecvDataAllowed(slot, more, flush) ==
    IF ScenarioCancelRetry
        THEN CancelRetryRecvWitness(slot, more, flush)
        ELSE IF ScenarioReceiveLiveness
            THEN ReceiveLivenessRecvWitness(slot, more, flush)
        ELSE IF ScenarioStructuralSafety \/ ScenarioStaleClose
            THEN StaleCloseRecvWitness(slot, more, flush)
            ELSE IF ScenarioCloseRelease
                THEN FALSE
            ELSE TRUE

RetryPassPending ==
    /\ recvRetryDue
    /\ (recvRetrySet # {} \/ (FixCancelRetryPassGate /\ recvCancelRetrySet # {}))

AcceptRetryDueNow ==
    /\ acceptRetryRequired
    /\ acceptArmed = "none"
    /\ ~stopping
    /\ acceptRetryDue

RetryPassDueNow ==
    RetryPassPending

RetryPassBlockedAfterCleanup(nextRecvRetrySet, nextRecvCancelRetrySet) ==
    /\ recvRetryPassBlockedOnSq
    /\ (nextRecvRetrySet # {} \/ nextRecvCancelRetrySet # {})

DispatchRecord(kind, matched, applied) ==
    /\ disposeDrainCompleted' = disposeDrainCompleted
    /\ dispatchLastKind' = kind
    /\ dispatchLastMatched' = matched
    /\ dispatchLastApplied' = applied

OverflowVarsUnchanged ==
    /\ disposeOverflowBacklog' = disposeOverflowBacklog
    /\ disposeOverflowTrackedFds' = disposeOverflowTrackedFds
    /\ disposeOverflowPreDrainEnterCount' = disposeOverflowPreDrainEnterCount

CanSubmitRecv(slot) ==
    /\ slotState[slot] = "open"
    /\ recvArmed[slot] = "none"
    /\ cancelInFlight[slot] = "none"
    /\ recvEpoch[slot] < MaxRecvEpoch
    /\ sqAvail > 0

CanSubmitCancel(slot) ==
    /\ slotState[slot] = "open"
    /\ UseBufferRing
    /\ recvArmed[slot] # "none"
    /\ cancelInFlight[slot] = "none"
    /\ sqAvail > 0

CanSubmitSend(slot) ==
    /\ slotState[slot] = "open"
    /\ sendInFlight[slot] = "none"
    /\ ~sendWaitingRetry[slot]
    /\ sendPending[slot] # << >>
    /\ sqAvail > 0

CanFinalizeClosing(slot) ==
    /\ slotState[slot] = "closing"
    /\ sendLoopCompleted[slot]
    /\ sendInFlight[slot] = "none"
    /\ cancelInFlight[slot] = "none"

NeedShutdownForClose(slot) ==
    /\ CanFinalizeClosing(slot)
    /\ recvArmed[slot] # "none"

CanReleaseClosing(slot) ==
    /\ CanFinalizeClosing(slot)
    /\ recvArmed[slot] = "none"

ShouldStayUnparked ==
    \/ schedulerQueue # {}
    \/ (FixAcceptRetryPark /\ AcceptRetryDueNow)
    \/ (FixRetryPassPark /\ recvRetryPassBlockedOnSq)
    \/ (FixEventFdRearmPark /\ eventfdReadRetryRequired)

BaseInit ==
    /\ freeSlots = InitFreeSlots
    /\ slotState = [s \in Slots |-> "free"]
    /\ slotGen = [s \in Slots |-> 0]
    /\ slotAccepted = [s \in Slots |-> FALSE]
    /\ slotFd = [s \in Slots |-> NoFd]
    /\ slotFile = [s \in Slots |-> NoFile]
    /\ outputCompleted = [s \in Slots |-> FALSE]
    /\ sendLoopCompleted = [s \in Slots |-> TRUE]
    /\ recvEpoch = [s \in Slots |-> 0]
    /\ recvArmed = [s \in Slots |-> "none"]
    /\ recvFdRef = [s \in Slots |-> NoFd]
    /\ recvRearmPending = [s \in Slots |-> FALSE]
    /\ pendingRecv = [s \in Slots |-> << >>]
    /\ recvSeen = [s \in Slots |-> << >>]
    /\ recvDelivered = [s \in Slots |-> << >>]
    /\ sendInFlight = [s \in Slots |-> "none"]
    /\ sendOpToken = [s \in Slots |-> NoToken]
    /\ sendOpParts = [s \in Slots |-> 0]
    /\ sendOpFd = [s \in Slots |-> NoFd]
    /\ sendWaitingRetry = [s \in Slots |-> FALSE]
    /\ sendApp = [s \in Slots |-> << >>]
    /\ sendPending = [s \in Slots |-> << >>]
    /\ sendHeadRemaining = [s \in Slots |-> 0]
    /\ sendSent = [s \in Slots |-> << >>]
    /\ sendPin = [s \in Slots |-> NoToken]
    /\ sendReady = {}
    /\ cancelInFlight = [s \in Slots |-> "none"]
    /\ cancelTargetEpoch = [s \in Slots |-> 0]
    /\ cancelFd = [s \in Slots |-> NoFd]
    /\ appCloseIssued = [s \in Slots |-> FALSE]
    /\ abortIssued = [s \in Slots |-> FALSE]
    /\ transportAborted = [s \in Slots |-> FALSE]
    /\ socketShutdown = [s \in Slots |-> FALSE]
    /\ socketClosed = [s \in Slots |-> FALSE]
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot = TRUE
    /\ eventfdReadArmed = "armed"
    /\ cqQueue = << >>
    /\ sqAvail = SqCapacity
    /\ recvRetrySet = {}
    /\ recvRetryDue = TRUE
    /\ recvRetryTimerArmed = FALSE
    /\ recvRetryPassActive = FALSE
    /\ recvRetryPassBlockedOnSq = FALSE
    /\ recvCancelRetrySet = {}
    /\ cancelRetryOriginGen = [s \in Slots |-> 0]
    /\ cancelRetryOriginEpoch = [s \in Slots |-> 0]
    /\ sendRetrySet = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ schedulerQueue = {}
    /\ signalPending = FALSE
    /\ eventfdPending = FALSE
    /\ useMultishotAccept = TRUE
    /\ acceptRetryRequired = FALSE
    /\ acceptRetryDue = TRUE
    /\ acceptRetryTimerArmed = FALSE
    /\ eventfdReadRetryRequired = FALSE
    /\ loopPhase = "top"
    /\ stopping = FALSE
    /\ ringFailed = FALSE
    /\ unbindStarted = FALSE
    /\ disposeStarted = FALSE
    /\ unbindTimeoutElapsed = FALSE
    /\ ringDisposed = FALSE
    /\ availableConnFds = ConnFdUniverse
    /\ availableRecvTokens = RecvTokenUniverse
    /\ availableSendTokens = SendTokenUniverse
    /\ releasedConnFds = {}
    /\ acceptChannelDrops = 0
    /\ nextFileSlot = 2
    /\ freeFileSlots = << >>
    /\ fileSlotOwner =
        [f \in AllFileSlots |->
            IF ~UseRegisteredFiles THEN OwnerFree
            ELSE IF f = 0 THEN OwnerListen
            ELSE IF f = 1 THEN OwnerEvent
            ELSE OwnerFree]
    /\ disposeDrainCompleted = FALSE
    /\ disposeOverflowBacklog = << >>
    /\ disposeOverflowTrackedFds = {}
    /\ disposeOverflowPreDrainEnterCount = 0
    /\ dispatchLastKind = "none"
    /\ dispatchLastMatched = TRUE
    /\ dispatchLastApplied = FALSE

ConnVars ==
    << freeSlots,
       slotState,
       slotGen,
       slotAccepted,
       slotFd,
       slotFile,
       outputCompleted,
       sendLoopCompleted,
       recvEpoch,
       recvArmed,
       recvFdRef,
       recvRearmPending,
       pendingRecv,
       recvSeen,
       recvDelivered,
       sendInFlight,
       sendOpToken,
       sendOpParts,
       sendOpFd,
       sendWaitingRetry,
       sendApp,
       sendPending,
       sendHeadRemaining,
       sendSent,
       sendPin,
       sendReady,
       cancelInFlight,
       cancelTargetEpoch,
       cancelFd,
       appCloseIssued,
       abortIssued,
       transportAborted,
       socketShutdown,
       socketClosed >>

RetryVars ==
    << recvRetrySet,
       recvRetryDue,
       recvRetryTimerArmed,
       recvRetryPassActive,
       recvRetryPassBlockedOnSq,
       recvCancelRetrySet,
       cancelRetryOriginGen,
       cancelRetryOriginEpoch,
       sendRetrySet >>

QueueVars == << recvResubmitQueue, closeRequestQueue, acceptChannel, schedulerQueue >>

CtrlVars ==
    << acceptArmed,
       acceptArmedMultishot,
       eventfdReadArmed,
       cqQueue,
       sqAvail,
       signalPending,
       eventfdPending,
       useMultishotAccept,
       acceptRetryRequired,
       acceptRetryDue,
       acceptRetryTimerArmed,
       eventfdReadRetryRequired,
       loopPhase,
       stopping,
       ringFailed,
       unbindStarted,
       disposeStarted,
       unbindTimeoutElapsed,
       ringDisposed >>

PoolVars ==
    << availableConnFds,
       availableRecvTokens,
       availableSendTokens,
       releasedConnFds,
       acceptChannelDrops >>

FileVars ==
    << fileSlotOwner,
       nextFileSlot,
       freeFileSlots,
       disposeOverflowBacklog,
       disposeOverflowTrackedFds,
       disposeOverflowPreDrainEnterCount >>
BugVars ==
    << disposeDrainCompleted,
       dispatchLastKind,
       dispatchLastMatched,
       dispatchLastApplied >>

RecvMatchedNoop ==
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, unbindStarted,
                    disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars >>
ProcessRecvMatched(cqe) ==
    IF slotState[cqe.slot] = "closing"
                                 THEN RecvMatchedNoop
                      ELSE (IF cqe.outcome = "enobufs"
                           THEN /\ recvRetrySet' = recvRetrySet \cup {cqe.slot}
                                /\ recvRetryDue' = FALSE
                                /\ recvRetryTimerArmed' = TRUE
                                /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted,
                                                slotFd, slotFile, outputCompleted,
                                                sendLoopCompleted, recvEpoch, recvArmed,
                                                recvFdRef, recvRearmPending, pendingRecv,
                                                recvSeen, recvDelivered, sendInFlight,
                                                sendOpToken, sendOpParts, sendOpFd,
                                                sendWaitingRetry, sendApp, sendPending,
                                                sendHeadRemaining, sendSent, sendPin,
                                                sendReady, cancelInFlight,
                                                cancelTargetEpoch, cancelFd,
                                                appCloseIssued, abortIssued,
                                                transportAborted, socketShutdown,
                                                socketClosed, recvCancelRetrySet,
                                                cancelRetryOriginGen,
                                                cancelRetryOriginEpoch, sendRetrySet,
                                                recvResubmitQueue, closeRequestQueue,
                                                acceptChannel, schedulerQueue,
                                                acceptArmed, acceptArmedMultishot,
                                                eventfdReadArmed, sqAvail, signalPending,
                                                eventfdPending, useMultishotAccept,
                                                acceptRetryRequired, acceptRetryDue,
                                                acceptRetryTimerArmed,
                                                eventfdReadRetryRequired, loopPhase,
                                                stopping, ringFailed, unbindStarted,
                                                disposeStarted, unbindTimeoutElapsed,
                                                ringDisposed, PoolVars, FileVars,
                                                recvRetryPassActive, recvRetryPassBlockedOnSq>>
                           ELSE IF cqe.outcome = "ecanceled"
                                THEN /\ (IF UseBufferRing /\ slotState[cqe.slot] = "open" /\ ~recvRearmPending[cqe.slot] /\ cancelInFlight[cqe.slot] = "none" /\ recvArmed[cqe.slot] = "none" /\ recvEpoch[cqe.slot] < MaxRecvEpoch /\ sqAvail > 0
                                          THEN /\ recvArmed' = [recvArmed EXCEPT ![cqe.slot] = "staged"]
                                               /\ recvEpoch' = [recvEpoch EXCEPT ![cqe.slot] = @ + 1]
                                               /\ recvFdRef' = [recvFdRef EXCEPT ![cqe.slot] = ConnFdRef(cqe.slot, slotFd, slotFile)]
                                               /\ sqAvail' = sqAvail - 1
                                               /\ recvRetrySet' = recvRetrySet
                                               /\ recvCancelRetrySet' =
                                                    IF FixCancelRetryGate /\ slotGen[cqe.slot] = cancelRetryOriginGen[cqe.slot]
                                                        THEN recvCancelRetrySet \ {cqe.slot}
                                                        ELSE recvCancelRetrySet
                                          ELSE IF UseBufferRing /\ slotState[cqe.slot] = "open" /\ ~recvRearmPending[cqe.slot] /\ cancelInFlight[cqe.slot] = "none" /\ recvArmed[cqe.slot] = "none"
                                               THEN /\ recvRetrySet' = recvRetrySet \cup {cqe.slot}
                                                    /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail >>
                                                    /\ UNCHANGED recvCancelRetrySet
                                               ELSE /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail, recvRetrySet , recvRetryPassActive, recvRetryPassBlockedOnSq>>
                                                    /\ UNCHANGED recvCancelRetrySet)
                                     /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted,
                                                     slotFd, slotFile, outputCompleted,
                                                     sendLoopCompleted, recvRearmPending,
                                                     pendingRecv, recvSeen, recvDelivered,
                                                     sendInFlight, sendOpToken, sendOpParts,
                                                     sendOpFd, sendWaitingRetry, sendApp,
                                                     sendPending, sendHeadRemaining,
                                                     sendSent, sendPin, sendReady,
                                                     cancelInFlight, cancelTargetEpoch,
                                                     cancelFd, appCloseIssued,
                                                     abortIssued, transportAborted,
                                                     socketShutdown, socketClosed,
                                                     recvRetryDue, recvRetryTimerArmed,
                                                     recvRetryPassActive,
                                                     recvRetryPassBlockedOnSq,
                                                     cancelRetryOriginGen,
                                                     cancelRetryOriginEpoch, sendRetrySet,
                                                     recvResubmitQueue, closeRequestQueue,
                                                     acceptChannel, schedulerQueue,
                                                     acceptArmed, acceptArmedMultishot,
                                                     eventfdReadArmed, signalPending,
                                                     eventfdPending, useMultishotAccept,
                                                     acceptRetryRequired, acceptRetryDue,
                                                     acceptRetryTimerArmed,
                                                     eventfdReadRetryRequired, loopPhase,
                                                     stopping, ringFailed, unbindStarted,
                                                     disposeStarted, unbindTimeoutElapsed,
                                                     ringDisposed, PoolVars, FileVars >>
                                ELSE IF cqe.outcome = "terminal"
                                     THEN /\ slotState' = [slotState EXCEPT ![cqe.slot] = "closing"]
                                          /\ recvRetrySet' = recvRetrySet \ {cqe.slot}
                                          /\ recvCancelRetrySet' = IF FixStaleCancelSetOnClose THEN recvCancelRetrySet \ {cqe.slot} ELSE recvCancelRetrySet
                                          /\ recvRetryPassBlockedOnSq' =
                                                RetryPassBlockedAfterCleanup(
                                                    recvRetrySet \ {cqe.slot},
                                                    IF FixStaleCancelSetOnClose
                                                        THEN recvCancelRetrySet \ {cqe.slot}
                                                        ELSE recvCancelRetrySet)
                                          /\ UNCHANGED << freeSlots, slotGen, slotAccepted,
                                                          slotFd, slotFile,
                                                          outputCompleted,
                                                          sendLoopCompleted, recvEpoch,
                                                          recvArmed, recvFdRef,
                                                          recvRearmPending, pendingRecv,
                                                          recvSeen, recvDelivered,
                                                          sendInFlight, sendOpToken,
                                                          sendOpParts, sendOpFd,
                                                          sendWaitingRetry, sendApp,
                                                          sendPending,
                                                          sendHeadRemaining, sendSent,
                                                          sendPin, sendReady,
                                                          cancelInFlight,
                                                          cancelTargetEpoch, cancelFd,
                                                          appCloseIssued, abortIssued,
                                                          transportAborted,
                                                          socketShutdown, socketClosed,
                                                          recvRetryDue,
                                                          recvRetryTimerArmed,
                                                          recvRetryPassActive,
                                                          cancelRetryOriginGen,
                                                          cancelRetryOriginEpoch,
                                                          sendRetrySet,
                                                          recvResubmitQueue,
                                                          closeRequestQueue,
                                                          acceptChannel,
                                                          schedulerQueue,
                                                          acceptArmed,
                                                          acceptArmedMultishot,
                                                          eventfdReadArmed, sqAvail,
                                                          signalPending, eventfdPending,
                                                          useMultishotAccept,
                                                          acceptRetryRequired,
                                                          acceptRetryDue,
                                                          acceptRetryTimerArmed,
                                                          eventfdReadRetryRequired,
                                                          loopPhase, stopping,
                                                          ringFailed, unbindStarted,
                                                          disposeStarted,
                                                          unbindTimeoutElapsed,
                                                          ringDisposed, PoolVars,
                                                          FileVars >>
                                ELSE \* sync / deferred / closed positive token
                                     IF recvRearmPending[cqe.slot]
                                         THEN IF PendingRecvBytes(pendingRecv[cqe.slot]) + 1 > PendingRecvCap
                                                THEN /\ slotState' = [slotState EXCEPT ![cqe.slot] = "closing"]
                                                     /\ outputCompleted' = [outputCompleted EXCEPT ![cqe.slot] = TRUE]
                                                     /\ transportAborted' = [transportAborted EXCEPT ![cqe.slot] = TRUE]
                                                     /\ socketShutdown' = [socketShutdown EXCEPT ![cqe.slot] = TRUE]
                                                     /\ recvRetrySet' = recvRetrySet \ {cqe.slot}
                                                     /\ recvCancelRetrySet' =
                                                          IF FixStaleCancelSetOnClose
                                                              THEN recvCancelRetrySet \ {cqe.slot}
                                                              ELSE recvCancelRetrySet
                                                     /\ recvRetryPassBlockedOnSq' =
                                                           RetryPassBlockedAfterCleanup(
                                                               recvRetrySet \ {cqe.slot},
                                                               IF FixStaleCancelSetOnClose
                                                                   THEN recvCancelRetrySet \ {cqe.slot}
                                                                   ELSE recvCancelRetrySet)
                                                     /\ UNCHANGED << freeSlots, slotGen, slotAccepted,
                                                                     slotFd, slotFile,
                                                                     sendLoopCompleted, recvEpoch,
                                                                     recvArmed, recvFdRef,
                                                                     recvRearmPending, pendingRecv,
                                                                     recvSeen, recvDelivered,
                                                                     sendInFlight, sendOpToken,
                                                                     sendOpParts, sendOpFd,
                                                                     sendWaitingRetry, sendApp,
                                                                     sendPending, sendHeadRemaining,
                                                                     sendSent, sendPin, sendReady,
                                                                     cancelInFlight,
                                                                     cancelTargetEpoch, cancelFd,
                                                                     appCloseIssued, abortIssued,
                                                                     socketClosed, recvRetryDue,
                                                                     recvRetryTimerArmed,
                                                                     recvRetryPassActive,
                                                                     cancelRetryOriginGen,
                                                                     cancelRetryOriginEpoch,
                                                                     sendRetrySet,
                                                                     recvResubmitQueue,
                                                                     closeRequestQueue,
                                                                     acceptChannel, schedulerQueue,
                                                                     acceptArmed,
                                                                     acceptArmedMultishot,
                                                                     eventfdReadArmed, sqAvail,
                                                                     signalPending, eventfdPending,
                                                                     useMultishotAccept,
                                                                     acceptRetryRequired,
                                                                     acceptRetryDue,
                                                                     acceptRetryTimerArmed,
                                                                     eventfdReadRetryRequired,
                                                                     loopPhase, stopping,
                                                                     ringFailed, unbindStarted,
                                                                     disposeStarted,
                                                                     unbindTimeoutElapsed,
                                                                     ringDisposed, PoolVars,
                                                                     FileVars >>
                                                ELSE /\ recvSeen' = [recvSeen EXCEPT ![cqe.slot] = Append(@, cqe.token)]
                                                     /\ pendingRecv' = [pendingRecv EXCEPT ![cqe.slot] = Append(@, cqe.token)]
                                                     /\ IF cqe.more /\ UseBufferRing /\ cancelInFlight[cqe.slot] = "none" /\ sqAvail > 0
                                                           THEN /\ cancelInFlight' = [cancelInFlight EXCEPT ![cqe.slot] = "staged"]
                                                                /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![cqe.slot] = recvEpoch[cqe.slot]]
                                                                /\ cancelFd' = [cancelFd EXCEPT ![cqe.slot] = ConnFdRef(cqe.slot, slotFd, slotFile)]
                                                                /\ sqAvail' = sqAvail - 1
                                                                /\ recvCancelRetrySet' = recvCancelRetrySet
                                                                /\ UNCHANGED << cancelRetryOriginGen,
                                                                                cancelRetryOriginEpoch,
                                                                                recvRetryPassActive, recvRetryPassBlockedOnSq >>
                                                           ELSE IF cqe.more /\ UseBufferRing /\ cancelInFlight[cqe.slot] = "none"
                                                                THEN /\ recvCancelRetrySet' = recvCancelRetrySet \cup {cqe.slot}
                                                                     /\ cancelRetryOriginGen' = [cancelRetryOriginGen EXCEPT ![cqe.slot] = slotGen[cqe.slot]]
                                                                     /\ cancelRetryOriginEpoch' = [cancelRetryOriginEpoch EXCEPT ![cqe.slot] = recvEpoch[cqe.slot]]
                                                                     /\ UNCHANGED << cancelInFlight,
                                                                                     cancelTargetEpoch,
                                                                                     cancelFd, sqAvail >>
                                                                ELSE /\ UNCHANGED << cancelInFlight,
                                                                                    cancelTargetEpoch,
                                                                                    cancelFd, sqAvail,
                                                                                    recvCancelRetrySet,
                                                                                    cancelRetryOriginGen,
                                                                                    cancelRetryOriginEpoch,
                                                                                    recvRetryPassActive, recvRetryPassBlockedOnSq >>
                                                     /\ UNCHANGED << freeSlots, slotState, slotGen,
                                                                     slotAccepted, slotFd,
                                                                     slotFile, outputCompleted,
                                                                     sendLoopCompleted, recvEpoch,
                                                                     recvArmed, recvFdRef,
                                                                     recvRearmPending,
                                                                     recvDelivered,
                                                                     sendInFlight, sendOpToken,
                                                                     sendOpParts, sendOpFd,
                                                                     sendWaitingRetry, sendApp,
                                                                     sendPending, sendHeadRemaining,
                                                                     sendSent, sendPin, sendReady,
                                                                     appCloseIssued, abortIssued,
                                                                     transportAborted,
                                                                     socketShutdown, socketClosed,
                                                                     recvRetrySet, recvRetryDue,
                                                                     recvRetryTimerArmed,
                                                                     recvRetryPassActive,
                                                                     recvRetryPassBlockedOnSq,
                                                                     sendRetrySet,
                                                                     recvResubmitQueue,
                                                                     closeRequestQueue,
                                                                     acceptChannel,
                                                                     schedulerQueue,
                                                                     acceptArmed,
                                                                     acceptArmedMultishot,
                                                                     eventfdReadArmed,
                                                                     signalPending,
                                                                     eventfdPending,
                                                                     useMultishotAccept,
                                                                     acceptRetryRequired,
                                                                     acceptRetryDue,
                                                                     acceptRetryTimerArmed,
                                                                     eventfdReadRetryRequired,
                                                                     loopPhase, stopping,
                                                                     ringFailed,
                                                                     unbindStarted,
                                                                     disposeStarted,
                                                                     unbindTimeoutElapsed,
                                                                     ringDisposed, PoolVars,
                                                                     FileVars >>
                                         ELSE /\ recvSeen' = [recvSeen EXCEPT ![cqe.slot] = Append(@, cqe.token)]
                                              /\ recvDelivered' = [recvDelivered EXCEPT ![cqe.slot] = Append(@, cqe.token)]
                                              /\ IF cqe.outcome = "closed"
                                                    THEN /\ slotState' = [slotState EXCEPT ![cqe.slot] = "closing"]
                                                         /\ recvRetrySet' = recvRetrySet \ {cqe.slot}
                                                         /\ recvCancelRetrySet' =
                                                              IF FixStaleCancelSetOnClose
                                                                  THEN recvCancelRetrySet \ {cqe.slot}
                                                                  ELSE recvCancelRetrySet
                                                         /\ recvRetryPassBlockedOnSq' =
                                                               RetryPassBlockedAfterCleanup(
                                                                   recvRetrySet \ {cqe.slot},
                                                                   IF FixStaleCancelSetOnClose
                                                                       THEN recvCancelRetrySet \ {cqe.slot}
                                                                       ELSE recvCancelRetrySet)
                                                         /\ UNCHANGED << outputCompleted,
                                                                         transportAborted,
                                                                         socketShutdown,
                                                                         recvArmed, recvEpoch,
                                                                         recvFdRef,
                                                                         recvRearmPending,
                                                                         pendingRecv,
                                                                         cancelInFlight,
                                                                         cancelTargetEpoch,
                                                                         cancelFd, sqAvail,
                                                                         cancelRetryOriginGen,
                                                                         cancelRetryOriginEpoch,
                                                                         recvRetryPassActive >>
                                                    ELSE IF cqe.outcome = "deferred"
                                                         THEN /\ recvRearmPending' = [recvRearmPending EXCEPT ![cqe.slot] = TRUE]
                                                              /\ UNCHANGED << slotState,
                                                                              outputCompleted,
                                                                              transportAborted,
                                                                              socketShutdown,
                                                                              recvArmed,
                                                                              recvEpoch,
                                                                              recvFdRef,
                                                                              recvRetrySet,
                                                                              recvRetryPassActive, recvRetryPassBlockedOnSq >>
                                                              /\ IF cqe.more /\ UseBufferRing /\ cancelInFlight[cqe.slot] = "none" /\ sqAvail > 0
                                                                    THEN /\ cancelInFlight' = [cancelInFlight EXCEPT ![cqe.slot] = "staged"]
                                                                         /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![cqe.slot] = recvEpoch[cqe.slot]]
                                                                         /\ cancelFd' = [cancelFd EXCEPT ![cqe.slot] = ConnFdRef(cqe.slot, slotFd, slotFile)]
                                                                         /\ sqAvail' = sqAvail - 1
                                                                         /\ recvCancelRetrySet' = recvCancelRetrySet
                                                                         /\ UNCHANGED << cancelRetryOriginGen,
                                                                                         cancelRetryOriginEpoch,
                                                                                         recvRetryPassActive, recvRetryPassBlockedOnSq >>
                                                                    ELSE IF cqe.more /\ UseBufferRing /\ cancelInFlight[cqe.slot] = "none"
                                                                         THEN /\ recvCancelRetrySet' = recvCancelRetrySet \cup {cqe.slot}
                                                                              /\ cancelRetryOriginGen' = [cancelRetryOriginGen EXCEPT ![cqe.slot] = slotGen[cqe.slot]]
                                                                              /\ cancelRetryOriginEpoch' = [cancelRetryOriginEpoch EXCEPT ![cqe.slot] = recvEpoch[cqe.slot]]
                                                                              /\ UNCHANGED << cancelInFlight,
                                                                                              cancelTargetEpoch,
                                                                                              cancelFd, sqAvail >>
                                                                         ELSE /\ UNCHANGED << cancelInFlight,
                                                                                             cancelTargetEpoch,
                                                                                             cancelFd, sqAvail,
                                                                                             recvCancelRetrySet,
                                                                                             cancelRetryOriginGen,
                                                                                             cancelRetryOriginEpoch,
                                                                                             recvRetryPassActive, recvRetryPassBlockedOnSq >>
                                                         ELSE /\ UNCHANGED << slotState,
                                                                             outputCompleted,
                                                                             transportAborted,
                                                                             socketShutdown,
                                                                             recvRearmPending,
                                                                             cancelInFlight,
                                                                             cancelTargetEpoch,
                                                                             cancelFd,
                                                                             cancelRetryOriginGen,
                                                                             cancelRetryOriginEpoch,
                                                                             recvRetryPassActive, recvRetryPassBlockedOnSq >>
                                                              /\ IF ~cqe.more /\ slotState[cqe.slot] = "open" /\ recvArmed[cqe.slot] = "none" /\ cancelInFlight[cqe.slot] = "none" /\ recvEpoch[cqe.slot] < MaxRecvEpoch /\ sqAvail > 0
                                                                    THEN /\ recvArmed' = [recvArmed EXCEPT ![cqe.slot] = "staged"]
                                                                         /\ recvEpoch' = [recvEpoch EXCEPT ![cqe.slot] = @ + 1]
                                                                         /\ recvFdRef' = [recvFdRef EXCEPT ![cqe.slot] = ConnFdRef(cqe.slot, slotFd, slotFile)]
                                                                         /\ sqAvail' = sqAvail - 1
                                                                         /\ recvRetrySet' = recvRetrySet
                                                                         /\ recvCancelRetrySet' =
                                                                              IF FixCancelRetryGate /\ slotGen[cqe.slot] = cancelRetryOriginGen[cqe.slot]
                                                                                  THEN recvCancelRetrySet \ {cqe.slot}
                                                                                  ELSE recvCancelRetrySet
                                                                    ELSE IF ~cqe.more /\ slotState[cqe.slot] = "open" /\ recvArmed[cqe.slot] = "none" /\ cancelInFlight[cqe.slot] = "none"
                                                                         THEN /\ recvRetrySet' = recvRetrySet \cup {cqe.slot}
                                                                              /\ UNCHANGED << recvArmed,
                                                                                              recvEpoch,
                                                                                              recvFdRef,
                                                                                              sqAvail >>
                                                                              /\ UNCHANGED recvCancelRetrySet
                                                                         ELSE /\ UNCHANGED << recvArmed,
                                                                                             recvEpoch,
                                                                                             recvFdRef,
                                                                                             sqAvail,
                                                                                             recvRetrySet,
                                                                                             recvRetryPassActive, recvRetryPassBlockedOnSq >>
                                                                              /\ UNCHANGED recvCancelRetrySet
                                              /\ UNCHANGED << freeSlots, slotGen, slotAccepted,
                                                              slotFd, slotFile,
                                                              sendLoopCompleted,
                                                              pendingRecv,
                                                              sendInFlight,
                                                              sendOpToken,
                                                              sendOpParts, sendOpFd,
                                                              sendWaitingRetry,
                                                              sendApp, sendPending,
                                                              sendHeadRemaining,
                                                              sendSent, sendPin,
                                                              sendReady,
                                                              appCloseIssued,
                                                              abortIssued,
                                                              socketClosed,
                                                              recvRetryDue,
                                                              recvRetryTimerArmed,
                                                              recvRetryPassActive,
                                                              recvRetryPassBlockedOnSq,
                                                              sendRetrySet,
                                                              recvResubmitQueue,
                                                              closeRequestQueue,
                                                              acceptChannel,
                                                              schedulerQueue,
                                                              acceptArmed,
                                                              acceptArmedMultishot,
                                                              eventfdReadArmed,
                                                              signalPending,
                                                              eventfdPending,
                                                              useMultishotAccept,
                                                              acceptRetryRequired,
                                                              acceptRetryDue,
                                                              acceptRetryTimerArmed,
                                                              eventfdReadRetryRequired,
                                                              loopPhase,
                                                              stopping, ringFailed,
                                                              unbindStarted,
                                                              disposeStarted,
                                                              unbindTimeoutElapsed,
                                                              ringDisposed, PoolVars,
                                                              FileVars >>
    )



\* --- Environment actions -------------------------------------------------

KernelMakesSqSpace ==
    /\ SqSpaceScenario
    /\ sqAvail < SqCapacity
    /\ sqAvail' = sqAvail + 1
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed, acceptArmedMultishot,
                    eventfdReadArmed, cqQueue, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, loopPhase,
                    stopping, ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteAcceptFd ==
    /\ AcceptScenario
    /\ acceptArmed = "armed"
    /\ availableConnFds # {}
    /\ \E fd \in availableConnFds, more \in BOOLEAN :
        /\ WitnessAcceptMore(more)
        /\ more => acceptArmedMultishot
        /\ acceptArmed' = IF more THEN acceptArmed ELSE "none"
        /\ acceptArmedMultishot' = IF more THEN acceptArmedMultishot ELSE FALSE
        /\ cqQueue' = Append(cqQueue, Cqe("accept", NoSlot, 0, 0, "fd", more, fd, 0))
        /\ availableConnFds' = availableConnFds \ {fd}
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, availableRecvTokens, availableSendTokens,
                    releasedConnFds, acceptChannelDrops, FileVars, BugVars >>

KernelCompleteAcceptUnsupported ==
    /\ AcceptRetryScenario
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot
    /\ acceptArmed' = "none"
    /\ acceptArmedMultishot' = FALSE
    /\ cqQueue' = Append(cqQueue, Cqe("accept", NoSlot, 0, 0, "einval", FALSE, NoToken, 0))
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteAcceptDelayedError ==
    /\ AcceptRetryScenario
    /\ acceptArmed = "armed"
    /\ acceptArmed' = "none"
    /\ acceptArmedMultishot' = FALSE
    /\ cqQueue' = Append(cqQueue, Cqe("accept", NoSlot, 0, 0, "delay", FALSE, NoToken, 0))
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteAcceptTransientError ==
    /\ AcceptRetryScenario
    /\ acceptArmed = "armed"
    /\ acceptArmed' = "none"
    /\ acceptArmedMultishot' = FALSE
    /\ cqQueue' = Append(cqQueue, Cqe("accept", NoSlot, 0, 0, "retry", FALSE, NoToken, 0))
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteEventfd ==
    /\ EventfdScenario
    /\ eventfdReadArmed = "armed"
    /\ eventfdPending
    /\ eventfdReadArmed' = "none"
    /\ eventfdPending' = FALSE
    /\ cqQueue' = Append(cqQueue, Cqe("eventfd", NoSlot, 0, 0, "ok", FALSE, NoToken, 0))
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed, acceptArmedMultishot,
                    sqAvail, signalPending, useMultishotAccept, acceptRetryRequired,
                    acceptRetryDue, acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteRecvData ==
    /\ RecvBranchesEnabled
    /\ availableRecvTokens # {}
    /\ \E s \in Slots, tok \in availableRecvTokens, more \in BOOLEAN,
         flush \in {"sync", "deferred", "closed"} :
        /\ recvArmed[s] = "armed"
        /\ WitnessRecvDataAllowed(s, more, flush)
        /\ more => UseBufferRing
        /\ IF more THEN TRUE ELSE TRUE
        /\ recvArmed' = [recvArmed EXCEPT ![s] = IF more THEN @ ELSE "none"]
        /\ cqQueue' = Append(cqQueue, Cqe("recv", s, slotGen[s], recvEpoch[s], flush, more, tok, 0))
        /\ availableRecvTokens' = availableRecvTokens \ {tok}
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvFdRef,
                    recvRearmPending, pendingRecv, recvSeen, recvDelivered,
                    sendInFlight, sendOpToken, sendOpParts, sendOpFd,
                    sendWaitingRetry, sendApp, sendPending, sendHeadRemaining,
                    sendSent, sendPin, sendReady, cancelInFlight,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    QueueVars, acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    sqAvail, signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, availableConnFds, availableSendTokens,
                    releasedConnFds, acceptChannelDrops, FileVars, BugVars >>

KernelCompleteRecvEnobufs ==
    /\ ScenarioRecvFocused
    /\ UseBufferRing
    /\ \E s \in Slots : recvArmed[s] = "armed"
    /\ \E s \in Slots :
        /\ recvArmed[s] = "armed"
        /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
        /\ cqQueue' = Append(cqQueue, Cqe("recv", s, slotGen[s], recvEpoch[s], "enobufs", FALSE, NoToken, 0))
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvFdRef,
                    recvRearmPending, pendingRecv, recvSeen, recvDelivered,
                    sendInFlight, sendOpToken, sendOpParts, sendOpFd,
                    sendWaitingRetry, sendApp, sendPending, sendHeadRemaining,
                    sendSent, sendPin, sendReady, cancelInFlight,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    QueueVars, acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    sqAvail, signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteRecvTerminal ==
    /\ (ScenarioRecvFocused \/ ScenarioLifecycleFocused \/ ScenarioStructuralSafety \/ ScenarioStaleClose \/ ScenarioCloseRelease)
    /\ \E s \in Slots : recvArmed[s] = "armed" /\ (ScenarioRecvFocused \/ slotState[s] = "closing")
    /\ \E s \in Slots :
        /\ recvArmed[s] = "armed"
        /\ (ScenarioRecvFocused \/ slotState[s] = "closing")
        /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
        /\ cqQueue' = Append(cqQueue, Cqe("recv", s, slotGen[s], recvEpoch[s], "terminal", FALSE, NoToken, 0))
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvFdRef,
                    recvRearmPending, pendingRecv, recvSeen, recvDelivered,
                    sendInFlight, sendOpToken, sendOpParts, sendOpFd,
                    sendWaitingRetry, sendApp, sendPending, sendHeadRemaining,
                    sendSent, sendPin, sendReady, cancelInFlight,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    QueueVars, acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    sqAvail, signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteRecvCanceled ==
    /\ (ScenarioRecvFocused \/ ScenarioCancelRetry \/ ScenarioReceiveLiveness)
    /\ \E s \in Slots : recvArmed[s] = "armed" /\ cancelInFlight[s] = "armed" /\ cancelTargetEpoch[s] = recvEpoch[s]
    /\ \E s \in Slots :
        /\ recvArmed[s] = "armed"
        /\ cancelInFlight[s] = "armed"
        /\ cancelTargetEpoch[s] = recvEpoch[s]
        /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
        /\ cqQueue' = Append(cqQueue, Cqe("recv", s, slotGen[s], recvEpoch[s], "ecanceled", FALSE, NoToken, 0))
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvFdRef,
                    recvRearmPending, pendingRecv, recvSeen, recvDelivered,
                    sendInFlight, sendOpToken, sendOpParts, sendOpFd,
                    sendWaitingRetry, sendApp, sendPending, sendHeadRemaining,
                    sendSent, sendPin, sendReady, cancelInFlight,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    QueueVars, acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    sqAvail, signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteSend ==
    /\ SendBranchesEnabled
    /\ \E s \in Slots, parts \in 0..2 :
        /\ sendInFlight[s] = "armed"
        /\ parts <= sendOpParts[s]
        /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
        /\ cqQueue' = Append(cqQueue, Cqe("send", s, slotGen[s], 0,
                                        IF parts = 0 THEN "abort" ELSE "positive",
                                        FALSE, sendOpToken[s], parts))
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendOpToken, sendOpParts, sendOpFd,
                    sendWaitingRetry, sendApp, sendPending, sendHeadRemaining,
                    sendSent, sendPin, sendReady, cancelInFlight,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    QueueVars, acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    sqAvail, signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

KernelCompleteCancelControl ==
    /\ (ScenarioRecvFocused \/ ScenarioCancelRetry \/ ScenarioReceiveLiveness \/ ScenarioStaleClose)
    /\ \E s \in Slots : cancelInFlight[s] = "armed"
    /\ \E s \in Slots :
        /\ cancelInFlight[s] = "armed"
        /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
        /\ cqQueue' = Append(cqQueue, Cqe("cancel", s, slotGen[s], cancelTargetEpoch[s], "done", FALSE, NoToken, 0))
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendApp, sendPending,
                    sendHeadRemaining, sendSent, sendPin, sendReady,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    QueueVars, acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    sqAvail, signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

AppDrainFlushSuccess ==
    /\ (ScenarioRecvFocused \/ ScenarioCancelRetry \/ ScenarioReceiveLiveness)
    /\ \E s \in Slots : recvRearmPending[s] /\ slotState[s] = "open"
    /\ \E s \in Slots :
        /\ recvRearmPending[s]
        /\ slotState[s] = "open"
        /\ IF ScenarioCancelRetry \/ ScenarioReceiveLiveness THEN recvArmed[s] = "none" ELSE TRUE
        /\ recvResubmitQueue' = Append(recvResubmitQueue, ConnRef(s, slotGen[s]))
        /\ eventfdPending' = IF ScenarioCancelRetry \/ ScenarioReceiveLiveness THEN eventfdPending ELSE TRUE
    /\ UNCHANGED << ConnVars, RetryVars, closeRequestQueue, acceptChannel,
                    schedulerQueue, acceptArmed, acceptArmedMultishot,
                    eventfdReadArmed, cqQueue, sqAvail, signalPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, loopPhase,
                    stopping, ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, PoolVars, FileVars,
                    BugVars >>

AppDrainFlushClose ==
    /\ (ScenarioRecvFocused \/ ScenarioStructuralSafety \/ ScenarioStaleClose \/ ScenarioCloseRelease)
    /\ \E s \in Slots : recvRearmPending[s] /\ slotState[s] = "open"
    /\ \E s \in Slots :
        /\ recvRearmPending[s]
        /\ slotState[s] = "open"
        /\ closeRequestQueue' = Append(closeRequestQueue, CloseReq(s, slotGen[s], "app"))
        /\ eventfdPending' = IF ScenarioStructuralSafety \/ ScenarioStaleClose THEN eventfdPending ELSE TRUE
    /\ UNCHANGED << ConnVars, RetryVars, recvResubmitQueue, acceptChannel,
                    schedulerQueue, acceptArmed, acceptArmedMultishot,
                    eventfdReadArmed, cqQueue, sqAvail, signalPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, loopPhase,
                    stopping, ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, PoolVars, FileVars,
                    BugVars >>

AppAcceptConnection ==
    /\ AcceptAppScenario
    /\ IF acceptChannel = << >>
          THEN FALSE
          ELSE LET ref == Head(acceptChannel) IN
               /\ acceptChannel' = TailOrEmpty(acceptChannel)
               /\ slotAccepted' =
                    IF ref.slot \in Slots /\ slotState[ref.slot] = "open" /\ slotGen[ref.slot] = ref.gen
                        THEN [slotAccepted EXCEPT ![ref.slot] = TRUE]
                        ELSE slotAccepted
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendApp, sendPending,
                    sendHeadRemaining, sendSent, sendPin, sendReady,
                    cancelInFlight, cancelTargetEpoch, cancelFd,
                    appCloseIssued, abortIssued, transportAborted,
                    socketShutdown, socketClosed, RetryVars, recvResubmitQueue,
                    closeRequestQueue, schedulerQueue, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

AppProduceSend ==
    /\ SendBranchesEnabled
    /\ availableSendTokens # {}
    /\ \E s \in Slots, tok \in availableSendTokens :
        /\ slotState[s] = "open"
        /\ slotAccepted[s]
        /\ ~outputCompleted[s]
        /\ sendApp' = [sendApp EXCEPT ![s] = Append(@, tok)]
        /\ sendPending' = [sendPending EXCEPT ![s] = Append(@, tok)]
        /\ sendHeadRemaining' = IF sendPending[s] = << >>
                                  THEN [sendHeadRemaining EXCEPT ![s] = SendParts(tok)]
                                  ELSE sendHeadRemaining
        /\ schedulerQueue' = schedulerQueue \cup {s}
        /\ signalPending' = TRUE
        /\ eventfdPending' = TRUE
        /\ availableSendTokens' = availableSendTokens \ {tok}
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendSent, sendPin, sendReady,
                    cancelInFlight, cancelTargetEpoch, cancelFd,
                    appCloseIssued, abortIssued, transportAborted,
                    socketShutdown, socketClosed, RetryVars, recvResubmitQueue,
                    closeRequestQueue, acceptChannel, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, loopPhase,
                    stopping, ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, availableConnFds,
                    availableRecvTokens, releasedConnFds, acceptChannelDrops,
                    FileVars, BugVars >>

AppRequestDispose ==
    /\ LifecycleAppScenario
    /\ \E s \in Slots : slotState[s] = "open" /\ ~appCloseIssued[s]
    /\ \E s \in Slots :
        /\ slotState[s] = "open"
        /\ ~appCloseIssued[s]
        /\ IF ScenarioCloseRelease THEN recvArmed[s] = "armed" ELSE TRUE
        /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = TRUE]
        /\ closeRequestQueue' = Append(closeRequestQueue, CloseReq(s, slotGen[s], "app"))
        /\ eventfdPending' = TRUE
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendApp, sendPending,
                    sendHeadRemaining, sendSent, sendPin, sendReady,
                    cancelInFlight, cancelTargetEpoch, cancelFd, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    recvResubmitQueue, acceptChannel, schedulerQueue,
                    acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    cqQueue, sqAvail, signalPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

AppRequestAbort ==
    /\ LifecycleAppScenario
    /\ \E s \in Slots : slotState[s] = "open" /\ ~abortIssued[s]
    /\ \E s \in Slots :
        /\ slotState[s] = "open"
        /\ ~abortIssued[s]
        /\ IF ScenarioCloseRelease THEN recvArmed[s] = "armed" ELSE TRUE
        /\ abortIssued' = [abortIssued EXCEPT ![s] = TRUE]
        /\ closeRequestQueue' = Append(closeRequestQueue, CloseReq(s, slotGen[s], "abort"))
        /\ eventfdPending' = TRUE
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendApp, sendPending,
                    sendHeadRemaining, sendSent, sendPin, sendReady,
                    cancelInFlight, cancelTargetEpoch, cancelFd, appCloseIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    recvResubmitQueue, acceptChannel, schedulerQueue,
                    acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    cqQueue, sqAvail, signalPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

FireAcceptRetryTimer ==
    /\ ScenarioAcceptFocused
    /\ acceptRetryTimerArmed
    /\ acceptRetryRequired
    /\ acceptRetryDue' = TRUE
    /\ acceptRetryTimerArmed' = FALSE
    /\ eventfdPending' = TRUE
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, useMultishotAccept, acceptRetryRequired,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

FireRecvRetryTimer ==
    /\ RecvRetryScenario
    /\ recvRetryTimerArmed
    /\ recvRetrySet # {}
    /\ recvRetryDue' = TRUE
    /\ recvRetryTimerArmed' = FALSE
    /\ eventfdPending' = TRUE
    /\ UNCHANGED << ConnVars, recvRetrySet, recvCancelRetrySet, cancelRetryOriginGen,
                    cancelRetryOriginEpoch, sendRetrySet, recvResubmitQueue,
                    closeRequestQueue, acceptChannel, schedulerQueue,
                    acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    cqQueue, sqAvail, signalPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars ,
                    recvRetryPassActive, recvRetryPassBlockedOnSq>>

RequestUnbind ==
    /\ LifecycleScenario
    /\ ~unbindStarted
    /\ unbindStarted' = TRUE
    /\ stopping' = TRUE
    /\ eventfdPending' = TRUE
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, useMultishotAccept, acceptRetryRequired,
                    acceptRetryDue, acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, ringFailed, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

RequestDisposeTransport ==
    /\ LifecycleScenario
    /\ ~disposeStarted
    /\ disposeStarted' = TRUE
    /\ unbindStarted' = TRUE
    /\ stopping' = TRUE
    /\ eventfdPending' = TRUE
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, ringFailed,
                    unbindTimeoutElapsed, ringDisposed, PoolVars,
                    FileVars, BugVars >>

TriggerRingFailure ==
    /\ ScenarioLifecycleFocused
    /\ ~ringFailed
    /\ ringFailed' = TRUE
    /\ stopping' = TRUE
    /\ outputCompleted' = [s \in Slots |-> IF slotState[s] # "free" THEN TRUE ELSE outputCompleted[s]]
    /\ transportAborted' = [s \in Slots |-> IF slotState[s] # "free" THEN TRUE ELSE transportAborted[s]]
    /\ sendWaitingRetry' = [s \in Slots |-> FALSE]
    /\ eventfdPending' = TRUE
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    sendLoopCompleted, recvEpoch, recvArmed, recvFdRef,
                    recvRearmPending, pendingRecv, recvSeen, recvDelivered,
                    sendInFlight, sendOpToken, sendOpParts, sendOpFd, sendApp,
                    sendPending, sendHeadRemaining, sendSent, sendPin, sendReady,
                    cancelInFlight, cancelTargetEpoch, cancelFd, appCloseIssued,
                    abortIssued, socketShutdown, socketClosed, RetryVars,
                    QueueVars, acceptArmed, acceptArmedMultishot,
                    eventfdReadArmed, cqQueue, sqAvail, signalPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, loopPhase,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

UnbindTimeoutExpires ==
    /\ ScenarioLifecycleFocused
    /\ unbindStarted
    /\ ~unbindTimeoutElapsed
    /\ ~AllSlotsFree
    /\ unbindTimeoutElapsed' = TRUE
    /\ eventfdPending' = TRUE
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, useMultishotAccept, acceptRetryRequired,
                    acceptRetryDue, acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, disposeStarted,
                    unbindStarted, ringDisposed, PoolVars, FileVars, BugVars >>

\* --- Scheduler / transport actions ---------------------------------------

LoopDrainScheduler ==
    /\ loopPhase = "top"
    /\ schedulerQueue # {}
    /\ schedulerQueue' = {}
    /\ sendReady' = sendReady \cup schedulerQueue
    /\ signalPending' = FALSE
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendApp, sendPending,
                    sendHeadRemaining, sendSent, sendPin, cancelInFlight,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    recvResubmitQueue, closeRequestQueue, acceptChannel,
                    acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    cqQueue, sqAvail, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

LoopMaybeFinishSend ==
    /\ loopPhase = "top"
    /\ \E s \in Slots :
        /\ slotState[s] # "free"
        /\ ~sendLoopCompleted[s]
        /\ outputCompleted[s]
        /\ (sendPending[s] = << >> \/ transportAborted[s])
        /\ sendInFlight[s] = "none"
        /\ ~sendWaitingRetry[s]
    /\ \E s \in Slots :
        /\ slotState[s] # "free"
        /\ ~sendLoopCompleted[s]
        /\ outputCompleted[s]
        /\ (sendPending[s] = << >> \/ transportAborted[s])
        /\ sendInFlight[s] = "none"
        /\ ~sendWaitingRetry[s]
        /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = TRUE]
        /\ closeRequestQueue' = Append(closeRequestQueue, CloseReq(s, slotGen[s], "app"))
        /\ eventfdPending' = IF ScenarioStructuralSafety \/ ScenarioStaleClose THEN eventfdPending ELSE TRUE
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, recvEpoch, recvArmed, recvFdRef,
                    recvRearmPending, pendingRecv, recvSeen, recvDelivered,
                    sendInFlight, sendOpToken, sendOpParts, sendOpFd,
                    sendWaitingRetry, sendApp, sendPending, sendHeadRemaining,
                    sendSent, sendPin, sendReady, cancelInFlight,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    recvResubmitQueue, acceptChannel, schedulerQueue,
                    signalPending, acceptArmed, acceptArmedMultishot,
                    eventfdReadArmed, cqQueue, sqAvail, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

LoopQueueUnbindClose ==
    /\ loopPhase = "top"
    /\ unbindStarted
    /\ \E s \in Slots : slotState[s] # "free" /\ ~abortIssued[s]
    /\ \E s \in Slots :
        /\ slotState[s] # "free"
        /\ ~abortIssued[s]
        /\ abortIssued' = [abortIssued EXCEPT ![s] = TRUE]
        /\ closeRequestQueue' = Append(closeRequestQueue, CloseReq(s, slotGen[s], "abort"))
        /\ eventfdPending' = TRUE
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendApp, sendPending,
                    sendHeadRemaining, sendSent, sendPin, sendReady,
                    cancelInFlight, cancelTargetEpoch, cancelFd,
                    appCloseIssued, transportAborted, socketShutdown,
                    socketClosed, RetryVars, recvResubmitQueue, acceptChannel,
                    schedulerQueue, signalPending, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, loopPhase,
                    stopping, ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, PoolVars, FileVars,
                    BugVars >>

LoopSubmitStaged ==
    /\ loopPhase = "top"
    /\ TopWorkDrained
    /\ HasStagedOps
    /\ acceptArmed' = ArmOpState(acceptArmed)
    /\ acceptArmedMultishot' = acceptArmedMultishot
    /\ eventfdReadArmed' = ArmOpState(eventfdReadArmed)
    /\ recvArmed' = ArmPerSlot(recvArmed)
    /\ sendInFlight' = ArmPerSlot(sendInFlight)
    /\ cancelInFlight' = ArmPerSlot(cancelInFlight)
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvFdRef,
                    recvRearmPending, pendingRecv, recvSeen, recvDelivered,
                    sendOpToken, sendOpParts, sendOpFd, sendWaitingRetry,
                    sendApp, sendPending, sendHeadRemaining, sendSent,
                    sendPin, sendReady, cancelTargetEpoch, cancelFd,
                    appCloseIssued, abortIssued, transportAborted,
                    socketShutdown, socketClosed, RetryVars, QueueVars,
                    cqQueue, sqAvail, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, loopPhase,
                    stopping, ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, PoolVars, FileVars,
                    BugVars >>

LoopDrainCloseHead ==
    /\ loopPhase = "top"
    /\ IF closeRequestQueue = << >>
          THEN FALSE
          ELSE LET req == Head(closeRequestQueue) IN
               /\ closeRequestQueue' = TailOrEmpty(closeRequestQueue)
               /\ IF req.slot \notin Slots \/ slotState[req.slot] = "free" \/ slotGen[req.slot] /= req.gen
                     THEN /\ UNCHANGED << slotState, outputCompleted, transportAborted,
                                          socketShutdown, sendWaitingRetry,
                                          recvRetrySet, recvCancelRetrySet ,
                                          recvRetryPassActive, recvRetryPassBlockedOnSq>>
                     ELSE /\ slotState' = [slotState EXCEPT ![req.slot] = "closing"]
                          /\ outputCompleted' = [outputCompleted EXCEPT ![req.slot] = TRUE]
                          /\ transportAborted' =
                                IF req.kind = "abort"
                                    THEN [transportAborted EXCEPT ![req.slot] = TRUE]
                                    ELSE transportAborted
                          /\ socketShutdown' =
                                IF req.kind = "abort"
                                    THEN [socketShutdown EXCEPT ![req.slot] = TRUE]
                                    ELSE socketShutdown
                          /\ sendWaitingRetry' =
                                IF req.kind = "abort"
                                    THEN [sendWaitingRetry EXCEPT ![req.slot] = FALSE]
                                    ELSE sendWaitingRetry
                          /\ recvRetrySet' = recvRetrySet \ {req.slot}
                          /\ recvCancelRetrySet' =
                                IF FixStaleCancelSetOnClose
                                    THEN recvCancelRetrySet \ {req.slot}
                                    ELSE recvCancelRetrySet
                          /\ recvRetryPassBlockedOnSq' =
                                RetryPassBlockedAfterCleanup(
                                    recvRetrySet \ {req.slot},
                                    IF FixStaleCancelSetOnClose
                                        THEN recvCancelRetrySet \ {req.slot}
                                        ELSE recvCancelRetrySet)
    /\ UNCHANGED << freeSlots, slotGen, slotAccepted, slotFd, slotFile,
                    sendLoopCompleted, recvEpoch, recvArmed, recvFdRef,
                    recvRearmPending, pendingRecv, recvSeen, recvDelivered,
                    sendInFlight, sendOpToken, sendOpParts, sendOpFd,
                    sendApp, sendPending, sendHeadRemaining, sendSent,
                    sendPin, sendReady, cancelInFlight, cancelTargetEpoch,
                    cancelFd, appCloseIssued, abortIssued, socketClosed,
                    recvRetryDue, recvRetryTimerArmed, recvRetryPassActive, cancelRetryOriginGen,
                    cancelRetryOriginEpoch, sendRetrySet, recvResubmitQueue,
                    acceptChannel, schedulerQueue, signalPending, eventfdPending,
                    acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    cqQueue, sqAvail, useMultishotAccept, acceptRetryRequired,
                    acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

LoopDrainRecvResubmitHead ==
    /\ loopPhase = "top"
    /\ IF recvResubmitQueue = << >>
          THEN FALSE
          ELSE LET req == Head(recvResubmitQueue) IN
               /\ IF req.slot \notin Slots \/ slotState[req.slot] # "open" \/ slotGen[req.slot] /= req.gen
                     THEN /\ recvResubmitQueue' = TailOrEmpty(recvResubmitQueue)
                          /\ UNCHANGED << recvRearmPending, pendingRecv, recvDelivered,
                                          recvArmed, recvEpoch, recvFdRef, sqAvail,
                                          recvRetrySet ,
                                          recvRetryPassActive, recvRetryPassBlockedOnSq>>
                          /\ UNCHANGED recvCancelRetrySet
                     ELSE IF pendingRecv[req.slot] # << >>
                         THEN LET tok == Head(pendingRecv[req.slot])
                                  rest == TailOrEmpty(pendingRecv[req.slot])
                              IN
                              /\ pendingRecv' = [pendingRecv EXCEPT ![req.slot] = rest]
                              /\ recvDelivered' = [recvDelivered EXCEPT ![req.slot] = Append(@, tok)]
                              /\ IF rest # << >>
                                    THEN /\ UNCHANGED << recvResubmitQueue, recvRearmPending,
                                                        recvArmed, recvEpoch, recvFdRef,
                                                        sqAvail, recvRetrySet ,
                                                        recvRetryPassActive, recvRetryPassBlockedOnSq>>
                                         /\ UNCHANGED recvCancelRetrySet
                                    ELSE /\ recvRearmPending' = [recvRearmPending EXCEPT ![req.slot] = FALSE]
                                         /\ IF CanSubmitRecv(req.slot)
                                               THEN /\ recvArmed' = [recvArmed EXCEPT ![req.slot] = "staged"]
                                                    /\ recvEpoch' = [recvEpoch EXCEPT ![req.slot] = @ + 1]
                                                    /\ recvFdRef' = [recvFdRef EXCEPT ![req.slot] = ConnFdRef(req.slot, slotFd, slotFile)]
                                                    /\ sqAvail' = sqAvail - 1
                                                    /\ recvRetrySet' = recvRetrySet
                                                    /\ recvCancelRetrySet' =
                                                        IF FixCancelRetryGate /\ slotGen[req.slot] = cancelRetryOriginGen[req.slot]
                                                            THEN recvCancelRetrySet \ {req.slot}
                                                            ELSE recvCancelRetrySet
                                               ELSE IF slotState[req.slot] = "open" /\ recvArmed[req.slot] = "none" /\ cancelInFlight[req.slot] = "none"
                                                   THEN /\ recvRetrySet' = recvRetrySet \cup {req.slot}
                                                        /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail >>
                                                        /\ UNCHANGED recvCancelRetrySet
                                                   ELSE /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail, recvRetrySet , recvRetryPassActive, recvRetryPassBlockedOnSq>>
                                                        /\ UNCHANGED recvCancelRetrySet
                                         /\ recvResubmitQueue' = TailOrEmpty(recvResubmitQueue)
                         ELSE /\ recvRearmPending' = [recvRearmPending EXCEPT ![req.slot] = FALSE]
                              /\ IF CanSubmitRecv(req.slot)
                                    THEN /\ recvArmed' = [recvArmed EXCEPT ![req.slot] = "staged"]
                                         /\ recvEpoch' = [recvEpoch EXCEPT ![req.slot] = @ + 1]
                                         /\ recvFdRef' = [recvFdRef EXCEPT ![req.slot] = ConnFdRef(req.slot, slotFd, slotFile)]
                                         /\ sqAvail' = sqAvail - 1
                                         /\ recvRetrySet' = recvRetrySet
                                         /\ recvCancelRetrySet' =
                                             IF FixCancelRetryGate /\ slotGen[req.slot] = cancelRetryOriginGen[req.slot]
                                                 THEN recvCancelRetrySet \ {req.slot}
                                                 ELSE recvCancelRetrySet
                                    ELSE IF slotState[req.slot] = "open" /\ recvArmed[req.slot] = "none" /\ cancelInFlight[req.slot] = "none"
                                        THEN /\ recvRetrySet' = recvRetrySet \cup {req.slot}
                                             /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail >>
                                             /\ UNCHANGED recvCancelRetrySet
                                        ELSE /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail, recvRetrySet , recvRetryPassActive, recvRetryPassBlockedOnSq>>
                                             /\ UNCHANGED recvCancelRetrySet
                              /\ recvResubmitQueue' = TailOrEmpty(recvResubmitQueue)
                              /\ UNCHANGED << pendingRecv, recvDelivered >>
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvSeen, sendInFlight,
                    sendOpToken, sendOpParts, sendOpFd, sendWaitingRetry,
                    sendApp, sendPending, sendHeadRemaining, sendSent,
                    sendPin, sendReady, cancelInFlight, cancelTargetEpoch,
                    cancelFd, appCloseIssued, abortIssued, transportAborted,
                    socketShutdown, socketClosed, recvRetryDue,
                    recvRetryTimerArmed, recvRetryPassActive, recvRetryPassBlockedOnSq,
                    recvRetryPassBlockedOnSq,
                    cancelRetryOriginGen, cancelRetryOriginEpoch,
                    sendRetrySet, closeRequestQueue, acceptChannel,
                    schedulerQueue, signalPending, eventfdPending, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, loopPhase,
                    stopping, ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, PoolVars, FileVars,
                    BugVars >>

LoopTryFinalizeClosing ==
    /\ loopPhase = "top"
    /\ \E s \in Slots : slotState[s] = "closing"
    /\ \E s \in Slots :
        /\ slotState[s] = "closing"
        /\ IF NeedShutdownForClose(s)
              THEN /\ socketShutdown' = [socketShutdown EXCEPT ![s] = TRUE]
                   /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted,
                                   slotFd, slotFile, outputCompleted,
                                   sendLoopCompleted, recvEpoch, recvArmed,
                                   recvFdRef, recvRearmPending, pendingRecv,
                                   recvSeen, recvDelivered, sendInFlight,
                                   sendOpToken, sendOpParts, sendOpFd,
                                   sendWaitingRetry, sendApp, sendPending,
                                   sendHeadRemaining, sendSent, sendPin,
                                   sendReady, cancelInFlight, cancelTargetEpoch,
                                   cancelFd, appCloseIssued, abortIssued,
                                   transportAborted, socketClosed, RetryVars,
                                   QueueVars, acceptArmed, acceptArmedMultishot,
                                   eventfdReadArmed, cqQueue, sqAvail,
                                   signalPending, eventfdPending,
                                   useMultishotAccept, acceptRetryRequired,
                                   acceptRetryDue, acceptRetryTimerArmed,
                                   eventfdReadRetryRequired, loopPhase,
                                   stopping, ringFailed, unbindStarted,
                                   disposeStarted, unbindTimeoutElapsed,
                                   ringDisposed, PoolVars, FileVars, BugVars >>
              ELSE IF CanReleaseClosing(s)
                  THEN /\ freeSlots' = Append(freeSlots, s)
                       /\ slotState' = [slotState EXCEPT ![s] = "free"]
                       /\ slotAccepted' = [slotAccepted EXCEPT ![s] = FALSE]
                       /\ slotFd' = [slotFd EXCEPT ![s] = NoFd]
                       /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
                       /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = TRUE]
                       /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
                       /\ recvFdRef' = [recvFdRef EXCEPT ![s] = NoFd]
                       /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
                       /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
                       /\ recvSeen' = [recvSeen EXCEPT ![s] = << >>]
                       /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << >>]
                       /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
                       /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
                       /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
                       /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
                       /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
                       /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
                       /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
                       /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
                       /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
                       /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
                       /\ sendReady' = sendReady \ {s}
                       /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
                       /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
                       /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
                       /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
                       /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
                       /\ socketClosed' = [socketClosed EXCEPT ![s] = TRUE]
                       /\ releasedConnFds' = IF slotFd[s] = NoFd THEN releasedConnFds ELSE releasedConnFds \cup {slotFd[s]}
                       /\ recvRetrySet' = recvRetrySet \ {s}
                       /\ recvCancelRetrySet' = IF FixStaleCancelSetOnClose THEN recvCancelRetrySet \ {s} ELSE recvCancelRetrySet
                       /\ recvRetryPassBlockedOnSq' =
                             RetryPassBlockedAfterCleanup(
                                 recvRetrySet \ {s},
                                 IF FixStaleCancelSetOnClose
                                     THEN recvCancelRetrySet \ {s}
                                     ELSE recvCancelRetrySet)
                       /\ IF slotFile[s] = NoFile
                             THEN /\ slotFile' = slotFile
                                  /\ fileSlotOwner' = fileSlotOwner
                                  /\ freeFileSlots' = freeFileSlots
                             ELSE /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
                                  /\ fileSlotOwner' = [fileSlotOwner EXCEPT ![slotFile[s]] = OwnerFree]
                                  /\ freeFileSlots' = Append(freeFileSlots, slotFile[s])
                       /\ UNCHANGED << slotGen, recvEpoch, appCloseIssued,
                                       abortIssued, recvRetryDue,
                                       recvRetryTimerArmed,
                                       recvRetryPassActive,
                                       cancelRetryOriginGen,
                                       cancelRetryOriginEpoch, sendRetrySet,
                                       recvResubmitQueue, closeRequestQueue,
                                       acceptChannel, schedulerQueue,
                                       signalPending, eventfdPending,
                                       acceptArmed, acceptArmedMultishot,
                                       eventfdReadArmed, cqQueue, sqAvail,
                                       useMultishotAccept, acceptRetryRequired,
                                       acceptRetryDue, acceptRetryTimerArmed,
                                       eventfdReadRetryRequired, loopPhase,
                                       stopping, ringFailed, unbindStarted,
                                       disposeStarted, unbindTimeoutElapsed,
                                       ringDisposed, availableConnFds,
                                       availableRecvTokens, availableSendTokens,
                                       acceptChannelDrops, nextFileSlot,
                                       BugVars >>
                  ELSE UNCHANGED Vars

LoopRunSendReady ==
    /\ loopPhase = "top"
    /\ sendReady # {}
    /\ \E s \in sendReady : TRUE
    /\ \E s \in sendReady :
        /\ sendReady' = sendReady \ {s}
        /\ IF slotState[s] = "free" \/ sendLoopCompleted[s] \/ sendPending[s] = << >> \/ sendInFlight[s] # "none" \/ sendWaitingRetry[s]
              THEN /\ UNCHANGED << sendInFlight, sendOpToken, sendOpParts,
                                  sendOpFd, sendWaitingRetry, sendPin,
                                  sendRetrySet, sqAvail ,
                                  recvRetryPassActive, recvRetryPassBlockedOnSq>>
                   /\ eventfdPending' = eventfdPending
              ELSE LET tok == Head(sendPending[s]) IN
                   IF sqAvail > 0
                       THEN /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "staged"]
                            /\ sendOpToken' = [sendOpToken EXCEPT ![s] = tok]
                            /\ sendOpParts' = [sendOpParts EXCEPT ![s] = sendHeadRemaining[s]]
                            /\ sendOpFd' = [sendOpFd EXCEPT ![s] = ConnFdRef(s, slotFd, slotFile)]
                            /\ sendPin' = [sendPin EXCEPT ![s] = IF tok \in LargeSendTokens THEN tok ELSE NoToken]
                            /\ sqAvail' = sqAvail - 1
                            /\ UNCHANGED << sendWaitingRetry, sendRetrySet , recvRetryPassActive, recvRetryPassBlockedOnSq>>
                            /\ eventfdPending' = eventfdPending
                       ELSE /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = TRUE]
                            /\ sendRetrySet' = sendRetrySet \cup {ConnRef(s, slotGen[s])}
                            /\ sendPin' = [sendPin EXCEPT ![s] = IF tok \in LargeSendTokens THEN tok ELSE NoToken]
                            /\ UNCHANGED << sendInFlight, sendOpToken, sendOpParts, sendOpFd, sqAvail >>
                            /\ eventfdPending' = IF FixSendRetryPark THEN TRUE ELSE eventfdPending
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendApp, sendPending, sendHeadRemaining,
                    sendSent, cancelInFlight, cancelTargetEpoch, cancelFd,
                    appCloseIssued, abortIssued, transportAborted,
                    socketShutdown, socketClosed, recvRetrySet, recvRetryDue,
                    recvRetryTimerArmed, recvRetryPassActive, recvRetryPassBlockedOnSq, recvCancelRetrySet,
                    cancelRetryOriginGen, cancelRetryOriginEpoch,
                    recvResubmitQueue, closeRequestQueue, acceptChannel,
                    schedulerQueue, signalPending,
                    acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    cqQueue, useMultishotAccept, acceptRetryRequired,
                    acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

LoopTopToWait ==
    /\ loopPhase = "top"
    /\ TopWorkDrained
    /\ ~HasStagedOps
    /\ loopPhase' = "wait"
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

LoopWaitDecision ==
    /\ loopPhase = "wait"
    /\ IF cqQueue # << >>
          THEN /\ loopPhase' = "process"
               /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                               acceptArmedMultishot, eventfdReadArmed, cqQueue,
                               sqAvail, signalPending, eventfdPending,
                               useMultishotAccept, acceptRetryRequired,
                               acceptRetryDue, acceptRetryTimerArmed,
                               eventfdReadRetryRequired, stopping, ringFailed,
                               unbindStarted, disposeStarted,
                               unbindTimeoutElapsed, ringDisposed, PoolVars,
                               FileVars, BugVars >>
          ELSE IF ShouldStayUnparked
              THEN /\ loopPhase' = "after"
                   /\ recvRetryPassActive' = RetryPassPending
                   /\ recvRetryPassBlockedOnSq' = FALSE
                   /\ UNCHANGED << ConnVars, recvRetrySet, recvRetryDue,
                                   recvRetryTimerArmed, recvCancelRetrySet,
                                   cancelRetryOriginGen, cancelRetryOriginEpoch,
                                   sendRetrySet, QueueVars, acceptArmed,
                                   acceptArmedMultishot, eventfdReadArmed, cqQueue,
                                   sqAvail, signalPending, eventfdPending,
                                   useMultishotAccept, acceptRetryRequired,
                                   acceptRetryDue, acceptRetryTimerArmed,
                                   eventfdReadRetryRequired, stopping, ringFailed,
                                   unbindStarted, disposeStarted,
                                   unbindTimeoutElapsed, ringDisposed, PoolVars,
                                   FileVars, BugVars >>
              ELSE /\ loopPhase' = "parked"
                   /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                                   acceptArmedMultishot, eventfdReadArmed, cqQueue,
                                   sqAvail, signalPending, eventfdPending,
                                   useMultishotAccept, acceptRetryRequired,
                                   acceptRetryDue, acceptRetryTimerArmed,
                                   eventfdReadRetryRequired, stopping, ringFailed,
                                   unbindStarted, disposeStarted,
                                   unbindTimeoutElapsed, ringDisposed, PoolVars,
                                   FileVars, BugVars >>

LoopStop ==
    /\ loopPhase \in {"top", "wait", "after", "parked"}
    /\ stopping
    /\ ~HasStagedOps
    /\ (ringFailed \/ (unbindStarted /\ (AllSlotsFree \/ unbindTimeoutElapsed \/ disposeStarted)))
    /\ loopPhase' = "stopped"
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

LoopUnpark ==
    /\ loopPhase = "parked"
    /\ cqQueue # << >>
    /\ loopPhase' = "process"
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

ProcessEventfdCqe ==
    /\ loopPhase = "process"
    /\ IF cqQueue = << >>
          THEN FALSE
          ELSE /\ Head(cqQueue).kind = "eventfd"
               /\ cqQueue' = TailOrEmpty(cqQueue)
               /\ DispatchRecord("eventfd", TRUE, TRUE)
               /\ schedulerQueue' = {}
               /\ sendReady' = sendReady \cup schedulerQueue
               /\ signalPending' = FALSE
               /\ IF ~ringDisposed /\ sqAvail > 0
                     THEN /\ eventfdReadArmed' = "staged"
                          /\ sqAvail' = sqAvail - 1
                          /\ eventfdReadRetryRequired' = FALSE
                     ELSE IF ~ringDisposed
                          THEN /\ eventfdReadRetryRequired' = TRUE
                               /\ UNCHANGED << eventfdReadArmed, sqAvail >>
                          ELSE /\ UNCHANGED << eventfdReadArmed, sqAvail, eventfdReadRetryRequired >>
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendApp, sendPending,
                    sendHeadRemaining, sendSent, sendPin, cancelInFlight,
                    cancelTargetEpoch, cancelFd, appCloseIssued, abortIssued,
                    transportAborted, socketShutdown, socketClosed, RetryVars,
                    recvResubmitQueue, closeRequestQueue, acceptChannel,
                    eventfdPending, acceptArmed, acceptArmedMultishot,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed, ringDisposed,
                    PoolVars, FileVars >>

ProcessAcceptCqe ==
    /\ loopPhase = "process"
    /\ IF cqQueue = << >>
          THEN FALSE
          ELSE LET cqe == Head(cqQueue) IN
               (/\ cqe.kind = "accept"
               /\ cqQueue' = TailOrEmpty(cqQueue)
               /\ DispatchRecord("accept", TRUE, TRUE)
               /\ (IF cqe.outcome # "fd"
                     THEN IF cqe.outcome = "einval"
                             THEN IF useMultishotAccept
                                     THEN /\ useMultishotAccept' = FALSE
                                          /\ IF ~stopping /\ sqAvail > 0
                                                THEN /\ acceptArmed' = "staged"
                                                     /\ acceptArmedMultishot' = FALSE
                                                     /\ sqAvail' = sqAvail - 1
                                                     /\ acceptRetryRequired' = FALSE
                                                     /\ acceptRetryDue' = TRUE
                                                ELSE IF ~stopping
                                                     THEN /\ acceptArmed' = "none"
                                                          /\ acceptArmedMultishot' = FALSE
                                                          /\ UNCHANGED sqAvail
                                                          /\ acceptRetryRequired' = TRUE
                                                          /\ acceptRetryDue' = TRUE
                                                     ELSE /\ acceptArmed' = "none"
                                                          /\ acceptArmedMultishot' = FALSE
                                                          /\ UNCHANGED << sqAvail, acceptRetryRequired, acceptRetryDue >>
                                          /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed,
                                                          signalPending, eventfdPending, acceptRetryTimerArmed,
                                                          eventfdReadRetryRequired, loopPhase, stopping,
                                                          ringFailed, unbindStarted, disposeStarted,
                                                          unbindTimeoutElapsed, ringDisposed, PoolVars,
                                                          FileVars >>
                                     ELSE /\ acceptRetryRequired' = TRUE
                                          /\ acceptRetryDue' = FALSE
                                          /\ acceptRetryTimerArmed' = TRUE
                                          /\ acceptArmed' = "none"
                                          /\ acceptArmedMultishot' = FALSE
                                          /\ UNCHANGED sqAvail
                                          /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed,
                                                          signalPending, eventfdPending, useMultishotAccept,
                                                          eventfdReadRetryRequired, loopPhase, stopping,
                                                          ringFailed, unbindStarted, disposeStarted,
                                                          unbindTimeoutElapsed, ringDisposed, PoolVars,
                                                          FileVars >>
                             ELSE IF cqe.outcome = "delay"
                                     THEN /\ acceptRetryRequired' = TRUE
                                          /\ acceptRetryDue' = FALSE
                                          /\ acceptRetryTimerArmed' = TRUE
                                          /\ acceptArmed' = "none"
                                          /\ acceptArmedMultishot' = FALSE
                                          /\ UNCHANGED sqAvail
                                          /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed,
                                                          signalPending, eventfdPending, useMultishotAccept,
                                                          eventfdReadRetryRequired, loopPhase, stopping,
                                                          ringFailed, unbindStarted, disposeStarted,
                                                          unbindTimeoutElapsed, ringDisposed, PoolVars,
                                                          FileVars >>
                                     ELSE IF cqe.outcome = "retry"
                                             THEN /\ IF ~stopping /\ sqAvail > 0
                                                       THEN /\ acceptArmed' = "staged"
                                                            /\ acceptArmedMultishot' = useMultishotAccept
                                                            /\ sqAvail' = sqAvail - 1
                                                            /\ acceptRetryRequired' = FALSE
                                                            /\ acceptRetryDue' = TRUE
                                                       ELSE IF ~stopping
                                                            THEN /\ acceptArmed' = "none"
                                                                 /\ acceptArmedMultishot' = FALSE
                                                                 /\ UNCHANGED sqAvail
                                                                 /\ acceptRetryRequired' = TRUE
                                                                 /\ acceptRetryDue' = TRUE
                                                            ELSE /\ acceptArmed' = "none"
                                                                 /\ acceptArmedMultishot' = FALSE
                                                                 /\ UNCHANGED << sqAvail, acceptRetryRequired, acceptRetryDue >>
                                                  /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed,
                                                                  signalPending, eventfdPending, useMultishotAccept,
                                                                  acceptRetryTimerArmed, eventfdReadRetryRequired,
                                                                  loopPhase, stopping, ringFailed, unbindStarted,
                                                                  disposeStarted, unbindTimeoutElapsed,
                                                                  ringDisposed, PoolVars, FileVars >>
                                             ELSE FALSE
                     ELSE \* accepted fd
                          IF stopping \/ freeSlots = << >>
                                THEN /\ releasedConnFds' = releasedConnFds \cup {cqe.token}
                                     /\ UNCHANGED << ConnVars, RetryVars, QueueVars,
                                                     eventfdReadArmed, sqAvail,
                                                     signalPending, eventfdPending,
                                                     useMultishotAccept, acceptRetryRequired,
                                                     acceptRetryDue, acceptRetryTimerArmed,
                                                     eventfdReadRetryRequired, loopPhase,
                                                     stopping, ringFailed, unbindStarted,
                                                     disposeStarted, unbindTimeoutElapsed,
                                                     ringDisposed, availableRecvTokens,
                                                     availableSendTokens, acceptChannelDrops,
                                                     FileVars,  acceptArmed,
                                                     acceptArmedMultishot >>
                                ELSE LET s == LastOf(freeSlots)
                                         gen == slotGen[s] + 1
                                         canQueue == Len(acceptChannel) < AcceptQueueCapacity
                                         allocFromFree == Len(freeFileSlots) > 0
                                         haveFresh == nextFileSlot <= SlotCount + 1
                                         fileSlot == IF ~UseRegisteredFiles THEN NoFile
                                                     ELSE IF allocFromFree THEN LastOf(freeFileSlots)
                                                     ELSE IF haveFresh THEN nextFileSlot
                                                     ELSE NoFile
                                         canStageRecv == canQueue /\ sqAvail > 0
                                         sqAfterRecv == sqAvail - BoolToNat(canStageRecv)
                                         keepAccept == cqe.more
                                         rearmAccept == AcceptRearmEnabled /\ ~cqe.more /\ ~stopping /\ sqAfterRecv > 0
                                     IN
                                     /\ freeSlots' = PopLast(freeSlots)
                                     /\ slotState' = [slotState EXCEPT ![s] = IF canQueue THEN "open" ELSE "closing"]
                                     /\ slotGen' = [slotGen EXCEPT ![s] = gen]
                                     /\ slotAccepted' = [slotAccepted EXCEPT ![s] = FALSE]
                                     /\ slotFd' = [slotFd EXCEPT ![s] = cqe.token]
                                     /\ slotFile' = [slotFile EXCEPT ![s] = fileSlot]
                                     /\ outputCompleted' = [outputCompleted EXCEPT ![s] = ~canQueue]
                                     /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = ~canQueue]
                                     /\ recvEpoch' = [recvEpoch EXCEPT ![s] = IF canStageRecv THEN 1 ELSE 0]
                                     /\ recvArmed' = [recvArmed EXCEPT ![s] = IF canStageRecv THEN "staged" ELSE "none"]
                                     /\ recvFdRef' = [recvFdRef EXCEPT ![s] = IF canStageRecv THEN (IF fileSlot # NoFile THEN fileSlot ELSE cqe.token) ELSE NoFd]
                                     /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
                                     /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
                                     /\ recvSeen' = [recvSeen EXCEPT ![s] = << >>]
                                     /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << >>]
                                     /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
                                     /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
                                     /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
                                     /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
                                     /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
                                     /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
                                     /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
                                     /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
                                     /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
                                     /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
                                     /\ sendReady' = sendReady \ {s}
                                     /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
                                     /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
                                     /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
                                     /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
                                     /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
                                     /\ transportAborted' = [transportAborted EXCEPT ![s] = ~canQueue]
                                     /\ socketShutdown' = [socketShutdown EXCEPT ![s] = ~canQueue]
                                     /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
                                     /\ recvRetrySet' = IF canQueue /\ ~canStageRecv THEN recvRetrySet \cup {s} ELSE recvRetrySet
                                     /\ recvCancelRetrySet' = recvCancelRetrySet
                                     /\ acceptChannel' = IF canQueue THEN Append(acceptChannel, ConnRef(s, gen)) ELSE acceptChannel
                                     /\ acceptChannelDrops' = IF canQueue THEN acceptChannelDrops ELSE acceptChannelDrops + 1
                                     /\ IF fileSlot = NoFile
                                           THEN /\ fileSlotOwner' = fileSlotOwner
                                                /\ nextFileSlot' = nextFileSlot
                                                /\ freeFileSlots' = freeFileSlots
                                           ELSE IF allocFromFree
                                                THEN /\ fileSlotOwner' = [fileSlotOwner EXCEPT ![fileSlot] = ConnOwner(s, gen)]
                                                     /\ nextFileSlot' = nextFileSlot
                                                     /\ freeFileSlots' = PopLast(freeFileSlots)
                                                ELSE /\ fileSlotOwner' = [fileSlotOwner EXCEPT ![fileSlot] = ConnOwner(s, gen)]
                                                     /\ nextFileSlot' = nextFileSlot + 1
                                                     /\ freeFileSlots' = freeFileSlots
                                     /\ acceptArmed' = IF keepAccept THEN "armed" ELSE IF rearmAccept THEN "staged" ELSE "none"
                                     /\ acceptArmedMultishot' = IF keepAccept THEN TRUE ELSE IF rearmAccept THEN useMultishotAccept ELSE FALSE
                                     /\ sqAvail' = sqAvail - BoolToNat(canStageRecv) - BoolToNat(rearmAccept)
                                     /\ acceptRetryRequired' = IF (AcceptRearmEnabled /\ ~cqe.more /\ ~stopping /\ ~rearmAccept) THEN TRUE ELSE FALSE
                                     /\ acceptRetryDue' = TRUE
                                     /\ UNCHANGED << useMultishotAccept, acceptRetryTimerArmed,
                                                     eventfdReadRetryRequired, loopPhase,
                                                     stopping, ringFailed, unbindStarted,
                                                     disposeStarted, unbindTimeoutElapsed,
                                                     ringDisposed, availableRecvTokens,
                                                     availableSendTokens, cancelRetryOriginGen,
                                                     cancelRetryOriginEpoch, sendRetrySet,
                                                     recvResubmitQueue, closeRequestQueue,
                                                     schedulerQueue, signalPending,
                                                     eventfdPending, 
                                                     recvRetryPassActive, recvRetryPassBlockedOnSq>>)
                /\ availableConnFds' = availableConnFds
                /\ recvRetryDue' = recvRetryDue
                /\ recvRetryTimerArmed' = recvRetryTimerArmed
                /\ IF cqe.outcome = "fd" /\ (stopping \/ freeSlots = << >>)
                      THEN TRUE
                      ELSE releasedConnFds' = releasedConnFds
                /\ UNCHANGED << eventfdReadArmed >>)

ProcessRecvCqe ==
    /\ loopPhase = "process"
    /\ IF cqQueue = << >>
          THEN FALSE
          ELSE LET cqe == Head(cqQueue) IN
               (/\ cqe.kind = "recv"
               /\ cqQueue' = TailOrEmpty(cqQueue)
               /\ IF cqe.slot \notin Slots \/ slotState[cqe.slot] = "free" \/ slotGen[cqe.slot] /= cqe.gen
                     THEN /\ DispatchRecord("recv", FALSE, FALSE)
                          /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                                         acceptArmedMultishot, eventfdReadArmed, sqAvail,
                                         signalPending, eventfdPending, useMultishotAccept,
                                         acceptRetryRequired, acceptRetryDue,
                                         acceptRetryTimerArmed, eventfdReadRetryRequired,
                                         loopPhase, stopping, ringFailed, unbindStarted,
                                         disposeStarted, unbindTimeoutElapsed,
                                         ringDisposed, PoolVars, FileVars >>
                     ELSE /\ DispatchRecord("recv", TRUE, TRUE)
                          /\ ProcessRecvMatched(cqe))

ProcessSendCqe ==
    /\ loopPhase = "process"
    /\ IF cqQueue = << >>
          THEN FALSE
          ELSE LET cqe == Head(cqQueue) IN
               (/\ cqe.kind = "send"
               /\ cqQueue' = TailOrEmpty(cqQueue)
               /\ IF cqe.slot \notin Slots \/ slotState[cqe.slot] = "free" \/ slotGen[cqe.slot] /= cqe.gen
                     THEN /\ DispatchRecord("send", FALSE, FALSE)
                          /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                                         acceptArmedMultishot, eventfdReadArmed, sqAvail,
                                         signalPending, eventfdPending, useMultishotAccept,
                                         acceptRetryRequired, acceptRetryDue,
                                         acceptRetryTimerArmed, eventfdReadRetryRequired,
                                         loopPhase, stopping, ringFailed, unbindStarted,
                                         disposeStarted, unbindTimeoutElapsed,
                                         ringDisposed, PoolVars, FileVars >>
                     ELSE /\ DispatchRecord("send", TRUE, TRUE)
                          /\ (IF cqe.outcome = "abort"
                                THEN /\ outputCompleted' = [outputCompleted EXCEPT ![cqe.slot] = TRUE]
                                  /\ transportAborted' = [transportAborted EXCEPT ![cqe.slot] = TRUE]
                                  /\ sendPin' = [sendPin EXCEPT ![cqe.slot] = NoToken]
                                  /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted,
                                                  slotFd, slotFile, sendLoopCompleted,
                                                  recvEpoch, recvArmed, recvFdRef,
                                                  recvRearmPending, pendingRecv, recvSeen,
                                                  recvDelivered, sendInFlight, sendOpToken,
                                                  sendOpParts, sendOpFd, sendWaitingRetry,
                                                  sendApp, sendPending, sendHeadRemaining,
                                                  sendSent, sendReady, cancelInFlight,
                                                  cancelTargetEpoch, cancelFd,
                                                  appCloseIssued, abortIssued,
                                                  socketShutdown,
                                                  socketClosed, RetryVars,
                                                  QueueVars, acceptArmed,
                                                  acceptArmedMultishot,
                                                  eventfdReadArmed, sqAvail, signalPending,
                                                  eventfdPending, useMultishotAccept,
                                                  acceptRetryRequired, acceptRetryDue,
                                                  acceptRetryTimerArmed,
                                                  eventfdReadRetryRequired, loopPhase,
                                                  stopping, ringFailed, unbindStarted,
                                                  disposeStarted, unbindTimeoutElapsed,
                                                  ringDisposed, PoolVars, FileVars >>
                             ELSE IF sendPending[cqe.slot] = << >>
                                     THEN /\ sendPin' = [sendPin EXCEPT ![cqe.slot] = NoToken]
                                          /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted,
                                                          slotFd, slotFile, outputCompleted,
                                                          sendLoopCompleted, recvEpoch, recvArmed,
                                                          recvFdRef, recvRearmPending, pendingRecv,
                                                          recvSeen, recvDelivered, sendInFlight,
                                                          sendOpToken, sendOpParts, sendOpFd,
                                                          sendWaitingRetry, sendApp, sendPending,
                                                          sendHeadRemaining, sendSent, sendReady,
                                                          cancelInFlight, cancelTargetEpoch,
                                                          cancelFd, appCloseIssued, abortIssued,
                                                          transportAborted, socketShutdown,
                                                          socketClosed, RetryVars, QueueVars,
                                                          acceptArmed, acceptArmedMultishot,
                                                          eventfdReadArmed, sqAvail, signalPending,
                                                          eventfdPending, useMultishotAccept,
                                                          acceptRetryRequired, acceptRetryDue,
                                                          acceptRetryTimerArmed,
                                                          eventfdReadRetryRequired, loopPhase,
                                                          stopping, ringFailed, unbindStarted,
                                                          disposeStarted, unbindTimeoutElapsed,
                                                          ringDisposed, PoolVars, FileVars >>
                                     ELSE LET rem == sendHeadRemaining[cqe.slot]
                                              tok == Head(sendPending[cqe.slot])
                                          IN
                                          /\ sendPin' = [sendPin EXCEPT ![cqe.slot] = NoToken]
                                          /\ IF cqe.parts < rem
                                                THEN /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![cqe.slot] = @ - cqe.parts]
                                                     /\ sendReady' = sendReady \cup {cqe.slot}
                                                     /\ UNCHANGED << sendPending, sendSent >>
                                                ELSE LET rest == TailOrEmpty(sendPending[cqe.slot]) IN
                                                     /\ sendPending' = [sendPending EXCEPT ![cqe.slot] = rest]
                                                     /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![cqe.slot] = IF rest = << >> THEN 0 ELSE SendParts(Head(rest))]
                                                     /\ sendSent' = [sendSent EXCEPT ![cqe.slot] = Append(@, tok)]
                                                     /\ sendReady' = IF rest = << >> THEN sendReady ELSE sendReady \cup {cqe.slot}
                                          /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted,
                                                          slotFd, slotFile, outputCompleted,
                                                          sendLoopCompleted, recvEpoch, recvArmed,
                                                          recvFdRef, recvRearmPending, pendingRecv,
                                                          recvSeen, recvDelivered, sendInFlight,
                                                          sendOpToken, sendOpParts, sendOpFd,
                                                          sendWaitingRetry, sendApp,
                                                          cancelInFlight, cancelTargetEpoch,
                                                          cancelFd, appCloseIssued, abortIssued,
                                                          transportAborted, socketShutdown,
                                                          socketClosed, RetryVars, QueueVars,
                                                          acceptArmed, acceptArmedMultishot,
                                                          eventfdReadArmed, sqAvail, signalPending,
                                                          eventfdPending, useMultishotAccept,
                                                          acceptRetryRequired, acceptRetryDue,
                                                          acceptRetryTimerArmed,
                                                          eventfdReadRetryRequired, loopPhase,
                                                          stopping, ringFailed, unbindStarted,
                                                          disposeStarted, unbindTimeoutElapsed,
                                                          ringDisposed, PoolVars, FileVars >>))

ProcessCancelCqe ==
    /\ loopPhase = "process"
    /\ IF cqQueue = << >>
          THEN FALSE
          ELSE LET cqe == Head(cqQueue) IN
               (/\ cqe.kind = "cancel"
               /\ cqQueue' = TailOrEmpty(cqQueue)
               /\ IF cqe.slot \notin Slots \/ slotState[cqe.slot] = "free" \/ slotGen[cqe.slot] /= cqe.gen
                     THEN /\ DispatchRecord("cancel", FALSE, FALSE)
                          /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                                         acceptArmedMultishot, eventfdReadArmed, sqAvail,
                                         signalPending, eventfdPending, useMultishotAccept,
                                         acceptRetryRequired, acceptRetryDue,
                                         acceptRetryTimerArmed, eventfdReadRetryRequired,
                                         loopPhase, stopping, ringFailed, unbindStarted,
                                         disposeStarted, unbindTimeoutElapsed,
                                         ringDisposed, PoolVars, FileVars >>
                     ELSE /\ DispatchRecord("cancel", TRUE, TRUE)
                          /\ (IF UseBufferRing /\ slotState[cqe.slot] = "open" /\ ~recvRearmPending[cqe.slot] /\ recvArmed[cqe.slot] = "none" /\ cancelInFlight[cqe.slot] = "none" /\ recvEpoch[cqe.slot] < MaxRecvEpoch /\ sqAvail > 0
                               THEN /\ recvArmed' = [recvArmed EXCEPT ![cqe.slot] = "staged"]
                                    /\ recvEpoch' = [recvEpoch EXCEPT ![cqe.slot] = @ + 1]
                                    /\ recvFdRef' = [recvFdRef EXCEPT ![cqe.slot] = ConnFdRef(cqe.slot, slotFd, slotFile)]
                                    /\ sqAvail' = sqAvail - 1
                                    /\ recvRetrySet' = recvRetrySet
                               ELSE IF UseBufferRing /\ slotState[cqe.slot] = "open" /\ ~recvRearmPending[cqe.slot] /\ recvArmed[cqe.slot] = "none" /\ cancelInFlight[cqe.slot] = "none"
                                    THEN /\ recvRetrySet' = recvRetrySet \cup {cqe.slot}
                                         /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail >>
                                    ELSE /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail, recvRetrySet , recvRetryPassActive, recvRetryPassBlockedOnSq>>)
                  /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted,
                                  slotFd, slotFile, outputCompleted,
                                  sendLoopCompleted, recvRearmPending,
                                  pendingRecv, recvSeen, recvDelivered,
                                  sendInFlight, sendOpToken, sendOpParts,
                                  sendOpFd, sendWaitingRetry, sendApp,
                                  sendPending, sendHeadRemaining, sendSent,
                                  sendPin, sendReady, cancelInFlight,
                                  cancelTargetEpoch, cancelFd,
                                  appCloseIssued, abortIssued,
                                  transportAborted, socketShutdown,
                                  socketClosed, recvRetryDue,
                                  recvRetryTimerArmed, recvRetryPassActive, recvRetryPassBlockedOnSq, recvCancelRetrySet,
                                  cancelRetryOriginGen,
                                  cancelRetryOriginEpoch, sendRetrySet,
                                  recvResubmitQueue, closeRequestQueue,
                                  acceptChannel, schedulerQueue,
                                  acceptArmed, acceptArmedMultishot,
                                  eventfdReadArmed, signalPending,
                                  eventfdPending, useMultishotAccept,
                                  acceptRetryRequired, acceptRetryDue,
                                  acceptRetryTimerArmed,
                                  eventfdReadRetryRequired, loopPhase,
                                  stopping, ringFailed, unbindStarted,
                                  disposeStarted, unbindTimeoutElapsed,
                                  ringDisposed, PoolVars, FileVars >>)

ProcessToAfter ==
    /\ loopPhase = "process"
    /\ cqQueue = << >>
    /\ loopPhase' = "after"
    /\ recvRetryPassActive' = RetryPassPending
    /\ recvRetryPassBlockedOnSq' = FALSE
    /\ UNCHANGED << ConnVars, recvRetrySet, recvRetryDue, recvRetryTimerArmed,
                    recvCancelRetrySet, cancelRetryOriginGen,
                    cancelRetryOriginEpoch, sendRetrySet, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

CanProcessRecvRetry(s) ==
    \/ slotState[s] # "open"
    \/ recvArmed[s] # "none"
    \/ cancelInFlight[s] # "none"
    \/ recvEpoch[s] = MaxRecvEpoch
    \/ (slotState[s] = "open" /\ recvArmed[s] = "none" /\ cancelInFlight[s] = "none" /\ recvEpoch[s] < MaxRecvEpoch /\ ~recvRetryPassBlockedOnSq)

HasRetryRecvWork ==
    recvRetryPassActive /\ \E s \in recvRetrySet : CanProcessRecvRetry(s)

CanProcessCancelRetry(s) ==
    \/ slotState[s] # "open"
    \/ recvArmed[s] = "none"
    \/ (FixCancelRetryGate /\ (slotGen[s] # cancelRetryOriginGen[s] \/ recvEpoch[s] # cancelRetryOriginEpoch[s]))
    \/ (cancelInFlight[s] = "none" /\ ~recvRetryPassBlockedOnSq)

HasRetryCancelWork ==
    recvRetryPassActive /\ ~HasRetryRecvWork /\ \E s \in recvCancelRetrySet : CanProcessCancelRetry(s)

CanAfterRetryRecv ==
    HasRetryRecvWork

CanAfterRetryCancel ==
    HasRetryCancelWork

CanAfterRetryAccept ==
    /\ (~recvRetryPassActive \/ (~HasRetryRecvWork /\ ~HasRetryCancelWork))
    /\ acceptRetryRequired /\ acceptRetryDue /\ ~stopping /\ acceptArmed = "none" /\ sqAvail > 0

CanAfterRetryEventfd ==
    /\ (~recvRetryPassActive \/ (~HasRetryRecvWork /\ ~HasRetryCancelWork))
    /\ eventfdReadRetryRequired /\ ~ringDisposed /\ eventfdReadArmed = "none" /\ sqAvail > 0

AfterRetryRecvOne ==
    /\ loopPhase = "after"
    /\ CanAfterRetryRecv
    /\ \E s \in recvRetrySet :
        /\ CanProcessRecvRetry(s)
        /\ IF slotState[s] # "open" \/ recvArmed[s] # "none" \/ cancelInFlight[s] # "none" \/ recvEpoch[s] = MaxRecvEpoch
              THEN /\ recvRetrySet' = recvRetrySet \ {s}
                   /\ UNCHANGED << recvArmed, recvEpoch, recvFdRef, sqAvail >>
                   /\ UNCHANGED recvCancelRetrySet
                   /\ recvRetryPassBlockedOnSq' = recvRetryPassBlockedOnSq
              ELSE IF sqAvail > 0
                   THEN /\ recvRetrySet' = recvRetrySet \ {s}
                        /\ recvArmed' = [recvArmed EXCEPT ![s] = "staged"]
                        /\ recvEpoch' = [recvEpoch EXCEPT ![s] = @ + 1]
                        /\ recvFdRef' = [recvFdRef EXCEPT ![s] = ConnFdRef(s, slotFd, slotFile)]
                        /\ sqAvail' = sqAvail - 1
                        /\ recvCancelRetrySet' =
                             IF FixCancelRetryGate /\ slotGen[s] = cancelRetryOriginGen[s]
                                 THEN recvCancelRetrySet \ {s}
                                 ELSE recvCancelRetrySet
                        /\ recvRetryPassBlockedOnSq' = recvRetryPassBlockedOnSq
                   ELSE /\ UNCHANGED << recvRetrySet, recvArmed, recvEpoch, recvFdRef, sqAvail, recvCancelRetrySet >>
                        /\ recvRetryPassBlockedOnSq' = TRUE
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvRearmPending,
                    pendingRecv, recvSeen, recvDelivered, sendInFlight,
                    sendOpToken, sendOpParts, sendOpFd, sendWaitingRetry,
                    sendApp, sendPending, sendHeadRemaining, sendSent,
                    sendPin, sendReady, cancelInFlight, cancelTargetEpoch,
                    cancelFd,                     appCloseIssued, abortIssued, transportAborted,
                    socketShutdown, socketClosed, recvRetryDue,
                    recvRetryTimerArmed, recvRetryPassActive,
                    cancelRetryOriginGen, cancelRetryOriginEpoch,
                    sendRetrySet, recvResubmitQueue, closeRequestQueue,
                    acceptChannel, schedulerQueue, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, cqQueue,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

AfterRetryCancelOne ==
    /\ loopPhase = "after"
    /\ CanAfterRetryCancel
    /\ \E s \in recvCancelRetrySet :
        /\ CanProcessCancelRetry(s)
        /\ IF slotState[s] # "open" \/ recvArmed[s] = "none"
              THEN /\ recvCancelRetrySet' = recvCancelRetrySet \ {s}
                   /\ UNCHANGED << cancelInFlight, cancelTargetEpoch, cancelFd,
                                   sqAvail >>
                   /\ recvRetryPassBlockedOnSq' = recvRetryPassBlockedOnSq
              ELSE IF FixCancelRetryGate /\ (slotGen[s] # cancelRetryOriginGen[s] \/ recvEpoch[s] # cancelRetryOriginEpoch[s])
                   THEN /\ recvCancelRetrySet' = recvCancelRetrySet \ {s}
                        /\ UNCHANGED << cancelInFlight, cancelTargetEpoch, cancelFd,
                                        sqAvail >>
                        /\ recvRetryPassBlockedOnSq' = recvRetryPassBlockedOnSq
                   ELSE IF sqAvail > 0
                        THEN /\ recvCancelRetrySet' = recvCancelRetrySet \ {s}
                             /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "staged"]
                             /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = recvEpoch[s]]
                             /\ cancelFd' = [cancelFd EXCEPT ![s] = ConnFdRef(s, slotFd, slotFile)]
                             /\ sqAvail' = sqAvail - 1
                             /\ recvRetryPassBlockedOnSq' = recvRetryPassBlockedOnSq
                        ELSE /\ UNCHANGED << recvCancelRetrySet, cancelInFlight, cancelTargetEpoch, cancelFd, sqAvail >>
                             /\ recvRetryPassBlockedOnSq' = TRUE
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendWaitingRetry, sendApp, sendPending,
                    sendHeadRemaining, sendSent, sendPin, sendReady,
                    appCloseIssued, abortIssued, transportAborted,
                    socketShutdown, socketClosed, recvRetrySet, recvRetryDue,
                    recvRetryTimerArmed, recvRetryPassActive,
                    cancelRetryOriginGen,
                    cancelRetryOriginEpoch, sendRetrySet, recvResubmitQueue,
                    closeRequestQueue, acceptChannel, schedulerQueue,
                    acceptArmed, acceptArmedMultishot, eventfdReadArmed,
                    cqQueue, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, unbindStarted,
                    disposeStarted, unbindTimeoutElapsed, ringDisposed,
                    PoolVars, FileVars, BugVars >>

AfterRetryAccept ==
    /\ loopPhase = "after"
    /\ CanAfterRetryAccept
    /\ acceptArmed' = "staged"
    /\ acceptArmedMultishot' = useMultishotAccept
    /\ sqAvail' = sqAvail - 1
    /\ acceptRetryRequired' = FALSE
    /\ acceptRetryDue' = TRUE
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed,
                    cqQueue, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

AfterRetryEventfd ==
    /\ loopPhase = "after"
    /\ CanAfterRetryEventfd
    /\ eventfdReadArmed' = "staged"
    /\ eventfdReadRetryRequired' = FALSE
    /\ sqAvail' = sqAvail - 1
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, cqQueue, signalPending,
                    eventfdPending, useMultishotAccept, acceptRetryRequired,
                    acceptRetryDue, acceptRetryTimerArmed, loopPhase, stopping,
                    ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, PoolVars, FileVars,
                    BugVars >>

AfterResumeSend ==
    /\ loopPhase = "after"
    /\ ~CanAfterRetryRecv
    /\ ~CanAfterRetryCancel
    /\ ~CanAfterRetryAccept
    /\ ~CanAfterRetryEventfd
    /\ recvRetryPassActive' = FALSE
    /\ LET resumed == { s \in Slots : ConnRef(s, slotGen[s]) \in sendRetrySet /\ slotState[s] # "free" /\ sendWaitingRetry[s] } IN
       /\ sendReady' = sendReady \cup resumed
       /\ sendWaitingRetry' = [s \in Slots |-> IF s \in resumed THEN FALSE ELSE sendWaitingRetry[s]]
       /\ sendRetrySet' = {}
       /\ loopPhase' = IF unbindStarted /\ (ringFailed \/ AllSlotsFree \/ unbindTimeoutElapsed) THEN "stopped" ELSE "top"
    /\ UNCHANGED << freeSlots, slotState, slotGen, slotAccepted, slotFd, slotFile,
                    outputCompleted, sendLoopCompleted, recvEpoch, recvArmed,
                    recvFdRef, recvRearmPending, pendingRecv, recvSeen,
                    recvDelivered, sendInFlight, sendOpToken, sendOpParts,
                    sendOpFd, sendApp, sendPending, sendHeadRemaining,
                    sendSent, sendPin, cancelInFlight, cancelTargetEpoch,
                    cancelFd, appCloseIssued, abortIssued, transportAborted,
                    socketShutdown, socketClosed, recvRetrySet, recvRetryDue,
                    recvRetryTimerArmed, recvRetryPassBlockedOnSq, recvCancelRetrySet,
                    cancelRetryOriginGen, cancelRetryOriginEpoch,
                    recvResubmitQueue, closeRequestQueue, acceptChannel,
                    schedulerQueue, acceptArmed, acceptArmedMultishot,
                    eventfdReadArmed, cqQueue, sqAvail, signalPending,
                    eventfdPending, useMultishotAccept, acceptRetryRequired,
                    acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, PoolVars, FileVars, BugVars >>

\* Source traceability: after UnbindAsync observes _ioLoopStopped, DisposeAsync
\* runs one bounded final-drain step before Ring.Dispose closes the ring. This
\* snapshot accounts only for CQEs visible/materialized at that boundary; any
\* later completion stays outside this proof, and a post-snapshot accepted-fd
\* CQE is not reclaimed by this model's ring-close step.
DisposeDrainBeforeRingClose ==
    /\ disposeStarted
    /\ loopPhase = "stopped"
    /\ ~ringDisposed
    /\ FixDisposeAcceptedCqeLeak
    /\ ~disposeDrainCompleted
    /\ cqQueue' = << >>
    /\ releasedConnFds' = releasedConnFds \cup AcceptCqeFdSet(cqQueue)
    /\ disposeDrainCompleted' = TRUE
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, unbindStarted,
                    disposeStarted, unbindTimeoutElapsed, ringDisposed,
                    availableConnFds, availableRecvTokens, availableSendTokens,
                    acceptChannelDrops, FileVars, disposeOverflowBacklog,
                    disposeOverflowPreDrainEnterCount, dispatchLastKind,
                    dispatchLastMatched, dispatchLastApplied >>

DisposeCloseRing ==
    /\ disposeStarted
    /\ loopPhase = "stopped"
    /\ ~ringDisposed
    /\ (~FixDisposeAcceptedCqeLeak \/ disposeDrainCompleted)
    /\ ringDisposed' = TRUE
    /\ freeSlots' = InitFreeSlots
    /\ slotState' = [s \in Slots |-> "free"]
    /\ slotAccepted' = [s \in Slots |-> FALSE]
    /\ slotFd' = [s \in Slots |-> NoFd]
    /\ slotFile' = [s \in Slots |-> NoFile]
    /\ outputCompleted' = [s \in Slots |-> FALSE]
    /\ sendLoopCompleted' = [s \in Slots |-> TRUE]
    /\ recvArmed' = [s \in Slots |-> "none"]
    /\ recvFdRef' = [s \in Slots |-> NoFd]
    /\ recvRearmPending' = [s \in Slots |-> FALSE]
    /\ pendingRecv' = [s \in Slots |-> << >>]
    /\ recvSeen' = [s \in Slots |-> << >>]
    /\ recvDelivered' = [s \in Slots |-> << >>]
    /\ sendInFlight' = [s \in Slots |-> "none"]
    /\ sendOpToken' = [s \in Slots |-> NoToken]
    /\ sendOpParts' = [s \in Slots |-> 0]
    /\ sendOpFd' = [s \in Slots |-> NoFd]
    /\ sendWaitingRetry' = [s \in Slots |-> FALSE]
    /\ sendApp' = [s \in Slots |-> << >>]
    /\ sendPending' = [s \in Slots |-> << >>]
    /\ sendHeadRemaining' = [s \in Slots |-> 0]
    /\ sendSent' = [s \in Slots |-> << >>]
    /\ sendPin' = [s \in Slots |-> NoToken]
    /\ sendReady' = {}
    /\ cancelInFlight' = [s \in Slots |-> "none"]
    /\ cancelTargetEpoch' = [s \in Slots |-> 0]
    /\ cancelFd' = [s \in Slots |-> NoFd]
    /\ transportAborted' = [s \in Slots |-> FALSE]
    /\ socketShutdown' = [s \in Slots |-> FALSE]
    /\ socketClosed' = [s \in Slots |-> TRUE]
    /\ recvRetrySet' = {}
    /\ recvRetryPassActive' = FALSE
    /\ recvRetryPassBlockedOnSq' = FALSE
    /\ recvCancelRetrySet' = {}
    /\ sendRetrySet' = {}
    /\ recvResubmitQueue' = << >>
    /\ closeRequestQueue' = << >>
    /\ acceptChannel' = << >>
    /\ schedulerQueue' = {}
    /\ signalPending' = FALSE
    /\ eventfdPending' = FALSE
    /\ acceptArmed' = "none"
    /\ acceptArmedMultishot' = FALSE
    /\ eventfdReadArmed' = "none"
    /\ cqQueue' = << >>
    /\ disposeOverflowBacklog' = << >>
    /\ disposeOverflowTrackedFds' = disposeOverflowTrackedFds
    /\ disposeOverflowPreDrainEnterCount' = 0
    /\ sqAvail' = SqCapacity
    /\ eventfdReadRetryRequired' = FALSE
    /\ fileSlotOwner' = [f \in AllFileSlots |-> OwnerFree]
    /\ freeFileSlots' = << >>
    /\ releasedConnFds' =
        releasedConnFds
            \cup LiveConnFdSet(slotState, slotFd)
    /\ UNCHANGED << slotGen, recvEpoch, appCloseIssued, abortIssued,
                    recvRetryDue, recvRetryTimerArmed,
                    cancelRetryOriginGen, cancelRetryOriginEpoch,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    availableConnFds, availableRecvTokens, availableSendTokens,
                    acceptChannelDrops, nextFileSlot, disposeDrainCompleted,
                    dispatchLastKind, dispatchLastMatched, dispatchLastApplied >>

SchedulerStep ==
    /\ (\/ LoopDrainScheduler
        \/ LoopMaybeFinishSend
        \/ LoopQueueUnbindClose
        \/ LoopSubmitStaged
        \/ LoopDrainCloseHead
        \/ LoopDrainRecvResubmitHead
        \/ LoopTryFinalizeClosing
        \/ LoopRunSendReady
        \/ LoopTopToWait
        \/ LoopWaitDecision
        \/ LoopUnpark
        \/ ProcessEventfdCqe
        \/ ProcessAcceptCqe
        \/ ProcessRecvCqe
        \/ ProcessSendCqe
        \/ ProcessCancelCqe
        \/ ProcessToAfter
        \/ AfterRetryRecvOne
        \/ AfterRetryCancelOne
        \/ AfterRetryAccept
        \/ AfterRetryEventfd
        \/ AfterResumeSend
        \/ LoopStop
        \/ DisposeDrainBeforeRingClose
        \/ DisposeCloseRing)
    /\ OverflowVarsUnchanged

KernelCompleteAny ==
    \* Assumption 10: final-dispose teardown freezes new kernel completions once
    \* the bounded snapshot has been taken.
    /\ ~disposeDrainCompleted
    /\ (\/ KernelCompleteAcceptFd
        \/ KernelCompleteAcceptUnsupported
        \/ KernelCompleteAcceptDelayedError
        \/ KernelCompleteAcceptTransientError
        \/ KernelCompleteEventfd
        \/ KernelCompleteRecvData
        \/ KernelCompleteRecvEnobufs
        \/ KernelCompleteRecvTerminal
        \/ KernelCompleteRecvCanceled
        \/ KernelCompleteSend
        \/ KernelCompleteCancelControl)
    /\ OverflowVarsUnchanged

AppDrainAny ==
    /\ (AppDrainFlushSuccess \/ AppDrainFlushClose)
    /\ OverflowVarsUnchanged
TimerFireAny ==
    /\ (FireAcceptRetryTimer \/ FireRecvRetryTimer \/ UnbindTimeoutExpires)
    /\ OverflowVarsUnchanged

\* Atomic bounded-prefix abstraction for the due-now accept-retry park hazard.
\* It summarizes KernelCompleteAcceptFd ; LoopTopToWait ; LoopWaitDecision so
\* the focused body still executes the real ProcessAcceptCqe rearm decision,
\* the real accept-retry marker transition, and the real LoopWaitDecision that
\* can park before AfterRetryAccept once SQ space returns.
SetupAcceptRetryParkReady ==
    /\ ScenarioAcceptFocused
    /\ ~AcceptRetryParkSetupDone
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E fd \in availableConnFds :
        /\ acceptArmed' = "none"
        /\ acceptArmedMultishot' = FALSE
        /\ cqQueue' = << Cqe("accept", NoSlot, 0, 0, "fd", FALSE, fd, 0) >>
        /\ loopPhase' = "process"
        /\ availableConnFds' = availableConnFds \ {fd}
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, availableRecvTokens, availableSendTokens,
                    releasedConnFds, acceptChannelDrops, FileVars, BugVars >>

\* Atomic bounded-prefix abstraction for the final-dispose accepted-CQE leak.
\* It summarizes KernelCompleteAcceptFd into an all-slots-free loop-idle state
\* so the focused body still executes the real RequestDisposeTransport,
\* LoopStop, DisposeDrainBeforeRingClose, and DisposeCloseRing teardown
\* against a pending accepted-fd CQE.
SetupDisposeAcceptedCqeLeakReady ==
    /\ ScenarioDisposeAcceptedCqeFocused
    /\ ~DisposeAcceptedCqeFocusedSetupDone
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E fd \in availableConnFds :
        /\ acceptArmed' = "none"
        /\ acceptArmedMultishot' = FALSE
        /\ cqQueue' = << Cqe("accept", NoSlot, 0, 0, "fd", FALSE, fd, 0) >>
        /\ availableConnFds' = availableConnFds \ {fd}
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, availableRecvTokens, availableSendTokens,
                    releasedConnFds, acceptChannelDrops, FileVars, BugVars >>

\* Atomic bounded-prefix abstraction for the NODROP-overflow teardown gap.
\* It summarizes a post-stop boundary where one accepted-fd CQE is already
\* visible in cqQueue and an additional bounded hidden overflow-list backlog of
\* accepted fds is already kernel-owned but not yet visible in cqQueue. The
\* focused witness allows up to the bounded round cap plus one documented
\* residual hidden item for the separate cap-exhaustion config; it still does
\* not claim any unbounded recovery or any unrelated visible-only completion
\* bursts after the boundary.
SetupDisposeAcceptedOverflowReady ==
    /\ ScenarioDisposeAcceptedOverflowFocused
    /\ ~DisposeAcceptedOverflowFocusedSetupDone
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ disposeOverflowBacklog = << >>
    /\ disposeOverflowTrackedFds = {}
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ \E fdVisible \in availableConnFds :
        \E fdHiddenSeq \in DistinctNonEmptySeqs(
            availableConnFds \ {fdVisible},
            DisposeOverflowHiddenBacklogModelLimit) :
        /\ acceptArmed' = "none"
        /\ acceptArmedMultishot' = FALSE
        /\ cqQueue' = << Cqe("accept", NoSlot, 0, 0, "fd", FALSE, fdVisible, 0) >>
        /\ disposeOverflowBacklog' = AcceptCqeSeq(fdHiddenSeq)
        /\ disposeOverflowTrackedFds' = {fdVisible} \cup SeqToSet(fdHiddenSeq)
        /\ availableConnFds' = availableConnFds \ ({fdVisible} \cup SeqToSet(fdHiddenSeq))
    /\ disposeOverflowPreDrainEnterCount' = 0
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, loopPhase, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, availableRecvTokens, availableSendTokens,
                    releasedConnFds, acceptChannelDrops,
                    fileSlotOwner, nextFileSlot, freeFileSlots,
                    disposeDrainCompleted, dispatchLastKind,
                    dispatchLastMatched, dispatchLastApplied >>

\* Atomic bounded-prefix abstraction for the due-now retry-pass park hazard.
\* It summarizes a reachable one-connection receive-completion state where the
\* positive recv CQE is ready to process, the only SQ slot is already consumed,
\* and no async flush or eventfd wake remains to rescue the loop. The focused
\* body still executes the real ProcessRecvCqe SQ-full rearm miss, enters the
\* retry pass, records recvRetryPassBlockedOnSq only after the real
\* AfterRetryRecvOne submit attempt loses to SQ-full, regains SQ space, and
\* then traverses the real LoopWaitDecision that can park before the next
\* retry pass or LoopSubmitStaged run.
SetupRetryPassParkReady ==
    /\ ScenarioReceiveLiveness
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ availableRecvTokens # {}
    /\ \E fd \in availableConnFds, tok \in availableRecvTokens :
        LET s == LastOf(freeSlots)
        IN /\ freeSlots' = PopLast(freeSlots)
           /\ slotState' = [slotState EXCEPT ![s] = "open"]
           /\ slotGen' = [slotGen EXCEPT ![s] = 1]
           /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
           /\ slotFd' = [slotFd EXCEPT ![s] = fd]
           /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
           /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
           /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
           /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 1]
           /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
           /\ recvFdRef' = [recvFdRef EXCEPT ![s] = fd]
           /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
           /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
           /\ recvSeen' = [recvSeen EXCEPT ![s] = << >>]
           /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << >>]
           /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
           /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
           /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
           /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
           /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
           /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
           /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
           /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
           /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
           /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
           /\ sendReady' = {}
           /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
           /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
           /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
           /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
           /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
           /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
           /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
           /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
           /\ recvRetrySet' = {}
           /\ recvRetryDue' = TRUE
           /\ recvRetryTimerArmed' = FALSE
           /\ recvRetryPassActive' = FALSE
           /\ recvRetryPassBlockedOnSq' = FALSE
           /\ recvCancelRetrySet' = {}
           /\ cancelRetryOriginGen' = cancelRetryOriginGen
           /\ cancelRetryOriginEpoch' = cancelRetryOriginEpoch
           /\ sendRetrySet' = {}
           /\ recvResubmitQueue' = << >>
           /\ closeRequestQueue' = << >>
           /\ acceptChannel' = << >>
           /\ schedulerQueue' = {}
           /\ signalPending' = FALSE
           /\ eventfdPending' = FALSE
           /\ acceptArmed' = "none"
           /\ acceptArmedMultishot' = FALSE
           /\ eventfdReadArmed' = "armed"
           /\ cqQueue' = << Cqe("recv", s, 1, 1, "sync", FALSE, tok, 0) >>
           /\ sqAvail' = 0
           /\ acceptRetryRequired' = FALSE
           /\ acceptRetryDue' = TRUE
           /\ acceptRetryTimerArmed' = FALSE
           /\ eventfdReadRetryRequired' = FALSE
           /\ loopPhase' = "process"
           /\ availableConnFds' = availableConnFds \ {fd}
           /\ availableRecvTokens' = availableRecvTokens \ {tok}
           /\ availableSendTokens' = availableSendTokens
    /\ UNCHANGED << useMultishotAccept, stopping, ringFailed, unbindStarted,
                    disposeStarted, unbindTimeoutElapsed, ringDisposed,
                    releasedConnFds, acceptChannelDrops, fileSlotOwner,
                    nextFileSlot, freeFileSlots, BugVars >>

\* SetupAcceptedRecvAwaitingAcceptRetry refines the bounded source prefix
\*   KernelCompleteAcceptFd ; ProcessAcceptCqe ; AppAcceptConnection ; ProcessToAfter ;
\*   AfterResumeSend ; LoopSubmitStaged
\* for the single accepted/open connection case where the accepted recv consumes
\* the only SQ slot, so accept rearm is left pending for the normal retry pass.
\* This keeps the receive/close/reuse witnesses on reachable source-like states
\* while preserving SlotPartition and fixed-file ownership.
SetupAcceptedRecvAwaitingAcceptRetry ==
    /\ ScenarioRecvFocused \/ ScenarioStructuralSafety \/ ScenarioStaleClose
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E fd \in availableConnFds :
       LET s == LastOf(freeSlots)
           gen == slotGen[s] + 1
           allocFromFree == Len(freeFileSlots) > 0
           haveFresh == nextFileSlot <= SlotCount + 1
           fileSlot ==
               IF ~UseRegisteredFiles
                   THEN NoFile
                   ELSE IF allocFromFree
                       THEN LastOf(freeFileSlots)
                       ELSE IF haveFresh
                           THEN nextFileSlot
                           ELSE NoFile
           fdRef == IF fileSlot # NoFile THEN fileSlot ELSE fd
       IN /\ ~UseRegisteredFiles \/ allocFromFree \/ haveFresh
          /\ freeSlots' = PopLast(freeSlots)
          /\ slotState' = [slotState EXCEPT ![s] = "open"]
          /\ slotGen' = [slotGen EXCEPT ![s] = gen]
          /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
          /\ slotFd' = [slotFd EXCEPT ![s] = fd]
          /\ slotFile' = [slotFile EXCEPT ![s] = fileSlot]
          /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
          /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
          /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 1]
          /\ recvArmed' = [recvArmed EXCEPT ![s] = "armed"]
          /\ recvFdRef' = [recvFdRef EXCEPT ![s] = fdRef]
          /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
          /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
          /\ recvSeen' = [recvSeen EXCEPT ![s] = << >>]
          /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << >>]
          /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
          /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
          /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
          /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
          /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
          /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
          /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
          /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
          /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
          /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
          /\ sendReady' = {}
          /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
          /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
          /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
          /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
          /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
          /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
          /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
          /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
          /\ sqAvail' = 0
          /\ acceptArmed' = "none"
          /\ acceptArmedMultishot' = FALSE
          /\ eventfdReadArmed' = eventfdReadArmed
          /\ cqQueue' = << >>
          /\ recvResubmitQueue' = << >>
          /\ closeRequestQueue' = << >>
          /\ acceptChannel' = << >>
          /\ schedulerQueue' = {}
          /\ loopPhase' = "top"
          /\ availableConnFds' = availableConnFds \ {fd}
          /\ acceptRetryRequired' = TRUE
          /\ acceptRetryDue' = TRUE
          /\ IF fileSlot = NoFile
                THEN /\ fileSlotOwner' = fileSlotOwner
                     /\ nextFileSlot' = nextFileSlot
                     /\ freeFileSlots' = freeFileSlots
                ELSE IF allocFromFree
                    THEN /\ fileSlotOwner' = [fileSlotOwner EXCEPT ![fileSlot] = ConnOwner(s, gen)]
                         /\ nextFileSlot' = nextFileSlot
                         /\ freeFileSlots' = PopLast(freeFileSlots)
                    ELSE /\ fileSlotOwner' = [fileSlotOwner EXCEPT ![fileSlot] = ConnOwner(s, gen)]
                         /\ nextFileSlot' = nextFileSlot + 1
                         /\ freeFileSlots' = freeFileSlots
    /\ UNCHANGED << recvRetrySet, recvRetryDue, recvRetryTimerArmed,
                    recvRetryPassActive, recvRetryPassBlockedOnSq, recvCancelRetrySet,
                    cancelRetryOriginGen, cancelRetryOriginEpoch, sendRetrySet,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, stopping,
                    ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, availableRecvTokens,
                    availableSendTokens, releasedConnFds, acceptChannelDrops,
                    BugVars >>

SetupReceiveRearmPending ==
    /\ ScenarioReceiveLiveness \/ ScenarioRecvFocused
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E firstTok \in availableRecvTokens :
        \E secondTok \in availableRecvTokens \ {firstTok},
           fd \in availableConnFds :
            LET s == LastOf(freeSlots)
                gen == slotGen[s] + 1
            IN /\ freeSlots' = PopLast(freeSlots)
               /\ slotState' = [slotState EXCEPT ![s] = "open"]
               /\ slotGen' = [slotGen EXCEPT ![s] = gen]
               /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
               /\ slotFd' = [slotFd EXCEPT ![s] = fd]
               /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
               /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
               /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
               /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 1]
               /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
               /\ recvFdRef' = [recvFdRef EXCEPT ![s] = fd]
               /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = TRUE]
               /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << secondTok >>]
               /\ recvSeen' = [recvSeen EXCEPT ![s] = << firstTok, secondTok >>]
               /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << firstTok >>]
               /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
               /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
               /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
               /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
               /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
               /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
               /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
               /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
               /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
               /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
               /\ sendReady' = {}
               /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
               /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
               /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
               /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
               /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
               /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
               /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
               /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
               /\ sqAvail' = 0
               /\ recvRetrySet' = {}
               /\ recvRetryDue' = TRUE
               /\ recvRetryTimerArmed' = FALSE
               /\ recvRetryPassActive' = FALSE
               /\ recvRetryPassBlockedOnSq' = FALSE
               /\ recvCancelRetrySet' = {s}
               /\ cancelRetryOriginGen' = [cancelRetryOriginGen EXCEPT ![s] = gen]
               /\ cancelRetryOriginEpoch' = [cancelRetryOriginEpoch EXCEPT ![s] = 1]
               /\ sendRetrySet' = {}
               /\ acceptArmed' = "none"
               /\ acceptArmedMultishot' = FALSE
               /\ eventfdReadArmed' = eventfdReadArmed
               /\ cqQueue' = << >>
               /\ loopPhase' = "top"
               /\ availableConnFds' = availableConnFds \ {fd}
               /\ availableRecvTokens' = availableRecvTokens \ {firstTok, secondTok}
               /\ availableSendTokens' = availableSendTokens
    /\ UNCHANGED << recvResubmitQueue, closeRequestQueue, acceptChannel,
                    schedulerQueue, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, stopping,
                    ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, releasedConnFds,
                    acceptChannelDrops, fileSlotOwner, nextFileSlot,
                    freeFileSlots, BugVars >>

SetupReceiveRetryPassReady ==
    /\ ScenarioReceiveLiveness
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E fd \in availableConnFds :
        LET s == LastOf(freeSlots)
            gen == slotGen[s] + 1
        IN /\ freeSlots' = PopLast(freeSlots)
           /\ slotState' = [slotState EXCEPT ![s] = "open"]
           /\ slotGen' = [slotGen EXCEPT ![s] = gen]
           /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
           /\ slotFd' = [slotFd EXCEPT ![s] = fd]
           /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
           /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
           /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
           /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 1]
           /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
           /\ recvFdRef' = [recvFdRef EXCEPT ![s] = fd]
           /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
           /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
           /\ recvSeen' = [recvSeen EXCEPT ![s] = << >>]
           /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << >>]
           /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
           /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
           /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
           /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
           /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
           /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
           /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
           /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
           /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
           /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
           /\ sendReady' = {}
           /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
           /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
           /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
           /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
           /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
           /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
           /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
           /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
           /\ sqAvail' = 0
           /\ recvRetrySet' = {s}
           /\ recvRetryDue' = TRUE
           /\ recvRetryTimerArmed' = FALSE
           /\ recvRetryPassActive' = TRUE
           /\ recvRetryPassBlockedOnSq' = FALSE
           /\ recvCancelRetrySet' = {}
           /\ cancelRetryOriginGen' = cancelRetryOriginGen
           /\ cancelRetryOriginEpoch' = cancelRetryOriginEpoch
           /\ sendRetrySet' = {}
           /\ acceptArmed' = "none"
           /\ acceptArmedMultishot' = FALSE
           /\ eventfdReadArmed' = eventfdReadArmed
           /\ cqQueue' = << >>
           /\ loopPhase' = "after"
           /\ availableConnFds' = availableConnFds \ {fd}
           /\ availableRecvTokens' = availableRecvTokens
           /\ availableSendTokens' = availableSendTokens
    /\ UNCHANGED << recvResubmitQueue, closeRequestQueue, acceptChannel,
                    schedulerQueue, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, stopping,
                    ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, releasedConnFds,
                    acceptChannelDrops, fileSlotOwner, nextFileSlot,
                    freeFileSlots, BugVars >>

SetupCancelRetryPassReady ==
    /\ ScenarioCancelRetry
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E firstTok \in availableRecvTokens :
        \E secondTok \in availableRecvTokens \ {firstTok},
           fd \in availableConnFds :
            LET s == LastOf(freeSlots)
                gen == slotGen[s] + 1
            IN /\ freeSlots' = PopLast(freeSlots)
               /\ slotState' = [slotState EXCEPT ![s] = "open"]
               /\ slotGen' = [slotGen EXCEPT ![s] = gen]
               /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
               /\ slotFd' = [slotFd EXCEPT ![s] = fd]
               /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
               /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
               /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
               /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 1]
               /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
               /\ recvFdRef' = [recvFdRef EXCEPT ![s] = fd]
               /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
               /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
               /\ recvSeen' = [recvSeen EXCEPT ![s] = << firstTok, secondTok >>]
               /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << firstTok, secondTok >>]
               /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
               /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
               /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
               /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
               /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
               /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
               /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
               /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
               /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
               /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
               /\ sendReady' = {}
               /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
               /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
               /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
               /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
               /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
               /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
               /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
               /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
               /\ sqAvail' = 0
               /\ recvRetrySet' = {s}
               /\ recvRetryDue' = TRUE
               /\ recvRetryTimerArmed' = FALSE
               /\ recvRetryPassActive' = TRUE
               /\ recvRetryPassBlockedOnSq' = FALSE
               /\ recvCancelRetrySet' = {s}
               /\ cancelRetryOriginGen' = [cancelRetryOriginGen EXCEPT ![s] = gen]
               /\ cancelRetryOriginEpoch' = [cancelRetryOriginEpoch EXCEPT ![s] = 1]
               /\ sendRetrySet' = {}
               /\ acceptArmed' = "none"
               /\ acceptArmedMultishot' = FALSE
               /\ eventfdReadArmed' = eventfdReadArmed
               /\ cqQueue' = << >>
               /\ loopPhase' = "after"
               /\ availableConnFds' = availableConnFds \ {fd}
               /\ availableRecvTokens' = availableRecvTokens \ {firstTok, secondTok}
               /\ availableSendTokens' = availableSendTokens
    /\ UNCHANGED << recvResubmitQueue, closeRequestQueue, acceptChannel,
                    schedulerQueue, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, stopping,
                    ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, releasedConnFds,
                    acceptChannelDrops, fileSlotOwner, nextFileSlot,
                    freeFileSlots, BugVars >>

SetupRecvCancelReady ==
    /\ ScenarioRecvFocused
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E tok \in availableRecvTokens,
         fd \in availableConnFds :
        LET s == LastOf(freeSlots)
            gen == slotGen[s] + 1
        IN /\ freeSlots' = PopLast(freeSlots)
           /\ slotState' = [slotState EXCEPT ![s] = "open"]
           /\ slotGen' = [slotGen EXCEPT ![s] = gen]
           /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
           /\ slotFd' = [slotFd EXCEPT ![s] = fd]
           /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
           /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
           /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
           /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 1]
           /\ recvArmed' = [recvArmed EXCEPT ![s] = "armed"]
           /\ recvFdRef' = [recvFdRef EXCEPT ![s] = fd]
           /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = TRUE]
           /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
           /\ recvSeen' = [recvSeen EXCEPT ![s] = << tok >>]
           /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << tok >>]
           /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
           /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
           /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
           /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
           /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
           /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
           /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
           /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
           /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
           /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
           /\ sendReady' = {}
           /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "armed"]
           /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 1]
           /\ cancelFd' = [cancelFd EXCEPT ![s] = fd]
           /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
           /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
           /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
           /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
           /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
           /\ sqAvail' = 0
           /\ recvRetrySet' = {}
           /\ recvRetryDue' = TRUE
           /\ recvRetryTimerArmed' = FALSE
           /\ recvRetryPassActive' = FALSE
           /\ recvRetryPassBlockedOnSq' = FALSE
           /\ recvCancelRetrySet' = {}
           /\ cancelRetryOriginGen' = cancelRetryOriginGen
           /\ cancelRetryOriginEpoch' = cancelRetryOriginEpoch
           /\ sendRetrySet' = {}
           /\ acceptArmed' = "none"
           /\ acceptArmedMultishot' = FALSE
           /\ eventfdReadArmed' = eventfdReadArmed
           /\ cqQueue' = << >>
           /\ loopPhase' = "top"
           /\ availableConnFds' = availableConnFds \ {fd}
           /\ availableRecvTokens' = availableRecvTokens \ {tok}
           /\ availableSendTokens' = availableSendTokens
           /\ acceptRetryRequired' = TRUE
           /\ acceptRetryDue' = TRUE
    /\ UNCHANGED << recvResubmitQueue, closeRequestQueue, acceptChannel,
                    schedulerQueue, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, releasedConnFds, acceptChannelDrops,
                    fileSlotOwner, nextFileSlot, freeFileSlots, BugVars >>

\* Shared one-slot witness for a live, generation+epoch-matching armed recv
\* with only a matching cancel-retry marker pending. Callers choose whether the
\* listener is already in the after phase or must traverse ProcessToAfter.
SetupMatchingCancelRetryReady(nextLoopPhase, nextRecvRetryPassActive) ==
    /\ ScenarioRecvFocused
    /\ Cardinality(SeqToSet(InitFreeSlots)) > 1
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E fd \in availableConnFds :
       LET s == LastOf(freeSlots)
       IN /\ freeSlots' = PopLast(freeSlots)
          /\ slotState' = [slotState EXCEPT ![s] = "open"]
          /\ slotGen' = [slotGen EXCEPT ![s] = 1]
          /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
          /\ slotFd' = [slotFd EXCEPT ![s] = fd]
          /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
          /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
          /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
          /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 1]
          /\ recvArmed' = [recvArmed EXCEPT ![s] = "armed"]
          /\ recvFdRef' = [recvFdRef EXCEPT ![s] = fd]
          /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
          /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
          /\ recvSeen' = [recvSeen EXCEPT ![s] = << >>]
          /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << >>]
          /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
          /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
          /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
          /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
          /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
          /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
          /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
          /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
          /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
          /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
          /\ sendReady' = {}
          /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
          /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
          /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
          /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
          /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
          /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
          /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
          /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
          /\ sqAvail' = 1
          /\ recvRetrySet' = {}
          /\ recvRetryDue' = TRUE
          /\ recvRetryTimerArmed' = FALSE
          /\ recvRetryPassActive' = nextRecvRetryPassActive
          /\ recvRetryPassBlockedOnSq' = FALSE
          /\ recvCancelRetrySet' = {s}
          /\ cancelRetryOriginGen' = [cancelRetryOriginGen EXCEPT ![s] = 1]
          /\ cancelRetryOriginEpoch' = [cancelRetryOriginEpoch EXCEPT ![s] = 1]
          /\ sendRetrySet' = {}
          /\ acceptArmed' = "none"
          /\ acceptArmedMultishot' = FALSE
          /\ eventfdReadArmed' = eventfdReadArmed
          /\ cqQueue' = << >>
          /\ loopPhase' = nextLoopPhase
          /\ availableConnFds' = availableConnFds \ {fd}
          /\ availableRecvTokens' = availableRecvTokens
          /\ availableSendTokens' = availableSendTokens
          /\ acceptRetryRequired' = FALSE
          /\ acceptRetryDue' = TRUE
    /\ UNCHANGED << recvResubmitQueue, closeRequestQueue, acceptChannel,
                    schedulerQueue, signalPending, eventfdPending,
                    useMultishotAccept, acceptRetryTimerArmed,
                    eventfdReadRetryRequired, stopping, ringFailed,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, releasedConnFds, acceptChannelDrops,
                    fileSlotOwner, nextFileSlot, freeFileSlots, BugVars >>

SetupAfterRetryCancelReady ==
    SetupMatchingCancelRetryReady("after", FixCancelRetryPassGate)

SetupCancelRetryPassGateReady ==
    SetupMatchingCancelRetryReady("process", FALSE)

\* Atomic bounded-prefix abstraction for the close/reuse-focused configs. This
\* summarizes a reachable accept -> recv-arm -> app-close-ready prefix into one
\* step so the bounded witness can spend its state budget on the release/reuse
\* behavior instead of rediscovering the setup from BaseInit.
SetupCloseReuseReady ==
    /\ ScenarioStaleClose \/ ScenarioStructuralSafety \/ ScenarioLifecycleFocused
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E tok \in availableRecvTokens, fd \in availableConnFds :
        LET s == LastOf(freeSlots)
            gen == slotGen[s] + 1
            allocFromFree == Len(freeFileSlots) > 0
            haveFresh == nextFileSlot <= SlotCount + 1
            fileSlot ==
                IF ~UseRegisteredFiles
                    THEN NoFile
                    ELSE IF allocFromFree
                        THEN LastOf(freeFileSlots)
                        ELSE IF haveFresh
                            THEN nextFileSlot
                            ELSE NoFile
            fdRef == IF fileSlot # NoFile THEN fileSlot ELSE fd
        IN /\ ~UseRegisteredFiles \/ allocFromFree \/ haveFresh
           /\ freeSlots' = PopLast(freeSlots)
           /\ slotState' = [slotState EXCEPT ![s] = "open"]
           /\ slotGen' = [slotGen EXCEPT ![s] = gen]
           /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
           /\ slotFd' = [slotFd EXCEPT ![s] = fd]
           /\ slotFile' = [slotFile EXCEPT ![s] = fileSlot]
           /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
           /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
           /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 1]
           /\ recvArmed' = [recvArmed EXCEPT ![s] = "armed"]
           /\ recvFdRef' = [recvFdRef EXCEPT ![s] = fdRef]
           /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = TRUE]
           /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
           /\ recvSeen' = [recvSeen EXCEPT ![s] = << tok >>]
           /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << tok >>]
           /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
           /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
           /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
           /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
           /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
           /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
           /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
           /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
           /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
           /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
           /\ sendReady' = {}
           /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
           /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
           /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
           /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
           /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
           /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
           /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
           /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
           /\ sqAvail' = 0
           /\ recvRetrySet' = {}
           /\ recvRetryDue' = TRUE
           /\ recvRetryTimerArmed' = FALSE
           /\ recvRetryPassActive' = FALSE
           /\ recvRetryPassBlockedOnSq' = FALSE
           /\ recvCancelRetrySet' = {s}
           /\ cancelRetryOriginGen' = [cancelRetryOriginGen EXCEPT ![s] = gen]
           /\ cancelRetryOriginEpoch' = [cancelRetryOriginEpoch EXCEPT ![s] = 1]
           /\ sendRetrySet' = {}
           /\ closeRequestQueue' = << CloseReq(s, gen, "app") >>
           /\ acceptArmed' = "none"
           /\ acceptArmedMultishot' = FALSE
           /\ eventfdReadArmed' = eventfdReadArmed
           /\ cqQueue' = << >>
           /\ loopPhase' = "top"
           /\ availableConnFds' = availableConnFds \ {fd}
           /\ availableRecvTokens' = availableRecvTokens \ {tok}
           /\ availableSendTokens' = availableSendTokens
           /\ acceptRetryRequired' = TRUE
           /\ acceptRetryDue' = TRUE
           /\ IF fileSlot = NoFile
                 THEN /\ fileSlotOwner' = fileSlotOwner
                      /\ nextFileSlot' = nextFileSlot
                      /\ freeFileSlots' = freeFileSlots
                 ELSE IF allocFromFree
                     THEN /\ fileSlotOwner' = [fileSlotOwner EXCEPT ![fileSlot] = ConnOwner(s, gen)]
                          /\ nextFileSlot' = nextFileSlot
                          /\ freeFileSlots' = PopLast(freeFileSlots)
                     ELSE /\ fileSlotOwner' = [fileSlotOwner EXCEPT ![fileSlot] = ConnOwner(s, gen)]
                          /\ nextFileSlot' = nextFileSlot + 1
                          /\ freeFileSlots' = freeFileSlots
    /\ UNCHANGED << recvResubmitQueue, acceptChannel, schedulerQueue,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryTimerArmed, eventfdReadRetryRequired, stopping,
                    ringFailed, unbindStarted, disposeStarted,
                    unbindTimeoutElapsed, ringDisposed, releasedConnFds,
                    acceptChannelDrops, BugVars >>

\* Atomic bounded-prefix abstraction for Bug 4's focused send-retry
\* wake-on-request regression. It summarizes a reachable pre-request state with
\* one live connection already present in sendReady and the eventfd read armed,
\* so the focused witness still executes the real RequestSendRetry branch inside
\* LoopRunSendReady and the real LoopWaitDecision afterwards.
SetupSendRetryRequestReady ==
    /\ ScenarioSendRetry
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ acceptArmedMultishot
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ availableSendTokens # {}
    /\ \E fd \in availableConnFds, tok \in availableSendTokens :
        LET s == LastOf(freeSlots)
            gen == slotGen[s] + 1
        IN /\ freeSlots' = PopLast(freeSlots)
           /\ slotState' = [slotState EXCEPT ![s] = "open"]
           /\ slotGen' = [slotGen EXCEPT ![s] = gen]
           /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
           /\ slotFd' = [slotFd EXCEPT ![s] = fd]
           /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
           /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
           /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
           /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 0]
           /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
           /\ recvFdRef' = [recvFdRef EXCEPT ![s] = NoFd]
           /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
           /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
           /\ recvSeen' = [recvSeen EXCEPT ![s] = << >>]
           /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << >>]
           /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
           /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
           /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
           /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
           /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
           /\ sendApp' = [sendApp EXCEPT ![s] = << tok >>]
           /\ sendPending' = [sendPending EXCEPT ![s] = << tok >>]
           /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = SendParts(tok)]
           /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
           /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
           /\ sendReady' = {s}
           /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
           /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
           /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
           /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
           /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
           /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
           /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
           /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
           /\ recvRetrySet' = {}
           /\ recvRetryDue' = TRUE
           /\ recvRetryTimerArmed' = FALSE
           /\ recvRetryPassActive' = FALSE
           /\ recvRetryPassBlockedOnSq' = FALSE
           /\ recvCancelRetrySet' = {}
           /\ cancelRetryOriginGen' = cancelRetryOriginGen
           /\ cancelRetryOriginEpoch' = cancelRetryOriginEpoch
           /\ sendRetrySet' = {}
           /\ recvResubmitQueue' = << >>
           /\ closeRequestQueue' = << >>
           /\ acceptChannel' = << >>
           /\ schedulerQueue' = {}
           /\ signalPending' = FALSE
           /\ eventfdPending' = FALSE
           /\ acceptArmed' = "armed"
           /\ acceptArmedMultishot' = useMultishotAccept
           /\ eventfdReadArmed' = "armed"
           /\ cqQueue' = << >>
           /\ sqAvail' = 0
           /\ acceptRetryRequired' = FALSE
           /\ acceptRetryDue' = TRUE
           /\ acceptRetryTimerArmed' = FALSE
           /\ eventfdReadRetryRequired' = FALSE
           /\ loopPhase' = "top"
           /\ availableConnFds' = availableConnFds \ {fd}
           /\ availableRecvTokens' = availableRecvTokens
           /\ availableSendTokens' = availableSendTokens \ {tok}
    /\ UNCHANGED << useMultishotAccept, ringFailed, stopping,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, releasedConnFds, acceptChannelDrops,
                    fileSlotOwner, nextFileSlot, freeFileSlots, BugVars >>

\* Atomic bounded-prefix abstraction for the focused eventfd rearm/park
\* regression. It summarizes a reachable parked state with one live connection,
\* the listener eventfd read armed, one wake already pending, and the single
\* SQ slot consumed by that read so the focused body must traverse the real
\* completion -> failed rearm -> SQ-space recovery -> wait-decision slice.
SetupEventFdWakeReady ==
    /\ ScenarioEventFdWake
    /\ AllSlotsFree
    /\ freeSlots = InitFreeSlots
    /\ loopPhase = "top"
    /\ schedulerQueue = {}
    /\ sendReady = {}
    /\ recvResubmitQueue = << >>
    /\ closeRequestQueue = << >>
    /\ acceptChannel = << >>
    /\ cqQueue = << >>
    /\ acceptArmed = "armed"
    /\ eventfdReadArmed = "armed"
    /\ sqAvail = 1
    /\ availableConnFds # {}
    /\ \E fd \in availableConnFds :
        LET s == LastOf(freeSlots)
            gen == slotGen[s] + 1
        IN /\ freeSlots' = PopLast(freeSlots)
           /\ slotState' = [slotState EXCEPT ![s] = "open"]
           /\ slotGen' = [slotGen EXCEPT ![s] = gen]
           /\ slotAccepted' = [slotAccepted EXCEPT ![s] = TRUE]
           /\ slotFd' = [slotFd EXCEPT ![s] = fd]
           /\ slotFile' = [slotFile EXCEPT ![s] = NoFile]
           /\ outputCompleted' = [outputCompleted EXCEPT ![s] = FALSE]
           /\ sendLoopCompleted' = [sendLoopCompleted EXCEPT ![s] = FALSE]
           /\ recvEpoch' = [recvEpoch EXCEPT ![s] = 0]
           /\ recvArmed' = [recvArmed EXCEPT ![s] = "none"]
           /\ recvFdRef' = [recvFdRef EXCEPT ![s] = NoFd]
           /\ recvRearmPending' = [recvRearmPending EXCEPT ![s] = FALSE]
           /\ pendingRecv' = [pendingRecv EXCEPT ![s] = << >>]
           /\ recvSeen' = [recvSeen EXCEPT ![s] = << >>]
           /\ recvDelivered' = [recvDelivered EXCEPT ![s] = << >>]
           /\ sendInFlight' = [sendInFlight EXCEPT ![s] = "none"]
           /\ sendOpToken' = [sendOpToken EXCEPT ![s] = NoToken]
           /\ sendOpParts' = [sendOpParts EXCEPT ![s] = 0]
           /\ sendOpFd' = [sendOpFd EXCEPT ![s] = NoFd]
           /\ sendWaitingRetry' = [sendWaitingRetry EXCEPT ![s] = FALSE]
           /\ sendApp' = [sendApp EXCEPT ![s] = << >>]
           /\ sendPending' = [sendPending EXCEPT ![s] = << >>]
           /\ sendHeadRemaining' = [sendHeadRemaining EXCEPT ![s] = 0]
           /\ sendSent' = [sendSent EXCEPT ![s] = << >>]
           /\ sendPin' = [sendPin EXCEPT ![s] = NoToken]
           /\ sendReady' = {}
           /\ cancelInFlight' = [cancelInFlight EXCEPT ![s] = "none"]
           /\ cancelTargetEpoch' = [cancelTargetEpoch EXCEPT ![s] = 0]
           /\ cancelFd' = [cancelFd EXCEPT ![s] = NoFd]
           /\ appCloseIssued' = [appCloseIssued EXCEPT ![s] = FALSE]
           /\ abortIssued' = [abortIssued EXCEPT ![s] = FALSE]
           /\ transportAborted' = [transportAborted EXCEPT ![s] = FALSE]
           /\ socketShutdown' = [socketShutdown EXCEPT ![s] = FALSE]
           /\ socketClosed' = [socketClosed EXCEPT ![s] = FALSE]
           /\ recvRetrySet' = {}
           /\ recvRetryDue' = TRUE
           /\ recvRetryTimerArmed' = FALSE
           /\ recvRetryPassActive' = FALSE
           /\ recvRetryPassBlockedOnSq' = FALSE
           /\ recvCancelRetrySet' = {}
           /\ cancelRetryOriginGen' = cancelRetryOriginGen
           /\ cancelRetryOriginEpoch' = cancelRetryOriginEpoch
           /\ sendRetrySet' = {}
           /\ recvResubmitQueue' = << >>
           /\ closeRequestQueue' = << >>
           /\ acceptChannel' = << >>
           /\ schedulerQueue' = {}
           /\ signalPending' = FALSE
           /\ eventfdPending' = TRUE
           /\ acceptArmed' = "none"
           /\ acceptArmedMultishot' = FALSE
           /\ eventfdReadArmed' = "armed"
           /\ cqQueue' = << >>
           /\ sqAvail' = 0
           /\ acceptRetryRequired' = FALSE
           /\ acceptRetryDue' = TRUE
           /\ acceptRetryTimerArmed' = FALSE
           /\ eventfdReadRetryRequired' = FALSE
           /\ loopPhase' = "parked"
           /\ availableConnFds' = availableConnFds \ {fd}
           /\ availableRecvTokens' = availableRecvTokens
           /\ availableSendTokens' = availableSendTokens
    /\ UNCHANGED << useMultishotAccept, ringFailed, stopping,
                    unbindStarted, disposeStarted, unbindTimeoutElapsed,
                    ringDisposed, releasedConnFds, acceptChannelDrops,
                    fileSlotOwner, nextFileSlot, freeFileSlots, BugVars >>

CancelRetryFocusedKernelAny ==
    \/ KernelCompleteRecvCanceled
    \/ KernelCompleteCancelControl

CancelRetryFocusedBodyStep ==
    \/ KernelMakesSqSpace
    \/ CancelRetryFocusedKernelAny
    \/ SchedulerStep

CancelRetryFocusedStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupAfterRetryCancelReady
        \/ /\ FocusedSetupDone
           /\ CancelRetryFocusedBodyStep)
    /\ OverflowVarsUnchanged

RecvResubmitFocusedDrainWhenSqReady ==
    /\ sqAvail > 0
    /\ LoopDrainRecvResubmitHead

RecvResubmitFocusedBodyStep ==
    \/ AppDrainFlushSuccess
    \/ KernelMakesSqSpace
    \/ RecvResubmitFocusedDrainWhenSqReady
    \/ LoopSubmitStaged

RecvResubmitFocusedStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupReceiveRearmPending
        \/ /\ FocusedSetupDone
           /\ RecvResubmitFocusedBodyStep)
    /\ OverflowVarsUnchanged

RetryPassReadyBodyStep ==
    \/ KernelMakesSqSpace
    \/ SchedulerStep

ReceiveRetryPassReadyStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupReceiveRetryPassReady
        \/ /\ FocusedSetupDone
           /\ RetryPassReadyBodyStep)
    /\ OverflowVarsUnchanged

CancelRetryPassReadyStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupCancelRetryPassReady
        \/ /\ FocusedSetupDone
           /\ RetryPassReadyBodyStep)
    /\ OverflowVarsUnchanged

\* Focused bug-3 pass-gate witness: the dedicated setup leaves only a matching
\* cancel-retry marker pending, then must traverse ProcessToAfter /
\* AfterRetryCancelOne through the same RetryPassPending gate production uses.
CancelRetryPassGateBodyStep ==
    \/ ProcessToAfter
    \/ AfterRetryCancelOne

CancelRetryPassGateStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupCancelRetryPassGateReady
        \/ /\ FocusedSetupDone
           /\ CancelRetryPassGateBodyStep)
    /\ OverflowVarsUnchanged

RecvFocusedKernelAny ==
    \/ KernelCompleteRecvEnobufs
    \/ KernelCompleteRecvTerminal
    \/ KernelCompleteRecvCanceled
    \/ KernelCompleteCancelControl

RecvFocusedSchedulerStep ==
    \/ LoopTopToWait
    \/ LoopWaitDecision
    \/ ProcessRecvCqe
    \/ ProcessCancelCqe
    \/ ProcessToAfter
    \/ AfterRetryRecvOne
    \/ LoopSubmitStaged
    \/ AfterResumeSend

RecvFocusedStep ==
    /\ (\/ SetupAcceptedRecvAwaitingAcceptRetry
        \/ SetupRecvCancelReady
        \/ SetupAfterRetryCancelReady
        \/ KernelMakesSqSpace
        \/ RecvFocusedKernelAny
        \/ FireRecvRetryTimer
        \/ RecvFocusedSchedulerStep)
    /\ OverflowVarsUnchanged

CloseReuseFocusedKernelAny ==
    \/ KernelCompleteRecvData
    \/ KernelCompleteRecvTerminal
    \/ KernelCompleteAcceptFd

CloseReuseSchedulerStep ==
    \/ LoopMaybeFinishSend
    \/ LoopDrainCloseHead
    \/ LoopTryFinalizeClosing
    \/ LoopTopToWait
    \/ LoopWaitDecision
    \/ LoopUnpark
    \/ ProcessRecvCqe
    \/ ProcessAcceptCqe
    \/ ProcessToAfter
    \/ AfterRetryAccept
    \/ LoopSubmitStaged
    \/ AfterResumeSend

\* Restricted post-setup body for the close/reuse-focused witness.
CloseReuseFocusedBodyStep ==
    \/ KernelMakesSqSpace
    \/ CloseReuseFocusedKernelAny
    \/ AppDrainFlushClose
    \/ CloseReuseSchedulerStep

CloseReuseFocusedStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupCloseReuseReady
        \/ /\ FocusedSetupDone
           /\ CloseReuseFocusedBodyStep)
    /\ OverflowVarsUnchanged

LifecycleFocusedBodyStep ==
    \/ RequestDisposeTransport
    \/ KernelMakesSqSpace
    \/ CloseReuseFocusedKernelAny
    \/ TimerFireAny
    \/ SchedulerStep

LifecycleFocusedStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupCloseReuseReady
        \/ /\ FocusedSetupDone
           /\ LifecycleFocusedBodyStep)
    /\ OverflowVarsUnchanged

SendRetryFocusedKernelCompleteEventfd ==
    /\ loopPhase = "parked"
    /\ KernelCompleteEventfd

\* Keep SQ-capacity recovery in the focused body so corrected traces must
\* regain a submission slot before the resumed send can make forward progress.
SendRetryParkFocusedBodyStep ==
    \/ LoopRunSendReady
    \/ KernelMakesSqSpace
    \/ LoopSubmitStaged
    \/ LoopTopToWait
    \/ LoopWaitDecision
    \/ SendRetryFocusedKernelCompleteEventfd
    \/ LoopUnpark
    \/ ProcessEventfdCqe
    \/ ProcessToAfter
    \/ AfterRetryEventfd
    \/ AfterResumeSend

SendRetryParkFocusedStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupSendRetryRequestReady
        \/ /\ FocusedSetupDone
           /\ SendRetryParkFocusedBodyStep)
    /\ OverflowVarsUnchanged

EventFdWakeFocusedBodyStep ==
    \/ /\ loopPhase = "parked"
       /\ KernelCompleteEventfd
    \/ LoopUnpark
    \/ ProcessEventfdCqe
    \/ ProcessToAfter
    \/ /\ eventfdReadRetryRequired
       /\ KernelMakesSqSpace
    \/ LoopTopToWait
    \/ LoopWaitDecision
    \/ AfterRetryEventfd
    \/ AfterResumeSend
    \/ LoopSubmitStaged

EventFdWakeFocusedStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupEventFdWakeReady
        \/ /\ FocusedSetupDone
           /\ EventFdWakeFocusedBodyStep)
    /\ OverflowVarsUnchanged

DisposeAcceptedCqeFocusedBodyStep ==
    \/ RequestDisposeTransport
    \/ LoopStop
    \/ DisposeDrainBeforeRingClose
    \/ DisposeCloseRing

DisposeAcceptedCqeFocusedStep ==
    /\ (\/ /\ ~DisposeAcceptedCqeFocusedSetupDone
           /\ SetupDisposeAcceptedCqeLeakReady
        \/ /\ DisposeAcceptedCqeFocusedSetupDone
           /\ DisposeAcceptedCqeFocusedBodyStep)
    /\ OverflowVarsUnchanged

\* Shared bounded abstraction for the hidden-overflow dispose witness. With
\* FixDisposeOverflowDrain disabled, the focused body consumes the bounded
\* boundary-enter budget and then drains only the initial visible snapshot.
\* With the fix enabled, it alternates visible snapshot drains with one hidden
\* overflow-list materialization enter per round, capped to
\* DisposeOverflowDrainRoundLimit, and can terminate early on no progress or
\* after exhausting that bounded round budget.
DisposeOverflowConsumeCurrentRound ==
    /\ ScenarioDisposeAcceptedOverflowFocused
    /\ disposeStarted
    /\ loopPhase = "stopped"
    /\ ~ringDisposed
    /\ ~disposeDrainCompleted
    /\ ~FixDisposeOverflowDrain
    /\ cqQueue # << >>
    /\ disposeOverflowBacklog # << >>
    /\ disposeOverflowPreDrainEnterCount < DisposeOverflowDrainRoundLimit
    /\ disposeOverflowPreDrainEnterCount' = disposeOverflowPreDrainEnterCount + 1
    /\ cqQueue' = cqQueue
    /\ disposeOverflowBacklog' = disposeOverflowBacklog
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, unbindStarted,
                    disposeStarted, unbindTimeoutElapsed, ringDisposed,
                    availableConnFds, availableRecvTokens, availableSendTokens,
                    releasedConnFds, acceptChannelDrops,
                    fileSlotOwner, nextFileSlot, freeFileSlots,
                    disposeDrainCompleted, disposeOverflowTrackedFds,
                    dispatchLastKind, dispatchLastMatched,
                    dispatchLastApplied >>

DisposeOverflowDrainVisible ==
    /\ ScenarioDisposeAcceptedOverflowFocused
    /\ disposeStarted
    /\ loopPhase = "stopped"
    /\ ~ringDisposed
    /\ ~disposeDrainCompleted
    /\ cqQueue # << >>
    /\ IF FixDisposeOverflowDrain
          THEN TRUE
          ELSE (disposeOverflowBacklog = << >>
                \/ disposeOverflowPreDrainEnterCount = DisposeOverflowDrainRoundLimit)
    /\ releasedConnFds' = releasedConnFds \cup AcceptCqeFdSet(cqQueue)
    /\ cqQueue' = << >>
    /\ disposeDrainCompleted' =
        IF FixDisposeOverflowDrain
            THEN disposeOverflowBacklog = << >>
            ELSE TRUE
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, unbindStarted,
                    disposeStarted, unbindTimeoutElapsed, ringDisposed,
                    availableConnFds, availableRecvTokens, availableSendTokens,
                    acceptChannelDrops, fileSlotOwner, nextFileSlot,
                    freeFileSlots, disposeOverflowBacklog,
                    disposeOverflowTrackedFds,
                    disposeOverflowPreDrainEnterCount, dispatchLastKind,
                    dispatchLastMatched, dispatchLastApplied >>

DisposeOverflowMaterializeRound ==
    /\ ScenarioDisposeAcceptedOverflowFocused
    /\ disposeStarted
    /\ loopPhase = "stopped"
    /\ ~ringDisposed
    /\ ~disposeDrainCompleted
    /\ FixDisposeOverflowDrain
    /\ cqQueue = << >>
    /\ disposeOverflowBacklog # << >>
    /\ disposeOverflowPreDrainEnterCount < DisposeOverflowDrainRoundLimit
    /\ cqQueue' = << Head(disposeOverflowBacklog) >>
    /\ disposeOverflowBacklog' = TailOrEmpty(disposeOverflowBacklog)
    /\ disposeOverflowPreDrainEnterCount' = disposeOverflowPreDrainEnterCount + 1
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, unbindStarted,
                    disposeStarted, unbindTimeoutElapsed, ringDisposed,
                    availableConnFds, availableRecvTokens, availableSendTokens,
                    releasedConnFds, acceptChannelDrops,
                    fileSlotOwner, nextFileSlot, freeFileSlots,
                    disposeDrainCompleted, disposeOverflowTrackedFds,
                    dispatchLastKind, dispatchLastMatched,
                    dispatchLastApplied >>

DisposeOverflowTerminateWithoutProgress ==
    /\ ScenarioDisposeAcceptedOverflowFocused
    /\ disposeStarted
    /\ loopPhase = "stopped"
    /\ ~ringDisposed
    /\ ~disposeDrainCompleted
    /\ FixDisposeOverflowDrain
    /\ cqQueue = << >>
    /\ disposeOverflowBacklog # << >>
    /\ (disposeOverflowPreDrainEnterCount = DisposeOverflowDrainRoundLimit
        \/ ~DisposeOverflowGuaranteedProgress)
    \* Bounded abstraction of "no progress" / materialization errno termination.
    /\ disposeDrainCompleted' = TRUE
    /\ disposeOverflowPreDrainEnterCount' =
        IF disposeOverflowPreDrainEnterCount < DisposeOverflowDrainRoundLimit
            THEN disposeOverflowPreDrainEnterCount + 1
            ELSE disposeOverflowPreDrainEnterCount
    /\ UNCHANGED << ConnVars, RetryVars, QueueVars, acceptArmed,
                    acceptArmedMultishot, eventfdReadArmed, sqAvail,
                    signalPending, eventfdPending, useMultishotAccept,
                    acceptRetryRequired, acceptRetryDue,
                    acceptRetryTimerArmed, eventfdReadRetryRequired,
                    loopPhase, stopping, ringFailed, unbindStarted,
                    disposeStarted, unbindTimeoutElapsed, ringDisposed,
                    availableConnFds, availableRecvTokens, availableSendTokens,
                    releasedConnFds, acceptChannelDrops, fileSlotOwner,
                    nextFileSlot, freeFileSlots, cqQueue,
                    disposeOverflowBacklog, disposeOverflowTrackedFds,
                    dispatchLastKind,
                    dispatchLastMatched, dispatchLastApplied >>

DisposeAcceptedOverflowFocusedBodyStep ==
    \/ RequestDisposeTransport
    \/ LoopStop
    \/ DisposeOverflowConsumeCurrentRound
    \/ DisposeOverflowDrainVisible
    \/ DisposeOverflowMaterializeRound
    \/ DisposeOverflowTerminateWithoutProgress
    \/ DisposeCloseRing

DisposeAcceptedOverflowFocusedStep ==
    \/ /\ ~DisposeAcceptedOverflowFocusedSetupDone
       /\ SetupDisposeAcceptedOverflowReady
    \/ /\ DisposeAcceptedOverflowFocusedSetupDone
       /\ DisposeAcceptedOverflowFocusedBodyStep

AcceptRetryParkFocusedBodyStep ==
    \/ ProcessAcceptCqe
    \/ ProcessToAfter
    \/ AfterResumeSend
    \/ LoopSubmitStaged
    \/ /\ acceptRetryRequired
       /\ KernelMakesSqSpace
    \/ LoopTopToWait
    \/ LoopWaitDecision
    \/ AfterRetryAccept

AcceptRetryParkFocusedStep ==
    /\ (\/ /\ ~AcceptRetryParkSetupDone
           /\ SetupAcceptRetryParkReady
        \/ /\ AcceptRetryParkSetupDone
           /\ AcceptRetryParkFocusedBodyStep)
    /\ OverflowVarsUnchanged

RetryPassParkFocusedBodyStep ==
    \/ ProcessRecvCqe
    \/ ProcessToAfter
    \/ AfterResumeSend
    \/ /\ recvRetryPassBlockedOnSq
       /\ KernelMakesSqSpace
    \/ LoopTopToWait
    \/ LoopWaitDecision
    \/ AfterRetryRecvOne
    \/ LoopSubmitStaged

RetryPassParkFocusedStep ==
    /\ (\/ /\ ~FocusedSetupDone
           /\ SetupRetryPassParkReady
        \/ /\ FocusedSetupDone
           /\ RetryPassParkFocusedBodyStep)
    /\ OverflowVarsUnchanged

ExternalAny ==
    /\ (\/ AppAcceptConnection
        \/ AppProduceSend
        \/ AppRequestDispose
        \/ AppRequestAbort
        \/ RequestUnbind
        \/ RequestDisposeTransport
        \/ TriggerRingFailure)
    /\ OverflowVarsUnchanged

Spec ==
    /\ BaseInit
    /\ [][((KernelMakesSqSpace \/ KernelCompleteAny \/ AppDrainAny \/ TimerFireAny \/ ExternalAny \/ SchedulerStep)
           /\ OverflowVarsUnchanged)]_Vars

CancelRetryFocusedSpec ==
    /\ BaseInit
    /\ [][CancelRetryFocusedStep]_Vars

RecvFocusedSpec ==
    /\ BaseInit
    /\ [][RecvFocusedStep]_Vars

CloseReuseFocusedSpec ==
    /\ BaseInit
    /\ [][CloseReuseFocusedStep]_Vars

LifecycleFocusedSpec ==
    /\ BaseInit
    /\ [][LifecycleFocusedStep]_Vars

DisposeAcceptedCqeFocusedSpec ==
    /\ BaseInit
    /\ [][DisposeAcceptedCqeFocusedStep]_Vars

DisposeAcceptedOverflowFocusedSpec ==
    /\ BaseInit
    /\ [][DisposeAcceptedOverflowFocusedStep]_Vars

SendRetryParkFocusedSpec ==
    /\ BaseInit
    /\ [][SendRetryParkFocusedStep]_Vars

EventFdWakeFocusedSpec ==
    /\ BaseInit
    /\ [][EventFdWakeFocusedStep]_Vars

AcceptRetryParkFocusedSpec ==
    /\ BaseInit
    /\ [][AcceptRetryParkFocusedStep]_Vars

RetryPassParkFocusedSpec ==
    /\ BaseInit
    /\ [][RetryPassParkFocusedStep]_Vars

RecvResubmitFocusedSpec ==
    /\ BaseInit
    /\ [][RecvResubmitFocusedStep]_Vars

ReceiveRetryPassReadySpec ==
    /\ BaseInit
    /\ [][ReceiveRetryPassReadyStep]_Vars

CancelRetryPassReadySpec ==
    /\ BaseInit
    /\ [][CancelRetryPassReadyStep]_Vars

CancelRetryPassGateSpec ==
    /\ BaseInit
    /\ [][CancelRetryPassGateStep]_Vars

LiveSpec ==
    /\ Spec
    /\ WF_Vars((/\ SchedulerStep /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopMaybeFinishSend /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopDrainCloseHead /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopTryFinalizeClosing /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopSubmitStaged /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryRecvOne /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryCancelOne /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryAccept /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryEventfd /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterResumeSend /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopStop /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ DisposeDrainBeforeRingClose /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ DisposeCloseRing /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ KernelMakesSqSpace /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ KernelCompleteAny /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AppDrainAny /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ TimerFireAny /\ OverflowVarsUnchanged))

CancelRetryFocusedLiveSpec ==
    /\ CancelRetryFocusedSpec
    /\ WF_Vars((/\ SchedulerStep /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopSubmitStaged /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryCancelOne /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterResumeSend /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ KernelMakesSqSpace /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ CancelRetryFocusedKernelAny /\ OverflowVarsUnchanged))

CloseReuseFocusedLiveSpec ==
    /\ CloseReuseFocusedSpec
    /\ WF_Vars((/\ CloseReuseSchedulerStep /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopMaybeFinishSend /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopDrainCloseHead /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopTryFinalizeClosing /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryAccept /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopSubmitStaged /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterResumeSend /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ KernelMakesSqSpace /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ CloseReuseFocusedKernelAny /\ OverflowVarsUnchanged))

LifecycleFocusedLiveSpec ==
    /\ LifecycleFocusedSpec
    /\ WF_Vars((/\ RequestDisposeTransport /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ SchedulerStep /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopMaybeFinishSend /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopDrainCloseHead /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopTryFinalizeClosing /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopStop /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ DisposeDrainBeforeRingClose /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ DisposeCloseRing /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ KernelMakesSqSpace /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ CloseReuseFocusedKernelAny /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ TimerFireAny /\ OverflowVarsUnchanged))

SendRetryParkFocusedLiveSpec ==
    /\ SendRetryParkFocusedSpec
    /\ WF_Vars((/\ SendRetryParkFocusedBodyStep /\ OverflowVarsUnchanged))

RecvResubmitFocusedLiveSpec ==
    /\ RecvResubmitFocusedSpec
    /\ WF_Vars((/\ SetupReceiveRearmPending /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopSubmitStaged /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ RecvResubmitFocusedDrainWhenSqReady /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ KernelMakesSqSpace /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AppDrainFlushSuccess /\ OverflowVarsUnchanged))

ReceiveRetryPassReadyLiveSpec ==
    /\ ReceiveRetryPassReadySpec
    /\ WF_Vars((/\ SchedulerStep /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopSubmitStaged /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryRecvOne /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterResumeSend /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ KernelMakesSqSpace /\ OverflowVarsUnchanged))

CancelRetryPassReadyLiveSpec ==
    /\ CancelRetryPassReadySpec
    /\ WF_Vars((/\ SchedulerStep /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ LoopSubmitStaged /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryRecvOne /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryCancelOne /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterResumeSend /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ KernelMakesSqSpace /\ OverflowVarsUnchanged))

CancelRetryPassGateLiveSpec ==
    /\ CancelRetryPassGateSpec
    /\ WF_Vars((/\ SetupCancelRetryPassGateReady /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ ProcessToAfter /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AfterRetryCancelOne /\ OverflowVarsUnchanged))

EventFdWakeFocusedLiveSpec ==
    /\ EventFdWakeFocusedSpec
    /\ WF_Vars((/\ eventfdReadRetryRequired
                /\ KernelMakesSqSpace
                /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ EventFdWakeFocusedBodyStep /\ OverflowVarsUnchanged))

AcceptRetryParkFocusedLiveSpec ==
    /\ AcceptRetryParkFocusedSpec
    /\ WF_Vars((/\ acceptRetryRequired
                /\ KernelMakesSqSpace
                /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ AcceptRetryParkFocusedBodyStep /\ OverflowVarsUnchanged))

RetryPassParkFocusedLiveSpec ==
    /\ RetryPassParkFocusedSpec
    /\ SF_Vars((/\ recvRetryPassBlockedOnSq
                /\ KernelMakesSqSpace
                /\ OverflowVarsUnchanged))
    /\ WF_Vars((/\ RetryPassParkFocusedBodyStep /\ OverflowVarsUnchanged))

TypeOK ==
    /\ freeSlots \in Seq(Slots)
    /\ UniqueSeq(freeSlots)
    /\ slotState \in [Slots -> ConnStates]
    /\ slotGen \in [Slots -> 0..MaxConnGen]
    /\ slotAccepted \in [Slots -> BOOLEAN]
    /\ slotFd \in [Slots -> ConnFdUniverse \cup {NoFd}]
    /\ slotFile \in [Slots -> ConnFileSlots \cup {NoFile}]
    /\ outputCompleted \in [Slots -> BOOLEAN]
    /\ sendLoopCompleted \in [Slots -> BOOLEAN]
    /\ recvEpoch \in [Slots -> 0..MaxRecvEpoch]
    /\ recvArmed \in [Slots -> {"none", "staged", "armed"}]
    /\ recvFdRef \in [Slots -> ConnFdUniverse \cup ConnFileSlots \cup {NoFd}]
    /\ recvRearmPending \in [Slots -> BOOLEAN]
    /\ pendingRecv \in [Slots -> Seq(RecvTokenUniverse)]
    /\ recvSeen \in [Slots -> Seq(RecvTokenUniverse)]
    /\ recvDelivered \in [Slots -> Seq(RecvTokenUniverse)]
    /\ sendInFlight \in [Slots -> {"none", "staged", "armed"}]
    /\ sendOpToken \in [Slots -> SendTokenUniverse \cup {NoToken}]
    /\ sendOpParts \in [Slots -> 0..2]
    /\ sendOpFd \in [Slots -> ConnFdUniverse \cup ConnFileSlots \cup {NoFd}]
    /\ sendWaitingRetry \in [Slots -> BOOLEAN]
    /\ sendApp \in [Slots -> Seq(SendTokenUniverse)]
    /\ sendPending \in [Slots -> Seq(SendTokenUniverse)]
    /\ sendHeadRemaining \in [Slots -> 0..2]
    /\ sendSent \in [Slots -> Seq(SendTokenUniverse)]
    /\ sendPin \in [Slots -> SendTokenUniverse \cup {NoToken}]
    /\ sendReady \subseteq Slots
    /\ cancelInFlight \in [Slots -> {"none", "staged", "armed"}]
    /\ cancelTargetEpoch \in [Slots -> 0..MaxRecvEpoch]
    /\ cancelFd \in [Slots -> ConnFdUniverse \cup ConnFileSlots \cup {NoFd}]
    /\ appCloseIssued \in [Slots -> BOOLEAN]
    /\ abortIssued \in [Slots -> BOOLEAN]
    /\ transportAborted \in [Slots -> BOOLEAN]
    /\ socketShutdown \in [Slots -> BOOLEAN]
    /\ socketClosed \in [Slots -> BOOLEAN]
    /\ acceptArmed \in {"none", "staged", "armed"}
    /\ acceptArmedMultishot \in BOOLEAN
    /\ eventfdReadArmed \in {"none", "staged", "armed"}
    /\ cqQueue \in Seq([kind : AllowedCqeKinds,
                        slot : Slots \cup {NoSlot},
                        gen : 0..MaxConnGen,
                        epoch : 0..MaxRecvEpoch,
                        outcome : CqeOutcomes,
                        more : BOOLEAN,
                        token : ConnFdUniverse \cup RecvTokenUniverse \cup SendTokenUniverse \cup {NoToken},
                        parts : 0..2])
    /\ \A i \in 1..Len(cqQueue) : IsCqe(cqQueue[i])
    /\ sqAvail \in 0..SqCapacity
    /\ recvRetrySet \subseteq Slots
    /\ recvRetryDue \in BOOLEAN
    /\ recvRetryTimerArmed \in BOOLEAN
    /\ recvRetryPassActive \in BOOLEAN
    /\ recvRetryPassBlockedOnSq \in BOOLEAN
    /\ recvCancelRetrySet \subseteq Slots
    /\ cancelRetryOriginGen \in [Slots -> 0..MaxConnGen]
    /\ cancelRetryOriginEpoch \in [Slots -> 0..MaxRecvEpoch]
    /\ sendRetrySet \subseteq [slot : Slots, gen : 0..MaxConnGen]
    /\ recvResubmitQueue \in Seq([slot : Slots, gen : 0..MaxConnGen])
    /\ \A i \in 1..Len(recvResubmitQueue) : IsConnRef(recvResubmitQueue[i])
    /\ closeRequestQueue \in Seq([slot : Slots, gen : 0..MaxConnGen, kind : CloseKinds])
    /\ \A i \in 1..Len(closeRequestQueue) : IsCloseReq(closeRequestQueue[i])
    /\ acceptChannel \in Seq([slot : Slots, gen : 0..MaxConnGen])
    /\ \A i \in 1..Len(acceptChannel) : IsConnRef(acceptChannel[i])
    /\ schedulerQueue \subseteq Slots
    /\ signalPending \in BOOLEAN
    /\ eventfdPending \in BOOLEAN
    /\ useMultishotAccept \in BOOLEAN
    /\ acceptRetryRequired \in BOOLEAN
    /\ acceptRetryDue \in BOOLEAN
    /\ acceptRetryTimerArmed \in BOOLEAN
    /\ eventfdReadRetryRequired \in BOOLEAN
    /\ loopPhase \in LoopPhases
    /\ stopping \in BOOLEAN
    /\ ringFailed \in BOOLEAN
    /\ unbindStarted \in BOOLEAN
    /\ disposeStarted \in BOOLEAN
    /\ unbindTimeoutElapsed \in BOOLEAN
    /\ ringDisposed \in BOOLEAN
    /\ availableConnFds \subseteq ConnFdUniverse
    /\ availableRecvTokens \subseteq RecvTokenUniverse
    /\ availableSendTokens \subseteq SendTokenUniverse
    /\ releasedConnFds \subseteq ConnFdUniverse
    /\ acceptChannelDrops \in Nat
    /\ fileSlotOwner \in [AllFileSlots -> ({OwnerFree, OwnerListen, OwnerEvent} \cup [kind : {"conn"}, slot : Slots, gen : 0..MaxConnGen])]
    /\ \A f \in AllFileSlots : IsConnOwner(fileSlotOwner[f])
    /\ nextFileSlot \in 2..(SlotCount + 2)
    /\ freeFileSlots \in Seq(ConnFileSlots)
    /\ UniqueSeq(freeFileSlots)
    /\ disposeDrainCompleted \in BOOLEAN
    /\ disposeOverflowBacklog \in Seq([kind : AllowedCqeKinds,
                                       slot : Slots \cup {NoSlot},
                                       gen : 0..MaxConnGen,
                                       epoch : 0..MaxRecvEpoch,
                                       outcome : CqeOutcomes,
                                       more : BOOLEAN,
                                       token : ConnFdUniverse \cup RecvTokenUniverse \cup SendTokenUniverse \cup {NoToken},
                                       parts : 0..2])
    /\ \A i \in 1..Len(disposeOverflowBacklog) : IsCqe(disposeOverflowBacklog[i])
    /\ Len(disposeOverflowBacklog) <= DisposeOverflowHiddenBacklogModelLimit
    /\ disposeOverflowTrackedFds \subseteq ConnFdUniverse
    /\ disposeOverflowPreDrainEnterCount \in 0..DisposeOverflowDrainRoundLimit
    /\ dispatchLastKind \in DispatchKinds
    /\ dispatchLastMatched \in BOOLEAN
    /\ dispatchLastApplied \in BOOLEAN

SlotPartition ==
    /\ SeqToSet(freeSlots) = { s \in Slots : slotState[s] = "free" }
    /\ \A s \in Slots : slotState[s] = "free" => slotFd[s] = NoFd

RecvArmedHasLiveFdRef ==
    \A s \in Slots :
        recvArmed[s] # "none" =>
            /\ slotState[s] # "free"
            /\ recvFdRef[s] = ConnFdRef(s, slotFd, slotFile)
            /\ recvFdRef[s] \in ConnFdUniverse \cup ConnFileSlots

ReleasedSlotsNoLiveHandles ==
    \A s \in Slots :
        slotState[s] = "free" =>
            /\ recvArmed[s] = "none"
            /\ sendInFlight[s] = "none"
            /\ cancelInFlight[s] = "none"
            /\ sendReady \cap {s} = {}
            /\ pendingRecv[s] = << >>
            /\ sendPending[s] = << >>
            /\ sendPin[s] = NoToken
            /\ s \notin recvRetrySet

\* Traceability: ListenerRecvRetryState.OnConnectionReleased performs the
\* mandatory final-release purge of recv-cancel retry slot references before the
\* live slot is cleared and returned to the free list.
ReleasedSlotsNoCancelRetryReference ==
    \A s \in Slots : slotState[s] = "free" => s \notin recvCancelRetrySet

LiveDeferredRef(slot, gen) ==
    /\ slot \in Slots
    /\ slotState[slot] # "free"
    /\ slotGen[slot] = gen

NoStaleDeferredReference ==
    /\ \A i \in 1..Len(recvResubmitQueue) : LiveDeferredRef(recvResubmitQueue[i].slot, recvResubmitQueue[i].gen)
    /\ \A ref \in sendRetrySet : LiveDeferredRef(ref.slot, ref.gen)
    /\ \A s \in recvCancelRetrySet : slotGen[s] = cancelRetryOriginGen[s]

GenerationSafeDispatch ==
    ~(dispatchLastKind # "none" /\ ~dispatchLastMatched /\ dispatchLastApplied)

FixedFileSafety ==
    /\ \A s \in Slots : slotState[s] # "free" /\ slotFile[s] # NoFile => fileSlotOwner[slotFile[s]] = ConnOwner(s, slotGen[s])
    /\ \A s \in Slots : recvArmed[s] # "none" /\ recvFdRef[s] \in ConnFileSlots => fileSlotOwner[recvFdRef[s]] = ConnOwner(s, slotGen[s])
    /\ \A s \in Slots : sendInFlight[s] # "none" /\ sendOpFd[s] \in ConnFileSlots => fileSlotOwner[sendOpFd[s]] = ConnOwner(s, slotGen[s])
    /\ \A s \in Slots : cancelInFlight[s] # "none" /\ cancelFd[s] \in ConnFileSlots => fileSlotOwner[cancelFd[s]] = ConnOwner(s, slotGen[s])

RecvTokenOrder ==
    \A s \in Slots : slotState[s] # "free" => recvDelivered[s] \o pendingRecv[s] = recvSeen[s]

SendTokenOrder ==
    \A s \in Slots : slotState[s] # "free" =>
        /\ sendSent[s] \o sendPending[s] = sendApp[s]
        /\ IF sendPending[s] = << >>
              THEN sendHeadRemaining[s] = 0
              ELSE sendHeadRemaining[s] \in 1..SendParts(Head(sendPending[s]))

SendPinSafety ==
    \A s \in Slots :
        IF sendPin[s] = NoToken
            THEN TRUE
            ELSE /\ sendPending[s] # << >>
                 /\ sendPin[s] = Head(sendPending[s])
                 /\ sendPin[s] \in LargeSendTokens

FdAccounting ==
    LET live == LiveConnFdSet(slotState, slotFd)
        visible == AcceptCqeFdSet(cqQueue)
        overflow == AcceptCqeFdSet(disposeOverflowBacklog)
    IN /\ availableConnFds \cap releasedConnFds = {}
       /\ availableConnFds \cap live = {}
       /\ availableConnFds \cap visible = {}
       /\ availableConnFds \cap overflow = {}
       /\ releasedConnFds \cap live = {}
       /\ releasedConnFds \cap visible = {}
       /\ releasedConnFds \cap overflow = {}
       /\ live \cap visible = {}
       /\ live \cap overflow = {}
       /\ visible \cap overflow = {}
       /\ availableConnFds \cup releasedConnFds \cup live \cup visible \cup overflow = ConnFdUniverse

RingCloseAfterDisposeDrain ==
    (~FixDisposeAcceptedCqeLeak) \/ (~ringDisposed) \/ disposeDrainCompleted

DisposeOverflowTrackedAcceptedFdsReleased ==
    (~ScenarioDisposeAcceptedOverflowFocused)
        \/ (~FixDisposeOverflowDrain)
        \/ (~DisposeOverflowGuaranteedProgress)
        \/ ~(Cardinality(disposeOverflowTrackedFds) <= DisposeOverflowDrainRoundLimit + 1)
        \/ (~ringDisposed)
        \/ disposeOverflowTrackedFds \subseteq releasedConnFds

\* Traceability: ProcessRecvCancelRetry re-resolves the live slot and requires
\* both generation and recv epoch to match the stored origin before it can
\* submit a cancel retry.
CancelRetryTracksRecvEpoch ==
    \A s \in recvCancelRetrySet :
        /\ slotGen[s] = cancelRetryOriginGen[s]
        /\ recvEpoch[s] = cancelRetryOriginEpoch[s]
NoStaleRetryCrossGeneration ==
    \A s \in recvCancelRetrySet : slotGen[s] = cancelRetryOriginGen[s]

ReceiveRetryPassEventuallyArms ==
    \A s \in Slots :
        []((slotState[s] = "open" /\ recvRetryPassActive /\ s \in recvRetrySet)
            => <>(slotState[s] # "open" \/ recvArmed[s] = "armed"))

\* Traceability: RetryFailedRecvs / RunRetryPass must not strand a live,
\* generation+epoch-matching armed recv by silently dropping its cancel-retry
\* marker merely because recvRetrySet is empty.
ActionableMatchingCancelRetry(s) ==
    /\ s \in recvCancelRetrySet
    /\ slotState[s] = "open"
    /\ slotGen[s] = cancelRetryOriginGen[s]
    /\ recvArmed[s] # "none"
    /\ recvEpoch[s] = cancelRetryOriginEpoch[s]
    /\ cancelInFlight[s] = "none"
    /\ sqAvail > 0

MatchingCancelRetryStillLive(s) ==
    /\ slotState[s] = "open"
    /\ slotGen[s] = cancelRetryOriginGen[s]
    /\ recvArmed[s] # "none"
    /\ recvEpoch[s] = cancelRetryOriginEpoch[s]

MatchingCancelRetryEventuallyIssuesOrResolves ==
    \A s \in Slots :
        [](ActionableMatchingCancelRetry(s)
            => <>(cancelInFlight[s] # "none" \/ ~MatchingCancelRetryStillLive(s)))

CancelRetryEventuallyIssuesOrDrops ==
    \A s \in Slots :
        []((s \in recvCancelRetrySet /\ (recvRetrySet # {} \/ recvRetryPassActive))
            => <>(s \notin recvCancelRetrySet \/ cancelInFlight[s] # "none"))

CancelRetryIssuedFromOrigin ==
    [](\A s \in Slots :
        cancelInFlight[s] # "none" /\ cancelRetryOriginEpoch[s] # 0
            => /\ cancelTargetEpoch[s] = cancelRetryOriginEpoch[s]
               /\ slotGen[s] = cancelRetryOriginGen[s])

CancelRetryEventuallySafe ==
    /\ CancelRetryEventuallyIssuesOrDrops
    /\ CancelRetryIssuedFromOrigin

EventuallyCancelRetryIssued ==
    <>(\E s \in Slots : cancelRetryOriginEpoch[s] # 0 /\ cancelInFlight[s] # "none")

EventuallyStaleRecvDispatch ==
    <>(dispatchLastKind = "recv" /\ ~dispatchLastMatched /\ ~dispatchLastApplied)

EventuallyFixedFileConnection ==
    <>(\E s \in Slots : slotState[s] # "free" /\ slotFile[s] # NoFile)

EventuallyRecvTokenObserved ==
    <>(\E s \in Slots : recvSeen[s] # << >>)

EventuallyRecvResubmitDrainRearms ==
    <>(\E s \in Slots :
        /\ recvEpoch[s] > 1
        /\ recvArmed[s] = "armed"
        /\ recvRearmPending[s] = FALSE
        /\ pendingRecv[s] = << >>
        /\ recvDelivered[s] = recvSeen[s])

EventuallySlotReleased ==
    <>(\E s \in Slots : slotState[s] = "free" /\ s \in SeqToSet(freeSlots) /\ releasedConnFds # {})

EventuallySlotReused ==
    <>(\E s \in Slots : slotGen[s] > 1)

RecvResubmitQueueBounded ==
    Len(recvResubmitQueue) <= 1

CloseRequestQueueBounded ==
    Len(closeRequestQueue) <= 1

SendRetryEventuallyResumes ==
    \A s \in Slots :
        []((slotState[s] = "open" /\ sendWaitingRetry[s])
            => <>(slotState[s] # "open" \/ ~sendWaitingRetry[s]))

AcceptRetryEventuallyRearms ==
    [](AcceptRetryDueNow
        => <>(ringDisposed
            \/ (acceptArmed = "armed" /\ ~acceptRetryRequired)))

\* Production intentionally stops rearming the eventfd read once cancellation /
\* disposal wins, so this obligation is scoped to the retry-required state
\* reached after an SQ-full rearm failure rather than every unarmed state.
\* Merely clearing the retry flag is not enough; the read must become armed
\* again or the ring must dispose.
EventFdWakeEventuallyDrains ==
    [](eventfdReadRetryRequired
        => <>(ringDisposed
            \/ (eventfdReadArmed = "armed" /\ ~eventfdReadRetryRequired)))

CloseEventuallyReleasesSlot ==
    \A s \in Slots :
        []((slotState[s] = "closing") => <>(slotState[s] = "free" \/ ringDisposed))

UnbindEventuallyStops ==
    []((unbindStarted) => <>(loopPhase = "stopped" \/ ringDisposed))

DisposeEventuallyClosesRing ==
    []((disposeStarted) => <> ringDisposed)

====
