---- MODULE IoUringRing ----
EXTENDS Naturals, Sequences

\* Traceability to the current implementation:
\*   - Stage abstracts SubmissionQueue.TryGetSqe and the forwarding
\*     Ring.TryGetSqe. The guard OutstandingCount < SqEntries is the model form
\*     of `_sqeTail - Volatile.Read(_head) < *_ringEntries`.
\*   - Flush abstracts SubmissionQueue.Flush plus the Ring.Submit /
\*     Ring.SubmitAndWait call sites that first publish staged SQEs to the
\*     kernel-visible SQ ring.
\*   - BeginEnterCall and Enter* abstract Ring.Submit, Ring.SubmitAndWait, and
\*     Ring.Enter. With FixEnterSubmitBudget = FALSE, the model preserves the
\*     legacy latest-Flush budgeting and the zero-budget retry on EINTR /
\*     EEXIST. With FixEnterSubmitBudget = TRUE, the model follows the
\*     corrected SubmissionQueue.PendingSubmit-based budget and retry
\*     re-accounting, which uses only kernel-visible SQ head/tail state.
\*     FixEnterDecision = FALSE models the legacy public-submit decision that
\*     only re-enters after a new Flush delta; FixEnterDecision = TRUE models
\*     the corrected non-SQPOLL decision that re-reads the same
\*     SubmissionQueue.PendingSubmit / ready-count state to decide whether a
\*     submit-bearing public enter should run. The focused model still excludes
\*     SQPOLL-specific wakeup / poll-thread behavior, where production code
\*     intentionally remains Flush-gated.
\*   - AdvanceCompletion abstracts CompletionQueue.TryPeekCompletion,
\*     CompletionQueue.AdvanceHead, and Ring.AdvanceCompletion.
\*   - ObserveOverflowAndFail abstracts CompletionQueue.OverflowCount together
\*     with the listener fail-safe in
\*     IoUringConnectionListener.ProcessCompletions.
\*
\* Focused assumptions used for phases 1-2:
\*   1. Volatile.Read / Volatile.Write are modeled as sequentially consistent
\*      cursor observations and updates; relaxed-memory or torn-read behaviors
\*      are excluded.
\*   2. A single managed producer sequence is modeled around Stage/Flush,
\*      matching Ring.SubmitLock together with Ring.TryGetSqe and
\*      SubmissionQueue.Flush. Concurrent staging while inside the private Enter
\*      retry loop is excluded, so staged-only SQEs remain outside ReadyCount
\*      until a later Flush publishes them.
\*   3. A successful enter consumes SQEs in ring order: either every SQE the
\*      call asked the kernel to submit, or a documented positive proper prefix.
\*      Generated completions first enter a kernel backlog and later become
\*      user-visible CQEs one-for-one.
\*   4. CQ-full pressure does not itself imply loss. Instead the model keeps a
\*      kernel CQ backlog and a separate nondeterministic drop / OOM action.
\*      Once CQ overflow becomes visible, the listener observes it before
\*      consuming more CQEs and fails fast, so further progress is blocked until
\*      ObserveOverflowAndFail runs.
\*
\* Exclusions: setup fallback logic, SQPOLL / SQ_WAKEUP / GETEVENTS side
\* effects, minComplete blocking semantics, unexpected-errno exception
\* behavior, registered files, buffer rings, payload data, network protocol
\* behavior, disposal, and cancellation. Completion-consumer progress is an
\* assumption of LiveSpec; the real EBUSY/CQ-overflow coupling is deferred.
\* Legacy zero-submit retries after EINTR / EEXIST remain modeled when
\* FixEnterSubmitBudget = FALSE so the expected-counterexample configs still
\* witness the buggy behavior. Concurrent staging/Flush while another thread is
\* inside Enter is a tracked refinement obligation for the integrated model
\* because Ring.Enter runs outside SubmitLock and corrected
\* SubmissionQueue.PendingSubmit / ready-count accounting intentionally excludes
\* staged-only SQEs until a later Flush.
\*
\* Compositional note:
\*   Listener.tla does not compose this module mechanically; it uses an
\*   abstract sqAvail capacity instead. Any combined reasoning across the
\*   modules is therefore compositional, not a proved refinement, and the
\*   Enter-outside-SubmitLock behavior above remains a residual integration
\*   obligation.

