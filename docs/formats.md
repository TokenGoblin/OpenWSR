# Binary format notes

Field notes on the formats OpenWSR decodes — specifically the parts that are not
obvious from the specification, cost real debugging time, or differ between what the
spec says and what the live feed actually serves.

Specifications are linked in [data-sources.md](data-sources.md). This file is the
delta between those documents and reality.

---

## NEXRAD Level II (Archive II)

Reference: NOAA ICD 2620010H, "Interface Control Document for the Archive II/User".
Implementation: `src/OpenWSR.Nexrad/`.

### Two different file layouts

The single biggest trap. Modern files are LDM-framed: a 24-byte volume header, then a
sequence of records each prefixed with a big-endian int32 length, each bzip2-compressed.
Pre-2016 gzip-era files have **no LDM framing at all** — after the volume header the
messages simply begin, and the whole file is gzip-wrapped.

Detection is by sniffing for the bzip2 magic where the first compressed record would
start:

```csharp
if (bytes.Length >= 31 && bytes[28] == (byte)'B' && bytes[29] == (byte)'Z')
    // LDM records
else
    // raw messages directly after the 24-byte volume header
```

Symptom if you get this wrong: "Truncated LDM record body" on any historical volume.
The Moore 2013-05-20 file is a good regression case — it is the gzip-era layout.

### Message 31 sizing

Message 31 is the digital radar data generic format. Its size field is in halfwords, but
a super-res message can exceed the 65534-halfword range the header field allows. When it
does, the true size lives in the **segment-count fields** rather than the size field.
Reading only the size field silently truncates long radials.

### Data moment blocks

Message 31 begins with a block-pointer table; walk it rather than assuming a fixed order.
Moment blocks (REF, VEL, SW, ZDR, PHI, RHO, CFP) carry a word size of 8 or 16 bits and a
scale/offset pair:

```
value = (raw - offset) / scale
```

Raw codes **0 and 1 are sentinels**, not data — 0 is "below threshold", 1 is "range
folded". Both must become NaN before any arithmetic. Feeding them through the scale
equation produces plausible-looking garbage that survives all the way to the screen, and
smoothing then smears it across real gates.

### VCP elevation angles

Elevation angles in the VCP definition (Message 5) are binary angles, not degrees. The
conversion means the nominal cuts are not round numbers: VCP 35's lowest cut is
**0.4834°**, not 0.5°. A test asserting 0.5 is asserting the wrong thing.

### Volume start time

The volume header timestamp is not the start of data. The first radial's own timestamp
is, and it can differ by seconds. Use the radial.

### SAILS / MRLE

Supplemental scans repeat a low elevation mid-volume. Sweep assembly must key on
(elevation number, azimuth) and tolerate the same nominal elevation appearing more than
once in a volume, otherwise supplemental cuts overwrite the base cut.

### Rotation tracks accumulate a maximum, and that is unforgiving

The swath keeps the strongest shear seen at each point, which is what makes it a track — a
cell that rotated hard once holds that value as the storm moves on. It also means one
spurious gate in one scan survives into the result forever, so each scan is smoothed 3x3
before accumulation. On the Moore volume that alone took the peak from 0.18 to 0.12 1/s.

Signed maximum, not absolute: anticyclonic rotation is real but is a different question,
and folding it in by magnitude would let a strong anticyclonic couplet paint what looks
like a tornado track.

### Azimuthal shear is derived, not decoded

Nothing in Message 31 carries it. `Moment.AzimuthalShear` exists so the product bar and the
palette machinery can treat it like any other product, but the decoder never emits it —
`AzimuthalShear.Compute` derives it from velocity on demand, and `RadarDisplayController`
offers it whenever velocity is present.

Two properties worth knowing before reading one:

- **It always unfolds first**, whatever the velocity toggle says. A fold is a
  2 x V_nyquist step between adjacent radials, and differentiating across it yields shear
  several times larger than any real vortex — the display would be showing its own
  aliasing artefacts rather than the rotation it exists to reveal.
- **Close range is suppressed** below 5 km. A real vortex's shear is its angular velocity
  and does not vary with range, but a *uniform* wind of V reads as V/R, because the same
  velocity difference is spread over a shorter arc. A 30 m/s wind reads 0.015 1/s at 2 km,
  which is mesocyclone territory — so without a floor, ordinary wind over the radar site
  paints the same colour as a tornado.

### Velocity aliasing and the RRAD block

