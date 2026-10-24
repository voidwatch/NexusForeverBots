#!/usr/bin/env python3
"""Cross Jabbithole "Started by"/"Completed by" (jh-quests.tsv) with the questcheck gaps: what the archive says starts/ends each gap quest
and whether the client tables already know it. Writes research/decode/quests/jh-cross.tsv, prints <= 30 lines.
    jh_quest_cross.py <quest-check.txt>
"""
import os, re, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text
R = os.path.join(os.path.dirname(__file__), '..', '..', 'research')
def slug(n): return re.sub(r'[^a-z0-9]+', '-', n.lower().replace("'", '')).strip('-')
cr = {c['ID']: c for c in load('Creature2')}
byname = collections.defaultdict(list)
for c in cr.values():
    n = text(c['localizedTextIdName'])
    if n: byname[slug(n)].append(c['ID'])
items = {i['ID']: i for i in load('Item2')}
give, recv = collections.defaultdict(set), collections.defaultdict(set)
for c in cr.values():
    for k, v in c.items():
        if k.startswith('questIdGiven') and v: give[v].add(c['ID'])
        if k.startswith('questIdReceive') and v: recv[v].add(c['ID'])
jh = {}
for l in list(open(f'{R}/decode/quests/jh-quests.tsv'))[1:]:
    f = l.rstrip('\n').split('\t')
    jh[int(f[0])] = (f[3].split('|') if f[3] else [], f[4].split('|') if f[4] else [], f[1])
gaps = collections.defaultdict(set)
for l in open(sys.argv[1]):
    f = l.split('\t'); gaps[int(f[1].split()[1])].add(f[0])
out = open(f'{R}/decode/quests/jh-cross.tsv', 'w')
summ = collections.Counter()
for q, kinds in sorted(gaps.items()):
    if q not in jh: continue
    start, end, zone = jh[q]
    for kind in kinds:
        if kind in ('no-giver', 'giver-not-spawned'):
            ents = start
        elif kind in ('no-receiver', 'receiver-not-spawned'):
            ents = end
        else:
            continue
        cls = []
        for e in ents:
            t, _, v = e.partition(':')
            if t == 'npc':
                ids = byname.get(re.sub(r'-\d+$', '', v), [])
                known = give[q] if 'giver' in kind else recv[q]
                cls.append('npc-known' if set(ids) & known else ('npc-name-in-tables' if ids else 'npc-unknown-name'))
            elif t == 'item':
                m = re.search(r'-(\d+)(?:-[a-z0-9]+)?$', v)
                it = items.get(int(m.group(1))) if m else None
                cls.append('item-activation-in-tables' if it and it['quest2IdActivation'] == q else ('item-in-tables' if it else 'item-unknown'))
            else:
                cls.append(t)
        key = ','.join(sorted(set(cls))) or 'nothing-listed'
        summ[(kind, key)] += 1
        out.write(f'{q}\t{kind}\t{key}\t{"|".join(ents)}\t{zone}\n')
for (k, c), n in sorted(summ.items(), key=lambda t: (t[0][0], -t[1])):
    print(n, k, c)
