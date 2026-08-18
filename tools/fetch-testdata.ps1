# Re-downloads the committed test data from AWS Open Data (anonymous access).
# Run from the repo root: powershell -File tools/fetch-testdata.ps1
$ErrorActionPreference = 'Stop'
$archive = 'https://unidata-nexrad-level2.s3.amazonaws.com'
$chunks  = 'https://unidata-nexrad-level2-chunks.s3.amazonaws.com'
$dest    = 'assets/testdata'

New-Item -ItemType Directory -Force "$dest/chunks/KTLX" | Out-Null

curl.exe -sf -o "$dest/KTLX20130520_201643_V06.gz" "$archive/2013/05/20/KTLX/KTLX20130520_201643_V06.gz"
curl.exe -sf -o "$dest/KTLX20260810_181228_V06"    "$archive/2026/08/10/KTLX/KTLX20260810_181228_V06"
curl.exe -sf -o "$dest/KTLX20260816_082009_V06"    "$archive/2026/08/16/KTLX/KTLX20260816_082009_V06"

# NOTE: the chunks bucket only retains recent data. If these keys have aged out,
# the committed copies in git are the only source — do not delete them.
foreach ($i in 1..55) {
    $type = if ($i -eq 1) { 'S' } elseif ($i -eq 55) { 'E' } else { 'I' }
    $key = 'KTLX/1/20260816-082009-{0:d3}-{1}' -f $i, $type
    curl.exe -sf -o "$dest/chunks/KTLX/$($key -replace '/', '_')" "$chunks/$key"
}
foreach ($i in 1..3) {
    $type = if ($i -eq 1) { 'S' } else { 'I' }
    $key = 'KTLX/2/20260816-082711-{0:d3}-{1}' -f $i, $type
    curl.exe -sf -o "$dest/chunks/KTLX/$($key -replace '/', '_')" "$chunks/$key"
}
# Level III storm-product golden files (TLX 2021-10-11 severe weather event)
New-Item -ItemType Directory -Force "$dest/level3" | Out-Null
$level3 = 'https://unidata-nexrad-level3.s3.amazonaws.com'
foreach ($prod in @('NST', 'NHI', 'NMD')) {
    $key = "TLX_${prod}_2021_10_11_01_49_20"
    curl.exe -sf -o "$dest/level3/$key" "$level3/$key"
}
# GRIB2 golden files: MRMS (PNG packing) and HRRR REFC (complex packing, Lambert grid).
New-Item -ItemType Directory -Force "$dest/grib2" | Out-Null
curl.exe -sf -o "$dest/grib2/MRMS_MergedReflectivityQCComposite_20260818-191441.grib2.gz" `
    "https://noaa-mrms-pds.s3.amazonaws.com/CONUS/MergedReflectivityQCComposite_00.50/20260818/MRMS_MergedReflectivityQCComposite_00.50_20260818-191441.grib2.gz"
# The HRRR file is a byte range (the REFC record only) and AWS retains cycles for a
# limited window, so this one may 404 once the day rolls off. The committed copy is
# the source of truth for the tests.
curl.exe -sf -r 0-136839 -o "$dest/grib2/HRRR_REFC_20260818_t18z_f01.grib2" `
    "https://noaa-hrrr-bdp-pds.s3.amazonaws.com/hrrr.20260818/conus/hrrr.t18z.wrfsfcf01.grib2"

# Community placefiles used by the parser tests.
New-Item -ItemType Directory -Force "$dest/placefiles" | Out-Null
curl.exe -sf -o "$dest/placefiles/iem_asos.txt" `
    "https://mesonet.agron.iastate.edu/request/grx/asos.php"
curl.exe -sf -o "$dest/placefiles/iem_time_mot_loc.txt" `
    "https://mesonet.agron.iastate.edu/request/grx/time_mot_loc.txt?all"

Write-Host 'Done.'
