#!/usr/bin/env python3
"""Builds floor-height sidecars (<Asset>.nffloor) for worlds whose geometry is props (dungeon .dgn / building .i3).
Usage: floorgen.py [--m3] [-o data/map] [--check spawns.tsv] <world id|asset name> ...   (ids from research/worlds.tsv)
Format: see write() (NFFL v2, sparse 32x32 cell tiles).
A cell holds the heights of every upward-facing surface (normal y >= 0.5) of the instance geometry sampled at the cell centre."""
import sys, os, struct, time
from array import array
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wsgeom import *

def world_assets():
    here = os.path.dirname(os.path.abspath(__file__))
    W = {}
    for l in open(os.path.join(here, '../../research/worlds.tsv')):
        f = l.rstrip('\n').split('\t')
        if len(f) >= 4 and f[3]: W[f[0]] = f[3].replace('\\', '/')
    return W

def build(arc, asset, cache, cell=1.0, m3=False):
    props = area_props(arc, asset)
    layers = {}
    stats = {'dgn': 0, 'i3': 0, 'm3_skipped': 0, 'sectors': 0, 'tris': 0, 'missing': 0}
    for uid, (mtype, path, scale, q, pos) in props.items():
        path = path.replace('\\', '/')
        if mtype == 3:
            b, _ = dgn_building(arc, path)
            stats['dgn'] += 1
            if not b: stats['missing'] += 1; continue
            path = b.replace('\\', '/')
        elif mtype == 1:
            stats['i3'] += 1
        elif mtype == 0 and m3:
            if path not in cache:
                mesh = m3_mesh(arc, path)
                cache[path] = [mesh] if mesh else []
        else:
            if mtype == 0: stats['m3_skipped'] += 1
            continue
        if path not in cache:
            cache[path] = [m for m in (sector_mesh(arc, s) for s in building_sectors(arc, path)) if m]
        meshes = cache[path]
        if not meshes: stats['missing'] += 1
        for v, t in meshes:
            stats['sectors'] += 1
            stats['tris'] += len(t)
            rasterize(layers, transform(v, scale, q, pos), t, cell)
    merge_layers(layers)
    return layers, stats

TILE = 32

def write(layers, path, cell=1.0):
    """NFFL v2, sparse: header magic 'NFFL', version 2, cell f32, tile cells u32 (32), minTX i32, minTZ i32, tilesW u32, tilesD u32,
    heightCount u32; tileIndex u32[tilesW*tilesD] (0xFFFFFFFF empty, else tile number); per tile u32 start[TILE*TILE+1] into heights
    (cell (cx,cz) of the tile at cz*TILE+cx); heights f32[] ascending per cell. Cell ix covers x in [ix*cell, (ix+1)*cell)."""
    tiles = {}
    for k in layers:
        ix, iz = key_cell(k)
        tiles.setdefault((ix // TILE, iz // TILE), []).append((ix, iz))
    tx0 = min(t[0] for t in tiles); tx1 = max(t[0] for t in tiles)
    tz0 = min(t[1] for t in tiles); tz1 = max(t[1] for t in tiles)
    w, d = tx1 - tx0 + 1, tz1 - tz0 + 1
    index = array('I', [0xFFFFFFFF]) * (w * d)
    starts = array('I'); hs = array('f')
    for n, (tx, tz) in enumerate(sorted(tiles, key=lambda t: (t[1], t[0]))):
        index[(tz - tz0) * w + (tx - tx0)] = n
        for cz in range(TILE):
            for cx in range(TILE):
                starts.append(len(hs))
                hs.extend(layers.get(cell_key(tx * TILE + cx, tz * TILE + cz), ()))
        starts.append(len(hs))
    with open(path, 'wb') as f:
        f.write(struct.pack('<IIfIiiIII', 0x4C46464E, 2, cell, TILE, tx0, tz0, w, d, len(hs)))
        index.tofile(f); starts.tofile(f); hs.tofile(f)
    return len(tiles), len(hs)

def check(layers, spawns):
    near = miss = 0
    for x, y, z in spawns:
        hs = layers.get(cell_key(int(x // 1), int(z // 1)))
        if not hs: miss += 1
        elif min(abs(h - y) for h in hs) < 2.0: near += 1
    return near, miss

if __name__ == '__main__':
    args = sys.argv[1:]
    out = 'data/map'; spawnfile = None; m3 = False
    if args[:1] == ['--m3']: m3 = True; args = args[1:]
    if args[:1] == ['-o']: out = args[1]; args = args[2:]
    if args[:1] == ['--check']: spawnfile = args[1]; args = args[2:]
    W = world_assets()
    arc = Archive(); cache = {}
    for a in args:
        asset = W.get(a, a if '/' in a else 'Map/' + a)
        t = time.time()
        cache.clear()
        layers, st = build(arc, asset, cache, m3=m3)
        name = asset.rsplit('/', 1)[-1]
        if not layers:
            print(f'{a} {name}: no prop geometry {st}'); continue
        nt, n = write(layers, os.path.join(out, name + '.nffloor'))
        msg = f'{a} {name}: {len(layers)} cells {nt} tiles {n} heights, {st}, {time.time() - t:.0f}s'
        if spawnfile:
            sp = [tuple(map(float, l.split('\t')[1:4])) for l in open(spawnfile) if l.split('\t')[0] == a]
            if sp:
                near, miss = check(layers, sp)
                msg += f' | spawns {len(sp)}: floor within 2m {near}, no floor {miss}'
        print(msg, flush=True)
        del layers
