#!/usr/bin/env python3
"""keyscan.py id id id ... : which tables have these ids as primary key (counts), top 15."""
import sys, os, tbl, collections
ids = [int(x) for x in sys.argv[1:]]
res = []
for fn in os.listdir(tbl.TBL_DIR):
    if not fn.endswith('.tbl'): continue
    t = fn[:-4]
    try: ix = tbl.index(t)
    except Exception: continue
    n = sum(1 for i in ids if i in ix)
    if n: res.append((n, t, len(ix)))
for n, t, sz in sorted(res, reverse=True)[:15]: print(f'{n}/{len(ids)} {t} ({sz} rows)')
