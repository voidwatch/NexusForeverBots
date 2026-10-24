#!/usr/bin/env python3
"""Read-only reader for WildStar ClientData.index/.archive (PACK/AIDX/AARC), ported from
The-Ham-Sandwich src/Archive.cpp. Usage: wsarchive.py ls <glob> | cat <path> <outfile>"""
import struct, zlib, lzma, sys, os, fnmatch

PATCH = os.environ.get('PATCH', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'client', 'Patch'))

def _pack_blocks(f):
    f.seek(0)
    sig, ver = struct.unpack('<II', f.read(8))
    assert sig == 0x5041434B and ver == 1, 'not a PACK file'
    hdr = f.read(548)
    start = struct.unpack_from('<Q', hdr, 528)[0]
    count = struct.unpack_from('<I', hdr, 536)[0]
    f.seek(start)
    raw = f.read(count * 16)
    return [struct.unpack_from('<QQ', raw, i * 16) for i in range(count)]

class Archive:
    def __init__(self, index_path=os.path.join(PATCH, 'ClientData.index')):
        self.idx = open(index_path, 'rb')
        self.pk = open(index_path[:-len('.index')] + '.archive', 'rb')
        self.iblocks = _pack_blocks(self.idx)
        self.files = {}   # lower path -> (flags, usize, csize, hash, path)
        root = None
        for off, size in self.iblocks:
            if size < 16: continue
            self.idx.seek(off)
            magic, ver, unk, rootblk = struct.unpack('<IIII', self.idx.read(16))
            if magic == 0x41494458:
                root = rootblk; break
        assert root is not None, 'no AIDX'
        self._walk(root, '')
        self.pblocks = _pack_blocks(self.pk)
        self.aarc = {}
        for off, size in self.pblocks:
            if size < 16: continue
            self.pk.seek(off)
            magic, ver, n, tblk = struct.unpack('<IIII', self.pk.read(16))
            if magic == 0x41415243:
                self.pk.seek(self.pblocks[tblk][0])
                raw = self.pk.read(n * 32)
                for i in range(n):
                    blk, h, us = struct.unpack_from('<I20sQ', raw, i * 32)
                    self.aarc[h] = blk
                break

    def _walk(self, blk, prefix):
        off, size = self.iblocks[blk]
        self.idx.seek(off)
        data = self.idx.read(size)
        nd, nf = struct.unpack_from('<II', data, 0)
        names = data[8 + nd * 8 + nf * 56:]
        def name(o): return names[o:names.index(b'\0', o)].decode('latin1')
        p = 8
        dirs = []
        for _ in range(nd):
            no, nb = struct.unpack_from('<II', data, p); p += 8
            dirs.append((name(no), nb))
        for _ in range(nf):
            no, = struct.unpack_from('<I', data, p)
            flags, unk, us, cs = struct.unpack_from('<IQQQ', data, p + 4)
            h = data[p + 32:p + 52]
            p += 56
            path = prefix + name(no)
            self.files[path.lower()] = (flags, us, cs, h, path)
        for n, nb in dirs:
            self._walk(nb, prefix + n + '/')

    def ls(self, pattern):
        pattern = pattern.replace('\\', '/').lower()
        return [v[4] for k, v in self.files.items() if fnmatch.fnmatch(k, pattern)]

    def read(self, path):
        e = self.files.get(path.replace('\\', '/').lower())
        if e is None: return None
        flags, us, cs, h, _ = e
        blk = self.aarc.get(h)
        if blk is None: return None
        off, size = self.pblocks[blk]
        self.pk.seek(off)
        c = self.pk.read(size)
        if flags == 3:
            return zlib.decompress(c)
        if len(c) > 5 and c[0] == 0x5D:
            # LZMA1 raw: rebuild the .lzma "alone" header (5 props + 8 byte size)
            return lzma.decompress(c[:5] + struct.pack('<Q', us) + c[5:], format=lzma.FORMAT_ALONE)[:us]
        return c

if __name__ == '__main__':
    a = Archive()
    if sys.argv[1] == 'ls':
        r = sorted(a.ls(sys.argv[2]))
        for p in r[:int(sys.argv[3]) if len(sys.argv) > 3 else 40]: print(p, a.files[p.lower()][1])
        print(len(r), 'files')
    elif sys.argv[1] == 'cat':
        d = a.read(sys.argv[2]); open(sys.argv[3], 'wb').write(d); print(len(d), 'bytes')
