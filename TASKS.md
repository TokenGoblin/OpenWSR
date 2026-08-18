# OpenWSR — Phases & Subtasks

Work breakdown for `OpenWSR-build-plan.md`. Work strictly one phase at a time; a
phase is done only when its **gate** passes. Check items off as they land.

---

## Phase 0 — Bootstrap & test data

- [ ] `git init`; commit the build plan and this file
- [ ] Create `OpenWSR.sln` with projects: `App`, `Render`, `Nexrad`, `Ingest`, `Geo`, `Palettes` + `Nexrad.Tests`, `Geo.Tests`
- [ ] Project settings everywhere: `net10.0` (`-windows` only for App/Render), `<Nullable>enable</Nullable>`, `<AllowUnsafeBlocks>`, `TreatWarningsAsErrors` in Release
- [ ] Enforce purity: `Nexrad` and `Geo` reference nothing but the BCL (add a test that asserts their assembly references)
- [ ] Add packages: Vortice trio, `AWSSDK.S3`, `SharpZipLib`, `CommunityToolkit.Mvvm`, `Serilog` + file sink
- [ ] Download quiet-weather volume → `assets/testdata/`
- [ ] Download KTLX 2013-05-20 ~20:00Z volume → `assets/testdata/`
- [ ] Capture ~30 consecutive live chunks **spanning a volume boundary** (E → S) → `assets/testdata/chunks/`
- [ ] Decide storage for test data (plain commit vs Git LFS vs fetch script) — likely 30–50 MB total

**Gate:** solution builds clean; test data present and committed/fetchable.

---

## Phase 1 — Level II decoder (`OpenWSR.Nexrad`)

- [ ] 24-byte volume header parser (`AR2V00xx.`, date/time, ICAO)
- [ ] LDM record framing: 4-byte big-endian control word (abs = length, negative = final record) + bzip2 decompress (SharpZipLib)
- [ ] Message framing: 12-byte CTM prefix + 16-byte message header; dispatch by message type; handle message segmentation
- [ ] Message 31: block-pointer walk; VOL / ELV / RAD blocks
- [ ] Message 31 moments: REF, VEL, SW, ZDR, PHI, RHO; scale/offset to physical units; codes 0/1 → NaN + parallel range-fold flags; **skip unknown block names** (CFP etc.)
- [ ] Message 5 (VCP definition) parser
- [ ] Message 2 (RDA status) parser
- [ ] `Sweep` record + volume→sweeps assembly; correct handling of duplicate 0.5° cuts (SAILS/MESO-SAILS/MRLE); super-res REF vs VEL geometry kept independent
- [ ] Console harness: print per-sweep elevation, moment, radial count, gate count, min/max
- [ ] Golden-file tests: exact values from a hand-verified radial (cross-check with Py-ART/MetPy); both test volumes; sanity range −32..+95 dBZ

**Gate:** harness output physically sane on both volumes; golden-file tests green.

---

## Phase 2 — Geo math (`OpenWSR.Geo`)

- [ ] `BeamPath` — 4/3 effective-earth-radius formulas from the plan
- [ ] `Offset` — spherical great-circle (or geodesic) destination point
- [ ] `ToMercator` / `FromMercator` (EPSG:3857)
- [ ] Analytic tests: 0.5°/100 km slant → ≈1461 m above radar level; Mercator round-trip < 1e-9°; offset vs known geodesic cases

**Gate:** all analytic tests green.

---

## Phase 3 — D3D11 window & map (`OpenWSR.Render` + App shell)

- [ ] `HwndHost` subclass with child HWND; D3D11 device + flip-model swapchain; resize handling
- [ ] Render loop on dedicated render thread (owns the device context)
- [ ] Orthographic camera in Mercator space; mouse + touch pan/zoom with inertia
- [ ] Visible-tile computation for current zoom/viewport (XYZ scheme)
- [ ] Async tile fetch (MapTiler primary, OSM fallback; provider + key in settings from day one)
- [ ] Disk tile cache under `%LOCALAPPDATA%\OpenWSR\tiles`; VRAM LRU cache capped ~512 MB
- [ ] Parent-zoom tile stretch while children load — no blank checkerboard
- [ ] Debug marker layer: radar site lat/lons rendered as dots for alignment check
- [ ] Establish the airspace pattern now: side-docked WPF panels; Popup/ToolTip for anything floating over the map

**Gate:** 60 fps pan/zoom over CONUS on a mid-range GPU; site markers land on the right cities.

---

## Phase 4 — Radar rendering

