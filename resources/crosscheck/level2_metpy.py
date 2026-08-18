"""Dump reference values from MetPy's independent NEXRAD decoder for golden tests."""
import sys
import numpy as np
from metpy.io import Level2File

def dump(path, sweep_idx, ray_idx, gate_start, gate_end):
    f = Level2File(path)
    vol = f.sweeps[0][0][1]
    print(f"file: {path}")
    print(f"stid: {f.stid}, dt: {f.dt}")
    print(f"vol lat/lon: {vol.lat!r} {vol.lon!r} alt: {vol.site_amsl + vol.feedhorn_agl} vcp: {vol.vcp}")
    sweep = f.sweeps[sweep_idx]
    print(f"sweep {sweep_idx}: rays={len(sweep)}")
    ray = sweep[ray_idx]
    hdr = ray[0]
    print(f"ray {ray_idx}: az={hdr.az_angle!r} el={hdr.el_angle!r} az_num={hdr.az_num}")
    for name, (bhdr, data) in sorted(ray[4].items()):
        print(f"  {name.decode()}: gates={bhdr.num_gates} first={bhdr.first_gate!r}km "
              f"width={bhdr.gate_width!r}km bits={bhdr.data_size} scale={bhdr.scale!r} offset={bhdr.offset!r}")
        vals = [f"{v:.6f}" if not np.isnan(v) else "nan" for v in data[gate_start:gate_end]]
        print(f"  values[{gate_start}:{gate_end}] = {vals}")

if __name__ == '__main__':
    dump(sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]))
