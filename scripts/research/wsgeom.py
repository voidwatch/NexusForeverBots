#!/usr/bin/env python3
"""Instance geometry from WildStar client files (read-only). Formats (reverse engineered 2026-10-03, see docs/ASSUMPTIONS.md):
  area PROp chunk: Ham Sandwich src/Area/Props.cpp (104-byte entries, model type 0 m3, 1 i3, 3 dgn, UTF-16 path at nameOffset).
  .dgn 'NGND': UTF-16 strings, first path ending .i3 is the dungeon's IDEF building.
  .i3 'FEDI' (IDEF): building, names its sectors; sector geometry is '<base>.<SectorName>.i3'.
  sector .i3 'FEDS' (SDEF v0x1e): 0x1b0 byte header of (count u64, offset u64) pairs, offsets relative to 0x1b0:
     0x20 name (utf16 chars), 0x38 positions (vec3 f32), 0x48 vertex attributes (20 bytes), 0x58 indices (u32), 0x68 per-triangle u16."""
import struct, re
from wsarchive import Archive

SDEF_BASE = 0x1b0

def utf16_strings(d, minlen=4):
    return [m.decode('utf-16le') for m in re.findall(rb'(?:[\x20-\x7e]\x00){%d,}' % minlen, d)]

def area_props(arc, asset):
    """Unique props of a world: uniqueID -> (type, path, scale, quat(x,y,z,w), pos)"""
    props = {}
    for p in arc.ls(asset.replace('\\', '/') + '/*.area'):
        if '_low' in p.lower(): continue
        d = arc.read(p)
        if d is None: continue
        o = 8
        while o + 8 <= len(d):
            magic = d[o:o + 4]; size = struct.unpack_from('<I', d, o + 4)[0]; o += 8
            if magic == b'pORP':      # 'PROp' little endian
                c = d[o:o + size]
                n = struct.unpack_from('<I', c, 0)[0]
                for i in range(n):
                    b = 4 + i * 104
                    if b + 104 > len(c): break
                    uid, = struct.unpack_from('<I', c, b)
                    mtype, noff = struct.unpack_from('<ii', c, b + 16)
                    scale, qx, qy, qz, qw, px, py, pz = struct.unpack_from('<8f', c, b + 28)
                    path = ''
                    if 0 < noff < len(c):
                        e = noff
                        while e + 1 < len(c) and c[e:e + 2] != b'\0\0': e += 2
                        path = c[noff:e].decode('utf-16le', 'replace')
                    props[uid] = (mtype, path, scale, (qx, qy, qz, qw), (px, py, pz))
            o += size
    return props

def dgn_building(arc, path):
    d = arc.read(path)
    if d is None or d[:4] != b'NGND': return None, []
    s = utf16_strings(d)
    i3 = [x for x in s if x.lower().endswith('.i3')]
    sub = [x for x in s if x.lower().endswith('.dgn')]
    return (i3[0] if i3 else None), sub

def building_sectors(arc, i3path):
    """Sector file paths of an IDEF building (falls back to all '<base>.*.i3' files)."""
    d = arc.read(i3path)
    base = i3path[:-3]
    if d is None: return []
    if d[:4] == b'FEDS': return [i3path]
    names = [x for x in utf16_strings(d) if x.startswith('Sector')]
    out = []
    for n in dict.fromkeys(names):
        p = base + '.' + n + '.i3'
        if p.lower() in arc.files: out.append(p)
    if not out:
        out = [p for p in arc.ls(base + '.*.i3')]
    return out

def sector_mesh(arc, path):
    """(positions list of (x,y,z), triangles list of (a,b,c)) or None"""
    d = arc.read(path)
    if d is None or d[:4] != b'FEDS' or len(d) < SDEF_BASE: return None
    nv, vo = struct.unpack_from('<QQ', d, 0x38)
    ni, io = struct.unpack_from('<QQ', d, 0x58)
    if nv == 0 or ni < 3 or SDEF_BASE + vo + nv * 12 > len(d) or SDEF_BASE + io + ni * 4 > len(d): return None
    pos = struct.unpack_from('<%df' % (nv * 3), d, SDEF_BASE + vo)
    idx = struct.unpack_from('<%dI' % ni, d, SDEF_BASE + io)
    if max(idx) >= nv: return None
    verts = [(pos[i], pos[i + 1], pos[i + 2]) for i in range(0, nv * 3, 3)]
    tris = [(idx[i], idx[i + 1], idx[i + 2]) for i in range(0, ni - ni % 3, 3)]
    return verts, tris

def transform(verts, scale, q, t):
    qx, qy, qz, qw = q
    # rotation matrix from unit quaternion
    xx, yy, zz = qx * qx, qy * qy, qz * qz
    xy, xz, yz, wx, wy, wz = qx * qy, qx * qz, qy * qz, qw * qx, qw * qy, qw * qz
    m = ((1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy)),
         (2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx)),
         (2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy)))
    tx, ty, tz = t
    out = []
    for x, y, z in verts:
        x *= scale; y *= scale; z *= scale
        out.append((m[0][0] * x + m[0][1] * y + m[0][2] * z + tx,
                    m[1][0] * x + m[1][1] * y + m[1][2] * z + ty,
                    m[2][0] * x + m[2][1] * y + m[2][2] * z + tz))
    return out

