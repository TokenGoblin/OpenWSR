# OpenWSR — working notes

A native Windows NEXRAD radar viewer: .NET 10, WPF shell, Direct3D 11 via Vortice, own
Web Mercator tile engine. Reads only public, unauthenticated data. See `README.md` for
what it does and `TASKS.md` for the build history.

**Before touching a decoder, read `docs/formats.md`** — it holds the field notes for
every binary format here, specifically the parts where the spec and the live feed
disagree. The other reference docs:

| | |
|---|---|
| `docs/formats.md` | Level II / Level III / GRIB2 / placefile gotchas |
| `docs/verification.md` | The golden-test methodology and how to run a cross-check |
| `docs/data-sources.md` | Endpoints, specs, and which ones have already moved |
| `docs/parity.md` | Competitive gaps, annotated with what's closed |
| `docs/audit.md` | The 2026-08-18 code/feature/UI audit — 21 findings, all closed |
| `resources/crosscheck/` | MetPy, ecCodes and Py-ART dump scripts — run these before trusting a decode |
| `resources/measurements/` | Harnesses that re-derive every number quoted in `verification.md` |

Anything worth keeping goes in `docs/` or `resources/`, not in a scratch directory.
Scratch directories are session-scoped and get lost.

## Never leak personal information

This repository is public and is published **pseudonymously**. Nothing identifying the author,
the machine, or where they live may enter it — not in code, comments, tests, fixtures, docs,
commit messages, or screenshots. This rule outranks convenience every time.

Never commit any of:

- a real name or email address — in `LICENSE`, in prose, or as the git identity a commit is
  authored with
- a home or work location: its address, its coordinates, or the name of the town
- the machine's Windows username, or any absolute path containing it
- the development Forgejo hostname, its ports, or anything about reaching it
- an API key of any kind, including one pasted in while testing

**This file is in the repository too**, so it must describe what to look for without spelling
any of it out. A checklist naming the values would itself be the leak. Derive them from the
machine at check time: the username from the environment, the saved coordinates from
`%LOCALAPPDATA%\OpenWSR\settings.json`, the git identity from `git config user.email`.

Three routes it has actually taken, none of them through code:

**Test fixtures default to wherever the developer is.** Writing a test against the point the
app happened to be pointed at means writing your own address into a public repo. **Location
fixtures use Norman, Oklahoma — 35.2226, -97.4395** — which is the ground the committed Level II
golden volumes and the api.weather.gov fixtures already cover, so it is free and keeps the suite
consistent. This has gone wrong once, in the station-network tests, and was caught by a
pre-commit scan rather than by review.

**Prose that quotes the running app is a transcript of a real screen.** A verification note
reading "the bar showed …" copies whatever was on it, and the centre readout displays the saved
place's coordinates by design. Paraphrase the shape, never the values. `TASKS.md` is where this
happens, because that is where the measurements go.

**Screenshots carry it invisibly.** Anything captured after the places feature can show the home
ring, the coordinate readout, or a saved place's name in the site bar. Check the top bar and the
map before committing an image; better, capture with no place saved.

**Scan the staged diff before every commit**, not just the files you think you touched — and
scan `git rev-list --all` before publishing anywhere new, because a value removed at HEAD is
still served from the commit that introduced it.

> **Known outstanding:** the audit on 2026-09-07 found the current tree still carries a real
> home location in four `TASKS.md` lines, and four commits from 2026-08-27 still carry a real
> name in `LICENSE` (corrected at HEAD, present in history). Cleaning history means a force push
> and a support request to purge orphaned objects, so it is the repository owner's decision and
> was left open deliberately. Do not quietly rewrite history to fix it.

## Git hosting

Development happens against a **self-hosted Forgejo instance on a private network**. Its
hostname, ports and credentials are deliberately not in this repository — they live in the
machine's git config and `~/.ssh/config`, which is where machine-specific facts belong.

What is worth writing down is the behaviour, not the address:

- **Do not use the `gh` CLI against the development remote.** It only speaks to GitHub, so it
  will either fail or, worse, act against an unrelated GitHub repo. Use plain `git`. Forgejo's
  equivalent CLI is `tea`.
- The SSH remote is on a **non-standard port**, because the Forgejo container remaps it. An
  SSH URL without the port silently tries 22 and fails.
- The host resolves only on the local network or over a VPN. **A push failing with a DNS or
  connection error is almost always that**, not a repo or auth problem — check the network
  before debugging anything else.
- If a push prompts for credentials or returns 403, **stop and report it** rather than
  reconfiguring auth or switching remotes.
- Commit and push only when asked.

## CI

`.github/workflows/ci.yml` runs on every push and pull request, in two jobs that guard
different things.

**Windows is the real gate** and runs all 767 tests. It is the only platform that can: the
shell is WPF and Direct3D. The shader tests work on a hosted runner because they compile HLSL
through `d3dcompiler` rather than creating a device, so no GPU is needed.

**Linux builds the eight `net10.0` projects and runs their 406 tests.** That job exists to
guard a property, not a platform: the decoders, geodesy and format readers are meant to stay
free of Windows, which `PurityTests` asserts in-process but only a build without Windows to
fall back on actually proves. `OpenWSR.App` and `OpenWSR.Render` are deliberately absent —
they *can* be built there with `EnableWindowsTargeting`, but never run, so building them
would prove nothing.

Both build with **`-warnaserror`**, because the project holds itself to zero warnings and a
standard nobody checks stops being one. No network is needed: all 56 MB of fixtures are
committed, so a red build is a code failure rather than a flaky download.

Forgejo Actions is similar but distinct, and the workflow here is GitHub's format.

## Commands

```
dotnet build OpenWSR.slnx                    # NOTE: .slnx, not .sln
dotnet test OpenWSR.slnx                     # 767 tests
dotnet run --project src/OpenWSR.App
dotnet publish src/OpenWSR.App -c Release    # single-file self-contained exe
```

The published and Debug binaries are both **`OpenWSR.exe`** (AssemblyName is set), not
`OpenWSR.App.exe`.

## Architecture and the rules that hold it together

```
Nexrad ──┐
Grib2 ───┤
NetCdf ──┼─→ Ingest ──┐
Geo ─────┤            ├─→ App
Placefiles┤  Render ──┘
Palettes ─┘
```

`Placefiles` depends on `Geo` — the only edge between the pure libraries. A drawn circle
has to be written out as a ring of a true *ground* radius, which is geodesy, not geometry.

**Purity is enforced by a test.** `Nexrad`, `Geo`, `Grib2`, `NetCdf` and `Placefiles` must not
reference WPF, Direct3D or the network. `PurityTests` asserts this against assembly
references — if you need imaging or HTTP in one of them, that is a signal the code
belongs somewhere else. `MiniPng` exists inside `Grib2` precisely because of this rule.

