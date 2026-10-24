#!/usr/bin/env python3
"""Proc (effect 14) rows by trigger (DataBits00) and target mode (DataBits03), with spell names. Output research/decode/procs.txt"""
import tbl, struct, collections
F=lambda u: struct.unpack('<f',struct.pack('<I',u))[0]
eff=tbl.load('Spell4Effects'); sp=tbl.index('Spell4'); sb=tbl.index('Spell4Base')
def sname(i):
    s=sp.get(i)
    if not s: return '?'
    b=sb.get(s['spell4BaseIdBaseSpell']); return (s['description'] or '')[:70]+' / '+(tbl.text(b['localizedTextIdName']) if b else '')
rows=[e for e in eff if e['effectType']==14]
print(len(rows),'procs; triggers',collections.Counter(e['dataBits00'] for e in rows).most_common(30))
print('modes',collections.Counter(e['dataBits03'] for e in rows).most_common(12))
print('trigger x mode',collections.Counter((e['dataBits00'],e['dataBits03']) for e in rows).most_common(25))
with open('../../research/decode/procs.txt','w') as f:
    for t in sorted(set(e['dataBits00'] for e in rows)):
        es=[e for e in rows if e['dataBits00']==t]
        f.write(f'== trigger {t} ({len(es)})\n')
        for e in es[:60]:
            f.write(f"  mode {e['dataBits03']} chance {F(e['dataBits02']):.3g} icd {e['dataBits04']} b5-9 {[e[f'dataBits0{i}'] for i in range(5,10)]} tf {e['targetFlags']} | {sname(e['spellId'])} -> {e['dataBits01']} {sname(e['dataBits01'])}\n")
