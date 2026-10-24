#!/usr/bin/env python3
"""synthwhy.py [synthetic-objectives.tsv] [out.tsv]: for each objective the server credits synthetically (QuestCoverage.NoPath, file written by the
world to /cache/synthetic-objectives.tsv), look in the game tables for a real path:
  qao     a spell with a QuestAdvanceObjective effect (0x2A, quest + objective index) and where that spell can come from
          (creature activation, item use, quest pushed item, proxy chains from those, AddSpell grants)
  item    CollectItem: GiveItemToPlayer effects for the item (and their sources), quest pushed items, vendors are not in the tables
Prints a summary by objective type (<= 30 lines) and writes one line per objective to out.tsv."""
import sys, os, collections
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load, text

src = sys.argv[1] if len(sys.argv) > 1 else 'research/synth/synthetic-objectives.tsv'
out = sys.argv[2] if len(sys.argv) > 2 else 'research/synth/synthwhy.tsv'
rows = [l.rstrip('\n').split('\t') for l in open(src) if l.strip()]

E = load('Spell4Effects')
S4 = {r['ID']: r for r in load('Spell4')}
PROXY = {0x13, 0x19, 0x1A, 0x5E, 0x60, 0x6F}
qao = collections.defaultdict(set)          # (quest, index) -> spells
gives = collections.defaultdict(set)        # item -> spells (GiveItemToPlayer)
cast_by = collections.defaultdict(set)      # spell -> spells that proxy it
addspell = collections.defaultdict(set)     # spell4 base -> spells that add it
for e in E:
    t = e['effectType']
    if t == 0x2A:
        qao[(e['dataBits00'], e['dataBits01'])].add(e['spellId'])
    elif t == 0x2B:
        gives[e['dataBits00']].add(e['spellId'])
    elif t in PROXY and e['dataBits00']:
        cast_by[e['dataBits00']].add(e['spellId'])
    elif t == 0x39 and e['dataBits00']:
        addspell[e['dataBits00']].add(e['spellId'])

roots = collections.defaultdict(set)        # spell -> how a player gets to cast it
for c in load('Creature2'):
    for k in ('spell4IdActivate00', 'spell4IdActivate01', 'spell4IdActivate02', 'spell4IdActivate03'):
        if c[k]:
            roots[c[k]].add(f'activate:{c["ID"]}')
ISP = {r['ID']: r for r in load('ItemSpecial')}
item_spell = {}
for i in load('Item2'):
    sp = ISP.get(i['itemSpecialId00'])
    if sp and sp['spell4IdOnActivate']:
        roots[sp['spell4IdOnActivate']].add(f'item:{i["ID"]}')
        item_spell[i['ID']] = sp['spell4IdOnActivate']
Q = {q['ID']: q for q in load('Quest2')}
pushed = collections.defaultdict(set)
for q in Q.values():
    for k in ('pushed_itemId0', 'pushed_itemId01', 'pushed_itemId02', 'pushed_itemId03', 'pushed_itemId04', 'pushed_itemId05'):
        if q[k]:
            pushed[q[k]].add(q['ID'])
for base, adders in addspell.items():
    for s in S4.values():
        if s['spell4BaseIdBaseSpell'] == base:
            roots[s['ID']].add('addspell')


def origin(spell, depth=0, seen=None):
    seen = seen or set()
    if spell in seen or depth > 4:
        return set()
    seen.add(spell)
    found = set(roots.get(spell, ()))
    for parent in cast_by.get(spell, ()):
        found |= origin(parent, depth + 1, seen)
    return found


def describe(srcs, quest):
    kinds = set()
    for s in srcs:
        kind, _, val = s.partition(':')
        if kind == 'item' and quest in pushed.get(int(val), ()):
            kinds.add('pushed-item')
        else:
            kinds.add(kind)
    return kinds


TYPES = {4: 'CollectItem', 5: 'ActivateEntity', 10: 'Unknown10', 11: 'SpellSuccess', 12: 'SucceedCSI', 14: 'ActivateTGChecklist', 15: 'Unknown15',
         17: 'EnterZone', 22: 'EnterArea', 25: 'CompleteEvent', 31: 'Unknown31', 32: 'VirtualCollect'}
summary = collections.defaultdict(collections.Counter)
with open(out, 'w') as f:
    for quest, index, obj, typ, data in rows:
        quest, index, obj, typ, data = int(quest), int(index), int(obj), int(typ), int(data)
        spells = qao.get((quest, index), set())
        kinds = set()
        for s in spells:
            kinds |= describe(origin(s), quest) or {'qao-no-source'}
        verdict = 'qao:' + '+'.join(sorted(kinds)) if spells else 'no-qao'
        if typ == 4:
            g = gives.get(data, set())
            ik = set()
            for s in g:
                ik |= describe(origin(s), quest) or {'give-no-source'}
            if quest in pushed.get(data, ()):
                ik.add('pushed')
            verdict += ' item:' + ('+'.join(sorted(ik)) if ik else 'none')
        summary[typ][verdict] += 1
        f.write(f'{quest}\t{index}\t{obj}\t{typ}\t{data}\t{verdict}\t{",".join(map(str, sorted(spells)))}\n')

lines = 0
for typ, c in sorted(summary.items(), key=lambda kv: -sum(kv[1].values())):
    if lines >= 30:
        break
    print(f'{TYPES.get(typ, typ)} ({typ}) {sum(c.values())}: ' + ', '.join(f'{k} {v}' for k, v in c.most_common(5)))
    lines += 1