**The shell is arranged around five questions**, each with exactly one place on screen
and never a second: **where** (top bar: search, site, the map-centre readout, `Recenter`) → **what
product** (the segmented bar above the map) → **when** (the time bar: one
`LIVE · ARCHIVE · FORECAST` switcher that owns the transport) → **what's on top** (the layers
half of the right column, which holds layers and nothing else) → **what's coming at me** (the
`APPROACHING` panel above it). Before adding a control, decide which question it answers and
put it there. Set-once configuration goes in Settings, not the layers panel; reference
material (shortcuts, the symbol key, About) goes in `InfoWindow`, not a panel or a MessageBox.

**"Which radar" and "what am I looking at" are different questions, and the site combo only
answers the first.** After a hotspot jump, a hand pan or a pinned pane the camera can sit
hundreds of miles from the place being watched with nothing on screen admitting it, so
`CentreReadout` names the ground under the middle of the map and its offset from the nearest
saved place. **Centred is judged in screen pixels, not kilometres** — at national zoom a place
20 km off centre is a pixel from the middle and is centred by any honest reading, while at
street zoom the same 20 km is off the side of the map, so a fixed ground tolerance is wrong at
one end or the other. It is painted off the 500 ms status tick rather than a camera event
because inertial panning settles over about a second after the mouse is released, and a
one-shot notification would report where the drag ended rather than where the map came to rest.

`Recenter` beside it is the way back, and it moves **the site as well as the camera** — the
search box two controls to its left already selects the nearest WSR-88D when it lands
somewhere, so that is the convention here, and recentring without it leaves the map over your
house reading a radar hundreds of miles away. It parts company with the search box on zoom,
which it keeps rather than resetting: you press it to fix where you are looking, not how
closely. It targets `AppSettings.Primary` rather than the nearest saved place the readout
names — "put me back where I live" has to land in the same spot every time — and with one
place saved the two are the same question.

**The panel remembers itself, and the walk has to be the logical tree.** `PanelSections`
holds which sections are open and `LayerToggles`/`LayerSliders` hold the controls inside
them, keyed by control name and hooked by walking the panel rather than by wiring thirty
handlers — so a layer added later persists without anyone remembering to make it so. Two
things that are easy to get wrong. Walk the **logical** tree: a collapsed `Expander` has not
realised its content, so a visual walk finds nothing inside Warnings, SPC or "Symbols shown"
while they are shut, which is most of the time. And do **not** suppress the handlers on
restore — ticking Satellite is what starts its fetch, so the handler running *is* the state
being applied; the save debounce is what stops a restore writing the file thirty times. Only
changed controls are recorded, which is what lets a shipped default be revised later instead
of being pinned by a settings file that merely agreed with the old one. Site, product and tilt
are deliberately not persisted: those are where you were looking, not how you like the app.

**The layers panel has a budget too, and it was 92 % over.** Measured rather than eyeballed:
1758 px of content in a 914 px column, 52 % of it visible. Three rules got it to 100 % with no
scrollbar. A control that is inert until some state exists is **collapsed, not shown** — an
opacity slider for a layer that is off, or eleven storm filters while tracking is off (that one
was a third of the panel). Fine-grained settings that are chosen once sit behind a nested
disclosure rather than costing rows every session. And sections that are set-once — RADAR,
Warnings, SPC — start closed. What makes those defaults safe is that `PanelSections` in
settings **remembers how each section was left**, so a default is only a starting point: which
sections a person needs open is not something this can know, since a chaser lives in STORMS and
never opens SPC while someone watching one town is the reverse. Measure with UI Automation's
`ScrollPattern.VerticalViewSize` before and after; guessing at panel height does not work.

**The left rail has a fixed budget and it is already spent.** Sixteen 44 px buttons needed
1109 px against 1096 available, so `About` sat entirely below the window edge — and because
the bottom group is docked to the bottom, overflow eats Settings, Help and About first: the
three you least want to lose. There is no scrollbar and no overflow affordance, so a rail
that does not fit fails silently. Before adding a button, take one out. What went, and why:
set-once actions (`Set home`, palette import) belong in Settings, reference material
(`About`) belongs in `InfoWindow`, and a control that is inert until some state exists
(`Link panes`) should be **collapsed** rather than merely disabled — a greyed button still
costs a slot.

Those removals bought room at a maximised window — fourteen controls need **663 px against
1096** — and that is the number that misleads. **The budget that binds is `MinHeight`, not the
screen.** At the window's own declared minimum of 900 × 600 the rail has about 570 px, so it
has been over budget for some time; adding Forecast is merely what pushed `Help` over the edge,
measured at a bottom of 904 px against a window bottom of 850.

The fix was **declaration order**, not a removal. A `DockPanel` gives each child the space it
asks for in the order it is declared, so with the tools declared first they took everything and
the bottom-docked group got the remainder — which is precisely backwards, because Settings and
Help are the two you least want to lose. They are declared **first** now and the tools follow
in a `ScrollViewer` with `Auto` visibility, so the rail degrades **visibly** instead of
silently: no scrollbar at any ordinary height, a scrollbar and an intact bottom group at the
minimum. Verify by driving the window to 900 × 600 and reading every rail button's
`BoundingRectangle` — `resources/measurements` has no harness for this, but the check is four
lines of UI Automation and guessing at it does not work.

The right column carries the last two, and they behave differently on purpose. The layers
toggle governs **only** the lower half — tidying the layers away must not take a tornado
warning off the screen with it — and `APPROACHING` collapses on its own whenever nothing is
threatening, so the column is unchanged from before on a quiet day. `RightColumn` disappears
only when both halves are hidden.

**The forecast page is the one screen that is not the map, and it is a station reading rather
than a model.** `ForecastWindow` answers the question the radar cannot — what the weather will
*do* — from api.weather.gov, keyless, the same host and User-Agent rule as the warnings poll.
Three things about it are load-bearing. It forecasts for **`AppSettings.Primary`, not the
camera**, for the same reason `Recenter` does: after a hotspot jump the map is routinely a
thousand miles from where you live, and "my area" means where you live. Current conditions are
a **station** measurement and the card names the station and its distance, because "75° here"
and "75° at an airport sixteen miles away" are different claims and only one of them is true.
And it is **modeless with its own ten-minute clock**, so `HideToTray` closes it — it is neither
an `ITimedLayer` nor hidden when its owner is, and an owned window whose owner is hidden stays
on the desktop.

**Hyperlocal home-station networks have no keyless path — do not re-derive this.** Weather
Underground answers `401 Missing apiKey` and issues keys only to people who contribute a
station; aprs.fi and Synoptic need tokens; findu.com's TLS is broken; IEM carries no CWOP
network. MADIS *is* keyless and does carry CWOP, at **33 MB gzipped per hourly file,
nationwide, an hour in arrears** — which is not current conditions. `docs/data-sources.md` has
the table. Without a key the answer is still decent, because `gridpoints/.../stations` is not
airports-only: NWS carries RWIS and mesonet sites in the same list, so the nearest reporting
station is often much closer than the ASOS.

**A WU key needs a station of your own, and it expires.** Free keys go only to "registered and
active" PWS users — running one *and* uploading — so this is not a credential most people can
get, and the app has to be complete without it. Because they lapse, a rejected key is far more
often expired than mistyped: the message leads with regenerating at
`wunderground.com/member/api-keys` (*not* `/member/devices`). And **a bad key must cost the
personal stations and nothing else** — `StationAuthException` is caught around the merge, not around
the load, or an expired key blanks the whole forecast page, which is what it did until it was
caught.

