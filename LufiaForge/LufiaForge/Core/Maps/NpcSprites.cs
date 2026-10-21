namespace LufiaForge.Core.Maps;

/// <summary>
/// Map character sprites (the party and every character on the maps).
///
/// Sprite number n → record pointer: u16 at $01:E25F + n*2, read only by the lookup at $01:AC50 (LDA $01E25F,X;
/// TAY), whose callers then read the record with the data bank set to 01 - so records must be in bank 01.
/// Record (17 bytes): [0] width and [1] height in 8x8 tiles, [2] frames per pair (2), [3] which pairs of frames
/// the map loader copies to VRAM ($01:AE7F: bit 1 = frames 2-3, 2 = 4-5, 3 = 6-7), [4] OAM attribute (low 3 bits = palette row),
/// [6..8] LoROM address of the graphics (4bpp, uncompressed), [9..16] first tile of each of 8 frames.
/// A frame is width*height tiles in row order. Colours: sprite palette resource 0xAF (8 rows of 16).
/// The game's sprites are 16x16, 16x24 or 32x32.
///
/// New sprites: the pointer table moves to the added space (block "LFSP": count, then room for
/// <see cref="Capacity"/> pointers; only the lookup's long operand changes) and the new records go into the unused
/// tail of bank 01 (<see cref="RecordArea"/>; the game's code ends at $01:F849, after it is filler), with their
/// graphics in the added space.
/// </summary>
public static class NpcSprites
{
    public const int RecordTable = 0xE25F;   // file offset of the game's $01:E25F pointer table
    public const int SpriteCount = 0x4A;     // the game's sprites (entries after 49 are other data)
    public const int RecordSize = 17, Capacity = 0xC0;
    /// <summary>Bank 01 space for new records (file offsets; bank 01 file offset = its address).</summary>
    public const int RecordArea = 0xF880, RecordAreaEnd = 0x10000;
    private const int LookupOperand = 0xAC5D;   // long operand of LDA $01E25F,X in the lookup
    private const int PaletteResource = 0xAF;
    private const int Header = 8;
    private static readonly byte[] Magic = "LFSP"u8.ToArray();

    public sealed record SpriteInfo(int Width, int Height, int Palette, int GraphicsOffset, int FirstTile);

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int Long(RomBuffer rom, int at) => rom.ReadByte(at) | rom.ReadByte(at + 1) << 8 | rom.ReadByte(at + 2) << 16;

    /// <summary>File offset of the pointer table the game reads.</summary>
    public static int TableOffset(RomBuffer rom) => FileOffset(Long(rom, LookupOperand));

    private static int Block(RomBuffer rom)
    {
        int b = TableOffset(rom) - Header;
        return b != RecordTable - Header && b > 0 && rom.ReadBytes(b, 4).SequenceEqual(Magic) ? b : 0;
    }
    public static bool IsMoved(RomBuffer rom) => Block(rom) > 0;

    /// <summary>How many sprites the ROM has (0x4A in the game).</summary>
    public static int Count(RomBuffer rom) { int b = Block(rom); return b > 0 ? rom.ReadByte(b + 4) : SpriteCount; }

    /// <summary>File offset of a sprite's record (bank 01).</summary>
    public static int RecordOffset(RomBuffer rom, int sprite) => rom.ReadUInt16Le(TableOffset(rom) + sprite * 2);

    /// <summary>The record for a sprite number, or null if it isn't a character sprite.</summary>
    public static SpriteInfo? Info(RomBuffer rom, int sprite)
    {
        if (sprite < 0 || sprite >= Count(rom)) return null;
        int rec = RecordOffset(rom, sprite);
        if (rec < 0x8000) return null;
        int w = rom.ReadByte(rec), h = rom.ReadByte(rec + 1);
        if (w is < 1 or > 4 || h is < 1 or > 4) return null;
        int attr = rom.ReadByte(rec + 4);
        int addr = Long(rom, rec + 6);
        int file = FileOffset(addr);
        if ((addr & 0x8000) == 0 || file >= rom.Length) return null;
        return new SpriteInfo(w, h, attr & 7, file, rom.ReadByte(rec + 9));
    }

    /// <summary>Whether the sprite was added by Lufia Forge (its record is in the new record area).</summary>
    public static bool IsNew(RomBuffer rom, int sprite) => sprite >= SpriteCount && sprite < Count(rom) && RecordOffset(rom, sprite) >= RecordArea;

    private static readonly Dictionary<int, (uint[] Px, int W, int H)?> Cache = new();
    private static uint[]? _palette;
    private static RomBuffer? _rom;

