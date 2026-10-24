#!/usr/bin/env python3
"""Time-to-kill per boss and elite family, offline from the game tables with the server's formulas (combatlib.py).

    scripts/research/ttk.py [bot-stats.tsv] [spawned.tsv]   -> research/decode/combat/ttk-families.tsv, ttk-bosses.tsv, ttk-summary.txt

Player: a level-appropriate geared player = CreatureLevel row of the level + a full item set of item level = level (ItemStats.cs formulas:
health 60 x ilvl x slot, armor 20 x ilvl x slot (medium), power 111.375 x ilvl weapon + 3.6 x ilvl x 9 slots at 75% assault, energy
shield 75 x 2 x ilvl), crit 5% x severity 1.5. gt botstats data (live bots of that level +-2, median) is shown next to it
(the live mortal bots carry less: summary lines 'bots'). Player DPS per class from the class abilities of SpellLevel up to the level (tier 1, the largest Damage row reached
through proxies per cast, periodic rows for all ticks; busy time = max(table global cooldown, cast time, channel)): 'low' = the best builder (no cooldown, no class-resource cost) cast back to back, 'high' = a greedy
60 s rotation of every ability off its cooldown with one spender per 4 s (guess), 'server' = spenders whenever the global cooldown
allows (the server does not charge Spell4 innateCost today). The middle class
of the six is reported. Creature: table stats (health + shield, armor mitigation); its damage = researched auto-attack row every 2.5 s
(else 0.8 x (Strength + AP)) + researched abilities (one per 3 s + cast time, off their cooldowns; NonPlayer.TryCastAbility), the largest
row per cast sustained, every telegraph landing for the 5 s burst. Bosses: encounter names (data/encounters) matched to the Creature2 rows
spawned in the encounter's world (else every spawned row of the name, else the highest-rank row, marked unspawned). Player effective health = health + min(shield, health) (half of each hit goes to the shield, 0.5 default).
Flags: a single player kills a boss in under 30 s; a boss kills a level-appropriate player in under 5 s."""
import sys, os, re, math, statistics, collections
sys.path.insert(0, os.path.dirname(__file__))
import tbl
from combatlib import *

OUT = os.path.join(ROOT, 'research/decode/combat')
os.makedirs(OUT, exist_ok=True)
bot_stats = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OUT, 'bot-stats.tsv')
spawned_f = sys.argv[2] if len(sys.argv) > 2 else os.path.join(OUT, 'spawned.tsv')

# ---------------- player ----------------
CLASSES = {1: 'Warrior', 2: 'Engineer', 3: 'Esper', 4: 'Medic', 5: 'Stalker', 7: 'Spellslinger'}
spell_level = collections.defaultdict(list)
for r in tbl.load('SpellLevel'):
    if r['spell4Id'] in spell4: spell_level[r['classId']].append((r['characterLevel'], r['spell4Id']))

def gear(level):
    il = level
    return dict(health=60 * il * 6.0, armor=20 * il * 6.0, assault=111.375 * il * 0.75 + 3.6 * il * 9 * 0.75, shield=75 * 2.0 * il)

bots = collections.defaultdict(list)
if os.path.exists(bot_stats):
    for i, l in enumerate(open(bot_stats)):
        f = l.rstrip('\n').split('\t')
        if i == 0 or len(f) < 13: continue
        bots[int(f[2])].append(dict(cls=f[1], health=float(f[3]), shield=float(f[4]), mit=float(f[7]), assault=float(f[8]), crit=float(f[10]), sev=float(f[11]), eq=int(f[12])))

def player(level):
    near = [b for d in (0, -1, 1, -2, 2) for b in bots.get(level + d, [])]
    g = gear(level)
    model = dict(health=lv(level, HEALTH) + g['health'], shield=lv(level, SHIELD) + g['shield'], assault=lv(level, AP) + g['assault'],
                 mit=mitigation(lv(level, ARMOR) + g['armor'], level), crit=0.05, sev=1.5, src='model')
    if len(near) >= 3:
        med = lambda k: statistics.median(b[k] for b in near)
        return model, dict(health=med('health'), shield=med('shield'), assault=med('assault'), mit=med('mit'), crit=med('crit'), sev=med('sev'),
                           src=f'bots({len(near)})')
    return model, None

