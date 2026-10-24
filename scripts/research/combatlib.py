"""Shared offline combat math for the research scripts: the same formulas the world server uses (Game/Combat/SpellFormula.cs,
UnitStats.cs, DamageMath.cs, ItemStats.cs), computed from the game tables. Used by ttk.py."""
import os, re, math, struct, collections
import tbl

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
STR, AP, SP, HEALTH, SHIELD, ARMOR = 0, 35, 36, 7, 41, 42
PROXY = {0x13, 0x19, 0x1A, 0x5E, 0x60}  # ProxyLinearAE, ProxyChannel, Proxy, ProxyChannelVariableTime, ProxyRandomExclusive
DAMAGE = {8: 'Damage', 12: 'DistanceDependentDamage', 33: 'DistributedDamage'}

spell4 = tbl.index('Spell4')
creature2 = tbl.index('Creature2')
eff_by_spell = collections.defaultdict(list)
for _e in tbl.load('Spell4Effects'):
    eff_by_spell[_e['spellId']].append(_e)
L = {r['ID']: r for r in tbl.load('CreatureLevel')}
GF = {r['ID']: r for r in tbl.load('GameFormula')}
COOLDOWN = {r['ID']: r['cooldownTime'] for r in tbl.load('SpellCoolDown')}
DIFFICULTY = tbl.index('Creature2Difficulty')
PREREQ = tbl.index('Prerequisite')
# instance mode for prerequisite type 143 (0 normal, 1 veteran; Spell.DifficultyAllows): None = every row (the old behaviour)
VETERAN = None


def difficulty_ok(e):
    """False when a type 143 term of the effect's apply prerequisites (AND rows, Equal/NotEqual) excludes the row in mode VETERAN."""
    if VETERAN is None: return True
    for f in ('prerequisiteIdCasterApply', 'prerequisiteIdTargetApply'):
        r = PREREQ.get(e[f])
        if not r or r['flags'] == 1: continue
        for i in range(3):
            if r[f'prerequisiteTypeId{i}'] != 143: continue
            eq = r[f'value{i}'] == VETERAN
            if (r[f'prerequisiteComparisonId{i}'] == 1 and not eq) or (r[f'prerequisiteComparisonId{i}'] == 2 and eq): return False
    return True


def fl(u): return struct.unpack('<f', struct.pack('<I', u & 0xffffffff))[0]
def lv(level, k): return L[max(1, min(50, level))][f'unitPropertyValue{k:02d}']
def mult(t, i, k):
    r = tbl.index(t).get(i); return r[f'unitPropertyMultiplier{k:03d}'] if r else 1.0
def cstat(c, level, k):
    """Creature stat: CreatureLevel row x archetype x difficulty x tier (UnitStats.Creature, certain)."""
    return lv(level, k) * mult('Creature2ArcheType', c['creature2ArcheTypeId'], k) * mult('Creature2Difficulty', c['creature2DifficultyId'], k) * mult('Creature2Tier', c['creature2TierId'], k)


def curve(fid, rating, level, base=0.0):
    """Client rating curve (DamageMath.Curve)."""
    g = GF[fid]
    i0, i1 = [v for k, v in g.items() if k.lower().startswith('dataint')][:2]
    f0, f1, f2, f3 = [v for k, v in g.items() if k.lower().startswith('datafloat')][:4]
    s = f0 / level * f1; knee = (f2 - i0) / s
    v = s * rating + i0 if rating < knee else f2 + s * (rating - knee) * math.cos(math.pi / 2 * (rating - knee) / ((rating - knee) + f3))
    return min(v * 0.01 + base, i1 * 0.01)


def mitigation(armor, level): return max(0.0, min(1.0, curve(1234, max(0.0, armor), level)))


