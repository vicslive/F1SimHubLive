# Replay (the 3rd source) & driver-picker behaviour — read before touching replay or row identity

F1SimHubLive has **three interchangeable telemetry sources**, all behind one `ITelemetrySource` interface so the wheel + dashboard never know which is feeding them:

| Source | File | Needs | Notes |
|---|---|---|---|
| **MultiViewer API** | `MultiViewer/MultiViewerHttpClient.cs` | MV running + Live Timing streaming | The original source. Polls MV's localhost API. |
| **Live SignalR** | `F1Signalr/…` | a live F1 session | Connects straight to F1's live-timing SignalR feed. |
| **F1Replay (on-demand VOD)** | `F1Replay/F1ReplayClient.cs` | **nothing** — no MV, no F1 TV sub, no live session | Plays a past session from F1's **public live-timing static archive**. |

This doc covers the **replay source** and the **driver-picker behaviour that the replay option exposed/changed**. For the clock/countdown in replay vs live, see [CLOCKS.md → Live vs replay](CLOCKS.md). For sector colours, see [SECTORS.md](SECTORS.md).

---

## How the replay source works

`F1ReplayClient` replays a recorded session by driving the **same `ITelemetrySource` events** the live SignalR and MV sources emit — so the wheel, dashboard, and picker render identically regardless of source.

