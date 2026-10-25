#!/bin/bash
# One measured repetition: run wrk against the already-started server and decide whether the run is valid.
# PTS parses "Requests/sec:" from $LOG_FILE and treats a non-zero ~/test-exit-status as a failed run.
#
# wrk prints Requests/sec and exits 0 even when connections time out or the server returns errors, so the
# validity checks below are what keeps an invalid run from becoming a number.

. "$(dirname "$0")/common.sh"

parse_args "$@" || { echo 1 > "${PTS_HOME}/test-exit-status"; exit 1; }
cd "$PTS_HOME" || exit 1

LOG="${LOG_FILE:-${PTS_HOME}/wrk-last.log}"
ulimit -n 1048576 2>/dev/null || ulimit -n "$(ulimit -Hn)" 2>/dev/null

if ! server_alive; then
    echo "INVALID RUN: the server is not running" > "$LOG"
    echo 1 > "${PTS_HOME}/test-exit-status"
    exit 1
fi

THREADS="$(wrk_threads)"
"${PTS_HOME}/wrk-4.2.0/wrk" -t "$THREADS" -c "$CONNECTIONS" -d "${DURATION}s" --latency "$URL" > "$LOG" 2>&1
RC=$?

REASON=""
[ "$RC" -ne 0 ] && REASON="wrk exited with status $RC"
server_alive || REASON="${REASON:+$REASON; }the server died during the run"
TRANSPORT_REASON="$(verify_transport)" || REASON="${REASON:+$REASON; }$TRANSPORT_REASON"
if [ "${PTS_ALLOW_ERRORS:-0}" != "1" ] && grep -qE "Socket errors|Non-2xx" "$LOG"; then
    REASON="${REASON:+$REASON; }wrk reported errors: $(grep -E "Socket errors|Non-2xx" "$LOG" | tr '\n' ' ')"
fi

if [ -n "$REASON" ]; then
    echo "INVALID RUN: $REASON" >> "$LOG"
    echo 1 > "${PTS_HOME}/test-exit-status"
    exit 1
fi

echo "wrk threads=$THREADS connections=$CONNECTIONS duration=${DURATION}s url=$URL" >> "$LOG"
echo 0 > "${PTS_HOME}/test-exit-status"
exit 0
