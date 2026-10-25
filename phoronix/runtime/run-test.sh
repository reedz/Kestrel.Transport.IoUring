#!/bin/bash
# One measured repetition: run wrk against the already-started server and decide whether the run is valid.
# PTS parses "Requests/sec:" from $LOG_FILE and drops the repetition when ~/test-exit-status is non-zero.
#
# wrk prints Requests/sec and exits 0 even when connections time out or the server returns errors, so the
# validity checks below are what keeps an invalid run from becoming a number. PTS averages the repetitions that
# survive without saying how many were dropped, so every invalid repetition is also appended to the invalid-runs
# log, which run-local.sh prints and turns into a failing exit status.

. "$(dirname "$0")/common.sh"

LOG="${LOG_FILE:-${PTS_HOME}/wrk-last.log}"

invalid() {
    echo "INVALID RUN: $1" >> "$LOG"
    record_invalid "run: $1"
    echo 1 > "${PTS_HOME}/test-exit-status"
    exit 1
}

parse_args "$@" || invalid "invalid arguments: $*"
cd "$PTS_HOME" || exit 1

[ -f "$PTS_FAILURE_LATCH" ] && invalid "configuration was not set up: $(cat "$PTS_FAILURE_LATCH")"

ulimit -n 1048576 2>/dev/null || ulimit -n "$(ulimit -Hn)" 2>/dev/null

server_alive || invalid "the server is not running"
REASON="$(verify_endpoint_identity)" || invalid "$REASON"

THREADS="$(wrk_threads)"
"${PTS_HOME}/wrk-4.2.0/wrk" -t "$THREADS" -c "$CONNECTIONS" -d "${DURATION}s" --latency "$URL" > "$LOG" 2>&1
RC=$?

REASON=""
[ "$RC" -ne 0 ] && REASON="wrk exited with status $RC"
server_alive || REASON="${REASON:+$REASON; }the server died during the run"
TRANSPORT_REASON="$(verify_transport)" || REASON="${REASON:+$REASON; }$TRANSPORT_REASON"
IDENTITY_REASON="$(verify_endpoint_identity)" || REASON="${REASON:+$REASON; }$IDENTITY_REASON"
# wrk counts HTTP statuses above 399 as "Non-2xx or 3xx responses"; the server never redirects, so any such
# line is an error response.
if grep -qE "Socket errors|Non-2xx" "$LOG"; then
    REASON="${REASON:+$REASON; }wrk reported errors: $(grep -E "Socket errors|Non-2xx" "$LOG" | tr '\n' ' ')"
fi

[ -z "$REASON" ] || invalid "$REASON"

echo "wrk threads=$THREADS connections=$CONNECTIONS duration=${DURATION}s url=$URL" >> "$LOG"
echo 0 > "${PTS_HOME}/test-exit-status"
exit 0
