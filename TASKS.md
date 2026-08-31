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

## Strokes that look drawn rather than plotted

Requested: thicker, smoother storm tracks and proximity ring, closer to how an iPhone app
draws. Three separate causes, all in the overlay stroke path.

- [x] **Overlay lines had no antialiasing at all.** Every segment was a bare screen-space
      quad and the pixel shader returned flat colour, so every diagonal was a staircase.
      They are drawn as a signed distance field now: each vertex carries where it sits
      relative to its own segment — (along, across) in pixels — and the shader measures the
      true distance to the centreline and feathers the last pixel. Distance to the
      *segment* rather than to the infinite line, so past an endpoint the nearest point is
      the endpoint itself and the contour closes as a semicircle: round caps for free.
      Cheaper than multisampling the target, which would have cost bandwidth on every layer
      underneath to fix the thinnest one
- [x] **A segment rounds its far end only.** Rounding both would stack two half-discs on
      every shared vertex of a chain, and at any alpha below opaque that reads as a string
      of beads — the secondary place ring at alpha 150 would go from 0.59 to 0.83 at each of
      96 joints. Flat-at-A, round-at-B means the round end lands on the shared vertex and
      covers the square start the next segment puts there: proper round joins, each disc
      drawn once. The square end still gets its own antialiasing ramp in the shader rather
      than being cut off by wherever the quad stops. `LineCaps.Both` is opt-in for segments
      that genuinely stand alone — dashes, crosshairs, the measure line
- [x] **Widths were quoted in physical pixels, on a 150 % display.** The swap chain is sized
      in physical pixels, so every stroke was drawing at two thirds of its intended weight —
      most of why they read as hairlines. Widths are device-independent units now, scaled by
      the display's DPI at the one place that already knew it. Verified against the running
      app: the place ring measures 5.2 px for 3.5 DIU at 1.5x, with both edges blended
- [x] **A ring was 28 sides.** Visibly a polygon once it is more than a few hundred pixels
      across, which is what a proximity ring is at any useful zoom. 96 sides: the sagitta
      `r(1 - cos(pi/n))` on a 500 px ring falls from 3.1 px to 0.27 px, under the pixel the
      shader feathers over. 576 vertices, which costs nothing worth measuring
- [x] **Dash length was 2.5 km of Mercator, not a screen measure.** So the forecast track
      was a solid line at national zoom and three long strokes at street zoom — the same
      mistake the symbol sizes had already been corrected for. 10 px on, 7 px off
- [x] **Weights lifted:** place ring 2 → 3.5, storm past track and forecast dashes 1.5 → 2.5,
      mesocyclone ring 2 → 3 and its track 1 → 2, projection cone 1.5 → 2, warning polygons
      2 → 2.5, measure line 2 → 2.5
- [x] **The overlay shader is covered by `ValidateShaders` now**, like the volume and sweep
      ones. It compiles on the render thread, where a syntax error is a black window and an
      exception nobody sees; the new one is far too involved to leave to that

**Gate:** [PASSED] 539/539 tests, 0 warnings. Verified in the running app against live
storms over Salt Lake City: the ring is round and evenly weighted with no facets and no
beading, track bends join cleanly, cone edges and the arc are smooth.

## Clutter: the other half of the rotation-track mask

The one quality gap `CLAUDE.md` still listed as open. Reflectivity removes *noise* — gates
where nothing reflected — but clutter reflects strongly and passes that test intact, showing
as radial spikes at coastal sites. Correlation coefficient is the only field that identifies
it, and is also the most dangerous thing to threshold, because a debris signature is itself
a low-CC target.

- [x] **Measured how dangerous, and it is worse than the old note said.** That note claimed a
      CC threshold would delete "a third of the signature". Masking the Moore volume on
      `CC < 0.85` alone deletes **all 89** debris gates and then reports a confident peak on an
      unrelated feature 20 km east — 0.1000 1/s at 35.276,−97.282 in place of 0.1297 at
      35.323,−97.527. Every summary statistic improves while the product answers a different
      question, which is the failure mode worth naming
- [x] **`GateQuality.MaskClutter` gates CC on the echo.** Low CC only condemns a gate whose
      reflectivity is also below 40 dBZ. Debris is the low-CC target that is *strong* — that is
      what makes a TDS detectable — while clutter and biologicals are low-CC and weak. Keeps
      all 89 debris gates on Moore with the peak bit-identical; takes KBOX from 23 strong-shear
      gates peaking at 0.0995 1/s of pure sea clutter down to 3 gates at 0.0226
- [x] **40 dBZ is the knee, not a tuned number.** The benefit has saturated by 40 (63/87/67/61 %
      of speckle removed across four coastal volumes, identical at 45, 50 and no ceiling) while
      the cost is still exactly zero; at 45 debris starts dying for nothing in return
- [x] **Two plausible discriminators measured and rejected.** *Radial velocity* does not
      separate them at all — clutter's median |v| is 6.0 m/s against debris' 10.5 — because the
      RDA's own clutter filter has already notched out the genuinely stationary returns, so what
      survives is not at zero Doppler. *Spectrum width* separates them physically (debris tumbles,
      median 5.5 m/s against 1.0) but is a weak instrument: `sw < 2` buys 6–24 % of the speckle
      and costs 4 debris gates, against 61–87 % for nothing. Correct physics, poor discrimination
- [x] **A second committed volume, because one cannot show both halves.** Moore can show a mask
      spares the debris signature but contains no clutter to remove, so it cannot show the mask
      does anything at all. `assets/testdata/clutter/KBOX20260720_090022_V06` is a clear July
      night over New England — 96 % of what survived the echo mask had CC below 0.85, median 0.43
- [x] **Applied at both seams**, so the live product and the rotation-track swath show the same
      field: `RadarDisplayController.Materialise` and `RotationTracksController`
- [x] **`CorrelationFor` matches by angle, not index, with a half-degree guard.** On VCP 12 the
      split cut puts velocity at 0.53° and CC at 0.60° under different elevation indices;
      matching by index would pair the wrong altitude. A volume with no low-level dual-pol
      returns null and goes unmasked, which is the safe way to be wrong

**Gate:** [PASSED] 546/546 tests, 0 warnings. `ClutterMaskTests` pins both ends — the Moore
peak bit-identical with all 89 debris gates kept, KBOX down to 3 gates and 0.0226 — and keeps
the bare-CC dead end ruled out as an executable test rather than a comment.

## Boundaries as a layer of their own

First of the two Expected-tier Partials in `docs/parity.md`. The basemap already draws
boundaries, but baked into the tile: they cannot be turned off when they clutter a velocity
field, cannot be brightened when an echo covers them, and vanish if the provider is switched
to one that omits them. A county line is how a warning is described — "northern Cleveland
County until 7:15" — so it has to be something the app controls rather than inherits.

- [x] **`Shapefile` reader in `OpenWSR.Geo`.** Every public boundary dataset ships as one.
      Geometry in the .shp, attributes in a .dbf beside it, matched by position with no key
      linking them — that is the whole relational model. Byte order is mixed *within the same
      header*: file code and record headers big-endian, everything else little-endian, which
      read backwards produces plausible garbage rather than an error
- [x] **Verified against pyshp 3.1.6** on the committed 1:20,000,000 state file — record
      count, total points, per-record part and point counts, and the coordinate order. That
      last one earns its own test: x is longitude, and swapping them does not throw, it just
      silently puts the United States in Somalia. Alaska is the awkward case at 47 parts, and
      flattening those into one ring would draw a line from each island to the next
- [x] **FIPS codes stay strings.** `GEOID` "06" read as a number becomes 6, and a code that
      has lost its leading zero no longer joins to anything. Everything from the .dbf comes
      back as a trimmed string; it is stored as ASCII anyway
- [x] **`Polyline.Simplify` — Ramer-Douglas-Peucker.** The 1:500,000 county file is 1.03
      million points, which at national zoom is about two hundred points per pixel and over
      six million vertices a frame. Distance is measured perpendicular to the chord, not
      between consecutive points: a long shallow curve has no point far from its neighbour
      even though the run of them bends a long way, and thinning by neighbour distance
      flattens a coastline
