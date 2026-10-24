#!/usr/bin/env python3
"""Salvage outputs from archived Jabbithole item pages (research/items-raw/*.html, tab "Salvaged to").

    jh_salvage.py      write data/research/salvage-outputs.tsv and print a summary (max 30 lines)

Each page lists the items players got when they salvaged that item, with observed counts (n of total salvages).
Rows are grouped by the client's salvage key: Item2Type and the Salvage.tbl row with the highest Level <= the item's
power level (the same lookup the client uses for Item:CanSalvage). Output: one row per (type, level band, output item)
with the pooled observed chance; per-item rows are kept with key kind 'item'.
"""
import os, re, sys, glob, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
RAW = os.path.join(ROOT, 'research', 'items-raw')
OUT = os.path.join(ROOT, 'data', 'research', 'salvage-outputs.tsv')
ROW = re.compile(r'<tr>\s*<td class="name">.*?href="/items/[^"]*?-(\d+)".*?</td>.*?<td align="center">\s*([\d.]+)%<br>\s*<span class="small">\((\d+)/(\d+)\)</span>', re.S)

items = {r['ID']: r for r in load('Item2')}
bands = collections.defaultdict(list)
for r in load('Salvage'):
    bands[r['item2TypeId']].append(r['level'])
exceptions = {r['item2Id']: r['flags'] for r in load('SalvageException')}


def band(item):
    levels = [l for l in bands.get(item['item2TypeId'], []) if l <= item['powerLevel']]
    return max(levels) if levels else None


per_item = {}
for path in sorted(glob.glob(os.path.join(RAW, '*.html'))):
    iid = int(re.sub(r'\D', '', os.path.basename(path)) or 0)
    t = open(path, encoding='utf8', errors='ignore').read()
    i = t.find('id="tab-sal2"')
    if i < 0 or iid not in items:
        continue
    j = t.find('</table>', i)
    rows = [(int(o), int(n), int(tot)) for o, pct, n, tot in ROW.findall(t[i:j])]
    if rows:
        per_item[iid] = rows

pooled = collections.defaultdict(lambda: [collections.Counter(), 0])
no_band = 0
for iid, rows in per_item.items():
    it = items[iid]
    b = band(it)
    if b is None:
        no_band += 1
        continue
    key = (it['item2TypeId'], b)
    total = max(t for _, _, t in rows)
    pooled[key][1] += total
    for o, n, _ in rows:
        pooled[key][0][o] += n

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, 'w') as f:
    f.write('# kind\titem2TypeId_or_itemId\tlevel\toutputItemId\tobserved\tsalvages\tchance\toutputName\n')
    for (ty, lv), (cnt, tot) in sorted(pooled.items()):
        for o, n in cnt.most_common():
            f.write(f'type\t{ty}\t{lv}\t{o}\t{n}\t{tot}\t{n / tot:.4f}\t{text(items[o]["localizedTextIdName"]) if o in items else "?"}\n')
    for iid, rows in sorted(per_item.items()):
        for o, n, tot in rows:
            f.write(f'item\t{iid}\t{items[iid]["powerLevel"]}\t{o}\t{n}\t{tot}\t{n / tot:.4f}\t{text(items[o]["localizedTextIdName"]) if o in items else "?"}\n')

types = {r['ID']: r for r in load('Item2Type')}
sal_keys = sum(len(v) for v in bands.values())
print(f'pages with salvage data: {len(per_item)}; without a Salvage.tbl band: {no_band}')
print(f'Salvage.tbl keys (type, level): {sal_keys} over {len(bands)} types; keys with data: {len(pooled)}; types with data: {len({k[0] for k in pooled})}')
miss = sum(1 for o in {o for c, _ in pooled.values() for o in c} if o not in items)
print(f'distinct outputs: {len({o for c, _ in pooled.values() for o in c})} (not in Item2: {miss}); SalvageException rows: {len(exceptions)}')
top = collections.Counter()
for c, _ in pooled.values():
    for o, n in c.items():
        top[o] += n
for o, n in top.most_common(10):
    print(f'  {o} {text(items[o]["localizedTextIdName"]) if o in items else "?"}: {n}')
print('wrote', os.path.relpath(OUT, ROOT))
