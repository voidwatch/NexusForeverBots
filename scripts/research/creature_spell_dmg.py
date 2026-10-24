#!/usr/bin/env python3
"""Damage the most-cast creature spells deal to a level-appropriate player, computed offline from the game tables the way the server
computes it (Game/Spell/SpellEffectHandler.cs HandleEffectDamage, Combat/SpellFormula.cs, Combat/DamageMath.cs).

    scripts/research/creature_spell_dmg.py <soak failures.txt> [top N] > research/decode/combat/creature-spell-dmg.tsv

Per spell: the effects reached through Proxy chains, the caster (creature-abilities.tsv page -> Creature2 by name and level), the
amount today and with assault power = assault rating x 0.25 (client GetAssaultPower, GameFormula 1266), the player's mitigation
(client curve 1234 on the level-row armor) and the share of a level-row player's health + shield one cast takes.
Prints a summary of at most 30 lines on stderr."""
import sys, re, os, math, collections
sys.path.insert(0, os.path.dirname(__file__))
import tbl

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
eff_by_spell = collections.defaultdict(list)
for e in tbl.load('Spell4Effects'):
    eff_by_spell[e['spellId']].append(e)
spell4 = tbl.index('Spell4')
creature2 = tbl.load('Creature2')
L = {r['ID']: r for r in tbl.load('CreatureLevel')}
GF = {r['ID']: r for r in tbl.load('GameFormula')}
AP, ARMOR, HEALTH, SHIELD = 35, 42, 7, 41
handled = set()
for root, _, files in os.walk(os.path.join(ROOT, 'Source/NexusForever.WorldServer/Game')):
    for f in files:
        if f.endswith('.cs'):
            handled |= set(re.findall(r'SpellEffectHandler\(SpellEffectType\.(\w+)', open(os.path.join(root, f), encoding='utf-8-sig').read()))
etype = {}
for l in open(os.path.join(ROOT, 'Source/NexusForever.WorldServer/Game/Spell/Static/SpellEffectType.cs'), encoding='utf-8-sig'):
    m = re.match(r'\s*(\w+)\s*=\s*0x([0-9A-Fa-f]+)', l)
    if m: etype[int(m.group(2), 16)] = m.group(1)

def lv(level, k): return L[max(1, min(50, level))][f'unitPropertyValue{k:02d}']
def mult(t, i, k):
    r = tbl.index(t).get(i); return r[f'unitPropertyMultiplier{k:03d}'] if r else 1.0
def cstat(c, level, k):
    return lv(level, k) * mult('Creature2ArcheType', c['creature2ArcheTypeId'], k) * mult('Creature2Difficulty', c['creature2DifficultyId'], k) * mult('Creature2Tier', c['creature2TierId'], k)
def fl(u): import struct; return struct.unpack('<f', struct.pack('<I', u & 0xffffffff))[0]
def curve(fid, rating, level, base=0.0):
    g = GF[fid]
    i0, i1 = [v for k, v in g.items() if k.lower().startswith('dataint')][:2]
    f0, f1, f2, f3 = [v for k, v in g.items() if k.lower().startswith('datafloat')][:4]
    s = f0 / level * f1; knee = (f2 - i0) / s
    v = s * rating + i0 if rating < knee else f2 + s * (rating - knee) * math.cos(math.pi / 2 * (rating - knee) / ((rating - knee) + f3))
    return min(v * 0.01 + base, i1 * 0.01)

PROXY = {0x13, 0x19, 0x1A, 0x5E, 0x60}  # ProxyLinearAE, ProxyChannel, Proxy, ProxyChannelVariableTime, ProxyRandomExclusive
DAMAGE = {8: 'Damage', 12: 'DistanceDependentDamage', 33: 'DistributedDamage'}

def walk(sid, depth=0, seen=None):
    seen = seen if seen is not None else set()
    if sid in seen or depth > 4: return []
    seen.add(sid); out = []
    s = spell4.get(sid)
    for e in eff_by_spell.get(sid, []):
        out.append((depth, e))
        if e['effectType'] in PROXY:
            for k in range(10):
                child = e[f'dataBits{k:02d}']
                if child in spell4 and child != sid: out += walk(child, depth + 1, seen)
                if e['effectType'] in (0x13, 0x19, 0x1A) and k >= 1: break
    if s:
        for k in range(4):
            ev = s[f'spell4IdCastEvent{k:02d}']
            if ev: out += walk(ev, depth + 1, seen)
    return out

names = {}
for c in creature2:
    n = re.sub(r'\{p:[^}]*\}', '', tbl.text(c['localizedTextIdName']) or '').strip()
    names.setdefault(re.sub(r'[^a-z0-9]+', '-', n.lower().replace("'", '')).strip('-'), []).append(c)
pages = collections.defaultdict(list)
for l in open(os.path.join(ROOT, 'data/research/creature-abilities.tsv')):
    f = l.rstrip('\n').split('\t')
    if len(f) >= 4 and f[3].isdigit():
        lo = re.findall(r'\d+', f[2]); pages[int(f[3])].append((f[0], f[1], int(lo[0]) if lo else 0))

top = []
sec = False
for l in open(sys.argv[1]):
    if l.startswith('== creature-spell '): sec = True; continue
    if sec and l.startswith('=='): break
    m = re.match(r'\s*(\d+)x cast (\d+) (.*)', l) if sec else None
    if m: top.append((int(m.group(1)), int(m.group(2)), m.group(3).strip()))
top = top[:int(sys.argv[2]) if len(sys.argv) > 2 else 25]

