# Changelog

All notable changes to F1SimHubLive and the companion `F1RaceSim_GSIFPEV2` dashboard.

Format follows [Keep a Changelog](https://keepachangelog.com/). Dates in `YYYY-MM-DD`.

## [Unreleased]

### Fixed
- **Wheel title now updates immediately when switching drivers via the picker in F1 Live SignalR mode** (regression introduced by the picker work). Picker click clears the dashboard's `DriverLastName` (and TLA, team, colour, etc.) to empty so the old driver's identity doesn't linger, then waits for the SignalR client to re-resolve the new driver from the next `DriverList` feed event. Problem: in live mode, `DriverList` deltas only fire when a specific driver's lap/pit status changes, and during a quiet practice session those deltas can be minutes apart (and only contain the changed driver, not the one we just switched to). Result: the dashboard's wheel-area title formula (`$prop('DriverLastName') ?? 'F1 LIVE'`) falls back to "F1 LIVE" until a delta happens to include the new driver. `F1SignalRClient` now caches the initial full `DriverList` snapshot from the Subscribe response (identified as "any snapshot that grows `TotalDrivers`" — deltas have count=1 and are skipped) and re-runs the identity lookup from cache immediately inside `SetDriverNumber`. New driver's name + team colour appear on the wheel within ~250 ms of the picker click instead of "whenever the next delta happens." MV mode is unaffected (the MV HTTP poller already returns a full snapshot every cycle).
- **Track Status (VSC, SC, Yellow, Red) now updates in F1 Live SignalR mode.** The live source subscribed to the `TrackStatus` topic but `OnFeed` only parsed `CarData` and `DriverList` — every other topic was silently dropped, so the dashboard's track-status indicator stayed blank/AllClear regardless of what was happening on track. `F1SignalRClient` now caches `TrackStatusCode` + `TrackStatusMessage`, parses both the initial Subscribe response and subsequent feed updates, and emits a `SessionSnapshot` so the existing plugin → SimHub property → dashboard pipeline lights up the right state. Parse failures (code 0) deliberately do not clobber a valid VSC/SC/Red state. Bonus: `TotalDrivers` is now also populated in live mode (was always 0), so the "P 14/22" position display works. DriverList deltas only ever raise the count, never lower it.
- **Picker keeps teammates paired during practice and qualifying sessions.** MultiViewer's `ChampionshipPrediction` endpoint only returns populated `Teams` data once a race weekend reaches a points-bearing session — during a Friday FP1/FP2/FP3 or a Saturday quali, the endpoint typically returns an empty `Teams` dictionary. The picker's sort then collapsed to race-number order (every driver got `TeamPosition = int.MaxValue`, every team had `0` points, and the race-number tiebreak became dominant), scattering teammates across the list. Sort now includes `TeamName` as a secondary key — a no-op when standings exist (teammates already share the same `TeamPosition`), but a guaranteed teammate-grouping fallback when they don't.
- **Picker now orders teams by Constructors' Championship position during practice and qualifying sessions, not just races.** Even with the teammate-pairing fix above, teams were ordered alphabetically (Alpine, Aston Martin, Audi, …) during a Friday FP2 because MultiViewer's `ChampionshipPrediction` only populates `Teams` data once a session is points-bearing. New `JolpicaStandingsClient` pulls season-to-date Constructors' and Drivers' standings from the Jolpica/Ergast public API (`api.jolpi.ca`) as the **primary** standings source — always reflects post-last-race totals, works during FP1 / FP2 / FP3 / quali / Sprint / Sprint Quali, and is the right answer for "Antonelli is leading the championship, put Mercedes at top during Monaco FP2." Local `ChampionshipPrediction` becomes the offline fallback (when there's no internet but MV is running and a race is in progress). Alphabetical fall-through still anchors the last resort. Fetches are coalesced into a 1-hour in-memory cache so the 5-second picker poll doesn't hammer the API — standings only change on race-result publication anyway. Team-name aliasing layer (`TeamNameAliaser`) reconciles the long sponsor-prefixed names MV reports ("Red Bull Racing", "Aston Martin Aramco", "Kick Sauber") with the short forms Jolpica returns ("Red Bull", "Aston Martin", "Sauber"); covers the 2026 grid including the Sauber→Audi rebrand and Cadillac's debut.

### Changed
- Swapped hero and layout screenshot assignments: the data-rich mid-race shot (`docs/screenshots/GSIFPEV2-2.png`) is now the README/DASHBOARD hero, and the cleaner full-grid shot (`docs/screenshots/GSIFPEV2.png`) anchors the README layout section. Bigger visual impact at the top of the docs.
- Documentation refresh for v1.1.0: README Configure section now lists `AutoLaunchPicker` and flags `DriverNumber` as the only hot-reloadable key; File layout includes the new `picker/` tree, `scripts/install-picker.ps1`, and Start Menu shortcut path; Troubleshooting gains four picker-specific entries (no drivers, race-number sort fallback, click-not-flipping, UAC pain); Driver Picker section replaces the missing PNG reference with an ASCII layout diagram so the doc still reads without an asset on disk. SIGNING.md gains a "Signing both binaries" section that covers picker-before-installer build ordering, same-account zero-incremental-cost billing, and the ~14-line CI workflow patch.
- Installer wizard now hints under the driver dropdown that the choice is reversible at runtime via the Driver Picker — reduces first-install decision anxiety for new users.
- `scripts/install-picker.ps1` always re-publishes the picker on each run (previous behaviour skipped publish if any picker exe already existed, which silently shipped stale binaries during iteration). Also fixed a `$env:ProgramFiles(x86)` interpolation bug that resolved to `C:\Program Files(x86)\SimHub` (no space) and broke the auto-detect on default installs.

### Added
- Picker now has a proper multi-resolution app icon (`picker/Assets/picker.ico` — 16/24/32/48/64/128/256 px). Renders in Explorer, taskbar, Window title bar, and Start Menu shortcut.

## [1.1.0] — 2026-05-26

### Added
- **Live driver hot-reload.** The plugin now watches `settings.json` and applies a `DriverNumber` change in-flight — no SimHub restart, no MV warm-up wait. Other settings are intentionally left frozen mid-session (URLs, polling intervals); only the driver number is hot-swapped. On a change the plugin resets `_lastEmittedUtc` and re-emits `DriverInfo` so the dashboard immediately repaints with the new driver's name, TLA, team colour, and racing number. Top-speed high-water mark is per-driver. Debounced FileSystemWatcher (250ms) to absorb Windows's double-fire on save.
- **F1SimHubLive Driver Picker** — standalone WPF app (`picker/F1SimHubLive.Picker.csproj`) for mid-race driver switching. Big team-coloured TLA tiles, current driver highlighted, always-on-top by default. One click on a driver writes the new `DriverNumber` to `settings.json` and the plugin picks it up within ~1 second. Driver list is fetched live from MultiViewer (`/api/v1/live-timing/DriverList`) every 5 seconds, with a bundled-fallback grid for offline use.
- **Championship-order sort in the picker.** Drivers are paired by team and the teams are ordered by current Constructors' Championship position (pulled from MultiViewer's `/api/v1/live-timing/ChampionshipPrediction`). Within a team, the leading driver by points is shown first. The current points tally for each driver is shown subtly under the racing number. Graceful fallback to race-number order when standings are unavailable (qualifying-only sessions, MV offline, season-opening race).
- **Picker integrated into the installer.** `F1SimHubLive-Installer.exe` now chain-publishes the picker, embeds it as a resource, copies it next to the plugin in the SimHub install directory on deploy, and creates an All-Users Start Menu shortcut (`F1SimHubLive\F1SimHubLive Driver Picker`). New `AutoLaunchPicker` setting (default `false`) lets the plugin spawn the picker automatically when SimHub starts; left off by default to avoid a UAC prompt on every SimHub launch.
- **`scripts/install-picker.ps1`** — helper script for local deploys without a full installer rebuild. Auto-builds the picker if not yet published, copies the exe to the SimHub install dir, and creates the Start Menu shortcut. Must be run elevated.

### Changed
- Bumped plugin / installer / picker versions to `1.1.0`.

## [1.0.3] — 2026-05-25

### Fixed
- **LED rewire on install**: legacy plugin-name references in per-device LED configurations are now auto-rewired during installation. The plugin was renamed twice during development (`F1SimSubGSIPlugin` → `F1SimHubGSIPlugin` → `F1SimHubLivePlugin`), but per-device `settings.json` files under `PluginsData\Common\Devices\<guid>\` were never repointed. After upgrading from a pre-v1.0.0 build the wheel LEDs would blink white only and the RPM gradient would not render, because every zone-enable formula like `if([F1SimSubGSIPlugin.RpmPercent] > 78, 1, 0)` silently evaluated to 0 (no such plugin loaded). The installer now scans every SimHub device's `settings.json`, replaces `F1SimSubGSIPlugin.` and `F1SimHubGSIPlugin.` prefixes with `F1SimHubLivePlugin.`, and writes a timestamped backup (`settings.json.preLedRewire-<YYYYMMDD-HHMMSS>`) before mutating each touched file. Idempotent: re-running the installer on an already-clean device is a no-op.

## [1.0.2] — 2026-05-25

### Changed
- Dashboard signature separator switched from middle-dot `·` to ASCII pipe `|` (`github  |  instagram`). The middle-dot character is multi-byte UTF-8 and was getting mojibake'd by Dash Studio's save-encoding round-trip; ASCII pipe is durable across any future save cycle.

### Added
- Second screenshot at `docs/screenshots/GSIFPEV2-2.png` showing the full broadcast layout with all three sector times (S1/S2/S3), gear, RPM, speed, throttle/brake inputs, and a magenta personal-best sector. Now used in the README "F1RaceSim_GSIFPEV2 dashboard" layout section; the original `GSIFPEV2.png` remains the README/DASHBOARD hero.

## [1.0.1] — 2026-05-25

### Changed
- Dashboard signature row: fixed UTF-8 triple-mojibake on the middle-dot separator. The `SignaturePlatforms` widget now renders cleanly as `github  ·  instagram` instead of the corrupted `github  Ã‚Â·  instagram` produced by an earlier Dash Studio save cycle.
- Dashboard INPUTS panel labels renamed: `BRAKE` → `BRAKE PRESSURE`, `THROTTLE` → `THROTTLE POSITION` (matches the F1 international-feed convention).

### Added
- Hero screenshot of the live dashboard at `docs/screenshots/GSIFPEV2.png` (HAMILTON on Ferrari, INPUTS panel mid-session). Referenced from `README.md` and `DASHBOARD.md`.

## [1.0.0] — 2026-05-25

### Added — first release

F1SimHubLive is a SimHub plugin + companion `F1RaceSim_GSIFPEV2` Dash Studio dashboard that pipes
live Formula 1 broadcast telemetry (via F1 Live SignalR or F1 MultiViewer's local HTTP API)
into a SimHub-connected wheel screen. The current dashboard is laid out for an 800×480
wheel screen and has been validated on the GSI Formula Pro Elite V2 and GSI Hyper P1.

**Plugin** (`F1SimHubLive.dll`, net48, runs inside SimHub):

- Dual telemetry source: `F1Live` (SignalR feed from `livetiming.formula1.com` — live broadcasts only) or `MultiViewer` (HTTP API at `localhost:10101` — works for live AND replays).
- 60 Hz interpolated render over a ~3–10 Hz upstream feed, configurable `OutputHz` / `RenderDelayMs`.
- Per-driver telemetry: RPM, gear, speed, throttle, brake, DRS state, lap time, sector splits, gap to leader, interval to ahead, tyre compound/age, pit-stop count, top-speed rank, overtake availability.
- **TopSpeed running-max** — the `TopSpeed` property is computed as the max of every live `Speed` sample plus the upstream `BestSpeeds.ST` (speed-trap) snapshot, so the dashboard never visually regresses when a driver hits a higher peak away from the trap. Sanity-capped at 450 km/h and reset on session boundaries.
- Session state: current lap / total laps, session time remaining, track status (YELLOW / SC / VSC / RED), race control flag text.
- Driver identity: TLA, first name, last name, full name, broadcast name, team name, team colour — resolved from MultiViewer's `DriverList` topic.
- Weather snapshot: air temp, track temp, humidity, rainfall, wind speed.

**Installer** (`F1SimHubLive-Installer.exe`, net8 WPF, single-file self-contained ~86 MB):

- Five-step wizard: Welcome → Prerequisites → Driver & source → Install → Done.
- Prereq checks: SimHub install path (auto-detected from registry + standard locations), F1 MultiViewer install path, MultiViewer Live Timing actively streaming (`Heartbeat` AND `SessionInfo` probes — Heartbeat alone is not enough), and **wheel device detection** (enumerates `PluginsData\Common\Devices\` so you can see exactly which screen(s) F1SimHubLive will target).
- Driver dropdown loaded live from MultiViewer's running grid with a bundled 2026 fallback list.
- **Idle dashboard consent** — opt-in checkbox to set `F1RaceSim_GSIFPEV2` as the SimHub idle dashboard on every detected screen. Timestamped backup of each device's `settings.json` written before mutation; declined choice leaves SimHub untouched and the Done page shows a warning explaining how to flip it manually.
- Self-update check on launch — yellow banner appears on Welcome when GitHub Releases reports a newer tag than the installed installer. 3-second timeout, silent on failure, never blocks install.
- Plugin DLL version logging during deploy — shows existing vs incoming `F1SimHubLive.dll` versions so upgrades are explicit, not silent.
- Stops SimHub, deploys plugin DLLs and dashboard files, writes `F1SimHubLive.Settings.json`, applies the idle-dashboard change, restarts SimHub.

**Dashboard** (`F1RaceSim_GSIFPEV2.djson`, 800×480):

- Broadcast-style layout with shift lights driven by live RPM.
- Top-center title shows the selected driver's last name (e.g. `HAMILTON`, `VERSTAPPEN`) so the wheel always makes it obvious which car the telemetry belongs to. Falls back to `F1 LIVE` for the brief window before the DriverList resolves.
- **Driver-name title renders in the live F1 team colour** — when the plugin is `Connected` and the upstream `TeamColour` resolves, the title paints in the broadcast-accurate hex (Ferrari `#E80020`, Mercedes `#27F4D2`, Red Bull `#3671C6`, etc.) so the wheel matches the on-screen TV graphic. Falls back to green on connect, red-orange on connecting, amber on disconnect.
- Left-side broadcast pills for car ahead (INT) and leader (LDR) with car numbers.
- LAST / GAP cluster, sector splits with personal-best (green) and overall-best (purple) flags.
- **INPUTS panel** — live throttle (white) and brake (yellow) bar charts driven by `F1SimHubLivePlugin.Throttle` and `F1SimHubLivePlugin.Brake`, labelled `BRAKE PRESSURE` and `THROTTLE POSITION`, rolling 100-point history.
- Flag/Caution indicator driven by `TrackStatusCode` (YELLOW / SC / VSC / RED).
- `@vicslive` signature widget.

**Build and release infrastructure**:

- `F1SimHubLive.csproj` includes an `AfterTargets="Build"` step that auto-deploys the plugin DLL and dashboard files into the local SimHub install. Opt out with `-p:DeploySimHub=false` (CI). Pass `-p:StartSimHub=true` to chain a SimHub relaunch onto a successful build.
- `scripts/deploy.ps1` — idempotent PowerShell deployer used by the MSBuild target. Skips gracefully when SimHub is running (DLL locked) or not installed. Excludes `*.bak-*`, `*.pre*-*`, `*.backup-*`.
- `.github/workflows/release.yml` — builds and publishes the installer on every `v*.*.*` tag push or via `workflow_dispatch`. Optional Microsoft Trusted Signing via `azure/trusted-signing-action` with federated identity (no long-lived secrets).
- `SIGNING.md` — full code-signing playbook (5 options ranked by UX and cost, signtool examples, timestamping rules, SFI gotcha for Microsoft employees on personal subscriptions).

**Documentation**: `README.md` (user + developer guide), `DASHBOARD.md` (widget reference), `docs/multiviewer-api.md` (MultiViewer endpoint table + why `SessionInfo` is the right liveness probe), `CONTRIBUTING.md`.