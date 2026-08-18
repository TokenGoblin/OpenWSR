# OpenWSR — Build Plan

A native Windows NEXRAD Level II/III radar viewer in .NET 10. This document is the
implementation brief: read it fully before writing code, then work phase by phase.

**Working name:** OpenWSR. Do not use the name "RadarScope" or imitate its icon —
that is an active trademark held by DTN.

---

## 0. Read this first

### What this app is
A single-site radar viewer that renders NEXRAD data in its **native polar (radial)
form**, georeferenced onto a slippy map, with looping, product switching, and NWS
warning polygons. It is not a forecast app and not a national mosaic.

### Non-negotiable design decisions
These are settled. Do not relitigate them mid-build.

| Decision | Choice | Why |
|---|---|---|
| UI framework | WPF (.NET 10, `net10.0-windows`) | Mature, familiar, good `HwndHost` interop story |
| Render API | Direct3D 11 via **Vortice.Windows** | Actively maintained managed bindings; D3D11 is enough and far simpler than D3D12 |
| Render surface | `HwndHost` wrapping a child HWND with its own swapchain | Avoids WPF's software compositing path entirely |
| Map | **Own raster tile renderer**, not an embedded map control | Radar must composite in the same scene with correct depth/blend; embedding MapLibre or WebView2 makes overlay alignment miserable |
| Coordinates | Web Mercator (EPSG:3857) internally, everywhere | Matches XYZ tile schemes; one projection, no conversions at draw time |
| Data source | AWS Open Data S3, anonymous access | Free, no auth, no egress cost to us |

**Airspace caveat for the `HwndHost` decision:** a child HWND always draws above
WPF content within its rectangle, so in-window WPF elements cannot overlap the map.
Design around it: dock panels beside the map, use `Popup`/`ToolTip`/`ContextMenu`
(separate top-level HWNDs, which do overlap) for the inspector and warning detail
flyouts, and render anything that must sit *on* the map (polygons, markers, legends)
in the D3D scene itself.

### Licensing posture
- NEXRAD data carries **no use restrictions** (NOAA Big Data Project / AWS Open Data).
- Basemap tiles do have terms. Default to a MapTiler key (no card required) with
  OSM raster as fallback. Make the provider and key configurable from day one.
- Supercell Wx (MIT, C++/Qt) solves many of the same problems. **Use it as an
  algorithmic reference.** If any code is actually ported, reproduce the MIT notice
  in `THIRD-PARTY-NOTICES.md`.
- Ship a "not for life-safety decisions" disclaimer in the About box.

---

## 1. Solution layout

```
OpenWSR.sln
├── src/
│   ├── OpenWSR.App/             WPF shell, viewmodels, settings UI
│   ├── OpenWSR.Render/          D3D11 device, passes, shaders, camera
│   ├── OpenWSR.Nexrad/          Level II + Level III decoders (pure, no I/O)
│   ├── OpenWSR.Ingest/          S3 clients, chunk assembly, disk cache, NWS alerts
│   ├── OpenWSR.Geo/             Mercator, beam propagation, geodesy
│   └── OpenWSR.Palettes/        Color table parsing + generation
├── tests/
│   ├── OpenWSR.Nexrad.Tests/    Golden-file decoder tests
│   └── OpenWSR.Geo.Tests/      Analytic math tests
├── assets/testdata/             Committed sample volumes (see §3)
└── docs/
```

`OpenWSR.Nexrad` and `OpenWSR.Geo` must have **zero** dependencies on WPF, D3D,
or the network. They are pure and fully unit-testable. Enforce this.

### Package set
- `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.D3DCompiler`
- `AWSSDK.S3` (used with `AnonymousAWSCredentials`)
- `SharpZipLib` — bzip2 decompression; .NET has no built-in bzip2
- `CommunityToolkit.Mvvm` — MVVM plumbing
- `Serilog` + file sink
- `System.Threading.Channels` (in-box) for the ingest pipeline

Enable `<Nullable>enable</Nullable>`, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
(needed for GPU buffer marshalling), and `TreatWarningsAsErrors` in Release.

---

## 2. Data sources — exact endpoints

### Level II archive (full volume scans)
- Bucket: `unidata-nexrad-level2`, region `us-east-1`
- **The old `noaa-nexrad-level2` bucket is deprecated** (decommissioned 2025-09-01).
  Do not use it.
- Key layout: `<YYYY>/<MM>/<DD>/<SITE>/<SITE><YYYYMMDD>_<HHMMSS>_V06`
- Files after 2016-06-02 have no `.gz` suffix; earlier files do. Handle both.
- Listings also contain `*_MDM` objects (metadata-only). Skip them when
  enumerating volumes.