- [x] **`BoundariesController` projects once and thins per view.** Projection and bounds do
      not depend on the camera, so they are computed at load; each rebuild culls to the
      viewport and simplifies the survivors to 1.2 px. Rebuilds are triggered by a zoom or a
      quarter-viewport pan, and a build the camera has already overtaken is discarded
- [x] **`BoundaryClient` fetches once and never again.** Boundaries do not change, so unlike
      every other client here it does not poll. Cached under `%LOCALAPPDATA%\OpenWSR\boundaries`
      rather than in the LRU volume cache, because these must not be evicted: someone who
      turned the layer on and then went offline should still have it. 22 MB for both sets
- [x] **Off by default.** The first tick downloads, and a startup that reaches for the network
      before the user has asked for anything is the wrong default

**Gate:** [PASSED] 569/569 tests, 0 warnings. Verified in the running app: both sets drawn
over Utah, `Boundaries "Counties": 3235 shapes, 1033837 points` in the log, restored from
settings across a restart, 60 fps with both on.

## Importing your own shapes

Falls almost entirely out of the boundaries work: once a reader turns bytes into
`ShapeFeature`s, nothing about a county outline is special.

- [x] **`GeoJson` reader in `OpenWSR.Geo`**, producing the same `ShapeFeature` a shapefile
      does, so one drawing path serves both. Handles FeatureCollection, Feature, bare
      geometry and GeometryCollection; Point, MultiPoint, LineString, MultiLineString,
      Polygon and MultiPolygon. Polygon holes are kept as parts of their own, because the
      inner boundary of a doughnut is as real a line as the outer one when drawing outlines
- [x] **Coordinates are [longitude, latitude]** — RFC 7946 §3.1.1, the opposite of how a
      position is spoken and of the (lat, lon) used everywhere else here. Reading it backwards
      throws nothing, so it has its own test
- [x] **A byte-order mark is stripped.** `System.Text.Json` rejects one outright rather than
      skipping it, and SPC serves its GeoJSON with a UTF-8 BOM — a reader without this fails
      on a real, current, official source
- [x] **`ShapeLayer` extracted** from the boundaries controller, so project-once/cull-and-thin
      lives in one place and both layers use it. Points draw as pixel-sized crosses rather
      than as ground extents
- [x] **`.geojson`, `.json`, `.shp` and `.zip`.** An archive is what a public data portal
      actually hands you. The `.dbf` inside is matched by base name, not by "the first .dbf":
      that works on a Census archive, which holds one of each, and silently mismatches on a
      bundle of several layers. A missing `.dbf` costs attributes, not geometry, so it loads
- [x] **Imports persist across a restart**, like placefiles. A path that has since moved is
      dropped from the list rather than reported — it is a layer someone added once, not a
      document they asked to open
- [x] **Colours cycle through a fixed palette**, deliberately avoiding red, amber and green:
      those already mean tornado, severe and flood on this map, and an imported county list
      must not read as a warning

**Gate:** [PASSED] 590/590 tests, 0 warnings. Verified end to end in the running app: a
GeoJSON of a polygon, a line and a point restored from settings on startup and listed as
"3 features, 8 points", at 60 fps.

## Hail as a field, not a scatter of markers

The second Expected-tier Partial. Hail was per-cell markers sized by severe-hail
probability — points from the SCIT algorithm, which say *a cell probably has hail* and not
*this town was hit and by what size*.

- [x] **Contour the right data.** The obvious move — interpolating between the dozen or so
      SCIT cell points — would have invented a field between them. MRMS already publishes
      MESH, maximum estimated size of hail, on the 7000×3500 CONUS grid, and the project
      already decodes MRMS GRIB2. That is a measured field rather than a drawn-in one
- [x] **The hourly maximum, not the instantaneous field.** Hail is a thing that *happened* to
      a place: the instantaneous MESH shows only where a core is right now, and it is speckled
      where the hourly one is a coherent swath
- [x] **Units established from the data, because nothing states them.** MRMS publishes MESH
      under local discipline 209, so ecCodes reports `units: unknown`. The peak of a CONUS
      hour is 67.6, which is 2.66 in as millimetres — a baseball, and entirely ordinary. As
      inches it would be a five-foot hailstone. Getting this wrong is a factor of 25 and the
      file will not tell you
- [x] **Two flag values, and no zeros at all.** Every cell is a size or a flag: −1 for "no
      hail" inside radar coverage (16,122,011 of them) and −3 for "no coverage" outside it
      (8,337,611). Drawing either paints the country
- [x] **Bands are the sizes hail is reported in** — 19 mm is the 0.75 in severe threshold,
      25 mm a quarter, 45 mm a golf ball, 70 mm a baseball — not an even ramp, because those
      are the units a warning is written in. Below half an inch it fades out rather than
      turning every thunderstorm into a swath
- [x] **`MrmsLayer` generalises the controller** rather than copying it. The composite and the
      hail field differ only in product path, colour table, threshold and slot; fetching,
      decoding 24.5 million points, quantising and re-cutting the raster are identical
- [x] **It draws in the `Analysis` slot, over the sweep.** Hail is read against the echo that
      produced it, and the `Field` slot is already contested by the two mosaics and the
      forecast raster — a third claimant there would have made it worse

**Gate:** [PASSED] 599/599 tests, 0 warnings. Counts at every named hail size are golden
against ecCodes 2.47.0 on a committed MESH hour. Verified live: the layer fetched
`MRMS_MESH_Max_60min_00.50 ... 00:16Z`, 134 KB, no errors, 60 fps.

## A terrain basemap, and the silent failure that hid behind it

- [x] **`TileProvider` carries its own label layer.** `MapView` used to hardcode
      `if (provider.Name == "carto-dark")`, so no other style could ever have separate place
      names. Whether labels are published apart from the ground is a property of the style,
      and so is how hard they need lifting, so both moved onto the provider
- [x] **USGS topographic basemap**, public domain and keyless, selectable in Settings and
      appended as the last entry so no saved setting shifts meaning
- [x] **Not shaded relief, which would have suited the radar better.** Grey relief leaves the
      reflectivity palette the only saturated thing on screen — the same argument that makes
      the default dark. But `USGSShadedReliefOnly`'s tile cache has holes: over northern Utah
      it serves z8 and z12 and returns 404 at z9 and z10, while advertising levels 0 to 23 in
      its own metadata. A basemap that vanishes at two zooms in the middle of the range is not
      a basemap. `USGSTopo` was complete at every level checked
- [x] **ArcGIS tile paths are `{z}/{y}/{x}`** — row before column, where XYZ is column before
      row. Proven rather than assumed: in the right order a Rockies tile is 20 kB of texture
      and a Pacific one 2.4 kB of nothing; in the wrong order the Rockies tile comes back
      byte-identical to blank ocean. The swap returns tiles, just the wrong ones
- [x] **`TileFetcher` no longer swallows failures silently.** It caught every exception into a
      retry map, so a basemap where *every* tile 404s was indistinguishable on screen from one
      that was merely slow — the map is blank either way. This is what hid the shaded-relief
      problem for three rounds of guessing. It keeps the first failure now, and the status tick
      reports it once as an error: `Basemap tiles are not loading — usgs-relief 9/97/191:
      HttpRequestException: ... 404`. That one line found it immediately

**Gate:** [PASSED] 599/599 tests, 0 warnings. Verified in the running app: 24 distinct tiles
cached, attribution reads "USGS The National Map", no tile failures, 60 fps.

## A hodograph, from the profile already being fitted

The wind profile panel listed barbs. A barb list says what the wind does at each height; the
hodograph says what the *profile* does, which is the question that matters for rotation —
length is shear, curvature is the streamwise vorticity a storm tilts into a mesocyclone.

- [x] **`Hodograph` in `OpenWSR.Nexrad.Analysis`**, working from the VAD profile the app
      already fits from Level II velocity. No new data source, and nothing thermodynamic:
      storm-relative helicity needs heights and winds only
