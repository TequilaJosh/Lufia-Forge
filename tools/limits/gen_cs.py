"""gen_cs.py <asm> <clean rom> : prints C# data for HookPatch (sites with their original bytes, the added block, and
the places holding a 24-bit address inside the block, stored as the offset into it)."""
import sys
from asm65 import assemble, lorom

asm, romp = sys.argv[1], sys.argv[2]
rom = open(romp, 'rb').read()
src = open(asm).read()
A, B = 0x218000, 0x3FC120
ra, _ = assemble(src, {'HOOK': A})
rb, _ = assemble(src, {'HOOK': B})
assert [a if a != A else 0 for a, _ in ra] == [a if a != B else 0 for a, _ in rb]


def relocs(ba, bb):
    """positions p where bytes p..p+2 hold base + offset (A in ba, B in bb) -> [(p, offset)], bytes with the offset"""
    out, fixed = [], bytearray(ba)
    p = 0
    while p < len(ba):
        if ba[p] != bb[p]:
            p0 = p
            while p0 > 0 and p0 > p - 2 and ba[p0 - 1] != bb[p0 - 1]:
                p0 -= 1
            # find the 3-byte window containing the diff that decodes to base + same offset
            for s in (p - 2, p - 1, p):
                if s < 0 or s + 3 > len(ba):
                    continue
                va = int.from_bytes(ba[s:s + 3], 'little') - A
                vb = int.from_bytes(bb[s:s + 3], 'little') - B
                if va == vb and 0 <= va < 0x8000:
                    out.append((s, va))
                    fixed[s:s + 3] = va.to_bytes(3, 'little')
                    p = s + 3
                    break
            else:
                raise SystemExit(f'unmatched difference at {p}')
        else:
            p += 1
    return out, bytes(fixed)


def hx(b):
    return ' '.join(f'{x:02X}' for x in b)


block = None
sites = []
for (addr, ba), (_, bb) in zip(ra, rb):
    if addr == A:
        rl, fixed = relocs(ba, bb)
        block = (fixed, rl)
        continue
    rl, fixed = relocs(ba, bb)
    off = lorom(addr)
    orig = rom[off:off + len(ba)]
    sites.append((off, addr, orig, fixed, rl))

print('    private static readonly HookPatch.Site[] Sites =\n    {')
for off, addr, orig, fixed, rl in sites:
    r = ', '.join(str(p) for p, _ in rl)
    print(f'        new(0x{off:05X}, "{hx(orig)}", "{hx(fixed)}"{", " + r if r else ""}),   // ${addr >> 16:02X}:{addr & 0xFFFF:04X}')
print('    };')
fixed, rl = block
print(f'    private static readonly HookPatch.Block Code = new("{hx(fixed)}"{", " + ", ".join(str(p) for p, _ in rl) if rl else ""});')
print(f'// {len(sites)} sites, block {len(fixed)} bytes, {len(rl)} block relocations', file=sys.stderr)
