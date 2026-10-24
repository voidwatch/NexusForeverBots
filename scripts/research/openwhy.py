#!/usr/bin/env python3
"""Exact reason per open doable quest from forced sweep results (docs/OPEN-QUESTS.tsv).
   openwhy.py <quest-dump.tsv> <feed.log> <out.tsv> <sweep-results.tsv>...   (later sweep files win; prints <= 30 lines)
A quest's reason is what its last forced run showed: the first open objective (index:type:data:progress) and what the bot's trace says
stopped it there."""
import csv, re, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tbl
_, creatures = tbl.read('Creature2')
from collections import Counter, defaultdict
dump, feed, out, sweeps = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
names = {}
for line in open('Source/NexusForever.WorldServer/Game/Quest/Static/QuestObjectiveType.cs'):
    m = re.match(r'\s*(\w+)\s*=\s*(\d+),', line)
    if m: names[m.group(2)] = m.group(1)
qd = {r[0]: r for r in csv.reader(open(dump), delimiter='\t')}
last = {}
for f in sweeps:
    for r in csv.DictReader(open(f), delimiter='\t'):
        last[r['quest']] = r
# traces per (bot, quest) from the feed, for the runs in the result files
want = {(r['bot'], q) for q, r in last.items()}
active, traces = {}, defaultdict(list)
for line in open(feed, errors='replace'):
    p = line.rstrip('\n').split('\t')
    if len(p) < 5: continue
    bot, text = p[1], p[4]
    m = re.match(r'forced quest (\d+) \(test\)', text)
    if m:
        q = m.group(1); active[bot] = q if (bot, q) in want else None
        if active[bot]: traces[(bot, q)] = []
        continue
    if re.match(r'forced quest \d+ left the log', text): active[bot] = None; continue
    q = active.get(bot)
    if q and text.startswith('trace: '): traces[(bot, q)].append(text[7:])

def first_open(objs):
    for o in objs.split():
        if o.endswith('*'):
            i, t, d, pr = o.rstrip('*').split(':', 3)
            return i, t, d, pr
    return None

