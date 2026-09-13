#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${JF_OUT:-$HOME/jf-iptv}"
WEBDIR="${JF_WEBDIR:-/usr/share/jellyfin/web}"
FFMPEG="${JF_FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}"

if [[ -z "${DOTNET_ROOT:-}" ]]; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet"
  elif command -v dotnet >/dev/null 2>&1; then
    export DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
  fi
fi

if [[ -n "${DOTNET_ROOT:-}" ]]; then
  export PATH="$DOTNET_ROOT:$PATH"
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet nicht gefunden. SDK liegt vermutlich unter $HOME/.dotnet" >&2
  exit 1
fi

echo "DOTNET_ROOT=${DOTNET_ROOT:-}"
dotnet publish "$ROOT/Jellyfin.Server" -c Release -o "$OUT"
sudo systemctl stop jellyfin || true
exec "$OUT/jellyfin" --webdir "$WEBDIR" --ffmpeg "$FFMPEG"
