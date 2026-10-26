---- MODULE Teardown ----
EXTENDS Naturals, Sequences, FiniteSets

\* Scope / traceability: the disposal drain that decides when memory handed to
\* the kernel may be released.
\*   - src/Kestrel.Transport.IoUring/Ring.cs
\*     (TryGetSqe counts SQEs handed out; AdvanceCompletion counts completions
\*      without IORING_CQE_F_MORE; OutstandingOperations is their difference)
\*   - src/Kestrel.Transport.IoUring/Transport/IoUringConnectionListener.cs
\*     (RunIoLoop finally / DisposeAsync -> DrainKernelOperations,
\*      AbortRemainingConnections, SubmitTeardownCancels, DrainTeardownCompletions,
\*      HandleAccept closing sockets accepted while stopping, Quarantine)
\*
\* Kernel facts the model encodes (Linux io_uring):
\*   - close(ring_fd) returns before the kernel has cancelled and finished its
\*     requests, so it gives no ownership back; only a request's final completion
\*     does. The pre-fix code trusted close (MutateTrustClose).
\*   - A multishot request (accept, provided-buffer recv) posts any number of
\*     completions with F_MORE and exactly one final one.
\*   - A cancel by user_data can race a request that is not cancellable yet
\*     (ENOENT), so the drain re-issues cancels while requests remain.
\*   - IORING_ASYNC_CANCEL_ANY|ALL (Linux 5.19+, BulkCancel) reaches every
\*     request, including ones the listener does not name. Without it a request
\*     nobody targets ("foreign") ends only by itself.
\*   - Shutting a socket down completes the receive and send parked on it.
\*
\* Abstractions: one request per kind, each submitted a bounded number of times;
\* the CQ is a bounded FIFO; the deadline is an unfair action, so liveness is
\* about what the drain achieves on its own.

CONSTANTS
    BulkCancel,
    IncludeForeign,
    Fds,
    MaxCq,
    MutateTrustClose,
    MutateFreeOnTimeout,
    MutateCountEveryCqe

ASSUME
    /\ BulkCancel \in BOOLEAN
    /\ IncludeForeign \in BOOLEAN
    /\ MaxCq \in Nat \ {0}
    /\ MutateTrustClose \in BOOLEAN
    /\ MutateFreeOnTimeout \in BOOLEAN
    /\ MutateCountEveryCqe \in BOOLEAN

Ops == {"accept", "eventfd", "recv", "send"} \cup (IF IncludeForeign THEN {"foreign"} ELSE {})
Multishot == {"accept", "recv"}
\* Named by SubmitTeardownCancels' targeted cancels.
Targeted == {"accept", "eventfd"}
\* Completed by the socket shutdowns in AbortRemainingConnections.
EndedByShutdown == {"recv", "send"}
\* A request that cannot end unless something cancels it.
NeverEndsAlone == {"foreign"}
\* The send loop may flush one more send to a connection while the drain runs.
MaxSubmits(o) == IF o = "send" THEN 2 ELSE 1
NoFd == "none"
Phases == {"serving", "draining", "quiesced", "quarantined", "released"}
OpStates == {"idle", "armed", "posted", "done"}

CancelReaches(o) == o \in EndedByShutdown \/ o \in Targeted \/ BulkCancel

VARIABLES
    phase,
    op,            \* armed: the kernel owns the request and may touch its memory;
                   \* posted: its final completion is in the CQ, not consumed yet
    cancelled,     \* a cancel or shutdown currently applies to the request
    submits,
    cq,            \* << [op |-> o, fd |-> f, final |-> b] >>
    acquired,      \* Ring ledger: SQEs handed out
    consumedFinal, \* Ring ledger: final completions consumed
    postedFds,     \* sockets the kernel accepted
    closedFds,     \* closed by the drain (HandleAccept while stopping)
    deliveredFds,  \* became connections, closed through connection teardown
    quiescedSeen

Vars == << phase, op, cancelled, submits, cq, acquired, consumedFinal,
           postedFds, closedFds, deliveredFds, quiescedSeen >>

Ledger == acquired - consumedFinal
Cqe(o, f, final) == [op |-> o, fd |-> f, final |-> final]

Init ==
    /\ phase = "serving"
    /\ op = [o \in Ops |-> "idle"]
    /\ cancelled = [o \in Ops |-> FALSE]
    /\ submits = [o \in Ops |-> 0]
    /\ cq = << >>
    /\ acquired = 0
    /\ consumedFinal = 0
    /\ postedFds = {}
    /\ closedFds = {}
    /\ deliveredFds = {}
    /\ quiescedSeen = FALSE

\* --- transport -------------------------------------------------------------

