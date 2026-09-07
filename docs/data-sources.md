# Data sources and endpoints

Every source OpenWSR reads by default is public and unauthenticated, and the app is complete
without a credential of any kind. Two are opt-in and take the user's own key — MapTiler for an
alternative basemap, Weather Underground for personal weather stations — and both are marked as
such below. This file records the exact endpoints, the specifications behind them, and —
importantly — **which endpoints have already moved once**, so a future outage can be diagnosed
as drift rather than a bug.

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
| `api.weather.gov/points/{lat},{lon}` | Grid cell, forecast office, town name, station list URL |
| `api.weather.gov/gridpoints/{office}/{x},{y}/forecast` | 14 half-day periods — seven days, day and night |
| `api.weather.gov/gridpoints/{office}/{x},{y}/stations` | Surface stations near that cell, GeoJSON |
| `api.weather.gov/stations/{id}/observations/latest` | Current conditions from one station |
| `spc.noaa.gov/products/outlook/day1otlk_cat.nolyr.geojson` | Day 1 categorical outlook |

`api.weather.gov` asks every client to identify itself via User-Agent and may block those
that do not. OpenWSR sends a contact address set in Settings; the Settings window shows a
live preview of the exact User-Agent string being sent. The address goes only to
weather.gov.

SPC GeoJSON carries a UTF-8 BOM that `System.Text.Json` rejects — see
[formats.md](formats.md).

### The forecast chain costs three requests, and the first one is cacheable

`/points` is a grid lookup that does not change, so `ForecastClient` caches it for the
session; the forecast and the station list come from the URLs it returns rather than from
paths built by hand, because the office and grid indices are its answer to give. Temperatures
arrive in **Fahrenheit** from every US office (`temperatureUnit`), and are normalised to
Celsius on the way in so `Units` decides how they read.

The **icon URL is the only machine-readable condition** in the whole response —
`shortForecast` is a sentence written for a person. `.../icons/land/night/tsra_sct,50/tsra_sct,30`
carries the day/night flag, the condition token, and a rain chance, and for a split period a
second condition after it. The leading token is the one that describes the period.

Observation fields are **null far more often than not**, and NWS sends the measurement object
anyway with `"value": null` and `qualityControl: "Z"` — so the presence of the property proves
nothing. A station reporting temperature and nothing else is ordinary. So is a station
reporting nothing at all: `observations/latest` answers **404**, which is a state of the world
here rather than a failure, so the client walks out to the sixth-nearest station before giving
up.

### Hyperlocal stations: no keyless path, so it is an opt-in key

The obvious wish — read the home weather station down the street rather than the airport
sixteen miles away — was investigated properly rather than assumed. Every candidate, and why
it is not wired up:

| Source | Result |
|---|---|
| **Weather Underground** `api.weather.com/v2/pws/...` | **401 `CDN-0004 Missing apiKey`.** There is no keyless tier. Free keys go only to "registered and active" PWS users — you must be running a station and uploading to it — so this is a per-user credential, not a public endpoint |
| **aprs.fi** | API key, per user |
| **Synoptic Data / MesoWest** | Free tier, but a registration token |
| **findu.com** (CWOP mirror) | TLS handshake fails outright (curl exit 35) |
| **IEM** | No CWOP/APRSWXNET network among its 600 — checked `api/1/networks.json` |
| **MADIS** `LDAD/mesonet/netCDF/` | Genuinely keyless and *does* carry CWOP. Also **33 MB gzipped per hourly file**, nationwide, published about an hour in arrears. Fetching 33 MB an hour to read one station, an hour late, is not current conditions |

What ships instead is the honest keyless answer: `gridpoints/.../stations` is **not** limited
to airports — NWS carries RWIS and other mesonet feeds in the same list, and one of those is
often much nearer than the ASOS. The page sorts them by our own geodesy, picks the nearest one
that is actually reporting, and **names the station and its distance on screen**, so "75° at a
station 16 miles southeast" is never mistaken for "75° in the garden".

### The Weather Underground key path

Since there is no keyless route, the user's own key is the route — the same contract as
`MapTilerKey`, which set the precedent: optional, off by default, entered in Settings, and the
app is complete without it. `AppSettings.WeatherUndergroundKey`, `PwsClient`.

**Who can get one:** Weather Underground's own wording is that free keys are issued only to
"registered and active" PWS users — running a station *and* uploading to it. Registering a
device you do not own is a known community workaround; it uploads nothing, so the station is not
active, and WU has publicly discussed turning off PWS-associated keys. Do not design around it.
**Keys also expire and must be regenerated** at `wunderground.com/member/api-keys`, which is why
the rejection message leads with that rather than with "check for a typo".

| Endpoint | Provides |
|---|---|
| `api.weather.com/v3/location/near?geocode={lat},{lon}&product=pws` | Personal stations near a point |
| `api.weather.com/v2/pws/observations/current?stationId={id}&units=m` | One station's current reading |

Things worth knowing before touching it:

- **The location service answers in parallel arrays**, one per field indexed together
  (`stationId[]`, `latitude[]`, `qcStatus[]`, …), not an array of objects. A ragged response is
  not an error — read by index against the shortest array that matters.
