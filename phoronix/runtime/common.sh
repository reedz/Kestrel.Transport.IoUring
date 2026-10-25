#!/bin/bash
# Shared helpers for the aspnet-kestrel-iouring PTS profile (sourced by pre.sh, post.sh, run-test.sh and the
# launcher/setup scripts).
#
# PTS runs profile scripts with the installed-test directory as both cwd and $HOME, so profile state lives under
# $PTS_HOME (defaults to $HOME). Nothing in this file talks to the network or to OpenBenchmarking.org except the
# loopback requests to the benchmark server.

PTS_COMMON_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# State lives in the directory PTS gives the hooks as $HOME. Deliberately not overridable from the environment:
# an inherited override would send status files somewhere PTS never reads them.
PTS_HOME="${HOME%/}"   # PTS exports $HOME with a trailing slash; normalised so paths compare equal everywhere
PTS_PORT="${PTS_KESTREL_PORT:-18099}"
PTS_SERVER_PID_FILE="${PTS_HOME}/server.pid"
PTS_SERVER_LOG="${PTS_HOME}/server.log"
PTS_INVALID_LOG="${PTS_HOME}/invalid-runs.log"
PTS_FAILURE_LATCH="${PTS_HOME}/config-invalid"

# Option parsing. PTS concatenates the selected menu entries into one argument string, e.g.
#   -d 90 --transport iouring --workload test.html -c 100 --runtime net10 --rings 1
# Every value is validated here, so nothing user-controlled reaches a command line unchecked.
parse_args() {
    TRANSPORT=iouring
    WORKLOAD=test.html
    CONNECTIONS=1
    RUNTIME=net10
    RINGS=1
    DURATION=90
    ARGS_TEXT="$*"
    while [ $# -gt 0 ]; do
        case "$1" in
            --transport|--workload|--runtime|--rings|-c|-d)
                [ $# -ge 2 ] || { echo "Missing value for $1" >&2; return 1; }
                case "$1" in
                    --transport) TRANSPORT="$2" ;;
                    --workload)  WORKLOAD="$2" ;;
                    --runtime)   RUNTIME="$2" ;;
                    --rings)     RINGS="$2" ;;
                    -c)          CONNECTIONS="$2" ;;
                    -d)          DURATION="$2" ;;
                esac
                shift 2 ;;
            *) echo "Unknown argument '$1'" >&2; return 1 ;;
        esac
    done

    case "$TRANSPORT" in iouring|sockets) ;; *) echo "Unknown transport '$TRANSPORT'" >&2; return 1 ;; esac
    case "$WORKLOAD" in test.html|plaintext|json) ;; *) echo "Unknown workload '$WORKLOAD'" >&2; return 1 ;; esac
    [[ "$CONNECTIONS" =~ ^[1-9][0-9]{0,5}$ ]] || { echo "Invalid connection count '$CONNECTIONS'" >&2; return 1; }
    [[ "$DURATION" =~ ^[1-9][0-9]{0,4}$ ]] || { echo "Invalid duration '$DURATION'" >&2; return 1; }
    [[ "$RINGS" =~ ^([1-9][0-9]{0,2}|auto)$ ]] || { echo "Invalid ring count '$RINGS'" >&2; return 1; }
    WARMUP="${PTS_WARMUP:-8}"
    [[ "$WARMUP" =~ ^[0-9]{1,3}$ ]] || { echo "Invalid PTS_WARMUP '$WARMUP'" >&2; return 1; }
    case "$RUNTIME" in
        net10) DOTNET_DIR="${PTS_HOME}/dotnet10"; RUNTIME_BANNER='.NET 10.' ;;
        net11) DOTNET_DIR="${PTS_HOME}/dotnet11"; RUNTIME_BANNER='.NET 11.' ;;
        *) echo "Unknown runtime '$RUNTIME'" >&2; return 1 ;;
    esac
    PUBLISH_DIR="${PTS_HOME}/publish/${RUNTIME}"
    URL="http://127.0.0.1:${PTS_PORT}/${WORKLOAD}"
    return 0
}

# Records that this configuration (or repetition) produced no trustworthy number. run-local.sh prints this log
# and fails, because PTS itself averages whatever repetitions survived and says nothing about the rest.
record_invalid() {
    printf '%s | %s | %s\n' "$(date +%Y-%m-%dT%H:%M:%S)" "${ARGS_TEXT:-?}" "$1" >> "$PTS_INVALID_LOG"
}

# wrk rejects fewer connections than threads, and divides connections evenly across threads (rounding down).
# Pick the largest thread count <= min(cores, connections) that divides the connection count.
wrk_threads() {
    local threads="${NUM_CPU_CORES:-$(nproc)}"
    [ "$threads" -gt "$CONNECTIONS" ] && threads="$CONNECTIONS"
    while [ "$threads" -gt 1 ] && [ $((CONNECTIONS % threads)) -ne 0 ]; do
        threads=$((threads - 1))
    done
    echo "$threads"
}

