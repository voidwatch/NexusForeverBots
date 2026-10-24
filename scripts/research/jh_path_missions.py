#!/usr/bin/env python3
"""Path mission targets from the cached Jabbithole NPC pages (research/jh-raw, tab "tab-opm": the path missions an NPC is part of,
with the PathMission id in the link: /path-missions/biology-corrupted-coterie-2391).

Writes data/research/path-mission-targets.tsv: mission id, mission type (path, type), creature ids with that NPC name, NPC name, page.
Creature ids are every Creature2 row whose localized name is the page name; the server keeps only those spawned in the mission's world.
Prints a per-type summary (at most 30 lines).
"""
import os, re, gzip, html, collections, sys
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
RAW = os.path.join(ROOT, 'research', 'jh-raw')
OUT = os.path.join(ROOT, 'data', 'research', 'path-mission-targets.tsv')

missions = {m['ID']: m for m in load('PathMission')}
by_name = collections.defaultdict(list)
for c in load('Creature2'):
    n = (text(c['localizedTextIdName']) or '').strip().lower()
    if n:
        by_name[n].append(c['ID'])

rows = set()
pages = with_tab = 0
for f in sorted(os.listdir(RAW)):
    pages += 1
    h = gzip.open(os.path.join(RAW, f), 'rt', errors='replace').read()
    i = h.find('id="tab-opm"')
    if i < 0:
        continue
    with_tab += 1
    m = re.search(r'<h1>([^<]+)<', h)
    if not m:
        continue
    name = html.unescape(m.group(1)).strip()
    j = h.find('</table>', i)
    for mid in re.findall(r'href="/path-missions/[^"]*-(\d+)"', h[i:j]):
        mid = int(mid)
        if mid in missions:
            rows.add((mid, name, f[:-8]))

out = []
for mid, name, page in sorted(rows):
    m = missions[mid]
    ids = by_name.get(name.lower(), [])
    out.append((mid, f"{m['pathTypeEnum']}/{m['pathMissionTypeEnum']}", ','.join(map(str, ids)), name, page))
with open(OUT, 'w') as fo:
    fo.write('# mission\tpath/type\tcreatures\tname\tjabbithole page  (scripts/research/jh_path_missions.py)\n')
    for o in out:
        fo.write('\t'.join(map(str, o)) + '\n')

print(f'pages {pages}, with a path mission tab {with_tab}, rows {len(out)}, without a Creature2 name match {sum(1 for o in out if not o[2])}')
have = collections.Counter(missions[o[0]]['pathTypeEnum'] * 100 + missions[o[0]]['pathMissionTypeEnum'] for o in out if o[2])
covered = collections.defaultdict(set)
for o in out:
    if o[2]:
        covered[(missions[o[0]]['pathTypeEnum'], missions[o[0]]['pathMissionTypeEnum'])].add(o[0])
total = collections.Counter((m['pathTypeEnum'], m['pathMissionTypeEnum']) for m in missions.values())
for k in sorted(total):
    print(f'path {k[0]} type {k[1]:>2}: missions {total[k]:>4}, with jabbithole targets {len(covered[k]):>4}')
