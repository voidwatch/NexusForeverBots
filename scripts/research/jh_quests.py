#!/usr/bin/env python3
"""Archived Jabbithole quest pages (Wayback, 2014-2016) for the quests gt questcheck reports as gaps: how a quest starts and ends.

    jh_quests.py fetch <quest-check.txt> [max_seconds]   fetch pages of gap quests (cached in research/jh-quest-raw/, 1 request / 1.5 s, resumable)
    jh_quests.py parse                                   write research/decode/quests/jh-quests.tsv, print a summary (<= 30 lines)

Needs research/jh-quest-cdx.txt (Wayback CDX of jabbithole.com/quests/*). The page slug ends in the Quest2 id.
Page fields used: "Started by:" and "Completed by:" lists (NPC link, "Call in <zone>", "call", item link, zone), Found in, Level, Side.
"""
import os, re, sys, html, time, collections
sys.path.insert(0, os.path.dirname(__file__))
from wayback import Wayback

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
RAW = os.path.join(ROOT, 'research', 'jh-quest-raw')
OUT = os.path.join(ROOT, 'research', 'decode', 'quests')
KINDS = ('no-giver', 'no-receiver', 'giver-not-spawned', 'receiver-not-spawned', 'objective-not-spawned', 'objective-group-empty')


def pages():
    by_id = {}
    for line in open(os.path.join(ROOT, 'research', 'jh-quest-cdx.txt')):
        url, ts = line.split()
        m = re.search(r'/quests/(?:.*-)?(\d+)$', url)
        if m:
            by_id.setdefault(int(m.group(1)), (ts, url.replace(':80/', '/')))
    return by_id


def fetch(qc, max_seconds):
    os.makedirs(RAW, exist_ok=True)
    want = set()
    for l in open(qc):
        f = l.split('\t')
        if f[0] in KINDS:
            want.add(int(f[1].split()[1]))
    by_id = pages()
    todo = sorted(q for q in want if q in by_id and not os.path.exists(os.path.join(RAW, f'{q}.html')))
    wb = Wayback(min_interval=1.5)
    start, got, miss = time.time(), 0, 0
    for q in todo:
        if time.time() - start > max_seconds:
            break
        body = wb.get(*by_id[q])
        open(os.path.join(RAW, f'{q}.html'), 'wb').write(body or b'')
        got += bool(body)
        miss += not body
    left = sum(1 for q in want if q in by_id and not os.path.exists(os.path.join(RAW, f'{q}.html')))
    print(f'gap quests {len(want)}, with archived page {sum(1 for q in want if q in by_id)}, fetched {got}, empty {miss}, left {left}')


def entries(aside, label):
    m = re.search(label + r':\s*<ul>(.*?)</ul>', aside, re.S)
    if not m:
        return []
    out = []
    for li in re.findall(r'<li>(.*?)</li>', m.group(1), re.S):
        npc = re.search(r'href="/npcs/([^"]+)"', li)
        item = re.search(r'href="/items/([^"]+)"', li)
        txt = html.unescape(re.sub(r'<[^>]+>', '', li)).strip()
        if npc:
            out.append('npc:' + npc.group(1))
        elif item:
            out.append('item:' + item.group(1))
        elif txt.lower().startswith('call'):
            out.append('call')
        else:
            out.append('text:' + txt[:40])
    return out


def parse():
    os.makedirs(OUT, exist_ok=True)
    c_start, c_end = collections.Counter(), collections.Counter()
    n = 0
    with open(os.path.join(OUT, 'jh-quests.tsv'), 'w') as out:
        out.write('quest\tzone\tlevel\tstarted_by\tcompleted_by\n')
        for name in sorted(os.listdir(RAW)):
            body = open(os.path.join(RAW, name), 'rb').read().decode('utf8', 'replace')
            if not body:
                continue
            a = re.search(r'<aside class="side-info">(.*?)</aside>', body, re.S)
            if not a:
                continue
            n += 1
            aside = a.group(1)
            zone = re.search(r'Found in:\s*<ul>\s*<li><a[^>]*>(.*?)</a>', aside, re.S)
            lvl = re.search(r'Level:\s*(\d+)', aside)
            s, e = entries(aside, 'Started by'), entries(aside, 'Completed by')
            c_start[','.join(sorted({x.split(':')[0] for x in s})) or 'none'] += 1
            c_end[','.join(sorted({x.split(':')[0] for x in e})) or 'none'] += 1
            out.write(f"{name[:-5]}\t{html.unescape(zone.group(1)) if zone else ''}\t{lvl.group(1) if lvl else ''}\t{'|'.join(s)}\t{'|'.join(e)}\n")
    print('pages parsed', n)
    print('started by', dict(c_start.most_common(10)))
    print('completed by', dict(c_end.most_common(10)))


if __name__ == '__main__':
    if sys.argv[1] == 'fetch':
        fetch(sys.argv[2], float(sys.argv[3]) if len(sys.argv) > 3 else 480)
    else:
        parse()
