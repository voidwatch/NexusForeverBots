#!/usr/bin/env python3
"""Fetch archived Jabbithole NPC pages (web.archive.org copies) whose name matches a quest creature without spawn.

Reads research/needed-creatures.tsv (from `gt exportneeded`) and research/jh-npc-cdx.txt (Wayback CDX listing of
jabbithole.com/npcs/*). One request per second, raw pages cached gzip in research/jh-raw/ (gitignored). Resumable.
"""
import gzip, os, re, sys, time

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'research')
RAW = os.path.join(ROOT, 'jh-raw')
os.makedirs(RAW, exist_ok=True)

def slug(name):
    return re.sub(r'[^a-z0-9]+', '-', name.lower().replace("'", '')).strip('-')

need = set()
for line in open(os.path.join(ROOT, 'needed-creatures.tsv'), encoding='utf-8'):
    f = line.rstrip('\n').split('\t')
    if len(f) > 1 and f[1]:
        need.add(slug(f[1]))

pages = {}
for line in open(os.path.join(ROOT, 'jh-npc-cdx.txt')):
    url, ts = line.split()
    m = re.search(r'/npcs/(.+?)-(\d+(?:-veteran)?)$', url)
    if m and m.group(1) in need:
        pages.setdefault((m.group(1), m.group(2)), (url, ts))

ALL = '--all' in sys.argv
if ALL:
    # everything else afterwards, so quest creatures come first
    for line in open(os.path.join(ROOT, 'jh-npc-cdx.txt')):
        url, ts = line.split()
        m = re.search(r'/npcs/(.+?)-(\d+(?:-veteran)?)$', url)
        if m:
            pages.setdefault((m.group(1), m.group(2)), (url, ts))
    priority = set()
    if os.path.exists(os.path.join(ROOT, 'jh-priority.txt')):
        priority = {l.strip() for l in open(os.path.join(ROOT, 'jh-priority.txt')) if l.strip()}
    # quest creatures first, then the NPCs of dungeons/raids/adventures, then everything else
    order = sorted(pages.items(), key=lambda kv: (0 if kv[0][0] in need else 1 if f'{kv[0][0]}-{kv[0][1]}' in priority else 2, kv[0]))
else:
    order = sorted(pages.items())

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wayback import Wayback

wb = Wayback(1.0)
print(len(order), 'pages to fetch', flush=True)
count = 0
for (s_, n), (url, ts) in order:
    path = os.path.join(RAW, f'{s_}-{n}.html.gz')
    if os.path.exists(path):
        continue
    data = wb.get(ts, url)
    with gzip.open(path + '.tmp', 'wb') as f:
        f.write(data or b'')
    os.rename(path + '.tmp', path)
    count += 1
    if count % 100 == 0:
        print(count, flush=True)
print('finished', flush=True)
