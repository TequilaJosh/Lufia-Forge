"""Render every map from ROM data, using each map's captured VRAM/CGRAM for tiles and palette.

Usage: python render_all.py <capture_dir> <out_dir>
The capture dir holds mapXX_vram.bin / mapXX_cgram.bin from mapcapture.lua.
Yellow = NPCs (thin box = wander area), red = exits/doors (section A of the object block).
"""
import sys, os, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lz import decompress, res_ptr, ROM
from PIL import Image, ImageDraw

CAP, OUT = sys.argv[1], sys.argv[2]
os.makedirs(OUT, exist_ok=True)

def w16(b, o): return b[o] | b[o + 1] << 8
def u32(b, o): return b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24

def palette(cg):
    p = []
    for i in range(256):
        c = cg[2 * i] | cg[2 * i + 1] << 8
        p.append(((c & 31) << 3, ((c >> 5) & 31) << 3, ((c >> 10) & 31) << 3))
    return p

def tile_decoder(vram):
    cache = {}
    def tile(n):
        if n not in cache:
            b = vram[0x2000 + n * 32: 0x2000 + n * 32 + 32]
            if len(b) < 32: b = bytes(32)
            px = []
            for y in range(8):
                p0, p1, p2, p3 = b[2 * y], b[2 * y + 1], b[16 + 2 * y], b[16 + 2 * y + 1]
                px.append([((p0 >> (7 - x)) & 1) | (((p1 >> (7 - x)) & 1) << 1) |
                           (((p2 >> (7 - x)) & 1) << 2) | (((p3 >> (7 - x)) & 1) << 3) for x in range(8)])
            cache[n] = px
        return cache[n]
    return tile

def render(m):
    md, _ = decompress(res_ptr(0xB2 + ROM[0xE19B + m]))
    ts = md[8]
    blk, _ = decompress(res_ptr(0xB1 + ROM[0xE140 + ts]))
    w, h = w16(md, 0x12), w16(md, 0x14)
    start = u32(md, 0x28)
    vram = open(os.path.join(CAP, 'map%02X_vram.bin' % m), 'rb').read()
    pal = palette(open(os.path.join(CAP, 'map%02X_cgram.bin' % m), 'rb').read())
    tile = tile_decoder(vram)
    img = Image.new('RGB', (w * 16, h * 16), pal[0])
    P = img.load()
    # Blocks at or above header word 0x1E are composites: the game ($01:BEDF) splits each into a
    # ground block (BG2) and an overlay block (BG1) using a per-tileset table of word pairs at
    # [$01:E14B + tileset*3] + (block - threshold)*4.  Only when header 0x10 bit 1 is set,
    # 0x11 bit 7 is clear and 0x23 is zero.
    thr = w16(md, 0x1E)
    split = (md[0x10] & 2) and not (md[0x11] & 0x80) and md[0x23] == 0
    sp = ROM[0xE14B + ts * 3] | ROM[0xE14C + ts * 3] << 8 | ROM[0xE14D + ts * 3] << 16
    sp = (sp >> 16 & 0x7F) * 0x8000 + (sp & 0x7FFF)
    def draw(mt, mx, my):
        rec = blk[mt * 9: mt * 9 + 8]
        if len(rec) < 8: return
        for q, (qx, qy) in enumerate(((0, 0), (0, 8), (8, 0), (8, 8))):
            e = rec[2 * q] | rec[2 * q + 1] << 8
            t = tile(e & 0x3FF); pn = (e >> 10) & 7; hf = e & 0x4000; vf = e & 0x8000
            for y in range(8):
                row = t[7 - y if vf else y]
                for x in range(8):
                    c = row[7 - x if hf else x]
                    if c: P[mx * 16 + qx + x, my * 16 + qy + y] = pal[pn * 16 + c]
    for my in range(h):
        for mx in range(w):
            o = start + 2 * (my * w + mx)
            if o + 1 >= len(md): continue
            mt = (md[o] | md[o + 1] << 8) & 0x3FF
            if split and mt >= thr:
                e = sp + (mt - thr) * 4
                draw(w16(ROM, e) & 0x3FF, mx, my)
                draw(w16(ROM, e + 2) & 0x3FF, mx, my)
            else:
                draw(mt, mx, my)
    # objects
    info = dict(map=m, w=w, h=h, tileset=ts, exits=[], npcs=[])
    ob = u32(md, 0x30)
    if 0 < ob < len(md) - 24:
        t = md[ob:]
        offs = [w16(t, 8 + 2 * i) for i in range(8)]
        dr = ImageDraw.Draw(img)
        A = t[offs[0]:offs[1]]
        for i in range(len(A) // 12):
            r = A[i * 12:i * 12 + 12]
            x1, y1, x2, y2 = w16(r, 4), w16(r, 6), w16(r, 8), w16(r, 10)
            info['exits'].append(dict(i=i, raw=r.hex(), x1=x1, y1=y1, x2=x2, y2=y2))
            dr.rectangle([x1 * 16, y1 * 16, x2 * 16 + 15, y2 * 16 + 15], outline=(255, 0, 0), width=3)
            dr.text((x1 * 16 + 2, y1 * 16 + 2), 'E%d' % i, fill=(255, 255, 255))
        C = t[offs[2]:offs[3]]
        for i in range(len(C) // 14):
            r = C[i * 14:i * 14 + 14]
            if r[0] == 0xFF: continue
            x, y = w16(r, 2), w16(r, 4)
            bx1, by1, bx2, by2 = w16(r, 6), w16(r, 8), w16(r, 10), w16(r, 12)
            info['npcs'].append(dict(i=i, sprite=r[0], flags=r[1], x=x, y=y, box=[bx1, by1, bx2, by2]))
            if (bx2 - bx1) > 1 or (by2 - by1) > 1:
                dr.rectangle([bx1 * 16, by1 * 16, bx2 * 16 + 15, by2 * 16 + 15], outline=(255, 255, 0), width=1)
            dr.rectangle([x * 16, y * 16, x * 16 + 15, y * 16 + 15], outline=(255, 255, 0), width=3)
            dr.text((x * 16 + 1, y * 16 + 2), str(i), fill=(255, 255, 0))
    img.save(os.path.join(OUT, 'map%02X.png' % m))
    return info

catalog = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'map_catalog.json')))
results, failed = [], []
for entry in catalog:
    m = entry['map']
    if m == 0x01:            # world map uses a different format
        continue
    try:
        results.append(render(m))
    except Exception as e:
        failed.append((m, repr(e)))
json.dump(results, open(os.path.join(OUT, 'maps_objects.json'), 'w'), indent=1)
print('rendered', len(results), 'maps; failed', failed)
