#!/usr/bin/env bash
#
# Removes a previous Jellyfin / Treasure-Maps install on a Raspberry Pi and
# deploys this fork's Jellyfin 12 + the current Treasure-Maps plugin.
#
# Run on the Pi (from the repo, or from anywhere — it will clone if needed):
#   chmod +x scripts/pi/full-redeploy.sh
#   ./scripts/pi/full-redeploy.sh
#
# Options:
#   --yes            Do not ask for confirmation
#   --wipe-data      Also delete the Jellyfin data dir (users, libraries, plugin config)
#   --remove-apt     Purge the Debian jellyfin / jellyfin-server / jellyfin-web packages
#   --remove-only    Stop after removing the old install (do not build/start)
#   --skip-web       Start with --nowebclient (skip building jellyfin-web)
#   --dry-run        Print actions only
#
# Environment:
#   BRANCH     Git branch to deploy          (default: cursor/set-up-dev-environment-0947)
#   REPO_URL   Git remote                    (default: https://github.com/insidia-jonas/jellyfin.git)
#   REPO_DIR   Existing checkout             (default: this repo, else $HOME/jellyfin)
#   OUT_DIR    Install prefix                (default: $HOME/jellyfin12)
#   DATA_DIR   Jellyfin data directory       (default: $OUT_DIR/data)
#   WEB_DIST   Prebuilt jellyfin-web/dist    (skips the webpack build)
#   SKIP_WEB=1 Same as --skip-web
#   SKIP_STOP=1 Do not stop running Jellyfin processes (test/debug only)
#
set -euo pipefail

BRANCH="${BRANCH:-cursor/set-up-dev-environment-0947}"
REPO_URL="${REPO_URL:-https://github.com/insidia-jonas/jellyfin.git}"
OUT_DIR="${OUT_DIR:-$HOME/jellyfin12}"
DATA_DIR="${DATA_DIR:-$OUT_DIR/data}"
SERVER_DIR="$OUT_DIR/server"
PLUGIN_DIR="$DATA_DIR/plugins/Treasure-Maps"
DOTNET_DIR="${DOTNET_DIR:-$HOME/.dotnet}"
SERVICE_NAME="jellyfin12"

ASSUME_YES=0
WIPE_DATA=0
REMOVE_APT=0
REMOVE_ONLY=0
DRY_RUN=0
SKIP_WEB="${SKIP_WEB:-}"

while [ $# -gt 0 ]; do
    case "$1" in
        --yes|-y) ASSUME_YES=1 ;;
        --wipe-data) WIPE_DATA=1 ;;
        --remove-apt) REMOVE_APT=1 ;;
        --remove-only) REMOVE_ONLY=1 ;;
        --skip-web) SKIP_WEB=1 ;;
        --dry-run) DRY_RUN=1 ;;
        -h|--help)
            sed -n '2,27p' "$0"
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            exit 2
            ;;
    esac
    shift
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CANDIDATE_REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"
if [ -z "${REPO_DIR:-}" ]; then
    if [ -f "$CANDIDATE_REPO/Jellyfin.Server/Jellyfin.Server.csproj" ]; then
        REPO_DIR="$CANDIDATE_REPO"
    else
        REPO_DIR="$HOME/jellyfin"
    fi
fi

export PATH="$DOTNET_DIR:$DOTNET_DIR/tools:$PATH"
export DOTNET_ROOT="$DOTNET_DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

log() { printf '\n\033[1;32m== %s ==\033[0m\n' "$*"; }
warn() { printf '\033[1;33m!! %s\033[0m\n' "$*"; }

run_cmd() {
    if [ "$DRY_RUN" -eq 1 ]; then
        printf '+'
        printf ' %q' "$@"
        printf '\n'
        return 0
    fi
    "$@"
}

confirm() {
    if [ "$ASSUME_YES" -eq 1 ] || [ "$DRY_RUN" -eq 1 ]; then
        return 0
    fi
    printf '%s [y/N] ' "$1"
    read -r answer
    case "$answer" in
        y|Y|yes|YES|j|J) return 0 ;;
        *) echo "Aborted."; exit 1 ;;
    esac
}

