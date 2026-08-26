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

## Git hosting

This repository is hosted on a **self-hosted Forgejo instance**, not GitHub.

**Do not use the `gh` CLI.** It only speaks to GitHub, so it will either fail against this
remote or, worse, act against an unrelated GitHub repo. Use plain `git` for everything. For
pull requests and issues use the Forgejo web UI, or the `tea` CLI — Forgejo/Gitea's equivalent
of `gh`, and not currently installed here.

| Protocol | URL |
|---|---|
| HTTP | `http://forgejo-host:3000/<owner>/<repo>.git` |
| SSH | `ssh://git@forgejo-host:2222/<owner>/<repo>.git` |

**SSH is on port 2222**, not 22 — the Forgejo container maps host 2222 to container 22, so an
SSH URL without the port will fail. Web UI: `http://forgejo-host:3000`.

`forgejo-host` resolves only on the home LAN or over the Tailscale tailnet. **A push failing with a
DNS or connection error is almost certainly that**, not a repo or auth problem — check the
tailnet before debugging anything else.

Auth is configured on the machine, by SSH key or a stored Forgejo token. If a push prompts for
credentials or returns 403, **stop and report it** rather than reconfiguring auth or switching
remotes.

- Never point `origin` at a GitHub URL.
- Never create a GitHub repo as a fallback when a push fails.
- No GitHub Actions workflows. Forgejo Actions is similar but distinct — ask before adding CI.
- Commit and push only when asked.

GitHub is deferred rather than abandoned: revisit it at release time.

## Commands