- [x] **Storm-relative helicity golden against MetPy 1.7.1**, on a regular grid and on an
      irregular profile whose layer tops fall *between* levels — MetPy interpolates there and
      snapping to the level below would drop a third of the layer. Depth is measured from the
      lowest fitted level, matching MetPy's `with_agl=True`: a VAD profile starts wherever the
      lowest cut found echo, so measuring from sea level would make "0–1 km" mean something
      different every scan
- [x] **Positive and negative helicity reported apart**, not just summed. A hodograph that
      doubles back contains rotation of both signs and the total alone calls that benign
- [x] **Helicity is measured against the *observed* storm motion** when a cell is tracked —
      the advantage of doing this in a radar application rather than from a sounding, which
      has to estimate where a storm would go. The 0–6 km mean wind is the fallback, and the
      readout says which was used, because the number means different things
- [x] **Bunkers deliberately not implemented.** Its mean wind is pressure-weighted and a radar
      profile has no pressure. Synthesising a standard atmosphere would produce a number that
      agrees with MetPy only because both were handed the same invention — and we already have
      something better in the measured cell motion
- [x] **Caught by running it: the readout was quoting layers the profile did not span.** On a
      live volume fitted to 2.7 km it printed "0–6 shear" and "SRH 0–3", because
      `Hodograph.Layer` returns what it has when asked for more. It now checks
      `Hodograph.DepthM` and omits any layer it cannot honestly fill, saying how deep the
      profile actually is instead

**Gate:** [PASSED] 613/613 tests, 0 warnings. Verified live: on a 0.6 km profile it draws the
trace and suppresses both derived layers rather than inventing them.

## The layers panel needed twice the height it had

Reported: too much scrolling on the right. Measured with UI Automation before touching
anything — **1758 px of content in a 914 px column, 52 % visible**. This session had made it
worse: BOUNDARIES, IMPORTED SHAPES and the hail swath all landed there.

- [x] **Inert controls are collapsed, not shown.** Four opacity sliders and three status notes
      were on screen while their layers were switched off, and the eleven storm filters were on
      screen while storm tracking was off — that alone was a third of the panel. Each now
      follows its own checkbox through a `BooleanToVisibilityConverter`, which is the same rule
      the left rail already lives by
- [x] **Fine-grained settings sit behind a disclosure.** Which storm symbols to draw is chosen
      once and then left, so the six filters and the hail-probability slider moved into a
      nested "Symbols shown" section. STORMS went 443 px → 261
- [x] **Set-once sections start closed** — RADAR, Warnings, SPC & REPORTS. Together 626 px → 111
- [x] **The panel remembers how you leave it.** `AppSettings.PanelSections` stores each
      section's state by header and restores it on launch. This is what makes tighter defaults
      safe: which sections a person needs open is not something a default can know — a chaser
      lives in STORMS and never opens SPC, someone watching one town is the reverse — so the
      shipped defaults only have to be a reasonable start. Hooked by walking the visual tree
      for `Expander`s rather than wiring nine handlers, so a section added later persists
      without anyone remembering to make it so

| | content | visible | scrollbar |
|---|---|---|---|
| before | 1758 px | 52 % | yes |
| inert controls hidden | ~1350 px | 68 % | yes |
| + symbols disclosure | ~1055 px | 87 % | yes |
| + set-once closed | **~855 px** | **100 %** | **none** |

**Gate:** [PASSED] 613/613 tests, 0 warnings. Verified in the running app at each step by
`ScrollPattern.VerticalViewSize`; persistence checked by expanding RADAR, confirming
`{"RADAR":true}` in settings, and finding it still open after a restart.

## The clear-air haze was drowning the weather

Reported: the display had got busier — blue strips everywhere with the storms in between.
Measured on a live Salt Lake volume: **26.7 % of the map was faint blue against 12 % of
actual storm**, so the context outweighed the subject two to one.

Three causes, only one of them a deliberate visual decision.

- [x] **State and county lines had been left switched on** in `settings.json` from testing the
      boundaries layer. 4,791 outlines over the map. Off again; the shipped default was
      always off
- [x] **The palette's low end reached full opacity at 10 dBZ.** Clear-air return — insects,
      birds, residual clutter — painted bright cyan at full strength. Nothing about that
      changed this month; what changed was the ground beneath it. On the old light basemap
      pale cyan on white recedes, and against the near-black default it is the second
      brightest thing on screen. The low end now fades in: alpha 0x28 at 5 dBZ, 0x68 at 10,
      0xC0 at 18, full from 22
- [x] **Weather is byte-for-byte unchanged.** Every band from the green stop upward has both
      endpoints untouched, and `ReflectivityPaletteTests` builds the previous table and
      asserts the two 256-entry ramps agree exactly from 22 dBZ up — not "look similar", agree

| dBZ | on-screen luminance |
|---|---|
| 5 | 48 % of before |
| 10 | 49 % |
| 15 | 65 % |
| 18 | 80 % |
| 22 and above | **100 %** |

Overlay strokes were the third cause and are left alone by request: they are 2–2.6× their
old weight because a nominal increase and the new DPI scaling compounded in one commit.

**Gate:** [PASSED] 619/619 tests, 0 warnings.

## A dBZ window, so hiding clear air is the user's call

Asked for: a min/max dBZ filter, so the blue strips can be hidden by anyone who wants them
hidden rather than by a decision baked into the palette.

- [x] **Two sliders in the RADAR section**, which already owns how the radar layer is drawn.
      Default is the full −30…75 range, so nothing ships filtered — a product that silently
      omits data it was given is worse than a busy one
- [x] **Applied as a draw-time discard**, not by editing the data or the palette. Two floats
      in the sweep shader's constant buffer, so dragging the control redraws the sweep already
      on screen: no decode, no restage, and archive playback keeps its prebuilt geometry.
      Zeroing the palette's alpha instead would have needed no shader change but would have
      forced a restage on every drag
- [x] **Reflectivity only.** The window is in dBZ, and a dBZ bound against velocity or
      correlation coefficient would blank most of the product. Other moments get the full
      range, and the panel says "Reflectivity only — not applied to this product" so a
      control that appears dead is explained rather than merely dead
- [x] **The ends cannot cross.** Dragging one past the other pushes the other along instead of
      asking for an empty window
- [x] **Persisted**, and it costs nothing in the panel: RADAR is collapsed by default, so the
      column still fits without a scrollbar

**Gate:** [PASSED] 619/619 tests, 0 warnings — including the sweep shader's compile guard,
which is what a constant-buffer change most needs. Verified live: a clean settings file opens
at "Showing everything"; 20 gives "Showing 20 to 75 dBZ"; pushing the maximum below the
minimum drags it along; switching to velocity stands the filter down.

## Code review: nine findings, all fixed

A review of the nine commits on this branch. Two were shipping blockers, both in the dBZ
window added an hour earlier, and both in the one path I had not exercised — restoring a
*saved* value. I had tested the default and I had tested dragging the control at runtime.

- [x] **Startup crash for anyone with a saved window.** `DbzMinSlider.Value = settings.…`
      raises `ValueChanged` synchronously, and the handler reached `_radar`, which the
      constructor does not assign until twelve lines later. Reproduced from the log:
      `NullReferenceException at ApplyDbzFilter, MainWindow.xaml.cs:1412`, from the
      constructor at line 90. The window never opened. Fixed by guarding `_radar` and by
      calling `ApplyDbzFilter` once after the controller exists
- [x] **Restoring the minimum destroyed the saved maximum.** The same synchronous event wrote
      *both* ends back to disk while the maximum still held its XAML default, so a saved
      50 dBZ became 75 on every launch. Demonstrated: a seeded max of 50 came back as 75.
      Fixed with the `_suppressSliderEvents` flag this class already uses, and the restored
      pair is clamped so a corrupt file cannot produce an inverted window
- [x] **`Analysis` had two unmanaged claimants.** The hail swath and the rotation-track swath
      both wrote it wholesale, so switching hail off cleared the slot and took the tracks with
      it until they were rebuilt from scratch. They are different quantities, not alternatives,
      so the answer is a fourth slot rather than the mutual exclusion `Field` uses
