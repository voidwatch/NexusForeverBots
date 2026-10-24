#!/usr/bin/env python3
"""Parse cached Jabbithole zone pages.

data/research/zone-info.tsv   zone slug, name, kind (dungeon, raid, ...), bosses (; separated), npc count, veteran npc count
data/research/zone-npcs.tsv   zone slug, npc slug (with id), npc name, level, rank
research/jh-priority.txt      NPC page slugs of dungeons/raids/adventures/expeditions, fetched right after the quest creatures
"""
import glob, gzip, html, os, re

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
OUT = os.path.join(ROOT, 'data', 'research')

def text(s):
    return re.sub(r'\s+', ' ', html.unescape(re.sub(r'<[^>]+>', ' ', s))).strip()

# the archive only marks five zones as dungeons, the rest are picked by name (dungeons, raids, expeditions, adventures)
INSTANCE_WORDS = ('kel-voreth', 'skullcano', 'stormtalon', 'swordmaiden', 'genetic-archives', 'bloodsworn', 'infinite-mind', 'infinite-generator',
                  'datascape', 'initialization-core', 'rage-factory', 'rage-logic', 'shiphand', 'outpost-m-13', 'gauntlet', 'hycrest-insurrection',
                  'malgrave-trail', 'space-madness', 'void-hunter', 'salvage-rights', 'deep-space', 'fragment-zero', 'infestation', 'ether',
                  'ultimate-protogames', 'protogames', 'whitevale-adventure', 'astrovoid', 'riot-in-the-void', 'hungry-ghost', 'bay-of-betrayal', 'skywatch',
                  'northern-wilds-adventure', 'galeras', 'crimson-badlands')
info, npcs, priority = [], [], []
for path in sorted(glob.glob(f'{ROOT}/research/jh-zone-raw/*.html.gz')):
    h = gzip.open(path).read().decode('utf8', 'replace')
    if not h:
        continue
    slug = os.path.basename(path)[:-8]
    t = re.search(r'<h1>(.*?)<', h, re.S)
    name = text(t.group(1)) if t else slug
    side = re.search(r'<aside class="side-info">(.*?)</aside>', h, re.S)
    side_h = side.group(1) if side else ''
    kind = (re.search(r'This zone is an? ([a-z ]+?)(?:<|\s*Boss|\s*$)', text(side_h)) or [None, ''])[1].strip()
    bosses = []
    bm = re.search(r'Boss encounters:(.*?)(?:Last seen|Added in|$)', side_h, re.S)
    if bm:
        bosses = [text(x) for x in re.findall(r'<a [^>]*>(.*?)</a>', bm.group(1), re.S)]
    nn = re.search(r'NPCs \((\d+)\)', h)
    nv = re.search(r'Veteran NPCs \((\d+)\)', h)
    info.append('\t'.join([slug, name, kind, ';'.join(dict.fromkeys(bosses)), nn.group(1) if nn else '0', nv.group(1) if nv else '0']))
    for tab in ('tab-npc', 'tab-npcv'):
        i = h.find(f'id="{tab}"')
        if i < 0:
            continue
        j = h.find('</table>', i)
        for row in re.findall(r'<tr>(.*?)</tr>', h[i:j], re.S):
            link = re.search(r'<a href="/npcs/([^"]+)">(.*?)</a>', row, re.S)
            cells = re.findall(r'<td[^>]*>(.*?)</td>', row, re.S)
            if link and len(cells) >= 2:
                c1 = text(cells[1])
                lv = re.match(r'(\d+)\s*(.*)', c1)
                npcs.append('\t'.join([slug, link.group(1), text(link.group(2)), lv.group(1) if lv else '', lv.group(2) if lv else c1]))
                if kind in ('dungeon', 'raid', 'adventure', 'expedition') or any(w in slug for w in INSTANCE_WORDS):
                    priority.append(link.group(1))

for fn, rows in (('zone-info.tsv', info), ('zone-npcs.tsv', npcs)):
    with open(f'{OUT}/{fn}', 'w', encoding='utf-8') as f:
        f.write('\n'.join(rows) + '\n')
with open(f'{ROOT}/research/jh-priority.txt', 'w') as f:
    f.write('\n'.join(dict.fromkeys(priority)) + '\n')
import collections
print(len(info), 'zones', collections.Counter(r.split('\t')[2] for r in info), len(npcs), 'npc rows', len(set(priority)), 'priority pages')
