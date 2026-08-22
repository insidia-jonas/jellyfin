# AGENTS.md

## Cursor Cloud specific instructions

This repository is the **Jellyfin Server** backend — a .NET 10 (`net10.0`) ASP.NET Core
media server. The web UI lives in a separate repo (`jellyfin-web`) and is **not** part of
this codebase, so run the server with `--nowebclient` and exercise it through its REST API.

### Toolchain / environment

- The .NET 10 SDK is installed at `~/.dotnet` (not on the base image). The startup update
  script installs it idempotently and runs `dotnet tool restore` + `dotnet restore Jellyfin.sln`.
- `~/.bashrc` puts `~/.dotnet` and `~/.dotnet/tools` on `PATH` and sets `DOTNET_ROOT`.
  Interactive shells therefore have `dotnet` available. In a non-login shell where `PATH`
  is not set up, invoke the SDK explicitly as `~/.dotnet/dotnet ...`.
- `dotnet-ef` (EF Core CLI, pinned in `.config/dotnet-tools.json`) is available via
  `dotnet tool restore` → `dotnet ef`.
- `ffmpeg` is the system build (`/usr/bin/ffmpeg`, v6.1.1) and is auto-detected by the
  server. The upstream `.devcontainer/install-ffmpeg.sh` installs `jellyfin-ffmpeg` instead;
  that is optional and only needed for transcoding parity with production. The `ffmpeg:
  libncursesw... no version information` lines in the log are harmless.

### Build / lint / test / run (standard commands)

- Build: `dotnet build Jellyfin.Server/Jellyfin.Server.csproj` (or `dotnet build` for the
  whole solution). Note `Directory.Build.props` sets `TreatWarningsAsErrors=true` and enables
  StyleCop/custom analyzers in Debug, so analyzer/style violations fail the build.
- Lint/format: `dotnet format --verify-no-changes --verbosity minimal` (this is exactly what
  `.github/workflows/ci-format.yml` runs). A benign "Warnings were encountered while loading
  the workspace" line is expected.
- Test: `dotnet test tests/<Project>/<Project>.csproj` for a single suite, or `dotnet test`
  for all suites under `tests/`. Suites are xUnit.
- Run (dev): `dotnet run --project Jellyfin.Server --nowebclient`. Kestrel listens on
  `http://0.0.0.0:8096`. Swagger/OpenAPI is at `/api-docs/swagger/index.html`.
- Server state lives outside the repo: config/data in `~/.local/share/jellyfin`, cache in
  `~/.cache/jellyfin`. Delete those directories to reset to a fresh (pre-setup-wizard) server.

### API / auth gotchas (useful for smoke-testing)

- A brand-new server reports `StartupWizardCompleted: false`. Complete the wizard via
  `POST /Startup/Configuration`, `POST /Startup/User` (creates the first admin), then
  `POST /Startup/Complete`. Startup endpoints accept an `Authorization: MediaBrowser Client=...,
  Device=..., DeviceId=..., Version=...` header.
- Log in with `POST /Users/AuthenticateByName` body `{"Username":"...","Pw":"..."}` to get an
  `AccessToken`.
- Authenticated requests must send the token as `Authorization: MediaBrowser Token="<token>"`
  — the token value **must be wrapped in double quotes** or the request is treated as
  anonymous (endpoints silently return empty/401). Alternatively use `X-Emby-Token: <token>`.

### Treasure-Maps plugin (`plugins/Jellyfin.Plugin.TreasureMaps`)

A standalone, installable plugin that surfaces a Treasure-Maps (Newznab-style) indexer as a
browsable Jellyfin channel, with a config page (`/web` → Dashboard → Plugins → Treasure-Maps),
a `TreasureMaps/Test` + `TreasureMaps/Releases/{guid}/Grab` API, and unit tests. Non-obvious notes:

- It is deliberately **not** part of `Jellyfin.sln`. It has its own `Directory.Build.props`
  and `Directory.Packages.props` that opt out of the repo-wide analyzers/warnings-as-errors and
  central package management, so it builds like a normal external Jellyfin plugin. Build it
  directly: `dotnet build -c Release plugins/Jellyfin.Plugin.TreasureMaps`. Test it:
  `dotnet test plugins/Jellyfin.Plugin.TreasureMaps.Tests`.
