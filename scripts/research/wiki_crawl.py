#!/usr/bin/env python3
"""MediaWiki API crawler (fandom). Polite: 1.5 s between requests, descriptive User-Agent, raw wikitext cached in research/wiki-raw/.
Usage: wiki_crawl.py zones   -> Farside/Arcterra pages and everything they link to (depth 2, capped)
       wiki_crawl.py mech    -> mechanics pages on wildstar.fandom.com found through list=search
"""
import json, os, re, sys, time, urllib.parse, urllib.request

UA = 'NexusForeverBots-research/1.0 (WildStar emulator QA tooling; github voidwatch/NexusForeverBots; 1 request per 1.5 s)'
RAW = 'research/wiki-raw'
last = 0.0

def api(host, **params):
    global last
    params['format'] = 'json'
    url = f'https://{host}/api.php?' + urllib.parse.urlencode(params)
    for attempt in range(5):
        w = last + 1.5 - time.time()
        if w > 0: time.sleep(w)
        last = time.time()
        try:
            req = urllib.request.Request(url, headers={'User-Agent': UA})
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.load(r)
        except Exception as e:
            print('retry', attempt, str(e)[:80], file=sys.stderr)
            time.sleep(5 * (attempt + 1))
    return None

def safe(t): return re.sub(r'[^A-Za-z0-9_.-]', '_', t)[:150]

def fetch_page(host, sub, title):
    path = f'{RAW}/{sub}/{safe(title)}.json'
    if os.path.exists(path):
        return json.load(open(path))
    d = api(host, action='parse', page=title, prop='wikitext|links|categories', redirects=1)
    if not d or 'parse' not in d:
        return None
    p = d['parse']
    out = {'title': p['title'], 'wikitext': p['wikitext']['*'],
           'links': [l['*'] for l in p.get('links', []) if l.get('ns') == 0 and 'exists' in l],
           'categories': [c['*'] for c in p.get('categories', [])]}
    os.makedirs(os.path.dirname(path), exist_ok=True)
    json.dump(out, open(path, 'w'))
    return out

def zones():
    host, sub = 'wildstaronline-archive.fandom.com', 'archive'
    seen, queue, cap = set(), [(t, 0) for t in ('Farside', 'Arcterra')], 900
    # category members of the zone pages' categories are queued too
    while queue and len(seen) < cap:
        t, depth = queue.pop(0)
        if t in seen: continue
        seen.add(t)
        p = fetch_page(host, sub, t)
        if not p: continue
        if depth < 2:
            for l in p['links']:
                if l not in seen: queue.append((l, depth + 1))
        if depth == 0:
            for c in p['categories']:
                cm = api(host, action='query', list='categorymembers', cmtitle='Category:' + c, cmlimit=500, cmnamespace=0)
                for m in (cm or {}).get('query', {}).get('categorymembers', []):
                    queue.append((m['title'], 1))
        if len(seen) % 50 == 0: print('fetched', len(seen), 'queue', len(queue), flush=True)
    print('zones done', len(seen))

MECH = ['public event', 'event tracker', 'quest', 'party', 'group', 'raid', 'death', 'resurrection', 'respawn', 'companion',
        'path mission', 'challenge', 'rewards', 'dungeon', 'adventure', 'reputation', 'loot']

def mech():
    host, sub = 'wildstar.fandom.com', 'mech'
    titles = []
    for q in MECH:
        d = api(host, action='query', list='search', srsearch=q, srlimit=15, srnamespace=0)
        for s in (d or {}).get('query', {}).get('search', []):
            if s['title'] not in titles: titles.append(s['title'])
    print('found', len(titles), 'pages')
    for t in titles:
        fetch_page(host, sub, t)
    json.dump(titles, open('research/wiki-mech-titles.json', 'w'))
    print('mech done')

if __name__ == '__main__':
    {'zones': zones, 'mech': mech}[sys.argv[1]]()
