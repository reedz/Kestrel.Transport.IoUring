#!/bin/bash
# Installs the private profile and the no-upload PTS configuration into the CURRENT USER's PTS directory.
# It does not run PTS, does not upload anything and does not publish the profile.
#
#   phoronix/setup-local-pts.sh [--artifacts DIR]     install profile + configuration (run make-bundle.sh first)
#   phoronix/setup-local-pts.sh --verify              only check the PTS configuration PTS will actually read
#
#   --artifacts DIR   directory holding the third-party files named in downloads.xml (SDK tarballs, wrk,
#                     http-test-files), copied into PTS's download cache. fetch-artifacts.sh puts them there too.
#
# PTS is configured with NoNetworkCommunication=TRUE and the profile installs offline, so PTS never needs the network:
# every download must already be in the cache (fetch-artifacts.sh).

set -euo pipefail

REPO="$(git rev-parse --show-toplevel)"
DIST="${DIST:-$REPO/phoronix/dist}"
. "$REPO/phoronix/runtime/common.sh"

verify_config() {
    if config_check; then
        echo "PTS configuration verified ($CFG_FILE): result uploads, usage reporting, log uploads and PTS network access are disabled."
        return 0
    fi
    echo "PTS configuration is NOT local-only:" >&2
    echo "$CFG_PROBLEMS" | sed 's/^/  - /' >&2
    return 1
}

ARTIFACTS=""
while [ $# -gt 0 ]; do
    case "$1" in
        --artifacts) [ $# -ge 2 ] || { echo "--artifacts needs a directory" >&2; exit 2; }; ARTIFACTS="$2"; shift 2 ;;
        --verify) verify_config; exit $? ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

SRC_BUNDLE="$(ls "$DIST"/aspnet-kestrel-iouring-src-*.tar.gz 2> /dev/null | head -1 || true)"
[ -n "$SRC_BUNDLE" ] && [ -f "$DIST/user-config.xml" ] || { echo "Run phoronix/make-bundle.sh first." >&2; exit 1; }
SRC_NAME="$(basename "$SRC_BUNDLE")"

# Where PTS will look, decided the way PTS decides it (this is also where the configuration must go).
config_check > /dev/null || true
[ -n "$CFG_USER_PATH" ] && [ -n "$CFG_FILE" ] || { echo "Cannot determine the PTS user directory." >&2; exit 1; }
case "$CFG_FILE" in
    /etc/*) echo "PTS would read $CFG_FILE (privileged install); run this as a normal user." >&2; exit 1 ;;
esac

mkdir -p "$CFG_USER_PATH/test-profiles/local" "$CFG_USER_PATH/download-cache"

rm -rf "$CFG_USER_PATH/test-profiles/local/aspnet-kestrel-iouring"
cp -r "$DIST/test-profiles/local/aspnet-kestrel-iouring" "$CFG_USER_PATH/test-profiles/local/"

# Only the bundle of this commit stays in the cache: PTS keeps an already-present download without re-hashing it.
for old in "$CFG_USER_PATH"/download-cache/aspnet-kestrel-iouring-src*.tar.gz; do
    [ -e "$old" ] && [ "$(basename "$old")" != "$SRC_NAME" ] && rm -f "$old"
done
cp "$SRC_BUNDLE" "$CFG_USER_PATH/download-cache/"

if [ -n "$ARTIFACTS" ]; then
    for f in dotnet-sdk-10.0.402-linux-x64.tar.gz dotnet-sdk-11.0.100-rc.2.26504.105-linux-x64.tar.gz \
             wrk-4.2.0.tar.gz http-test-files-1.tar.xz; do
        [ -f "$ARTIFACTS/$f" ] && cp "$ARTIFACTS/$f" "$CFG_USER_PATH/download-cache/"
    done
fi

if [ -f "$CFG_FILE" ]; then
    cp "$CFG_FILE" "$CFG_FILE.bak.$(date +%Y%m%d%H%M%S)"
fi
cp "$DIST/user-config.xml" "$CFG_FILE"
verify_config

MISSING=""
for f in dotnet-sdk-10.0.402-linux-x64.tar.gz dotnet-sdk-11.0.100-rc.2.26504.105-linux-x64.tar.gz \
         wrk-4.2.0.tar.gz http-test-files-1.tar.xz; do
    [ -f "$CFG_USER_PATH/download-cache/$f" ] || MISSING="$MISSING $f"
done
if [ -n "$MISSING" ]; then
    echo
    echo "Missing from the PTS download cache:$MISSING"
    echo "Run phoronix/fetch-artifacts.sh (needs network, uses curl only) and then this script again."
fi

cat <<EOF

Profile installed as local/aspnet-kestrel-iouring (source commit $(cat "$DIST/SOURCE_COMMIT")).

Next (needs PTS and php; no network):
  phoronix-test-suite force-install local/aspnet-kestrel-iouring
  phoronix/run-local.sh --duration 10 --runs 1 --connections 100 --rings 1       # smoke test

See phoronix/README.md for the full matrix and for the no-egress mode.
EOF
