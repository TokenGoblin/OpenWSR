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

`fetch-spec-page.py` lives here too, and is not a cross-check script — it is the fetcher
that gets around specification sites which reject automated clients. grlevelx.com returns
**403 to any non-browser User-Agent**, so `WebFetch` and `curl` defaults both fail on the
placefile and colour-table specs. This sends a browser User-Agent and flattens the result
to text:

```
python resources/crosscheck/fetch-spec-page.py https://www.grlevelx.com/manuals/gis/files_places.htm
```

## `reference/`

`nexrad-stations.txt` — the NCEI HOMR station table: all 163 WSR-88D sites with
coordinates and elevations. The table the app ships is embedded in `OpenWSR.Ingest`; this
is the source it was derived from, kept so it can be regenerated or audited.

## `research/`

Source material behind [docs/parity.md](../docs/parity.md). Kept because it is either
hard to re-fetch or was expensive to gather, and because a ranked gap list is much less
useful without the evidence under it.

| File | Provenance |
|---|---|
| `supercell-wx-open-issues.txt` | Full enumeration of Supercell Wx's open GitHub issues. Public issue-tracker text, and a user-validated list of what people miss versus GRLevel3 and RadarScope |
| `grlevelx-color-tables.txt` | GR colour table documentation — behind the 403 |
| `grlevelx-algorithms-menu.txt` | GR2Analyst algorithms menu — behind the 403 |
| `gr2analyst-2-user-guide.txt` | GR2Analyst 2 user guide index — behind the 403 |

Third-party excerpts retained for reference, not redistribution.

---

## Kept outside the repository

`../../OpenWSR-reference/` (a sibling of the repo, not under git) holds bulky third-party
material: the RadarOmega user guide PDFs (~48 MB of commercial vendor documentation) and
a clone of the Supercell Wx docs. Both informed the parity scan; neither is ours to
redistribute, and neither belongs in git history where it could never be removed. See
that folder's own README. Nothing there is needed to build, test or run OpenWSR.
