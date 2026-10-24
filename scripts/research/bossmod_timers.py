#!/usr/bin/env python3
"""Reconcile raid encounter timers against three WildStar boss mods (only facts are taken: timer lengths and labels).

Sources (cloned into research/, gitignored):
  RaidCore        https://github.com/Immemor/RaidCore           AddTimerBar("KEY", "label" or self.L["label"], seconds)
  DruseraBossMods https://github.com/Immemor/DruseraBossMods    SetTimer("KEY", seconds), KEY -> English text in RegisterEnglishLocale
  LUI-BossMods    https://github.com/laserloui/LUI-BossMods     core:AddTimer("key", self.L["message.x"], seconds); LUI reuses RaidCore
                  numbers in most modules, so it only counts as a second source where its value differs from RaidCore.

A timer belongs to an ability when the words (4+ letters, no filler words) of the timer label are all in the spell name or the other way round. A timer set in a
function that runs at pull (OnBossEnable, OnStartCombat, ...) is a first-cast timer, any other one a repeat timer.
Two independent sources agree when their values differ by at most max(5 s, 15 %). Then the ability timer is replaced by the RaidCore
value (Drusera when RaidCore has none) and marked "likely"; one source keeps its value as "single source"; no source keeps the old
value as "guess". Each encounter gets "confidence": likely (half or more of its abilities have agreeing repeat timers), partial (some),
guess (none) and "timerSources" listing the evidence per ability.

Writes the encounter files in place (data/encounters/raids/*.json, data/encounters/raidcore/*.json) and research/bossmod-timers.tsv.
"""
import glob, json, os, re

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
RES = os.path.join(ROOT, 'research')
FILLER = {'next', 'timer', 'phase', 'first', 'soon', 'message', 'cast', 'with', 'from', 'timeout', 'approximate', 'switch', 'wave',
          'waves', 'boss', 'adds', 'unit', 'this', 'that', 'interrupt', 'into', 'from', 'tank'}
PULL = re.compile(r'(Enable|StartCombat|OnEngage|OnEncounterStart)', re.I)


def words(s):
    return {w for w in re.findall(r'[a-z]{4,}', (s or '').lower()) if w not in FILLER}


def english_locale(t, marker):
    i = t.find(marker)
    if i < 0:
        return {}
    j = len(t)
    for other in ('RegisterFrenchLocale', 'RegisterGermanLocale', '["deDE"]', '["frFR"]', 'RegisterKoreanLocale'):
        k = t.find(other, i + 1)
        if k > i:
            j = min(j, k)
    return dict(re.findall(r'\["([^"]+)"\]\s*=\s*"([^"]*)"', t[i:j]))


def functions(t):
    """(start offset, function name) for every function in a Lua file."""
    # anonymous functions are callbacks (on cast start/end, on a datachron line): their timers are repeat timers
    return [(m.start(), m.group(1) or 'callback') for m in re.finditer(r'function(?:\s+[\w.:]*?(\w+))?\s*\(', t)]


def enclosing(funcs, pos):
    name = ''
    for start, n in funcs:
        if start > pos:
            break
        name = n
    return name


def scan(source, pattern, files, locale_marker):
    """[(file, set of locale texts, label, seconds, kind)] for literal timers."""
    out = []
    for path in files:
        t = open(path, encoding='utf8', errors='replace').read()
        loc = english_locale(t, locale_marker)
        funcs = functions(t)
        for m in re.finditer(pattern, t):
            key, label, secs = m.group('key'), m.group('label') or '', m.group('secs')
            text = loc.get(label, label) or loc.get(key, key)
            kind = 'first' if PULL.search(enclosing(funcs, m.start())) else 'every'
            out.append((source, os.path.relpath(path, RES), set(loc.values()) | set(loc.keys()), f'{key} {text}', float(secs), kind))
    return out


def collect():
    rc = scan('raidcore', r'AddTimerBar\("(?P<key>[^"]+)",\s*(?:self\.L\["(?P<label>[^"]+)"\][^,]*|"[^"]*"),\s*(?P<secs>[0-9.]+)\s*[,)]',
              glob.glob(f'{RES}/raidcore/Encounters/*/*.lua') + glob.glob(f'{RES}/raidcore/Encounters/*/*/*.lua'), 'RegisterEnglishLocale')
    dr = scan('drusera', r'SetTimer\("(?P<key>[A-Z0-9_]+)",\s*(?P<label>)(?P<secs>[0-9.]+)\s*[,)]',
              glob.glob(f'{RES}/DruseraBossMods/encounters/*/*.lua') + glob.glob(f'{RES}/DruseraBossMods/encounters/*/*/*.lua'), 'RegisterEnglishLocale')
    lui = scan('lui', r'AddTimer\("(?P<key>[^"]+)",\s*self\.L\["(?P<label>[^"]+)"\][^,]*,\s*(?P<secs>[0-9.]+)\s*[,)]',
               glob.glob(f'{RES}/LUI-BossMods/Encounters/*/*.lua') + glob.glob(f'{RES}/LUI-BossMods/Encounters/*/*/*.lua'), '["enUS"]')
    # Drusera labels: the timer key resolved through the locale ("NEXT_RUPTURE" -> "Next Rupture") is already in the label text
    return rc + dr + lui


