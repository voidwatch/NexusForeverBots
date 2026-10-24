#!/usr/bin/env python3
"""Summary of a clean-bot run: scripts/quest-truth.py <dir> (reads <dir>/quest-truth.tsv and <dir>/quest-truth-open.tsv, prints at most ~40 lines).
Per quest the best completion counts: real (verified) < grant < heuristic < synthetic. Kinds are counted over the quests whose best completion
has that class; one quest can carry several kinds."""
import collections, csv, os, sys

d = sys.argv[1] if len(sys.argv) > 1 else '.'
rank = {'real': 0, 'grant': 1, 'heuristic': 2, 'synthetic': 3}
best, rows = {}, 0
turnin = collections.Counter()
chars = set()
path = os.path.join(d, 'quest-truth.tsv')
if os.path.exists(path):
    with open(path, newline='') as f:
        for r in csv.DictReader(f, delimiter='\t'):
            if not r.get('quest') or r['quest'] == 'quest':
                continue
            rows += 1
            chars.add(r['character'])
            turnin[r['turn_in']] += 1
            q = r['quest']
            if q not in best or rank[r['how']] < rank[best[q]['how']]:
                best[q] = r
by = collections.Counter(r['how'] for r in best.values())
kinds = {c: collections.Counter() for c in rank}
for r in best.values():
    if r['how'] in ('heuristic', 'synthetic'):
        for k in filter(None, r['heuristic'].split(',')):
            kinds[r['how']][k] += 1
    if r['how'] in ('grant', 'heuristic', 'synthetic'):
        for k in filter(None, r['grants'].split(',')):
            kinds['grant' if r['how'] == 'grant' else r['how']]['grant:' + k if r['how'] != 'grant' else k] += 1
print(f"clean completions {rows} by {len(chars)} characters, distinct quests {len(best)}")
print(f"| class | quests | kinds |\n|---|---|---|")
print(f"| verified (real objectives only, no grant) | {by['real']} | |")
for c in ('heuristic', 'grant', 'synthetic'):
    print(f"| {c} | {by[c]} | {', '.join(f'{k} {n}' for k, n in kinds[c].most_common(8))} |")
openp = os.path.join(d, 'quest-truth-open.tsv')
if os.path.exists(openp):
    with open(openp, newline='') as f:
        op = [r for r in csv.DictReader(f, delimiter='\t')]
    reasons = collections.Counter()
    for r in op:
        reason = r['reason']
        for cut in '0123456789':
            i = reason.find(cut)
            if i > 0:
                reason = reason[:i].strip()
        reasons[reason] += 1
    print(f"| not completed | {len(op)} | {', '.join(f'{k} {n}' for k, n in reasons.most_common(8))} |")
print("turn-in of all completions: " + ', '.join(f'{k} {n}' for k, n in turnin.most_common()))
