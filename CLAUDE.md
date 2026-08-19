# OpenWSR — working notes

A native Windows NEXRAD radar viewer: .NET 10, WPF shell, Direct3D 11 via Vortice, own
Web Mercator tile engine. Reads only public, unauthenticated data. See `README.md` for
what it does and `TASKS.md` for the build history.

**Before touching a decoder, read `docs/formats.md`** — it holds the field notes for
every binary format here, specifically the parts where the spec and the live feed
disagree. The other reference docs:

| | |
|---|---|
| `docs/formats.md` | Level II / Level III / GRIB2 / placefile gotchas |
| `docs/verification.md` | The golden-test methodology and how to run a cross-check |
| `docs/data-sources.md` | Endpoints, specs, and which ones have already moved |
| `docs/parity.md` | Competitive gaps, annotated with what's closed |
| `docs/audit.md` | The 2026-08-18 code/feature/UI audit — 21 findings, all closed |
| `resources/crosscheck/` | MetPy and ecCodes dump scripts — run these before trusting a decode |

Anything worth keeping goes in `docs/` or `resources/`, not in a scratch directory.
Scratch directories are session-scoped and get lost.

## Commands

```
dotnet build OpenWSR.slnx                    # NOTE: .slnx, not .sln
dotnet test OpenWSR.slnx                     # 229 tests
dotnet run --project src/OpenWSR.App
dotnet publish src/OpenWSR.App -c Release    # single-file self-contained exe
```

The published and Debug binaries are both **`OpenWSR.exe`** (AssemblyName is set), not
`OpenWSR.App.exe`.

## Architecture and the rules that hold it together

```
Nexrad ──┐
Grib2 ───┤
NetCdf ──┼─→ Ingest ──┐
Geo ─────┤            ├─→ App
Placefiles┤  Render ──┘
Palettes ─┘
```

`Placefiles` depends on `Geo` — the only edge between the pure libraries. A drawn circle
has to be written out as a ring of a true *ground* radius, which is geodesy, not geometry.

**Purity is enforced by a test.** `Nexrad`, `Geo`, `Grib2`, `NetCdf` and `Placefiles` must not
reference WPF, Direct3D or the network. `PurityTests` asserts this against assembly
references — if you need imaging or HTTP in one of them, that is a signal the code
belongs somewhere else. `MiniPng` exists inside `Grib2` precisely because of this rule.

**The shell is arranged around four questions**, each with exactly one place on screen
and never a second: **where** (top bar: search, site) → **what product** (the segmented
bar above the map) → **when** (the time bar: one `LIVE · ARCHIVE · FORECAST` switcher that
owns the transport) → **what's on top** (the right panel, which holds layers and nothing
else). Before adding a control, decide which question it answers and put it there. Set-once
configuration goes in Settings, not the layers panel; reference material (shortcuts, the
symbol key, About) goes in `InfoWindow`, not a panel or a MessageBox.

`MainViewModel` holds the state the layout is built from — mode, product, tilt, armed map
tool. Keep it there rather than in control properties, or moving a control between
containers means rewriting the logic that reads it.

**Threading.** The UI thread does UI. The render thread owns the D3D device context and
every D3D object. Ingest and decode run on the thread pool. Cross boundaries with
channels, immutable records, or a lock — never by touching a D3D object off the render
thread. Anything the render thread must do for a caller (frame capture, texture upload)
is queued as a request and serviced after present.

## Things that cost real time to discover

**A pinned pane owns its own place.** Pinning a pane to a site moves its camera there
and drops it out of camera linking — otherwise the link drags it straight back and you are
looking at the right data over the wrong ground. Pane state (including the link snapshot)
lives on the `Pane` object rather than in arrays indexed by position, because that position
shifts whenever panes are added, removed or pinned.

**Airspace.** The D3D child HWND always draws above WPF content inside its rectangle.
WPF controls cannot overlay the map. Anything that must appear *over* the map is either
drawn in the D3D scene (the colour scale, storm labels — see `GlyphAtlas` and
`DrawLegend`) or is a `Popup`/`ToolTip`, which get their own HWNDs. Docked panels beside
or below the map are fine.

**Automating the UI: use `Checked`, not `Click`, on ToggleButtons.** `TogglePattern.Toggle()`
does not raise `Click` in WPF, so `Click`-handled toggles are unreachable to assistive
technology and to UI-automation tests. Handle `Checked` (with a suppress flag) and re-check
in `Unchecked` to keep radio behaviour. Glyph-only controls need
`AutomationProperties.Name` — a screen reader announces "▤" otherwise.

**Coordinate-based clicking fights the foreground.** Other windows steal it mid-run and the
clicks land somewhere else entirely; prefer UI Automation patterns, and check
`BoundingRectangle` is on-screen before falling back to a synthetic click.

