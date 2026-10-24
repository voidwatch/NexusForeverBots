#!/usr/bin/env python3
"""Item durability from wildstar.fandom.com item pages (search 'durability', parse the item infobox), matched to Item2 by name.
Checks the client rule max durability = GameFormula 559 dataint01 (20) + Item2.powerLevel (client code).
Raw pages cached in research/wiki-raw/dur/. Prints a summary (max 30 lines)."""
import json, os, re, sys, time, urllib.parse, urllib.request
sys.path.insert(0, os.path.dirname(__file__)); import tbl
API = 'https://wildstar.fandom.com/api.php'; UA = 'NexusForeverBots-research/1.0 (personal emulator QA, 1 request per 1.5 s)'
RAW = 'research/wiki-raw/dur'
def get(params, cache):
    p = os.path.join(RAW, cache)
    if os.path.exists(p): return json.load(open(p))
    time.sleep(1.5)
    req = urllib.request.Request(API + '?' + urllib.parse.urlencode(params), headers={'User-Agent': UA})
    d = json.load(urllib.request.urlopen(req, timeout=60)); json.dump(d, open(p, 'w')); return d
titles = []; off = 0
while off is not None and off < 2000:
    d = get({'action': 'query', 'list': 'search', 'srsearch': 'durability', 'srlimit': 50, 'sroffset': off, 'format': 'json'}, f'search-{off}.json')
    titles += [x['title'] for x in d['query']['search']]
    off = d.get('continue', {}).get('sroffset')
items = {}
for r in tbl.load('Item2'):
    nm = tbl.text(r['localizedTextIdName'])
    if nm: items.setdefault(nm.lower(), []).append(r)
ok = bad = nomatch = nodur = 0; badrows = []
for i in range(0, len(titles), 40):
    chunk = titles[i:i + 40]
    d = get({'action': 'query', 'prop': 'revisions', 'rvprop': 'content', 'rvslots': 'main', 'titles': '|'.join(chunk), 'format': 'json'}, f'pages-{i}.json')
    for pg in d['query']['pages'].values():
        txt = pg.get('revisions', [{}])[0].get('slots', {}).get('main', {}).get('*', '')
        m = re.search(r'\|\s*dur(?:ability)?\s*=\s*(\d+)', txt)
        if not m: nodur += 1; continue
        dur = int(m.group(1)); rows = items.get(pg['title'].lower(), [])
        if not rows: nomatch += 1; continue
        if any(20 + r['powerLevel'] == dur for r in rows): ok += 1
        else: bad += 1; badrows.append((pg['title'], dur, [(r['ID'], r['powerLevel'], r['item2FamilyId']) for r in rows[:3]]))
print('search titles', len(titles), 'with durability', ok + bad + nomatch, 'no durability field', nodur)
print('matched by name', ok + bad, 'durability == 20 + powerLevel:', ok, 'mismatch:', bad, 'no Item2 name match:', nomatch)
for b in badrows[:15]: print('  mismatch', b)
