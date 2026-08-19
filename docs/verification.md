# How the decoders are verified

The rule for every binary format in this repo: **check it against an independent
implementation, not against its own output.** A decoder tested only on values it
produced itself will confirm its own bugs. Where no independent reference exists, assert
physics instead.

76 tests, all green: 34 in `OpenWSR.Geo.Tests`, 42 in `OpenWSR.Nexrad.Tests`.

```
dotnet test OpenWSR.slnx
```

---

## The references

| Format | Reference | Why this one |
|---|---|---|
| NEXRAD Level II | [MetPy](https://unidata.github.io/MetPy/) `metpy.io.Level2File` | Unidata's own decoder; widely used in research |
| NEXRAD Level III | MetPy `metpy.io.Level3File` | Same |
| GRIB2 | [ecCodes](https://confluence.ecmwf.int/display/ECC) | ECMWF's reference decoder — the de-facto ground truth |
| Lambert projection | ecCodes' own grid iterator | Compares against the projection the data producer assumes |
| Chunk assembly | The archive file for the same scan | Two independent paths to the same volume |
| Cross-section | *(none exists)* | Physics assertions instead |

Setup:

```
pip install metpy eccodes
```

---

## Running a cross-check

The dump scripts live in [`resources/crosscheck/`](../resources/crosscheck/). Each prints
reference values in a form you can paste into a test as an expectation.

```
python resources/crosscheck/level2_metpy.py assets/testdata/KTLX20260816_082009_V06 0 0 0 20
python resources/crosscheck/level3_metpy.py assets/testdata/level3/TLX_NST_2021_10_11_01_49_20
python resources/crosscheck/grib2_walk.py   assets/testdata/grib2/HRRR_REFC_20260818_t18z_f01.grib2
```

`grib2_walk.py` is a structural walker rather than a decoder — it prints the section
layout, templates and packing parameters of a GRIB2 message, which is how you find out
*which* template you actually need to implement before writing any code.

For ecCodes value comparison, `grib_get_data` is usually enough:

```
grib_get_data assets/testdata/grib2/HRRR_REFC_20260818_t18z_f01.grib2 | head -50
```

---

## What each test class actually asserts

**Level II** — station ID, timestamp, site lat/lon/altitude, VCP number, sweep count,
per-sweep elevation angles, and a window of individual gate values from a chosen sweep
and ray, all matched to MetPy. Both 8-bit and 16-bit moments, both file layouts.

**Level III** — product code, site, lat/lon/height, VCP, metadata dictionary, and decoded
symbology contents for NST, NHI, NMD, NSS and DVL, matched to MetPy.

**GRIB2** — full field statistics and spot values against ecCodes for both packing paths:
MRMS (PNG, template 5.41, lat/lon grid) and HRRR REFC (complex packing with spatial
differencing, template 5.3, Lambert grid). Getting both right means the sign-and-magnitude
and spatial-differencing paths are genuinely exercised.

**Lambert conformal** — grid point coordinates matched to ecCodes' iterator, agreeing to
about **11 m on a 3 km grid**, i.e. well under a thousandth of a cell.

**Chunk assembly** — the committed 58-chunk real-time corpus is replayed in randomised
order and the assembled volume is compared **float-for-float** against the archive file
for the same scan. This is the strongest test in the repo: two entirely different
ingestion paths must converge on identical data.

**Cross-section** — no reference implementation exists, so the tests assert what the
physics requires: nothing is sampled in the cone of silence directly overhead; low
altitude coverage thins with range as the lowest beam climbs; and interpolation between
bracketing elevation cuts is continuous.

**Purity** — `OpenWSR.Nexrad`, `OpenWSR.Geo`, `OpenWSR.Grib2` and `OpenWSR.Placefiles`
are asserted at the assembly-reference level to depend on no WPF, no Vortice, and no
networking. This is what keeps the decoders testable headlessly.

---

### Velocity dealiasing — Py-ART, and why not gate-for-gate

Dealiasing is the one decoder-adjacent feature where matching the reference exactly is the
*wrong* bar. Region-based dealiasing makes a chain of discrete judgement calls, and two
correct implementations legitimately differ on which marginal folds they commit to.

`resources/crosscheck/dealias_pyart.py` dumps Py-ART 2.2.5's `dealias_region_based` on the
same sweep. What is asserted in `VelocityDealiasingGoldenTests` is structural agreement:

| | Py-ART | OpenWSR |
|---|---|---|
| Nyquist | 26.1200 m/s | identical |
| Valid gates (0.5° cut) | 155 912 | identical |
| Raw min/max | −26.00 / +26.00 | identical |
| Shift values used | −1, 0, +1 | identical |
| Corrected, 1.3° cut | 4 116 (2.71 %) | 2 788 (1.84 %) |
| Corrected mean range | 176 km | 171 km |
| Top azimuth sectors | 30-45, 210-225, 195-210, 15-30 | same, same order |

OpenWSR is deliberately the more conservative of the two, correcting roughly two thirds as
many gates. It demands a boundary of at least five gate pairs before letting a correction
cross. That number is measured rather than chosen — sweeping it on the Moore volume:

| `MinBoundaryGates` | corrected (0.5° cut) | largest shift | peak |
|---|---|---|---|
| 5 | 0.47 % | ±1 | 78.2 m/s |
| 3 | 0.96 % | ±2 | 130.5 m/s |
| 2 | 1.01 % | ±2 | 130.5 m/s |

There is a cliff between 3 and 5. Below it the rate matches Py-ART's 0.89 % almost exactly
— and the algorithm starts chaining corrections through already-shifted regions to reach
±2 intervals, which no single boundary can justify since the raw field spans exactly one.
A 130 m/s wind is two stacked guesses, not a measurement. Refusing to unfold leaves a
measured value in place; unfolding wrongly invents one, so the conservative side is the
right one to err on.

It is worth stating what this is *not*, because it looks like a signal-quality problem and
is not one: the corrected gates average 29.3 dBZ against 15.8 dBZ for the untouched ones,
and at 250 km the tenth percentile is still 14.5 dBZ. A gatefilter on weak returns would
discard the wrong gates entirely.

The strongest tests are not the reference comparison at all — they are synthetic. A uniform
wind field faster than Nyquist is folded, dealiased, and compared against the truth it was
built from. Since dealiasing can only ever recover a field up to one global interval, the
assertion is that `recovered − truth` is the *same* whole interval at every gate: that is
the shape being exactly right.

## A note on test expectations

Six tests failed on first write during the build, and **all six were wrong
expectations rather than wrong code**:

- VCP 35's lowest cut is 0.4834°, not the 0.5° the round-number assumption suggests.
- Volume start time comes from the first radial, not the header stamp.
- Beam height needed the exact 4/3-earth curvature terms, not the small-angle
  approximation.
- Palette colour stops do not land exactly on the 256-entry sampling grid.

The lesson worth keeping: when a golden test disagrees with a reference implementation,
confirm which side is wrong before touching the decoder. Half the time here it was
the test.

---

## Live verification

Golden files prove the decoder; they do not prove the pipeline. These were checked
against real weather as it happened:

- **30-minute live soak** — `--soak KTLX 30`, 8/8 volumes assembled, 0 errors. Log:
  [`evidence-live-soak-30min.log`](evidence-live-soak-30min.log).
- **Hotspot scan** — 147 of 163 sites reporting DVL, peak at KAMX with VIL 47.5.
- **Proximity alerts** — fired correctly on live KEAX convection.
- **Future radar** — HRRR 19z run decoded and resampled.
- **Geocoding** — ZIP 84058 resolves to Orem UT and the nearest radar KMTX.

```
dotnet run --project src/OpenWSR.Harness -- --soak KTLX 30
```
