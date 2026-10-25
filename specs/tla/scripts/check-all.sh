#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=specs/tla/scripts/check.sh
source "$SCRIPT_DIR/check.sh"

usage() {
  cat <<USAGE
Usage: $(basename "$0")

Runs every committed specs/tla/configs/*.cfg deterministically.
Expected passes run first, then expected violations.
Honors the same TLA_* environment overrides as check.sh, including TLA_SEED.
USAGE
}

main() {
  local -a committed_cfgs=()
  local -a pass_cfgs=()
  local -a violation_cfgs=()
  local -A committed_lookup=()
  local rel_path
  local cfg_base
  local failures=0
  local total=0

  if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
  fi
  if [[ $# -ne 0 ]]; then
    usage >&2
    exit 1
  fi

  load_config_catalog
  mapfile -t committed_cfgs < <(git -C "$ROOT_DIR" ls-files -- specs/tla/configs | LC_ALL=C sort)
  if (( ${#committed_cfgs[@]} == 0 )); then
    echo "check-all.sh: no committed cfgs found; falling back to the working-tree specs/tla/configs files for this checkout" >&2
    mapfile -t committed_cfgs < <(find "$CONFIG_DIR" -maxdepth 1 -type f -name '*.cfg' -printf 'specs/tla/configs/%f\n' | LC_ALL=C sort)
  fi
  (( ${#committed_cfgs[@]} > 0 )) || die "no configs found under specs/tla/configs"

  for rel_path in "${committed_cfgs[@]}"; do
    cfg_base="${rel_path##*/}"
    committed_lookup["$cfg_base"]=1
    validate_catalog_entry "$cfg_base"
    case "${EXPECTED_OUTCOME[$cfg_base]}" in
      pass) pass_cfgs+=("$rel_path") ;;
      violation) violation_cfgs+=("$rel_path") ;;
      *) die "unsupported expected outcome '${EXPECTED_OUTCOME[$cfg_base]}' for $cfg_base" ;;
    esac
  done

  for cfg_base in "${!EXPECTED_OUTCOME[@]}"; do
    [[ -n "${committed_lookup[$cfg_base]:-}" ]] || die "catalog entry has no committed cfg: $cfg_base"
  done

  echo "== Expected pass configs =="
  for rel_path in "${pass_cfgs[@]}"; do
    total=$((total + 1))
    if "$SCRIPT_DIR/check.sh" "$ROOT_DIR/$rel_path"; then
      :
    else
      failures=$((failures + 1))
    fi
    echo
  done

  echo "== Expected violation configs =="
  for rel_path in "${violation_cfgs[@]}"; do
    total=$((total + 1))
    if "$SCRIPT_DIR/check.sh" "$ROOT_DIR/$rel_path"; then
      :
    else
      failures=$((failures + 1))
    fi
    echo
  done

  echo "Summary: $((total - failures))/$total configs matched their cataloged expectations."
  (( failures == 0 ))
}

main "$@"
