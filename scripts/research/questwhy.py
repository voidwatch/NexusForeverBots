#!/usr/bin/env python3
"""First failing step for every doable quest nobody completed.
    questwhy.py <quest-dump.tsv> [out.tsv]   (dump from `scripts/gt.sh gt questdump`, /cache/quest-dump.tsv)
Prints <= 40 lines: counts per reason; writes one line per open quest (quest, level, reason, detail) to out.tsv.
"""
import os, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text
OT = {2:'KillCreature',3:'TalkTo',4:'CollectItem',5:'ActivateEntity',8:'KillTargetGroups',9:'ActivateEntity2',10:'Unknown10',11:'SpellSuccess',
      12:'SucceedCSI',13:'SpellSuccess2',14:'ActivateTargetGroupChecklist',15:'Unknown15',16:'KillTargetGroup',17:'EnterZone',18:'TalkToTargetGroup',
      20:'Unknown20',21:'GatherResource',22:'EnterArea',23:'ActivateTargetGroup',24:'CompleteQuest',25:'CompleteEvent',27:'Unknown27',28:'Unknown28',
      29:'Unknown29',31:'Unknown31',32:'VirtualCollect',33:'CraftSchematic',35:'SpellSuccess3',36:'SpellSuccess4',37:'LearnTradeskill',38:'ObtainSchematic',
      39:'Unknown39',40:'Unknown40',41:'PvPKills',42:'EarnCurrency',44:'CombatMomentum',46:'KillCreature2',47:'CompleteChallenge',48:'BeginMatrix'}
Q = {}
for l in open(sys.argv[1]):
    f = l.rstrip('\n').split('\t')
    if len(f) < 17: continue
    q = dict(id=int(f[0]), indexed=f[1]=='1', doable=f[2]=='1', done=f[3]=='1', acc=int(f[4]), stl=int(f[5]), lvl=int(f[6]), plvl=int(f[7]),
             fac=int(f[8]), race=int(f[9]), cls=int(f[10]), pflags=int(f[11]), preq=[int(x) for x in f[12].split(',') if x], pid=int(f[13]),
             periodic=int(f[14]), type=int(f[15]), obj=[tuple(int(y) for y in x.split(':')) for x in f[16].split(',') if x], stall=f[17] if len(f) > 17 else '')
    Q[q['id']] = q
pre = {p['ID']: p for p in load('Prerequisite')}
def ptypes(pid):
    p = pre.get(pid)
    if not p: return 'missing'
    return '+'.join(f"t{p[f'prerequisiteTypeId{i}']}(o{p[f'objectId{i}']},v{p[f'value{i}']},c{p[f'prerequisiteComparisonId{i}']})" for i in range(3) if p[f'prerequisiteTypeId{i}'])
def objs(q): return '/'.join(sorted({OT.get(o[0], str(o[0])) for o in q['obj']})) or 'no-objectives'
def blockers(q):
    if not q['preq']: return []
    open_ = [p for p in q['preq'] if not (p in Q and Q[p]['done'])]
    if q['pflags'] & 1: return open_ if len(open_) == len(q['preq']) else []
    return open_
def root(qid, seen):
    """reason the quest itself can never be finished by bots (used for chain roots)"""
    q = Q.get(qid)
    if q is None: return ('chain: prerequisite quest not in table', str(qid))
    if q['done']: return None
    if qid in seen: return ('chain: cycle', str(qid))
    seen.add(qid)
    b = blockers(q)
    if b:
        r = root(b[0], seen)
        return r
    if not q['indexed']:
        if not q['obj']: return ('chain root: prerequisite quest has no objectives (bots never take it)', f'quest {qid} type {q["type"]}')
        return (f'chain root: prerequisite quest not doable (an objective has no spawned target or no bot path: {objs(q)})', f'quest {qid}')
    if not q['doable']: return ('chain root: prerequisite quest has no open-world spawn of its targets', f'quest {qid} {objs(q)}')
    r, d = own(q)
    return ('chain root (doable, open): ' + r, f'quest {qid} {d}')