**So personal stations are an opt-in key, and `MapTilerKey` is the precedent to copy.**
`AppSettings.WeatherUndergroundKey` + `PwsClient`: optional, null by default, pasted into
Settings, sent to `api.weather.com` and nowhere else, and the app is complete without it. Three
rules hold it together. **The key is a credential and rides in the query string**, so anything
that formats the URL formats the key — `PwsClient` owns its own exception type and logs status
codes rather than URLs, because an exception message reaches the forecast page's error bar.
**Clearing the box must reach `null`, not `""`** — an empty key is *sent* and rejected, turning
"go back to NWS" into an auth error — and it also releases a pinned personal station, which
would otherwise point at a network the app can no longer reach. And **the choice stays visible**:
personal and NWS stations interleave in one distance-sorted list, so each is labelled with its
network and the card says which kind it is reading. Closer must never quietly become official.

**Ambient Weather is the third station source and answers a different question.** Weather
Underground finds stations *near a point*; Ambient has **no geolocation endpoint at all** and
answers *what this account owns* — so `AmbientClient` reads the user's own station, and
`StationSource.Own` always wins the automatic pick while it is reporting, on the grounds that it
is the actual ground the forecast is for rather than merely the nearest. One request does both
jobs: `/v1/devices` embeds `lastData`. Two keys, both the user's, both self-serve from one
account page — and the application key is deliberately **not** baked into the build, because a
public repository would publish it and every copy would share one rate limit. Everything Ambient
sends is **imperial with no way to ask otherwise**, so every reading crosses a unit boundary on
the way in; `baromrelin` (sea-level corrected) beats `baromabsin` (raw), and `dateutc` is
milliseconds. `feelsLike` restates the air temperature outside its own range, the same trap as
Weather Underground's.

**Each network is guarded separately, and `TryMergeAsync` catches everything except
cancellation.** A station network failing must cost its own network and nothing else — not the
forecast (an expired key once blanked the whole page), and not the *other* network, since a
stale Weather Underground key has nothing to do with the station in your garden. Catching only
`StationAuthException` was too narrow, and the gap was the interesting half: a refused key is the
*rare* case, while a timeout, a 404 from the nearby lookup, or a body that does not parse are the
ordinary ones — and each of those escaped to the general handler, producing a blank page blaming
the National Weather Service for a third party's outage after the forecast had already arrived.
Cancellation is excluded deliberately, or a superseded refresh carries on drawing.

**Every station source needs its own read, including the picker.** Routing anything but
`Personal` at the NWS client sent an Ambient MAC address to `api.weather.gov`, so the user's own
station — the one that always wins the automatic pick — was the single entry in the list that
could not be selected. `AmbientClient.GetDeviceAsync` exists for that: Ambient has no per-device
current endpoint, so reading one station means re-reading the account and picking it out, and a
caller should not have to know that.

**The station layer draws all three sources and needs no key to be useful.** `StationLayerController`
puts a dot per surface station on the map with its temperature, coloured by who runs it. The NWS
list is public, so it works the moment it is ticked; Weather Underground and the user's own
station join it when their keys are set, which is the point — those are the ones actually near
you. Three things shape it. **There is no bulk observation endpoint**, so each NWS station is a
separate request and the count is capped at twelve — which caps the clutter too, since fifty
temperatures over one state is a wall rather than a display. **Stations are gathered around a
point**, so the layer refetches when the camera has drifted past 60 km, rather than on every
camera event or never. And it is an `ITimedLayer`: it draws rather than watches, so it stops in
the tray like the rest.

**Map labels use `Units.TemperatureShort` — "73°", not "73 °F".** The glyph atlas is a
fixed-cell monospace grid with wide tracking, so the unit letter nearly doubles the label and it
starts colliding with the place names baked into the basemap. The scale is named once, in the
panel caption, rather than on every marker.

**A glyph the atlas font does not carry overflows its cell and lands on its neighbour.** Consolas
has no `⚠`, so font fallback returned it wider than the 14 px cell, centring spilled it both
ways, and the degree sign — immediately to its left in `Charset` — put a shard of a warning
triangle on every temperature the station layer drew. `GlyphAtlas` now shrinks an oversized glyph
to fit and clips to the cell regardless; `GlyphAtlasTests` asserts no glyph reaches its cell edge.
Adding a character to `Charset` is adding a neighbour to two existing ones — check that test.

**An unresolvable pin is released for the load, not deleted from settings.** A pinned station
that has dropped out of its network's list used to match neither branch of its own merge and
then, being non-null, suppress every *other* source's automatic pick — silently, permanently,
across launches. `ForecastWindow._pin` is the per-load copy each merge nulls when it cannot find
it. Not cleared in settings, because a station can drop out of a nearby list for a scan or two
without being gone, so the pin self-heals if it comes back.

**The PWS and Ambient response shapes are from documentation, not live captures.** A key needs a
contributed station and there was none to test with; the endpoints and their auth behaviour were
verified live, the bodies were not — the same for Ambient, which needs an account that owns a
station. `PwsClientTests` and `AmbientClientTests` carry that warning at the top; re-capture
against a real account before trusting the field names.

**In-app explanation lives in `InfoWindow`, and `?` opens the guide rather than the shortcut
table.** Someone presses `?` because they do not know how the thing works, and the first
question is not which key selects velocity — it is what the app will do on their behalf and
when. `ShowGuide` answers that in prose, with the alerting rules first, and links on to the
keyboard card and the symbol key. The first run shows it too. Anything explaining a *feature*
belongs there; a panel is for operating one, not for describing it.

**The app has a second life with no window, and the rules there are different.** Closing
hides to the tray and keeps watching, because the alerting is worth nothing while the process
is not running. `MainWindow._inTray` is the flag, and three things read it: the status tick
drops everything but the settings flush and `ExpireStale`, `StartLiveAsync` refuses to connect,
and `OnThreat` skips the in-window toast — a `Popup` owns its own HWND, so with the window
hidden it is not hidden along with it and would open on the bare desktop. What keeps running is
exactly what the alarm is built on: the Level III storm poll and the warnings poll, both
`DispatcherTimer`s that never cared what was on screen. **Every other clock in the app draws
rather than watches, and stops** — that is what `ITimedLayer` is for. Anything new with a timer
belongs on one side of that line or the other, and the default is the drawing side.

**`ITimedLayer` suspension is a latch, not a snapshot, and that distinction was a bug.** The
first version only stopped clocks that were already running, which is wrong on a tray start:
`RestoreLayerState` switches layers on *after* the hide, and their own `Enable` starts the
fetch — a 61 MB satellite granule landed 71 seconds after the window disappeared. `Enable` now
checks the latch and takes the state without starting the clock. Test it by soaking a
`--tray` start and categorising the log: anything but `alerts/active` and `Level3` is a leak
of this kind. Measured over five minutes: 0.9 % of one core, 179 MB, and nothing else on the
wire at all.

