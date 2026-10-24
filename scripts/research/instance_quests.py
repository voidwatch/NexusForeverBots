#!/usr/bin/env python3
"""Quests that need an objective in an instanced world (World.type 1, 6, 9, 11): every objective with its type, data, count and the
worlds where its targets are spawned, plus the completion state and how completions were made.

    scripts/research/instance_quests.py QUEST_TARGETS FAILURE_REPORT OUT.tsv

QUEST_TARGETS: /cache/quest-targets.tsv (quest, doable, completed, targets, spawn worlds). FAILURE_REPORT: the world's failure report
(instance-quest-completed lines say real / heuristic / synthetic per quest). Prints a summary of at most 30 lines.
"""
import collections, os, re, sys
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text


def main(targets_path, report_path, out_path):
    worlds = {r['ID']: r for r in load('World')}
    instanced = {w for w, r in worlds.items() if r['type'] in (1, 6, 9, 11)}
    quests = {r['ID']: r for r in load('Quest2')}
    objectives = {r['ID']: r for r in load('QuestObjective')}
    how = collections.defaultdict(collections.Counter)
    for line in open(report_path, errors='replace'):
        m = re.match(r'\s+(\d+)x quest (\d+) (real|synthetic|heuristic \S+)', line)
        if m and 'instance-quest-completed' in section:
            how[int(m.group(2))][m.group(3)] += int(m.group(1))
        if line.startswith('== '):
            section = line
    rows = []
    for line in open(targets_path).read().split('\n')[1:]:
        f = line.split('\t')
        if len(f) < 5 or f[1] == '1':
            continue
        spawn_worlds = sorted({int(x) for x in f[4].split(',') if x})
        if not any(w in instanced for w in spawn_worlds):
            continue
        q = quests.get(int(f[0]))
        if q is None:
            continue
        objs = []
        for key in ['objective0'] + [f'objective{i:02d}' for i in range(1, 10)]:
            o = objectives.get(q.get(key, 0))
            if o:
                objs.append(f"{o['type']}:{o['data']}x{o['count']}")
        h = how.get(q['ID'], {})
        state = 'real' if h.get('real') else ('heuristic' if any(k.startswith('heuristic') for k in h) else ('synthetic' if h.get('synthetic') else ('history' if f[2] == '1' else 'open')))
        rows.append((q['ID'], text(q['localizedTextIdTitle'])[:50], q.get('conLevel', 0), ','.join(map(str, spawn_worlds)), ' '.join(objs), f[2], state,
                     ' '.join(f'{k}:{v}' for k, v in sorted(h.items()))))
    with open(out_path, 'w') as out:
        out.write('quest\ttitle\tlevel\tspawn worlds\tobjectives (type:data x count)\tcompleted ever\tthis run\tcompletions this run\n')
        for r in rows:
            out.write('\t'.join(str(x) for x in r) + '\n')
    c = collections.Counter(r[6] for r in rows)
    print(f"{len(rows)} instance quests -> {out_path}: " + ', '.join(f'{k} {v}' for k, v in sorted(c.items())))
    print(f"completed ever (history): {sum(1 for r in rows if r[5] == '1')}")


section = ''
if __name__ == '__main__':
    main(*sys.argv[1:4])
