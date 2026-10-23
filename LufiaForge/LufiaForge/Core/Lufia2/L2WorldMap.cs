namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II's world map (LEdit's L2WorldMap, US file numbers): packed file 0x28C is a 0x40-byte head and 128 x 128
/// u16 big-block numbers (row by row); a big block (32x32 pixels) is 4 small-block numbers at $9A:8000 (file 0xD0000,
/// top left, top right, bottom left, bottom right); a small block (16x16) is 4 tile bytes at file 0xF3000 (top left,
/// bottom left, top right, bottom right) and a terrain byte; tiles are 8x8 with a byte per pixel in packed file
/// 0x28B (low nibble the colour, bits 4-6 the palette), palettes at file 0x133400.
/// </summary>
public static class L2WorldMap
{
    public const int MapFile = 0x28C, TileFile = 0x28B, BigBlocks = 0xD0000, SmallBlocks = 0xF3000, Palettes = 0x133400, Size = 128;

    /// <summary>The terrain of a small block: 00 plain, 10 desert, 20 forest, 30 water, 40 mountain.</summary>
    public static int Terrain(RomBuffer rom, int small) => rom.ReadByte(SmallBlocks + small * 5 + 4);

    /// <summary>Draws the world map at 1/<paramref name="scale"/> size (1 = 4096 x 4096 pixels).</summary>
    public static (int Width, int Height, uint[] Pixels) Render(RomBuffer rom, int scale = 4)
    {
        byte[] map = L2Rom.ReadFile(rom, MapFile), tiles = L2Rom.ReadFile(rom, TileFile);
        var pal = new uint[8 * 16];
        for (int i = 0; i < pal.Length; i++)
        {
            int v = rom.ReadUInt16Le(Palettes + i * 2);
            pal[i] = 0xFF000000u | (uint)((v & 31) * 255 / 31) << 16 | (uint)((v >> 5 & 31) * 255 / 31) << 8 | (uint)((v >> 10 & 31) * 255 / 31);
        }
        int full = Size * 32, w = full / scale;
        var px = new uint[w * w];
        for (int by = 0; by < Size; by++)
            for (int bx = 0; bx < Size; bx++)
            {
                int big = map[0x40 + (by * Size + bx) * 2] | map[0x41 + (by * Size + bx) * 2] << 8;
                for (int q = 0; q < 4; q++)
                {
                    int small = rom.ReadUInt16Le(BigBlocks + big * 8 + q * 2);
                    int sx = bx * 32 + (q & 1) * 16, sy = by * 32 + (q >> 1) * 16;
                    for (int t = 0; t < 4; t++)
                    {
                        int tile = rom.ReadByte(SmallBlocks + small * 5 + t);
                        int tx = sx + (t >= 2 ? 8 : 0), ty = sy + (t & 1) * 8;
                        for (int y = 0; y < 8; y += scale > 8 ? 8 : 1)
                            for (int x = 0; x < 8; x++)
                            {
                                int X = tx + x, Y = ty + y;
                                if (X % scale != 0 || Y % scale != 0) continue;
                                int at = tile * 64 + y * 8 + x;
                                if (at >= tiles.Length) continue;
                                byte b = tiles[at];
                                px[(Y / scale) * w + X / scale] = pal[((b >> 4) & 7) * 16 + (b & 15)];
                            }
                    }
                }
            }
        return (w, w, px);
    }
}
