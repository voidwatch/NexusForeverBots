#!/usr/bin/env python3
"""Move or drop researched spawns of dungeon/raid creatures that ended up outside their instance.

Evidence: Creature2 descriptions carry an instance tag ([SK] Skullcano, [STL] Stormtalon's Lair, [KV] Kel Voreth, [GA] Genetic Archives,
[RMT] Redmoon Terror, ...). A tag belongs to an instance world when at least 80 % (and 5+) of the instance-pass and community spawns of its
creatures are in that world. jabbithole_match.py's main pass mapped zone names like "Skullcano Island" to an open world (its ALIAS table
was only used by the instance pass), so Bosun Octog and Mordechai Redmoon stood in world 22 and the Skullcano run had no Octog/Mordechai.
Pages whose zone is a capital or open zone (Thayd, Farside, a warplot) put dungeon and raid bosses into those worlds.
Rules, per research row (creature, world) of a tagged group-difficulty creature (groupValue 5+, so bounty boards with a tag stay) in a world that is not its tag's instance:
  - the source pages name a zone of the tag's instance (ALIAS) and the creature is a normal-mode one -> moved into the instance (same x, z)
  - otherwise (capital/open-zone page, veteran version) -> dropped
A creature of a group-size-20/40 difficulty (Creature2Difficulty.groupValue) whose tag names another instance and that stands in a
5-player instance is dropped (Laveka the Dark-Hearted 65997 [RMT], 26 M health, in Skullcano; the [SK] Laveka has two candidates,
24488 "Flavor" and 25698 "Vignette", so none is chosen).
    scripts/research/fix_instance_creatures.py [--write]     report: research/instance-creature-fix.txt
"""
import collections, os, re, subprocess, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tbl  # noqa: E402

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
SPAWNS = os.path.join(ROOT, 'data/spawns/research-spawns.tsv')
GENERIC = {'BOSS', 'TRASH', 'SPELL', 'UNUSED', 'NORMAL', 'VETERAN', 'CT', 'TEMPLATE', 'PH'}
ALIAS = {'skullcano-island': 1263, 'skullcano-golden-cave': 1263, 'skullcano-molten-cavern': 1263,
         'genetic-archives-act-1': 1462, 'genetic-archives-act-2': 1462, 'genetic-archives-act-3': 1462, 'the-datascape': 1333,
         'initialization-core-y-83': 3040, 'stormtalons-lair': 382, 'kel-voreth': 1336, 'ruins-of-kel-voreth': 1336,
         'sanctuary-of-the-swordmaiden': 1271, 'redmoon-terror': 3102}


def slug(s):
    return re.sub(r'[^a-z0-9]+', '-', s.lower()).strip('-')


desc, name, group = {}, {}, {}
diff = tbl.index('Creature2Difficulty')
for r in tbl.load('Creature2'):
    desc[r['ID']] = ''
    name[r['ID']] = tbl.text(r['localizedTextIdName']) or ''
    d = diff.get(r['creature2DifficultyId'])
    group[r['ID']] = d['groupValue'] if d else 0
for l in open(os.path.join(ROOT, 'research/creatures.tsv'), encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    if len(f) >= 8:
        desc[int(f[0])] = f[7]


def tag(c):
    m = re.match(r'\s*\[([A-Za-z0-9 ]+)\]', desc.get(c, ''))
    t = m.group(1).strip().upper() if m else None
    return None if t in GENERIC else t


rows = [l.rstrip('\n').split('\t') for l in open(SPAWNS) if l.strip()]
votes = collections.defaultdict(collections.Counter)
for f in rows:
    if f[4] == 'instance' and tag(int(f[0])):
        votes[tag(int(f[0]))][int(f[1])] += 1
env = dict(l.strip().split('=', 1) for l in open(os.path.join(ROOT, '.env')) if '=' in l and not l.startswith('#'))
q = subprocess.run(['docker', 'compose', 'exec', '-T', 'db', 'mariadb', f'-u{env["DB_USER"]}', f'-p{env["DB_PASSWORD"]}', 'nexus_forever_world', '-N', '-e',
                    'select creature, world from entity where id < 9000000'], capture_output=True, text=True, cwd=ROOT)
for l in q.stdout.splitlines():
    c, w = map(int, l.split())
    if tag(c):
        votes[tag(c)][w] += 1
worlds = {int(l.split('\t')[0]): int(l.split('\t')[1]) for l in open(os.path.join(ROOT, 'research/worlds.tsv'), encoding='utf-8')}
home = {}
for t, cnt in votes.items():
    w, n = cnt.most_common(1)[0]
    if worlds.get(w) in (1, 6, 9, 11) and n >= 5 and n >= 0.8 * sum(cnt.values()):
        home[t] = w
home.setdefault('RMT', 3102)   # Redmoon Terror: 40-player world 3102 (3032 has no spawns)

# page zones per (creature, world) from the matcher report
zones = collections.defaultdict(set)
for l in open(os.path.join(ROOT, 'research/match-report.txt'), encoding='utf-8'):
    m = re.match(r'matched\t\S+\tcreature (\d+) world (\d+) zone (.*?) (zone-match|name-only)$', l.rstrip('\n'))
    if m:
        zones[(int(m.group(1)), int(m.group(2)))].add(slug(m.group(3)))

out, report = [], []
small = {w for w, t in worlds.items() if t in (1, 6, 11)}
for f in rows:
    c, w = int(f[0]), int(f[1])
    t = tag(c)
    if t in home and home[t] != w and worlds.get(w) not in (1, 6, 9, 11) and group.get(c, 0) >= 5:
        target = home[t]
        z = zones.get((c, w), set())
        veteran = 'veteran' in desc[c].lower()
        if not veteran and any(ALIAS.get(s) == target for s in z):
            report.append(f'moved\t{c}\t{name[c]}\t{w} -> {target}\t{f[2]},{f[3]}\tpage zone {",".join(sorted(z))}\t{desc[c][:60]}')
            out.append([f[0], str(target), f[2], f[3], f[4] + '+instance-fix'])
        else:
            report.append(f'dropped\t{c}\t{name[c]}\tworld {w}\t{f[2]},{f[3]}\tpage zone {",".join(sorted(z)) or "-"}{" (veteran)" if veteran else ""}\t{desc[c][:60]}')
        continue
    if w in small and group.get(c, 0) >= 20 and t in home and home[t] != w:
        # a raid creature of another instance in a 5-player instance (name-only match): dropped, the same-name 5-player creature is not chosen
        same = [k for k, n in name.items() if n == name[c] and k != c and group.get(k, 0) <= 5 and home.get(tag(k)) == w]
        report.append(f'dropped\t{c}\t{name[c]}\tworld {w}\t{f[2]},{f[3]}\tgroup {group[c]} creature of instance {home[t]} in a 5-player instance; '
                      f'same-name candidates here {same}\t{desc[c][:50]}')
        continue
    out.append(f)

open(os.path.join(ROOT, 'research/instance-creature-fix.txt'), 'w').write('\n'.join(report) + '\n')
print(f'tags with a home instance: {len(home)}; rows {len(rows)} -> {len(out)}')
print(collections.Counter(r.split('\t')[0] for r in report))
for r in [r for r in report if not r.startswith('kept')][:30]:
    print(r[:180])
if '--write' in sys.argv:
    open(SPAWNS, 'w').write('\n'.join('\t'.join(f) for f in out) + '\n')
    print('written', SPAWNS)