- To run it in the dev server, copy `bin/Release/net10.0/Jellyfin.Plugin.TreasureMaps.dll`
  (only that DLL) into `~/.local/share/jellyfin/plugins/Treasure-Maps/` next to a `meta.json`
  (`assemblies` restricts loading to that DLL; `targetAbi` must be `12.0.0.0`), then restart.
- Channel items are cached by the channel's `DataVersion`. After changing item mapping (e.g.
  `ChannelItemType`) you must **bump `DataVersion`** (in `TreasureMapsChannel`) or Jellyfin will
  keep serving stale cached items/images. Clearing `~/.local/share/jellyfin/metadata/channels`
  also forces an image refresh.
- Indexer releases are not directly streamable (they resolve to NZBs), so pressing "Play" on a
  channel item is expected to fail; the plugin is for discovery + the `Grab` action. `Grab`
  pushes the NZB straight into **SABnzbd** (`TreasureMaps/Releases/{guid}/Grab`, configured via
  the SABnzbd URL/API key on the config page) and falls back to writing the NZB into the drop
  folder when SABnzbd is not configured.
- The channel mirrors the provider homepage: root folders `Trending` / `Movies` / `TV Shows` /
  `Browse by genre`, plus `ISupportsLatestMedia` which adds a "Recently Added in Treasure-Maps"
  poster row on the Jellyfin **Home** screen.
- GOTCHA: Jellyfin keys channel items by their `ChannelItemInfo.Id` (external id). If the same
  release appears in multiple folders (e.g. Trending + Movies + the Latest/home row) with the
  **same** id, Jellyfin reparents the shared item and the other folders appear empty after you
  open one of them. The plugin therefore prefixes item ids with a per-folder scope
  (`"movies|<guid>"`, `"latest|<guid>"`, …). Keep leaf item ids unique per folder.
