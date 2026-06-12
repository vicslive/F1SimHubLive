# WPF broadcast visuals — how the picker's input cluster works

Engineering reference for the **per-driver input cluster** introduced in `F1SimHubLive.Picker` v1.7.4 – v1.7.6: the small dark gear ring with the centered gear letter, blue throttle arc sweeping around it, and integer RPM beneath. This doc is the *how to build this kind of thing* companion to [PICKER.md → Per-driver input cluster](../PICKER.md#per-driver-input-cluster), which covers the user-facing description.

If you're porting this pattern to another WPF tool — or extending the picker with a brake arc, DRS pip, ERS bar, anything similar — start here.

---

## What we shipped, at a glance

A 50 px wide column on every driver row containing:

```
  ╭──────╮
  │ ╭──╮ │   34×34 dark ring (Ellipse, fill #0F0F14, stroke #3A3A44 2px)
  │ │ 6│ │   Gear letter on top — Consolas Black FS=18, white
  │ ╰──╯ │   Blue throttle arc behind it — Path, 3px #3399FF, sweeps clockwise
  ╰──────╯   0..260° starting at ~8 o'clock as throttle goes 0..100
   10952     RPM line — Consolas Bold FS=12 white, integer, blank at 0
```

All three values update at MV's CarData poll rate (~5 Hz / 200 ms) for **every** visible driver, not just the selected one. Per-row cost is essentially free — the existing speed updater already walks `Cars.*.Channels` once per poll.

---

## The big idea — value → frozen `PathGeometry` via `IValueConverter`

WPF has no stock "arc fills proportionally to a value" widget. The clean way to do this is:

1. Bind a `Path.Data` to your scalar value (here: `Throttle` 0..100).
2. Route the binding through an `IValueConverter` that returns a `PathGeometry` containing a single `ArcSegment`.
3. **`Freeze()` the geometry before returning it** — frozen geometries are immutable, thread-safe, share-safe, and skip a chunk of WPF's change-notification machinery. At 5 Hz × 20 rows that's 100 alloc/sec of geometry; freezing is the cheap win.

Full source: [`picker/Services/ThrottleToArcGeometryConverter.cs`](../picker/Services/ThrottleToArcGeometryConverter.cs).

### Key bits, annotated

```csharp
public sealed class ThrottleToArcGeometryConverter : IValueConverter
{
    // Centre of the 34x34 ring (the parent Grid is 34 wide × 34 tall).
    public double CenterX { get; set; } = 17;
    public double CenterY { get; set; } = 17;

    // Radius of the *arc itself*. Slightly inside the ring stroke so the
    // 3px blue arc sits just inside the dark ring outline.
    public double Radius { get; set; } = 14;

    // Where the arc starts, in WPF screen-coordinate degrees.
    // 0° = +X axis (3 o'clock). Y points DOWN in screen coords, so
    // 90° = 6 o'clock (bottom), 180° = 9 o'clock, 270° = 12 o'clock (top).
    // 140° lands at roughly 8 o'clock — bottom-left — giving us a clean
    // gap at the bottom (broadcast convention).
    public double StartAngleDegrees { get; set; } = 140;

    // Maximum sweep at 100% throttle. 260° = almost a full ring with a
    // ~100° gap at the bottom for the RPM label visual breathing room.
    public double MaxSweepDegrees { get; set; } = 260;

    // Below this throttle %, return Geometry.Empty so 0% renders as a
    // clean dark ring (no stray dot artifact from a tiny ArcSegment).
    public double MinVisibleThrottle { get; set; } = 0.5;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var t = ToDouble(value);                  // null-safe coerce
        if (t < MinVisibleThrottle) return Geometry.Empty;
        if (t > 100) t = 100;

        var sweep = MaxSweepDegrees * (t / 100.0);
        var startRad = StartAngleDegrees * Math.PI / 180.0;
        var endRad   = (StartAngleDegrees + sweep) * Math.PI / 180.0;

        var start = new Point(CenterX + Radius * Math.Cos(startRad),
                              CenterY + Radius * Math.Sin(startRad));
        var end   = new Point(CenterX + Radius * Math.Cos(endRad),
                              CenterY + Radius * Math.Sin(endRad));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point        = end,
            Size         = new Size(Radius, Radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc   = sweep > 180,    // critical — see "arc gotcha" below
            IsStroked    = true,
        });

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();                       // immutable + thread-safe
        return geo;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

XAML side:

```xml
<Window.Resources>
  <svc:ThrottleToArcGeometryConverter x:Key="ThrottleToArc" />
</Window.Resources>

...

<Grid Width="34" Height="34">
  <!-- 1) Dark ring background -->
  <Ellipse Stroke="#3A3A44" StrokeThickness="2" Fill="#0F0F14" />

  <!-- 2) Throttle arc (between ring and label so the gear letter wins) -->
  <Path Stroke="#3399FF" StrokeThickness="3"
        StrokeStartLineCap="Round"
        StrokeEndLineCap="Round"
        Data="{Binding Throttle, Converter={StaticResource ThrottleToArc}}"
        IsHitTestVisible="False" />

  <!-- 3) Gear letter on top -->
  <TextBlock Text="{Binding GearText}"
             FontSize="18" FontWeight="Black"
             Foreground="#E8E8EE"
             FontFamily="Consolas"
             HorizontalAlignment="Center"
             VerticalAlignment="Center" />
