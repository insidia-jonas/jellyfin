#!/usr/bin/env bash
# Compatibility entry point. State is preserved; master is the default branch.
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
args=(--apply)
for arg in "$@"; do
    case "$arg" in
        --yes|-y) ;;
        --dry-run) args+=(--plan) ;;
        --wipe-data|--remove-apt|--remove-only|--skip-web)
            echo "Option $arg is no longer supported by the preserving installer." >&2; exit 2 ;;
        *) args+=("$arg") ;;
    esac
done
if [ -n "${BRANCH:-}" ]; then args+=(--branch "$BRANCH"); fi
if [ -n "${OUT_DIR:-}" ]; then args+=(--out "$OUT_DIR"); fi
if [ -n "${WEB_DIST:-}" ]; then args+=(--web-dist "$WEB_DIST"); fi
exec sudo python3 "$script_dir/manage-stack.py" "${args[@]}"