**Hiding collects, twice, and this is the one place in the app where that is right.** Not
because the runtime needs help in general, but because every assumption behind leaving it
alone has just stopped holding: a live volume's worth of floats has been dropped, the clocks
that would allocate are stopped, and the process is idle for hours rather than seconds — so
left alone it simply keeps the peak, measured at **2.0 GB three minutes after a hide**. The
collect is compacting, because a decoded sweep is large-object-heap sized. The second pass,
two minutes later, exists for one specific thing: a fetch already in flight when the window
went away, which lands after the first pass. It is not a recurring sweep, and should not
become one — with the drawing clocks stopped there is nothing for a third to find.

**The single-instance guard has to own window creation, or it guards nothing.** `App.xaml`
deliberately has **no `StartupUri`**: it navigates after `OnStartup` returns, while
`Shutdown()` only queues a callback, so a second launch that had already decided to exit still
ran the entire `MainWindow` constructor on the way out — second tray icon, second warnings
fetch, second set of timers. `OnStartup` creates and shows the window itself, after the claim
succeeds. Do not reintroduce `StartupUri`.

**A session ending is not a user closing the window.** Windows logs off by calling
`Application.Shutdown`, which still raises `Closing` — so close-to-tray ran during sign-out and
spent the one-time "OpenWSR is still watching" balloon on somebody who never saw it, leaving
the next genuine close unexplained. `SessionEnding` sets `_exiting`.

**Login start lives in the registry and is not mirrored into settings.json.** It can be turned
off from Task Manager without this app being told, so a saved copy of the answer would be a
second spelling of the same fact — the same reason `MigrateLegacyHome` nulls the fields it
folds in. `StartupRegistration.IsEnabled` asks the Run key every time.

`MainViewModel` holds the state the layout is built from — mode, product, tilt, armed map
tool. Keep it there rather than in control properties, or moving a control between
containers means rewriting the logic that reads it.

**Threading.** The UI thread does UI. The render thread owns the D3D device context and
every D3D object. Ingest and decode run on the thread pool. Cross boundaries with
channels, immutable records, or a lock — never by touching a D3D object off the render
thread. Anything the render thread must do for a caller (frame capture, texture upload)
is queued as a request and serviced after present.

## Things that cost real time to discover

**A pinned pane owns its own place.** Pinning a pane to a site moves its camera there
and drops it out of camera linking — otherwise the link drags it straight back and you are
looking at the right data over the wrong ground. Pane state (including the link snapshot)
lives on the `Pane` object rather than in arrays indexed by position, because that position
shifts whenever panes are added, removed or pinned.

**The dBZ window is a draw-time discard, not a change to the data.** `MapView.SetValueFilter`
feeds two floats into the sweep shader's constant buffer and gates discard outside them. It
was tempting to do it by zeroing the palette's alpha instead, which needs no shader change —
but then moving the control would have to restage the sweep, and archive playback hands the
renderer prebuilt geometry it has no reason to rebuild. As a constant it redraws what is
already on screen. It applies to **reflectivity only**: the window is in dBZ, and a dBZ bound
against a velocity field or a correlation coefficient would silently blank most of the
product, so other moments get the full range and the panel says so.

**A palette's low end is a display decision, and the basemap changes the answer.**
Reflectivity used to reach full opacity at 10 dBZ, which is clear-air return — insects,
birds, residual clutter. That was invisible as a problem while the basemap was light, where
pale cyan on white recedes; against the near-black default it put bright blue over **26.7 %
of the map against 12 % of actual storm**. The low end fades in now and is full only from
22 dBZ. When touching it, keep the property that makes it safe: every band from the green
stop upward has both endpoints untouched, so weather is byte-for-byte what it was, and
`ReflectivityPaletteTests` asserts that against a copy of the previous table rather than
trusting the eye.

**The dark basemap is derived, not fetched, because the free keyless one ran out.** CARTO's
"Dark Matter" was the ground the whole colour design rests on, and in August 2026 CARTO began
requiring an API key — answering every unkeyed request with a valid PNG reading "API KEY
REQUIRED" — and started retiring its raster basemaps outright. `TileToning` derives the dark
style instead: OSM's own tiles, desaturated and inverted as they decode. Three things about it
are worth not relitigating. **Inverting, not darkening** — OSM's ground is near-white with its
detail above, so scaling flattens everything toward one grey, while inverting puts the ground
near-black and turns OSM's near-black label text white. **The gamma is set by the ground and
the labels, not by a mean** — at 1.35 OSM's land lands at 6 and its label text at 189. Mean
luminance is only a cross-check, and a loose one: over the twelve committed z9 tiles, CARTO's
own per-tile mean ranges 9.1 to 20.5, so anything from about 1.3 to 1.5 sits inside its spread.
Do not tune it to a decimal place; `resources/measurements/TileToningMeasurement.cs` reproduces
the table. **Esri's Dark Gray Canvas is not the answer** and was measured before being ruled
out — 66.7 against CARTO's 12.2 on the same tiles, and its land/water polarity is inverted, so
lakes read as holes punched in a grey field. Two things were lost and both are
acceptable at radar zooms: OSM draws minor roads white, which inverts to black, and it bakes
place names into the tile.

**Place names used to be their own layer, drawn above the weather.** The idea was right — baked
in, they are the first thing an echo covers, and the name of the town a storm is over is exactly
what wants reading at that moment — and `TileProvider.Labels`/`LabelBoost` and the `boost` tint
`DrawTiles` passes to the quad shader are all still there and still work. Nothing populates them
now: CARTO was the only style that published its labels separately, and OSM does not. The
machinery is kept rather than deleted because it is the seam a vector basemap or a future
labels-only source would attach to, and because splitting them out again is the single biggest
improvement available to the basemap. Do not wire a second copy of a baked-in style into it —
that draws every name twice, once shifted.

**A hidden swap chain does not throttle, so hiding the window used to spin a core.**
`Present(1)` is what paces the render loop, and it only waits for a vertical blank while there
is something on screen to wait for — occluded or hidden, DXGI returns immediately and the loop
runs as fast as the GPU will take it, drawing frames nobody can see. `MapView.Paused` sleeps
the loop instead of tearing the device down, so the device, its textures and the staged sweep
survive and coming back is a flag rather than a rebuild. Do not "fix" it by stopping the render
thread: `Stop()` disposes the device, and `_pendingSweep` is a single slot rather than a queue
precisely so a paused loop accumulates nothing.

**A window that is hidden before its first layout has no renderer.** `D3DHostControl` is an
`HwndHost`, and `BuildWindowCore` — which is what calls `MapView.Start` — only runs once the
window it sits in has been laid out. So starting into the tray opens the window **minimised and
unactivated** and hides it from the `Loaded` handler a frame later, rather than hiding it in
the constructor. It looks like an extra step and is the difference between a tray watcher that
can show you the map and one that comes back blank.

**Airspace.** The D3D child HWND always draws above WPF content inside its rectangle.
WPF controls cannot overlay the map. Anything that must appear *over* the map is either
drawn in the D3D scene (the colour scale, storm labels — see `GlyphAtlas` and
`DrawLegend`) or is a `Popup`/`ToolTip`, which get their own HWNDs. Docked panels beside
or below the map are fine.