</Grid>
```

---

## WPF arc gotchas that cost real time

These are the ones that bit during development. Save yourself the hour.

### 1. Screen coordinates: Y points **down**

The single biggest source of "arc is starting in the wrong place" headaches.

| Angle | Direction | Clock position |
|---|---|---|
| `0°` | +X | **3 o'clock** |
| `90°` | +Y | **6 o'clock (bottom)** |
| `180°` | -X | **9 o'clock** |
| `270°` | -Y | **12 o'clock (top)** |

Conventional math has +Y pointing up, so a `sin/cos` you'd write on paper for "start at the top" gives you "start at the bottom" in WPF. Just use the table above and stop trying to derive it.

### 2. `IsLargeArc` is **not optional cosmetic** — it's a correctness flag

`ArcSegment` is defined by *start point* + *end point* + *direction* + *radii*. For any sweep ≠ 180°, two arcs satisfy those constraints (a short one and a long one). `IsLargeArc` picks which.

**Rule:** `IsLargeArc = sweepDegrees > 180`. Get this wrong and 100 % throttle suddenly renders as a tiny 100° arc going the wrong way around the ring.

### 3. **Freeze the geometry** before returning

A frozen `PathGeometry` is:

- Immutable (caller can't mutate it and corrupt other bindings).
- Thread-safe (the WPF render thread can read it without locks).
- Cheaper to render (skips change-notification subscriptions).

At 20 rows × 5 Hz that's 100 geometries/sec. Without freezing you're handing the render thread mutable objects and accruing per-object event-handler bookkeeping. **Always freeze before `return`.**

### 4. `Path` swallows mouse hits — set `IsHitTestVisible="False"`

The picker's row is clickable to switch drivers. The cluster sits inside that clickable area. Without `IsHitTestVisible="False"` on the `Path`, clicks that land on the arc itself get caught by the path geometry and **don't** bubble to the row, so clicking certain drivers silently does nothing. Fast fix, but very confusing to diagnose because it only happens on rows where the arc is wide enough to overlap your click position.

### 5. Avoid the "tiny dot at 0 %" artifact

A `0.0001%` throttle reading produces a degenerate `ArcSegment` that WPF renders as a 1-pixel dot at the start point. Looks like a paint glitch. Solution: convert returns `Geometry.Empty` for anything below `MinVisibleThrottle` (we use 0.5 %). At 0 % the ring is just clean dark — exactly what broadcast TV does.

### 6. Don't widen `StrokeThickness` past a third of `Radius`

A 14 px radius with a 5 px stroke starts looking lumpy and the round line caps stop joining smoothly with the arc body. 3 px against `Radius=14` is the right ratio. Scale both if you want a bigger cluster.

---

## Per-driver batch event pattern (extending an existing poll loop)

Adding gear / throttle / RPM to every row would have been wasteful as three separate poll loops. The picker already had a `PickerTelemetryClient` polling MV CarData every 200 ms for **speed** per car. The pattern: harvest the new channels in the same single pass, fire a new batch event with all of them at once.

```csharp
// New record on PickerTelemetryClient
public sealed record DriverInputs(int Gear, double Throttle, double Rpm);

// New event next to the existing OnSpeedsBatch
public event Action<Dictionary<string, DriverInputs>>? OnInputsBatch;

// Inside the existing CarData parser — same JSON walk, three more reads.
// (Project uses Newtonsoft JObject/JToken — adjust to your serializer.)
foreach (var carProp in carsObj.Properties())
{
    if (carProp.Value?["Channels"] is not JObject ch) continue;

    speeds[carProp.Name] = TryReadDouble(ch["2"]);                            // already there
    inputs[carProp.Name] = new DriverInputs(                                 // added v1.7.4
        Gear:     ch["3"] is JToken g ? (int)TryReadDouble(g) : 0,
        Throttle: ch["4"] is JToken t ? TryReadDouble(t)      : 0,
        Rpm:      ch["0"] is JToken r ? TryReadDouble(r)      : 0);
}