- **Ask for `units=m`.** Celsius, km/h, hectopascals, millimetres — which is what the rest of
  the app stores, so no conversion leaks into the parser. Pressure still needs ×100 to reach
  the pascals api.weather.gov sends.
- **The readings are split across two levels.** Temperature, dew point, wind speed and pressure
  are inside `metric`; wind direction and humidity sit at the top level beside the station's
  identity.
- **`heatIndex` and `windChill` restate the air temperature when neither applies**, rather than
  being omitted, so a naive read gives every mild day a "feels like" that says nothing.
  `PwsClient` drops one within half a degree of the air temperature.
- **`qcStatus` is 1 passed / 0 not checked / −1 failed.** Not-checked is not passed; only an
  explicit failure keeps a station out of the automatic pick.
- **Auth failures distinguish themselves in the body**: `CDN-0004 Missing apiKey` against
  `CDN-0001 Invalid apiKey`, both under HTTP 401. One is a bug in this app; the other is almost
  never a typo, because **these keys expire** — the ordinary case is a key that worked for
  months and has lapsed. The message names regeneration first and points at
  `wunderground.com/member/api-keys` (*not* `/member/devices`, which is where the stations are).
- **A rejected key must cost the personal stations and nothing else.** The forecast page catches
  `StationAuthException` around the merge rather than around the whole load, so an expired key
  leaves the NWS forecast and observation on screen with a note above them. Caught any wider it
  blanks the page, which is what it did before this was noticed.
- **The key must never reach a log line or an exception message.** It rides in the query string,
  so anything that formats the URL formats the credential — `PwsClient` throws `StationAuthException`
  with wording it owns and logs status codes rather than URLs.
- **A personal station has no observer and no ceilometer**, so it reports numbers and never a
  sky or a visibility. The forecast page borrows the forecast's own wording for the description
  and leaves visibility absent rather than zero.

### Ambient Weather: your own station, and a different question entirely

`PwsClient` asks "what stations are near this point". **Ambient has no geolocation endpoint at
all** — its API answers "what stations does this account own" — so `AmbientClient` reads *your*
station rather than a neighbour's. The two are not alternatives and someone may want both.

| Endpoint | Provides |
|---|---|
| `rt.ambientweather.net/v1/devices?applicationKey={app}&apiKey={user}` | Every device on the account, each with its `lastData` |

- **One request does both jobs.** `lastData` comes back inside the device, so there is no
  separate observation call and nothing to walk.
- **Two keys, both the user's**, both self-serve at `ambientweather.net/account`: the *API key*
  grants access to that account's devices, the *application key* identifies the program and
  carries its own rate budget. The application key is deliberately **not** baked into this
  build — the repository is public, so a key committed here would be a key published here, and
  every copy of the app would then share one rate limit.
- **Everything is imperial and there is no way to ask otherwise** — `tempf`, `windspeedmph`,
  `baromrelin` (inHg), `hourlyrainin` — unlike Weather Underground's `units=m`. Converted on the
  way in, and `AmbientClientTests` tests the factors hardest, because a wrong one is a
  plausible-looking number rather than a crash.
- **`baromrelin` beats `baromabsin`.** Relative is the sea-level-corrected figure a weather
  report means; absolute is the raw sensor reading, and preferring it is wrong by however high
  the station sits — hundreds of hectopascals at altitude.
- **`dateutc` is milliseconds**, not seconds.
- **`feelsLike` restates the air temperature** whenever neither wind chill (< 50 °F) nor heat
  index (> 68 °F) applies — the same trap as Weather Underground's, handled the same way.
- **`info.coords.coords.lat/lon` is not always there.** The published REST example omits
  coordinates entirely while real accounts include them, so both shapes must work; a device that
  does not say where it is, is at the place being forecast for.
- **Rate limits:** 1 request/second per API key, 3/second per application key. The page refreshes
  every ten minutes, so this only matters if something else shares the key — hence 429 gets its
  own message saying exactly that.
- **Errors name the offending key** in the body: `{"error":"apiKey-missing"}`,
  `{"error":"applicationKey-invalid"}`. With two keys in play that distinction is the difference
  between useful advice and "something is wrong with one of your keys".

**The response shapes above are from Weather Underground's and Ambient's published
documentation, not from live captures** — a key requires contributing a station and there was none to test with. The
endpoints and their auth behaviour *were* verified live — Ambient answers 401
`{"error":"apiKey-missing"}` unkeyed and `{"error":"applicationKey-invalid"}` with junk keys.
`PwsClientTests` and `AmbientClientTests` both say this at the top: re-capture the fixtures
against a real account before trusting the field names.

## Iowa Environmental Mesonet

Pre-rendered tiles and normalised GeoJSON. This is what let the national mosaic and
satellite layers ship in hours instead of behind a full GRIB2/NetCDF pipeline.

