#!/usr/bin/env python3
"""Draft encounter definitions from the RaidCore addon sources (research/raidcore, https://github.com/Immemor/RaidCore, GPL-3.0 licensed addon, only
facts are taken: unit names, cast names, timer lengths, health thresholds).

For each module: the boss units (RegisterTrigMob names through the English locale), the casts of each unit (cast names seen in
OnCastStart/OnCastEnd next to the unit name), timer bars with a literal length (matched to casts by shared words), health thresholds.
Output: data/encounters/raidcore/<Group>_<Module>.json (list of encounter definitions, source "raidcore").
"""
import glob, json, os, re, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
SRC = os.path.join(ROOT, 'research', 'raidcore', 'Encounters')
OUT = os.path.join(ROOT, 'data', 'encounters', 'raidcore')
os.makedirs(OUT, exist_ok=True)

GROUPS = {'GA': ('Genetic Archives', 1462), 'DS': ('Datascape', 1333), 'Y83': ('Initialization Core Y-83', 0), 'RMT': ('Redmoon Terror', 3032)}

def slug(s):
    return re.sub(r'[^a-z0-9]+', '', s.lower())

def english_locale(t):
    i = t.find('RegisterEnglishLocale')
    j = min([x for x in (t.find('RegisterFrenchLocale'), t.find('RegisterGermanLocale')) if x > i] or [len(t)])
    m = re.search(r'RegisterEnglishLocale\(\{(.*)\}\s*\)', t[i:j], re.S)
    d = {}
    casts = []
    if m:
        section = None
        for line in m.group(1).split('\n'):
            c = re.match(r'\s*--+\s*(.*)', line)
            if c:
                section = c.group(1).strip().lower()
                continue
            kv = re.match(r'\s*\["([^"]+)"\]\s*=\s*"([^"]*)"', line)
            if kv:
                d[kv.group(1)] = kv.group(2)
                if section and section.startswith('cast'):
                    casts.append(kv.group(1))
    return d, casts

def main():
    total = 0
    for path in sorted(glob.glob(f'{SRC}/*/*.lua')):
        group = os.path.basename(os.path.dirname(path))
        if group not in GROUPS:
            continue
        t = open(path, encoding='utf8', errors='replace').read()
        loc, cast_names = english_locale(t)
        trig = re.findall(r'RegisterTrigMob\([^,]+,\s*\{([^}]*)\}', t)
        units = []
        for block in trig:
            for name in re.findall(r'"([^"]+)"', block):
                units.append(loc.get(name, name))
        # raidcore keys like "unit.laveka" are resolved by the locale; unresolved keys are dropped
        units = [u for u in dict.fromkeys(units) if not u.startswith('unit.')]
        if not units:
            continue
        timers = []
        for key, text, secs in re.findall(r'AddTimerBar\("([^"]+)",\s*(?:self\.L\["([^"]+)"\]|"[^"]+"),\s*([0-9.]+)', t):
            timers.append((key, loc.get(text, text), float(secs)))
        health = sorted({int(x) for x in re.findall(r'nPourcent\s*(?:==|<=|<)\s*(\d+)', t)}, reverse=True)
        casts = [loc.get(c, c) for c in cast_names]
        # which unit casts what: the nearest unit name before the cast name inside the file body
        body = t[t.find('function mod:'):] if 'function mod:' in t else t
        by_unit = {u: [] for u in units}
        for cast in casts:
            best = None
            for m in re.finditer(re.escape(cast), body):
                before = body[max(0, m.start() - 600):m.start()]
                for u in units:
                    pos = max(before.rfind(f'["{u}"]'), before.rfind(f'"{u}"'))
                    if pos >= 0 and (best is None or pos > best[0]):
                        best = (pos, u)
            by_unit[best[1] if best else units[0]].append(cast)
        encounters = []
        for unit in units:
            phases = []
            thresholds = [100] + ([h for h in health if 5 < h < 100][:3] if health else [])
            if len(thresholds) == 1:
                thresholds = [100]
            my_casts = by_unit[unit]
            for i, th in enumerate(thresholds):
                abil = []
                for j, cast in enumerate(my_casts):
                    words = {w for w in re.findall(r'[a-z]{4,}', cast.lower())}
                    match = [s for k, text, s in timers if words & set(re.findall(r'[a-z]{4,}', (k + ' ' + text).lower()))]
                    every = match[0] if match else 25.0
                    abil.append({'spell': cast, 'first': round(8 + 6 * j, 1), 'every': every, 'target': 'tank' if j % 2 == 0 else 'random'})
                phases.append({'name': f'Phase {i + 1}', 'health': th, 'abilities': abil})
            encounters.append({'boss': unit, 'world': GROUPS[group][1], 'enrage': 0, 'leash': 150, 'source': f'raidcore {GROUPS[group][0]}', 'phases': phases})
        name = os.path.splitext(os.path.basename(path))[0]
        with open(f'{OUT}/{group}_{name}.json', 'w', encoding='utf-8') as f:
            json.dump(encounters, f, indent=1)
        total += len(encounters)
        print(f'{group}/{name}: units {units} casts {sum(len(v) for v in by_unit.values())} timers {len(timers)} health {health}')
    print(total, 'encounters')

main()
