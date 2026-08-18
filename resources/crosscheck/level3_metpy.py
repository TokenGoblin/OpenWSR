"""Dump MetPy Level3File reference values for golden tests."""
import sys
from metpy.io import Level3File

for path in sys.argv[1:]:
    f = Level3File(path)
    print(f"=== {path}")
    print(f"product: {f.prod_desc.prod_code}  site: {f.siteID}  "
          f"lat/lon: {f.lat!r} {f.lon!r}  height: {f.height}")
    print(f"vcp: {f.prod_desc.vcp}  mode: {f.prod_desc.op_mode}")
    print(f"metadata: {f.metadata}")
    for li, layer in enumerate(f.sym_block if hasattr(f, 'sym_block') else []):
        for pi, packet in enumerate(layer):
            s = str(packet)
            print(f"  sym[{li}][{pi}]: {s[:500]}")
    if hasattr(f, 'graph_pages'):
        for gi, page in enumerate(f.graph_pages[:2]):
            print(f"  graph[{gi}]: {str(page)[:400]}")
