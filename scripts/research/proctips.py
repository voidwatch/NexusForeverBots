#!/usr/bin/env python3
"""For each proc trigger: tooltip texts of the spells carrying it (what the player is told triggers it). -> research/decode/proc-tooltips.txt"""
import tbl, collections, re
eff=tbl.load('Spell4Effects'); sp=tbl.index('Spell4')
rows=[e for e in eff if e['effectType']==14]
tips=collections.defaultdict(collections.Counter)
for e in rows:
    s=sp.get(e['spellId'])
    if not s: continue
    for k in ('localizedTextIdActionBarTooltip','localizedTextIdCasterIconSpellText'):
        t=tbl.text(s[k])
        if t: tips[e['dataBits00']][re.sub(r'\s+',' ',t)[:230]]+=1
with open('../../research/decode/proc-tooltips.txt','w') as f:
    for t in sorted(tips, key=lambda t:-sum(tips[t].values())):
        f.write(f'== trigger {t}\n')
        for txt,n in tips[t].most_common(12): f.write(f'  {n}x {txt}\n')
print(len(tips),'triggers with tooltips')
