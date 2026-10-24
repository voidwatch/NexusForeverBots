#!/usr/bin/env python3
"""Dump rows of a .tbl straight from data/tbl (no server needed): scripts/tbl-dump.py Name [maxrows]. Strings print as offsets; numbers raw."""
import struct,sys
name=sys.argv[1]; maxrows=int(sys.argv[2]) if len(sys.argv)>2 else 20
d=open(f'data/tbl/{name}.tbl','rb').read()
sig,ver,nl,u1,rs,fc,fo,rc,trs,ro,mx,lo,u2=struct.unpack('<IIQQQQQQQQQQQ',d[:96])
base=96; fields=[]; names=[]
for i in range(fc):
    o=base+fo+i*24
    nlen,noff,typ,_,_2=struct.unpack('<QQHHI',d[o:o+24]); fields.append(typ)
    s=base+noff*2 if False else None
# field names live in the string block after the field table; best effort
strbase=base+fo+fc*24
def readname(off,ln):
    return d[strbase+off:strbase+off+ln*2].decode('utf-16le',errors='replace').rstrip('\x00') if ln else ''
names=[]
for i in range(fc):
    o=base+fo+i*24
    nlen,noff,typ,_,_2=struct.unpack('<QQHHI',d[o:o+24]); names.append(readname(noff,nlen) or f'f{i}')
print(name,rc,'rows;',' '.join(f'{n}:{t}' for n,t in zip(names,fields)))
for r in range(min(rc,maxrows)):
    p=base+ro+r*rs; q=p; vals=[]
    for t in fields:
        if t in (3,11): vals.append(struct.unpack('<I',d[q:q+4])[0]);q+=4
        elif t==4: vals.append(round(struct.unpack('<f',d[q:q+4])[0],3));q+=4
        else: vals.append(struct.unpack('<Q',d[q:q+8])[0]);q+=8
    print(' '.join(f'{n}={v}' for n,v in zip(names,vals) if v))