    private static void Forget() { Cache.Clear(); _palette = null; }

    /// <summary>BGRA pixels of the sprite's first frame (0 = transparent) and its size, or null.</summary>
    public static (uint[] Px, int W, int H)? Render(RomBuffer rom, int sprite)
    {
        if (!ReferenceEquals(rom, _rom)) { Forget(); _rom = rom; }
        if (Cache.TryGetValue(sprite, out var hit)) return hit;
        (uint[], int, int)? result = null;
        if (Info(rom, sprite) is { } s)
        {
            var colours = Colours(rom, s.Palette);
            var idx = SheetIndices(rom, sprite, out int sw, out int sh);
            int w = s.Width * 8, h = s.Height * 8;
            var px = new uint[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = colours[idx[y * sw + x]];
            result = (px, w, h);
        }
        Cache[sprite] = result;
        return result;
    }

    /// <summary>First tile of each of the 8 frames (record bytes 9-16).</summary>
    public static int[] FrameTiles(RomBuffer rom, int sprite)
    {
        int rec = RecordOffset(rom, sprite);
        return Enumerable.Range(0, 8).Select(f => (int)rom.ReadByte(rec + 9 + f)).ToArray();
    }

    /// <summary>The 16 colours of a sprite palette row (BGRA; colour 0 transparent).</summary>
    public static uint[] Colours(RomBuffer rom, int row)
    {
        if (!ReferenceEquals(rom, _rom)) { Forget(); _rom = rom; }
        _palette ??= LoadPalette(rom);
        return Enumerable.Range(0, 16).Select(c => c == 0 ? 0u : _palette[row * 16 + c]).ToArray();
    }

    /// <summary>Colour numbers (0-15) of all 8 frames side by side.</summary>
    public static byte[] SheetIndices(RomBuffer rom, int sprite, out int w, out int h)
    {
        var s = Info(rom, sprite) ?? throw new InvalidOperationException("Not a character sprite.");
        int fw = s.Width * 8; h = s.Height * 8; w = fw * 8;
        var idx = new byte[w * h];
        var frames = FrameTiles(rom, sprite);
        for (int f = 0; f < 8; f++)
            for (int t = 0; t < s.Width * s.Height; t++)
            {
                int off = s.GraphicsOffset + (frames[f] + t) * 32;
                if (off + 32 > rom.Length) break;
                var tile = Modules.TileViewer.SnesTileDecoder.DecodeTile(rom.ReadBytes(off, 32), 0, Modules.TileViewer.BitDepth.Bpp4);
                int tx = f * fw + t % s.Width * 8, ty = t / s.Width * 8;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) idx[(ty + y) * w + tx + x] = tile[y * 8 + x];
            }
        return idx;
    }

    /// <summary>All 8 frames side by side (BGRA, alpha 0 = transparent).</summary>
    public static (uint[] Px, int W, int H)? RenderSheet(RomBuffer rom, int sprite)
    {
        if (Info(rom, sprite) is not { } s) return null;
        var colours = Colours(rom, s.Palette);
        var idx = SheetIndices(rom, sprite, out int w, out int h);
        return (idx.Select(i => colours[i]).ToArray(), w, h);
    }

    /// <summary>
    /// Writes colour numbers (0-15, same layout as <see cref="SheetIndices"/>) into the sprite's graphics. Frames
    /// that share tiles take the last frame drawn there. Returns the tiles written.
    /// </summary>
    public static int ImportIndices(RomBuffer rom, int sprite, byte[] idx, int w, int h)
    {
        if (Info(rom, sprite) is not { } s) throw new InvalidOperationException("Not a character sprite.");
        int fw = s.Width * 8;
        if (w != fw * 8 || h != s.Height * 8)
            throw new InvalidOperationException($"The sheet must be {fw * 8} x {s.Height * 8} pixels: 8 frames of {fw} x {s.Height * 8}.");
        var frames = FrameTiles(rom, sprite);
        int written = 0;
        for (int f = 0; f < 8; f++)
            for (int t = 0; t < s.Width * s.Height; t++)
            {
                var px = new byte[64];
                int tx = f * fw + t % s.Width * 8, ty = t / s.Width * 8;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[y * 8 + x] = (byte)(idx[(ty + y) * w + tx + x] & 15);
                int off = s.GraphicsOffset + (frames[f] + t) * 32;
                if (off + 32 > rom.Length) continue;
                rom.WriteBytes(off, Modules.TileViewer.SnesTileDecoder.EncodeTile(px, Modules.TileViewer.BitDepth.Bpp4)); written++;
            }
        Forget();
        return written;
    }