CONSTANTS
    SqEntries,
    CqEntries,
    MaxRequests,
    FixEnterSubmitBudget,
    FixEnterDecision

RequestIds == 0..(MaxRequests - 1)

EnterOutcomes ==
    {
        "idle",
        "begin",
        "ok-full",
        "ok-partial",
        "ok-short",
        "ok-zero",
        "eintr",
        "eagain",
        "ebusy",
        "eexist"
    }

Min2(a, b) == IF a < b THEN a ELSE b

IntRangeSeq(lo, hi) ==
    IF hi < lo
        THEN << >>
        ELSE [i \in 1..(hi - lo + 1) |-> lo + i - 1]

PrefixSet(n) == IF n = 0 THEN {} ELSE 0..(n - 1)

Unique(s) ==
    \A i, j \in 1..Len(s) : i /= j => s[i] /= s[j]

Take(s, n) ==
    IF n = 0 THEN << >> ELSE SubSeq(s, 1, n)

Drop(s, n) ==
    IF n >= Len(s) THEN << >> ELSE SubSeq(s, n + 1, Len(s))

TakeLast(s, n) ==
    IF n = 0 THEN << >> ELSE SubSeq(s, Len(s) - n + 1, Len(s))

DropLast(s, n) ==
    IF n >= Len(s) THEN << >> ELSE SubSeq(s, 1, Len(s) - n)

VARIABLES
    nextReqId,
    stagedHead,
    stagedTail,
    kernelTail,
    kernelHead,
    stagedOnly,
    visible,
    newVisibleSinceLastCall,
    inEnter,
    enterBudget,
    lastEnterOutcome,
    cqHead,
    cqTail,
    cqPending,
    cqBacklog,
    cqConsumed,
    cqLost,
    cqOverflow,
    failed

Vars ==
    << nextReqId,
       stagedHead,
       stagedTail,
       kernelTail,
       kernelHead,
       stagedOnly,
       visible,
       newVisibleSinceLastCall,
       inEnter,
       enterBudget,
       lastEnterOutcome,
       cqHead,
       cqTail,
       cqPending,
       cqBacklog,
       cqConsumed,
       cqLost,
       cqOverflow,
       failed >>

VisibleCount == kernelTail - kernelHead
StagedOnlyCount == stagedTail - stagedHead
OutstandingCount == stagedTail - kernelHead
ReadyCount == Len(visible)
FreeCqSlots == CqEntries - Len(cqPending)
SubmittedSet == PrefixSet(kernelHead)
FlushedSet == PrefixSet(kernelTail)

CanContinue ==
    /\ ~failed
    /\ cqOverflow = 0

Init ==
    /\ nextReqId = 0
    /\ stagedHead = 0
    /\ stagedTail = 0
    /\ kernelTail = 0
    /\ kernelHead = 0
    /\ stagedOnly = << >>
    /\ visible = << >>
    /\ newVisibleSinceLastCall = 0
    /\ inEnter = FALSE
    /\ enterBudget = 0
    /\ lastEnterOutcome = "idle"
    /\ cqHead = 0
    /\ cqTail = 0
    /\ cqPending = << >>
    /\ cqBacklog = << >>
    /\ cqConsumed = << >>
    /\ cqLost = << >>
    /\ cqOverflow = 0
    /\ failed = FALSE

\* SubmissionQueue.TryGetSqe.
CanStage ==
    /\ CanContinue
    /\ ~inEnter
    /\ nextReqId < MaxRequests
    /\ OutstandingCount < SqEntries

Stage ==
    /\ CanStage
    /\ nextReqId' = nextReqId + 1
    /\ stagedTail' = stagedTail + 1
    /\ stagedOnly' = Append(stagedOnly, nextReqId)
    /\ UNCHANGED
        << stagedHead,
           kernelTail,
           kernelHead,
           visible,
           newVisibleSinceLastCall,
           inEnter,
           enterBudget,
           lastEnterOutcome,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* SubmissionQueue.Flush.
\* newVisibleSinceLastCall keeps the legacy latest-Flush bookkeeping available
\* for the expected-counterexample configs. It intentionally accumulates across
\* multiple Flush steps until BeginEnterCall resets it, which is stronger than
\* the buggy source but still weaker than the corrected
\* SubmissionQueue.PendingSubmit / ready-count accounting that re-reads only the
\* kernel-visible SQ cursors.
CanFlush ==
    /\ CanContinue
    /\ ~inEnter
    /\ Len(stagedOnly) > 0

