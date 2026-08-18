# OpenWSR

An open-source native Windows NEXRAD radar viewer. .NET 10 / WPF shell, Direct3D 11
rendering via Vortice, and its own Web Mercator tile engine — so radar composites into
the same GPU scene as the basemap, drawn in its native polar geometry rather than as a
pre-rendered image.

No account, no API key, no subscription. Everything it reads is public data.

> **Not for life-safety decisions.** Use official National Weather Service products and
> local warning systems for protective action.

---

## What it does

**Radar**

- **Level II decoder** — Archive II per ICD 2620010H §7: Message 31 with every moment
  (REF, VEL, SW, ZDR, PHI, RHO, CFP), 8- and 16-bit words, values in physical units,
  SAILS/MRLE-aware sweep assembly. Verified value-for-value against MetPy.
- **GPU radial rendering** — sweep geometry is generated in the vertex shader from
  `SV_VertexID`; beam propagation, great-circle offset and the Mercator projection all
  happen on the GPU relative to the radar origin, so it stays precise at street zoom.
  Switching product or palette is a texture swap (measured 0.7–11 ms). 60 fps.
- **Archive** — any site, any UTC day back to 1991. Time slider across the day, 30-frame
  loop at 2/4/8 fps, 2 GB LRU disk cache.
- **Live** — real-time chunk streaming. Partial volumes render as each tilt completes,
  roughly five seconds after the radar sweeps it, with order-independent assembly and a
  prominent data-age indicator.
- **Smoothing** — a 0–100% slider from raw gates to a soft consumer-style display, using
  a sentinel-aware Gaussian that excludes no-data gates rather than smearing them in.
- **Vertical cross-section** — right-drag a line and get a true vertical slice through
  the volume, interpolated between elevation cuts.
- **Storm-relative velocity** — subtracts tracked storm motion so rotation stands out.

**National context**

- **National mosaic** — seamless CONUS reflectivity.
- **Satellite** — GOES-East infrared beneath the radar layers.
- **Future radar** — HRRR simulated reflectivity for the next six hours, decoded from
  GRIB2 and resampled from its Lambert grid into Mercator.
- **Warnings** — live NWS polygons, filtered by type, click for the full text.
- **SPC products** — Day 1 categorical outlook, mesoscale discussions, watch boxes.
- **Storm reports** — what actually happened on the ground, last six hours.

**Storm analysis**

- **Level III algorithms** — SCIT storm tracks with forecast positions and projection
  cones, hail markers sized by severe-hail probability, mesocyclone detections, and
  per-cell max dBZ / VIL / echo top.
- **Hotspot finder** — one click scans all 163 WSR-88Ds and flies to the heaviest
  precipitation in the country.
- **Proximity alerts** — set a home location and radius; OpenWSR raises a tray
  notification when a storm's forecast track will pass within range (with an ETA and the
  cell's attributes) or when a warning polygon includes or nears your area.

**Working with it**

- **Placefiles** — the GRLevelX community overlay format, by URL or file, with each
  file's own refresh interval and per-item zoom thresholds honoured.
- **Palettes** — import GR2Analyst `.pal` colour tables.
- **Multi-pane** — 1/2/4 panes with linked or independent pan.
- **Tools** — hover inspector (value, azimuth, ranges, beam height), geodesic
  distance/bearing measuring, colour scale on the map.
- **Export** — PNG stills and animated GIFs of the loop.
- **Search** — city, ZIP code or lat/lon, resolving to the nearest radar.

---

## Build and run

```
dotnet build OpenWSR.slnx
dotnet run --project src/OpenWSR.App
```

Single-file publish:

```
dotnet publish src/OpenWSR.App -c Release
```

→ `src/OpenWSR.App/bin/Release/net10.0-windows/win-x64/publish/OpenWSR.exe`
(self-contained, ReadyToRun, ~160 MB).

Requires the .NET 10 SDK and a Direct3D 11 capable GPU.

### First run

Open **Settings** (⚙, bottom of the left rail) and set a **contact address**. The
National Weather Service API asks every client to identify itself and may block those
that don't; the address is sent only to weather.gov. While you're there, pick units and
a basemap.

Then set a home location with **📍 Set home on map** in the layers panel to arm
proximity alerts, or press **🎯** to jump straight to the heaviest weather in the
country.

### Console harness