import re
def own(q):
    if q['acc'] > 0:
        stall = q['stall'] or 'none recorded'
        reason = 'accepted, stalls: ' + re.sub(r'objective \d+ ', '', re.sub(r'data \d+ \d+/\d+', '', stall))[:90]
        det = f'{objs(q)}'
    elif q['pid']:
        reason, det = f'never accepted: prerequisite id {ptypes(q["pid"]).split("(")[0]}', ptypes(q['pid'])
    elif q['periodic']:
        reason, det = 'never accepted: periodic (daily) group', f'group {q["periodic"]}'
    elif q['type'] == 13:
        reason, det = 'never accepted: contract', ''
    elif q['race'] or q['cls']:
        reason, det = 'never accepted: race/class restricted', f'race {q["race"]} class {q["cls"]}'
    else:
        reason, det = 'never accepted: no refusal reason in data (picker never chose it)', f'L{q["lvl"]} {objs(q)}'
    return reason, det
rows = []
for q in Q.values():
    if not q['doable'] or q['done']: continue
    b = blockers(q)
    if b:
        r = root(b[0], {q['id']})
        reason, det = r if r else ('chain: prerequisite done (stale)', '')
    else:
        reason, det = own(q)
    rows.append((q['id'], q['lvl'], reason, det, objs(q)))
c = collections.Counter(r[2] for r in rows)
print(f'open doable quests: {len(rows)}')
for k, v in c.most_common(40):
    ex = [r for r in rows if r[2] == k][:3]
    print(f'{v:5d}  {k[:110]}  e.g. ' + '; '.join(f'{e[0]} {e[3][:40]}' for e in ex))
if len(sys.argv) > 2:
    with open(sys.argv[2], 'w') as o:
        o.write('quest\tlevel\treason\tdetail\tobjectives\n')
        for r in sorted(rows, key=lambda r: (r[2], r[0])): o.write('\t'.join(map(str, r)) + '\n')

# markdown report: questwhy.py <dump> <out.tsv> <out.md> [title line]
if len(sys.argv) > 3:
    def family(r):
        k = r[2]
        if k.startswith('accepted, stalls'): return 'accepted but stalls (objective types left open)'
        if k.startswith('chain root: prerequisite quest not doable'): return 'chain root: prerequisite quest not doable (an objective has no spawned target or no bot path)'
        return k
    fam = collections.defaultdict(list)
    for r in rows: fam[family(r)].append(r)
    with open(sys.argv[3], 'w') as o:
        o.write('# Quest coverage: why doable quests are not completed\n\n')
        o.write((sys.argv[4] if len(sys.argv) > 4 else '') + '\n\n')
        o.write('Generated by `scripts/gt.sh gt questdump` + `scripts/research/questwhy.py`. A quest is doable when bots can resolve every objective and a target spawns in a world a bot can enter (or it needs no creature). The reason is the first failing step: a chain-blocked quest is charged to the root of its prerequisite chain.\n\n')
        o.write(f'Open doable quests: {len(rows)}\n\n| reason | quests | examples (quest: detail) |\n|---|---|---|\n')
        for k, rs in sorted(fam.items(), key=lambda kv: -len(kv[1])):
            ex = '; '.join(f'{e[0]}: {e[3][:50]}' for e in rs[:4]).replace('|', '/')
            o.write(f'| {k} | {len(rs)} | {ex} |\n')
        o.write('\n## Stalls by objective pattern\n\n| last stall | quests | examples |\n|---|---|---|\n')
        for k, v in collections.Counter(r[2] for r in rows if r[2].startswith('accepted')).most_common(25):
            o.write(f"| {k[18:].strip()} | {v} | {', '.join(str(r[0]) for r in rows if r[2] == k)[:80]} |\n")
