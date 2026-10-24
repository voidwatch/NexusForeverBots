#!/usr/bin/env python3
"""Spell4Effects ParameterType usage across damage/heal-like effects: counts, value ranges, example spells. Writes research/decode/combat/paramtypes.txt"""
import tbl, collections
eff=tbl.load('Spell4Effects'); sp=tbl.index('Spell4'); sb=tbl.index('Spell4Base')
def nm(i):
    s=sp.get(i); b=sb.get(s['spell4BaseIdBaseSpell']) if s else None
    return (tbl.text(b['localizedTextIdName']) if b else '?')[:28]
by=collections.defaultdict(list)
for e in eff:
    for k in range(4):
        t=e[f'parameterType0{k}']
        if t: by[(t,e['effectType'])].append((e[f'parameterValue0{k}'],e))
out=open('../../research/decode/combat/paramtypes.txt','w')
for (t,et),l in sorted(by.items(), key=lambda x:(x[0][0],-len(x[1]))):
    vals=sorted(v for v,_ in l)
    ex=collections.Counter(nm(e['spellId']) for _,e in l).most_common(5)
    line=f"ptype {t:3} eff {et:4} n {len(l):5} min {vals[0]:.4g} med {vals[len(vals)//2]:.4g} max {vals[-1]:.4g} ex {ex}"
    out.write(line+'\n')
    if len(l)>=30: print(line[:230])
