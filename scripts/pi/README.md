# Raspberry Pi media stack

The installer defaults to **master** and preserves users, libraries, plugin settings,
Sonarr/Radarr profiles, download categories and unrelated indexers. It detects actual
configuration and tests APIs; no AI service or paid inference is used for installation.

From this repository on a 64-bit Raspberry Pi with Python 3.11.8+:

```sh
sudo python3 scripts/pi/manage-stack.py --user jonas --plan
sudo python3 scripts/pi/manage-stack.py --user jonas --apply
```

`full-redeploy.sh` and `build-jellyfin12.sh` forward to this installer. Neither wipes
data or hard-resets a checkout. A dirty checkout or diverged branch stops the update.
Provide `--out /path/to/jellyfin12` for a nondefault installation. Existing service
definitions, FFmpeg paths and web builds are reused. For a new installation, provide
`--web-dist /path/to/jellyfin-web/dist`; the heavy web build is deliberately separate.

The server is built into staging while the old server remains available. The bundle
contains SHA-256 hashes and the source commit. Active playback defers deployment.
The installer stops Jellyfin, backs up its database including WAL, configuration and
plugin, switches binaries, then verifies `/health` and the management capability.
Failure restores the previous binaries, database and configuration. Backups remain
under `jellyfin12/backups/stack-*`. A repeated deployment with matching checksums does
not restart the server. This installer targets the `jellyfin12` service; it does not
replace an unrelated distribution installation or import its data automatically.

For a build on another machine:

```sh
dotnet publish Jellyfin.Server -c Release -r linux-arm64 --self-contained false -o /tmp/server
dotnet build plugins/Jellyfin.Plugin.TreasureMaps -c Release
python3 scripts/pi/package-release.py --server /tmp/server --output /tmp/release.zip
# Copy release.zip to the Pi, then:
sudo python3 scripts/pi/manage-stack.py --user jonas --apply --bundle /path/to/release.zip
```

The bundle contains no API keys. Development bundles require explicit `--allow-dirty`.
Committed releases are the default. Keep installation backups private: configurations
contain credentials. Do not put them in source control.

## Connections and service discovery

```sh
sudo python3 scripts/pi/manage-stack.py --user jonas --apply --configure-only
```

Discovery reads `/var/lib/{sonarr,radarr}/config.xml` and common SABnzbd INI paths.
Override with `--sonarr-config`, `--radarr-config`, `--sab-config`. Keys are read locally
and submitted directly to Jellyfin; console output never includes credentials.
Existing plugin keys/remote service URLs win. The script uses an existing verified
Jellyfin administrator session, or `JELLYFIN_API_KEY` supplied through the environment.
On a fresh server, finish Jellyfin's setup/login first and rerun configuration.

The management API reconciles:

* Jellyfin requests → Radarr for movies, Sonarr for series (existing profile/root).
* Treasure Maps → both Arr indexers, preserving their category filters.
* Both Arr applications → SABnzbd, preserving existing download categories.
* Both Arr applications → Jellyfin import/upgrade notifications, using a dedicated API key.
* Arr final media directories → Jellyfin movie/series libraries.
* Treasure Maps direct downloads → SABnzbd movie/TV categories.

Resource tests run before writes, including on already configured connections. Repeating
the operation does not duplicate clients, indexers, notifications or libraries. Choose
ambiguous quality profiles/root folders in the management UI. Existing files are never
relocated. Containers/remote services need appropriate reachable URLs and shared path
mounts; set the Jellyfin callback URL accordingly. Missing/unreachable directories are
reported instead of silently creating an empty library.

`--install-missing` optionally installs absent Radarr/Sonarr from their official stable
download endpoints and SABnzbd from the configured Debian repositories. Existing units
and data prevent duplicate installation. New services still require their own initial
account/provider setup, media roots and quality selection; the script does not invent
Usenet or indexer credentials. Configure Debian's contrib/backports if SABnzbd is not
available. Sources: [Servarr](https://github.com/Servarr/Wiki/blob/master/servarr/servarr-install-script.sh),
[Sonarr](https://github.com/Sonarr/Sonarr/blob/develop/distribution/debian/install.sh),
[SABnzbd](https://sabnzbd.org/wiki/installation/install-debian).

## Unified management

Open Jellyfin Dashboard → **Medienzentrale**, or
`http://<pi>:8096/web/#/configurationpage?name=TreasureMapsManagement`.
The responsive interface contains service health, separate download queues, pause/resume,
library refresh, connection tests and configuration. Existing service interfaces remain
one click away. All management endpoints require Jellyfin administrator privileges.
Stored API keys never appear in settings responses or the page.

Search supports `film: Matrix 1999`, `serie: The Bear S02E03`, `"Matrix"` and IMDb IDs.
Complete-title matches suppress unrelated word matches. Completed direct downloads are
polled every five seconds and discovered with targeted folder scans. Metadata providers
and Arr postprocessing can add time; a global scan is no longer started for each job.
AI subtitles support native Grok speech recognition with timed cues and optional
translation. Keys remain scoped to their provider; search/quotes never generate bills.

Validation: `python3 -m unittest discover -s scripts/pi -p 'test_*.py'`, plugin tests,
Live TV tests, `npm test --prefix FireTV/tests/web`, Fire TV core tests and Android lint.
