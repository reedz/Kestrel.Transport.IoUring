#!/bin/bash
# Installs the private profile and a no-upload PTS configuration into the CURRENT USER's
# ~/.phoronix-test-suite. It does not run PTS, does not upload anything and does not publish the profile.
#
#   phoronix/setup-local-pts.sh [--artifacts DIR] [--no-config]
#
#   --artifacts DIR   directory holding the pre-downloaded third-party files named in downloads.xml
#                     (SDK tarballs, wrk, http-test-files). Copied into PTS's download cache so the install
#                     does not re-download them. Optional: PTS downloads anything missing.
#   --no-config       leave ~/.phoronix-test-suite/user-config.xml alone (then YOU must make sure uploads are off)
#   --offline         set NoNetworkCommunication=TRUE in the installed config (use after `install`, before measuring)
#   --verify          only check that the PTS configuration PTS will read cannot upload results or usage data
#                     (run it again after the first PTS command: PTS rewrites the file and falls back to
#                     upload-enabled defaults if it cannot parse it)
#
# Run phoronix/make-bundle.sh first.

set -euo pipefail

REPO="$(git rev-parse --show-toplevel)"
. "$REPO/phoronix/runtime/common.sh"

verify_config() {
    local problems
    problems="$(config_upload_problems)"
    if [ -n "$problems" ]; then
        echo "PTS configuration is NOT local-only:" >&2
        echo "$problems" >&2
        return 1
    fi
    echo "PTS configuration verified: result uploads, usage reporting and log uploads are disabled."
}
DIST="${DIST:-$REPO/phoronix/dist}"
PTS_USER="$HOME/.phoronix-test-suite"
ARTIFACTS=""
WRITE_CONFIG=1
OFFLINE=0

while [ $# -gt 0 ]; do
    case "$1" in
        --artifacts) ARTIFACTS="$2"; shift 2 ;;
        --no-config) WRITE_CONFIG=0; shift ;;
        --offline) OFFLINE=1; shift ;;
        --verify) verify_config; exit $? ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [ "$OFFLINE" = "1" ]; then
    [ -f "$PTS_USER/user-config.xml" ] || { echo "No $PTS_USER/user-config.xml; run without --offline first." >&2; exit 1; }
    sed -i 's#<NoNetworkCommunication>FALSE</NoNetworkCommunication>#<NoNetworkCommunication>TRUE</NoNetworkCommunication>#' "$PTS_USER/user-config.xml"
    echo "NoNetworkCommunication=TRUE in $PTS_USER/user-config.xml"
    verify_config
    exit $?
fi

[ -f "$DIST/aspnet-kestrel-iouring-src.tar.gz" ] || { echo "Run phoronix/make-bundle.sh first." >&2; exit 1; }

mkdir -p "$PTS_USER/test-profiles/local" "$PTS_USER/download-cache"

rm -rf "$PTS_USER/test-profiles/local/aspnet-kestrel-iouring"
cp -r "$DIST/test-profiles/local/aspnet-kestrel-iouring" "$PTS_USER/test-profiles/local/"
cp "$DIST/aspnet-kestrel-iouring-src.tar.gz" "$PTS_USER/download-cache/"
if [ -n "$ARTIFACTS" ]; then
    for f in dotnet-sdk-10.0.402-linux-x64.tar.gz dotnet-sdk-11.0.100-rc.2.26504.105-linux-x64.tar.gz \
             wrk-4.2.0.tar.gz http-test-files-1.tar.xz; do
        [ -f "$ARTIFACTS/$f" ] && cp "$ARTIFACTS/$f" "$PTS_USER/download-cache/"
    done
fi

if [ "$WRITE_CONFIG" = "1" ]; then
    if [ -f "$PTS_USER/user-config.xml" ]; then
        cp "$PTS_USER/user-config.xml" "$PTS_USER/user-config.xml.bak.$(date +%Y%m%d%H%M%S)"
    fi
    cp "$REPO/phoronix/config/user-config.xml" "$PTS_USER/user-config.xml"
    echo "Wrote $PTS_USER/user-config.xml (uploads, usage reporting and prompts disabled)."
    verify_config
fi

cat <<EOF

Profile installed as local/aspnet-kestrel-iouring (source commit $(cat "$DIST/SOURCE_COMMIT")).

Next (PTS must already be on your PATH or use its full path):
  phoronix-test-suite install local/aspnet-kestrel-iouring
  # short smoke run: one repetition, 10 s per measurement, one configuration
  PTS_DURATION=10 FORCE_TIMES_TO_RUN=1 \\
  PRESET_OPTIONS='aspnet-kestrel-iouring.transport=io_uring;aspnet-kestrel-iouring.workload=Plaintext;aspnet-kestrel-iouring.connections=100;aspnet-kestrel-iouring.runtime=.NET 10;aspnet-kestrel-iouring.rings=1' \\
  TEST_RESULTS_NAME=kestrel-smoke TEST_RESULTS_IDENTIFIER=smoke \\
  phoronix-test-suite batch-benchmark local/aspnet-kestrel-iouring

See phoronix/README.md for the full matrix and for keeping the run strictly local.
EOF
