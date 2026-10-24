#!/usr/bin/env python3
"""Move researched spawns out of worlds that have no map file into the world their coordinates belong to.

Jabbithole pages give a zone name, jabbithole_match.py turns it into a world; for some zones it chose a world without map data
(3091 ShiphandExileArkship for Mondo's Laboratory, which is in 1634 DominionArkShipTutorial: quest 6993/6994 objective 52246 has its
indicator 41283 in world 1634 at the same coordinates as creatures 33220/52245/54243). Bots cannot enter a world without a map, so
those quests stalled. Rule: a spawn in a map-less world whose (x, z) has at least 3 WorldLocation2 rows of other worlds within 60 m,
at least 80 % of them in one world that has a map, moves to that world. The frame must fit the world as a whole: at least 30 % of the
world's spawns point at the same mapped world. Second rule (direct evidence): a spawn moves when a quest objective targeting that
creature (data, or a $(creature=N) macro in its text) has an indicator WorldLocation2 in a mapped world within 60 m of it (Mondo 54243 at
691,-928 in 3091; objective 16048's indicator 41283 is in 1634 at 708,-917). Everything else stays.

    scripts/research/remap_mapless.py [--write]      (dry run without --write; writes data/spawns/research-spawns.tsv)
"""
import collections, math, os, struct, subprocess, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
TBL = os.path.join(ROOT, 'data', 'tbl')
SPAWNS = os.path.join(ROOT, 'data', 'spawns', 'research-spawns.tsv')


def table(name):
    d = open(os.path.join(TBL, name + '.tbl'), 'rb').read()
    hdr = struct.unpack('<IIQQQQQQQQQQQ', d[:96])
    return d, hdr[4], hdr[7], hdr[9]


def world_locations():
    d, rs, rc, ro = table('WorldLocation2')
    out = []
    for r in range(rc):
        p = 96 + ro + r * rs
        v = struct.unpack('<I9fII', d[p:p + 48])
        out.append((v[3], v[5], v[10]))
    return out


def objective_indicators():
    """creature id -> [(world, x, z)] of the indicators of quest objectives that target it."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import re
    from spellinfo import load, text
    wl = {}
    d, rs, rc, ro = table('WorldLocation2')
    for r in range(rc):
        p = 96 + ro + r * rs
        v = struct.unpack('<I9fII', d[p:p + 48])
        wl[v[0]] = (v[10], v[3], v[5])
    out = collections.defaultdict(list)
    for o in load('QuestObjective'):
        creatures = set()
        if o['type'] in (2, 3, 5, 9, 12, 21) and o['data']:
            creatures.add(o['data'])
        creatures |= {int(c) for c in re.findall(r'\$[a-z]?\([Cc]reature=(\d+)\)', text(o['localizedTextIdFull']) or '')}
        for k in ('worldLocationsIdIndicator00', 'worldLocationsIdIndicator01', 'worldLocationsIdIndicator02', 'worldLocationsIdIndicator03'):
            if o[k] in wl:
                for c in creatures:
                    out[c].append(wl[o[k]])
    return out


def worlds_with_map():
    """World ids with a map file, from the world server: scripts/gt.sh gt worlds (lines 'world N type T ... | map yes ...')."""
    out = subprocess.run([os.path.join(ROOT, 'scripts', 'gt.sh'), 'gt', 'worlds'], capture_output=True, text=True).stdout
    mapped = {int(l.split()[1]) for l in out.splitlines() if l.startswith('world ') and '| map yes' in l}
    # only open worlds (type 0) are moved: dungeon, adventure and raid worlds have their own coordinate frames, overlaps are coincidence
    open_mapless = {int(l.split()[1]) for l in out.splitlines() if l.startswith('world ') and l.split()[3] == '0' and '| map NO' in l}
    return mapped, open_mapless


def main():
    write = '--write' in sys.argv
    mapped, open_mapless = worlds_with_map()
    if len(mapped) < 10:
        sys.exit(f'only {len(mapped)} worlds with a map from gt worlds; is the world server up?')
    grid = collections.defaultdict(list)
    for x, z, w in world_locations():
        grid[(round(x / 100), round(z / 100))].append((x, z, w))
    rows = [l.rstrip('\n').split('\t') for l in open(SPAWNS)]
    moved = collections.Counter()
    kept = collections.Counter()
    candidates = []
    indicators = objective_indicators()
    direct = 0
    for r in rows:
        if len(r) < 4 or not r[1].isdigit() or int(r[1]) not in open_mapless:
            continue
        w, x, z = int(r[1]), float(r[2]), float(r[3])
        hit = [iw for iw, ix, iz in indicators.get(int(r[0]), []) if iw in mapped and math.hypot(ix - x, iz - z) < 60]
        if hit:
            moved[(w, hit[0])] += 1
            direct += 1
            r[1] = str(hit[0])
            if len(r) > 4:
                r[4] = r[4] + '+remap-indicator'
            continue
        near = collections.Counter()
        gx, gz = round(x / 100), round(z / 100)
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for lx, lz, lw in grid[(gx + dx, gz + dz)]:
                    if lw != w and math.hypot(lx - x, lz - z) < 60:
                        near[lw] += 1
        total = sum(near.values())
        best, n = near.most_common(1)[0] if near else (None, 0)
        if total >= 3 and best in mapped and n >= 0.8 * total:
            candidates.append((r, w, best))
        else:
            kept[w] += 1
    # the frame has to match for the world as a whole: at least 30 % of its spawns point at the same mapped world
    # (3091 -> 1634: 21 of 61; scattered hits from 1629 PublicEventIsland or 1163 CombatSandbox are coincidence and stay)
    per_world = collections.Counter(w for _, w, _ in candidates)
    per_target = collections.Counter((w, b) for _, w, b in candidates)
    size = collections.Counter(int(r[1]) for r in rows if len(r) >= 4 and r[1].isdigit())
    for r, w, best in candidates:
        if per_target[(w, best)] >= 0.3 * size[w]:
            moved[(w, best)] += 1
            r[1] = str(best)
            if len(r) > 4:
                r[4] = r[4] + '+remap'
        else:
            kept[w] += 1
    for (w, b), n in moved.most_common(15):
        print(f'moved {n:5d} spawns world {w} -> {b}')
    print(f'{direct} moved by an objective indicator of their creature')
    print('kept in map-less worlds:', ', '.join(f'{w}:{n}' for w, n in kept.most_common(10)))
    if write:
        with open(SPAWNS, 'w') as f:
            for r in rows:
                f.write('\t'.join(r) + '\n')
        print('written', SPAWNS)


if __name__ == '__main__':
    main()
