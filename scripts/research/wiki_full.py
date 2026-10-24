#!/usr/bin/env python3
"""Full crawl of wildstaronline-archive.fandom.com through the MediaWiki API (allpages, namespaces 0/4/10/14/500, batches of 50 wikitext).
1.5 s between requests, cached in research/wiki-raw/full/<n>.json (resumable). Output: research/wiki-all.jsonl (title, ns, categories, wikitext)."""
import json, os, sys, time
sys.path.insert(0, os.path.dirname(__file__))
import wiki_crawl as w
H = 'wildstaronline-archive.fandom.com'
D = 'research/wiki-raw/full'
os.makedirs(D, exist_ok=True)
titles = []
tp = D + '/titles.json'
if os.path.exists(tp):
    titles = json.load(open(tp))
else:
    for ns in (0, 4, 10, 14, 500):
        cont = {}
        while True:
            d = w.api(H, action='query', list='allpages', aplimit=500, apnamespace=ns, apfilterredir='nonredirects', **cont)
            if not d: break
            titles += [(p['title'], ns) for p in d['query']['allpages']]
            if 'continue' not in d: break
            cont = {'apcontinue': d['continue']['apcontinue']}
        print('ns', ns, 'total', len(titles), flush=True)
    json.dump(titles, open(tp, 'w'))
out = open('research/wiki-all.jsonl', 'w')
n = 0
for i in range(0, len(titles), 50):
    batch = titles[i:i + 50]
    cp = f'{D}/b{i // 50:04d}.json'
    if os.path.exists(cp):
        pages = json.load(open(cp))
    else:
        d = w.api(H, action='query', prop='revisions|categories', rvprop='content', rvslots='main', cllimit='max',
                  titles='|'.join(t for t, _ in batch))
        if not d: continue
        pages = []
        for p in d['query']['pages'].values():
            rev = (p.get('revisions') or [{}])[0]
            txt = rev.get('slots', {}).get('main', {}).get('*') or rev.get('*', '')
            pages.append({'title': p['title'], 'wikitext': txt, 'categories': [c['title'] for c in p.get('categories', [])]})
        json.dump(pages, open(cp, 'w'))
    for p in pages:
        out.write(json.dumps(p) + '\n'); n += 1
    if (i // 50) % 20 == 0: print('pages', n, flush=True)
print('done pages', n)