stop_unit() {
    local unit="$1"
    if systemctl list-unit-files --type=service --no-legend 2>/dev/null | awk '{print $1}' | grep -qx "$unit.service" \
        || systemctl is-active --quiet "$unit" 2>/dev/null; then
        log "Stoppe $unit"
        run_cmd sudo systemctl stop "$unit" || true
        run_cmd sudo systemctl disable "$unit" || true
    fi
}

kill_jellyfin_pids() {
    local pid cmdline
    for pid in $(pgrep -x jellyfin 2>/dev/null || true); do
        warn "Beende jellyfin PID $pid"
        run_cmd kill "$pid" || true
    done
    for pid in $(pgrep -x dotnet 2>/dev/null || true); do
        cmdline="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
        if printf '%s' "$cmdline" | grep -q 'jellyfin.dll'; then
            warn "Beende dotnet jellyfin.dll PID $pid"
            run_cmd kill "$pid" || true
        fi
    done
}

rm_tree() {
    local path="$1"
    if [ ! -e "$path" ]; then
        return 0
    fi
    warn "Lösche $path"
    if [ "$DRY_RUN" -eq 1 ]; then
        printf '+ rm -rf %q\n' "$path"
        return 0
    fi
    rm -rf "$path" 2>/dev/null || sudo rm -rf "$path"
}

# ---------- plan ----------
log "Deploy-Plan"
cat <<EOF
  Branch:      $BRANCH
  Repo:        $REPO_DIR
  Install:     $OUT_DIR
  Daten:       $DATA_DIR
  Plugin:      $PLUGIN_DIR
  Web:         $([ -n "${WEB_DIST:-}" ] && echo "WEB_DIST=$WEB_DIST" || { [ -n "$SKIP_WEB" ] && echo "übersprungen (--nowebclient)" || echo "jellyfin-web bauen"; })
  Wipe data:   $WIPE_DATA
  Remove apt:  $REMOVE_APT
  Dry-run:     $DRY_RUN
EOF

confirm "Alten Stand entfernen und $BRANCH vollständig auf diesem Pi deployen?"

# ---------- 1. stop old ----------
log "1/8  Alten Server stoppen"
if [ -n "${SKIP_STOP:-}" ]; then
    warn "SKIP_STOP gesetzt — lasse laufende Prozesse unangetastet"
else
    stop_unit "$SERVICE_NAME"
    stop_unit jellyfin
    kill_jellyfin_pids
    if [ "$DRY_RUN" -eq 0 ]; then
        sleep 1
        kill_jellyfin_pids
    fi
fi

# ---------- 2. remove old plugin + channel cache ----------
log "2/8  Altes Plugin und Channel-Cache entfernen"
OLD_PLUGIN_DIRS=(
    "$PLUGIN_DIR"
    "$OUT_DIR/data/plugins/Treasure-Maps"
    "$HOME/jellyfin12/data/plugins/Treasure-Maps"
    "$HOME/.local/share/jellyfin/plugins/Treasure-Maps"
    "$HOME/.local/share/jellyfin/plugins/TreasureMaps"
    "/var/lib/jellyfin/plugins/Treasure-Maps"
    "/var/lib/jellyfin/plugins/TreasureMaps"
)
for dir in "${OLD_PLUGIN_DIRS[@]}"; do
    rm_tree "$dir"
done

# Any leftover folder whose name is Treasure-Maps / TreasureMaps under known plugin roots.
while IFS= read -r dir; do
    [ -n "$dir" ] || continue
    rm_tree "$dir"
done < <(find \
    "$DATA_DIR/plugins" \
    "$HOME/jellyfin12/data/plugins" \
    "$HOME/.local/share/jellyfin/plugins" \
    /var/lib/jellyfin/plugins \
    -maxdepth 1 -type d \( -iname '*treasure*map*' \) 2>/dev/null || true)

OLD_CHANNEL_CACHE=(
    "$DATA_DIR/metadata/channels"
    "$OUT_DIR/data/metadata/channels"
    "$HOME/jellyfin12/data/metadata/channels"
    "$HOME/.local/share/jellyfin/metadata/channels"
    "/var/lib/jellyfin/metadata/channels"
)
for dir in "${OLD_CHANNEL_CACHE[@]}"; do
    rm_tree "$dir"