def rasterize(layers, verts, tris, cell, up_sign=1.0, min_ny=0.5):
    """Adds floor heights of upward-facing triangles to layers {cell_key(ix,iz): [y,...]} sampled at cell centres."""
    added = 0
    for a, b, c in tris:
        ax, ay, az = verts[a]; bx, by, bz = verts[b]; cx, cy, cz = verts[c]
        ux, uy, uz = bx - ax, by - ay, bz - az
        vx, vy, vz = cx - ax, cy - ay, cz - az
        nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
        ln = (nx * nx + ny * ny + nz * nz) ** 0.5
        if ln == 0 or ny * up_sign / ln < min_ny: continue
        det = ux * vz - uz * vx            # xz-projected area x2
        if det == 0: continue
        x0 = int((min(ax, bx, cx) / cell) // 1); x1 = int((max(ax, bx, cx) / cell) // 1)
        z0 = int((min(az, bz, cz) / cell) // 1); z1 = int((max(az, bz, cz) / cell) // 1)
        for ix in range(x0, x1 + 1):
            px = (ix + 0.5) * cell - ax
            for iz in range(z0, z1 + 1):
                pz = (iz + 0.5) * cell - az
                s = (px * vz - pz * vx) / det
                t = (ux * pz - uz * px) / det
                if s < -1e-4 or t < -1e-4 or s + t > 1 + 1e-4: continue
                layers.setdefault((ix << 20) | (iz & 0xFFFFF), []).append(ay + s * uy + t * vy)
                added += 1
    return added

def cell_key(ix, iz):
    return (ix << 20) | (iz & 0xFFFFF)

def key_cell(k):
    iz = k & 0xFFFFF
    if iz & 0x80000: iz -= 0x100000
    return k >> 20, iz

def merge_layers(layers, eps=0.75):
    for k, hs in layers.items():
        hs.sort()
        out = [hs[0]]
        for h in hs[1:]:
            if h - out[-1] > eps: out.append(h)
            else: out[-1] = max(out[-1], h)
        layers[k] = out

M3_HEADER = 1584
M3_GEOM = 200
M3_SUBMESH = 112

def m3_mesh(arc, path):
    """Static mesh of an .m3 model (version 100, Ham Sandwich src/models/M3Loader.cpp ReadGeometry): (verts, tris) of the submeshes
    the renderer shows (lowest variant group id, plus group 255), or None. Bones/animation are ignored."""
    d = arc.read(path)
    if d is None or len(d) < M3_HEADER or d[:4] != b'LDOM': return None
    if struct.unpack_from('<I', d, 4)[0] != 100: return None
    gcount, _, goff = struct.unpack_from('<IIq', d, 0x250)
    if gcount == 0: return None
    g = M3_HEADER + goff
    nested = g + ((gcount * M3_GEOM + 15) & ~15)
    nv, = struct.unpack_from('<I', d, g + 0x18)
    vsize = d[g + 0x1C]
    vflags, = struct.unpack_from('<h', d, g + 0x1E)
    ftype0 = d[g + 0x20]; foff0 = d[g + 0x2B]
    ni, = struct.unpack_from('<I', d, g + 0x68)
    isize = d[g + 0x6C]
    _, _, voff = struct.unpack_from('<IIq', d, g + 0x38)
    _, _, ioff = struct.unpack_from('<IIq', d, g + 0x70)
    nsm, _, smoff = struct.unpack_from('<IIq', d, g + 0x80)
    if not (vflags & 1) or isize not in (2, 4): return None
    vs = nested + voff; is_ = nested + ioff; ss = nested + smoff
    if vs + nv * vsize > len(d) or is_ + ni * isize > len(d) or ss + nsm * M3_SUBMESH > len(d): return None
    verts = []
    for i in range(nv):
        o = vs + i * vsize + foff0
        if ftype0 == 1: verts.append(struct.unpack_from('<3f', d, o))
        else:
            x, y, z = struct.unpack_from('<3h', d, o); verts.append((x / 1024.0, y / 1024.0, z / 1024.0))
    idx = struct.unpack_from('<%d%s' % (ni, 'I' if isize == 4 else 'H'), d, is_)
    subs = []
    for i in range(nsm):
        o = ss + i * M3_SUBMESH
        si, sv, sc = struct.unpack_from('<III', d, o)
        subs.append((si, sv, sc, d[o + 30]))
    groups = [s[3] for s in subs if s[3] != 255]
    gmin = min(groups) if groups else 255
    tris = []
    for si, sv, sc, grp in subs:
        if grp != gmin and grp != 255: continue
        for k in range(si, si + sc - 2, 3):
            a, b, c = idx[k] + sv, idx[k + 1] + sv, idx[k + 2] + sv
            if a < nv and b < nv and c < nv: tris.append((a, b, c))
    return verts, tris
