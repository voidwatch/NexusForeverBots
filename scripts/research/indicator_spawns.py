#!/usr/bin/env python3
"""Place single-creature objective targets that have no spawn at the objective's map indicator (QuestObjective.WorldLocationsIdIndicator00-03).

Evidence (indicator_check.py): for spawned targets of single-creature objectives the indicator lies within 5 m of a target spawn in 559 of 971
cases, within 20 m in 72 %, within 50 m in 85 % -> confidence likely. Target groups are not placed (which member stands where is unknown).
Only objectives gt questcheck lists as objective-not-spawned; the target must have no row in research/db-spawns.tsv; the location must not be in a
cut zone or an unnamed test map. KillCreature targets get 3 spawns 6 m apart per indicator (counts need several), others 1.
    indicator_spawns.py <quest-check.txt>   writes data/spawns/objind.tsv, prints <= 30 lines
"""
import os, re, sys, random, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
R = os.path.join(os.path.dirname(__file__), '..', '..', 'research')
loc = {l['ID']: l for l in load('WorldLocation2')}
obj = {o['ID']: o for o in load('QuestObjective')}
zones = {z['ID']: z for z in load('WorldZone')}
worlds = {w['ID']: w for w in load('World')}
def cut(zid):
    for _ in range(8):
        if zid in (207, 866): return True
        if not zid or zid not in zones: return False
        zid = zones[zid]['parentZoneId']
    return False
def test_world(wid):
    w = worlds.get(wid)
    return bool(w) and not w['localizedTextIdName'] and any(t in w['assetPath'].lower() for t in ('test', 'sandbox'))
creatures = {c['ID'] for c in load('Creature2')}
spawned = {int(l.split()[0]) for l in open(f'{R}/db-spawns.tsv')}
SINGLE = {2: 'KillCreature', 3: 'TalkTo', 5: 'ActivateEntity', 9: 'ActivateEntity2', 12: 'SucceedCSI'}
rng = random.Random(5)
rows, stats, done = [], collections.Counter(), set()
for l in open(sys.argv[1]):
    f = l.split('\t')
    if f[0] != 'objective-not-spawned':
        continue
    o = obj[int(re.match(r'objective (\d+)', f[2]).group(1))]
    if o['type'] not in SINGLE:
        stats['group objective, not placed'] += 1
        continue
    c = o['data']
    if c not in creatures:
        stats['target has no Creature2 row (table gap)'] += 1
        continue
    if c in spawned:
        stats['target already has a row'] += 1
        continue
    inds = [loc[o[k]] for k in ('worldLocationsIdIndicator00', 'worldLocationsIdIndicator01', 'worldLocationsIdIndicator02', 'worldLocationsIdIndicator03') if o[k] in loc]
    inds = [L for L in inds if L['worldId'] and not cut(L['worldZoneId']) and not test_world(L['worldId'])]
    if not inds:
        stats['no usable indicator'] += 1
        continue
    per = 3 if o['type'] == 2 else 1
    for L in inds:
        if (c, L['ID']) in done:
            continue
        done.add((c, L['ID']))
        for k in range(per):
            j = 0 if per == 1 else 6
            rows.append(f"{c}\t{L['worldId']}\t{L['position0'] + rng.uniform(-j, j):.1f}\t{L['position2'] + rng.uniform(-j, j):.1f}\tobjind-{L['ID']}")
    stats['placed ' + SINGLE[o['type']]] += 1
os.makedirs(f'{R}/../data/spawns', exist_ok=True)
open(f'{R}/../data/spawns/objind.tsv', 'w').write('\n'.join(rows) + '\n')
print(len(rows), 'rows'); print(dict(stats))
