#!/usr/bin/env python3
"""prereqscan.py <type> [n]: every Prerequisite row using the type, its condition and who uses it (spells, effects, quests, nested). Capped."""
import sys, collections
sys.path.insert(0, __import__('os').path.dirname(__file__))
from spellinfo import *
T = int(sys.argv[1]); N = int(sys.argv[2]) if len(sys.argv) > 2 else 30
P = load('Prerequisite'); byid = {r['ID']: r for r in P}
def fmt(r): return 'f%d ' % r['flags'] + ' & '.join(f"t{r['prerequisiteTypeId%d'%i]} c{r['prerequisiteComparisonId%d'%i]} o{r['objectId%d'%i]} v{r['value%d'%i]}" for i in range(3) if r['prerequisiteTypeId%d'%i])
refs = collections.defaultdict(list)
for s in S4.values():
    for k in ('prerequisiteIdCasterCast', 'prerequisiteIdTargetCast', 'prerequisiteIdCasterPersistence', 'prerequisiteIdTargetPersistence'):
        if s[k]: refs[s[k]].append(k[14:20] + ':' + name(s['ID']).split('/')[0][:26])
for e in load('Spell4Effects'):
    for k in ('prerequisiteIdCasterApply', 'prerequisiteIdTargetApply'):
        if e[k]: refs[e[k]].append('eff:' + name(e['spellId']).split('/')[0][:22])
for q in load('Quest2'):
    for k in q:
        if 'rerequisite' in k and q[k]: refs[q[k]].append(f"quest{q['ID']}:" + text(q.get('localizedTextIdTitle', 0))[:22])
par = collections.defaultdict(list)
for r in P:
    for i in range(3):
        if r['prerequisiteTypeId%d' % i] == 11: par[r['objectId%d' % i]].append(r['ID'])
rows = [r for r in P if T in [r['prerequisiteTypeId%d' % i] for i in range(3)]]
print(len(rows), 'rows')
for r in rows[:N]:
    users = refs[r['ID']] + [f"via{p}:{refs[p][:1]}" for p in par[r['ID']]]
    print(r['ID'], fmt(r), '|', len(users), users[:3])
