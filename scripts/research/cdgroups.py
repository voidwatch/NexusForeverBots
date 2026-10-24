#!/usr/bin/env python3
"""ModifySpellCooldown (effect 73) modes 2/3: which table does DataBits01 name? Tests Spell4GroupList id, spell group id (member of a
Spell4GroupList row) and SpellCoolDown id (Spell4.spellCoolDownId00-02) and prints how many player spells each reading reaches."""
import sys, collections
sys.path.insert(0, __import__('os').path.dirname(__file__))
from spellinfo import *
gl = {r['ID']: [r['spellGroupId%02d' % i] for i in range(32) if 'spellGroupId%02d' % i in r and r['spellGroupId%02d' % i]] for r in load('Spell4GroupList')}
sc = {r['ID'] for r in load('SpellCoolDown')}
def cls(i): return SB.get(S4[i]['spell4BaseIdBaseSpell'], {}).get('classIdPlayer', 0)
bylist = collections.defaultdict(set); bygroup = collections.defaultdict(set); bycd = collections.defaultdict(set)
for i, r in S4.items():
    if not cls(i): continue
    nm = name(i)
    if 'DEP' in nm or 'TEST' in nm: continue
    b = r['spell4BaseIdBaseSpell']
    if r['spell4GroupListId']: bylist[r['spell4GroupListId']].add(b)
    for g in gl.get(r['spell4GroupListId'], []): bygroup[g].add(b)
    for k in ('spellCoolDownId00', 'spellCoolDownId01', 'spellCoolDownId02'):
        if r[k]: bycd[r[k]].add(b)
seen = set()
for e in load('Spell4Effects'):
    if e['effectType'] != 73 or e['dataBits00'] not in (2, 3): continue
    k = (e['dataBits00'], e['dataBits01'])
    if k in seen: continue
    seen.add(k)
    v = e['dataBits01']
    def ex(s): return [text(SB[b]['localizedTextIdName'])[:18] for b in list(s)[:4]]
    print(f"mode {k[0]} id {v:4} by {name(e['spellId'])[:48]:48} | list {len(bylist[v]):3} group {len(bygroup[v]):3} {ex(bygroup[v])} cd {len(bycd[v]):2}{'*' if v in sc else ' '} {ex(bycd[v])}")
