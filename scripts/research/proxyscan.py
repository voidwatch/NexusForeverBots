#!/usr/bin/env python3
"""Who casts a proxied spell? For Proxy effects (type 26) by where they point (tf of the proxy effect) and
DataBits04, count the proxied spell's effect types by their own target flags."""
import sys, collections
sys.path.insert(0, __import__('os').path.dirname(__file__))
from spellinfo import *
N = {0:'?'}
try:
    import re
    src = open(__import__('os').path.join(__import__('os').path.dirname(__file__), '../../Source/NexusForever.WorldServer/Game/Spell/Static/SpellEffectType.cs')).read()
    for m in re.finditer(r'(\w+)\s*=\s*(\d+)', src): N[int(m.group(2))] = m.group(1)
except Exception: pass
c = collections.Counter(); bits4 = collections.Counter()
for sid, effs in EFF.items():
    cls = SB.get(S4.get(sid, {}).get('spell4BaseIdBaseSpell'), {}).get('classIdPlayer', 0)
    for e in effs:
        if e['effectType'] != 26: continue
        p = e['dataBits00'] or e['dataBits01']
        bits4[(e['targetFlags'], e['dataBits04'] if e['dataBits04'] < 1000 else -1)] += 1
        for pe in EFF.get(p, []):
            if pe['effectType'] in (0, 1, 8, 10, 14, 26):
                c[(('player' if cls else 'npc'), e['targetFlags'], N.get(pe['effectType'], pe['effectType']), pe['targetFlags'])] += 1
print('proxy (tf, bits04) counts:', bits4.most_common(12))
for k, v in sorted(c.items(), key=lambda x: -x[1])[:30]:
    print(v, k)
