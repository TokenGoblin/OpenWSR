# Changelog

## 0.2.0

**The dark basemap works again.** CARTO began requiring an API key in August 2026 and answered
every unkeyed request with a valid PNG reading "API KEY REQUIRED", so a fresh 0.1.0 install drew
that watermark across the whole map. The dark style is now derived from OpenStreetMap's own
tiles — desaturated and inverted as they decode — rather than fetched from a second service, so
it cannot be taken away again. **If you installed 0.1.0, your setting is migrated for you**; the
watermarked tiles already on disk are replaced as they expire. Two costs, both acceptable at
radar zooms: OSM draws minor roads white, which inverts to black, and it bakes place names into
the tile, so they now sit under the weather rather than above it.

**The hotspot finder goes to the nearest storm, not the biggest one.** Clicking 🎯 now scans all
163 radars and flies to the nearest cell that is genuinely raining, measured from your saved
place. **Hold Shift** for the old behaviour — the heaviest precipitation in the country.

**The top bar says what it is.** New this release:

- A **map-centre readout** showing the ground under the middle of the map, lit up when that is
  one of your saved places. The radar dropdown says which station is feeding the screen, which
  is a different question — after a jump or a pan the two can be hundreds of miles apart.
- A **Recenter** button that puts the map *and* the radar back on your place, keeping your zoom.
- The **search box** now says what it accepts: a city, a ZIP code, or `lat,lon`.
- The radar dropdown is labelled **Radar station**, and `Nearest` is now **Nearest to map
  center** — it was never clear what it was nearest *to*.

**Smoothing now defaults to half** rather than off, which is a visible change to how
reflectivity is drawn. If you have ever moved that slider yourself, your setting is kept.

Fixes: the radar opacity and smoothing sliders now reach every pane in a multi-pane layout
rather than only the first; the top bar wraps instead of clipping controls off the edge on a
small window.

## 0.1.0

First release. Level II and Level III decoding, live streaming and archive playback, national
mosaic, satellite, HRRR forecast, warnings, SPC products, lightning, storm tracks and proximity
alerts, multi-pane layouts, 3D volume rendering, placefiles and a per-user MSI installer.
