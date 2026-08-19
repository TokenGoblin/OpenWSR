# Code and feature audit — 2026-08-18

> **Status: all 21 findings closed, same day.** This file is kept as the record of what was
> wrong and why it mattered; `TASKS.md` carries the remediation log, and the fixes are
> verified live against KMTX (all three data modes, loop, and GIF export). Where a finding
> describes present-tense behaviour below, read it as the behaviour *before* the fix.

A full read of the tree at `968edf0`: 80 C# files, ~11,240 lines, 927 lines of XAML.
Build clean, 0 warnings. 76/76 tests green (34 Geo, 42 Nexrad).

The decoders are the strongest part of this project and the audit found nothing wrong
with them — they are golden-tested against MetPy and ecCodes and the methodology in
`verification.md` is genuinely better than most commercial radar software. Everything
below is about the layers that sit on top: the app controllers, the render helpers, and
above all the UI, which is where the whole codebase's untested surface lives.

**Headline:** one shipped feature cannot work (GIF export), one input bug corrupts the
radar product while you type a city name, and the single most-used control in a radar
viewer — product selection — has no UI at all.

---

## Code findings

Severity uses the app's own NWS convention: **critical** = the feature cannot work,
**major** = wrong behaviour a user will hit, **moderate/minor** = correctness or
maintenance debt.

### C-01 — critical — Animated GIF export can never produce a file

`MainWindow.xaml.cs:755` calls `_playback.StopLoop()`, which sets `_loop = null`
(`ArchivePlaybackController.cs:192`). The next line reads `_playback.LoopGeometryCount`,
which is now 0, so the capture loop body never executes and the method always falls
through to `"Nothing captured."`

The dialog compounds it: the GIF filter is only offered when `LoopGeometryCount > 0`
(`MainWindow.xaml.cs:711`), which is only true while a loop is playing — so the sole path
to the feature is the one that destroys its own input.

This matches the note in `CLAUDE.md` that GIF export "has never been run end-to-end."
It is not that it was never run; it cannot succeed as written.

**Fix.** Split `StopLoop()` into `PauseLoop()` (stop the timer, keep `_loop`) and
`ClearLoop()`. `SaveLoopGifAsync` and `ScrubToAsync` want the former.

### C-02 — major — Typing in the search box changes the radar product

`MainWindow.xaml.cs:65` binds `Window.KeyDown` directly to `_radar.OnKey`. `Window.KeyDown`
is a bubbling routed event and a `TextBox` does not mark character keys handled, so every
keystroke typed into `SearchBox` is also a radar command:

- typing `Vail` → **V**elocity
- typing `Denver` → **D**ifferential reflectivity, then **R**eflectivity
- `↑`/`↓` in `SiteCombo` changes the site *and* the tilt

**Fix.** Ignore the key when `Keyboard.FocusedElement` is a text-entry control, and prefer
routing through `MapView.KeyPressed` (`MapView.cs:91`), which already fires from the D3D
child HWND and therefore only when the map has focus.

### C-03 — major — The moment/tilt handler is wired twice

`_mapView.KeyPressed += key => _radar.OnKey(key)` at `:64` and `KeyDown += … OnKey` at
`:65`. When the D3D child window has focus, `WM_KEYDOWN` raises `KeyPressed`
(`D3DHostControl.cs:95`) and `HwndHost` also surfaces the key into WPF's routed-event
tree, so `↑`/`↓` most likely steps **two tilts per press**. Product keys hide the double
fire because `SetMoment` returns early when the moment is unchanged; `MoveCut` does not.

Verify against the running app, then fix with C-02 — one source of map keys, not two.

### C-04 — major — `LiveFeed.Stop()` blocks the UI thread for up to 5 seconds

`LiveFeed.cs:45` does `Task.WhenAll(poller, decoder).Wait(TimeSpan.FromSeconds(5))`, and
that method is called from the UI thread in three places: `LiveToggle_Unchecked`,
`Dispose()` during window close, and twice consecutively inside `HotspotButton_Click`
(`MainWindow.xaml.cs:983-984`, which toggles Live off then on to re-point the feed).

Turning Live off, or pressing 🎯 while live, can freeze the window while the poller
finishes an in-flight S3 request.

**Fix.** Expose `StopAsync()` and await it, or cancel the CTS and let the drain complete
on a background continuation.

### C-05 — major — A geocoder failure takes down the app

`SearchBox_KeyDown` (`:1035`) is `async void` with `try`/`finally` and **no** `catch`. A
DNS failure or a 5xx from the geocoder propagates to the dispatcher, and
`App.xaml.cs:22` logs `DispatcherUnhandledException` without setting `args.Handled = true`
— so the process dies.

