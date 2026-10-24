#!/usr/bin/env python3
"""Scan targets for scientist missions (Scan 2, ScanChecklist 14, SpecimenSurvey 23) that Jabbithole lists no creature for:
the subject of the mission title ("BOTANY: Glowstalks" -> Glowstalk) matched against Creature2 names and their plural forms ({p:...}).
Skips untitled, [TEST] and DEPRECATED rows. Writes data/research/path-mission-name-targets.tsv in the format of path-mission-targets.tsv
(mission, path/type, creatures, name, source). Prints a summary (<= 30 lines).
"""
import os, re, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
OUT = os.path.join(ROOT, 'data', 'research', 'path-mission-name-targets.tsv')
JH = os.path.join(ROOT, 'data', 'research', 'path-mission-targets.tsv')

names = collections.defaultdict(set)
for c in load('Creature2'):
    raw = text(c['localizedTextIdName']) or ''
    if not raw or 'DEPRECATE' in c['description'].upper() or '[TEST]' in c['description'].upper():
        continue
    m = re.match(r'^(.*?)\{p:(.*?)\}$', raw)
    for f in ([m.group(1), m.group(2)] if m else [raw]):
        f = f.strip().lower()
        if len(f) >= 4:
            names[f].add(c['ID'])
for n in list(names):
    for p in (n + 's', n + 'es', n[:-1] + 'ies' if n.endswith('y') else None):
        if p and p not in names:
            names[p] |= names[n]

listed = {int(l.split('\t')[0]) for l in open(JH) if l[:1].isdigit()}
out, missed = [], []
for r in load('PathMission'):
    if r['pathMissionTypeEnum'] not in (2, 14, 23) or r['ID'] in listed:
        continue
    title = text(r['localizedTextIdName']) or ''
    if not title or 'DEPRECAT' in title.upper() or '[TEST]' in title.upper():
        continue
    subject = title.split(':', 1)[-1].strip().lower()
    ids = names.get(subject)
    if not ids:
        # the longest creature name inside the subject ("Glowstalk Feeders" has none, "Space Debris" is one)
        found = sorted((n for n in names if f' {n} ' in f' {subject} '), key=len, reverse=True)
        # only a name that is most of the subject ("Fire and Ice" -> "fire" and "Glowstalk Feeders" -> "glowstalk" are not)
        ids = names[found[0]] if found and len(found[0]) >= 0.75 * len(subject) else None
    if ids:
        out.append((r['ID'], f"2/{r['pathMissionTypeEnum']}", ','.join(map(str, sorted(ids))), title, 'title-match'))
    else:
        missed.append((r['ID'], title))

with open(OUT, 'w') as fo:
    fo.write('# mission\tpath/type\tcreatures\tname\tsource  (scripts/research/path_name_targets.py)\n')
    for o in out:
        fo.write('\t'.join(map(str, o)) + '\n')
print(f'{len(out)} missions matched by title, {len(missed)} not')
for o in out[:12]:
    print(o[0], o[3], len(o[2].split(',')), 'ids')
for m in missed[:10]:
    print('no match:', m[0], m[1])
