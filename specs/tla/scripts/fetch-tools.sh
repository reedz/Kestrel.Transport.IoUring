#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
TLA_DIR="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)"
TOOLS_DIR="$TLA_DIR/tools"

TLA2TOOLS_VERSION="1.7.4"
TLA2TOOLS_URL="https://github.com/tlaplus/tlaplus/releases/download/v1.7.4/tla2tools.jar"
# Source: https://github.com/tlaplus/tlaplus/releases/tag/v1.7.4 publishes the
# matching SHA-1 (bee4a54f3ee3d4afc347c3240ec2d9e93b075104) for the official
# tla2tools.jar asset above. The SHA-256 below was computed from that same asset
# and is pinned here so the scripts can verify the download before use.
TLA2TOOLS_SHA256="936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88"
TLA2TOOLS_JAR="$TOOLS_DIR/tla2tools-${TLA2TOOLS_VERSION}.jar"
PARTIAL_JAR="$TOOLS_DIR/.tla2tools-${TLA2TOOLS_VERSION}.$$.jar.download"

usage() {
  cat <<USAGE
Usage: $(basename "$0")

Downloads and verifies the pinned tla2tools.jar into specs/tla/tools.
Prints the verified jar path on stdout.
USAGE
}

die() {
  echo "fetch-tools.sh: $*" >&2
  exit 1
}

sha256_of() {
  sha256sum "$1" | awk '{print $1}'
}

verify_existing() {
  local actual_sha256

  [[ -f "$TLA2TOOLS_JAR" ]] || return 1

  actual_sha256="$(sha256_of "$TLA2TOOLS_JAR")"
  if [[ "$actual_sha256" != "$TLA2TOOLS_SHA256" ]]; then
    echo "fetch-tools.sh: removing stale $TLA2TOOLS_JAR (expected $TLA2TOOLS_SHA256, got $actual_sha256)" >&2
    rm -f -- "$TLA2TOOLS_JAR"
    return 1
  fi

  return 0
}

download_jar() {
  local actual_sha256

  rm -f -- "$PARTIAL_JAR"
  if ! curl --fail --location --silent --show-error "$TLA2TOOLS_URL" --output "$PARTIAL_JAR"; then
    rm -f -- "$PARTIAL_JAR"
    die "failed to download $TLA2TOOLS_URL. If you are offline, reconnect or place the verified jar at $TLA2TOOLS_JAR."
  fi

  actual_sha256="$(sha256_of "$PARTIAL_JAR")"
  if [[ "$actual_sha256" != "$TLA2TOOLS_SHA256" ]]; then
    rm -f -- "$PARTIAL_JAR"
    die "checksum mismatch for downloaded tla2tools.jar (expected $TLA2TOOLS_SHA256, got $actual_sha256)"
  fi

  mv -- "$PARTIAL_JAR" "$TLA2TOOLS_JAR"
}

main() {
  if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
  fi
  if [[ $# -ne 0 ]]; then
    die "unexpected arguments: $*"
  fi

  command -v curl >/dev/null 2>&1 || die "curl is required"
  command -v sha256sum >/dev/null 2>&1 || die "sha256sum is required"

  mkdir -p -- "$TOOLS_DIR"

  if ! verify_existing; then
    echo "fetch-tools.sh: fetching tla2tools ${TLA2TOOLS_VERSION} from the official GitHub release" >&2
    download_jar
  fi

  printf '%s\n' "$TLA2TOOLS_JAR"
}

main "$@"