- **Virtual clock:** a ~60 Hz tick (`TickMs = 16`) advances `Position` by *wall-elapsed × speed* while playing, emitting every timeline event it crosses. Seeking jumps `Position`; pausing freezes it.
- **Wall-clock stamping:** emitted `DriverSnapshot`s are stamped with the **current wall clock** (not the archive's original timestamp) so the 60 Hz interpolator brackets them exactly as it does for a live feed. This is why the clock playhead (freshest CarData frame `Utc`) advances at 1× during replay — see CLOCKS.md.
- **Source archive:** F1's public live-timing static archive (`ArchiveClient`) — no MultiViewer dependency and no F1 TV subscription required to replay.

---

## Trap #1 — replay carries only CarData + DriverList, so timing columns are BLANK by design

**The replay topic set is `CarData` + `DriverList` only.** That is enough for driver **identity** (number, TLA, name, team, colour) and **car telemetry** (RPM, speed, gear, throttle), but it carries **no `TimingData` / `TimingStats`**. So in replay (Phase 1):

- ✅ shown: identity, team livery, RPM / speed / gear / throttle.
- ⬜ blank **on purpose**: position, gaps, intervals, sector times/colours, lap times, tyre compound/age, pit count.

This is not a bug — a blank gap/sector column in replay means "this phase doesn't subscribe `TimingData`," not "the decoder broke." A future phase can light those columns up by adding the `TimingData` topic to the replay subscription. *(`F1Replay/F1ReplayClient.cs` — `ReplayGridRow` has identity + car fields only, with a comment to this effect.)*

**Rule: before debugging "missing timing in replay," confirm whether the field comes from `TimingData` (expected blank) vs `CarData`/`DriverList` (should be populated).**

---

## Trap #2 — `{ get; init; }` row identity froze team info across a session change (Hamilton-as-Ferrari)

The most visible driver-picker regression the replay option exposed. `DriverTimingRow.{Tla, LastName, TeamName, TeamColour}` were declared **`{ get; init; }`** (C# init-only), so once a row existed those fields were **locked**.

- The poll loop refreshed `_drivers` from MV/`DriverList` and correctly re-used existing rows by `RacingNumber`, but the language wouldn't let it touch the init-only identity fields — so it only updated the *mutable* timing fields.
- **Symptom:** load a **2020 race replay** in MV while the picker is already running, and Hamilton still shows as **Ferrari** (his 2026 team) with the wrong livery, while drivers who only exist in one era (Räikkönen/Grosjean/Latifi for 2020; Bortoleto/Antonelli for 2026) render correctly — because they were brand-new rows created with the right info on their first poll.

**Fix:** convert `Tla` / `LastName` / `TeamName` / `TeamColour` to **mutable INPC properties** and add a "refresh from snapshot" pass in the row-exists branch of `ApplySnapshot`. `RacingNumber` stays `init` (it's the dictionary key). Identity diffs only fire `PropertyChanged` when a value actually changes, so steady-state cost is zero. *(`picker/Models/DriverTimingRow.cs`, `LiveTimingClient.ApplySnapshot`)*

**Rule: any field that can differ between sessions/eras (identity, livery) must be a mutable INPC property and be refreshed on every snapshot. Only the immutable key (`RacingNumber`) may be `init`.**

---

## Trap #3 — the clock playhead must NOT reset when you switch drivers

Switching the picked driver must never blank or jump the session clock. The clock playhead is **driver-independent** (`_playheadUtc` / `LatestCarDataUtc`, the freshest CarData frame across **all** cars) and is never reset on a driver switch. Never wire the clock to `_lastEmittedUtc` (the per-driver, forward-only dedup cursor that `SetDriverNumber` resets to `MinValue`). This is **CLOCKS.md Trap #2** — see it for the full story; it lives here too because it's a driver-picker behaviour.

---

## Trap #4 — the per-driver cluster swallowed row clicks (driver switch silently failed)

The picker's driver row is **clickable to switch drivers**, and the per-driver input cluster (gear ring / throttle arc) sits inside that clickable area. Without `IsHitTestVisible="False"` on the cluster's `Path`, a click that lands on the arc geometry is caught by the path and **doesn't bubble to the row**, so clicking certain drivers silently does nothing — and only on rows where the arc is wide enough to overlap your click. *(`picker/MainWindow.xaml`; see also `docs/wpf-broadcast-visuals.md`.)*

**Rule: any decorative overlay inside the clickable driver row must set `IsHitTestVisible="False"` so clicks reach the row's switch handler.**

---

## Picker ↔ plugin replay transport (the JSON file channel)

The picker (WPF) controls replay in the plugin (in-process with SimHub) over **atomic JSON files** written next to the shared settings file. No sockets, no IPC handles — just tmp-write + rename, polled.

| File | Direction | Purpose |
|---|---|---|
| `F1SimHubLive.ReplayCommand.json` | picker → plugin | the command (atomic tmp+rename, monotonic `Seq`) |
| `F1SimHubLive.ReplayStatus.json` | plugin → picker | scrubber position + replay state |
| `F1SimHubLive.ReplayGrid.json` | plugin → picker | identity + car-telemetry grid (Phase-1 fields) |
| `F1SimHubLive.ReplayPrefs.json` | persisted | per-session sync offset + last position (so reload doesn't re-sync) |

- **Commands:** `load play pause toggle speed seek seeklap stop golive`. The schema mirrors `F1SimHubLivePlugin.DispatchReplayCommand` exactly.
- **"Nudge" is just an absolute `seek`** to `PositionSec ± delta` — the plugin needs no relative-seek command.
- **`Seq` seeds from Unix-ms** so it always exceeds whatever the plugin last saw, even across picker restarts; the plugin **ignores any `Seq <= lastSeen`** (idempotent, replay-safe). *(`picker/Services/ReplayControlClient.cs`, `picker/MainWindow.Replay.cs`)*

**Rule: every replay command must carry a strictly-increasing `Seq`; never reuse or reset it. Writes are tmp+rename (atomic) so the reader never sees a half-written file.**

---

## Invariants — the DO-NOT-BREAK list

1. **Three sources, one `ITelemetrySource`.** Replay must emit the same events as live/MV so the wheel/dashboard stay source-agnostic.
2. **Replay = CarData + DriverList only.** Timing columns are intentionally blank in Phase 1; don't "fix" them without subscribing `TimingData`.
3. **Row identity (TLA/name/team/colour) is mutable INPC and refreshed every snapshot.** Only `RacingNumber` is `init`.
4. **The clock playhead is driver-independent and never resets on a driver switch** (CLOCKS.md Trap #2).
5. **Decorative overlays in the driver row set `IsHitTestVisible="False"`** so row clicks switch drivers.
6. **Replay commands carry a monotonic `Seq` (Unix-ms seed) and are written atomically** (tmp+rename); the plugin ignores stale `Seq`.
7. **Replay snapshots are wall-clock stamped** so the 60 Hz interpolator and the clock playhead behave exactly as live.

---

## File / line map

| Concern | File |
|---|---|
| Replay source (virtual clock, event emission) | `F1Replay/F1ReplayClient.cs` |
| Replay timeline model | `F1Replay/ReplayTimeline.cs` |
| Replay grid row (identity + car telemetry, no timing) | `F1Replay/F1ReplayClient.cs` (`ReplayGridRow`) |
| Picker→plugin transport (commands, Seq, prefs) | `picker/Services/ReplayControlClient.cs` |
| Picker replay UI (grid mode enter/exit/update, scrubber) | `picker/MainWindow.Replay.cs` |
| Plugin command dispatch | `F1SimHubLivePlugin.DispatchReplayCommand` |
| Mutable row identity refresh | `picker/Models/DriverTimingRow.cs`, `picker/Services/LiveTimingClient.cs` (`ApplySnapshot`) |
| Driver-independent playhead | `MultiViewer/MultiViewerHttpClient.cs` (`_playheadUtc`), `F1Signalr/CarDataDecoder.cs` (`LatestFrameUtc`) |
| Row-click overlay hit-testing | `picker/MainWindow.xaml` (`IsHitTestVisible="False"` on cluster `Path`) |

---

## Regression history (so we don't repeat it)

| Symptom | Root cause | Fix |
|---|---|---|
| Hamilton shows as Ferrari in a 2020 replay | `Tla/LastName/TeamName/TeamColour` were `{ get; init; }` — locked at row creation | Mutable INPC + refresh-from-snapshot; only `RacingNumber` stays `init` |
| Clock blanks / jumps to `-:--:--` on driver switch | Clock used `_lastEmittedUtc` (per-driver, resets on `SetDriverNumber`) | Driver-independent `_playheadUtc` / `LatestCarDataUtc` (CLOCKS.md Trap #2) |
| Clicking some drivers does nothing | Per-driver cluster `Path` caught the click before it bubbled to the row | `IsHitTestVisible="False"` on the overlay path |
| Gaps/sectors/tyres blank in replay | Replay topic set is CarData + DriverList only | By design (Phase 1); add `TimingData` topic in a future phase |
| Duplicate/stale replay commands processed | — | Monotonic `Seq` (Unix-ms seed); plugin ignores `Seq <= lastSeen` |
