#!/usr/bin/env python3
"""Fit item stat formulas against research/decode/items/jh-items.tsv (from jh_items.py). Prints a short summary."""
import os, sys, csv
from collections import defaultdict, Counter
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
slots = {x['ID']: x for x in load('ItemSlot')}
cats = {x['ID']: x for x in load('Item2Category')}
items = defaultdict(dict); meta = {}
for r in csv.DictReader(open(os.path.join(ROOT, 'research/decode/items/jh-items.tsv')), delimiter='\t'):
    v = r['value']
    if v.endswith('%'):
        continue
    items[r['item']][r['stat']] = float(v)
    meta[r['item']] = r
def stats(name, vals):
    vals = sorted(vals)
    if not vals: return f'{name}: none'
    c = Counter(round(v, 2) for v in vals).most_common(3)
    return f'{name}: n={len(vals)} min {vals[0]:.3f} med {vals[len(vals)//2]:.3f} max {vals[-1]:.3f} top {c}'
ilvl_mismatch = sum(1 for i, m in meta.items() if m['jh_ilvl'] != m['powerLevel'])
print('items', len(items), 'jh ilvl != powerLevel:', ilvl_mismatch)
mode = sys.argv[1] if len(sys.argv) > 1 else 'all'
pw = defaultdict(list); hp = defaultdict(list); ar = defaultdict(list); sh = []; sec = defaultdict(list); split = Counter()
for i, s in items.items():
    m = meta[i]; il = float(m['powerLevel']); slot = int(m['slot']); q = int(m['quality'])
    if il <= 0: continue
    sm = slots[slot]['armorModifier']; cm = cats[int(m['category'])]['armorModifier']
    tot = s.get('Assault Rating', 0) + s.get('Support Rating', 0)
    if tot:
        pw[(slot, q)].append(tot / il / (sm if slot != 20 else 1))
        sp = float(m['supportPct'])
        split[(sp, round(s.get('Support Rating', 0) / tot, 2))] += 1
    if 'Max Health' in s: hp[slot].append(s['Max Health'] / il / sm)
    if 'Armor' in s and sm and cm: ar[(slot, int(m['category']))].append(s['Armor'] / il / sm / cm)
    if 'Max Shield' in s: sh.append((s['Max Shield'] / il, m['budget'], q))
    for k in ('Critical Hit Rating', 'Multi-Hit Rating', 'Critical Hit Severity Rating', 'Strikethrough Rating', 'Vigor Rating', 'PvP Power Rating', 'PvP Defense', 'Deflect Chance Rating', 'Glance Rating', 'Intensity Rating', 'Critical Mitigation Rating', 'Focus Pool'):
        if k in s: sec[q].append((k, s[k] / il / (sm if slot != 20 else 1), slot, m['budget'], m['statId']))
if mode in ('all', 'power'):
    for k in sorted(pw): print(stats(f'power/(ilvl*slotmod) slot {k[0]} q {k[1]}', pw[k]))
    print('split (supportPct, observed SR share):', split.most_common(14))
if mode in ('all', 'hp'):
    for k in sorted(hp): print(stats(f'health/(ilvl*slotmod) slot {k}', hp[k]))
    for k in sorted(ar): print(stats(f'armor/(ilvl*slot*cat) slot,cat {k}', ar[k]))
    print(stats('shield/ilvl', [x[0] for x in sh]), Counter((x[1], round(x[0])) for x in sh).most_common(6))
if mode == 'sec':
    for q in sorted(sec):
        print('q', q, Counter((k, round(v, 2), slot) for k, v, slot, b, st in sec[q]).most_common(12))
