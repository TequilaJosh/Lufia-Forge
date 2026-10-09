namespace LufiaForge.Core.Battle;

/// <summary>
/// Monster battle pictures: resource 0x43 + the monster's graphic number (record byte +14), unpacked by the battle
/// code to $7E:7000 and assembled by $08:D8E1-$08:D9C4. Layout:
/// <code>
/// 0x00  palette 0 (16 BGR555 colours; colour 0 = see-through)
/// 0x20  palette 1 (the recoloured monster: record byte +15 = 1, e.g. Red Wolf next to Blue Wolf)
/// 0x40  u16 start of the tile data (0x49 + width * height)
/// 0x42  u16 tile data length (128 per block)
/// 0x44  u16 width * height + 5
/// 0x46  05
/// 0x47  blocks that have graphics
/// 0x48  size in 16x16 blocks: high nibble = width, low nibble = height
/// 0x49  one byte per block, row by row: its slot * 2, or 7F = empty (nothing stored)
/// then  128 bytes per non-empty block: four 4bpp 8x8 tiles (top-left, top-right, bottom-left, bottom-right)
/// </code>
/// Graphics 00-6B are monsters; the resources after them are something else.
/// </summary>
public static class MonsterGraphics
{
    public const int FirstResource = 0x43;
    public const int Count = 0x6C;
    /// <summary>Largest picture the game uses (the final bosses): 8 x 7 blocks = 128 x 112 pixels.</summary>
    public const int MaxWidth = 8, MaxHeight = 7, MaxBlocks = 56;

    public sealed record Picture(byte[] Data, int Width, int Height)
    {
        public int PixelWidth => Width * 16;
        public int PixelHeight => Height * 16;
    }

    public static int Resource(int graphic) => FirstResource + graphic;

    public static Picture Load(RomBuffer rom, int graphic)
    {
        var d = LufiaCompression.DecompressResource(rom, Resource(graphic), out _);
        if (d.Length < 0x4A) throw new System.IO.InvalidDataException($"Graphic {graphic:X2} is too short.");
        return new Picture(d, d[0x48] >> 4, d[0x48] & 15);
    }

    /// <summary>The 16 colours of palette 0 or 1 as BGRA (colour 0 transparent).</summary>
    public static uint[] Colours(Picture p, int palette)
    {
        var c = new uint[16];
        for (int i = 1; i < 16; i++)
        {
            int at = (palette & 1) * 32 + i * 2, v = p.Data[at] | p.Data[at + 1] << 8;
            uint r = (uint)(v & 31) << 3, g = (uint)(v >> 5 & 31) << 3, b = (uint)(v >> 10 & 31) << 3;
            c[i] = 0xFF000000u | r << 16 | g << 8 | b;
        }
        return c;
    }

    /// <summary>Colour numbers (0-15), one byte per pixel, PixelWidth x PixelHeight.</summary>
    public static byte[] Indices(Picture p)
    {
        var d = p.Data;
        int w = p.PixelWidth, start = d[0x40] | d[0x41] << 8;
        var idx = new byte[w * p.PixelHeight];
        int k = 0;
        for (int b = 0; b < p.Width * p.Height; b++)
        {
            if (d[0x49 + b] == 0x7F) continue;
            int bx = b % p.Width * 16, by = b / p.Width * 16, at = start + k++ * 128;
            for (int q = 0; q < 4; q++)
                for (int y = 0; y < 8; y++)
                {
                    int t = at + q * 32;
                    byte p0 = d[t + y * 2], p1 = d[t + y * 2 + 1], p2 = d[t + 16 + y * 2], p3 = d[t + 16 + y * 2 + 1];
                    for (int x = 0; x < 8; x++)
                    {
                        int bit = 7 - x;
                        idx[(by + q / 2 * 8 + y) * w + bx + q % 2 * 8 + x] =
                            (byte)((p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3);
                    }
                }
        }
        return idx;
    }

    public static (uint[] Px, int W, int H) Render(Picture p, int palette)
    {
        var pal = Colours(p, palette);
        return (Indices(p).Select(i => pal[i]).ToArray(), p.PixelWidth, p.PixelHeight);
    }

    /// <summary>
    /// A new graphic from colour numbers (pixelWidth x pixelHeight, multiples of 16). Blocks that are all
    /// see-through are stored as empty. The palettes come from <paramref name="palettes"/> (64 bytes), or stay as
    /// they were in <paramref name="old"/>.
    /// </summary>
    public static byte[] Build(Picture old, byte[] idx, int pixelWidth, int pixelHeight, byte[]? palettes = null)
    {
        if (pixelWidth % 16 != 0 || pixelHeight % 16 != 0)
            throw new ArgumentException("The picture's width and height must be multiples of 16 pixels.");
        int w = pixelWidth / 16, h = pixelHeight / 16;
        if (w < 1 || h < 1 || w > MaxWidth || h > MaxHeight)
            throw new ArgumentException($"The picture can be at most {MaxWidth * 16} x {MaxHeight * 16} pixels ({MaxWidth} x {MaxHeight} blocks of 16).");

        var blocks = new List<byte[]>();
        var slots = new byte[w * h];
        for (int b = 0; b < w * h; b++)
        {
            int bx = b % w * 16, by = b / w * 16;
            bool empty = true;
            for (int y = 0; y < 16 && empty; y++)
                for (int x = 0; x < 16; x++)
                    if (idx[(by + y) * pixelWidth + bx + x] != 0) { empty = false; break; }
            if (empty) { slots[b] = 0x7F; continue; }
            var data = new byte[128];
            for (int q = 0; q < 4; q++)
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int c = idx[(by + q / 2 * 8 + y) * pixelWidth + bx + q % 2 * 8 + x] & 15, bit = 7 - x, t = q * 32;
                        data[t + y * 2] |= (byte)((c & 1) << bit);
                        data[t + y * 2 + 1] |= (byte)((c >> 1 & 1) << bit);
                        data[t + 16 + y * 2] |= (byte)((c >> 2 & 1) << bit);
                        data[t + 16 + y * 2 + 1] |= (byte)((c >> 3 & 1) << bit);
                    }
            slots[b] = (byte)(blocks.Count * 2);
            blocks.Add(data);
        }
        if (blocks.Count == 0) throw new ArgumentException("The picture is empty (all see-through).");
        if (blocks.Count > MaxBlocks) throw new ArgumentException($"The picture uses {blocks.Count} blocks of 16x16; the game handles at most {MaxBlocks}.");

        int start = 0x49 + w * h;
        var o = new byte[start + blocks.Count * 128];
        Array.Copy(palettes ?? old.Data, 0, o, 0, 0x40);
        o[0x40] = (byte)start; o[0x41] = (byte)(start >> 8);
        int len = blocks.Count * 128;
        o[0x42] = (byte)len; o[0x43] = (byte)(len >> 8);
        o[0x44] = (byte)(w * h + 5); o[0x45] = 0;
        o[0x46] = 5;
        o[0x47] = (byte)blocks.Count;
        o[0x48] = (byte)(w << 4 | h);
        slots.CopyTo(o, 0x49);
        for (int i = 0; i < blocks.Count; i++) blocks[i].CopyTo(o, start + i * 128);
        return o;
    }

    /// <summary>BGR555 of a colour.</summary>
    public static ushort ToBgr555(uint argb) =>
        (ushort)((argb >> 19 & 31) | (argb >> 11 & 31) << 5 | (argb >> 3 & 31) << 10);
}
