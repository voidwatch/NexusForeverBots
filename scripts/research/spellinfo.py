#!/usr/bin/env python3
"""spellinfo.py <spell4 id ...>: name, base, cast/cooldown and its effects (type, target flags, data bits, params). Capped output."""
import sys, struct
sys.path.insert(0, __import__('os').path.dirname(__file__))
from tbl import load, text
S4 = {r['ID']: r for r in load('Spell4')}
SB = {r['ID']: r for r in load('Spell4Base')}
EFF = {}
for e in load('Spell4Effects'):
    EFF.setdefault(e['spellId'], []).append(e)
def f(u): return struct.unpack('<f', struct.pack('<I', u & 0xffffffff))[0]
def name(i):
    r = S4.get(i)
    if not r: return '?'
    b = SB.get(r['spell4BaseIdBaseSpell'])
    return (text(b['localizedTextIdName']) if b else '?') + ' / ' + (r.get('description') or '')
def show(i, effects=True):
    r = S4.get(i)
    if not r: print(i, 'missing'); return
    b = SB.get(r['spell4BaseIdBaseSpell'], {})
    print(f"{i} [{name(i)}] base {r['spell4BaseIdBaseSpell']} class {b.get('classIdPlayer')} cast {r['castTime']} cd {r['spellCoolDown']} grp {r.get('spell4GroupListId')} tags {[r['spell4TagId0%d'%k] for k in range(5)]}")
    if effects:
        for e in EFF.get(i, [])[:12]:
            print(f"   eff {e['effectType']:3} tf {e['targetFlags']} dur {e['durationTime']} bits {[e['dataBits0%d'%k] for k in range(6)]} pt {[e['parameterType0%d'%k] for k in range(4)]} pv {[round(e['parameterValue0%d'%k],3) for k in range(4)]}")
if __name__ == '__main__':
    for a in sys.argv[1:]:
        show(int(a))
