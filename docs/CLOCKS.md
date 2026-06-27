# Clocks & countdowns — how the session timer works (read before touching it)

F1SimHubLive renders the **session time remaining** on two independent surfaces:

| Surface | Process | Owner file | Property/field |
|---|---|---|---|
| **Picker header clock** | `F1SimHubLive-Picker.exe` (WPF) | `picker/Services/SessionInfoClient.cs` | `SessionHeaderModel.TimeText` |
| **Wheel / dashboard countdown** | SimHub plugin → `F1RaceSim_GSIFPEV2` dashboard | `MultiViewer/MultiViewerHttpClient.cs` | `F1SimHubLivePlugin.SessionTimeRemaining` |

These two surfaces are **separate codebases that must stay behaviourally identical.** They were repeatedly broken by the same handful of traps during the 1.10.7–1.10.14 bug-fixing run. This document is the contract that keeps them working. **If you change one clock, change the other to match, and re-read the invariants below.**

> **Verification status (as of 1.10.14):** matched MV Live Timing to ~1s on **replayed practice**, **replayed race** (Barcelona, formation lap correctly accounted for), and **replayed full qualifying** (Barcelona Q1→Q2→Q3, all segment re-anchors + between-segment freezes captured — see "Qualifying & sprint" below for the captured timeline). **Live sessions are not yet verified** — first live test is qualifying. See "Live vs replay" for what to watch.

---

## The one true formula

```
remaining = ExtrapolatedClock.Remaining − ((playhead − PlaybackLead) − ExtrapolatedClock.Utc)
            when Extrapolating == true
remaining = ExtrapolatedClock.Remaining          (frozen, e.g. formation lap)
            when Extrapolating == false
```

- **`ExtrapolatedClock`** — MV's official session/race clock, a self-consistent `{Utc, Remaining}` anchor ("at `Utc`, `Remaining` was left"). For a **race** MV pushes the anchor `Utc` to **lights-out** the instant the red lights go off, so the anchor already bakes in the formation lap + grid forming + any aborted-start delay. For **practice/qualifying** the anchor is the session start. Extrapolating the anchor to the playback position therefore matches MV Live Timing **to the second** for every session type, with no special-casing.
- **`playhead`** — the UTC timestamp of the **freshest CarData frame**. The only MV signal that tracks the video frame-for-frame (advances at 1× while playing, freezes on pause, jumps on seek).
- **`PlaybackLead`** — a constant `2s` that compensates MV's decode buffer (MV hands over the freshest CarData frame ~2s *ahead* of the frame it paints on screen and ahead of its own Live Timing panel).

Both surfaces implement exactly this as the **primary** path.

### Fallback: `SessionEndUtc − playhead`

```
remaining = SessionEndUtc − (playhead − PlaybackLead)
```

Used **only** until the `ExtrapolatedClock` anchor is available. `SessionEndUtc` is the *scheduled* end (`SessionInfo` `EndDate` + `GmtOffset`); it knows nothing about the pre-race delay, so during a **race start it reads several minutes ahead** of MV Live Timing (the ~4-minute formation-lap error that 1.10.13 fixed). Correct enough as a stopgap for practice, wrong for a race — never promote it back to primary.

---

## Why this formula, and the signals we DON'T trust

MV exposes three time-ish signals:

| Signal | Endpoint | Behaviour | Verdict |
|---|---|---|---|
| `ExtrapolatedClock` | `/ExtrapolatedClock` | `{Utc, Remaining, Extrapolating}` anchor. On a race `Utc` is **lights-out**; while paused/pre-start `Extrapolating==false` and `Remaining` is frozen | ✅ **Official clock — extrapolate the anchor to the playhead.** Never read `Remaining` as "now" without extrapolating. |
| **CarData frame `Utc`** | `/CarData` | Advances 1× while playing, freezes on pause, jumps on seek | ✅ **This is the playhead** we extrapolate the anchor to. |
| `Heartbeat` | `/Heartbeat` | Wall-clock `Utc`, updates only **every ~10s** | ⚠️ Causes lock-then-jump if used as the clock. Race fallback only. |

