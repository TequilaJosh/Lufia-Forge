namespace LufiaForge.Core.Battle;

// the 24 battle backgrounds of Lufia II: Rise of the Sinistrals (USA), read from the user's own copy of that game,
// so Lufia Forge carries none of its pictures. a Lufia II fight reads its background number ($7E:11E1, 00-17) and
// looks it up in a 4-byte table at $97:FD40 ($81:B9C7): layout, tiles, colours, effect (animated sand, water, lava).
// - tiles: resource 16C + tiles, unpacked to $7E:C000 (256 tiles, 4bpp, used as tiles 100-1FF)
// - layout: resource 179 + layout, unpacked to $7E:2000 (32 x 32 words), then each word is the one before
//   minus the stored value ($81:BA53)
// - colours: palette rows 2 and 3, raw at $97:CD58 + colours x 40
// resources: 3-byte table at $A7:8000, a resource sits at file 0x138000 + its value, packed the same way as
// Lufia 1's own (LufiaCompression)
public static class Lufia2Backgrounds
{
    public const int Count = 24;
    private const int Table = 0xBFD40, Colours = 0xBCD58, Resources = 0x138000;
    private const int TilesBase = 0x16C, LayoutBase = 0x179;

    // names we gave the 24 backgrounds (Lufia II has none of its own)
    public static readonly string[] Names =
    {
        "Desert", "Dark Cave", "Stone Lake", "Ship Deck", "Grassland", "Palace Hall", "Mossy Rock", "Mountain Path",
        "Tower", "Dungeon", "Shrine", "Red Cave", "Brown Cave", "Cave Pool", "Crimson Cave", "Waterfall",
        "Moss Cave", "Vine Cave", "Lava Cave", "Ember Cave", "Leaf Cave", "Fortress", "Temple", "Shrine Hall",
    };

    // the rom without a copier header, or null when it isn't Lufia II (USA)
    public static byte[]? Rom(byte[] file)
    {
        var romBytes = file.Length % 0x8000 == 0x200 ? file[0x200..] : file;
        if (romBytes.Length < 0x280000) return null;
        string title = new string(romBytes.Skip(0x7FC0).Take(21).Select(b => (char)b).ToArray());
        return title.StartsWith("Lufia II") ? romBytes : null;
    }

    private static int ResourceOffset(byte[] romBytes, int id)
    {
        int tableAt = Resources + id * 3;
        return Resources + (romBytes[tableAt] | romBytes[tableAt + 1] << 8 | romBytes[tableAt + 2] << 16);
    }

    // background number (0-23) the way the game shows it (ARGB, 256 x 224, colour 0 black)
    public static uint[] Picture(byte[] romBytes, int number)
    {
        var rom = new RomBuffer(romBytes, "Lufia II");
        int entry = Table + number * 4;
        var layout = LufiaCompression.Decompress(rom, ResourceOffset(romBytes, LayoutBase + romBytes[entry]), out _);
        var tiles = LufiaCompression.Decompress(rom, ResourceOffset(romBytes, TilesBase + romBytes[entry + 1]), out _);
        for (int i = 2, prev = layout[0] | layout[1] << 8; i + 1 < layout.Length && i < 0x800; i += 2)
        {
            prev = (prev - (layout[i] | layout[i + 1] << 8)) & 0xFFFF;
            layout[i] = (byte)prev; layout[i + 1] = (byte)(prev >> 8);
        }
        int colours = Colours + romBytes[entry + 2] * 0x40;
        uint Colour(int row, int index)
        {
            if (index == 0 || row < 2 || row > 3) return 0xFF000000;
            int colourAt = colours + (row - 2) * 0x20 + index * 2, c = romBytes[colourAt] | romBytes[colourAt + 1] << 8;
            uint R = (uint)(c & 31), G = (uint)(c >> 5 & 31), B = (uint)(c >> 10 & 31);
            return 0xFF000000 | (R << 3 | R >> 2) << 16 | (G << 3 | G >> 2) << 8 | (B << 3 | B >> 2);
        }
        var output = new uint[256 * 224];
        for (int ty = 0; ty < 28; ty++)
            for (int tx = 0; tx < 32; tx++)
            {
                int wordAt = (ty * 32 + tx) * 2, word = layout[wordAt] | layout[wordAt + 1] << 8;
                int tile = (word & 0x3FF) - 0x100, row = word >> 10 & 7;
                bool hflip = (word & 0x4000) != 0, vflip = (word & 0x8000) != 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int sy = vflip ? 7 - y : y, bit = hflip ? x : 7 - x, tileAt = tile * 32, index = 0;
                        if (tile >= 0 && tileAt + 31 < tiles.Length)
                            index = (tiles[tileAt + sy * 2] >> bit & 1) | (tiles[tileAt + sy * 2 + 1] >> bit & 1) << 1 |
                                (tiles[tileAt + 16 + sy * 2] >> bit & 1) << 2 | (tiles[tileAt + 17 + sy * 2] >> bit & 1) << 3;
                        output[(ty * 8 + y) * 256 + tx * 8 + x] = Colour(row, index);
                    }
            }
        return output;
    }
}
