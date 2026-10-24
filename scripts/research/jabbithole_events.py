#!/usr/bin/env python3
"""Parse cached Jabbithole public event pages into data/research/event-npcs.tsv (event name, NPC name, level, rank, zone)
and data/research/event-info.tsv (event name, zone, type, objective texts)."""
import glob, gzip, html, os, re

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
OUT = os.path.join(ROOT, 'data', 'research')

def text(s):
    return html.unescape(re.sub(r'<[^>]+>', ' ', s)).replace('\t', ' ').strip()

npcs, info, slugs = [], [], []
for path in sorted(glob.glob(f'{ROOT}/research/jh-event-raw/*.html.gz')):
    h = gzip.open(path).read().decode('utf8', 'replace')
    m = re.search(r'<h1>(.*?)<span>', h, re.S)
    if not m:
        continue
    name = text(m.group(1))
    side = re.search(r'<aside class="side-info">(.*?)</aside>', h, re.S)
    side_t = re.sub(r'\s+', ' ', text(side.group(1))) if side else ''
    zone = (re.search(r'Found in:\s*(.*?)\s*(Event type|$)', side_t) or [None, ''])[1]
    etype = (re.search(r'Event type:\s*([A-Za-z ]+?)\s*(Reward|Side|$)', side_t) or [None, ''])[1]
    objectives = [text(x) for x in re.findall(r'<li>(.*?)</li>', (re.search(r'Objectives:(.*?)</ul>', h, re.S) or [None, ''])[1], re.S)]
    info.append('\t'.join([name, zone, etype, ' | '.join(objectives)]))
    tab = re.search(r'id="tab-cr"(.*?)</table>', h, re.S)
    for row in re.findall(r'<tr>(.*?)</tr>', tab.group(1) if tab else '', re.S):
        link = re.search(r'<a href="/npcs/[^"]*">(.*?)</a>', row, re.S)
        cells = re.findall(r'<td[^>]*>(.*?)</td>', row, re.S)
        if link and len(cells) >= 3:
            c1 = re.sub(r'\s+', ' ', text(cells[1]))
            lv = re.match(r'(\d+)\s*(.*)', c1)
            slugs.append(re.search(r'/npcs/([^"]+)"', row).group(1))
            npcs.append('\t'.join([name, re.sub(r'\s*<[^>]*>\s*$', '', text(link.group(1))), lv.group(1) if lv else '', lv.group(2) if lv else c1, text(cells[2]) if len(cells) > 2 else '']))

for fn, rows in (('event-npcs.tsv', npcs), ('event-info.tsv', info)):
    with open(f'{OUT}/{fn}', 'w', encoding='utf-8') as f:
        f.write('\n'.join(rows) + '\n')
prio = os.path.join(ROOT, 'research', 'jh-priority.txt')
have = set(open(prio).read().split()) if os.path.exists(prio) else set()
with open(prio, 'a') as f:
    f.write('\n'.join(s for s in dict.fromkeys(slugs) if s not in have) + '\n')
print(len(info), 'events,', len(npcs), 'npc rows')
