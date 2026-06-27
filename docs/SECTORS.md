# Sector & lap-time colour coding — the F1 timing palette contract (read before touching colours)

F1SimHubLive colour-codes timing exactly the way F1's official Live Timing (and MultiViewer, which mirrors it) does. Getting this right is what makes the picker and wheel readable "across the room" — but it is also where several regressions hid, because **there is more than one colour system on screen and they do not share a source of truth.** This is the contract that keeps them correct.

> **Verification:** the palette and the status rules below were mined from the **MultiViewer 2.7.3 `app.asar`** renderer bundle (the canonical Material UI colour map) and verified live against MV (Austrian GP P2, Barcelona qualifying). See "Where the colours come from" for the extraction workflow.

---

## The four colour systems (don't mix them up)

| What | Element | Source signal | Default | Owner converter |
|---|---|---|---|---|
| **Mini-sector tiles** | the little segment bars inside each sector | MV per-segment **integer status code** | dark (no data) | `SegmentStatusToBrushConverter` |
| **Sector-time text** | the S1/S2/S3 *time* numbers | `LapStatus` (None / PB / SB) | **yellow** (not white) | `SectorStatusToBrushConverter` |
| **Lap-time pills** | LAST / BEST / INT / LDR backgrounds | `LapStatus` → background fill | transparent | `LapStatusToBoxBackgroundConverter` + `…ForegroundConverter` |
| **IN PIT box** | pit indicator | the literal string `"IN PIT"` | transparent | `InPitTextConverters` |

**Rule: text-type fields (sectors, best-sector) render the status as TEXT COLOUR; everything else (lap-time, gap, interval) renders the status as a BACKGROUND FILL with contrast text on top.** This is MV's own rule (`for sector/best-sector → colour the text; else → fill the pill`), reproduced 1:1.

---

## The canonical palette (MultiViewer Material UI, mined from `app.asar` 2.7.3)

| Status | Material token | Hex | Where |
|---|---|---|---|
| Not improved / set | `yellow[600]` | `#FDD835` | sector text default, segment `2048` |
| Personal best | `green[500]` | `#4CAF50` | sector text, lap pill, segment `2049` |
| Overall / session best | `purple[500]` | `#9C27B0` | sector text, lap pill, segment `2051` |
| In pit / pit out / stopped / retired / knocked out | `red[500]` (`#F44336`), pitOut `red[800]` | `#F44336` | IN PIT box, segment `2064` (blue, see note) |

> The segment-tile converter uses slightly punchier shades (`#F5C518` yellow, `#3FD06A` green, `#A050E0` purple, `#3C9CF0` blue) tuned for the small tiles on a dark row; the text/pill converters use the exact MV hexes. Both are intentional — tiles need to pop at ~8 px, text needs to match MV's screen 1:1.

### Segment-tile status codes (the F1 SignalR contract)

```
0    = no data (dark)
2048 = yellow  — sector set, not improved
2049 = green   — personal best in this mini-sector
2051 = purple  — overall (session) best in this mini-sector
2064 = blue    — pit lane (in-lap / out-lap segment)
```

**These exact codes matter.** A regression once mapped `2049→purple, 2051→blue, 2064→green`, so in-pit cars (RUS, HAD, ANT, VER) showed **green pit segments and never showed blue at all**. Verified-live mapping: in-pit cars report `2064` on their pit segments, which Live Timing renders blue. *(`SegmentStatusToBrushConverter.cs`)*

---

## Trap #1 — purple is owned by MV, NOT by our own running-minimum

The single biggest sector-colour trap. **Purple (session best) must be driven by MultiViewer's authoritative `TimingStats.BestSectors[i].Position == 1`**, not by a client-side running-minimum comparison.

- Our own running-min (`_bestSectorSeconds`) gets **polluted by live sector values MV doesn't count as a valid best** (in/out laps, deleted times), so multiple drivers falsely tie for the field minimum → several drivers show purple they don't own.
- `TimingStats.BestSectors[i].Position == 1` is MV's stable, authoritative ranking → **exactly one driver per sector is purple.**
- The running-min remains only as a **fallback** when MV reports no position (older MV builds / session types without `TimingStats`).

**Rule: purple = `TimingStats` Position 1. Running-min is fallback only.** Same rule for the BEST lap-time pill: `PersonalBestLapTime.Position == 1` → purple (session best), any non-zero position → green (PB). *(`LiveTimingClient.cs` ~184–246, 272–290)*

---

## Trap #2 — MV's per-lap PB/SB flag fades a tick after it's set

