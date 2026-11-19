namespace LufiaForge.Core.Lufia2;

// lufia 2's world map (LEdit's L2WorldMap, US file numbers): packed file 0x28C is a 0x40-byte head and 128 x 128 u16
// big block numbers (row by row). a big block (32x32 pixels) is 4 small block numbers at $9A:8000 (file 0xD0000: top
// left, top right, bottom left, bottom right). a small block (16x16) is 4 tile bytes at file 0xF3000 (top left, bottom
// left, top right, bottom right) and a terrain byte. tiles are 8x8 with a byte per pixel in packed file 0x28B (low
// nibble the colour, bits 4-6 the palette), palettes at file 0x133400
public static class L2WorldMap
{
    public const int MapFile = 0x28C, TileFile = 0x28B, BigBlocks = 0xD0000, SmallBlocks = 0xF3000, Palettes = 0x133400, Size = 128;

    // the terrain of a small block: 00 plain, 10 desert, 20 forest, 30 water, 40 mountain
    public static int Terrain(RomBuffer rom, int small) => rom.ReadByte(SmallBlocks + small * 5 + 4);

    // where the world map (0) and the sea floor (1) keep their parts: the map file, the tile file, the big and small
    // block tables and the palettes (the sea floor: $86:CE32 entry 1, big blocks $9B:8000, small $A1:A8F2, palettes $A6:BA00)
    public sealed record Parts(int MapFile, int TileFile, int BigBlocks, int SmallBlocks, int Palettes);
    public static readonly Parts World = new(0x28C, 0x28B, 0xD0000, 0xF3000, 0x133400);
    public static readonly Parts SeaFloor = new(0x28F, 0x28E, 0xD8000, 0x10A8F2, 0x133A00);
    public static Parts Of(int map) => map == 1 ? SeaFloor : World;

    // the 128 x 128 big block numbers of a map file
    public static int[] Cells(byte[] file) => Enumerable.Range(0, Size * Size).Select(i => file[0x40 + i * 2] | file[0x41 + i * 2] << 8).ToArray();

    // the map file with these big block numbers (its head and anything after the cells kept)
    public static byte[] WithCells(byte[] file, int[] cells)
    {
        var newFile = (byte[])file.Clone();
        for (int i = 0; i < Size * Size; i++) { newFile[0x40 + i * 2] = (byte)cells[i]; newFile[0x41 + i * 2] = (byte)(cells[i] >> 8); }
        return newFile;
    }

    // the colours (8 rows of 16)
    public static uint[] Colours(RomBuffer rom, Parts parts)
    {
        var palette = new uint[8 * 16];
        for (int i = 0; i < palette.Length; i++)
        {
            int colour = rom.ReadUInt16Le(parts.Palettes + i * 2);
            palette[i] = 0xFF000000u | (uint)((colour & 31) * 255 / 31) << 16 | (uint)((colour >> 5 & 31) * 255 / 31) << 8 | (uint)((colour >> 10 & 31) * 255 / 31);
        }
        return palette;
    }

    // a big block at full size (32 x 32 pixels, the way the game shows it)
    public static uint[] BigBlock(RomBuffer rom, Parts parts, byte[] tiles, uint[] pal, int big)
    {
        var pixels = new uint[32 * 32];
        for (int quarter = 0; quarter < 4; quarter++)
        {
            int small = rom.ReadUInt16Le(parts.BigBlocks + big * 8 + quarter * 2);
            for (int t = 0; t < 4; t++)
            {
                int tile = rom.ReadByte(parts.SmallBlocks + small * 5 + t);
                int tx = (quarter & 1) * 16 + (t >= 2 ? 8 : 0), ty = (quarter >> 1) * 16 + (t & 1) * 8;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int at = tile * 64 + y * 8 + x;
                        if (at >= tiles.Length) continue;
                        byte pixel = tiles[at];
                        pixels[(ty + y) * 32 + tx + x] = pal[((pixel >> 4) & 7) * 16 + (pixel & 15)];
                    }
            }
        }
        return pixels;
    }

    // a big block at half size (16 x 16 pixels, each the average of 2 x 2 of the full picture), for lists
    public static uint[] BigBlockHalf(RomBuffer rom, Parts parts, byte[] tiles, uint[] pal, int big) => Half(BigBlock(rom, parts, tiles, pal, big));

    // a 32 x 32 picture at 16 x 16, averaging each 2 x 2
    public static uint[] Half(uint[] full)
    {
        var pixels = new uint[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                uint r = 0, g = 0, b = 0;
                foreach (var c in new[] { full[y * 64 + x * 2], full[y * 64 + x * 2 + 1], full[y * 64 + 32 + x * 2], full[y * 64 + 33 + x * 2] })
                { r += c >> 16 & 0xFF; g += c >> 8 & 0xFF; b += c & 0xFF; }
                pixels[y * 16 + x] = 0xFF000000u | r / 4 << 16 | g / 4 << 8 | b / 4;
            }
        return pixels;
    }

    // how many big blocks the table can hold (up to the small block table, or the highest one the map uses)
    public static int BigBlockCount(Parts parts, int[] cells) => Math.Max(cells.Max() + 1, Math.Min(0x1000, parts == World ? (parts.SmallBlocks - parts.BigBlocks) / 8 : cells.Max() + 1));

    // draws the world map at 1/scale size (1 = 4096 x 4096 pixels)
    public static (int Width, int Height, uint[] Pixels) Render(RomBuffer rom, int scale = 4)
    {
        byte[] map = L2Rom.ReadFile(rom, MapFile), tiles = L2Rom.ReadFile(rom, TileFile);
        var palette = new uint[8 * 16];
        for (int i = 0; i < palette.Length; i++)
        {
            int colour = rom.ReadUInt16Le(Palettes + i * 2);
            palette[i] = 0xFF000000u | (uint)((colour & 31) * 255 / 31) << 16 | (uint)((colour >> 5 & 31) * 255 / 31) << 8 | (uint)((colour >> 10 & 31) * 255 / 31);
        }
        int full = Size * 32, w = full / scale;
        var pixels = new uint[w * w];
        for (int by = 0; by < Size; by++)
            for (int bx = 0; bx < Size; bx++)
            {
                int big = map[0x40 + (by * Size + bx) * 2] | map[0x41 + (by * Size + bx) * 2] << 8;
                for (int quarter = 0; quarter < 4; quarter++)
                {
                    int small = rom.ReadUInt16Le(BigBlocks + big * 8 + quarter * 2);
                    int sx = bx * 32 + (quarter & 1) * 16, sy = by * 32 + (quarter >> 1) * 16;
                    for (int t = 0; t < 4; t++)
                    {
                        int tile = rom.ReadByte(SmallBlocks + small * 5 + t);
                        int tx = sx + (t >= 2 ? 8 : 0), ty = sy + (t & 1) * 8;
                        for (int y = 0; y < 8; y += scale > 8 ? 8 : 1)
                            for (int x = 0; x < 8; x++)
                            {
                                int fullX = tx + x, fullY = ty + y;
                                if (fullX % scale != 0 || fullY % scale != 0) continue;
                                int at = tile * 64 + y * 8 + x;
                                if (at >= tiles.Length) continue;
                                byte pixel = tiles[at];
                                pixels[(fullY / scale) * w + fullX / scale] = palette[((pixel >> 4) & 7) * 16 + (pixel & 15)];
                            }
                    }
                }
            }
        return (w, w, pixels);
    }
}