    /// <summary>
    /// Draws a full-colour sheet (same layout as <see cref="RenderSheet"/>) into the sprite's graphics, in its
    /// palette row: transparent pixels become colour 0, others the nearest of colours 1-15.
    /// </summary>
    public static int ImportSheet(RomBuffer rom, int sprite, uint[] px, int w, int h)
    {
        if (Info(rom, sprite) is not { } s) throw new InvalidOperationException("Not a character sprite.");
        var raw = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
        var pal = Enumerable.Range(0, 16).Select(c => raw[(s.Palette * 16 + c) * 2] | raw[(s.Palette * 16 + c) * 2 + 1] << 8).ToArray();
        var idx = new byte[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            uint p = px[i];
            if (p >> 24 < 128) continue;
            int r = (int)(p >> 16 & 255) >> 3, g = (int)(p >> 8 & 255) >> 3, b = (int)(p & 255) >> 3;
            long best = long.MaxValue;
            for (int k = 1; k < 16; k++)
            {
                int dr = r - (pal[k] & 31), dg = g - (pal[k] >> 5 & 31), db = b - (pal[k] >> 10 & 31);
                long e = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (e < best) { best = e; idx[i] = (byte)k; }
            }
        }
        return ImportIndices(rom, sprite, idx, w, h);
    }

    /// <summary>Gives the sprite another palette row (0-7) of the sprite palette.</summary>
    public static void SetPaletteRow(RomBuffer rom, int sprite, int row)
    {
        if (Info(rom, sprite) == null) throw new InvalidOperationException("Not a character sprite.");
        int rec = RecordOffset(rom, sprite);
        rom.WriteByte(rec + 4, (byte)(rom.ReadByte(rec + 4) & ~7 | row & 7));
        rom.FixChecksum();
        Forget();
    }

    // ── new sprites ───────────────────────────────────────────────────────

    /// <summary>Record slots in the bank 01 area that no sprite uses.</summary>
    private static List<int> FreeRecordSlots(RomBuffer rom)
    {
        var used = Enumerable.Range(0, Count(rom)).Select(s => RecordOffset(rom, s)).ToHashSet();
        var free = new List<int>();
        for (int p = RecordArea; p + RecordSize <= RecordAreaEnd; p += RecordSize) if (!used.Contains(p)) free.Add(p);
        return free;
    }

    /// <summary>How many more sprites can be added.</summary>
    public static int Room(RomBuffer rom) => Math.Min(Capacity - Count(rom), FreeRecordSlots(rom).Count);

    /// <summary>Moves the pointer table into the added space (room for <see cref="Capacity"/> sprites).</summary>
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (TableOffset(rom) != RecordTable || rom.ReadByte(LookupOperand - 1) != 0xBF)
            throw new InvalidOperationException("The game's sprite lookup isn't the original, so its table can't be moved safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(Header + Capacity * 2, ExpansionSpace.Kind.Patch, 13);
        space.Save();
        var block = new byte[Header + Capacity * 2];
        Magic.CopyTo(block, 0);
        block[4] = SpriteCount;
        // (the entries past the last sprite are whatever data follows the table; they are copied too, so a number
        // the game might use there still reads the same "record")
        rom.ReadBytes(RecordTable, Capacity * 2).CopyTo(block, Header);
        rom.WriteBytes(at, block);
        int a = SnesAddress(at + Header);
        rom.WriteBytes(LookupOperand, new[] { (byte)a, (byte)(a >> 8), (byte)(a >> 16) });
        rom.FixChecksum();
    }

    /// <summary>
    /// A new sprite the size of <paramref name="template"/>, with the template's 8 frames copied into graphics of
    /// its own (every frame gets its own tiles, so all 8 can be drawn). Returns its number.
    /// </summary>
    public static int Add(RomBuffer rom, int template)
    {
        var s = Info(rom, template) ?? throw new InvalidOperationException("Not a character sprite.");
        Move(rom);
        int n = Count(rom);
        if (n >= Capacity) throw new InvalidOperationException($"The ROM already has {Capacity} sprites.");
        var slots = FreeRecordSlots(rom);
        if (slots.Count == 0) throw new InvalidOperationException("There is no room left in bank 01 for another sprite record.");
        int tiles = s.Width * s.Height;
        var frames = FrameTiles(rom, template);
        var gfx = new byte[8 * tiles * 32];
        for (int f = 0; f < 8; f++) rom.ReadBytes(s.GraphicsOffset + frames[f] * 32, tiles * 32).CopyTo(gfx, f * tiles * 32);
        var space = ExpansionSpace.Load(rom);
        int g = space.AllocateInOneBank(gfx.Length, ExpansionSpace.Kind.Patch, 0x100 + n);
        space.Save();
        rom.WriteBytes(g, gfx);
        int rec = slots[0];
        var r = rom.ReadBytes(RecordOffset(rom, template), RecordSize);
        int a = SnesAddress(g);
        r[6] = (byte)a; r[7] = (byte)(a >> 8); r[8] = (byte)(a >> 16);
        for (int f = 0; f < 8; f++) r[9 + f] = (byte)(f * tiles);
        rom.WriteBytes(rec, r);
        int b = Block(rom);
        rom.WriteUInt16Le(b + Header + n * 2, (ushort)rec);
        rom.WriteByte(b + 4, (byte)(n + 1));
        rom.FixChecksum();
        Forget();
        return n;
    }

