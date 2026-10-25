#!/bin/bash
# Runs the installed local/aspnet-kestrel-iouring profile for exactly the option VALUES you name, locally. It never
# uploads: it refuses to start unless the PTS configuration PTS will read is verified local-only (uploads, usage
# reporting and PTS network access off), it disables PTS modules, and it only ever calls `batch-benchmark`.
#
#   phoronix/run-local.sh [--transport iouring,sockets] [--workload test.html,plaintext,json]
#                         [--connections 1,20,100,200,500,1000,4000] [--runtime net10,net11]
#                         [--rings 1,auto] [--duration 90|30|10] [--runs N] [--name RESULT_NAME]
#                         [--pts PATH] [--no-egress]
#
# Every option is given by VALUE (the <Value> elements of test-definition.xml). PTS's own PRESET_OPTIONS matches
# menu positions and names instead: a number such as connections=1 selects the second entry, a number equal to the
# entry count means "test all", and a typo silently selects the first entry. Values avoid all three.
# Comma-separated lists run every combination. Defaults compare both transports on Plaintext at 100 connections.
#
# --no-egress runs everything in a network namespace that has only loopback (needs sudo, unshare and ip): the
# one guarantee that does not depend on PTS settings. The exit status is non-zero when PTS fails OR when any
# repetition/configuration was invalid (PTS itself averages whatever repetitions survive).

set -uo pipefail

SELF="$(readlink -f "$0")"
REPO="$(git -C "$(dirname "$SELF")" rev-parse --show-toplevel)"
. "$REPO/phoronix/runtime/common.sh"

die() { echo "run-local: $*" >&2; exit 2; }

TRANSPORT_LIST=iouring,sockets
WORKLOAD_LIST=plaintext
CONNECTIONS_LIST=100
RUNTIME_LIST=net10
RINGS_LIST=auto
DURATION=90
RUNS=3
NAME="kestrel-$(date +%Y%m%d-%H%M%S)"
PTS_BIN="${PTS:-$(command -v phoronix-test-suite || true)}"
NO_EGRESS=0
FORWARD_ARGS=()

while [ $# -gt 0 ]; do
    case "$1" in
        --no-egress) NO_EGRESS=1; shift; continue ;;
        --transport|--workload|--connections|--runtime|--rings|--duration|--runs|--name|--pts)
            [ $# -ge 2 ] || die "$1 needs a value"
            FORWARD_ARGS+=("$1" "$2")
            case "$1" in
                --transport)   TRANSPORT_LIST="$2" ;;
                --workload)    WORKLOAD_LIST="$2" ;;
                --connections) CONNECTIONS_LIST="$2" ;;
                --runtime)     RUNTIME_LIST="$2" ;;
                --rings)       RINGS_LIST="$2" ;;
                --duration)    DURATION="$2" ;;
                --runs)        RUNS="$2" ;;
                --name)        NAME="$2" ;;
                --pts)         PTS_BIN="$2" ;;
            esac
            shift 2 ;;
        *) die "unknown argument: $1" ;;
    esac
done

# check_csv <option> <comma separated list> <regex for one item>: no empty items, no spaces, no globbing.
check_csv() {
    local option="$1" list="$2" regex="$3"
    [ -n "$list" ] || die "--$option must not be empty"
    [[ "$list" =~ ^($regex)(,($regex))*$ ]] || die "invalid --$option '$list' (comma separated, each of: $regex)"
}
check_csv transport   "$TRANSPORT_LIST"   'iouring|sockets'
check_csv workload    "$WORKLOAD_LIST"    'test\.html|plaintext|json'
check_csv connections "$CONNECTIONS_LIST" '[1-9][0-9]{0,5}'
check_csv runtime     "$RUNTIME_LIST"     'net10|net11'
check_csv rings       "$RINGS_LIST"       '[1-9][0-9]{0,2}|auto'
[[ "$DURATION" =~ ^[1-9][0-9]{0,4}$ ]] || die "invalid --duration '$DURATION' (seconds)"
[[ "$RUNS" =~ ^[1-9][0-9]?$ ]] || die "invalid --runs '$RUNS'"
[[ "$NAME" =~ ^[A-Za-z0-9._-]{1,60}$ ]] || die "invalid --name '$NAME' (letters, digits, . _ - only)"