Submit(o) ==
    /\ \/ phase = "serving"
       \/ phase = "draining" /\ o = "send"
    /\ op[o] \in {"idle", "done"}
    /\ submits[o] < MaxSubmits(o)
    /\ op' = [op EXCEPT ![o] = "armed"]
    \* A send started during the drain targets a socket that is already shut down.
    /\ cancelled' = [cancelled EXCEPT ![o] = (phase = "draining")]
    /\ submits' = [submits EXCEPT ![o] = @ + 1]
    /\ acquired' = acquired + 1
    /\ UNCHANGED << phase, cq, consumedFinal, postedFds, closedFds, deliveredFds, quiescedSeen >>

\* Disposal starts: abort the connections, shut their sockets down and issue the cancels.
StartDrain ==
    /\ phase = "serving"
    /\ phase' = "draining"
    /\ cancelled' = [o \in Ops |-> cancelled[o] \/ (op[o] = "armed" /\ CancelReaches(o))]
    /\ UNCHANGED << op, submits, cq, acquired, consumedFinal, postedFds, closedFds,
                    deliveredFds, quiescedSeen >>

ReissueCancels ==
    /\ phase = "draining"
    /\ \E o \in Ops : op[o] = "armed" /\ ~cancelled[o] /\ CancelReaches(o)
    /\ cancelled' = [o \in Ops |-> cancelled[o] \/ (op[o] = "armed" /\ CancelReaches(o))]
    /\ UNCHANGED << phase, op, submits, cq, acquired, consumedFinal, postedFds, closedFds,
                    deliveredFds, quiescedSeen >>

\* DispatchCompletion: a socket accepted while serving becomes a connection; one
\* accepted while stopping is closed. Only a final completion ends a request
\* (MutateCountEveryCqe counts every completion, like a ledger blind to F_MORE).
ConsumeCqe ==
    /\ phase \in {"serving", "draining"}
    /\ cq # << >>
    /\ LET c == Head(cq) IN
        /\ cq' = Tail(cq)
        /\ IF c.fd = NoFd
              THEN UNCHANGED << closedFds, deliveredFds >>
              ELSE IF phase = "serving"
                      THEN /\ deliveredFds' = deliveredFds \cup {c.fd}
                           /\ UNCHANGED closedFds
                      ELSE /\ closedFds' = closedFds \cup {c.fd}
                           /\ UNCHANGED deliveredFds
        /\ op' = IF c.final THEN [op EXCEPT ![c.op] = "done"] ELSE op
        /\ consumedFinal' = IF c.final \/ MutateCountEveryCqe THEN consumedFinal + 1 ELSE consumedFinal
    /\ UNCHANGED << phase, cancelled, submits, acquired, postedFds, quiescedSeen >>

\* The ledger is zero: seal the ring.
ObserveQuiescence ==
    /\ phase = "draining"
    /\ Ledger = 0
    /\ phase' = "quiesced"
    /\ quiescedSeen' = TRUE
    /\ UNCHANGED << op, cancelled, submits, cq, acquired, consumedFinal, postedFds,
                    closedFds, deliveredFds >>

\* TeardownDrainTimeout passed first: quarantine, release nothing.
DeadlineExpires ==
    /\ phase = "draining"
    /\ Ledger # 0
    /\ phase' = "quarantined"
    /\ UNCHANGED << op, cancelled, submits, cq, acquired, consumedFinal, postedFds,
                    closedFds, deliveredFds, quiescedSeen >>

\* Close the ring, unmap, unpin.
Release ==
    /\ phase = "quiesced"
    /\ phase' = "released"
    /\ UNCHANGED << op, cancelled, submits, cq, acquired, consumedFinal, postedFds,
                    closedFds, deliveredFds, quiescedSeen >>

\* Pre-fix order: drain what is visible, close the ring and free, trusting close.
TrustCloseBug ==
    /\ MutateTrustClose
    /\ phase = "draining"
    /\ cq = << >>
    /\ phase' = "released"
    /\ UNCHANGED << op, cancelled, submits, cq, acquired, consumedFinal, postedFds,
                    closedFds, deliveredFds, quiescedSeen >>

\* Free after the deadline (what a 200 ms drain-then-free does).
FreeOnTimeoutBug ==
    /\ MutateFreeOnTimeout
    /\ phase = "quarantined"
    /\ phase' = "released"
    /\ UNCHANGED << op, cancelled, submits, cq, acquired, consumedFinal, postedFds,
                    closedFds, deliveredFds, quiescedSeen >>

\* --- kernel ----------------------------------------------------------------

