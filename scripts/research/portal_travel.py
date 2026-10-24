#!/usr/bin/env python3
"""Open-world portal destinations: "Ship to X" / "Portal to X" / "Transport to X" go to the dock of zone X, i.e. a portal unit spawned in
zone X (zone of a spawn = WorldZoneId of the nearest WorldLocation2 in the same world within 400 m, then its parent zones).
Prints one line per resolvable open-world portal and a summary (at most 40 lines)."""
import tbl, re, collections, math
cr = {c['ID']: c for c in tbl.load('Creature2')}; ip = {x['ID']: x for x in tbl.load('InstancePortal')}
Z = {z['ID']: z for z in tbl.load('WorldZone')}; W = {w['ID']: w for w in tbl.load('World')}
def N(s): return re.sub('[^a-z0-9]', '', re.sub(r'\bthe\b', '', (s or '').lower()))
pre = re.compile(r'^(ship|portal|transport|journey|fly|shuttle|travel)\s+to\s+', re.I)
locs = collections.defaultdict(list)
for l in tbl.load('WorldLocation2'):
    if l['worldZoneId']: locs[l['worldId']].append((l['position0'], l['position2'], l['worldZoneId']))
def zone(world, x, z):
    best = min(((x - a) ** 2 + (z - b) ** 2, zid) for a, b, zid in locs.get(world, [(1e9, 1e9, 0)]))
    return best[1] if best[0] < 400 ** 2 else 0
def chain(zid):
    out = []
    while zid and zid not in out: out.append(zid); zid = Z[zid]['parentZoneId'] if zid in Z else 0
    return out
spawns = []
for l in open('../../research/db-spawns.tsv'):
    p = l.rstrip('\n').split('\t')
    if not p[0].isdigit(): continue
    c = cr.get(int(p[0]))
    if c and c.get('instancePortalId') in ip and ip[c['instancePortalId']]['instancePortalTypeEnum'] < 2:
        w = int(p[1]); x, z = float(p[2]), float(p[4])
        spawns.append((c['ID'], c['instancePortalId'], w, x, float(p[3]), z, chain(zone(w, x, z))))
zbyname = collections.defaultdict(set)
for z in Z.values(): zbyname[N(tbl.text(z['localizedTextIdName']))].add(z['ID'])
res = collections.Counter()
for cid, pid, w, x, y, z, ch in spawns:
    name = tbl.text(ip[pid]['localizedTextIdName']) or ''
    if not pre.match(name): continue
    target = zbyname.get(N(pre.sub('', name)), set())
    if not target: res['no zone of that name'] += 1; print('  nozone', name); continue
    dests = [(w2, x2, y2, z2) for c2, p2, w2, x2, y2, z2, ch2 in spawns if (w2, x2) != (w, x) and target & set(ch2) and not target & set(ch)]
    if not dests: res['no portal unit in the target zone'] += 1; print('  nodock', name, w); continue
    worlds = {d[0] for d in dests}
    res['resolved'] += 1; print('ok', name, 'from', w, '->', sorted(worlds), dests[0][1:])
print(dict(res))
