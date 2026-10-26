#!/bin/bash
# Pre-test hook: start the ASP.NET Core server with the requested transport and runtime, prove that this process
# serves the port wrk will hit with the requested transport, check the workload's response, and warm it up.
# Runs once per option combination (before its repetitions).
#
# Failure protocol: this hook never signals failure through ~/pre-test-exit-status. PTS 10.8 returns from that
# path without releasing its run lock, which makes every remaining configuration of a multi-configuration run
# fail with "test is already running". Instead the failure is latched in ~/config-invalid and recorded in the
# invalid-runs log, and run-test.sh turns every repetition of this configuration into an invalid run (no number).

. "$(dirname "$0")/common.sh"

PRE_COMPLETED=0

fail() {
    echo "ERROR: $*" >&2
    [ -f "$PTS_SERVER_LOG" ] && tail -n 20 "$PTS_SERVER_LOG" >&2
    stop_server
    printf '%s\n' "$*" > "$PTS_FAILURE_LATCH"
    record_invalid "pre-test: $*"
    PRE_COMPLETED=1
    echo 0 > "${PTS_HOME}/pre-test-exit-status"
    exit 0
}

rm -f "${PTS_HOME}/pre-test-exit-status"
# Pessimistic: PTS treats a missing status as success, so until this hook reaches its last line the configuration
# counts as not set up. A hook killed anywhere in between leaves this latch, which run-test.sh turns into invalid
# repetitions.
printf 'pre-test hook did not complete\n' > "$PTS_FAILURE_LATCH" || exit 0
trap '[ "$PRE_COMPLETED" = 1 ] || { stop_server; record_invalid "pre-test: hook ended before completing"; }' EXIT
parse_args "$@" || fail "invalid arguments: $*"
cd "$PTS_HOME" || fail "cannot cd to $PTS_HOME"

# This profile is for local, unpublished runs: refuse to measure under a PTS configuration that could upload
# anything or touch the network.
config_check || fail "the PTS configuration is not local-only: ${CFG_PROBLEMS//$'\n'/; }"

# A leftover server from an interrupted run would hold the port.
stop_server
rm -f "$PTS_SERVER_LOG"

[ -x "$DOTNET_DIR/dotnet" ] || fail "missing runtime $RUNTIME ($DOTNET_DIR); was it installed?"
[ -f "$PUBLISH_DIR/PtsServer.dll" ] || fail "missing published server $PUBLISH_DIR/PtsServer.dll"
command -v curl > /dev/null || fail "curl is required to verify the server"

# Another listener on the port (including a same-user io_uring server, which could share it via SO_REUSEPORT)
# would contaminate the measurement.
if (exec 3<>"/dev/tcp/127.0.0.1/${PTS_PORT}") 2> /dev/null; then
    fail "port ${PTS_PORT} already has a listener; stop it or set PTS_KESTREL_PORT"
fi

# 4000 connections need far more than the common soft limit of 1024.
ulimit -n 1048576 2>/dev/null || ulimit -n "$(ulimit -Hn)" 2>/dev/null
FD_LIMIT="$(ulimit -n)"
if [ "$CONNECTIONS" -ge 500 ] && [ "$FD_LIMIT" -lt $((CONNECTIONS * 2 + 64)) ]; then
    fail "open-file limit $FD_LIMIT is too low for $CONNECTIONS connections (raise the hard limit)"
fi

export DOTNET_ROOT="$DOTNET_DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PTS_TRANSPORT="$TRANSPORT" PTS_PORT="$PTS_PORT" PTS_BIND=127.0.0.1 PTS_WWWROOT="${PTS_HOME}/wwwroot"
[ "$RINGS" = "auto" ] && export PTS_THREADS=auto || export PTS_THREADS="$RINGS"

# stdio MUST be redirected: PTS captures this script's output through a pipe and would wait for it to close,
# i.e. until the server exits, if the background server inherited it. setsid detaches it from PTS's process group.
setsid "$DOTNET_DIR/dotnet" "$PUBLISH_DIR/PtsServer.dll" > "$PTS_SERVER_LOG" 2>&1 < /dev/null &
LAUNCH_PID=$!
# Recorded immediately so fail() can stop a server that never becomes ready; replaced below by the pid the
# server reports about itself.
record_server "$LAUNCH_PID"

for _ in $(seq 1 80); do
    grep -q "PTS_SERVER_READY" "$PTS_SERVER_LOG" 2>/dev/null && break
    kill -0 "$LAUNCH_PID" 2>/dev/null || break
    sleep 0.5
