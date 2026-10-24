#!/usr/bin/env python3
"""Everything in the game tables that nothing leads to. Writes docs/ORPHANS.md (counts, examples, how it was decided) and docs/orphans/*.tsv (full lists, git-ignored, not shipped).
Reference = another table has a field of the matching kind holding the id (generic scan by field name), a spawn row, a scraped drop, or an effect payload.
Run: python3 scripts/research/orphans.py   (needs data/tbl, research/db-spawns.tsv, data/research/creature-drops.tsv)"""
import os, re, collections, tbl
ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
DOCS = os.path.join(ROOT, 'docs')
os.makedirs(os.path.join(DOCS, 'orphans'), exist_ok=True)
names = sorted(f[:-4] for f in os.listdir(os.path.join(ROOT, 'data', 'tbl')) if f.endswith('.tbl'))
T = {}
for n in names:
    try:
        r = tbl.load(n)
    except Exception:
        continue
    if r:
        T[n] = r

def idof(row):
    return row.get('ID', row.get('Id', row.get('id')))

def refs(kind_re, exclude=(), skip_tables=(), only_tables=None):
    """values of every field whose name matches, by (table, field), minus excluded ones"""
    out = collections.defaultdict(set)
    rx = re.compile(kind_re, re.I)
    for n, rows in T.items():
        if n in skip_tables or (only_tables and n not in only_tables):
            continue
        for k in rows[0]:
            if k in ('ID',) or not rx.search(k) or f'{n}.{k}' in exclude:
                continue
            for r in rows:
                v = r.get(k)
                if isinstance(v, int) and v:
                    out[v].add(f'{n}.{k}')
    return out

def spawns():
    c, w = set(), set()
    with open(os.path.join(ROOT, 'research', 'db-spawns.tsv')) as f:
        for line in f:
            p = line.split('\t')
            if len(p) > 1:
                c.add(int(p[0])); w.add(int(p[1]))
    return c, w

spawn_creatures, spawn_worlds = spawns()
drops = collections.defaultdict(set)
with open(os.path.join(ROOT, 'data', 'research', 'creature-drops.tsv')) as f:
    for line in f:
        p = line.rstrip('\n').split('\t')
        if len(p) > 3 and p[3].isdigit():
            drops[int(p[3])].add(p[0])

def text(i):
    try:
        return tbl.text(i) if i else ''
    except Exception:
        return ''

report = []
def section(title, how, rows, header, fname):
    with open(os.path.join(DOCS, 'orphans', fname), 'w') as f:
        f.write('\t'.join(header) + '\n')
        for r in rows:
            f.write('\t'.join(str(x) for x in r) + '\n')
    report.append(f'## {title}: {len(rows)}\n\n{how}\n\nFull list: written locally by the script to `docs/orphans/{fname}` (git-ignored, not shipped). First 15:\n')
    for r in rows[:15]:
        report.append('- ' + ' | '.join(str(x) for x in r))
    report.append('')

# worlds -----------------------------------------------------------------------------------------------------------------
wl = {r['ID']: r for r in T['WorldLocation2']}
wref = refs(r'^worldid|worldid\d*$|plug', exclude={'WorldLocation2.worldId'}, skip_tables={'World'})
loc_refs = refs(r'worldlocation2id', skip_tables={'WorldLocation2'})
world_links = collections.defaultdict(set)
for w, s in wref.items():
    world_links[w] |= s
for lid, s in loc_refs.items():
    if lid in wl:
        world_links[wl[lid]['worldId']] |= {'via ' + x for x in s}
for w in spawn_worlds:
    world_links[w].add('spawn')
rows = []
for r in T['World']:
    if r['ID'] not in world_links:
        rows.append((r['ID'], r['assetPath'], text(r['localizedTextIdName'])))
section('Worlds nothing leads to', 'No creature spawn, no table field with a world id (portal, socket, matching map, event, housing, path episode...) and no WorldLocation2 reference from a quest, taxi node, bind point, challenge, path mission or similar.',
        rows, ['world', 'asset', 'name'], 'worlds.tsv')

# zones ------------------------------------------------------------------------------------------------------------------
zref = refs(r'worldzoneid|^zoneid|mapzoneid', exclude={'WorldLocation2.worldZoneId'}, skip_tables={'WorldZone'})
zone_links = collections.defaultdict(set)
for z, s in zref.items():
    zone_links[z] |= s
for lid, s in loc_refs.items():
    if lid in wl and wl[lid]['worldZoneId']:
        zone_links[wl[lid]['worldZoneId']] |= {'via ' + x for x in s}
parents = {r['parentZoneId'] for r in T['WorldZone'] if r['parentZoneId']}
rows = [(r['ID'], text(r['localizedTextIdName']), r['parentZoneId']) for r in T['WorldZone'] if r['ID'] not in zone_links and r['ID'] not in parents]
section('Zones nothing leads to', 'No table field with a zone id other than the zone tree itself, no WorldLocation2 reference from game content, and no child zone.', rows, ['zone', 'name', 'parent'], 'zones.tsv')

# creatures --------------------------------------------------------------------------------------------------------------
cref = refs(r'creature2id|creatureid', skip_tables={'Creature2', 'Creature2Action'})
giver = set()
for r in T['Creature2']:
    for k, v in r.items():
        if re.match(r'questId(Given|Receive)', k) and v:
            giver.add(r['ID'])
        if re.match(r'spell4IdActivate', k) and v:
            giver.add(r['ID'])
