# Test data

All files are NEXRAD Level II data from the AWS Open Data bucket
`unidata-nexrad-level2` (archive) and `unidata-nexrad-level2-chunks` (real-time),
region `us-east-1`, anonymous access. NEXRAD data carries no use restrictions
(NOAA Big Data Project). Re-download with `tools/fetch-testdata.ps1`.

| File | Purpose |
|---|---|
| `KTLX20130520_201643_V06.gz` | Moore, OK tornado (2013-05-20 20:16Z). Dense dual-pol stress case; hook echo present. Pre-2016 `.gz`-wrapped format. |
| `KTLX20260810_181228_V06` | Quiet-weather happy path, current uncompressed-key format. |
| `KTLX20260816_082009_V06` | Archive counterpart of the chunk corpus below — ground truth for chunk-assembly replay tests. |
| `chunks/KTLX/KTLX_1_20260816-082009-*` | One complete real-time volume: `001-S` … `055-E` (55 chunks). |
| `chunks/KTLX/KTLX_2_20260816-082711-*` | First 3 chunks of the next volume — exercises the volume-boundary / rollover path. |

Chunk filenames are the S3 keys with `/` replaced by `_`
(original key: `KTLX/1/20260816-082009-001-S`).
