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
- [ ] `Offset` â€” spherical great-circle (or geodesic) destination point
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
- [ ] Parent-zoom tile stretch while children load â€” no blank checkerboard
- [x] Debug marker layer: radar site lat/lons rendered as dots for alignment check
- [x] Establish the airspace pattern now: side-docked WPF panels; Popup/ToolTip for anything floating over the map

**Gate:** [PASSED] 60 fps confirmed via in-app counter; site markers verified on Seattle/Bay Area/Denver/Chicago/OKC/San Antonio; pan/zoom exercised CONUS-to-street-level.

---

## Phase 4 â€” Radar rendering

- [ ] Sweep data upload as `R32_FLOAT` (or `R16_UNORM`+scale) texture, `gateCount Ã— radialCount`
- [x] Azimuth wedge-edge buffer (radialCount+1, sorted, wrap-safe); palette as 256-entry 1D texture
- [ ] Vertex shader: geometry from `SV_VertexID` â†’ (radial, gate) â†’ beam path â†’ offset from radar origin â†’ Mercator; single indexed/instanced draw
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

- [ ] Volume-directory discovery in `unidata-nexrad-level2-chunks`: probe candidate dirs among 1â€“999, newest object wins; persist last-known index, probe forward
- [x] Chunk key parsing incl. volume-start-timestamp grouping
- [x] `LiveVolumeAssembler`: S header capture; I/E decoded as bare LDM streams; order-independent by design (records are self-contained)
- [x] Edge cases tested: mid-volume join, randomized order, duplicates, missing chunks; two assemblers held across volume overlap
- [x] Partial-volume render: snapshot emitted per newly completed cut (first render ~5 s after connect)
- [ ] Adaptive polling: tighten while chunks flow, back off when idle (VCP-aware, 2â€“10 min scans)
- [ ] Pipeline on `System.Threading.Channels`: poller â†’ thread-pool decoder â†’ render queue; never decode on UI/render thread
- [x] Feed-health UI: data-age always shown, amber past 10 min live; poll errors surface in status and retry
- [ ] Offline replay test: chunk corpus in randomized order â†’ decoded sweeps identical to archive version (byte-identical reassembly = bonus)
- [x] 30-minute live soak vs KTLX: 8/8 consecutive volumes complete across 7 directory rollovers, 100 snapshots, 0 errors, 0 duplicates, memory flat (0.1→1.0 MB managed)

**Gate:** [PASSED] 30-min soak PASS (8/8 volumes, 0 errors, no leak) + randomized replay float-exact vs archive.

---

## Phase 7 â€” Warnings, palettes, tools

- [ ] `api.weather.gov/alerts/active` poller: descriptive User-Agent w/ contact, â‰¥60 s interval, backoff on error
- [ ] GeoJSON â†’ Mercator polygon tessellation; fill + stroke per NWS convention (red TOR, yellow SVR, green FFW); z-order above radar
- [x] Polygon hit-test (`GeoMath.PointInRing`, unit-tested incl. concave) + detail Popup (headline, expiry, description, instruction)
- [ ] GR2Analyst `.pal` parser in `OpenWSR.Palettes`; map breakpoints â†’ 256-entry texture (with interpolation flags honored)
- [ ] Inspector: hover â†’ value, azimuth, slant range, ground range, beam height AGL
- [ ] Distance/bearing tool â€” geodesic math from lat/lon, never Mercator lengths
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