Flush ==
    /\ CanFlush
    /\ LET flushed == Len(stagedOnly) IN
       /\ stagedHead' = stagedTail
       /\ kernelTail' = kernelTail + flushed
       /\ visible' = visible \o stagedOnly
       /\ stagedOnly' = << >>
       /\ newVisibleSinceLastCall' = newVisibleSinceLastCall + flushed
    /\ UNCHANGED
        << nextReqId,
           stagedTail,
           kernelHead,
           inEnter,
           enterBudget,
           lastEnterOutcome,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* Ring.Submit / Ring.SubmitAndWait compute a public Enter call only after
\* Flush. FixEnterDecision abstracts whether non-SQPOLL callers decide to
\* re-enter from only the legacy latest-Flush delta or from the corrected
\* kernel-visible pending budget (modeled here as ReadyCount > 0).
\* FixEnterSubmitBudget
\* abstracts whether the `toSubmit` value and EINTR / EEXIST retry budget come
\* from the legacy latest Flush delta or the corrected kernel-visible pending
\* count (SubmissionQueue.PendingSubmit).
CanBeginEnter ==
    /\ CanContinue
    /\ ~inEnter
    /\ Len(stagedOnly) = 0
    /\ IF FixEnterDecision
          THEN ReadyCount > 0
          ELSE newVisibleSinceLastCall > 0

BeginEnterCall ==
    /\ CanBeginEnter
    /\ inEnter' = TRUE
    /\ enterBudget' =
        IF FixEnterSubmitBudget
            THEN ReadyCount
            ELSE newVisibleSinceLastCall
    /\ newVisibleSinceLastCall' = 0
    /\ lastEnterOutcome' = "begin"
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

RetryBudget ==
    IF FixEnterSubmitBudget
        THEN ReadyCount
        ELSE 0

\* Ring.Enter success path. This is "full" when enterBudget
\* covers all ready SQEs and "partial" when older visible SQEs remain after a
\* later call forwards only a smaller newly-flushed count. CQEs first enter the
\* kernel backlog and are published to the user-visible CQ by a later action.
EnterOk ==
    /\ CanContinue
    /\ inEnter
    /\ LET ready == ReadyCount
           consumed == Min2(enterBudget, ready)
           submittedNow == Take(visible, consumed)
       IN
       /\ consumed > 0
       /\ kernelHead' = kernelHead + consumed
       /\ visible' = Drop(visible, consumed)
       /\ cqBacklog' = cqBacklog \o submittedNow
       /\ inEnter' = FALSE
       /\ enterBudget' = 0
       /\ lastEnterOutcome' =
            IF consumed = ready
                THEN "ok-full"
                ELSE "ok-partial"
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           stagedOnly,
           newVisibleSinceLastCall,
           cqHead,
           cqTail,
           cqPending,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* io_uring_enter may return a positive value smaller than `toSubmit`. This
\* action models that documented short successful submission by consuming a
\* nondeterministic positive proper prefix of the requested SQEs.
EnterOkShort ==
    /\ CanContinue
    /\ inEnter
    /\ LET requested == Min2(enterBudget, ReadyCount) IN
       /\ requested > 1
       /\ \E consumed \in 1..(requested - 1) :
            LET submittedNow == Take(visible, consumed) IN
            /\ kernelHead' = kernelHead + consumed
            /\ visible' = Drop(visible, consumed)
            /\ cqBacklog' = cqBacklog \o submittedNow
            /\ inEnter' = FALSE
            /\ enterBudget' = 0
            /\ lastEnterOutcome' = "ok-short"
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           stagedOnly,
           newVisibleSinceLastCall,
           cqHead,
           cqTail,
           cqPending,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* Legacy Ring.Enter retry behavior can turn a retried call into a zero-submit
