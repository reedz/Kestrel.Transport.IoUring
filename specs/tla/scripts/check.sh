#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
TLA_DIR="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)"
ROOT_DIR="$(CDPATH= cd -- "$SCRIPT_DIR/../../.." && pwd)"
CONFIG_DIR="$TLA_DIR/configs"
STATE_DIR="$TLA_DIR/states"
FETCH_TOOLS_SH="$SCRIPT_DIR/fetch-tools.sh"

# This catalog is the single source of truth for the committed configs. Every
# entry is a TLC model-checking run, never TLC -simulate: "focused" means the
# state space is narrowed by the spec/config, while "exhaustive" means the
# bounded state graph is explored without such witness-only narrowing.
declare -A EXPECTED_OUTCOME=()
declare -A EXPECTED_LABEL=()
declare -A EXPECTED_KIND=()
declare -A EXPECTED_TARGET=()
declare -A EXPECTED_MIN_DISTINCT=()
declare -A DECLARATION_TWIN_PEER=(
  ["Listener_DisposeAcceptedOverflowCorrected.cfg"]="Listener_DisposeAcceptedOverflowCurrentExpectedViolation.cfg"
  ["Listener_DisposeAcceptedOverflowCurrentExpectedViolation.cfg"]="Listener_DisposeAcceptedOverflowCorrected.cfg"
  ["MultiListener_FixDrainOnUnbindCorrected.cfg"]="MultiListener_FixDrainOnUnbindExpectedViolation.cfg"
  ["MultiListener_FixDrainOnUnbindExpectedViolation.cfg"]="MultiListener_FixDrainOnUnbindCorrected.cfg"
)
declare -A DECLARATION_TWIN_FLAG=(
  ["Listener_DisposeAcceptedOverflowCorrected.cfg"]="FixDisposeOverflowDrain"
  ["Listener_DisposeAcceptedOverflowCurrentExpectedViolation.cfg"]="FixDisposeOverflowDrain"
  ["MultiListener_FixDrainOnUnbindCorrected.cfg"]="FixDrainOnUnbind"
  ["MultiListener_FixDrainOnUnbindExpectedViolation.cfg"]="FixDrainOnUnbind"
)
CHECK_SH_CONFIGS_LOADED=0
DEFAULT_TLA_SEED=20260817

declare -a PARSED_INVARIANTS=()
declare -a PARSED_PROPERTIES=()
declare -a PARSED_CONSTRAINTS=()
declare -a PARSED_ACTION_CONSTRAINTS=()
declare -a PARSED_CONDITIONAL_SCOPE_REPORTS=()
declare -a PARSED_CATALOG_TARGETS=()
declare -A PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS=()
declare -A PARSED_CONSTANT_ASSIGNMENTS=()
PARSED_HAS_SYMMETRY=0
PARSED_SPECIFICATION=""

usage() {
  cat <<USAGE
Usage: $(basename "$0") <config>

Runs one committed TLC config from any working directory.
Environment overrides:
  TLA_WORKERS          TLC worker count (default: 2)
  TLA_FPMEM            TLC -fpmem ratio (default: 0.25)
  TLA_SEED             TLC -seed value (default: 20260817)
  TLA_TIMEOUT_SECONDS  Optional positive per-run timeout in seconds (default: unset = none)
  TLA_JVM_MAX_HEAP_MB  JVM -Xmx heap ceiling in MiB (default: 4096)
USAGE
}

die() {
  echo "check.sh: $*" >&2
  exit 1
}

load_config_catalog() {
  if [[ "$CHECK_SH_CONFIGS_LOADED" -eq 1 ]]; then
    return
  fi

  while IFS='|' read -r cfg outcome label kind target min_distinct; do
    [[ -n "$cfg" ]] || continue
    [[ "$cfg" == \#* ]] && continue
    case "$outcome" in
      pass|violation) ;;
      *) die "invalid catalog outcome '$outcome' for $cfg" ;;
    esac
    case "$kind" in
      pass|invariant|temporal) ;;
      *) die "invalid catalog kind '$kind' for $cfg" ;;
    esac
    [[ "$min_distinct" =~ ^[1-9][0-9]*$ ]] || die "invalid minimum distinct-state floor '$min_distinct' for $cfg"
    EXPECTED_OUTCOME["$cfg"]="$outcome"
    EXPECTED_LABEL["$cfg"]="$label"
    EXPECTED_KIND["$cfg"]="$kind"
    EXPECTED_TARGET["$cfg"]="$target"
    EXPECTED_MIN_DISTINCT["$cfg"]="$min_distinct"
  done <<'CATALOG'
