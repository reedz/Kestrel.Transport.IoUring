#!/bin/bash
# Builds the private PTS profile bundle from a COMMITTED revision of this repository.
#
#   phoronix/make-bundle.sh [revision]        # default HEAD; writes phoronix/dist/
#
# Output (phoronix/dist/):
#   aspnet-kestrel-iouring-src.tar.gz         the source bundle (git archive; deterministic for a given commit)
#   test-profiles/local/aspnet-kestrel-iouring/   the profile with downloads.xml filled in (SHA-256 + size)
#   SOURCE_COMMIT                             the exact commit that was bundled
#
# Only committed files are bundled (git archive), so a profile can never silently run uncommitted code.
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
rm -rf "$OUT"
mkdir -p "$OUT/test-profiles/local/aspnet-kestrel-iouring"

git archive --format=tar "$COMMIT" | gzip -n > "$OUT/aspnet-kestrel-iouring-src.tar.gz"
SHA256="$(sha256sum "$OUT/aspnet-kestrel-iouring-src.tar.gz" | cut -d' ' -f1)"
SIZE="$(stat -c %s "$OUT/aspnet-kestrel-iouring-src.tar.gz")"

DEST="$OUT/test-profiles/local/aspnet-kestrel-iouring"
for f in test-definition.xml results-definition.xml install.sh pre.sh post.sh; do
    cp "$PROFILE_SRC/$f" "$DEST/$f"
done
sed -e "s/@SRC_SHA256@/$SHA256/" -e "s/@SRC_SIZE@/$SIZE/" "$PROFILE_SRC/downloads.xml.in" > "$DEST/downloads.xml"
chmod +x "$DEST/install.sh" "$DEST/pre.sh" "$DEST/post.sh"
echo "$COMMIT" > "$OUT/SOURCE_COMMIT"

# Well-formedness check of the generated XML (PTS itself validates the schema on install).
for x in test-definition.xml results-definition.xml downloads.xml; do
    php -r '$d = new DOMDocument(); if (!$d->load($argv[1])) { exit(1); }' "$DEST/$x" \
        || { echo "Malformed XML: $DEST/$x" >&2; exit 1; }
done
bash -n "$DEST/install.sh" "$DEST/pre.sh" "$DEST/post.sh" \
    phoronix/runtime/common.sh phoronix/runtime/pre.sh phoronix/runtime/post.sh phoronix/runtime/run-test.sh

echo "Bundled commit $COMMIT"
echo "  source bundle: $OUT/aspnet-kestrel-iouring-src.tar.gz ($SIZE bytes, sha256 $SHA256)"
echo "  profile:       $DEST"
echo "Next: phoronix/setup-local-pts.sh"
