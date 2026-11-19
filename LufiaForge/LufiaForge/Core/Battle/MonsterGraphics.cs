namespace LufiaForge.Core.Battle;

// monster battle pictures: resource 0x43 + the monster's graphic number (record byte +14), unpacked by the battle
// code to $7E:7000 and put together by $08:D8E1-$08:D9C4. layout:
//   0x00  palette 0 (16 BGR555 colours, colour 0 = see-through)
//   0x20  palette 1 (the recoloured monster: record byte +15 = 1, like Red Wolf next to Blue Wolf)
//   0x40  u16 start of the tile data (0x49 + width * height)
//   0x42  u16 tile data length (128 per block)
//   0x44  u16 width * height + 5
//   0x46  05
//   0x47  blocks that have graphics
//   0x48  size in 16x16 blocks: high nibble = width, low nibble = height
//   0x49  one byte per block, row by row: its slot * 2, or 7F = empty (nothing stored)
//   then  128 bytes per non-empty block: four 4bpp 8x8 tiles (top-left, top-right, bottom-left, bottom-right)
// graphics 00-6B are monsters, the resources after them are something else
public static class MonsterGraphics
{
    public const int FirstResource = 0x43;
    public const int OriginalCount = 0x6C;
    // biggest picture the game uses (the final bosses): 8 x 7 blocks = 128 x 112 pixels
    public const int MaxWidth = 8, MaxHeight = 7, MaxBlocks = 56;

    public sealed record Picture(byte[] Data, int Width, int Height)
    {
        public int PixelWidth => Width * 16;
        public int PixelHeight => Height * 16;
    }

    public static int Resource(RomBuffer rom, int graphic) => MonsterTable.PictureResource(rom, graphic);
    public static int Count(RomBuffer rom) => MonsterTable.PictureCount(rom);

    public static Picture Load(RomBuffer rom, int graphic)
    {
        var data = LufiaCompression.DecompressResource(rom, Resource(rom, graphic), out _);
        if (data.Length < 0x4A) throw new System.IO.InvalidDataException($"Graphic {graphic:X2} is too short.");
        return new Picture(data, data[0x48] >> 4, data[0x48] & 15);
    }

    // the 16 colours of palette 0 or 1 as BGRA (colour 0 transparent)
    public static uint[] Colours(Picture picture, int palette)
    {
        var colours = new uint[16];
        for (int i = 1; i < 16; i++)
        {
            int colourAt = (palette & 1) * 32 + i * 2, bgr = picture.Data[colourAt] | picture.Data[colourAt + 1] << 8;
            uint r = (uint)(bgr & 31) << 3, g = (uint)(bgr >> 5 & 31) << 3, b = (uint)(bgr >> 10 & 31) << 3;
            colours[i] = 0xFF000000u | r << 16 | g << 8 | b;
        }
        return colours;
    }

    // colour numbers (0-15), one byte per pixel, PixelWidth x PixelHeight
    public static byte[] Indices(Picture picture)
    {
        var data = picture.Data;
        int width = picture.PixelWidth, start = data[0x40] | data[0x41] << 8;
        var indices = new byte[width * picture.PixelHeight];
        int storedBlock = 0;
        for (int block = 0; block < picture.Width * picture.Height; block++)
        {
            if (data[0x49 + block] == 0x7F) continue;
            int bx = block % picture.Width * 16, by = block / picture.Width * 16, blockAt = start + storedBlock++ * 128;
            for (int quarter = 0; quarter < 4; quarter++)
                for (int y = 0; y < 8; y++)
                {
                    int tileAt = blockAt + quarter * 32;
                    byte p0 = data[tileAt + y * 2], p1 = data[tileAt + y * 2 + 1], p2 = data[tileAt + 16 + y * 2], p3 = data[tileAt + 16 + y * 2 + 1];
                    for (int x = 0; x < 8; x++)
                    {
                        int bit = 7 - x;
                        indices[(by + quarter / 2 * 8 + y) * width + bx + quarter % 2 * 8 + x] =
                            (byte)((p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3);
                    }
                }
        }
        return indices;
    }

    public static (uint[] Px, int W, int H) Render(Picture picture, int palette)
    {
        var colours = Colours(picture, palette);
        return (Indices(picture).Select(i => colours[i]).ToArray(), picture.PixelWidth, picture.PixelHeight);
    }

    // a new graphic from colour numbers (pixelWidth x pixelHeight, multiples of 16). blocks that are all
    // see-through get stored as empty. the palettes come from palettes (64 bytes), or stay as
    // they were in old
    public static byte[] Build(Picture old, byte[] indices, int pixelWidth, int pixelHeight, byte[]? palettes = null)
    {
        if (pixelWidth % 16 != 0 || pixelHeight % 16 != 0)
            throw new ArgumentException("The picture's width and height must be multiples of 16 pixels.");
        int width = pixelWidth / 16, height = pixelHeight / 16;
        if (width < 1 || height < 1 || width > MaxWidth || height > MaxHeight)
            throw new ArgumentException($"The picture can be at most {MaxWidth * 16} x {MaxHeight * 16} pixels ({MaxWidth} x {MaxHeight} blocks of 16).");

        var blocks = new List<byte[]>();
        var slots = new byte[width * height];
        for (int block = 0; block < width * height; block++)
        {
            int bx = block % width * 16, by = block / width * 16;
            bool empty = true;
            for (int y = 0; y < 16 && empty; y++)
                for (int x = 0; x < 16; x++)
                    if (indices[(by + y) * pixelWidth + bx + x] != 0) { empty = false; break; }
            if (empty) { slots[block] = 0x7F; continue; }
            var data = new byte[128];
            for (int quarter = 0; quarter < 4; quarter++)
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int colour = indices[(by + quarter / 2 * 8 + y) * pixelWidth + bx + quarter % 2 * 8 + x] & 15, bit = 7 - x, tileAt = quarter * 32;
                        data[tileAt + y * 2] |= (byte)((colour & 1) << bit);
                        data[tileAt + y * 2 + 1] |= (byte)((colour >> 1 & 1) << bit);
                        data[tileAt + 16 + y * 2] |= (byte)((colour >> 2 & 1) << bit);
                        data[tileAt + 16 + y * 2 + 1] |= (byte)((colour >> 3 & 1) << bit);
                    }
            slots[block] = (byte)(blocks.Count * 2);
            blocks.Add(data);
        }
        if (blocks.Count == 0) throw new ArgumentException("The picture is empty (all see-through).");
        if (blocks.Count > MaxBlocks) throw new ArgumentException($"The picture uses {blocks.Count} blocks of 16x16; the game handles at most {MaxBlocks}.");

        int start = 0x49 + width * height;
        var output = new byte[start + blocks.Count * 128];
        Array.Copy(palettes ?? old.Data, 0, output, 0, 0x40);
        output[0x40] = (byte)start; output[0x41] = (byte)(start >> 8);
        int length = blocks.Count * 128;
        output[0x42] = (byte)length; output[0x43] = (byte)(length >> 8);
        output[0x44] = (byte)(width * height + 5); output[0x45] = 0;
        output[0x46] = 5;
        output[0x47] = (byte)blocks.Count;
        output[0x48] = (byte)(width << 4 | height);
        slots.CopyTo(output, 0x49);
        for (int i = 0; i < blocks.Count; i++) blocks[i].CopyTo(output, start + i * 128);
        return output;
    }

    // BGR555 of a colour
    public static ushort ToBgr555(uint argb) =>
        (ushort)((argb >> 19 & 31) | (argb >> 11 & 31) << 5 | (argb >> 3 & 31) << 10);
}