_dps = {}
def class_dps(cls, level, assault):
    key = (cls, level, round(assault))
    if key in _dps: return _dps[key]
    def stat(pt):
        if pt in (1, 2, 3, 4, 5): return lv(level, pt - 1)
        if pt == 10: return level
        if pt == 11: return lv(level, 40)
        if pt in (12, 13): return assault * 0.25
        return None
    abil = []
    for lvl, sid in spell_level[cls]:
        s = spell4[sid]
        # tier 1 only: higher tiers cost ability points (a level-appropriate player has some; tier 1 keeps the estimate conservative)
        if lvl > level or s['tierIndex'] != 1: continue
        dmg = cast_damage(sid, level, stat, True)
        if dmg <= 0: continue
        gcd = COOLDOWN.get(s['spellCoolDownIdGlobal'], 1000) / 1000
        busy = max(0.25, s['castTime'] / 1000, s['channelMaxTime'] / 1000 if s['channelMaxTime'] else 0, gcd)
        # spenders: a class-resource cost (Spell4 innateCost, types other than 15 Focus and 16 Dash); the server does not charge it
        spender = any(s[f'innateCost{k}'] > 0 and s[f'innateCostType{k}'] not in (0, 15, 16) for k in (0, 1))
        abil.append((dmg, busy, s['spellCoolDown'] / 1000, sid, spender))
    if not abil:
        _dps[key] = (0.0, 0.0, 0.0); return _dps[key]
    def rotation(spender_gap):
        t = 0.0; total = 0.0; ready = {a[3]: 0.0 for a in abil}; next_spend = 0.0
        while t < 60:
            cand = [a for a in abil if ready[a[3]] <= t and (not a[4] or (spender_gap is not None and next_spend <= t))]
            if not cand: t = min([v for v in ready.values() if v > t] + [next_spend if next_spend > t else t + 0.25]); continue
            a = max(cand, key=lambda a: a[0] / a[1])
            total += a[0]; ready[a[3]] = t + max(a[2], a[1]); t += a[1]
            if a[4]: next_spend = t + spender_gap
        return total / t
    spam = [a for a in abil if a[2] <= a[1] and not a[4]]
    low = max((a[0] / a[1] for a in spam), default=0.0)
    # 'high': one spender per 4 s of builders (guess: three 1 s builder casts fill a WildStar class resource for one spender);
    # 'server': spenders as often as the global cooldown allows, which is what the server permits today
    high = max(low, rotation(4.0))  # greedy is not optimal: never below plain builder spam
    _dps[key] = (low, high, max(high, rotation(0.0))); return _dps[key]

def player_dps(p, level):
    """middle class of the six (median low, median high) and the extremes"""
    vals = [class_dps(c, level, p['assault']) for c in CLASSES]
    crit = 1 + p['crit'] * (p['sev'] - 1)
    return tuple(statistics.median(v[i] for v in vals) * crit for i in range(3))

# ---------------- creatures ----------------
abilities_by_slug = researched_abilities()
worlds = tbl.index('World')
spawned = collections.defaultdict(lambda: [0, set(), []])
if os.path.exists(spawned_f):
    for l in open(spawned_f):
        f = l.rstrip('\n').split('\t')
        if len(f) < 4 or not f[0].isdigit(): continue
        c = int(f[0]); s = spawned[c]; s[0] += int(f[2]); s[1].add(int(f[1]))
        if f[3] not in ('NULL', ''): s[2].append(int(float(f[3])))

