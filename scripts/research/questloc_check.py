#!/usr/bin/env python3
"""How well does Quest2.WorldLocation2IdReceiver (and the AltReceiver locations) point at the receiver creature?
Checks quests whose receiver IS spawned in the world database (community data, not research rows): distance from the location to the
nearest spawn of a receiver creature in the same world. Prints a distance histogram (<= 30 lines).
Needs research/db-spawns.tsv (creature world x y z id) from the db.
"""
import os, sys, collections, math
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
R = os.path.join(os.path.dirname(__file__), '..', '..', 'research')
loc = {l['ID']: l for l in load('WorldLocation2')}
recv = collections.defaultdict(set)
give = collections.defaultdict(set)
for c in load('Creature2'):
    for k, v in c.items():
        if k.startswith('questIdReceive') and v:
            recv[v].add(c['ID'])
        if k.startswith('questIdGiven') and v:
            give[v].add(c['ID'])
spawns = collections.defaultdict(list)
for l in open(f'{R}/db-spawns.tsv'):
    c, w, x, y, z, i = l.split()
    if int(i) < 9000000:
        spawns[int(c)].append((int(w), float(x), float(y), float(z)))
def check(field, who):
    hist = collections.Counter(); radii = []
    for q in load('Quest2'):
        lid = q[field]
        if not lid or lid not in loc:
            continue
        L = loc[lid]
        pts = [p for c in who[q['ID']] for p in spawns.get(c, ()) if p[0] == L['worldId']]
        if not pts:
            hist['no-spawn-same-world' if any(spawns.get(c) for c in who[q['ID']]) else 'not-spawned'] += 1
            continue
        d = min(math.hypot(p[1] - L['position0'], p[3] - L['position2']) for p in pts)
        b = next(t for t in (2, 5, 10, 20, 50, 100, 300, 1e9) if d <= t)
        hist[f'<= {b:g} m'] += 1
        radii.append(L['radius'])
    print(field, 'vs', 'receivers' if who is recv else 'givers', dict(sorted(hist.items())), 'median radius', sorted(radii)[len(radii) // 2] if radii else '-')
check('WorldLocation2IdReceiver', recv)
check('WorldLocation2IdReceiver', give)
check('worldLocation2IdAltReceiver00', recv)