\* success because the buggy EINTR / EEXIST path clears the retry budget when
\* FixEnterSubmitBudget = FALSE.
EnterOkZero ==
    /\ CanContinue
    /\ inEnter
    /\ Min2(enterBudget, ReadyCount) = 0
    /\ inEnter' = FALSE
    /\ enterBudget' = 0
    /\ lastEnterOutcome' = "ok-zero"
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* Ring.Enter EINTR branch.
EnterEINTR ==
    /\ CanContinue
    /\ inEnter
    /\ inEnter' = TRUE
    /\ enterBudget' = RetryBudget
    /\ lastEnterOutcome' = "eintr"
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* Ring.Enter EAGAIN branch.
EnterEAGAIN ==
    /\ CanContinue
    /\ inEnter
    /\ inEnter' = FALSE
    /\ enterBudget' = 0
    /\ lastEnterOutcome' = "eagain"
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* Ring.Enter EBUSY branch.
EnterEBUSY ==
    /\ CanContinue
    /\ inEnter
    /\ inEnter' = FALSE
    /\ enterBudget' = 0
    /\ lastEnterOutcome' = "ebusy"
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* Ring.Enter EEXIST branch.
EnterEEXIST ==
    /\ CanContinue
    /\ inEnter
    /\ inEnter' = TRUE
    /\ enterBudget' = RetryBudget
    /\ lastEnterOutcome' = "eexist"
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* Kernel publication from its private CQ backlog into the user-visible CQ ring.
PublishBacklogToCq ==
    /\ CanContinue
    /\ ~inEnter
    /\ Len(cqBacklog) > 0
    /\ FreeCqSlots > 0
    /\ \E published \in 1..Min2(Len(cqBacklog), FreeCqSlots) :
        /\ cqTail' = cqTail + published
        /\ cqPending' = cqPending \o Take(cqBacklog, published)
        /\ cqBacklog' = Drop(cqBacklog, published)
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           inEnter,
           enterBudget,
           lastEnterOutcome,
           cqHead,
           cqConsumed,
           cqLost,
           cqOverflow,
           failed >>

\* CQ-full pressure does not itself drop entries; instead the kernel may
\* independently lose a suffix of unpublished completions, which increments the
\* visible overflow counter that the listener later observes.
DropBacklogOnOverflowOrOom ==
    /\ CanContinue
    /\ ~inEnter
    /\ Len(cqBacklog) > 0
    /\ FreeCqSlots = 0
    /\ \E lost \in 1..Len(cqBacklog) :
        LET lostIds == TakeLast(cqBacklog, lost) IN
        /\ cqBacklog' = DropLast(cqBacklog, lost)
        /\ cqLost' = lostIds \o cqLost
        /\ cqOverflow' = cqOverflow + lost
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           inEnter,
           enterBudget,
           lastEnterOutcome,
           cqHead,
           cqTail,
           cqPending,
           cqConsumed,
           failed >>

\* CompletionQueue.TryPeekCompletion + CompletionQueue.AdvanceHead and
\* Ring.AdvanceCompletion. The focused model advances one published CQE at a
\* time after the listener's pre-loop overflow check.
AdvanceCompletion ==
    /\ CanContinue
    /\ ~inEnter
    /\ Len(cqPending) > 0
    /\ cqHead' = cqHead + 1
    /\ cqConsumed' = Append(cqConsumed, Head(cqPending))
    /\ cqPending' = Drop(cqPending, 1)
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           inEnter,
           enterBudget,
           lastEnterOutcome,
           cqTail,
           cqBacklog,
           cqLost,
           cqOverflow,
           failed >>

\* CompletionQueue.OverflowCount +
\* IoUringConnectionListener.ProcessCompletions fail-safe.
ObserveOverflowAndFail ==
    /\ ~failed
    /\ cqOverflow > 0
    /\ failed' = TRUE
    /\ UNCHANGED
        << nextReqId,
           stagedHead,
           stagedTail,
           kernelTail,
           kernelHead,
           stagedOnly,
           visible,
           newVisibleSinceLastCall,
           inEnter,
           enterBudget,
           lastEnterOutcome,
           cqHead,
           cqTail,
           cqPending,
           cqBacklog,
           cqConsumed,
           cqLost,
           cqOverflow >>

Next ==
    \/ Stage
    \/ Flush
    \/ BeginEnterCall
    \/ EnterOk
    \/ EnterOkShort
    \/ EnterOkZero
    \/ EnterEINTR
    \/ EnterEAGAIN
    \/ EnterEBUSY
    \/ EnterEEXIST
    \/ PublishBacklogToCq
    \/ DropBacklogOnOverflowOrOom
    \/ AdvanceCompletion
    \/ ObserveOverflowAndFail

Spec ==
    Init /\ [][Next]_Vars