Nine other `async void` handlers exist; the rest catch. This one is the gap. Add a catch,
and set `Handled = true` in the global handler for non-fatal exceptions so a single failed
fetch is a status message rather than a crash.

### C-06 — moderate — Proximity alerts measure to polygon vertices, not edges

`ThreatMonitor.cs:75` computes `nearestKm` as the minimum distance from home to each
ring's *points*. A warning polygon whose nearest **edge** passes 5 km from home but whose
nearest **vertex** sits 60 km away will not fire inside a 40 km radius. NWS warning
polygons are coarse — typically 4 to 8 vertices — so long edges are the normal case, not
an edge case.

**Fix.** Point-to-segment distance across consecutive vertex pairs. The math already
exists next door in `GeoMath.ClosestApproachToPath`, which the storm-track path uses
correctly.

### C-07 — moderate — Storm symbols are sized in Mercator metres, so they don't survive zoom

`StormOverlayController` sizes its glyphs in world units: cell diamonds 900 m, forecast
markers 700 m shrinking by 120 m per step, hail triangles `1200 + 40 × POSH` metres. At
CONUS zoom (~5 km/px) every one of them is sub-pixel; at street zoom a severe-hail
triangle spans the screen.

`MapView.DrawMarkers` already does this correctly — `double half = 4 * cam.MetersPerPixel`
gives a constant 8 px square. Line *widths* are also correct: `OverlayRenderer.cs:207`
converts `WidthPx` through `MetersPerPixel` per frame. Only the vertex positions of the
symbol helpers are wrong.

Mesocyclone circles are a genuine physical radius and should stay in metres.

**Fix.** Pass `cam.MetersPerPixel` into the symbol helpers, or emit symbols in a screen-space
pass alongside the markers.

### C-08 — moderate — Camera link yanks the view after a pane-count change

`PaneManager._lastSnapshots` (`PaneManager.cs:26`) is a fixed array indexed by pane
position, and `SetPaneCount` never resets it. After adding or removing panes, the stale
entry at an index makes `SyncCameras` pick the wrong pane as "the one that moved" on the
next tick, and every other pane snaps to it.

**Fix.** Clear `_lastSnapshots` at the end of `SetPaneCount`.

### C-09 — minor — `CommunityToolkit.Mvvm` is a dead dependency

Referenced in `OpenWSR.App.csproj`, used nowhere: zero `ObservableObject`,
`RelayCommand` or `INotifyPropertyChanged` in the tree, and 4 `Binding` expressions total,
all inside the placefile `DataTemplate`. Either adopt it (see U-08) or drop the reference.

### C-10 — minor — Alert poll interval and client-side cache are both exactly 60 s

`WarningsController` ticks every 60 s (`:47`); `AlertsClient.GetActiveAsync` returns the
cached set when called within 60 s of the last fetch (`AlertsClient.cs:41`). A tick that
arrives a millisecond early is silently a no-op, giving 120 s latency on that cycle. Make
the cache window shorter than the poll — 50 s keeps the API guard without eating scheduled
refreshes.

Related: the client fetches the entire national alert set every minute with no `area=`
filter and no backoff on failure. `TASKS.md:116` still lists "backoff on error" as part of
this task.

### C-11 — minor — `TrayNotifier.Activated` has broken event accessors

`TrayNotifier.cs:26`: every `add` subscribes a fresh lambda to `BalloonTipClicked`, and
`remove` does nothing at all. It works only because there is exactly one subscriber and
the object lives for the process lifetime. Use a plain field-like event.

### C-12 — minor — No test covers App, Render or Ingest

All 76 tests live in `Nexrad`, `Geo`, `Grib2`, `Placefiles` and `Palettes` — the pure
layers that are already cross-validated against MetPy and ecCodes. The ~2,900 lines of app
controllers and ~1,900 lines of render code have none, and that is exactly where C-01
through C-08 live.

Several of these are pure and directly testable with no WPF or D3D involved:

| Target | What the test asserts |
|---|---|
| `ThreatMonitor.ClosestApproach` | ETA and distance against a hand-computed track; C-06's edge case |
| `RadarDisplayController.SetMoment` | nearest-elevation carry-over when the new moment has different cuts |
| `Units` | round-trips for all three systems |
| `MapView.NiceStep` | 1 / 2 / 2.5 / 5 × 10ⁿ selection |
| `GifWriter.WriteAnimated` | the patched stream still parses as a GIF with N graphic control extensions |
| `ArchivePlaybackController` loop lifecycle | C-01 directly: frames survive a pause |

