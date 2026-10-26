#!/bin/bash
# Runs the installed local/aspnet-kestrel-iouring profile for exactly the option VALUES you name, locally. It never
# uploads: it refuses to start unless the PTS configuration and environment PTS will use are verified local-only
# (uploads, usage reporting and PTS network access off), it disables PTS modules, it refuses to install anything,
# and it only ever calls `batch-run` (run installed tests; unlike `batch-benchmark` it never invokes the installer).
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
# --name must be new, lowercase letters/digits/hyphens: PTS silently skips a configuration that already has a result
# under the same name (before any hook runs), which would let old numbers stand in for a failed rerun.
#
# --no-egress runs everything in a network namespace that has only loopback (needs sudo, unshare and ip): the one
# guarantee about IP traffic that does not depend on PTS settings, and a private loopback, so no other process can
# bind the benchmark port. Only a fixed allowlist of PTS_*/IOURING_*/DOTNET_* variables crosses sudo into it.
#
# The exit status is non-zero when PTS fails, when any repetition/configuration was invalid (PTS itself averages
# whatever repetitions survive), or when the number of results is not the number of requested configurations.

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
[[ "$NAME" =~ ^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$ ]] || die "invalid --name '$NAME' (lowercase letters, digits and single hyphens; PTS would rewrite anything else)"
[[ "$NAME" != *--* ]] || die "invalid --name '$NAME' (no double hyphens; PTS would collapse them)"
[ "$NAME" != "0" ] || die "invalid --name '0' (PTS treats it as empty and invents a timestamp)"

[ -n "$PTS_BIN" ] && [ -x "$PTS_BIN" ] || die "phoronix-test-suite not found; pass --pts PATH or set PTS"

if [ "$NO_EGRESS" = "1" ]; then
    command -v sudo > /dev/null && command -v unshare > /dev/null && command -v ip > /dev/null \
        || die "--no-egress needs sudo, unshare and ip"
    # sudo's env_reset would silently drop the experiment's settings (a forced-fallback negative test could become an
    # ordinary successful run), so a fixed allowlist is forwarded explicitly and echoed.
    FORWARD_ENV=()
    for name in $(compgen -e); do
        case "$name" in
            PTS_WARMUP|PTS_KESTREL_PORT|IOURING_*|DOTNET_*) FORWARD_ENV+=("$name=${!name}") ;;
        esac
    done
    [ "${#FORWARD_ENV[@]}" -eq 0 ] || echo "run-local: forwarding into the namespace: ${FORWARD_ENV[*]}"
    # Always creates a fresh namespace (never trusts an inherited marker) and checks that it has only loopback.
    # ${arr[@]+"${arr[@]}"} keeps an empty array from tripping `set -u` on bash older than 4.4.
    exec sudo unshare -n bash -c '
        ip link set lo up || exit 1
        [ "$(ip -o link show | wc -l)" = 1 ] || { echo "run-local: the namespace has more than the loopback interface" >&2; exit 1; }
        user="$0"
        exec sudo -u "$user" -H env "$@"' \
        "$(id -un)" PATH="$PATH" PTS="$PTS_BIN" ${FORWARD_ENV[@]+"${FORWARD_ENV[@]}"} "$SELF" ${FORWARD_ARGS[@]+"${FORWARD_ARGS[@]}"}
fi

if ! config_check; then
    echo "Refusing to run: the PTS configuration/environment is not local-only:" >&2
    echo "$CFG_PROBLEMS" | sed 's/^/  - /' >&2
    echo "Run phoronix/setup-local-pts.sh (and unset the variables named above) and retry." >&2
    exit 1
fi

OPT="aspnet-kestrel-iouring"
INSTALLED="${CFG_USER_PATH}installed-tests/local/$OPT"
if [ ! -f "$INSTALLED/source-commit" ] || [ ! -f "$INSTALLED/pts-install.json" ]; then
    echo "Refusing to run: local/$OPT is not installed." >&2
    echo "Run: phoronix-test-suite force-install local/$OPT" >&2
    exit 1
fi
INSTALLED_COMMIT="$(cat "$INSTALLED/source-commit")"
if [ -f "$REPO/phoronix/dist/SOURCE_COMMIT" ] && [ "$INSTALLED_COMMIT" != "$(cat "$REPO/phoronix/dist/SOURCE_COMMIT")" ]; then
    echo "warning: installed source $INSTALLED_COMMIT differs from the last bundle $(cat "$REPO/phoronix/dist/SOURCE_COMMIT"); run force-install to update." >&2
fi

# One launcher per installation: the server pid file, invalid-runs log and cleanup are shared state.
exec 9> "$INSTALLED/run-local.lock" || die "cannot create the launcher lock"
flock -n 9 || die "another run-local.sh is already using this installation"

RESULT_DIR="${CFG_USER_PATH}test-results/$NAME"
[ ! -e "$RESULT_DIR" ] || die "a result named '$NAME' already exists in ${CFG_USER_PATH}test-results/; PTS would silently skip configurations that are already stored. Pick a new --name."

