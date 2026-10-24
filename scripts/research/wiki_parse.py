#!/usr/bin/env python3
"""Parse research/wiki-all.jsonl (wiki_full.py) for coordinates and match named NPCs to needed creatures.
Output: research/wiki-coords.tsv (page, name, x, z), research/wiki-spawns.tsv (creature, world, x, z, wiki), research/wiki-yield.md"""
import json, re, collections
R = 'research'
need = {}   # name -> [creature ids]
for l in open(f'{R}/needed-creatures.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    if len(f) > 1 and f[1]: need.setdefault(f[1].lower(), []).append(f[0])
allc = collections.defaultdict(list)
for l in open(f'{R}/creatures.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    if len(f) > 1 and f[1]: allc[f[1].lower()].append(f[0])
cx = re.compile(r'\(\s*(-?\d{2,5}(?:\.\d+)?)\s*[, ]\s*(-?\d{2,5}(?:\.\d+)?)')
pages = coordpages = 0
rows = []
for l in open(f'{R}/wiki-all.jsonl', encoding='utf-8'):
    p = json.loads(l); pages += 1; hit = False
    for line in p['wikitext'].split('\n'):
        m = cx.search(line)
        if not m: continue
        hit = True
        names = re.findall(r'\{\{Tooltip\|([^}|]+)', line)
        lead = re.sub(r'^[*:# ]+', '', line[:m.start()]).split(':')[0].strip()
        lead = re.sub(r"\[\[|\]\]|'''|''", '', lead)
        if not names and lead: names = [lead]
        for n in names or ['']:
            rows.append((p['title'], n.strip(), m.group(1), m.group(2)))
    coordpages += hit
with open(f'{R}/wiki-coords.tsv', 'w', encoding='utf-8') as o:
    for r in rows: o.write('\t'.join(r) + '\n')
matched = [(r, need[r[1].lower()]) for r in rows if r[1].lower() in need]
anyname = [r for r in rows if r[1].lower() in allc]
with open(f'{R}/wiki-yield.md', 'w') as o:
    o.write(f'# Archive wiki yield\n\n- pages fetched: {pages} (namespaces 0, 4, 10, 14, 500, redirects excluded)\n- pages with coordinate pairs: {coordpages} ({len(rows)} coordinate rows)\n'
            f'- rows whose name equals a needed creature (quest giver/receiver/target without spawn): {len(matched)}\n- rows whose name equals any Creature2 name: {len(anyname)}\n')
print(pages, coordpages, len(rows), len(matched), len(anyname))
for r, ids in matched[:15]: print(r, ids[:3])
for r in anyname[:10]: print('any', r)
