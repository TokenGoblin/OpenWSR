# The radar sources, and how they fit together

OpenWSR draws from eight independent feeds. Several of them show *the same weather*, measured
differently, at different resolutions, with different latency — and knowing which one is
answering a given question is most of knowing how to read the app.

This document is the map of that. [`data-sources.md`](data-sources.md) has the endpoints;
[`formats.md`](formats.md) has the binary field notes. This one is about what each source
*is*, and what happens when two of them want the same part of the screen.

---

## The sources at a glance

| Source | What it measures | Resolution | Cadence | Coverage |
|---|---|---|---|---|
| **WSR-88D Level II** | Everything one S-band radar sees — six moments, every tilt | 250 m × 0.5° super-res | ~4.5–10 min per volume, **streamed by the tilt** | One site, 460 km |
| **WSR-88D Level III** | Products the radar's own algorithms derived: storm tracks, hail, mesocyclones, VIL | product-dependent | ~2–6 min | One site |
| **TDWR Level III** | Terminal radar reflectivity and velocity | **150 m × 0.55°** | ~1 min | One airport, 90 km |
| **MRMS** | All radars merged into one national grid | 1 km | 2 min | CONUS |
| **IEM N0Q tiles** | The same national mosaic, pre-rendered | ~1 km, capped at z12 | 5 min | CONUS |
| **HRRR** | *Forecast* reflectivity from a numerical model | 3 km | hourly run, 6 h out | CONUS |
| **GOES ABI** | Cloud — reflected sunlight by day, emitted heat by night | 2 km | 5 min | Full disk |
| **GOES GLM** | Lightning flashes | point events | 20 s | Full disk |

Only the first three are radar in the literal sense. The rest are context, and the layer
rules below exist mostly to stop the context from covering the subject.

---

## The two kinds of radar, and why both exist

### WSR-88D — the network

163 S-band (10 cm) radars covering the country, and the reason this app exists. **Level II is
the raw thing**: every moment at every tilt, as the antenna turns. Because NOAA publishes it in
chunks rather than whole volumes, OpenWSR can draw a tilt about five seconds after the radar
swept it, which is why the live view is ahead of most consumer apps.

Its weakness is geometry. The beam is about 1° wide and climbs with range, so at 150 km it is
over a kilometre across and several thousand feet up. Directly over a city 60 miles from the
tower, the lowest tilt is sampling *the middle of the storm*, not the part landing on anyone.

### TDWR — the terminal radars

47 C-band (5 cm) radars beside major airports, built to catch microbursts on final approach.
Compared with the WSR-88D they have **a 0.55° beam against 0.95°, 150 m gates against 250 m,
and siting chosen for the runway rather than for regional coverage.** Over a metro area that is
a different picture, not a slightly sharper one.

Three things follow, and they are all consequences of it being a *Level III* feed:

- **Two moments, three tilts.** Reflectivity and velocity only — no dual-pol, because the
  Level III products do not carry it. Azimuthal shear still works, because this app derives it
  from velocity rather than decoding it.
- **No archive, no forecast.** There is no Level II to scrub through, so selecting a TDWR
  switches the app to live.
- **It goes quiet.** TDWRs run a hazardous-weather scan strategy and stop publishing in clear
  air, where a WSR-88D keeps sweeping a clear-air pattern. *"Nothing published"* is normal, and
  the app says so rather than reporting a failure.

### Which to use

| You want to know | Look at |
|---|---|
| Is this cell rotating, is there debris, is the hail big | **WSR-88D Level II** — the dual-pol moments only exist here |
| What is happening over this neighbourhood, right now | **TDWR**, if one is near — finer beam, lower to the ground, faster |
| What is happening in the country | **MRMS** or the tile mosaic |
| What happened three years ago | **WSR-88D Level II archive** — the only source with history |
| What happens in two hours | **HRRR** — a model, not a measurement |

The honest summary: **the WSR-88D tells you what a storm *is*, and the TDWR tells you where it
*is* more precisely.** In a metro area with both, flipping between them is the point.

---

## The national mosaics

Two layers show the same national field and are **mutually exclusive by construction** —
turning one on turns the other off, because they are the same measurement and drawing both is
never right.