[ -n "$PTS_BIN" ] && [ -x "$PTS_BIN" ] || die "phoronix-test-suite not found; pass --pts PATH or set PTS"

if ! config_check; then
    echo "Refusing to run: the PTS configuration is not local-only:" >&2
    echo "$CFG_PROBLEMS" | sed 's/^/  - /' >&2
    echo "Run phoronix/setup-local-pts.sh and retry." >&2
    exit 1
fi

OPT="aspnet-kestrel-iouring"
INSTALLED="${CFG_USER_PATH}installed-tests/local/$OPT"
if [ ! -f "$INSTALLED/source-commit" ] || [ ! -f "$INSTALLED/pts-install.json" ]; then
    echo "Refusing to run: local/$OPT is not installed (batch-benchmark would try to install it)." >&2
    echo "Run: phoronix-test-suite force-install local/$OPT" >&2
    exit 1
fi
INSTALLED_COMMIT="$(cat "$INSTALLED/source-commit")"
if [ -f "$REPO/phoronix/dist/SOURCE_COMMIT" ] && [ "$INSTALLED_COMMIT" != "$(cat "$REPO/phoronix/dist/SOURCE_COMMIT")" ]; then
    echo "warning: installed source $INSTALLED_COMMIT differs from the last bundle $(cat "$REPO/phoronix/dist/SOURCE_COMMIT"); run force-install to update." >&2
fi

if [ "$NO_EGRESS" = "1" ] && [ -z "${PTS_LOCAL_IN_NETNS:-}" ]; then
    command -v sudo > /dev/null && command -v unshare > /dev/null && command -v ip > /dev/null \
        || die "--no-egress needs sudo, unshare and ip"
    exec sudo unshare -n bash -c 'ip link set lo up && exec sudo -u "$0" -H env PATH="$1" PTS_LOCAL_IN_NETNS=1 PTS="$2" "${@:3}"' \
        "$USER" "$PATH" "$PTS_BIN" "$SELF" "${FORWARD_ARGS[@]}"
fi

# PTS modules can reach the network on their own (direct cURL, mail) no matter what NoNetworkCommunication says.
export PTS_IGNORE_MODULES=1
export PRESET_OPTIONS_VALUES="$OPT.transport=$TRANSPORT_LIST;$OPT.workload=$WORKLOAD_LIST;$OPT.connections=$CONNECTIONS_LIST;$OPT.runtime=$RUNTIME_LIST;$OPT.rings=$RINGS_LIST;$OPT.duration=$DURATION"
export FORCE_TIMES_TO_RUN="$RUNS"
export TEST_RESULTS_NAME="$NAME" TEST_RESULTS_IDENTIFIER="$NAME"
export TEST_RESULTS_DESCRIPTION="${TEST_RESULTS_DESCRIPTION:-local run, not uploaded}"

: > "$INSTALLED/invalid-runs.log"

cleanup_server() {
    ( PTS_HOME="$INSTALLED"; . "$REPO/phoronix/runtime/common.sh"; stop_server )
}

echo "Results: $NAME (stored under ${CFG_USER_PATH}test-results/, not uploaded)"
echo "Options: $PRESET_OPTIONS_VALUES; $RUNS run(s)"

"$PTS_BIN" batch-benchmark "local/$OPT" &
PTS_PID=$!
trap 'kill -TERM "$PTS_PID" 2>/dev/null' INT TERM
while true; do
    wait "$PTS_PID"
    PTS_RC=$?
    # A trapped signal interrupts `wait` while PTS is still shutting down.
    kill -0 "$PTS_PID" 2> /dev/null || break
done
trap - INT TERM
cleanup_server

STATUS=0
[ "${PTS_RC:-1}" -eq 0 ] || { echo "run-local: PTS exited with status ${PTS_RC:-?}" >&2; STATUS=1; }
if [ -s "$INSTALLED/invalid-runs.log" ]; then
    echo >&2
    echo "run-local: INVALID runs (PTS averaged whatever repetitions survived; these produced no number):" >&2
    sed 's/^/  /' "$INSTALLED/invalid-runs.log" >&2
    STATUS=1
fi
exit "$STATUS"
