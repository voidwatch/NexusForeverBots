#!/usr/bin/env python3
"""Remaining questcheck gaps with the reason each could not be filled -> docs/quest-gaps.tsv (committed). Prints a summary (<= 30 lines).
    gapreport.py <quest-check.txt>
Reasons: no archived page (name not among 24,000 Jabbithole NPC pages, most are 2016+ content: Arcterra, Nexus Virtuality, holiday events),
ambiguous archive match, placeholder NPC without name, no turn-in location, objective group without placeable member, archive lists nothing.
"""
import os, re, sys, glob, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text
ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
R = os.path.join(ROOT, 'research')
def slug(n): return re.sub(r'[^a-z0-9]+', '-', n.lower().replace("'", '')).strip('-')
cr = {c['ID']: c for c in load('Creature2')}
q2 = {q['ID']: q for q in load('Quest2')}
pages = {re.sub(r'-\d+(-veteran)?$', '', os.path.basename(p)[:-8]) for p in glob.glob(f'{R}/jh-raw/*.html.gz')}
amb = set()
for l in open(f'{R}/match-report.txt'):
    f = l.split('\t')
    if f[0] == 'ambiguous':
        amb.add(re.sub(r'-\d+(-veteran)?\.html\.gz$', '', f[1]))
jh = {}
if os.path.exists(f'{R}/decode/quests/jh-quests.tsv'):
    for l in list(open(f'{R}/decode/quests/jh-quests.tsv'))[1:]:
        f = l.rstrip('\n').split('\t'); jh[int(f[0])] = f
def npc_reason(c):
    if c not in cr: return 'creature missing from Creature2'
    n = text(cr[c]['localizedTextIdName'])
    if not n: return 'placeholder NPC without name'
    if slug(n) in amb: return 'archive page ambiguous (several same-name creatures, no zone/level match)'
    if slug(n) in pages: return 'archive page without usable zone/coordinates'
    return 'no archived Jabbithole page (name not among 24,000 NPC pages)'
rows, summ = [], collections.Counter()
for l in open(sys.argv[1]):
    f = l.rstrip('\n').split('\t')
    if f[0] not in ('giver-not-spawned', 'receiver-not-spawned', 'objective-not-spawned', 'no-giver', 'no-receiver'):
        continue
    q = int(f[1].split()[1]); title = text(q2[q]['localizedTextIdTitle'])
    if f[0] in ('giver-not-spawned', 'receiver-not-spawned'):
        c = int(f[2].split()[1].split(',')[0])
        reason = npc_reason(c)
        if f[0] == 'receiver-not-spawned' and not q2[q]['WorldLocation2IdReceiver']:
            reason += '; no turn-in location in Quest2'
        who = f"creature {c} {text(cr[c]['localizedTextIdName']) if c in cr else ''}"
    elif f[0] == 'objective-not-spawned':
        reason = 'objective target(s) without archive page or usable indicator'
        who = f[2]
    else:
        page = jh.get(q)
        col = 3 if f[0] == 'no-giver' else 4
        reason = ('archive lists: ' + page[col]) if page and page[col] else ('archive page lists nothing' if page else 'no archived quest page; no giver/receiver in the tables')
        who = ''
    summ[(f[0], reason.split(';')[0].split(':')[0])] += 1
    rows.append(f'{f[0]}\t{q}\t{title}\t{who}\t{reason}')
with open(f'{ROOT}/docs/quest-gaps.tsv', 'w') as o:
    o.write('# remaining gt questcheck gaps (scripts/research/gapreport.py); kind, quest, title, creature/objective, reason\n')
    o.write('\n'.join(rows) + '\n')
for (k, r), n in sorted(summ.items(), key=lambda t: (t[0][0], -t[1])):
    print(n, k, r)