MV sets `PersonalFastest` / `OverallFastest` on a sector **only on the lap that set the time**, then clears it on the next snapshot. If you colour straight off that flag, a green/purple **last-lap** sector reverts to yellow a tick later (mismatching Live Timing — e.g. Leclerc's green S1/S2 flicking back to yellow).

**Rule: derive the last-lap sector colour BY VALUE** — green when the last-lap sector equals the driver's own best for that sector, purple when it's the field-fastest (via `TimingStats` position) — and let MV's explicit flag only **strengthen** the colour, never be the sole source. Stable across ticks, matches Live Timing. *(value-derivation in `LiveTimingClient.cs`)*

---

## Trap #3 — qualifying BEST is the CURRENT segment's PB, not the all-quali PB

In qualifying, `TimingStats.PersonalBestLapTime` is the driver's best across **all of Q1+Q2+Q3**. But MV's cockpit / Live Timing **BEST** column shows the **current-segment** PB (Q3-only once Q3 starts).

**Rule: use `PersonalBestLapTime.Position` for the pill COLOUR, but do NOT overwrite the BEST lap-time VALUE with the `TimingStats` value** — keep the current-segment value MV shows in the row. *(`LiveTimingClient.cs` ~218–246)*

Related Q-mode gap trap: don't read gaps from `Stats[0]` — once Q2 starts, `Stats[0]` is the **frozen Q1 snapshot** (`Stats[1]`=Q2, `Stats[2]`=Q3), so gaps stick on stale Q1 values. Synthesise the Q-mode gap from each driver's `PersonalBestLapTime` differential instead. *(`LiveTimingClient.cs` ~250–265)*

---

## Where the colours come from — the `app.asar` mining workflow

**Stop guessing hex codes from screenshots.** MultiViewer's main app is closed-source, but the Electron renderer is one extract away, and it contains the exact Material UI colour map MV paints with.

```powershell
# 1. extract the MV app bundle (≈150 MB temp; Remove-Item -Recurse when done)
npx @electron/asar extract `
  "$env:LOCALAPPDATA\multiviewer\app-2.7.3\resources\app.asar" `
  "$env:TEMP\mv-asar-peek"

# 2. grep the renderer bundle for the timing component / a literal string
#    e.g. the TimingValue styled component, "IN PIT", "PIT OUT", personalFastest
Select-String -Path "$env:TEMP\mv-asar-peek\.webpack\renderer\main_window\index.js" `
  -Pattern 'personalFastest|overallFastest|inPit' | Select-Object -First 5

# 3. pull the styled-component definition, translate the Material UI tokens 1:1
#    (green[500]=#4CAF50, purple[500]=#9C27B0, yellow[600]=#FDD835, red[500]=#F44336)
```

MV's `body1` typography (its `darkTheme`, bundle offset ~7.0 MB) is `fontSize: 14px, letterSpacing: -0.05px, fontWeight: bold` — **every visible timing field in MV is 14 px bold**, which is why the picker's LAST/BEST/INT/LDR are 14 px Bold and current-sector text is 14 px. For any future "make the picker look like MV" work, this extraction is faster and more accurate than screenshot ping-pong.

---

## Invariants — the DO-NOT-BREAK list

1. **Two colour systems, two sources.** Mini-sector **tiles** = MV integer status code (2048/2049/2051/2064). Sector **text** + lap **pills** = `LapStatus` (None/PB/SB). Never cross-wire them.
2. **Sector text defaults to YELLOW, never white.** A completed-but-not-improved sector is yellow (MV convention); white makes it vanish on the dark row.
3. **Purple = MV `TimingStats` Position 1.** Client-side running-minimum is fallback only and over-applies purple.
4. **Last-lap sector colour is derived by value**, not off MV's one-tick-only `PersonalFastest`/`OverallFastest` flag.
5. **Qualifying BEST pill: colour from `PersonalBestLapTime.Position`, value stays current-segment.** Don't overwrite the displayed BEST time with the all-quali PB.
6. **Segment codes are exact:** `2048`=yellow, `2049`=green, `2051`=purple, `2064`=blue(pit). Mis-mapping hides blue and turns pit segments green.
7. **Text-type → colour the text; everything else → fill the pill** (MV's own branch on field type).

---

## File / line map

| Concern | File |
|---|---|
| Mini-sector tile colour (status codes) | `picker/Services/SegmentStatusToBrushConverter.cs` |
| Sector-time text colour (LapStatus, yellow default) | `picker/Services/SectorStatusToBrushConverter.cs` |
| Lap-time pill background / foreground | `picker/Services/LapStatusToBoxBackgroundConverter.cs`, `…ForegroundConverter.cs` |
| IN PIT red box | `picker/Services/InPitTextConverters.cs` |
| LapStatus derivation (PB/SB, purple-by-Position, last-lap-by-value) | `picker/Services/LiveTimingClient.cs` |
| Per-(driver,sector) running-min fallback | `picker/Services/LiveTimingClient.cs` (`_bestSectorSeconds` / `_bestSectorStrings`) |
| Plugin/wheel sector decode | `MultiViewer/TimingDataDecoder.cs`, `TimingStatsDecoder.cs` |
| Sector sub-column width (segment-count weighted) | `picker/Services/CountToGridStarConverter.cs`, `picker/Models/DriverTimingRow.cs` (`SectorView.SegmentCount`) |

---

## Regression history (so we don't repeat it)

| Symptom | Root cause | Fix |
|---|---|---|
| In-pit cars showed green pit segments, never blue | Segment codes mis-mapped (`2049→purple, 2051→blue, 2064→green`) | Correct map `2048`=yellow `2049`=green `2051`=purple `2064`=blue |
| Last-lap green/purple sector reverted to yellow after a tick | Coloured off MV's one-tick `PersonalFastest`/`OverallFastest` flag | Derive colour by value; flag only strengthens |
| Several drivers falsely showed purple sectors | Purple based on polluted client-side running-min | Drive purple from `TimingStats.BestSectors[i].Position == 1` |
| Sector times all white, no PB/SB coding | Sector text used the white-defaulting `LapStatusToBrush` | New `SectorStatusToBrush` (defaults yellow); pills + IN PIT box added |
| Q2/Q3 gaps stuck on stale Q1 values | Read from frozen `Stats[0]` (Q1 snapshot) | Synthesise Q-mode gap from `PersonalBestLapTime` differential |
| S1 (3 segs) bars fatter than S2 (7 segs) | Sector sub-columns split equally `* * *` | Weight sub-column width by `SegmentCount` |
