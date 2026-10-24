#!/usr/bin/env python3
"""resource_flow.py: every VitalModifier (effect 1) row on a class resource vital (4-12, 15, 16, 18, 19, 26), with the class of its spell,
sign, tick/duration and which spells reach it (proxy/proc/cast-event parents, 2 levels). Live class spells only (SpellLevel or Class passives
reachable). Writes research/decode/resources/resource-flow.tsv, prints a summary (<= 30 lines)."""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
from collections import defaultdict, Counter
def f(u): return struct.unpack('<f', struct.pack('<I', u & 0xffffffff))[0]
def s32(u): return u - (1 << 32) if u >= 1 << 31 else u
S4 = {r['ID']: r for r in load('Spell4')}
SB = {r['ID']: r for r in load('Spell4Base')}
EFF = defaultdict(list)
for e in load('Spell4Effects'): EFF[e['spellId']].append(e)
VIT = {4: 'KineticEnergy', 5: 'Endurance', 6: 'Resource1', 7: 'Resource2', 8: 'Resource3', 9: 'Resource4', 10: 'Resource5', 11: 'Resource6',
       12: 'SuitPower', 15: 'Focus', 16: 'Dash', 18: 'Actuator', 19: 'SpellSurge', 26: 'Volatility'}
parents = defaultdict(set)   # child spell -> parent spells (proxy 26, proc 14 DataBits01, cast events)
for sid, effs in EFF.items():
    for e in effs:
        if e['effectType'] == 26:
            parents[e['dataBits00'] or e['dataBits01']].add(sid)
        if e['effectType'] == 14:
            parents[e['dataBits01']].add(sid)
for s in S4.values():
    for k in ('spell4IdCastEvent00', 'spell4IdCastEvent01', 'spell4IdCastEvent02', 'spell4IdCastEvent03'):
        if s.get(k): parents[s[k]].add(s['ID'])
live = set(r['spell4Id'] for r in load('SpellLevel'))
for c in load('Class'):
    for k, v in c.items():
        if k.startswith('spell4IdInnate') and v: live.add(v)
def cls(sid):
    s = S4.get(sid); return SB.get(s['spell4BaseIdBaseSpell'], {}).get('classIdPlayer', 0) if s else 0
def reach(sid, depth=0):
    if sid in live: return True
    if depth >= 3: return False
    return any(reach(p, depth + 1) for p in parents.get(sid, ()))
out = open('research/decode/resources/resource-flow.tsv', 'w')
out.write('class\tvital\tname\tspell\tamount\ttick\tduration\tlive\tparents\tdescription\n')
summ = defaultdict(Counter)
for sid, effs in EFF.items():
    for e in effs:
        if e['effectType'] != 1 or e['dataBits00'] not in VIT: continue
        amt = s32(e['dataBits01']); c = cls(sid); lv = reach(sid)
        out.write('%d\t%d\t%s\t%d\t%d\t%d\t%d\t%d\t%s\t%s\n' % (c, e['dataBits00'], VIT[e['dataBits00']], sid, amt, e['tickTime'], e['durationTime'], lv,
                  ','.join(map(str, sorted(parents.get(sid, ()))[:5])), S4[sid]['description'] if sid in S4 else '?'))
        if lv:
            summ[(c, VIT[e['dataBits00']])]['+' if amt > 0 else '-' if amt < 0 else '0'] += 1
            if e['tickTime']: summ[(c, VIT[e['dataBits00']])]['ticking'] += 1
for (c, v), cnt in sorted(summ.items())[:30]:
    print('class %2d %-14s %s' % (c, v, dict(cnt)))
