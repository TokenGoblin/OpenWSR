"""Cross-check velocity dealiasing against Py-ART's independent implementation.

OpenWSR's `VelocityDealiasing` is region-based, after Py-ART's `dealias_region_based`.
This dumps Py-ART's answer for the same sweep so the two can be compared.

    pip install arm-pyart
    python dealias_pyart.py ../../assets/testdata/KTLX20130520_201643_V06.gz 0

Gate-for-gate identity is NOT the bar and should not be expected: the two differ in
region-labelling order, tie-breaking, and how weak boundaries are treated. What must
agree is the structure — the Nyquist velocity, which gates get corrected, and the sign
and magnitude of the correction where both are confident. Read the AGREEMENT block: a
high agreement fraction plus matching peak magnitudes is the pass condition, and any
gate where the two differ by a whole interval is worth looking at by hand.

Note that Py-ART is normally run with a gatefilter (excluding low-SNR or low-CC gates).
This script deliberately runs without one, because OpenWSR does too — the low-elevation
Doppler split cuts of VCP 12/212 carry no correlation coefficient to filter on.
"""
import sys

import numpy as np
import pyart


def dump(path, sweep_index):
    radar = pyart.io.read_nexrad_archive(path)
    print(f"file: {path}")
    print(f"sweeps: {radar.nsweeps}  fields: {sorted(radar.fields)}")

    # VCP 12/212 split the low cuts: a long-PRT surveillance sweep carrying reflectivity
    # only, then a Doppler sweep at the same elevation carrying velocity. Only the latter
    # can be dealiased, so say which sweeps those are rather than failing on an empty one.
    usable = []
    for s in range(radar.nsweeps):
        lo, hi = radar.get_start_end(s)
        v = radar.fields["velocity"]["data"][lo : hi + 1]
        n = int((~np.ma.getmaskarray(v)).sum())
        if n:
            usable.append((s, radar.fixed_angle["data"][s], n))
    print("sweeps carrying velocity: " + ", ".join(f"{s}({e:.2f}deg,{n})" for s, e, n in usable))
    if sweep_index not in [s for s, _, _ in usable]:
        print(f"\nsweep {sweep_index} carries no velocity — pick one of the above.")
        return

    nyquist = radar.get_nyquist_vel(sweep_index, check_uniform=False)
    interval = 2.0 * nyquist
    print(f"sweep {sweep_index}: elevation={radar.fixed_angle['data'][sweep_index]:.2f} deg")
    print(f"  nyquist={nyquist:.4f} m/s  interval={interval:.4f} m/s")

    dealiased = pyart.correct.dealias_region_based(
        radar, vel_field="velocity", nyquist_vel=nyquist, keep_original=False
    )
    radar.add_field("dealiased", dealiased, replace_existing=True)

    start, end = radar.get_start_end(sweep_index)
    raw = radar.fields["velocity"]["data"][start : end + 1]
    fixed = radar.fields["dealiased"]["data"][start : end + 1]

    valid = ~np.ma.getmaskarray(raw) & ~np.ma.getmaskarray(fixed)
    shifts = np.zeros(raw.shape)
    shifts[valid] = np.round((fixed[valid] - raw[valid]) / interval)

    print(f"  rays={raw.shape[0]} gates={raw.shape[1]} valid={int(valid.sum())}")
    print(f"  raw     min={raw[valid].min():.2f} max={raw[valid].max():.2f}")
    print(f"  dealias min={fixed[valid].min():.2f} max={fixed[valid].max():.2f}")

    print("  shift histogram (whole intervals -> gates):")
    for k in np.unique(shifts[valid]):
        print(f"    {int(k):+d} -> {int((shifts[valid] == k).sum())}")

    corrected = int((shifts[valid] != 0).sum())
    print(f"  corrected={corrected} ({100.0 * corrected / valid.sum():.2f}% of valid gates)")

    # Where the corrections sit. A correct unfold clusters in coherent sectors; scattered
    # single-gate corrections are the signature of noise being amplified.
    rays, gates = np.nonzero((shifts != 0) & valid)
    if len(rays):
        az = radar.azimuth["data"][start : end + 1][rays]
        rng_km = radar.range["data"][gates] / 1000.0
        print(f"  corrected range: {rng_km.min():.0f}-{rng_km.max():.0f} km, mean {rng_km.mean():.0f} km")
        hist, edges = np.histogram(az, bins=24, range=(0, 360))
        top = np.argsort(hist)[::-1][:6]
        print("  corrected by azimuth sector:")
        for i in sorted(top):
            if hist[i]:
                print(f"    {edges[i]:3.0f}-{edges[i+1]:3.0f} -> {hist[i]}")


if __name__ == "__main__":
    dump(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 0)