- [x] **The hail swath could become unreachable while still running.** Its checkbox was inside
      the panel gated on `StormsToggle` — today's decluttering work put it there — while
      `StormsToggle_Unchecked` only stops storm tracking. Unticking "Track storms" hid the
      control and left the layer painting and polling MRMS with no way to stop it. It is a
      national MRMS field with no per-site dependency, so it moved to LAYERS
- [x] **One failed tile raised a permanent error bar.** `BasemapFailure` fired at the first
      exception, and a 404, a rate limit or one dropped packet is ordinary — ArcGIS caches
      404 for tiles they do not hold, so the new terrain basemap would have tripped it on a
      normal session. Now twelve failures, which is more than a screen's worth and not luck
- [x] **A tombstoned `.dbf` row shifted every later attribute.** The row was skipped, but the
      `.shp` has no matching deletion and the two are paired by position alone — one deleted
      record silently drew Texas with California's name and FIPS. Deleted rows keep their
      place now, with a regression test
- [x] **Vertical pan could outrun the built margin.** The cull box is a fraction of each
      viewport axis but the rebuild threshold divided both by the *width*, so on a pane wider
      than tall a downward pan left an unbuilt strip along the bottom indefinitely. Measured
      per axis now, in both the boundaries and the import controllers
- [x] **A failed boundary download left the checkbox ticked** over a layer that was off, so
      retrying meant unticking and reticking. The controller raises `LoadFailed` and the panel
      puts the tick back where the layer actually is
- [x] **A duplicated `<summary>` tag** orphaned the boundaries documentation onto the dBZ
      property and left `ShowStateLines` undocumented — from a scripted insert of mine. Doc
      generation is off, so nothing would have caught it

**Gate:** [PASSED] 621/621 tests, 0 warnings. Both blockers reproduced before fixing and
re-verified after: the app now starts with a saved 20–50 window, restores it, draws it, and
leaves it intact on disk. The hail control stays in the tree with storm tracking off.

## Sweep after the review: three items, and a memory question answered

With the review's nine findings closed, a sweep for what was actually left. No TODO or FIXME
anywhere, and the bug class the review found — a XAML handler firing during
`InitializeComponent` and reaching a field assigned later — was checked across every handler
that a XAML default can fire. `ApplyStormFilters`, `WarningFilter_Changed`,
`SiteMarkers_Changed` and the sliders all guard correctly; the dBZ one was the only gap and
it is fixed.

- [x] **`CLAUDE.md` claimed 546 tests**, against 621. It had already gone stale once this
      session, from 229
- [x] **`OverlaySlot` declared `Swath = 3` above `Satellite = 2`** — my own edit an hour
      earlier. The enum documents draw order, so reading out of value order is exactly the
      wrong place for it
- [x] **The 1–1.8 GB working set is normal, and now measured rather than wondered about.**
      Twenty minutes of live streaming with no interaction: it sawtooths between about 700 MB
      and 1.45 GB, with two complete gen2 cycles returning it to a stable floor — 685 MB at
      60 s, 748 MB at 440 s, 754 MB at 480 s. Not a leak. Handles and threads stay flat,
      `LiveFeed`'s assemblers are capped at two and pruned, and a Level II volume is about
      210 MB as floats once every moment is decoded, so the peaks are one volume's decode
      awaiting collection
- [x] **Tile textures are not part of that number**, which is worth writing down before
      someone blames them: they are `ResourceUsage.Immutable` with no CPU access and live in
      VRAM. The cache comment did need correcting though — its "~512 MB" described the whole
      system when there was one cache, and there are four now, so the ceiling across them is
      nearer 1 GB

**Gate:** [PASSED] 621/621 tests, 0 warnings.

## The dBZ window opens where rain starts

Requested: default the low end to 20 dBZ so light rain is what you see on opening, with the
slider still free to go lower.

- [x] **20 dBZ, not −30.** Marshall–Palmer puts 20 dBZ at 0.026 in/hr — light rain reaching
      the ground — against 0.006 for 10 dBZ, which is insects, birds and dust. Four times the
      rate across ten dBZ, which is why a floor between them separates weather from clutter
      at all
- [x] **It agrees with `GateQuality.DefaultMinReflectivityDbz`**, which arrived at 20 from a
      different question entirely — where velocity stops meaning anything. `DbzWindowTests`
      asserts the two match, so if one moves the other is revisited
- [x] **This reverses a principle written into the setting's own doc comment**, and the comment
      now says so rather than being quietly deleted. "A product that silently omits data it was
      given is worse than a busy one" is still right; a floor is only an omission if what it
      removes is weather
- [x] **The cost is snow**, which returns far less energy per unit water — 15 dBZ of it can be
      accumulating steadily and this default hides it. So the panel says "Lower it for snow"
      whenever the floor is above 15, at the control rather than in a document nobody has open

**Gate:** [PASSED] 625/625 tests, 0 warnings. Verified in the running app: opens at 20, slider
still reaches −30, note reads "Showing 20 to 75 dBZ. Lower it for snow."

## Second review: nine findings, all fixed

No blockers this time. The two that mattered were both consequences of yesterday's work
landing on code that had been fine when the default hid nothing.

- [x] **A crafted or corrupt shapefile record asked for a 2 GB array.** `pointCount * 16` is
      int arithmetic, so a record declaring `0x08000000` points overflows to −2,147,483,648;
      a bounds check done in `int` sees a negative length and waves it through. `partCount * 4`
      overflows the same way, and `new int[partCount]` was reached before any bound on it. The
      file is user-chosen — anything openable through the import panel gets here. Both
      comparisons are done in `long` now, with four crafted-record tests
- [x] **MultiPoint had no bounds check its siblings had**, so a truncated record threw where
      Point and Polygon degrade to an empty feature — one bad record cost every good one in
      the file
- [x] **The dBZ window only reached the primary pane.** Every pane builds its own `MapView`,
      and this was invisible while the window defaulted to everything — it became visible the
      moment it defaulted to 20 dBZ, as two panes on the same site and product drawing
      demonstrably different data. `PaneManager.ApplyValueFilter` applies it to all of them,
      per pane, because the window is reflectivity-only and panes open on different products
- [x] **`BasemapFailure` fired on a working basemap.** The count was monotonic and a failed
      tile is retried after a 30-second cool-down, so one permanently-missing tile — exactly
      what `UsgsTopo`'s own comment says ArcGIS caches do — crossed the twelve-failure
      threshold in six minutes on its own. It counts *distinct* tiles currently failing now,
      which is what "more than a screen's worth" meant
- [x] **The hodograph's storm-motion label named a depth the profile lacked.** The SRH and
      shear captions already refused to; the fallback motion was unconditionally
      "vs 0–6 km mean wind" even on a 4.5 km profile. Same rule now: name the depth used
- [x] **The dBZ sliders wrote the settings file on every tick** — snapped every 5 dBZ, so one
      drag across the range was twenty-odd whole-document writes on the UI thread, which is
      what CLAUDE.md's rule about UI-thread I/O exists to prevent. The window still applies
      live; only writing it down waits a second for the drag to settle, with a flush on close
- [x] **Importing the same file twice** drew two layers in different colours while persisting
      only one, so removing "it" left a copy that would not come back. Rejected with a message,
      as the placefile panel already does
- [x] **`MrmsController`'s comment still named the Analysis slot** the review before last moved
      it out of
- [x] **Reflectivity was resampled twice per derived shear** — an azimuth index and a full
      float array over every gate, built and discarded, on every uncached cut and every scan
      of a rotation-track loop. `GateQuality.Mask` does both tests in one pass, and a test
      asserts it is gate-for-gate identical to the two steps in sequence

**Gate:** [PASSED] 632/632 tests, 0 warnings. Verified in the running app: opens at 20 dBZ,
two panes render consistently, no tile-failure bar, 60 fps.

## The first run finds you

Requested: check location services when the app opens and save a user profile. Until now the
only way to get a place was a button in Settings, so a new user opened on the national view
with a hint to go and find one.

- [x] **`TryLocateOnFirstRunAsync`**, run when no place is saved. On success it writes a
      primary `SavedLocation` named Home with the fix's source and accuracy, arms the threat
      monitor and storm watch, draws the ring, and opens live on the nearest radar. That is
      the whole setup step: with a place the app watches for storms heading at you, and
      without one it is a browser for other people's weather
