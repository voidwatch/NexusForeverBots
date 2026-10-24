#!/usr/bin/env python3
"""Per-world summary of an instance run from a failure report: instance_table.py REPORT TEAM MINUTES WORLD...
One tab-separated line per world: world, team, minutes, entered, cleared, timeouts, wipes, bosses killed (distinct), best clear,
remaining hostile at the last progress line, stalled creatures with reasons, exceptions (map/spell/encounter cast)."""
import re
import sys
from collections import defaultdict

report, team, minutes, worlds = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
cats = defaultdict(list)  # category -> [(count, key+detail)]
cur = None
for line in open(report, errors='replace'):
    m = re.match(r'^== (\S+) ', line)
    if m:
        cur = m.group(1)
        continue
    m = re.match(r'^\s+(\d+)x (.*)$', line)
    if m and cur:
        cats[cur].append((int(m.group(1)), m.group(2).rstrip()))


def lines(cat, world):
    return [(c, t) for c, t in cats.get(cat, []) if re.search(rf'\bworld {world}\b', t)]


exc = sum(c for cat in ('map-update-exception', 'spell-update-exception', 'encounter-cast-exception') for c, _ in cats.get(cat, []))
for w in worlds:
    entered = sum(c for c, _ in lines('dungeon-entered', w))
    cleared = lines('dungeon-cleared', w)
    timeouts = lines('dungeon-timeout', w)
    wipes = sum(c for c, _ in lines('dungeon-wipe', w))
    bosses = sorted({re.sub(r'^world \d+ ', '', t).split(' (entity')[0] for _, t in lines('encounter-boss-killed', w)})
    best = ''
    secs = [int(m.group(1)) for _, t in cleared for m in [re.search(r', (\d+) s, ', t)] if m]
    if secs:
        best = f'{min(secs)} s'
    rem = lines('dungeon-remaining', w)
    # the remaining line with the fewest hostile is the furthest the team got
    left = ''
    if rem:
        c, t = min(rem, key=lambda x: int(re.search(r'world \d+ (\d+) hostile', x[1]).group(1)))
        left = re.sub(rf'^world {w} ', '', t)[:160]
    stalled = '; '.join(re.sub(rf'^world {w} ', '', t)[:140] for _, t in lines('dungeon-stalled', w)[:3])
    other = [f'{cat} {sum(c for c, _ in lines(cat, w))}' for cat in ('dungeon-no-map', 'dungeon-enter-failed', 'dungeon-empty') if lines(cat, w)]
    print('\t'.join(map(str, [w, team, minutes, entered, sum(c for c, _ in cleared), sum(c for c, _ in timeouts), wipes,
                                len(bosses), '|'.join(bosses)[:300], best, left, stalled, ','.join(other), exc])))