- OpenSubtitles: `OpenSubtitlesProvider` implements Jellyfin's `ISubtitleProvider` (registered in
  `PluginServiceRegistrator`), so it appears in each item's native "Subtitles → Search". It matches
  by OSDB **movie-hash** (exact file, computed by `MovieHasher` from the item's `MediaPath`) first,
  then IMDb id / query, ranked by hash-match then download count. Search needs the OpenSubtitles API
  key; downloading needs the account login (username/password). Config + "test" button on the plugin
  config page.
- Pi deployment: `scripts/pi/full-redeploy.sh` is the one-shot for a Raspberry Pi — it stops
  `jellyfin` / `jellyfin12`, deletes every known old Treasure-Maps plugin dir and channel cache,
  checks out `BRANCH` (default `cursor/set-up-dev-environment-0947`), publishes Jellyfin 12,
  installs the plugin + `meta.json`, builds or reuses jellyfin-web, writes
  `/etc/systemd/system/jellyfin12.service`, and starts it. Users/libraries/plugin config in
  `DATA_DIR` are kept unless you pass `--wipe-data`. `--remove-apt` purges the Debian packages.
  `--skip-web` / `WEB_DIST=` avoid the heavy webpack build on a small Pi. `--remove-only` stops
  after cleanup. `--dry-run` prints actions. `SKIP_STOP=1` skips killing running processes
  (for tests). `scripts/pi/build-jellyfin12.sh` is the older
  build-only helper (no uninstall, no systemd). A fresh Jellyfin 12 DB initialises fine from
  zero (an earlier __EFMigrationsHistory crash was a corrupted partial data-dir, not a real
  bug); do not wipe a data-dir partially.
- SABnzbd self-configuration: the config page's "Set up SABnzbd categories" button
  (`POST TreasureMaps/Sabnzbd/Setup`) creates/updates the `movies` and `tv` categories with their
  download folders in SABnzbd via `mode=set_config&section=categories` (`SabnzbdMovieFolder` /
  `SabnzbdTvFolder`). This needs the SABnzbd **full** API key (the NZB-only key can submit downloads
  but not change config).
- Grab from the normal UI: `GrabOnFavoriteService` (an `IHostedService`) subscribes to
  `IUserDataManager.UserDataSaved`; marking a Treasure-Maps channel item as a **favorite** (the ❤ in
  the normal browsing view) triggers a grab to SABnzbd. Channel items are tagged with
  `ProviderIds["TreasureMaps"]` (release guid) + `["TreasureMapsKind"]` (movie/tv) by the mapper so
  the handler can identify them. This exists because Jellyfin channels cannot add custom action
  buttons to items — favouriting is the usable in-place gesture. Toggle: `GrabOnFavorite` (default on).
- Browse & Grab: the plugin ships a **second** dashboard page (`browse.html`, registered in
  `Plugin.GetPages()` as `TreasureMapsBrowse`, linked from the config page) styled like the
  Treasure-Maps website (tabs Trending/Movies/TV, poster grid, curated genre selector, 1-click Grab).
  It calls `GET TreasureMaps/Search?type=trending|movie|tv&q=&genre=` (+ `GET TreasureMaps/Genres`
  for the selector) and each poster's `Grab` button calls `POST TreasureMaps/Releases/{guid}/Grab?type=&name=`.
  The Grab endpoint auto-picks the SABnzbd category by media type (`SabnzbdMovieCategory` /
  `SabnzbdTvCategory`, fallback `SabnzbdCategory`) so movies and series land in their own SABnzbd
  completed folders — point the Jellyfin Movies/Shows libraries at those folders and everything sorts
  itself. NOTE: this dashboard page is **admin-web only**; it is NOT reachable on TV/mobile client apps.
- TV/mobile clients (e.g. Fire TV) see ONLY Libraries, **Channels**, and global Search — never plugin
  dashboard pages. So the Fire-TV-facing surface is the **channel** (`TreasureMapsChannel`): its root
  shows ONLY the category folders `Recently added / Trending / Movies / TV Shows / Movies (DE) /
  TV Shows (DE) / Browse by genre / Find A–Z`. This is deliberate and hard-learned: clients offer
  many sort modes (name, date added, random, release date, ...) and ANY grid that mixes category
  folders with title cards will scatter the folders between the posters under some of them (name
  prefixes only win the name sort, DateCreated pinning only the date sort, random defeats both).
  The recent titles live behind `Recently added`; the home screen keeps its Latest row. Folders
  carry far-future staggered `DateCreated` (2099 minus index minutes) and cards/tiles the real
  `posted_at`, so "Date added" sorts sensibly. GOTCHA: category folders have static external ids,
  so their entities are reused forever — ChannelManager was patched (this fork) to update
  `DateCreated` on reused channel items.
- The indexer goes FULLY down at times (whole site 503 "No healthy origin available") and also
  rate-limits. The API client serves expired cache entries as a fallback when a request fails
  (stale-while-error), so browsing keeps working during outages once caches are warm.
- Category tiles must stay uniform (plain colored tiles): Jellyfin's `FolderImageProvider` used to
  compose folder images from child posters, making half the category tiles look like movie cards.
  Channel-sourced folders are excluded from that provider (server patch), and the category folder
  ids carry a generation prefix (`c2-`, stripped in `GetChannelItems`) that was bumped once to
  discard the poster-baked entities. If category tiles ever show posters again, bump the prefix.
- Top-menu ordering is a per-user preference (`OrderedViews`), not sortable server-wide.
  `POST TreasureMaps/Menu/MoveChannelLast` (config-page button "Move Treasure-Maps to the end of
  the menu") rewrites every user's ordered views so the channel comes after the media libraries.
  NOTE: `IUserManager.UpdateConfigurationAsync` overwrites the whole `UserConfiguration` — always
  carry over all current values (see `BuildUserConfiguration` in the controller). The DE rows mirror the website's language blocks: the indexer's `x100`
  category block is German (`cat=2100` movies, `cat=5100` TV — constants
  `GermanMovieCategories`/`GermanTvCategories` on the API client; `SearchMoviesAsync`/`SearchTvAsync`
  take optional category + offset overrides). The cat filter is loose: a few cross-listed non-DE
  releases can appear in the DE rows (API-side behavior).
- API paging/limits: a single request above ~100 items **504s**; use `limit=100` + `offset` paging
  (`FetchPagesAsync`). Cold single-letter/substring queries can hang until the gateway 504s (~55s),
  so the client uses a 30s timeout and pages are fetched fault-tolerantly (one failed page must not
  blank the view). CRITICAL: if a channel folder returns an *empty* result, Jellyfin **caches it as
  empty for hours** — on total failure the channel must THROW (it does), never return empty.
- The API client keeps a 5-minute in-memory response cache (1h for caps), which is what makes
  channel navigation fast (~30ms cached vs 4-60s cold). Restarting the server clears it; Jellyfin's
  own channel item cache persists across restarts (bump `DataVersion` to bust it).
- Genres: the indexer's caps expose ~5000 raw library tags incl. adult ones ("Adult/porn", "Erotica",
  "Hentai", ...). Both the channel's "Browse by genre" and the Browse page selector only surface the
  curated `CommonGenres` whitelist (20 common genres) — keep it that way.
- Library setup: `POST TreasureMaps/Libraries/Setup` (config-page button "Create media libraries")
  creates/completes the Jellyfin **Movies** / **TV Shows** libraries pointing at the SABnzbd
  movie/TV download folders (absolute folders used as-is; relative ones resolved under SABnzbd's
  `misc.complete_dir`), so finished downloads appear in the top menu. GOTCHA: Jellyfin ignores video
  files with "sample" in the name — don't name test files `...-SAMPLE.mp4` when seeding a library.
- Two-level, website-like layout: a category (Movies/TV/genre/letter/trending) shows **one poster
  card per title** (grouped by tmdb/imdb/normalized-title via `ReleaseGrouper`, id prefix `GRP::`),
  NOT one card per release. Opening a title card re-fetches that title's releases (`q=<title>`,
  filtered to the group key) and lists the **individual releases/qualities** as tiles (id prefix
  `REL::`, capped to `ResultLimit`, sorted language-rank then quality-score). Tile names are
  **language-flag + quality-badge labels** (`ReleaseMapper.LanguageFlags` + `BuildQualityLabel`,
  e.g. `🇬🇧🇩🇪 1080p · BluRay · AVC · German · DL · 21.25 GB · [GROUP]`, falling back to the raw
  scene name when nothing parses); the full scene name stays in the tile overview (`Release: ...`).
  Covers are **unified**: the card's poster URL is base64-encoded into the `GRP::` id and re-applied
  to every release tile, so the card and its releases always show the same artwork.