def creature_level(cid):
    s = spawned.get(cid)
    if s and s[2]: return max(1, min(50, round(statistics.median(s[2]))))
    c = creature2[cid]
    return max(1, min(50, (c['minLevel'] + max(c['maxLevel'], c['minLevel'])) // 2 or 0)) if c['minLevel'] else 0

def creature_damage(c, level, p_mit):
    stat = creature_stat_fn(c, level)
    spells = abilities_by_slug.get(slug(creature_name(c)), [])
    auto = None; abil = []
    seen = set()
    for sid in spells:
        s = spell4.get(sid)
        if not s: continue
        d = s['description'] or ''
        basic = 'auto-attack' in d.lower() or 'basic attack' in d.lower()
        if basic:
            if auto is None:
                e = next((e for e in eff_by_spell.get(sid, []) if e['effectType'] == 8), None)
                if e: auto = amount(e, level, stat, False)
            continue
        if s['spell4BaseIdBaseSpell'] in seen: continue
        seen.add(s['spell4BaseIdBaseSpell'])
        dmg = cast_damage(sid, level, stat, False, all_rows=True)
        abil.append((dmg, s['castTime'] / 1000, s['spellCoolDown'] / 1000 if s['spellCoolDown'] else 10.0, sid, cast_damage(sid, level, stat, False)))
    if auto is None: auto = 0.8 * (cstat(c, level, STR) + cstat(c, level, AP) * 0.25)
    auto *= (1 - p_mit)
    # sustained: autos + abilities, one per 3 s + cast, each off its cooldown (expected value of the random pick ~ mean)
    if abil:
        gap = 3 + statistics.mean(a[1] for a in abil)
        rate = min(1 / gap, sum(1 / a[2] for a in abil))
        abil_dps = rate * statistics.mean(a[4] for a in abil) * (1 - p_mit)
    else: abil_dps = 0.0
    dps = auto / 2.5 + abil_dps
    # burst in the first 5 s: swings at 0, 2.5, 5 and the two hardest abilities (t = 0 and t = 3 + cast <= 5)
    top = sorted(abil, reverse=True)
    burst = 3 * auto + sum(a[0] for a in top[:1]) * (1 - p_mit)
    if len(top) > 1 and 3 + top[0][1] <= 5: burst += top[1][0] * (1 - p_mit)
    worst = max([a for a in top[:1]], default=(0, 0, 0, 0, 0))
    return dps, burst, len(abil), worst[0] * (1 - p_mit), worst[3]

def ehp(p): return p['health'] + min(p['shield'], p['health'])

# encounter bosses: names in data/encounters (dungeons, raids, world bosses, raidcore)
import json, glob
boss_names = {}
for f in glob.glob(os.path.join(ROOT, 'data/encounters/**/*.json'), recursive=True):
    try: data = json.load(open(f))
    except Exception: continue
    for d in data if isinstance(data, list) else [data]:
        if isinstance(d, dict) and d.get('boss'): boss_names[slug(d['boss'])] = (d['boss'], d.get('world', 0), os.path.basename(os.path.dirname(f)))

BOSS_RANKS = {6, 12, 18, 24}
ELITE_RANKS = {5, 11, 17, 23, 10, 16, 22}
by_name = collections.defaultdict(list)
for cid, c in creature2.items():
    if slug(creature_name(c)) in boss_names: by_name[slug(creature_name(c))].append(cid)
boss_ids = {}
for sl, ids in by_name.items():
    world = boss_names[sl][1]
    ids = [i for i in ids if creature2[i]['creature2DifficultyId']] or ids  # rank-0 rows are flavor copies (EncounterManager skips them)
    pick = [i for i in ids if world in spawned.get(i, (0, set()))[1]] or [i for i in ids if i in spawned]
    note = '' if pick else 'unspawned'
    if not pick: pick = [max(ids, key=lambda i: (creature2[i]['creature2DifficultyId'], i))]
    for i in pick: boss_ids[i] = note
fam = collections.defaultdict(list)
for cid, c in creature2.items():
    rank = c['creature2DifficultyId']
    nm = creature_name(c)
    is_boss = cid in boss_ids
    if rank not in BOSS_RANKS | ELITE_RANKS and not is_boss: continue
    if cid not in spawned and not is_boss: continue
    level = creature_level(cid)
    if not level: continue
    fam[(rank, c['creature2ArcheTypeId'], c['creature2TierId'])].append((cid, level, nm, is_boss))

lines = ['rank\trank_name\tgroup\tarchetype\ttier\tcreatures\tlevels\texample\thealth_L\tc_ehp\tp_dps_low\tp_dps_high\tp_dps_server\tttk_solo_low_s\tttk_solo_high_s\tttk_solo_server_s\tttk_group_s\tc_dps\tp_ehp\tplayer_survives_s\tburst5\tflags']
flags_all = []
def rank_name(r): return (tbl.text(DIFFICULTY[r]['localizedTextIdTitle']) if r in DIFFICULTY else f'rank {r}').replace('Rank: ', '')
def evaluate(cid, level):
    c = creature2[cid]
    p, model = player(level)
    hp = cstat(c, level, HEALTH) + cstat(c, level, SHIELD)
    cmit = mitigation(cstat(c, level, ARMOR), level)
    c_ehp = hp / (1 - cmit) if cmit < 1 else float('inf')
    lo, hi, sv = player_dps(p, level)
    dps, burst, nab, worst, wsid = creature_damage(c, level, p['mit'])
    group = (DIFFICULTY.get(c['creature2DifficultyId']) or {}).get('groupValue') or 1
    pe = ehp(p)
    return dict(hp=hp, c_ehp=c_ehp, lo=lo, hi=hi, ttk_lo=c_ehp / lo if lo else float('inf'), ttk_hi=c_ehp / hi if hi else float('inf'),
                ttk_group=c_ehp / (hi * group) if hi else float('inf'), ttk_sv=c_ehp / sv if sv else float('inf'), sv=sv, dps=dps, pe=pe, surv=pe / dps if dps else float('inf'), burst=burst,
                group=group, nab=nab, worst=worst, wsid=wsid, psrc=p['src'])

# TTK_CREATURES=id[:level],... : evaluate single creatures (top killers of the mortal runs) and stop
if os.environ.get('TTK_CREATURES'):
    print('creature\tname\tlevel\trank\thealth\tc_ehp\tttk_solo_server_s\tttk_group_s\tc_dps\tabilities\tp_ehp\tplayer_survives_s\tburst5\thardest_hit\thardest_spell\tplayer')
    for item in os.environ['TTK_CREATURES'].split(','):
        cid, _, lvl_s = item.partition(':')
        cid = int(cid)
        if cid not in creature2: print(cid, 'not in Creature2'); continue
        level = int(lvl_s) if lvl_s else creature_level(cid)
        c = creature2[cid]
        # TTK_MODE=both: the old server (normal and veteran rows both land); else the row mode of the creature's designer segment
        import combatlib
        combatlib.VETERAN = None if os.environ.get('TTK_MODE') == 'both' else (1 if 'veteran' in (c['description'] or '').lower() else 0)
        r = evaluate(cid, level)
        print(f"{cid}\t{creature_name(c)}\t{level}\t{rank_name(c['creature2DifficultyId'])}\t{r['hp']:.0f}\t{r['c_ehp']:.0f}\t{r['ttk_sv']:.0f}\t{r['ttk_group']:.0f}\t{r['dps']:.0f}\t{r['nab']}\t{r['pe']:.0f}\t{r['surv']:.1f}\t{r['burst']:.0f}\t{r['worst']:.0f}\t{r['wsid']}\t{r['psrc']}")
    sys.exit(0)

for (rank, arch, tier), members in sorted(fam.items()):
    members.sort(key=lambda m: m[1])
    mid = members[len(members) // 2]
    r = evaluate(mid[0], mid[1])
    fl_ = []
    if rank in BOSS_RANKS and r['ttk_hi'] < 30: fl_.append('solo kill <30s (rotation)')
    if rank in BOSS_RANKS and r['ttk_lo'] < 30: fl_.append('solo kill <30s (builder only)')
    if rank in BOSS_RANKS and r['ttk_sv'] < 30 <= r['ttk_hi']: fl_.append('solo kill <30s (server, spender costs not charged)')
    if r['burst'] >= r['pe']: fl_.append('kills player <5s')
    lvls = f"{members[0][1]}-{members[-1][1]}"
    lines.append(f"{rank}\t{rank_name(rank)}\t{r['group']}\t{arch}\t{tier}\t{len(members)}\t{lvls}\t{mid[0]} {mid[2][:28]} L{mid[1]}\t{r['hp']:.0f}\t{r['c_ehp']:.0f}\t{r['lo']:.0f}\t{r['hi']:.0f}\t{r['sv']:.0f}\t{r['ttk_lo']:.0f}\t{r['ttk_hi']:.0f}\t{r['ttk_sv']:.0f}\t{r['ttk_group']:.0f}\t{r['dps']:.0f}\t{r['pe']:.0f}\t{r['surv']:.1f}\t{r['burst']:.0f}\t{'; '.join(fl_)}")
    if fl_: flags_all.append((rank, arch, tier, len(members), mid, r, fl_))
open(os.path.join(OUT, 'ttk-families.tsv'), 'w').write('\n'.join(lines) + '\n')

# every encounter boss (and every boss-rank creature in an instance) individually
blines = ['creature\tname\tlevel\trank\tgroup\tencounter\thealth\tc_ehp\tttk_solo_low_s\tttk_solo_high_s\tttk_solo_server_s\tttk_group_s\tc_dps\tabilities\tplayer_survives_s\tburst5\thardest_hit\thardest_spell\tplayer\tflags']
bflags = []
for (rank, arch, tier), members in fam.items():
    for cid, level, nm, is_boss in members:
        if not is_boss: continue
        r = evaluate(cid, level)
        fl_ = []
        if r['ttk_hi'] < 30: fl_.append('solo kill <30s (rotation)')
        elif r['ttk_sv'] < 30: fl_.append('solo kill <30s (server, spender costs not charged)')
        if r['burst'] >= r['pe']: fl_.append('kills player <5s')
        enc = boss_names[slug(nm)]
        if boss_ids[cid]: fl_.append(boss_ids[cid])
        blines.append(f"{cid}\t{nm}\t{level}\t{rank_name(rank)}\t{r['group']}\t{enc[2]}:{enc[1]}\t{r['hp']:.0f}\t{r['c_ehp']:.0f}\t{r['ttk_lo']:.0f}\t{r['ttk_hi']:.0f}\t{r['ttk_sv']:.0f}\t{r['ttk_group']:.0f}\t{r['dps']:.0f}\t{r['nab']}\t{r['surv']:.1f}\t{r['burst']:.0f}\t{r['worst']:.0f}\t{r['wsid']}\t{r['psrc']}\t{'; '.join(fl_)}")
        if fl_: bflags.append((cid, nm, level, rank, r, fl_))
open(os.path.join(OUT, 'ttk-bosses.tsv'), 'w').write('\n'.join(blines) + '\n')

summ = []
summ.append(f"families {len(fam)} (boss ranks {sum(1 for k in fam if k[0] in BOSS_RANKS)}, elite ranks {sum(1 for k in fam if k[0] in ELITE_RANKS)}), encounter bosses matched {len(blines)-1} of {len(boss_names)} names; bot stats levels {sorted(bots)[:3]}..{sorted(bots)[-3:] if bots else ''}")
for lvl in (10, 15, 25, 35, 50):
    p, b = player(lvl); lo, hi, sv = player_dps(p, lvl)
    bl = f"; bots({b['src'][5:-1]}) ehp {ehp(b):.0f} AR {b['assault']:.0f} mit {b['mit']:.3f} dps {player_dps(b, lvl)[1]:.0f}" if b else ''
    summ.append(f"player L{lvl} geared: ehp {ehp(p):.0f} AR {p['assault']:.0f} mit {p['mit']:.3f} dps {lo:.0f}/{hi:.0f}/{sv:.0f}{bl}")
by = collections.Counter(f for *_, fl_ in flags_all for f in fl_)
summ.append('family flags: ' + ', '.join(f'{k} {v}' for k, v in by.items()))
by = collections.Counter(f for *_, fl_ in bflags for f in fl_)
summ.append('boss flags: ' + ', '.join(f'{k} {v}' for k, v in by.items()))
for cid, nm, level, rank, r, fl_ in sorted(bflags, key=lambda b: b[4]['ttk_hi'])[:12]:
    summ.append(f"  boss {cid} {nm[:24]} L{level} {rank_name(rank)[:20]}: solo {r['ttk_sv']:.0f}/{r['ttk_hi']:.0f}/{r['ttk_lo']:.0f}s, player lasts {r['surv']:.1f}s, burst5 {r['burst']:.0f}/{r['pe']:.0f} {'; '.join(fl_)}")
open(os.path.join(OUT, 'ttk-summary.txt'), 'w').write('\n'.join(summ) + '\n')
print('\n'.join(summ[:30]))
