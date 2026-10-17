# Emoticon bubbles for Lufia cutscenes: 16x16, 4bpp-friendly (one 16-colour sprite palette, colour 0 = see-through).
# Each emotion is drawn as text art: one character per pixel.
from PIL import Image
import os

OUT = os.path.dirname(os.path.abspath(__file__))

# palette (SNES BGR555 friendly): index -> RGB
PAL = {
    '.': None,                  # 0 transparent
    'K': (24, 16, 40),          # 1 outline (dark purple-black)
    'W': (248, 248, 248),       # 2 bubble white
    'G': (184, 184, 200),       # 3 bubble shade
    'R': (232, 48, 56),         # 4 red (heart, anger)
    'r': (152, 24, 40),         # 5 dark red
    'B': (72, 136, 248),        # 6 blue (sweat, tears)
    'b': (40, 72, 184),         # 7 dark blue
    'Y': (248, 208, 56),        # 8 yellow (light bulb, sparkle)
    'y': (200, 136, 24),        # 9 dark yellow
    'P': (248, 152, 184),       # 10 pink (blush)
    'D': (64, 56, 80),          # 11 dark grey (symbols)
    'L': (120, 112, 136),       # 12 mid grey
    'g': (72, 184, 72),         # 13 green (music)
    'O': (248, 128, 40),        # 14 orange
    'w': (216, 224, 248),       # 15 pale blue (shine)
}
ORDER = ['.', 'K', 'W', 'G', 'R', 'r', 'B', 'b', 'Y', 'y', 'P', 'D', 'L', 'g', 'O', 'w']

# the bubble: 16x16 with a tail at the bottom left; symbols are drawn inside rows 1..11, cols 2..13
BUBBLE = [
    "....KKKKKKKK....",
    "..KKWWWWWWWWKK..",
    ".KWWWWWWWWWWWWK.",
    ".KWWWWWWWWWWWWK.",
    "KWWWWWWWWWWWWWWK",
    "KWWWWWWWWWWWWWWK",
    "KWWWWWWWWWWWWWWK",
    "KWWWWWWWWWWWWWWK",
    "KWWWWWWWWWWWWWWK",
    ".KWWWWWWWWWWWWK.",
    ".KGWWWWWWWWWWGK.",
    "..KKGGWWWWGGKK..",
    "....KKKWWKKKK...",
    "......KWK.......",
    ".....KWK........",
    ".....KK.........",
]

# symbols: 12 wide x 10 tall, placed at (2, 1); '.' keeps the bubble
SYM = {
 'Exclamation': [
    "....KK......",
    "....RR......",
    "....RR......",
    "....RR......",
    "....RR......",
    "....rr......",
    "............",
    "....RR......",
    "....rr......",
    "............"],
 'Question': [
    "...KKKK.....",
    "..KBBBBK....",
    "..Kb..BBK...",
    "......BBK...",
    ".....BBK....",
    "....BBK.....",
    "....bb......",
    "............",
    "....BB......",
    "....bb......"],
 'Heart': [
    "............",
    "..KK...KK...",
    ".KRRK.KRRK..",
    ".KRwRKRRRK..",
    ".KRRRRRRRK..",
    "..KRRRRRK...",
    "...KRRRK....",
    "....KRK.....",
    ".....K......",
    "............"],
 'Laugh': [
    "............",
    "..KK....KK..",
    ".K..K..K..K.",
    "............",
    "..KKKKKKKK..",
    "..KrrrrrrK..",
    "..KrRRRRrK..",
    "...KrRRrK...",
    "....KKKK....",
    "............"],
 'Tears': [
    "............",
    ".KKK....KKK.",
    "............",
    ".B........B.",
    ".B..KKKK..B.",
    "BwB.K..K.BwB",
    "BBB......BBB",
    ".b........b.",
    "............",
    "............"],
 'Anger': [
    "............",
    "...R....R...",
    "..RRr..rRR..",
    ".RRr....rRR.",
    "............",
    "............",
    ".RRr....rRR.",
    "..RRr..rRR..",
    "...R....R...",
    "............"],
 'Sweat': [
    "........K...",
    ".......KBK..",
    "......KBwBK.",
    "......KBBBK.",
    "......KBBbK.",
    ".......KbK..",
    "............",
    "............",
    "............",
    "............"],
 'Music': [
    "......KKKK..",
    "......KggK..",
    "......K..K..",
    "......K..K..",
    "......K..K..",
    "....KgK.KgK.",
    "...KgggKggg.",
    "...KgggKggg.",
    "....KKK.KKK.",
    "............"],
 'Idea': [
    "....KKKK....",
    "...KYYYYK...",
    "..KYwYYYYK..",
    "..KYwYYYYK..",
    "..KYYYYYYK..",
    "...KYYYYK...",
    "....KyyK....",
    "....KLLK....",
    "....KLLK....",
    ".....KK....."],
 'Sleep': [
    "........KKKK",
    "..........K.",
    ".........K..",
    "....KKK.KKKK",
    "......K.....",
    ".....K......",
    "KKK.KKK.....",
    "..K.........",
    ".K..........",
    "KKK........."],
 'Silence': [
    "............",
    "............",
    "............",
    "............",
    ".KK..KK..KK.",
    ".DD..DD..DD.",
    "............",
    "............",
    "............",
    "............"],
 'Shock': [
    "............",
    "..KKK..KKK..",
    ".K.w.KK.w.K.",
    ".K.D.KK.D.K.",
    "..KKK..KKK..",
    "............",
    ".....KK.....",
    "....K..K....",
    ".....KK.....",
    "............"],
}

def emote(name):
    rows = [list(r) for r in BUBBLE]
    for y, line in enumerate(SYM[name]):
        for x, c in enumerate(line):
            if c != '.': rows[1 + y][2 + x] = c
    return [''.join(r) for r in rows]

def image(rows, scale=1):
    im = Image.new('RGBA', (16, 16), (0, 0, 0, 0))
    for y, r in enumerate(rows):
        for x, c in enumerate(r):
            rgb = PAL[c]
            if rgb: im.putpixel((x, y), rgb + (255,))
    return im.resize((16 * scale, 16 * scale), Image.NEAREST)

if __name__ == '__main__':
    names = list(SYM)
    for n in names: image(emote(n)).save(os.path.join(OUT, f'emote_{n.lower()}.png'))
    # a sheet for people: each emotion at 6x on a dark and a light background, with its number
    from PIL import ImageDraw
    cols, s, pad = 6, 6, 18
    w = cols * (16 * s + pad) + pad
    h = (len(names) + cols - 1) // cols * (16 * s + pad + 22) + pad
    sheet = Image.new('RGB', (w, h), (46, 30, 74))
    d = ImageDraw.Draw(sheet)
    for i, n in enumerate(names):
        x = pad + (i % cols) * (16 * s + pad); y = pad + (i // cols) * (16 * s + pad + 22)
        d.rectangle([x - 4, y - 4, x + 16 * s + 3, y + 16 * s + 3], fill=(32, 44, 32))
        sheet.paste(image(emote(n), s), (x, y), image(emote(n), s))
        d.text((x, y + 16 * s + 6), f'{i:02X} {n}', fill=(240, 200, 100))
    sheet.save(os.path.join(OUT, 'emotes_sheet.png'))
    print('ok', len(names))