done
grep -q "PTS_SERVER_READY" "$PTS_SERVER_LOG" \
    || fail "server did not become ready: $(grep -m1 -E 'Exception|Unhandled|Requested transport|must be' "$PTS_SERVER_LOG" 2>/dev/null | cut -c1-220)"

READY_LINE="$(grep "PTS_SERVER_READY" "$PTS_SERVER_LOG" | head -1)"
PID="$(echo "$READY_LINE" | sed -n 's/.*"pid":\([0-9]*\).*/\1/p')"
[ -n "$PID" ] || fail "could not read the server pid from: $READY_LINE"
record_server "$PID" || fail "server pid $PID is not running"
server_alive || fail "server pid $PID is not our PtsServer"

# Independent evidence of the transport and of who answers the port, read from /proc and HTTP rather than from
# the server's own startup report.
REASON="$(verify_transport)" || fail "$REASON"
REASON="$(verify_endpoint_identity)" || fail "$REASON"

# The workload must answer with what the profile claims to measure.
EXPECTED_SIZE=""
case "$WORKLOAD" in
    test.html) EXPECTED_SIZE="$(stat -c %s "${PTS_HOME}/wwwroot/test.html" 2>/dev/null)" ;;
    plaintext) EXPECTED_SIZE=13 ;;
esac
RESPONSE="$(curl_local -sS --max-time 10 -o /dev/null -w '%{http_code} %{size_download} %{content_type}' "$URL" 2>&1)" \
    || fail "workload request failed: $RESPONSE"
read -r HTTP_CODE HTTP_SIZE HTTP_TYPE <<< "$RESPONSE"
[ "$HTTP_CODE" = "200" ] || fail "workload $WORKLOAD answered HTTP $HTTP_CODE"
if [ -n "$EXPECTED_SIZE" ]; then
    [ "$HTTP_SIZE" = "$EXPECTED_SIZE" ] || fail "workload $WORKLOAD returned $HTTP_SIZE bytes, expected $EXPECTED_SIZE"
elif [ "$WORKLOAD" = "json" ]; then
    [ "$HTTP_SIZE" -gt 0 ] && [[ "$HTTP_TYPE" == application/json* ]] || fail "json workload returned '$HTTP_TYPE' with $HTTP_SIZE bytes"
fi

# Warm-up: JIT/tiering, connection pools and page cache.
if [ "$WARMUP" -gt 0 ]; then
    WARM_CONNECTIONS=$CONNECTIONS
    [ "$WARM_CONNECTIONS" -gt 64 ] && WARM_CONNECTIONS=64
    WARM_OUT="$("${PTS_HOME}/wrk-4.2.0/wrk" -t1 -c"$WARM_CONNECTIONS" -d"${WARMUP}"s "$URL" 2>&1)" \
        || fail "warm-up request failed: $WARM_OUT"
    echo "$WARM_OUT" | grep -qE "Non-2xx|Socket errors" && fail "warm-up produced errors: $WARM_OUT"
    REASON="$(verify_transport)" || fail "transport changed during warm-up: $REASON"
fi

ACTUAL="$(echo "$READY_LINE" | sed -n 's/.*"actualTransport":"\([^"]*\)".*/\1/p')"
RUNTIME_DESC="$(echo "$READY_LINE" | sed -n 's/.*"runtime":"\([^"]*\)".*/\1/p')"
RINGS_ACTUAL="$(echo "$READY_LINE" | sed -n 's/.*"rings":\([0-9]*\).*/\1/p')"
OVERRIDES="$(env | grep -E '^(DOTNET_|IOURING_)' | grep -vE '^DOTNET_(ROOT|CLI_TELEMETRY_OPTOUT|NOLOGO)=' | sort | tr '\n' ' ')"
echo "Transport: $ACTUAL (rings=$RINGS_ACTUAL), $RUNTIME_DESC, kernel $(uname -r), open-file limit $FD_LIMIT, wrk threads $(wrk_threads), warm-up ${WARMUP}s, source $(cat "${PTS_HOME}/source-commit" 2>/dev/null || echo unknown)${OVERRIDES:+, env: ${OVERRIDES% }}" \
    > "${PTS_HOME}/pts-footnote"

rm -f "$PTS_FAILURE_LATCH"
PRE_COMPLETED=1
echo 0 > "${PTS_HOME}/pre-test-exit-status"
exit 0
