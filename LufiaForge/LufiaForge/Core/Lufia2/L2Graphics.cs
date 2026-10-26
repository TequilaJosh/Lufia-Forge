namespace LufiaForge.Core.Lufia2;

/// <summary>Lufia II pictures: monster battle sprites (LEdit's L2MonsterSprite, US file numbers).</summary>
public static class L2Graphics
{
    /// <summary>Monster sprite n (1-based, as a monster record names it) is file 0x1B6 + n; its size in 8x8 tiles is
    /// in a table at $97:CA64 (2 bytes: tiles across, tiles down in the low 5 bits).</summary>
    public const int MonsterSpriteFileBase = 0x1B6, MonsterSpriteSizes = 0xBCA64, MonsterSpriteCount = 0x86;

    /// <summary>Map sprites (people, monsters, objects): a u16 per sprite at $CF:F000 (file 0x27F000) to a 5-byte entry
    /// in the same bank (new sprites FB-FD: <see cref="L2Sprites"/>): flags (01 tall: 4 tiles high, 02 wide: 4 tiles across, 08 two frames, 10 six frames), palette
    /// row (of the 8 at file 0x133F00), and the 24-bit address of plain 4bpp tiles. Frame f's tile (x, y) =
    /// f*n + (y&amp;1)*(n/2) + (y&gt;&gt;1)*width + x, n = the frame's tile count (LEdit's L2MapSprite).</summary>
    public const int MapSpriteTable = 0x27F000, MapSpritePalettes = 0x133F00, MapSpriteCount = 251;

    /// <summary>Map sprite <paramref name="sprite"/>'s first frame (facing the screen), ARGB; null when out of range.</summary>
    public static (int Width, int Height, uint[] Pixels)? MapSprite(RomBuffer rom, int sprite, int frame = 0)
    {
        if (sprite < 0 || sprite >= L2Sprites.Count(rom)) return null;
        int e = L2Sprites.EntryAt(rom, sprite);
        int flags = rom.ReadByte(e), row = rom.ReadByte(e + 1) & 7;
        int gfx = L2Rom.SnesToFile(rom.ReadByte(e + 2) | rom.ReadByte(e + 3) << 8 | rom.ReadByte(e + 4) << 16);
        int fx = (flags & 2) != 0 ? 4 : 2, fy = (flags & 1) != 0 ? 4 : 2, n = fx * fy;
        int p = L2Sprites.PaletteOf(rom, sprite), pal = p > 0 ? L2Sprites.PaletteAt(rom, p) : MapSpritePalettes + row * 32;
        var colours = new uint[16];
        for (int c = 1; c < 16; c++)
        {
            int v = rom.ReadUInt16Le(pal + c * 2);
            colours[c] = 0xFF000000u | (uint)((v & 31) * 255 / 31) << 16 | (uint)((v >> 5 & 31) * 255 / 31) << 8 | (uint)((v >> 10 & 31) * 255 / 31);
        }
        int w = fx * 8, h = fy * 8;
        var px = new uint[w * h];
        for (int y = 0; y < fy; y++)
            for (int x = 0; x < fx; x++)
            {
                int t = frame * n + (y & 1) * (n / 2) + (y >> 1) * fx + x;
                int at = gfx + t * 32;
                if (at + 32 > rom.Length) continue;
                for (int r = 0; r < 8; r++)
                {
                    byte p0 = rom.ReadByte(at + r * 2), p1 = rom.ReadByte(at + r * 2 + 1), p2 = rom.ReadByte(at + 0x10 + r * 2), p3 = rom.ReadByte(at + 0x11 + r * 2);
                    for (int c = 0; c < 8; c++)
                    {
                        int bit = 7 - c;
                        int ci = (p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3;
                        px[(y * 8 + r) * w + x * 8 + c] = colours[ci];
                    }
                }
            }
        return (w, h, px);
    }

    /// <summary>A monster sprite as ARGB pixels (palette <paramref name="palette"/> of the file's own palettes;
    /// colour 0 see-through). Null for a sprite number out of range.</summary>
    public static (int Width, int Height, uint[] Pixels)? MonsterSprite(RomBuffer rom, int sprite, int palette)
    {
        if (sprite < 1 || sprite > MonsterSpriteCount) return null;
        int tx = rom.ReadByte(MonsterSpriteSizes + (sprite - 1) * 2), ty = rom.ReadByte(MonsterSpriteSizes + (sprite - 1) * 2 + 1) & 0x1F;
        if (tx == 0 || ty == 0) return null;
        byte[] f = L2Rom.ReadFile(rom, MonsterSpriteFileBase + sprite);
        int pals = f[0];
        if (pals == 0) return null;
        palette = Math.Clamp(palette, 0, pals - 1);
        var colours = new uint[16];
        for (int c = 0; c < 16; c++)
        {
            int v = f[1 + palette * 32 + c * 2] | f[2 + palette * 32 + c * 2] << 8;
            colours[c] = c == 0 ? 0u : 0xFF000000u | (uint)((v & 31) * 255 / 31) << 16 | (uint)((v >> 5 & 31) * 255 / 31) << 8 | (uint)((v >> 10 & 31) * 255 / 31);
        }
        int tiles = 1 + pals * 32;
        int w = tx * 8, h = ty * 8;
        var px = new uint[w * h];
        for (int y = 0; y < ty; y++)
            for (int x = 0; x < tx; x++)
            {
                // 16x16 blocks row by row, each stored top left, top right, bottom left, bottom right
                int t = ((y & 1) + 2 * (x >> 1) + (y >> 1) * tx) * 2 + (x & 1);
                int at = tiles + t * 32;
                if (at + 32 > f.Length) continue;
                for (int r = 0; r < 8; r++)
                {
                    byte p0 = f[at + r * 2], p1 = f[at + r * 2 + 1], p2 = f[at + 0x10 + r * 2], p3 = f[at + 0x11 + r * 2];
                    for (int c = 0; c < 8; c++)
                    {
                        int bit = 7 - c;
                        int ci = (p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3;
                        px[(y * 8 + r) * w + x * 8 + c] = colours[ci];
                    }
                }
            }
        return (w, h, px);
    }
}
