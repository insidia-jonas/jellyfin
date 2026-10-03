#!/usr/bin/env bash
# Historical entry point now uses the same transactional installer.
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$script_dir/full-redeploy.sh" "$@"
