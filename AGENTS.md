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
