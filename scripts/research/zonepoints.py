#!/usr/bin/env python3
"""zonepoints.py [zones...]: read the zone ids the server's own map files (data/map/*.nfmap) store per cell and write one standing point per
(zone, world) to data/research/zone-points.tsv: zone, world, x, y, z, samples. The point is the zone sample nearest to the mean of all samples
of that zone in that world, so it lies inside the zone (GridEntity.OnRelocate resolves the same zone id there). Heights come from the same
cells' height grid. Prints a summary (<= 30 lines); with zone ids as arguments only reports those."""
import os, struct, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load

MAP = os.path.join(os.path.dirname(__file__), '..', '..', 'data', 'map')
OUT = os.path.join(os.path.dirname(__file__), '..', '..', 'data', 'research', 'zone-points.tsv')
ORIGIN, GRID, CELL = 64, 512, 32          # MapDefines: WorldGridOrigin (WorldGridCount 128 / 2), GridSize, GridCellSize
BOUND = {0x20: 0, 0x08: 1, 0x10: 2}       # ZoneBoundFlags WorldZone0/1/2 -> worldZoneIds index

worlds = {}
for w in load('World'):
    asset = (w.get('assetPath') or '')
    if asset:
        worlds.setdefault(os.path.basename(asset.replace('\\', '/')).lower(), []).append(w['ID'])


def rstr(b, o):
    n = s = 0
    while True:
        c = b[o]; o += 1; n |= (c & 0x7F) << s; s += 7
        if c < 0x80:
            break
    return b[o:o + n].decode('utf-8', 'replace'), o + n


samples = collections.defaultdict(list)    # (zone, world) -> [(x, y, z)]
for name in sorted(os.listdir(MAP)):
    if not name.endswith('.nfmap'):
        continue
    ids = worlds.get(name[:-6].lower())
    if not ids:
        continue
    b = open(os.path.join(MAP, name), 'rb').read()
    o = 12
    _, o = rstr(b, o)
    grids, = struct.unpack_from('<I', b, o); o += 4
    for _ in range(grids):
        gx, gy, cells = struct.unpack_from('<III', b, o); o += 12
        for _ in range(cells):
            cx, cy, flags = struct.unpack_from('<III', b, o); o += 12
            zones = heights = bounds = None
            if flags & 1:
                zones = struct.unpack_from('<4I', b, o); o += 16
            if flags & 2:
                heights = struct.unpack_from('<289f', b, o); o += 289 * 4
            if flags & 0x10:
                bounds = b[o:o + 4096]; o += 4096
            if not zones:
                continue
            # a grid's X in the file is the world Z grid index (MapFile.GetGrid hashes (gridZ << 16) | gridX against (grid.X << 16) | grid.Y);
            # cells are not swapped (MapFileGrid.GetCell)
            x0 = (gy - ORIGIN) * GRID + cx * CELL
            z0 = (gx - ORIGIN) * GRID + cy * CELL
            # 4 x 4 samples per cell (8 m apart), each resolved like MapFileCell.GetWorldZoneId
            for sx in range(4):
                for sz in range(4):
                    lx, lz = sx * 8 + 4, sz * 8 + 4
                    zone = zones[0]
                    if bounds is not None:
                        f = bounds[(lz * 2) * 64 + lx * 2]        # bounds[x, y] was read y-major
                        for bit in (0x10, 0x08, 0x20):
                            if f & bit:
                                zone = zones[BOUND[bit]]
                                break
                    if not zone:
                        continue
                    y = heights[(lz // 2) * 17 + lx // 2] if heights else 0.0
                    for w in ids:
                        samples[(zone, w)].append((x0 + lx, y, z0 + lz))

with open(OUT, 'w') as f:
    f.write('# zone\tworld\tx\ty\tz\tsamples (scripts/research/zonepoints.py, from data/map/*.nfmap)\n')
    for (zone, world), pts in sorted(samples.items()):
        mx = sum(p[0] for p in pts) / len(pts); mz = sum(p[2] for p in pts) / len(pts)
        best = min(pts, key=lambda p: (p[0] - mx) ** 2 + (p[2] - mz) ** 2)
        f.write(f'{zone}\t{world}\t{best[0]:.1f}\t{best[1]:.1f}\t{best[2]:.1f}\t{len(pts)}\n')

zones = {z for z, _ in samples}
print(f'{len(samples)} (zone, world) points, {len(zones)} zones -> {os.path.relpath(OUT)}')
want = [int(a) for a in sys.argv[1:]]
for z in want[:25]:
    print(z, [(w, len(p)) for (zz, w), p in samples.items() if zz == z][:4] or 'not in any map')
