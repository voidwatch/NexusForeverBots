#!/usr/bin/env python3
"""Parse cached Jabbithole NPC pages and match them to quest creatures that have no spawn.

Input : research/jh-raw/*.html.gz, research/needed-creatures.tsv, research/zones.tsv, research/zone-worlds.tsv
Output: data/spawns/research-spawns.tsv  (creature, world, x, z, source)  -> gt importspawns
        research/match-report.txt     (matched / ambiguous / unmatched, per creature)
Placement rule: same name; the page zone must map to a world; if the candidate has a level range the page level must be within 3;
candidates whose description or quest zones name the page zone win. More than 3 equal candidates = ambiguous, not placed.
"""
import glob, gzip, html, os, re, collections

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'research')
MAX_ROWS = 60

def slug(name):
    return re.sub(r'[^a-z0-9]+', '-', html.unescape(name).lower().replace("'", '')).strip('-')

zone_names = {}      # zone id -> name
for l in open(f'{ROOT}/zones.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    zone_names[int(f[0])] = f[1]
zone_world = collections.defaultdict(collections.Counter)   # zone name slug -> world -> locations
for l in open(f'{ROOT}/zone-worlds.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    zone_world[slug(f[1])][int(f[2])] += int(f[3])

# worlds with spawn data or maps are the open worlds; prefer them when a zone name exists in several worlds
# open world continents: type 0 worlds on the real continent and pocket maps (the zone names of adventures, e.g. Farside, exist in both)
OPEN = {int(l.split('\t')[0]) for l in open(f'{ROOT}/worlds.tsv', encoding='utf-8')
        if l.split('\t')[1] == '0' and any(k in l.split('\t')[3] for k in ('Western', 'Eastern', 'NewCentral', 'Farsidesque', 'HalonRingNew', 'Arcterra', 'PCPLevianBay', 'PocketCaps', 'GrimvaultCore', 'NewPlayerExperience', 'ArkShipTutorial'))}

world_name = {}
for l in open(f'{ROOT}/worlds.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    if len(f) > 2 and f[2]:
        world_name.setdefault(slug(f[2]), int(f[0]))

ZONE_OVERRIDE = {'farside': 1421}   # the open world zone has no world location of its own, the adventure of the same name has

ALIAS = {'skullcano-island': 1263, 'skullcano-golden-cave': 1263, 'skullcano-molten-cavern': 1263,
         'genetic-archives-act-1': 1462, 'genetic-archives-act-2': 1462, 'genetic-archives-act-3': 1462, 'the-datascape': 1333, 'initialization-core-y-83': 3040}

def world_for(zone):
    # instance zones first: "Skullcano Island" also names open-world locations and voted for world 22 (scripts/research/fix_instance_creatures.py)
    if slug(zone) in ALIAS:
        return ALIAS[slug(zone)]
    if slug(zone) in ZONE_OVERRIDE:
        return ZONE_OVERRIDE[slug(zone)]
    if slug(zone) in world_name and not zone_world.get(slug(zone)):
        return world_name[slug(zone)]
    c = zone_world.get(slug(zone))
    if not c:
        return None
    best = [w for w, _ in c.most_common() if w in OPEN]
    return best[0] if best else c.most_common(1)[0][0]

need = collections.defaultdict(list)
indicators = {}   # creature id -> quest objective indicator location ids (placement fallback)
needed_roles = {}
for l in open(f'{ROOT}/needed-creatures.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    if len(f) < 9 or not f[1]:
        continue
    zones = {slug(zone_names.get(int(z), '')) for z in f[4].split(',') if z}
    if len(f) > 10 and f[10]:
        indicators[int(f[0])] = [int(x) for x in f[10].split(',')]
    needed_roles[int(f[0])] = f[2]
    need[slug(f[1])].append(dict(id=int(f[0]), roles=f[2], zones=zones, lo=int(f[7] or 0), hi=int(f[8] or 0), desc=slug(f[9]) if len(f) > 9 else ''))

# nearest existing open-world spawn votes for the world when the zone name does not map to one
CELL = 150
grid = collections.defaultdict(list)
for l in open(f'{ROOT}/existing-spawns.tsv'):
    w, x, z = l.split()
    grid[(int(float(x)) // CELL, int(float(z)) // CELL)].append((int(w), float(x), float(z)))

INSTANCE_TYPES = {1, 6, 9, 11}
world_type = {}
for l in open(f'{ROOT}/worlds.tsv', encoding='utf-8'):
    f = l.rstrip('\n').split('\t')
    world_type[int(f[0])] = int(f[1])
# a level for pages without one: the median of the pages of the same world
page_levels = collections.defaultdict(list)
for path in glob.glob(f'{ROOT}/jh-raw/*.html.gz'):
    if path.endswith('-veteran.html.gz'):
        continue
    try:
        h = gzip.open(path).read().decode('utf8', 'replace')
    except Exception:
        continue
    zm = re.search(r'Found in:.*?<a [^>]*>(.*?)</a>', h, re.S)
    lm = re.search(r'Level:\s*(\d+)', h)
    if zm and lm:
        w = world_for(html.unescape(zm.group(1)).strip())
        if w is not None and world_type.get(w) in INSTANCE_TYPES:
            page_levels[w].append(int(lm.group(1)))
world_level = {w: sorted(v)[len(v) // 2] for w, v in page_levels.items()}

# world locations of the client tables (id, world, zone, x, y, z): the nearest one inside 1500 m names the world when no spawn is near
locations = []
if os.path.exists(f'{ROOT}/locations.tsv'):
    for l in open(f'{ROOT}/locations.tsv'):
        f = l.split()
        locations.append((int(f[1]), float(f[3]), float(f[5])))

def world_by_locations(coords):
    votes = collections.Counter()
    for x, z in coords[:10]:
        x, z = float(x), float(z)
        best = min(((w, (lx - x) ** 2 + (lz - z) ** 2) for w, lx, lz in locations), key=lambda t: t[1], default=None)
        if best and best[1] < 1500 ** 2:
            votes[best[0]] += 1
    return votes.most_common(1)[0][0] if votes else None

inst_locations = []
for lw, lx, lz in locations:
    if world_type.get(lw) in INSTANCE_TYPES:
        inst_locations.append((lw, lx, lz))

def instance_world_by_locations(coords):
    votes = collections.Counter()
    for x, z in coords[:15]:
        x, z = float(x), float(z)
        best = min(((w, (lx - x) ** 2 + (lz - z) ** 2) for w, lx, lz in inst_locations), key=lambda t: t[1], default=None)
        if best and best[1] < 250 ** 2:
            votes[best[0]] += 1
    return votes.most_common(1)[0][0] if votes and votes.most_common(1)[0][1] * 2 >= len(coords[:15]) else None

def world_by_coords(coords):
    votes = collections.Counter()
    for x, z in coords[:20]:
        x, z = float(x), float(z)
        best = None
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for w, sx, sz in grid.get((int(x) // CELL + dx, int(z) // CELL + dz), ()):
                    d = (sx - x) ** 2 + (sz - z) ** 2
                    if best is None or d < best[0]:
                        best = (d, w)
        if best and best[0] < CELL * CELL:
            votes[best[1]] += 1
    return votes.most_common(1)[0][0] if votes and votes.most_common(1)[0][1] * 2 > len(coords[:20]) else None

def cluster(points, eps=12.0):
    """Jabbithole lists sightings, not spawn points: a creature seen at a hundred nearby places is a handful of creatures. Greedy clustering
    by distance, the centre of each cluster is a spawn, largest clusters first."""
    pts = [(float(x), float(z)) for x, z in points if abs(float(x)) < 40000 and abs(float(z)) < 40000]   # junk markers exist (-1.49e9)
    clusters = []   # [sumx, sumz, n]
    for x, z in pts:
        for c in clusters:
            if (c[0] / c[2] - x) ** 2 + (c[1] / c[2] - z) ** 2 <= eps * eps:
                c[0] += x; c[1] += z; c[2] += 1
                break
        else:
            clusters.append([x, z, 1])
    clusters.sort(key=lambda c: -c[2])
    return [(c[0] / c[2], c[1] / c[2], c[2]) for c in clusters]



rows, report = [], []
main_points = collections.defaultdict(list)
main_boss = {}
seen = set()
placed = collections.defaultdict(int)
for path in sorted(glob.glob(f'{ROOT}/jh-raw/*.html.gz')):
    try:
        h = gzip.open(path).read().decode('utf8', 'replace')
    except Exception:
        continue
    if not h:
        continue
    m = re.search(r'<h1>(.*?)<span>', h, re.S)
    if not m:
        continue
    name = slug(m.group(1))
    cands = need.get(name)
    if not cands:
        continue
    zm = re.search(r'Found in:.*?<a [^>]*>(.*?)</a>', h, re.S)
    lm = re.search(r'Level:\s*(\d+)', h)
    zone = html.unescape(zm.group(1)).strip() if zm else ''
    level = int(lm.group(1)) if lm else 0
    coords = re.findall(r'class="location_large"[^>]*title="(-?\d+),(-?\d+)"', h, re.S)
    rm = re.search(r'Rank:\s*([A-Za-z]+)', h)
    em = re.search(r'Eliteness:\s*([A-Za-z]+)', h)
    is_boss = (rm and rm.group(1) == 'Prime') or (em and em.group(1) in ('Group', 'Raid', 'Small', 'Large'))
    centers = cluster(coords)
    if is_boss:
        centers = centers[:1]   # a boss is one creature that was seen walking around
    coords = [(f'{x:.1f}', f'{z:.1f}') for x, z, n in centers]
    world = world_for(zone) or world_by_coords(coords) or world_by_locations(coords)
    base = os.path.basename(path)
    if not coords or world is None:
        report.append(f'skip\t{base}\tzone "{zone}" world {world} coords {len(coords)}')
        continue
    zslug = slug(zone)
    # the level only narrows when more than one creature shares the name (quest NPCs often list a different level)
    if not level:
        level = world_level.get(world, 0)
    ok = cands
    if len(ok) > 1:
        ok = [c for c in cands if not (level and c['hi'] and not (c['lo'] - 3 <= level <= c['hi'] + 3))] or cands
    byzone = [c for c in ok if zslug in c['zones'] or zslug in c['desc']]
    ok = byzone or ok
    if world_type.get(world) in INSTANCE_TYPES:
        # veteran creatures belong to veteran instances, which do not exist here: only the normal difficulty is placed
        ok = [c for c in ok if 'veteran' not in c['desc']] or []
        if not ok:
            continue
    npcs_only = all(c['roles'] in ('giver', 'receiver', 'giver,receiver', 'receiver,giver') for c in ok)
    if len(ok) > (30 if npcs_only else 3):
        report.append(f'ambiguous\t{base}\t{len(ok)} candidates {[c["id"] for c in ok][:8]}')
        continue
    for c in ok:
        main_points[(c['id'], world)].extend(coords)
        main_boss[(c['id'], world)] = main_boss.get((c['id'], world), False) or bool(is_boss)
        report.append(f'matched\t{base}\tcreature {c["id"]} world {world} zone {zone} {"zone-match" if byzone else "name-only"}')

for (cid, world), coords in sorted(main_points.items()):
    centers = cluster(coords)
    if main_boss[(cid, world)]:
        centers = centers[:1]
    for x, z, n in centers[:MAX_ROWS]:
        rows.append(f'{cid}\t{world}\t{x:.1f}\t{z:.1f}\tpage')
        placed[cid] += 1

# ---- instances: every NPC page of a dungeon, raid, expedition or adventure zone is placed, quest creature or not ----------------------------
creatures_by_name = collections.defaultdict(list)
if os.path.exists(f'{ROOT}/creatures.tsv'):
    for l in open(f'{ROOT}/creatures.tsv', encoding='utf-8'):
        f = l.rstrip('\n').split('\t')
        if len(f) >= 8 and f[1]:
            creatures_by_name[slug(f[1])].append(dict(id=int(f[0]), lo=int(f[2] or 0), hi=int(f[3] or 0), desc=f[7].lower()))

def zone_tags(zone):
    words = [w for w in re.split(r"[^A-Za-z0-9]+", zone.lower().replace("'s", "")) if w]
    tags = {w for w in words if len(w) > 3 and w not in ('lair', 'ruins', 'island', 'the', 'archives', 'sanctuary')}
    initials = ''.join(w[0] for w in words if w not in ('of', 'the'))
    if len(initials) >= 2:
        tags.add(initials)
    if len(words) >= 2:
        tags.add(words[-2][0] + words[-1][0])
        tags.add(words[0][:2] + words[1][0])
    if words:
        tags.add(words[0][:2])
    tags.add(''.join(w[0] for w in words))   # initials with the small words (Sanctuary of the Swordmaiden: sots)
    return {t for t in tags if len(t) >= 2}

open_locations = [(w, x, z) for w, x, z in locations if w in OPEN]
inst_locations_by_world = collections.defaultdict(list)
for w, x, z in locations:
    if world_type.get(w) in INSTANCE_TYPES:
        inst_locations_by_world[w].append((x, z))
zone_is_open = {}   # (zone slug, world) -> True when the sightings lie closer to open world locations than to the instance's own

def nearest2(points, coords):
    best = None
    for cx, cz in coords:
        d = min(((px - cx) ** 2 + (pz - cz) ** 2 for px, pz in points), default=None)
        if d is not None and (best is None or d < best):
            best = d
    return best

instance_rows = 0
instance_seen = collections.Counter()
inst_points = collections.defaultdict(list)
inst_boss = {}
for path in sorted(glob.glob(f'{ROOT}/jh-raw/*.html.gz')):
    if path.endswith('-veteran.html.gz'):
        continue
    try:
        h = gzip.open(path).read().decode('utf8', 'replace')
    except Exception:
        continue
    if not h:
        continue
    m = re.search(r'<h1>(.*?)<span>', h, re.S)
    zm = re.search(r'Found in:.*?<a [^>]*>(.*?)</a>', h, re.S)
    if not m or not zm:
        continue
    zone = html.unescape(zm.group(1)).strip()
    name = slug(m.group(1))
    world = ALIAS.get(slug(zone)) or world_for(zone)
    if world is None:
        # a zone the client tables do not know: the instance whose world locations the sightings fall on
        world = instance_world_by_locations(re.findall(r'class="location_large"[^>]*title="(-?\d+),(-?\d+)"', h, re.S))
    if world is None or world_type.get(world) not in INSTANCE_TYPES:
        continue
    # a zone that shares its name with an open world zone (Whitevale, Malgrave, Galeras, ...): decide once by where the sightings are
    key = (slug(zone), world)
    if key not in zone_is_open:
        pts = [(float(a), float(b)) for a, b in re.findall(r'class="location_large"[^>]*title="(-?\d+),(-?\d+)"', h, re.S)[:6]]
        d_inst = nearest2(inst_locations_by_world.get(world, []), pts)
        d_open = nearest2([(x, z) for w, x, z in open_locations], pts)
        zone_is_open[key] = d_open is not None and (d_inst is None or d_open < 0.5 * d_inst)
    if zone_is_open[key]:
        continue
    # open world zones are placed by the quest pass and the world database, never into instances
    if name in need:
        continue   # handled above (quest creature)
    cands = creatures_by_name.get(name)
    coords = re.findall(r'class="location_large"[^>]*title="(-?\d+),(-?\d+)"', h, re.S)
    if not cands or not coords:
        continue
    lm = re.search(r'Level:\s*(\d+)', h)
    level = int(lm.group(1)) if lm else world_level.get(world, 0)
    tags = zone_tags(zone)
    def score(c):
        sc = 0
        d = c['desc']
        if any(re.search(r'(^|[^a-z0-9])' + re.escape(t) + r'([^a-z0-9]|$)', d) for t in tags):
            sc += 4
        if 'veteran' in d or 'test' in d.split(' - ')[0]:
            sc -= 3
        if level and c['hi'] and c['lo'] - 1 <= level <= c['hi'] + 1:
            sc += 2
        elif level and c['hi']:
            sc -= min(3, abs(c['hi'] - level) // 5)
        return sc
    best = max(cands, key=lambda c: (score(c), -c['id']))
    if score(best) < 2:
        report.append(f'instance-unmatched\t{os.path.basename(path)}\t{zone} {level} {len(cands)} candidates')
        continue
    rm = re.search(r'Rank:\s*([A-Za-z]+)', h)
    em = re.search(r'Eliteness:\s*([A-Za-z]+)', h)
    is_boss = (rm and rm.group(1) == 'Prime') or (em and em.group(1) in ('Group', 'Raid', 'Small', 'Large'))
    key = (best['id'], world)
    inst_points[key].extend(coords)
    inst_boss[key] = inst_boss.get(key, False) or bool(is_boss)
    instance_seen[world] += 1
# one clustering per creature and instance: several pages (zone variants, normal and veteran listings) describe the same creatures
for (cid, world), coords in sorted(inst_points.items()):
    centers = cluster(coords)
    if inst_boss[(cid, world)]:
        centers = centers[:1]
    for x, z, n in centers[:MAX_ROWS]:
        rows.append(f'{cid}\t{world}\t{x:.1f}\t{z:.1f}\tinstance')
        instance_rows += 1
report.append(f'instance-rows\t{instance_rows} rows, pages per world {dict(instance_seen)}')

# fallback: creatures no page could place go where the quest objectives that need them point to
import random
loc_by_id = {}
if os.path.exists(f'{ROOT}/locations.tsv'):
    for l in open(f'{ROOT}/locations.tsv'):
        f = l.split()
        loc_by_id[int(f[0])] = (int(f[1]), float(f[3]), float(f[5]))
rng = random.Random(1)
fallback = 0
for cid, locs in sorted(indicators.items()):
    if placed.get(cid):
        continue
    per = 1 if needed_roles.get(cid, '') in ('giver', 'receiver', 'giver,receiver', 'receiver,giver') else 4
    for lid in locs[:3]:
        if lid in loc_by_id:
            w, x, z = loc_by_id[lid]
            for k in range(per):
                jitter = 0 if per == 1 else 6
                rows.append(f'{cid}\t{w}\t{x + rng.uniform(-jitter, jitter):.1f}\t{z + rng.uniform(-jitter, jitter):.1f}\tindicator-{lid}')
            placed[cid] += per
            fallback += 1
report.append(f'indicator-fallback\t{fallback} locations used')
open(f'{ROOT}/../data/spawns/research-spawns.tsv', 'w').write('\n'.join(rows) + '\n')
open(f'{ROOT}/match-report.txt', 'w').write('\n'.join(report) + '\n')
ids = len(placed)
ids = len(placed)
print(f'{len(rows)} spawn rows for {ids} of {sum(len(v) for v in need.values())} needed creatures')
print(collections.Counter(r.split('\t')[0] for r in report))
