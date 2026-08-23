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
| `resources/crosscheck/` | MetPy, ecCodes and Py-ART dump scripts — run these before trusting a decode |
| `resources/measurements/` | Harnesses that re-derive every number quoted in `verification.md` |

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

**The shell is arranged around five questions**, each with exactly one place on screen
and never a second: **where** (top bar: search, site) → **what product** (the segmented
bar above the map) → **when** (the time bar: one `LIVE · ARCHIVE · FORECAST` switcher that
owns the transport) → **what's on top** (the layers half of the right column, which holds
layers and nothing else) → **what's coming at me** (the `APPROACHING` panel above it).
Before adding a control, decide which question it answers and put it there. Set-once
configuration goes in Settings, not the layers panel; reference material (shortcuts, the
symbol key, About) goes in `InfoWindow`, not a panel or a MessageBox.

**The left rail has a fixed budget and it is already spent.** Sixteen 44 px buttons needed
1109 px against 1096 available, so `About` sat entirely below the window edge — and because
the bottom group is docked to the bottom, overflow eats Settings, Help and About first: the
three you least want to lose. There is no scrollbar and no overflow affordance, so a rail
that does not fit fails silently. Before adding a button, take one out. What went, and why:
set-once actions (`Set home`, palette import) belong in Settings, reference material
(`About`) belongs in `InfoWindow`, and a control that is inert until some state exists
(`Link panes`) should be **collapsed** rather than merely disabled — a greyed button still
costs a slot.

The right column carries the last two, and they behave differently on purpose. The layers
toggle governs **only** the lower half — tidying the layers away must not take a tornado
warning off the screen with it — and `APPROACHING` collapses on its own whenever nothing is
threatening, so the column is unchanged from before on a quiet day. `RightColumn` disappears
only when both halves are hidden.

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

**A track resolves to one of three things, and "closest approach" alone cannot tell them
apart.** `DistanceKm` and `EtaMinutes` report the same pair — the current distance, zero
minutes — whether a storm is arriving right now or is as close as it will ever get and
leaving, because the search starts at the current position and only improves on it. Reading
that as "now" turned every departing storm into an alert. `GeoMath.PathApproach` carries
`CurrentKm` and `FinalKm` so `IsReceding`, `IsClosing` and `IsStationary` can be told apart.
A receding storm is not on the list at all — the panel is titled APPROACHING.

The other half is miss distance: passing thirty-five miles away inside a fifty-mile radius
is not the same claim as passing overhead. `DirectHitRadiusKm` splits them. A **glancing**
pass lists, names the side it goes by, and does **not** interrupt; only `Interrupts` threats
raise. Warnings are exempt from the tiering — a polygon has no track, so there is nothing to
judge — and so is a rotating cell, whose forecast track is the part least worth betting on.

**The SCIT forecast is frequently degenerate while the motion is known.** Its forecast points
sit on the current position for a while after a cell appears, but `SpeedKmh`/`BearingDeg` come
from the *past* track and are usually good by then. Treating a degenerate forecast as "not
tracked yet" throws away measured motion and drops storms that are demonstrably coming, so
`ClosestApproach` extrapolates an hour along the measured heading instead. Only a cell with a
degenerate forecast **and** null motion is genuinely untracked.

**Alerting has two outputs and they answer different questions.** `ThreatMonitor.ThreatDetected`
fires once per hour per source and is what *interrupts* — a tray balloon and a sound.
`ThreatsChanged` carries the whole current set on every evaluation and is what the side list
*draws*. A threat still present on the next poll has to stay on that list without re-alerting,
so the throttle sits on the notification and never on the set. The storm and warning halves
run on different clocks, so each evaluation replaces only its own half.

**The threat list is ordered nearest-first, and this was measured, not reasoned.** Soonest-first
reads wrong: a cell whose closest approach is where it already sits reports an ETA of zero — it
is at its nearest *now*, possibly receding — so four such cells at 18–44 miles sorted above one
closing to 14 miles in nineteen minutes. Also, **advisories are not threats**: the warnings layer
draws statements when ticked and should, but `IsDangerous` keeps them off the list and out of the
tray. Warnings always count; anything else needs a Severe or Extreme severity.

**Windows Location Services works unpackaged, but says `Denied` rather than prompting.** The two
privacy switches that block it live in different places on the same Settings page, and an
unpackaged desktop app gets no consent dialog when either is off — so `GeoLocationService` names
both in every failure message. `Geolocator.RequestAccessAsync` needs a message pump, so call it
from the dispatcher. Ask for `PositionAccuracy.Default`, not `High`: the GPS-grade path can sit
on a cold radio for a minute and a home location does not need metres.

**A dialog that mutates the live `AppSettings` has no working Cancel.** Every control in
`SettingsWindow` is read *on save*, which is what makes Cancel work; home was written the instant
"Use my location" succeeded, so Cancel left it moved and waiting for the next unrelated `Save()`.
Home is held in pending fields now. Anything else added there must be too.

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
of folding, not of real data. Unfolding is region-based and follows Py-ART: regions are
**merged** in order of boundary strength, each merge pooling both sides' remaining
boundaries, so a region reached only through several thin boundaries is judged on all of
them at once. There are no width or tolerance thresholds left to tune — the three the older
spanning-tree walk needed were deleted with it, and the fold count is `Math.Round` of the
mean boundary difference. Corrections still chain, so the failure mode to watch for has not
changed: two stacked guesses turn −22 m/s into +82 m/s. `docs/verification.md` carries the
rate measurements and the two dead ends that were ruled out.

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