### Things that caused real regressions (do not reintroduce)

1. **`ExtrapolatedClock.Remaining` displayed raw (un-extrapolated)** → stuck at `59:58` on replays (the anchor is frozen). Always extrapolate it to the playhead. *(pre-1.10.7)*
2. **`Heartbeat` as the clock** → ticks, freezes for ~10s, then jumps to catch up. *(1.10.7)*
3. **A hard-coded session duration** (`_sessionDuration = TimeSpan.FromHours(2)`) → phantom leading `1:` hour on sub-hour practice sessions when the default wasn't overwritten. *(≤1.10.8)* **There is no session-length constant anymore. Do not add one.**
4. **`SessionEnd − playhead` as the primary race countdown** → ~4 minutes ahead of MV because the scheduled end ignores the formation lap. *(1.10.11–1.10.12)* It's a fallback only.

---

## Trap #1 — Newtonsoft drops the `Z`, then `.ToUniversalTime()` adds +5h

**This is the single most dangerous trap in the whole clock path.** It silently shifts the playhead by the local UTC offset and clamps the countdown to `0:00`.

```csharp
// ☠️ WRONG — JToken.ToString() renders "6/26/2026 3:26:15 PM" (no Z).
//    DateTime.TryParse → Kind=Unspecified → .ToUniversalTime() re-applies
//    the local offset (+5h on a CDT box) → playhead lands past SessionEnd
//    → remaining clamps to 0:00.
DateTime utc = DateTime.Parse(token.ToString()).ToUniversalTime();

// ✅ RIGHT — Value<DateTime>() preserves Kind=Utc directly.
DateTime utc = token.Value<DateTime>();
```

**Rule: every MV UTC timestamp read through Newtonsoft must use `token.Value<DateTime>()`, never `DateTime.Parse(token.ToString())`.** This applies to CarData `Utc` (`F1Signalr/CarDataDecoder.cs`) and anywhere else a frame/anchor time is parsed.

In the **picker** (which uses `System.Text.Json`, not Newtonsoft) the equivalent guard is `DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal` plus `DateTime.SpecifyKind(..., Utc)` — see `SessionInfoClient.cs`.

---

## Trap #1b — Newtonsoft auto-converts an ISO-8601 string to a `Date` token, so a `Type == String` guard silently drops it

