#!/bin/bash
# Pre-test hook: start the ASP.NET Core server with the requested transport and runtime, prove which transport
# is really serving, and warm it up. Runs once per option combination (before its repetitions).
#
# Failure protocol: a non-zero value in ~/pre-test-exit-status makes PTS abandon this configuration, so a run
# that would have measured the wrong transport produces no number at all.

. "$(dirname "$0")/common.sh"

fail() {
    echo "ERROR: $*" >&2
    [ -f "$PTS_SERVER_LOG" ] && tail -n 20 "$PTS_SERVER_LOG" >&2
    stop_server
    echo 1 > "${PTS_HOME}/pre-test-exit-status"
    exit 1
}

parse_args "$@" || fail "invalid arguments: $*"
cd "$PTS_HOME" || fail "cannot cd to $PTS_HOME"

# This profile is for local, unpublished runs: refuse to measure under a PTS configuration that would upload.
if [ "${PTS_KESTREL_ALLOW_UPLOAD_CONFIG:-0}" != "1" ]; then
    PROBLEMS="$(config_upload_problems)"
    [ -z "$PROBLEMS" ] || fail "PTS is configured so that results or usage data may be uploaded; run phoronix/setup-local-pts.sh (or set PTS_KESTREL_ALLOW_UPLOAD_CONFIG=1 to override):
$PROBLEMS"
fi

# A leftover server from an interrupted run would hold the port.
stop_server
rm -f "$PTS_SERVER_LOG" "${PTS_HOME}/pre-test-exit-status"

[ -x "$DOTNET_DIR/dotnet" ] || fail "missing runtime $RUNTIME ($DOTNET_DIR); was it installed?"
[ -f "$PUBLISH_DIR/PtsServer.dll" ] || fail "missing published server $PUBLISH_DIR/PtsServer.dll"

# 4000 connections need far more than the common soft limit of 1024.
ulimit -n 1048576 2>/dev/null || ulimit -n "$(ulimit -Hn)" 2>/dev/null
FD_LIMIT="$(ulimit -n)"
if [ "$CONNECTIONS" -ge 500 ] && [ "$FD_LIMIT" -lt $((CONNECTIONS * 2 + 64)) ]; then
    fail "open-file limit $FD_LIMIT is too low for $CONNECTIONS connections (raise the hard limit)"
fi

export DOTNET_ROOT="$DOTNET_DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PTS_TRANSPORT="$TRANSPORT" PTS_PORT="$PTS_PORT" PTS_WWWROOT="${PTS_HOME}/wwwroot"
[ "$RINGS" = "auto" ] && export PTS_THREADS=auto || export PTS_THREADS="$RINGS"

# stdio MUST be redirected: PTS captures this script's output through a pipe and would wait for it to close,
# i.e. until the server exits, if the background server inherited it. setsid detaches it from PTS's process group.
setsid "$DOTNET_DIR/dotnet" "$PUBLISH_DIR/PtsServer.dll" > "$PTS_SERVER_LOG" 2>&1 < /dev/null &

for _ in $(seq 1 80); do
    grep -q "PTS_SERVER_READY" "$PTS_SERVER_LOG" 2>/dev/null && break
    grep -qE "Unhandled exception|PTS_TRANSPORT must" "$PTS_SERVER_LOG" 2>/dev/null && break
    sleep 0.5
done
grep -q "PTS_SERVER_READY" "$PTS_SERVER_LOG" || fail "server did not become ready within 40 s"

READY_LINE="$(grep "PTS_SERVER_READY" "$PTS_SERVER_LOG" | head -1)"
PID="$(echo "$READY_LINE" | sed -n 's/.*"pid":\([0-9]*\).*/\1/p')"
[ -n "$PID" ] || fail "could not read the server pid from: $READY_LINE"
echo "$PID" > "$PTS_SERVER_PID_FILE"
server_alive || fail "server pid $PID is not running"

# Independent evidence of the transport, read from /proc rather than from the server's own report.
REASON="$(verify_transport)" || fail "$REASON"

# Warm-up: JIT/tiering, connection pools and page cache, and a sanity check of the workload itself.
WARM_CONNECTIONS=$CONNECTIONS
[ "$WARM_CONNECTIONS" -gt 64 ] && WARM_CONNECTIONS=64
WARM_OUT="$("${PTS_HOME}/wrk-4.2.0/wrk" -t1 -c"$WARM_CONNECTIONS" -d"${PTS_WARMUP:-8}"s "$URL" 2>&1)" \
    || fail "warm-up request failed: $WARM_OUT"
echo "$WARM_OUT" | grep -qE "Non-2xx|Socket errors" && fail "warm-up produced errors: $WARM_OUT"
verify_transport > /dev/null || fail "transport changed during warm-up"

ACTUAL="$(echo "$READY_LINE" | sed -n 's/.*"actualTransport":"\([^"]*\)".*/\1/p')"
RUNTIME_DESC="$(echo "$READY_LINE" | sed -n 's/.*"runtime":"\([^"]*\)".*/\1/p')"
RINGS_ACTUAL="$(echo "$READY_LINE" | sed -n 's/.*"rings":\([0-9]*\).*/\1/p')"
echo "Transport: $ACTUAL (rings=$RINGS_ACTUAL), $RUNTIME_DESC, kernel $(uname -r), open-file limit $FD_LIMIT" \
    > "${PTS_HOME}/pts-footnote"

echo 0 > "${PTS_HOME}/pre-test-exit-status"
exit 0
