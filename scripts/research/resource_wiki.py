#!/usr/bin/env python3
"""resource_wiki.py: pull the class resource sections and focus/regen sentences out of the cached wiki pages (research/wiki-raw),
writes research/decode/resources/wiki-resources.md, prints at most 30 lines."""
import json, glob, re
pages = {}
for fn in glob.glob('research/wiki-raw/**/*.json', recursive=True):
    try: d = json.load(open(fn))
    except Exception: continue
    for p in (d if isinstance(d, list) else [d]):
        if isinstance(p, dict) and p.get('title') and (p.get('wikitext') or p.get('text')):
            pages.setdefault(p['title'], (str(p.get('wikitext') or p.get('text')), fn))
out = ['# Class resources in the cached wiki pages (research/wiki-raw)', '']
for cls in ('Warrior', 'Engineer', 'Esper', 'Medic', 'Stalker', 'Spellslinger'):
    t = pages.get(cls)
    if not t: out.append('## %s: not cached' % cls); continue
    m = re.search(r'==\s*(Ability )?Resources?\s*==(.*?)(\n==[^=])', t[0], re.S)
    out.append('## %s (%s)' % (cls, t[1])); out.append((m.group(2).strip() if m else '(no resource section)')[:1500]); out.append('')
for title in ('Attributes', 'Character', 'Focus', 'Combat'):
    t = pages.get(title)
    if not t: continue
    for m in re.finditer(r'[^.\n]*(focus|regen|out of combat|in combat)[^.\n]*\.', t[0], re.I):
        s = m.group(0).strip()
        if any(w in s.lower() for w in ('focus', 'regen')) and len(s) < 400:
            out.append('- %s: %s' % (title, s))
open('research/decode/resources/wiki-resources.md', 'w').write('\n'.join(out) + '\n')
print('\n'.join(l[:300] for l in out)[:6000])
