#!/bin/bash
# Runs the installed local/aspnet-kestrel-iouring profile for exactly the option values you name, locally.
# It never uploads: it refuses to start unless the PTS configuration is verified upload-free, and it only calls
# `batch-benchmark`.
#
#   phoronix/run-local.sh [--transport iouring,sockets] [--workload test.html,plaintext,json]
#                         [--connections 1,20,100,200,500,1000,4000] [--runtime net10,net11]
#                         [--rings 1,auto] [--duration SECONDS] [--runs N] [--name RESULT_NAME] [--pts PATH]
#
# Every option is given by VALUE (the <Value> elements of test-definition.xml). PTS's own PRESET_OPTIONS matches
# menu positions and names instead: a number such as connections=1 selects the second entry, a number equal to the
# entry count means "test all", and a typo silently selects the first entry. Values avoid all three.
# Comma-separated lists run every combination. Defaults compare both transports on Plaintext at 100 connections.

set -euo pipefail

REPO="$(git rev-parse --show-toplevel)"
. "$REPO/phoronix/runtime/common.sh"

TRANSPORT_LIST=iouring,sockets
WORKLOAD_LIST=plaintext
CONNECTIONS_LIST=100
RUNTIME_LIST=net10
RINGS_LIST=auto
DURATION=90
RUNS=3
NAME="kestrel-$(date +%Y%m%d-%H%M%S)"
PTS_BIN="${PTS:-$(command -v phoronix-test-suite || true)}"

while [ $# -gt 0 ]; do
    case "$1" in
        --transport)   TRANSPORT_LIST="$2";   shift 2 ;;
        --workload)    WORKLOAD_LIST="$2";    shift 2 ;;
        --connections) CONNECTIONS_LIST="$2"; shift 2 ;;
        --runtime)     RUNTIME_LIST="$2";     shift 2 ;;
        --rings)       RINGS_LIST="$2";       shift 2 ;;
        --duration)    DURATION="$2";         shift 2 ;;
        --runs)        RUNS="$2";             shift 2 ;;
        --name)        NAME="$2";             shift 2 ;;
        --pts)         PTS_BIN="$2";          shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

check_list() { # option, list, regex of one allowed value
    local item
    for item in ${2//,/ }; do
        [[ "$item" =~ ^($3)$ ]] || { echo "Invalid --$1 value '$item' (allowed: $3)" >&2; exit 2; }
    done
}
check_list transport   "$TRANSPORT_LIST"   'iouring|sockets'
check_list workload    "$WORKLOAD_LIST"    'test\.html|plaintext|json'
check_list connections "$CONNECTIONS_LIST" '[1-9][0-9]*'
check_list runtime     "$RUNTIME_LIST"     'net10|net11'
check_list rings       "$RINGS_LIST"       '[1-9][0-9]*|auto'
check_list duration    "$DURATION"         '[1-9][0-9]*'
check_list runs        "$RUNS"             '[1-9][0-9]*'

[ -n "$PTS_BIN" ] && [ -x "$PTS_BIN" ] || { echo "phoronix-test-suite not found; pass --pts PATH or set PTS." >&2; exit 1; }

problems="$(config_upload_problems)"
if [ -n "$problems" ]; then
    echo "Refusing to run: the PTS configuration is not local-only:" >&2
    echo "$problems" >&2
    echo "Run phoronix/setup-local-pts.sh and retry." >&2
    exit 1
fi

OPT="aspnet-kestrel-iouring"
export PRESET_OPTIONS_VALUES="$OPT.transport=$TRANSPORT_LIST;$OPT.workload=$WORKLOAD_LIST;$OPT.connections=$CONNECTIONS_LIST;$OPT.runtime=$RUNTIME_LIST;$OPT.rings=$RINGS_LIST"
export FORCE_TIMES_TO_RUN="$RUNS" PTS_DURATION="$DURATION"
export TEST_RESULTS_NAME="$NAME" TEST_RESULTS_IDENTIFIER="$NAME"
export TEST_RESULTS_DESCRIPTION="${TEST_RESULTS_DESCRIPTION:-local run, not uploaded}"

echo "Results: $NAME (stored under ~/.phoronix-test-suite/test-results/, not uploaded)"
echo "Options: $PRESET_OPTIONS_VALUES; $RUNS run(s) of ${DURATION}s each"
exec "$PTS_BIN" batch-benchmark "local/$OPT"
