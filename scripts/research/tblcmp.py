#!/usr/bin/env python3
"""Compare the C# GameTable reader rules (string pad heuristic, 64-bit alignment) with the 8-byte aligned Python reader over data/tbl;
prints tables whose field values would differ. Run from scripts/research."""
import os,struct
D='../../data/tbl'; H=0x60; out=[]
for fn in sorted(os.listdir(D)):
    if not fn.endswith('.tbl'): continue
    b=open(os.path.join(D,fn),'rb').read()
    (sig,ver,nl,_u1,rs,fc,fo,rc,trs,ro,mi,lo,_u2)=struct.unpack_from('<IIQQQQQQQQQQQ',b,0)
    types=[struct.unpack_from('<QQHHI',b,H+fo+i*24)[2] for i in range(fc)]
    if 130 not in types and 20 not in types: continue
    bad=0; badf={}
    for r in range(rc):
        p=H+ro+r*rs; a=0; c=0
        for idx,t in enumerate(types):
            if t in (3,4,11):
                va=b[p+a:p+a+4]; vc=b[p+c:p+c+4]; a+=4; c+=4
            elif t==20:
                if a%8: a+=8-a%8
                if c%8: c+=8-c%8   # GameTable.AlignULong
                va=b[p+a:p+a+8]; vc=b[p+c:p+c+8]; a+=8; c+=8
            else:
                if a%8: a+=8-a%8
                o1,o2=struct.unpack_from('<II',b,p+a); va=max(o1,o2); a+=8
                o1,o2=struct.unpack_from('<II',b,p+c); vc=max(o1,o2); c+=8
                if idx<fc-1 and o1==0 and types[idx+1]!=130: c+=4
            if va!=vc: badf[idx]=badf.get(idx,0)+1
        if badf: bad+=1
    if badf: out.append((fn,rc,sorted(badf.items())[:4]))
for o in out: print(*o)
print('tables with differing values:',len(out))
