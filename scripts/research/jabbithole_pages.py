#!/usr/bin/env python3
"""Fetch archived Jabbithole pages listed in a CDX file, one request at a time, 1.5 s apart.

Usage: jabbithole_pages.py <kind>   (zones: jh-zone-cdx.txt -> jh-zone-raw/, public-events: jh-event-cdx.txt -> jh-event-raw/)
"""
import gzip, os, re, sys, time
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'research')
KIND = sys.argv[1] if len(sys.argv) > 1 else 'zones'
CDX, RAW = {'zones': ('jh-zone-cdx.txt', 'jh-zone-raw'), 'public-events': ('jh-event-cdx.txt', 'jh-event-raw')}[KIND]
RAW = os.path.join(ROOT, RAW)
os.makedirs(RAW, exist_ok=True)
seen = {}
for line in open(os.path.join(ROOT, CDX)):
    url, ts = line.split()
    m = re.search(r'/' + KIND + r'/(.+)$', url)
    if m:
        seen.setdefault(m.group(1), (url, ts))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wayback import Wayback

wb = Wayback(1.0)
for slug, (url, ts) in sorted(seen.items()):
    path = os.path.join(RAW, slug + '.html.gz')
    if os.path.exists(path):
        continue
    data = wb.get(ts, url)
    with gzip.open(path, 'wb') as f:
        f.write(data or b'')