OnSpeedsBatch?.Invoke(speeds);   // existing
OnInputsBatch?.Invoke(inputs);   // added
```

Then the `MainWindow` subscribes once at startup and dispatches to the right row by racing number:

```csharp
_telemetry.OnInputsBatch += dict =>
    Application.Current.Dispatcher.BeginInvoke(() =>
    {
        foreach (var row in _rows)
            if (dict.TryGetValue(row.RacingNumber, out var inp))
            {
                row.Gear     = inp.Gear;
                row.Throttle = inp.Throttle;
                row.Rpm      = inp.Rpm;
            }
    });
```

### Why a batch event (and not three separate events)

- **One dispatch per poll**, not three. WPF dispatcher trips have fixed overhead; doing 3 round-trips per poll triples the cost for no benefit.
- **Atomic-feeling updates**. If gear and throttle update on different frames, you can briefly see N + 100 % throttle (impossible mid-shift) — visually jarring on slow-mo replays. One event = one frame.
- **No new poll loop to manage**. The 200 ms cadence, retry logic, and JSON parsing already exist.

---

## INotifyPropertyChanged dependent-property fan-out

A subtle one. `DriverTimingRow` has:

```csharp
public int Gear { get => _gear; set { if (SetField(ref _gear, value)) ... } }
public string GearText => _gear switch { 0 => "N", < 0 => "R", _ => _gear.ToString() };
```

The XAML binds `GearText`. If you only fire `PropertyChanged(nameof(Gear))` from the setter, **`GearText` will never refresh on the UI** — WPF only re-evaluates bindings whose source property fired a change notification.

Fix: manually raise both inside the setter:

```csharp
public int Gear
{
    get => _gear;
    set
    {
        if (SetField(ref _gear, value))
            OnPropertyChanged(nameof(GearText));   // fan-out for the computed prop
    }
}
```

Same pattern for `Rpm` → `RpmText`. Easy to miss if you've done a lot of code-behind UI but less MVVM with computed wrappers.

---

## Broadcast-layout conventions (why these choices)

These are the visual choices the F1 international broadcast UI uses. Matching them isn't required, but it makes the cluster feel "obviously F1" to anyone who watches the sport.

| Element | Convention | Why |
|---|---|---|
| Throttle arc direction | **Clockwise** | Reads as "filling up" — same metaphor as a fuel gauge or rev counter |
| Arc gap | **At the bottom** (start ~140°, sweep 260°) | Leaves room visually for a label *below* the ring (we use RPM); the bottom is where viewers' eyes drop to next |
| Arc colour | **Blue** (`#3399FF`) | F1 broadcast uses blue for throttle, green for brake-applied confirmation light, **red** for "off-throttle / coasting" indicator. We only render throttle, hence blue. |
| Gear letter | **White, centered, big** (FS=18 in a 34 px ring) | The gear is the single most-glanced number during a session; everything else is supporting context |
| Ring background | **Dark, low contrast with the row** (`#0F0F14` fill, `#3A3A44` stroke) | The ring is a frame, not a feature. Letting it pop visually competes with the gear letter for attention. |
| RPM line | **Below**, white, integer | RPM is high-cardinality (4 digits, changes every frame), so it goes on its own line so it doesn't crowd the cluster |

### Why we shipped white RPM, not red (v1.7.6)

First pass (v1.7.4) used red `#FF4040` at FS=10 SemiBold for the RPM digit. Looked good in mockups, was hard to read in practice — red-on-dark with a small font fights the row's own colour-coded status pills (`IN PIT` red, etc.) for attention. v1.7.6 bumped it to white `#E8E8EE` at FS=12 Bold. Reads at a glance, doesn't crowd the 50 px column. Lesson: **on dark UI, reserve red for things that mean "stop / pit / abandon"** — don't spend it on neutral telemetry.

---

## Performance notes

At 20 visible rows × 5 Hz poll rate:

- **100 `PathGeometry` allocations/sec** — frozen, GC'd promptly. Negligible.
- **One dispatcher invoke per poll** — cheap.
- **No virtualisation** — picker's `ItemsControl` holds all 20 rows in memory always. Sub-millisecond layout passes.
- **WPF render thread** does the actual stroke rasterisation — happens off the UI thread, no main-thread blocking.