# The exact configurations that must end up stored, in the argument format PTS records (option order of
# test-definition.xml). Compared as a set, not only by count: a count can match while the wrong configurations ran.
EXPECTED_ARGS=()
split_csv() { local IFS=,; read -ra "$1" <<< "$2"; }
split_csv T_LIST "$TRANSPORT_LIST"; split_csv W_LIST "$WORKLOAD_LIST"; split_csv C_LIST "$CONNECTIONS_LIST"
split_csv R_LIST "$RUNTIME_LIST"; split_csv G_LIST "$RINGS_LIST"
for t in "${T_LIST[@]}"; do for w in "${W_LIST[@]}"; do for c in "${C_LIST[@]}"; do for rt in "${R_LIST[@]}"; do for g in "${G_LIST[@]}"; do
    EXPECTED_ARGS+=("-d $DURATION --transport $t --workload $w -c $c --runtime $rt --rings $g")
done; done; done; done; done
EXPECTED_RESULTS="${#EXPECTED_ARGS[@]}"

# PTS modules can reach the network on their own (direct cURL, mail) no matter what NoNetworkCommunication says.
export PTS_IGNORE_MODULES=1
# The launcher's values already cover every option; a stray PRESET_OPTIONS must not take part in the selection.
unset PRESET_OPTIONS
export PRESET_OPTIONS_VALUES="$OPT.transport=$TRANSPORT_LIST;$OPT.workload=$WORKLOAD_LIST;$OPT.connections=$CONNECTIONS_LIST;$OPT.runtime=$RUNTIME_LIST;$OPT.rings=$RINGS_LIST;$OPT.duration=$DURATION"
export FORCE_TIMES_TO_RUN="$RUNS"
export TEST_RESULTS_NAME="$NAME" TEST_RESULTS_IDENTIFIER="$NAME"
export TEST_RESULTS_DESCRIPTION="${TEST_RESULTS_DESCRIPTION:-local run, not uploaded}"

: > "$INSTALLED/invalid-runs.log"

cleanup_server() {
    ( HOME="$INSTALLED"; . "$REPO/phoronix/runtime/common.sh"; stop_server )
}

echo "Results: $NAME (stored under ${CFG_USER_PATH}test-results/, not uploaded); expecting $EXPECTED_RESULTS configuration(s)"
echo "Options: $PRESET_OPTIONS_VALUES; $RUNS run(s)"

# PTS runs in its own process group so an interrupt can take down the whole tree (PTS's sh wrapper, php, the
# measured wrapper and wrk), not just the wrapper. The benchmark server has its own session and is stopped by PID.
setsid "$PTS_BIN" batch-run "local/$OPT" &
PTS_PID=$!
trap 'kill -TERM -- "-$PTS_PID" 2>/dev/null' INT TERM
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

# Process exit codes and the invalid-runs log are not enough on their own: PTS drops failed repetitions from the
# average without saying so, records an empty stub for a failed configuration, and can skip configurations without
# running any hook. So compare what was actually stored with what was requested: every requested configuration must
# be stored exactly once with exactly --runs valid numbers, and nothing else may be stored.
COVERAGE="$(printf '%s\n' "${EXPECTED_ARGS[@]}" | php -r '
    $expected = array_filter(explode("\n", stream_get_contents(STDIN)), "strlen");
    $d = new DOMDocument();
    if (!file_exists($argv[1]) || !@$d->load($argv[1])) { echo "0\nno readable stored result\n"; exit; }
    $runs = (int) $argv[2]; $good = 0; $bad = []; $seen = [];
    foreach ($d->getElementsByTagName("Result") as $r) {
        $text = function ($tag) use ($r) { $n = $r->getElementsByTagName($tag); return $n->length ? trim($n->item(0)->nodeValue) : ""; };
        $args = $text("Arguments");
        if (!in_array($args, $expected, true)) { $bad[] = "unexpected configuration stored: $args"; continue; }
        if (isset($seen[$args])) { $bad[] = "configuration stored twice: $args"; continue; }
        $seen[$args] = true;
        $positive = fn($v) => is_numeric(trim($v)) && (float) $v > 0;
        $values = array_values(array_filter(explode(":", $text("RawString")), $positive));
        if (!$values && $positive($text("Value"))) { $values = [$text("Value")]; }
        if (count($values) === $runs) { $good++; } else { $bad[] = "$args: " . count($values) . " of $runs valid repetitions stored"; }
    }
    foreach ($expected as $args) { if (!isset($seen[$args])) { $bad[] = "requested configuration was not stored: $args"; } }
    echo $good, "\n", implode("\n", $bad), "\n";' "$RESULT_DIR/composite.xml" "$RUNS")"
RESULTS_GOOD="$(head -n 1 <<< "$COVERAGE")"
EXTRA_PROBLEMS="$(tail -n +2 <<< "$COVERAGE" | sed '/^$/d')"
if [ "$RESULTS_GOOD" != "$EXPECTED_RESULTS" ] || [ -n "$EXTRA_PROBLEMS" ]; then
    echo "run-local: $RESULTS_GOOD of $EXPECTED_RESULTS requested configuration(s) hold $RUNS valid repetition(s):" >&2
    [ -z "$EXTRA_PROBLEMS" ] || sed 's/^/  /' <<< "$EXTRA_PROBLEMS" >&2
    STATUS=1
fi
exit "$STATUS"