# cfg|outcome|label|kind|target|min_distinct
BufferRing_CorrectedExhaustive.cfg|pass|exhaustive safety|pass|TypeOK, ExclusiveNonRingOwners, AllBufferConservation, ValidCompletionSemantics, RingAvailabilityAccounting, RingCapacityAndTail, CqCapacity, KernelTeardownReclaimsKernelOwners, KernelTeardownStopsRecv, NoLiveBuffersAfterUnmap, NoUnmapBeforeKernelTeardown, NoUnmapBeforeRingClosed|1000
BufferRing_CorrectedLiveness.cfg|pass|focused liveness|pass|ReceiveEventuallyRearmable|2000
BufferRing_CorrectedSafety.cfg|pass|exhaustive safety|pass|TypeOK, ExclusiveNonRingOwners, AllBufferConservation, ValidCompletionSemantics, RingAvailabilityAccounting, RingCapacityAndTail, CqCapacity, KernelTeardownReclaimsKernelOwners, KernelTeardownStopsRecv, NoLiveBuffersAfterUnmap, NoUnmapBeforeKernelTeardown, NoUnmapBeforeRingClosed|1000
BufferRing_DoubleRecycleExpectedViolation.cfg|violation|focused safety|invariant|AllBufferConservation|10
BufferRing_LoseRecycleExpectedViolation.cfg|violation|focused safety|invariant|AllBufferConservation|10
BufferRing_UnmapBeforeRingCloseExpectedViolation.cfg|violation|focused safety|invariant|NoUnmapBeforeRingClosed|2
BufferRing_UnmapBeforeTeardownExpectedViolation.cfg|violation|focused safety|invariant|NoLiveBuffersAfterUnmap|5
IoUringRing_CorrectedLiveness.cfg|pass|exhaustive liveness|pass|FlushedRequestsEventuallySubmitted|5
IoUringRing_CorrectedOverflowFailSafe.cfg|pass|focused liveness|pass|OverflowEventuallyFailsSafe|5
IoUringRing_CorrectedSafety.cfg|pass|exhaustive safety|pass|TypeOK, SqCursorRelation, CqCursorRelation, SqNoOverwrite, CqNoOverwrite, SqExactAccounting, CqExactAccounting|5
IoUringRing_CorrectedShortSubmitLiveness.cfg|pass|focused liveness|pass|FlushedRequestsEventuallySubmitted|5
IoUringRing_CurrentExpectedViolation.cfg|violation|exhaustive liveness|temporal|FlushedRequestsEventuallySubmitted|5
IoUringRing_FixEnterDecisionOnlyExpectedViolation.cfg|violation|focused liveness|temporal|FlushedRequestsEventuallySubmitted|5
IoUringRing_FixEnterSubmitBudgetOnlyExpectedViolation.cfg|violation|focused liveness|temporal|FlushedRequestsEventuallySubmitted|5
Listener_AcceptFocused.cfg|pass|focused safety|pass|TypeOK, SlotPartition, FdAccounting|20000
Listener_CancelRetryLiveness.cfg|pass|focused liveness|pass|EventuallyCancelRetryIssued, CancelRetryIssuedFromOrigin, CancelRetryEventuallySafe|10
Listener_CloseRelease.cfg|pass|focused liveness|pass|CloseEventuallyReleasesSlot|5
Listener_CloseReuseReachability.cfg|pass|focused liveness|pass|EventuallyStaleRecvDispatch, EventuallyFixedFileConnection, EventuallyRecvTokenObserved, EventuallySlotReleased, EventuallySlotReused|10
Listener_CorrectedLifecycle.cfg|pass|focused liveness|pass|UnbindEventuallyStops, DisposeEventuallyClosesRing|20
Listener_CorrectedLiveness.cfg|pass|focused liveness|pass|SendRetryEventuallyResumes|5000
Listener_CorrectedReceiveLiveness.cfg|pass|focused liveness|pass|ReceiveRetryPassEventuallyArms|5
Listener_CorrectedSafety.cfg|pass|focused safety|pass|TypeOK, SlotPartition, RecvArmedHasLiveFdRef, ReleasedSlotsNoLiveHandles, ReleasedSlotsNoCancelRetryReference, GenerationSafeDispatch, FixedFileSafety, RecvTokenOrder, CancelRetryTracksRecvEpoch, NoStaleDeferredReference, NoStaleRetryCrossGeneration, FdAccounting, RingCloseAfterDisposeDrain|10
# Listener accept-retry park twins: the focused body must execute the real
# accepted-fd rearm miss, later SQ-space recovery, and the real LoopWaitDecision;
# the twin differs only by FixAcceptRetryPark.
Listener_FixAcceptRetryParkCorrected.cfg|pass|focused liveness|pass|AcceptRetryEventuallyRearms|10
Listener_FixAcceptRetryParkExpectedViolation.cfg|violation|focused liveness|temporal|AcceptRetryEventuallyRearms|10
# Listener cancel-retry gate twins: same bounded setup, exact pass/violation
# classification, and no TLC -simulate "witness" shortcuts.
Listener_FixCancelRetryGateCorrected.cfg|pass|focused safety|pass|TypeOK, SlotPartition, FixedFileSafety, RecvTokenOrder, CancelRetryTracksRecvEpoch, FdAccounting|5
Listener_FixCancelRetryGateExpectedViolation.cfg|violation|focused safety|invariant|CancelRetryTracksRecvEpoch|2
# Listener Bug 3 pass-gate twins: the current config must violate the temporal
# property, while the corrected twin must pass by flipping only FixCancelRetryPassGate.
Listener_FixCancelRetryPassGateCorrected.cfg|pass|focused liveness|pass|MatchingCancelRetryEventuallyIssuesOrResolves|4
Listener_FixCancelRetryPassGateExpectedViolation.cfg|violation|focused liveness|temporal|MatchingCancelRetryEventuallyIssuesOrResolves|3
# Listener Bug 6 dispose-accepted-CQE twins: the focused setup leaves a kernel
# accepted fd represented only by cqQueue, then the body must execute the real
# RequestDisposeTransport / LoopStop / DisposeDrainBeforeRingClose /
# DisposeCloseRing teardown. The twin differs only by FixDisposeAcceptedCqeLeak,
# and the distinct-state floor prevents the witness from collapsing into a
# vacuous no-dispose model.
Listener_FixDisposeAcceptedCqeLeakCorrected.cfg|pass|focused safety|pass|TypeOK, SlotPartition, FdAccounting, RingCloseAfterDisposeDrain|9
Listener_FixDisposeAcceptedCqeLeakExpectedViolation.cfg|violation|focused safety|invariant|FdAccounting|9
# Listener hidden-overflow dispose twins: the focused setup leaves one
# accepted fd visible in cqQueue and a bounded hidden overflow backlog of
# accepted fds outside the visible CQ snapshot. The twin differs only by
# FixDisposeOverflowDrain: with the flag off, the shared body stops after the
# initial visible snapshot and loses the hidden accepts at ring close; with the
# flag on, guaranteed-progress hidden backlog that fits within the fixed round
# cap is reclaimed by alternating bounded drain/materialize rounds.
Listener_DisposeAcceptedOverflowCorrected.cfg|pass|focused safety|pass|TypeOK, SlotPartition, FdAccounting, RingCloseAfterDisposeDrain, DisposeOverflowTrackedAcceptedFdsReleased|8
Listener_DisposeAcceptedOverflowCurrentExpectedViolation.cfg|violation|focused safety|invariant|FdAccounting|8
# Listener hidden-overflow termination config: the same focused body runs with
# guaranteed progress disabled and one extra hidden backlog item available for
# the documented residual / cap-exhaustion case. It must still terminate in a
# bounded way without falsely requiring full hidden-backlog reclamation.
Listener_DisposeAcceptedOverflowTermination.cfg|pass|focused safety|pass|TypeOK, SlotPartition, RingCloseAfterDisposeDrain, DisposeOverflowTrackedAcceptedFdsReleased|8
# Listener Bug 5 eventfd rearm/park twins: the focused body must traverse the
# real eventfd completion, failed rearm, later SQ-space recovery, and the real
# LoopWaitDecision; the twin differs only by FixEventFdRearmPark.
Listener_FixEventFdRearmParkCorrected.cfg|pass|focused liveness|pass|EventFdWakeEventuallyDrains|30
Listener_FixEventFdRearmParkExpectedViolation.cfg|violation|focused liveness|temporal|EventFdWakeEventuallyDrains|30
# Listener Bug 4 send-retry wake-on-request twins: a focused pre-request
# prefix must execute the real RequestSendRetry branch and the real
# LoopWaitDecision, and the twin differs only by FixSendRetryPark.
Listener_FixSendRetryParkCorrected.cfg|pass|focused liveness|pass|SendRetryEventuallyResumes|5
Listener_FixSendRetryParkExpectedViolation.cfg|violation|focused liveness|temporal|SendRetryEventuallyResumes|5
# Listener retry-pass park twins: the focused body must execute the real
# ProcessRecvCqe receive-rearm miss, later SQ-space recovery, and the real
# LoopWaitDecision; the twin differs only by FixRetryPassPark.
Listener_FixRetryPassParkCorrected.cfg|pass|focused liveness|pass|ReceiveRetryPassEventuallyArms|10
Listener_FixRetryPassParkExpectedViolation.cfg|violation|focused liveness|temporal|ReceiveRetryPassEventuallyArms|10
Listener_FixStaleCancelSetOnCloseCorrected.cfg|pass|focused safety|pass|TypeOK, SlotPartition, ReleasedSlotsNoCancelRetryReference, NoStaleRetryCrossGeneration, GenerationSafeDispatch, FixedFileSafety, FdAccounting|10
Listener_FixStaleCancelSetOnCloseExpectedViolation.cfg|violation|focused safety|invariant|ReleasedSlotsNoCancelRetryReference|5
Listener_LifecycleFocused.cfg|pass|focused safety|pass|TypeOK, SlotPartition, FdAccounting, RingCloseAfterDisposeDrain, UnbindAloneKeepsLoopRunning|5000
Listener_RecvFocused.cfg|pass|focused safety|pass|TypeOK, SlotPartition, FixedFileSafety, RecvTokenOrder, FdAccounting|200
Listener_RecvResubmitFocused.cfg|pass|focused liveness|pass|EventuallyRecvResubmitDrainRearms|2
Listener_SendFocused.cfg|pass|focused safety|pass|TypeOK, SlotPartition, SendTokenOrder, SendPinSafety, FdAccounting|5000
MultiListener_CorrectedSafety.cfg|pass|exhaustive safety|pass|TypeOK, OwnershipPartition, BoundedChannels, WorkerCapacityBound, TotalCapacityExact, RemainderAllocation, MergedCapacityWithinSourceBound, UniqueForwardHistory, ClosedRefsTerminal, DroppedRefsSeparate, DroppedRefsNeverMerged, UnboundHasNoLiveConnections, DisposedQuiescesListenerLocations, DisposedHasNoDroppedRefs|20000
MultiListener_CurrentSourceSafety.cfg|pass|exhaustive safety|pass|TypeOK, OwnershipPartition, BoundedChannels, WorkerCapacityBound, TotalCapacityExact, RemainderAllocation, MergedCapacityWithinSourceBound, UniqueForwardHistory, ClosedRefsTerminal, DroppedRefsSeparate, DroppedRefsNeverMerged, UnboundHasNoLiveConnections|50000
# MultiListener shutdown-drain twins: the focused body must execute the real
# WorkerAbortQueuedHead and ForwardCancelTransit actions under both flags; only
# FixDrainOnUnbind flips whether shutdown preserves/disposes or consumes/drops
# the affected reference ownership. The corrected twin now models the real
# early CompleteMergedWriter-on-unbind ordering, and the separate late-order
# mutation keeps FixDrainOnUnbind=TRUE while delaying that completion until
# after forwarders.
MultiListener_FixDrainOnUnbindCorrected.cfg|pass|focused liveness|pass|EventuallyShutdownDisposeWitness|2000
MultiListener_FixDrainOnUnbindCorrectedQuiescence.cfg|pass|focused liveness|pass|EventuallyShutdownDisposeWitness, UnbindEventuallyCompletes, DisposeEventuallyQuiesces|2000
MultiListener_FixDrainOnUnbindLateCompleteExpectedViolation.cfg|violation|focused liveness|temporal|DisposeEventuallyQuiesces|850
MultiListener_FixDrainOnUnbindExpectedViolation.cfg|violation|focused safety|invariant|DisposedHasNoDroppedRefs|2000
MultiListener_ForwardProgress.cfg|pass|focused liveness|pass|EventuallyInTransitWitness, EventuallyMergedWitness, TransitEventuallyAccepted|2
CATALOG

  CHECK_SH_CONFIGS_LOADED=1
}

