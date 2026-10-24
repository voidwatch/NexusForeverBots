#!/usr/bin/env python3
"""Dump quest objectives of given types with quest titles, texts and id joins. Output -> research/decode/obj-<type>.txt"""
import sys, collections, tbl
OUT = '../../research/decode/'
obj = tbl.index('QuestObjective')
owner = collections.defaultdict(list)
for q in tbl.load('Quest2'):
    for i, k in enumerate(['objective0','objective01','objective02','objective03','objective04','objective05']):
        if q[k]: owner[q[k]].append((q, i))
joins = {}
for t in ['Creature2','Spell4','Item2','Quest2','WorldZone','WorldLocation2','PublicEvent','TargetGroup','Challenge','WorldArea','Achievement','PathMission','Spell4Base','VirtualItem','Datacube','PublicEventObjective','TradeskillSchematic2','WorldDungeon' ,'MatchingGameMap','Prerequisite']:
    try: joins[t] = tbl.index(t)
    except Exception as e: pass
def name(t, r):
    for k in ('localizedTextIdName','localizedTextIdTitle','localizedTextId','localizedTextIdObjective','description'):
        if k in r and r[k]:
            return tbl.text(r[k]) if k != 'description' else r[k]
    return ''
for typ in map(int, sys.argv[1:]):
    rows = [o for o in obj.values() if o['type'] == typ]
    hits = collections.Counter()
    with open(f'{OUT}obj-{typ}.txt', 'w') as f:
        for o in rows:
            j = [t for t, ix in joins.items() if o['data'] in ix]
            for t in j: hits[t] += 1
            for q, i in owner.get(o['ID'], [(None, None)]):
                qt = tbl.text(q['localizedTextIdTitle']) if q else '-'
                f.write(f"obj {o['ID']} q {q['ID'] if q else '-'}#{i} data {o['data']} count {o['count']} flags {o['flags']} tg {o['targetGroupIdRewardPane']} | {qt} | {tbl.text(o['localizedTextIdFull'])} | short {tbl.text(o['localizedTextIdShort'])}\n")
            f.write('   in: ' + ', '.join(f"{t}:{name(t, joins[t][o['data']])[:40]}" for t in j) + '\n')
    print(typ, len(rows), 'data-joins:', hits.most_common(8))