KernelAcceptFd(f) ==
    /\ op["accept"] = "armed"
    /\ f \notin postedFds
    /\ Len(cq) < MaxCq
    /\ cq' = Append(cq, Cqe("accept", f, FALSE))
    /\ postedFds' = postedFds \cup {f}
    /\ UNCHANGED << phase, op, cancelled, submits, acquired, consumedFinal, closedFds,
                    deliveredFds, quiescedSeen >>

KernelRecvData ==
    /\ op["recv"] = "armed"
    /\ ~cancelled["recv"]
    /\ Len(cq) < MaxCq
    /\ cq' = Append(cq, Cqe("recv", NoFd, FALSE))
    /\ UNCHANGED << phase, op, cancelled, submits, acquired, consumedFinal, postedFds,
                    closedFds, deliveredFds, quiescedSeen >>

\* The request ends (cancelled, shut down, or on its own) and posts its final completion.
KernelFinish(o) ==
    /\ op[o] = "armed"
    /\ cancelled[o] \/ o \notin NeverEndsAlone
    /\ Len(cq) < MaxCq
    /\ op' = [op EXCEPT ![o] = "posted"]
    /\ cq' = Append(cq, Cqe(o, NoFd, TRUE))
    /\ UNCHANGED << phase, cancelled, submits, acquired, consumedFinal, postedFds,
                    closedFds, deliveredFds, quiescedSeen >>

\* A targeted or bulk cancel raced a request that was not cancellable yet.
KernelMissesCancel(o) ==
    /\ op[o] = "armed"
    /\ cancelled[o]
    /\ o \notin EndedByShutdown
    /\ cancelled' = [cancelled EXCEPT ![o] = FALSE]
    /\ UNCHANGED << phase, op, submits, cq, acquired, consumedFinal, postedFds,
                    closedFds, deliveredFds, quiescedSeen >>

Next ==
    \/ \E o \in Ops : Submit(o)
    \/ StartDrain
    \/ ReissueCancels
    \/ ConsumeCqe
    \/ ObserveQuiescence
    \/ DeadlineExpires
    \/ Release
    \/ TrustCloseBug
    \/ FreeOnTimeoutBug
    \/ \E f \in Fds : KernelAcceptFd(f)
    \/ KernelRecvData
    \/ \E o \in Ops : KernelFinish(o)
    \/ \E o \in Ops : KernelMissesCancel(o)

Spec == Init /\ [][Next]_Vars

\* The drain keeps consuming, re-issuing cancels and observing the ledger; the
\* kernel eventually completes a request a cancel or shutdown keeps applying to
\* (strong fairness: a cancel can be missed, but not forever). The deadline and
\* the dispose request itself get no fairness.
LiveSpec ==
    /\ Spec
    /\ WF_Vars(ConsumeCqe)
    /\ WF_Vars(ReissueCancels)
    /\ WF_Vars(ObserveQuiescence)
    /\ WF_Vars(Release)
    /\ \A o \in Ops : SF_Vars(cancelled[o] /\ KernelFinish(o))

\* --- properties ------------------------------------------------------------

TypeOK ==
    /\ phase \in Phases
    /\ op \in [Ops -> OpStates]
    /\ cancelled \in [Ops -> BOOLEAN]
    /\ submits \in [Ops -> 0..2]
    /\ Len(cq) <= MaxCq
    /\ acquired \in Nat
    /\ consumedFinal \in Nat
    /\ postedFds \subseteq Fds
    /\ closedFds \subseteq Fds
    /\ deliveredFds \subseteq Fds
    /\ quiescedSeen \in BOOLEAN

\* The ledger is exactly the number of requests not yet ended and consumed.
LedgerCountsLiveRequests ==
    Ledger = Cardinality({o \in Ops : op[o] \in {"armed", "posted"}})

\* Observed quiescence means the kernel owns nothing and nothing is left to consume.
QuiescedMeansKernelIdle ==
    phase \in {"quiesced", "released"} /\ quiescedSeen =>
        /\ \A o \in Ops : op[o] # "armed"
        /\ cq = << >>

\* The memory safety goal: nothing is released while the kernel may still use it.
NoReleaseWhileKernelOwns ==
    phase = "released" => \A o \in Ops : op[o] # "armed"

\* Every socket the kernel accepted is closed or owned by a connection by release.
NoAcceptedFdLeak ==
    phase = "released" => postedFds \subseteq closedFds \cup deliveredFds

\* Only an observed quiescence may lead to release; a quarantined ring is kept.
ReleaseOnlyAfterQuiescence ==
    phase = "released" => quiescedSeen

\* With every request reachable by a cancel or a shutdown, the drain gets the
\* ring idle on its own, without relying on the deadline.
DrainEnds ==
    (phase = "draining") ~> (phase \in {"quiesced", "released", "quarantined"})

====
