#!/usr/bin/env python3
"""Checks .nffloor sidecars against WorldLocation2 points (designer-placed, exact) of each world.
Usage: floorcheck.py <world id> ...   prints per world: locations, with floor in cell, within 1 m / 2.5 m of the chosen floor."""
import sys, os, struct
from array import array
from tbl import load
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from floorgen import world_assets

def read_floor(path):
    b = open(path, 'rb').read()
    magic, ver, cell, T, tx0, tz0, w, d, nh = struct.unpack_from('<IIfIiiIII', b, 0)
    o = 36
    index = array('I'); index.frombytes(b[o:o + 4 * w * d]); o += 4 * w * d
    nt = sum(1 for i in index if i != 0xFFFFFFFF)
    starts = array('I'); starts.frombytes(b[o:o + 4 * nt * (T * T + 1)]); o += 4 * nt * (T * T + 1)
    hs = array('f'); hs.frombytes(b[o:o + 4 * nh])
    def heights(x, z):
        ix, iz = int(x // cell), int(z // cell)
        tx, tz = ix // T - tx0, iz // T - tz0
        if not (0 <= tx < w and 0 <= tz < d): return []
        t = index[tz * w + tx]
        if t == 0xFFFFFFFF: return []
        c = t * (T * T + 1) + (iz % T) * T + (ix % T)
        return list(hs[starts[c]:starts[c + 1]])
    return heights

def choose(hs, y, step=2.5):
    below = [h for h in hs if h <= y + step]
    return max(below) if below else min(hs)

if __name__ == '__main__':
    W = world_assets()
    locs = load('WorldLocation2')
    for w in sys.argv[1:]:
        name = W[w].rsplit('/', 1)[-1]
        p = os.path.join(os.environ.get('FLOORDIR') or os.path.join(os.path.dirname(__file__), '../../data/map'), name + '.nffloor')
        if not os.path.exists(p): print(w, name, 'no sidecar'); continue
        heights = read_floor(p)
        pts = [(r['position0'], r['position1'], r['position2']) for r in locs if r['worldId'] == int(w)]
        has = n1 = n25 = 0
        for x, y, z in pts:
            hs = heights(x, z)
            if not hs: continue
            has += 1
            dy = abs(choose(hs, y) - y)
            n1 += dy < 1; n25 += dy < 2.5
        print(f'{w} {name}: locations {len(pts)}, floor in cell {has}, chosen floor within 1m {n1}, within 2.5m {n25}')
