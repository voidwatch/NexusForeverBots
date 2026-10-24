#!/usr/bin/env python3
"""Pair normal instance creatures with their veteran (Prime) Creature2 rows.

Evidence: veteran rows are separate Creature2 entries whose description ends in "Veteran" (e.g. 17163 "[STL] e314 - (N) Stormtalon",
level 20 tier 1 -> 33406 "[STL] e314 - Stormtalon - Veteran", level 50 tier 5). A pair needs the same localized name; ties are broken by the
description tag ([STL]), the encounter code (e314), the display group and the model. Ambiguous pairs are written with all candidates, not guessed.

Usage: veteran_pairs.py <world-creatures.tsv (world, creature, count)> <out.tsv> [report.md]
Out: world, spawned creature, replacement, mode it applies in (veteran: normal->veteran, normal: veteran->Normal row), match, name.
"""
import re, sys, collections
from tbl import load, text

# docs/INSTANCES.md: dungeons, shiphands, raids
INSTANCE_WORLDS = {int(m.group(1)) for m in re.finditer(r'^\| (\d+) \| \w+ \| (?:dungeon|shiphand|raid) \|', open(
    __import__('os').path.join(__import__('os').path.dirname(__file__), '..', '..', 'docs', 'INSTANCES.md')).read(), re.M)}
# the description tag of each dungeon's own creatures
WORLD_TAG = {382: 'STL', 1263: 'SK', 1336: 'KV', 1271: 'TS', 3173: 'UPJ'}

def wtoken(d):
    m = re.search(r'\b(W\d{3,4})\b', d)
    return m.group(1) if m else ''

def tag(d):
    m = re.match(r'\s*\[([^\]]+)\]', d)
    return m.group(1).strip().upper() if m else ''

def code(d):
    m = re.search(r'\b(e\d{3,4})\b', d)
    return m.group(1) if m else ''

def is_vet(d):
    return re.search(r'veteran', d, re.I) is not None

rows = {r['ID']: r for r in load('Creature2')}
names = {i: (text(r['localizedTextIdName']) or '').strip() for i, r in rows.items()}
vets_by_name = collections.defaultdict(list)
for i, r in rows.items():
    if is_vet(r['description']) and names[i]:
        vets_by_name[names[i].lower()].append(i)

spawned = collections.defaultdict(dict)
for line in open(sys.argv[1]):
    w, c, n = (int(x) for x in line.split('\t'))
    if w in INSTANCE_WORLDS:
        spawned[w][c] = n

out, amb, nomatch = [], [], collections.Counter()
for w in sorted(spawned):
    for c in sorted(spawned[w]):
        r = rows.get(c)
        if r is None:
            continue
        if is_vet(r['description']):
            # a veteran row spawned by the community data (Swordmaiden): its "- Normal" row with the same name and tag is the normal-mode creature
            norm = [i for i, x in rows.items() if names[i].lower() == names[c].lower() and tag(x['description']) == tag(r['description'])
                    and tag(r['description']) and re.search(r'-\s*Normal\b', x['description']) and not is_vet(x['description'])]
            if len(norm) == 1:
                out.append((w, c, norm[0], 'normal', 'tag normal', names[c]))
            continue
        cands = vets_by_name.get(names[c].lower(), [])
        if not cands:
            nomatch[w] += 1
            continue
        def score(v):
            rv = rows[v]
            return ((tag(rv['description']) == tag(r['description']) and tag(r['description']) != '') * 4
                    + (code(rv['description']) == code(r['description']) and code(r['description']) != '') * 2
                    + (rv['creature2DisplayGroupId'] == r['creature2DisplayGroupId']) * 1)
        ranked = sorted(cands, key=score, reverse=True)
        best = score(ranked[0])
        top = [v for v in ranked if score(v) == best]
        # a veteran row that is already spawned in this world as itself is not a variant of something else
        # without the same tag the veteran row must belong to this dungeon (its tag) or to the same world token (W2149 shiphands)
        if best < 4:
            ranked = [v for v in ranked if tag(rows[v]['description']) == WORLD_TAG.get(w, '?')
                      or (wtoken(r['description']) and wtoken(rows[v]['description']) == wtoken(r['description']))]
            if not ranked:
                amb.append((w, c, names[c], cands, 'no tag match'))
                continue
            best = score(ranked[0])
            top = [v for v in ranked if score(v) == best]
        if len(top) > 1:
            # prefer the one not already used by another normal creature in the same world later; record ambiguity
            amb.append((w, c, names[c], top, 'tie'))
            continue
        how = ('tag ' if best & 4 else '') + ('code ' if best & 2 else '') + ('display' if best & 1 else '')
        out.append((w, c, top[0], 'veteran', how.strip(), names[c]))

with open(sys.argv[2], 'w') as f:
    f.write('# world\tspawned\treplacement\tin mode\tmatch\tname  (scripts/research/veteran_pairs.py)\n')
    for o in out:
        f.write('\t'.join(map(str, o)) + '\n')
per = collections.Counter(o[0] for o in out if o[3] == 'veteran')
print('veteran -> normal (Normal rows):', sum(o[3] == 'normal' for o in out))
print(f'pairs {len(out)}, ambiguous {len(amb)}')
for w in sorted(spawned):
    print(f'world {w}: spawned {len(spawned[w])} creatures, paired {per[w]}, no veteran row by name {nomatch[w]}')
if len(sys.argv) > 3:
    with open(sys.argv[3], 'w') as f:
        for a in amb:
            f.write(f'{a[0]}\t{a[1]}\t{a[2]}\t{a[4]}\t' + ' '.join(f'{v}:{rows[v]["description"]}' for v in a[3]) + '\n')
