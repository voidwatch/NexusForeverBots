#!/usr/bin/env python3
"""Creature health from Jabbithole NPC pages (Level a - b, Rank, Eliteness, Health x - y) vs the table formula
(CreatureLevel x ArcheType x Difficulty x Tier). Writes research/decode/combat/jhstats.tsv, prints <=30 lines."""
import html, gzip, os, re, collections, statistics, tbl
from wikistats import stats, byname
RAW='../../research/jh-raw'
rx=re.compile(r'<h1[^>]*>(.*?)</h1>.*?Level: ([\d]+)(?: - ([\d]+))?.*?Rank: (\w+).*?Eliteness: ([\w ]+?)\s.*?Health: ([\d,]+)(?: - ([\d,]+))?',re.S)
out=open('../../research/decode/combat/jhstats.tsv','w')
out.write('page\tname\trank\telite\tlmin\tlmax\thp_min\thp_max\tcreature\tdiff\tarch\ttier\ttable_min\ttable_max\tratio\tcands\n')
res=collections.defaultdict(list); n=0; nm=0
for f in sorted(os.listdir(RAW)):
    if 'loot' in f: continue
    try: t=gzip.open(os.path.join(RAW,f)).read().decode('utf8','replace')
    except Exception: continue
    i=t.find('Health: ')
    if i<0: continue
    s=re.sub(r'\s+',' ',re.sub(r'<[^>]*>',' ',t[max(0,i-3000):i+60]))
    m=re.search(r'Level: (\d+)(?: - (\d+))? Rank: (\w+) Eliteness: (\w+(?: \w+)?) Reaction: \S+ Health: ([\d,]+)(?: - ([\d,]+))?',s)
    if not m: continue
    n+=1
    name=re.search(r'<h1[^>]*>([^<]*)',t); name=html.unescape(name.group(1) if name else f).strip()
    l0=int(m.group(1)); l1=int(m.group(2) or l0); h0=float(m.group(5).replace(',','')); h1=float((m.group(6) or m.group(5)).replace(',',''))
    cands=byname.get(name.lower(),[])
    if not cands or h0<=1: continue
    best=None
    for c in cands:
        a=stats(c,l0)[7]; b=stats(c,l1)[7]
        if a<=0: continue
        r=(h0/a + h1/b)/2
        if best is None or abs(r-1)<abs(best[0]-1): best=(r,c,a,b)
    if not best: continue
    nm+=1; r,c,a,b=best
    key=(m.group(3),m.group(4)); res[key].append(r)
    out.write(f"{f}\t{name}\t{m.group(3)}\t{m.group(4)}\t{l0}\t{l1}\t{h0:.0f}\t{h1:.0f}\t{c['ID']}\t{c['creature2DifficultyId']}\t{c['creature2ArcheTypeId']}\t{c['creature2TierId']}\t{a:.0f}\t{b:.0f}\t{r:.3f}\t{len(cands)}\n")
print(f'pages with health {n}, matched by name {nm}')
allr=[x for v in res.values() for x in v]
print(f'all: median ratio wiki/table {statistics.median(allr):.3f}, within 5% {sum(abs(x-1)<=.05 for x in allr)}, within 15% {sum(abs(x-1)<=.15 for x in allr)} of {len(allr)}')
for k,v in sorted(res.items(),key=lambda x:-len(x[1]))[:20]:
    print(f'{k[0]:11} {k[1]:12} n {len(v):5} median {statistics.median(v):.3f}  within5% {sum(abs(x-1)<=.05 for x in v):5}  within15% {sum(abs(x-1)<=.15 for x in v):5}')