### C-13 — minor — `TASKS.md` checkboxes are stale

Thirteen items are unchecked that demonstrably shipped: `GeoMath.Offset`, the alerts
poller, warning tessellation, the `.pal` parser, the hover inspector, the measuring tool,
the Channels pipeline, adaptive polling. `CLAUDE.md` asks for `TASKS.md` to be updated in
the same commit as the feature; the file currently under-reports the project.

---

## Feature audit

**Working and independently verified.** Level II (MetPy), Level III, GRIB2 (ecCodes),
Lambert projection, placefiles, tile engine, live chunk assembly, archive browsing,
cross-section, storm-relative velocity, warnings, SPC layers, storm tracking, hotspot
scan, proximity alerts, palette import, multi-pane, PNG capture. This is a large and
genuinely working feature set.

**Shipped but non-functional.** Animated GIF export — see C-01.

**Degraded by upstream, and the UI doesn't say so.** NSS (Storm Structure) stopped being
distributed around 2021, so `TrackedStorm.MaxDbz`, `CellBasedVil` and `EchoTopKft` are
always null on live data. The layers panel offers a checkbox labelled **"Cell ID + max
dBZ"** that on live data can only ever draw the ID. Either relabel it or show a note when
NSS is absent — `CLAUDE.md` already records this, but a user never reads `CLAUDE.md`.

**Genuinely open**, unchanged from `parity.md`: lightning (GOES GLM, needs NetCDF), MRMS
native rendering (reader done, draw path missing), velocity dealiasing, azimuthal shear,
VWP panel, placefile icon sheets, drawing tools, 3D volume, independent site per pane,
loops longer than 30 frames.

---

## The UI

`parity.md` closed 8 of its 11 UI findings. The findings below are the ones that survived,
plus new ones the later features introduced. This is where the largest remaining gap
between what OpenWSR *can do* and what a user can *reach* sits.

### U-01 — critical — The two most-used controls have no UI at all

Selecting the moment (REF / VEL / SW / ZDR / PHI / RHO) and changing the elevation tilt
are the two things a radar operator does constantly. Both are keyboard-only —
`R`/`V`/`W`/`D`/`P`/`C` and `↑`/`↓` — and they are documented in exactly one place: a
paragraph at the bottom of the `MessageBox` behind the ⓘ button (`MainWindow.xaml.cs:1010`).

There is no product selector, no tilt selector, and no indication that the volume contains
17 elevation cuts and six moments. A first-time user cannot find velocity. The status bar
reports the current selection after the fact but offers no way to change it.

This is the single highest-value change in the whole audit.

### U-02 — major — Three data modes, no mode control

Live, Archive and Forecast are mutually exclusive ways of answering "when", and each is
expressed differently in a different place:

| Mode | Where it is | Its transport |
|---|---|---|
| Live | `⦿ Live` toggle, top bar | none — auto |
| Archive | DatePicker + "Load day", top bar | play/speed/slider, bottom bar |
| Forecast | "Load HRRR forecast" button, **right panel** | `◀ ▶ ▶\|` and Clear, **right panel** |

Two transports in two places is the same defect `parity.md` recorded as finding 05 and
marked done — the HRRR feature reopened it by growing its own playback controls inside
the layers panel.

The mode switches also have invisible side effects. `LiveToggle_Checked` silently disables
the time slider and moves the camera; loading an archive day does not clear the HRRR
overlay, so a forecast raster can sit over a 2013 volume with nothing on screen explaining
the mismatch.

### U-03 — major — The right panel is one scroll doing six jobs

238 px wide, ~30 controls, nothing collapsible, everything always expanded. It currently
contains: display tuning (radar opacity, smoothing), layer visibility (7 national + 4
warning + 6 storm checkboxes), a legend explaining storm symbols, a placefile manager, a
forecast playback transport, and home/alert-radius configuration.

Only the mosaic has a per-layer opacity slider. `parity.md` finding 07 names per-layer
opacity and reordering as the remaining gap and it is still open.

### U-04 — major — Six gestures share two mouse buttons, with no visible mode

- **Hover** → inspector readout
- **Left-click** → routes through set-home, then storm hit-test, then warning hit-test
  (`RouteMapClick:394`)
- **Right-drag** → measure distance/bearing
- **Right-drag, again** → cross-section slice, when `CrossSectionToggle` is on

Measure and cross-section are both bound to right-drag and both stay live simultaneously —
`InspectorTools.OnMeasureDrag` and the cross-section handler in `MainWindow` are separate
subscribers to the same `MeasureDragged` event. Nothing on screen says which tool is armed
except the `⌇` rail toggle and the temporarily relabelled "Click the map…" button.

