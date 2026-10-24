#!/usr/bin/env python3
"""Compare creature health/shield from the tables (CreatureLevel x ArcheType x Difficulty x Tier) with archive-wiki NPC infoboxes.
Writes research/decode/combat/wikistats.tsv, prints a summary (<=30 lines)."""
import tbl, json, re, collections, statistics
lvl=tbl.load('CreatureLevel'); L={r['ID']:r for r in lvl}
def row(t,i):
    r=tbl.index(t).get(i)
    return [r[f'unitPropertyMultiplier{k:03d}'] for k in range(200)] if r else [1.0]*200
def lrow(l):
    r=L.get(max(1,min(50,l))); return [r[f'unitPropertyValue{k:02d}'] if f'unitPropertyValue{k:02d}' in r else r[f'unitPropertyValue{k:03d}'] for k in range(100)] if r else None
def stats(c,l):
    v=lrow(l); a=row('Creature2ArcheType',c['creature2ArcheTypeId']); d=row('Creature2Difficulty',c['creature2DifficultyId']); t=row('Creature2Tier',c['creature2TierId'])
    return [v[k]*a[k]*d[k]*t[k] for k in range(100)]
byname=collections.defaultdict(list)
for c in tbl.load('Creature2'):
    n=tbl.text(c['localizedTextIdName'])
    if n: byname[n.lower()].append(c)
def num(s):
    s=(s or '').strip().lower().replace(',','')
    m=re.match(r'([\d.]+)\s*([km]?)',s)
    if not m: return None
    return float(m.group(1))*{'':1,'k':1e3,'m':1e6}[m.group(2)]
def main():
    out=open('../../research/decode/combat/wikistats.tsv','w'); out.write('name\twtype\tlvl\twiki_hp\ttable_hp\tratio_hp\twiki_sh\ttable_sh\tratio_sh\tcreature\tdiff\tarch\ttier\n')
    rh=collections.defaultdict(list); rs=collections.defaultdict(list); n=0
    for line in open('../../research/wiki-all.jsonl'):
        d=json.loads(line); w=d.get('wikitext') or ''
        if '{{NPC infobox' not in w: continue
        f=dict(re.findall(r'^\|\s*(\w+)\s*=\s*(.*)$',w,re.M))
        hp=num(f.get('health')); sh=num(f.get('shield')); lv=num(f.get('lvl'))
        if not hp or not lv: continue
        cands=byname.get(d['title'].lower().strip('"'),[]) or byname.get(re.sub(r'\s*\(.*\)','',d['title']).lower(),[])
        if not cands: continue
        # best candidate: the one whose table health is closest (report the spread, so ambiguity is visible)
        best=None
        for c in cands:
            s=stats(c,int(lv)); r=s[7]/hp if hp else 0
            if best is None or abs(r-1)<abs(best[1]-1): best=(c,r,s)
        c,r,s=best; n+=1
        typ=f.get('type','').strip()
        rh[typ].append(r)
        if sh: rs[typ].append(s[41]/sh)
        out.write(f"{d['title']}\t{typ}\t{int(lv)}\t{hp:.0f}\t{s[7]:.0f}\t{r:.3f}\t{sh or 0:.0f}\t{s[41]:.0f}\t{(s[41]/sh if sh else 0):.3f}\t{c['ID']}\t{c['creature2DifficultyId']}\t{c['creature2ArcheTypeId']}\t{c['creature2TierId']}\n")
    print('matched npc pages with health:',n)
    for typ in sorted(rh,key=lambda t:-len(rh[t])):
        h=rh[typ]; s=rs.get(typ,[])
        within=sum(1 for x in h if abs(x-1)<=0.15)
        print(f"type {typ or '?':14} n {len(h):4} hp ratio median {statistics.median(h):.3f} within15% {within}/{len(h)}  shield n {len(s)} median {statistics.median(s) if s else 0:.3f}")

if __name__=='__main__':
    main()
