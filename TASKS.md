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

- [ ] `HwndHost` subclass with child HWND; D3D11 device + flip-model swapchain; resize handling
- [ ] Render loop on dedicated render thread (owns the device context)
- [ ] Orthographic camera in Mercator space; mouse + touch pan/zoom with inertia
- [ ] Visible-tile computation for current zoom/viewport (XYZ scheme)
- [ ] Async tile fetch (MapTiler primary, OSM fallback; provider + key in settings from day one)
- [ ] Disk tile cache under `%LOCALAPPDATA%\OpenWSR\tiles`; VRAM LRU cache capped ~512 MB
- [ ] Parent-zoom tile stretch while children load â€” no blank checkerboard
- [ ] Debug marker layer: radar site lat/lons rendered as dots for alignment check
- [ ] Establish the airspace pattern now: side-docked WPF panels; Popup/ToolTip for anything floating over the map

**Gate:** 60 fps pan/zoom over CONUS on a mid-range GPU; site markers land on the right cities.

---

## Phase 4 â€” Radar rendering

- [ ] Sweep data upload as `R32_FLOAT` (or `R16_UNORM`+scale) texture, `gateCount Ã— radialCount`
- [ ] Azimuth structured buffer; palette as 256-entry 1D texture
- [ ] Vertex shader: geometry from `SV_VertexID` â†’ (radial, gate) â†’ beam path â†’ offset from radar origin â†’ Mercator; single indexed/instanced draw
- [ ] Pixel shader: sample data, normalize via `ScaleInfo`, sample palette; `clip()` NaN; optional distinct range-fold color
- [ ] Premultiplied alpha blend over basemap; user-adjustable opacity
- [ ] Product/palette switch = texture rebind only (verify zero geometry work)
- [ ] VRAM budget policy: only displayed moment/tilt resident per loop frame
- [ ] (If profiling demands) per-gate beam-path lookup buffer

**Gate:** KTLX 2013-05-20 hook echo visible, correctly over Moore OK, 60 fps while panning, product switch < 16 ms.

---

## Phase 5 â€” Archive ingest (`OpenWSR.Ingest`)

- [ ] Anonymous S3 client (`unidata-nexrad-level2`, us-east-1); timeout + retry/backoff on every call
- [ ] List volumes by site + UTC date; handle `.gz` (pre-2016-06-02) and bare files; **skip `*_MDM`**
- [ ] Volume disk cache under `%LOCALAPPDATA%\OpenWSR\volumes` with size cap + LRU eviction
- [ ] Radar site table (ICAO, lat/lon, elevation, TDWR flag) as embedded resource
- [ ] Time-slider UI; N-frame loop with configurable speed
- [ ] Nearest-radar selection from user-entered location
- [ ] Data-age indicator groundwork (timestamp of displayed frame shown prominently)

**Gate:** scrub a day's volumes for a chosen site; loop 30 frames smoothly; second load hits disk cache with zero network calls.

---

## Phase 6 â€” Real-time chunks

- [ ] Volume-directory discovery in `unidata-nexrad-level2-chunks`: probe candidate dirs among 1â€“999, newest object wins; persist last-known index, probe forward
- [ ] Chunk key parsing: `<SITE>/<vol>/<YYYYMMDD-HHMMSS>-<seq>-<S|I|E>`; group by volume-start timestamp
- [ ] `VolumeAssembler` per site: hold header state from `S`; decode `I`/`E` as bare LDM streams
- [ ] Edge cases: join mid-volume (no `S` seen), out-of-order arrival, missing chunks, new volume starting before old completes
- [ ] Partial-volume render: show lowest tilt as soon as complete
- [ ] Adaptive polling: tighten while chunks flow, back off when idle (VCP-aware, 2â€“10 min scans)
- [ ] Pipeline on `System.Threading.Channels`: poller â†’ thread-pool decoder â†’ render queue; never decode on UI/render thread
- [ ] Feed-health UI: data age colored past ~10 min; visible failure state on feed death
- [ ] Offline replay test: chunk corpus in randomized order â†’ decoded sweeps identical to archive version (byte-identical reassembly = bonus)
- [ ] 30-minute live soak: no gaps, no duplicate frames, no leaks (memory profile before/after)

**Gate:** soak test and randomized replay test both pass.

---

## Phase 7 â€” Warnings, palettes, tools

- [ ] `api.weather.gov/alerts/active` poller: descriptive User-Agent w/ contact, â‰¥60 s interval, backoff on error
- [ ] GeoJSON â†’ Mercator polygon tessellation; fill + stroke per NWS convention (red TOR, yellow SVR, green FFW); z-order above radar
- [ ] Polygon hit-test + detail flyout (Popup, per airspace pattern): headline, expiry, full text
- [ ] GR2Analyst `.pal` parser in `OpenWSR.Palettes`; map breakpoints â†’ 256-entry texture (with interpolation flags honored)
- [ ] Inspector: hover â†’ value, azimuth, slant range, ground range, beam height AGL
- [ ] Distance/bearing tool â€” geodesic math from lat/lon, never Mercator lengths
- [ ] Warning expiry/refresh lifecycle (polygons disappear on expiry without restart)

**Gate:** live warnings appear/expire correctly; imported third-party `.pal` matches its reference screenshot.

---

## Phase 8 â€” Polish

- [ ] Multi-pane 1/2/4 layout; linked or independent pan per pane
- [ ] Level III decoder: packet framing, then storm tracks, hail index, mesocyclone detection (bucket `unidata-nexrad-level3`; Iowa State Mesonet / NWS TGFTP fallbacks)
- [ ] Settings persistence; keyboard shortcuts; per-monitor DPI awareness (verify HwndHost across mixed-DPI monitors)
- [ ] Single-file, self-contained, ReadyToRun publish
- [ ] About box: "not for life-safety decisions" disclaimer; `THIRD-PARTY-NOTICES.md` (MIT notice if any Supercell Wx code ported)

**Gate:** published single-file build runs on a clean Windows machine with all of the above working.

