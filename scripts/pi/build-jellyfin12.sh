#!/usr/bin/env bash
#
# Builds this repo's Jellyfin 12 server (.NET 10) + the Treasure-Maps plugin on a
# Raspberry Pi (aarch64 / Debian 12) and prepares it to run.
#
# Usage (run from the repo root on the Pi):
#   chmod +x scripts/pi/build-jellyfin12.sh
#   ./scripts/pi/build-jellyfin12.sh
#
# To replace an older install (stop services, delete the old plugin/channel cache,
# rebuild, install the systemd unit and start), use scripts/pi/full-redeploy.sh
# instead.
#
# Options (environment variables):
#   OUT_DIR    Output directory              (default: $HOME/jellyfin12)
#   DATA_DIR   Jellyfin data directory       (default: $OUT_DIR/data)
#   WEB_DIST   Path to a prebuilt jellyfin-web 'dist' (skips the heavy web build)
#   SKIP_WEB=1 Run headless (--nowebclient) and skip building the web client
#
# The web client build (webpack) needs Node >= 24 and a few GB of RAM. On a small
# Pi, prefer WEB_DIST=<prebuilt dist> to avoid it.
set -euo pipefail

REPO_DIR="${REPO_DIR:-$(pwd)}"
OUT_DIR="${OUT_DIR:-$HOME/jellyfin12}"
DATA_DIR="${DATA_DIR:-$OUT_DIR/data}"
SERVER_DIR="$OUT_DIR/server"
PLUGIN_DIR="$DATA_DIR/plugins/Treasure-Maps"
DOTNET_DIR="$HOME/.dotnet"

export PATH="$DOTNET_DIR:$DOTNET_DIR/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

log() { printf '\n\033[1;32m== %s ==\033[0m\n' "$*"; }

log "1/6  System dependencies"
sudo apt-get update -y
sudo apt-get install -y --no-install-recommends git curl ca-certificates libfontconfig1 libssl3
# ffmpeg: the system package is auto-detected by Jellyfin. jellyfin-ffmpeg is optional but nicer.
sudo apt-get install -y ffmpeg || true

log "2/6  .NET 10 SDK"
if [ ! -x "$DOTNET_DIR/dotnet" ]; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR"
fi
dotnet --version

log "3/6  Publish the Jellyfin 12 server"
cd "$REPO_DIR"
rm -rf "$SERVER_DIR"
# Framework-dependent publish (runs via 'dotnet jellyfin.dll'); native deps resolve for arm64 at runtime.
dotnet publish Jellyfin.Server -c Release -o "$SERVER_DIR"

log "4/6  Build and install the Treasure-Maps plugin"
dotnet build plugins/Jellyfin.Plugin.TreasureMaps -c Release
mkdir -p "$PLUGIN_DIR"
cp plugins/Jellyfin.Plugin.TreasureMaps/bin/Release/net10.0/Jellyfin.Plugin.TreasureMaps.dll "$PLUGIN_DIR/"
cat > "$PLUGIN_DIR/meta.json" <<'META'
{
    "category": "General",
    "changelog": "Treasure-Maps indexer integration.",
    "description": "Browse a Treasure-Maps indexer inside Jellyfin, grab into SABnzbd, language/xREL/release-name parsing, OpenSubtitles.",
    "guid": "e2c9a6f4-8b1d-4f3a-9c2e-7a5b6d4c3e21",
    "name": "Treasure-Maps",
    "overview": "Treasure-Maps indexer integration",
    "owner": "self",
    "targetAbi": "12.0.0.0",
    "timestamp": "2026-08-20T00:00:00Z",
    "version": "1.0.0.0",
    "status": 0,
    "autoUpdate": false,
    "assemblies": [ "Jellyfin.Plugin.TreasureMaps.dll" ]
}
META

log "5/6  Web client"
WEB_ARG="--nowebclient"
if [ -n "${WEB_DIST:-}" ]; then
    WEB_ARG="--webdir ${WEB_DIST}"
elif [ -z "${SKIP_WEB:-}" ]; then
    # Build jellyfin-web (needs Node >= 24). Uses nvm if the system Node is too old.
    NODE_MAJOR="$(node -v 2>/dev/null | sed 's/v\([0-9]*\).*/\1/' || echo 0)"
    if [ "${NODE_MAJOR:-0}" -lt 24 ]; then
        export NVM_DIR="$HOME/.nvm"
        if [ ! -s "$NVM_DIR/nvm.sh" ]; then
            curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash
        fi
        # shellcheck disable=SC1091
        . "$NVM_DIR/nvm.sh"
        nvm install 24
        nvm use 24
    fi
    rm -rf "$OUT_DIR/jellyfin-web"
    git clone --depth 1 https://github.com/jellyfin/jellyfin-web "$OUT_DIR/jellyfin-web"
    ( cd "$OUT_DIR/jellyfin-web" && npm ci && npm run build:production )
    WEB_ARG="--webdir $OUT_DIR/jellyfin-web/dist"
fi

log "6/6  Done"
mkdir -p "$DATA_DIR"
RUN_CMD="$DOTNET_DIR/dotnet \"$SERVER_DIR/jellyfin.dll\" --datadir \"$DATA_DIR\" $WEB_ARG"
cat <<EOF

Jellyfin 12 + Treasure-Maps plugin are built.

Start it with:
  $RUN_CMD

Then open http://<pi>:8096 , finish the setup wizard, and add libraries.
The plugin appears under Dashboard -> Plugins -> Treasure-Maps.

To run it as a service, create /etc/systemd/system/jellyfin12.service:

  [Unit]
  Description=Jellyfin 12 (Treasure-Maps)
  After=network-online.target

  [Service]
  User=$USER
  ExecStart=$DOTNET_DIR/dotnet $SERVER_DIR/jellyfin.dll --datadir $DATA_DIR $WEB_ARG
  Restart=on-failure

  [Install]
  WantedBy=multi-user.target

Then: sudo systemctl daemon-reload && sudo systemctl enable --now jellyfin12
EOF
