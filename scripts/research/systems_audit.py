#!/usr/bin/env python3
"""systems_audit.py <failure-report> [more reports]: for every docs/SYSTEMS.md row, the probe names in the evidence column (`name`)
and their ok/fail counts in the reports (system-ok / system-fail sections). Flags done rows without a probe that passed and
blocked rows without written evidence. Writes research/decode/systems-audit.tsv, prints at most 40 lines."""
import re, sys, collections
ok, fail = collections.Counter(), collections.Counter()
for f in sys.argv[1:]:
    mode = None
    for l in open(f, errors='ignore'):
        if l.startswith('== system-ok'): mode = ok; continue
        if l.startswith('== system-fail'): mode = fail; continue
        if l.startswith('=='): mode = None; continue
        m = re.match(r'\s+(\d+)x (\S+)', l)
        if mode is not None and m: mode[m.group(2)] += int(m.group(1))
rows = []
for l in open('docs/SYSTEMS.md'):
    p = [x.strip() for x in l.split('|')]
    if len(p) < 6 or p[1] in ('system', '') or set(p[1]) <= set('-: '): continue
    probes = re.findall(r'`([a-z0-9]+)`()', p[3] + ' ' + p[4])
    names = [a or b for a, b in probes]
    good = [n for n in names if ok[n] > 0]
    flag = ''
    if p[2] == 'done' and not good: flag = 'DONE-WITHOUT-PASSING-PROBE'
    if p[2] == 'blocked' and len(p[4]) < 40: flag = 'BLOCKED-WITHOUT-EVIDENCE'
    rows.append((p[1], p[2], ' '.join(f'{n}:{ok[n]}/{fail[n]}' for n in names), flag))
with open('research/decode/systems-audit.tsv', 'w') as o:
    for r in rows: o.write('\t'.join(r) + '\n')
for r in rows[:40]: print(f'{r[0][:34]:34} {r[1]:8} {r[3]:26} {r[2][:90]}')