Tested on Vic's race rig (RTX 4090, i9-13900K). Picker uses ~0.5 % CPU steady-state with a live MV session. Could probably scale to 100 drivers + 10 Hz before anything becomes noticeable.

---

## Extending the pattern

If you want to add a **brake arc** (green, overlaid on the same ring) or an **ERS bar** (vertical, beside the ring):

1. **Brake arc** — copy `ThrottleToArcGeometryConverter` as `BrakeToArcGeometryConverter`. Change colour to green `#33CC66` and consider sweeping *counter-clockwise* from the start angle so the two arcs grow toward each other (visually distinguishable when both are applied during a corner). Keep the same start angle so they share the bottom gap. MV CarData channel for brake is `5` (binary on/off in current F1, treat any value ≥ 1 as 100 %).
2. **ERS bar** — that's a `ProgressBar` with an `Orientation="Vertical"` and a custom dark template. Or use the same converter pattern with a `RectangleGeometry` that grows top-down. F1 SignalR doesn't currently expose ERS — you'd need to scrape it from MV's session reconstruction.
3. **DRS pip** — a tiny `Border` (8×8 cornered) that flips visible/hidden on a `BoolToVisibility` converter against MV CarData channel `45` (`0` = off, `8`/`10`/`12`/`14` = available/active states).

The picker's repository file `picker/Services/PickerTelemetryClient.cs` has comments at the top of the file listing every CarData channel it currently parses — add the new channel reads alongside, fire from the same `OnInputsBatch` (extend the record), bind in the row.

---

## File index — where the v1.7.4 – v1.7.6 changes live

| File | What it has |
|---|---|
| [`picker/Services/ThrottleToArcGeometryConverter.cs`](../picker/Services/ThrottleToArcGeometryConverter.cs) | The `IValueConverter` from throttle 0..100 to frozen `PathGeometry`. **Self-contained — copy this file as a starting point for any new arc converter.** |
| [`picker/Services/PickerTelemetryClient.cs`](../picker/Services/PickerTelemetryClient.cs) | MV polling client. `DriverInputs` record + `OnInputsBatch` event are the entry points for any new per-driver telemetry. |
| [`picker/Models/DriverTimingRow.cs`](../picker/Models/DriverTimingRow.cs) | The per-row VM. `Gear` / `Throttle` / `Rpm` observable props + computed `GearText` / `RpmText` (with INPC fan-out for the computed ones). |
| [`picker/MainWindow.xaml`](../picker/MainWindow.xaml) | XAML — converter registration at top of `Window.Resources`, per-row cluster XAML at column index `2` of the row template (`DataTemplate DataType="{x:Type models:DriverTimingRow}"`), focused-driver header cluster in the LED preview `Border`. |
| [`picker/MainWindow.xaml.cs`](../picker/MainWindow.xaml.cs) | Event wiring — `OnInputsBatch` subscription that dispatches to the right row by racing number. |

---

## One process lesson, separate from the code

**The v1.7.3 wrong-host mistake.** First implementation pass put the input cluster on the wheel's LCD dashboard (`F1RaceSim_GSIFPEV2.djson`) instead of the picker. User's intent was "match the F1 Live Timing per-row layout in the picker" — agent misread "between driver name and km/h" as referring to the wheel's name/speed labels rather than the picker's row anatomy.

**What worked for the recovery:**

1. A `.bak` of the dashboard JSON existed from the deploy script — byte-for-byte restore was trivial.
2. The wrong work was on a feature branch (`feat/throttle-gear-rpm-cluster`), so reverting the dashboard + landing the picker work shipped as a single clean PR with no v1.7.3 mistake in main's history (squash merge).
3. The plugin's auto-deploy target had already pushed the wrong dashboard to SimHub — redeploying the restored bytes overwrote it cleanly with no SimHub restart needed.

**Lesson for future contributors** (and any AI agents working on this repo): when a request says "match this screenshot", **always confirm the host** (wheel LCD vs picker desktop window vs SimHub OSD overlay) before touching code. The F1SimHubLive project has three render surfaces; "the visual layer" is ambiguous here in a way it usually isn't in single-window apps. One disambiguating question at the top of the task saves a wrong-host shipping cycle.

---

*This doc lives alongside the code, not in a wiki. If you change `ThrottleToArcGeometryConverter`, the cluster XAML, or the `OnInputsBatch` contract, please update the relevant section here in the same PR so the next person doesn't have to re-derive the geometry math.*