| Endpoint | Provides |
|---|---|
| `mesonet.agron.iastate.edu/cache/tile.py/1.0.0/n0q-{ts}/{z}/{x}/{y}.png` | National reflectivity mosaic (XYZ) |
| `mesonet.agron.iastate.edu/cache/tile.py/1.0.0/goes_east_conus_ch13/{z}/{x}/{y}.png` | GOES-East infrared (channel 13) — **fallback only**, see ABI below |
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
| `/c/tile.py/1.0.0/goes_east_ch13/` | `/cache/tile.py/1.0.0/goes_east_conus_ch13/` |

When an IEM feed starts failing, check the current path in
`mesonet.agron.iastate.edu/api/1/openapi.json` before debugging the parser.

### The tile service fails as a picture, not as a status code

The GOES one is worth its own paragraph because a status check cannot catch it. An unknown
**layer name** on `tile.py` is answered with **HTTP 200 and a valid 256x256 PNG** reading
"Invalid TMS Request :( Need help? akrherz@iastate.edu". It decodes cleanly, so the fetch
path has no way to tell it from imagery: the map went solid red, and 253 copies of that one
image were written into the disk cache as though they were data.

**Verify a tile layer by fetching two different tiles and confirming the bytes differ.** A
404 means the *path* is wrong and is easy; a 200 means nothing at all here.

```
for t in 5/6/11 5/7/11; do curl -s -o - ".../$LAYER/$t.png" | md5sum; done
# identical hashes  -> placeholder, the layer name is wrong
```

An unknown *path prefix* does 404 properly — it is only the layer segment that behaves this
way. Note the cache directory is named for the layer, not for the provider, so correcting a
path abandons the tiles the old one wrote rather than serving them for ever.

## Basemap and geocoding

| Endpoint | Provides |
|---|---|
| `tile.openstreetmap.org` XYZ tiles | Basemap, light and dark |
| `basemap.nationalmap.gov` USGSTopo | Terrain basemap |
| `api.maptiler.com` (needs a free key) | Optional basemap |
| `nominatim.openstreetmap.org/search` | City / ZIP geocoding |

**CARTO is gone, and this is the shape of that failure.** `basemaps.cartocdn.com` served the
dark basemap free and keyless until August 2026, when it began requiring an API key and started
retiring its raster tiles. Unkeyed requests still return HTTP 200 and a valid PNG — one with
"API KEY REQUIRED" stamped across it — so nothing in the fetch path could tell it from imagery,
and because the tile cache never expires it was invisible on every machine that had run the app
before. A free key (5M tiles/month, no account) would have restored it, but the raster tiles are
being retired regardless, so the dark style is derived from OSM's own tiles instead. See
`TileToning`.

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


## GOES ABI imagery

The cloud layer, decoded from the source rather than from anyone's pre-rendered tiles. Same
bucket as GLM.

```
https://noaa-goes19.s3.amazonaws.com/ABI-L2-CMIPC/{yyyy}/{ddd}/{HH}/
    OR_ABI-L2-CMIPC-M6C{bb}_G19_s{start}_e{end}_c{created}.nc
```

`CMIPC` is the CONUS sector: five minutes, 2 km, about 4 MB for one band. `CMIPF` is full
disk at ten minutes and much larger, for ground this app never shows. The `M6` is the scan
mode and varies with the schedule, so a prefix filter must stop before it; the band is the
two digits after `C`.

| Band | What it is | Resolution |
|---|---|---|
| 13 | Clean longwave IR, 10.3 µm — cloud tops day and night | 2 km |
| 2 | Red visible, 0.64 µm — daylight only, four times the detail | 0.5 km |
| 1, 3 | Blue and veggie — needed with 2 to synthesise a true-colour green | 1 km |

Everything needed to interpret the pixels is an **attribute**, not data: `scale_factor`,
`add_offset`, `valid_range`, `_Unsigned`, and the whole projection on `goes_imager_projection`.
See the `MiniHdf5` notes — attributes are stored densely in a fractal heap, not in the object
header.

The grid is a **geostationary perspective projection**, not a map projection: its coordinates
are scan angles in radians and most of a full-disk grid is empty space. The corners of the
CONUS rectangle are off the limb of the earth, so a bounding box built from the four corners
is built partly from a non-place — walk the edges instead.

## GOES lightning (GLM)

`noaa-goes19` is GOES-**East** and covers the Americas; `noaa-goes18` is West. **GOES-16 is
no longer filled** — it was replaced as East in 2025 and a listing against it returns an
empty result rather than an error, which looks exactly like "no lightning right now".

    https://noaa-goes19.s3.amazonaws.com/GLM-L2-LCFA/{yyyy}/{ddd}/{HH}/

Anonymous, no key. Keys carry the scan start as `_sYYYYDDDHHMMSSt` — day-of-year, not
month and day, and the trailing digit is tenths of a second. Files land every 20 seconds at
roughly 250 kB, so a ten-minute window is about thirty small reads.

Products publish a couple of minutes behind real time. Asking for the current minute
reliably returns nothing, so the window is walked back from two minutes ago.

The files are NetCDF-4, which is HDF5 underneath — see `formats.md` for what of it
`MiniHdf5` implements.