- [ ] Sweep data upload as `R32_FLOAT` (or `R16_UNORM`+scale) texture, `gateCount × radialCount`
- [ ] Azimuth structured buffer; palette as 256-entry 1D texture
- [ ] Vertex shader: geometry from `SV_VertexID` → (radial, gate) → beam path → offset from radar origin → Mercator; single indexed/instanced draw
- [ ] Pixel shader: sample data, normalize via `ScaleInfo`, sample palette; `clip()` NaN; optional distinct range-fold color
- [ ] Premultiplied alpha blend over basemap; user-adjustable opacity
- [ ] Product/palette switch = texture rebind only (verify zero geometry work)
- [ ] VRAM budget policy: only displayed moment/tilt resident per loop frame
- [ ] (If profiling demands) per-gate beam-path lookup buffer

**Gate:** KTLX 2013-05-20 hook echo visible, correctly over Moore OK, 60 fps while panning, product switch < 16 ms.

---

## Phase 5 — Archive ingest (`OpenWSR.Ingest`)

- [ ] Anonymous S3 client (`unidata-nexrad-level2`, us-east-1); timeout + retry/backoff on every call
- [ ] List volumes by site + UTC date; handle `.gz` (pre-2016-06-02) and bare files; **skip `*_MDM`**
- [ ] Volume disk cache under `%LOCALAPPDATA%\OpenWSR\volumes` with size cap + LRU eviction
- [ ] Radar site table (ICAO, lat/lon, elevation, TDWR flag) as embedded resource
- [ ] Time-slider UI; N-frame loop with configurable speed
- [ ] Nearest-radar selection from user-entered location
- [ ] Data-age indicator groundwork (timestamp of displayed frame shown prominently)

**Gate:** scrub a day's volumes for a chosen site; loop 30 frames smoothly; second load hits disk cache with zero network calls.

---

## Phase 6 — Real-time chunks

- [ ] Volume-directory discovery in `unidata-nexrad-level2-chunks`: probe candidate dirs among 1–999, newest object wins; persist last-known index, probe forward
- [ ] Chunk key parsing: `<SITE>/<vol>/<YYYYMMDD-HHMMSS>-<seq>-<S|I|E>`; group by volume-start timestamp
- [ ] `VolumeAssembler` per site: hold header state from `S`; decode `I`/`E` as bare LDM streams
- [ ] Edge cases: join mid-volume (no `S` seen), out-of-order arrival, missing chunks, new volume starting before old completes
- [ ] Partial-volume render: show lowest tilt as soon as complete
- [ ] Adaptive polling: tighten while chunks flow, back off when idle (VCP-aware, 2–10 min scans)
- [ ] Pipeline on `System.Threading.Channels`: poller → thread-pool decoder → render queue; never decode on UI/render thread
- [ ] Feed-health UI: data age colored past ~10 min; visible failure state on feed death
- [ ] Offline replay test: chunk corpus in randomized order → decoded sweeps identical to archive version (byte-identical reassembly = bonus)
- [ ] 30-minute live soak: no gaps, no duplicate frames, no leaks (memory profile before/after)

**Gate:** soak test and randomized replay test both pass.

---

## Phase 7 — Warnings, palettes, tools

- [ ] `api.weather.gov/alerts/active` poller: descriptive User-Agent w/ contact, ≥60 s interval, backoff on error
- [ ] GeoJSON → Mercator polygon tessellation; fill + stroke per NWS convention (red TOR, yellow SVR, green FFW); z-order above radar
- [ ] Polygon hit-test + detail flyout (Popup, per airspace pattern): headline, expiry, full text
- [ ] GR2Analyst `.pal` parser in `OpenWSR.Palettes`; map breakpoints → 256-entry texture (with interpolation flags honored)
- [ ] Inspector: hover → value, azimuth, slant range, ground range, beam height AGL
- [ ] Distance/bearing tool — geodesic math from lat/lon, never Mercator lengths
- [ ] Warning expiry/refresh lifecycle (polygons disappear on expiry without restart)

**Gate:** live warnings appear/expire correctly; imported third-party `.pal` matches its reference screenshot.

---

## Phase 8 — Polish

- [ ] Multi-pane 1/2/4 layout; linked or independent pan per pane
- [ ] Level III decoder: packet framing, then storm tracks, hail index, mesocyclone detection (bucket `unidata-nexrad-level3`; Iowa State Mesonet / NWS TGFTP fallbacks)
- [ ] Settings persistence; keyboard shortcuts; per-monitor DPI awareness (verify HwndHost across mixed-DPI monitors)
- [ ] Single-file, self-contained, ReadyToRun publish
- [ ] About box: "not for life-safety decisions" disclaimer; `THIRD-PARTY-NOTICES.md` (MIT notice if any Supercell Wx code ported)

**Gate:** published single-file build runs on a clean Windows machine with all of the above working.
