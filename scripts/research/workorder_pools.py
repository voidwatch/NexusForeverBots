#!/usr/bin/env python3
"""Random objective pools for quest objective type 31 (work orders and random dailies).

The client replaces a quest's type 31 objective with the QuestObjective row named by the RandomResultId field of the quest
messages (ServerQuestInit active entry, ServerQuestStateChange). Which rows belong to a pool (the type 31 objective's data) is
not in the tables. The pool members are the 2593 CraftSchematic/ObtainSchematic objectives no quest references: they come in
blocks of one row per item slot with the same delivery location, skill, tier and item line, and the blocks are laid out in the
same order as the pool ids. A quest is matched to a block by its text (delivery location, profession, title words); where that
leaves several blocks, by position between neighbouring matched pools.

Writes data/research/workorder-pools.tsv (pool, quests, objective ids, how) and prints a summary. Run from the repo root."""
import sys, re, collections, bisect
sys.path.insert(0, 'scripts/research')
from tbl import load, text

obj = load('QuestObjective'); quests = load('Quest2')
sch = {r['ID']: r for r in load('TradeskillSchematic2')}
od = {r['ID']: r for r in obj}
okeys = [k for k in quests[0] if k.startswith('objective0')]
used = {r[k] for r in quests for k in okeys if r[k]}

def loc(r):
    m = re.search(r'Location:(\d+)', text(r['localizedTextIdFull']) or ''); return m.group(1) if m else '-'
def line(r):
    s = sch.get(r['data'])
    if not s: return '?'
    n = text(s['localizedTextIdName']) or ''
    m = re.match(r"\S+'s (\S+)", n)
    return m.group(1) if m else (n.split()[0] if n else '?')
def key(r):
    s = sch.get(r['data'])
    return (r['type'], loc(r), s['tradeSkillId'] if s else -1, s['tier'] if s else -1, line(r))

# blocks: runs of consecutive unreferenced 33/38 rows with the same key (a used row in between also ends a block)
blocks, prev, last = [], None, None
for r in sorted(obj, key=lambda r: r['ID']):
    if r['ID'] in used or r['type'] not in (33, 38):
        prev = None; continue
    k = key(r)
    if k != prev: blocks.append((k, [])); prev = k
    blocks[-1][1].append(r)
starts = [b[1][0]['ID'] for b in blocks]

prof = {'weaponsmith': 1, 'armorer': 12, 'outfitter': 14, 'tailor': 21}
words = ['training', 'prototype', 'capstone', 'complex']
tiers = {'novice': 1, 'apprentice': 2, 'journeyman': 3, 'artisan': 4, 'expert': 5, 'master': 6}
# armour weight named in the text: heavy = Armorer, medium = Outfitter, light = Tailor (Item2 armour categories of their schematic outputs)
weight = {'heavy armor': 12, 'medium armor': 14, 'light armor': 21}
pools = collections.defaultdict(list)  # pool id -> quests
qinfo = {}
for r in quests:
    obs = [od[r[k]] for k in okeys if r[k] in od]
    t31 = [o for o in obs if o['type'] == 31]
    if not t31: continue
    t = (text(r['localizedTextIdText']) or '') + ' ' + (text(r['localizedTextIdCompletionOverride']) or '')
    title = text(r['localizedTextIdTitle']) or ''
    pools[t31[0]['data']].append(r['ID'])
    qinfo.setdefault(t31[0]['data'], (set(re.findall(r'Location:(\d+)', t)), [v for w, v in list(prof.items()) + list(weight.items()) if w in t.lower()],
                                      [w for w in words if w in title.lower()], title, [v for w, v in tiers.items() if w in title.lower()]))

def candidates(pool):
    locs, profs, tw, _, tt = qinfo[pool]
    out = []
    for i, (k, rows) in enumerate(blocks):
        if k[1] not in locs or k[2] == -1 or (profs and k[2] not in profs): continue
        if tw and not all(w in k[4].lower() for w in tw): continue
        if tt and k[3] not in tt: continue
        out.append(i)
    return out

result = {}
for p in pools:
    c = candidates(p)
    if len(c) == 1: result[p] = (c[0], 'text')
known = sorted((p, i) for p, (i, _) in result.items())
violations = sum(1 for a, b in zip(known, known[1:]) if a[1] > b[1])
# second pass: several text candidates; keep those between the neighbouring matched pools and not taken by another pool
# (every matched block belongs to exactly one pool); repeat while it resolves more
changed = True
while changed:
    changed = False
    known = sorted((p, i) for p, (i, _) in result.items())
    ks = [x[0] for x in known]; taken = {i for _, i in known}
    for p in pools:
        if p in result: continue
        c = candidates(p)
        if len(c) < 2: continue
        j = bisect.bisect_left(ks, p)
        lo = known[j - 1][1] if j > 0 else -1
        hi = known[j][1] if j < len(known) else len(blocks)
        c = [i for i in c if lo < i < hi and i not in taken]
        if len(c) == 1:
            result[p] = (c[0], 'order'); changed = True

with open('data/research/workorder-pools.tsv', 'w', encoding='utf-8') as o:
    o.write('# pool (type 31 objective data)\tquests\tobjectives (QuestObjective ids the server picks from)\thow (text = unique text match, order = text + position between matched pools)\n')
    for p in sorted(result):
        i, how = result[p]
        o.write(f"{p}\t{','.join(map(str, pools[p]))}\t{','.join(str(r['ID']) for r in blocks[i][1])}\t{how}\n")

unres = [p for p in pools if p not in result]
nq = sum(len(pools[p]) for p in result)
print(f'blocks {len(blocks)} (live schematics {sum(1 for k, _ in blocks if k[2] != -1)}), pools {len(pools)} with {sum(len(v) for v in pools.values())} quests')
print(f'resolved pools {len(result)} ({sum(1 for v in result.values() if v[1] == "text")} text, {sum(1 for v in result.values() if v[1] == "order")} order) = {nq} quests; order violations among text matches {violations}')
print(f'distinct blocks {len({i for i, _ in result.values()})}'); print(f'block sizes of resolved pools {collections.Counter(len(blocks[i][1]) for i, _ in result.values()).most_common(5)}')
print(f'unresolved {len(unres)} pools / {sum(len(pools[p]) for p in unres)} quests:')
for p in sorted(unres)[:20]:
    print(f'  pool {p} quests {pools[p][:3]} "{qinfo[p][3]}" candidates {len(candidates(p))}')
