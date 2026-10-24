#!/usr/bin/env python3
"""Per stalled/timed-out quest of a forced sweep: the distinct trace kinds its bot printed while it held the quest.
   sweepwhy.py <sweep-results.tsv> <bot-feed.log> [out.tsv]   (prints at most 30 lines)"""
import csv, re, sys
from collections import Counter, defaultdict
res, feed = sys.argv[1], sys.argv[2]
out = sys.argv[3] if len(sys.argv) > 3 else None
rows = [r for r in csv.DictReader(open(res), delimiter='\t') if r['outcome'] in ('stalled', 'timeout')]
want = {(r['bot'], r['quest']) for r in rows}
active, traces = {}, defaultdict(list)
for line in open(feed, errors='replace'):
    p = line.rstrip('\n').split('\t')
    if len(p) < 5: continue
    bot, text = p[1], p[4]
    m = re.match(r'forced quest (\d+) \(test\)', text)
    if m:
        active[bot] = m.group(1) if (bot, m.group(1)) in want else None
        if active[bot]: traces[(bot, active[bot])] = []
        continue
    if re.match(r'forced quest \d+ left the log', text):
        active[bot] = None; continue
    q = active.get(bot)
    if q and text.startswith('trace: '):
        traces[(bot, q)].append(text[7:])
def kind(t):
    t = re.sub(r'\[[\d,]*\]', '[..]', t); t = re.sub(r'-?\d+(\.\d+)?', '#', t); return t[:80]
lines = []
for r in rows:
    tr = traces.get((r['bot'], r['quest']), [])
    fights = [t for t in tr if t.startswith('fighting creature')]
    friendly = sorted({re.search(r'creature (\d+)', t).group(1) + ':' + re.search(r'faction (\d+)', t).group(1) for t in fights if ' Friendly' in t or ' Neutral' in t})
    lastfight = fights[-1] if fights else ''
    kinds = Counter(kind(t) for t in tr)
    lines.append((r['quest'], r['outcome'], r['objectives'], ','.join(friendly), lastfight[:120], ' ; '.join(k for k, _ in kinds.most_common(4))))
if out:
    with open(out, 'w') as f:
        f.write('quest\toutcome\tobjectives\tnonhostile_targets\tlast_fight\ttrace_kinds\n')
        for l in lines: f.write('\t'.join(l) + '\n')
c = Counter('non-hostile kill target' if l[3] else (l[5].split(' ; ')[0] if l[5] else 'no trace') for l in lines)
for k, v in c.most_common(30): print(v, k)
