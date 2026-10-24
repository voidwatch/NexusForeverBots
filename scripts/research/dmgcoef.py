#!/usr/bin/env python3
"""Damage/heal effect rows: which fields carry the scaling. Writes research/decode/combat/dmgcoef.txt, prints a summary."""
import tbl, struct, collections, sys
F=lambda u: struct.unpack('<f',struct.pack('<I',u&0xffffffff))[0]
eff=tbl.load('Spell4Effects'); sp=tbl.index('Spell4'); sb=tbl.index('Spell4Base')
def sname(i):
    s=sp.get(i)
    if not s: return '?'
    b=sb.get(s['spell4BaseIdBaseSpell']); return (tbl.text(b['localizedTextIdName']) if b else '')[:30]+' T'+str(s.get('tierIndex',''))
types={int(a):b for a,b in (x.split('=') for x in sys.argv[1:])} if len(sys.argv)>1 else {0:'Damage'}
out=open('../../research/decode/combat/dmgcoef.txt','w')
print('fields:', [k for k in eff[0].keys()][:60])
for t,n in types.items():
    es=[e for e in eff if e['effectType']==t]
    print(f'== {n} ({t}): {len(es)} rows')
    pt=collections.Counter((e['parameterType00'],e['parameterType01'],e['parameterType02'],e['parameterType03']) for e in es)
    print(' paramTypes', pt.most_common(8))
    for k in range(0,5):
        c=collections.Counter(round(F(e[f'dataBits0{k}']),3) for e in es)
        print(f' db{k} float', c.most_common(6))
    for e in es[:3000]:
        out.write(f"{n} spell {e['spellId']} {sname(e['spellId'])} dt {e['damageType']} db " + ' '.join(f"{F(e[f'dataBits0{k}']):.4g}" for k in range(6)) +
                  ' pt ' + ' '.join(str(e[f'parameterType0{k}']) for k in range(4)) + ' pv ' + ' '.join(f"{e[f'parameterValue0{k}']:.4g}" for k in range(4)) + '\n')