    // ── sprites from a picture ───────────────────────────────────────────

    /// <summary>The palette rows map characters don't use: the game's sprite palette has the same placeholder
    /// colours in rows 0-3, and its map characters use rows 4-7.</summary>
    public const int FirstCharacterRow = 4;

    private static ushort Bgr555(uint argb) =>
        (ushort)((argb >> 19 & 31) | (argb >> 11 & 31) << 5 | (argb >> 3 & 31) << 10);

    /// <summary>
    /// A sprite palette row for <paramref name="colours"/> (up to 15 BGR555 colours, colour 0 left out): a row that
    /// already has them all, else one of rows 0-3 that no sprite uses (they get the colours: the palette resource is
    /// rewritten), else -1.
    /// </summary>
    public static int PaletteRowFor(RomBuffer rom, IReadOnlyList<ushort> colours, bool allowExpand)
    {
        var raw = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
        ushort At(int row, int c) => (ushort)(raw[(row * 16 + c) * 2] | raw[(row * 16 + c) * 2 + 1] << 8);
        for (int row = 0; row < 8; row++)
        {
            var have = Enumerable.Range(1, 15).Select(c => At(row, c)).ToHashSet();
            if (colours.All(have.Contains)) return row;
        }
        var used = Enumerable.Range(0, Count(rom)).Select(s => Info(rom, s)?.Palette ?? -1).ToHashSet();
        for (int row = 0; row < FirstCharacterRow; row++)
        {
            if (used.Contains(row)) continue;
            for (int c = 1; c < 16; c++)
            {
                ushort v = c - 1 < colours.Count ? colours[c - 1] : (ushort)0;
                raw[(row * 16 + c) * 2] = (byte)v; raw[(row * 16 + c) * 2 + 1] = (byte)(v >> 8);
            }
            ResourceWriter.Save(rom, PaletteResource, raw, allowExpand);
            Forget();
            return row;
        }
        return -1;
    }

