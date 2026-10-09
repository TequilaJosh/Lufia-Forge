namespace LufiaForge.Core.Maps;

/// <summary>
/// Pictures of map characters for lists (first frame, facing down), read from the game's sprite table.
///
/// Sprite number n → record pointer at $01:E25F + n*2 (bank 01; used by the spawn code at $01:AC50).
/// Record (17 bytes): [0] width and [1] height in 8x8 tiles, [4] OAM attribute (low 3 bits = palette row),
/// [6..8] LoROM address of the graphics (4bpp, uncompressed), [9..16] first tile of each of 8 frames.
/// A frame is width*height tiles in row order. Colours: sprite palette resource 0xAF (8 rows of 16).
/// </summary>
public static class NpcSprites
{
    public const int RecordTable = 0xE25F;   // file offset of the $01:E25F pointer table
    public const int SpriteCount = 0x4A;     // entries after 49 are other data
    private const int PaletteResource = 0xAF;

    public sealed record SpriteInfo(int Width, int Height, int Palette, int GraphicsOffset, int FirstTile);

    /// <summary>The record for a sprite number, or null if it isn't a character sprite.</summary>
    public static SpriteInfo? Info(RomBuffer rom, int sprite)
    {
        if (sprite < 0 || sprite >= SpriteCount) return null;
        int ptr = rom.ReadUInt16Le(RecordTable + sprite * 2);
        if (ptr < 0x8000) return null;
        int rec = 0x8000 + (ptr - 0x8000);                 // bank 01: file = 0x8000 + (addr - 0x8000)
        int w = rom.ReadByte(rec), h = rom.ReadByte(rec + 1);
        if (w is < 1 or > 4 || h is < 1 or > 4) return null;
        int attr = rom.ReadByte(rec + 4);
        int addr = rom.ReadByte(rec + 6) | (rom.ReadByte(rec + 7) << 8) | (rom.ReadByte(rec + 8) << 16);
        int file = ((addr >> 16) & 0x7F) * 0x8000 + (addr & 0x7FFF);
        if ((addr & 0x8000) == 0 || file >= rom.Length) return null;
        return new SpriteInfo(w, h, attr & 7, file, rom.ReadByte(rec + 9));
    }

    private static readonly Dictionary<int, (uint[] Px, int W, int H)?> Cache = new();
    private static uint[]? _palette;
    private static RomBuffer? _rom;

