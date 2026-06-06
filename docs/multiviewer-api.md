# F1 MultiViewer local API — what F1SimHubLive relies on

F1 MultiViewer (<https://multiviewer.app/>) exposes a local HTTP API on `http://localhost:10101` whenever the desktop app is running. F1SimHubLive uses that API as one of its two telemetry sources (`Source=MultiViewer` in `F1SimHubLive.Settings.json`).

This document captures the *exact* state MultiViewer has to be in for the plugin (and the installer's prerequisite check) to see telemetry, and why a naive "is the API up?" probe is not enough.

---

## The gotcha: API up ≠ telemetry flowing

MultiViewer's local HTTP server starts the moment the app launches. It will happily answer:

```
GET http://localhost:10101/api/v1/live-timing/Heartbeat
→ 200 OK
```

…as soon as the app is open and signed into F1 TV — *even if the user is doing nothing more than watching the broadcast video feed.* No actual telemetry is being decoded or streamed at that point.

Telemetry only starts flowing once the user has **opened the Live Timing view** for the current session:

- For a **live session** (FP1/2/3, Q, Sprint, Race): when the live broadcast picks up, MultiViewer auto-engages Live Timing. Usually nothing extra to click.
- For a **replay**: the user must explicitly click the **"Replay Live Timing"** button on the session card inside MultiViewer. Just hitting play on the video does *not* start Live Timing. This is the most common foot-gun on a fresh install.

Field test that proved this: a known-good install on a freshly built office PC. SimHub installed, F1 MultiViewer installed, signed into F1 TV, replay session loaded, video feed playing. Prerequisite #3 in the installer (which at the time only probed `/Heartbeat`) showed green ✓. Plugin loaded fine but every property stayed at 0. The fix was to go back to MultiViewer and click **Replay Live Timing** on the session card — instantly, telemetry started flowing and SimHub locked on.

---

## Two-stage probe (what the installer does as of `fb17a18`)

```
┌──────────────────────────────────────────────────────────────────┐
│ Stage 1 — is the local API up?                                   │
│   GET /api/v1/live-timing/Heartbeat                              │
│   200 OK → MultiViewer is running and signed in                  │
│   timeout / 5xx → MultiViewer is not running                     │
│   4xx (non-200) → app is up but the F1 TV session is not        │
└──────────────────────────────────────────────────────────────────┘
                          │ pass
                          ▼
┌──────────────────────────────────────────────────────────────────┐
│ Stage 2 — is Live Timing actually streaming?                     │
│   GET /api/v1/live-timing/SessionInfo                            │
│   200 + populated body → Live Timing is on. Telemetry will flow. │
│   404 / empty body    → user is only watching video; tell them   │
│                         to click "Replay Live Timing".           │
└──────────────────────────────────────────────────────────────────┘
```

Both probes use a 3-second timeout. `SessionInfo` returns a JSON document whose top-level shape varies (`Meeting.Name`, `Name`, etc., depending on session type); the installer pulls a friendly session label from it when present and falls back to a generic "Live Timing active" message.

Source: [`installer/Services/PrereqChecker.cs`](../installer/Services/PrereqChecker.cs).

---

## Manual verification recipe

When the installer (or the plugin itself) says "telemetry is not flowing," walk this in order. Each step is independently verifiable in a browser.

1. **API up at all?**
   - URL: `http://localhost:10101/api/v1/live-timing/Heartbeat`
   - Expected: `200 OK`, body irrelevant.
   - If this fails → MultiViewer is not running. Start it.

2. **Signed into F1 TV?**
   - In the MultiViewer UI, top-right should show your F1 TV account name, not a "Sign in" button.
   - The `Heartbeat` probe returning 200 implicitly confirms this on most builds.

3. **Live Timing actively streaming?**
   - URL: `http://localhost:10101/api/v1/live-timing/SessionInfo`
   - Expected: `200 OK` with a non-trivial JSON body (Meeting / Name / Type / Path fields populated).
   - If this returns 404 or an empty body → Live Timing is NOT on. Go to step 4.

4. **Click "Replay Live Timing"** on the session card inside MultiViewer.
   - For a live session: pick the session from MultiViewer's home and choose the **Live Timing** view (not just the video feed).
   - For a replay: load the session, then click the **"Replay Live Timing"** button on the session card. The cursor scrubber and live timing panel should appear.
   - Re-check the URL from step 3 — it should now return populated JSON.

5. **Per-driver CarData arriving?**
   - URL: `http://localhost:10101/api/v1/live-timing/CarData`
   - Expected: a non-empty `Entries[]` array, each entry containing `Cars[<number>].Channels`.
   - This is what the plugin actually polls. If `SessionInfo` is green but `CarData.Entries` is empty, the session may be paused inside MultiViewer — hit play.

Once steps 1–5 all pass, restart SimHub and the plugin will pick up telemetry within ~1 second of the next poll tick (default 250 ms).

---

## Endpoints the plugin polls

For reference — these are the actual HTTP endpoints consumed at runtime (see [`MultiViewer/MultiViewerHttpClient.cs`](../MultiViewer/MultiViewerHttpClient.cs)):

| Endpoint                                          | Poll interval | Drives                                              |
|---------------------------------------------------|---------------|-----------------------------------------------------|
| `/api/v1/live-timing/CarData`                     | 250 ms        | RPM, Speed, Gear, Throttle, Brake, DRS (per driver) |
| `/api/v1/live-timing/TimingData`                  | 1000 ms       | Position, Gap, Interval, sector splits              |
| `/api/v1/live-timing/TimingAppData`               | 1000 ms       | Tyre compound, tyre age, pit stop count             |
| `/api/v1/live-timing/TimingStats`                 | 1000 ms       | TopSpeed + rank                                     |
| `/api/v1/live-timing/RaceControlMessages`         | 1000 ms       | FlagText, overtake-system enabled                   |
| `/api/v1/live-timing/LapCount`                    | 1000 ms       | CurrentLap / TotalLaps                              |
| `/api/v1/live-timing/TrackStatus`                 | 1000 ms       | TrackStatusCode, TrackStatusMessage                 |
| `/api/v1/live-timing/ExtrapolatedClock`           | 1000 ms       | Session time remaining (fallback)                   |
| `/api/v1/live-timing/SessionData`                 | 1000 ms       | Race start UTC anchor                               |
| `/api/v1/live-timing/DriverList`                  | once          | Driver count, driver→metadata map                   |
| `/api/v1/live-timing/WeatherData`                 | 5000 ms       | AirTemp, TrackTemp, Humidity, Rainfall, Wind        |

All endpoints return decompressed JSON (no need to inflate the base64-DEFLATE payload that comes through the live SignalR `Streaming` hub). MultiViewer does the decompression for you. This is why **MultiViewer mode is the recommended source for replays and for development** — the plugin doesn't have to handle SignalR negotiation or DEFLATE.

---

## Live vs replay payload shapes — what changes per-session

The same MV endpoint can return slightly different JSON depending on whether the session is **live** (MV is connected to F1's real SignalR feed during a session weekend) or a **replay** (MV is replaying a recorded session, including its own automatically-captured live sessions). The replay layer reconstructs some fields that the live feed doesn't emit for non-race sessions, which means code that works on replay can silently break on live.

### `TimingData` — gap to leader and interval to driver ahead

`/api/v1/live-timing/TimingData` is the worst offender as of MV 1.30+.

| Session mode                                | `Lines[N].GapToLeader` | `Lines[N].IntervalToPositionAhead.Value` | Where the data actually lives                                                                  |
|---------------------------------------------|------------------------|------------------------------------------|------------------------------------------------------------------------------------------------|
| Race (live or replay)                       | ✓ populated            | ✓ populated                              | top-level fields, used directly                                                                |
| **Any** session replay (Q replay, FP replay) | ✓ populated            | ✓ populated                              | top-level fields, reconstructed by the replay layer                                            |
| **Live Practice / Qualifying / Sprint Shootout** | `""` (empty string) or absent | `null` or `{ Value: "" }`         | `Lines[N].Stats[0].TimeDiffToFastest` (= LDR) and `Lines[N].Stats[0].TimeDifftoPositionAhead` (= INT) |

This caught us in production on 2026-06-06 during live Q1 — every driver showed `LDR` for the leader (correct) but blank `INT` and `LDR` for everyone else. The same session worked fine on replay 10 minutes later because the replay layer fills `GapToLeader` / `IntervalToPositionAhead.Value` back in for Q sessions.

**Mitigation in the codebase:** both `picker/Services/LiveTimingClient.cs` and `MultiViewer/TimingDataDecoder.cs` read the top-level fields first, then fall back to `Stats[0].TimeDiffToFastest` / `Stats[0].TimeDifftoPositionAhead` when the top-level value comes back empty. The fastest driver still gets `""` from MV in either shape, and the picker continues to render that as `LDR`, so the leader badge keeps working without any special-casing.

**MV's spelling, not ours:** the second field is `TimeDif`**`f`**`to`**`P`**`ositionAhead` — lowercase `t` in `to`, uppercase `P` in `Position`. This is the actual JSON key returned by MV, not a typo in the codebase. Do not "correct" it.

### How to verify on your own box

While MV is connected to a live FP / Q / SQ session, hit the endpoint directly:

```pwsh
curl http://localhost:10101/api/v1/live-timing/TimingData |
  ConvertFrom-Json |
  ForEach-Object { $_.Lines.PSObject.Properties[0].Value } |
  Select-Object Position,GapToLeader,IntervalToPositionAhead,Stats
```

`GapToLeader` will be empty, `IntervalToPositionAhead` will be null or `Value=""`, but `Stats[0]` will have the two `TimeDiff*` fields filled in.

---

## Probes you should *not* rely on

The following endpoints are tempting but not reliable signals of "Live Timing is on":

- `/api/v1/live-timing/Heartbeat` — see top of this doc. Up before telemetry. ❌ for a positive signal, ✓ as a fast negative ("API not reachable at all").
- `/api/v1/live-timing/state` — older MultiViewer builds exposed a `state` endpoint, but the path has changed across versions; the README previously referenced this and it 404s on current MV. ❌ unstable.

`SessionInfo` is the most stable positive signal across MultiViewer 1.30+ builds tested so far.

---

## See also

- [`README.md` → Troubleshooting](../README.md#troubleshooting) — top-level user-facing troubleshooting.
- [`installer/Services/PrereqChecker.cs`](../installer/Services/PrereqChecker.cs) — the actual probe implementation.
- [`MultiViewer/MultiViewerHttpClient.cs`](../MultiViewer/MultiViewerHttpClient.cs) — the runtime poller that consumes the endpoints listed above.
