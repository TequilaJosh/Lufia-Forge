"""build.py <clean rom> <asm> <out rom> [HOOK hex] [expand MB]: applies the asm (LoROM), printing old -> new bytes."""
import sys
from asm65 import assemble, lorom

src, asm, dst = sys.argv[1], sys.argv[2], sys.argv[3]
hook = int(sys.argv[4], 16) if len(sys.argv) > 4 else 0x218000
mb = float(sys.argv[5]) if len(sys.argv) > 5 else 0
d = bytearray(open(src, 'rb').read())
if mb and len(d) < int(mb * 0x100000):
    d += b'\x00' * (int(mb * 0x100000) - len(d))
recs, labels = assemble(open(asm).read(), {'HOOK': hook})
for a, b in recs:
    o = lorom(a)
    old = bytes(d[o:o + len(b)])
    print(f'{a:06X} {o:06X} {len(b):3d}: {old.hex(" ").upper()[:60]:<60} -> {b.hex(" ").upper()[:60]}')
    d[o:o + len(b)] = b
open(dst, 'wb').write(d)
for k, v in labels.items():
    if k != 'HOOK' and not k.startswith('_'):
        print(f'  {k} = {v:06X}')
