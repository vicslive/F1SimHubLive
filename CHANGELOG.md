# Changelog

All notable changes to F1SimHubLive and the companion `F1RaceSim_GSIFPEV2` dashboard.

Format follows [Keep a Changelog](https://keepachangelog.com/). Dates in `YYYY-MM-DD`.

## [Unreleased]

### Docs
- **Verified the full qualifying clock behaviour (Q1→Q2→Q3) on a Barcelona replay** and recorded the captured timeline in [`docs/CLOCKS.md`](docs/CLOCKS.md). Confirmed MV uses a **two-phase per-segment re-anchor** (stages the next segment frozen with `Extrapolating=false`, then flips to `true` at green) and that both surfaces follow each reset to ~1s with no segment-detection code, while the `SessionEnd − playhead` fallback was 40–58 min wrong through Q1/Q2. Updated the verification-status banner (practice + race + full qualifying now replay-verified; live still pending).

- **Consolidated two scattered sets of hard-won learnings into dedicated contract docs** (same "read before you touch this" style as `docs/CLOCKS.md`):
  - [`docs/SECTORS.md`](docs/SECTORS.md) — the sector/lap-time colour-coding contract: the four separate colour systems and their sources, the canonical MultiViewer Material UI palette mined from `app.asar`, the segment status codes, and the traps (purple owned by MV `TimingStats` Position 1, last-lap colour derived by value, quali current-segment-vs-all-quali BEST) with invariants + regression history.
  - [`docs/REPLAY.md`](docs/REPLAY.md) — the replay source & driver-picker contract: the three `ITelemetrySource` sources, the replay virtual clock + wall-clock stamping, the CarData+DriverList-only topic set (timing blank by design), the `{ get; init; }` identity-freeze trap, the driver-independent playhead, row-click hit-testing, and the picker↔plugin JSON command channel.
  - Linked both from the README companion-docs index.

### Added
- `scripts/Capture-ClockTimeline.ps1` — a standalone poller that samples MV's `ExtrapolatedClock` + freshest CarData playhead every few seconds and computes our countdown exactly as the plugin/picker do (primary anchor-extrapolation **and** the SessionEnd fallback), flagging `Extrapolating=false` segment gaps. Lets us validate the clock against MV Live Timing across session phases **without SimHub running**, and is the tool to re-run for the first live-qualifying verification.

## [1.10.15] — 2026-06-28

### Added
- **`RETIRED` pill in the timing tower** — when a driver retires, the LAST column now shows a dark-maroon `RETIRED` pill (matching F1 official Live Timing), instead of the bright-red `IN PIT`. The `Retired` flag was already parsed from `TimingData` and plumbed to `DriverTimingRow.Retired`; this wires it through to the LAST cell via a new `LapStatus.Retired` and the box converters. **Retired takes precedence over in-pit** — verified live against MV (`/api/v1/live-timing/TimingData`), where retired cars (#11 PER, #77 BOT at the 2026 race) report `Retired:true` **and** `InPit:true`/`Stopped:true` simultaneously; without the precedence they'd wrongly read `IN PIT`.
## [1.10.14] — 2026-06-27

### Fixed
- **Wheel/dashboard race countdown was stuck ~3 minutes ahead of MV Live Timing (ignored the formation lap), even after 1.10.13.** The 1.10.13 fix correctly made the `ExtrapolatedClock` anchor the primary clock, but the plugin's `ExtrapolatedClockDecoder` never actually produced a valid anchor: it guarded the `Utc` field with `token.Type == JTokenType.String`, and **Newtonsoft's `JObject.Parse` auto-converts an ISO-8601 string into a `Date` token (`JTokenType.Date`), not a `String`.** So the guard always failed, the anchor stayed `MinValue`, `Clock.IsValid` was always `false`, `_lastClock` was never cached, and the wheel fell back permanently to `SessionEnd − playhead` — which is ~3 min fast on a race because the scheduled end knows nothing about the formation lap. (The picker header clock was unaffected: it parses with `System.Text.Json`, which does not auto-convert dates.) The decoder now reads the anchor with `Value<DateTime>()` regardless of whether Newtonsoft surfaced it as a `Date` or a `String` (preserving `Kind=Utc`), so the wheel countdown is now lights-out-anchored and matches MV Live Timing to the second, identically to the picker.

### Docs
- `docs/CLOCKS.md`: added the Newtonsoft auto-Date-conversion trap (a `Type == JTokenType.String` guard silently drops the `ExtrapolatedClock` `Utc` anchor) and recorded 1.10.14 in the regression history.

## [1.10.13] — 2026-06-27

### Fixed
- **Race countdown was ~4 minutes ahead of MV Live Timing (ignored the formation lap).** Both clocks computed `SessionEndUtc − playhead` against the *scheduled* session end, which knows nothing about the pre-race delay — formation lap, grid forming, aborted starts — so a race countdown ran several minutes fast versus MV's own panel. The session/race countdown is now driven by MV's **`ExtrapolatedClock`** anchor extrapolated to the playback position: `Remaining − (playhead − anchorUtc)` while extrapolating, frozen `Remaining` otherwise. For a race MV pushes the anchor `Utc` to **lights-out** the instant the red lights go off, so this automatically bakes in the formation lap + pre-race delay and matches MV Live Timing to the second; for practice/qualifying the anchor is the session start, so the same formula stays correct. `SessionEndUtc − playhead` is now a **fallback** only, used until the `ExtrapolatedClock` anchor arrives. Applied identically to the picker header clock and the wheel/dashboard countdown.

### Docs
- Updated [`docs/CLOCKS.md`](docs/CLOCKS.md): the canonical formula is now the `ExtrapolatedClock` extrapolation (lights-out-anchored for races), with `SessionEnd − playhead` demoted to a fallback; documents the lights-out anchor behaviour and adds 1.10.13 to the regression history.

## [1.10.12] — 2026-06-27

### Changed
- **Both clocks now compensate MV's ~2s playback-buffer lead.** MV hands over the freshest CarData frame a beat ahead of the frame it paints on screen (and ahead of its own Live Timing panel), so the raw playhead led MV's on-screen clock by ~2s. A shared `PlaybackLead = 2s` constant is now subtracted from the playhead on both the picker header clock and the wheel/dashboard countdown, lining both up with MV Live Timing. The constant is duplicated in both processes on purpose and must be kept equal.

### Docs
- Added [`docs/CLOCKS.md`](docs/CLOCKS.md) — the definitive reference for the session clock/countdown: the single formula both surfaces share, the three MV time signals and which to trust, the four traps behind the 1.10.7–1.10.12 regressions (Newtonsoft `Z`-drop +5h, driver-reset playhead, `-:--:--` placeholder, hour-format), the DO-NOT-BREAK invariants, a file/line map, and the full regression history. Linked from the README docs index.

## [1.10.11] — 2026-06-27

### Changed
- **Wheel/dashboard countdown now uses the exact same formula as the picker header clock.** The wheel previously derived `SessionTimeRemaining` from MV's `ExtrapolatedClock` anchor; on this setup that produced an empty string, so the dashboard fell back to its `-:--:--` placeholder. The plugin now polls **SessionInfo** for `EndDate`+`GmtOffset`, caches the session end in UTC, and emits `SessionEndUtc − playhead` (driver-independent CarData frame position) — byte-for-byte the picker's proven approach, including its `M:SS` / `H:MM:SS` formatting (no leading-zero minutes, no phantom hour). The `ExtrapolatedClock` anchor is retained only as a fallback until SessionInfo yields an end time.

## [1.10.10] — 2026-06-27

### Fixed
- **Wheel/dashboard session clock showed `-:--:--`.** The dashboard renders that placeholder whenever the plugin emits an empty `SessionTimeRemaining`. The wheel clock was extrapolating MV's `ExtrapolatedClock` anchor to `_lastEmittedUtc` — but that field is the *per-driver, forward-only* CarData dedup cursor, which is reset to `MinValue` on every driver switch and only advances for frames matching the currently-selected driver. So whenever the selected driver had no frames in a batch (or immediately after a driver switch) the playhead was `MinValue`, the clock string went empty, and the wheel fell back to `-:--:--`. The wheel clock now uses a dedicated **driver-independent playhead** (`_playheadUtc`, decoded from the freshest frame across all cars via `CarDataDecoder.LatestFrameUtc`) that is set on every CarData response and never reset on a driver switch — mirroring the picker header clock, which already worked for this exact reason.

## [1.10.9] — 2026-06-26

### Fixed
- **Picker header clock showed `0:00`.** The CarData playhead the 1.10.8 clock relies on was read via `DateTime.TryParse(token.ToString())`, but Newtonsoft's `JToken.ToString()` drops the `Z` from MV's UTC timestamps — `TryParse` then returns `Kind=Unspecified` and the subsequent `ToUniversalTime()` re-applied the local offset (e.g. +5 h), pushing the playhead *past* session end so `SessionEnd − playhead` clamped to `0:00`. (The old code only used this value for monotonic frame dedup, so the bug was invisible until 1.10.8 made it an absolute time.) The frame UTC is now read with `token.Value<DateTime>()`, which preserves `Kind=Utc`, so the header counts down correctly in lockstep with the video.
- **Wheel/dashboard clock showed a phantom leading hour (e.g. `1:34:xx` for a 35-minute practice).** The plugin derived remaining as `sessionDuration − (CarDataUtc − raceStart)` with `sessionDuration` defaulting to **2 h**; whenever that default wasn't overwritten by a live `ExtrapolatedClock.Remaining`, the minutes/seconds were right but an extra hour was tacked on. The wheel clock now extrapolates MV's self-consistent `ExtrapolatedClock` anchor (`Remaining` measured at its own `Utc`) to the current CarData playback position — no hard-coded duration and no race-start dependency — so it can't gain a phantom hour. Combined with the 1.10.8 `MM:SS` formatting, sub-60-minute sessions now read e.g. `34:12`.

## [1.10.8] — 2026-06-26

### Fixed
- **Header clock now tracks the video frame-for-frame (no more stuck/lock-jump/flicker).** The 1.10.7 heartbeat-anchor approach still misbehaved because MultiViewer's `Heartbeat` only ticks every ~10 s on a replay, so the clock locked then jumped each time it caught up. The picker now drives the header countdown from the **CarData playhead** — the session-timeline UTC of the freshest telemetry frame, which the wheel dashboard already uses as its "now". The countdown is simply `SessionEndUtc − playhead`: it advances at 1× while the video plays, freezes the instant playback pauses (frames stop arriving), and jumps on a seek (frame UTC jumps, including backward seeks). This is the same proven signal the plugin's wheel clock uses, so the two clocks and the video now stay in lockstep. The `/api/v1/players` `isPaused` polling and the self-running session clock are retired in favour of this single source of truth.
- **Wheel/dashboard clock dropped the spurious hour for sub-60-minute sessions.** `ExtrapolatedClockDecoder.Format` always emitted `H:MM:SS` (e.g. `0:45:18`); practice and qualifying are always under an hour, so it now shows `MM:SS` (`45:18`) to match F1 TV, the video, and MV's live timing. Races (~2 h) still show `H:MM:SS`.

## [1.10.7] — 2026-06-26

### Fixed
- **Header clock was stuck near 59:58 on replays/VODs.** Measured live: for a replayed session MultiViewer serves a *static* `ExtrapolatedClock` anchor whose `Remaining` never decrements (held at 59:59 across 18 s while the `Heartbeat` advanced 15 s during playback). The previous logic re-seeded the countdown from that frozen `Remaining` every poll, so it could only hover at ~59:58. The practice/qualifying clock is now derived as `anchorRemaining − (sessionNow − anchorUtc)`, where `sessionNow` tracks the `Heartbeat` (the field that actually advances with playback) — exactly how MV's own UI counts the clock down in sync with the video. Wall-clock interpolation between the 1 Hz heartbeats is clamped to 1.5 s so a *paused* replay holds cleanly instead of running ahead and snapping back (the earlier flicker). Live sessions and older MV builds fall back to the prior Remaining-decrement logic.

## [1.10.6] — 2026-06-26

### Fixed
- **Segment mini-bar colours were scrambled** — the per-segment status→colour map had three codes wrong: `2049` (personal-best segment) rendered purple, `2051` (overall-best segment) rendered blue, and `2064` (pit-lane in/out-lap segment) rendered green. In-pit cars (RUS, HAD, ANT, VER) therefore showed green pit segments and never showed blue at all. Corrected to the standard F1 codes: `2048`=yellow, `2049`=green, `2051`=purple, `2064`=blue (verified live against MV for the Austrian GP P2 session).
- **Last-lap sector time colour fell back to yellow** — MultiViewer's per-sector `PersonalFastest`/`OverallFastest` flags clear a tick after they're set, so a green/purple last-lap sector reverted to yellow (mismatching live timing, e.g. Leclerc's green S1/S2). The last-lap sector colour is now derived by value (green when the last-lap sector matches the driver's best, purple when it's the field-fastest via `TimingStats` position), with MV's explicit flag only strengthening the colour — matching live timing and stable across ticks.

## [1.10.5] — 2026-06-26

### Fixed
- **Sector purple (session-best) colour was over-applied** — several drivers (e.g. Antonelli, Russell) showed purple sectors they didn't own. The purple decision was based on our own client-side running-minimum sector tracking, which gets polluted by live sector values MultiViewer doesn't count as a valid best, so multiple drivers falsely tied for the field minimum. Purple is now driven by MultiViewer's authoritative `TimingStats.BestSectors[i].Position == 1` flag (the running-min comparison remains only as a fallback when MV reports no position). Now exactly one driver per sector shows purple.

## [1.10.4] — 2026-06-26

### Changed
- **Reverted the live/MultiViewer header session clock to its pre-1.10.2 behaviour.** The 1.10.2/1.10.3 attempts to extrapolate the practice/quali clock from MV's `ExtrapolatedClock` anchor + `Heartbeat` made it worse for the common case: when MV's heartbeat is frozen (e.g. a finished session whose data isn't actively advancing) the smoothed display drifted then snapped, so it flickered and ran behind. Restored the simple, known-good logic (decrement MV's `Remaining` by elapsed wall-clock, re-seeded each poll) that ticks smoothly for genuinely live/streaming sessions. For a static/finished-session snapshot the header clock simply holds — read the on-screen video clock (or the Replay panel's own clock) in that case.

### Added
- **Header now shows the session number** — "Austrian GP: Practice 2" instead of just "Austrian GP: Practice". Reads MultiViewer's `SessionInfo.Name` (the full label: "Practice 2", "Sprint Qualifying", "Qualifying", "Race") for display, while still using `Type` for race detection.

## [1.10.3] — 2026-06-26

### Fixed
- **Live/MultiViewer header clock sawtoothed ("stuck a few seconds, then jumps").** 1.10.2 correctly extrapolated the practice/quali clock from MV's anchor, but recomputed it straight from MV's truth every 250 ms tick — and that truth is re-seeded from MV's jittery 1 Hz `Heartbeat` each poll, so the display stuck then snapped instead of ticking evenly. The countdown now runs on smooth real wall-clock from a baseline and only re-anchors to MV when it drifts past 2 s (a scrub / pause / red-flag stoppage), so it decrements perfectly in lockstep with the 1× video and MultiViewer's own clock. The same smoothing now also covers the race clock.

## [1.10.2] — 2026-06-26

### Fixed
- **Live/MultiViewer header session clock frozen for practice & qualifying.** MultiViewer (like F1's feed) serves `ExtrapolatedClock` as a *static* green anchor — `{Utc, Remaining, Extrapolating:true}` — and never decrements it server-side; the client is expected to extrapolate from the anchor `Utc`. The picker instead decremented `Remaining` by real wall-clock since the last poll, but re-seeded `Remaining` to the same anchor every 1 Hz poll, so the clock stuck at ~59:58 and, in a replayed/VOD session (whose simulated time ≠ real now), never moved at all. (Races were unaffected — they use the Heartbeat/`EndDate` path.) The non-race clock now extrapolates from the anchor `Utc` against *simulated* session time (the Heartbeat), so it ticks down in lockstep with MultiViewer's own header. `SessionInfoClient` now reads `ExtrapolatedClock.Utc`.

## [1.10.1] — 2026-06-26

### Fixed
- **Picker locked onto a frozen, stale replay grid after leaving replay mode.** The plugin rewrites `ReplayStatus.json` / `ReplayGrid.json` ~3 Hz only while a replay is loaded; when SimHub restarts into live/MultiViewer mode (or otherwise leaves replay), those files linger on disk with `Loaded:true`. The picker read them with no freshness check and stayed in replay-grid mode forever — showing a 10-plus-minute-old snapshot (blank TLAs, frozen telemetry) layered under the live MultiViewer wheel/header, which looked like "everything broke". `ReplayControlClient.ReadStatus()` / `ReadGrid()` now ignore status/grid files older than 5 s, so the picker automatically falls back to MultiViewer/live mode when replay isn't actively running.

## [1.10.0] — 2026-06-26

### Added
- **Sync to video by the on-screen session clock — the natural anchor for practice and qualifying.** Lap-sync only works for races (practice/qualifying have no lap counter), and DRM blacks out the F1 TV / Apple TV picture on screen-capture so OCR auto-sync is impossible. So the picker now lets you read the official session clock off the screen (e.g. `P2 59:20`), type it into the new **"Sync to video — Clock"** box, press Enter, and the data jumps to the exact moment the feed showed that value. A live `P MM:SS` readout next to the box shows our current session clock so you can confirm the anchor holds. Works for every session type and is the primary anchor; lap-sync stays as the race-only secondary.
  - Plugin: `ReplayTimeline` indexes F1's `ExtrapolatedClock` topic (`OffsetForRemaining` maps remaining-time → data offset across running/frozen segments; `RemainingAt` is the inverse for the live readout). `F1ReplayClient.SeekToRemaining` / `HasSessionClock` / `SessionRemaining`; `seekclock` command + `HasClock` / `RemainingSec` in the status channel.
  - Picker: `ReplayControlClient.SeekToClock`, the clock row in `MainWindow.xaml`, and `SyncToClock` / `ReplayClockBox_KeyDown` parsing (`mm:ss` or `h:mm:ss`) + the live session-clock readout in `MainWindow.Replay.cs`.

### Fixed
- **Replay driver grid showed blank TLA / team colour for the whole field (regression in 1.9.0).** F1's in-session `DriverList` deltas carry only line/position updates and omit `Tla` / `TeamName` / `TeamColour`, but 1.9.0 upserted grid identity from every delta — blanking the field as soon as the first in-session delta arrived (only the one driver whose delta replayed on seek survived). Identity is now populated **once** from the fully-merged `FirstDriverListJson` snapshot in `ResolveDriverIdentity` and never touched in the per-delta `ApplyDriverList`, so all 22 drivers keep their TLA and team accent colour throughout playback.

## [1.9.0] — 2026-06-26

### Added
- **Replay driver grid — the picker now shows the whole field while in `F1Replay` mode, no MultiViewer required.** Until now the picker's driver grid was fed exclusively by MultiViewer (`LiveTimingClient` + `PickerTelemetryClient` polling `localhost:10101`), so in replay it sat blank and you couldn't see or switch drivers. The plugin now decodes **all** cars at the current replay position and publishes a per-driver snapshot — identity (TLA / last name / team colour) merged with live car telemetry (RPM / speed / gear / throttle) — to `F1SimHubLive.ReplayGrid.json` at the existing ~3 Hz status cadence. The picker binds the grid to these rows whenever a replay session is loaded and click-to-switch still works (writes `DriverNumber` → plugin hot-swaps the wheel's active driver via `F1ReplayClient.SetDriverNumber`), so all three modes — live race, MultiViewer on-demand, and pure replay — drive the same grid.
  - Plugin: `F1ReplayClient.GetGrid()` (all-driver identity+telemetry merge), `CarDataDecoder.ParseAllLatestJson`, `DriverListDecoder.ParseAllDrivers`, and `PublishReplayGrid` on the status timer.
  - Picker: `ReplayControlClient.ReadGrid()` + `ReplayGridDriver`, and replay-grid binding in `MainWindow.Replay.cs` (`EnterReplayGridMode` / `ExitReplayGridMode` / `UpdateReplayGrid`).
  - Phase 1 shows identity + car telemetry only; timing columns (position, gaps, sectors, lap times, tyres) render blank in replay because the replay topic set carries CarData + DriverList only — a future phase can add the `TimingData` topic to light those up.

### Fixed
- **Archive `Index.json` parse crash.** `Meeting.Country` is a nested object (`{Key,Code,Name}`) in F1's archive, not a string — the picker and plugin POCOs typed it as `string`, which threw `Unexpected character … Path 'Meetings[0].Country'` and aborted the whole session list. Removed the unused `Country` field from both `ArchiveMeeting` (picker) and `MeetingInfo` (plugin); Newtonsoft ignores the unmapped property. (`Location`, a real string, is kept.)
- **White-on-white Replay dropdowns.** The year/session combo boxes rendered light text on a light system background, making options unreadable until hovered. Replaced the shallow combo style with a full dark `ControlTemplate` (hardcoded `#1A1A20` field, custom arrow, dark popup) + `ReplayComboItemStyle`, and added `ArchiveSession.ToString()` so the selected session shows its label instead of the raw type name.

## [1.8.0] — 2026-06-26

### Added
- **Third telemetry source: `F1Replay` — on-demand replay straight from F1's public archive, no MultiViewer and no F1 TV subscription for the data.** Alongside `F1Live` (direct SignalR) and `MultiViewer` (local API), the plugin can now play back any past session (race, qualifying, practice, sprint) by reading F1's live-timing static archive at `livetiming.formula1.com/static/`. Same decoders, same dashboard, same SimHub properties — only the source of the bytes changes. This is the same recorded feed FastF1 and the community tooling read; it carries **data only** (no video — the 4K picture stays with Apple TV / MultiViewer, which is the right place for the DRM stream).
  - New `F1Replay/` engine: `ArchiveClient` (HTTP + BOM strip + `BestHTTP` UA), `JsonStreamParser` (`.jsonStream` → timestamped events), `ReplayTimeline` (parallel topic download, merged sorted timeline, lap→offset seek index, deep-merged DriverList for team colours), `F1ReplayClient` (`ITelemetrySource` + transport: play/pause/speed/seek/seek-to-lap, clamped to 16×). Snapshots are re-stamped `Utc=UtcNow` at emit so the interpolator's timing contract holds.
  - Plugin wiring: file-based command channel `F1SimHubLive.ReplayCommand.json` (picker→plugin, monotonic `Seq`) and status channel `F1SimHubLive.ReplayStatus.json` (plugin→picker, ~3 Hz). Runtime source swap (`EnterReplay` / `ExitReplayToLive`) flips between live and replay without restarting SimHub. New `ReplaySessionPath` setting + `ReplayActive/Playing/Speed/PositionSec/DurationSec/SessionName` props.
- **Picker — on-demand Replay panel** (new `⏯ Replay` toggle in the header). Browse the archive by **year → session** (dropdowns from `Index.json`, current season back to 2018, newest first), **Load** / **● Go Live**, and a full transport row: play/pause, **0.5× / 1× / 2× / 4×** speed, a live scrubber, and `MM:SS / H:MM:SS` position. Per-session anchor (last position + speed) is persisted to `F1SimHubLive.ReplayPrefs.json` so reloading a session resumes where you left it. New services: `ArchiveIndexClient`, `ReplayControlClient`, `Models/ArchiveModels.cs`.
- **Video-sync controls (MultiViewer-free viewing).** Because the video and data are now two independent players, sync is explicit:
  - **Replay:** anchor once to the on-screen **lap** (type it → jump there) then fine-nudge **◀ −0.5 s / +0.5 s ▶**. Both run at 1× afterwards, so they stay aligned (quartz drift over a race is sub-second) — re-anchor only if you seek the video.
  - **Live:** a **"Live video delay (Apple TV)"** slider (0–30 s) holds the near-live data back to match a delayed broadcast feed.

### Changed
- **`TelemetryBuffer`** now keeps a short, time-trimmed snapshot history (`RetentionMs`) and exposes `PairAt(target)` so the interpolator can render delayed playback. The classic `prev`/`curr` fast path is unchanged and is still used whenever the broadcast delay is 0 (today's default behaviour, byte-for-byte).
- **`Interpolator`** gains a runtime-settable `BroadcastDelayMs`; when >0 it renders against `UtcNow − (renderDelay + broadcastDelay)` via `PairAt`. New `Settings.BroadcastDelayMs` (default `0`) is hot-reloaded — the plugin re-applies it to the live interpolator within ~250 ms of the picker writing it, and forces it to 0 while the replay source is active.

## [1.7.6] — 2026-06-12

### Changed
- **Picker — per-driver cluster RPM readout: white + larger.** The integer RPM line beneath each gear ring goes from red `#FF4040` FS=10 SemiBold to white `#E8E8EE` FS=12 Bold for better legibility on the dark row background. No layout shift — the cluster column already had headroom for the larger glyphs (5-digit max RPM at Consolas FS=12 ≈ 36 px in a 50-px column).

## [1.7.5] — 2026-06-12

### Added
- **Picker — throttle arc on every gear-cluster ring.** The dark ring around each driver's gear letter now hosts a **blue clockwise arc** (`#3399FF`, 3px stroke, round caps) that sweeps from ~8 o'clock around the top to ~4 o'clock as the driver's throttle goes 0 → 100 %. Closes the visual gap vs F1 Live Timing, which uses exactly this pattern on its per-row driver bar.

### Changed
- New `ThrottleToArcGeometryConverter` (`picker/Services/`) — one-way `IValueConverter` from `double` throttle % to a frozen `PathGeometry` containing one `ArcSegment`. Tunable starting angle / max sweep / radius / min-visible-throttle as `IValueConverter` properties so future style adjustments don't need code changes. Geometry is frozen before return so it's safe to share across all rows and across threads.
- Cluster Grid in `MainWindow.xaml` now layers `Ellipse` (background ring) → `Path` (throttle arc) → `TextBlock` (gear letter). The `Path` has `IsHitTestVisible="False"` so the driver-row click target is unaffected.

## [1.7.4] — 2026-06-12

### Added
- **Picker — broadcast-style per-driver input cluster.** Every row in the live-timing driver list now shows a small dark circular ring with the **current gear letter centered** (`N` / `R` / `1`–`8`) and the **integer RPM** in red beneath, matching the F1 Live Timing per-row layout. Slots between the team-colour TLA tile and the speed column — no other row content moved.
- **Picker — header focused-driver cluster.** Just left of the live RPM readout in the LED preview Border: a **vertical green throttle bar** (0–100%, MV CarData ch.4) and a **big gear letter** (white, MV CarData ch.3) for the currently-selected driver. Pairs with the existing RPM digits + LED shift-light strip so the whole header reads as a single broadcast-style status block for "your driver".

### Changed
- **`PickerTelemetryClient.cs`** — new `DriverInputs(Gear, Throttle, Rpm)` record + `OnInputsBatch` event that fires every CarData poll with one entry per driver in the freshest payload. The parser walks all `Cars.*.Channels` once and harvests channels `0` (RPM), `2` (speed km/h), `3` (gear), and `4` (throttle %) in a single pass, so per-row updates cost the same as the pre-existing per-row speed updates. Selected-driver convenience events `OnRpm` / `OnGear` / `OnThrottle` reuse the same batch entry (no second JSON walk).
- **`DriverTimingRow.cs`** — new `Gear` / `Throttle` / `Rpm` observable properties, plus computed `GearText` (broadcast formatting: `0 → "N"`, `<0 → "R"`, else digit) and `RpmText` (integer, blank when zero so unloaded rows don't show "0").
- **`MainWindow.xaml`** — row Grid gains a new 50-px column at index 2 for the cluster; subsequent columns (speed, LAST/BEST, INT/LDR, tires, PIT, sectors) shift +1.

### Notes
- This release initially attempted to mount the cluster on the wheel LCD (`F1RaceSim_GSIFPEV2.djson`). That was the wrong host — the cluster is for the **picker** (desktop UI), not the wheel. The dashboard change has been fully reverted; the wheel layout is unchanged from v1.7.2. See PR #25 for the full diff including the revert.
- The header throttle bar and gear letter use the selected-driver convenience events, so they only update when the user picks a different driver in the picker. The per-row cluster updates every CarData frame (every ~200ms) for every visible driver — no perceptible cost since the data was already being harvested for `OnSpeedsBatch`.

## [1.7.2] — 2026-06-07

### Fixed
- **Driver Picker EXE now actually gets overwritten on every install.** v1.7.1 (and every prior release) silently kept the previous picker EXE on disk if the picker process was open at install time. `Deployer.TryExtractResourceTo` wraps `File.Create(destPath)` in a catch-all that logged the IOException and returned `false` without any visible signal — the install ran to completion, the user thought it worked, and the picker UI kept showing the old version.

  Symptom that surfaced this: on Dev box post-v1.7.1 install, the wheel LCD showed `v1.7.1` (plugin DLL updated correctly because the installer stops SimHub first) but the picker UI version label still read `v1.7.0` (picker process from v1.7.0 auto-launch held the file open at 18:09 when the v1.7.1 install attempted the overwrite).

  v1.7.2 changes:
    1. New `MaybeStopPicker()` method — mirrors `MaybeStopSimHub()`. Soft-close via `CloseMainWindow()`, wait 1.5s, then `Kill()` any survivors. Called immediately before the picker extract.
    2. New `ReportExistingPickerVersion()` / `ReportNewlyInstalledPickerVersion()` log pair (analogous to the plugin DLL pair) so the install log reads `Existing picker X → Installed picker Y` at every install. If the overwrite silently fails, the "Installed" line will show the OLD version, making the bug visible during the install itself.
    3. Loud warning if `TryExtractResourceTo` returns `false` for the picker: "Driver Picker EXE was NOT updated. If you had the picker open during install, close it and re-run the installer."

  The plugin DLL path was always safe because the installer stops SimHub (which has the DLL loaded) at the very start. The picker EXE path had no equivalent step.

## [1.7.1] — 2026-06-07

### Fixed
- **`ProfileSwitchingMode=1` now applied to ALL touched sections, not just sections we auto-activated into.** The v1.6.0 fix had a hidden hole: `LedProfileSeederService` would `continue;` out of the per-section loop in the "preserve user's existing active profile" branch, skipping the `EnsureSwitchingModeDisabled` call. That meant any section where the user already had a non-default profile selected (the typical state of the `leds` section after using SimHub with any racing game) was left in Mode 2 ("Last selected profile, per game"). Buttons and raw sections — usually on "Default Profile" or empty — were correctly flipped to Mode 1.

  Combined with v1.7.0's custom-game seeding, this created a regression cascade on Dev box:
    1. v1.7.0 installer added `F1SimHubLive` to `CustomGames.json`
    2. SimHub auto-switched to that game on MultiViewer launch (working as designed)
    3. `leds` section was on Mode 2, looked up `LastGameProfiles[Custom_<guid>]`, found nothing
    4. Fell back to "Default Profile" — i.e., **Basic** — wiping the LED bar
    5. User had to manually re-select F1SimHubLive every time

  v1.7.1 removes the `continue;` so `EnsureSwitchingModeDisabled` runs unconditionally on any section we touched (with or without activating into it). The preserved active profile choice stays preserved; Mode 1 ensures SimHub actually honors it across game changes.

  Verified against Vic's Dev box post-v1.7.0 state: `Settings.LEDS.leds.ProfileSwitchingMode = 2` (broken), `Settings.LEDS.buttons.ProfileSwitchingMode = 1` (working), `Settings.LEDS.raw.ProfileSwitchingMode = 1` (working) — exact pattern predicted by the bug analysis.

### Design note
- F1SimHubLive is opinionated about Mode 1 because the plugin's whole purpose is to show a stable LED profile for F1 viewing. Users who genuinely want per-game LED switching across multiple racing titles can re-enable Mode 2 manually in SimHub Settings > Telemetry, and our installer will not re-flip it without also seeding new profiles (the EnsureSwitchingModeDisabled call only runs inside the per-section seed loop, which runs only when there's something to seed). On a stable system where all profiles are present and active, no settings.json write happens at all.

## [1.7.0] — 2026-06-07

### Added
- **SimHub Custom Game auto-seeding.** Installer now creates an `F1SimHubLive` custom game entry in `<SimHub>\PluginsData\CustomGames.json` so SimHub auto-switches its "active game" pointer to F1SimHubLive the moment MultiViewer for F1 launches. Previously SimHub stayed attached to whatever real game it last saw (often Forza, AC, iRacing), which meant any per-game LED/dashboard/motion settings tied to those titles bled into F1-viewing sessions. With the custom game tied to the `MultiViewer` process name, SimHub has a clean game identity for F1 use.

  Implementation (new file `installer\Services\CustomGameSeederService.cs`):
    - Deterministic `Code` field — every install on every machine references the same custom game identifier `Custom_f15ec0de-f1f1-f1f1-f1f1-f15ecf15ecf1`, useful for cross-machine config portability and any future per-game LED binding work.
    - MultiViewer auto-detect — scans `%LOCALAPPDATA%\multiviewer\MultiViewer.exe`, Windows uninstall registry entries (CurrentUser + LocalMachine 32/64), and `%ProgramFiles%\MultiViewer\MultiViewer.exe`. If found, the SimHub "Launch Game" button works out of the box. If not found, `StartPath` is left null — process detection still flips SimHub to F1SimHubLive the moment the user launches MultiViewer manually.
    - `ProcessNames = "MultiViewer"`, `UseAutomaticDetection = true` (game-switching radio), `UseProcessDetectionToActivateGame = false` (stronger activation toggle — switching alone is sufficient).
    - Full `InputsToTelemetrySettings` default block included verbatim from a reference custom game (SimHub appears to require the structure even when no motion mapping is wired).
    - **Idempotent**: matches existing entries by `Name` (case-insensitive). If an `F1SimHubLive` custom game already exists — whether from a prior install or hand-created by the user via the SimHub UI — it is left COMPLETELY untouched. User edits (alternate process names, custom StartPath, manual toggle flips, motion mappings) are preserved.
    - Backup written as `CustomGames.json.preCustomGameSeed-<timestamp>` before any modification.

  Schema reverse-engineered by manually creating a custom game in SimHub's UI on Dev box, forcing a clean SimHub shutdown to flush the in-memory write (SimHub buffers `CustomGames.json` writes until process exit), and diffing the populated file. Documented inline in `CustomGameSeederService.cs` XMLdoc.

### Investigation note
- SimHub's `CustomGames.json` is held in memory until clean shutdown. Installer already stops SimHub before any settings write (see `Deployer.MaybeStopSimHub`), so seeding runs in a safe window. Verified by polling the file after manual creation — file stayed `[]` while SimHub was open, became 2318 bytes within seconds of SimHub exiting.

## [1.6.0] — 2026-06-07

### Fixed
- **Media PC: LED profile picks STILL weren't persisting across SimHub restarts (THE actual root cause).** v1.5.9's defensive ACL grant was applied but the symptom remained: every SimHub restart on Media PC reverted the LED profile dropdown to "Default Profile" despite `activeProfileId` in `settings.json` correctly pointing at F1SimHubLive's GUID. ACLs turned out to be a red herring — `BUILTIN\Users` already had `FullControl` on Vic's Media PC settings.json (inherited), and `LastWriteTime` proved SimHub WAS persisting writes successfully. The file's `activeProfileId` matched our profile after every session. Yet SimHub's UI dropdown kept showing "Default Profile" on next open.

  Real root cause (found by diffing Media PC vs Dev box settings.json field-by-field): the `ProfileSwitchingMode` integer field on the device's LEDS section controls SimHub's "Automatic profile switching" UI radio group, with three values:
    - `1` = **Disabled** — SimHub respects `activeProfileId` as the static pick. (Dev box config.)
    - `2` = **Last selected profile, per game** — SimHub IGNORES `activeProfileId` and uses `LastGameProfiles[<CurrentProfileGame>]` instead. (Media PC config.)
    - `3` = **Automatic** — Best-matching, rule-driven.

  Media PC was in Mode 2 with `CurrentProfileGame = XPlane12`, and `LastGameProfiles[xplane12]` for the buttons section pointed at SimHub's built-in `Default Profile`. So on every restart SimHub correctly read the file but used the per-game-profile lookup path, which had no F1SimHubLive entry for XPlane12, falling back to Default Profile. Every previous fix (orphan-GUID safety in v1.5.8, EnsureActiveOnStartup in v1.5.8, ACL grant in v1.5.9) was writing to the wrong field — `activeProfileId` doesn't matter in Mode 2.

  Fix in v1.6.0: both the installer's `LedProfileSeederService` and the runtime `LedRuntimeSwitcher.EnsureActiveOnStartup` now force `ProfileSwitchingMode = 1` (Disabled) on each section where they activate our profile. New helper `EnsureSwitchingModeDisabled(sectionObj)` in both files (System.Text.Json variant in seeder, Newtonsoft variant in switcher). New counter `SectionsSwitchingModeFixed` on `LedProfileSeedChange` for install-time logging.

  Safety: the mode is forced ONLY when we activate our own profile in a section. If the user has their own racing profile selected (Forza, AC, iRacing, etc.) the seeder's safety check skips activation AND the mode change — their per-game switching preference is preserved untouched.

  Effect on Vic's Media PC: install v1.6.0, then SimHub's LED dropdown will show F1SimHubLive on every open (no manual re-pick required). The LED bar will activate as soon as MultiViewer is detected, and persist correctly across all subsequent SimHub sessions. Combined with v1.5.8 (orphan-GUID safety + EnsureActiveOnStartup) and v1.5.9 (ACL belt-and-suspenders), this completes the Media PC LED reliability story.

## [1.5.9] — 2026-06-07

### Fixed
- **Media PC: LED profile picks weren't persisting across SimHub restarts (defensive ACL fix).** After v1.5.8 shipped, Vic tested on Media PC and confirmed the real bug: even when he manually picked F1SimHubLive via SimHub's LED dropdown, the choice didn't survive a close/reopen cycle — every SimHub restart reverted to the GSI FPE V2 default profile. The dev box correctly persisted picks. The LCD always worked fine because dashboards live in a different code path (DashTemplates) that SimHub doesn't try to write at runtime.

  Root cause: when the F1SimHubLive installer is run elevated (UAC prompt accepted), Windows persists tighter ACLs on newly written files than what the SimHub-running user account has. SimHub runs as the regular user (non-elevated), so its serialization of `activeProfileId` to `Program Files\SimHub\PluginsData\Common\Devices\<guid>\settings.json` silently fails on close. On next start SimHub re-reads the stale value from disk. The dev box was the lucky case — its settings.json happened to have `BUILTIN\Users: FullControl` from an earlier non-elevated touch (`ICACLS` showed Users had inherited write rights), so SimHub's user-mode process could persist UI picks. Media PC didn't have that grant.

  Fix in v1.5.9: `LedProfileSeederService.cs` now calls a new `TryEnsureUsersCanWrite()` helper after every successful `settings.json` write. The helper clears any ReadOnly attribute and adds a `BUILTIN\Users: Modify` ACL rule (locale-independent SID `S-1-5-32-545`, so it works on non-English Windows installs too). Best-effort — failures are logged but don't abort the install (some locked-down GP-controlled boxes or network-share installs may not permit ACL changes).

  Effect on Vic's Media PC: install v1.5.9, then SimHub's UI picks will persist across close/reopen for real. Combined with v1.5.8's orphan-GUID safety check and runtime EnsureActiveOnStartup pass, the LED bar will activate on install AND stay activated through all subsequent SimHub sessions.

  Note: this ONLY corrects the ACL on files the F1SimHubLive seeder writes (i.e., the device settings.json for supported wheels). It does not touch any other SimHub config files. Users with the same persistence bug for OTHER LED profiles or other devices will need a separate fix from SimHub upstream — but for our profile on our supported wheels, v1.5.9 is a clean defensive patch.

## [1.5.8] — 2026-06-07

### Fixed
- **Media PC: LEDs stuck on "Default" profile after every fresh install — the wheel LCD lit up correctly, but the LED bar stayed dark until Vic manually opened SimHub → LEDs → Profile dropdown and picked F1SimHubLive every time.** The root cause was a two-layer issue in the LED profile activation pipeline:

  1. **Installer seeder safety check had an orphan-GUID gap.** `LedProfileSeederService.cs`'s `safeToActivate` logic was: "the current `activeProfileId` is empty OR resolves to a profile whose Name starts with `Default*`." But SimHub on some boxes (Media PC, fresh installs, boxes that have never had a custom LED profile picked) leaves `activeProfileId` pointing at a GUID for a *built-in* default profile that **isn't enumerated in the `Profiles[]` array**. The seeder's `FindById` returned null, `currentActiveName` stayed null, `safeToActivate` stayed false, and the seeder preserved Default instead of flipping to ours. On Vic's dev box, the buttons section happened to enumerate its Default Profile in `Profiles[]` (the "lucky" case), so the seeder worked — Media PC was the unlucky case.
  2. **Runtime LED switcher (v1.5.0) only fires on MultiViewer up/down transitions** and even then SimHub doesn't hot-reload `settings.json` while running — there's no FileSystemWatcher on the per-device LED settings, so any write is invisible to SimHub until its next cold start. Combined with bug #1, this meant the v1.5.0 auto-switcher could never recover from the "stuck on Default" state on its own; the user had to either reinstall (which re-ran the buggy seeder and changed nothing) or manually pick via the UI (which DID update SimHub's in-memory state).

  Fixes in v1.5.8:
  - **Seeder safety check now treats orphan GUIDs as safe to overwrite** (in `installer/Services/LedProfileSeederService.cs`). If the current `activeProfileId` doesn't resolve to any profile in the `Profiles[]` array, we assume it's pointing at SimHub's built-in/implicit default (the user can't have consciously selected it — there's no UI entry for it) and we flip to ours. Real user picks (Forza, AC, iRacing profiles, etc., all of which DO enumerate in `Profiles[]` because the user created/imported them via the UI) are still preserved.
  - **New `LedRuntimeSwitcher.EnsureActiveOnStartup()` runs on plugin Init** regardless of MultiViewer state. Walks every supported device's `settings.json`, applies the same safety check, and re-asserts our profile as active if the current selection is empty, `Default*`, or an orphan GUID. The write is atomic; SimHub picks it up on its next start. This closes the gap for users who install a plugin-DLL-only update without re-running the installer (the seeder doesn't run in that path).

  Effect: on next install of v1.5.8, Vic's Media PC will write the correct `activeProfileId` to every device's `settings.json`, SimHub will read it on next start, and the LEDs will work immediately — no more manual UI flip after each install.

## [1.5.7] — 2026-06-07

### Changed
- **Wheel LCD signature layout polish** (Media-PC verification feedback from v1.5.6): `@vicslive` was over-corrected to the far left in v1.5.6 — moved it back to sit directly above the `github | instagram | Ver. X.X.X` strip (both elements now share `Left=442, Width=340`, both center-aligned, so the cyan handle is perfectly vertically aligned with the gray meta line beneath it). Also shifted the meta strip ~10 px right (`Left 432 → 442`) so it doesn't kiss the left edge of the right panel.

## [1.5.6] — 2026-06-07

### Added
- **Wheel LCD now shows the running plugin version** so Vic can confirm at a glance which release is actually loaded — no more guessing whether the wheel picked up the latest build. The bottom-right signature row previously read `github  |  instagram`; it now reads `github  |  instagram  |  Ver. 1.5.6` (the version part is dynamically bound to a new `F1SimHubLive.Version` plugin property and updates automatically every release — no future `.djson` edits required).
- **New `F1SimHubLive.Version` plugin property** exposed to SimHub, set at `Init` from the assembly's `InformationalVersion` attribute (trimmed of any `+commit-sha` SourceLink suffix). Any other dashboard, formula, or button binding can reference `$prop('F1SimHubLivePlugin.Version')` to display the version too.

### Changed
- **`@vicslive` signature on the wheel LCD moved from the right side (Left=572) to the left edge (Left=20)** with left-alignment, freeing the right side of the bottom row for the new wider `github | instagram | Ver. X.X.X` strip. Same `Top` position, same font, same color — purely a horizontal shift.

## [1.5.5] — 2026-06-07

### Fixed
- **Picker LED preview bar still dim and every driver stuck at 0 km/h on Media PC even with v1.5.4's Newtonsoft parity fix.** Real root cause was simpler than parser asymmetry: the picker's `PickerTelemetryClient` had a **2-second HTTP timeout** while the plugin (which works on the same machine driving the wheel) and the picker's own `LiveTimingClient` (which also works) both use **3 seconds**. The MV CarData endpoint is the largest payload in the API — a full-grid telemetry frame for ~20 drivers with multiple historical entries is typically 30-80 KB. On Vic's Media PC the body transfer + JSON deserialization consistently exceeded 2s, causing every CarData poll to throw `TaskCanceledException`, which the picker's outer catch counted as a consecutive failure and the loop kept retrying forever with nothing to show.

  Fixes in v1.5.5:
  - **Timeout bumped 2s → 5s** in `PickerTelemetryClient`. This is more headroom than the plugin (3s) because the Media PC is the worst-case box; if the wheel survives, the picker will too.
  - **Automatic GZip + Deflate decompression** enabled on the picker's HttpClient. MV serves CarData uncompressed by default but supports gzip when `Accept-Encoding: gzip, deflate` is sent — this cuts the over-the-wire payload roughly 5×. The `Accept-Encoding` header is now set explicitly.
  - **User-Agent identifier added** (`F1SimHubLive-Picker/1.5.5`) so future MV-side log diffs can distinguish picker requests from plugin requests at a glance.

- **"Waiting for MultiViewer telemetry" appears on the very first failed poll when the picker has never connected**, instead of waiting for 3 consecutive failures (which was ~15s with the old 2s timeout). After first successful connect, the 3-failure buffer is restored so brief CarData drops at session boundaries don't flap the status.

### Why v1.5.4's Newtonsoft fix wasn't enough
v1.5.4 was a real fix for a real bug (System.Text.Json strictness vs MV schema drift) but it wasn't *Vic's* bug on Vic's Media PC. The picker on Media PC never reached the parser at all — every HTTP call was timing out before the body was fully received. v1.5.5 keeps v1.5.4's Newtonsoft parity (so any future schema-drift case is handled) AND adds the actual fix (timeout headroom) for the Media PC scenario. If the symptom persists after v1.5.5, the picker will now show **"Waiting for MultiViewer telemetry (ExceptionType)"** in the RPM readout within ~5 seconds instead of staring blank — making the next round of diagnosis fast.

## [1.5.4] — 2026-06-07

### Fixed
- **Picker LED preview bar stayed dim on Vic's Media PC even though the wheel was lighting up correctly.** Same machine, same MultiViewer instance, same `/api/v1/live-timing/CarData` endpoint — yet the plugin (running inside SimHub) parsed CarData and drove the wheel, while the picker silently received nothing. Symptom matrix: wheel responsive ✓, every driver in the picker leaderboard showing 0 km/h ✗, picker RPM readout never updating ✗, no error message anywhere ✗.

  Root cause: parser asymmetry between the two processes. The plugin (`F1SimHubLive.dll`, targets .NET Framework 4.8) uses **Newtonsoft.Json** (`JObject.Parse`), which tolerates trailing commas, comments, duplicate keys, and other minor JSON schema drift. The picker (`F1SimHubLive-Picker.exe`, targets .NET 8) was using **System.Text.Json** (`JsonDocument.Parse`) with its default strict options, and was swallowing whichever schema-drift exception MV's Media-PC payload triggered (the parser threw, the `catch` block silently returned false, the loop kept polling forever with nothing to show). Vic's dev machine (SupermanOne) parses the same endpoint fine, so the bug never surfaced before this Media-PC install.

  Fix: picker's `PickerTelemetryClient.TryParseLatest` is rewritten to use `Newtonsoft.Json` (`JObject`/`JArray`) mirroring the plugin's `CarDataDecoder`. The picker's `.csproj` now references `Newtonsoft.Json 13.0.3` (same version as the plugin). If the plugin can parse a CarData response, the picker can now parse it too — by construction.

### Added
- **Telemetry health is now visible in the picker UI.** Previously when CarData wasn't flowing, the LED preview bar simply stayed dim and there was no indication of why. v1.5.4 surfaces three new states next to the LED strip via the RPM readout text:
  - `—` (gray) — never received a frame yet; waiting for first CarData poll
  - `no MV` (orange) — HTTP failures: MultiViewer process not running, port wrong, firewall blocking
  - `ERR` (red) — HTTP succeeded but the CarData payload failed to parse. Tooltip points to the diagnostic log file location.
  - `stale` (orange) — was previously receiving data but no fresh frame in the last 5 seconds. Also forces the LED strip to dim so the user doesn't trust a frozen shift-light pattern.

  Healthy state stays unchanged: the integer RPM with the existing tooltip "Live RPM (MultiViewer telemetry)".

- **First-failure raw-response dump for diagnostics.** When the picker's CarData parser throws, v1.5.4 dumps the raw HTTP response (plus a header noting the driver number, base URL, and exception type) to `%APPDATA%\F1SimHubLive\Diagnostics\picker-cardata-failed-<timestamp>.json`. Only the *first* failure per process lifetime is logged — subsequent failures are skipped to avoid filling the disk with identical payloads. Hover over the `ERR` readout to see the dump folder path. This file is what a future bug report can attach to identify the next schema drift.

### Why this was missed before
The picker's parser worked perfectly on Vic's dev machine (SupermanOne) because that machine's MultiViewer was producing strict JSON that both STJ and NJSON accepted. The Media PC happened to surface a payload variation that NJSON tolerates but STJ rejects — likely a trailing comma or a number-as-string somewhere in `Cars["44"]["Channels"]`. Without a raw-response dump anywhere in the picker, the only diagnostic was "LED bar dim, no message" — which made it look like an MV issue rather than a parser issue. v1.5.4 adds both the structural fix (NJSON parity with plugin) AND the diagnostic surface (visible status + on-disk dump) so any future asymmetry is detectable on first observation rather than reverse-engineered after multiple iterations.

## [1.5.3] — 2026-06-07

### Fixed
- **The v1.5.2 `RpmShiftLight` migration didn't actually reach the file the plugin reads.** v1.5.2 changed the in-installer migration to detect the pre-1.5.2 default pair `(5500, 11500)` and upgrade it to `(3500, 13000)` — but the upgrade was only written to the machine-wide **PROGRAMDATA** seed (`%PROGRAMDATA%\F1SimHubLive\F1SimHubLive.Settings.json`). The plugin and picker both read from per-user **APPDATA** (`%APPDATA%\F1SimHubLive\F1SimHubLive.Settings.json`), and the resolver only copies PROGRAMDATA → APPDATA on **first** launch. Anyone who had installed any v1.4.x / v1.5.0 / v1.5.1 build already had an APPDATA file with the saturated `(5500, 11500)` values, and v1.5.2 never touched it — so the white-flash redline + non-sequential gradient symptoms persisted exactly as before.

  Reproduced on Vic's Media PC: installed v1.5.2, wheel still flashed white at 11,300 RPM (which is 96.7% of the 5500–11500 range = solidly inside the Redline 1 + Redline 2 bands, exactly the visible symptom).

  Fix: new `AppDataSettingsMigrationService` runs after `WriteSettings()` in the installer's `Deployer`. It walks every user profile's APPDATA folder (the installer is elevated, so writes into other users' profiles succeed), parses each `F1SimHubLive.Settings.json`, and rewrites the `RpmShiftLight*` values **only** when the exact pre-1.5.2 default pair `(5500, 11500)` is present. Any other value pair is treated as an intentional customization and preserved. Each modified file gets a timestamped `.preAppDataMigration-<stamp>` backup next to the original, and writes are atomic (sibling temp file + rename) so the plugin's `FileSystemWatcher` never sees a partial JSON document.

  Workaround for users who already installed v1.5.2 and don't want to wait for v1.5.3: open `%APPDATA%\F1SimHubLive\F1SimHubLive.Settings.json` in Notepad, change `RpmShiftLightStartRpm` from `5500` to `3500` and `RpmShiftLightEndRpm` from `11500` to `13000`, save. The plugin reloads within ~250 ms via its FileSystemWatcher — no SimHub restart needed.

- **Picker LED-preview bar stayed dark when MultiViewer was on a non-default URL.** The driver picker hardcoded `http://localhost:10101` as the MultiViewer base URL and ignored the `MultiViewerBaseUrl` field in the settings file. The plugin (which DOES read it from settings) would happily talk to MV on whatever URL the install wizard captured, but the picker's HTTP polling silently hit nothing — so the wheel would light up correctly while the picker's preview strip sat dim. Fixed by reading `MultiViewerBaseUrl` from the settings file when the `--mv-url` CLI override is not provided. Falls back to `http://localhost:10101` if the field is missing or fails loopback-URL validation (same validation rule the plugin's `Settings.Validate` uses, so a malformed or attacker-edited URL can't redirect the picker either).

  `SettingsFileWriter.ReadMultiViewerBaseUrl()` is the new helper; `MainWindow` consumes it once in the constructor, in the order: CLI arg → settings file → default. Picker reads the file freshly on every launch — no caching, no further migration logic required.

## [1.5.2] — 2026-06-07

### Changed
- **`RpmShiftLight` defaults tightened to match real-world F1 V6 hybrid RPM ranges.** Pre-1.5.2 defaults of `RpmShiftLightStartRpm=5500` / `RpmShiftLightEndRpm=11500` were too narrow for modern F1 cars, which routinely rev to 12–14k RPM on DRS straights. The result on a fresh install: the wheel LED bar pinned to redline (white flash) almost constantly through normal racing, and the gradient could not fill sequentially because RPM exceeded the ceiling — individual LEDs lit non-contiguously as RPM bounced back and forth across thresholds (e.g. one green lit, then a gap of dark LEDs, then blues). v1.5.2 ships `3500` / `13000` as the new defaults — empirically tuned on Vic's dev box (SupermanOne) over months of live F1 broadcast viewing through MultiViewer, with the GSI Formula Pro Elite V2 wheel. The new range gives greens visible during pit-lane out-laps and slow corners, smooth gradient fills through most of a normal lap, and redline white flash only when the car actually approaches its peak (≥12.5k RPM).

  Updated in: `F1SimHubLive/Settings.cs`, `installer/Services/Deployer.cs`, `picker/MainWindow.xaml.cs`, `picker/Services/SettingsFileWriter.cs`, `README.md`.

### Added
- **Installer auto-migrates existing users still on the pre-1.5.2 default pair.** If `%APPDATA%\F1SimHubLive\F1SimHubLive.Settings.json` contains the exact pair `RpmShiftLightStartRpm=5500` / `RpmShiftLightEndRpm=11500`, the v1.5.2 installer treats it as "user never opened the picker" and upgrades them in place to the new `3500` / `13000` defaults, with a log notice explaining what happened and pointing to the picker for further tuning. Any other value pair is treated as an intentional customization and preserved as-is (existing v1.5.1 preservation behavior).

  This catches Vic's Media PC (fresh v1.4.0+ install → seeded with the old defaults) without forcing him to delete his settings file. Any user who manually tuned to something other than (5500, 11500) is left alone.

### Why this was missed before
The 5500/11500 defaults shipped in v1.3.x as "calibrated" values, but they were calibrated against the assumption that LED telemetry profiles would care about *typical* RPM, not peak RPM. The Telemetry profile bands actually trigger on `RpmShiftPercent` thresholds, which means peak-RPM saturation is what matters most — and on real F1 broadcasts modern PUs sustain 12-14k far more than the original tuning assumed. Vic tuned his dev box to 3500/13000 manually months ago, the wheel worked beautifully, and the original defaults were never revisited. The Media PC (first true fresh v1.4.0+ install, see v1.5.1 notes) made the gap visible — LEDs were "going crazy" because the defaults were genuinely wrong, not because of a code bug.

## [1.5.1] — 2026-06-07

### Fixed
- **Critical: all F1SimHubLive LED profiles dark on fresh installs of v1.4.0 / v1.4.1 / v1.5.0.** The Telemetry and Prime Gradient seed-asset JSONs shipped a typo in their master-gate trigger expressions: `if([F1SimHubLive.MultiViewerRunning] = 1, 1, 0)` instead of `if([F1SimHubLivePlugin.MultiViewerRunning] = 1, 1, 0)`. SimHub exposes plugin properties under the plugin **class name** (`F1SimHubLivePlugin`), NOT the `[PluginName]` attribute value (`F1SimHubLive`) — every other binding in those profiles correctly used `F1SimHubLivePlugin.*`, only the two master gates carried the typo. NCalc evaluated the broken gate to null (`Value cannot be null. Parameter name: conversionType`), which cascaded to disable every child LED container in the profile. Symptom on a fresh install: LCD works perfectly (LCD bindings used the correct namespace) but the LED bar stays completely dark even while MultiViewer is running.

  Bug never surfaced before today because no one had run the installer on a clean box since the typo was introduced in v1.4.0 — Vic's dev box (SupermanOne) was running his hand-crafted v1.0 profiles with `DataCorePlugin.GameRunning` and never touched. The Media PC was the first true fresh install of the v1.4.0+ generation.

  Fix is two-pronged: (1) seed-asset JSONs (`installer/Assets/LedProfiles/leds-F1SimHubLive-Telemetry.json` and `raw-F1SimHubLive-PrimeGradient.json`) are corrected so new installs are clean from the start, and (2) `LedConfigRewireService` gained a new `SpecificRewrites` table that auto-heals existing v1.4.0/1.4.1/1.5.0 installs: it rewrites `F1SimHubLive.MultiViewerRunning` → `F1SimHubLivePlugin.MultiViewerRunning` in place in each device's `settings.json`, with the same backup + atomic-write discipline used by the existing legacy-plugin-prefix rewrites. A broad `F1SimHubLive.` prefix rewrite was intentionally avoided — kept as a narrow surgical replacement to prevent over-matching any unrelated future settings keys.

  Effect: install v1.5.1 over the broken state → installer logs `rewired 1 legacy plugin reference(s)` per device, LED profile lights up on next MultiViewer launch with zero further user intervention.

## [1.5.0] — 2026-06-07

### Added
- **Plugin auto-switches LED profiles when MultiViewer starts / stops** — eliminates the v1.4.x manual step where the user had to open *SimHub > Devices > GSI Formula Pro Elite V2 > LEDs* and pick `F1SimHubLive` for each of the three sections (Buttons, Telemetry, Individual). New `LedRuntimeSwitcher` class is wired into `F1SimHubLivePlugin.UpdateMultiViewerRunning()`: every time the 5-sec process poll detects a false→true transition, it snapshots the device's current `activeProfileId` for each LED section, then sets ours active. On the true→false transition, it restores the snapshot.

  Behavior matrix (per device, per LED section):

  | At MV start, current selection is… | Action |
  |---|---|
  | Already F1SimHubLive | No-op |
  | Anything else (Forza, AC, iRacing, Default…) | Snapshot it, set F1SimHubLive active |

  | At MV stop, current selection is… | Action |
  |---|---|
  | Still F1SimHubLive | Restore the snapshotted profile |
  | User manually changed it during the MV session | Leave the user's choice alone, discard snapshot |
  | F1SimHubLive but no snapshot recorded (SimHub restarted while MV was up) | Leave F1SimHubLive active; user can re-pick manually if desired |

  Writes are atomic (temp file + `File.Replace`) so SimHub's `FileSystemWatcher` sees one clean state every cycle — same pattern the picker has been using for our own settings file since v1.4.1. Runs on a `ThreadPool.QueueUserWorkItem` so file I/O can't stall the 5-sec poll timer. Multi-device safe: iterates every directory under `PluginsData\Common\Devices\<guid>\` and skips anything whose `DeviceTypeID` is not GSI FPE V2 (`EFC17674-559A-44DB-8D24-C6CFD203384D`).

  Effect: Vic's Media PC (clean install, no racing profiles) will now go from no-LEDs-at-all to fully-lit within ~5 seconds of launching MultiViewer, without ever opening the SimHub Devices page. His dev box (SupermanOne, F1 2025 + AC Rally setups intact) will see Forza/F1 2025 profiles preserved when not viewing F1, and auto-switch to F1SimHubLive only while MultiViewer is up.

- **New `scripts/Check-VersionAlignment.ps1` + CI gate** — verifies all three csproj `<Version>` values (plugin, picker, installer) agree with each other and with the git tag. v1.4.0 shipped with the picker and plugin csprojs still saying `1.3.9` while the installer csproj said `1.4.0`; CI now fails fast on this kind of slip. Run locally with `pwsh ./scripts/Check-VersionAlignment.ps1 -Expected 1.5.0`.

### Changed
- **`IdleDashboardService` now respects user's existing IDLE dashboard selection.** v1.4.x always overwrote `CurrentIdleDashboard` to `F1RaceSim_GSIFPEV2` on every install, even if the user had picked something else. New safety rule (mirrors the v1.4.0 LED `activeProfileId` safety check): only overwrite when the current value is null/empty, starts with `Default`/`SimHub`, or is one of our own prior selections (`F1RaceSim`, `F1RaceSim_HyperP1`, `F1RaceSim_GSIFPEV2`). Otherwise log "preserved. F1SimHubLive dashboard installed but not auto-activated. To use it, open SimHub > Devices > … > LCD and pick …" and leave the user's choice intact.

- All three csproj `<Version>` values bumped to `1.5.0` together (plugin, picker, installer). Validated by the new alignment check.

## [1.4.1] — 2026-06-07

### Added
- **Picker has a new `🚀 Auto-launch` checkbox** in the top-right toolbar (next to `📌 Pin`) that toggles the `AutoLaunchPicker` setting on the fly. Symptom Vic caught on the Media PC after running the v1.4.0 installer: he forgot to tick the "Launch picker with SimHub" option in the install wizard, and there was no way to turn it on afterward short of editing `F1SimHubLive.Settings.json` by hand or re-running the installer. The new checkbox mirrors the existing `📌 Pin` toggle pattern: on picker startup, the checkbox is seeded from the current `AutoLaunchPicker` value in `%APPDATA%\F1SimHubLive\F1SimHubLive.Settings.json`; checking or unchecking it writes the new value atomically (via the existing `SettingsFileWriter` infrastructure that already handles `DriverNumber` and `RpmShiftLight*`). The setting takes effect on the next SimHub launch — the plugin reads `AutoLaunchPicker` once during `Init()` to decide whether to spawn the picker. No need to ever re-run the installer for this preference again.

### Changed
- Picker and plugin csproj versions bumped from `1.3.9` → `1.4.1` to align with the installer. The v1.4.0 release accidentally shipped with the picker and plugin still labeled `1.3.9` internally even though both binaries had real changes (plugin gained `MultiViewerRunning`); the picker's About tooltip and the plugin DLL's FileVersion now correctly read `1.4.1`.

## [1.4.0] — 2026-06-07

### Added
- **Installer now seeds three `F1SimHubLive` LED profiles** into every SimHub GSI Formula Pro Elite V2 device on the target machine, fixing a long-standing fresh-install bug where the wheel LEDs only ever showed "Default Profile" because our installer wired up the LCD dashboard but left the LED side untouched.

  Symptom Vic caught on a fresh Media PC install today: after running the v1.3.9 installer on a clean machine (SimHub installed from scratch, GSI FPE V2 paired, F1RaceSim_GSIFPEV2 dashboard manually selected on LCD), opening *SimHub > Devices > GSI Formula Pro Elite V2 > LEDs* shows only `Default Profile` in every section (Buttons lighting / Telemetry Leds / Individual leds). SimHub's Default Profile is gated on `GameRunning = 1`, and F1SimHubLive deliberately runs with no game launched (telemetry flows in via the plugin from F1 MultiViewer's local API or F1 live-timing's SignalR feed), so the wheel stays dark forever. No amount of dashboard reselection fixes it — the LED profiles are stored in a *completely separate* section of the device's `settings.json` (`Settings.LEDS.{leds,buttons,raw}.Profiles`) that our installer never touched.

  Root cause: three working profiles only existed on Vic's dev box because he hand-built them in SimHub's profile editor over multiple sessions (the earliest `LastLoaded` timestamp on the captured profiles is 2024-02-12). The original trick — `if([DataCorePlugin.GameRunning] = 0, 1, 0)` inside each profile's `LedContainers[N].TriggerFormula.Expression` — flipped the firing condition so the LEDs animated in IDLE mode instead of in-game. None of this state lives in the dashboard `.djson` or the plugin DLL — it's all per-device wheel-config JSON.

  Fix is a new `installer/Services/LedProfileSeederService.cs` that enumerates every device under `PluginsData\Common\Devices\<guid>\settings.json`, matches `DeviceTypeID == EFC17674-559A-44DB-8D24-C6CFD203384D` (GSI FPE V2's stable type ID — same on every install), and for each of the three sections:
  1. Backs up `settings.json` to `settings.json.preLedProfileSeed-<stamp>` before any mutation.
  2. Loads the corresponding embedded `F1SimHubLive` profile from a new `Assets/LedProfiles/` folder.
  3. Skips insertion if a profile with the same `Name` is already present (idempotent — safe to re-run on dev box or on upgrade).
  4. Otherwise mints a fresh `Guid.NewGuid()` for `ProfileId` so the dev-box GUID never leaks to multiple machines.
  5. Flips `activeProfileId` to ours **only** if the current selection is empty or points to SimHub's built-in `Default Profile`. If the user has selected their own racing profile (Forza, iRacing, AC, etc.), it is left untouched — the user picks `F1SimHubLive` manually from the SimHub UI when they want it. This prevents the installer from overwriting an existing gaming setup.

  Three profiles seeded:
  - `leds`    → `F1SimHubLive - Telemetry` — RPM shift-light bar (1 LedContainer)
  - `buttons` → `F1SimHubLive` — static button colors (0 LedContainers — inherits brightness only)
  - `raw`     → `F1SimHubLive - Prime Gradient` — individual-LED gradient (4 LedContainers)

  Wired into `Deployer.cs` immediately after `LedConfigRewireService` (the plugin-name rewire), before the idle-dashboard write. Adds ~1.4 MB to the installer (telemetry profile alone is ~1.1 MB of LED-segment definitions); installer total stays under 100 MB.

  Unsupported wheels (anything other than GSI FPE V2) are skipped with a log line. Adding Hyper P1 or other GSI wheels requires capturing the equivalent profile shape from a known-working install of that wheel; the seeder architecture supports it via the `Seeds` table — only the asset files need to be added.

- **New plugin property `F1SimHubLive.MultiViewerRunning`** (bool). Polls the live Windows process table every 5 seconds for any `MultiViewer*` or `F1MV*` process. Surfaces the result as a SimHub property that LED profile TriggerFormulas can gate on. Used by the three seeded LED profiles as their firing condition:
  ```
  if([F1SimHubLive.MultiViewerRunning] = 1, 1, 0)
  ```
  Replaces the older `GameRunning = 0` gate (which was too permissive — fired any time SimHub had no game running, including pure idle staring at the wheel). The new gate is precise: LEDs only fire when MultiViewer is up on the same machine, which is the exact signal for "user is in F1-viewing mode." When MultiViewer is closed (gaming, idle, or anything else), the trigger evaluates false and our profile goes dark, leaving the wheel free for other SimHub profiles. Per Vic 2026-06-07: "nobody runs F1 MultiViewer at the same time they're actually gaming" — so MV-running is a clean binary signal. Logs the transition each direction (MV detected / MV no longer running) so the SimHub log shows the on/off events.

### Changed
- LED profile names rebranded from `F1 Live` to `F1SimHubLive`:
  - `F1 Live- Telemetry for F1 Race viewing` → `F1SimHubLive - Telemetry`
  - `F1 Live` → `F1SimHubLive`
  - `F1 Live - Prime Gradient` → `F1SimHubLive - Prime Gradient`

  Why: (1) clearer attribution in SimHub UI — users see our brand, not a generic "F1 Live", (2) Vic's dev box has the old `F1 Live` names, so the new names avoid an idempotent skip-path on his dev box during fresh-EXE testing, (3) future-proofs against name collision with any community profile that ships a "F1 Live" named profile.

- All `LedContainers[N].TriggerFormula.Expression` references to `[DataCorePlugin.GameRunning]` rewritten to `[F1SimHubLive.MultiViewerRunning]` (see "Added" above for context).

- Scrubbed three leftover `"GameCode": "IRacing"` strings inside the Telemetry profile's per-LedContainer animation overrides. These were dead code under the GameRunning-based trigger but a latent hazard now that the trigger is no longer game-gated — without scrub, a user who actually runs iRacing could see our LED behavior unexpectedly switch under iRacing's GameCode. Cleared to empty so per-container overrides are game-agnostic.

- `LedProfileSeederService` no longer always flips `activeProfileId`. New safety check: only flips if current selection is empty/null or points to SimHub's built-in `Default Profile`. Existing custom selections are preserved with a log message telling the user how to manually select the F1SimHubLive profile.

- README: extended the "LED config auto-rewire" section to cover both legacy plugin-name rewire *and* the new F1SimHubLive profile seeding. New troubleshooting entry: "LED area shows only Default Profile after install".

## [1.3.9] — 2026-06-06

### Added
- **Session header bar above the picker driver list** — mirrors MultiViewer's top banner so the picker can sit side-by-side with MV and read like one app. Shows: country flag (PNG, real image — not Segoe UI Emoji which renders as literal "CA" letters on most Windows builds), race name + session type ("Canadian GP: Race"), live race countdown clock, lap counter ("Lap 21/68 (47 left)" — race only), and a track-status pill (Track Clear / Yellow Flag / Safety Car / Virtual SC / Red Flag) coloured to match MV.
  - 31 F1 country flag PNGs bundled under `picker/Assets/Flags/{iso2}.png` (~11 KB total), loaded as WPF Resources via pack URI, frozen on first load for cross-thread reuse.
  - Race countdown derives from `SessionInfo.EndDate` (with `GmtOffset` → UTC) minus MV's `Heartbeat.Utc` (replay-aware simulated time). MV's `ExtrapolatedClock.Remaining` sticks at `01:59:59` for the entire race — it's only useful for Practice / Qualifying countdowns, where this build still uses it.
  - 1 Hz parallel HTTP poll of `SessionInfo` + `TrackStatus` + `LapCount` + `ExtrapolatedClock` + `Heartbeat`. UI tick at 4 Hz extrapolates the clock between polls so seconds visibly count down.

### Changed
- **Picker driver position block restyled as a two-tone tile** matching MV's `[number][TLA]` layout: dark-grey rounded square holding the position number, then a 1 px seam, then the team-colour TLA tile. Replaces the single-tile design from v1.3.4. No behavioural change — purely visual to align with MV when the two apps are docked side-by-side.

## [1.3.8.1] — 2026-06-06

### Fixed
- **Picker `BEST` / `INT` / `LDR` now show segment-scoped values matching MV's cockpit, not all-qualifying values.** Vic caught this immediately after v1.3.8 shipped during Monaco Q3 2026: LEC was P10 in our picker with `BEST 1:12.774` (his Q2 PB) and `INT -0.988 / LDR +0.399`, but MV cockpit showed LEC `BEST 1:16.662` (his only Q3 lap) and `INT +2.650 / LDR +4.287`. The numbers and the position weren't telling the same story.

  Root cause: `picker/Services/LiveTimingClient.cs` was reading `TimingData.BestLapTime.Value` (correct — segment-scoped, matches what MV shows) but then immediately overwriting it with `TimingStats.PersonalBestLapTime.Value` (wrong — that's the best across Q1+Q2+Q3 combined). The override was originally added in v1.3.4 because MV's per-lap `OverallFastest` / `PersonalFastest` flags fade to false on subsequent ticks, so we cross-referenced TimingStats to keep the green/purple pill stable. But TimingStats has BOTH the value and the position, and we accidentally took both instead of just the position.

  Downstream effect: the v1.3.8 PB-differential gap calc (myPB − aheadPB / myPB − leaderPB) ran on the wrong value, so `INT` / `LDR` were computed against all-Q PBs across drivers. In a session like Q3 where some drivers' all-Q PB is from Q2 and others' is from Q3, the deltas mixed segments and produced numbers that looked plausible but matched nothing on screen.

  Fix: kept the TimingStats cross-reference for the PILL COLOR (`PersonalBestLapTime.Position` → green/purple) but stopped overwriting the displayed `bestLap` string. `TimingData.BestLapTime.Value` was right all along — it's segment-scoped per MV's design (verified live by curling MV for ANT/HAM/NOR/LEC: top-level `BestLapTime.Value` always equals `BestLapTimes[currentSegmentIndex].Value`, even when an earlier segment was faster). With the override gone, both the `BEST` cell text and the PB-diff `INT`/`LDR` calculations use the same segment-scoped reference MV does, so the picker matches the cockpit number-for-number.

  Plugin wheel HUD was unaffected because `MultiViewer/TimingDataDecoder.cs` reads `driver["BestLapTime"]["Value"]` directly and never had a TimingStats override — it was already segment-correct from v1.3.8 onward.

  Process learning: when adding a cross-reference field for "stable presentation" (like the v1.3.4 PB-color), only take the specific sub-field you need (Position), not the whole record. Filed for code-review checklist.

## [1.3.8] — 2026-06-06

### Fixed
- **Wheel HUD `INT` / `LDR` gap badges showed stale Q1 values for the rest of qualifying.** Caught by Vic at the Q2→Q3 break in Monaco: HAM picked, MV cockpit showed `INT +0.015` / `LDR +0.435` (the live Q2 PB-to-PB differentials), our wheel HUD showed `INT +0.147` / `LDR +0.484` (frozen Q1 values from ~25 minutes earlier). Side-by-side screenshots vs MV confirmed the picker also displayed the stale values.

  Root cause: in qualifying, MV's `/api/v1/live-timing/TimingData` returns empty strings for the top-level `GapToLeader` / `IntervalToPositionAhead.Value` fields — MV's SignalR feed only populates per-stat blocks in Q-mode. The v1.3.4 fix added a fallback that read `Stats[0].TimeDiffToFastest` / `TimeDifftoPositionAhead`, which works fine in Q1 because Stats[0] is the live Q1 segment. But the Stats array is one entry per Q segment, and **each segment freezes its values when the next segment begins** (Stats[0]=Q1 frozen, Stats[1]=Q2 frozen-just-now, Stats[2]=Q3 empty mid-session). So once Q2 started, every driver's wheel + picker INT/LDR was reading the frozen Q1 snapshot and never updated again — masquerading as "live" data for the rest of the session.

  Discovered the real MV cockpit formula by reverse-comparing values: HAM PB `1:12.934` − VER PB `1:12.499` = `+0.435`, exactly matching MV cockpit. MV doesn't display the Stats values at all in cockpit view — it computes PB-to-PB differential live from each driver's `PersonalBestLapTime`. Verified across 6 drivers; formula matches to 3 decimals.

  Fix: removed the `Stats[0]` fallback from both `MultiViewer/TimingDataDecoder.cs` (plugin, drives wheel HUD) and `picker/Services/LiveTimingClient.cs` (picker). When MV's top-level gap fields are empty, synthesise `(myPB − aheadPB)` for INT and `(myPB − leaderPB)` for LDR using the BestLapTime values that were already being parsed for the `BEST` column. Format `+X.XXX` / `-X.XXX` with three decimals, invariant culture so EU comma-decimals don't break parsing. Plugin computes per-driver in `FillAhead`/`FillLeader`'s post-pass; picker computes per-row in `ApplySnapshot` after position-sort. Em-dash fallback when even PB-diff can't be computed (driver hasn't set a flying lap yet).

  Plugin needs SimHub restart to load new DLL; picker auto-reloads when relaunched. Race / replay sessions are unaffected — MV populates the top-level fields there so the PB-diff branch never runs.

  Process learning: should have flagged the Stats[0] approach in v1.3.4 review — "always reads index 0" + "array indexed by Q segment" was a smell. Added a defensive comment block at the removed fallback site explaining why Stats[N] is fundamentally wrong for Q-mode, so the next person doesn't reintroduce it.

- **Picker now keeps `BEST` / `INT` / `LDR` visible when a driver is in pit, with `IN PIT` shown only on the `LAST` column** — matching MV Live Timing's behaviour. Symptom Vic flagged immediately after the Q-mode fix shipped: when a driver pitted (e.g. ANT for fresh tyres in Q3), our picker overlaid `IN PIT` on BOTH `INT` and `LDR` columns, hiding the gap reference. MV Live Timing only overlays `IN PIT` on `LAST` — it keeps `BEST`, `INT` and `LDR` visible so users still know how close the pitter is to the cars around them while he's stationary.

  Fix in `picker/Services/LiveTimingClient.cs ApplySnapshot`: removed the `else if (s.InPit) { row.GapToLeader = "IN PIT"; row.IntervalToAhead = "IN PIT"; }` block entirely. Added inverse: when `s.InPit`, override `row.LastLapTime = "IN PIT"` and `row.LastLapStatus = LapStatus.InPit` (new enum sentinel). MV's TimingData returns the actual last lap time for in-pit drivers (the lap they crossed the line on before pitting), not a literal `"IN PIT"` string, so we synthesise it client-side.

  Plumbed `LapStatus.InPit` through both pill converters: `LapStatusToBoxBackgroundConverter` now returns red (`#F44336`, Material UI red[500]) for InPit, and `LapStatusToBoxForegroundConverter` returns white. Reuses the existing `LAST` cell binding — no XAML changes needed. Net effect: `LAST` cell shows a red `IN PIT` pill, while `BEST` / `INT` / `LDR` show real values throughout the pit stop. Wheel HUD's `INT IN PIT` header (which describes the ahead car's pit state, not the picked driver's) is unchanged — that's separate dashboard logic and Vic uses it as a "the guy I'm chasing is stationary" cue.

## [1.3.7] — 2026-06-06

### Fixed
- **Wheel `INT` / `LDR` panels showed `---` after v1.3.6 ship — root cause was the 4 new lap-time properties were never registered with SimHub.** Symptom right after restarting SimHub on v1.3.6: the wheel HUD's `INT` and `LDR` panel centers showed `---` (the fallback string from the dashboard formula) instead of the leader's / ahead car's lap time. Sectors, gaps, and the `LAST` panel for the picked driver all kept updating correctly — only the two new lap-time panels were stuck on `---`. Confirmed via direct curl against MV's `/api/v1/live-timing/TimingData` endpoint that `Lines.<n>.LastLapTime.Value` and `Lines.<n>.BestLapTime.Value` are populated for every driver including the leader (e.g. leader had `LastLapTime: { Value: "1:12.499" }`), and via SimHub log inspection that the v1.3.6 DLL did load (`MultiViewer first snapshot received` at 11:59:45 right after the deploy at 11:55:18) and was polling TimingData every 1000 ms with no errors. So the data was flowing into the decoder fine.

  Root cause: SimHub's property model requires `PluginManager.AddProperty(name, type, initial)` to be called **once** at plugin `Init` to register a property in SimHub's metadata catalog. After that, `PluginManager.SetPropertyValue(name, type, value)` updates the value on subsequent ticks. If `SetPropertyValue` is called for a name that was never `AddProperty`'d, **SimHub silently no-ops** — the call doesn't throw, doesn't log, and the property never appears in the global `$prop()` namespace. The dashboard's `$prop('F1SimHubLivePlugin.LeaderLastLapTime')` then returns empty, and our defensive formula `(v && v != '') ? v : '---'` correctly falls back to `---`.

  v1.3.6 added the 4 new `SetProp` calls in `F1SimHubLivePlugin.cs OnTimingSnapshot` (lines 146–149: `AheadLastLapTime`, `AheadBestLapTime`, `LeaderLastLapTime`, `LeaderBestLapTime`) but **forgot to add the corresponding 4 `Register()` calls** in `Init()` — every other prop in the plugin has a `Register("X", initial)` at startup paired with a `SetProp("X", value)` per tick, and the new ones were missing the registration half. So every TimingData tick was calling `SetPropertyValue` on 4 unknown property names and silently dropping the writes.

  Fix: added 4 `Register` calls in `F1SimHubLivePlugin.cs Init()` (alongside the existing `LeaderCarNumber` / `AheadCarNumber` registrations) with initial value `""`. No decoder, dashboard, or telemetry changes needed — those were already correct in v1.3.6. Requires SimHub restart to load the new DLL.

  Process learning: the build pipeline does not have an end-to-end smoke test for "every SetProp has a matching Register". Should add a lint pass in `F1SimHubLivePlugin.cs` (or a build-time roslyn analyzer) that diffs `Register(` vs `SetProp(` name literals and fails the build if they don't match. Filed mentally as a v1.4.x cleanup.

- **Picker `📌 Pin` checkbox state now persists across launches.** Symptom Vic caught while testing v1.3.7's wheel fix: unchecking the `📌 Pin` toggle (which controls `Window.Topmost`) correctly let the picker drop behind other windows for the rest of the session, but closing and relaunching the picker put it back at the top with `📌 Pin` rechecked — every launch reset the user's choice. Root cause: `picker/MainWindow.xaml` hardcodes `Topmost="True"` on the `Window` element and `IsChecked="True"` on the `TopmostCheck` `CheckBox`, and the `TopmostCheck_Changed` handler only updated the live `Window.Topmost` property without writing the new state anywhere — there was no persistence layer at all for UI toggles, only for window geometry (`WindowGeometryStore` shipped in v1.3.3). So XAML's hardcoded defaults won on every cold start.

  Fix: added a new `picker/Services/WindowPreferencesStore.cs` that mirrors the `WindowGeometryStore` pattern but writes to a separate file (`F1SimHubLive.PickerPreferences.json` in `%APPDATA%\F1SimHubLive\`, alongside `F1SimHubLive.Settings.json` and `F1SimHubLive.PickerWindow.json`). Kept it as a separate file deliberately so a corrupted prefs blob can never wipe saved window geometry, and so future toggle additions (the `💡 LEDs` checkbox is the obvious next candidate) don't grow the geometry JSON schema. Persisted field is `Topmost: bool?` — nullable so we can distinguish "user hasn't toggled yet, use XAML default" (`null` → keep `IsChecked="True"`) from "user explicitly unchecked it last time" (`false` → uncheck). `MainWindow` ctor calls `WindowPreferencesStore.Apply(this, TopmostCheck)` right after `WindowGeometryStore.Apply`/`Attach`, which sets `TopmostCheck.IsChecked` from the saved value (which in turn triggers `TopmostCheck_Changed` via the existing `Checked`/`Unchecked` handlers, so the live `Window.Topmost` falls in line). `TopmostCheck_Changed` now calls `WindowPreferencesStore.Save(cb)` at the end so every toggle writes synchronously to disk — no debounce needed because clicks are sparse compared to drag events. Net result: uncheck Pin once, it stays unchecked across every future launch until you check it again.

- **Picker `INT` / `LDR` columns now show `—` for the leader and `IN PIT` for drivers in the pit lane** instead of stale or nonsensical gap values. Two coupled bugs caught the moment the wheel fix landed and Vic compared picker vs MV side-by-side. **Bug 1 — leader had populated INT / LDR:** in image 2 of the screenshot pair, ANT was P1 in the picker but the columns showed `INT +0.109` and `LDR +0.306`. Definitionally the leader has no INT (no car ahead) and no LDR (he *is* the leader), so any value in those columns for P1 is wrong. The bug is that MV does not always clear `GapToLeader` on the row that just became P1 — there's a tick or two of lag where the field still carries the previous-leader's gap, especially during live Q where session-best ordering reshuffles every few seconds. The decoder in `picker/Services/LiveTimingClient.cs` was passing through whatever MV returned, so the picker faithfully rendered that stale +0.306 for ~1 poll cycle (~500ms) every time the lead changed hands. Fix: force `IntervalToAhead = "\u2014"` and `GapToLeader = "\u2014"` (em dash, matching MV Live Timing's display convention) whenever `Position == 1`, before binding to the row. Defensive — doesn't trust MV to return empty for P1.

  **Bug 2 — pit-bound drivers showed stale gap values:** while watching ANT in pit at 10 km/h, his INT and LDR still showed the gap from his last lap crossing the line, which becomes increasingly misleading as the pit-lane delta (~20s) accumulates before his next valid measurement. The wheel HUD already got an `IN PIT` indicator for the leader/ahead car as part of this v1.3.7 ship (see Added section below), but Vic noticed the picker had no equivalent — the wheel and picker were inconsistent. Fix: when `InPit == true` and the driver is not P1 (P1 wins precedence — leader always shows `—`), override INT and LDR to display `IN PIT`. The pit count badge in column 7 is unchanged — that still shows the cumulative count.

  Precedence order applied in `picker/Services/LiveTimingClient.cs` (kept in deliberate sync with the wheel HUD dashboard formulas — if you change one, change both): (1) `Position == 1` → both columns show `—`, (2) `InPit` → both columns show `IN PIT`, (3) otherwise → raw gap string from MV's `GapToLeader` / `IntervalToPositionAhead.Value` (with the `Stats[0].TimeDiffToFastest` / `TimeDifftoPositionAhead` fallback for live FP/Q sessions, which is the v1.3.4 fix).

### Changed
- **Picker driver-row UI rewritten to match MultiViewer Live Timing conventions — bigger fonts, F1-standard sector colors, "IN PIT" red box, PB/SB lap-time pills.** Symptom Vic called out comparing picker vs MV side-by-side during live qualifying: (a) sector times were all rendered in white, no PB/SB color coding to distinguish a freshly-improved sector from a stale one — MV uses yellow / green / purple, F1's official timing convention; (b) `LAST` / `BEST` lap-time text was 12pt with subtle color shifts that Vic couldn't read at the wheel — MV uses larger, bolder, colored *background pills* that pop visually when a driver sets a PB or SB; (c) `IN PIT` was white text on dark background, easy to miss — MV uses a vivid red box that's recognizable across the room. Combined effect: the picker looked technically correct but lacked the at-a-glance readability of real F1 timing screens.

  Rather than guessing at colors and sizes from screenshots, mined the actual MultiViewer 2.7.3 Electron app's `app.asar` bundle (`C:\Users\vics\AppData\Local\multiviewer\app-2.7.3\resources\app.asar` extracted with `npx @electron/asar extract` to `$env:TEMP\mv-asar-peek`) and grep'd the renderer webpack bundle for the timing component. Found the canonical `TimingValue` styled component in `.webpack/renderer/main_window/index.js` at offset ~1.99 MB — it exports the exact color map MV uses for every status: `personalFastest: green[500]` (`#4CAF50`), `overallFastest: purple[500]` (`#9C27B0`), `notImproved: yellow[600]` (`#FDD835`), `inPit: red[500]` (`#F44336`), `pitOut: red[800]`, `stopped/retired/knockedOut: red[500]`. The component's render branches on type: for `sector` / `best-sector` the status is rendered as **text color only**, but for everything else (`lap-time`, `gap`, `interval`) the status becomes the **background fill** with `e.palette.getContrastText(...)` on top. Pills use `borderRadius: 15px`, `padding: theme.spacing(0,1)` (= `0 8px`), `fontWeight: bold`, `whiteSpace: nowrap`. Underlying `body1` typography from MV's `darkTheme` (offset ~7.00 MB) is `fontSize: "14px", letterSpacing: "-0.05px"` — so every visible timing field in MV is 14px bold; no exceptions.

  Translated each piece into matching WPF converters in `picker/Services/`: `SectorStatusToBrushConverter.cs` returns yellow / green / purple for sector text color (defaulting to yellow, not white — MV's convention); `LapStatusToBoxBackgroundConverter.cs` and `LapStatusToBoxForegroundConverter.cs` return the pill background brush (transparent / green / purple) and contrast text (default light gray / BLACK on green / WHITE on purple) — kept as two separate converters bound to the same `LapStatus` so the XAML can wrap each lap-time `<TextBlock>` in a `<Border Background=... Foreground inherits via TextBlock>` pair without code duplication. For the pit indicator, added `InPitTextConverters.cs` containing `InPitTextToBackgroundConverter` (red `#F44336` when the bound value equals `"IN PIT"` exactly, transparent otherwise) and `InPitTextToForegroundConverter` (white on red, default gray otherwise) — binding off the literal string instead of a status enum because `LiveTimingClient.cs` already injects `"IN PIT"` into `IntervalToAhead` / `GapToLeader` directly for pit-bound non-leaders (the v1.3.7 fix above), so no model changes were needed.

  XAML wiring in `picker/MainWindow.xaml`: wrapped the four lap-time `<TextBlock>` elements (LAST, BEST, INT, LDR) inside `<Border CornerRadius="8" Padding="6,0" HorizontalAlignment="Right" VerticalAlignment="Center">` — radius 8 (not MV's 15) because picker rows are still roughly two-thirds the height of MV's full-screen rows so the pill stays proportional. Bumped all four to `FontSize=14 FontWeight=Bold` to match MV's `body1`. To make room for MV-matching font sizes everywhere (Vic: "the closest we can be with them, the better"), bumped the driver-row Grid `Height` from 46 → 56, the team-color name strip Height 18 → 22, the speed readout 17→20 with `LineHeight=22`, the tire-compound circle 28x28 → 34x34 (radius 14→17, letter 13→15, tire-age label 9→10), and the PIT-count number 14→16 with `LineHeight=18`. With the extra row height, sector text was promoted to MV's full 14px (current sector) and 12px (driver's best for that sector) — no more compromise for column-width constraints. All six sector text bindings switched from the old `LapStatusToBrush` converter (which defaults to white) to the new `SectorStatusToBrush` (defaults to yellow), so a regular completed sector now reads yellow instead of disappearing into the dark background, a new PB sector is green, and an overall session-best sector is purple.

  Process learning: stop guessing at hex codes from screenshots. MultiViewer's main app is closed-source on GitHub (their `github.com/multiviewer` org is just utility forks — shaka-player, plug_cloudflare, an empty `open-data` repo) but the Electron renderer source is one `asar extract` away. For any future "make picker look like MV" work, the workflow is: extract `app.asar`, grep `.webpack/renderer/main_window/index.js` for the component name or a literal string (`"IN PIT"`, `"PIT OUT"`, `personalFastest`, etc.), pull the styled-component definition, and translate the Material UI tokens 1:1. Faster, more accurate, and doesn't require Vic to send screenshots back and forth across testing sessions. Extraction temp folder is ~150MB and can be `Remove-Item -Recurse -Force` at session end. Caught while debugging the v1.3.6 → v1.3.7 fix: Vic's MV Live Timing screenshot showed both VER (P1) and ANT (P2) as `IN PIT` in their LAST column, but the wheel HUD has no equivalent indicator for the leader / ahead car — it was about to show their last lap time even while they were stationary in their pit boxes. That makes the pace comparison misleading (a car in the pit is not running at the lap time being shown — that's their *previous* outlap or in-lap time). Added two new plugin properties (`AheadInPit`, `LeaderInPit`) fed from MV's `Lines.<n>.InPit` boolean (same field the picked-driver `InPit` already uses), wired through `TimingSnapshot.cs`, `TimingDataDecoder.cs` `FillAheadSectors`/`FillLeaderSectors`, plus `Register` + `SetProp` in the plugin (avoiding the v1.3.6 omission). Dashboard formulas now short-circuit: `BehindRank` (LDR center) returns `'IN PIT'` when `LeaderInPit` is true and the picked driver is not the leader; `AheadRank` (INT center) returns `'IN PIT'` when `AheadInPit` is true and the picked driver is not the leader. The colored gap badges to the right of each panel are unchanged — they continue to show the gap value (which MV may still report while a car is in the pit, or may go empty depending on session shape). Order of precedence in each formula: `LEADER` literal (picked driver is P1) → `---` (no peer to compare) → `IN PIT` (peer is stationary) → lap-time string → `---` fallback.

## [1.3.6] — 2026-06-06

### Fixed
- **Wheel dashboard: `INT` and `LDR` panels now show the leader's and ahead car's actual lap times instead of duplicating the gap.** Symptom in v1.3.5 (PR #21, commit 579fd96): after rebinding the `INT`/`LDR` panel centers away from the picked driver's own `BestLapTime`/`LastLapTime`, they then showed `IntervalToAhead` (`+0.147`) and `GapToLeader` (`+0.484`) — which is the *exact same value* already rendered in the colored badge to the right of each panel. So the panel center and the right-side badge were both displaying the gap, twice. Vic: "we already had that number on the right of those boxes ... LDR should say 1:13.293 instead 0.484 ... since I already see .484 on the right of that." What he wants instead: the **leader's actual last lap time** in the LDR panel center (so he can see what pace the front of the field is running) and the **car-ahead's actual last lap time** in the INT panel center (so he can see what pace the next car up is running). The `LAST` panel under the @vicslive signature already shows his own last lap, so the wheel HUD becomes a three-way pace comparison: my last lap vs. ahead's last lap vs. leader's last lap. The colored gap badges on the right of each panel are unchanged (they still show the gap, formatted via `FormatString:"0.0"`), and the `GAP +0.484` label inside the `LAST` panel is also unchanged (`LapsVal` element still bound to `GapToLeader`).

  Implementation required new plugin properties because the existing `Leader*` and `Ahead*` props only covered sectors and car numbers — not lap times. Added 4 new strings to `Telemetry/TimingSnapshot.cs`: `LeaderLastLapTime`, `LeaderBestLapTime`, `AheadLastLapTime`, `AheadBestLapTime`. Extended `MultiViewer/TimingDataDecoder.cs` `FillAheadSectors` and `FillLeaderSectors` to also read `d["LastLapTime"]?["Value"]` and `d["BestLapTime"]?["Value"]` from the MV TimingData JSON (same shape that's used for the picked driver). Added 4 `SetProp` calls in `F1SimHubLivePlugin.cs` `OnTimingSnapshot` to surface the new props to SimHub. Rebound `BehindRank` Expression in `F1RaceSim_GSIFPEV2.djson` to `$prop('F1SimHubLivePlugin.LeaderLastLapTime')` (still returns `'LEADER'` when our driver is P1, `'---'` when empty) and `AheadRank` Expression to `$prop('F1SimHubLivePlugin.AheadLastLapTime')` (returns `'---'` when our driver is P1 or when empty). `BestLapTime` variants are exposed as props for future use but not currently bound in the dashboard — keeping LAST consistent across all three panels matches Vic's mental model (compare current-lap pace, not session-best). Requires SimHub restart to load the new plugin DLL + dashboard template.

## [1.3.5] — 2026-06-06

### Changed
- **Driver row layout: tighter rows, no dead space, sector bars uniform across S1/S2/S3.** Three coupled changes:
  1. **Row height 72 → 46 px.** The old row had a lot of blank space above and below the content because the tallest element (driver name + team name stack at FontSize 14/10) only needed ~30 px and the row was padded to 72 anyway. Tuned the row down to ~46 px so it just fits the tire badge stack (28 px badge + 1 px gap + ~10 px L# text ≈ 40 px) — Vic explicitly wanted the row to match the tire stack's natural footprint. Font sizes scaled proportionally so nothing clips: Position 22→18, TLA 20→16, name 14→13 / team 10→9, speed 22→17 / km/h 9→8, LAST/BEST 13→12, INT/LDR 12→11, PIT 16→14, sector times 10→9. Tire badge shrunk a hair (30→28 px) to keep its head-room in the new row height. Net effect: roughly 1.5× more drivers fit in the same window height with no readability loss.
  2. **Driver-name column: `*` → `Auto` (synced via `SharedSizeGroup`), sector strip column: `200 px` → `* (MinWidth=260)`.** The driver name column used to be a star sized so it absorbed all extra horizontal space — which is exactly what created the dead zone Vic circled between "Antonelli / Mercedes" and the speed number. Inverted the responsibility: name shrinks to fit content, the sector strip absorbs slack instead. To avoid each row sizing its name column independently (which would shift downstream Speed / LAST / INT columns left/right depending on whether the local driver was "Perez Cadillac" or "Antonelli Mercedes"), the name column uses `SharedSizeGroup="NameCol"` with `Grid.IsSharedSizeScope="True"` on the `DriverList` ItemsControl, so every row's name column ends at the same x position (= max name+team width across all visible drivers). Result: zero dead space when names are short across the board, perfect vertical alignment of Speed / LAST / BEST / INT / LDR columns regardless of which driver is in which row. As a bonus, dragging the window wider now grows the sector mini-bars instead of growing nothing-useful, so wider windows actually look better.
  3. **Per-sector sub-column widths weighted by `SegmentCount`.** The three sectors inside the sector strip used to split their available width as `*, *, *` (equal). On Imola Vic noticed S1 (3 mini-sectors) had visually fatter bars than S2 (7 mini-sectors) because the same column width was divided among more segments in S2. Added a new `CountToGridStarConverter` and a `SectorView.SegmentCount` INPC property that fires on every `Segments` collection change, then bound each sub-column's width to its sector's segment count. Now S1's three bars and S2's seven bars are the same pixel width (the sector with more mini-sectors just gets a proportionally wider column), matching what the user would intuitively expect when comparing the three sectors at a glance. Pre-race sectors with zero segments collapse to a star-weight of 1 (placeholder) instead of zero-width so the layout doesn't visibly jump when the first lap completes.

  Implementation: `picker/MainWindow.xaml` driver-row template revamp (column widths, font sizes, sector sub-column bindings); `picker/Models/DriverTimingRow.cs` adds `SectorView.SegmentCount` getter + collection-change subscription; new `picker/Services/CountToGridStarConverter.cs` returns a star-sized `GridLength` from an int.

## [1.3.4] — 2026-06-06

### Fixed
- **Picker LED bar now lights at the same RPM thresholds as the physical wheel.** Symptom: when comparing the on-screen picker LED strip to the actual SimHub-driven wheel LEDs during driving, the picker was consistently one LED behind — by the time the wheel had 10 LEDs lit, the picker only showed 9. Root cause: the picker was using a uniform spread (1/14 ≈ 7.14% per LED), but the F1 wheel device JSON in SimHub drives its 14-LED bar through three independent `CustomGradient` rules with non-uniform per-segment lengths — green (5 LEDs covering 0–30% of `RpmShiftPercent`, so 6% per LED), blue (5 LEDs covering 30–63%, so 6.6% per LED), red (4 LEDs covering 63–93%, so 7.5% per LED). With uniform spread, the picker's last LED only lights at ~93% RPM while the wheel's last LED lights at 85.5% — that 7.5%-point gap is the visible "one LED away" offset Vic noticed.

  Fix: replaced the on-the-fly `(i + 0.001) * 100 / LedCount` threshold computation with a hardcoded `LedLightThresholdsPercent[]` array containing the exact per-LED percentages copied from the wheel's CustomGradient rules — `{0.001, 6, 12, 18, 24, 30, 36.6, 43.2, 49.8, 56.4, 63, 70.5, 78, 85.5}`. Both ends still gate correctly: at idle (`RpmShiftPercent` ≤ 0) the strip stays fully dim because LED 1 needs > 0.001%, matching the wheel's `EnabledFormula: > 0` gate; at redline (`RpmShiftPercent` ≥ 85.5%) all 14 LEDs light, matching the wheel's full-bar visual. The colors per segment (5 green, 5 blue, 4 red) didn't change. Verified by reading `SimHub\PluginsData\Common\Devices\<wheel-guid>\settings.json` directly and matching threshold-by-threshold. (`picker/MainWindow.xaml.cs`.)

  Caveat: these thresholds assume the F1 wheel device config Vic uses (the one whose three RpmShiftPercent CustomGradient rules cover 0/30/63/94 boundaries). Anyone running a different SimHub LED config would still get the old uniform spread off-by-one feel — eventually the picker should read the wheel's actual rules instead of hardcoding them, but that's a v1.4 problem (the device JSON is a 1.4 MB blob with 1442 rules — a lot of work for a niche secondary-display app, and the current values are correct for the canonical F1 wheel which is the only device this picker has ever been tested against).

## [1.3.3] — 2026-06-06

### CI / build
- Bumped release-workflow action pins to their Node 24-native majors ahead of GitHub's 2026-06-16 deprecation of Node 20 actions: `actions/checkout` v4→v5, `actions/setup-dotnet` v4→v5, `azure/login` v2→v3, `softprops/action-gh-release` v2→v3. All four v3/v5 releases are pure runtime swaps with no public-API breakage for our usage (federated identity, `allow-no-subscriptions`, `files:` glob, `body_path`, etc. all still work). `windows-latest` runner image also redirects to `windows-2025-vs2026` on 2026-06-15 — no workflow change needed for that, but documenting here in case a future tag-build regresses unexpectedly.

### Added
- **Picker now remembers its window position, size, and maximized state across launches.** Drag the picker to your secondary monitor, resize it to a tall slim list, close it — next launch comes back exactly where you left it. Implemented as a small per-user JSON file (`F1SimHubLive.PickerWindow.json`) in the same `%APPDATA%\F1SimHubLive\` folder as the main settings; deliberately kept separate from `F1SimHubLive.Settings.json` so window-geometry writes (which happen any time the user drags or resizes) never trigger the plugin's settings-file watcher and force a needless reload. Multi-monitor safety: on launch we verify the saved rect would land at least 120x80 pixels on the currently-attached virtual screen — if you unplug the monitor the picker was last on, the saved geometry is silently discarded and the picker falls back to its default placement instead of opening off-screen with no easy way to drag it back. Maximized windows save their underlying `RestoreBounds` (so un-maximizing on the next launch reveals the right size) plus the maximized state itself (so it comes back maximized on the next monitor too). Windows does not provide per-app window memory automatically — every WPF app has to serialize its own geometry, which is what this adds.

  Save is **continuous and debounced** rather than only-on-close: subscribing to `LocationChanged`, `SizeChanged`, `StateChanged`, and `Closing` and writing through a 500 ms `DispatcherTimer`. First cut hooked only `Closed` and the file never appeared in practice — turned out the picker's `Closed` handler runs after several other disposables that can throw, and SimHub also terminates the child picker on its own shutdown without giving WPF a chance to fire `Closed`. Continuous save makes the persistence path independent of the close path: the latest geometry is on disk within half a second of the user letting go of the mouse, so a process kill loses at most ~500 ms of position data. (`picker/Services/WindowGeometryStore.cs`, wired into `MainWindow` ctor via `Apply()` + `Attach()`.)

### Fixed
- **Picker INT and LDR columns now populate during live Practice and Qualifying.** The v1.3.2 fix correctly switched the picker (and the plugin's `TimingDataDecoder`) to MV's race / replay payload shape (`GapToLeader` top-level string + `IntervalToPositionAhead.Value` nested object). That shape is what MV exposes for races AND for replays of any session type — including Q replays — because the replay layer reconstructs those fields. But MV's **live** SignalR feed for FP / Q sessions does not populate those two fields at all (they're either absent or empty), so live qualifying showed blank INT / LDR columns again, even though the same session worked perfectly on replay. Discovered ~30 minutes into live Q on 2026-06-06.

  In the live FP / Q payload, gap data lives inside the per-stint `Stats[]` array — specifically `Stats[0].TimeDiffToFastest` (= gap to the fastest lap of the session, our LDR equivalent) and `Stats[0].TimeDifftoPositionAhead` (= gap to the driver immediately ahead by best lap, our INT equivalent). Note MV's spelling: lowercase `t` in `TimeDif`**`f`**`to`**`P`**`ositionAhead` is **not** a typo in this codebase — that is the actual field name returned by MV.

  Fix: when `GapToLeader` or `IntervalToPositionAhead.Value` come back empty, fall back to `Stats[0].TimeDiffToFastest` / `Stats[0].TimeDifftoPositionAhead`. Applied symmetrically in both `picker/Services/LiveTimingClient.cs` (the picker's polling parser) and `MultiViewer/TimingDataDecoder.cs` (the plugin's per-driver decoder so SimHub dashboards see the same value). The fastest driver still gets `""` from MV in either shape, which the picker continues to render as `LDR`, so the "leader" badge keeps working without special-casing.

## [1.3.2] — 2026-06-06

### Fixed
- **Picker INT and LDR columns now show real values** instead of blank labels. The poll loop was reading from MV property names that don't exist on the public ``/api/v1/live-timing/TimingData`` payload (``TimeDiffToFastest`` / ``TimeDiffToPositionAhead`` — those are signalr-internal). Switched to the actual payload shape: ``GapToLeader`` (top-level string like ``"+9.322"``, ``"1 L"`` for lapped cars, ``""`` for the leader) and ``IntervalToPositionAhead.Value`` (nested object). Renamed the column label from ``GAP`` to ``LDR`` to match MultiViewer's terminology — INT = interval to the car ahead, LDR = gap to the leader. Visible immediately on any session, live or VOD.

## [1.3.1] — 2026-06-06

### Fixed
- **Picker now refreshes driver team info when MultiViewer switches sessions.** `DriverTimingRow.{Tla, LastName, TeamName, TeamColour}` were declared as `{ get; init; }` (C# init-only), so once a row was created the team paint and TLA were locked in. The polling loop refreshed `_drivers` from MV's `/DriverList` every 30 seconds (correctly), and re-used existing rows by `RacingNumber` (correctly), but only updated the *mutable* fields (Position, LastLap, gap, sectors, tyre, pit count) — the language wouldn't let it touch team info even though the latest snapshot had the right values sitting right there. Most visible symptom: load a 2020 race replay in MV after the picker was already running and Hamilton would still show as Ferrari (his 2026 team) with the wrong livery colour, while drivers who only exist in one era (Räikkönen, Grosjean, Latifi for 2020; Bortoleto, Antonelli for 2026) showed correctly because they were brand-new rows created with the right team info from their very first poll. Fix: convert those four fields to mutable INPC properties (`RacingNumber` stays `init` — it's the dictionary key) and add a "refresh from snapshot" pass in the row-exists branch of `ApplySnapshot`. Identity diffs only fire `PropertyChanged` when a value actually changes, so the steady-state cost is zero. WPF repaints just the affected cells via INPC.

## [1.3.0] — 2026-06-06

### Changed
- **🎉 Picker no longer triggers a UAC prompt on launch.** Manifest changed from `requireAdministrator` to `asInvoker`. The reason the old manifest existed was that the picker writes `DriverNumber` (and any other field it touches) to `F1SimHubLive.Settings.json`, which lived under `C:\Program Files (x86)\SimHub\` — an admin-only path. Net effect for users: every picker launch popped a UAC dialog, every `AutoLaunchPicker = true` session popped UAC on SimHub start, and the Start Menu shortcut inherited the requirement. Annoying enough that the v1.1.0 changelog explicitly flagged it as the reason `AutoLaunchPicker` defaulted to off.
- **Settings file moved to per-user location.** `F1SimHubLive.Settings.json` now lives at `%APPDATA%\F1SimHubLive\F1SimHubLive.Settings.json` (typically `C:\Users\<you>\AppData\Roaming\F1SimHubLive\`). Both the plugin and the picker resolve the path through a shared `SettingsPathResolver` so they never disagree on where the file is. Writes happen in user space — no admin needed.
- **Automatic one-shot migration from v1.2.x.** On the first run of the picker or the plugin after upgrade, the resolver checks for a legacy file at `C:\Program Files (x86)\SimHub\F1SimHubLive.Settings.json` and, if found, byte-copies it to the new per-user path. Every user customization — `DriverNumber`, `RpmShiftLightStartRpm`/`EndRpm`, `OutputHz`, `RenderDelayMs`, `MultiViewerBaseUrl`, `AutoLaunchPicker`, etc. — is preserved. The legacy file is **never deleted** (no admin available); it's left in place but never read again. Users who want a perfectly clean uninstall can delete it manually.
- **Installer now writes seed config to `%PROGRAMDATA%\F1SimHubLive\`**, not to `Program Files (x86)\SimHub\`. The installer runs elevated, but the *real* user's APPDATA is not reliably writable from an elevated process (the elevating admin could be a different account than the desktop user). Per-machine PROGRAMDATA is the right intermediary: installer-writable, user-readable. On first picker / plugin run, the resolver copies the PROGRAMDATA seed into the user's APPDATA. `Deployer.WriteSettings` also preserves user values from three candidate locations — APPDATA → PROGRAMDATA → legacy Program Files — so re-installing v1.3.x never blows away a hand-tuned config.
- **`AutoLaunchPicker = true` is now fully unattended.** Previously you'd get a UAC dialog every time SimHub started. With v1.3.0 it just opens. Safe to leave on permanently. The XML doc on `AutoLaunchPicker` in `Settings.cs` is updated to reflect this.
- **Documentation refresh** for the per-user move: README's settings table, troubleshooting section, and file-layout tree all reflect the new path; PICKER.md's privileges callout flips from "runs as administrator (UAC prompt on launch)" to "runs as `asInvoker` — no UAC prompt, ever"; troubleshooting's "click doesn't change the wheel" entry updated to point at `%APPDATA%\F1SimHubLive\` and call out Controlled Folder Access as a more likely culprit than a permission error; new "stale settings after upgrading from v1.2.x" entry explains the automatic migration.

### Added
- **`SettingsPathResolver`** — single source of truth for "where is `F1SimHubLive.Settings.json`?" Mirrored in `picker/Services/SettingsPathResolver.cs` (picker, `net8.0-windows`) and `SettingsPathResolver.cs` at repo root (plugin, `net48`). The two copies share identical logic but can't be a shared csproj — the picker is `net8.0-windows`/WPF and the plugin is `net48`/SimHub plugin DLL, completely different dependency trees. Both files have a header comment flagging the duplication and reminding maintainers to keep them in sync.

## [1.1.3] — 2026-06-05

### Added
- **New `RpmShiftPercent` property for Ferrari-realistic LED behavior.** The original `RpmPercent` normalizes raw RPM over a fixed 13,000 ceiling — perfectly fine, but mismatched to what real F1 wheel LED bars actually do (greens visible while rolling out of the pit lane at 5–7K RPM, full bar at fast-corner peaks around 11.5K). When watching an onboard camera, viewers see Hamilton's Ferrari wheel light up greens during out-laps but their own SimHub wheel sits dark until ~10K RPM — the plugin was publishing the right RPM, but the percent normalization was pessimistic. `RpmShiftPercent` rescales RPM linearly between two new settings (`RpmShiftLightStartRpm` default 5500, `RpmShiftLightEndRpm` default 11500) into 0–100. Bind your wheel device's LED `ValueFormula` / `EnabledFormula` to `F1SimHubLivePlugin.RpmShiftPercent` instead of `F1SimHubLivePlugin.RpmPercent` to get the real-F1-wheel curve. Both new settings hot-reload on save and are preserved across installer upgrades. The legacy `RpmPercent` property is unchanged — existing LED configurations continue to behave identically.

## [1.1.2] — 2026-06-05

### Fixed
- **🔥 Installer was shipping a stale plugin DLL with no picker code.** `installer/F1SimHubLive.Installer.csproj` embedded `installer/Assets/F1SimHubLive.dll` as a checked-in static binary — that binary was last refreshed at the initial commit (`dc92d6b`, FileVersion 1.0.0.0, 62,976 bytes) and never rebuilt afterward. When the picker feature shipped in v1.1.0 (`660e70f`), the plugin source got `MaybeLaunchPicker` and `AutoLaunchPicker` but the embedded binary did not — so every v1.1.0/v1.1.1 official installer dropped a pre-picker plugin DLL into SimHub. Setting `AutoLaunchPicker: true` was a silent no-op because the code that reads it didn't exist in the shipped binary. Local dev was masked by `scripts/deploy.ps1` (auto-called by the plugin csproj after Release builds), which copied `bin\Release\F1SimHubLive.dll` directly into SimHub and bypassed the installer entirely. Fix: new `PublishPlugin` MSBuild target in `installer/F1SimHubLive.Installer.csproj` (mirrors the existing `PublishPicker` target — `BeforeTargets="ResolveReferences;PrepareResources;CoreCompile"`) runs `MSBuild ..\F1SimHubLive.csproj -t:Restore;Build -p:Configuration=$(Configuration);DeploySimHub=false` and copies the fresh `..\bin\$(Configuration)\F1SimHubLive.dll` over `installer\Assets\F1SimHubLive.dll` before the EmbeddedResource pipeline reads it. Self-contained, works locally and in CI, no `release.yml` changes needed. Adds ~3 sec to installer builds; net-positive every release. Verify post-install: `(Get-Item 'C:\Program Files (x86)\SimHub\F1SimHubLive.dll').VersionInfo.FileVersion` should now match the release tag.
- **Installer was overwriting user-tunable `settings.json` values on every upgrade.** `Deployer.WriteSettings` rebuilt the file from installer-UI inputs only, blowing away any field the user (or the picker) had hand-tuned between installs. Most visible symptom: `AutoLaunchPicker: true` reverted to `false` on every upgrade, so even users who'd explicitly enabled picker auto-launch lost it. Fix: before writing, attempt to read existing `settings.json` and preserve `AutoLaunchPicker`, `OutputHz`, and `RenderDelayMs` if present. Parse failures fall through to fresh defaults (non-blocking, logged). Installer-UI-driven fields (`DriverNumber`, `Source`, `MultiViewer*`) still overwrite — those are explicit choices the user just made in the wizard. New log line documents which preserved values survived the install.
- **Picker driver number and points were nearly unreadable** — racing-number text was `#3C3C48` (RGB 60,60,72 — only ~23% luminance) on a dark row background, and the "X pts" suffix was `#6F6F7C` (~43%, 9pt). On the current-driver highlighted row the contrast was the same. Bumped racing number to `#E8E8EE` (~91% luminance, still visually subordinate to the bold-white driver name) and points to `#B8B8C4` (~72%, 10pt — one pt larger too). Both now meet WCAG AA contrast against the dark row backgrounds; readable at a glance during a race without breaking the row's visual hierarchy.

### Fixed
- **MultiViewer mode now resolves driver identity, track status, and lap count during practice / qualifying sessions.** `SessionDataLoopAsync` was fanning out four endpoint polls (`LapCount`, `TrackStatus`, `ExtrapolatedClock`, `SessionData`) via a single `Task.WhenAll` and reading `.Result` after — pattern that throws an `AggregateException` if **any one** task fails. In live practice / qualifying, MV returns 404 "No data found, do you have live timing running?" for `LapCount` (practice has no lap count), which tripped `Task.WhenAll`, jumped to the loop's catch block, and **skipped the DriverList fetch, TrackStatus decoding, and SessionSnapshot emission entirely**. The wheel-area title sat on the "F1 LIVE" fallback (`DriverLastName` empty), the track-status indicator stayed blank, the "P 14/22" display stayed at 0/0. Symptom only surfaced in live FP/quali because recorded race replays serve all four endpoints with 200. Fix: new `SafeGetString` helper awaits each task individually and returns `""` on failure with a one-shot log. Downstream decoders (`LapCountDecoder`, `TrackStatusDecoder`, `ExtrapolatedClockDecoder`, `SessionDataDecoder`) already handle empty input gracefully (return zero/default), so the per-endpoint 404 now leaves only that piece of the snapshot blank instead of killing the whole emit. CarDataLoop and per-driver TimingDataLoop are unaffected — they were already single-endpoint per loop.
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