- [x] **Not awaited.** Windows can sit on a cold radio for twelve seconds, and the national
      view is a fine thing to look at meanwhile — holding the first paint hostage to an OS
      call would make the app feel broken to anyone whose location is switched off
- [x] **Asked once.** `AppSettings.LocationAsked` is written *before* the attempt, so a call
      that throws or an app closed mid-way still counts. It matters because an unpackaged app
      gets no consent dialog when either privacy switch is off — the call simply returns
      Denied — so retrying every launch would put the same bar in front of someone who has
      already decided. Settings still has "Add my location" for anyone who turns it on later
- [x] **Never overwrites an existing place**, including one added by hand while the call was
      still outstanding
- [x] **`ReportNotice` added**, because testing showed the confirmation being overwritten by
      live-feed chatter within seconds. `Report` is a running commentary; the one thing the
      app did without being asked needs the error bar's persistence and none of its alarm
- [x] **The guide was corrected.** Its first instruction was to open Settings and add a place
      — which the app now does itself, so it was telling new users to redo the setup that had
      just happened

**Gate:** [PASSED] 634/634 tests, 0 warnings. Verified on a simulated first run: a Home was
saved from a Wi-Fi fix at 165 m, the app went live on KMTX, and the bar read "Found you near
KMTX and saved it as Home — watching for storms within 9.3 mi."

## The panel remembers the layers, not just the furniture

Asked: does the profile save the last settings? Audited — **5 of 34** panel controls were
restored. Places, radii, units, provider, the dBZ window and which *sections* were open all
survived a restart; the toggles inside those sections did not. Remembering the furniture and
forgetting the contents.

- [x] **`LayerToggles` and `LayerSliders`**, keyed by control name, hooked by walking the panel
      rather than by wiring thirty handlers — the same approach `PanelSections` already used,
      so a layer added later is remembered without anyone remembering to make it so
- [x] **Walked over the logical tree, not the visual one.** A collapsed `Expander` has not
      realised its content, so a visual walk finds nothing inside Warnings, SPC or "Symbols
      shown" while they are shut — which is most of the time, since all three ship collapsed.
      Verified by toggling Flash flood, restarting, and finding it still off. The same bug was
      latent in `RestorePanelSections`, which could not see a nested section, and is fixed too
- [x] **Handlers are deliberately not suppressed on restore.** Ticking Satellite is what starts
      its fetch, so the handler running *is* the state being applied. The save debounce is what
      stops a restore writing the file thirty times
- [x] **Only changed controls are recorded.** Anything never touched stays out of the file and
      keeps its XAML default, so a shipped default can still be revised later rather than being
      pinned by a settings file that merely agreed with the old one
- [x] **Site, product and tilt stay unsaved**, by request and on merit: those are where you were
      looking rather than how you like the app set up, and reopening on velocity at tilt four
      over last week's site is worse than opening on reflectivity at home
- [x] **The dBZ debounce generalised** into `MarkSettingsDirty`, now that more than one control
      can dirty the settings quickly

**Gate:** [PASSED] 637/637 tests, 0 warnings. Verified across a restart: the hail swath came
back on, Flash flood came back off from inside a collapsed section, and Tornado — never
touched — was neither written nor changed.

## CI, and a public repository

Sanitised for publication and wired for CI on GitHub. The move to GitHub changes the answer
I gave earlier about a Linux runner: **public repositories get free Windows runners**, so all
637 tests can run rather than the 406 a Linux-only runner allows.

- [x] **Two jobs, guarding different things.** Windows is the real gate and runs everything —
      it is the only platform that can, since the shell is WPF and Direct3D. The shader tests
      work on a hosted runner because they compile HLSL through `d3dcompiler` rather than
      creating a device, so no GPU is needed
- [x] **The Linux job guards a property, not a platform.** The decoders, geodesy and format
      readers are meant to stay free of Windows. `PurityTests` asserts that in-process; only
      a build somewhere without Windows to fall back on actually proves it. `App` and `Render`
      are deliberately absent — they *can* be built there with `EnableWindowsTargeting` but
      never run, so building them would prove nothing
- [x] **`-warnaserror` on both.** Every gate in this file says "0 warnings"; a standard nobody
      checks stops being one
- [x] **Both commands verified locally first**, in Release, which is not the configuration
      anything had been built in — 637 tests, 0 warnings. Running CI to discover whether the
      build works is how a first red build ends up meaning nothing
- [x] **No network needed.** All 56 MB of fixtures are committed, so a red build is a code
      failure rather than a flaky download

Sanitisation re-verified after the two commits that landed since the history rewrite: no
session URLs, hostname, home coordinates or absolute personal paths, in the tree or in any
commit reachable from it.

**Gate:** [PASSED] 637/637 tests in Release with `-warnaserror`, 0 warnings. The published
single-file artifact was also built and run: 261 MB, live data, 60 fps.

## 100 % display scaling, and the bugs that looking for it found

The app had only ever run at 150 %, which is where its DPI bugs were found and fixed, so the
easier case was the untested one.

- [x] **Verified at 100 %.** `DipScale` computes 1.0, the map surface is 1622×957, the layers
      panel fits with no scrollbar at all, every one of the twelve left-rail buttons is
      on-screen including the bottom-docked group that overflow eats first, 59 fps, and not a
      warning in the log. Nothing broke. In hindsight 100 % was always the safer case: a DIU
      is a pixel there, so the panel gets 1009 units of height against 609 at 150 %, and the
      rail budget CLAUDE.md calls "already spent" is only tight at the higher scale
- [x] **`DipScale` was read once and never again.** Fixed while looking: `D3DHostControl`
      overrides `OnDpiChanged`, so a window dragged between a laptop panel and an external
      monitor at another scale no longer keeps the stroke weights of the display it was born
      on. The swap chain never needed help — it is sized from `WM_SIZE`, which reports real
      pixels whatever the scaling
- [x] **Scaling is logged at startup.** "It looks thin on his machine" is close to unactionable
      without it
- [x] **`new Geolocator()` sat outside the try block.** On a machine with no location provider
      it throws rather than returning a status, so it escaped as something other than
      `LocationUnavailableException` — which the first-run caller filters on. Since that
      caller is fire-and-forget and records `LocationAsked` *before* the attempt, the failure
      would have been silent, permanent and unexplained. Guarded, and the caller now also
      catches broadly, because nothing awaits it
- [x] **A notice could bury an error.** `ReportNotice` and `ReportError` share one bar, so the
      first-run "found you" message could paper over a failure the user still had to act on.
      A notice now falls back to the status line rather than displacing an error

**Gate:** [PASSED] 637/637 tests, 0 warnings, verified running at both 100 % and 150 %.

## An MSI, per-user and self-contained

Requested: the release should be an installer rather than a bare executable.

- [x] **Self-contained, because a plain MSI cannot install a prerequisite.** Chaining the
      .NET runtime needs a Burn bundle, which is an `.exe` and no longer an MSI — so bundling
      it is the only way this stays one file that works for someone who is not a developer.
      261 MB of payload compresses to a **78 MB** MSI
- [x] **Per-user, no elevation.** Installs to `%LocalAppData%\Programs\OpenWSR`, which is
      where the application already keeps its settings, cache and logs. It also avoids an
      elevation prompt on an unsigned package, where the warning is the yellow
      "unknown publisher" one rather than the blue one
- [x] **Uninstall leaves your data alone.** The install folder and shortcut go; the settings,
      saved places and cached tiles under `%LocalAppData%\OpenWSR` stay. Removing an
      application should not throw away the places someone asked it to watch. Verified by
      installing, uninstalling and checking `settings.json` survived
- [x] **`-arch x64`, found by checking rather than assuming.** WiX defaults to x86, and the
      first build registered a 64-bit-only application in the `WOW6432Node` view of the
      registry. It still worked, which is exactly why it was worth looking for
- [x] **A plain shortcut, not an advertised one.** Advertised shortcuts resolve through the
      installer, so their target reads as an icon in the MSI cache and launching one can
      raise "please wait while Windows configures OpenWSR" — a poor thing to meet on a first
      run