def agree(a, b):
    return abs(a - b) <= max(5.0, 0.15 * max(a, b))


def main():
    timers = collect()
    rows, changed, stats = [], 0, {'likely': 0, 'partial': 0, 'guess': 0}
    files = sorted(glob.glob(f'{ROOT}/data/encounters/raids/*.json') + glob.glob(f'{ROOT}/data/encounters/raidcore/*.json'))
    for path in files:
        data = json.load(open(path))
        single = isinstance(data, dict)
        defs = [data] if single else data
        for d in defs:
            boss = d['boss']
            mine = [x for x in timers if boss in x[2]]
            evidence, sourced, total = {}, set(), set()
            for phase in d['phases']:
                for ab in phase['abilities']:
                    spell = ab['spell']
                    total.add(spell)
                    w = words(spell)
                    if not w:
                        continue
                    # every word of the label in the spell name or every word of the spell name in the label; 0 s timers only cancel a bar
                    hits = [x for x in mine if x[4] > 0 and words(x[3]) and (words(x[3]) <= w or w <= words(x[3]))]
                    by = {}
                    for src, f, _, label, secs, kind in hits:
                        by.setdefault((src, kind), []).append(secs)
                    for kind in ('every', 'first'):
                        rcv, drv, luv = by.get(('raidcore', kind), []), by.get(('drusera', kind), []), by.get(('lui', kind), [])
                        luo = [v for v in luv if v not in rcv]   # LUI values that are not a copy of RaidCore
                        pairs = [(a, b) for a in rcv for b in drv + luo] + [(a, b) for a in luo for b in drv]
                        pair = next(((a, b) for a, b in pairs if agree(a, b)), None)
                        if not (rcv or drv or luv):
                            verdict, value = 'none', None
                        elif pair:
                            verdict, value = 'agree', pair[0]
                        else:
                            verdict, value = 'single source' if len(set(rcv + drv + luv)) == 1 or not (rcv and drv) else 'sources disagree', (rcv or drv or luv)[0]
                        labels = sorted({f'{src}:{label}={secs:g}' for src, f, _, label, secs, k in hits if k == kind})
                        rows.append('\t'.join([boss, spell, kind, ','.join(map(str, rcv)), ','.join(map(str, drv)), ','.join(map(str, luv)), verdict, ' | '.join(labels)]))
                        if verdict == 'none':
                            continue
                        note = f"{kind}: raidcore {rcv or '-'}, drusera {drv or '-'}, lui {luv or '-'} -> {verdict}"
                        evidence[spell + ' ' + kind] = note
                        if verdict == 'agree' and value and ab.get(kind) != value:
                            ab[kind] = value
                            changed += 1
                        if verdict == 'agree' and kind == 'every':
                            sourced.add(spell)
            n = len(total)
            conf = 'likely' if n and len(sourced) * 2 >= n else 'partial' if sourced else 'guess'
            d['confidence'] = conf
            d['timerSources'] = evidence
            stats[conf] += 1
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(data if single else defs, f, indent=1, ensure_ascii=False)
            f.write('\n')
    with open(f'{RES}/bossmod-timers.tsv', 'w', encoding='utf-8') as f:
        f.write('boss\tspell\tkind\traidcore\tdrusera\tlui\tverdict\tlabels\n' + '\n'.join(rows) + '\n')
    verdicts = {}
    for r in rows:
        v = r.split('\t')[6]
        verdicts[v] = verdicts.get(v, 0) + 1
    print(f'{len(timers)} literal timers (raidcore {sum(1 for x in timers if x[0]=="raidcore")}, drusera {sum(1 for x in timers if x[0]=="drusera")}, lui {sum(1 for x in timers if x[0]=="lui")})')
    print(f'ability timer checks: {verdicts}; values changed: {changed}')
    print(f'encounters by confidence: {stats}')


main()
