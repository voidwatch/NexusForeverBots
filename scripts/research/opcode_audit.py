#!/usr/bin/env python3
"""Opcode coverage audit: every message the retail client can send (registry entries with a writer, research/clientre/registry.tsv)
against the server's handlers ([MessageHandler(GameMessageOpcode.X)] in Source), and every message the client can read against the
server's writable models. Names: this repo's GameMessageOpcode, else upstream game_rework, else the client Lua function that sends it.
Writes docs/OPCODE-COVERAGE.md; prints a summary (max 30 lines)."""
import re, glob, collections
reg = [l.rstrip('\n').split('\t') for l in open('research/clientre/registry.tsv')]
client = {}; server = {}
for r in reg:
    op = int(r[0], 16)
    if r[2] != '-' and r[3] not in ('-', '0x0'): client.setdefault(op, r[6])
    if r[4] not in ('-', '0x0'): server.setdefault(op, r[6])
def enum(path):
    d = {}
    for m in re.finditer(r'(\w+)\s*=\s*0x([0-9A-Fa-f]+)', open(path, encoding='utf-8-sig').read()):
        d.setdefault(int(m.group(2), 16), m.group(1))
    return d
ours = enum('Source/NexusForever.Shared/Network/Message/GameMessageOpcode.cs')
up = enum('research/protocol/op-game_rework.cs')
lua = collections.defaultdict(set)
for l in open('research/clientre/lua-tables.tsv'):
    p = l.rstrip('\n').split('\t')
    if len(p) > 4 and p[4]:
        for o in p[4].split():
            if o != '-0x1': lua[int(o, 16)].add(p[2])
for l in open('research/clientre/lua-sends.tsv'):
    p = l.rstrip('\n').split('\t')
    for o in p[2].split():
        if o != '-0x1': lua[int(o, 16)].add(p[0])
handled = set(); writable = set()
for f in glob.glob('Source/**/*.cs', recursive=True):
    t = open(f, encoding='utf-8-sig', errors='replace').read()
    handled |= set(re.findall(r'MessageHandler\(GameMessageOpcode\.(\w+)\)', t))
    for m in re.finditer(r'\[Message\(GameMessageOpcode\.(\w+)\)\]\s*public\s+(?:sealed\s+)?class\s+\w+\s*:\s*([\w, ]+)', t):
        if 'IWritable' in m.group(2): writable.add(m.group(1))
def name(op): return ours.get(op) or (up.get(op, '') + ' (upstream)' if op in up else '')
rows_c = []
for op in sorted(client):
    n = ours.get(op)
    status = 'handled' if n and n in handled else ('enum only' if n else ('upstream name' if op in up else 'unnamed'))
    rows_c.append((op, name(op), status, ', '.join(sorted(lua.get(op, [])))[:80]))
cnt = collections.Counter(r[2] for r in rows_c)
rows_s = []
for op in sorted(server):
    n = ours.get(op)
    rows_s.append((op, name(op), 'model' if n and n in writable else ('enum only' if n else 'none')))
cnts = collections.Counter(r[2] for r in rows_s)
with open('docs/OPCODE-COVERAGE.md', 'w') as w:
    w.write('# Opcode coverage (scripts/research/opcode_audit.py)\n\n')
    w.write('Client messages = registry entries with a writer in the retail client (research/clientre/registry.tsv); server messages = entries with a reader. '
            'Handled = a [MessageHandler] exists here. Lua = client API functions that send the opcode (static analysis, depth 5). '
            'Live counts of client messages without a handler: ledger category `system-unhandled-message`.\n\n')
    w.write(f'Client messages: {len(rows_c)}: ' + ', '.join(f'{k} {v}' for k, v in cnt.most_common()) + '\n\n')
    w.write(f'Server messages: {len(rows_s)}: ' + ', '.join(f'{k} {v}' for k, v in cnts.most_common()) + '\n\n')
    w.write('## Client messages without a handler\n| opcode | name | status | sent by (Lua) |\n|---|---|---|---|\n')
    for r in rows_c:
        if r[2] != 'handled': w.write(f'| 0x{r[0]:04X} | {r[1]} | {r[2]} | {r[3]} |\n')
    w.write('\n## Client messages with a handler\n')
    w.write(', '.join(f'0x{r[0]:04X} {r[1]}' for r in rows_c if r[2] == 'handled') + '\n')
print('client messages', len(rows_c), dict(cnt))
print('server messages', len(rows_s), dict(cnts))
unh = [r for r in rows_c if r[2] != 'handled' and r[3]]
print('unhandled client messages sent by a Lua API:', len(unh))
for r in unh[:20]: print(f'  0x{r[0]:04X} {r[1][:40]:40} {r[3][:60]}')
