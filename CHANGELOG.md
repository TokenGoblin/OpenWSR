# Changelog

## Unreleased

**OpenWSR runs in the background now.** Closing the window puts it in the notification area
and it keeps watching, which is the point of the alerting: it is worth nothing while the
process is not running, and the close button is how people tidy a desktop rather than how
they decide to stop being warned about tornadoes. The first time it happens you get a
notification saying where the app went and that Exit is in the tray menu, and the whole
behaviour can be turned off in Settings under **RUNNING IN THE BACKGROUND**, along with
switches for minimising to the tray, starting straight into the tray, and starting with
Windows.

Hidden, it costs almost nothing. Measured over a five-minute soak started into the tray:
**0.9 % of one core, 179 MB, and no network traffic at all** beyond the two polls the alarm
needs — against 60 % of a core and up to 2 GB with a live volume on screen. Three reasons.
The Level II stream stops while the window is away: it is by far the most expensive thing the
app does and the alarm has never used it. So does every other clock in the app, because they
all draw rather than watch — the satellite most of all, which fetches a 61 MB granule every
four minutes and would otherwise have pulled the better part of a gigabyte an hour to paint a
window nobody was looking at. And the renderer idles rather than presenting frames nobody can
see, which it was previously doing at full speed against a hidden swap chain.

Hiding also asks for the memory back rather than waiting for a collection that may never
come, since the process is about to sit idle for hours: measured at 766 MB of managed heap
down to 269 MB, with a second pass two minutes later to catch anything that was still being
decoded when the window went away.

Also: **launching OpenWSR a second time now brings the running copy back** instead of
starting a duplicate. With the window hidden, launching it again is the natural way to ask
for it — and two copies meant two of every storm notification, which is how an alarm stops
being believed.

## 0.2.1

**The Settings window fits on the screen again.** It sized itself to its content with no
ceiling, so on a 1200-pixel display at 125 % scaling it wanted 1222 pixels, centred itself, and
hung off the top with **Save and Cancel below the bottom edge** — and because the window is not
resizable there was no way to drag it back. It is now capped to the usable height of your
screen and scrolls, with the buttons pinned outside the scrolling area so they cannot be pushed
out of reach. Anyone on a smaller or scaled display could not change settings at all in 0.2.0.

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