def classify(r):
    tr = traces.get((r['bot'], r['quest']), [])
    fo = first_open(r['objectives'])
    where = f"objective {fo[0]} {names.get(fo[1], 'type ' + fo[1])} data {fo[2]} at {fo[3]}" if fo else "no open objective recorded"
    friendly = [t for t in tr if t.startswith('fighting creature') and (' Friendly' in t or ' Neutral' in t)]
    hostile_fights = [t for t in tr if t.startswith('fighting creature') and ' Hostile' in t]
    notfound = [t for t in tr if t.startswith('no kill target')]
    act_refused = [t for t in tr if 'not cast (cooldown or prerequisite)' in t]
    act_cast = [t for t in tr if re.match(r'activating creature \d+: cast', t)]
    nodriver = [t for t in tr if t.startswith('no creature targets')]
    if fo and fo[1] == '24':
        child = qd.get(fo[2])
        if child is None or child[2] != '1':
            objs = child[16] if child else '?'
            return 'needs-undoable-quest', f"{where}: needs quest {fo[2]}, which bots cannot do (its objectives {objs}: no spawned target or no driver)"
        return 'needs-open-quest', f"{where}: needs quest {fo[2]}, still open"
    if fo and fo[1] == '10':
        return 'scripted-objective', f"{where}: Unknown10 objective (scripted credit, data {fo[2]} not decoded); kills of hostile creatures near the quest did not credit it"
    ids = [int(x) for x in re.findall(r'\d+', ' '.join(re.findall(r'\[([\d,]*)\]', ' '.join(tr))))]
    harvest = sorted({i for i in ids if i in creatures and creatures[i][1] == 5})
    if harvest and fo and fo[1] in ('4', '21', '2', '8'):
        return 'harvest-node', f"{where}: the item comes from harvest nodes {harvest[:6]} (HarvestUnit, TradeskillHarvestingInfo): harvesting is not implemented and node loot is not in the client data"
    if harvest:
        return 'harvest-node', f"{where}: a later objective collects from harvest nodes {harvest[:6]} (HarvestUnit, not implemented, node loot not in the client data); the bot waits at the nodes (spawned as Simple entities it cannot use) instead of going to this objective's creature"
    if r['outcome'] == 'not-takeable':
        return 'not-takeable', r['detail']
    if r['outcome'] == 'timeout':
        return 'slow', f"{where}: still progressing when the {r['seconds']} s bound ended (objectives {r['objectives']})"
    if friendly:
        c = re.search(r'creature (\d+).*faction (\d+) (\w+)', friendly[-1])
        return 'target-not-damageable', f"{where}: target creature {c.group(1)} faction {c.group(2)} is {c.group(3)} to the bot, no damage"
    if nodriver and fo and fo[1] in ('10', '24', '25', '36', '17', '22', '32', '33', '38'):
        return 'no-driver', f"{where}: the bot has no creature or place to go to for this objective ({nodriver[-1][:140]})"
    if act_refused and fo and fo[1] in ('5', '9', '12', '14', '23'):
        c = re.search(r'creature (\d+)', act_refused[-1])
        return 'activation-refused', f"{where}: activate spell of creature {c.group(1)} refused (cooldown or caster prerequisite)"
    if act_cast and fo and fo[1] in ('4', '5', '9', '12', '14', '23', '32'):
        c = re.search(r'creature (\d+)', act_cast[-1])
        if fo[1] == '4':
            return 'activation-no-credit', f"{where}: activate spell of creature {c.group(1)} cast (generic activate, effect Activate only); the item is not given: the object's loot is not in the client data"
        return 'activation-no-credit', f"{where}: activate spell of creature {c.group(1)} cast, the objective was not credited"
    if notfound and not hostile_fights:
        ids = re.search(r'\[([\d,]*)\]', notfound[-1]).group(1)
        return 'target-not-found', f"{where}: no creature of [{ids[:60]}] within 300 m of the bot at the target spawn"
    big = [t for t in hostile_fights if int(re.search(r'health \d+/(\d+)', t).group(1)) >= 1000000]
    if big:
        c = re.search(r'creature (\d+): health (\d+)/(\d+)', big[-1])
        return 'group-boss', f"{where}: target creature {c.group(1)} has {int(c.group(3)):,} health (world boss), one bot took it to {int(c.group(2)):,} in the run; needs a group"
    if fo and fo[3].split('/')[0] not in ('0', ''):
        return 'slow-credit', f"{where}: progressed, then no progress for 6 min (drop/credit rate or targets used up near the bot; fought {', '.join(sorted(set(re.findall(r'fighting creature (\d+)', ' '.join(hostile_fights)))))[:60]})"
    if hostile_fights:
        c = re.search(r'creature (\d+)', hostile_fights[-1])
        return 'kills-no-credit', f"{where}: the bot fought hostile creature {c.group(1)} (and others), the objective did not advance"
    return 'other', f"{where}: {'; '.join(tr[-2:])[:160] or r['detail'][:160]}"

rows, kinds = [], Counter()
open_ids = [q for q, r in qd.items() if r[2] == '1' and r[3] == '0']
for q in sorted(open_ids, key=int):
    r = qd[q]
    s = last.get(q)
    if s is None:
        pre = [p for p in r[12].split(',') if p]
        openpre = [p for p in pre if p in qd and qd[p][3] == '0']
        kind, why = ('chain-blocked', f"waits for open prerequisite quest(s) {','.join(openpre)}") if openpre else ('not-swept', 'not in any sweep run')
        rows.append((q, r[6], kind, '', '', why)); kinds[kind] += 1; continue
    kind, why = classify(s)
    if s['outcome'] == 'completed':
        kind, why = 'completed-forced-only', f"completed by the forced sweep via {s['via']} but not recorded as completed (history lost?)"
    rows.append((q, r[6], kind, s['outcome'] + ' via ' + s['via'], s['normal_verdict'], why)); kinds[kind] += 1
with open(out, 'w') as f:
    f.write('quest\tlevel\treason_kind\tlast_forced_run\tnormal_picker_verdict\treason\n')
    for x in rows: f.write('\t'.join(x) + '\n')
print(f'{len(rows)} open doable quests -> {out}')
for k, v in kinds.most_common(): print(f'  {v} {k}')
