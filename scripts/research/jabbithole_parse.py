#!/usr/bin/env python3
"""Parse cached Jabbithole NPC pages into compact tables under data/research/ (committed, loaded by the world server).

creature-info.tsv       name, zone, level, rank, eliteness, health, spawn count, events
creature-drops.tsv      name, zone, level, item id, chance percent, kills sampled
creature-abilities.tsv  name, zone, level, spell4 id, tier, school
Names are the page names (client text), zones the Jabbithole zone names.
"""
import glob, gzip, html, os, re
from tbl import load

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
RAW = os.path.join(ROOT, 'research', 'jh-raw')
OUT = os.path.join(ROOT, 'data', 'research')
os.makedirs(OUT, exist_ok=True)

def text(s):
    return html.unescape(re.sub(r'<[^>]+>', ' ', s)).replace('\t', ' ').strip()

def section(h, tab):
    i = h.find(f'id="{tab}"')
    if i < 0:
        return ''
    j = h.find('</table>', i)
    return h[i:j]

info, drops, abil = [], [], []
# item ids the client tables do not have (removed items) are left out
ITEMS = {r['ID'] for r in load('Item2')}
unknown_items = 0
for path in sorted(glob.glob(f'{RAW}/*.html.gz')):
    try:
        h = gzip.open(path).read().decode('utf8', 'replace')
    except Exception:
        continue
    m = re.search(r'<h1>(.*?)<span>', h, re.S)
    if not m:
        continue
    name = text(m.group(1))
    zm = re.search(r'Found in:.*?<a [^>]*>(.*?)</a>', h, re.S)
    zone = text(zm.group(1)) if zm else ''
    if path.endswith('-veteran.html.gz'):
        zone += ' (veteran)'
    def field(label):
        r = re.search(label + r':\s*([^<]*)<', h)
        return r.group(1).strip().replace(',', '') if r else ''
    level, rank, elite, health = field('Level'), field('Rank'), field('Eliteness'), field('Health')
    spawns = len(re.findall(r'class="location_large"', h))
    ev = [text(x) for x in re.findall(r'href="/public-events/[^"]*">(.*?)</a>', section(h, 'tab-ope'))]
    info.append('\t'.join([name, zone, level, rank, elite, health, str(spawns), ';'.join(dict.fromkeys(ev))]))
    # newer pages use tab-odrop and /items/<name>-<id>, older ones (2014) tab-drop and /items/<name>-<id>-<hash>
    for row in re.findall(r'<tr>(.*?)</tr>', section(h, 'tab-odrop') or section(h, 'tab-drop'), re.S):
        im = re.search(r'href="/items/[^"]*?-(\d+)(?:-[0-9a-z]*[a-z][0-9a-z]*)?"', row)
        cm = re.search(r'([\d.]+)%<br>\s*<span class="small">\((\d+)/(\d+)\)', row)
        if im and cm and int(im.group(1)) not in ITEMS:
            unknown_items += 1
        elif im and cm:
            drops.append('\t'.join([name, zone, level, im.group(1), cm.group(1), cm.group(3)]))
    for row in re.findall(r'<tr>(.*?)</tr>', section(h, 'tab-abi'), re.S):
        sm = re.search(r'href="/spells/[^"]*-(\d+)"', row)
        cells = re.findall(r'<td[^>]*>(.*?)</td>', row, re.S)
        if sm and len(cells) >= 4:
            abil.append('\t'.join([name, zone, level, sm.group(1), text(cells[1]), text(cells[2])]))

for fn, rows in (('creature-info.tsv', info), ('creature-drops.tsv', drops), ('creature-abilities.tsv', abil)):
    with open(f'{OUT}/{fn}', 'w', encoding='utf-8') as f:
        f.write('\n'.join(rows) + '\n')
print(len(info), 'pages,', len(drops), 'drops,', len(abil), 'abilities,', unknown_items, 'drops of items missing from Item2 left out')
