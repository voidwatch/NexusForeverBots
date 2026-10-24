#!/usr/bin/env python3
"""Which creatures sit behind the gt questcheck gaps, and what research data exists for each.

    gapcreatures.py <quest-check.txt>   writes research/decode/quests/gap-creatures.tsv, prints a summary (<= 30 lines)
Columns: creature, name, roles (giver/receiver/target), gap quests, has-jh-page, jh-cdx, match status, receiver location from Quest2, indicators.
"""
import os, re, sys, gzip, glob, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
R = os.path.join(ROOT, 'research')
def slug(name):
    return re.sub(r'[^a-z0-9]+', '-', name.lower().replace("'", '')).strip('-')

gap = collections.defaultdict(lambda: collections.defaultdict(set))   # creature -> role -> quests
qrows = collections.defaultdict(set)
quests = {q['ID']: q for q in load('Quest2')}
obj = {o['ID']: o for o in load('QuestObjective')}
for l in open(sys.argv[1]):
    f = l.rstrip('\n').split('\t')
    if len(f) < 3:
        continue
    q = int(f[1].split()[1])
    if f[0] == 'giver-not-spawned':
        for c in f[2].split()[1].split(','):
            gap[int(c)]['giver'].add(q)
    elif f[0] == 'receiver-not-spawned':
        for c in f[2].split()[1].split(','):
            gap[int(c)]['receiver'].add(q)
    elif f[0] == 'objective-not-spawned':
        qrows['objective'].add((q, int(f[2].split()[1])))
need = {}
for l in open(f'{R}/needed-creatures.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    need[int(f[0])] = f
# objective targets: needed creatures with role target in those quests (exportneeded already lists only unsatisfied groups)
objq = {q for q, _ in qrows['objective']}
for cid, f in need.items():
    if 'target' in f[2]:
        gap[cid]['target'] |= set()   # quests unknown per creature here; mark role only
names = {}
for l in open(f'{R}/creatures.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    names[int(f[0])] = f[1] if len(f) > 1 else ''
pages = collections.defaultdict(list)
for p in glob.glob(f'{R}/jh-raw/*.html.gz'):
    b = os.path.basename(p)[:-8]
    pages[re.sub(r'-\d+(-veteran)?$', '', b)].append(b)
cdx = collections.Counter()
for l in open(f'{R}/jh-npc-cdx.txt'):
    m = re.search(r'/npcs/([^ ?]+)', l)
    if m:
        cdx[re.sub(r'-\d+(-veteran)?$', '', m.group(1).rstrip('/'))] += 1
status = collections.defaultdict(set)
for l in open(f'{R}/match-report.txt'):
    f = l.split('\t')
    if len(f) > 1:
        status[re.sub(r'-\d+(-veteran)?\.html\.gz$', '', f[1])].add(f[0])
out = open(f'{R}/decode/quests/gap-creatures.tsv', 'w')
summary = collections.Counter()
for cid in sorted(gap):
    roles = gap[cid]
    nm = names.get(cid, '')
    s = slug(nm) if nm else ''
    has_page = bool(pages.get(s))
    st = ','.join(sorted(status.get(s, ()))) or '-'
    kind = 'no-name' if not nm else ('page:' + st if has_page else ('cdx-only' if cdx.get(s) else 'no-page'))
    for r in roles:
        if r == 'target':
            continue
        summary[(r, kind)] += 1
    out.write(f"{cid}\t{nm}\t{','.join(r for r in roles)}\t{','.join(str(q) for r in roles.values() for q in sorted(r))}\t{kind}\n")
out.close()
for k, n in sorted(summary.items(), key=lambda t: -t[1])[:20]:
    print(n, k)
# receivers: Quest2 WorldLocation2IdReceiver available?
loc = {int(l.split()[0]): l.split() for l in open(f'{R}/locations.tsv')}
rq = [q for c in gap for q in gap[c]['receiver']]
withloc = [q for q in set(rq) if quests[q]['WorldLocation2IdReceiver'] in loc]
print('receiver-not-spawned quests', len(set(rq)), 'with WorldLocation2IdReceiver in a world', len(withloc))
