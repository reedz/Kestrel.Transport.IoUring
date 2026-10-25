#!/bin/bash
# Builds the private PTS profile bundle from a COMMITTED revision of this repository.
#
#   phoronix/make-bundle.sh [revision]        # default HEAD; writes phoronix/dist/
#
# Output (phoronix/dist/):
#   aspnet-kestrel-iouring-src-<commit>.tar.gz   the source bundle (git archive; deterministic for a given commit)
#   test-profiles/local/aspnet-kestrel-iouring/  the profile: install.sh and downloads.xml generated from the
#                                                templates with this bundle's name, SHA-256 and size
#   user-config.xml                              the no-upload PTS configuration, from the same commit
#   SOURCE_COMMIT                                the exact commit that was bundled
#
# Everything (source, profile, templates, configuration) is read from the selected commit, never from the working
# tree, so a profile can never silently run uncommitted code and an explicit revision can never mix revisions.
# The bundle name carries the commit: PTS keeps a same-named download that already sits in an install directory
# without re-hashing it, so a reused name would silently install stale code.
# Nothing is uploaded or published; this script only writes under phoronix/dist/.

set -euo pipefail

REPO="$(git rev-parse --show-toplevel)"
cd "$REPO"
REV="${1:-HEAD}"
OUT="${OUT:-$REPO/phoronix/dist}"
PROFILE_SRC="phoronix/test-profiles/local/aspnet-kestrel-iouring"

if [ "${ALLOW_DIRTY:-0}" != "1" ] && [ -n "$(git status --porcelain -- . ':!phoronix/dist')" ]; then
    echo "The working tree has uncommitted changes, which git archive would NOT include." >&2
    echo "Commit them first, or set ALLOW_DIRTY=1 to bundle the committed revision anyway." >&2
    exit 1
fi

COMMIT="$(git rev-parse --verify "$REV^{commit}")"
SRC_FILE="aspnet-kestrel-iouring-src-${COMMIT:0:12}.tar.gz"
rm -rf "$OUT"
mkdir -p "$OUT"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
git archive --format=tar "$COMMIT" phoronix | tar -x -C "$STAGE"

git archive --format=tar "$COMMIT" | gzip -n > "$OUT/$SRC_FILE"
SHA256="$(sha256sum "$OUT/$SRC_FILE" | cut -d' ' -f1)"
SIZE="$(stat -c %s "$OUT/$SRC_FILE")"

DEST="$OUT/test-profiles/local/aspnet-kestrel-iouring"
mkdir -p "$DEST"
for f in test-definition.xml results-definition.xml pre.sh post.sh; do
    cp "$STAGE/$PROFILE_SRC/$f" "$DEST/$f"
done
sed -e "s|@SRC_FILE@|$SRC_FILE|g" -e "s|@SRC_SHA256@|$SHA256|g" -e "s|@SOURCE_COMMIT@|$COMMIT|g" \
    "$STAGE/$PROFILE_SRC/install.sh.in" > "$DEST/install.sh"
sed -e "s|@SRC_FILE@|$SRC_FILE|g" -e "s|@SRC_SHA256@|$SHA256|g" -e "s|@SRC_SIZE@|$SIZE|g" \
    "$STAGE/$PROFILE_SRC/downloads.xml.in" > "$DEST/downloads.xml"
chmod +x "$DEST/install.sh" "$DEST/pre.sh" "$DEST/post.sh"
cp "$STAGE/phoronix/config/user-config.xml" "$OUT/user-config.xml"
echo "$COMMIT" > "$OUT/SOURCE_COMMIT"

if grep -rn '@[A-Z_0-9]*@' "$DEST" > /dev/null; then
    echo "Unfilled placeholder left in the generated profile:" >&2
    grep -rn '@[A-Z_0-9]*@' "$DEST" >&2
    exit 1
fi

# Local checks (PTS itself validates the schema on install).
for x in "$DEST/test-definition.xml" "$DEST/results-definition.xml" "$DEST/downloads.xml"; do
    php -r '$d = new DOMDocument(); if (!$d->load($argv[1])) { exit(1); }' "$x" \
        || { echo "Malformed XML: $x" >&2; exit 1; }
done
for s in "$DEST/install.sh" "$DEST/pre.sh" "$DEST/post.sh" "$STAGE"/phoronix/*.sh "$STAGE"/phoronix/runtime/*.sh; do
    bash -n "$s" || { echo "Syntax error in $s" >&2; exit 1; }
done
php -l "$STAGE/phoronix/runtime/check-pts-config.php" > /dev/null || { echo "Syntax error in check-pts-config.php" >&2; exit 1; }
# The shipped PTS configuration must itself satisfy the local-only checks (PHP DOM parse, exact values).
php "$STAGE/phoronix/runtime/check-pts-config.php" --file "$OUT/user-config.xml" > /dev/null \
    || { echo "The shipped user-config.xml is not local-only:" >&2
         php "$STAGE/phoronix/runtime/check-pts-config.php" --file "$OUT/user-config.xml" >&2 || true; exit 1; }

echo "Bundled commit $COMMIT"
echo "  source bundle: $OUT/$SRC_FILE ($SIZE bytes, sha256 $SHA256)"
echo "  profile:       $DEST"
echo "Next: phoronix/fetch-artifacts.sh (once), then phoronix/setup-local-pts.sh"
