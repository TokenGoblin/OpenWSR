# OpenWSR

[![build](https://github.com/TokenGoblin/OpenWSR/actions/workflows/ci.yml/badge.svg)](https://github.com/TokenGoblin/OpenWSR/actions/workflows/ci.yml)

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
- **Archive** — any site, any UTC day back to 1991. Time slider across the day, loops of
  12/30/60/144 volumes at 2/4/8 fps, 2 GB LRU disk cache.
- **Live** — real-time chunk streaming. Partial volumes render as each tilt completes,
  roughly five seconds after the radar sweeps it, with order-independent assembly and a
  prominent data-age indicator.
- **Smoothing** — a 0–100% slider from raw gates to a soft consumer-style display, using
  a sentinel-aware Gaussian that excludes no-data gates rather than smearing them in.
- **Vertical cross-section** — right-drag a line and get a true vertical slice through
  the volume, interpolated between elevation cuts.
- **Wind profile** — horizontal wind against height, fitted from the velocity field
  itself: ride a ring of constant range and the radial velocity traces a sine wave whose
  amplitude is the wind speed and whose phase is its direction. Drawn as station barbs.
- **Storm-relative velocity** — subtracts tracked storm motion so rotation stands out.
- **Velocity unfolding** — recovers velocities past the Nyquist limit, where a strong
  couplet otherwise reads with its sign reversed. Region-based, needing no sounding or
  previous volume, and cross-checked against Py-ART.
- **Azimuthal shear** — rotation as a number rather than a judgement call, computed from
  the unfolded velocity field. A uniform wind has no azimuthal gradient, so differentiating
  across the beam removes both the background flow and the storm's own translation.
- **Rotation tracks** — the strongest rotation seen at each point over the hour leading up
  to wherever you are in the timeline. A single scan says where rotation is; a swath says
  where it has been, which is the question a damage survey asks.
- **Terminal radars (TDWR)** — all 47, alongside the 163 WSR-88Ds in the same site list.
  C-band, a 0.55° beam against the WSR-88D's 0.95° and 150 m gates against 250 m, sited for
  airport approach paths. Over a metro area that is a different picture, not a slightly
  sharper one. Reflectivity and velocity at three tilts, from their Level III products —
  and azimuthal shear works on them too, because this app derives it rather than decoding
  it. See [how the radar types interact](docs/radar-sources.md).

**National context**

- **National mosaic** — seamless CONUS reflectivity, either from pre-rendered tiles or
  decoded natively from MRMS GRIB2. The tiles are published only to zoom 12 and are
  stretched above it; the native grid is 0.01° and stays sharp all the way in.
- **Satellite** — GOES-East cloud beneath the radar layers, decoded from ABI on the NOAA
  bucket rather than fetched as somebody's pre-rendered tiles. True colour where the sun is
  up and infrared where it is not, cross-faded across the terminator by solar zenith angle,
  with clear ground left transparent so the basemap still reads through it.
- **Future radar** — HRRR simulated reflectivity for the next six hours, decoded from
  GRIB2 and resampled from its Lambert grid into Mercator.
- **Warnings** — live NWS polygons, filtered by type, click for the full text.
- **SPC products** — Day 1 categorical outlook, mesoscale discussions, watch boxes.
- **Storm reports** — what actually happened on the ground, last six hours.
- **Lightning** — GOES-19 Lightning Mapper flashes from the last ten minutes, fading with
  age. The one layer that is not inferred: reflectivity says what the beam scattered off,
  but a flash is a discharge that actually happened.

**Storm analysis**

- **Level III algorithms** — SCIT storm tracks with forecast positions and projection
  cones, hail markers sized by severe-hail probability, mesocyclone detections, and
  per-cell max dBZ / VIL / echo top.
- **Hotspot finder** — one click scans all 163 WSR-88Ds and flies to the nearest real
  precipitation to your saved place; hold Shift for the heaviest in the country.
- **Hail size swath** — MRMS maximum estimated hail size over the last hour, banded at the
  sizes hail is actually reported in: 0.75 in severe, 1 in a quarter, 1.75 in a golf ball,
  2.75 in a baseball. Where hail fell, rather than where a cell happens to be now.
- **Hodograph** — the wind profile plotted as a curve, coloured by height with kilometre
  ticks, plus storm-relative helicity and bulk shear. Helicity is measured against the
  *observed* motion of a tracked cell where there is one, which is the advantage of doing
  this in a radar application: a sounding has to estimate where a storm would go.
- **Clutter suppression** — rotation products are masked twice, because noise and clutter
  are different problems. Weak echo goes first (shear computed where nothing reflected is
  the phase of receiver noise), then low correlation coefficient *but only under a weak
  echo* — because a tornadic debris signature is itself a low-CC target, and a bare CC
  threshold deletes the very thing the product exists to find.
- **Watched places** — a named list, each with its own alert radius. On its first run the
  app asks Windows where it is and saves that as Home, which is the whole setup step.
  OpenWSR then raises a tray notification when a storm's forecast track will pass within
  range — with an ETA and the cell's attributes — or when a warning polygon includes or
  nears one of them. A storm that comes close but misses is listed and named, not alarmed
  about; one that is leaving is not listed at all.

**Map and overlays**

- **Basemaps** — dark by default, because reflectivity is a bright saturated palette and
  against a near-black ground the weather is the only bright thing on screen. The dark style
  is OpenStreetMap's own tiles inverted as they load rather than a second tile service, so it
  cannot be taken away; the trade is that OSM bakes its place names in, so they sit under the
  weather. OpenStreetMap as published, USGS
  topographic and MapTiler are also selectable.
- **Political boundaries** — US Census state and county outlines as a layer you control,
  rather than whatever the basemap happens to bake in. A county line is how a warning is
  described, so it needs to be something you can turn up. Downloaded once and cached.
- **Bring your own geometry** — import GeoJSON or ESRI shapefiles, zipped or not, as
  toggleable overlays.
- **Reflectivity window** — a min/max dBZ filter, opening at 20 dBZ, which is about
  0.026 in/hr: light rain reaching the ground. Below it is mostly insects, birds and dust.
  Drop it for snow, which returns far less energy for the same water.

**Working with it**

- **Placefiles** — the GRLevelX community overlay format, by URL or file, with each
  file's own refresh interval and per-item zoom thresholds honoured.
- **Palettes** — import GR2Analyst `.pal` colour tables.
- **Multi-pane** — 1/2/4 panes with linked or independent pan. A pane either follows
  the main one (same volume, different product) or is pinned to a radar of its own,
  which turns the split from a product comparison into a place comparison.
- **Tools** — hover inspector (value, azimuth, ranges, beam height), geodesic
  distance/bearing measuring, colour scale on the map.
- **Drawing** — freehand lines, areas, circles of a true ground radius, and labels, saved
  out as placefiles so they can be shared with other GRLevelX-compatible software.
- **Export** — PNG stills and animated GIFs of the loop, carrying every layer on screen.
- **It remembers** — layers, filters, opacities, which panel sections you left open, your
  places and their radii. Not which site or product you were last looking at: that is where
  you happened to be looking, not how you like the application set up.
- **Search** — city, ZIP code or lat/lon, resolving to the nearest radar.

---

## Install

Download the `.msi` from [Releases](https://github.com/TokenGoblin/OpenWSR/releases) and run
it. It installs per-user under `%LocalAppData%\Programs\OpenWSR` with no administrator
prompt, and adds a Start menu entry. Uninstall from Settings → Apps; your saved places,
settings and cached tiles are left alone.

The installer is **unsigned**, so SmartScreen shows "Windows protected your PC" on first run
— *More info → Run anyway*. That is about the absence of a code-signing certificate, not
about anything found in the file.

Requires 64-bit Windows 10 1809 or later. Nothing else: the .NET runtime is bundled, which
is most of the download.

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

Then set a home location — **Settings → My area → 📍 Pick on map** — to arm proximity
alerts. With a home set, OpenWSR opens on its nearest radar streaming live and watches
that site for storm tracks; without one it opens on the national view. Either way,
**🎯** jumps straight to the heaviest weather in the country.

The shortcuts card (**?** in the rail, or **F1**) shows once on first run.

### Console harness

```
dotnet run --project src/OpenWSR.Harness -- <archive-file>          # per-sweep stats
dotnet run --project src/OpenWSR.Harness -- --soak KTLX 30          # headless live soak
dotnet run --project src/OpenWSR.Harness -- --export-sweep <f> out.js
```

---

## Built-in guide

Press **?** in the left rail, or **F1**, for *How OpenWSR works* — what each feature does and,
in particular, how the storm alarm decides what is worth interrupting you for: the two radii,
the direct/glancing/receding tiering, what always interrupts regardless, and what the
APPROACHING panel's headings mean. It is also what a first run opens on. The keyboard card and
the storm symbol key are one click from there.

## Controls

The window is arranged around the four questions you ask it, each with one place:

| Where | Top bar | Search a place, pick a site, jump to the nearest — a readout of the ground under the map centre, lit up when it is one of your saved places, and `Recenter` to put the map and the radar back on your place |
|---|---|---|
| **What** | Above the map | `REF VEL SW ZDR PHI CC AZS` and the elevation tilt |
| **When** | Below the map | `LIVE · ARCHIVE · FORECAST`, and that mode's transport |
| **What's on top** | Right panel | Layers, each with its own opacity |

| Input | Action |
|---|---|
| Drag / wheel | Pan (with inertia) / zoom at cursor |
| `R` `V` `W` `D` `P` `C` `A` | Reflectivity, velocity, spectrum width, ZDR, PhiDP, RhoHV, azimuthal shear |
| `↑` / `↓` | Elevation tilt up and down |
| `F1` | Keyboard and mouse reference |
| Hover | Inspector readout in the status bar |
| Right-drag | Whichever map tool is armed in the rail — measure, or cut a cross-section |
| Click a storm cell | Details: motion, max dBZ, hail, mesocyclone, approach to home |
| Click a warning | Full warning text |

The product buttons carry their own shortcut letters, so the keyboard is the buttons.

Left rail, top to bottom: layers panel, hotspot finder, pane layout, pane linking — then
the map tools (inspect, measure, cross-section, set home) as one armed mode at a time —
then capture and palette import, with settings, help and about at the bottom.

---

## Data sources

Everything below is public and unauthenticated.

| Source | What it provides |
|---|---|
| `unidata-nexrad-level2` (AWS) | Level II archive, 1991→now |
| `unidata-nexrad-level2-chunks` | Level II real-time chunks |
| `unidata-nexrad-level3` | Level III products (NST, NHI, NMD, NSS, DVL) and the TDWR terminal radars (TZ0–2, TV0–2) |
| `noaa-hrrr-bdp-pds` (AWS) | HRRR model output for future radar |
| `noaa-mrms-pds` (AWS) | MRMS national composite and MESH hail size, decoded natively from GRIB2 |
| `noaa-goes19` (AWS) | GOES-East ABI cloud imagery and GLM lightning |
| `api.weather.gov` | Active warnings |
| `spc.noaa.gov` | Day 1 convective outlooks |
| Iowa Environmental Mesonet | National radar mosaic and GOES tiles, SPC watches and discussions, storm reports |
| US Census (cartographic boundary files) | State and county outlines, 1:500,000 |
| USGS The National Map | Topographic basemap tiles |
| OpenStreetMap (MapTiler optional, with your own key) | Basemap tiles — dark by default, so the radar palette is the only bright thing on screen |
| NCEI HOMR | Radar site table (embedded) |

NEXRAD data carries no use restrictions. Basemap and tile imagery are subject to their
providers' terms — see `THIRD-PARTY-NOTICES.md`.

---

## Layout

```
src/OpenWSR.Nexrad      Level II decoder, live chunk assembler, Level III, cross-section
src/OpenWSR.Grib2       GRIB2 edition-2 reader (MRMS, HRRR)
src/OpenWSR.NetCdf      Minimal HDF5/NetCDF-4 reader (GOES ABI imagery, GLM lightning)
src/OpenWSR.Geo         Beam propagation, geodesy, Mercator, Lambert, geostationary, solar, tile math
src/OpenWSR.Placefiles  GRLevelX placefile parser
src/OpenWSR.Palettes    Colour tables and GR2Analyst .pal import
src/OpenWSR.Render      D3D11 device, map/radar/overlay renderers, HwndHost
src/OpenWSR.Ingest      S3 clients, caches, alerts, geocoding, site table
src/OpenWSR.App         WPF shell
src/OpenWSR.Harness     Console decoder harness and live soak
tests/                  513 tests
docs/                   Format notes, verification, endpoints, parity, screenshots
resources/              Cross-check scripts, sample placefiles, reference tables
```

`Nexrad`, `Geo`, `Grib2`, `NetCdf` and `Placefiles` are pure: no WPF, no Direct3D, no
network. A test enforces it.

---

## Documentation

| | |
|---|---|
| [docs/](docs/) | Index, plus screenshots of every major capability |
| [docs/radar-sources.md](docs/radar-sources.md) | **What each feed is, how WSR-88D and TDWR differ and when to use which, and which layers are exclusive with which** |
| [docs/formats.md](docs/formats.md) | Binary format field notes — the parts that cost real debugging time |
| [docs/verification.md](docs/verification.md) | How the decoders are golden-tested, and how to run a cross-check |
| [docs/data-sources.md](docs/data-sources.md) | Every endpoint, the specs behind them, and which have already moved |
| [docs/parity.md](docs/parity.md) | Competitive gap analysis, annotated with what's since been closed |
| [CLAUDE.md](CLAUDE.md) | Working brief: architecture, threading rules, hard-won gotchas |
| [TASKS.md](TASKS.md) | Phase-by-phase build log |

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

- **Windows only, and US only.** The shell is WPF and Direct3D; the data sources are the
  US radar network and NOAA's public buckets.
- **Storm-structure attributes are mostly empty on live data.** NSS stopped being
  distributed around 2021, so max dBZ, cell-based VIL and echo top come back null on
  anything current. The decoder still works on archives. The layers panel says so when it
  detects it — it is not a bug to be fixed here.
- **Velocity dealiasing reaches 85–92 % of Py-ART's correction rate.** The gap is genuine
  judgement-call difference on marginal folds, and two plausible fixes were measured and
  rejected — see `docs/verification.md`.
- **Placefile icon sheets** are fetched and drawn, but `Triangles` and `Image` blocks are
  skipped and reported.
- **Ground clutter at coastal sites** is suppressed on rotation products but not on plain
  reflectivity, where it is left visible rather than guessed at.
- **Tested on one machine.** One Windows 11 install, one GPU — at both 100 % and 150 %
  display scaling, but still one machine. CI builds and tests on hosted Windows and Linux
  runners, so a clean checkout is known to compile and pass; the rendering half has only
  ever been *run* here.

`OpenWSR-build-plan.md` is the original phase-gated brief; `TASKS.md` tracks everything
built since, including the competitive parity work.

## Licence

MIT — see [LICENSE](LICENSE). The data it reads is public and unrestricted; the third-party
components and data sources, with their own terms and required attributions, are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
