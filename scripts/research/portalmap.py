#!/usr/bin/env python3
"""InstancePortal -> World by name: portal name ("Enter X", "X") against World names and their WorldZone/MapZone names.
Writes research/portal-worlds.tsv: portal id, type, portal name, world id, world name, how matched. Prints a summary."""
import re, os, sys
sys.path.insert(0, os.path.dirname(__file__)); from tbl import load, text
def norm(s): return re.sub(r'[^a-z0-9]', '', s.lower().replace("the ", ""))
worlds = load('World'); wk = [k for k in worlds[0] if 'ocalized' in k]
names = {}
for w in worlds:
    for k in wk:
        t = text(w[k]) if w[k] else ''
        if t: names.setdefault(norm(t), []).append((w['ID'], t))
    a = w.get('assetPath') or ''
    if a: names.setdefault(norm(os.path.basename(a)), []).append((w['ID'], a))
rows, hit = [], 0
for p in load('InstancePortal'):
    n = text(p['localizedTextIdName']) or ''
    base = re.sub(r'^(Enter|Instance Portal to|Leave|Exit)\s+', '', n).strip()
    m = names.get(norm(base)) or []
    how = 'name' if m else ''
    if len(m) > 1: how = 'ambiguous'
    rows.append((p['ID'], p['instancePortalTypeEnum'], n, m[0][0] if len(m) == 1 else '', m[0][1] if len(m) == 1 else ';'.join(str(x[0]) for x in m), how))
    hit += len(m) == 1
os.makedirs('research', exist_ok=True)
with open(os.path.join(os.path.dirname(__file__), '..', '..', 'research', 'portal-worlds.tsv'), 'w') as f:
    for r in rows: f.write('\t'.join(map(str, r)) + '\n')
from collections import Counter
print('portals', len(rows), 'matched', hit, 'by type', Counter((r[1], bool(r[3])) for r in rows))
for r in rows[:12]: print(r)
