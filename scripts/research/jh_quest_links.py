#!/usr/bin/env python3
"""Quest giver/receiver links that the archived Jabbithole quest pages show and the client tables do not (Creature2.QuestIdGiven/Receive).

Input : research/decode/quests/jh-quests.tsv (jh_quests.py parse), research/jh-zone-cdx.txt (zone slug -> Jabbithole zone number),
        research/db-spawns.tsv (creature world x y z id), research/zone-worlds.tsv.
Output: data/research/quest-links.tsv (committed, loaded by GlobalQuestManager): quest, role (giver|receiver), creature, source, confidence
        research/decode/quests/quest-links-report.tsv (ambiguous / unresolved links with the reason). Prints <= 30 lines.
Resolution of an NPC link "name-<jh zone>": Creature2 rows with that name; one row -> it; several -> those whose description names the
Jabbithole zone; several -> the one whose description names the quest's faction (Exile/Dominion); still several -> those with a database spawn in a world of that zone; exactly one left -> placed (likely), else ambiguous.
Cut and test content is never linked (quest zone or turn-in location in Halon Ring/Murkmire, unnamed test maps).
"""
import os, re, sys, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text
ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
R = os.path.join(ROOT, 'research')
def slug(n): return re.sub(r'[^a-z0-9]+', '-', n.lower().replace("'", '')).strip('-')

jhzone = {}
for l in open(f'{R}/jh-zone-cdx.txt'):
    m = re.search(r'/zones/([a-z0-9-]+)-(\d+)\b', l)
    if m:
        jhzone.setdefault(m.group(2), m.group(1))
zone_worlds = collections.defaultdict(set)
for l in open(f'{R}/zone-worlds.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    zone_worlds[slug(f[1])].add(int(f[2]))
spawn_worlds = collections.defaultdict(set)
for l in open(f'{R}/db-spawns.tsv'):
    c, w = l.split()[:2]
    spawn_worlds[int(c)].add(int(w))
cr = {c['ID']: c for c in load('Creature2')}
byname = collections.defaultdict(list)
for c in cr.values():
    n = text(c['localizedTextIdName'])
    if n:
        byname[slug(n)].append(c['ID'])
give, recv = collections.defaultdict(set), collections.defaultdict(set)
for c in cr.values():
    for k, v in c.items():
        if k.startswith('questIdGiven') and v: give[v].add(c['ID'])
        if k.startswith('questIdReceive') and v: recv[v].add(c['ID'])
q2 = {q['ID']: q for q in load('Quest2')}
loc = {l['ID']: l for l in load('WorldLocation2')}
zones = {z['ID']: z for z in load('WorldZone')}
worlds = {w['ID']: w for w in load('World')}
def cut(zid):
    for _ in range(8):
        if zid in (207, 866): return True
        if not zid or zid not in zones: return False
        zid = zones[zid]['parentZoneId']
    return False
def excluded(q):
    L = loc.get(q['WorldLocation2IdReceiver'])
    if cut(q['worldZoneId']) or (L and cut(L['worldZoneId'])): return 'cut'
    if L and L['worldId'] in worlds and not worlds[L['worldId']]['localizedTextIdName'] and any(t in worlds[L['worldId']]['assetPath'].lower() for t in ('test', 'sandbox')): return 'test-map'
    return None

def resolve(npc, faction):
    m = re.match(r'(.*)-(\d+)$', npc)
    name, zn = (m.group(1), m.group(2)) if m else (npc, '')
    cands = byname.get(name, [])
    if len(cands) <= 1:
        return cands, 'name'
    zslug = jhzone.get(zn, '')
    zwords = zslug.replace('-', ' ')
    byz = [c for c in cands if zwords and zwords in (cr[c]['description'] or '').lower().replace('-', ' ')]
    if not byz and zslug.count('-') >= 1 and len(zslug.split('-')[-1]) > 4:
        # "southern-grimvault" vs description "... - Grimvault - ...": the last word of the zone name
        byz = [c for c in cands if re.search(r'\b' + zslug.split('-')[-1] + r'\b', (cr[c]['description'] or '').lower())]
    if len(byz) == 1:
        return byz, 'name+zone-description'
    pool = byz or cands
    side = {0: 'exile', 1: 'dominion'}.get(faction)
    if side and len(pool) > 1:
        byf = [c for c in pool if side in (cr[c]['description'] or '').lower()]
        if len(byf) == 1:
            return byf, 'name+zone+faction-description'
        pool = byf or pool
    ws = zone_worlds.get(zslug, set())
    bysp = [c for c in pool if spawn_worlds.get(c, set()) & ws]
    if len(bysp) == 1:
        return bysp, 'name+spawned-in-zone-world'
    return (bysp or pool), 'ambiguous'

rows, report, stats = [], [], collections.Counter()
for l in list(open(f'{R}/decode/quests/jh-quests.tsv'))[1:]:
    f = l.rstrip('\n').split('\t')
    q = int(f[0])
    if q not in q2:
        continue
    ex = excluded(q2[q])
    for role, col, known in (('giver', 3, give[q]), ('receiver', 4, recv[q])):
        for e in (f[col].split('|') if f[col] else []):
            if not e.startswith('npc:'):
                continue
            ids, how = resolve(e[4:], q2[q]['questPlayerFactionEnum'])
            if ex:
                stats[f'{role} skipped {ex}'] += 1
                continue
            if set(ids) & known:
                stats[f'{role} already in tables'] += 1
                continue
            if how == 'ambiguous' or not ids:
                stats[f'{role} {"ambiguous" if ids else "unknown name"}'] += 1
                report.append(f'{q}\t{role}\t{e}\t{how}\t{ids[:10]}')
                continue
            rows.append(f'{q}\t{role}\t{ids[0]}\tjabbithole-quest-page\tlikely ({how})')
            stats[f'{role} linked ({how})'] += 1
os.makedirs(f'{ROOT}/data/research', exist_ok=True)
with open(f'{ROOT}/data/research/quest-links.tsv', 'w') as o:
    o.write('# quest\trole\tcreature\tsource\tconfidence  (scripts/research/jh_quest_links.py; giver/receiver links shown on archived Jabbithole quest pages, absent from Creature2)\n')
    o.write('\n'.join(rows) + '\n')
open(f'{R}/decode/quests/quest-links-report.tsv', 'w').write('\n'.join(report) + '\n')
for k, n in sorted(stats.items()):
    print(n, k)