normalize_config_path() {
  local arg="$1"
  local resolved

  if [[ -f "$arg" ]]; then
    resolved="$(CDPATH= cd -- "$(dirname -- "$arg")" && pwd)/$(basename -- "$arg")"
  elif [[ -f "$CONFIG_DIR/$arg" ]]; then
    resolved="$CONFIG_DIR/$arg"
  elif [[ -f "$CONFIG_DIR/$(basename -- "$arg")" ]]; then
    resolved="$CONFIG_DIR/$(basename -- "$arg")"
  else
    return 1
  fi

  case "$resolved" in
    "$CONFIG_DIR"/*.cfg) printf '%s\n' "$resolved" ;;
    *) die "config must live under $CONFIG_DIR: $arg" ;;
  esac
}

infer_module() {
  local cfg_base="$1"
  local cfg_stem="${cfg_base%.cfg}"
  local best=""
  local module_path
  local module_name

  shopt -s nullglob
  for module_path in "$TLA_DIR"/*.tla; do
    module_name="${module_path##*/}"
    module_name="${module_name%.tla}"
    case "$cfg_stem" in
      "$module_name"|"$module_name"_*)
        if (( ${#module_name} > ${#best} )); then
          best="$module_name"
        fi
        ;;
    esac
  done
  shopt -u nullglob

  [[ -n "$best" ]] || die "could not infer a module for $cfg_base"
  printf '%s\n' "$best"
}

validate_catalog_entry() {
  local cfg_base="$1"
  [[ -n "${EXPECTED_OUTCOME[$cfg_base]:-}" ]] || die "missing catalog entry for $cfg_base"
  [[ -n "${EXPECTED_LABEL[$cfg_base]:-}" ]] || die "missing catalog label for $cfg_base"
  [[ -n "${EXPECTED_KIND[$cfg_base]:-}" ]] || die "missing catalog kind for $cfg_base"
  [[ -n "${EXPECTED_TARGET[$cfg_base]:-}" ]] || die "missing catalog target for $cfg_base"
  [[ -n "${EXPECTED_MIN_DISTINCT[$cfg_base]:-}" ]] || die "missing catalog minimum distinct-state floor for $cfg_base"
}

trim_whitespace() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

append_declaration_words() {
  local section="$1"
  local text="$2"
  local -a words=()
  local word

  read -r -a words <<< "$text"
  for word in "${words[@]}"; do
    case "$section" in
      INVARIANT) PARSED_INVARIANTS+=("$word") ;;
      PROPERTY) PARSED_PROPERTIES+=("$word") ;;
      CONSTRAINT) PARSED_CONSTRAINTS+=("$word") ;;
      ACTION_CONSTRAINT) PARSED_ACTION_CONSTRAINTS+=("$word") ;;
      *) die "unsupported declaration section '$section'" ;;
    esac
  done
}

