#!/usr/bin/env python3
"""Predict gt questcheck giver/receiver-not-spawned counts after adding planned spawn files, and break down what is left.
    gapsim.py <quest-check.txt> [planned-spawns.tsv ...]   prints <= 30 lines
Left-over quests are classed by where Quest2.WorldLocation2IdReceiver points (cut zone, test map, none, open world).
"""
import os, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text
R = os.path.join(os.path.dirname(__file__), '..', '..', 'research')
spawned = {int(l.split()[0]) for l in open(f'{R}/db-spawns.tsv')}
for p in sys.argv[2:]:
    spawned |= {int(l.split()[0]) for l in open(p) if l.strip()}
give, recv = collections.defaultdict(set), collections.defaultdict(set)
for c in load('Creature2'):
    for k, v in c.items():
        if k.startswith('questIdGiven') and v: give[v].add(c['ID'])
        if k.startswith('questIdReceive') and v: recv[v].add(c['ID'])
q2 = {q['ID']: q for q in load('Quest2')}
loc = {l['ID']: l for l in load('WorldLocation2')}
worlds = {w['ID']: w for w in load('World')}
zones = {z['ID']: z for z in load('WorldZone')}
def cut(zid):
    for _ in range(8):
        if zid in (207, 866): return True
        if not zid or zid not in zones: return False
        zid = zones[zid]['parentZoneId']
    return False
def where(q):
    L = loc.get(q['WorldLocation2IdReceiver'])
    if not L: return 'no-receiver-location'
    if cut(L['worldZoneId']): return 'receiver-location-in-cut-zone'
    a = worlds.get(L['worldId'], {}).get('assetPath', '')
    if any(t in a.lower() for t in ('test', 'sandbox')): return 'receiver-location-on-test-map'
    return 'receiver-location-world-%d' % L['worldId']
res = collections.Counter(); left = collections.defaultdict(collections.Counter)
for l in open(sys.argv[1]):
    f = l.split('\t')
    if f[0] not in ('giver-not-spawned', 'receiver-not-spawned'): continue
    q = int(f[1].split()[1])
    who = give[q] if f[0].startswith('giver') else recv[q]
    if who & spawned:
        res[f[0] + ' fixed'] += 1
    else:
        res[f[0] + ' left'] += 1
        left[f[0]][where(q2[q])] += 1
print(dict(res))
for k, c in left.items():
    print(k, c.most_common(8))
