#!/usr/bin/env python3
"""How close are QuestObjective.WorldLocationsIdIndicator00-03 to the objective's target spawns (community rows only, id < 9000000)?
Single-creature objectives (KillCreature, ActivateEntity, TalkTo, SucceedCSI): distance from each indicator location (and its radius) to the nearest
spawn of the target in the same world. Prints a histogram (<= 30 lines)."""
import os, sys, math, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
R = os.path.join(os.path.dirname(__file__), '..', '..', 'research')
loc = {l['ID']: l for l in load('WorldLocation2')}
sp = collections.defaultdict(list)
for l in open(f'{R}/db-spawns.tsv'):
    c, w, x, y, z, i = l.split()
    if int(i) < 9000000:
        sp[int(c)].append((int(w), float(x), float(z)))
hist, inside, radii = collections.Counter(), collections.Counter(), []
for o in load('QuestObjective'):
    if o['type'] not in (2, 3, 5, 9, 12) or not o['data']:
        continue
    for k in ('worldLocationsIdIndicator00', 'worldLocationsIdIndicator01', 'worldLocationsIdIndicator02', 'worldLocationsIdIndicator03'):
        L = loc.get(o[k])
        if not L:
            continue
        pts = [p for p in sp.get(o['data'], ()) if p[0] == L['worldId']]
        if not pts:
            continue
        d = min(math.hypot(x - L['position0'], z - L['position2']) for _, x, z in pts)
        b = next(t for t in (5, 20, 50, 100, 200, 500, 1e9) if d <= t)
        hist[b] += 1
        inside['within radius' if d <= max(L['radius'], 5) else 'outside radius'] += 1
        radii.append(L['radius'])
print('distance indicator -> nearest target spawn', sorted(hist.items()))
print(dict(inside), 'median radius', sorted(radii)[len(radii) // 2] if radii else '-')