```
dotnet run --project src/OpenWSR.Harness -- <archive-file>          # per-sweep stats
dotnet run --project src/OpenWSR.Harness -- --soak KTLX 30          # headless live soak
dotnet run --project src/OpenWSR.Harness -- --export-sweep <f> out.js
```

---

## Controls

| Input | Action |
|---|---|
| Drag / wheel | Pan (with inertia) / zoom at cursor |
| `R` `V` `W` `D` `P` `C` | Reflectivity, velocity, spectrum width, ZDR, PhiDP, RhoHV |
| `↑` / `↓` | Elevation tilt up and down |
| Hover | Inspector readout in the status bar |
| Right-drag | Measure distance and bearing — or cut a cross-section when ⌇ is on |
| Click a storm cell | Details: motion, max dBZ, hail, mesocyclone, approach to home |
| Click a warning | Full warning text |

Left rail, top to bottom: layers panel, hotspot finder, pane layout, pane linking,
cross-section mode, capture, palette import — then settings and about at the bottom.

---

## Data sources

Everything below is public and unauthenticated.

| Source | What it provides |
|---|---|
| `unidata-nexrad-level2` (AWS) | Level II archive, 1991→now |
| `unidata-nexrad-level2-chunks` | Level II real-time chunks |
| `unidata-nexrad-level3` | Level III products (NST, NHI, NMD, NSS, DVL) |
| `noaa-hrrr-bdp-pds` (AWS) | HRRR model output for future radar |
| `api.weather.gov` | Active warnings |
| `spc.noaa.gov` | Day 1 convective outlooks |
| Iowa Environmental Mesonet | National radar mosaic and GOES tiles, SPC watches and discussions, storm reports |
| OpenStreetMap / MapTiler | Basemap tiles |
| NCEI HOMR | Radar site table (embedded) |

NEXRAD data carries no use restrictions. Basemap and tile imagery are subject to their
providers' terms — see `THIRD-PARTY-NOTICES.md`.

---

## Layout

```
src/OpenWSR.Nexrad      Level II decoder, live chunk assembler, Level III, cross-section
src/OpenWSR.Grib2       GRIB2 edition-2 reader (MRMS, HRRR)
src/OpenWSR.Geo         Beam propagation, geodesy, Mercator, Lambert, tile math
src/OpenWSR.Placefiles  GRLevelX placefile parser
src/OpenWSR.Palettes    Colour tables and GR2Analyst .pal import
src/OpenWSR.Render      D3D11 device, map/radar/overlay renderers, HwndHost
src/OpenWSR.Ingest      S3 clients, caches, alerts, geocoding, site table
src/OpenWSR.App         WPF shell
src/OpenWSR.Harness     Console decoder harness and live soak
tests/                  76 tests
```

`Nexrad`, `Geo`, `Grib2` and `Placefiles` are pure: no WPF, no Direct3D, no network. A
test enforces it.

---

## How it's verified

Binary format work is checked against an independent reference implementation rather
than against its own output:

- **Level II** — golden files asserted against **MetPy**.
- **GRIB2** — MRMS and HRRR fields asserted against **ecCodes** (ECMWF's reference
  decoder), covering both PNG packing and complex packing with spatial differencing.
- **Lambert projection** — grid points matched to ecCodes' own iterator, agreeing to
  about 11 m on a 3 km grid.
- **Level III** — NST/NHI/NMD/NSS/DVL asserted against MetPy.
- **Chunk assembly** — replaying the committed real-time corpus in randomised order
  reproduces the archive volume for the same scan, float-for-float.
- **Cross-section** — no external reference exists, so the tests assert physics: nothing
  in the cone of silence overhead, low-altitude coverage thinning with range.

`assets/testdata/` carries the committed fixtures (~40 MB), including a complete 58-chunk
real-time corpus spanning a volume boundary and its archive ground truth.
`tools/fetch-testdata.ps1` re-downloads them.

---

## Known limitations

- Placefile icon *sheets* are not downloaded; `Icon` statements draw as markers.
  `Triangles` and `Image` blocks are skipped and reported.
- MRMS decodes natively and is golden-tested, but is not yet drawn — the tile mosaic
  covers the visual.
- No lightning, velocity dealiasing, azimuthal shear, VWP panel, or 3D volume rendering.
- Velocity is displayed as received, so it folds beyond the Nyquist limit.
- Animated GIF export is implemented but has not been exercised end-to-end.

`OpenWSR-build-plan.md` is the original phase-gated brief; `TASKS.md` tracks everything
built since, including the competitive parity work.
