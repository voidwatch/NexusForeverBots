#!/usr/bin/env python3
"""Sample archived Jabbithole item pages (Wayback, 2016-2019 = after the Drop 6 / F2P stat revamp) and parse the tooltip stats.

    jh_items.py fetch [max_seconds]   fetch a stratified sample (cached in research/items-raw/, 1 request/s, resumable)
    jh_items.py parse                 write research/decode/items/jh-items.tsv (one row per item and stat)

Needs research/items-raw/cdx.txt (Wayback CDX list of jabbithole.com/items/* captures 2016-2019).
"""
import os, re, sys, html, time, random
sys.path.insert(0, os.path.dirname(__file__))
from tbl import load
from wayback import Wayback

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
RAW = os.path.join(ROOT, 'research', 'items-raw')
OUT = os.path.join(ROOT, 'research', 'decode', 'items')
# slot id -> how many pages to sample
QUOTA = {20: 260, 1: 50, 2: 30, 3: 30, 4: 30, 5: 30, 6: 30, 43: 60, 57: 30, 58: 30, 60: 30}


def candidates():
    types = {x['ID']: x for x in load('Item2Type')}
    items = {x['ID']: x for x in load('Item2')}
    by_slot = {}
    for line in open(os.path.join(RAW, 'cdx.txt')):
        parts = line.split()
        if len(parts) != 2:
            continue
        ts, url = parts
        m = re.search(r'/items/[^/?]*-(\d+)$', url)
        if not m:
            continue
        item = items.get(int(m.group(1)))
        if not item:
            continue
        slot = types[item['item2TypeId']]['itemSlotId']
        if slot in QUOTA:
            by_slot.setdefault(slot, []).append((ts, url, item['ID']))
    rnd = random.Random(16042)
    picked = []
    for slot, rows in by_slot.items():
        rnd.shuffle(rows)
        picked += rows[:QUOTA[slot]]
    return picked


def fetch(max_seconds):
    os.makedirs(RAW, exist_ok=True)
    wb = Wayback()
    start = time.time()
    got = miss = have = 0
    for ts, url, item_id in candidates():
        path = os.path.join(RAW, f'{item_id}.html')
        if os.path.exists(path):
            have += 1
            continue
        if time.time() - start > max_seconds:
            break
        body = wb.get(ts, url.replace(':80/', '/'))
        with open(path, 'wb') as f:
            f.write(body or b'')
        if body:
            got += 1
        else:
            miss += 1
    total = len(candidates())
    print(f'cached before {have}, fetched {got}, missing {miss}, sample {total}, done {have + got + miss >= total}')


STAT = re.compile(r'^\+?\s*([\d,]+(?:\.\d+)?)(%?)\s+(.+)$')


def parse_page(text):
    t = re.sub(r'<script.*?</script>', '', text, flags=re.S)
    t = re.sub(r'<[^>]+>', '\n', t)
    lines = [l.strip() for l in html.unescape(t).split('\n') if l.strip()]
    info = {}
    for i, l in enumerate(lines):
        if l == 'Last seen in:' and i + 1 < len(lines):
            info['seen'] = lines[i + 1]
        if l.startswith('Effective Level:'):
            info['ilvl'] = l.split(':')[1].strip()
            stats = []
            for s in lines[i + 1:i + 40]:
                if s.startswith(('Required', 'Bind', 'Binds', 'Unique', 'Class', 'Durability')):
                    continue
                if 'Rune Slot' in s or s in ('Salvageable', 'Sell for:', '+') or s.startswith('Sell'):
                    break
                m = STAT.match(s)
                if m:
                    stats.append((m.group(3).strip(), m.group(1).replace(',', '') + m.group(2)))
            info['stats'] = stats
            break
    return info


def parse():
    os.makedirs(OUT, exist_ok=True)
    items = {x['ID']: x for x in load('Item2')}
    types = {x['ID']: x for x in load('Item2Type')}
    n = rows = 0
    with open(os.path.join(OUT, 'jh-items.tsv'), 'w') as out:
        out.write('item\tslot\tcategory\tquality\tpowerLevel\tsupportPct\tbudget\tstatId\tjh_ilvl\tseen\tstat\tvalue\n')
        for name in sorted(os.listdir(RAW)):
            if not name.endswith('.html'):
                continue
            item = items.get(int(name[:-5]))
            body = open(os.path.join(RAW, name), 'rb').read().decode('utf8', 'replace')
            if not item or not body:
                continue
            info = parse_page(body)
            if 'stats' not in info:
                continue
            n += 1
            slot = types[item['item2TypeId']]['itemSlotId']
            for stat, value in info['stats']:
                rows += 1
                out.write(f"{item['ID']}\t{slot}\t{item['item2CategoryId']}\t{item['itemQualityId']}\t{item['powerLevel']}\t"
                          f"{item['supportPowerPercentage']:.3f}\t{item['itemBudgetId']}\t{item['itemStatId']}\t{info.get('ilvl')}\t"
                          f"{info.get('seen', '')}\t{stat}\t{value}\n")
    print(f'parsed {n} items, {rows} stat rows -> research/decode/items/jh-items.tsv')


if __name__ == '__main__':
    if sys.argv[1] == 'fetch':
        fetch(float(sys.argv[2]) if len(sys.argv) > 2 else 500)
    else:
        parse()