parse_config_structure() {
  local cfg_path="$1"
  local line
  local stripped
  local current_section=""
  local in_constants=0
  local in_header=1
  local header_marker_regex='^[[:space:]]*\\\*[[:space:]]+CONDITIONAL_SCOPE:[[:space:]]+(CONSTRAINT|ACTION_CONSTRAINT)[[:space:]]+([^[:space:]]+)[[:space:]]+--[[:space:]]+(.+[^[:space:]])[[:space:]]*$'

  PARSED_INVARIANTS=()
  PARSED_PROPERTIES=()
  PARSED_CONSTRAINTS=()
  PARSED_ACTION_CONSTRAINTS=()
  PARSED_CONDITIONAL_SCOPE_REPORTS=()
  PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS=()
  PARSED_CONSTANT_ASSIGNMENTS=()
  PARSED_HAS_SYMMETRY=0
  PARSED_SPECIFICATION=""

  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    stripped="$(trim_whitespace "$line")"

    if [[ -z "$stripped" ]]; then
      [[ "$in_header" -eq 1 ]] || current_section=""
      continue
    fi

    if [[ "$stripped" =~ ^\\\* ]]; then
      if [[ "$in_header" -eq 1 && "$line" =~ $header_marker_regex ]]; then
        PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS["${BASH_REMATCH[1]}|${BASH_REMATCH[2]}"]="${BASH_REMATCH[3]}"
        PARSED_CONDITIONAL_SCOPE_REPORTS+=("${BASH_REMATCH[1]} ${BASH_REMATCH[2]} -- ${BASH_REMATCH[3]}")
      fi
      continue
    fi

    in_header=0

    if [[ "$stripped" =~ ^SPECIFICATION[[:space:]]+(.+[^[:space:]])[[:space:]]*$ ]]; then
      PARSED_SPECIFICATION="${BASH_REMATCH[1]}"
      current_section=""
      in_constants=0
      continue
    fi

    if [[ "$stripped" == "CONSTANTS" ]]; then
      current_section=""
      in_constants=1
      continue
    fi

    if [[ "$stripped" =~ ^(INVARIANT|INVARIANTS|PROPERTY|PROPERTIES|CONSTRAINT|ACTION_CONSTRAINT)([[:space:]]+(.*))?$ ]]; then
      in_constants=0
      case "${BASH_REMATCH[1]}" in
        INVARIANT|INVARIANTS) current_section="INVARIANT" ;;
        PROPERTY|PROPERTIES) current_section="PROPERTY" ;;
        CONSTRAINT) current_section="CONSTRAINT" ;;
        ACTION_CONSTRAINT) current_section="ACTION_CONSTRAINT" ;;
      esac

      if [[ -n "${BASH_REMATCH[3]:-}" ]]; then
        append_declaration_words "$current_section" "${BASH_REMATCH[3]}"
        current_section=""
      fi
      continue
    fi

    if [[ "$in_constants" -eq 1 && "$stripped" =~ ^([A-Za-z][A-Za-z0-9_]*)[[:space:]]*=[[:space:]]*(.+[^[:space:]])[[:space:]]*$ ]]; then
      PARSED_CONSTANT_ASSIGNMENTS["${BASH_REMATCH[1]}"]="${BASH_REMATCH[2]}"
      continue
    fi

    if [[ "$stripped" =~ ^SYMMETRY([[:space:]]+.*)?$ ]]; then
      PARSED_HAS_SYMMETRY=1
      current_section=""
      in_constants=0
      continue
    fi

    if [[ "$stripped" =~ ^[A-Z][A-Z0-9_]*([[:space:]]+.*)?$ ]]; then
      current_section=""
      in_constants=0
      continue
    fi

    if [[ -n "$current_section" ]]; then
      append_declaration_words "$current_section" "$stripped"
    fi
  done < "$cfg_path"
}

