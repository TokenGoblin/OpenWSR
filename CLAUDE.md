# OpenWSR — working notes

A native Windows NEXRAD radar viewer: .NET 10, WPF shell, Direct3D 11 via Vortice, own
Web Mercator tile engine. Reads only public, unauthenticated data. See `README.md` for
what it does and `TASKS.md` for the build history.

## Commands

```
dotnet build OpenWSR.slnx                    # NOTE: .slnx, not .sln
dotnet test OpenWSR.slnx                     # 76 tests
dotnet run --project src/OpenWSR.App
dotnet publish src/OpenWSR.App -c Release    # single-file self-contained exe
```

The published and Debug binaries are both **`OpenWSR.exe`** (AssemblyName is set), not
`OpenWSR.App.exe`.

## Architecture and the rules that hold it together

```
Nexrad ──┐
Grib2 ───┼─→ Ingest ──┐
Geo ─────┤            ├─→ App
Placefiles┤  Render ──┘
Palettes ─┘
```

**Purity is enforced by a test.** `Nexrad`, `Geo`, `Grib2` and `Placefiles` must not
reference WPF, Direct3D or the network. `PurityTests` asserts this against assembly
references — if you need imaging or HTTP in one of them, that is a signal the code
belongs somewhere else. `MiniPng` exists inside `Grib2` precisely because of this rule.

**Threading.** The UI thread does UI. The render thread owns the D3D device context and
every D3D object. Ingest and decode run on the thread pool. Cross boundaries with
channels, immutable records, or a lock — never by touching a D3D object off the render
thread. Anything the render thread must do for a caller (frame capture, texture upload)
is queued as a request and serviced after present.

## Things that cost real time to discover

**Airspace.** The D3D child HWND always draws above WPF content inside its rectangle.
WPF controls cannot overlay the map. Anything that must appear *over* the map is either
drawn in the D3D scene (the colour scale, storm labels — see `GlyphAtlas` and
`DrawLegend`) or is a `Popup`/`ToolTip`, which get their own HWNDs. Docked panels beside
or below the map are fine.

**Screenshots lie unless the capturing process is DPI-aware.** PowerShell is not, so
`GetWindowRect` and `CopyFromScreen` return virtualised coordinates and you capture about
two-thirds of the window — which looks exactly like a broken layout. Always
`SetProcessDPIAware()` first. Several hours went into chasing a phantom layout bug here.

**WPF's default control templates are unreadable on a dark ground.** `Theme.xaml`
provides explicit templates for Button, ToggleButton, ComboBox, TextBox, CheckBox,
Slider, DatePicker and ScrollBar. Style through it; don't hardcode colours.

**XAML event handlers fire during `InitializeComponent`.** A filter handler that touches
a control declared later in the file will hit a null. Guard every control it reads.

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

**GRIB2.** MRMS uses PNG packing (template 5.41) — easy. HRRR uses complex packing with
second-order spatial differencing (5.3) on a Lambert grid (3.30) — the hard path, and the
one to be careful with. Scale factors are sign-and-magnitude, not two's complement. Fetch
only the field you need using the `.idx` sidecar and an HTTP range request.

**Placefiles.** Inside an `Object` block, "lat, lon" are **pixel offsets** from the
anchor with +y upward, not degrees. Polygon contours close on a repeated first point and
the next point starts a new contour. `;` starts a comment except inside quotes. The spec
at grlevelx.com needs a browser User-Agent — WebFetch gets a 403.

**Endpoints drift.** Two IEM paths verified earlier in development had been retired by
the time they were wired up. When something returns HTML instead of data, check
`https://mesonet.agron.iastate.edu/api/1/openapi.json` for the current path. SPC serves
GeoJSON with a **UTF-8 BOM**, which `System.Text.Json` rejects outright.

## How to verify work here

The project's standard is to check binary decoding against an **independent reference
implementation**, not against its own output. Follow it:

- NEXRAD Level II and Level III → **MetPy** (`pip install metpy`)
- GRIB2 → **ecCodes** (`pip install eccodes`), which also has a grid iterator useful for
  validating projections

Write the reference values into the test as literals with a comment naming the source.
When no reference exists — the cross-section, for instance — assert **physics** instead:
the cone of silence must be empty, beams must climb with range.

Live features are verified against live weather, not mocks. `🎯 Hotspot` finds the
heaviest precipitation in the country, which is the fastest way to get real data on
screen. `--soak` runs the live pipeline headless.

## Conventions

- Comments explain constraints and *why*, never what the next line does.
- Prefer records and immutability for anything crossing a thread boundary.
- User-facing text says what happened in plain language; errors say how to fix it.
- Commit at feature boundaries with a message that explains the reasoning, and update
  `TASKS.md` in the same commit.
- Units: `Units.System` drives all display formatting. Never hardcode km or miles in
  user-facing strings.

## Open work

Lightning (GOES GLM, needs NetCDF), MRMS native rendering (reader is done and
golden-tested; only the draw path is missing), velocity dealiasing, azimuthal shear, VWP
panel, placefile icon sheets, drawing tools, 3D volume rendering. Animated GIF export is
written but has never been run end-to-end.
