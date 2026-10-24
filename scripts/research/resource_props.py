#!/usr/bin/env python3
"""resource_props.py: every UnitPropertyModifier (effect 11) row on a resource property (focus pool/recovery, ResourceMax0-10,
ResourceRegenMultiplier0-10, 117), grouped by property and class of the spell. Writes research/decode/resources/resource-props.tsv,
prints a summary (<= 30 lines)."""
import sys, os, struct
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
from collections import defaultdict
def f(u): return struct.unpack('<f', struct.pack('<I', u & 0xffffffff))[0]
S4 = {r['ID']: r for r in load('Spell4')}
SB = {r['ID']: r for r in load('Spell4Base')}
PROPS = {5: 'BaseFocusPool', 6: 'RatingFocusRecovery', 9: 'ResourceMax0', 10: 'ResourceMax1', 11: 'ResourceMax2', 12: 'ResourceMax3',
         13: 'ResourceMax4', 14: 'ResourceMax5', 15: 'ResourceMax6', 38: 'ResourceMax7', 50: 'ResourceMax8', 52: 'ResourceMax9', 54: 'ResourceMax10',
         107: 'FocusRecoveryInCombat', 108: 'FocusRecoveryOutOfCombat', 117: 'MechanicRegenOrDecay', 135: 'FocusCostModifier'}
for i in range(7): PROPS[16 + i] = 'ResourceRegenMultiplier%d' % i
PROPS.update({39: 'ResourceRegenMultiplier7', 51: 'ResourceRegenMultiplier8', 53: 'ResourceRegenMultiplier9', 55: 'ResourceRegenMultiplier10'})
out = open('research/decode/resources/resource-props.tsv', 'w')
out.write('property\tname\tclass\tspell\ttype\tA\tB\tduration\tdescription\n')
groups = defaultdict(list)
for e in load('Spell4Effects'):
    if e['effectType'] != 11 or e['dataBits00'] not in PROPS: continue
    s = S4.get(e['spellId'])
    if not s: continue
    cls = SB.get(s['spell4BaseIdBaseSpell'], {}).get('classIdPlayer', 0)
    a, b = f(e['dataBits02']), f(e['dataBits03'])
    out.write('%d\t%s\t%d\t%d\t%d\t%g\t%g\t%d\t%s\n' % (e['dataBits00'], PROPS[e['dataBits00']], cls, s['ID'], e['dataBits01'], a, b, e['durationTime'], s['description']))
    groups[(e['dataBits00'], cls)].append((s['ID'], e['dataBits01'], a, b, e['durationTime'], s['description']))
n = 0
for (p, cls), rows in sorted(groups.items()):
    if n >= 30: break
    ex = rows[0]
    print('%-26s class %d: %3d rows, e.g. %d t%d A=%g B=%g dur %d %s' % (PROPS[p] + '(%d)' % p, cls, len(rows), ex[0], ex[1], ex[2], ex[3], ex[4], ex[5][:45]))
    n += 1