**Screenshots lie unless the capturing process is DPI-aware.** PowerShell is not, so
`GetWindowRect` and `CopyFromScreen` return virtualised coordinates and you capture about
two-thirds of the window — which looks exactly like a broken layout. Always
`SetProcessDPIAware()` first. Several hours went into chasing a phantom layout bug here.

**WPF's default control templates are unreadable on a dark ground.** `Theme.xaml`
provides explicit templates for Button, ToggleButton, ComboBox, TextBox, CheckBox,
Slider, DatePicker, ScrollBar and ToolTip. Style through it; don't hardcode colours.
The ToolTip one is not optional: a tooltip inherits `Foreground` from the control it
belongs to, so every one of them was near-white text on WPF's default *light* popup —
invisible. Its wrap width has to sit on the inner `TextBlock`; a `MaxWidth` on the ToolTip
lets the text measure unconstrained and then clips the end of the sentence off.

**Map keyboard has exactly one route.** `MapView.KeyPressed` (raised from the D3D child
window's `WM_KEYDOWN`) is it. Do not also handle `Window.KeyDown` for the same keys: it is
a bubbling routed event and a `TextBox` does not mark character keys handled, so typing
"Vail" into the search box selected velocity, and arrow keys double-stepped the tilt.
`Window_PreviewKeyDown` handles only the app-level keys and bails when focus is in a
text-entry control or the map host.

**Overlay symbols are sized in screen pixels, not Mercator metres.** A marker drawn 900 m
across is sub-pixel at national zoom and screen-filling at street zoom. Multiply by
`cam.MetersPerPixel` and rebuild when the zoom moves materially (`NotifyViewChanged`).
Only things that *are* a physical extent — a mesocyclone radius, an alert ring — stay in
metres. Line widths are already pixel-constant in `OverlayRenderer`.

**Nothing on the UI thread may block on I/O.** `LiveFeed` exposes `StartAsync`/`StopAsync`;
a blocking `Wait(5s)` in `Stop()` froze the window on every live toggle. Dispose paths
cancel and let the drain finish on a continuation.

**Errors need somewhere to live that isn't the status line.** `Report()` is the running
commentary and is overwritten constantly; `ReportError()` puts a failure in a bar that
persists until dismissed. A failed warning fetch must never look like "no warnings".

**XAML event handlers fire during `InitializeComponent`.** A filter handler that touches
a control declared later in the file will hit a null. Guard every control it reads.

**WinForms is referenced only for `NotifyIcon`.** Its implicit usings are removed in the
csproj (`<Using Remove="System.Windows.Forms" />` and `System.Drawing`) because they
collide with WPF on `Application`, `Color`, `Brushes`, `MessageBox` and `Size`.
`TrayNotifier` imports them explicitly.

**PowerShell gotchas.** `lp` is an alias for `Out-Printer`, so a helper named `LP`
silently breaks pipelines. Never round-trip a UTF-8 source file through
`Get-Content | Set-Content` — it mangles non-ASCII; use `git checkout` plus the Edit tool.
Double quotes inside a git commit message passed via `-m @'...'@` break argument parsing.

## Format notes

**Level II.** Pre-2016 gzip-era archive files have **no LDM record framing** — raw
messages follow the 24-byte header. Message 31 sizes above 65534 halfwords live in the
segment-count fields. Walk Message 31 block pointers and skip unknown block names;
newer RDA builds add blocks. VCP elevation angles are binary angle units, so the nominal
"0.5°" cut reports 0.4834°.

**Level III.** Live symbology is **bzip2** (`BZh`), not only zlib. NSS (Storm Structure)
stopped being distributed around 2021 — the decoder still works for archives, which is
why live cell labels usually show an ID with no dBZ. Stand-alone tabular products put the
tabular pointer in the *symbology* offset slot.

**Velocity aliasing.** The Nyquist velocity comes from the RRAD block, is absent on some
radials (take the first that has it), differs between the split surveillance and Doppler
cuts of VCP 12/212, and is missing entirely from pre-2000 archives — hence
`Sweep.NyquistMs` is nullable. Raw velocity pinned at exactly ±V_nyquist is the signature
of folding, not of real data. Unfolding is region-based and deliberately conservative: a
correction must never cross a hairline region boundary, because corrections chain, and two
stacked guesses turn −22 m/s into +82 m/s. `MinBoundaryGates` carries the measurements
behind its value — read them before touching it.

**The Field overlay slot has three claimants** — the HRRR forecast, the native MRMS
composite, and anything gridded added later. They are mutually exclusive by construction:
enabling MRMS leaves forecast mode, and entering forecast mode switches MRMS off. Native
MRMS and the tile mosaic are the same field, so each turns the other off too.

**Two image-overlay slots.** `MapView.SetImageOverlay` takes an `OverlaySlot`: `Field`
draws under the radar sweep (model output, mosaics) and `Analysis` draws over it (derived
products read *against* the echo). They are independent, so the HRRR forecast and a
rotation-track swath no longer fight over one slot.

**Prefer deriving over decoding, where the maths is honest.** The wind profile is fitted
from Level II velocity rather than decoded from the Level III NVW product, and azimuthal
shear likewise. NVW is still distributed, but this project already lost the storm-structure
cell attributes when NSS stopped being generated around 2021 — deriving keeps a product
working on any volume back to 1991 and makes it impossible to take away. `GeoMath.BeamPath`
and its inverse `GeoMath.SlantRangeForHeight` are the pair that makes height-based products
possible.

**Derived products.** `Moment.AzimuthalShear` sits in the moment enum so the product bar
and palettes treat it uniformly, but the decoder never emits it — `RadarDisplayController`
materialises it from velocity on demand and caches per cut. Add further derived products at
that seam rather than teaching the decoder to invent data. Anything derived from velocity
must dealias first; on a folded field it shows its own artefacts.

**GRIB2.** MRMS uses PNG packing (template 5.41) — easy. HRRR uses complex packing with
second-order spatial differencing (5.3) on a Lambert grid (3.30) — the hard path, and the
one to be careful with. Scale factors are sign-and-magnitude, not two's complement. Fetch
only the field you need using the `.idx` sidecar and an HTTP range request.

**Placefiles.** Icon sheets frequently ship with **no alpha channel** — the IEM wind-barb
sheet is grey artwork on an opaque black ground — and the convention is that black is the
transparent colour when there is nothing else to go on. Key it out only when every pixel
decodes opaque, or you punch holes in a legitimately black icon. Inside an `Object` block,
"lat, lon" are **pixel offsets** from the
anchor with +y upward, not degrees. Polygon contours close on a repeated first point and
the next point starts a new contour. `;` starts a comment except inside quotes. The spec
at grlevelx.com needs a browser User-Agent — WebFetch gets a 403.

**Endpoints drift.** Two IEM paths verified earlier in development had been retired by
the time they were wired up. When something returns HTML instead of data, check
`https://mesonet.agron.iastate.edu/api/1/openapi.json` for the current path. SPC serves
GeoJSON with a **UTF-8 BOM**, which `System.Text.Json` rejects outright.

## How to verify work here

The project's standard is to check binary decoding against an **independent reference
implementation**, not against its own output. Follow it:

- NEXRAD Level II and Level III → **MetPy** (`pip install metpy`)
- GRIB2 → **ecCodes** (`pip install eccodes`), which also has a grid iterator useful for
  validating projections

Write the reference values into the test as literals with a comment naming the source.
When no reference exists — the cross-section, for instance — assert **physics** instead:
the cone of silence must be empty, beams must climb with range.

Live features are verified against live weather, not mocks. `🎯 Hotspot` finds the
heaviest precipitation in the country, which is the fastest way to get real data on
screen. `--soak` runs the live pipeline headless.

## Conventions

- Comments explain constraints and *why*, never what the next line does.
- Prefer records and immutability for anything crossing a thread boundary.
- User-facing text says what happened in plain language; errors say how to fix it.
- Commit at feature boundaries with a message that explains the reasoning, and update
  `TASKS.md` in the same commit.
- Units: `Units.System` drives all display formatting. Never hardcode km or miles in
  user-facing strings.

## Open work

3D volume rendering.

Every gap in `docs/parity.md` is now closed. What is left is the "Nice" and "Cosmetic"
tier plus the two Partials.

Rotation tracks work but carry visible background speckle: accumulating a maximum over a
dozen scans is unforgiving, and the low Doppler cuts have no correlation coefficient to
filter marginal gates on. A 3x3 smoothing pass before accumulation cut the peak from 0.18
to 0.12 1/s; a real quality mask would do better.

Velocity dealiasing corrects about two thirds as many gates as Py-ART. A signal-quality
gatefilter will **not** close that gap — measured on the Moore volume, the corrected gates
average 29 dBZ against 16 dBZ for untouched ones, so filtering weak returns discards the
wrong gates. Nor will loosening `MinBoundaryGates`: at 3 the rate matches Py-ART but
corrections start chaining to ±2 intervals and fabricate 130 m/s winds. Closing it properly
means Py-ART's multi-pass structure, not a threshold tweak.

Note for storm labels: **NSS (storm structure) has not been distributed since ~2021**, so
`MaxDbz` / `CellBasedVil` / `EchoTopKft` are always null on live data. The layers panel says
so when it detects it — don't "fix" the decoder for this.
