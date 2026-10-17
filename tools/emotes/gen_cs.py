# Writes Core/Maps/EmoteGraphics.cs: the 12 emote bubbles as SNES 4bpp tiles (4 tiles of 8x8 each:
# top-left, top-right, bottom-left, bottom-right) and their 16-colour palette (BGR555).
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from make_emotes import SYM, PAL, ORDER, emote

def tile(rows, tx, ty):
    out = bytearray(32)
    for y in range(8):
        for x in range(8):
            c = ORDER.index(rows[ty * 8 + y][tx * 8 + x])
            bit = 7 - x
            if c & 1: out[2 * y] |= 1 << bit
            if c & 2: out[2 * y + 1] |= 1 << bit
            if c & 4: out[16 + 2 * y] |= 1 << bit
            if c & 8: out[16 + 2 * y + 1] |= 1 << bit
    return out

names = list(SYM)
data = bytearray()
for n in names:
    r = emote(n)
    for ty in range(2):
        for tx in range(2):
            data += tile(r, tx, ty)
pal = []
for k in ORDER:
    rgb = PAL[k] or (0, 0, 0)
    pal.append((rgb[0] >> 3) | (rgb[1] >> 3) << 5 | (rgb[2] >> 3) << 10)

def hexrows(b, per=32):
    return '\n'.join('        ' + ', '.join(f'0x{x:02X}' for x in b[i:i + per]) + ',' for i in range(0, len(b), per))

cs = f'''namespace LufiaForge.Core.Maps;

/// <summary>
/// The emote bubbles of the cutscene command 6F (see <see cref="Battle.EmotePatch"/>): 16x16, drawn as four 8x8
/// sprite tiles each (top-left, top-right, bottom-left, bottom-right, 4bpp), all in one 16-colour palette.
/// Generated from the pixel art in tools/emotes/make_emotes.py by tools/emotes/gen_cs.py.
/// </summary>
public static partial class EmoteGraphics
{{
    public static readonly string[] Names = {{ {', '.join('"' + n + '"' for n in names)} }};

    public const int BytesPerEmote = 128;

    /// <summary>SNES colours (BGR555), colour 0 = see-through.</summary>
    public static readonly ushort[] Palette = {{ {', '.join(f'0x{c:04X}' for c in pal)} }};

    public static readonly byte[] Tiles =
    {{
{hexrows(data)}
    }};
}}
'''
open(sys.argv[1], 'w', newline='\r\n').write(cs)
print(len(data), 'bytes,', len(names), 'emotes')