**This is the trap that broke the wheel race countdown twice (1.10.13 looked fixed but wasn't; 1.10.14 actually fixed it).** When `JObject.Parse` reads `"Utc":"2026-06-14T13:03:28.026Z"`, Newtonsoft's default `DateParseHandling.DateTime` **parses the value into a `DateTime` immediately**, so the token's `Type` is **`JTokenType.Date`, not `JTokenType.String`**.

```csharp
// ☠️ WRONG — the ExtrapolatedClock "Utc" anchor is a Date token, so this guard
//    is false, `utc` stays MinValue, Clock.IsValid is false, _lastClock is never
//    cached, and the wheel countdown falls back forever to SessionEnd−playhead
//    (~3 min off on a race).
if (utcTok.Type == JTokenType.String) { utc = DateTime.Parse(utcTok.Value<string>()); }

// ✅ RIGHT — read the value regardless of how Newtonsoft surfaced it (Date OR
//    String). Value<DateTime>() preserves Kind=Utc (the trailing Z), matching
//    the CarData playhead's basis so the subtraction is correct.
if (utcTok != null && utcTok.Type != JTokenType.Null)
    utc = utcTok.Value<DateTime>();
```

- This is verifiable: `JObject.Parse("{\"Utc\":\"...Z\"}")["Utc"].Type` returns `Date`, and `.Value<DateTime>()` returns the value with `Kind=Utc`.
- A bare time string like `"01:59:59"` (the `Remaining` field) is **not** auto-converted — it stays `String` — which is exactly why only the `Utc` anchor was hit while `Remaining` parsed fine.
- The **picker** never saw this because `System.Text.Json` does not auto-convert dates; `TryGetProperty("Utc").GetString()` returns the raw string.

**Rule: never gate an MV timestamp read on `token.Type == JTokenType.String` in Newtonsoft. Read it with `Value<DateTime>()` and only null-check the token.** This is why the wheel and the picker disagreed by exactly the formation lap even with identical formulas.

---

## Trap #2 — the playhead must be driver-INDEPENDENT and must NOT reset on driver switch

The wheel clock died to `-:--:--` because it reused the wrong field as the playhead.

- `_lastEmittedUtc` is the **per-driver, forward-only CarData dedup cursor.** It only advances for frames matching the *selected* driver, and **`SetDriverNumber` resets it to `DateTime.MinValue`.** When the selected driver had no frames in a batch — or right after a driver switch — it was `MinValue`, the countdown string went empty, and the dashboard fell back to its placeholder. *(1.10.9–1.10.10)*
- `_playheadUtc` is the **driver-independent** playhead: set on **every** CarData response from the freshest frame **across all cars** (`CarDataDecoder.LatestFrameUtc`), and **never reset on a driver switch.** *(1.10.11)*

**Rule: the clock playhead is `_playheadUtc` (plugin) / `LatestCarDataUtc` (picker). It is independent of the selected driver and is never reset when the driver changes.** Switching drivers must not blank or jump the clock.

---

## Trap #3 — the dashboard placeholder is `-:--:--`

The wheel dashboard binding is literally:

```js
var t = $prop('F1SimHubLivePlugin.SessionTimeRemaining');
return (t && t != '') ? t : '-:--:--';
```

So **`-:--:--` on the wheel always means the plugin emitted an empty `SessionTimeRemaining`** — i.e. one of the formula inputs was missing (no `SessionEndUtc` *and* no valid anchor, or no `_playheadUtc`). It is never a formatting bug in the dashboard; always debug the plugin side. *(binding lives in `dashboards/F1RaceSim_GSIFPEV2/F1RaceSim_GSIFPEV2.djson`, element `Name:"TimeLeft"`.)*

---

## Trap #4 — formatting: drop the hour under 60 min, keep it for races

Practice/qualifying are always <1h; races run >1h. The format must adapt, identically on both surfaces:

```csharp
ts.TotalHours >= 1
    ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"   // race:      "1:32:40"
    : $"{ts.Minutes}:{ts.Seconds:D2}";                          // practice:  "34:12"  (no leading zero, no phantom hour)
```

- Plugin: `MultiViewerHttpClient.FormatRemaining`.
- Picker: `SessionInfoClient.FormatHms`.

**Keep these two byte-for-byte identical.** Always clamp negative `TimeSpan` to zero first.

---

## SessionEndUtc — parse it from SessionInfo, not SessionData

```
EndDate  = session-local end time, ISO, NO offset   (e.g. "2026-06-26T18:00:00")
GmtOffset = local→UTC delta                          (e.g. "+02:00")
SessionEndUtc = new DateTimeOffset(SpecifyKind(EndDate, Unspecified), GmtOffset).UtcDateTime
```

This is correct regardless of the machine's local time zone or DST. Both surfaces parse it the same way (`SessionInfoClient.cs` and `MultiViewerHttpClient.TryUpdateSessionEnd`). It's cached once per session (`_sessionEndUtc`); MV does not change it mid-session.

> The plugin used to fetch `SessionData` for a race-start time + duration; that path is gone. It now fetches **`SessionInfo`** in the same slot.

---

## The 2-second PlaybackLead

```csharp
private static readonly TimeSpan PlaybackLead = TimeSpan.FromSeconds(2);
```

MV decodes CarData a beat ahead of the painted video, so the raw playhead leads MV's on-screen Live Timing clock by ~2s. Subtracting `PlaybackLead` from the playhead lines our countdown up with what's on screen.

**The constant is duplicated in both files on purpose (separate processes). Keep the two values equal.** If MV ever changes its buffering and the lead drifts, this is the single number to retune on each side.

---

## Live vs replay (VOD) — same formula, different inputs

The whole 1.10.7–1.10.14 hunt was done on **replays**. The formula is identical live, but the inputs behave differently — know what's normal in each mode so a live oddity isn't mistaken for a bug:

| Aspect | Replay / VOD | Live |
|---|---|---|
| **playhead** (CarData frame `Utc`) | Advances at 1× while playing, **freezes on pause**, **jumps on seek**. Can be minutes behind wall-clock. | Arrives in real time, so playhead ≈ now (UTC). No pause/seek. |
| **`ExtrapolatedClock.Utc`** anchor | A **static** baseline for the session/segment (race = lights-out). Doesn't move as you scrub. | Real wall-clock anchor; `Remaining` genuinely decrements. Still extrapolate to the playhead. |
| **`Extrapolating`** | `true` during green running; we extrapolate to the (possibly-paused) playhead. | `true` during green running; `false` when MV freezes the clock (red flag, SC/VSC sometimes, between quali segments). |
| **`PlaybackLead` (2s)** | Compensates MV's decode buffer vs the painted frame. | Same buffer exists live — keep the 2s. |

**Because the primary formula is `Remaining − (playhead − anchor)`, it self-corrects in both modes**: in replay the playhead is the scrub position; live it's ~now. The one thing to confirm live the first time: that MV's **race** anchor `Utc` is still lights-out live (expected — it's the same timing feed), so the live race countdown also accounts for the formation lap.

**Session suspension (red flag / clock stopped):** MV sets `Extrapolating=false` and freezes `Remaining`. Our `LiveRemaining` returns that frozen value as-is, matching MV's frozen on-screen clock — no special-casing needed. Don't "fix" this into a still-ticking clock.

---

## Qualifying & sprint — multi-segment clocks (Q1/Q2/Q3)

Qualifying is **not one clock** — it's three back-to-back segments, each with its own countdown that **resets**:

| Segment | Length | Between segments |
|---|---|---|
| Q1 | ~18 min | clock stopped, cars to pit, slowest knocked out |
| Q2 | ~15 min | clock stopped |
| Q3 | ~12 min | — |

(Sprint Shootout is the same shape: SQ1/SQ2/SQ3 at 12/10/8 min.)

**Why our approach handles this for free:** MV **re-anchors** its `ExtrapolatedClock` `{Utc, Remaining}` at the **start of each segment**. Because we always extrapolate *the current anchor* to the playhead, the countdown follows each Q1→Q2→Q3 reset automatically — no segment detection, no per-segment constants. (The old `SessionEnd − playhead` formula would have been wrong for every segment, since there is no single scheduled "end" that maps to a segment clock.)

**What to watch on the first live quali (Saturday):**
1. Each segment start: countdown should jump to the new segment's full time (≈18:00 → ≈15:00 → ≈12:00) within a second of MV.
2. Between segments: `Extrapolating` goes `false`, so the countdown **freezes** (at `0:00`, then the next full time once MV re-anchors) — that's correct, it should mirror MV's panel, not keep ticking.
3. The 2s `PlaybackLead` should still line us up with the painted video; if live introduces a different buffer, this is the only number to retune.

**Captured evidence (Barcelona quali replay, 2026-06-27 — `scripts\Capture-ClockTimeline.ps1`):** the full Q1→Q2→Q3 timeline confirmed the behaviour above to the second. MV uses a **two-phase re-anchor** at each segment — it first *stages* the next segment (new baseline, `Extrapolating=false`, clock held) ~1 min before going green, then flips `Extrapolating=true` at green:

| Phase | Anchor `Utc` | Baseline `Remaining` | `Extrapolating` | Our display |
|---|---|---|---|---|
| Q1 green | 14:00:00 | 17:59 | true | counts down 1× |
| Q1→Q2 gap | 14:18:00 | 0:00 | **false** | frozen 0:00 |
| Q2 staged | 14:24:01 | 15:00 | **false** | held 15:00 |
| Q2 green | 14:25:01 | 14:59 | true | counts down 1× |
| Q2→Q3 gap | 14:40:00 | 0:00 | **false** | frozen 0:00 |
| Q3 staged | 14:46:01 | 13:00 | **false** | held 13:00 |
| Q3 green | 14:47:01 | 12:59 | true | counts down 1× |

Both surfaces (picker + wheel) tracked MV to ~1s at every row; the `SessionEnd − playhead` **fallback was wrong by ~40–58 min** through Q1/Q2 (it only coincidentally agreed in Q3 when the scheduled session end happened to be ~13 min out), which is exactly why ExtrapolatedClock is primary and `SessionEnd` is fallback-only.

---

## Debugging playbook — find ground truth fast (this hunt took hours; next one shouldn't)

The clocks ate a lot of time because of guessing. The fast path:

1. **Never guess MV's behaviour — probe its localhost API directly.** MV serves the same data we consume:
   ```powershell
   $b='http://localhost:10101/api/v1/live-timing'
   Invoke-RestMethod "$b/ExtrapolatedClock"   # {Utc, Remaining, Extrapolating}
   Invoke-RestMethod "$b/SessionInfo"          # StartDate/EndDate/GmtOffset
   Invoke-RestMethod "$b/LapCount"             # CurrentLap/TotalLaps
   ($r = Invoke-RestMethod "$b/CarData").Entries[-1].Utc   # freshest playhead
   ```
   Compute the formula by hand from these and compare to what's on screen **before** touching code.
2. **Two surfaces disagree?** The picker and the wheel are **independent computations of the same formula** (picker = `System.Text.Json`, wheel = Newtonsoft). If one is right and one is wrong, the formula is fine — the bug is in **inputs or one decoder**, not the maths. (1.10.14 was exactly this: picker right, wheel wrong.)
3. **Confirm what's actually deployed — `FileVersion` is not enough.** A correct version can sit next to stale logic. Decompile the live DLL's method body and read the real IL:
   ```powershell
   ilspycmd 'C:\Program Files (x86)\SimHub\F1SimHubLive.dll' -t F1SimHubLive.MultiViewer.MultiViewerHttpClient
   ```
   This is how we proved the wheel *was* running the new primary path, which redirected the hunt to the decoder.
4. **Still stuck? Add a throttled diagnostic, don't theorise.** Drop a `_log("[clockdiag] …")` in the compute path dumping every input + which branch was taken (throttle ~5s), build (SimHub **closed**), let it run ~15s, then read the lines from `C:\Program Files (x86)\SimHub\Logs\SimHub.txt`. Remove the diagnostic before shipping. One diag line root-caused 1.10.14 (`IsValid=False` next to a valid 78-char `clockRaw`).
5. **Newtonsoft token-type check (offline, no restart).** Before trusting any `token.Type == …` guard, verify what Newtonsoft actually produces:
   ```powershell
   Add-Type -Path (gci 'C:\Program Files (x86)\SimHub' -Filter Newtonsoft.Json.dll)[0].FullName
   ([Newtonsoft.Json.Linq.JObject]::Parse('{"Utc":"2026-01-01T00:00:00Z"}'))['Utc'].Type  # => Date, not String
   ```
6. **Deploy reality:** the DLL/dashboard deploy is auto, but **only when SimHub is closed** (`dotnet build` prints `[deploy] SKIP` otherwise). The picker is single-file — copying the exe over `SimHub\F1SimHubLive-Picker.exe` is a full deploy. Both SimHub **and** the picker must be closed.

---

## Invariants — the DO-NOT-BREAK list

1. **One formula, two surfaces, identical behaviour.** Change the picker clock and the wheel clock together.
2. **Read every Newtonsoft UTC with `Value<DateTime>()`**, never `Parse(ToString())`. (System.Text.Json side: `AssumeUniversal | AdjustToUniversal` + `SpecifyKind(Utc)`.)
3. **The clock playhead is driver-independent and never reset on a driver switch.** Never wire the clock to `_lastEmittedUtc` or any per-driver/forward-only/dedup cursor.
4. **No hard-coded session length.** Session end comes from `SessionInfo` (`EndDate`+`GmtOffset`); never reintroduce a `_sessionDuration`/`FromHours(2)` default.
5. **Never display `ExtrapolatedClock.Remaining` raw** — extrapolate the anchor to the playhead (`Remaining − (playhead − Utc)` while `Extrapolating`, frozen otherwise). This anchor-extrapolation is the **primary** countdown; `SessionEnd − playhead` is fallback only and is wrong for races (ignores the formation lap).
6. **`FormatRemaining`/`FormatHms` stay identical** and drop the hour below 60 min.
7. **`-:--:--` on the wheel = empty `SessionTimeRemaining` from the plugin.** Debug the plugin inputs, not the dashboard.
8. **Keep `PlaybackLead` equal on both sides.**

---

## File / line map

| Concern | Plugin (wheel) | Picker (header) |
|---|---|---|
| Playhead capture | `MultiViewerHttpClient.HandleCarDataResponse` → `_playheadUtc` via `CarDataDecoder.LatestFrameUtc` | `PickerTelemetryClient` → `LatestCarDataUtc` (`_latestFrameUtc`) |
| Driver-independent frame UTC | `F1Signalr/CarDataDecoder.LatestFrameUtc` | `PickerTelemetryClient.TryParseLatestNJson` |
| SessionEnd parse | `MultiViewerHttpClient.TryUpdateSessionEnd` | `SessionInfoClient` (EndDate+GmtOffset block) |
| Countdown computation | `MultiViewerHttpClient.SessionDataLoopAsync` (the `remainingText` block — ExtrapolatedClock primary, SessionEnd fallback) | `SessionInfoClient.TickClock` (same) |
| ExtrapolatedClock anchor decode | `MultiViewer/ExtrapolatedClockDecoder.cs` (`Parse` / `LiveRemaining`) | `SessionInfoClient` clock-parse block → `_clockAnchorUtc` |
| Formatting | `MultiViewerHttpClient.FormatRemaining` | `SessionInfoClient.FormatHms` |
| PlaybackLead constant | `MultiViewerHttpClient.PlaybackLead` | `SessionInfoClient.PlaybackLead` |
| Dashboard binding / placeholder | `dashboards/F1RaceSim_GSIFPEV2/F1RaceSim_GSIFPEV2.djson` (`TimeLeft`) | — |

---

## Regression history (so we don't repeat it)

| Version | Symptom | Root cause | Fix |
|---|---|---|---|
| 1.10.7 | Clock ticks then jumps every ~10s | Used `Heartbeat` (updates ~10s) as the clock | Switch to CarData playhead |
| ≤1.10.8 | Wheel shows phantom leading `1:` on practice | `_sessionDuration` defaulted to `FromHours(2)` | Removed; no session-length constant |
| 1.10.8 | Picker header stuck at `0:00` | Newtonsoft `ToString()` dropped `Z` → +5h → playhead past end | Read with `Value<DateTime>()` |
| 1.10.9–1.10.10 | Wheel countdown `-:--:--` | Clock used `_lastEmittedUtc` (per-driver, resets on switch) | Driver-independent `_playheadUtc` |
| 1.10.11 | Wheel still `-:--:--` (anchor produced empty) | Wheel used `ExtrapolatedClock` anchor, not `SessionEnd−playhead` | Port the picker's `SessionInfo` `SessionEnd−playhead` formula to the plugin |
| 1.10.12 | Both clocks ~2s ahead of MV Live Timing | MV decode-buffer lead | `PlaybackLead = 2s` on both surfaces |
| 1.10.13 | Race countdown ~4 min ahead of MV (ignored formation lap) | `SessionEnd − playhead` uses the *scheduled* end, blind to the pre-race delay | `ExtrapolatedClock` anchor (lights-out for a race) extrapolated to the playhead is now primary; `SessionEnd − playhead` demoted to fallback |
| 1.10.14 | Wheel **still** ~3 min ahead after 1.10.13 (picker was correct) | `ExtrapolatedClockDecoder` guarded the `Utc` anchor with `Type == JTokenType.String`, but Newtonsoft surfaces it as a `Date` token → anchor `MinValue` → `Clock.IsValid` always false → `_lastClock` never cached → permanent `SessionEnd` fallback | Read the anchor with `Value<DateTime>()` regardless of token type (Trap #1b) |
