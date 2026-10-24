#!/usr/bin/env python3
"""Damage/Heal rows with no parameter at all. The client formula (client code) gives
(DataBits01 + 0) x DataBits00 for them (0 when DataBits01 is 0); the server keeps DataBits00 x assault power (SpellEffectHandler.cs).
Counts the rows, the spells holding them, and the creatures (Jabbithole abilities, data/research/creature-abilities.tsv) that cast them,
ranked by spawned rows (research/decode/combat/spawned.tsv).   -> research/decode/combat/noparam-creatures.tsv, summary on stdout"""
import os, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
import tbl
from combatlib import ROOT, spell4, eff_by_spell, fl, walk, creature2, researched_abilities, slug
TYPES = {8: 'Damage', 12: 'DistanceDependentDamage', 33: 'DistributedDamage', 10: 'Heal', 0x76: 'HealShields', 0x8A: 'DamageShields', 5: 'Transference'}
rows = []
for sid, effs in eff_by_spell.items():
    for e in effs:
        if e['effectType'] in TYPES and not any(e[f'parameterType0{i}'] for i in range(4)):
            rows.append(e)
by_type = collections.Counter(TYPES[e['effectType']] for e in rows)
nz01 = sum(1 for e in rows if e['dataBits01'])
spells = {e['spellId'] for e in rows}
dmg_rows_total = sum(1 for effs in eff_by_spell.values() for e in effs if e['effectType'] in TYPES)
print(f'rows without parameters: {len(rows)} of {dmg_rows_total} damage/heal rows ({dict(by_type)}); DataBits01 != 0 in {nz01}; spells {len(spells)}')

spawned = collections.Counter()
f = os.path.join(ROOT, 'research/decode/combat/spawned.tsv')
for l in open(f):
    p = l.rstrip('\n').split('\t')
    if len(p) >= 3 and p[0].isdigit(): spawned[int(p[0])] += int(p[2])
abil = researched_abilities()
names = collections.defaultdict(list)
for cid, c in creature2.items():
    nm = tbl.text(c['localizedTextIdName']) if c['localizedTextIdName'] else ''
    if nm: names[slug(nm)].append(cid)

out = []
dependent_spells = set()
for sl, sids in abil.items():
    hit = []
    for sid in sids:
        if sid not in spell4: continue
        reached = [e for _, e in walk(sid) if e['effectType'] in TYPES]
        if not reached: continue
        # still on the server's DataBits00 x power fallback: no parameter and no designer amount (the server applies DataBits01 x DataBits00)
        bare = [e for e in reached if not any(e[f'parameterType0{i}'] for i in range(4)) and not (e['dataBits03'] if e['effectType'] == 5 else e['dataBits01'])]
        if bare:
            hit.append((sid, len(bare), len(reached)))
            if len(bare) == len(reached): dependent_spells.add(sid)
    if not hit: continue
    cids = names.get(sl, [])
    n = sum(spawned[c] for c in cids)
    out.append((n, sl, cids, hit))
out.sort(key=lambda o: -o[0])
spawned_creatures = sum(1 for o in out if o[0] > 0)
fallback_rows = [e for e in rows if not (e['dataBits03'] if e['effectType'] == 5 else e['dataBits01'])]
print(f'still on the fallback (no designer amount): {len(fallback_rows)} rows in {len({e["spellId"] for e in fallback_rows})} spells')
print(f'creature names casting a fallback spell: {len(out)} ({spawned_creatures} spawned, {sum(o[0] for o in out)} spawn rows); '
      f'abilities whose every damage/heal row is bare (fully on the fallback): {len(dependent_spells)}')
with open(os.path.join(ROOT, 'research/decode/combat/noparam-creatures.tsv'), 'w') as w:
    w.write('spawns\tcreature\tcreature2_ids\tspell(bare rows/damage rows)\n')
    for n, sl, cids, hit in out:
        w.write(f"{n}\t{sl}\t{','.join(map(str, cids[:6]))}\t{' '.join(f'{s}({b}/{t})' for s, b, t in hit)}\n")
print('top 20 by spawn rows: spawns creature spell(bare/damage rows) description')
for n, sl, cids, hit in out[:20]:
    s, b, t = hit[0]
    print(f"{n:5d} {sl[:28]:28s} {len(hit)} spells, e.g. {s}({b}/{t}) {(spell4[s]['description'] or '')[:55]}")