Radial velocity is measured from Doppler phase shift, which wraps. Anything outside
±V_nyquist is reported shifted by a whole number of `2 * V_nyquist` intervals, so the raw
field is hard-clipped at the fold limit — on KTLX 2013-05-20 the 0.5° cut reads exactly
−26.00 to +26.00 m/s and nothing outside it. That pinning is the diagnostic: real data
scatters, folded data saturates.

The Nyquist velocity lives in the **RRAD block** at byte offset 16, as a `u16` in
hundredths of a metre per second. It rides on individual radials and is **not present on
every one**, so take the first radial in the cut that carries it (`VolumeBuilder`), rather
than assuming radial zero has it.

Two traps:

- **Split cuts.** VCP 12/212 scan the low elevations twice — a long-PRT surveillance cut
  carrying reflectivity, then a Doppler cut at the same elevation carrying velocity and
  spectrum width. The Nyquist differs between them (8.3 vs 26.12 m/s on the same volume),
  and the surveillance cut has no velocity at all. Py-ART's sweep 0 is the surveillance
  cut; asking it to dealias yields an empty array.
- **No dual-pol on the Doppler cut.** The low Doppler cuts carry no correlation
  coefficient, so the usual quality mask for dealiasing is unavailable there. Spectrum
  width is co-located and is the only quality field you get.

Pre-2000 archives frequently carry no RRAD block at all. `Sweep.NyquistMs` is nullable for
exactly this reason, and unfolding is skipped rather than guessing an interval.

### Real-time chunks

The chunks bucket serves a volume as S (start) / I (intermediate) / E (end) parts. They
do not arrive in order. `LiveVolumeAssembler` is order-independent by design and is
regression-tested by replaying the committed 58-chunk corpus in randomised order and
asserting the result matches the archive volume for the same scan float-for-float.

---

## NEXRAD Level III

Implementation: `src/OpenWSR.Nexrad/Level3/`.

### Compression is bzip2, not just zlib

The symbology block may be zlib-compressed *or* bzip2-compressed (`BZh` magic). The live
feed uses bzip2. Handling only zlib fails on everything current.

### Packet types

Symbology is a sequence of typed packets. OpenWSR decodes 2, 8, 15, 16, 19, 20, 23, 24.
Packet 16 is the radial image used by DVL (digital VIL) and carries **packed float16
thresholds** in its header — not the plain scale/offset of the older packet types.

### Stand-alone tabular layout

Some products (NSS, product 62) have no symbology block at all. Their tabular-block
pointer sits in the slot the header otherwise uses for the symbology offset. Reading that
slot as a symbology offset walks off into nonsense.

### NSS is discontinued

The storm-structure product has not appeared in the live feed since roughly 2021. The
decoder is retained because it still works on archive data, but nothing current will
exercise it. This is why live per-cell max-dBZ labels usually show an ID only, and why
the hotspot scanner was rebuilt on DVL instead — a scan across all 163 sites returned
0 NSS products and 147 DVL.

---

## GRIB2

Reference: WMO FM-92 GRIB edition 2. Implementation: `src/OpenWSR.Grib2/`.

### Sections and templates implemented

| | |
|---|---|
| Grid definition (§3) | template 3.0 lat/lon, 3.30 Lambert conformal |
| Data representation (§5) | 5.0 simple, 5.2/5.3 complex + spatial differencing, 5.41 PNG |

### Sign-and-magnitude scale factors

GRIB2 scale factors are **sign-and-magnitude**, not two's complement. The high bit is the
sign; the remaining bits are the magnitude. Decoding them as two's complement gives
wildly wrong values for every negative exponent, which is most of them.

### Complex packing with spatial differencing

Template 5.3 stores differences, and the reconstruction differs by order:

```csharp
if (drs.SpatialOrder == 1) {
    output[0] = firstValue;
    for (int i = 1; i < count; i++)
        output[i] += minimumDifference + output[i - 1];
} else {
    output[0] = firstValue;
    if (count > 1) output[1] = secondValue;
    for (int i = 2; i < count; i++)
        output[i] += minimumDifference + 2 * output[i - 1] - output[i - 2];
}
```

`minimumDifference` is itself sign-and-magnitude encoded. HRRR uses this path; it is the
hard one. MRMS uses PNG packing and is comparatively easy.

### PNG packing without an imaging dependency

Template 5.41 wraps the grid in a greyscale PNG. To keep `OpenWSR.Grib2` pure (no WPF, no
`System.Drawing`), it ships `MiniPng.cs`, a minimal greyscale-only PNG decoder. It handles
8- and 16-bit depths and the filter types PNG actually uses for this data.

### Byte-range fetching via .idx