- [x] **An icon and product metadata**, neither of which existed. The shortcut and Add/Remove
      entry were going to be generic otherwise
- [x] **`release.yml` builds it on a version tag**, taking the version from the tag so a
      release cannot claim a version its binary disagrees with, running the full test suite
      first, and attaching the MSI to a GitHub Release

Verified end to end: install (exit 0, no prompt), correct registration, shortcut points at
the executable, the installed copy runs live at 60 fps, uninstall removes everything it
should and nothing it should not.

**Gate:** [PASSED] 637/637 tests, 0 warnings.

## The dark basemap, after CARTO closed the door

Reported against the 0.1.0 release: a fresh install draws "API KEY REQUIRED" across every
basemap tile. CARTO began requiring an API key in August 2026 and is retiring its raster
basemaps outright.

- [x] **The bug was invisible here for the same reason it existed.** The tile cache never
      expires, so every development machine held tiles fetched before the change and drew the
      map correctly. Nothing in the fetch path could catch it either: the watermark arrives as
      HTTP 200, a valid PNG, and different bytes for every tile, so it passes even the
      two-tile check that caught the IEM "Invalid TMS Request" incident. Re-verified by moving
      the cache directory aside and cold-starting, which is now the documented way to test a
      tile source
- [x] **The dark style is derived rather than fetched.** `TileToning` desaturates and inverts
      OSM's own tiles as they decode. This removes the dependency instead of moving it — the
      light option already fetched those tiles, and a style nobody serves cannot be gated
- [x] **Inverting, not darkening, and the difference was measured.** OSM's ground is
      near-white with its detail above it, so scaling everything down flattens the map toward
      one grey. Inversion puts the ground near-black and turns OSM's near-black label text
      white, which is the arrangement the dark style had
- [x] **Gamma 1.35, set by the ground and the labels rather than by matching a mean.** OSM's
      land lands at 6 and its label text at 189, legible over an echo. Mean luminance is the
      cross-check and deliberately a loose one — CARTO's own per-tile mean ranges 9.1 to 20.5
      over the twelve committed tiles, against 14.70 for the curve, so anything from 1.3 to 1.5
      sits inside its spread. `TileToningTests` holds the CARTO figure as a literal because it
      can no longer be re-fetched, and `TileToningMeasurement` reproduces the whole table
- [x] **Esri's Dark Gray Canvas was measured and rejected**, not assumed. It is the only
      keyless dark raster style left and it is not a substitute: 66.7 against CARTO's 12.2 on
      the same tiles, and its land/water polarity is inverted, so lakes read as holes punched in
      a grey field. Darkening it crushes what little detail it has
- [x] **Existing installs migrate.** A changed default never reaches a settings file that
      already has the setting, so anyone who installed 0.1.0 would have kept the watermark for
      ever. `MigrateRetiredBasemap` moves `carto-dark` to `osm-dark` on every load, alongside
      the legacy-home migration and for the same reason
- [x] **What it costs, stated rather than hidden.** OSM draws minor roads white, which inverts
      to black and takes them out; and it bakes place names into the tile, so they no longer
      draw above the weather. The label-layer machinery is kept for the source that brings
      them back

Verified cold — cache moved aside, live KMTX, 60 fps, no watermark, place names readable.

**Gate:** [PASSED] 652/652 tests, 0 warnings.

## The hotspot finder goes to the nearest storm, not the biggest one

Requested: the 🎯 button should find the heaviest precipitation *nearest to you* rather than
the heaviest in the country.

- [x] **Plain click ranks by distance, Shift-click still ranks by weight.** The national scan
      is how live features get verified — `CLAUDE.md` names it as the fastest way to get real
      weather on screen — so removing it would have taken the test rig out with the feature.
      A modifier costs no rail slot, which matters because the rail budget is spent
- [x] **A floor of 3.5 kg/m², because "nearest return" is not "nearest storm".** DVL reports
      something almost everywhere, so ranking on raw proximity reliably lands on an insect
      swarm or a patch of virga a few miles out. 3.5 is the conventional light/moderate break
      and is a floor on *existence*, not on severity — an ordinary rain shower still qualifies,
      which is what the button was asked for
- [x] **Distance is measured to the cell, not to its radar.** A site 200 km away can hold a
      storm that is nearly overhead, and ranking on the radar picks the wrong one. Asserted
      by a test built from exactly that pair
- [x] **Nothing raining anywhere falls back to the heaviest**, rather than reporting failure.
      On a genuinely quiet day the strongest echo in the country is still the best answer
      available, and a button that does nothing reads as broken rather than as calm
- [x] **No saved place falls back too.** There is no "near me" to search from before the
      first run has found one, so the plain click behaves as the Shift click and says why
- [x] **The scan and the choice are now separate.** `ScanAsync` does the 163 fetches;
      `SelectNearest`/`Heaviest` are pure and carry seven new tests. The ranking decides where
      the camera lands and was previously untestable without live weather

**Gate:** [PASSED] 659/659 tests, 0 warnings.

## A map-centre readout in the WHERE bar

Requested: next to `Nearest`, show the location the map is centred on, so it is obvious
whether you are looking at the right ground.

- [x] **It answers a question the site combo beside it does not.** The combo names the radar
      feeding the screen; after a hotspot jump, a hand pan or a pinned pane the camera can be
      hundreds of miles from the place being watched, and nothing on screen admitted it.
      Verified live: the combo reads `KICX — CEDAR CITY, UT` while the readout reads
      `94.7 mi S of Home`
- [x] **Centred is judged in screen pixels, not kilometres.** At national zoom a place 20 km
      off centre is a pixel from the middle and is centred by any honest reading; at street
      zoom the same 20 km is off the side of the map. A fixed ground tolerance is wrong at one
      end or the other, and choosing which end to be wrong at is not a choice worth making.
      12 px, asserted both ways by a test that holds the offset still and moves the zoom
- [x] **Lit when you are on a place, muted when you are not.** The point is to be readable at
      a glance while looking at the weather, and a distance that happens to read `0.4 mi` is
      not something the eye catches. Styled through `Theme.xaml` rather than hardcoded
- [x] **The nearest saved place, not the primary one.** Being centred on the office answers
      "am I looking at the right ground" as well as being centred on home does, and with one
      place saved the two are the same question
- [x] **Painted off the 500 ms status tick, not a camera event.** Inertial panning settles
      about a second after the mouse is released, so a one-shot notification would report
      where the drag ended rather than where the map came to rest
- [x] **A `Border` gets no automation peer in WPF**, so the composed name first written onto
      the box never reached the automation tree — dead code that looked like accessibility.
      The label sits on the glyph instead, where a peer exists; confirmed by walking the tree
      and reading back `[Map centre] [35.223, -97.440] [on Home]`
- [x] **Seven tests**, including the one that caught a real crash: `MinBy` over a sequence of
      value tuples throws on an empty one rather than returning a default, so a fresh install
      with no place saved would have taken the readout down on the first tick

Verified live at both states — exactly on the saved place, and 94.7 mi off it.

**Gate:** [PASSED] 666/666 tests, 0 warnings.

## A Recenter button beside the readout

Requested: a button to make getting back to your own location easier.

- [x] **It moves the site as well as the camera.** The first cut moved only the camera, on the
      reasoning that changing which radar you are trusting is too big a thing for a button
      labelled "Recenter". Watching it run killed that: the bar read `KICX — CEDAR CITY, UT`
      beside a readout saying `on Home`, which is the map over your house fed by a radar 95
      miles away. The search box two controls to the left already selects the nearest WSR-88D
      when it lands somewhere, so following it is the convention here rather than a surprise
- [x] **The zoom is kept, which is where it parts company with the search box.** That resets
      to 220 m/px; this keeps what you had. You press it to fix *where* you are looking, not
      how closely, and throwing away the scale chosen for the storm being watched would make
      it a worse deal than panning back by hand
- [x] **It targets the primary place, not the nearest one the readout names.** "Put me back
      where I live" has to land in the same spot every time rather than following whichever
      saved place the camera has drifted toward. With one place saved the two are identical
- [x] **Collapsed until a place exists**, the rule the rail already follows: a greyed control
      still has to be read before it can be dismissed

