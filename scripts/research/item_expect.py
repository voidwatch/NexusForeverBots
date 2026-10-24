#!/usr/bin/env python3
"""Item stat formula (the same as Game/Combat/ItemStats.cs) checked against the archived Jabbithole tooltips (research/decode/items/jh-items.tsv).
    item_expect.py            accuracy per stat over all scraped items (summary)
    item_expect.py check      lines '<key> <expected> <tolerance> <source>' for scripts/combat-check.sh (expected = the tooltip value)
"""
import os, sys, csv, math
from collections import defaultdict, Counter
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
I = {x['ID']: x for x in load('Item2')}; T = {x['ID']: x for x in load('Item2Type')}; S = {x['ID']: x for x in load('ItemSlot')}
C = {x['ID']: x for x in load('Item2Category')}; ST = {x['ID']: x for x in load('ItemStat')}; B = {x['ID']: x for x in load('ItemBudget')}
UP = {x['ID']: x for x in load('UnitProperty2')}
RG = defaultdict(list)
for e in sorted(load('ItemRandomStat'), key=lambda e: e['ID']):
    if e['itemStatTypeEnum'] == 1: RG[e['itemRandomStatGroupId']].append(e['itemStatData'])
NAMES = {35: 'Assault Rating', 36: 'Support Rating', 7: 'Max Health', 42: 'Armor', 41: 'Max Shield', 34: 'Critical Hit Rating', 29: 'Multi-Hit Rating',
         47: 'Critical Hit Severity Rating', 32: 'Strikethrough Rating', 57: 'Vigor Rating', 33: 'Deflect Chance Rating', 58: 'Glance Rating',
         56: 'Intensity Rating', 37: 'Critical Mitigation Rating', 5: 'Focus Pool', 6: 'Focus Recovery Rating'}
fl = lambda v: math.floor(v + 0.01)


def stats(iid):
    it = I[iid]; il = it['powerLevel']; slot = S[T[it['item2TypeId']]['itemSlotId']]; sid = slot['ID']; sm = slot['armorModifier']
    armor = 1 <= sid <= 6; powerslot = armor or sid in (57, 58, 60); weapon = sid == 20; shield = sid == 43
    v = defaultdict(float)
    if il <= 0: return v
    st = ST.get(it['itemStatId']) if it['itemStatId'] else None; b = B.get(it['itemBudgetId'])
    p = 111.375 * il if weapon else ((3.6 if st else 4.5) * il * sm if powerslot else 0)
    if p:
        s = min(1, max(0, (it['supportPowerPercentage'] + 1) / 2)); v[35] = fl(p * (1 - s)); v[36] = fl(p * s)
    if armor:
        v[7] = fl(60 * il * sm); c = C[it['item2CategoryId']]; ail = il if il <= 50 else 50 + 0.34 * (il - 50); v[42] = fl(c['armorBase'] + 20 * ail * sm * c['armorModifier'])
    if not st: return v
    used = set()
    for k in range(5):
        t = st['itemStatTypeEnum%02d' % k]; d = st['itemStatData%02d' % k]; f = b['budget%02d' % k] if b else 0
        if t == 1: prop = d
        elif t == 2:
            prop = next((x for x in RG.get(d, []) if x not in used), None)
            if prop is None: continue
        else: continue
        used.add(prop)
        if shield and prop == 41: v[41] = fl(75 * (1.5 + f) * il); continue
        if shield and prop in (175, 176, 178): continue
        if f <= 0 or UP[prop]['valuePerPoint'] <= 0: continue
        per = 3.75 if weapon else 5 * sm
        v[prop] += fl(per * il * f * UP[prop]['valuePerPoint'] / 10)
    return v


def observed():
    obs = defaultdict(dict)
    for r in csv.DictReader(open(os.path.join(ROOT, 'research/decode/items/jh-items.tsv')), delimiter='\t'):
        if not r['value'].endswith('%'): obs[int(r['item'])][r['stat']] = float(r['value'])
    return obs


# items with fixed stats (no random group) used by combat-check: weapon, chest, gloves, shield, implant
CHECK = [74338, 19048]
if __name__ == '__main__':
    obs = observed()
    if len(sys.argv) > 1 and sys.argv[1] == 'check':
        picked = list(CHECK)
        for iid, o in sorted(obs.items()):
            it = I.get(iid)
            if not it: continue
            sid = T[it['item2TypeId']]['itemSlotId']
            if sid in (20, 1, 6, 43, 60) and it['itemStatId'] and ST[it['itemStatId']]['itemStatTypeEnum00'] == 1 and len([p for p in picked if T[I[p]['item2TypeId']]['itemSlotId'] == sid]) < 1 and it['powerLevel'] >= 40:
                picked.append(iid)
        # archive wiki infobox (research/wiki-all.jsonl), Consort's Vesture, light chest, item level 9, quality Good
        for prop, value, name in ((35, 40, 'Assault Rating'), (7, 675, 'Max Health'), (42, 168, 'Armor'), (47, 56, 'Critical Hit Severity Rating')):
            print(f"item.19048.{prop} {value} {0.002 if prop != 47 else 0.12} archive wiki Consort's Vesture {name}; formula {stats(19048).get(prop, 0):.0f}")
        picked.remove(19048)
        for iid in picked:
            v = stats(iid)
            for prop, name in NAMES.items():
                if name in obs.get(iid, {}):
                    tol = 0.002 if prop in (7, 42) else 0.012 if prop in (35, 36) else 0.12
                    print(f"item.{iid}.{prop} {obs[iid][name]:.0f} {tol} Jabbithole tooltip (Wayback 2016-19) {name}; formula {v.get(prop, 0):.0f}")
        sys.exit()
    acc = defaultdict(Counter)
    for iid, o in obs.items():
        if iid not in I: continue
        v = stats(iid)
        for prop, name in NAMES.items():
            if name not in o: continue
            got, want = v.get(prop, 0), o[name]
            err = abs(got - want) / max(1, want)
            acc[name]['exact' if err <= 0.002 else 'within10%' if err <= 0.1 else 'off'] += 1
    for name, c in sorted(acc.items(), key=lambda x: -sum(x[1].values())):
        print(f'{name:30s} {dict(c)}')