### U-05 — major — Every outcome shares one status line

`StatusText` receives: archive list failures, decode failures, live poll errors, alert
fetch failures, Level III fetch failures, palette import results, hotspot scan progress,
threat titles, HRRR progress, placefile status, and the current sweep readout — which
rewrites it on every tilt change.

An `"Alerts fetch failed: …"` message is overwritten by the next sweep in well under a
second, and there is no history. This is `parity.md` finding 08, still partial.

### U-06 — moderate — The debug marker layer shipped

`MapView.SetMarkers` is called once at startup with all 210 non-TDWR sites
(`MainWindow.xaml.cs:59`), and `DrawMarkers` draws each as an 8 px red square at every
zoom level with no toggle, no labels, and no visual distinction for the *selected* site.
`TASKS.md:64` introduced this as a "debug marker layer … for alignment check"; it was
never promoted into a real layer or removed.

At CONUS zoom it is a red rash across the country, over the top of the national mosaic
that the same zoom level is there to show.

### U-07 — moderate — First run shows a 2013 storm with no explanation

`DayPicker.SelectedDate = new DateTime(2013, 5, 20)` (`:87`) — the Moore tornado day. A
new user's first screen is a decade-old event with nothing indicating it is a demo.
`parity.md` finding 10 is still open and correctly diagnosed.

### U-08 — moderate — The code-behind is why reorganising the UI is hard

`MainWindow.xaml.cs` is 1,122 lines with 33 event handlers, and control state **is** the
model: `LiveToggle.IsChecked` is the live/archive flag, `_paneCount` is a private int
mirrored into a button's `Content`, `FilterHail.IsChecked` is the storm filter. Nine
handlers exist purely to copy checkbox state into a controller property.

Any of the layout changes below require moving a control from one container to another,
and every such move currently means touching imperative code that reads the control by
name. This is the enabling fix, not a nice-to-have.

---

## How to organize the UI

**The organizing principle.** Using this app is a loop of four questions, always in the
same order:

> **Where** am I looking → **When** is this → **What** product → **What's on top**

Give each question exactly one place on screen, and never a second. Today: *where* is in
the top bar, *when* is split across the top bar, the bottom bar and the right panel,
*what* has no UI at all, and *what's on top* is a thirty-item scroll.