rows = []
skipped_cinematic = 0
for r in T['Creature2']:
    i = r['ID']
    if i in spawn_creatures or i in giver or i in cref or any(i in v for v in ()):
        continue
    if i in drops or not (r.get('description') or text(r['localizedTextIdName'])):
        continue
    rows.append((i, text(r['localizedTextIdName']) or r.get('description'), r['creature2TierId'], r['creature2ArcheTypeId']))
section('Creatures nothing leads to', 'No spawn row, not a quest giver or receiver, no activation spell, no table field referencing it (target groups, path missions, events, summon tables...); creatures without any name or description are left out (internal models).', rows, ['creature', 'name', 'tier', 'archetype'], 'creatures.tsv')

# quests -----------------------------------------------------------------------------------------------------------------
qref = refs(r'quest2id|questid|^quest_|preq_quest', skip_tables={'QuestObjective', 'QuestCategory', 'QuestDirection', 'QuestDirectionEntry', 'QuestHub', 'QuestReward'})
start = refs(r'quest2id|questid', skip_tables={'Quest2', 'QuestObjective', 'QuestCategory', 'QuestDirection', 'QuestHub'})
rec_only = set()
for r in T['Creature2']:
    for k, v in r.items():
        if re.match(r'questIdGiven', k) and v:
            start[v].add('Creature2.giver')
rows = [(r['ID'], text(r['localizedTextIdTitle']), r['worldZoneId'], r['preq_level']) for r in T['Quest2'] if r['ID'] not in start]
section('Quests with no way to start', 'Not in any creature giver slot and no other table (item activation, path mission, achievement, event, communicator...) names it. Quests that are only follow-ups (preq_quest) are listed too: they start through the chain, see below.', rows, ['quest', 'title', 'zone', 'level'], 'quests-no-start.tsv')
chained = {v for r in T['Quest2'] for k, v in r.items() if k.startswith('preq_quest') and v}
rows2 = [r for r in rows if r[0] not in chained]
section('Quests with no way to start and not a follow-up', 'The subset of the list above that no other quest requires either: nothing at all leads to them.', rows2, ['quest', 'title', 'zone', 'level'], 'quests-unreachable.tsv')

# spells -----------------------------------------------------------------------------------------------------------------
sref = refs(r'spell4id|spellid|spell4base', skip_tables={'Spell4Effects', 'Spell4', 'Spell4Base'})
base_of = {r['ID']: r['spell4BaseIdBaseSpell'] for r in T['Spell4']}
for e in T['Spell4Effects']:
    t = e['effectType']
    if t in (0x1A, 0x13, 0x19, 0x5E, 0x60, 0x15, 0x6, 0x24, 0x62) and e['dataBits00']:
        sref[e['dataBits00']].add('effect payload')
    if t == 0x0E and e['dataBits01']:
        sref[e['dataBits01']].add('proc')
for s4 in T['Spell4']:
    for k in s4:
        if re.match(r'spell4IdCastEvent|spell4IdChain|spell4IdOn', k) and s4[k]:
            sref[s4[k]].add('Spell4.' + k)
base_refd = {b for b in sref}
rows = []
for s4 in T['Spell4']:
    i = s4['ID']
    if i in sref or s4['spell4BaseIdBaseSpell'] in sref:
        continue
    d = s4.get('description') or ''
    if not d:
        continue
    rows.append((i, d, s4['spell4BaseIdBaseSpell']))
section('Spells nothing refers to', 'Neither the spell nor its base spell is named by an item, creature, class action set, path, tradeskill, other spell effect (proxy, proc, summon), cast event or chain. Spells without a designer description are left out.', rows, ['spell4', 'description', 'base'], 'spells.tsv')

# items ------------------------------------------------------------------------------------------------------------------
iref = refs(r'item2id|itemid\d*$|^item_|pushed_itemid|virtualitemid', exclude=set(), skip_tables={'Item2', 'ItemDisplay', 'ItemDisplaySourceEntry', 'Creature2DisplayInfo', 'Creature2ModelInfo', 'CharacterCreationArmorSet'})
rows = []
for r in T['Item2']:
    i = r['ID']
    if i in iref or i in drops:
        continue
    n = text(r.get('localizedTextIdName', 0)) if 'localizedTextIdName' in r else ''
    if not n:
        continue
    rows.append((i, n, r['requiredLevel'], r['itemQualityId']))
section('Items nothing leads to', 'Not a quest reward, vendor item, schematic output, loot spell result, scraped creature drop, currency item, account item or any other table reference; unnamed items are left out. Randomly generated loot (level based drops) can still produce them: this is the list of items with no *specific* source.', rows, ['item', 'name', 'level', 'quality'], 'items.tsv')

with open(os.path.join(DOCS, 'ORPHANS.md'), 'w') as f:
    f.write('# Orphans: things in the game tables that nothing leads to\n\nGenerated by `scripts/research/orphans.py` (offline, from data/tbl and the imported spawn list). Counts are of the 2014 table set; many are cut content or client-only data.\n\n')
    f.write('\n'.join(report))
print('\n'.join(l for l in report if l.startswith('## ')))