**Automating the UI: use `Checked`, not `Click`, on ToggleButtons.** `TogglePattern.Toggle()`
does not raise `Click` in WPF, so `Click`-handled toggles are unreachable to assistive
technology and to UI-automation tests. Handle `Checked` (with a suppress flag) and re-check
in `Unchecked` to keep radio behaviour. Glyph-only controls need
`AutomationProperties.Name` — a screen reader announces "▤" otherwise.

**Coordinate-based clicking fights the foreground.** Other windows steal it mid-run and the
clicks land somewhere else entirely; prefer UI Automation patterns, and check
`BoundingRectangle` is on-screen before falling back to a synthetic click.

**Screenshots lie unless the capturing process is DPI-aware.** PowerShell is not, so
`GetWindowRect` and `CopyFromScreen` return virtualised coordinates and you capture about
two-thirds of the window — which looks exactly like a broken layout. Always
`SetProcessDPIAware()` first. Several hours went into chasing a phantom layout bug here.

And `CopyFromScreen` captures *the screen*, so it captures whatever is actually in front —
another window stealing focus mid-run yields a screenshot of that window, which looks like a
broken app rather than a lost race. **Use `PrintWindow` with `PW_RENDERFULLCONTENT` (flag 2)
instead**: it asks the window to draw itself and is immune to occlusion, focus and window
position, so it needs no `SetForegroundWindow` and no retry loop. The map is a D3D child HWND
and may come back black that way; the WPF chrome, which is what layout work is usually about,
comes back correctly.

**WPF's default control templates are unreadable on a dark ground.** `Theme.xaml`
provides explicit templates for Button, ToggleButton, ComboBox, TextBox, CheckBox,
Slider, DatePicker, ScrollBar and ToolTip. Style through it; don't hardcode colours.
The ToolTip one is not optional: a tooltip inherits `Foreground` from the control it
belongs to, so every one of them was near-white text on WPF's default *light* popup —
invisible. Its wrap width has to sit on the inner `TextBlock`; a `MaxWidth` on the ToolTip
lets the text measure unconstrained and then clips the end of the sentence off.

