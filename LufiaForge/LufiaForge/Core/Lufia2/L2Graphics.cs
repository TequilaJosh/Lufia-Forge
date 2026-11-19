namespace LufiaForge.Core.Lufia2;

// lufia 2 pictures: monster battle sprites (LEdit's L2MonsterSprite, US file numbers)
public static class L2Graphics
{
    // monster sprite n (1-based, the way a monster record names it) is file 0x1B6 + n. its size in 8x8 tiles is in a
    // table at $97:CA64 (2 bytes: tiles across, tiles down in the low 5 bits). new sprites and the rest: L2MonsterSprites
    public const int MonsterSpriteFileBase = 0x1B6, MonsterSpriteSizes = 0xBCA64, MonsterSpriteCount = 0x86;

    // map sprites (people, monsters, objects): a u16 per sprite at $CF:F000 (file 0x27F000) to a 5-byte entry in the
    // same bank (new sprites FB-FD: L2Sprites): flags (01 tall: 4 tiles high, 02 wide: 4 tiles across, 08 two frames,
    // 10 six frames), palette row (of the 8 at file 0x133F00), and the 24-bit address of plain 4bpp tiles. frame f's
    // tile (x, y) = f*n + (y&1)*(n/2) + (y>>1)*width + x, n = the frame's tile count (LEdit's L2MapSprite)
    public const int MapSpriteTable = 0x27F000, MapSpritePalettes = 0x133F00, MapSpriteCount = 251;

    // map sprite sprite's first frame (facing the screen), ARGB. null if out of range
    public static (int Width, int Height, uint[] Pixels)? MapSprite(RomBuffer rom, int sprite, int frame = 0)
    {
        if (sprite < 0 || sprite >= L2Sprites.Count(rom)) return null;
        int entry = L2Sprites.EntryAt(rom, sprite);
        int flags = rom.ReadByte(entry), row = rom.ReadByte(entry + 1) & 7;
        int gfx = L2Rom.SnesToFile(rom.ReadByte(entry + 2) | rom.ReadByte(entry + 3) << 8 | rom.ReadByte(entry + 4) << 16);
        int tilesAcross = (flags & 2) != 0 ? 4 : 2, tilesDown = (flags & 1) != 0 ? 4 : 2, frameTiles = tilesAcross * tilesDown;
        int palette = L2Sprites.PaletteOf(rom, sprite), paletteAt = palette > 0 ? L2Sprites.PaletteAt(rom, palette) : MapSpritePalettes + row * 32;
        var colours = new uint[16];
        for (int c = 1; c < 16; c++)
        {
            int bgr = rom.ReadUInt16Le(paletteAt + c * 2);
            colours[c] = 0xFF000000u | (uint)((bgr & 31) * 255 / 31) << 16 | (uint)((bgr >> 5 & 31) * 255 / 31) << 8 | (uint)((bgr >> 10 & 31) * 255 / 31);
        }
        int w = tilesAcross * 8, h = tilesDown * 8;
        var pixels = new uint[w * h];
        for (int y = 0; y < tilesDown; y++)
            for (int x = 0; x < tilesAcross; x++)
            {
                int tile = frame * frameTiles + (y & 1) * (frameTiles / 2) + (y >> 1) * tilesAcross + x;
                int tileAt = gfx + tile * 32;
                if (tileAt + 32 > rom.Length) continue;
                for (int r = 0; r < 8; r++)
                {
                    byte p0 = rom.ReadByte(tileAt + r * 2), p1 = rom.ReadByte(tileAt + r * 2 + 1), p2 = rom.ReadByte(tileAt + 0x10 + r * 2), p3 = rom.ReadByte(tileAt + 0x11 + r * 2);
                    for (int c = 0; c < 8; c++)
                    {
                        int bit = 7 - c;
                        int colorIndex = (p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3;
                        pixels[(y * 8 + r) * w + x * 8 + c] = colours[colorIndex];
                    }
                }
            }
        return (w, h, pixels);
    }

    // a monster sprite as ARGB pixels (palette palette of the file's own palettes, colour 0 see-through). null for a
    // sprite number out of range
    public static (int Width, int Height, uint[] Pixels)? MonsterSprite(RomBuffer rom, int sprite, int palette)
    {
        if (sprite < 1 || sprite > L2MonsterSprites.Count(rom)) return null;
        try
        {
            var data = L2MonsterSprites.Read(rom, sprite);
            if (data.Length == 0 || data[0] == 0) return null;
            var (w, h, pixels) = L2MonsterSprites.Pixels(rom, sprite, data);
            if (w == 0 || h == 0) return null;
            var colours = L2MonsterSprites.Palette(data, palette).Select(L2Gfx.Argb).ToArray();
            return (w, h, pixels.Select(c => c == 0 ? 0u : colours[c]).ToArray());
        }
        catch (Exception) { return null; }
    }
}
