# Competitive parity: gaps and status

A feature scan of OpenWSR against RadarScope, GRLevel3/GR2Analyst, RadarOmega, MyRadar,
Windy and Supercell Wx, originally produced 18 Aug 2026. The presentation version with
the full reasoning is [`parity-scan.html`](parity-scan.html) — open it in a browser.

This file is the tracked version: the same 31 gaps and 11 UI findings, annotated with
what has since been **done**, what is **partial**, and what is still **open**.

> Method: competitor features from RadarScope's published Pro tier documentation and
> MyRadar's store listing; open-source gaps from a full enumeration of Supercell Wx's 65
> open GitHub issues, which doubles as a user-validated list of what people miss when
> comparing against GRLevel3, GR2Analyst and RadarScope. UI findings came from querying
> the running app's automation tree, not from reading source. Every data endpoint was
> requested anonymously on 18 Aug 2026 and all seven responded.

**Status at time of writing: 20 of 31 gaps closed, 4 partial, 7 open.** Of the 11 UI
findings, 8 are closed, 2 partial and 1 open.

> **Update, 18 Aug 2026.** The three remaining UI findings (07, 08, 10) are now closed by
> the audit remediation in `audit.md`, which also closed **GIF export** — listed below as
> "implemented, not yet exercised end-to-end" and in fact impossible to complete as
> written. All 11 UI findings are done.

---

## Where OpenWSR already led

Worth restating, because it shaped what was worth chasing. Several of these are sold as
premium tiers elsewhere and two have no commercial equivalent:

- **Unlimited Level II archive** — any site, any day back to 1991. RadarScope gates its
  historical archive behind Pro Tier Two.
- **Sub-scan live latency** — partial volumes render as each tilt completes, roughly five
  seconds after the radar sweeps it, rather than after the full scan.
- **Zero-cost product switching** — geometry is generated in the vertex shader, so
  changing moment or palette is a texture swap. Measured 0.7–11 ms.
- **National hotspot finder** — one click scans all 163 sites and flies to the heaviest
  cell in the country. No competitor ships this.
- **Track-based proximity alerts** — alerts carry a real ETA from the forecast track, not
  just "a warning was issued near you".
- **No account, no key, no tier** — runs anonymously against public data. Supercell Wx
  has five open issues about its mandatory paid map keys.

---

## Data and layers

| Gap | Impact | Status |
|---|---|---|
| National mosaic | Blocker | **Done** — Iowa State pre-rendered N0Q tiles as a second tile layer |
| Lightning | Blocker | **Done** — GOES-19 GLM flashes, via a minimal HDF5 reader written for it |
| Watches, SPS, mesoscale discussions | Costs users | **Done** — watch boxes and MCDs from IEM |
| SPC convective outlooks | Costs users | **Done** — Day 1 categorical |
| Local storm reports | Costs users | **Done** — last six hours |
| Satellite imagery | Expected | **Done** — native GOES-East ABI from S3, decoded and reprojected, true colour by day and infrared by night; IEM tiles as fallback |
| TDWR terminal radars | Expected | **Done** — all 47, via their Level III digital radial products |
| Future radar (nowcast) | Expected | **Done** — HRRR simulated reflectivity, six-hour loop, native GRIB2 |
| County and state boundaries | Expected | **Partial** — inherited from the basemap, not a controllable layer |
| Placefile support | Expected | **Done** — full GRLevelX parser, icon sheets included |
| Shapefile / GeoJSON / KML import | Nice | **Open** |
| Terrain / topography | Cosmetic | **Open** — mostly a basemap style swap |

## Analysis and derived products

| Gap | Impact | Status |
|---|---|---|
| Vertical cross-section | Costs users | **Done** — interpolated between bracketing elevation cuts |
| Storm-relative velocity | Costs users | **Done** |
| Velocity dealiasing | Costs users | **Done** — region-based, cross-checked against Py-ART; no quality mask yet |
| Azimuthal shear / rotation tracks | Expected | **Done** — LLSD azimuthal shear as a live product, plus time-accumulated rotation-track swaths |
| Hail size contours | Expected | **Partial** — per-cell markers sized by severe-hail probability, not contours |
| VWP / VAD wind profile | Nice | **Done** — fitted from Level II velocity rather than decoded from NVW, so it works on any volume back to 1991 |
| Soundings / hodographs | Nice | **Open** — arguably out of scope |
| 3D volume rendering | Cosmetic | **Done** — Cartesian resample, ray marched, orbit camera |

