#!/usr/bin/env python3
"""Place quest NPCs that have no spawn at the turn-in location the client tables give for their quests.

Evidence (questloc_check.py): for quests whose receiver is spawned by the community database, Quest2.WorldLocation2IdReceiver lies within
2 m of a receiver spawn in 1660 of 2023 quests and within 10 m in 91 %. So a quest's receiver location is where its receiver stands
(confidence: likely). A creature that gives one quest and receives another is placed through the quest it receives.

Rules (no guesses below "likely"):
- only creatures in research/needed-creatures.tsv (cut zones already excluded by gt exportneeded) that have no row at all in
  research/db-spawns.tsv (community and earlier research rows);
- only the main receiver location (AltReceiver locations belong to alternative receivers chosen by prerequisites, not used);
- the location's zone must not be cut (Halon Ring 866, Murkmire 207 and their children), its world not an unnamed test/sandbox map;
- a quest with several unspawned receivers of the same name (versions of one NPC): the one receiving most quests is placed;
- a quest with several receivers: skipped when one of them is already spawned within 20 m of the location (the spot is taken), and
  when more than one receiver is unspawned (which of them stands there is unknown -> recorded as ambiguous);
- locations of one creature closer than 12 m merge into one spawn.
Writes data/spawns/questloc.tsv (creature, world, x, z, source) and research/decode/quests/questloc-report.tsv, prints <= 30 lines.
"""
import os, sys, collections, math
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
R = os.path.join(os.path.dirname(__file__), '..', '..', 'research')

zones = {z['ID']: z for z in load('WorldZone')}
def cut(zid):
    for _ in range(8):
        if zid in (207, 866):
            return True
        if not zid or zid not in zones:
            return False
        zid = zones[zid]['parentZoneId']
    return False

loc = {l['ID']: l for l in load('WorldLocation2')}
worlds = {w['ID']: w for w in load('World')}
def test_world(wid):
    w = worlds.get(wid)
    return bool(w) and not w['localizedTextIdName'] and any(t in w['assetPath'].lower() for t in ('test', 'sandbox'))
recv = collections.defaultdict(set)
for c in load('Creature2'):
    for k, v in c.items():
        if k.startswith('questIdReceive') and v:
            recv[v].add(c['ID'])
recv_of = collections.defaultdict(set)
for qid, cs in recv.items():
    for c in cs:
        recv_of[c].add(qid)
need = {}
for l in open(f'{R}/needed-creatures.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    need[int(f[0])] = f
spawns = collections.defaultdict(list)
for l in open(f'{R}/db-spawns.tsv'):
    c, w, x, y, z, i = l.split()
    spawns[int(c)].append((int(w), float(x), float(z)))

points = collections.defaultdict(list)   # creature -> (world, x, z, quest, loc)
report = []
stats = collections.Counter()
for q in load('Quest2'):
    lid = q['WorldLocation2IdReceiver']
    if not lid or lid not in loc or cut(q['worldZoneId']):
        continue
    L = loc[lid]
    if cut(L['worldZoneId']) or not L['worldId']:
        stats['cut-or-no-world'] += 1
        continue
    if test_world(L['worldId']):
        stats['developer-test-map'] += 1
        continue
    rs = recv.get(q['ID'], set())
    unspawned = [c for c in rs if c in need and not spawns.get(c)]
    if not unspawned:
        continue
    taken = any(w == L['worldId'] and math.hypot(x - L['position0'], z - L['position2']) <= 20
                for c in rs if spawns.get(c) for w, x, z in spawns.get(c, ()))
    if taken:
        stats['spot-taken-by-spawned-receiver'] += 1
        report.append(f"skip\tquest {q['ID']}\tlocation {lid} already has a spawned receiver within 20 m\t{unspawned}")
        continue
    if len(unspawned) > 1 and len({need[c][1] for c in unspawned}) == 1 and need[unspawned[0]][1]:
        # versions of one NPC (same name): one of them standing there serves the quest; take the one that receives most quests
        unspawned = [max(sorted(unspawned), key=lambda c: (len(recv_of[c]), -c))]
        stats['same-name-versions-one-placed'] += 1
    if len(unspawned) > 1:
        stats['ambiguous-several-unspawned-receivers'] += 1
        report.append(f"ambiguous\tquest {q['ID']}\tlocation {lid}\treceivers {sorted(unspawned)}")
        continue
    points[unspawned[0]].append((L['worldId'], L['position0'], L['position2'], q['ID'], lid))

rows = []
for cid, pts in sorted(points.items()):
    merged = []   # [world, sx, sz, n, quests, lid]
    for w, x, z, qid, lid in pts:
        for m in merged:
            if m[0] == w and math.hypot(m[1] / m[3] - x, m[2] / m[3] - z) <= 12:
                m[1] += x; m[2] += z; m[3] += 1; m[4].append(qid)
                break
        else:
            merged.append([w, x, z, 1, [qid], lid])
    for w, sx, sz, n, qs, lid in merged:
        rows.append(f'{cid}\t{w}\t{sx / n:.1f}\t{sz / n:.1f}\tquestloc-{lid}')
        report.append(f"placed\tcreature {cid}\tworld {w}\t{sx / n:.1f},{sz / n:.1f}\tquests {qs}\tconfidence likely\troles {need[cid][2]}")
    stats['creatures-placed'] += 1
    stats['spawn-rows'] += len(merged)
os.makedirs(f'{R}/../data/spawns', exist_ok=True)
open(f'{R}/../data/spawns/questloc.tsv', 'w').write('\n'.join(rows) + '\n')
os.makedirs(f'{R}/decode/quests', exist_ok=True)
open(f'{R}/decode/quests/questloc-report.tsv', 'w').write('\n'.join(report) + '\n')
roles = collections.Counter(r for c in points for r in need[c][2].split(','))
print(dict(stats))
print('placed creatures by role', dict(roles))