done

# ---------- 3. optional apt + data wipe ----------
if [ "$REMOVE_APT" -eq 1 ]; then
    log "3/8  Debian-Jellyfin-Pakete entfernen"
    run_cmd sudo apt-get remove --purge -y jellyfin jellyfin-server jellyfin-web || true
    run_cmd sudo apt-get autoremove -y || true
elif [ "$WIPE_DATA" -eq 1 ]; then
    log "3/8  Datenordner wird geleert"
else
    log "3/8  Debian-Jellyfin bleibt (nur deaktiviert). Datenordner bleibt (Nutzer, Bibliotheken, API-Keys)."
fi

if [ "$WIPE_DATA" -eq 1 ]; then
    warn "Lösche den kompletten Datenordner (Nutzer, Bibliotheken, Plugin-Config)"
    rm_tree "$DATA_DIR"
    rm_tree "$HOME/.local/share/jellyfin"
    if [ "$REMOVE_APT" -eq 1 ]; then
        rm_tree /var/lib/jellyfin
        rm_tree /etc/jellyfin
        rm_tree /var/cache/jellyfin
    fi
fi

# Always rebuild the published server binaries on a full deploy.
if [ "$REMOVE_ONLY" -eq 0 ]; then
    rm_tree "$SERVER_DIR"
fi

if [ "$REMOVE_ONLY" -eq 1 ]; then
    log "Fertig (--remove-only). Kein Build, kein Start."
    exit 0
fi

# ---------- 4. repo ----------
log "4/8  Repo auf $BRANCH bringen"
if [ ! -f "$REPO_DIR/Jellyfin.Server/Jellyfin.Server.csproj" ]; then
    warn "Kein Checkout unter $REPO_DIR — klone $REPO_URL"
    run_cmd mkdir -p "$(dirname "$REPO_DIR")"
    run_cmd git clone --branch "$BRANCH" "$REPO_URL" "$REPO_DIR"
else
    run_cmd git -C "$REPO_DIR" fetch origin "$BRANCH"
    run_cmd git -C "$REPO_DIR" checkout "$BRANCH"
    run_cmd git -C "$REPO_DIR" reset --hard "origin/$BRANCH"
fi
if [ "$DRY_RUN" -eq 0 ]; then
    git -C "$REPO_DIR" log -1 --oneline
fi

# ---------- 5. toolchain ----------
log "5/8  Abhängigkeiten und .NET 10"
if [ "$DRY_RUN" -eq 0 ]; then
    sudo apt-get update -y
    sudo apt-get install -y --no-install-recommends git curl ca-certificates libfontconfig1 libssl3
    sudo apt-get install -y ffmpeg || true
    if [ ! -x "$DOTNET_DIR/dotnet" ]; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR"
    fi
    "$DOTNET_DIR/dotnet" --version
else
    echo "+ apt-get install git curl ca-certificates libfontconfig1 libssl3 ffmpeg"
    echo "+ dotnet-install --channel 10.0"
fi

# ---------- 6. server + plugin ----------
log "6/8  Jellyfin 12 + Plugin bauen"
run_cmd mkdir -p "$DATA_DIR" "$PLUGIN_DIR"
if [ "$DRY_RUN" -eq 0 ]; then
    (
        cd "$REPO_DIR"
        "$DOTNET_DIR/dotnet" publish Jellyfin.Server -c Release -o "$SERVER_DIR"
        "$DOTNET_DIR/dotnet" build plugins/Jellyfin.Plugin.TreasureMaps -c Release
        cp plugins/Jellyfin.Plugin.TreasureMaps/bin/Release/net10.0/Jellyfin.Plugin.TreasureMaps.dll "$PLUGIN_DIR/"
    )
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
    "timestamp": "2026-08-22T00:00:00Z",
    "version": "1.0.0.0",
    "status": 0,
    "autoUpdate": false,
    "assemblies": [ "Jellyfin.Plugin.TreasureMaps.dll" ]
}
META
else
    echo "+ dotnet publish Jellyfin.Server -c Release -o $SERVER_DIR"
    echo "+ dotnet build plugins/Jellyfin.Plugin.TreasureMaps -c Release"
    echo "+ cp Jellyfin.Plugin.TreasureMaps.dll $PLUGIN_DIR/"
    echo "+ write $PLUGIN_DIR/meta.json"
