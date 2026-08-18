# Resources

Runnable inputs and reference data that support development but aren't part of the build.
Prose documentation lives in [`docs/`](../docs/).

Nothing here is referenced by the application at runtime — deleting this folder would not
break the build. It would, however, lose the tooling that backs every "golden-tested"
claim in the repo, which is why it is committed rather than left in a scratch directory.

---

## `crosscheck/`

Dump scripts that print reference values from independent implementations, for pasting
into tests as expectations. This is how the decoders are validated — see
[docs/verification.md](../docs/verification.md) for the full methodology.

```
pip install metpy eccodes
```

| Script | Reference | Usage |
|---|---|---|
| `level2_metpy.py` | MetPy `Level2File` | `python level2_metpy.py <file> <sweep> <ray> <gate_start> <gate_end>` |
| `level3_metpy.py` | MetPy `Level3File` | `python level3_metpy.py <file>...` |
| `grib2_walk.py` | *(none — structural)* | `python grib2_walk.py <file.grib2[.gz]>` |

`grib2_walk.py` is a walker, not a decoder: it prints the section layout, grid and packing
templates, and packing parameters of a GRIB2 message. Run it against an unfamiliar file
*before* writing decode code, to find out which templates you actually need. It handles
gzipped input.

> **Caveat:** it reads Section 3's coordinate fields at their template-3.0 (lat/lon)
> offsets, so for a Lambert file the `la1`/`lo1`/`Di`/`Dj` line prints garbage — on HRRR
> you'll see `la1=134.368118`, which is not a latitude. The template number, grid
> dimensions, point count, scan mode and everything in Sections 4/5 are correct and are
> what the script is for. For real Lambert coordinates use ecCodes:
> `grib_get -p latitudeOfFirstGridPointInDegrees,LoVInDegrees,LaDInDegrees <file>`.

For value-level GRIB2 comparison, ecCodes' own CLI is simpler than any script:

```
grib_get_data assets/testdata/grib2/HRRR_REFC_20260818_t18z_f01.grib2 | head -50
```

## `placefiles/`

`render-test.txt` — a hand-written GRLevelX placefile exercising the statements a real
community file uses: filled polygon, thick multi-point line, labelled places, an `Object:`
block with a pixel-offset `Text:` and `Icon:`, a custom `Font:`, and a `Threshold:` low
enough that its final item should stay hidden at national zoom and appear on zoom-in.

Load it from the layers panel to check the render path end to end. Real server-generated
placefiles for parser testing are committed under `assets/testdata/placefiles/`, and
`tools/fetch-testdata.ps1` re-fetches them.

## `reference/`

`nexrad-stations.txt` — the NCEI HOMR station table: all 163 WSR-88D sites with
coordinates and elevations. The table the app ships is embedded in `OpenWSR.Ingest`; this
is the source it was derived from, kept so it can be regenerated or audited.

---

## Deliberately not committed

- **NOAA RPG Operator's Guide PDFs** — large, public, and stable. Linked from
  [docs/data-sources.md](../docs/data-sources.md) instead.
- **Supercell Wx documentation and issue exports** — third-party, separately licensed.
  They informed [docs/parity.md](../docs/parity.md); the findings are recorded there.
- **GRLevelX manual pages** — third-party. Note that they reject non-browser
  User-Agents with a 403, so `WebFetch`-style tools fail on them.
