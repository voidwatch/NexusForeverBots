#!/usr/bin/env python3
"""Mark game tables as loaded ([GameData]) after checking the model class has the same field count as the .tbl file.
   scripts/enable-tables.py Name1 Name2 ...    prints OK/MISMATCH per table and edits GameTableManager.cs for the OK ones."""
import re,struct,sys,os
mgr='Source/NexusForever.Shared/GameTable/GameTableManager.cs'
src=open(mgr).read()
for name in sys.argv[1:]:
    tbl=f'data/tbl/{name}.tbl'; cls=f'Source/NexusForever.Shared/GameTable/Model/{name}Entry.cs'
    if not os.path.exists(tbl) or not os.path.exists(cls): print(name,'missing tbl or class'); continue
    d=open(tbl,'rb').read(96)
    fc=struct.unpack('<IIQQQQQQQQQQQ',d)[5]
    text=open(cls).read()
    n=len(re.findall(r'public (?:uint|float|bool|ulong|string)(?:\[\])? \w+;',text))
    arr=re.findall(r'GameTableFieldArray\((\d+)\)\]\s*public \w+\[\] \w+;',text)
    n2=n-len(arr)+sum(int(a) for a in arr)
    key=f'        public GameTable<{name}Entry> {name} '
    if key not in src: print(name,'not declared'); continue
    if fc!=n2: print(name,f'MISMATCH table {fc} model {n2}'); continue
    if '[GameData]\n'+key in src: print(name,'already loaded'); continue
    src=src.replace(key,'        [GameData]\n'+key,1); print(name,'enabled')
open(mgr,'w').write(src)
