#!/usr/bin/env python3
"""Creatures behind challenges whose table row has no creature list (VirtualItem 5, Item 4, General 2 without a goal location): the challenge's progress text names them
("Collect energized stones by killing Slateface Gronyx in Halon Ruins", "Harvest Succulent Buzzgoo from Buzzbing Honeyhives").
A creature matches when its localized name or plural form ({p:...}) occurs in the text as whole words; the longest names win
(a name inside a longer matched name is dropped). Item challenges (type 4) also get the creatures Jabbithole lists as dropping the item
(data/research/creature-drops.tsv, by name).

Writes data/research/challenge-sources.tsv: challenge, type, creature ids, matched names. Prints a summary (<= 30 lines).
"""
import os, re, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
OUT = os.path.join(ROOT, 'data', 'research', 'challenge-sources.tsv')

names = collections.defaultdict(set)
for c in load('Creature2'):
    raw = text(c['localizedTextIdName']) or ''
    if not raw or 'DEPRECATE' in c['description'].upper():
        continue
    m = re.match(r'^(.*?)\{p:(.*?)\}$', raw)
    forms = [m.group(1), m.group(2)] if m else [raw]
    for f in forms:
        f = f.strip().lower()
        if len(f) >= 5:
            names[f].add(c['ID'])
# naive English plurals for names without a plural form
for n in list(names):
    for p in (n + 's', n + 'es', n[:-1] + 'ies' if n.endswith('y') else None):
        if p and p not in names:
            names[p] |= names[n]

drops = collections.defaultdict(set)
dfile = os.path.join(ROOT, 'data', 'research', 'creature-drops.tsv')
if os.path.exists(dfile):
    for line in open(dfile, encoding='utf-8', errors='replace'):
        f = line.rstrip('\n').split('\t')
        if len(f) >= 4 and f[3].isdigit():
            drops[int(f[3])].add(f[0].strip().lower())

# creature names that are also plain words of the progress texts ("Clear pools ... by lighting them", "brew it at the still")
GENERIC = {'clear', 'lighting', 'lightning', 'still', 'destination', 'the void', 'malgrave', 'cannons'}
tier = {r['ID']: r for r in load('ChallengeTier')}
out = []
per = collections.Counter(); hit = collections.Counter()
for ch in load('Challenge'):
    t = ch['challengeTypeEnum']
    if t not in (2, 4, 5):
        continue
    # General (2): only rows without a goal location (races are played by reaching the goal) and with count tiers (no ms tiers)
    if t == 2:
        goal = ch['worldLocation2IdIndicator'] and ch['worldLocation2IdIndicator'] != ch['worldLocation2IdStartLocation']
        name = text(ch['localizedTextIdName']) or ''
        tiers = [tier[i]['count'] for i in (ch['challengeTierId00'], ch['challengeTierId01'], ch['challengeTierId02']) if i in tier]
        prog = (text(ch['localizedTextIdProgress']) or '').lower()
        # survive/dodge, follow-a-creature and adventure-medal rows are not counts of creatures
        if goal or 'DEPRECATE' in name.upper() or not tiers or max(tiers) > 1000 \
                or any(w in prog for w in ('dodge', "don't let", 'follow', 'adventure')):
            continue
    per[t] += 1
    progress = text(ch['localizedTextIdProgress']) or ''
    low = ' ' + re.sub(r'[^a-z0-9\' -]', ' ', progress.replace('$+', ' ').lower()) + ' '
    found = [n for n in names if f' {n} ' in low]
    found = [n for n in found if n not in GENERIC]
    found = [n for n in found if not any(n != o and f' {n} ' in f' {o} ' for o in found)]
    if t == 4:
        found += sorted(drops.get(ch['target'], ()))
    ids = sorted({i for n in found for i in names.get(n, ())})
    if ids:
        hit[t] += 1
        out.append((ch['ID'], t, ','.join(map(str, ids)), '; '.join(sorted(set(found)))))

with open(OUT, 'w') as fo:
    fo.write('# challenge\ttype\tcreatures\tnames  (scripts/research/challenge_sources.py)\n')
    for o in out:
        fo.write('\t'.join(map(str, o)) + '\n')
for t in sorted(per):
    print(f'type {t}: {per[t]} challenges, {hit[t]} with source creatures')
for o in out[:8]:
    print(o[0], o[1], o[3][:80], len(o[2].split(',')), 'ids')