Verified live end to end: from `KAMX — MIAMI, FL` at `2080.0 mi E of Home`, one press returns
both `KMTX — SALT LAKE CITY, UT` and `on Home`.

**Gate:** [PASSED] 666/666 tests, 0 warnings.

## The search box says what it is

Reported: the box at the top of the window does not explain itself.

- [x] **It had a placeholder all along and nothing drew it.** `SearchBox` carried
      `Tag="Search"`, and the theme's placeholder `TextBlock` lives only in the **ComboBox**
      template — the `TextBox` one never had it. So the box rendered as an empty rounded
      rectangle on a dark ground, which reads as decoration rather than as somewhere to type.
      The `Tag` was written for exactly this and had never once been visible
- [x] **The placeholder now sits in the `TextBox` template**, bound to `Tag` and shown on a
      `Text=""` trigger, matching the convention the ComboBox beside it already uses. Nothing
      else in the app sets `Tag` on a TextBox and no code reads `.Tag`, so no other control
      changes
- [x] **It says what the box accepts, not just that it is a box.** "Search a city, ZIP or
      lat,lon" — the three things `Geocoder` actually takes, one of which (lat,lon) is parsed
      locally and is the only way to reach a point with no name
- [x] **Widened 200 → 248 px** so that sentence fits without clipping. The WHERE bar had the
      room; nothing else on it moved

Verified live: the hint is drawn on an empty box, replaced by the text as soon as anything is
typed, and the border goes accent on focus.

**Gate:** [PASSED] 666/666 tests, 0 warnings.

## Labelling the radar controls

Reported: the radar dropdown and the `Nearest` button do not say what they are to anyone
meeting them for the first time.

- [x] **A `Radar station` label before the combo.** It read `KMTX — SALT LAKE CITY, UT`, which
      names a place and says nothing about what kind of thing is being picked — you have to
      already know that a four-letter ICAO in the top bar is a radar station. Sentence case,
      matching every other label in the app
- [x] **`Nearest` → `Nearest to map center`, deliberately not `Nearest radar station`.** Once
      the label two controls back names what is being picked, repeating the noun spends width
      on nothing. The ambiguity actually left in that button was nearest to *what* — to you, to
      the current station, to the map — and that is the half worth the pixels, especially with
      the centre readout sitting immediately beside it showing the very point it means
- [x] **The combo's tooltip was also wrong, not just terse.** It said "The WSR-88D whose data
      is being shown", but the list has held the 47 TDWRs since terminal radars were added, so
      it now names both
- [x] **Explicit `AutomationProperties.Name` on the button**, since the visible label is
      necessarily compressed and a screen reader has room for the whole sentence

Verified live: `[Search a city, ZIP or lat,lon]  Radar station [KMTX — SALT LAKE CITY, UT]
[Nearest to map center]  [◎ 35.223, -97.440 on Home]  [Recenter]`, with room to spare at
1700 px.

**Gate:** [PASSED] 666/666 tests, 0 warnings.

## Smoothing ships at half, and slider defaults now reach the renderer

Requested: make the app's default smoothing the middle of the slider.

- [x] **`SmoothSlider` default 0 → 50.** Raw gates are the honest thing to show a decoder
      author and the wrong thing to open with for everyone else
- [x] **Changing the XAML alone would have shipped a slider that lies.** `SmoothSlider_ValueChanged`
      guards on `_mapView is not null`, and it fires during `InitializeComponent` when
      `_mapView` is still null — so the XAML value was being dropped on the floor. That was
      invisible only because both ends were kept equal by hand: `Value="85"` against a
      `_radarOpacity` of `0.85f`, and `Value="0"` against a `_radarSmoothing` of `0f`. Two
      hand-synchronised copies of the same constant, in different projects
- [x] **The constructor now pushes both sliders into `_mapView` once, right after it is
      built**, so the XAML value is the only place a default is written. The `MapView` field
      initialisers stay as fallbacks for a renderer used without the shell. Settings restore
      runs further down and overrides both, unchanged
- [x] **Verified on a clean profile, not by reading the code.** With `settings.json` moved
      aside: `SmoothSlider = 50`, `OpacitySlider = 85`. The user's own file was checksummed
      before and after and restored byte-identical

Worth knowing: anyone who has already moved the slider keeps their value, because
`LayerSliders` records only changed controls. That is the property that makes revising a
shipped default possible at all — but it also means this change is invisible to them.

**Gate:** [PASSED] 666/666 tests, 0 warnings.

## Review fixes: seven findings, all real

`/code-review` over the session's working tree. Nothing was dismissed; two were regressions
introduced by the work above and would have shipped.

- [x] **`Recenter` was resetting the zoom it promised to keep.** Assigning `SiteCombo.SelectedItem`
      raises `SelectionChanged` synchronously, and `SiteCombo_SelectionChanged` runs `FrameSite`
      before its first `await` — which calls `Camera.MoveTo(…, 250)`. The snapshot taken on the
      next line therefore read 250, not the scale the user had. Worse, it was conditional: if the
      place's nearest site was already selected the assignment was a no-op, no event fired, and
      the zoom *was* kept — so the button behaved differently depending on where you had been.
      Fixed by reading `MetersPerPixel` before touching the combo
- [x] **The new smoothing default reached only the primary pane.** `PaneManager.CreateSecondary`
      builds its own `MapView` starting at the field default of 0, and the slider handler only
      ever touched `_mapView`. Two panes on the same product would have drawn smoothed and raw
      while the panel reported one number. This is the identical failure `ApplyValueFilter` was
      written for — "invisible while the window defaulted to everything" — and it stayed hidden
      here for the same reason, that 0 was also what a fresh secondary already had.
      `ApplyRadarAppearance` now fans both sliders out to every pane, and a pane opened later
      starts where the sliders are. It fixes opacity's identical latent split at the same time
- [x] **The WHERE bar overflowed and clipped in silence.** About 1070 px of fixed content
      against `MinWidth="900"` less the 50 px rail: `Recenter` and half the readout fell off the
      right edge with no scrollbar and no affordance — the failure mode `CLAUDE.md` already
      documents for the left rail, reintroduced one bar over. Now a `WrapPanel`. Verified at the
      900 DIP minimum: it wraps to two rows with nothing hidden
- [x] **An orphaned `<summary>`.** Inserting `UpdateCentreReadout` above `NotifyHomeViewChanged`
      left the latter's doc attached to the former, so it carried two summaries and the reason
      for its 5 % zoom threshold was silently reassigned. The compiler does not warn
- [x] **The "centred" tolerance mixed ground km with Mercator metres.** `metresPerPixel` is
      Mercator, which overstates ground metres by `1/cos(lat)`, so a rule stated as 12 px was
      enforced as ~16 px at 40°N and ~25 px in Alaska. Now multiplied by `cos(lat)`, with a test
      that pins the same offset and scale at both latitudes
- [x] **A vacuous assertion.** `Assert.Same(near.Site, pick.Site)` could not fail: the helper
      defaulted both cells to the same `RadarSite`, so it held whichever was picked. The distant
      cell now carries its own site
- [x] **Stale test counts** in `ci.yml` and `CLAUDE.md` — 652 against an actual 667 (406 pure
      library, 261 shell)

**Gate:** [PASSED] 667/667 tests, 0 warnings.

## The Settings window did not fit on the screen

Reported against 0.2.0: Settings overruns the top and bottom of the display.

- [x] **`SizeToContent="Height"` with no ceiling.** The window grew to whatever its content
      needed — measured at **1222 px on a 1200 px screen** at 125 % scaling — then
      `WindowStartupLocation="CenterOwner"` centred that, so it hung 41 px off the top and put
      its bottom under the taskbar. `ResizeMode="NoResize"` meant nobody could drag it back
- [x] **The buttons were the first thing to go.** The root was a single `StackPanel` with Save
      and Cancel at the end, so overflow pushed exactly the two controls a dialog cannot afford
      to lose off the bottom. They are now docked outside the scroller — content may scroll,
      the way out may not
