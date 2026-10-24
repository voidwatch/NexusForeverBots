#!/usr/bin/env python3
"""qobj.py <quest ids...>: title, zone and objectives (id, type, data, count, flags) of each quest. Capped one line per quest."""
import sys
sys.path.insert(0, __import__('os').path.dirname(__file__))
from spellinfo import *
Q = {q['ID']: q for q in load('Quest2')}; O = {o['ID']: o for o in load('QuestObjective')}
for a in sys.argv[1:]:
    q = Q[int(a)]
    ids = [q[k] for k in ('objective0', 'objective01', 'objective02', 'objective03', 'objective04', 'objective05') if q[k]]
    print(a, text(q['localizedTextIdTitle'])[:26], 'z%d' % q['worldZoneId'], ' '.join(f"{o}:t{O[o]['type']}/d{O[o]['data']}/x{O[o]['count']}/f{O[o]['flags']:x}" for o in ids if o in O))
