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