    /// <summary>
    /// A new sprite from a full-colour sheet: 8 frames side by side (side, side, side, side, front, front, back,
    /// back; side frames face left, frames 0-1 are drawn as 2-3), each 16x24 or 32x32 (16x16 also works, as the
    /// game's few small sprites).
    /// Its colours get a palette row of their own when one is free (<see cref="PaletteRowFor"/>), else the
    /// closest row; <paramref name="exact"/> says which. Frames that are the same share tiles. Returns its number.
    /// </summary>
    public static int AddFromSheet(RomBuffer rom, uint[] px, int frameWidth, int frameHeight, out bool exact, bool allowExpand = true)
    {
        int w = frameWidth / 8, h = frameHeight / 8;
        if (frameWidth % 8 != 0 || frameHeight % 8 != 0 || px.Length != frameWidth * 8 * frameHeight)
            throw new InvalidOperationException("The sheet must be 8 frames side by side, each a whole number of 8x8 tiles.");
        // a game sprite of the same size gives the record's other bytes (how the game draws it)
        // (a monster's first: 33 and 36 walk about as monsters in the game's dungeons)
        int template = new[] { 0x33, 0x36 }.Concat(Enumerable.Range(0, SpriteCount)).FirstOrDefault(s => Info(rom, s) is { } i && i.Width == w && i.Height == h, -1);
        if (template < 0) throw new InvalidOperationException($"The game has no {frameWidth}x{frameHeight} sprites to model one on: use 16x24 or 32x32.");
        var colours = px.Where(p => p >> 24 >= 128).Select(Bgr555).Distinct().ToList();
        if (colours.Count > 15) throw new InvalidOperationException($"The picture has {colours.Count} colours; a sprite can have 15 (and transparent).");
        int row = PaletteRowFor(rom, colours, allowExpand);
        exact = row >= 0;
        if (row < 0)
        {
            // the closest row: the least colour error over the picture
            var raw = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
            long Err(int r) => colours.Sum(c => Enumerable.Range(1, 15).Min(k => Dist(c, (ushort)(raw[(r * 16 + k) * 2] | raw[(r * 16 + k) * 2 + 1] << 8))));
            // (rows 0-3 count when a sprite uses them: their colours are real then, e.g. another Lufia 2 monster's)
            var used = Enumerable.Range(0, Count(rom)).Select(n => Info(rom, n)?.Palette ?? -1).ToHashSet();
            row = Enumerable.Range(0, 8).Where(r => r >= FirstCharacterRow || used.Contains(r)).MinBy(Err);
        }
        var pal = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
        var rowColours = Enumerable.Range(0, 16).Select(k => (ushort)(pal[(row * 16 + k) * 2] | pal[(row * 16 + k) * 2 + 1] << 8)).ToArray();
        byte Index(uint p)
        {
            if (p >> 24 < 128) return 0;
            ushort c = Bgr555(p);
            int best = 1; long be = long.MaxValue;
            for (int k = 1; k < 16; k++) { long e = Dist(c, rowColours[k]); if (e < be) { be = e; best = k; } }
            return (byte)best;
        }
        // tiles of each frame; the same frame twice uses the same tiles
        int sw = frameWidth * 8, tiles = w * h;
        var frameTiles = new List<byte[]>();
        var first = new int[8];
        for (int f = 0; f < 8; f++)
        {
            var data = new byte[tiles * 32];
            for (int t = 0; t < tiles; t++)
            {
                var p64 = new byte[64];
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        p64[y * 8 + x] = Index(px[(t / w * 8 + y) * sw + f * frameWidth + t % w * 8 + x]);
                Modules.TileViewer.SnesTileDecoder.EncodeTile(p64, Modules.TileViewer.BitDepth.Bpp4).CopyTo(data, t * 32);
            }
            int same = frameTiles.FindIndex(d => d.AsSpan().SequenceEqual(data));
            if (same < 0) { same = frameTiles.Count; frameTiles.Add(data); }
            first[f] = same * tiles;
        }
        // (the game draws frames 0-1 from the tiles of 2-3, so they are the same frames)
        first[0] = first[2]; first[1] = first[3];
        Move(rom);
        int n = Count(rom);
        if (n >= Capacity) throw new InvalidOperationException($"The ROM already has {Capacity} sprites.");
        var slots = FreeRecordSlots(rom);
        if (slots.Count == 0) throw new InvalidOperationException("There is no room left in bank 01 for another sprite record.");
        var gfx = frameTiles.SelectMany(d => d).ToArray();
        var space = ExpansionSpace.Load(rom);
        int g = space.AllocateInOneBank(gfx.Length, ExpansionSpace.Kind.Patch, 0x100 + n);
        space.Save();
        rom.WriteBytes(g, gfx);
        var r = rom.ReadBytes(RecordOffset(rom, template), RecordSize);
        r[4] = (byte)(r[4] & ~7 | row);
        // byte 3: which pairs of frames the map loader copies to VRAM ($01:AE7F; bit 1 = frames 2-3, 2 = 4-5,
        // 3 = 6-7; frames 0-1 are 2-3 flipped): the game's 32x32 sprites load only 2-3 (they look the same
        // every way), so a sheet with front and back views needs all three
        r[3] = (byte)(first[4] == first[2] && first[5] == first[3] && first[6] == first[2] && first[7] == first[3] ? 0x02 : 0x0E);
        int a = SnesAddress(g);
        r[6] = (byte)a; r[7] = (byte)(a >> 8); r[8] = (byte)(a >> 16);
        for (int f = 0; f < 8; f++) r[9 + f] = (byte)first[f];
        int rec = slots[0];
        rom.WriteBytes(rec, r);
        int b = Block(rom);
        rom.WriteUInt16Le(b + Header + n * 2, (ushort)rec);
        rom.WriteByte(b + 4, (byte)(n + 1));
        rom.FixChecksum();
        Forget();
        return n;
    }

    private static long Dist(ushort a, ushort b)
    {
        int dr = (a & 31) - (b & 31), dg = (a >> 5 & 31) - (b >> 5 & 31), db = (a >> 10 & 31) - (b >> 10 & 31);
        return dr * dr * 3 + dg * dg * 4 + db * db * 2;
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
