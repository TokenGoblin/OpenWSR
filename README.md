# OpenWSR

An open-source native Windows NEXRAD Level II radar viewer. .NET 10 / WPF shell,
Direct3D 11 rendering (Vortice), own Web Mercator tile engine — radar composites in
the same GPU scene as the basemap, in its native polar geometry.

> **Not for life-safety decisions.** Use official National Weather Service products
> and local warning systems for protective action.

## Features

- **Level II decoder** — Archive II (ICD 2620010H §7): Message 31 all moments
  (REF/VEL/SW/ZDR/PHI/RHO/CFP), 8/16-bit words, physical units, SAILS-aware sweep
  assembly. Golden-file tests verified bit-exact against MetPy.
- **GPU radial rendering** — sweep geometry generated in the vertex shader from
  `SV_VertexID` (beam propagation + great-circle + Mercator, jitter-free at street
  zoom); product/palette switches are texture swaps (< 16 ms); range folding in NWS
  purple; 60 fps.
- **Archive browsing** — any site, any UTC day since 1991 from
  `unidata-nexrad-level2` (anonymous). Time slider, 30-frame loop at 2/4/8 fps,
  2 GB LRU disk cache.
- **Real-time** — chunk streaming from `unidata-nexrad-level2-chunks`: partial
  volumes render as each tilt completes (~seconds after the radar sweeps it),
  order-independent assembly, adaptive polling, data-age indicator.
- **Warnings** — live NWS polygons (tornado red / severe yellow / flash-flood
  green), click for full text.
- **Storm products** — Level III NST/NHI/NMD/NSS overlays: SCIT storm tracks with
  WunderMap-style projection cones (motion vector + ±15° swept area over the next
  hour), on-map cell labels with max dBZ, hail markers sized by severe-hail
  probability, mesocyclone circles at detected radius. Click any cell for details
  (motion, max dBZ / echo top / VIL, hail probabilities, meso, closest approach
  to home). Decoders verified against MetPy.
- **Layers panel** — radar opacity, radar smoothing toggle (soft consumer-style
  blending between cells vs raw gates), per-type warning filters (tornado /
  severe / flash flood / other), storm-layer filters (past/forecast track,
  projection cones, cell labels, hail, meso, minimum severe-hail probability).
- **Proximity alerts** — set a home location and radius; OpenWSR alerts (toast +
  sound) when a storm's forecast track will pass within range (with ETA and cell
  attributes) or when a warning polygon includes or nears your area.
- **Multi-pane** — 1/2/4 panes with linked or independent pan/zoom; secondary
  panes default to velocity / ZDR / RhoHV with per-pane product switching.
- **Tools** — hover inspector (value, azimuth, ranges, beam height), right-drag
  geodesic measuring, GR2Analyst `.pal` palette import.

## Build & run

```
dotnet build OpenWSR.slnx
dotnet run --project src/OpenWSR.App
```

Single-file publish: `dotnet publish src/OpenWSR.App -c Release` →
`src/OpenWSR.App/bin/Release/net10.0-windows/win-x64/publish/OpenWSR.exe`.

Settings live at `%LOCALAPPDATA%\OpenWSR\settings.json` (tile provider/key, contact
string for the NWS API User-Agent). Tiles, volumes, and logs cache under the same
directory.

Console harness: `dotnet run --project src/OpenWSR.Harness -- <archive-file>` dumps
per-sweep stats; `--soak <SITE> <minutes>` runs the live pipeline headless.

## Controls

| Input | Action |
|---|---|
| Drag / wheel | Pan (with inertia) / zoom at cursor |
| `R` `V` `W` `D` `P` `C` | Reflectivity, velocity, spectrum width, ZDR, PhiDP, RhoHV |
| `↑` / `↓` | Elevation tilt up/down |
| Hover | Inspector readout in the status bar |
| Right-drag | Distance/bearing measure |
| Click a storm marker | Cell details (motion, hail, meso, approach to home) |
| Click a warning | Detail popup |
| 📍 Set home on map | Arm proximity alerts at the chosen radius |

## Layout

```
src/OpenWSR.Nexrad     Level II decoder, live chunk assembler, Level III decoder (pure, no I/O)
src/OpenWSR.Geo        Beam propagation, geodesy, Web Mercator, tile math (pure)
src/OpenWSR.Render     D3D11 device, map/radar/overlay renderers, HwndHost
src/OpenWSR.Ingest     S3 archive/chunk clients, caches, NWS alerts, site table
src/OpenWSR.Palettes   Color tables + GR2Analyst .pal import
src/OpenWSR.App        WPF shell
src/OpenWSR.Harness    Console decoder harness + live soak
tests/                 Golden-file, replay, geo, palette, Level III, threat tests (52)
```

`OpenWSR-build-plan.md` is the phase-gated implementation brief; `TASKS.md` tracks
progress. Data licensing and component notices: `THIRD-PARTY-NOTICES.md`.