print('casts\tspell\tname\tcaster\tcreature\tlevel\tarchetype/difficulty/tier\tcaster_AR\teffects\tdmg_before_fix\tdmg_now\tplayer_hp+sh\tplayer_mitig\tpct_before\tpct_now\tnotes')
summary = []
for casts, sid, desc in top:
    pg = pages.get(sid, [])
    caster = pg[0] if pg else ('?', '?', 0)
    slug = re.sub(r'[^a-z0-9]+', '-', caster[0].lower().replace("'", '')).strip('-')
    cands = names.get(slug, [])
    level = caster[2] or 20
    # a creature named in the spell description (boss spells: 'e449 - Forgemaster Trogun - Inferno') beats the first researched page
    for seg in re.sub(r'\[[^]]*\]|\([^)]*\)', '', desc).split(' - '):
        sl = re.sub(r'[^a-z0-9]+', '-', seg.strip().lower().replace("'", '')).strip('-')
        if sl in names and sl != slug:
            named = [q for q in pg if re.sub(r'[^a-z0-9]+', '-', q[0].lower().replace("'", '')).strip('-') == sl]
            cands = names[sl]; caster = named[0] if named else (seg.strip(), '(named in spell)', cands[0].get('minLevel', 0) or level)
            level = caster[2] or level
            break
    c = cands[0] if cands else None
    ar = cstat(c, level, AP) if c else lv(level, AP)
    notes = []
    if len(cands) > 1: notes.append(f'{len(cands)} Creature2 rows share the name')
    if not pg: notes.append('no researched caster')
    today = quarter = 0.0
    parts = []
    for depth, e in walk(sid):
        t = e['effectType']
        if t not in DAMAGE:
            if etype.get(t) not in handled and t not in PROXY: notes.append(f'unhandled {etype.get(t, t)}')
            elif t in PROXY and etype.get(t) not in handled: notes.append(f'unhandled proxy {etype.get(t, t)} (children not reached)')
            continue
        if etype.get(t) not in handled:
            notes.append(f'unhandled {DAMAGE[t]}'); continue
        terms = [(e[f'parameterType0{k}'], e[f'parameterValue0{k}']) for k in range(4) if e[f'parameterType0{k}']]
        def amount(apf, client=False):
            # old server: sum of value x stat (rating for 12/13); client (client code): stat part with the creature
            # soft cap (GameFormula 712) + (DataBits01 + flat part) x DataBits00, power = rating x 0.25, types 6/9/20/24 add nothing
            tot, stat, coefs, n, flat, any_ = 0.0, 0.0, 0.0, 0, 0.0, False
            for pt, pv in terms:
                if pt in (1, 2, 3, 4, 5): st = cstat(c, level, pt - 1) if c else lv(level, pt - 1)
                elif pt == 10: st = level
                elif pt == 11: st = cstat(c, level, 40) if c else lv(level, 40)
                elif pt == 12: st = ar * apf
                elif pt == 13: st = (cstat(c, level, 36) if c else lv(level, 36)) * apf
                else: continue
                tot += pv * st; any_ = True
                if pt == 10: flat += pv * st
                else: stat += pv * st; coefs += pv; n += 1
            if not terms:
                coef = fl(e['dataBits00']); coef = coef if 0 < coef < 1e6 else 1.0
                return coef * ar * apf, 'DataBits00 x power'
            if not client:
                return tot, ' + '.join(f'{pv:g}x{pt}' for pt, pv in terms)
            stat = math.floor(stat + 0.5)
            g = GF[712]; f0, f1, f2, f3 = g['datafloat0'], g['datafloat01'], g['datafloat02'], g['datafloat03']
            cap = (level * f0 + f1) * abs(coefs / n) if n else 0
            if n and stat > cap > 0: stat = cap * (1 + f2 * (1 - math.exp(-(stat - cap) / cap)))
            m = fl(e['dataBits00']); m = m if math.isfinite(m) else 0.0
            return (stat + (e['dataBits01'] + flat) * m) / f3, ''
        a, how = amount(1.0); b, _ = amount(0.25, True)
        ticks = 1
        if e['tickTime'] and e['durationTime'] > e['tickTime']:
            ticks = min(e['durationTime'] // e['tickTime'], 60)
            notes.append(f'periodic {e["durationTime"]}/{e["tickTime"]} ms: once on the old server, {ticks} ticks now')
        today += a; quarter += b * ticks
        parts.append(f'{e["ID"]}:{DAMAGE[t]} dt{e["damageType"]} d{depth} [{how}]')
    php = lv(level, HEALTH) + lv(level, SHIELD)
    mit = curve(1234, lv(level, ARMOR), level)
    pt = 100 * today * (1 - mit) / php; pq = 100 * quarter * (1 - mit) / php
    if today == 0: notes.append('ZERO damage')
    arch = f"{c['creature2ArcheTypeId']}/{c['creature2DifficultyId']}/{c['creature2TierId']}" if c else '-'
    print(f"{casts}\t{sid}\t{desc[:70]}\t{caster[0]} ({caster[1]})\t{c['ID'] if c else '-'}\t{level}\t{arch}\t{ar:.0f}\t{'; '.join(parts) or '-'}\t{today:.0f}\t{quarter:.0f}\t{php:.0f}\t{mit:.3f}\t{pt:.1f}\t{pq:.1f}\t{'; '.join(sorted(set(notes)))}")
    summary.append(f'{sid:>6} L{level:<2} {caster[0][:22]:22} before {today:7.0f} ({pt:5.1f}% hp+sh)  now {quarter:6.0f} ({pq:5.1f}%)  {"; ".join(sorted(set(notes)))[:70]}')
for s in summary[:30]: print(s, file=sys.stderr)
