# Build log screenshots

Working captures kept from the build, as opposed to the curated capability shots one
level up. Most are only interesting as a record of how something got to where it is, but
several document bugs whose *symptoms* are worth recognising again.

## The ones worth knowing about

| File | What it shows |
|---|---|
| `ui-dpi.jpg` | The phantom layout bug. PowerShell is DPI-unaware, so `GetWindowRect` and `CopyFromScreen` return virtualised coordinates and capture roughly two-thirds of the window — which looks exactly like a broken layout with the panel and timeline missing. Nothing was wrong with the app. Call `SetProcessDPIAware()` first. Several hours went into this |
| `w1100.jpg`, `w1400.jpg`, `w1920.jpg` | Responsive checks at three window widths, from parity UI finding 11 |
| `crop-legend.jpg`, `crop-right.jpg` | Crops used to confirm the D3D-drawn colour scale landed correctly, since it is drawn in the scene rather than as a WPF control |
| `wundermap.jpg`, `wundermap2.jpg` | Wunderground's WunderMap — the reference the smoothing slider was matched against |
| `zoom-raw.jpg`, `zoom-smooth.jpg` | Smoothing at street zoom, the case that exposed sentinel gates being smeared into real returns |
| `cones*.jpg` | Six iterations of the storm projection cone geometry |
| `xsec.jpg`, `xsec2.jpg` | Cross-section before the fix — sampling only the nearest elevation cut produces isolated diagonal streaks. Compare with `../cross-section.jpg` |
| `p1-*.jpg` | Phase 01 UI restyle in progress |
| `phase3*.jpg`, `phase4.jpg`, `phase7*.jpg` | Phase progression |
| `capture-test.jpg` | First successful backbuffer readback through the render-thread request queue |

Converted to JPEG at 1400 px wide to keep the repo reasonable; the originals were PNG
screen captures totalling about 47 MB.