# Checks the PTS configuration that PTS itself will read (see check-pts-config.php). Sets CFG_USER_PATH, CFG_FILE
# and CFG_PROBLEMS; returns 0 only when there are no problems. A checker that cannot run is a problem too.
config_check() {
    local out rc line
    CFG_USER_PATH="" CFG_FILE="" CFG_PROBLEMS=""
    if ! command -v php > /dev/null 2>&1; then
        CFG_PROBLEMS="php is not installed, so the PTS configuration cannot be verified"
        return 1
    fi
    out="$(php "$PTS_COMMON_DIR/check-pts-config.php" "$@" 2>&1)"
    rc=$?
    while IFS= read -r line; do
        case "$line" in
            USER_PATH=*) CFG_USER_PATH="${line#USER_PATH=}" ;;
            CONFIG=*)    CFG_FILE="${line#CONFIG=}" ;;
            PROBLEM=*)   CFG_PROBLEMS="${CFG_PROBLEMS:+$CFG_PROBLEMS$'\n'}${line#PROBLEM=}" ;;
            *)           [ "$rc" -gt 1 ] && CFG_PROBLEMS="${CFG_PROBLEMS:+$CFG_PROBLEMS$'\n'}checker error: $line" ;;
        esac
    done <<< "$out"
    [ "$rc" -le 1 ] || [ -n "$CFG_PROBLEMS" ] || CFG_PROBLEMS="config checker failed with exit status $rc"
    [ "$rc" -eq 0 ] && [ -z "$CFG_PROBLEMS" ]
}

proc_starttime() {
    sed 's/^.*) //' "/proc/$1/stat" 2>/dev/null | cut -d' ' -f20
}

# The server pid file holds "<pid> <process start time>": the start time makes a recycled pid unmistakable.
server_pid() {
    local pid
    [ -f "$PTS_SERVER_PID_FILE" ] || return 1
    read -r pid _ < "$PTS_SERVER_PID_FILE" || return 1
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    echo "$pid"
}

record_server() {
    local pid="$1" start
    start="$(proc_starttime "$pid")"
    [ -n "$start" ] || return 1
    echo "$pid $start" > "$PTS_SERVER_PID_FILE"
}

# True only if the recorded process is alive, started when we recorded it, and runs our published server.
server_alive() {
    local pid recorded_start
    pid="$(server_pid)" || return 1
    read -r _ recorded_start < "$PTS_SERVER_PID_FILE" || return 1
    [ -n "$recorded_start" ] && [ "$(proc_starttime "$pid")" = "$recorded_start" ] || return 1
    tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -q "/publish/net1[01]/PtsServer.dll"
}

# Number of io_uring instances open in the server process, read from /proc (independent of the server's own report).
server_io_uring_fds() {
    local pid="$1" count=0 fd
    for fd in /proc/"$pid"/fd/*; do
        case "$(readlink "$fd" 2>/dev/null)" in *io_uring*) count=$((count + 1)) ;; esac
    done
    echo "$count"
}

# Verifies that the server really serves with the requested transport. Prints a reason and returns 1 if not.
verify_transport() {
    local pid fds
    pid="$(server_pid)" || { echo "no server pid"; return 1; }
    fds="$(server_io_uring_fds "$pid")"
    if [ "$TRANSPORT" = "iouring" ] && [ "$fds" -lt 1 ]; then
        echo "requested io_uring but the server process has no io_uring instance (silent fallback to sockets?)"
        return 1
    fi
    if [ "$TRANSPORT" = "sockets" ] && [ "$fds" -ne 0 ]; then
        echo "requested sockets but the server process has $fds io_uring instance(s)"
        return 1
    fi
    return 0
}

# curl for the loopback checks: ignore ~/.curlrc and proxy environment variables, which would otherwise route even
# 127.0.0.1 requests through a remote proxy.
curl_local() {
    curl -q --noproxy '*' "$@"
}

# Verifies that the HTTP endpoint wrk will hit is answered by OUR server process, with the requested runtime and
# transport. READY plus /proc evidence alone would not notice another server sharing the port (SO_REUSEPORT).
verify_endpoint_identity() {
    local pid body served
    pid="$(server_pid)" || { echo "no server pid"; return 1; }
    body="$(curl_local -fsS --max-time 5 "http://127.0.0.1:${PTS_PORT}/__transport" 2>&1)" \
        || { echo "cannot query /__transport on port ${PTS_PORT}: $body"; return 1; }
    served="$(sed -n 's/.*"pid":\([0-9]*\).*/\1/p' <<< "$body")"
    [ "$served" = "$pid" ] || { echo "port ${PTS_PORT} is answered by pid '${served:-?}', not by the server this run started ($pid)"; return 1; }
    grep -q "\"requestedTransport\":\"${TRANSPORT}\"" <<< "$body" || { echo "server reports a different requested transport: $body"; return 1; }
    grep -q "\"runtime\":\"${RUNTIME_BANNER}" <<< "$body" || { echo "server runs a different .NET runtime than ${RUNTIME}: $body"; return 1; }
    return 0
}

# Stops the server we started: graceful SIGTERM first, SIGKILL only for that exact process after a timeout.
stop_server() {
    local pid i
    pid="$(server_pid)" || { rm -f "$PTS_SERVER_PID_FILE"; return 0; }
    if server_alive; then
        kill -TERM "$pid" 2>/dev/null
        for i in $(seq 1 30); do
            server_alive || break
            sleep 0.5
        done
        server_alive && kill -KILL "$pid" 2>/dev/null
    fi
    rm -f "$PTS_SERVER_PID_FILE"
}