**3D is a resample, not a layer.** `VolumeGrid3D` walks a Cartesian box and asks which
beam passed through each cell — the reverse mapping, because a forward splat leaves holes
wherever gates spread wider than a cell, and at 150 km a half-degree beam is over a
kilometre across. `GeoMath.BeamAngleTo` is the inverse that makes it possible and is shared
with the cross-section, so a slice through the 3D view and a 2D cross-section of the same
volume agree by construction. **Voxel 0 means unsampled, never "weak"** — the volume is
mostly empty (cone of silence, under the lowest beam, above the top cut) and a scale
starting at zero paints the sky. Vertical is exaggerated ~6× by default because a 12 km
storm in a 300 km box is a smear at true scale; the factor is on screen rather than hidden.

**A live volume is published cut by cut**, so anything that rebuilds on "the volume
changed" fires ten to fifteen times per scan. `VolumeController` debounces, and keeps the
previous volume on screen while the new one has too few cuts — otherwise the 3D view blanks
for a minute every five.

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

**Endpoints drift.** Three IEM paths verified earlier in development had been retired by
the time they were wired up. When something returns HTML instead of data, check
`https://mesonet.agron.iastate.edu/api/1/openapi.json` for the current path. SPC serves
GeoJSON with a **UTF-8 BOM**, which `System.Text.Json` rejects outright.

**IEM's tile service fails as a picture, not as a status code.** An unknown *layer name* on
`tile.py` comes back as **HTTP 200 with a valid PNG** reading "Invalid TMS Request", which
decodes exactly like imagery — the GOES layer painted the map solid red and cached 253 copies
of that one image as data. Verify a tile layer by fetching **two different tiles and checking
the bytes differ**; a status code proves nothing. An unknown path *prefix* does 404 properly,
so it is only the layer segment that hides. Tile cache directories are named for the layer
rather than the provider, so correcting a path abandons the old tiles instead of serving them
for ever. See `docs/data-sources.md`.

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

Every **Blocker** and **Expected** gap in `docs/parity.md` is closed. What is left there is
the Nice/Cosmetic tier — GeoJSON/shapefile import, terrain basemap, soundings, Spotter
Network — plus three Partials: boundaries come from the basemap rather than a controllable
layer, hail is per-cell markers rather than contours, and there is one home location rather
than a list of saved ones.

The two notes below are the real quality gaps, and both have had a wrong answer ruled out
already.

**Rotation-track speckle is mostly fixed, and the old note here was wrong about why.** It
said the low Doppler cuts have no correlation coefficient to filter on. True of that sweep
— but on a split-cut VCP the Doppler cut carries **reflectivity** alongside velocity, index
for index, and that is the field that matters: 88.8 % of strong-shear gates on the Moore
volume sat under less than 20 dBZ. Shear computed where nothing reflected is the phase of
receiver noise. `GateQuality.MaskByReflectivity` blanks those; it removes 89 % of the
strong-shear gates and leaves the peak bit-identical at 0.1297 1/s over the tornado.

**Do not add a CC mask without reading this.** The paired surveillance cut does carry CC,
and it is tempting. But a tornado debris signature *is* a low-CC, high-Z target: 34.6 % of
strong shear under 40+ dBZ on the Moore volume has CC below 0.85, so a CC threshold would
delete a third of the signature the product exists to find.

What is left is **clutter**, which is a different problem wearing the same clothes. Sea and
ground clutter return strongly, so a reflectivity mask keeps them — visible as radial
spikes at coastal sites. CC is the right discriminator for it and carries the debris risk
above, so it needs a design that separates the two cases rather than one threshold.

Velocity dealiasing now reaches 85–92 % of Py-ART's correction rate, up from about two
thirds. The fix was structural, and both obvious diagnoses were wrong: a signal-quality
gatefilter discards the wrong gates (corrected gates average 29 dBZ against 16 for
untouched ones), and loosening the boundary threshold buys the rate with ±2 chains that
fabricate 130 m/s winds. What worked was replacing the spanning-tree **walk** over regions
with Py-ART's **merge**: each merge combines the two sides' remaining boundaries, so a
region reached only through several thin boundaries is judged on all of them at once. Three
threshold constants were deleted in the process. Peak unfolded speed *fell*. The remaining
gap is genuine judgement-call difference on marginal folds — see `docs/verification.md`.

The lesson worth keeping: **read the reference implementation before theorising about it.**
Two measured dead ends came from reasoning about what Py-ART must be doing; the answer took
twenty minutes of reading `region_dealias.py`.

Note for storm labels: **NSS (storm structure) has not been distributed since ~2021**, so
`MaxDbz` / `CellBasedVil` / `EchoTopKft` are always null on live data. The layers panel says
so when it detects it — don't "fix" the decoder for this.
