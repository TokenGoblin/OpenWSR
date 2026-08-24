# OpenWSR â€” Phases & Subtasks

Work breakdown for `OpenWSR-build-plan.md`. Work strictly one phase at a time; a
phase is done only when its **gate** passes. Check items off as they land.

---

## Phase 0 â€” Bootstrap & test data

- [x] `git init`; commit the build plan and this file
- [x] Create `OpenWSR.slnx` with projects: `App`, `Render`, `Nexrad`, `Ingest`, `Geo`, `Palettes` + `Harness`, `Nexrad.Tests`, `Geo.Tests`
- [x] Project settings everywhere: `net10.0` (`-windows` only for App), `<Nullable>enable</Nullable>`, `<AllowUnsafeBlocks>`, `TreatWarningsAsErrors` in Release
- [x] Enforce purity: `Nexrad` and `Geo` reference nothing but the BCL (add a test that asserts their assembly references)
- [x] Add packages: Vortice trio, `AWSSDK.S3`, `SharpZipLib`, `CommunityToolkit.Mvvm`, `Serilog` + file sink
- [x] Download quiet-weather volume â†’ `assets/testdata/` (KTLX 2026-08-10)
- [x] Download KTLX 2013-05-20 ~20:00Z volume â†’ `assets/testdata/` (20:16Z, tornado on ground)
- [x] Capture live chunks **spanning a volume boundary** (55-chunk complete volume + next volume's start, plus the archive ground-truth file for the same scan)
- [x] Decide storage for test data: plain commit (~40 MB) + `tools/fetch-testdata.ps1` for provenance

**Gate:** âœ… solution builds clean; test data present and committed.

---

## Phase 1 â€” Level II decoder (`OpenWSR.Nexrad`)

- [x] 24-byte volume header parser (`AR2V00xx.`, date/time, ICAO)
- [x] LDM record framing: 4-byte big-endian control word (abs = length, negative = final record) + bzip2 decompress (SharpZipLib); also handles the gzip-era layout (raw unframed messages after the header)
- [x] Message framing: 12-byte CTM prefix + 16-byte message header; dispatch by message type; Message 31 >65534-halfword size carried in segment fields
- [x] Message 31: block-pointer walk; VOL / ELV / RAD blocks
- [x] Message 31 moments: REF, VEL, SW, ZDR, PHI, RHO, CFP; 8- and 16-bit words; scale/offset to physical units; codes 0/1 â†’ NaN + range-fold mask; unknown block names skipped
- [x] Message 5 (VCP definition) parser
- [x] Message 2 (RDA status) parser
- [x] `Sweep` record + `VolumeBuilder` (incremental, reused by Phase 6); SAILS-safe elevation-number grouping; independent REF/VEL geometry
- [x] Console harness: per-sweep elevation, moment, radial count, gate count, min/max
- [x] Golden-file tests: exact values cross-checked against MetPy 1.7.1 on both volumes; sanity range enforced

**Gate:** âœ… harness physically sane on both volumes; 10/10 golden-file tests green (values bit-identical to MetPy).

---

## Phase 2 â€” Geo math (`OpenWSR.Geo`)

- [x] `BeamPath` â€” 4/3 effective-earth-radius formulas from the plan
- [x] `Offset` â€” spherical great-circle (or geodesic) destination point
- [x] `ToMercator` / `FromMercator` (EPSG:3857)
- [x] Analytic tests: 0.5Â°/100 km slant â†’ â‰ˆ1461 m above radar level; Mercator round-trip < 1e-9Â°; offset vs known geodesic cases

**Gate:** [PASSED] 17/17 analytic tests green.

---

## Phase 3 â€” D3D11 window & map (`OpenWSR.Render` + App shell)

- [x] `HwndHost` subclass with child HWND; D3D11 device + flip-model swapchain; resize handling
- [x] Render loop on dedicated render thread (owns the device context)
- [x] Orthographic camera in Mercator space; mouse pan/zoom with inertia (touch/WM_POINTER deferred to Phase 8)
- [x] Visible-tile computation for current zoom/viewport (XYZ scheme)
- [x] Async tile fetch (OSM default, MapTiler via settings key; provider + key in settings.json from day one)
- [x] Disk tile cache under `%LOCALAPPDATA%\OpenWSR\tiles`; VRAM LRU cache capped ~512 MB
- [x] Parent-zoom tile stretch while children load â€” no blank checkerboard
- [x] Debug marker layer: radar site lat/lons rendered as dots for alignment check
- [x] Establish the airspace pattern now: side-docked WPF panels; Popup/ToolTip for anything floating over the map

**Gate:** [PASSED] 60 fps confirmed via in-app counter; site markers verified on Seattle/Bay Area/Denver/Chicago/OKC/San Antonio; pan/zoom exercised CONUS-to-street-level.

---

## Phase 4 â€” Radar rendering

- [x] Sweep data upload as `R32_FLOAT` (or `R16_UNORM`+scale) texture, `gateCount Ã— radialCount`
- [x] Azimuth wedge-edge buffer (radialCount+1, sorted, wrap-safe); palette as 256-entry 1D texture
- [x] Vertex shader: geometry from `SV_VertexID` â†’ (radial, gate) â†’ beam path â†’ offset from radar origin â†’ Mercator; single indexed/instanced draw
- [x] Pixel shader: point-sample data, normalize, palette lookup; discard below-threshold; range-fold rendered NWS purple
- [x] Premultiplied alpha blend over basemap; opacity property (UI slider later)
- [x] Product/palette switch = texture swap; measured 0.7–11.1 ms including CPU prep
- [x] VRAM budget policy: single displayed sweep resident (loop residency revisited in Phase 5)
- [ ] (Not needed — 60 fps without it) per-gate beam-path lookup buffer

**Gate:** [PASSED] hook echo verified over Moore; 60 fps with sweep rendered; REF↔VEL switch 0.7–11.1 ms; range-fold purple confirmed on velocity.

---

## Phase 5 â€” Archive ingest (`OpenWSR.Ingest`)

- [x] Anonymous S3 client (`unidata-nexrad-level2`, us-east-1); 30 s timeout, 4-attempt exponential backoff, Serilog per-request logging
- [x] List volumes by site + UTC date; handles `.gz` and bare files; skips `*_MDM`
- [x] Volume disk cache under `%LOCALAPPDATA%\OpenWSR\volumes`, 2 GB cap, last-access LRU eviction
- [x] Radar site table (210 sites from NCEI HOMR) as embedded CSV resource; all WSR-88Ds shown as map markers
- [x] Time-slider UI over the day's volumes; 30-frame loop at 2/4/8 fps with prebuilt geometry (zero per-frame CPU)
- [x] Nearest-radar selection from the map center (Nearest button)
- [x] Data-age groundwork: scan timestamp always in the status line (coloring lands with Phase 6 feed-health)

**Gate:** [PASSED] KTLX 2013-05-20: 323 volumes listed, scrubbed to arbitrary index, 30-frame loop steady at 4 fps; cached rebuild ~22 s with zero network (cold was ~7 min).

---

## Phase 6 â€” Real-time chunks

- [x] Volume-directory discovery in `unidata-nexrad-level2-chunks`: probe candidate dirs among 1â€“999, newest object wins; persist last-known index, probe forward
- [x] Chunk key parsing incl. volume-start-timestamp grouping
- [x] `LiveVolumeAssembler`: S header capture; I/E decoded as bare LDM streams; order-independent by design (records are self-contained)
- [x] Edge cases tested: mid-volume join, randomized order, duplicates, missing chunks; two assemblers held across volume overlap
- [x] Partial-volume render: snapshot emitted per newly completed cut (first render ~5 s after connect)
- [x] Adaptive polling: tighten while chunks flow, back off when idle (VCP-aware, 2â€“10 min scans)
- [x] Pipeline on `System.Threading.Channels`: poller â†’ thread-pool decoder â†’ render queue; never decode on UI/render thread
- [x] Feed-health UI: data-age always shown, amber past 10 min live; poll errors surface in status and retry
- [x] Offline replay test: chunk corpus in randomized order â†’ decoded sweeps identical to archive version (byte-identical reassembly = bonus)
- [x] 30-minute live soak vs KTLX: 8/8 consecutive volumes complete across 7 directory rollovers, 100 snapshots, 0 errors, 0 duplicates, memory flat (0.1→1.0 MB managed)

**Gate:** [PASSED] 30-min soak PASS (8/8 volumes, 0 errors, no leak) + randomized replay float-exact vs archive.

---

## Phase 7 â€” Warnings, palettes, tools

- [x] `api.weather.gov/alerts/active` poller: descriptive User-Agent w/ contact, â‰¥60 s interval, backoff on error
- [x] GeoJSON â†’ Mercator polygon tessellation; fill + stroke per NWS convention (red TOR, yellow SVR, green FFW); z-order above radar
- [x] Polygon hit-test (`GeoMath.PointInRing`, unit-tested incl. concave) + detail Popup (headline, expiry, description, instruction)
- [x] GR2Analyst `.pal` parser in `OpenWSR.Palettes`; map breakpoints â†’ 256-entry texture (with interpolation flags honored)
- [x] Inspector: hover â†’ value, azimuth, slant range, ground range, beam height AGL
- [x] Distance/bearing tool â€” geodesic math from lat/lon, never Mercator lengths
- [x] Warning expiry: pruned on every access + rebuilt each 60 s poll

**Gate:** live warnings appear/expire correctly; imported third-party `.pal` matches its reference screenshot.

---

## Phase 8 â€” Polish

- [x] Multi-pane 1/2/4 layout; linked pan (30 Hz camera sync, last-moved wins) or independent via toolbar toggle; secondary panes default to VEL/ZDR/RhoHV with per-pane keyboard product switching
- [x] Level III decoder: WMO/message/PDB headers, zlib-aware symbology, packets 2/8/15/19/20/23/24 → NST storm tracks, NHI hail, NMD mesocyclones; 4 golden tests vs MetPy; Level3Client + ⛈ Storms overlay (tracks/hail triangles/meso circles, 2-min refresh). Fallback feeds not wired.
- [x] Settings persistence (settings.json); keyboard shortcuts (moment/tilt/tools); PerMonitorV2 manifest (mixed-DPI HwndHost verification pending a second monitor)
- [x] Single-file self-contained ReadyToRun publish (OpenWSR.exe, 160 MB, smoke-tested at 60 fps)
- [x] About box with life-safety disclaimer + controls; THIRD-PARTY-NOTICES.md (no Supercell Wx code ported); Serilog rolling file logging wired

**Gate:** published single-file build runs on a clean Windows machine with all of the above working.










---

## Post-plan — WunderMap parity (2026-08-18)

- [x] Layers panel: radar opacity slider; warning filters by type (TOR/SVR/FFW/other); storm filters (past/forecast track, hail, meso, min severe-hail probability slider)
- [x] Storm tracking parity: cells joined across NST+NHI+NMD (motion speed/bearing from forecast step, hail probs, meso radius); forecast arrowheads + shrinking time markers; click-a-cell detail popup
- [x] Home area: click-to-set home (persisted in settings.json), radius ring drawn cos(lat)-corrected, 15/40/80/160 km
- [x] Proximity alerts: ThreatMonitor samples current→forecast path per minute for closest approach + ETA; warning polygons alert on containment or range; 1-hour de-dup per source; toast + sound + status
- [x] `GeoMath.ClosestApproachToPath` with 5 analytic tests

**Gate:** ✅ 52/52 tests; live end-to-end: home armed at KC, ⛈ Storms on KEAX → "⚠ Storm U0 approaching your area" fired from real convection.

## Post-plan — cones, cell dBZ, smoothing (2026-08-18)

- [x] NSS (Storm Structure, product 62) decoder: stand-alone tabular block parse → per-cell max dBZ, VIL, echo top; golden test vs raw table (40 rows)
- [x] Projection cones: apex at cell, ±15° spread to the 60-min forecast distance, fill + edges + motion-vector centerline; filter checkbox
- [x] On-map text: WPF-built glyph atlas → screen-space GPU quads with dark halo; cell labels show ID + max dBZ; filter checkbox
- [x] Radar smoothing toggle: sentinel-aware bilinear in the pixel shader (invalid gates excluded from the blend, edge feathering); Smooth radar checkbox
- [x] Storm popup: max dBZ / echo top / VIL line

**Gate:** ✅ 53/53 tests; verified live — smoothed radar, cell ID labels with halos on TLX cells, meso rings; cones gated on cell speed > 3 km/h (quiet-weather cells correctly show none).

## Post-plan — national hotspot finder (2026-08-18)

- [x] Packet-16 digital radial decoder (RadialImage) + DVL digital VIL decode (packed float16 thresholds, linear/log split); bzip2 symbology support; golden test vs MetPy (EAX max level 191 -> 15.73 kg/m2)
- [x] NSS az/range captured into StormCellStructure (cell geolocation)
- [x] NationalStormScan: sweeps all 163 WSR-88Ds newest DVL (12-way parallel, 30-min freshness), ranks by peak VIL
- [x] 🎯 Hotspot button: scan -> switch site, start Live, enable storm overlay on hotspot site, camera to the cell

**Gate:** ✅ 54/54 tests; live run: 147/163 sites reporting, winner KAMX VIL 47.5 kg/m2 — app jumped to central-FL convection with live feed + storm layers on.

## Phase 01 — legibility and native chrome (2026-08-18)

- [x] Colour scale drawn in the D3D scene (airspace-safe): 1x256 palette texture, nice-stepped tick labels, product-aware, follows imported .pal
- [x] Settings window: basemap provider + MapTiler key, units, weather.gov contact with live User-Agent preview
- [x] Location search: city / ZIP / lat,lon via Nominatim (rate-limited, identified UA) -> nearest radar + camera
- [x] Data-age fix: LIVE shows elapsed and colours; ARCHIVE shows the absolute scan time (was "116106 h 20 m")
- [x] Unified timeline: transport controls inline, hour ticks with labels
- [x] Storm symbol key in the layers panel
- [x] Units system (miles / km / nautical) applied to inspector, measure, storm details, alerts, hotspot
- [x] Windows-native restyle: dark title bar via DWM, left navigation rail with settings at the bottom, full dark control theme (button, toggle, combo, textbox, checkbox, slider, datepicker, scrollbar), collapsible layers panel
- [x] Toolbar overflow defect removed (toolbar replaced by rail + top bar)

**Gate:** 54/54 tests; verified live — legend renders with correct ticks, search resolved "Provo, UT" to KMTX at 82.2 mi, settings dialog round-trips, archive age reads as a date.

## Phase 02 — national layers (2026-08-18)

- [x] National radar mosaic: Iowa State pre-rendered NEXRAD N0Q tiles as a second tile layer (own fetcher + VRAM cache, opacity slider, zoom-capped at 12 with parent stretch) — a seamless CONUS view with no GRIB2 work
- [x] SPC Day 1 categorical outlook (BOM-tolerant GeoJSON, risk-coloured fills)
- [x] SPC watch boxes via the IEM API (spc_watch_outline; the old geojson path now redirects to HTML)
- [x] SPC mesoscale discussions with number labels and watch probability
- [x] NWS local storm reports (6 h) as X markers with magnitude labels
- [x] Windows tray notifications for proximity threats, so alerts land when the app is not focused

**Gate:** 54/54 tests; verified live — mosaic rendered a squall line from Kansas City to Texas; 3 outlook areas, 4 discussions, 1 watch box, 13 storm reports fetched and drawn.

## Phase 03 — the format work (2026-08-18)

- [x] `OpenWSR.Grib2`: pure GRIB2 edition-2 reader — sections 0-8, grid templates 3.0 (lat/lon) and 3.30 (Lambert), packing templates 5.0 simple, 5.41 PNG, and 5.2/5.3 complex with first/second-order spatial differencing; bitmap handling; sign-and-magnitude scale factors
- [x] `MiniPng`: minimal greyscale PNG decoder so the GRIB2 library needs no imaging dependency
- [x] Golden tests vs ecCodes 2.47 on committed MRMS (PNG-packed, 24.5 M points) and HRRR (complex-packed, Lambert) files
- [x] `LambertConformal` in Geo with 7 tests validated against ecCodes' grid iterator (~11 m agreement)
- [x] `HrrrClient`: byte-range fetch of just the REFC record using the .idx sidecar (~140 kB/hour instead of the whole cycle file), walking back cycles until one has published
- [x] **Future radar**: HRRR simulated reflectivity resampled Lambert→Mercator, played as a 6-hour forecast loop with step/play/clear
- [x] `MapView.SetImageOverlay`: georeferenced raster layer, reusable for any gridded field
- [x] Satellite layer: GOES-East infrared tiles under the radar layers

**Gate:** 63/63 tests; verified live — GRIB2 matches ecCodes exactly on both packing paths; HRRR 19z run loaded 6 forecast hours and rendered a correctly georeferenced squall line.

### Still open from the parity scan
- MRMS native rendering (reader works and is golden-tested; the tile mosaic covers the visual today)
- NetCDF/GLM lightning, vertical cross-section, SRM, velocity dealiasing, placefiles, GIF export

## Phase 04 — depth (2026-08-18)

- [x] **Vertical cross-section** — the standout gap across the open-source field. For each (distance, height) cell it solves the 4/3-earth geometry for the elevation angle whose beam passes through that point, then blends the two bracketing cuts. Docked WPF panel below the map, driven by a right-drag on the map in cross-section mode. 4 physics tests (cone of silence overhead, beams rising with range, storm found on a slice through it).
- [x] **Storm-relative velocity** — subtracts the tracked cells' mean motion along each radial; motion averaged as components so it does not wrap at north.
- [x] **Frame capture** — backbuffer readback serviced by the render thread after present, saved as PNG.
- [x] **Animated GIF export** of the loop: steps frames, captures each, then patches the Netscape looping block and per-frame delays into WPF's GIF output (which otherwise writes a still).

**Gate:** 67/67 tests; verified live — cross-section through the Moore volume sampled all 17 cuts from 0.5° to 19.4°; PNG capture produced a 1492x989 image with every layer and the colour scale.

### Still open
Placefiles, velocity dealiasing, azimuthal shear, MRMS native render, NetCDF/GLM lightning, drawing tools, VWP.

## Placefiles — the community overlay ecosystem (2026-08-18)

- [x] `OpenWSR.Placefiles`: pure GRLevelX placefile parser — Title, Refresh/RefreshSeconds, Color (with optional alpha), Threshold, Font, Place, Text, Icon, Line, Polygon (multi-contour, closing on the repeated first point), Object/End pixel-offset blocks, TimeRange, and comment handling that respects quoted semicolons. Triangles and Image blocks are skipped and reported rather than mis-drawn.
- [x] 7 tests: a synthetic file covering the spec plus two real IEM community placefiles (ASOS observations, NWS warning time-mot-loc tracks)
- [x] `PlacefileController`: loads from URL or disk, appends lat/lon/version to URLs the way GR does, honours each file's own refresh interval, and rebuilds when the zoom crosses item thresholds
- [x] Layers-panel manager: add by URL or file, per-file enable, remove, status tooltip; sources persist in settings
- [x] Rendering: polygons filled + outlined, lines at their declared pixel width, labels with Object-block pixel offsets, icons as markers

**Gate:** 74/74 tests; verified live — a real IEM warning-track placefile parsed and a purpose-built test overlay rendered polygon, line, labels, an Object-block offset label, and correctly hid a 40 nm-threshold item at national zoom.

### Known limitation
IconFile sheets are not downloaded; Icon statements draw as markers in the placefile's colour. Triangles and Image blocks are skipped.


## Audit remediation — code, features, UI (2026-08-18)

A full audit of the tree (`docs/audit.md`) found 21 issues. All are closed.

**Defects**

- [x] **C-01** GIF export could never write a file: `StopLoop()` discarded `_loop` immediately before the recorder read `LoopGeometryCount`. Split into `PauseLoop()` (keeps frames) and `StopLoop()` (discards); the recorder now builds the loop on demand, so the feature no longer requires a playing loop to reach it. Verified end-to-end: 13-frame 7.7 MB GIF with the NETSCAPE loop block and 25 cs delays
- [x] **C-02/C-03** Map shortcuts were wired from both the D3D child window and `Window.KeyDown`, so typing "Vail" into the search box selected velocity and ↑/↓ double-stepped the tilt. One route now, with a focus guard for text-entry controls
- [x] **C-04** `LiveFeed.Stop()` blocked the UI thread up to 5 s from three call sites; now `StartAsync`/`StopAsync`, and `Dispose` closes the S3 client on a continuation instead of under an in-flight request
- [x] **C-05** A geocoder failure reached the dispatcher and ended the session. Caught locally; the global handler now marks non-fatal exceptions handled and surfaces them
- [x] **C-06** Proximity alerts measured to polygon *vertices*, so a warning whose nearest edge ran 5 km away but whose nearest corner was 60 km away never fired. `GeoMath.DistanceToRingM`/`DistanceToSegmentM` measure to edges; 6 regression tests
- [x] **C-07** Storm symbols were sized in Mercator metres — sub-pixel at national zoom, screen-filling up close. Now screen pixels, rebuilt when the zoom moves >5 %. Mesocyclone rings keep metres (a real radius) with a pixel floor
- [x] **C-08** `PaneManager._lastSnapshots` was never cleared on a pane-count change, so the wrong pane drove the linked camera for a tick
- [x] **C-10** Alert cache window and poll interval were both exactly 60 s, so an early tick was silently a no-op; cache lowered to 50 s
- [x] **C-11** `TrayNotifier.Activated`'s custom accessors leaked a subscriber per add and ignored remove

**Debt**

- [x] **C-09** `CommunityToolkit.Mvvm` was referenced and unused — now carries `MainViewModel`
- [x] **C-12** Nothing covered App/Render/Ingest. Added `OpenWSR.App.Tests` (43) plus 6 Geo ring-distance tests: **76 → 125**
- [x] **C-13** 13 stale `TASKS.md` checkboxes ticked after verifying each in the tree

**UI reorganisation** — the shell is now arranged around the four questions the app answers, each with exactly one place: WHERE (top bar) → WHAT product (bar above the map) → WHEN (time bar) → WHAT'S ON TOP (right panel).

- [x] **U-01** Product and tilt had no UI at all — keyboard only, documented in a MessageBox. Segmented `REF VEL SW ZDR PHI CC` bar with each button carrying its shortcut letter, unavailable products greyed, plus a tilt dropdown with steppers and an "n of m" readout
- [x] **U-02** Three data modes expressed three different ways, with the forecast transport living in the layers panel. One `LIVE · ARCHIVE · FORECAST` switcher owns the time bar; leaving a mode takes its data with it
- [x] **U-03** The layers panel was a 30-control scroll doing six jobs. Collapsible sections; per-layer opacity; home/alert radius moved to Settings; the symbol key became a reference card
- [x] **U-04** Measure and cross-section were both bound to right-drag simultaneously. Map tools are now one armed mode, with the gesture named in the status bar
- [x] **U-05** Every outcome shared one status line, so a failed alert fetch was gone in under a second. Errors persist in a dismissible bar (Esc), progress has a determinate strip, status keeps the running commentary
- [x] **U-06** The Phase 3 debug marker layer shipped as-is: 210 red squares at every zoom, no labels, no toggle. Now zoom-gated, ICAO-labelled up close, selected site drawn amber, toggleable
- [x] **U-07** First run opened on a hard-coded 2013 storm. Now opens on home's nearest radar live, or the national view with an empty-state hint
- [x] **U-08** Mode and product state moved out of control properties into `MainViewModel`
- [x] Keyboard/gesture reference card on `?` and **F1**, shown once on first run
- [x] Accessibility: 28 glyph-only controls given automation names; mode and tool segments answer to `TogglePattern`

**Gate:** [PASSED] 125/125 tests; build clean. Verified live against KMTX: product bar switches products, all three modes exercised end-to-end (live chunk streaming, archive day + 13-frame loop, HRRR 00z forecast), loop frames survive a pause, GIF export produces a valid animation carrying every layer.


## Velocity dealiasing (2026-08-18)

Velocity folds at the Nyquist limit, so a strong couplet reads with its sign reversed —
the field does not merely omit information, it asserts the opposite of the truth. This was
the largest remaining analytical gap in `docs/parity.md`.

- [x] Carry the Nyquist velocity from the RRAD block into `Sweep`. It rides on individual
      radials and is absent from some, so the builder takes the first in the cut that has
      it; nullable, because pre-2000 archives carry no RRAD block at all
- [x] `VelocityDealiasing`: region-based unfolding after Py-ART's `dealias_region_based` —
      segment the sweep into regions of continuous velocity, measure the mean step across
      every shared boundary, and pick a whole-interval shift per region that makes the
      boundaries agree. No sounding and no previous volume, so live partial volumes and
      1991 archives both work
- [x] Conservative by construction: a correction only crosses a boundary of at least a few
      gates, and only when the step really lands near a whole interval. Without this, two
      spurious folds chained through low-SNR speckle at 250 km turned −22 m/s into +82 m/s
- [x] `BuiltinTables.DealiasedVelocity` — the standard scale stops at 35 m/s and would clip
      everything the unfold just recovered into one saturated colour
- [x] One `Prepare` path in `RadarDisplayController` so the map, the loop and the
      cross-section cannot disagree; unfold runs before storm-relative, since subtracting a
      real motion from a folded value only moves the discontinuity
- [x] `resources/crosscheck/dealias_pyart.py` against Py-ART 2.2.5
- [x] 24 tests: synthetic fold-and-recover across fold depths (including a couplet whose
      sign is reversed by folding), whole-interval invariants, and golden structural
      agreement with Py-ART on the Moore volume
- [x] Moment segments switched from `Click` to `Checked`, matching the mode and tool
      segments — `TogglePattern.Toggle()` does not raise `Click`, so they were unreachable
      to assistive technology

**Gate:** [PASSED] 149/149 tests; build clean in Debug and Release. Verified live on the
committed KTLX 2013-05-20 20:16Z volume: Nyquist decodes to 26.12 m/s (Py-ART: 26.1200),
raw pinned at exactly ±26.00, unfolded field reaches the 78.36 m/s single-unfold ceiling,
corrections land in the same azimuth sectors Py-ART finds, in the same rank order.

### Known limitation, and what will not fix it

Corrects about two thirds as many gates as Py-ART. Two candidate fixes were tested and
both are dead ends:

- **A signal-quality gatefilter.** Measured on the Moore volume, the corrected gates
  average 29.3 dBZ against 15.8 dBZ for untouched ones, and at 250 km the tenth percentile
  is still 14.5 dBZ. The gates in question carry strong, real returns — filtering on weak
  signal would discard exactly the wrong ones. (This was written up as "the obvious next
  improvement" before it was measured. It is not.)
- **Loosening `MinBoundaryGates`.** At 3 the correction rate reaches 0.96 % against
  Py-ART's 0.89 %, but the largest shift becomes ±2 and the peak velocity jumps to
  130.5 m/s. Matching the count by fabricating a 290 mph wind is not matching.

Closing the gap properly means adopting Py-ART's multi-pass structure — several passes at
different `IntervalSplits`, reconciled — rather than tuning a single pass.


## Azimuthal shear (2026-08-18)

Rotation is what a velocity couplet means, but reading it off a velocity display is a
judgement call: the eye has to separate the couplet from the storm's own translation and
from the background flow. A uniform wind has no azimuthal gradient, so differentiating
across the beam removes both and leaves rotation as a number.

- [x] `AzimuthalShear`: linear least-squares derivative (Smith and Elmore 2004), the
      estimator the operational rotation products use. Each fit is at constant range so the
      derivative is purely azimuthal; several range gates are averaged to damp the noise a
      three-radial fit would carry. Parallel over radials, since a sweep is ~860k gates
      with a fifteen-sample fit behind each one
- [x] `Moment.AzimuthalShear` as a **derived** product: the decoder never emits it, the
      controller materialises it from velocity on demand and caches per cut, and it is
      offered wherever velocity is present
- [x] Always dealiases first, whatever the velocity toggle says. Differentiating across a
      fold produces shear several times any real vortex, so on a folded field the product
      would show its own artefacts rather than the rotation it exists to reveal
- [x] Minimum range of 5 km. A real vortex's shear is its angular velocity and does not
      vary with range; a *uniform* wind reads as V/R, so 30 m/s at 2 km reads 0.015 1/s,
      which is mesocyclone territory. Without a floor, ordinary wind over the radar site
      paints the colour of a tornado
- [x] `BuiltinTables.AzimuthalShear` — muted blue for anticyclonic, yellow through red for
      cyclonic, topping out at 0.02 1/s where a mesocyclone becomes a warning
- [x] `A` shortcut and an AZS button in the product bar
- [x] 10 physics tests: exact recovery of a known constant gradient, a solid-body vortex
      returning its angular velocity, the sign convention, falloff away from the core,
      close-range suppression, and the folded-versus-unfolded artefact magnitude

Fixed along the way: every tick on the colour scale read "0.0" for any product whose whole
range is under 1, because the tick formatter used a single fixed decimal. It now takes its
precision from the step size, which the 0.04-wide shear scale needs.

Two of the physics tests failed first time and both were the fixture, not the code — a
synthetic vortex has an angular seam at the antipode, and "a uniform wind has no azimuthal
shear" is false near the radar. The second is why `MinRangeM` exists.

**Gate:** [PASSED] 160/160 tests; build clean in Debug and Release. Verified live on the
committed KTLX 2013-05-20 20:16Z volume: a concentrated band of strong cyclonic shear runs
west-southwest from the radar through the Newcastle-Moore corridor, which is where the EF5
was at 20:17Z. The product locates it without being told where to look.

### Still open

Rotation *tracks* - accumulating shear across a volume's scans into a swath - are the
natural follow-on and are not done.


## Rotation tracks (2026-08-18)

A single scan says where rotation is now. A damage survey, or the question "did that
couplet hold together or fall apart", needs where it has *been* — and a mesocyclone that
tracks for fifty kilometres draws a line no individual scan shows.

- [x] `RotationTracks.Accumulate`: signed maximum of azimuthal shear per cell across a run
      of scans, on a Mercator grid. Signed rather than absolute, so a strong anticyclonic
      couplet cannot paint what looks like a tornado track
- [x] Reverse mapping — walk the output grid and project each cell back into polar space —
      because a forward mapping leaves holes wherever gates spread wider than a cell. Per
      sweep, an azimuth lookup table replaces what would otherwise be a linear radial
      search inside a million-cell loop
- [x] Each scan is smoothed 3x3 before accumulation. Taking a maximum over a dozen scans is
      unforgiving: one spurious gate in one scan survives into the swath forever. On the
      Moore volume this took the peak from 0.1796 to 0.1225 1/s
- [x] `MapView.OverlaySlot` — image overlays gained a second slot. `Field` draws under the
      radar (model output, mosaics), `Analysis` over it (products read against the echo),
      so the HRRR forecast and a rotation swath no longer fight over one slot
- [x] `RotationTracksController`: fetch, decode, unfold, shear and accumulate off the UI
      thread, with progress; opacity control and a one-line summary
- [x] The swath ends where the timeline is, not at midnight — the useful hour is the one
      leading up to whatever you are looking at
- [x] 8 tests: keeps the strongest value rather than the latest, a moving couplet draws a
      line, the swath geolocates (a feature due east appears due east), anticyclonic
      rotation does not masquerade as a track, and the corners beyond radar range stay
      unsampled rather than zero

Fixed along the way: **changing the radar site never moved the camera** except in live
mode and on the very first archive load. Picking a new site later left the map over the old
one while the data quietly changed underneath — which is how this was found, with a Moore
tornado swath rendering correctly off-screen in Utah.

**Gate:** [PASSED] 168/168 tests; build clean in Debug and Release. Verified live on KTLX
2013-05-20, scrubbed to 20:16:43Z: a swath over 19:30-20:17Z draws a concentrated band of
strong cyclonic rotation running west-southwest from the radar through the
Newcastle-Moore corridor — the EF5's path.

### Known limitation

Visible background speckle. Accumulating a maximum amplifies outliers, and the low Doppler
cuts of VCP 12/212 carry no correlation coefficient to filter marginal gates on. Smoothing
helps; a real quality mask would help more.


## Lightning — the last blocker (2026-08-18)

`docs/parity.md` called this out as the one remaining Blocker-severity gap, and named the
reason: "it is the only feature still stuck behind the dependency the plan was designed to
route around." The dependency was NetCDF. This pays it down.

- [x] Reconnaissance first, per the project's own strategy note. IEM publishes no lightning
      endpoint; GOES-16 is no longer filled (it was replaced as East in 2025 and returns an
      empty listing, which looks exactly like "no lightning"); GOES-19 and GOES-18 both
      carry GLM. There is no simpler source, so the format work was unavoidable
- [x] `OpenWSR.NetCdf` with `MiniHdf5` — the slice of HDF5 that GLM files actually use:
      superblock v2, version-2 object headers with continuation blocks, dense links in a
      fractal heap, version-2 dataspaces, version-1 datatypes, version-3 contiguous and
      chunked layouts on a version-1 B-tree, shuffle and deflate. Roughly 500 lines, and
      pure — it is in `PurityTests` alongside `Nexrad`, `Geo`, `Grib2` and `Placefiles`
- [x] The B-tree is skipped deliberately: every link in the group is wanted, so the fractal
      heap's direct blocks are walked instead. That removes the most intricate structure in
      the format and recovers all 54 links with nothing missing and nothing spurious
- [x] `GlmFile`: flashes with position, energy and time, dropping the ones the producer
      flags. 30 of 177 in the committed file are flagged
- [x] `GlmClient`: anonymous S3, listing by day-of-year hour prefixes, bounded-parallel
      fetch, and a window walked back two minutes because products publish behind real time
- [x] `LightningController`: a fading cross per flash, sized in screen pixels, rebuilt on
      zoom like the storm symbols
- [x] 23 tests. The 17 decoder ones take every literal from **h5py 3.16.0** on the same
      file; the 6 client ones cover day-of-year key parsing including leap years and junk

Two format traps cost real time and are written up in `docs/formats.md`: the root group
address sits at superblock offset **36**, not 40; and the version-2 filter pipeline omits
the name-length field for filter IDs below 256, so parsing it as v1 walks off by two and
reports a filter the file does not use.

**Gate:** [PASSED] 197/197 tests; build clean in Debug and Release. Verified live: enabling
the layer fetched and decoded **5,817 flashes from the last ten minutes** of GOES-19 in
three seconds, and pressing the hotspot finder put them over a severe storm near KCYS with
the crosses sitting in the convective cores.

### Every parity gap is now closed

`docs/parity.md` began with 31 gaps and 11 UI findings. All 31 are done or partial, and all
11 UI findings are done.


## MRMS native rendering (2026-08-19)

The GRIB2 reader has decoded MRMS since Phase 03 and is golden-tested against ecCodes;
what was missing was everything after the decode. The tile mosaic covered the picture, but
it is published only to zoom 12 and stretched above that, so the seamless national view
turns to mush exactly when you lean in on a storm.

- [x] `MrmsClient`: newest composite from `noaa-mrms-pds`, anonymous, walking back a day
      because just after midnight UTC today's folder can be empty. Files land every two
      minutes at about 845 kB
- [x] `MrmsController`: decode off the UI thread, then **quantise to palette levels rather
      than keeping floats** — 24 MB instead of 98 MB for the same picture, since the grid
      is only ever drawn through a 256-entry table
- [x] **Rasterise the visible window, not the country.** A single CONUS texture at 2048
      would be about 3.4 km per pixel — coarser than the tiles it is meant to improve on.
      Cutting the viewport (with 35 % overscan so small pans reuse it) keeps it sharp at
      any zoom, and re-cuts only when the view leaves the area last drawn
- [x] Mutual exclusion: native MRMS and the tile mosaic are the same field, and the HRRR
      forecast wants the same overlay slot, so enabling any one switches the others off
- [x] `Quantise` and `RenderRegion` are pure and public, so the draw path is testable with
      no graphics device — 5 tests covering the no-coverage flag, transparency off-grid,
      that the strongest cell actually paints, and that two different parts of the country
      do not render identically

**Gate:** [PASSED] 210/210 tests; build clean in Debug and Release. Verified live: the
layer fetched, decoded 24.5 million points and drew in about three seconds, on a composite
one minute old — `MRMS composite 14:50Z (1 min old), 7000×3500 at 0.01°`.

### Verification note

Captured at national scale once the workstation was unlocked —
`docs/screenshots/mrms-native.jpg`, a seamless CONUS composite three minutes old with no
radar-by-radar seams. (The first attempt failed because the session locked partway
through, and a locked session blocks synthetic input and window capture.)


## Independent site per pane (2026-08-19)

The last "Expected"-severity gap in `docs/parity.md`: panes shared one volume, so a split
view could compare products but never places.

- [x] `PaneFeed`: a pane's own data source. Live mode runs its own `LiveFeed`; archive mode
      lists that site's day and shows the scan **nearest** the primary's time, because
      radars are not synchronised with each other. Supporting both matters — a pinned pane
      that went blank the moment you scrubbed would be worse than not offering the option
- [x] A header strip per pane with a site selector ("Follow primary" or any WSR-88D) and a
      live readout of what that pane is showing. The strip is WPF *above* the D3D child
      window, never over it
- [x] **Pinning moves the pane's camera to the site and drops it from camera linking.**
      Found by testing: without this the link immediately dragged the pane back, so it
      showed Oklahoma's data over Utah
- [x] Pane state — including the camera-link snapshot — moved onto the `Pane` object.
      Positional arrays were already the cause of C-08, and filtering pinned panes out of
      the link would have reintroduced exactly that bug

**Gate:** [PASSED] 210/210 tests; build clean in Debug and Release. Verified live with two
panes: KMTX reflectivity over Utah beside KTLX velocity over Oklahoma, each on its own
feed, each with its own scan time — then unpinned and watched the pane rejoin the primary.


## VAD wind profile (2026-08-19)

A single radar measures only motion along its own beam — but it measures it all the way
around. Ride a ring of constant range and the radial velocity traces a sine wave whose
amplitude is the wind speed and whose phase is its direction. That is the whole Velocity
Azimuth Display idea, and it turns one number per gate into a wind.

- [x] `VadProfile`: least-squares fit of `v = a0 + a1·sin(az) + a2·cos(az)` on a ring of
      constant *height*, per layer, choosing whichever cut fits cleanest.
      `GeoMath.SlantRangeForHeight` — the inverse of `BeamPath`, now living beside it —
      turns a height into the range to sample
- [x] **Derived from Level II, not decoded from NVW.** NVW is still distributed (checked:
      TLX had files today), but this project already lost the cell attributes when NSS
      stopped being generated around 2021. Deriving works on any volume back to 1991, at
      every scan rather than every tenth minute, and cannot be withdrawn
- [x] Unfolds velocity first — a fold is a discontinuity around the ring, and least squares
      will happily average it into a wind that was never blowing
- [x] Refuses rather than guesses: a ring needs eight of twelve azimuth sectors populated,
      and a residual small against its own amplitude. A sine fitted through one quadrant is
      that quadrant's velocities wearing a wind's clothes
- [x] Proper station-model barbs — pennant fifty knots, full barb ten, half barb five, staff
      pointing where the wind comes from. A column of barbs shows veering at a glance in a
      way a column of figures does not
- [x] 19 tests: exact recovery of known winds across five directions including the north
      wrap, the beam-geometry round trip, a folded wind, a veering profile, and refusal of
      both partial rings and noise

**Gate:** [PASSED] 229/229 tests; build clean in Debug and Release. Verified on the
committed KTLX 2013-05-20 20:16Z volume: 40 levels to 12 km, surface flow 134° at 17 kt
with 238-253° at 45-73 kt aloft — about 120° of directional shear between the inflow and
the storm top, which is the hodograph that made that day what it was.

## Placefile icon sheets

- [x] `IconFile:` parsed into `PlacefileDocument.IconSheets`, resolved relative to the
      placefile's own URL, fetched and cached by resolved URL so two files naming the same
      sheet fetch it once
- [x] `QuadRenderer` grew a rotated textured quad: the shader takes a clip-space pivot and
      a cos/sin pair, with cos=1/sin=0 as the identity so every existing caller is unchanged
      and pays nothing. Aspect is passed in so a rotation stays square on screen
- [x] `MapView` icon layer: fixed screen size, pinned by the sheet's hot spot rather than
      the cell's centre, per-sheet texture cache invalidated by reference
- [x] **Black is the transparent colour when a sheet has no alpha.** The IEM wind-barb
      sheet is plain opaque RGB, grey barbs on black; drawn literally it is a grid of black
      tiles. Key out black only when every pixel decodes opaque — a sheet with real alpha is
      trusted, or a legitimately black icon would be punched through
- [x] A sheet that fails to load leaves its icons as plain markers rather than failing the
      whole file; one missing PNG should not cost you the overlay
- [x] 11 tests, including both sides of the alpha rule

**Gate:** [PASSED] 240/240 tests. Verified live against the IEM ASOS placefile at KDMX:
full station models across Iowa — temperature and dewpoint pairs with wind barb staffs
turned to each station's own bearing, which is the `Icon: 0,0,70,1,1` rotation working.

## Configurable loop length

- [x] `LoopFrames` was a `const`, not a setting — the note claiming otherwise was wrong.
      It is now an instance property clamped to 2..144, persisted in `settings.json`, and
      chosen in Settings as a duration ("about 5 hours") rather than a count
- [x] Ceiling of 144 volumes — twelve hours of a five-minute VCP. A full UTC day is around
      250 volumes at roughly 5 MB of held geometry each, which is over a gigabyte and a
      download long enough that the loop is no longer about the weather you were watching
- [x] The span is part of `LoopSignature()`, so changing it rebuilds rather than silently
      replaying the old window
- [x] The play button's tooltip stated a hardcoded thirty; it now reports the configured
      span and its rough duration
- [x] 11 tests: the clamp at both ends, the default, a settings round trip through the real
      serializer, and a pre-existing settings file with no `loopFrames` key at all

**Gate:** [PASSED] 251/251 tests. Verified live end to end: chose "60 volumes" in the real
Settings dialog, confirmed `"loopFrames": 60` in settings.json, and watched the build
report `Downloading loop volumes… 13/60`.

## Drawing tools

- [x] Lines, filled areas, circles and labels placed on the map, in six colours and eight
      thicknesses. Held in geographic coordinates, so an annotation stays on the storm
      through a pan, a zoom, or a change of radar
- [x] **Saved as ordinary GRLevelX placefiles**, not a format of our own. A drawing opens
      in GR, can be handed to someone else, and comes back in through the parser that is
      already golden-tested. `OpenWSR.Placefiles` gained a writer and gained one dependency,
      `Geo` — a circle cannot be written without geodesy, because the ring has to be a true
      ground radius rather than a Mercator one
- [x] A circle survives the trip as a circle: the ring is preceded by a comment naming the
      centre and radius, matched back **by geometry** on read. GR ignores the comment; a
      tool that strips it costs the circle-ness and nothing else
- [x] Vertices are placed by clicking, not dragging — a left drag already pans the map, and
      taking that away for one tool would make the map feel broken while it was armed.
      Enter finishes, Esc abandons, Backspace takes back a point then a shape
- [x] Any placefile can be opened to trace over, and shapes are added rather than replacing
      what is there
- [x] 25 tests: every shape kind round-tripped, colour and width, a circle's true ground
      radius at 61°N where Mercator distortion is severe, two same-radius circles not
      stealing each other's hint, a stripped hint, syntax characters inside a label, a
      double save-and-reopen, and a foreign placefile importing

**Gate:** [PASSED] 276/276 tests. Verified live at KDMX: all four shape kinds drawn on the
map by synthetic clicks and rendered correctly, then a written file loaded back through
OpenWSR's own placefile pipeline — it appears in the layers panel under the title the
writer gave it, which is the interoperability claim proved rather than asserted.

Also fixed on the way past: tooltips were white text on WPF's default light popup, because
a tooltip inherits `Foreground` from the control it belongs to and every control here is
styled for a dark ground. `Theme.xaml` now templates ToolTip too.

## 3D volume rendering

- [x] `GeoMath.BeamAngleTo` — the other inverse of `BeamPath`: *which* beam passes through a
      point, not how far along a known one. Closed form on the 4/3 earth. `CrossSection` had
      this inline; it is now shared, which is why a slice through the 3D view and a
      cross-section of the same volume agree by construction
- [x] `VolumeGrid3D` — resamples a volume onto a Cartesian box by walking the grid and asking
      which beam reached each cell. 256 × 256 × 64, about 4 MB, parallel over layers.
      Voxel 0 means **unsampled**, never "weak": the cone of silence, everything below the
      lowest beam and everything above the top cut have to be see-through
- [x] `AzimuthIndex` hoisted out of `RotationTracks` and shared. Finding the nearest radial
      by scanning is fine once and ruinous a few million times
- [x] `VolumeRenderer` — ray marched, front to back, early-out at opacity. No isosurface, so
      no dBZ threshold has to be invented; weak echo stays as haze and a core is solid
      because it was integrated. Ground disc with range rings and a north line drawn in the
      same shader, because a storm in a void has no scale
- [x] Ray starts are jittered per pixel: marching every ray from the same place makes the
      step size visible as bands the radar never measured
- [x] `VolumeCamera` — orbit, not free flight. It is possible to get lost inside a
      thunderstorm; orbiting cannot. Elevation clamps short of the poles so the frame never
      collapses, and right is built horizontal so the horizon never rolls
- [x] Drag orbits, right-drag pans, wheel zooms. Tilt is disabled in 3D — it is every cut
      at once — while the product bar still applies
- [x] Debounced rebuilds, and the previous volume stays up while a live one is still
      scanning: otherwise the view blanks for a minute out of every five
- [x] 35 tests: the beam inverse round trip, cone of silence, nothing above the top cut,
      nothing outside the range disc, every filled voxel inside the scanned span, real gates
      landing in the right voxels, camera orthonormality and a level horizon over forty
      orbit steps, and a shader-compile test so a broken shader is a red test rather than a
      black window

**Gate:** [PASSED] 311/311 tests. Verified live: KFSD, 15 cuts 0.4°–12.3°, rendered at
60 fps with visible vertical structure, cores, range rings and the ground disc; the guard
for a still-scanning volume was seen firing on a real 1-cut live volume and was the reason
the debounce and keep-previous behaviour were added.

## Velocity dealiasing — closing the gap to Py-ART

- [x] Replaced the spanning-tree **walk** over regions with Py-ART's **merge**. The walk
      grew outward from the largest region and judged each boundary alone; the merge folds
      the smaller side onto the larger and then combines the merged side's remaining
      boundaries with the base node's. A region touching a large merged area through three
      thin boundaries is therefore judged on all three at once
- [x] Correction rate went from ~2/3 of the reference to **85–92 %**, measured on the
      committed Moore volume against Py-ART 2.2.5:

      cut     walk            merge           Py-ART
      0.5°     738 (0.47 %)   1288 (0.83 %)   1393 (0.89 %)
      1.3°    2788 (1.84 %)   3478 (2.29 %)   4116 (2.71 %)

      Largest shift stays ±1 and the peak unfolded speed *fell*, 78.2 → 75.2 m/s
- [x] `MinBoundaryGates`, `MinRegionGates` and `FoldTolerance` deleted — the walk needed
      them, the merge does not. Leaving three constants whose long measured comments
      described an algorithm that no longer existed would have been worse than no comment
- [x] Anchoring stays on the largest region rather than Py-ART's zero-mean centring: a
      forecaster reading a number off the screen should get the measured value unless there
      was a reason to change it
- [x] Two tests that discriminate: a folded core reachable only through three single-gate
      bridges (the walk leaves it at −18.2 m/s, the merge recovers 34), and its converse
      with no bridge at all (both leave it alone). The Py-ART rate guard's floor moved from
      0.4× to 0.8× so a regression to the walk fails loudly

**Gate:** [PASSED] 313/313 tests. The two new tests and both rate cases were run against the
old solver to confirm they fail on it — a regression guard that cannot fail is not one.

Both wrong diagnoses are recorded in `docs/verification.md`, because both are the obvious
ones. The answer came from reading `region_dealias.py`, not from reasoning about it.

## Rotation-track speckle — a reflectivity quality mask

- [x] The old note said the low Doppler cuts have no correlation coefficient to filter on.
      True of that sweep, and beside the point: on a split-cut VCP the Doppler cut carries
      **reflectivity** alongside velocity, index for index. That is the field that matters
- [x] Measured the case before building anything: **88.8 %** of strong cyclonic shear gates
      on the Moore volume sit under reflectivity below 20 dBZ. Shear where nothing reflected
      is the phase of receiver noise, and a maximum over a dozen scans makes it permanent
- [x] `GateQuality.MaskByReflectivity` blanks them. Removes **89 %** of the strong-shear
      gates; the peak stays bit-identical at 0.1297 1/s, in the same array position. The
      peak survives every threshold from 5 to 25 dBZ, so 20 was chosen for noise removed
      rather than by trading away signal
- [x] Gates are matched by **azimuth and slant range**, not by index — the surveillance cut
      at the same elevation has a different gate count, and indexing one with the other's
      numbers is wrong by kilometres without ever looking wrong
- [x] Applied to the live AZS product too, so it and the swath show the same field
- [x] Smoothing changed from a 3x3 **mean** to a **median**. With the mask doing the heavy
      lifting its job changed from holding down a noise floor to removing isolated spikes,
      which is what a median is for: 27 % of the speckle for 8 % of the peak, where the mean
      took 38 % for 16 %
- [x] **No CC mask, deliberately.** A tornado debris signature is a low-CC, high-Z target —
      34.6 % of strong shear under 40+ dBZ has CC below 0.85, so a CC threshold deletes a
      third of the signature the product exists to find
- [x] 15 tests, including the rule itself, slant-range matching, and both halves of the
      claim on the Moore volume

**Gate:** [PASSED] 328/328 tests. Verified visually on the Moore volume — the diffuse halo
goes, the track stays (`docs/screenshots/rotation-track-quality-mask.png`), and a 12-scan
swath built live from an archive day.

Still open: ground and sea clutter return strongly, so reflectivity keeps them. That is a
different problem from noise and needs a discriminator that separates clutter from debris.

## Code review of the 3D and dealiasing work

A review of `HEAD~4..HEAD` — the 3D volume rendering, the Py-ART merge rewrite, the shear
quality mask and the measurement harnesses. Eight findings, all applied.

- [x] **The azimuth index answered with a floor, not a nearest.** Both its doc comments
      claimed nearest and both passes of the gap fill swept the same direction, so the second
      was a no-op. Every consumer — the volume grid, the reflectivity mask, the rotation-track
      swath — was biased the same quarter of a degree clockwise, 650 m at 150 km. It swept
      once each way now, keeping whichever candidate is fewer bins away
- [x] Nothing caught it, because the error is smaller than a radial spacing: no decode
      changes and no golden file moves. `AzimuthIndexTests` is the guard, and two of its five
      cases were confirmed to fail against the old implementation
- [x] Re-derived every figure that reads through the index. The shear quality numbers are
      bit-identical — 88.8 %, peak 0.1297 1/s, 34.6 % of debris-signature gates under CC 0.85.
      The **smoothing comparison moved**, and its conclusion with it: measured again, none 34
      / mean 25 / median 23 strong cells, so the median now removes **32 %** of the speckle
      against the mean's 26 % for the same 8 % of peak against 16 %. It used to be a trade
      the median lost on volume; it is a clean win. The table in `RotationTracks.Smooth` and
      the recorded result in `SmoothingMeasurement.cs` both carry the new numbers
- [x] **The 3D view materialised its cuts on the UI thread.** Only the grid build was behind
      `Task.Run`; `SweepsForCurrentMoment` was in front of it, and for a derived moment that
      is a full dealias and shear recompute per cut — fourteen of them on VCP 212, on every
      live volume. Moved behind the same `Task.Run`, which meant making `_shearCache`
      concurrent: the map materialises the displayed cut on the UI thread at the same time
- [x] **Leaving 3D never released its textures.** `Clear()` only stages the release and
      `Draw` services it, but `VolumeMode` is cleared first so `Draw` never runs again — 4 MB
      of R8 volume texture plus the palette and the voxel array stayed resident until the next
      entry. `ClearNow()` releases on the render thread, where the caller already is
- [x] `stackalloc` inside the per-gate loop in `RotationTracks.Smooth` — `localloc` is not
      reclaimed until the frame returns, so a 1832-gate sweep grew each `Parallel.For` body's
      stack by 64 KB. The only warning the build emitted (`CA2014`); the build is clean now
- [x] The volume shader normalised its step length against a hardcoded 256 cells, which is a
      parameter of `VolumeGrid3D.Build` the renderer cannot know — the tests already pass 64
      and 96. It comes from the upload now
- [x] `VolumeCamera` was documented as safe to read across the thread boundary and was a
      mutable class; a pan concurrent with a frame could tear the twelve bytes of `TargetM`.
      Its pose is an immutable record swapped in one reference write now, which is what the
      comment always claimed
- [x] `ResetVolumeCamera` hardcoded the box top instead of reading `VolumeController.TopHeightM`
- [x] `CLAUDE.md`'s velocity-aliasing note still described the deleted `MinBoundaryGates` and
      told the reader to go and read its measurements. It describes the merge solver now

**Gate:** [PASSED] 333/333 tests, 0 warnings. The shear and smoothing harnesses were both
re-run against the fixed index; the new `AzimuthIndexTests` were checked to fail against the
old one.

## Home marker sized at the wrong zoom

- [x] The home triangle was drawn ~80 km across once the camera was over a single site.
      `RebuildHomeGeometry` sizes it in pixels (`6 * MetersPerPixel`) but ran only at startup,
      on setting home, and on settings-apply — never from the 500 ms tick that rebuilds the
      storm, lightning and MRMS overlays when the zoom moves. So it kept whatever metre size
      it was handed during startup, which is a national-zoom size
- [x] `NotifyHomeViewChanged` joins that tick on the same 5 % threshold the others use. The
      radius ring is a true ground extent and was always right; only the marker moves

**Gate:** [PASSED] 333/333 tests. This is the failure mode `CLAUDE.md` warns about under
"Overlay symbols are sized in screen pixels" — the home marker was the one symbol that had
the multiply but not the rebuild.

## Radar field: hardware filtering instead of a point sample

Asked for "anti-aliasing like GPUs use" on the map. MSAA is the wrong tool for it and the
investigation is the useful part: each radial is an instanced triangle strip and adjacent
gates share edges, so the colour step between gates falls in the rasterised interior where
coverage is 100 %. There is no geometric edge for MSAA to resolve. What the field needed was
filtering, which the sentinel encoding made impossible.

- [x] **The field ships as two planes now.** `Values` holds value×valid and `Mask` holds
      (valid, rangeFolded). Premultiplying is the whole trick: a bilinear tap and a mip level
      both reduce to a mean over the gates that measured something, divided by how much of the
      footprint they covered. A single sentinel-encoded plane cannot be filtered at all —
      there is no sentinel value that averages correctly with real data, which is why point
      sampling was the only safe thing to do with it
- [x] The `NoData`/`RangeFolded` constants and both magic thresholds in the shader are gone;
      validity is explicit
- [x] Full mip chain via `GenerateMips` — a box filter over premultiplied planes is exactly
      the weighted average wanted — sampled 16× anisotropic, because a pixel's footprint in
      gate/radial space is wildly elongated and an isotropic filter has to pick a mip for the
      worse axis and blurs the other away with it
- [x] **`o.uv.y` was constant across each wedge**, so every pixel sampled one texel row and no
      amount of filtering could blend across azimuth. It walks from one radial boundary to the
      next now, which also fixes the LOD derivative at the seam between two instances
- [x] Echo edges were a hard `discard` and are feathered by coverage — the actual antialiasing
      of the boundary, and free because coverage is already in hand
- [x] **Upload went 40.2 ms → 1.1 ms**, measured rather than guessed. Instrumenting it showed
      `GenerateMips` free and the whole cost in the CPU split: keeping the textures across
      uploads removed the mip-chain allocation, and moving the split into `SweepGeometry.Build`
      took the rest off the render thread. Costs ~50 % more per archive-loop frame (4 bytes
      plus 2, against 4) — the trade for not hitching on every new volume
- [x] The sweep shader had no compile test; only the volume one did. `ValidateShaders` and
      five tests on the encoding, including the property the design rests on: four gates with
      two holes must average to 30, not 15

**Gate:** [PASSED] 339/339 tests, 0 warnings. Verified live on KMTX — smooth field with the
smoothing slider still at 0, 60 fps, 1.1–2.0 ms uploads.

Note: the Smoothing slider's 0 end now means bilinear, not raw gates. There is currently no
way to see unfiltered gate blocks.

## Storm motion: 0 mph was two claims nobody measured

Reported from the app: clicking a storm showed its distance but "0 MPH". The decoder was
fine — live NST pulled for eight sites showed most cells carrying tracks — and the bug was
in deriving motion from them. Two bugs, in fact.

- [x] **A forecast point sitting on the cell is not motion.** The algorithm emits a cell the
      first time it sees one with `fc0` exactly equal to the current position. The old code
      took the distance between two identical points, got zero, and rendered "Moving N (0°)
      at 0 mph" — a claim that the storm was measured stationary *and* measured to be heading
      north. `SpeedKmh`/`BearingDeg` are nullable now and the popup says the motion is not
      tracked yet, the same distinction as voxel 0 meaning unsampled
- [x] **The past-position fallback was on the wrong clock.** The first forecast step is 15
      minutes out by definition of the product; the most recent past position is one *volume
      scan* back. Both were read as 15 minutes. Measured against the forecast leg on 24
      tracked cells across four sites, the past leg came out at a median of **0.30** of the
      real speed — 4.5 minutes, exactly the VCP 12/212 scan time. Those cells were reporting
      about a third of how fast they were moving
- [x] `VolumeCoveragePattern.NominalScanMinutes` derives the interval from the VCP, which
      `Level3Product` already carries, rather than hardcoding one. Precipitation VCPs sweep in
      4.5–6 minutes, clear-air ones take 7–10
- [x] A leg under 250 m — one quarter-kilometre centroid step, the resolution positions are
      reported at — is rounding rather than movement, so a degenerate forecast now falls
      *through* to a usable past track instead of winning with a zero
- [x] The wrong speeds were also feeding the average storm motion behind storm-relative
      velocity, the projection cones and the proximity alerts
- [x] Motion logic extracted to `StormTrackMotion` because it was private inside a controller
      that needs a live `MapView` and so could not be tested at all. Sixteen tests

**Gate:** [PASSED] 355/355 tests, 0 warnings. Verified against live NST from KMTX, KEAX,
KMLB, KAMX, KTLX and KFWS; the 0.30 ratio is reproducible from that dump.

## Finding the storms that are coming for you

Two halves of one question. Proximity alerting already existed but only ever spoke by
interrupting — a tray balloon, once an hour, then silence — and arming it meant knowing
your own latitude.

- [x] **`GeoLocationService`** wraps Windows Location Services: one call, a
      `LocationFix` with the provider that answered and the accuracy radius it quoted.
      Verified unpackaged on real hardware — `Allowed`, a Wi-Fi fix, ±141 m. Requires the
      App TFM at `net10.0-windows10.0.19041.0` for the WinRT projection; the tests project
      moves with it
- [x] Failure here is nearly always a privacy switch, and there are two of them in different
      places, so every failure message names both. An unpackaged desktop app gets no consent
      prompt when either is off — the call just returns `Denied`, which as a bare status code
      is indistinguishable from a bug
- [x] **Settings › MY AREA** gains "Use my location" beside "Pick on map", and the label
      records where the number came from: "35.2226, -97.4395 (from Wi-Fi, ±463 ft)". A GPS
      fix and an IP-address guess a state wide are both "your location" and should not read
      the same
- [x] Accuracy is formatted by a new `Units.ShortDistance` — metres or feet. `Units.Distance`
      renders a 141 m fix as "0.1 mi", which throws away the difference between a good fix
      and a useless one
- [x] **Home is now edited on a copy** and written back only on Save. Every other control in
      that dialog was already read on save, so Cancel undid it; home was not, and locating
      moved the real home the instant it succeeded — Cancel left it moved, waiting for the
      next unrelated `Save()` to commit it
- [x] **`ThreatMonitor` gained a second output.** `ThreatDetected` still fires once per hour
      per source and is what interrupts; `ThreatsChanged` carries the whole current set on
      every evaluation and is what the side list draws. A threat still present on the next
      poll has to stay on the list *without* re-alerting, which is why the throttle sits on
      the notification and not on the set
- [x] The storm and warning halves refresh on different clocks — the Level III poll and the
      api.weather.gov one — so each evaluation replaces only its own half
- [x] **The APPROACHING panel** sits above the layers in the right column, collapsed whenever
      nothing is threatening. The layers toggle governs only the lower half: tidying the
      layers away must not take a tornado warning off the screen with it. Clicking a row puts
      the camera on it
- [x] `ExpireStale` on the half-second tick drops warnings past their end time. The alerts
      poll does this too, but once a minute, and an expired warning left on the list reads as
      a live one

Both of the ordering decisions came from looking at the live panel, not from reasoning.

- [x] **Nearest first, ETA only breaking ties.** Soonest-first was written first and reads
      wrong: a cell whose closest approach is where it already sits reports an ETA of zero —
      it is at its nearest now, and may be receding — so on live KMTX data four such cells at
      18 to 44 miles all sorted above V1, which was closing to 14 miles over the next
      nineteen minutes. A 49-mile warning with no track at all sorted above it too
- [x] **Advisories are not threats.** The warnings layer draws statements when they are
      ticked, and it should. But "Special Weather Statement, 49 miles away" was landing at the
      top of the threat list — and raising a tray balloon. Warnings always count; anything
      else has to carry a Severe or Extreme severity
- [x] Rows carry a short label ("Storm V1") rather than the notification's sentence. The panel
      heading already says APPROACHING; repeating it down every row of a 248 px column leaves
      no room for the range, which is the part being scanned for

**Gate:** [PASSED] 367/367 tests, 0 warnings. Location verified against Windows on real
hardware; the panel verified against live KMTX storms.

- [x] **Open on the house, not the tower.** Startup already framed home and then lost it: the
      next line called `ApplyMode(Live)`, which starts the feed, which framed the tower
      straight over the top of it. `FrameSite` makes that one decision instead of a sequence
      of moves — home wins whenever the site in question is home's own radar, a deliberately
      chosen other site still frames its tower — so whoever moves the camera last computes the
      same answer. A WSR-88D is routinely fifty miles from the ground you care about

- [x] **GOES IR was painting the map with an error message.** Reported from the app: the
      satellite layer came up solid red with "Invalid TMS Request :( Need help?
      akrherz@iastate.edu" tiled across it. The layer name had drifted — `goes_east_ch13` is
      now `goes_east_conus_ch13` — and IEM answers an unknown layer with **HTTP 200 and a
      valid PNG** of that message, so the fetch path could not tell it from imagery. 253
      copies of the one image had been written to the disk cache as data
- [x] Tile cache directories are named for the layer rather than the provider now, so fixing
      a path abandons what the old one wrote instead of serving it for ever. The stale
      directory was removed — verified first that all 253 files hashed to a single value
- [x] Verified by fetching two different tiles and confirming the bytes differ, which is the
      only check that means anything here; the fresh cache holds 24 tiles with 24 distinct
      contents. The national mosaic was checked the same way and is unaffected

## Broadcast parity: clouds and terminal radar

Phased. The question behind it was whether TV stations fuse radar and satellite into one
product; they do not — the two stay independent layers composited at draw time, and the
work that makes them look like TV is on each layer separately.

### Phase 0 — GOES IR smoothing

- [x] **The jagged look was fabricated zoom, not sampling.** The tile quads already sample
      `MinMagMipLinear`. The layer was capped at z10, but ABI band 13 is 2 km at nadir and
      worse at CONUS latitudes — about **z6** in Mercator. IEM does not refuse a z10 request,
      it nearest-neighbour upsamples, so every source pixel arrived as a hard-edged block.
      Fetched z8/z9/z10/z11 directly to confirm: the staircase is already visible at z8 and
      by z11 the tile is a handful of flat parallelograms
- [x] Capped at z7 — one level of oversample above native — and the GPU's linear filter does
      the magnification. Also the honest answer: the detail past there was never measured, so
      clouds going soft as you zoom in is what the data supports

### Phase 1b — HDF5 attributes

Reading ABI needs attributes, and `MiniHdf5` deliberately did not walk them. GLM had worked
around it by copying two constants out of the L2 specification; ABI cannot, because its
whole projection — the perspective height, the earth figure, the sweep axis, the scan-angle
scaling — lives in attributes and nowhere else.

- [x] `Hdf5Attribute` with `AsDouble`/`AsString`, `MiniHdf5.AttributesOf(name)` for a dataset
      and `AttributesOf()` for the root group. Parsed on demand: a NetCDF-4 file carries
      hundreds and a caller wants three
- [x] **Compact storage is the path that never fires.** Attributes as object-header messages
      (0x0C) are what the format documentation leads with, but past a handful HDF5 moves them
      into a fractal heap and leaves an Attribute Info message (0x15) behind. Every netCDF-4
      variable crosses that threshold — the ABI file has *no* 0x0C messages at all. The first
      implementation handled only the compact form and reported "no attributes" for
      everything, which looks exactly like success
- [x] The heap walk is the one already written for dense links, split so both can scan it.
      Same byte-at-a-time validation, and the same reason: the offsets live in a B-tree this
      reader deliberately does not implement
- [x] **A dataspace can be four bytes.** The validator required eight, which is right for a
      rank-1 array and wrong for a scalar — and every text attribute is a scalar. It returned
      all the numbers and silently dropped every string, including `sweep_angle_axis`, which
      decides the handedness of the projection. That failure looked like a working reader
- [x] Eleven tests against h5py 3.16.0 reading the same two files. Committed an ABI CONUS
      band 13 file (3.9 MB) as the second fixture
- [x] `GlmFile` reads its energy scaling from the file now, keeping the published constants
      only as a fallback for a file that omits them

### Phase 1c — the ABI decoder and the geostationary projection

- [x] `Geostationary` in `OpenWSR.Geo`, beside `LambertConformal`. Not a map projection in the
      usual sense — it is what a camera 35,786 km up sees, so both directions have to be able
      to answer "nowhere": most of the planet is over the horizon, and most of a full-disk
      grid is empty space. Checked against **pyproj 3.7.2** driving PROJ's own `geos`, to 1e-9
      radians, which is about a metre on the ground
- [x] The height in the file is above the *ellipsoid*; the equations want distance from the
      earth's centre. There is a test for it because the failure is a plausible-looking image
      in the wrong place rather than an exception
- [x] `AbiFile` in `OpenWSR.NetCdf` decodes CMIP to brightness temperature. It reports the
      projection as plain numbers rather than building one — `Placefiles → Geo` stays the only
      edge between the pure libraries
- [x] `_Unsigned = "true"` on a variable declared signed 16-bit: the fill reads as -1, means
      65535, and scales to about -3900 K. `valid_range` is written in the declared type too,
      so its upper bound arrives negative
- [x] Verified against h5py and NumPy: individual pixels, exactly 3,702,837 valid of 3,750,000
      — the gaps are the CONUS rectangle's corners, which are off the limb of the earth and
      were never measured. pyproj agrees they are not on the planet
- [x] The strongest check is the file's own: ABI stores min, max and mean brightness
      temperature as plain floats computed by the producer before quantisation. Decoding and
      re-deriving them reproduces the mean to five decimal places

**A latent bug in the HDF5 reader surfaced here.** Chunked reads placed each chunk with a
single contiguous copy. In one dimension a chunk *is* a run and that is right — which is why
lightning never caught it. In two a chunk is a **tile** spanning many rows of the destination
and has to be scattered a row at a time. The old code laid each whole tile along one row and
left the rest of the array at zero, which for scaled data decodes as a uniform field of the
`add_offset` — 89.62 K everywhere — rather than as anything that looks broken.

### Phase 1a/1d/1e — fetch, reproject, and put it on the map

- [x] `AbiClient` reads `ABI-L2-CMIPC` from `noaa-goes19`, the same bucket GLM already uses.
      CONUS is the sector worth having: five minutes, 2 km, about 4 MB a band. The scan-mode
      digit varies with the schedule, so a key prefix has to stop before it and the band is
      matched from the `C{nn}` token
- [x] A third overlay slot, `Satellite`, drawn below everything. Deliberately not `Field`:
      Field's claimants are all the same quantity as the radar and are mutually exclusive by
      construction, whereas the whole point of clouds is seeing them *under* precipitation.
      Sharing a slot would have made the two turn each other off
- [x] `SatelliteController` resamples by walking the output raster and projecting each pixel
      back — the same reverse mapping the HRRR raster and the 3D volume use, and for the same
      reason. This source stretches hard: it is angles from a camera, so a pixel over Canada
      covers several times the ground one over the Gulf does
- [x] **The bounding box comes from the edges, not the corners.** Three of the four corners of
      the CONUS rectangle are on the earth and one is not, which is exactly the shape of trap
      that survives review — a corner-based box looks like it works
- [x] The infrared enhancement leaves warm ground transparent. An IR image has a reading at
      every pixel, so painting all of them buries the basemap under a grey sheet; what the
      layer is for is where the cloud is. Grey to about −40 °C, then the colour ramp every
      broadcast product uses for glaciated tops
- [x] The tile layer is kept as the fallback and draws beneath the native raster, so a bucket
      outage degrades to somebody's pictures rather than to nothing
- [x] Eight raster tests, on top of the pyproj-checked projection: the box covers CONUS, a
      pixel over Utah carries the temperature the source has at that ground, opacity rises
      monotonically as tops get colder, and deep cold is coloured where ordinary cloud is grey
- [x] Verified live: `GOES-East IR 03:42Z (3 min old)`, against the tile layer's tens of
      minutes

**Still open in this phase group:** Phase 2 (GeoColor-style day/night blend) and Phase 3
(TDWR).

## The left rail had run out of room

Reported from the app: not everything fits. Measured through the automation tree rather
than by eye — sixteen buttons at 44 px needed **1109 px** against **1096** available, so
`About` was entirely below the window edge and unreachable. Worse, the bottom group is
docked to the bottom, so overflow eats Settings, Help and About first: the three least
worth losing. There is no scrollbar and no overflow affordance, so it fails silently.

Condensing the buttons was the other option and was rejected: it shrinks the targets, buys
headroom once, and leaves the same problem for the next tool.

- [x] **Set home** → Settings › MY AREA, which already had "Pick on map" arming the identical
      tool and "Use my location" beside it. The `SetHome` tool itself is unchanged; only its
      rail button is gone, so no rail toggle is lit while it is armed — correct, because
      lighting Inspect would name a different tool than the one that is armed
- [x] **Palette import** → Settings. Set-once configuration, which is the line the shell
      already draws. It still applies to whichever product is showing, so Settings raises
      `WantsPaletteImport` and hands back rather than opening the picker itself — the same
      pattern `WantsHomePicker` established
- [x] **About** → folded into the card the `?` button already opens. Both are reference
      material and `InfoWindow` is where reference material lives
- [x] **Link panes** → collapsed at one pane instead of merely disabled. A greyed button
      still costs a permanent 44 px for something that cannot act until a second pane exists
- [x] 1109 px → about 840 px. Verified live: everything on screen with headroom, Link appears
      and disappears with the second pane, the help card offers About, and Settings carries
      the importer

## "It says a storm is coming when it is going the other way"

Reported from the app, and two separate defects behind it.

**The receding one was a bug with a one-line proof.** `ClosestApproachToPath` seeds its
search with the distance at the current position and only improves on it, so a storm that
never gets closer returns *(current distance, 0 minutes)* — which `ThreatMonitor` rendered as
"now". The function had no way to say "never gets closer than it already is".

**The other was a design gap.** Any pass inside the alert radius produced the same alert, so
a thirty-five-mile miss inside a fifty-mile radius read exactly like a direct hit.

- [x] `GeoMath.PathApproach` carries `CurrentKm` and `FinalKm` beside the closest approach,
      which is what separates `IsReceding` from `IsClosing` from `IsStationary`. It also
      carries *where* the closest approach happens, so the side can be named
- [x] Three outcomes: **Direct** (within the direct-hit radius and closing) interrupts;
      **Glancing** lists, says which side it passes, and stays quiet; **receding** is not on
      the list at all — the panel is titled APPROACHING
- [x] `Threat.Interrupts` gates the tray balloon. Warnings are exempt from the tiering — a
      polygon carries no track, so there is nothing to judge — and so is a rotating cell,
      whose forecast track is the part of this least worth betting on
- [x] New `DirectHitRadiusKm` setting, default 8 km / 5 mi, clamped to the alert radius. The
      last option is "anything inside the alert radius", which restores the old behaviour
- [x] The panel heading now reads **IN THE AREA** when nothing is actually on course, and the
      count separates the two claims: "6 · 1 passing wide" rather than "7"
- [x] The storm popup says the same three things, so clicking a cell answers "is this coming
      for me" directly

**The over-filter this nearly introduced.** SCIT emits forecast points sitting on the current
position for a while after a cell appears, but `SpeedKmh`/`BearingDeg` are derived from the
*past* track and are usually known by then — the projection cone already relies on exactly
that. Treating a degenerate forecast as "not tracked yet" dropped storms that were
demonstrably closing. `ClosestApproach` extrapolates an hour along the measured heading
instead; only a degenerate forecast **and** null motion is genuinely untracked.

**Gate:** [PASSED] 434/434 tests, 0 warnings. Verified against live KMTX with home moved into
the storm field: six cells in an 80 km radius all classified **passing wide**, heading reading
`IN THE AREA · 6 passing wide`, none interrupting — where all six would previously have raised
a tray balloon. Widening the direct-hit radius over the same weather flipped the heading to
`APPROACHING · 6 · 1 passing wide` with the direct rows carrying no compass letter, because a
direct hit does not pass to a side. Settings were backed up and restored.

### Phase 2c — the day/night composite

- [x] Daylight decides the fetch as well as the pixel. `AnyDaylightOverConus` samples across
      the sector rather than at its centre, because CONUS spans about three hours of longitude
      and at dawn one edge is lit while the other is dark — a centre reading would drop the
      visible bands for half the country. Dark everywhere means the single 3 MB infrared band
      instead of 40-plus for all sixteen
- [x] `SolarPosition.DaylightFraction` cross-fades the two per pixel, so the terminator is a
      band rather than a line sweeping westward across the map
- [x] **The satellite here is an overlay, not a basemap, and that changes the colour science.**
      GeoColor paints the whole earth because there the satellite *is* the base image. Here
      there is an OSM basemap underneath carrying the roads and boundaries a radar view is read
      against, so true-colour land would hide the map. True colour is kept, but alpha falls
      away over clear ground
- [x] Green is synthesised — ABI has no green detector — as the community's hybrid,
      `0.45·red + 0.10·veggie + 0.45·blue`. A plain red/blue average leaves vegetation brown
- [x] **Brightness alone is not a cloud test, and the first version got this wrong.** Rendering
      a real midday CONUS scene showed the desert southwest washed tan across the whole
      basemap: desert reflectance clears any threshold cloud does. Cloud is also spectrally
      *flat* across 0.47, 0.64 and 0.86 µm — which is why it looks white — where desert is
      markedly redder than it is blue. Alpha is brightness × neutrality now, and the wash is
      gone
- [x] Snow survives that and always will. It is genuinely bright and genuinely neutral, and
      three visible bands cannot separate it from cloud. There is a test asserting it shows,
      so the limitation is recorded rather than discovered later

**Gate:** [PASSED] 492/492 tests, 0 warnings. The day composite was verified by rendering a
real midday CONUS scan through the production path and looking at it — the night path cannot
show it, and waiting for daylight was not the way to find the desert problem. Cross-checked
against the Phase 1 raster, whose placement over the basemap was already verified in the app,
to confirm the sector footprint was unchanged. The night path was verified live in the app:
`GOES-East IR 05:17Z (2 min old)`, correctly choosing the 3 MB single-band fetch.

## Phase 3 — TDWR

The 45 terminal radars sit beside major airports: C-band, a 0.55° beam against the WSR-88D's
0.95°, 150 m gates, sited for approach paths rather than regional coverage. Over a metro area
that is a different picture, not a slightly sharper one. The site table has carried them since
the beginning with `IsTdwr` set, and every code path excluded them.

### 3a — reaching them

- [x] Their Level II is not in the NEXRAD bucket, but the Level III digital radial products
      are: `TZ0/TZ1/TZ2` reflectivity and `TV0/TV1/TV2` velocity, three tilts each, in the
      same `unidata-nexrad-level3` bucket under the same key format. Checked that no WSR-88D
      and TDWR share a three-letter suffix, so the existing key scheme cannot collide
- [x] The packet-16 decoder reads them unchanged — a TDWR digital radial is the same container
      as the WSR-88D ones. What is new is the scaling, which comes from the product's own
      threshold halfwords rather than a table keyed on product code: reflectivity reads
      (−320, 5, 254) and velocity (−635, 5, 254), the same half-unit step from very different
      floors
- [x] Levels 0 and 1 are flags, not data. Scaling them anyway would put "below threshold" at
      −33 dBZ in a field whose floor is −32 — a plausible number, which is what makes it worth
      a test rather than an assumption
- [x] `ToSweep` converts to a `Sweep`, so the renderer, palettes, inspector, cross-section and
      colour scale all apply without knowing anything new. That meant pulling the elevation
      angle out of the product description block, which the decoder had been skipping
- [x] Verified against **MetPy 1.7.1** over the whole field, not a few gates: 82,596
      reflectivity gates spanning −22.0 to 57.0 dBZ and 62,311 velocity gates spanning −27.5
      to +38.5 m/s. MetPy also settled the velocity units, which the ±63.5 halfword range
      leaves genuinely ambiguous between m/s and knots, and confirmed that level 1 means range
      folded on velocity and nothing on reflectivity

### 3b/3c — into the app

- [x] `TdwrFeed` assembles a volume from six products fetched together. They are not
      simultaneous — each publishes as its own scan finishes — so the volume takes the newest
      as its time and the sweeps keep their own. One missing tilt does not lose the other five
- [x] `ElevationIndex` is the tilt's position within its moment, not the sweep's position in
      the list, or the display pairs a reflectivity cut with the wrong velocity cut
- [x] The site list and marker layer include TDWRs now — 210 entries against 163
- [x] Selecting one switches to live: there is no Level II archive to scrub and no volume to
      run a forecast against
- [x] Polled at a minute rather than streamed. A WSR-88D publishes Level II in chunks as the
      antenna turns, which is what gives this app sub-scan latency; a TDWR only reaches the
      public as finished products
- [x] "Nothing published" is reported as normal rather than as a failure — these run a
      hazardous-weather strategy and go quiet in clear air

**Two bugs found by looking at the running app rather than at the tests.** Selecting a TDWR
left the previous site's Level II stream running, so its next volume landed on top of what had
just been drawn: the site box said TSLC and the picture was still KMTX with twenty cuts. Both
paths go through `StartLiveAsync` now, which is what stops the stream. And the live handler
drops a volume whose site no longer matches the selection, which closes the same race for a
fetch already in flight.

**Gate:** [PASSED] 513/513 tests, 0 warnings. Verified live against TSLC: `TSLC 05:51:55Z,
6 products`, three tilts, the field correctly georeferenced over the Salt Lake valley at its
90 km range. The product bar offers reflectivity and velocity, greys out the dual-pol moments
TDWR Level III does not carry, and offers **azimuthal shear** — which comes free, being derived
from velocity rather than decoded.

## Documenting how the feeds fit together

- [x] `docs/radar-sources.md` — the map of the whole thing. What each of the eight feeds is,
      how WSR-88D and TDWR differ and when to reach for which, the layer stack bottom to top,
      and which layers are exclusive with which and why
- [x] README picks up TDWR and the satellite day/night work, and three stale facts: the test
      count said 76 against 513, `OpenWSR.NetCdf` was missing from the layout, and the purity
      sentence left it out — though `PurityTests` has been enforcing it all along

**Writing it turned up a bug.** The satellite tile layer was described in two places as
drawing *beneath* the native ABI raster, so that "where both are present the good one wins".
It draws after it, and therefore over it — so for the whole of phases 1 and 2 the IEM tiles
sat on top of the imagery decoded from source at 60 % opacity. It was not obvious because
capping the tiles at z7 had already made them smooth, so the thing covering the good layer
looked much like the good layer. The tiles now draw only while no native raster is loaded,
which is what a fallback is, and both comments say so.

## Saved locations

The last **Expected**-tier gap on the parity list. A single home became a named list, every
entry watched, each with its own alert radius — fifty miles round the house is useful lead
time, fifty round an office you leave in an hour is noise.

- [x] Exactly one place is primary. It decides the startup camera and which radar the storm
      layer follows, and both need a single answer, so `SetPrimary`/`Remove` keep the invariant
      rather than leaving it to the UI
- [x] `ThreatMonitor` judges every threat against every place, so one storm crossing two is two
      entries at two distances. That forced a distinction the old code did not need:
      `SourceKey` identifies the storm or warning, `Key` appends the place, and the
      once-an-hour throttle uses the latter — hearing about a storm at home must not spend the
      alert for the office
- [x] The place is named on a row only when more than one is watched. With a single place it is
      the only answer there is, and a 248 px panel needs that space for the range
- [x] The direct-hit radius is clamped against the **narrowest** alert radius rather than the
      primary's, or a place with a tighter radius could not tell a direct hit from a pass
- [x] The map draws every place, the primary brighter, each ring at its own radius
- [x] Settings edits copies and writes back only on Save — the single home it replaced had that
      bug and it is not being reintroduced. The per-place radius combo is bound rather than
      driven by `SelectionChanged`, since an event only fires when the control is touched and a
      loaded row would show a blank box beside a radius it was using

**The migration is the part that had to be right.** A file older than the list carries a home
in `homeLatDeg` and nowhere else, and dropping it would silently un-arm the alerts of anyone
who already had one — quietly, at the moment they most want them. `MigrateLegacyHome` runs on
every load rather than once behind a version flag, because an older file can turn up at any
time, and nulls the legacy fields so a saved file never carries two spellings of the same fact.

**Gate:** [PASSED] 534/534 tests, 0 warnings. Verified live: the real `settings.json` migrated
in the running app, showing "Home" with its Wi-Fi provenance intact, and a three-place file drew
three rings at three radii over three neighbouring towns. The panel's place labelling is
covered by tests but was not seen live — nothing was threatening any of the three at the time.

## A guide inside the app

Reported: there was nowhere in the app that explained how a feature works — in particular,
what the storm alarm will do once someone sets their area.

- [x] `InfoWindow.ShowGuide` — six sections: start here, how the storm alarm works, what the
      panel is telling you, reading the radar, layers and what fights what, and when. Prose
      rather than a table, because the questions are "what will this do on my behalf, and
      when", not "what does this button do"
- [x] **`?` and F1 open the guide, not the shortcut table.** Someone presses `?` because they
      do not know how the thing works; the keys are a click away from there, along with the
      symbol key. The first run opens it too, where it used to open the keyboard card
- [x] The alerting section leads, because it is the part that decides what interrupts someone
      — and a person who cannot predict that either stops trusting it or stops reading it. It
      names the two radii, the three outcomes a track resolves to, the two things that always
      interrupt regardless, and the once-an-hour-per-place throttle

**Gate:** [PASSED] 534/534 tests, 0 warnings. Verified in the running app: all six headings
present, both cross-links working.

## The window opened bigger than the screen, and the map is dark now

- [x] **The window opened larger than the display.** `Width="1360" Height="860"` are
      device-independent units, so at 150 % scaling that is 2040x1290 real pixels against a
      1920x1200 screen. WPF does not clamp it, so it opened at 1946x1226 offset to (266, 266)
      with about 290 px hanging off the bottom and right — which is also where the bottom of
      the left rail had been disappearing to. Now maximised on launch, `CenterScreen` when
      restored, and the restore size is clamped to the work area at construction so
      un-maximising can never put it off screen again. Verified: client area exactly
      (0, 34)–(1920, 1128), filling the work area with the taskbar clear
- [x] **Dark basemap, and it is the default.** Reflectivity is a bright, saturated palette,
      and on the standard OSM style it competes with green landcover, blue water and orange
      roads for the same part of the eye. CARTO's "Dark Matter" was picked over the
      alternatives by fetching real tiles over the Great Salt Lake and comparing: Stadia and
      MapTiler both want an API key, and Esri's dark canvas is a mid-grey that gives up most
      of the contrast. Attribution names CARTO alongside OpenStreetMap, in the app and in
      `THIRD-PARTY-NOTICES.md`

- [x] **Brighter place names, and above the weather.** Reported: the labels were too dim. Two
      problems, not one. CARTO draws them mid-grey — measured from a real tile, the brightest
      pixel is (161,161,161) and the mean (103,103,103) — and they were baked into the
      basemap, so an echo covered them. The base is `dark_nolabels` now with
      `dark_only_labels` as its own layer drawn *after* the radar, boosted through the quad
      shader's existing tint (values above 1 lift toward white and clamp). The name of the
      town a storm is over is exactly what wants reading at that moment