The cross-section was called out in the original scan as *"the largest structural gap in
the whole open-source field — no OSS viewer has it."* It is now done.

## Alerting and workflow

| Gap | Impact | Status |
|---|---|---|
| Location search and GPS | Blocker | **Done** — city, ZIP or lat/lon, resolving to nearest radar |
| In-app settings | Blocker | **Done** — including the NWS contact string that was out of spec |
| Notifications when minimized | Costs users | **Done** — tray notifications |
| Saved locations | Expected | **Partial** — one home point, not a list |
| Per-type alert toggles and audio | Expected | **Done** — warning-type filters |
| GIF / video export | Expected | **Done** — and now actually verified end-to-end; it could not write a file before (`audit.md` C-01) |
| Independent site per pane | Expected | **Done** — any pane can be pinned to its own radar, in live or archive; pinned panes leave the camera link |
| Units preference | Expected | **Done** |
| Longer loops | Nice | **Done** — 12/30/60/144 volumes, chosen in Settings as a duration |
| Drawing and annotation | Nice | **Done** — lines, areas, circles, labels; saved as placefiles |
| Spotter Network | Nice | **Open** — requires an account, niche outside active chasers |
| Mobile client | Cosmetic | **Out of scope** — a different product |

---

## UI findings

Eleven findings from the running app. The first four were defects rather than preferences.

| # | Finding | Status |
|---|---|---|
| 01 | No colour scale anywhere on screen | **Done** — D3D-drawn scale, product-aware, honours imported palettes |
| 02 | Data age read "116106 h 20 m" on archive data | **Done** — elapsed time only in live mode |
| 03 | Toolbar overflow disabled, controls unreachable when narrow | **Done** — superseded by the left nav rail |
| 04 | Keyboard shortcuts advertised inside status text | **Done** |
| 05 | Time controlled from two places at once | **Done** — one unified timeline with hour ticks |
| 06 | Status bar is eight fields of run-on text | **Done** |
| 07 | Layers panel is a flat column of checkboxes | **Done** — collapsible sections, per-layer opacity, non-layer controls evicted to Settings |
| 08 | Failure and empty states are status-bar strings | **Done** — persistent error bar, determinate progress strip, map empty state |
| 09 | Nothing explains the storm symbols | **Done** — symbol key in the layers panel |
| 10 | First launch has nothing to show | **Done** — opens on home's nearest radar in live mode, or the national view with an empty-state hint. The 2013 demo is no longer a default |
| 11 | Window doesn't adapt | **Done** — collapsible rail and panel |

---

## The strategic call that mattered

The scan's central finding was a dependency, not a feature:

> **One dependency gates four features.** MRMS, HRRR, satellite and lightning all arrive
> as GRIB2 or NetCDF, and we currently decode neither. Supercell Wx is stuck at exactly
> this point — its GRIB2 issue blocks its mosaic, wind and precipitation-type work, and
> it has been open for two years.

MRMS native rendering closed on 19 Aug 2026 — the tile mosaic remains as the lightweight option, and the native layer is there for when the tiles run out of resolution.

The chosen route was to **decouple the layers from the formats**: ship the national
mosaic and satellite from Iowa State's pre-rendered tiles in hours using the existing
tile pipeline, then do the real GRIB2 work on its own schedule rather than blocking
everything behind it.

That worked. The mosaic and satellite shipped the same day; GRIB2 landed afterwards and
brought native HRRR future radar with it. **NetCDF was never done**, which is exactly why lightning stayed the one remaining
Blocker-severity gap for so long — it was the only feature still stuck behind the
dependency the plan was designed to route around.

> **Closed, 18 Aug 2026.** The dependency was finally paid down rather than routed around,
> and it turned out to be far smaller than feared: GLM files use a narrow, consistent slice
> of HDF5, and `MiniHdf5` implements only that slice — about 500 lines, following the same
> reasoning that put `MiniPng` inside `OpenWSR.Grib2`. Every gap in the original scan is
> now closed.