fi

# ---------- 7. web client ----------
log "7/8  Web-Client"
WEB_ARG="--nowebclient"
if [ -n "${WEB_DIST:-}" ]; then
    WEB_ARG="--webdir ${WEB_DIST}"
elif [ -z "$SKIP_WEB" ]; then
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "+ build jellyfin-web (Node >= 24) -> $OUT_DIR/jellyfin-web/dist"
        WEB_ARG="--webdir $OUT_DIR/jellyfin-web/dist"
    else
        NODE_MAJOR="$(node -v 2>/dev/null | sed 's/v\([0-9]*\).*/\1/' || echo 0)"
        if [ "${NODE_MAJOR:-0}" -lt 24 ]; then
            export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
            if [ ! -s "$NVM_DIR/nvm.sh" ]; then
                curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash
            fi
            # shellcheck disable=SC1091
            . "$NVM_DIR/nvm.sh"
            nvm install 24
            nvm use 24
        fi
        if [ ! -d "$OUT_DIR/jellyfin-web/.git" ]; then
            rm -rf "$OUT_DIR/jellyfin-web"
            git clone --depth 1 https://github.com/jellyfin/jellyfin-web "$OUT_DIR/jellyfin-web"
        fi
        ( cd "$OUT_DIR/jellyfin-web" && git fetch --depth 1 origin master && git reset --hard origin/master && npm ci && npm run build:production )
        WEB_ARG="--webdir $OUT_DIR/jellyfin-web/dist"
    fi
fi

# ---------- 8. systemd + start ----------
log "8/8  Dienst einrichten und starten"
UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
UNIT_BODY="$(cat <<EOF
[Unit]
Description=Jellyfin 12 (Treasure-Maps)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USER
Group=$USER
WorkingDirectory=$SERVER_DIR
Environment=DOTNET_ROOT=$DOTNET_DIR
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=$DOTNET_DIR/dotnet $SERVER_DIR/jellyfin.dll --datadir $DATA_DIR $WEB_ARG
Restart=on-failure
RestartSec=5
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF
)"

if [ "$DRY_RUN" -eq 1 ]; then
    printf '+ write %s\n%s\n' "$UNIT_PATH" "$UNIT_BODY"
    echo "+ sudo systemctl daemon-reload && sudo systemctl enable --now $SERVICE_NAME"
else
    printf '%s\n' "$UNIT_BODY" | sudo tee "$UNIT_PATH" >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable --now "$SERVICE_NAME"
fi

if [ "$DRY_RUN" -eq 0 ]; then
    log "Warte auf http://127.0.0.1:8096"
    ok=0
    for _ in $(seq 1 40); do
        if curl -fsS -o /dev/null --max-time 2 http://127.0.0.1:8096/health 2>/dev/null \
            || curl -fsS -o /dev/null --max-time 2 http://127.0.0.1:8096/System/Info/Public 2>/dev/null \
            || curl -fsS -o /dev/null --max-time 2 http://127.0.0.1:8096/ 2>/dev/null; then
            ok=1
            break
        fi
        sleep 3
    done
    if [ "$ok" -eq 1 ]; then
        log "Jellyfin 12 läuft"
    else
        warn "Kein HTTP auf :8096 nach ~2 Minuten. Logs:"
        sudo journalctl -u "$SERVICE_NAME" -n 80 --no-pager || true
        exit 1
    fi
fi

HOST_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
cat <<EOF

Fertig. Alter Stand ist entfernt, $BRANCH ist deployed.

  Dienst:   systemctl status $SERVICE_NAME
  URL:      http://${HOST_IP:-<pi-ip>}:8096
  Plugin:   Dashboard → Plugins → Treasure-Maps
  Channel:  Bibliothek → Treasure-Maps → Trending

API-Key und SABnzbd bleiben erhalten, außer du hast --wipe-data benutzt.
Fire-TV-App einmal neu öffnen (Channel-Cache wurde geleert).
EOF