def walk(sid, depth=0, seen=None):
    """Effects of a spell and the spells it reaches through proxies and cast events."""
    seen = seen if seen is not None else set()
    if sid in seen or depth > 4: return []
    seen.add(sid); out = []
    s = spell4.get(sid)
    for e in eff_by_spell.get(sid, []):
        if not difficulty_ok(e): continue
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


def amount(e, level, stat, character):
    """Client spell amount (SpellFormula.Amount): round(stat part) [creature soft cap GF 712] + (DataBits01 + flat) x DataBits00.
    stat(k) gives the caster's value of a parameter type. Rows without parameters: DataBits01 x DataBits00 when DataBits01 is set
    (client formula), else DataBits00 x assault power (server guess, step 3d)."""
    terms = [(e[f'parameterType0{k}'], e[f'parameterValue0{k}']) for k in range(4) if e[f'parameterType0{k}']]
    if not terms and e['effectType'] in (8, 10, 12, 33, 118, 138) and e['dataBits01']:
        m = fl(e['dataBits00']); return e['dataBits01'] * (m if math.isfinite(m) else 0.0)
    if not terms:
        coef = fl(e['dataBits00']); coef = coef if 0 < coef < 1e6 else 1.0
        return coef * stat(12)
    st = coefs = 0.0; n = 0; flat = 0.0
    for pt, pv in terms:
        v = stat(pt)
        if v is None: continue
        if pt in (1, 2, 3, 4, 5, 11, 12, 13): st += pv * v; coefs += pv; n += 1
        else: flat += pv * v
    st = math.floor(st + 0.5) if st >= 0 else math.ceil(st - 0.5)
    g = GF[712]; f0, f1, f2, f3 = g['datafloat0'], g['datafloat01'], g['datafloat02'], g['datafloat03']
    if not character and n:
        cap = (level * f0 + f1) * abs(coefs / n)
        if st > cap > 0: st = cap * (1 + f2 * (1 - math.exp(-(st - cap) / cap)))
    m = fl(e['dataBits00']); m = m if math.isfinite(m) else 0.0
    if e['effectType'] in (8, 10, 12, 33, 118, 138): flat = (e['dataBits01'] + flat) * m
    return (st + flat) / (f3 if not character and f3 else 1.0)


def cast_damage(sid, level, stat, character, all_rows=False):
    """Damage of one cast. Default: the largest single Damage row reached (the tooltip's main number: alternating hands, surged and
    execute variants and secondary AoE rows are alternatives, not extra hits), periodic rows for all their ticks. all_rows: every Damage
    row reached (every telegraph landing, worst case)."""
    rows = []
    for _, e in walk(sid):
        if e['effectType'] not in DAMAGE: continue
        a = amount(e, level, stat, character)
        ticks = min(e['durationTime'] // e['tickTime'], 60) if e['tickTime'] and e['durationTime'] > e['tickTime'] else 1
        rows.append(max(0.0, a) * ticks)
    return sum(rows) if all_rows else max(rows, default=0.0)


def creature_stat_fn(c, level):
    def stat(pt):
        if pt in (1, 2, 3, 4, 5): return cstat(c, level, pt - 1)
        if pt == 10: return level
        if pt == 11: return cstat(c, level, 40)
        if pt == 12: return cstat(c, level, AP) * 0.25
        if pt == 13: return cstat(c, level, SP) * 0.25
        return None
    return stat


def slug(s): return re.sub(r'[^a-z0-9]+', '-', (s or '').lower().replace("'", '')).strip('-')


def creature_name(c): return re.sub(r'\{p:[^}]*\}', '', tbl.text(c['localizedTextIdName']) or '').strip()


def researched_abilities():
    """creature-abilities.tsv (Jabbithole pages): page slug -> [(spell id, level text)]."""
    out = collections.defaultdict(list)
    for l in open(os.path.join(ROOT, 'data/research/creature-abilities.tsv')):
        f = l.rstrip('\n').split('\t')
        if len(f) >= 4 and f[3].isdigit():
            out[slug(f[0])].append(int(f[3]))
    return out
