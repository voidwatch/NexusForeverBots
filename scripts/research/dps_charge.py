#!/usr/bin/env python3
"""dps_charge.py: class DPS with innate costs charged vs. not charged, offline, same damage formula as ttk.py (combatlib).
'off' = spenders whenever the global cooldown allows (NF_INNATE_COSTS=0: costs only counted); 'on' = the server with NF_INNATE_COSTS=1:
Resource1 (Esper Psi 5, Engineer Volatility 100, Medic Actuators 4 starting full) is charged and refilled by the VitalModifier rows each cast
reaches (self rows and proxies, e.g. Telekinetic Strike -> 30900 +1); Warrior spells need casterInnateRequirement eval 2 (KE >= value) and
build KE from their Add Cell rows (pool 1000); Stalker Suit Power and Focus costs are not charged (no sourced regeneration).
Player: CreatureLevel row + level-appropriate item set (ttk.py gear()), tier-1 abilities from SpellLevel, 60 s greedy rotation.
Writes research/decode/combat/dps-charge.tsv, prints a table (<= 30 lines)."""
import sys, os, collections
sys.path.insert(0, os.path.dirname(__file__))
import tbl
from combatlib import *

CLASSES = {1: 'Warrior', 2: 'Engineer', 3: 'Esper', 4: 'Medic', 5: 'Stalker', 7: 'Spellslinger'}
POOL = {1: (1000, 0), 2: (100, 0), 3: (5, 0), 4: (4, 4)}
CLS = {r['ID']: r for r in tbl.load('Class')}
spell_level = collections.defaultdict(list)
for r in tbl.load('SpellLevel'):
    if r['spell4Id'] in spell4: spell_level[r['classId']].append((r['characterLevel'], r['spell4Id']))

def s32(u): return u - (1 << 32) if u >= 1 << 31 else u
def slot1(cls, vital):
    return vital == 6 or (vital and CLS[cls]['vitalEnumResource01'] == vital)

def gain(cls, sid):
    g = 0
    for depth, e in walk(sid):
        if e['effectType'] == 1 and slot1(cls, e['dataBits00']) and e['tickTime'] == 0:
            a = s32(e['dataBits01'])
            if a > 0: g += a
    return g

def gear_assault(level):
    return 111.375 * level * 0.75 + 3.6 * level * 9 * 0.75   # ttk.py gear()

def dps(cls, level, charged):
    assault = lv(level, AP) + gear_assault(level)
    def stat(pt):
        if pt in (1, 2, 3, 4, 5): return lv(level, pt - 1)
        if pt == 10: return level
        if pt == 11: return lv(level, 40)
        if pt in (12, 13): return assault * 0.25
        return None
    abil = []
    for lvl, sid in spell_level[cls]:
        s = spell4[sid]
        if lvl > level or s['tierIndex'] != 1: continue
        dmg = cast_damage(sid, level, stat, True)
        if dmg <= 0: continue
        gcd = COOLDOWN.get(s['spellCoolDownIdGlobal'], 1000) / 1000
        busy = max(0.25, s['castTime'] / 1000, s['channelMaxTime'] / 1000 if s['channelMaxTime'] else 0, gcd)
        cost = sum(s['innateCost%d' % k] for k in (0, 1) if slot1(cls, s['innateCostType%d' % k]))
        req = max([s['casterInnateRequirementValue%d' % k] for k in (0, 1)
                   if slot1(cls, s['casterInnateRequirement%d' % k]) and s['casterInnateRequirementEval%d' % k] == 2] + [0])
        abil.append(dict(dmg=dmg, busy=busy, cd=s['spellCoolDown'] / 1000, sid=sid, cost=cost, req=req, gain=gain(cls, sid)))
    if not abil: return 0.0, 0, 0
    mx, res = POOL.get(cls, (0, 0))
    t = total = 0.0; ready = {a['sid']: 0.0 for a in abil}; spenders = 0
    while t < 60:
        cand = [a for a in abil if ready[a['sid']] <= t and (not charged or (res >= a['cost'] and res >= a['req']))]
        if not cand: t += 0.25; continue
        a = max(cand, key=lambda a: a['dmg'] / a['busy'])
        total += a['dmg']; ready[a['sid']] = t + max(a['cd'], a['busy']); t += a['busy']
        if a['cost'] or a['req']: spenders += 1
        if charged and mx:
            res = min(mx, max(0, res - a['cost']) + a['gain'])
    return total / t, spenders, len(abil)

rows = []
print('%-13s %5s %9s %9s %6s %10s' % ('class', 'level', 'dps off', 'dps on', 'ratio', 'spenders off/on'))
for level in (10, 25, 50):
    for cls, name in CLASSES.items():
        off, so, n = dps(cls, level, False)
        on, sn, _ = dps(cls, level, True)
        rows.append((name, level, off, on, so, sn, n))
        print('%-13s %5d %9.0f %9.0f %6.2f %7d/%d' % (name, level, off, on, on / off if off else 0, so, sn))
os.makedirs(os.path.join(ROOT, 'research/decode/combat'), exist_ok=True)
with open(os.path.join(ROOT, 'research/decode/combat/dps-charge.tsv'), 'w') as f:
    f.write('class\tlevel\tdps_off\tdps_on\tspenders_off_60s\tspenders_on_60s\tabilities\n')
    for r in rows: f.write('%s\t%d\t%.0f\t%.0f\t%d\t%d\t%d\n' % r)