EnterPositiveProgress ==
    \/ EnterOk
    \/ EnterOkShort

\* Focused config constraint that removes transient Enter error outcomes so a
\* documented short success is the remaining liveness witness.
NoTransientEnterErrors ==
    lastEnterOutcome' \notin {"eintr", "eagain", "ebusy", "eexist"}

\* LiveSpec encodes the focused kernel/environment assumptions for the liveness
\* configs: callers eventually Flush locally staged SQEs before relying on
\* submission progress; callers that still can issue another public submit call
\* eventually do so; if unpublished CQEs sit in the kernel while user-visible CQ
\* space remains, the kernel eventually publishes some; if a positive-budget
\* successful Enter path is enabled infinitely often, the kernel eventually lets
\* one of those full or short successes make progress; and overflow is
\* eventually observed.
LiveSpec ==
    /\ Spec
    /\ WF_Vars(Flush)
    /\ WF_Vars(BeginEnterCall)
    /\ WF_Vars(PublishBacklogToCq)
    /\ SF_Vars(EnterPositiveProgress)
    /\ WF_Vars(ObserveOverflowAndFail)

TypeOK ==
    /\ SqEntries \in Nat
    /\ SqEntries > 0
    /\ CqEntries \in Nat
    /\ MaxRequests \in Nat
    /\ FixEnterSubmitBudget \in BOOLEAN
    /\ FixEnterDecision \in BOOLEAN
    /\ nextReqId \in 0..MaxRequests
    /\ stagedHead \in Nat
    /\ stagedTail \in Nat
    /\ kernelTail \in Nat
    /\ kernelHead \in Nat
    /\ stagedOnly \in Seq(RequestIds)
    /\ visible \in Seq(RequestIds)
    /\ newVisibleSinceLastCall \in Nat
    /\ inEnter \in BOOLEAN
    /\ enterBudget \in Nat
    /\ lastEnterOutcome \in EnterOutcomes
    /\ cqHead \in Nat
    /\ cqTail \in Nat
    /\ cqPending \in Seq(RequestIds)
    /\ cqBacklog \in Seq(RequestIds)
    /\ cqConsumed \in Seq(RequestIds)
    /\ cqLost \in Seq(RequestIds)
    /\ cqOverflow \in Nat
    /\ failed \in BOOLEAN
    /\ Unique(stagedOnly)
    /\ Unique(visible)
    /\ Unique(cqPending)
    /\ Unique(cqBacklog)
    /\ Unique(cqConsumed)
    /\ Unique(cqLost)

SqCursorRelation ==
    /\ kernelHead <= kernelTail
    /\ kernelTail = stagedHead
    /\ stagedHead <= stagedTail

CqCursorRelation ==
    /\ cqHead <= cqTail

SqNoOverwrite ==
    /\ OutstandingCount <= SqEntries
    /\ VisibleCount <= SqEntries

CqNoOverwrite ==
    /\ Len(cqPending) <= CqEntries

SqExactAccounting ==
    /\ Len(visible) = VisibleCount
    /\ Len(stagedOnly) = StagedOnlyCount
    /\ Len(visible \o stagedOnly) = OutstandingCount
    /\ visible \o stagedOnly = IntRangeSeq(kernelHead, nextReqId - 1)
    /\ newVisibleSinceLastCall <= VisibleCount

\* CqExactAccounting depends on the focused CQ publication abstraction above:
\* submitted completions stay in request order while moving between the
\* user-visible CQ, the kernel's unpublished backlog, and the modeled lost set.
CqExactAccounting ==
    /\ Len(cqConsumed) = cqHead
    /\ Len(cqPending) = cqTail - cqHead
    /\ Len(cqLost) = cqOverflow
    /\ cqConsumed \o cqPending \o cqBacklog \o cqLost =
        IntRangeSeq(0, kernelHead - 1)

\* Named liveness property used by the configs. Once a request has been flushed
\* to the kernel-visible SQ ring and not yet been consumed by kernelHead, it must
\* eventually be submitted unless the modeled listener has failed fast.
FlushedRequestsEventuallySubmitted ==
    \A req \in RequestIds :
        []((~failed /\ req \in FlushedSet /\ ~(req \in SubmittedSet))
            => <>(req \in SubmittedSet \/ failed))

OverflowEventuallyFailsSafe ==
    []((cqOverflow > 0 /\ ~failed) => <> failed)

====