A full HRRR surface file is hundreds of MB. Every file has a companion `.idx` sidecar
listing each record's byte offset. Fetching only the REFC record is about **140 kB per
forecast hour**, which is what makes a six-hour future-radar loop practical.

---

## NetCDF-4 / HDF5 (GOES lightning)

GLM files are NetCDF-4, which is HDF5 underneath. `MiniHdf5` implements only the slice
those files use — the same reasoning that put `MiniPng` inside `OpenWSR.Grib2`, since
taking a full HDF5 stack for four arrays of floats would be the tail wagging the dog.

### What is implemented, and what a GLM file actually uses

Superblock v2; version-2 object headers **with continuation blocks**; dense links in a
fractal heap; version-2 dataspaces; version-1 datatypes; version-3 layouts, both contiguous
and chunked with a version-1 B-tree index; the shuffle and deflate filters. Anything else
raises rather than guessing.

### The traps

- **Root group address is at superblock offset 36**, not 40. Reading 40 lands four bytes
  into the field and picks up the checksum, which then looks like a wild continuation
  address. This cost the first hour.
- **Filter pipeline v2 omits the name-length field** for filter IDs below 256, so its
  per-filter header is 6 bytes where v1's is 8. Parsing v2 as v1 walks off by two and reads
  a neighbouring value as the filter ID — the symptom was a complaint about "filter 5"
  (nbit) on a dataset that only uses shuffle and deflate.
- **Object headers continue.** Any dataset with a few attributes spills into an `OCHK`
  block, and the data layout message is frequently in the continuation rather than the
  first chunk. Not following them makes datasets silently read as empty.
- **The deflate filter writes zlib streams**, header and Adler-32 included — `ZLibStream`,
  not a raw `DeflateStream`.
- **Shuffle is not compression.** It stores every element's first byte, then every second
  byte, so neighbouring values share high bytes and deflate does better. Undo it after
  inflating, not before.

### Skipping the B-tree on purpose

Dense links live in a fractal heap with a version-2 B-tree beside it for lookup by name.
Since every link in the group is wanted anyway, the heap's direct blocks are walked and the
B-tree is never touched — removing the single most intricate structure in the format. The
heap does not delimit its objects (the B-tree's heap IDs carry the offsets), so each
candidate position is validated as a link record and the scan slides a byte on failure. On
the committed GLM file this recovers all 54 links with nothing missing and nothing spurious.

### Attributes are not read

Which means scale factors are not either. `flash_energy`'s scale and offset are hard-coded
from the published L2 specification — they are fixed for the product, not per-file. If a
future field needs a per-file scale, attribute parsing is the piece to add.

## GRLevelX placefiles

Reference: `grlevelx.com/manuals/gis/files_places.htm` (needs a browser User-Agent;
`WebFetch` gets a 403). Implementation: `src/OpenWSR.Placefiles/`.

### Object blocks switch coordinates to pixels

Inside an `Object:` block, the coordinates on `Text:` and `Icon:` statements are **pixel
offsets from the object's anchor**, not degrees. Outside a block they are lat/lon. Same
statement, different coordinate space depending on context:

```csharp
items.Add(new PlacefileLabel(
    anchor?.Lat ?? x, anchor?.Lon ?? y,
    parts[3].Trim().Trim('"'), fontNumber,
    anchor is null ? 0 : x, anchor is null ? 0 : y, ...));
```

### Comments respect quotes

`;` starts a comment — but not inside a quoted string, and label text frequently contains
semicolons. Stripping naively truncates labels:

```csharp
for (int i = 0; i < line.Length; i++) {
    if (line[i] == '"') inQuotes = !inQuotes;
    else if (line[i] == ';' && !inQuotes) return line[..i].Trim();
}
```

### Query parameters

GR appends `?lat=&lon=&version=` when fetching a placefile URL, and many server-generated
placefiles vary their output based on them. OpenWSR does the same.

### Not implemented

`Triangles` and `Image` blocks are parsed and skipped with a report rather than failing
the file. Icon *sheets* are not downloaded, so `Icon` statements draw as generic markers.

---

## Odds and ends

- **SPC GeoJSON carries a UTF-8 BOM** that `System.Text.Json` rejects outright. Skip it
  before parsing.
- **Level III fallback feeds** exist at Iowa State and TGFTP if the AWS bucket is
  unavailable.
- The **cone of silence** above a radar is a real geometric hole, not a decode bug — the
  highest cut still leaves a cone directly overhead unsampled. The cross-section tests
  assert it is empty.