- Title cards are `ChannelFolderType.BoxSet` so clients open a **details page** (poster, tagline +
  plot, rating, genres, cast, IMDb link) with the releases listed below. This needed three tiny
  **server** patches (this fork): `ChannelFolderType.BoxSet` + ChannelManager mapping,
  `GetClientTypeName` not masking channel BoxSets as `ChannelFolderItem` (else clients route to the
  bare list view), and `ImdbExternalId` supporting BoxSet. GOTCHAS: (1) ChannelManager must NOT
  queue a scraper refresh for channel box sets or the TMDB box-set provider renames them to
  "... Collection" (patched); (2) the indexer returns bare numeric IMDb ids — normalize to `tt...`
  (`ReleaseMapper.NormalizeImdbId`) or the IMDb links break; don't put a TMDB id on the cards (it
  renders a wrong /collection/ link); (3) on web, the poster hover play-overlay of a BoxSet card
  can trigger a harmless "no media source" error (BoxSets are considered playable client-side) —
  the details page itself hides play/shuffle because nothing in the collection is playable.
- Release tiles are `ChannelItemType.Folder` (NOT playable media) on purpose: a playable item shows a
  **Play** button that errors (no stream exists). As folders they have no Play button; opening a
  release tile returns a single "↓ Download – mark as favorite" entry, and the actual download is
  triggered by **favouriting (❤)** the tile or that entry (`GrabOnFavoriteService`, keyed on
  `ProviderIds["TreasureMaps"]` / `["TreasureMapsKind"]`). Favouriting works on folder channel items
  (their `ProviderIds` are persisted). NOTE: the grab does a live NZB download from the indexer, so it
  can take ~10–15s after favouriting before it appears in SABnzbd — don't judge it as failed after only
  a few seconds. If you change item ids/type/naming in the channel, **bump `DataVersion`** or Jellyfin
  reuses the cached entities (this bit us: name changes didn't show until the version was bumped).
- Trending: `Trending` splits into `Movies` / `TV Shows`, each listing the website's five TMDB
  spotlight rows via the undocumented `/spotlight?type=movie|tv&feed=1..5` endpoint (1=popular,
  2=trending today, 3=trending week, 4=top rated, 5=now playing / on air; only titles that have
  releases; discovered by probing — `feed` takes NUMBERS, named keys are rejected). Spotlight items
  only carry an `imdb` id and NO cover, so each item is enriched with a `q=<title>` lookup (matched
  by imdb) to pull the poster/metadata. Cover URLs cannot be synthesized (TV uses an internal id).
  API gotchas: `imdbid`/`tmdbid` filters return 0 (broken) — use `q=<title>`; `/tv` is only
  partially enriched; single-letter `q` is a broad substring search (hence Find A–Z prefix-filters
  client-side). Rapid API calls get rate-limited (degraded/empty JSON), so space out manual probing.
- `Find A–Z` (`SearchByLetterAsync`) is the TV-friendly search: letter folders `0-9,A–Z`, each runs a
  live indexer query (movies + TV, in parallel) and keeps titles whose article-stripped name starts
  with that letter. This is the only in-channel "search" — Jellyfin's channel API has no text-search
  hook (`ISearchableChannel`/`CanSearch` are unused here). Global search additionally finds channel
  items that have already been browsed (they get synced to the DB; ~3h cache).
- Release-name parsing: `ReleaseNameParser` extracts scene attributes from the release/dirname
  (resolution, source BluRay/WEB/CAM/TELESYNC/…, codec, HDR, `DL` dual-language, detected
  languages, group) and — for theatrical rips — the audio source `MIC` (microphone, worse) vs
  `LINE`/`LD` (line/direct audio, better) vs `MD` (mic dubbed). These become item tags; MIC/LINE
  also add an overview note. Parsed languages feed the language matcher, so name-based language
  (e.g. `GERMAN DL`) works even when the API omits `audio_languages`.
- xREL ratings: the config page has an "enable xREL" toggle + base URL (public API, no key). When
  enabled, each release is looked up on xREL by its scene/release name (`/release/info.json?dirname=`)
  and the scene video/audio rating (+ title rating) is added as a tag/overview line and, when the
  indexer has no rating, used as the community rating. `XrelClient` caches lookups in memory.
- Reliable-refresh gotcha: Jellyfin does not refresh tags/overview on **reused** channel items, so
  enrichment (language/xREL) would appear stale after enabling it on already-materialized items. The
  channel therefore folds a hash of the settings-dependent `DataVersion` into each item id, so a
  settings change recreates items fresh. (Rapidly changing settings during a single session can still
  leave orphaned items from earlier generations until the channel fully reconciles.)
- Language preferences: the config page has a primary language + accepted secondary languages +
  a "only these languages" filter. Releases are filtered/ranked by audio language
  (`LanguageMatcher`). Two Jellyfin caveats: (1) channel **folders are sorted by Jellyfin**
  (SortName), so the plugin's primary-first ordering is best-effort; (2) channel items are
  cached, so settings that change the produced items are folded into `DataVersion` to force a
  re-fetch. Rapidly toggling the language filter on/off within one session can briefly show
  stale items (shared ids get removed/re-added across cache generations) until the channel fully
  refreshes.
- "For You" (AI recommendations): the channel's `foryou` folder collects the requesting user's
  history (played movies/episodes via `ILibraryManager` + favorites incl. favorited channel
  BoxSets), builds a prompt (`AiRecommender.BuildPrompt`) and asks the configured LLM provider —
  `grok` (api.x.ai) / `openai` (both OpenAI chat-completions shaped) or `anthropic` (messages API).
  `AiBaseUrl` overrides the endpoint (OpenRouter/local LLMs/mocks — testing uses an
  OpenAI-compatible mock on 127.0.0.1:8092, `/tmp/ai_mock.py`, tmux `ai-mock`). Replies are parsed
  tolerantly (`ParseRecommendations` extracts the JSON array from prose/fences); each rec is
  resolved against the indexer by title (normalized match) and skipped when unavailable; the AI's
  reason is prepended to the card overview ("✨ ..."). Recommendations are cached per user+history
  hash (`ForYouCacheHours`, default 6h) so browsing doesn't burn tokens. `GET TreasureMaps/Ai/Test`
  validates the key with a tiny prompt. Channel query's `UserId` provides per-user personalization.
- FIRE TV / native clients (the user's PRIMARY device): no injected JS/CSS there — everything
  must work via standard APIs. The native download flow is **play-to-download**: the grab entry
  below a release is a playable clip (`grab::<kind>::<guid>::<b64 name>`, type Media/Clip). The
  channel implements `IRequiresMediaInfoCallback`: starting playback kicks off the SABnzbd grab in
  the background (shared `GrabService`, session-deduplicated, also used by favorite + API paths)
  and returns a bundled 6s "Download started ✓" mp4 (embedded resource, extracted to tmp,
  `MediaProtocol.File`). CRITICAL: the `MediaSourceInfo` must declare `MediaStreams` (h264/aac) or
  clients hit the transcode/HLS path and fail with a fatal player error.
- Download status for TV: the channel root has a **Downloads** folder listing the SABnzbd queue
  (progress/speed/ETA in names+overviews) and recent history (✓/✗). Jellyfin caches channel
  folders for 3h, so the channel implements `IHasCacheKey` with a 2-minute time bucket — keeps the
  view fresh while the plugin's in-memory API caches protect the indexer. Folder names on reused
  ids update because ChannelManager updates container-folder names (and this fork also updates
  DateCreated).
- Downloads tiles show the movie POSTER, not text tiles: the cover travels through the
  `REL::`/`grab::` ids into the grab, `GrabService` keeps an in-memory artwork registry
  (nzo id + normalized job name → cover; fed by the play, favorite and API grab paths — the web
  script passes a `poster` param). Registry is per-session: jobs grabbed before a server restart
  fall back to text tiles until they age out of the SABnzbd history.
- Client script injection (web only): `WebScriptInjector` inserts
  `<script plugin="TreasureMaps" defer src="/TreasureMaps/ClientScript">` into the web client's
  `index.html` at startup (marker-guarded, same pattern as Intro Skipper; served anonymously by
  `GET TreasureMaps/ClientScript` from `Web/treasuremaps.js`). Rebuilding jellyfin-web replaces
  index.html — the injection re-applies on the next server start. The script: (1) replaces the
  generic children card row on Treasure-Maps title pages with a "Releases" LIST (row per release,
  flag+badge name, blue Download button calling the Grab endpoint), (2) polls
  `GET TreasureMaps/Downloads/Status` (SABnzbd queue+history: percent/speed/ETA/completed/failed)
  every 3s and shows per-row live status, matching jobs by nzo id (from the grab response) with a
  normalized-name fallback, (3) adds a Download button on release tile pages, (4) hides hover play
  overlays on channel pages.
- Actor photos: channel items add People by NAME only and nothing refreshes them (the
  "Refresh People" task only validates/deletes). `PeopleImageService` queues full metadata+image
  refreshes for imageless persons every 12h (TMDB resolves them by name search);
  `POST TreasureMaps/People/RefreshImages` triggers a sweep on demand. Obscure names without a
  TMDB entry keep the placeholder.
- "Treasure Glass" theme: a macOS-like glassmorphism skin ships as an embedded resource
  (`Theme/glass.css`). `POST TreasureMaps/Theme/Apply` (config-page button) installs it into the
  server's branding Custom CSS inside a `/* TREASURE-GLASS-BEGIN/END */` marker block (existing
  custom CSS outside the block is preserved; `Theme/Remove` strips it). Custom CSS only affects
  WEB clients — native apps (Fire TV, mobile) ignore it. When editing the theme, verify selectors
  against the built jellyfin-web (`.skinHeader`, `.cardBox`, `.defaultCardBackground*`,
  `.actionSheet`, ...) and re-apply via the endpoint; clients need a hard reload.
- Demonstrating the UI requires the separate `jellyfin-web` client (Node >= 24): build its
  `dist` and start the server with `dotnet run --project Jellyfin.Server --webdir <dist>`
  instead of `--nowebclient`.
