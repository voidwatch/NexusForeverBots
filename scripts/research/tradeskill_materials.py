#!/usr/bin/env python3
"""Where each tradeskill schematic material comes from: harvest node drops, creature drops (Jabbithole data/research/creature-drops.tsv),
output of another schematic, or nowhere known. Writes research/tradeskill-materials.tsv; prints a summary. Run from the repo root."""
import sys, re, collections
sys.path.insert(0, 'scripts/research')
from tbl import load, text

def clean(n): return re.sub(r'\{p:[^}]*\}', '', n or '').split(' - Disguise')[0].strip()
cr = load('Creature2')
node_names = {clean(text(c['localizedTextIdName'])) for c in cr if c['tradeskillHarvestingInfoId']}
drops_node, drops_mob = collections.defaultdict(set), collections.defaultdict(set)
for l in open('data/research/creature-drops.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    if len(f) < 4 or not f[3].isdigit(): continue
    (drops_node if clean(f[0]) in node_names else drops_mob)[int(f[3])].add(clean(f[0]))
sch = load('TradeskillSchematic2')
outputs = {s['item2IdOutput'] for s in sch if s['item2IdOutput']}
used = collections.Counter()
for s in sch:
    for i in range(5):
        if s['item2IdMaterial0%d' % i]: used[s['item2IdMaterial0%d' % i]] += 1
kinds = collections.Counter(); weighted = collections.Counter()
with open('research/tradeskill-materials.tsv', 'w', encoding='utf-8') as o:
    o.write('item\tname\tschematics\tsource\tfrom\n')
    for item, n in used.most_common():
        if item in drops_node: k, src = 'node', sorted(drops_node[item])[:3]
        elif item in outputs: k, src = 'crafted', []
        elif item in drops_mob: k, src = 'creature', sorted(drops_mob[item])[:3]
        else: k, src = 'unknown', []
        kinds[k] += 1; weighted[k] += n
        o.write(f"{item}\t{clean(text(0) if False else '')}\t{n}\t{k}\t{','.join(src)}\n")
print('materials', len(used), dict(kinds)); print('schematic uses', dict(weighted))