### Level II real-time (chunks)
- Bucket: `unidata-nexrad-level2-chunks`
- Layout: `<SITE>/<volume 1-999>/<YYYYMMDD-HHMMSS>-<sequence>-<S|I|E>`
  (e.g. `KTLX/1/20260816-082009-001-S`). The timestamp is the volume start time
  and is shared by every chunk in the volume — use it to group chunks and detect
  a new volume starting.
- The 999 volume directories rotate. To find live data you must list several
  candidate directories and pick the one with the newest object — there is no
  "latest" pointer. Cache the last-known index and probe forward from it.
- Each chunk is roughly **100 radial degrees of one tilt** (treat the span as
  variable, not guaranteed), uploaded as the radar completes it.

### Level III real-time
- Bucket: `unidata-nexrad-level3` (a selected subset of products)
- Alternates worth wiring as fallbacks: Iowa State Mesonet, NWS TGFTP.

### Warnings
- `https://api.weather.gov/alerts/active` — GeoJSON, free, no key.
- **Requires a descriptive `User-Agent` with contact info** or it will 403 you.
- Be polite: poll no faster than every 60s.

### Basemap tiles
- Standard XYZ raster. MapTiler primary, OSM fallback. Key in user settings.

---

## 3. Test data — get this before writing the decoder