```
dotnet build OpenWSR.slnx                    # NOTE: .slnx, not .sln
dotnet test OpenWSR.slnx                     # 621 tests
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

**The layers panel has a budget too, and it was 92 % over.** Measured rather than eyeballed:
1758 px of content in a 914 px column, 52 % of it visible. Three rules got it to 100 % with no
scrollbar. A control that is inert until some state exists is **collapsed, not shown** — an
opacity slider for a layer that is off, or eleven storm filters while tracking is off (that one
was a third of the panel). Fine-grained settings that are chosen once sit behind a nested
disclosure rather than costing rows every session. And sections that are set-once — RADAR,
Warnings, SPC — start closed. What makes those defaults safe is that `PanelSections` in
settings **remembers how each section was left**, so a default is only a starting point: which
sections a person needs open is not something this can know, since a chaser lives in STORMS and
never opens SPC while someone watching one town is the reverse. Measure with UI Automation's
`ScrollPattern.VerticalViewSize` before and after; guessing at panel height does not work.

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

**In-app explanation lives in `InfoWindow`, and `?` opens the guide rather than the shortcut
table.** Someone presses `?` because they do not know how the thing works, and the first
question is not which key selects velocity — it is what the app will do on their behalf and
when. `ShowGuide` answers that in prose, with the alerting rules first, and links on to the
keyboard card and the symbol key. The first run shows it too. Anything explaining a *feature*
belongs there; a panel is for operating one, not for describing it.

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

**The dBZ window is a draw-time discard, not a change to the data.** `MapView.SetValueFilter`
feeds two floats into the sweep shader's constant buffer and gates discard outside them. It
was tempting to do it by zeroing the palette's alpha instead, which needs no shader change —
but then moving the control would have to restage the sweep, and archive playback hands the
renderer prebuilt geometry it has no reason to rebuild. As a constant it redraws what is
already on screen. It applies to **reflectivity only**: the window is in dBZ, and a dBZ bound
against a velocity field or a correlation coefficient would silently blank most of the
product, so other moments get the full range and the panel says so.

**A palette's low end is a display decision, and the basemap changes the answer.**
Reflectivity used to reach full opacity at 10 dBZ, which is clear-air return — insects,
birds, residual clutter. That was invisible as a problem while the basemap was light, where
pale cyan on white recedes; against the near-black default it put bright blue over **26.7 %
of the map against 12 % of actual storm**. The low end fades in now and is full only from
22 dBZ. When touching it, keep the property that makes it safe: every band from the green
stop upward has both endpoints untouched, so weather is byte-for-byte what it was, and
`ReflectivityPaletteTests` asserts that against a copy of the previous table rather than
trusting the eye.

**Place names are their own layer, drawn above the weather.** The dark basemap uses CARTO's
`dark_nolabels` with `dark_only_labels` as a separate tile layer drawn *after* the radar sweep.
Baked into the basemap they are the first thing an echo covers, and the name of the town a
storm is over is exactly what wants reading at that moment. They are also boosted: CARTO draws
them mid-grey — brightest pixel (161,161,161), mean (103,103,103) — so `DrawTiles` takes a
`boost` that the quad shader applies as its tint, and values above 1 lift toward white and
clamp. Only the dark style splits its labels out; the other providers bake them in, so drawing
a second copy would double every name.

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
metres. Line widths are already pixel-constant in `OverlayRenderer`. So are dash lengths —
the forecast track's dashes were 2.5 km of Mercator and went solid at national zoom.

**The hodograph has two sign-and-scope traps, and neither one throws.** A `VadLevel`
reports the direction the wind comes *from*, so components carry a negative sign
(`u = -speed·sin`, `v = -speed·cos`); lose it and the plot is rotated 180°, which still looks
like a hodograph. WPF's y grows downward, so the vertical axis is negated when projecting;
without that the plot is mirrored, turning a veering profile into a backing one and reversing
the sign of everything read off it. Separately, `Hodograph.Layer` returns what it has when
asked for more depth than the profile spans — right for a calculation, a trap for a caption,
because a 2.7 km profile will happily produce a "0–6 km shear". Check `Hodograph.DepthM`
before labelling anything with a depth. Storm-relative helicity is golden against MetPy 1.7.1
including the interpolated layer top; **Bunkers is deliberately absent**, because its mean
wind is pressure-weighted and a radar profile has no pressure — a standard atmosphere would
only make our number agree with the reference because both were fed the same invention.

**A tile service advertising a zoom range is not promising to have the tiles.**
`USGSShadedReliefOnly` would have been the better terrain basemap — grey relief leaves the
reflectivity palette the only saturated thing on screen — and its metadata advertises levels
0 to 23. Its cache has holes: over northern Utah it serves z8 and z12 and returns **404** at
z9 and z10. `USGSTopo` was complete everywhere checked and is what ships. Two other things
that cost time here: ArcGIS tile paths are `{z}/{y}/{x}`, row before column, and the swap
returns tiles rather than an error — just the wrong ones. And `TileFetcher` used to swallow
every failure into a retry map, so a basemap where *every* tile 404s looked exactly like one
that was merely slow. It now records the first failure and `MainWindow` reports it once.

**Boundary data has to be thinned per zoom, not drawn.** The Census 1:500,000 county file
is 1.03 million points — at national zoom about two hundred points to the pixel, and over six
million vertices a frame if handed straight to `OverlayRenderer`. `BoundariesController`
projects and bounds every ring once at load, then per view change culls to the viewport and
runs `Polyline.Simplify` at 1.2 px. Simplify in the plane you draw in (Mercator metres), not
in degrees, or the north of a shape thins harder than the south. And measure distance to the
*chord*, not between consecutive points: a coastline has no point far from its neighbour while
the run of them bends a long way, so neighbour-distance thinning flattens it.

**Overlay strokes are a distance field, and a segment rounds its far end only.** Each vertex
carries its (along, across) position within its own segment, and the shader measures distance
to the *segment* — so the ends round themselves and the last pixel feathers, with no
multisampled target. The cap rule is the part to not undo: `LineCaps.Joined` is flat at A and
round at B, so in a chain the round end lands on the shared vertex and covers the next
segment's square start. One disc per joint. Rounding both ends instead stacks two half-discs
on every shared vertex, which at any alpha below opaque reads as a string of beads along the
line — the 96-sided place ring is the worst case. `LineCaps.Both` is for segments that
genuinely stand alone: dashes, crosshairs, the measure line. Chains must actually chain, and
`OverlayStrokeTests` asserts that they do.

**Stroke widths are device-independent units, scaled by `MapView.DipScale`.** The swap chain
is sized in physical pixels, so a width taken literally draws at two thirds of its weight on a
150 % display — which looks like nothing more than a design choice, and was most of why every
overlay line read as a hairline.

**A gigabyte of working set is this application behaving normally.** Measured over twenty
minutes of live streaming with no interaction: it sawtooths between roughly 700 MB and
1.45 GB, with two full gen2 cycles returning it to a stable floor (685 MB, then 748 MB). It
is not a leak — handles and threads stay flat, `LiveFeed`'s assemblers are capped at two and
pruned, and the peaks are one volume's decode awaiting collection. A Level II volume is about
210 MB as floats once every moment is decoded, so a couple of them in flight is the shape of
the graph. Tile textures are *not* part of that number: they are `Immutable` with no CPU
access and live in VRAM.

**Nothing on the UI thread may block on I/O.** `LiveFeed` exposes `StartAsync`/`StopAsync`;
a blocking `Wait(5s)` in `Stop()` froze the window on every live toggle. Dispose paths
cancel and let the drain finish on a continuation.

**Places are a list, and one of them is primary.** `AppSettings.Locations` replaced a single
home. Exactly one entry is primary — it decides the startup camera and which radar the storm
layer follows, and both need a single answer, so `SetPrimary`/`Remove` keep the invariant. Each
place carries its own alert radius, falling back to the global one when null; `RadiusFor` is
the only correct way to ask.

`ThreatMonitor` evaluates every threat against every place, so one storm crossing two of them
is two entries. `Threat.SourceKey` identifies the storm or warning; `Threat.Key` appends the
place and is what the once-an-hour throttle uses, because hearing about a storm at home must
not spend the alert for the office. The place is named on the row only when more than one is
watched — with a single place it is the only answer there is, and a 248 px panel needs that
space for the range.

**A settings file older than the list still carries `homeLatDeg`.** `MigrateLegacyHome` folds
it in on every load, not once behind a version flag: an older file can appear at any time,
restored from a backup or synced from another machine. It is a no-op once the list holds
anything, and it nulls the legacy fields so a saved file never carries two spellings of the
same fact.

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

**The satellite is an overlay, not a basemap, and that changes the colour science.** CIRA's
GeoColor paints the whole earth because there the satellite *is* the base image. Here there is
an OSM basemap underneath carrying the roads and boundaries a radar view is read against, so
painting true-colour land hides the map. The day composite therefore keeps true colour but
lets the alpha fall away over clear ground.

Deciding what is cloud takes two things, not one. Brightness alone lets desert through — its
reflectance clears any threshold cloud does. Cloud is also spectrally **flat** across 0.47,
0.64 and 0.86 µm, which is why it looks white, where desert is markedly redder than it is blue
and vegetation is several times brighter in the near infrared. Alpha is brightness × neutrality.
Snow survives that and always will: it is genuinely bright and genuinely neutral, and three
visible bands cannot separate it from cloud. ABI has no green detector, so green is the
community's hybrid, `0.45·red + 0.10·veggie + 0.45·blue`.

**Fetch the multiband product, not the band you want.** Band 2 is published at 0.5 km and runs
to 65 MB at midday; the display raster is about 2.4 km per pixel, so nineteen twentieths of it
is discarded. `ABI-L2-MCMIPC` carries all sixteen bands already resampled to 2 km for 57 MB —
less than that one band, at the resolution actually wanted, co-registered on one set of scan
angles. At night the reflective bands are noise, so `AnyDaylightOverConus` drops back to the
single 3 MB infrared band; it samples across the sector rather than at its centre, because
CONUS spans three hours of longitude and at dawn one edge is lit while the other is dark.

**Two image-overlay slots.** `MapView.SetImageOverlay` takes an `OverlaySlot`: `Field`
draws under the radar sweep (model output, mosaics) and `Analysis` draws over it (derived
products read *against* the echo). They are independent, so the HRRR forecast and a
rotation-track swath no longer fight over one slot.

**TDWR is a Level III radar, and that shapes everything about it.** Terminal Doppler Weather
Radar Level II is not published to the NEXRAD bucket, so the 45-odd terminal radars are reached
as `TZ0/TZ1/TZ2` reflectivity and `TV0/TV1/TV2` velocity — three tilts each, in the same
`unidata-nexrad-level3` bucket and key format as every other Level III product, site prefix
being the ICAO minus its leading T. No WSR-88D and TDWR share a three-letter suffix, so the
existing key scheme cannot collide.

Consequences worth knowing before touching it. The scaling lives in the product's own
threshold halfwords — 31 the first data level in tenths, 32 the increment, 33 the count — and
differs between products (reflectivity floors at −32 dBZ, velocity at −63.5 m/s), so it cannot
be hardcoded per code. Levels 0 and 1 are flags, and level 1 means range folded on velocity and
nothing on reflectivity. `TdwrRadar.ToSweep` converts to a `Sweep` so the renderer, palettes,
inspector and cross-section all apply unchanged; azimuthal shear then works for free, being
derived from velocity. There is no archive and no forecast for a TDWR, so selecting one
switches to live. They also go quiet in clear air, unlike a WSR-88D's continuous clear-air VCP
— "nothing published" is normal rather than a failure.

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
Network — plus two Partials: boundaries come from the basemap rather than a controllable
layer, and hail is per-cell markers rather than contours.

The two notes below record the quality work that has the most measurement behind it, and
each had at least one plausible wrong answer ruled out by measuring it.

**Rotation-track speckle takes two masks, because noise and clutter are different
problems.** The first note here was wrong about why: it said the low Doppler cuts have no
correlation coefficient to filter on. True of that sweep — but on a split-cut VCP the Doppler
cut carries **reflectivity** alongside velocity, index for index, and that is the field that
matters for *noise*: 88.8 % of strong-shear gates on the Moore volume sat under less than
20 dBZ. Shear computed where nothing reflected is the phase of receiver noise.
`GateQuality.MaskByReflectivity` blanks those; it removes 89 % of the strong-shear gates and
leaves the peak bit-identical at 0.1297 1/s over the tornado. Clutter is the other half and
is below.

**Clutter is handled, and a bare CC threshold is still the wrong way to do it.** Clutter
reflects strongly and passes the reflectivity mask intact, so it needed a second test, and
correlation coefficient is the only field that identifies it. It is also the most dangerous
thing to threshold here: a debris signature *is* a low-CC target, and masking the Moore
volume on `CC < 0.85` alone removes **all 89** debris gates and then reports a confident peak
on an unrelated feature 20 km east — 0.1000 1/s at 35.276,−97.282 instead of 0.1297 at
35.323,−97.527. Every summary statistic improves while the product answers a different
question.

`GateQuality.MaskClutter` gates it on the echo instead: **low CC only condemns a gate when
the reflectivity is also below 40 dBZ.** Debris is the low-CC target that is *strong* — that
is what makes a TDS detectable — while clutter and biologicals are low-CC and weak. On Moore
that keeps all 89 debris gates with the peak bit-identical; on a clear-air coastal volume
(KBOX) it takes the swath from 23 strong-shear gates peaking at 0.0995 1/s of pure sea
clutter down to 3 gates at 0.0226. 40 dBZ is the knee, not a tuned number: the benefit has
saturated there while the cost is still exactly zero, and above it debris dies for nothing.

Two plausible discriminators were measured and rejected. **Radial velocity** does not
separate them at all — the RDA's own clutter filter has already notched out the genuinely
stationary returns, so what survives is not at zero Doppler. **Spectrum width** separates
them physically (debris tumbles, median 5.5 m/s against 1.0) but is a weak instrument:
`sw < 2` buys 6–24 % of the speckle and costs debris gates, against 61–87 % for nothing.
Correct physics, poor discrimination — see `docs/verification.md`.

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
