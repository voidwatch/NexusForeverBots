#!/usr/bin/env python3
"""tooltip_generators.py: class abilities whose action bar tooltip cites ($(eff=N...)) a VitalModifier row on a class resource vital that
the ability itself does not reach (no proxy, proc or cast event links it). Those generator spells ("[Hidden] Engineer - Add 15 Volatility")
are referenced only by the tooltip. Writes research/decode/resources/tooltip-generators.tsv, prints a summary (<= 30 lines)."""
import sys, os, re, struct
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text
from combatlib import walk, spell4
from collections import Counter
def s32(u): return u - (1 << 32) if u >= 1 << 31 else u
E = {e['ID']: e for e in load('Spell4Effects')}
SB = {r['ID']: r for r in load('Spell4Base')}
VIT = {4, 6, 7, 8, 9, 10, 11, 12, 18, 19, 26}
live = set(r['spell4Id'] for r in load('SpellLevel'))
tiers = {}
for s in spell4.values():
    tiers.setdefault(s['spell4BaseIdBaseSpell'], []).append(s['ID'])
rows = []; stat = Counter()
for sid, s in spell4.items():
    cls = SB.get(s['spell4BaseIdBaseSpell'], {}).get('classIdPlayer', 0)
    if not cls: continue
    tip = text(s['localizedTextIdActionBarTooltip']) or ''
    cited = [int(m) for m in re.findall(r'\$\(eff=(\d+)\.data0[12]raw', tip, re.I)]
    if not cited: continue
    reached = {e['ID'] for _, e in walk(sid)}
    for eid in cited:
        e = E.get(eid)
        if not e or e['effectType'] != 1 or e['dataBits00'] not in VIT: continue
        linked = eid in reached
        rows.append((cls, sid, s['description'], eid, e['spellId'], e['dataBits00'], s32(e['dataBits01']), e['prerequisiteIdCasterApply'], int(linked), int(sid in live)))
        stat[(cls, 'linked' if linked else 'tooltip only', sid in live)] += 1
with open('research/decode/resources/tooltip-generators.tsv', 'w') as f:
    f.write('class\tspell\tdescription\teffect\tgenerator_spell\tvital\tamount\tprereq_caster_apply\tlinked_in_tables\tin_spelllevel\n')
    for r in rows: f.write('\t'.join(map(str, r)) + '\n')
for k, n in sorted(stat.items()): print('class %d %-12s spelllevel=%s: %d' % (k[0], k[1], k[2], n))
for r in [r for r in rows if not r[8] and r[9]][:14]: print('  ', r[1], r[2][:45], '-> eff', r[3], 'spell', r[4], 'vital', r[5], r[6], 'pre', r[7])
