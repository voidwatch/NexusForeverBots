#!/usr/bin/env python3
"""Read WildStar game tables (.tbl) and LocalizedText from data/tbl, offline.

    from tbl import load, text
    rows = load('QuestObjective')   # list of dicts, field names from the table
    text(1234)                      # en-US localized string

CLI: tbl.py <Table> [id ...]   prints rows (capped at 20).
"""
import os, struct, sys

TBL_DIR = os.path.join(os.path.dirname(__file__), '..', '..', 'data', 'tbl')
_cache = {}


def _utf16z(buf, off):
    end = off
    while end + 1 < len(buf) and (buf[end] or buf[end + 1]):
        end += 2
    return buf[off:end].decode('utf-16-le', 'replace')


def load(name):
    if name in _cache:
        return _cache[name]
    with open(os.path.join(TBL_DIR, name + '.tbl'), 'rb') as f:
        b = f.read()
    H = 0x60
    (sig, ver, name_len, _u1, rec_size, field_count, field_off, rec_count,
     total_rec_size, rec_off, max_id, lookup_off, _u2) = struct.unpack_from('<IIQQQQQQQQQQQ', b, 0)
    assert sig == 0x4454424C, name
    fields = []
    fbase = H + field_off
    names_base = fbase + field_count * 24
    if names_base % 16:
        names_base += 16 - names_base % 16
    for i in range(field_count):
        nlen, noff, typ, _u, _u3 = struct.unpack_from('<QQHHI', b, fbase + i * 24)
        fields.append((_utf16z(b, names_base + noff), typ))
    rows = []
    rbase = H + rec_off
    for r in range(rec_count):
        p = rbase + r * rec_size
        o = 0
        row = {}
        for idx, (fname, typ) in enumerate(fields):
            if typ == 3:
                row[fname] = struct.unpack_from('<I', b, p + o)[0]; o += 4
            elif typ == 4:
                row[fname] = struct.unpack_from('<f', b, p + o)[0]; o += 4
            elif typ == 11:
                row[fname] = struct.unpack_from('<I', b, p + o)[0] != 0; o += 4
            elif typ == 20:
                if o % 8: o += 8 - o % 8
                row[fname] = struct.unpack_from('<Q', b, p + o)[0]; o += 8
            elif typ == 130:
                if o % 8: o += 8 - o % 8
                o1, o2 = struct.unpack_from('<II', b, p + o); o += 8
                row[fname] = _utf16z(b, rbase + max(o1, o2)) if max(o1, o2) else ''
            else:
                raise ValueError(f'{name}: type {typ}')
        rows.append(row)
    _cache[name] = rows
    return rows


def index(name):
    key = '_idx_' + name
    if key not in _cache:
        rows = load(name)
        k = next(iter(rows[0]))
        _cache[key] = {r[k]: r for r in rows}
    return _cache[key]


_text = None


def text(tid):
    global _text
    if _text is None:
        with open(os.path.join(TBL_DIR, 'en-US.bin'), 'rb') as f:
            b = f.read()
        hdr = struct.unpack_from('<IIII' + 'Q' * 10, b, 0)
        rec_count, rec_off, st_len, st_off = hdr[10], hdr[11], hdr[12], hdr[13]
        H = 0x60
        _text = {}
        sbase = H + st_off
        for i in range(rec_count):
            i_d, off = struct.unpack_from('<II', b, H + rec_off + i * 8)
            _text[i_d] = _utf16z(b, sbase + off * 2)
    return _text.get(tid, '')


if __name__ == '__main__':
    rows = load(sys.argv[1])
    ids = set(int(x) for x in sys.argv[2:])
    k = next(iter(rows[0]))
    n = 0
    for r in rows:
        if ids and r[k] not in ids:
            continue
        print(r); n += 1
        if n >= 20: break
    print(f'# {len(rows)} rows', file=sys.stderr)


# Minimal positional reader (be71fea): read(name) -> (field types, {id: [values]}), used by openwhy.py and the local client analysis scripts
def read(name, root='data/tbl'):
    d = open(f'{root}/{name}.tbl', 'rb').read()
    sig, ver, nl, u1, rs, fc, fo, rc, trs, ro, mx, lo, u2 = struct.unpack('<IIQQQQQQQQQQQ', d[:96])
    base = 96
    fields = []
    for i in range(fc):
        o = base + fo + i * 24
        nlen, noff, typ, _, _2 = struct.unpack('<QQHHI', d[o:o + 24])
        fields.append(typ)
    rows = {}
    for r in range(rc):
        p = base + ro + r * rs
        q = p
        vals = []
        for t in fields:
            if t in (3, 11):
                vals.append(struct.unpack('<I', d[q:q + 4])[0]); q += 4
            elif t == 4:
                vals.append(struct.unpack('<f', d[q:q + 4])[0]); q += 4
            elif t == 130:
                if (q - p) % 8: q += 4
                off = struct.unpack('<Q', d[q:q + 8])[0]; q += 8
                s = base + ro + off
                e = s
                while e + 1 < len(d) and d[e:e + 2] != b'\0\0':
                    e += 2
                vals.append(d[s:e].decode('utf-16le', errors='replace'))
            else:
                if (q - p) % 8: q += 4
                vals.append(struct.unpack('<Q', d[q:q + 8])[0]); q += 8
        rows[vals[0]] = vals
    return fields, rows
