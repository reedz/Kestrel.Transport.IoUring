#!/bin/bash
# Downloads the third-party files the profile needs into PTS's download cache, verifying each against the SHA-256
# and size in downloads.xml.in. This is the ONLY step that needs the network, and it is plain curl to the vendors'
# download hosts: PTS is not involved, so PTS itself can stay configured with NoNetworkCommunication=TRUE for the
# whole install and run (the build is offline too).
#
#   phoronix/fetch-artifacts.sh [DIR]      # default: the PTS download cache of the current user
#
# Files already present with a matching SHA-256 are kept. Run it before phoronix/setup-local-pts.sh --artifacts DIR
# if you want to stage the files on a machine that has network access and install on one that has not.

set -euo pipefail

REPO="$(git rev-parse --show-toplevel)"
. "$REPO/phoronix/runtime/common.sh"

if [ $# -ge 1 ]; then
    DEST="$1"
else
    config_check --file "$REPO/phoronix/config/user-config.xml" > /dev/null || true
    [ -n "$CFG_USER_PATH" ] || { echo "Cannot determine the PTS user directory; pass DIR." >&2; exit 1; }
    DEST="${CFG_USER_PATH}download-cache"
fi
mkdir -p "$DEST"

command -v curl > /dev/null || { echo "curl is required." >&2; exit 1; }

# "<file name> <url> <sha256> <size>" for every package that has a URL (the source bundle has none on purpose).
LIST="$(php -r '
    $d = new DOMDocument(); $d->load($argv[1]);
    foreach ($d->getElementsByTagName("Package") as $p) {
        $g = function ($t) use ($p) { $n = $p->getElementsByTagName($t); return $n->length ? trim($n->item(0)->textContent) : ""; };
        if ($g("URL") !== "") { echo $g("FileName"), " ", $g("URL"), " ", $g("SHA256"), " ", $g("FileSize"), "\n"; }
    }' "$REPO/phoronix/test-profiles/local/aspnet-kestrel-iouring/downloads.xml.in")"

[ -n "$LIST" ] || { echo "No downloadable packages found in downloads.xml.in" >&2; exit 1; }

while read -r NAME URL SHA SIZE; do
    TARGET="$DEST/$NAME"
    if [ -f "$TARGET" ] && echo "$SHA  $TARGET" | sha256sum --check --status; then
        echo "ok      $NAME (already present, SHA-256 verified)"
        continue
    fi
    echo "fetch   $NAME  <-  $URL"
    TMP="$(mktemp "$DEST/.fetch.XXXXXX")"
    if ! curl -fL --retry 3 --proto '=https,http' -o "$TMP" "$URL"; then
        rm -f "$TMP"; echo "Download failed: $URL" >&2; exit 1
    fi
    if [ "$(stat -c %s "$TMP")" != "$SIZE" ] || ! echo "$SHA  $TMP" | sha256sum --check --status; then
        rm -f "$TMP"; echo "$NAME does not match the expected size/SHA-256; discarded." >&2; exit 1
    fi
    mv "$TMP" "$TARGET"
    echo "ok      $NAME (verified)"
done <<< "$LIST"

echo "Artifacts are in $DEST"