- **MRMS composite** is the real product: NSSL merges every radar into a 1 km grid, decoded
  here natively from GRIB2. It stays sharp at any zoom.
- **National mosaic** is Iowa State's pre-rendered tiles of the same thing. Cheaper, and it
  stops at zoom 12 — above that it is stretched.

A single-site sweep always draws **over** the mosaic. The mosaic is context; the site you
selected is the subject.

---

## How the layers stack

Bottom to top, exactly as the renderer draws them:

```
    basemap tiles  (OpenStreetMap / MapTiler)
 ↑  satellite      ── native GOES ABI, or the IEM tile fallback (never both)
 |  national mosaic ── MRMS composite  XOR  N0Q tiles
 |  Field slot     ── HRRR forecast raster
 |  radar sweep    ── the selected WSR-88D or TDWR cut
 |  Analysis slot  ── rotation tracks
 |  overlays       ── warnings, storm tracks, lightning, placefiles, drawings
    legend/labels  ── colour scale, storm IDs, site markers
```

Two rules explain most of it:

**The Field slot has three claimants and they are exclusive.** The HRRR forecast, the native
MRMS composite and the tile mosaic are all *the same quantity as the radar* — a reflectivity
field over the whole country — so only one may occupy the layer under the sweep. Entering
forecast mode turns MRMS off; enabling MRMS turns the tile mosaic off, and vice versa.

**Satellite has its own slot, and is not exclusive with anything.** Cloud is a different
measurement from precipitation, and the entire point of it is seeing it *underneath* the radar.
Sharing the Field slot would have made the two turn each other off.

The satellite slot has one internal rule of its own: the pre-rendered tiles draw **only while
no native raster is loaded**. They are the same field, and since the tiles draw after the
raster they would otherwise cover the better data with the worse — which is not what a fallback
means.

---

## Where storm intelligence comes from

The storm layer, the approach alerts and the hotspot finder all run off **WSR-88D Level III**,
not off the Level II you are looking at:

- **Storm tracks** — `NST` cell positions and forecast paths, joined with `NHI` hail
  probabilities and `NMD` mesocyclone detections, from the radar nearest your home.
- **Hotspot finder** — scans all 163 sites for peak digital VIL (`DVL`) and flies to the
  heaviest precipitation in the country.
- **Approach alerts** — the storm tracks above, plus warning polygons from api.weather.gov,
  measured against your home location.

One consequence worth knowing: **`NSS` storm structure stopped being distributed around 2021**,
so live cells show an ID with no dBZ, VIL or echo top. That is the feed, not the decoder, and
the layers panel says so when it detects it.

Some products are *derived here rather than decoded*, deliberately. The wind profile is fitted
from Level II velocity instead of decoding the `NVW` product, and azimuthal shear likewise.
Deriving keeps them working on any volume back to 1991 — and makes it impossible for them to be
taken away, the way NSS was.

---

## Latency, and what "live" means

| Source | Behind real time |
|---|---|
| WSR-88D Level II | **~5 s per tilt** — chunk-streamed as the antenna turns |
| TDWR Level III | ~1–2 min — polled; only finished products are published |
| WSR-88D Level III | ~2–6 min |
| MRMS | ~2–3 min |
| GOES ABI | ~3–5 min |

The gap between the first two rows is the whole reason the WSR-88D path streams and the TDWR
path polls. Level II is published in pieces; nothing else here is.

---

## Reading the app with this in mind

- The site box holds **all 210 radars**, WSR-88D and TDWR together. TDWRs are the four-letter
  IDs beginning with T.
- **Nearest** picks the nearest WSR-88D, not the nearest TDWR — it is used for the storm watch
  and the archive, neither of which a terminal radar has.
- Greyed-out product buttons are not a bug: they mean the loaded volume does not carry that
  moment. A TDWR volume greys out all four dual-pol products.
- Tilt counts differ enormously — twenty-odd cuts on a WSR-88D volume, three on a TDWR.

---

## See also

- [`data-sources.md`](data-sources.md) — every endpoint, and which ones have already moved
- [`formats.md`](formats.md) — the binary field notes, including the TDWR threshold encoding
- [`verification.md`](verification.md) — how each decoder is checked against MetPy, ecCodes,
  h5py and pyproj