- [x] **`MaxHeight` comes from `SystemParameters.WorkArea`**, not a constant: the usable height
      is a fact about the machine, and the work area already excludes the taskbar
- [x] **Measured, per the panel-budget method.** Before: 700×1222 at y=−41, 41 px off the top
      and 41 px past the work area. After: 700×1080 at y=30, nothing off either edge, Save and
      Cancel at y=1039–1076, and `ScrollPattern.VerticalViewSize` reporting 70 % of the content
      visible with the rest scrollable. `ProviderCombo`, `KeyBox`, `ContactBox` and
      `PaletteButton` all still reachable; Cancel still closes the dialog

Not a bug: **the main window does open maximized.** It measured as `NORMAL` at first, which was
this session's own screenshot retry loop calling `ShowWindow(SW_RESTORE)` on it. On an untouched
launch it is `MAXIMIZED` at (−9,−9)–(1929,1149) — the 1920×1140 work area plus the invisible
resize border. Automation that fights for the foreground can manufacture the bug it is looking
for; measure on a launch nothing has touched.

**Gate:** [PASSED] 667/667 tests, 0 warnings.


## Running in the background, in the system tray

The alerting was already built — tracks, tiering, throttling, tray balloons — and it all
stopped the moment somebody closed the window. Making it survive that was the work.

- [x] **Close leaves it watching; Exit exits.** `MainWindow.OnClosing` cancels the close and
      hides when `CloseToTray` is set. Three things keep it from being a trap: the tray menu
      carries Exit (an app that can only be quit from a window it has hidden is a trap), a
      one-time balloon says where it went the first time it happens, and the behaviour has a
      switch. Minimise-to-tray exists too but is **off** by default — minimise has a meaning
      everybody knows, and a vanished taskbar button reads as a crash, whereas a redefined
      close button gets a notification that explains itself
- [x] **The renderer idles while hidden, and this was not an optimisation.** `Present(1)`
      paces the render loop by waiting for a vertical blank — but only while there is
      something on screen to wait for. Against a hidden swap chain DXGI returns immediately,
      so a window in the tray left the render thread spinning a core drawing frames nobody
      could see. `MapView.Paused` sleeps the loop instead, keeping the device, its textures
      and the staged sweep alive so coming back is a flag rather than a rebuild
- [x] **The Level II stream stands down; the watch does not.** A volume is ~210 MB as floats
      and none of it feeds the alarm — threats come from the Level III storm-track poll (2 min)
      and the api.weather.gov warnings poll (60 s), both already `DispatcherTimer`s that run
      regardless of what is on screen. Hiding stops the stream and leaves the mode alone, so
      restoring is a reconnect rather than a reconstruction. `HideToTray` also arms the storm
      watch: with a window up, whether the storm layer is on is the user's business, but here
      it is the only reason the process is still running
- [x] **Measured, not assumed.** Hidden: **1.7 % of one core over 10 s, 364 MB** after a live
      session; **1.2 % and 230 MB** started straight into the tray. Visible with a live volume:
      **60 %**. Verified against the running app — window opens, `WM_CLOSE` hides it without
      exiting, second launch wakes it, window returns
- [x] **One instance per session.** Only became necessary once the app could hide: with no
      taskbar button, relaunching from the Start menu is how people ask for the window back,
      and that used to start a second copy — two tray icons, two Level II streams, and two
      notifications for every storm. A named mutex decides who is first and a named event
      carries the "come back". A blocked mutex must never stop the app starting, so the guard
      failing is treated as "run unguarded"
- [x] **Start with Windows lives in the registry and nowhere else.** `HKCU\...\Run`, launching
      with `--tray`. Deliberately not mirrored into settings.json: it can be turned off from
      Task Manager's Startup tab without this app being told, and a ticked box over a Run key
      that is not there is a promise the app cannot keep. `SyncPath` repoints a stale entry,
      since it holds an absolute path and fails silently at login when the app moves
- [x] **Cancel still means cancel.** The registry is written in `Save_Click` with everything
      else, not when the box is ticked — the same rule that put home in pending fields. The
      failure case is reported by the main window rather than the dialog, because the dialog
      is closed by the time it is known
- [x] **Started-into-the-tray opens minimised, then hides.** The D3D surface is an `HwndHost`
      and only builds its child window once its parent has been laid out, so a window hidden
      before its first layout has no renderer to come back to. Minimised and unactivated is
      what stops it flashing on screen in between
- [x] **The tray line is the whole UI while it is hidden.** `TrayStatus` builds it — a threat
      always outranks the watch list, and "not watching anywhere" is said out loud, because an
      app with no place saved looks exactly like one that is watching and has nothing to
      report. Capped at 63 characters, which is a hard limit rather than a style rule: WinForms
      throws above it, and an over-long place name would take the tray icon down and the
      alerting with it

**Gate:** [PASSED] 675/675 tests, 0 warnings.


## Review of the tray work, and what it found

Ten findings against the change above. Four were substantive.

- [x] **The single-instance guard did not cover the door WPF actually uses.** `StartupUri`
      navigates *after* `OnStartup` returns and `Shutdown()` only queues a callback, so a
      second launch that had already decided to leave still ran the whole `MainWindow`
      constructor on its way out — a second tray icon, a second warnings fetch, a second set
      of timers. The window is created explicitly after the claim now and `StartupUri` is
      gone, which makes the early return structurally sufficient rather than dependent on
      dispatcher ordering. Second launch measured at **0.18 s**
- [x] **A tray start could watch nothing at all.** `HideToTray` arms the storm watch and
      `RestoreLayerState` replays the saved layer toggles; with the hide running first, anyone
      who had ever unticked "Track storms" got it switched straight back off — leaving the app
      in the tray reporting "Watching Home" with no storm poll running. The watch is re-armed
      after the restore now
- [x] **Only the Level II stream stood down; six other clocks kept running.** Satellite
      (61 MB every four minutes), both MRMS layers, lightning, placefiles, SPC outlooks and
      the archive loop all kept fetching and decoding into a paused renderer, while the
      release notes claimed a hidden OpenWSR cost almost nothing. `ITimedLayer` stops them.
      Suspension is a **latch, not a snapshot**: the first attempt only stopped clocks that
      were already running, so a layer switched on by the restore started fetching anyway —
      a 61 MB granule landed 71 s after a hide. `Enable` now takes the state without starting
      the clock. Verified by soaking a tray start for five minutes and categorising every log
      line: **zero satellite, lightning or MRMS fetches**, six warnings polls, twelve storm
      polls
- [x] **Logging off spent the one-time "still watching" balloon.** Windows ends a session
      through `Application.Shutdown`, which still raises `Closing` — so the close-to-tray path
      ran during sign-out, set `TrayHintShown` and fired the balloon at someone on their way
      out. The next genuine close would then have hidden the window with no explanation, which
      is the exact trap the hint exists to prevent. `SessionEnding` sets `_exiting` now
- [x] **Resuming yanked the camera back to the radar.** `StartLiveAsync` frames the site,
      which is right when you pick one and wrong when the feed is merely being picked back up.
      It takes a `frameSite` flag
- [x] Also: `ShowActivated` was left false after a tray start, so later restores came back
      without the foreground; the second instance now hands over its foreground rights, or
      `Activate()` is refused and the window it was asked for never comes forward; the mutex
      is claimed after the event is published, closing a race where a second launch found the
      slot taken and no event to signal and so did nothing at all; `SyncPath` only repoints a
      Run entry whose target is **gone**, rather than letting one `dotnet run` capture an
      installed copy's login start; and a null `SystemFonts.MenuFont` no longer takes the
      whole app down over the weight of one bold menu item
- [x] **Memory, measured rather than claimed.** The published figure was 230 MB, taken from a
      tray start and quietly untrue of a hide after a live session — that peaked at **2.0 GB
      three minutes in**. Hiding now collects, compacting, because every assumption behind
      "let the runtime decide" has just stopped holding: the volume is dropped, the clocks
      that would allocate are stopped, and the process is idle for hours. 766 MB of heap to
      269 MB, and a second pass two minutes later — for work that was still in flight when the
      window went away — took 285 MB to 78 MB

**Gate:** [PASSED] 675/675 tests, 0 warnings.