**Map keyboard has exactly one route.** `MapView.KeyPressed` (raised from the D3D child
window's `WM_KEYDOWN`) is it. Do not also handle `Window.KeyDown` for the same keys: it is
a bubbling routed event and a `TextBox` does not mark character keys handled, so typing
"Vail" into the search box selected velocity, and arrow keys double-stepped the tilt.
`Window_PreviewKeyDown` handles only the app-level keys and bails when focus is in a
text-entry control or the map host.

**Overlay symbols are sized in screen pixels, not Mercator metres.** A marker drawn 900 m
across is sub-pixel at national zoom and screen-filling at street zoom. Multiply by
`cam.MetersPerPixel` and rebuild when the zoom moves materially (`NotifyViewChanged`).
Only things that *are* a physical extent — a mesocyclone radius, an alert ring — stay in
metres. Line widths are already pixel-constant in `OverlayRenderer`. So are dash lengths —
the forecast track's dashes were 2.5 km of Mercator and went solid at national zoom.

**The hodograph has two sign-and-scope traps, and neither one throws.** A `VadLevel`
reports the direction the wind comes *from*, so components carry a negative sign
(`u = -speed·sin`, `v = -speed·cos`); lose it and the plot is rotated 180°, which still looks
like a hodograph. WPF's y grows downward, so the vertical axis is negated when projecting;
without that the plot is mirrored, turning a veering profile into a backing one and reversing
the sign of everything read off it. Separately, `Hodograph.Layer` returns what it has when
asked for more depth than the profile spans — right for a calculation, a trap for a caption,
because a 2.7 km profile will happily produce a "0–6 km shear". Check `Hodograph.DepthM`
before labelling anything with a depth. Storm-relative helicity is golden against MetPy 1.7.1
including the interpolated layer top; **Bunkers is deliberately absent**, because its mean
wind is pressure-weighted and a radar profile has no pressure — a standard atmosphere would
only make our number agree with the reference because both were fed the same invention.

**A tile service advertising a zoom range is not promising to have the tiles.**
`USGSShadedReliefOnly` would have been the better terrain basemap — grey relief leaves the
reflectivity palette the only saturated thing on screen — and its metadata advertises levels
0 to 23. Its cache has holes: over northern Utah it serves z8 and z12 and returns **404** at
z9 and z10. `USGSTopo` was complete everywhere checked and is what ships. Two other things
that cost time here: ArcGIS tile paths are `{z}/{y}/{x}`, row before column, and the swap
returns tiles rather than an error — just the wrong ones. And `TileFetcher` used to swallow
every failure into a retry map, so a basemap where *every* tile 404s looked exactly like one
that was merely slow. It now records the first failure and `MainWindow` reports it once.

**Boundary data has to be thinned per zoom, not drawn.** The Census 1:500,000 county file
is 1.03 million points — at national zoom about two hundred points to the pixel, and over six
million vertices a frame if handed straight to `OverlayRenderer`. `BoundariesController`
projects and bounds every ring once at load, then per view change culls to the viewport and
runs `Polyline.Simplify` at 1.2 px. Simplify in the plane you draw in (Mercator metres), not
in degrees, or the north of a shape thins harder than the south. And measure distance to the
*chord*, not between consecutive points: a coastline has no point far from its neighbour while
the run of them bends a long way, so neighbour-distance thinning flattens it.

**Overlay strokes are a distance field, and a segment rounds its far end only.** Each vertex
carries its (along, across) position within its own segment, and the shader measures distance
to the *segment* — so the ends round themselves and the last pixel feathers, with no
multisampled target. The cap rule is the part to not undo: `LineCaps.Joined` is flat at A and
round at B, so in a chain the round end lands on the shared vertex and covers the next
segment's square start. One disc per joint. Rounding both ends instead stacks two half-discs
on every shared vertex, which at any alpha below opaque reads as a string of beads along the
line — the 96-sided place ring is the worst case. `LineCaps.Both` is for segments that
genuinely stand alone: dashes, crosshairs, the measure line. Chains must actually chain, and
`OverlayStrokeTests` asserts that they do.

**Stroke widths are device-independent units, scaled by `MapView.DipScale`.** The swap chain
is sized in physical pixels, so a width taken literally draws at two thirds of its weight on a
150 % display — which looks like nothing more than a design choice, and was most of why every
overlay line read as a hairline.

**A gigabyte of working set is this application behaving normally.** Measured over twenty
minutes of live streaming with no interaction: it sawtooths between roughly 700 MB and
1.45 GB, with two full gen2 cycles returning it to a stable floor (685 MB, then 748 MB). It
is not a leak — handles and threads stay flat, `LiveFeed`'s assemblers are capped at two and
pruned, and the peaks are one volume's decode awaiting collection. A Level II volume is about
210 MB as floats once every moment is decoded, so a couple of them in flight is the shape of
the graph. Tile textures are *not* part of that number: they are `Immutable` with no CPU
access and live in VRAM.

**Nothing on the UI thread may block on I/O.** `LiveFeed` exposes `StartAsync`/`StopAsync`;
a blocking `Wait(5s)` in `Stop()` froze the window on every live toggle. Dispose paths
cancel and let the drain finish on a continuation.

**Places are a list, and one of them is primary.** `AppSettings.Locations` replaced a single
home. Exactly one entry is primary — it decides the startup camera and which radar the storm
layer follows, and both need a single answer, so `SetPrimary`/`Remove` keep the invariant. Each
place carries its own alert radius, falling back to the global one when null; `RadiusFor` is
the only correct way to ask.

`ThreatMonitor` evaluates every threat against every place, so one storm crossing two of them
is two entries. `Threat.SourceKey` identifies the storm or warning; `Threat.Key` appends the
place and is what the once-an-hour throttle uses, because hearing about a storm at home must
not spend the alert for the office. The place is named on the row only when more than one is
watched — with a single place it is the only answer there is, and a 248 px panel needs that
space for the range.

**A settings file older than the list still carries `homeLatDeg`.** `MigrateLegacyHome` folds
it in on every load, not once behind a version flag: an older file can appear at any time,
restored from a backup or synced from another machine. It is a no-op once the list holds
anything, and it nulls the legacy fields so a saved file never carries two spellings of the
same fact.

**A track resolves to one of three things, and "closest approach" alone cannot tell them
apart.** `DistanceKm` and `EtaMinutes` report the same pair — the current distance, zero
minutes — whether a storm is arriving right now or is as close as it will ever get and
leaving, because the search starts at the current position and only improves on it. Reading
that as "now" turned every departing storm into an alert. `GeoMath.PathApproach` carries
`CurrentKm` and `FinalKm` so `IsReceding`, `IsClosing` and `IsStationary` can be told apart.
A receding storm is not on the list at all — the panel is titled APPROACHING.

The other half is miss distance: passing thirty-five miles away inside a fifty-mile radius
is not the same claim as passing overhead. `DirectHitRadiusKm` splits them. A **glancing**
pass lists, names the side it goes by, and does **not** interrupt; only `Interrupts` threats
raise. Warnings are exempt from the tiering — a polygon has no track, so there is nothing to
judge — and so is a rotating cell, whose forecast track is the part least worth betting on.

**The SCIT forecast is frequently degenerate while the motion is known.** Its forecast points
sit on the current position for a while after a cell appears, but `SpeedKmh`/`BearingDeg` come
from the *past* track and are usually good by then. Treating a degenerate forecast as "not
tracked yet" throws away measured motion and drops storms that are demonstrably coming, so
`ClosestApproach` extrapolates an hour along the measured heading instead. Only a cell with a
degenerate forecast **and** null motion is genuinely untracked.

**Alerting has two outputs and they answer different questions.** `ThreatMonitor.ThreatDetected`
fires once per hour per source and is what *interrupts* — a tray balloon and a sound.
`ThreatsChanged` carries the whole current set on every evaluation and is what the side list
*draws*. A threat still present on the next poll has to stay on that list without re-alerting,
so the throttle sits on the notification and never on the set. The storm and warning halves
run on different clocks, so each evaluation replaces only its own half.

**The threat list is ordered nearest-first, and this was measured, not reasoned.** Soonest-first
reads wrong: a cell whose closest approach is where it already sits reports an ETA of zero — it
is at its nearest *now*, possibly receding — so four such cells at 18–44 miles sorted above one
closing to 14 miles in nineteen minutes. Also, **advisories are not threats**: the warnings layer
draws statements when ticked and should, but `IsDangerous` keeps them off the list and out of the
tray. Warnings always count; anything else needs a Severe or Extreme severity.

**The first run asks Windows where it is, once, and never again on its own.** With a place
saved the app opens on its radar, watches storms from it and can say what is heading for it;
without one it is a browser for other people's weather, so finding the machine is the whole
setup step. `TryLocateOnFirstRunAsync` runs only when no place exists, is **not awaited** —
Windows can sit on a cold radio for twelve seconds and holding the first paint hostage to
that would make the app feel broken — and sets `AppSettings.LocationAsked` **before** the
attempt, so a call that throws or an app closed mid-way still counts as having asked. That
flag is the point: with the privacy switches off there is no consent dialog to answer, so
retrying each launch would put the same bar in front of someone who has already decided.

**A one-time thing done on the user's behalf needs `ReportNotice`, not `Report`.** The status
line is a running commentary that the live feed overwrites within seconds, which is right for
a commentary and useless for "I saved a location for you". `ReportNotice` borrows the error
bar's persistence and drops its alarm colours.

**Windows Location Services works unpackaged, but says `Denied` rather than prompting.** The two
privacy switches that block it live in different places on the same Settings page, and an
unpackaged desktop app gets no consent dialog when either is off — so `GeoLocationService` names
both in every failure message. `Geolocator.RequestAccessAsync` needs a message pump, so call it
from the dispatcher. Ask for `PositionAccuracy.Default`, not `High`: the GPS-grade path can sit
on a cold radio for a minute and a home location does not need metres.

**A dialog that mutates the live `AppSettings` has no working Cancel.** Every control in
`SettingsWindow` is read *on save*, which is what makes Cancel work; home was written the instant
"Use my location" succeeded, so Cancel left it moved and waiting for the next unrelated `Save()`.
Home is held in pending fields now. Anything else added there must be too.

**Errors need somewhere to live that isn't the status line.** `Report()` is the running
commentary and is overwritten constantly; `ReportError()` puts a failure in a bar that
persists until dismissed. A failed warning fetch must never look like "no warnings".

**XAML event handlers fire during `InitializeComponent`.** A filter handler that touches
a control declared later in the file will hit a null. Guard every control it reads.

**And a guarded handler drops the value, which is how a shipped slider default goes missing.**
`OpacitySlider`/`SmoothSlider` raise `ValueChanged` during `InitializeComponent`, when
`_mapView` is still null, so their XAML values never reached the renderer — invisible only
because the two ends were kept equal by hand, `Value="85"` against `_radarOpacity = 0.85f`. A
coincidence, and one edit from a panel that disagrees with the picture. The constructor now
pushes both into `_mapView` right after it is built, so **the XAML value is the only place a
default is written**; the `MapView` field initialisers are fallbacks for a renderer used
without the shell, not a second opinion. Settings restore runs later and overrides both.

**WinForms is referenced only for `NotifyIcon`.** Its implicit usings are removed in the
csproj (`<Using Remove="System.Windows.Forms" />` and `System.Drawing`) because they
collide with WPF on `Application`, `Color`, `Brushes`, `MessageBox` and `Size`.
`TrayNotifier` imports them explicitly.

**PowerShell gotchas.** `lp` is an alias for `Out-Printer`, so a helper named `LP`
silently breaks pipelines. Never round-trip a UTF-8 source file through
`Get-Content | Set-Content` — it mangles non-ASCII; use `git checkout` plus the Edit tool.
Double quotes inside a git commit message passed via `-m @'...'@` break argument parsing.

## Format notes

**Level II.** Pre-2016 gzip-era archive files have **no LDM record framing** — raw
messages follow the 24-byte header. Message 31 sizes above 65534 halfwords live in the
segment-count fields. Walk Message 31 block pointers and skip unknown block names;
newer RDA builds add blocks. VCP elevation angles are binary angle units, so the nominal
"0.5°" cut reports 0.4834°.

**Level III.** Live symbology is **bzip2** (`BZh`), not only zlib. NSS (Storm Structure)
stopped being distributed around 2021 — the decoder still works for archives, which is
why live cell labels usually show an ID with no dBZ. Stand-alone tabular products put the
tabular pointer in the *symbology* offset slot.

**Velocity aliasing.** The Nyquist velocity comes from the RRAD block, is absent on some
radials (take the first that has it), differs between the split surveillance and Doppler
cuts of VCP 12/212, and is missing entirely from pre-2000 archives — hence
`Sweep.NyquistMs` is nullable. Raw velocity pinned at exactly ±V_nyquist is the signature
of folding, not of real data. Unfolding is region-based and follows Py-ART: regions are
**merged** in order of boundary strength, each merge pooling both sides' remaining
boundaries, so a region reached only through several thin boundaries is judged on all of
them at once. There are no width or tolerance thresholds left to tune — the three the older
spanning-tree walk needed were deleted with it, and the fold count is `Math.Round` of the
mean boundary difference. Corrections still chain, so the failure mode to watch for has not
changed: two stacked guesses turn −22 m/s into +82 m/s. `docs/verification.md` carries the
rate measurements and the two dead ends that were ruled out.

**The Field overlay slot has three claimants** — the HRRR forecast, the native MRMS
composite, and anything gridded added later. They are mutually exclusive by construction:
enabling MRMS leaves forecast mode, and entering forecast mode switches MRMS off. Native
MRMS and the tile mosaic are the same field, so each turns the other off too.

**The satellite is an overlay, not a basemap, and that changes the colour science.** CIRA's
GeoColor paints the whole earth because there the satellite *is* the base image. Here there is
an OSM basemap underneath carrying the roads and boundaries a radar view is read against, so
painting true-colour land hides the map. The day composite therefore keeps true colour but
lets the alpha fall away over clear ground.

Deciding what is cloud takes two things, not one. Brightness alone lets desert through — its
reflectance clears any threshold cloud does. Cloud is also spectrally **flat** across 0.47,
0.64 and 0.86 µm, which is why it looks white, where desert is markedly redder than it is blue
and vegetation is several times brighter in the near infrared. Alpha is brightness × neutrality.
Snow survives that and always will: it is genuinely bright and genuinely neutral, and three
visible bands cannot separate it from cloud. ABI has no green detector, so green is the
community's hybrid, `0.45·red + 0.10·veggie + 0.45·blue`.

**Fetch the multiband product, not the band you want.** Band 2 is published at 0.5 km and runs
to 65 MB at midday; the display raster is about 2.4 km per pixel, so nineteen twentieths of it
is discarded. `ABI-L2-MCMIPC` carries all sixteen bands already resampled to 2 km for 57 MB —
less than that one band, at the resolution actually wanted, co-registered on one set of scan
angles. At night the reflective bands are noise, so `AnyDaylightOverConus` drops back to the
single 3 MB infrared band; it samples across the sector rather than at its centre, because
CONUS spans three hours of longitude and at dawn one edge is lit while the other is dark.

**Two image-overlay slots.** `MapView.SetImageOverlay` takes an `OverlaySlot`: `Field`
draws under the radar sweep (model output, mosaics) and `Analysis` draws over it (derived
products read *against* the echo). They are independent, so the HRRR forecast and a
rotation-track swath no longer fight over one slot.

**TDWR is a Level III radar, and that shapes everything about it.** Terminal Doppler Weather
Radar Level II is not published to the NEXRAD bucket, so the 45-odd terminal radars are reached
as `TZ0/TZ1/TZ2` reflectivity and `TV0/TV1/TV2` velocity — three tilts each, in the same
`unidata-nexrad-level3` bucket and key format as every other Level III product, site prefix
being the ICAO minus its leading T. No WSR-88D and TDWR share a three-letter suffix, so the
existing key scheme cannot collide.

Consequences worth knowing before touching it. The scaling lives in the product's own
threshold halfwords — 31 the first data level in tenths, 32 the increment, 33 the count — and
differs between products (reflectivity floors at −32 dBZ, velocity at −63.5 m/s), so it cannot
be hardcoded per code. Levels 0 and 1 are flags, and level 1 means range folded on velocity and
nothing on reflectivity. `TdwrRadar.ToSweep` converts to a `Sweep` so the renderer, palettes,
inspector and cross-section all apply unchanged; azimuthal shear then works for free, being
derived from velocity. There is no archive and no forecast for a TDWR, so selecting one
switches to live. They also go quiet in clear air, unlike a WSR-88D's continuous clear-air VCP
— "nothing published" is normal rather than a failure.

**Prefer deriving over decoding, where the maths is honest.** The wind profile is fitted
from Level II velocity rather than decoded from the Level III NVW product, and azimuthal
shear likewise. NVW is still distributed, but this project already lost the storm-structure
cell attributes when NSS stopped being generated around 2021 — deriving keeps a product
working on any volume back to 1991 and makes it impossible to take away. `GeoMath.BeamPath`
and its inverse `GeoMath.SlantRangeForHeight` are the pair that makes height-based products
possible.

**3D is a resample, not a layer.** `VolumeGrid3D` walks a Cartesian box and asks which
beam passed through each cell — the reverse mapping, because a forward splat leaves holes
wherever gates spread wider than a cell, and at 150 km a half-degree beam is over a
kilometre across. `GeoMath.BeamAngleTo` is the inverse that makes it possible and is shared
with the cross-section, so a slice through the 3D view and a 2D cross-section of the same
volume agree by construction. **Voxel 0 means unsampled, never "weak"** — the volume is
mostly empty (cone of silence, under the lowest beam, above the top cut) and a scale
starting at zero paints the sky. Vertical is exaggerated ~6× by default because a 12 km
storm in a 300 km box is a smear at true scale; the factor is on screen rather than hidden.

**A live volume is published cut by cut**, so anything that rebuilds on "the volume
changed" fires ten to fifteen times per scan. `VolumeController` debounces, and keeps the
previous volume on screen while the new one has too few cuts — otherwise the 3D view blanks
for a minute every five.

**Derived products.** `Moment.AzimuthalShear` sits in the moment enum so the product bar
and palettes treat it uniformly, but the decoder never emits it — `RadarDisplayController`
materialises it from velocity on demand and caches per cut. Add further derived products at
that seam rather than teaching the decoder to invent data. Anything derived from velocity
must dealias first; on a folded field it shows its own artefacts.

**GRIB2.** MRMS uses PNG packing (template 5.41) — easy. HRRR uses complex packing with
second-order spatial differencing (5.3) on a Lambert grid (3.30) — the hard path, and the
one to be careful with. Scale factors are sign-and-magnitude, not two's complement. Fetch
only the field you need using the `.idx` sidecar and an HTTP range request.

**Placefiles.** Icon sheets frequently ship with **no alpha channel** — the IEM wind-barb
sheet is grey artwork on an opaque black ground — and the convention is that black is the
transparent colour when there is nothing else to go on. Key it out only when every pixel
decodes opaque, or you punch holes in a legitimately black icon. Inside an `Object` block,
"lat, lon" are **pixel offsets** from the
anchor with +y upward, not degrees. Polygon contours close on a repeated first point and
the next point starts a new contour. `;` starts a comment except inside quotes. The spec
at grlevelx.com needs a browser User-Agent — WebFetch gets a 403.

**Endpoints drift.** Three IEM paths verified earlier in development had been retired by
the time they were wired up. When something returns HTML instead of data, check
`https://mesonet.agron.iastate.edu/api/1/openapi.json` for the current path. SPC serves
GeoJSON with a **UTF-8 BOM**, which `System.Text.Json` rejects outright.

**A tile service can fail as a picture rather than as a status code, and this has now
happened twice.** CARTO's watermark was HTTP 200, a valid PNG, and *different bytes per tile*
— so it passed even the two-tile check below, and `BasemapFailure` never fired. It reached a
release and was invisible on every development machine, because the on-disk tile cache never
expires and every one of them held tiles from before the change. When a tile source changes
its terms, a warm cache is not evidence: test with the cache directory moved aside.

**IEM's tile service fails as a picture, not as a status code.** An unknown *layer name* on
`tile.py` comes back as **HTTP 200 with a valid PNG** reading "Invalid TMS Request", which
decodes exactly like imagery — the GOES layer painted the map solid red and cached 253 copies
of that one image as data. Verify a tile layer by fetching **two different tiles and checking
the bytes differ**; a status code proves nothing. An unknown path *prefix* does 404 properly,
so it is only the layer segment that hides. Tile cache directories are named for the layer
rather than the provider, so correcting a path abandons the old tiles instead of serving them
for ever. See `docs/data-sources.md`.

## How to verify work here

The project's standard is to check binary decoding against an **independent reference
implementation**, not against its own output. Follow it:

- NEXRAD Level II and Level III → **MetPy** (`pip install metpy`)
- GRIB2 → **ecCodes** (`pip install eccodes`), which also has a grid iterator useful for
  validating projections

Write the reference values into the test as literals with a comment naming the source.
When no reference exists — the cross-section, for instance — assert **physics** instead:
the cone of silence must be empty, beams must climb with range.

Live features are verified against live weather, not mocks. **Shift**-click `🎯 Hotspot`
for the heaviest precipitation in the country, which is the fastest way to get real data on
screen — a plain click goes to the nearest storm to your saved place instead, which on a
quiet day is exactly the wrong thing to test against. `--soak` runs the live pipeline
headless.

## Conventions

- Comments explain constraints and *why*, never what the next line does.
- Prefer records and immutability for anything crossing a thread boundary.
- User-facing text says what happened in plain language; errors say how to fix it.
- Commit at feature boundaries with a message that explains the reasoning, and update
  `TASKS.md` in the same commit.
- Units: `Units.System` drives all display formatting. Never hardcode km or miles in
  user-facing strings.

## Open work

Every **Blocker** and **Expected** gap in `docs/parity.md` is closed. What is left there is
the Nice/Cosmetic tier — GeoJSON/shapefile import, terrain basemap, soundings, Spotter
Network — plus two Partials: boundaries come from the basemap rather than a controllable
layer, and hail is per-cell markers rather than contours.

The two notes below record the quality work that has the most measurement behind it, and
each had at least one plausible wrong answer ruled out by measuring it.

**Rotation-track speckle takes two masks, because noise and clutter are different
problems.** The first note here was wrong about why: it said the low Doppler cuts have no
correlation coefficient to filter on. True of that sweep — but on a split-cut VCP the Doppler
cut carries **reflectivity** alongside velocity, index for index, and that is the field that
matters for *noise*: 88.8 % of strong-shear gates on the Moore volume sat under less than
20 dBZ. Shear computed where nothing reflected is the phase of receiver noise.
`GateQuality.MaskByReflectivity` blanks those; it removes 89 % of the strong-shear gates and
leaves the peak bit-identical at 0.1297 1/s over the tornado. Clutter is the other half and
is below.

**Clutter is handled, and a bare CC threshold is still the wrong way to do it.** Clutter
reflects strongly and passes the reflectivity mask intact, so it needed a second test, and
correlation coefficient is the only field that identifies it. It is also the most dangerous
thing to threshold here: a debris signature *is* a low-CC target, and masking the Moore
volume on `CC < 0.85` alone removes **all 89** debris gates and then reports a confident peak
on an unrelated feature 20 km east — 0.1000 1/s at 35.276,−97.282 instead of 0.1297 at
35.323,−97.527. Every summary statistic improves while the product answers a different
question.

`GateQuality.MaskClutter` gates it on the echo instead: **low CC only condemns a gate when
the reflectivity is also below 40 dBZ.** Debris is the low-CC target that is *strong* — that
is what makes a TDS detectable — while clutter and biologicals are low-CC and weak. On Moore
that keeps all 89 debris gates with the peak bit-identical; on a clear-air coastal volume
(KBOX) it takes the swath from 23 strong-shear gates peaking at 0.0995 1/s of pure sea
clutter down to 3 gates at 0.0226. 40 dBZ is the knee, not a tuned number: the benefit has
saturated there while the cost is still exactly zero, and above it debris dies for nothing.

Two plausible discriminators were measured and rejected. **Radial velocity** does not
separate them at all — the RDA's own clutter filter has already notched out the genuinely
stationary returns, so what survives is not at zero Doppler. **Spectrum width** separates
them physically (debris tumbles, median 5.5 m/s against 1.0) but is a weak instrument:
`sw < 2` buys 6–24 % of the speckle and costs debris gates, against 61–87 % for nothing.
Correct physics, poor discrimination — see `docs/verification.md`.

Velocity dealiasing now reaches 85–92 % of Py-ART's correction rate, up from about two
thirds. The fix was structural, and both obvious diagnoses were wrong: a signal-quality
gatefilter discards the wrong gates (corrected gates average 29 dBZ against 16 for
untouched ones), and loosening the boundary threshold buys the rate with ±2 chains that
fabricate 130 m/s winds. What worked was replacing the spanning-tree **walk** over regions
with Py-ART's **merge**: each merge combines the two sides' remaining boundaries, so a
region reached only through several thin boundaries is judged on all of them at once. Three
threshold constants were deleted in the process. Peak unfolded speed *fell*. The remaining
gap is genuine judgement-call difference on marginal folds — see `docs/verification.md`.

The lesson worth keeping: **read the reference implementation before theorising about it.**
Two measured dead ends came from reasoning about what Py-ART must be doing; the answer took
twenty minutes of reading `region_dealias.py`.

Note for storm labels: **NSS (storm structure) has not been distributed since ~2021**, so
`MaxDbz` / `CellBasedVil` / `EchoTopKft` are always null on live data. The layers panel says
so when it detects it — don't "fix" the decoder for this.
