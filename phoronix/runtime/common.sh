#!/bin/bash
# Shared helpers for the aspnet-kestrel-iouring PTS profile (sourced by pre.sh, post.sh and run-test.sh).
#
# PTS runs profile scripts with the installed-test directory as both cwd and $HOME, so every path here is
# relative to $HOME. Nothing in this file talks to the network or to OpenBenchmarking.org.

PTS_HOME="${HOME}"
PTS_PORT="${PTS_KESTREL_PORT:-18099}"
PTS_SERVER_PID_FILE="${PTS_HOME}/server.pid"
PTS_SERVER_LOG="${PTS_HOME}/server.log"

# Option parsing. PTS concatenates the selected menu entries into one argument string, e.g.
#   --transport iouring --workload test.html -c 100 --runtime net10 --rings 1 -d 90
parse_args() {
    TRANSPORT=iouring
    WORKLOAD=test.html
    CONNECTIONS=1
    RUNTIME=net10
    RINGS=1
    DURATION=90
    while [ $# -gt 0 ]; do
        case "$1" in
            --transport) TRANSPORT="$2"; shift 2 ;;
            --workload)  WORKLOAD="$2";  shift 2 ;;
            --runtime)   RUNTIME="$2";   shift 2 ;;
            --rings)     RINGS="$2";     shift 2 ;;
            -c)          CONNECTIONS="$2"; shift 2 ;;
            -d)          DURATION="${2%s}"; shift 2 ;;
            *)           shift ;;
        esac
    done
    # A shorter duration for smoke runs without editing the profile: PTS_DURATION=10
    DURATION="${PTS_DURATION:-$DURATION}"

    case "$TRANSPORT" in iouring|sockets) ;; *) echo "Unknown transport '$TRANSPORT'" >&2; return 1 ;; esac
    case "$WORKLOAD" in test.html|plaintext|json) ;; *) echo "Unknown workload '$WORKLOAD'" >&2; return 1 ;; esac
    case "$RUNTIME" in
        net10) DOTNET_DIR="${PTS_HOME}/dotnet10" ;;
        net11) DOTNET_DIR="${PTS_HOME}/dotnet11" ;;
        *) echo "Unknown runtime '$RUNTIME'" >&2; return 1 ;;
    esac
    PUBLISH_DIR="${PTS_HOME}/publish/${RUNTIME}"
    URL="http://127.0.0.1:${PTS_PORT}/${WORKLOAD}"
    return 0
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

server_pid() {
    [ -f "$PTS_SERVER_PID_FILE" ] && cat "$PTS_SERVER_PID_FILE"
}

# True only if the recorded PID is alive AND is our server (guards against PID reuse).
server_alive() {
    local pid
    pid="$(server_pid)" || return 1
    [ -n "$pid" ] && [ -r "/proc/$pid/cmdline" ] && tr '\0' ' ' < "/proc/$pid/cmdline" | grep -q "PtsServer.dll"
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

# Stops the server we started: graceful SIGTERM first, SIGKILL only for that exact PID after a timeout.
stop_server() {
    local pid i
    pid="$(server_pid)" || return 0
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
