"""Render the Lufia 1 world map (map 01) from ROM data only.
Layout (unpacked map data): blocks (9-byte records) at 0x40 (240 of them), chunk table at 0x8B0 (4 block bytes per
chunk: TL TR BL BR), 160x128 u16 chunk grid at 0x336C (each chunk = 2x2 blocks = 32x32 px), extra chunk list at
0xD36C, object block at 0xD46C. Tiles: resource 0x124 (VRAM char base). Palette: rows 0-1 at 0x11300, rows 2-7 = map
palette 17 (0x17040 + 18*0xC0)."""
import sys
from lz import decompress, res_ptr, ROM
from PIL import Image
def w16(b, o): return b[o] | b[o + 1] << 8
def bgr(c): return ((c & 31) << 3, ((c >> 5) & 31) << 3, ((c >> 10) & 31) << 3)
pal = [bgr(w16(ROM, 0x11300 + 2 * i)) for i in range(32)] + [bgr(w16(ROM, 0x17040 + 18 * 0xC0 + 2 * i)) for i in range(96)]
md, _ = decompress(res_ptr(0xB2 + ROM[0xE19B + 1])); tiles, _ = decompress(res_ptr(0x124))
cache = {}
def tile(n):
    if n not in cache:
        b = tiles[n * 32:n * 32 + 32]
        if len(b) < 32: b = bytes(32)
        cache[n] = [[((b[2*y]>>(7-x))&1)|(((b[2*y+1]>>(7-x))&1)<<1)|(((b[16+2*y]>>(7-x))&1)<<2)|(((b[17+2*y]>>(7-x))&1)<<3) for x in range(8)] for y in range(8)]
    return cache[n]
bcache = {}
def block(m):
    if m not in bcache:
        im = Image.new('RGB', (16, 16), pal[0]); P = im.load(); rec = 0x40 + m * 9
        for k, (qx, qy) in enumerate(((0, 0), (0, 8), (8, 0), (8, 8))):
            e = w16(md, rec + 2 * k); t = tile(e & 0x3FF); p = (e >> 10) & 7
            for y in range(8):
                for x in range(8):
                    c = t[7 - y if e & 0x8000 else y][7 - x if e & 0x4000 else x]
                    if c: P[qx + x, qy + y] = pal[p * 16 + c]
        bcache[m] = im
    return bcache[m]
x0, y0, w, h = [int(a) for a in sys.argv[1:5]] if len(sys.argv) > 4 else (0, 0, 160, 128)
out = sys.argv[5] if len(sys.argv) > 5 else 'renders/map01_world.png'
img = Image.new('RGB', (w * 32, h * 32))
for cy in range(h):
    for cx in range(w):
        ch = w16(md, 0x336C + ((y0 + cy) * 160 + x0 + cx) * 2) & 0xFFF
        for q in range(4):
            img.paste(block(md[0x8B0 + ch * 4 + q]), (cx * 32 + (q & 1) * 16, cy * 32 + (q >> 1) * 16))
img.save(out); print('saved', out, img.size)
