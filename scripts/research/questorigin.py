#!/usr/bin/env python3
"""Classify quests without giver/receiver (from gt questcheck output /cache/quest-check.txt copied to a file):
which other tables or spell effects reference the quest id, so we know how the quest is started/finished.

    questorigin.py <quest-check.txt>   writes research/decode/quests/origin.tsv, prints a summary (<= 30 lines)
"""
import os, re, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
OUT = os.path.join(ROOT, 'research', 'decode', 'quests')
os.makedirs(OUT, exist_ok=True)

gaps = collections.defaultdict(set)
for l in open(sys.argv[1]):
    f = l.split('\t')
    m = re.match(r'quest (\d+)', f[1]) if len(f) > 1 else None
    if m:
        gaps[f[0]].add(int(m.group(1)))

refs = collections.defaultdict(set)   # quest -> set of source labels
def add(q, label):
    if q and q != 0xFFFFFFFF:
        refs[q].add(label)

for r in load('PathSettlerSheriff'):
    for k, v in r.items():
        if k.startswith('quest2IdSheriff'):
            add(v, 'sheriff')
for r in load('PathReward'):
    add(r['quest2Id'], 'path-reward')
for r in load('ItemImbuement'):
    for k, v in r.items():
        if k.startswith('quest2Id'):
            add(v, 'imbuement')
for r in load('EpisodeQuest'):
    add(r['questId'], 'episode')
for r in load('Item2'):
    add(r['quest2IdActivation'], 'item-activation')
    add(r['quest2IdActivationRequired'], 'item-activation-required')
for r in load('CommunicatorMessages'):
    for k in ('quest00', 'quest01', 'quest02'):
        add(r[k], 'communicator-quest')
    add(r['questIdDelivered'], 'communicator-delivered')

quests = {q['ID']: q for q in load('Quest2')}
# spell effects: tally effect type + field where a data field equals a gap quest id
allgap = set().union(*gaps.values())
eff = collections.Counter()
effrows = collections.defaultdict(set)
for e in load('Spell4Effects'):
    for k, v in e.items():
        if k.startswith('dataBits') and v in quests:
            key = (e['effectType'], k)
            eff[key] += 1
            effrows[key].add(v)

with open(os.path.join(OUT, 'spell-effect-quest-refs.tsv'), 'w') as o:
    for (t, k), n in eff.most_common():
        o.write(f'{t}\t{k}\t{n}\t{len(effrows[(t, k)] & allgap)}\n')
print('spell effect fields holding a Quest2 id (type field rows gap-quests):')
for (t, k), n in eff.most_common(10):
    print(f'  {t}\t{k}\t{n}\t{len(effrows[(t, k)] & allgap)}')

with open(os.path.join(OUT, 'origin.tsv'), 'w') as o:
    o.write('kind\tquest\ttype\tflags\tsubtype\tcategory\tperiodicGroup\tcontentFinder\tzone\trefs\n')
    for kind in ('no-giver', 'no-receiver', 'giver-not-spawned', 'receiver-not-spawned'):
        c = collections.Counter()
        for q in sorted(gaps.get(kind, ())):
            e = quests[q]
            labels = sorted(refs.get(q, ()))
            o.write(f"{kind}\t{q}\t{e['type']}\t{e['flags']}\t{e['quest2SubTypeId']}\t{e['questCategoryId']}\t{e['periodicQuestGroupId']}\t{e['questContentFinderTypeEnum']}\t{e['worldZoneId']}\t{','.join(labels)}\n")
            key = ','.join(labels) or ('periodic' if e['periodicQuestGroupId'] else 'none')
            c[key] += 1
        print(kind, len(gaps.get(kind, ())), dict(c.most_common(8)))
