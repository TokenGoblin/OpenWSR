# Third-party notices

OpenWSR uses the following open-source components:

| Component | License | Use |
|---|---|---|
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) | MIT | Direct3D 11 / DXGI / D3DCompiler bindings |
| [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) | MIT | bzip2 decompression of LDM records |
| [AWS SDK for .NET](https://github.com/aws/aws-sdk-net) (AWSSDK.S3) | Apache-2.0 | Anonymous S3 access to NOAA Open Data |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | MVVM plumbing |
| [Serilog](https://github.com/serilog/serilog) + Serilog.Sinks.File | Apache-2.0 | Logging |

## Data sources

- **NEXRAD Level II data** — NOAA Big Data Program via AWS Open Data
  (`unidata-nexrad-level2`, `unidata-nexrad-level2-chunks`, maintained by NSF Unidata).
  NEXRAD data carries no use restrictions.
- **Warnings** — NWS API (`api.weather.gov`), public domain.
- **Radar site table** — NCEI Historical Observing Metadata Repository (HOMR), public domain.
- **Basemap tiles** — © OpenStreetMap contributors (ODbL), from `tile.openstreetmap.org`,
  and attributed as such in the app. Both the light and the dark style are those same tiles;
  the dark one is derived locally by `TileToning` rather than fetched, so it introduces no
  further party. MapTiler is used only when configured with a user-supplied key. Tile imagery
  is subject to the provider's terms, and OSM's tile usage policy asks that applications send
  an identifying User-Agent and not bulk-download — this one sends `OpenWSR/0.1` with the
  project URL and fetches only tiles being viewed. CARTO's "Dark Matter" was the default
  through 0.1.0 and is no longer used.
- **Terrain basemap** — USGS topographic maps from The National Map
  (`basemap.nationalmap.gov`), a work of the US government and in the public domain.
- **Political boundaries** — US Census Bureau cartographic boundary files (2023, 1:500,000),
  public domain. Downloaded once on demand and cached locally.
- **Hail size** — NOAA MRMS MESH via AWS Open Data (`noaa-mrms-pds`), public domain.

## Prior art

[Supercell Wx](https://github.com/dpaulat/supercell-wx) (MIT, © Dan Paulat) served as
algorithmic prior art for chunk assembly and GPU radial rendering. No source code from
Supercell Wx has been ported into this project; should any be ported in the future, its
MIT notice must be reproduced here.