Download two volumes into `assets/testdata/` and commit them (they're a few MB each):

1. A recent, quiet-weather volume from any site — validates the happy path.
2. `KTLX` from 2013-05-20 around 20:00Z (Moore, OK tornado) — dense data, dual-pol,
   good stress case with well-documented signatures to eyeball against.

Also capture ~30 consecutive live chunk files from one site so chunk-assembly logic
can be tested offline. Make sure the capture spans a **volume boundary** (the `E`
chunk of one volume and the `S` of the next) so mid-volume-start and rollover paths
are exercised. This matters: chunk assembly is the single buggiest part of this
project and you do not want to debug it against a live feed.

---

## 4. Phases

Each phase ends with a runnable artifact and a stated acceptance test. Do not start
a phase until the previous one's acceptance test passes. Commit at each boundary.

---

### Phase 1 — Level II decoder (no UI, no network)

Implement `OpenWSR.Nexrad` against **ICD 2620010H, Section 7** (Archive II/User).

Structure to decode:
- 24-byte volume header: `AR2V00xx.` + tape/date/time + ICAO
- Metadata record, then a sequence of **LDM records**: 4-byte big-endian control
  word (absolute value = byte length; a negative value marks the final record),
  followed by a bzip2 stream
- Each decompressed block contains messages, each with a 12-byte CTM header
  prefix and a 16-byte message header
- **Message 31** (Digital Radar Data Generic Format) is the payload that matters:
  variable data blocks — VOL, ELV, RAD, then moment blocks REF, VEL, SW, ZDR, PHI,
  RHO (newer RDA builds may add blocks such as CFP — walk the block pointers and
  skip unrecognized block names rather than assuming a fixed set)
- **Message 5** (VCP definition) — needed to know elevation cuts and scan strategy
- **Message 2** (RDA status) — radar operability state

Public output model:

```csharp
public sealed record Sweep(
    string SiteId,
    DateTime ScanTimeUtc,
    double RadarLatDeg, double RadarLonDeg, double RadarAltM,
    int ElevationIndex, float ElevationAngleDeg,
    Moment Moment,
    float[] AzimuthsDeg,        // length = radialCount, NOT evenly spaced
    float FirstGateM, float GateSpacingM,
    int GateCount,
    float[] Data,               // radialCount * gateCount, NaN = below threshold / range folded
    ScaleInfo Scale);
```

Decode moments to **physical units** (dBZ, m/s, dB, degrees) in the decoder, not the
renderer. Apply the per-moment scale/offset from the block header. Map the two
reserved codes correctly: 0 ⇒ below threshold, 1 ⇒ range folded. Both become `NaN`
in `Data`, but keep a parallel flags array if you want to render range folding
distinctly later (you will).

Gotchas:
- Everything on the wire is **big-endian**. Use `BinaryPrimitives.ReadInt16BigEndian`
  etc. throughout; do not hand-roll byte swaps.
- Super-res reflectivity and velocity have **different gate counts and spacings** in
  the same volume. Never assume they share geometry.
- Azimuths are not uniformly spaced and the sweep may not start at 0°.
- **SAILS/MESO-SAILS/MRLE**: modern VCPs insert supplemental low-level cuts
  mid-volume, so a volume can contain several 0.5° sweeps. `ElevationIndex` is a
  scan-order index, not an angle rank — "lowest tilt" logic and looping must handle
  duplicate elevation angles per volume.

**Acceptance:** a console harness reads both committed test volumes and prints, per
sweep, the elevation angle, moment, radial count, gate count, and min/max value.
Values must be physically sane (reflectivity roughly −32 to +95 dBZ). Unit tests
assert exact known values from a hand-verified radial.

---

### Phase 2 — Geo math

`OpenWSR.Geo`, small but must be exactly right.

```csharp
// 4/3 effective earth radius beam propagation
// ke = 4/3, a = 6371008.8 m
// h = sqrt(r² + (ke·a)² + 2·r·ke·a·sin θ) − ke·a
// s = ke·a · asin( r·cos θ / (ke·a + h) )
public static (double groundRangeM, double heightM) BeamPath(
    double slantRangeM, double elevationRad);

public static (double lat, double lon) Offset(
    double lat0, double lon0, double azimuthRad, double groundRangeM);

public static (double x, double y) ToMercator(double latDeg, double lonDeg);
```

Use the geodesic (or at minimum spherical great-circle) offset, not a flat-earth
approximation. At 250 km range flat-earth error is kilometres.

**Acceptance:** unit tests against the formula above — at 0.5° elevation and 100 km
slant range, beam centre height is ≈1.46 km above radar level (the formula gives
1461 m; cross-check against published beam-height tables, which show ≈1.5 km).
Mercator round-trip error < 1e-9 degrees.

---

### Phase 3 — D3D11 window and map

Get pixels on screen before touching radar rendering.

- `HwndHost` subclass creating a child window; D3D11 device + DXGI flip-model swapchain
- Orthographic camera in Mercator space; pan/zoom/inertia with mouse and touch
- Tile pipeline: compute visible XYZ tiles for the current zoom, async fetch,
  decode to texture, LRU cache in VRAM (cap ~512 MB), two-tier disk cache under
  `%LOCALAPPDATA%\OpenWSR\tiles`
- Draw parent-zoom tiles stretched while children load — no blank checkerboard

**Acceptance:** smooth 60 fps pan/zoom over the continental US on a mid-range GPU.
Verify alignment by hard-coding a handful of known lat/lon markers (radar site
locations work well) and confirming they land on the right cities.

---

### Phase 4 — Radar rendering

The performance-critical phase. A super-res reflectivity sweep is roughly
720 radials × 1832 gates ≈ 1.3M cells refreshing every few minutes. Brute-force
CPU triangulation will not hold frame rate.

**Required approach:**

1. Upload moment values as an `R32_FLOAT` (or `R16_UNORM` with scale) texture of
   dimensions `gateCount × radialCount`. This is one upload per sweep.
2. Upload azimuths as a small structured buffer (`radialCount` floats).
3. Upload the color table as a 256-entry `R8G8B8A8_UNORM` 1D texture.
4. **Generate geometry in the vertex shader from `SV_VertexID`.** Do not build or
   rebuild a CPU vertex buffer per sweep. Derive `(radialIndex, gateIndex)` from the
   vertex id, read the azimuth from the buffer, compute ground range from
   `firstGate + gateIndex * gateSpacing` via the beam-path formula, offset from the
   radar origin, and project to Mercator. Issue one instanced/indexed draw for the
   whole sweep.
5. Fragment shader samples the data texture, normalizes, and samples the palette.

Payoff: switching products or palettes is a texture swap with **zero** geometry work,
and looping is just cycling which data texture is bound.

Also:
- Discard `NaN` gates (`clip()`), optionally render range-folded cells in a distinct
  purple as the NWS does
- Premultiplied alpha blend over the basemap; user-adjustable opacity
- Precompute beam-path per gate index into a lookup buffer if profiling shows the
  trig is hot — it's constant per sweep geometry
- Budget VRAM for loops deliberately: keep only the displayed moment/tilt resident
  per loop frame and decode others on demand; a full super-res volume across all
  moments is hundreds of MB per frame if kept wholesale

**Acceptance:** the 2013-05-20 KTLX volume renders with a visible hook echo, at
60 fps while panning, correctly positioned over Moore, Oklahoma. Product switch
latency under 16 ms.

---

### Phase 5 — Archive ingest

- `AWSSDK.S3` with `AnonymousAWSCredentials`, region `us-east-1`
- List and fetch volumes by site + UTC date
- Disk cache under `%LOCALAPPDATA%\OpenWSR\volumes` with a size cap and LRU eviction
- Radar site table (ICAO, lat, lon, elevation, TDWR flag) as an embedded resource
- Time-slider UI; loop of N frames with configurable speed
- Nearest-radar selection from a user-entered location

**Acceptance:** pick a site and date, scrub through a day's volumes, loop 30 frames
smoothly. Second load of the same volume is served from disk with no network call.

---

### Phase 6 — Real-time chunks

The buggiest phase. This is why §3's offline chunk corpus exists.

- Poll the chunks bucket to locate the active volume directory among the 999
- Maintain a `VolumeAssembler` per site that ingests chunks in sequence order
- **The S/I/E problem:** only the `S` chunk carries the volume header. `I` and `E`
  chunks are bare LDM record streams and will fail any parser that expects a header.
  The assembler must hold the header state from `S` and feed it to subsequent
  decodes. Handle: arriving mid-volume (no `S` seen), out-of-order arrival, missing
  chunks, and a new volume starting before the old one completes.
- Render partial volumes — show the lowest tilt as soon as it's complete rather than
  waiting for the full scan. This is most of the perceived latency advantage.
- Adaptive polling: back off when idle, tighten when chunks are flowing. Sites scan
  every 2–10 minutes depending on VCP.
- Whole pipeline on `System.Threading.Channels`: poller → decoder (thread pool) →
  render queue. Never decode on the UI or render thread.

**Acceptance:** run live against an actively scanning site for 30 minutes with no
gaps, no duplicate frames, and no leaks. Replay the offline chunk corpus in
randomized order and produce decoded sweeps identical to those from the archive
version of the same scan (byte-identical file reassembly is a bonus, not the bar).

---

### Phase 7 — Warnings, palettes, tools

- `api.weather.gov/alerts/active` poller, GeoJSON → Mercator polygons, filled and
  stroked per NWS convention (red tornado, yellow severe thunderstorm, green flash
  flood). Correct z-order above radar.
- Click a polygon → detail panel with headline, expiry, and full text.
- Palette import: support the **GR2Analyst `.pal` format**. It's the community
  standard and there are hundreds of good tables freely available in it.
- Inspector tool: hover reports value, azimuth, slant range, ground range, beam
  height AGL at the cursor.
- Distance/bearing measuring tool. Compute distances geodesically from lat/lon —
  never in Mercator units, which overstate length by ~1/cos(latitude)
  (about 30% at 40°N).

**Acceptance:** live warning polygons appear and expire correctly; an imported
third-party `.pal` renders identically to its reference screenshot.

---

### Phase 8 — Polish

- Multi-pane (1/2/4) with linked or independent pan
- Level III decoder — separate packet-based format, ~80 product types; start with
  storm tracks, hail index, and mesocyclone detection
- Settings persistence, keyboard shortcuts, per-monitor DPI awareness
- Single-file publish, self-contained, ReadyToRun

---

## 5. Cross-cutting requirements

**Threading.** UI thread does UI. Render thread owns the D3D device context. Ingest
and decode run on the thread pool. Cross boundaries only via channels and immutable
records. `Sweep` is immutable by design — keep it that way (note the arrays inside
it are not enforced immutable; treat them as frozen after construction).

**Memory.** Rent large float arrays from `ArrayPool<float>.Shared` and return them
after GPU upload. A 30-frame loop of super-res data is on the order of a gigabyte if
handled naively; keep decoded frames as GPU textures and drop the CPU arrays.

**Errors.** Every network operation gets a timeout, retry with exponential backoff,
and a user-visible failure state. A stale radar image with no indication that the
feed died is genuinely dangerous in this application. Show the age of the displayed
data prominently and color it when it exceeds ~10 minutes.

**Logging.** Serilog to a rolling file. Log every S3 request with latency, every
decode failure with the offending key.

**Testing.** Golden-file tests for the decoder are the highest-value tests in the
project — every parser change reruns them. Geo math gets analytic tests. Do not
attempt UI automation; not worth it here.

---

## 6. Reference material

- **ICD 2620010H** — `https://www.roc.noaa.gov/wsr88d/PublicDocs/ICDs/` — Archive II
  format, Section 7. The authoritative source. Read it, don't guess.
- **Supercell Wx** — `github.com/dpaulat/supercell-wx` — MIT, C++/Qt. Closest prior
  art; reference for chunk assembly and GPU radial rendering.
- **Py-ART / MetPy / wradlib** — Python decoders, useful for cross-checking your
  output values against a known-good implementation.
- **nexrad-data (Rust crate)** — clean model of the archive/realtime split.

---

## 7. Working agreement for this build

- Work one phase at a time. State which phase you're on before writing code.
- Write the acceptance test for a phase before or alongside the implementation.
- When the ICD and an existing implementation disagree, follow the ICD and note the
  discrepancy in a comment.
- Do not add features from later phases opportunistically. Phase 6 is hard enough
  without Phase 8 half-present.
- If a design decision from §0 turns out to be wrong, stop and say so rather than
  silently working around it.