### Proposed layout

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ⛈ OpenWSR   [ 🔍 Search a place or site … ]   KTLX · Oklahoma City ▾   ⚙  ⓘ │  WHERE
├────┬────────────────────────────────────────────────────────┬────────────────┤
│    │  REF  VEL  SW  ZDR  KDP  CC        0.5° · tilt 1/17 ▾▲▼ │  ▼ RADAR      │  WHAT
│ ▤  ├────────────────────────────────────────────────────────┤    opacity ──● │
│ 🎯 │                                                        │    smooth  ●── │
│ ◱  │                                                        │                │
│ ⛓  │                    M A P                               │  ▼ LAYERS     │  ON TOP
│    │   ┌──────┐                                             │   ✓ Mosaic ──● │
│ ▶  │   │legend│                              76.5 dBZ       │   ☐ Satellite  │
│ 📐 │   └──────┘                              az 214°        │   ✓ Warnings ⋮ │
│ ⌇  │                                         41.2 mi        │   ✓ Storms   ⋮ │
│ 📍 │                                         4,180 ft       │   ☐ SPC      ⋮ │
│    ├────────────────────────────────────────────────────────┤   ☐ Forecast   │
│    │ ⦿LIVE  ARCHIVE  FORECAST │ ◀◀ ▶ ▶▶ ══●═════ 20:16:31Z │                │  WHEN
├────┴────────────────────────────────────────────────────────┤  ▼ PLACEFILES  │
│ LIVE · 2 m 14 s old      KTLX 20:16:31Z REF 0.5°   60 fps   │   ✓ IEM warns  │
└─────────────────────────────────────────────────────────────┴────────────────┘
```

### 1. Add a product bar across the top of the map — do this first

A segmented control `REF | VEL | SW | ZDR | KDP | CC`, with only the moments actually
present in the loaded volume enabled, and each button carrying its keyboard letter as a
subscript so the existing shortcuts get a visible home.

Beside it, a tilt control reading `0.5° · tilt 1 of 17` with `▲`/`▼` steppers and a
dropdown listing every elevation angle in the volume — `RadarDisplayController.CutsForMoment`
already computes exactly this list.

This alone converts the app from "powerful if you read the About box" to
"self-explanatory." It is also cheap: the controller API (`SetMoment`, `MoveCut`,
`CurrentMoment`) already exists and is already correct, including the nearest-elevation
carry-over when switching moments.

### 2. Collapse the three data modes into one switcher that owns the time bar

A three-way segmented control — `LIVE · ARCHIVE · FORECAST` — at the left of the bottom
bar. The rest of that bar is whatever the active mode needs:

- **LIVE** — age indicator, feed status, no slider
- **ARCHIVE** — date picker, transport, scrub slider with hour ticks, loop speed
- **FORECAST** — HRRR load / step / play / clear, valid-time readout

The forecast transport moves out of the layers panel. There is now one transport, one
timeline and one place that answers "when". Switching modes becomes an explicit action
with visible consequences instead of a toggle with silent side effects — and the mutual
exclusion the code already enforces becomes something the user can see.

### 3. Restructure the right panel into collapsible sections, and evict what isn't a layer

Move out:

- **Forecast transport** → the time bar (step 2)
- **Home + alert radius** → Settings. It is configured once and never touched again;
  it does not belong in a panel used every session.
- **Symbol key** → a `?` affordance on the Storms row, or fold it into the map legend.

What remains is two sections, both open by default:

- **Radar** — opacity, smoothing
- **Layers** — one row per layer: `[✓] Name ————●  ⋮`, where the inline slider is that
  layer's opacity and `⋮` expands its sub-filters (warning types, storm sub-layers, SPC
  product selection). This closes `parity.md` finding 07 and makes the panel scannable at
  a glance instead of a wall of equally-weighted checkboxes.

Below, collapsed by default: **Placefiles**.

### 4. Make map tools an explicit, mutually exclusive mode

Group Inspect / Measure / Cross-section / Set-home as **radio** buttons in the rail, not
independent toggles. Right-drag then means exactly one thing at a time, the armed tool is
visible without reading a button's label, and `RouteMapClick`'s priority chain becomes a
simple switch on the current tool rather than an implicit precedence order.

Show the current tool's gesture hint in the status bar while it is armed
("Right-drag to slice"), not as a one-shot message that the next sweep overwrites.

### 5. Separate messages by severity

Keep `StatusText` for the transient sweep readout, and route everything else by kind:

- **Errors** → a dismissible inline bar under the top bar, which persists until dismissed
- **Progress** (hotspot scan, HRRR fetch, loop build) → a determinate progress strip in
  the time bar
- **Threats** → the existing toast plus tray balloon, unchanged; they already work

Keep the last ~50 messages in a small openable log. Serilog is already writing them to
disk; surfacing them costs almost nothing.

### 6. Turn the radar-site markers into a real layer

Gate them on zoom (hide above ~2 km/px, where the national mosaic is the subject), add
ICAO labels at close zoom, draw the *selected* site differently, and give the layer a
toggle in the Layers section. Site markers are useful — they just should not be a
permanent red rash over the national view.

### 7. Fix the first run

Open on the national mosaic at CONUS zoom with an empty-state hint — *"Pick a radar site,
search a place, or press 🎯 to jump to the heaviest weather in the country"* — or, when a
home location is saved, on that home's nearest radar in Live mode. Keep the Moore volume
as an explicit **"Load the 2013 Moore demo"** item in the ⓘ menu, where it reads as a
showcase instead of a stale default.

### 8. Add a real help surface

`F1` (and `?`) opens a card listing the keyboard shortcuts and mouse gestures, with the
same content shown once on first run. Right now this information exists only inside an
`About` dialog most users will never open.

### 9. The enabling refactor

Steps 1–3 all move controls between containers, and today every such move means editing
imperative code that finds the control by name. Extract a `MainViewModel` holding the
state that is currently spread across control properties — `Mode`, `SelectedSite`,
`CurrentMoment`, `TiltIndex`, layer visibilities and opacities — and bind to it.

`CommunityToolkit.Mvvm` is already referenced and unused (C-09), so this costs no new
dependency. It does not need to be a big-bang rewrite: move the *mode* and *product* state
first, since those are what steps 1 and 2 need, and leave the rest in code-behind until
it gets in the way.

---

## Suggested order

1. **C-01** — GIF export. One shipped feature, currently impossible; a few lines.
2. **C-02 / C-03** — the key routing. Silent data corruption while typing.
3. **UI step 1** — the product and tilt bar. Highest user-visible value in the audit.
4. **C-04 / C-05** — the UI-thread block and the crash-on-search.
5. **U-08 / step 9** — the view model, scoped to mode and product state.
6. **UI steps 2 and 3** — the mode switcher and the layers panel.
7. **C-06 / C-07** — alert edge distance and symbol scaling; both are correctness.
8. **C-12** — tests for the app layer, starting with the controllers touched above.
