# Data sources and endpoints

Every source OpenWSR reads is public and unauthenticated. This file records the exact
endpoints, the specifications behind them, and — importantly — **which endpoints have
already moved once**, so a future outage can be diagnosed as drift rather than a bug.

---

## AWS Open Data (anonymous S3, no credentials)

| Bucket | Contents | Notes |
|---|---|---|
| `unidata-nexrad-level2` | Level II archive, 1991 → present | Key: `{yyyy}/{MM}/{dd}/{SITE}/{SITE}{yyyyMMdd}_{HHmmss}_V06` |
| `unidata-nexrad-level2-chunks` | Level II real-time chunks | **Short retention.** Keys age out in hours |
| `unidata-nexrad-level3` | Level III products | Key: `{SITE3}_{PROD}_{yyyy}_{MM}_{dd}_{HH}_{mm}_{ss}` |
| `noaa-hrrr-bdp-pds` | HRRR model output | Use the `.idx` sidecar for byte-range fetches |
| `noaa-mrms-pds` | MRMS composites | GRIB2, PNG-packed, gzipped |

The chunks bucket's retention is why `assets/testdata/chunks/` is committed to git: the
58-chunk corpus cannot be re-fetched once it ages out. `tools/fetch-testdata.ps1` notes
this inline. **Do not delete those files.**

## NWS and SPC

| Endpoint | Provides |
|---|---|
| `api.weather.gov/alerts/active` | Active warning polygons |
| `spc.noaa.gov/products/outlook/day1otlk_cat.nolyr.geojson` | Day 1 categorical outlook |

`api.weather.gov` asks every client to identify itself via User-Agent and may block those
that do not. OpenWSR sends a contact address set in Settings; the Settings window shows a
live preview of the exact User-Agent string being sent. The address goes only to
weather.gov.

SPC GeoJSON carries a UTF-8 BOM that `System.Text.Json` rejects — see
[formats.md](formats.md).

## Iowa Environmental Mesonet

Pre-rendered tiles and normalised GeoJSON. This is what let the national mosaic and
satellite layers ship in hours instead of behind a full GRIB2/NetCDF pipeline.

| Endpoint | Provides |
|---|---|
| `mesonet.agron.iastate.edu/cache/tile.py/1.0.0/n0q-{ts}/{z}/{x}/{y}.png` | National reflectivity mosaic (XYZ) |
| `mesonet.agron.iastate.edu/.../goes-ir/{z}/{x}/{y}.png` | GOES-East infrared |
| `mesonet.agron.iastate.edu/api/1/spc_watch_outline.geojson` | SPC watch boxes |
| `mesonet.agron.iastate.edu/api/1/nws/spc_mcd.geojson` | Mesoscale discussions |
| `mesonet.agron.iastate.edu/geojson/lsr.geojson` | Local storm reports |
| `mesonet.agron.iastate.edu/request/grx/asos.php` | Sample placefile (ASOS observations) |
| `mesonet.agron.iastate.edu/request/grx/time_mot_loc.txt?all` | Sample placefile (storm motion) |

### Endpoint drift already encountered

IEM's older GeoJSON paths **silently return HTML** rather than 404ing, which parses as a
JSON error rather than an obvious network failure:

| Retired | Replacement |
|---|---|
| `/geojson/spcwatch.py` | `/api/1/spc_watch_outline.geojson` |
| `/geojson/mcd.geojson` | `/api/1/nws/spc_mcd.geojson` |

When an IEM feed starts failing, check the current path in
`mesonet.agron.iastate.edu/api/1/openapi.json` before debugging the parser.

## Basemap and geocoding

| Endpoint | Provides |
|---|---|
| OpenStreetMap / MapTiler XYZ tiles | Basemap |
| `nominatim.openstreetmap.org/search` | City / ZIP geocoding |

Nominatim requires a descriptive User-Agent and rate-limits aggressively. Tile and
basemap imagery are subject to their providers' terms — see `THIRD-PARTY-NOTICES.md`.
NEXRAD data itself carries no use restrictions.

---

## Format specifications

| Spec | Where |
|---|---|
| NEXRAD Level II ICD | NOAA ICD 2620010H, "Archive II/User" |
| NEXRAD Level III ICD | NOAA ICD 2620001, "RPG to Class 1 User" |
| RPG Operator's Guide | NOAA — useful for product code meanings |
| GRIB2 | WMO FM-92 GRIB edition 2 |
| GRLevelX placefiles | `grlevelx.com/manuals/gis/files_places.htm` |
| GR colour tables | `grlevelx.com/manuals/color_tables/index.htm` |

The GRLevelX manual pages reject non-browser User-Agents with a 403 — `WebFetch` fails on
them. `resources/crosscheck/` has no scraper for this; fetch with a browser User-Agent if
you need the pages again.

## Reference data

[`resources/reference/nexrad-stations.txt`](../resources/reference/nexrad-stations.txt) —
the NCEI HOMR station table (all 163 WSR-88D sites with coordinates and elevations). The
shipping site table is embedded in `OpenWSR.Ingest`; this is the source it came from.
