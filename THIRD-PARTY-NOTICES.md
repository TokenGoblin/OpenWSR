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
- **Basemap tiles** — © OpenStreetMap contributors (ODbL); or MapTiler when configured
  with a user-supplied key. Tile imagery is subject to the provider's terms.

## Prior art

[Supercell Wx](https://github.com/dpaulat/supercell-wx) (MIT, © Dan Paulat) served as
algorithmic prior art for chunk assembly and GPU radial rendering. No source code from
Supercell Wx has been ported into this project; should any be ported in the future, its
MIT notice must be reproduced here.