    /// <summary>BGRA pixels of the sprite's first frame (0 = transparent) and its size, or null.</summary>
    public static (uint[] Px, int W, int H)? Render(RomBuffer rom, int sprite)
    {
        if (!ReferenceEquals(rom, _rom)) { Cache.Clear(); _palette = null; _rom = rom; }
        if (Cache.TryGetValue(sprite, out var hit)) return hit;
        (uint[], int, int)? result = null;
        if (Info(rom, sprite) is { } s)
        {
            _palette ??= LoadPalette(rom);
            int w = s.Width * 8, h = s.Height * 8;
            var px = new uint[w * h];
            int tiles = s.Width * s.Height;
            for (int t = 0; t < tiles; t++)
            {
                int off = s.GraphicsOffset + (s.FirstTile + t) * 32;
                if (off + 32 > rom.Length) break;
                var tile = Modules.TileViewer.SnesTileDecoder.DecodeTile(rom.ReadBytes(off, 32), 0, Modules.TileViewer.BitDepth.Bpp4);
                int tx = (t % s.Width) * 8, ty = (t / s.Width) * 8;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int c = tile[y * 8 + x];
                        if (c != 0) px[(ty + y) * w + tx + x] = _palette[s.Palette * 16 + c];
                    }
            }
            result = (px, w, h);
        }
        Cache[sprite] = result;
        return result;
    }

    /// <summary>First tile of each of the 8 frames (record bytes 9-16).</summary>
    public static int[] FrameTiles(RomBuffer rom, int sprite)
    {
        int rec = 0x8000 + (rom.ReadUInt16Le(RecordTable + sprite * 2) - 0x8000);
        return Enumerable.Range(0, 8).Select(f => (int)rom.ReadByte(rec + 9 + f)).ToArray();
    }

    /// <summary>All 8 frames side by side (BGRA, alpha 0 = transparent).</summary>
    public static (uint[] Px, int W, int H)? RenderSheet(RomBuffer rom, int sprite)
    {
        if (Info(rom, sprite) is not { } s) return null;
        _palette ??= LoadPalette(rom);
        int fw = s.Width * 8, h = s.Height * 8, w = fw * 8;
        var px = new uint[w * h];
        var frames = FrameTiles(rom, sprite);
        for (int f = 0; f < 8; f++)
            for (int t = 0; t < s.Width * s.Height; t++)
            {
                int off = s.GraphicsOffset + (frames[f] + t) * 32;
                if (off + 32 > rom.Length) break;
                var tile = Modules.TileViewer.SnesTileDecoder.DecodeTile(rom.ReadBytes(off, 32), 0, Modules.TileViewer.BitDepth.Bpp4);
                int tx = f * fw + t % s.Width * 8, ty = t / s.Width * 8;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        if (tile[y * 8 + x] is var c and not 0) px[(ty + y) * w + tx + x] = _palette[s.Palette * 16 + c];
            }
        return (px, w, h);
    }

    /// <summary>
    /// Draws a sheet (same layout as <see cref="RenderSheet"/>: 8 frames side by side) into the sprite's graphics,
    /// in its palette row: transparent pixels become colour 0, others the nearest of colours 1-15. The size can't
    /// change (the graphics are stored uncompressed, packed with other sprites). Frames that share tiles take the
    /// last frame drawn there.
    /// </summary>
    public static int ImportSheet(RomBuffer rom, int sprite, uint[] px, int w, int h)
    {
        if (Info(rom, sprite) is not { } s) throw new InvalidOperationException("Not a character sprite.");
        int fw = s.Width * 8;
        if (w != fw * 8 || h != s.Height * 8)
            throw new InvalidOperationException($"The sheet must be {fw * 8} x {s.Height * 8} pixels: 8 frames of {fw} x {s.Height * 8}.");
        var raw = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
        var pal = Enumerable.Range(0, 16).Select(c => raw[(s.Palette * 16 + c) * 2] | raw[(s.Palette * 16 + c) * 2 + 1] << 8).ToArray();
        var frames = FrameTiles(rom, sprite);
        int written = 0;
        for (int f = 0; f < 8; f++)
            for (int t = 0; t < s.Width * s.Height; t++)
            {
                var tile = new byte[32];
                int tx = f * fw + t % s.Width * 8, ty = t / s.Width * 8;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        uint p = px[(ty + y) * w + tx + x];
                        int c = 0;
                        if (p >> 24 >= 128)
                        {
                            int r = (int)(p >> 16 & 255) >> 3, g = (int)(p >> 8 & 255) >> 3, b = (int)(p & 255) >> 3;
                            long best = long.MaxValue;
                            for (int k = 1; k < 16; k++)
                            {
                                int dr = r - (pal[k] & 31), dg = g - (pal[k] >> 5 & 31), db = b - (pal[k] >> 10 & 31);
                                long e = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                                if (e < best) { best = e; c = k; }
                            }
                        }
                        int bit = 7 - x;
                        tile[y * 2] |= (byte)((c & 1) << bit); tile[y * 2 + 1] |= (byte)((c >> 1 & 1) << bit);
                        tile[16 + y * 2] |= (byte)((c >> 2 & 1) << bit); tile[16 + y * 2 + 1] |= (byte)((c >> 3 & 1) << bit);
                    }
                int off = s.GraphicsOffset + (frames[f] + t) * 32;
                if (off + 32 > rom.Length) continue;
                rom.WriteBytes(off, tile); written++;
            }
        Cache.Clear();
        return written;
    }

    private static uint[] LoadPalette(RomBuffer rom)
    {
        var raw = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
        var pal = new uint[128];
        for (int i = 0; i < 128 && i * 2 + 1 < raw.Length; i++)
            pal[i] = Modules.TileViewer.SnesPalette.Bgr555ToArgb32((ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8)));
        return pal;
    }
}
