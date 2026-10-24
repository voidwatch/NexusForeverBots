#!/usr/bin/env python3
"""Spawn and encounter coverage of every instanced world (World.type 1 expedition, 6 adventure, 9 raid, 11 dungeon).

    scripts/research/instance_baseline.py ENTITY_COUNTS QUEST_TARGETS OUT.md

ENTITY_COUNTS: "world<TAB>creature<TAB>count" from the world database (entity table).
QUEST_TARGETS: /cache/quest-targets.tsv from the world server (quest, doable, completed, targets, spawn worlds).
Columns: spawns and distinct creatures in the database, bosses (Creature2Difficulty rankValue 2 = Prime with groupValue >= 5, from the
tables) spawned / in the tables for the instance's designer tag, Spell4 rows that carry a spawned boss's designer tag and encounter
number (the boss spell list), encounter definitions (data/encounters), MatchingGameMap entries and the quests that need the world.
Prints a summary of at most 30 lines.
"""
import collections, glob, json, os, re, sys
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text

TAG = re.compile(r'^(?:\[Dungeon\])?\[(?P<tag>[A-Z0-9]{2,4})\]\s*e(?P<enc>\d+)\s*-\s*(?:\((?P<mode>[NV/]+)\)\s*)?(?P<boss>[^-]+?)\s*-')
TYPES = {1: 'expedition', 6: 'adventure', 9: 'raid', 11: 'dungeon'}


def main(counts_path, targets_path, out_path):
    worlds = {r['ID']: r for r in load('World') if r['type'] in TYPES}
    creatures = {r['ID']: r for r in load('Creature2')}
    difficulty = {r['ID']: r for r in load('Creature2Difficulty')}
    gtypes = {r['ID']: r for r in load('MatchingGameType')}
    matching = collections.defaultdict(list)
    for m in load('MatchingGameMap'):
        if m['worldId'] in worlds and m['matchingGameTypeId'] in gtypes:
            t = gtypes[m['matchingGameTypeId']]
            matching[m['worldId']].append(f"team {t['teamSize']} lvl {t['minLevel']}-{t['maxLevel']}")

    def is_boss(cid):
        d = difficulty.get(creatures[cid]['creature2DifficultyId']) if cid in creatures else None
        return d is not None and d['rankValue'] == 2 and d['groupValue'] >= 5

    def tag_of(cid):
        m = TAG.match(creatures[cid]['description'] or '') if cid in creatures else None
        return (m.group('tag'), int(m.group('enc'))) if m else None

    spawns = collections.defaultdict(collections.Counter)
    for line in open(counts_path):
        f = line.split('\t')
        if len(f) == 3 and int(f[0]) in worlds:
            spawns[int(f[0])][int(f[1])] += int(f[2])

    # boss spell lists: Spell4 rows by (tag, encounter)
    spells = collections.Counter()
    for s in load('Spell4'):
        m = TAG.match(s['description'] or '')
        if m and not re.search(r'proxy|visual|message|test|deprecat|auto ?attack', s['description'], re.I):
            spells[(m.group('tag'), int(m.group('enc')))] += 1

    # tags of a world = tags of its spawned creatures; bosses in the tables with those tags
    tag_bosses = collections.defaultdict(set)
    for cid, c in creatures.items():
        t = tag_of(cid)
        if t and is_boss(cid):
            tag_bosses[t[0]].add(cid)

    encounter_names = collections.defaultdict(set)
    for path in glob.glob(os.path.join(os.path.dirname(__file__), '..', '..', 'data', 'encounters', '**', '*.json'), recursive=True):
        try:
            data = json.load(open(path))
        except Exception:
            continue
        for e in data if isinstance(data, list) else [data]:
            if isinstance(e, dict) and e.get('boss'):
                encounter_names[e.get('world', 0)].add(e['boss'].lower())

    quests = collections.defaultdict(lambda: [0, 0])
    for line in open(targets_path).read().split('\n')[1:]:
        f = line.split('\t')
        if len(f) < 5 or f[1] == '1':
            continue
        for w in {int(x) for x in f[4].split(',') if x}:
            if w in worlds:
                quests[w][0] += 1
                quests[w][1] += int(f[2])

    rows = []
    for wid in sorted(worlds, key=lambda w: (worlds[w]['type'], w)):
        w = worlds[wid]
        sp = spawns.get(wid, collections.Counter())
        tags = collections.Counter(t[0] for c in sp if (t := tag_of(c)))
        tag = tags.most_common(1)[0][0] if tags else '-'
        bosses = sorted(c for c in sp if is_boss(c))
        table_bosses = tag_bosses.get(tag, set()) if tag != '-' else set()
        boss_spells = sum(spells.get(t, 0) for t in {tag_of(c) for c in bosses} if t)
        names = encounter_names.get(wid, set())
        scripted = sum(1 for c in bosses if text(creatures[c]['localizedTextIdName']).lower() in names)
        name = os.path.basename(w['assetPath'].replace('\\', '/')) or str(wid)
        playable = 'no spawns' if not sp else ('no boss' if not bosses else 'yes')
        rows.append((wid, name, TYPES[w['type']], sum(sp.values()), len(sp), tag, len(bosses), len(table_bosses), boss_spells,
                     scripted, len(names), '; '.join(sorted(set(matching.get(wid, [])))) or '-', quests[wid][0], quests[wid][1], playable))

    with open(out_path, 'w') as out:
        out.write('| world | name | type | spawns | creatures | tag | bosses spawned | bosses in tables (tag) | boss spells (tables) | bosses with encounter file | encounter files | matching | quests needing it | of those completed | playable |\n')
        out.write('|' + ' --- |' * 15 + '\n')
        for r in rows:
            out.write('| ' + ' | '.join(str(x) for x in r) + ' |\n')
    print(f"{len(rows)} instanced worlds -> {out_path}")
    for r in rows:
        if r[2] in ('raid', 'dungeon') or r[12]:
            print(f"{r[0]:5} {r[1][:26]:26} {r[2]:10} spawns {r[3]:5} bosses {r[6]:2}/{r[7]:2} spells {r[8]:4} enc {r[9]} quests {r[12]}/{r[13]} {r[14]}")


if __name__ == '__main__':
    main(*sys.argv[1:4])