parse_catalog_targets() {
  local target_csv="$1"
  local target
  local trimmed

  PARSED_CATALOG_TARGETS=()
  IFS=',' read -r -a PARSED_CATALOG_TARGETS <<< "$target_csv"
  for target in "${!PARSED_CATALOG_TARGETS[@]}"; do
    trimmed="$(trim_whitespace "${PARSED_CATALOG_TARGETS[$target]}")"
    PARSED_CATALOG_TARGETS[$target]="$trimmed"
  done
}

format_name_list() {
  local first=1
  local value

  for value in "$@"; do
    if [[ "$first" -eq 1 ]]; then
      printf '%s' "$value"
      first=0
    else
      printf ', %s' "$value"
    fi
  done
}

arrays_match_exactly() {
  local lhs_count="$1"
  local rhs_count="$2"
  shift 2
  local rhs_offset=$((lhs_count + 1))
  local -a lhs=("${@:1:lhs_count}")
  local -a rhs=("${@:rhs_offset:rhs_count}")
  local index

  [[ "$lhs_count" -eq "$rhs_count" ]] || return 1
  for index in "${!lhs[@]}"; do
    [[ "${lhs[$index]}" == "${rhs[$index]}" ]] || return 1
  done

  return 0
}

validate_conditional_scope_markers() {
  local cfg_base="$1"
  local constraint_name
  local key

  if (( ${#PARSED_CONSTRAINTS[@]} == 0 && ${#PARSED_ACTION_CONSTRAINTS[@]} == 0 )); then
    return
  fi

  for constraint_name in "${PARSED_CONSTRAINTS[@]}"; do
    key="CONSTRAINT|$constraint_name"
    [[ -n "${PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS[$key]:-}" ]] || die "$cfg_base declares CONSTRAINT $constraint_name but is missing a header '\\* CONDITIONAL_SCOPE:' justification marker"
  done

  for constraint_name in "${PARSED_ACTION_CONSTRAINTS[@]}"; do
    key="ACTION_CONSTRAINT|$constraint_name"
    [[ -n "${PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS[$key]:-}" ]] || die "$cfg_base declares ACTION_CONSTRAINT $constraint_name but is missing a header '\\* CONDITIONAL_SCOPE:' justification marker"
  done
}

validate_catalog_targets() {
  local cfg_base="$1"
  local expected_kind="$2"
  local expected_target="$3"
  local -a expected_targets=()
  local -a relevant_declarations=()
  local target

  parse_catalog_targets "$expected_target"
  expected_targets=("${PARSED_CATALOG_TARGETS[@]}")

  case "$expected_kind" in
    invariant)
      relevant_declarations=("${PARSED_INVARIANTS[@]}")
      (( ${#expected_targets[@]} == 1 )) || die "$cfg_base must record exactly one catalog target for an expected invariant violation"
      (( ${#relevant_declarations[@]} > 0 )) || die "$cfg_base has no INVARIANT declarations for expected invariant violation"
      for target in "${relevant_declarations[@]}"; do
        if [[ "$target" == "${expected_targets[0]}" ]]; then
          return
        fi
      done
      die "$cfg_base catalog invariant target '${expected_targets[0]}' is not declared by the config"
      ;;
    temporal)
      relevant_declarations=("${PARSED_PROPERTIES[@]}")
      (( ${#expected_targets[@]} == 1 )) || die "$cfg_base must record exactly one catalog target for an expected temporal violation"
      (( ${#relevant_declarations[@]} == 1 )) || die "$cfg_base must declare exactly one PROPERTY/PROPERTIES target for expected temporal violations; found ${#relevant_declarations[@]}"
      [[ "${relevant_declarations[0]}" == "${expected_targets[0]}" ]] || die "$cfg_base catalog temporal target '${expected_targets[0]}' does not match the lone PROPERTY/PROPERTIES declaration '${relevant_declarations[0]}'"
      return
      ;;
    pass)
      if (( ${#PARSED_PROPERTIES[@]} > 0 )); then
        relevant_declarations=("${PARSED_PROPERTIES[@]}")
      else
        relevant_declarations=("${PARSED_INVARIANTS[@]}")
      fi
      ;;
    *)
      die "unsupported expected kind '$expected_kind' for $cfg_base"
      ;;
  esac

  (( ${#relevant_declarations[@]} > 0 )) || die "$cfg_base has no relevant INVARIANT/PROPERTY declarations for catalog kind '$expected_kind'"
  arrays_match_exactly \
    "${#expected_targets[@]}" \
    "${#relevant_declarations[@]}" \
    "${expected_targets[@]}" \
    "${relevant_declarations[@]}" || die "$cfg_base catalog target list is stale; expected '$(format_name_list "${relevant_declarations[@]}")' but catalog records '$(format_name_list "${expected_targets[@]}")'"
}

validate_twin_spec_constant_parity() {
  local cfg_base="$1"
  local peer_base="$2"
  local lhs_specification="$3"
  local rhs_specification="$4"
  local -n lhs_constants="$5"
  local -n rhs_constants="$6"
  local varying_flag="$7"
  local key

  [[ -n "$lhs_specification" ]] || die "$cfg_base is missing a SPECIFICATION declaration"
  [[ -n "$rhs_specification" ]] || die "$peer_base is missing a SPECIFICATION declaration"
  [[ "$lhs_specification" == "$rhs_specification" ]] || die "$cfg_base SPECIFICATION '$lhs_specification' drifted from $peer_base '$rhs_specification'; declaration twins must stay on the same live spec"

  [[ -n "$varying_flag" ]] || die "$cfg_base declaration twin is missing a varying-flag mapping"
  [[ -n "${lhs_constants[$varying_flag]:-}" ]] || die "$cfg_base must declare $varying_flag in CONSTANTS"
  [[ -n "${rhs_constants[$varying_flag]:-}" ]] || die "$peer_base must declare $varying_flag in CONSTANTS"
  [[ "${lhs_constants[$varying_flag]}" != "${rhs_constants[$varying_flag]}" ]] || die "$cfg_base and $peer_base must differ on $varying_flag to remain meaningful declaration twins"

  for key in "${!lhs_constants[@]}"; do
    [[ "$key" == "$varying_flag" ]] && continue
    [[ -n "${rhs_constants[$key]:-}" ]] || die "$cfg_base declares CONSTANT $key but $peer_base does not; declaration twins must keep identical non-flag constants"
    [[ "${lhs_constants[$key]}" == "${rhs_constants[$key]}" ]] || die "$cfg_base CONSTANT $key='${lhs_constants[$key]}' drifted from $peer_base '${rhs_constants[$key]}'; declaration twins must keep identical non-flag constants"
  done

  for key in "${!rhs_constants[@]}"; do
    [[ "$key" == "$varying_flag" ]] && continue
    [[ -n "${lhs_constants[$key]:-}" ]] || die "$peer_base declares CONSTANT $key but $cfg_base does not; declaration twins must keep identical non-flag constants"
  done
}

validate_twin_declaration_parity() {
  local cfg_base="$1"
  local peer_base="${DECLARATION_TWIN_PEER[$cfg_base]:-}"
  local varying_flag="${DECLARATION_TWIN_FLAG[$cfg_base]:-}"
  local peer_path
  local current_has_symmetry="$PARSED_HAS_SYMMETRY"
  local current_specification="$PARSED_SPECIFICATION"
  local -a current_invariants=("${PARSED_INVARIANTS[@]}")
  local -a current_properties=("${PARSED_PROPERTIES[@]}")
  local -a current_constraints=("${PARSED_CONSTRAINTS[@]}")
  local -a current_action_constraints=("${PARSED_ACTION_CONSTRAINTS[@]}")
  local -a current_scope_reports=("${PARSED_CONDITIONAL_SCOPE_REPORTS[@]}")
  local -a current_catalog_targets=("${PARSED_CATALOG_TARGETS[@]}")
  local -A current_scope_justifications=()
  local -A current_constants=()
  local key

  [[ -n "$peer_base" ]] || return 0
  [[ -n "$varying_flag" ]] || die "$cfg_base has a declaration twin but no varying-flag mapping"
  [[ "${DECLARATION_TWIN_FLAG[$peer_base]:-}" == "$varying_flag" ]] || die "$cfg_base and $peer_base must agree on their varying declaration-twin flag"

  peer_path="$CONFIG_DIR/$peer_base"
  [[ -f "$peer_path" ]] || die "$cfg_base declaration twin $peer_base is missing"

  for key in "${!PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS[@]}"; do
    current_scope_justifications["$key"]="${PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS[$key]}"
  done
  for key in "${!PARSED_CONSTANT_ASSIGNMENTS[@]}"; do
    current_constants["$key"]="${PARSED_CONSTANT_ASSIGNMENTS[$key]}"
  done

  parse_config_structure "$peer_path"

  validate_twin_spec_constant_parity \
    "$cfg_base" \
    "$peer_base" \
    "$current_specification" \
    "$PARSED_SPECIFICATION" \
    current_constants \
    PARSED_CONSTANT_ASSIGNMENTS \
    "$varying_flag"

  arrays_match_exactly \
    "${#current_invariants[@]}" \
    "${#PARSED_INVARIANTS[@]}" \
    "${current_invariants[@]}" \
    "${PARSED_INVARIANTS[@]}" || die "$cfg_base INVARIANT declarations drifted from $peer_base; expected identical declaration-twin obligations"

  arrays_match_exactly \
    "${#current_properties[@]}" \
    "${#PARSED_PROPERTIES[@]}" \
    "${current_properties[@]}" \
    "${PARSED_PROPERTIES[@]}" || die "$cfg_base PROPERTY declarations drifted from $peer_base; expected identical declaration-twin obligations"

  PARSED_INVARIANTS=("${current_invariants[@]}")
  PARSED_PROPERTIES=("${current_properties[@]}")
  PARSED_CONSTRAINTS=("${current_constraints[@]}")
  PARSED_ACTION_CONSTRAINTS=("${current_action_constraints[@]}")
  PARSED_CONDITIONAL_SCOPE_REPORTS=("${current_scope_reports[@]}")
  PARSED_CATALOG_TARGETS=("${current_catalog_targets[@]}")
  PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS=()
  PARSED_CONSTANT_ASSIGNMENTS=()
  for key in "${!current_scope_justifications[@]}"; do
    PARSED_CONDITIONAL_SCOPE_JUSTIFICATIONS["$key"]="${current_scope_justifications[$key]}"
  done
  for key in "${!current_constants[@]}"; do
    PARSED_CONSTANT_ASSIGNMENTS["$key"]="${current_constants[$key]}"
  done
  PARSED_HAS_SYMMETRY="$current_has_symmetry"
  PARSED_SPECIFICATION="$current_specification"
}

validate_positive_integer() {
  local name="$1"
  local value="$2"

  [[ "$value" =~ ^[1-9][0-9]*$ ]] || die "$name must be a positive integer; got '$value'"
}

validate_fraction_between_zero_and_one() {
  local name="$1"
  local value="$2"

  [[ "$value" =~ ^([0-9]+([.][0-9]+)?|[.][0-9]+)$ ]] || die "$name must be a decimal between 0 and 1 inclusive; got '$value'"
  awk -v value="$value" 'BEGIN { exit !(value > 0 && value <= 1) }' || die "$name must be > 0 and <= 1; got '$value'"
}

extract_search_metrics() {
  local log_file="$1"
  local distinct_states
  local depth

  distinct_states="$(sed -nE 's/^[0-9,]+ states generated, ([0-9,]+) distinct states found, [0-9,]+ states left on queue\.$/\1/p' "$log_file" | tail -n 1 | tr -d ',')"
  if [[ -z "$distinct_states" ]]; then
    distinct_states="$(sed -nE 's/^Finished computing initial states: ([0-9,]+) distinct states? generated.*$/\1/p' "$log_file" | tail -n 1 | tr -d ',')"
  fi
  depth="$(sed -nE 's/^The depth of the complete state graph search is ([0-9,]+)\.$/\1/p' "$log_file" | tail -n 1 | tr -d ',')"

  printf '%s|%s\n' "$distinct_states" "$depth"
}

validate_min_distinct_floor() {
  local cfg_base="$1"
  local min_distinct="$2"
  local distinct_states="$3"

  [[ -n "$distinct_states" ]] || {
    echo "RESULT: mismatch; could not parse distinct-state count for $cfg_base" >&2
    return 1
  }

  if (( distinct_states < min_distinct )); then
    echo "RESULT: mismatch; expected at least $min_distinct distinct states for $cfg_base, observed $distinct_states (possible collapsed vacuity)" >&2
    return 1
  fi

  return 0
}

validate_runtime_settings() {
  local workers="$1"
  local fpmem="$2"
  local seed="$3"
  local timeout_seconds="$4"
  local timeout_is_set="$5"
  local jvm_heap_mb="$6"

  validate_positive_integer "TLA_WORKERS" "$workers"
  validate_fraction_between_zero_and_one "TLA_FPMEM" "$fpmem"
  validate_positive_integer "TLA_SEED" "$seed"
  validate_positive_integer "TLA_JVM_MAX_HEAP_MB" "$jvm_heap_mb"

  if [[ "$timeout_is_set" -eq 1 ]]; then
    validate_positive_integer "TLA_TIMEOUT_SECONDS" "$timeout_seconds"
  fi
}

validate_config_hygiene() {
  local cfg_path="$1"
  local cfg_base="$2"
  local expected_kind="$3"
  local expected_target="$4"

  if grep -qE '^[[:space:]]*PROPERT(Y|IES)([[:space:]]+.*)?$' "$cfg_path" && grep -qE '^[[:space:]]*SYMMETRY([[:space:]]+.*)?$' "$cfg_path"; then
    die "$cfg_base mixes PROPERTY/PROPERTIES with SYMMETRY; temporal checks must keep symmetry reduction off"
  fi

  parse_config_structure "$cfg_path"
  validate_conditional_scope_markers "$cfg_base"
  validate_catalog_targets "$cfg_base" "$expected_kind" "$expected_target"
  validate_twin_declaration_parity "$cfg_base"
}

remove_generated_artifacts() {
  local stale=()

  shopt -s nullglob
  stale=(
    "$TLA_DIR"/SpecTE*.tla
    "$TLA_DIR"/SpecTE*.cfg
    "$TLA_DIR"/*_TTrace_*.tla
    "$TLA_DIR"/*_TTrace_*.bin
  )
  shopt -u nullglob

  if (( ${#stale[@]} > 0 )); then
    rm -f -- "${stale[@]}"
  fi
}

classify_result() {
  local exit_code="$1"
  local log_file="$2"
  local invariant_name

  if grep -qF 'Model checking completed. No error has been found.' "$log_file"; then
    printf 'pass|\n'
    return
  fi

  invariant_name="$(sed -nE 's/^Error: Invariant ([^[:space:]]+) is violated( by (the )?initial state)?[.:]?$/\1/p' "$log_file" | head -n 1)"
  if [[ -n "$invariant_name" ]]; then
    printf 'invariant|%s\n' "$invariant_name"
    return
  fi

  if grep -qF 'Error: Temporal properties were violated.' "$log_file"; then
    printf 'temporal|\n'
    return
  fi

  if grep -qF 'Error: Deadlock reached.' "$log_file"; then
    printf 'deadlock|\n'
    return
  fi

  if [[ "$exit_code" -eq 124 ]]; then
    printf 'timeout|\n'
    return
  fi

  if grep -Eq 'OutOfMemoryError|Java heap space|GC overhead limit exceeded|unable to create native thread|Cannot allocate memory' "$log_file"; then
    printf 'oom|\n'
    return
  fi

  if [[ "$exit_code" -ne 0 ]]; then
    printf 'tool-error|\n'
    return
  fi

  printf 'unknown|\n'
}

run_config() {
  local cfg_path="$1"
  local cfg_base="${cfg_path##*/}"
  local cfg_stem="${cfg_base%.cfg}"
  local module_name
  local expected_outcome
  local expected_label
  local expected_kind
  local expected_target
  local workers
  local fpmem
  local seed
  local timeout_seconds
  local timeout_is_set
  local jvm_heap_mb
  local metadir
  local log_file
  local jar_path
  local min_distinct
  local outcome_detail
  local result_kind
  local result_detail
  local metrics_detail
  local distinct_states
  local search_depth
  local tlc_status=0
  local -a java_cmd=()
  local scope_line

  validate_catalog_entry "$cfg_base"

  module_name="$(infer_module "$cfg_base")"
  expected_outcome="${EXPECTED_OUTCOME[$cfg_base]}"
  expected_label="${EXPECTED_LABEL[$cfg_base]}"
  expected_kind="${EXPECTED_KIND[$cfg_base]}"
  expected_target="${EXPECTED_TARGET[$cfg_base]}"
  min_distinct="${EXPECTED_MIN_DISTINCT[$cfg_base]}"
  workers="${TLA_WORKERS:-2}"
  fpmem="${TLA_FPMEM:-0.25}"
  seed="${TLA_SEED:-$DEFAULT_TLA_SEED}"
  timeout_seconds="${TLA_TIMEOUT_SECONDS:-0}"
  timeout_is_set=0
  if [[ -n "${TLA_TIMEOUT_SECONDS:-}" ]]; then
    timeout_is_set=1
  fi
  jvm_heap_mb="${TLA_JVM_MAX_HEAP_MB:-4096}"
  metadir="$STATE_DIR/$cfg_stem"
  log_file="$TLA_DIR/$cfg_stem.out"

  validate_runtime_settings "$workers" "$fpmem" "$seed" "$timeout_seconds" "$timeout_is_set" "$jvm_heap_mb"
  validate_config_hygiene "$cfg_path" "$cfg_base" "$expected_kind" "$expected_target"

  jar_path="$("$FETCH_TOOLS_SH")"
  java_cmd=(
    java
    "-Xmx${jvm_heap_mb}m"
    -XX:+UseParallelGC
    -cp "$jar_path"
    tlc2.TLC
    -workers "$workers"
    -fpmem "$fpmem"
    -seed "$seed"
    -metadir "$metadir"
    -config "configs/$cfg_base"
    "${module_name}.tla"
  )

  mkdir -p -- "$STATE_DIR"
  rm -rf -- "$metadir"
  rm -f -- "$log_file"
  : > "$log_file"
  remove_generated_artifacts

  echo "==> $cfg_base [$expected_label]"
  echo "    module: ${module_name}.tla"
  echo "    expect: $expected_outcome ($expected_target)"
  for scope_line in "${PARSED_CONDITIONAL_SCOPE_REPORTS[@]}"; do
    echo "    conditional scope: $scope_line"
  done
  echo "    tlc: workers=$workers fpmem=$fpmem seed=$seed heap=${jvm_heap_mb}MiB metadir=$metadir"

  set +e
  if [[ "$timeout_is_set" -eq 1 ]]; then
    if ! command -v timeout >/dev/null 2>&1; then
      set -e
      die "timeout requested via TLA_TIMEOUT_SECONDS, but GNU timeout is unavailable"
    fi
    (
      cd -- "$TLA_DIR" || exit 1
      timeout --signal=TERM --kill-after=15s "${timeout_seconds}s" "${java_cmd[@]}"
    ) 2>&1 | tee -- "$log_file"
    tlc_status=${PIPESTATUS[0]}
  else
    (
      cd -- "$TLA_DIR" || exit 1
      "${java_cmd[@]}"
    ) 2>&1 | tee -- "$log_file"
    tlc_status=${PIPESTATUS[0]}
  fi
  set -e

  remove_generated_artifacts

  metrics_detail="$(extract_search_metrics "$log_file")"
  distinct_states="${metrics_detail%%|*}"
  search_depth="${metrics_detail#*|}"
  echo "    stats: distinct=${distinct_states:-n/a} depth=${search_depth:-n/a} (floor: $min_distinct)"

  outcome_detail="$(classify_result "$tlc_status" "$log_file")"
  result_kind="${outcome_detail%%|*}"
  result_detail="${outcome_detail#*|}"

  case "$expected_outcome" in
    pass)
      if [[ "$result_kind" == "pass" ]]; then
        validate_min_distinct_floor "$cfg_base" "$min_distinct" "$distinct_states" || return 1
        echo "RESULT: matched expected pass"
        return 0
      fi
      echo "RESULT: mismatch; expected a clean pass, observed $result_kind${result_detail:+ ($result_detail)}" >&2
      echo "        log: $log_file" >&2
      return 1
      ;;
    violation)
      case "$expected_kind" in
        invariant)
          if [[ "$result_kind" == "invariant" && "$result_detail" == "$expected_target" ]]; then
            validate_min_distinct_floor "$cfg_base" "$min_distinct" "$distinct_states" || return 1
            echo "RESULT: matched expected invariant violation ($expected_target)"
            return 0
          fi
          ;;
        temporal)
          if [[ "$result_kind" == "temporal" ]]; then
            validate_min_distinct_floor "$cfg_base" "$min_distinct" "$distinct_states" || return 1
            echo "RESULT: matched expected temporal violation ($expected_target)"
            return 0
          fi
          ;;
        *)
          die "unsupported expected violation kind '$expected_kind' for $cfg_base"
          ;;
      esac

      echo "RESULT: mismatch; expected $expected_kind violation of $expected_target, observed $result_kind${result_detail:+ ($result_detail)}" >&2
      echo "        log: $log_file" >&2
      return 1
      ;;
    *)
      die "unsupported expected outcome '$expected_outcome' for $cfg_base"
      ;;
  esac
}

main() {
  local cfg_path

  if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
  fi
  if [[ $# -ne 1 ]]; then
    usage >&2
    exit 1
  fi

  load_config_catalog
  cfg_path="$(normalize_config_path "$1")" || die "unknown config: $1"
  run_config "$cfg_path"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
