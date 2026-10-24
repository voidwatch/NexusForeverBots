#!/usr/bin/env python3
"""Summarise upstream NexusForever (game_rework) message models in research/protocol/upstream into one line per message:
name, opcode, field reads/writes with bit widths. Output research/protocol/layouts.tsv; prints a count."""
import re, os, glob
base = os.path.join(os.path.dirname(__file__), '../../research/protocol')
ops = dict(re.findall(r'(\w+)\s*=\s*(0x[0-9A-Fa-f]+)', open(f'{base}/op-game_rework.cs').read()))
out = []
for f in sorted(glob.glob(f'{base}/upstream/*.cs')):
    name = os.path.basename(f)[:-3]
    s = open(f, encoding='utf-8-sig').read()
    calls = re.findall(r'(\w+)\s*=\s*reader\.(Read\w+)(?:<(\w+)>)?\(([^)]*)\)', s) or []
    fields = [f'{n}:{t}{"<"+g+">" if g else ""}({a})' for n, t, g, a in calls]
    if not fields:
        fields = [f'{a.split(",")[0].strip()}{":bits="+a.split(",")[1].strip() if "," in a else ""}' for a in re.findall(r'writer\.Write\w*\(([^;]*)\);', s)]
    sub = re.findall(r'(\w+)\.(Read|Write)\((?:reader|writer)\)', s)
    if sub: fields += [f'{n}.{m}()' for n, m in sub]
    out.append(f'{name}\t{ops.get(name, "?")}\t{" ".join(fields)}')
open(f'{base}/layouts.tsv', 'w').write('\n'.join(out) + '\n')
print(len(out), 'messages ->', 'research/protocol/layouts.tsv')
