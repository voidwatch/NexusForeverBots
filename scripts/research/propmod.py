#!/usr/bin/env python3
"""UnitPropertyModifier (effect 11) rows by DataBits01 type: value patterns and spell names. Output research/decode/propmod.txt"""
import tbl, struct, collections
F=lambda u: struct.unpack('<f',struct.pack('<I',u))[0]
eff=tbl.load('Spell4Effects'); sp=tbl.index('Spell4'); sb=tbl.index('Spell4Base')
def sname(i):
    s=sp.get(i); 
    if not s: return '?'
    b=sb.get(s['spell4BaseIdBaseSpell']); return (s['description'] or '')[:60] + ' / ' + (tbl.text(b['localizedTextIdName']) if b else '')
rows=[e for e in eff if e['effectType']==11]
bytype=collections.defaultdict(list)
for e in rows: bytype[e['dataBits01']].append(e)
out=open('../../research/decode/propmod.txt','w')
for t,es in sorted(bytype.items()):
    props=collections.Counter(e['dataBits00'] for e in es)
    ab=collections.Counter((round(F(e['dataBits02']),3),round(F(e['dataBits03']),3)) for e in es)
    other=collections.Counter(tuple(e[f'dataBits0{i}'] for i in range(4,10)) for e in es)
    print(f'type {t}: {len(es)} rows, props {props.most_common(8)}, A/B {ab.most_common(6)}, bits4-9 {other.most_common(3)}')
    for e in es[:400]:
        out.write(f"t{t} prop {e['dataBits00']} A {F(e['dataBits02']):.4g} B {F(e['dataBits03']):.4g} raw {e['dataBits02']:#x}/{e['dataBits03']:#x} b4 {e['dataBits04']} dur {e['durationTime']} | {sname(e['spellId'])}\n")
