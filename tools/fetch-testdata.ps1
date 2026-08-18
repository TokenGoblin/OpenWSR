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
Write-Host 'Done.'
