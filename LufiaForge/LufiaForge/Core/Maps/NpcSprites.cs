namespace LufiaForge.Core.Maps;

// map character sprites (the party and every character on the maps).
// sprite number n -> record pointer: u16 at $01:E25F + n*2, only read by the lookup at $01:AC50 (LDA $01E25F,X /
// TAY), whose callers then read the record with the data bank set to 01, so records have to be in bank 01.
// record (17 bytes): [0] width and [1] height in 8x8 tiles, [2] frames per pair (2), [3] which pairs of frames the
// map loader copies to VRAM ($01:AE7F: bit 1 = frames 2-3, 2 = 4-5, 3 = 6-7), [4] OAM attribute (low 3 bits = palette
// row), [6..8] LoROM address of the graphics (4bpp, uncompressed), [9..16] first tile of each of 8 frames.
// a frame is width*height tiles in row order. colours: sprite palette resource 0xAF (8 rows of 16).
// the game's sprites are 16x16, 16x24 or 32x32.
// new sprites: the pointer table moves to the added space (block "LFSP": count, then room for Capacity pointers,
// only the lookup's long operand changes) and the new records go in the unused tail of bank 01 (RecordArea, the
// game's code ends at $01:F849 and after that it's filler), with their graphics in the added space
public static class NpcSprites
{
    public const int RecordTable = 0xE25F;   // file offset of the game's $01:E25F pointer table
    public const int SpriteCount = 0x4A;     // the game's sprites (entries after 49 are other data)
    public const int RecordSize = 17, Capacity = 0xC0;
    // bank 01 space for new records (file offsets, a bank 01 file offset = its address)
    public const int RecordArea = 0xF880, RecordAreaEnd = 0x10000;
    private const int LookupOperand = 0xAC5D;   // long operand of LDA $01E25F,X in the lookup
    private const int PaletteResource = 0xAF;
    private const int Header = 8;
    private static readonly byte[] Magic = "LFSP"u8.ToArray();

    public sealed record SpriteInfo(int Width, int Height, int Palette, int GraphicsOffset, int FirstTile);

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int Long(RomBuffer rom, int offset) => rom.ReadByte(offset) | rom.ReadByte(offset + 1) << 8 | rom.ReadByte(offset + 2) << 16;

    // file offset of the pointer table the game reads
    public static int TableOffset(RomBuffer rom) => FileOffset(Long(rom, LookupOperand));

    private static int Block(RomBuffer rom)
    {
        int block = TableOffset(rom) - Header;
        return block != RecordTable - Header && block > 0 && rom.ReadBytes(block, 4).SequenceEqual(Magic) ? block : 0;
    }
    public static bool IsMoved(RomBuffer rom) => Block(rom) > 0;

    // how many sprites the rom has (0x4A in the game)
    public static int Count(RomBuffer rom) { int block = Block(rom); return block > 0 ? rom.ReadByte(block + 4) : SpriteCount; }

    // file offset of a sprite's record (bank 01)
    public static int RecordOffset(RomBuffer rom, int sprite) => rom.ReadUInt16Le(TableOffset(rom) + sprite * 2);

    // the record for a sprite number, or null if it isn't a character sprite
    public static SpriteInfo? Info(RomBuffer rom, int sprite)
    {
        if (sprite < 0 || sprite >= Count(rom)) return null;
        int record = RecordOffset(rom, sprite);
        if (record < 0x8000) return null;
        int width = rom.ReadByte(record), height = rom.ReadByte(record + 1);
        if (width is < 1 or > 4 || height is < 1 or > 4) return null;
        int attribute = rom.ReadByte(record + 4);
        int address = Long(rom, record + 6);
        int file = FileOffset(address);
        if ((address & 0x8000) == 0 || file >= rom.Length) return null;
        return new SpriteInfo(width, height, attribute & 7, file, rom.ReadByte(record + 9));
    }

    // whether Lufia Forge added the sprite (its record is in the new record area)
    public static bool IsNew(RomBuffer rom, int sprite) => sprite >= SpriteCount && sprite < Count(rom) && RecordOffset(rom, sprite) >= RecordArea;

    private static readonly Dictionary<int, (uint[] Px, int W, int H)?> Cache = new();
    private static uint[]? _palette;
    private static RomBuffer? _rom;

    private static void Forget() { Cache.Clear(); _palette = null; }

    // BGRA pixels of the sprite's first frame (0 = transparent) and its size, or null
    public static (uint[] Px, int W, int H)? Render(RomBuffer rom, int sprite)
    {
        // (one at a time: the Cutscenes tab draws its pictures on another thread while the Events tab draws too, and
        // a Dictionary written by two threads breaks)
        lock (Cache) return RenderLocked(rom, sprite);
    }

    private static (uint[] Px, int W, int H)? RenderLocked(RomBuffer rom, int sprite)
    {
        if (!ReferenceEquals(rom, _rom)) { Forget(); _rom = rom; }
        if (Cache.TryGetValue(sprite, out var cached)) return cached;
        (uint[], int, int)? result = null;
        if (Info(rom, sprite) is { } info)
        {
            var colours = Colours(rom, info.Palette);
            var indices = SheetIndices(rom, sprite, out int sheetWidth, out int sheetHeight);
            int width = info.Width * 8, height = info.Height * 8;
            var pixels = new uint[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pixels[y * width + x] = colours[indices[y * sheetWidth + x]];
            result = (pixels, width, height);
        }
        Cache[sprite] = result;
        return result;
    }

    // first tile of each of the 8 frames (record bytes 9-16)
    public static int[] FrameTiles(RomBuffer rom, int sprite)
    {
        int record = RecordOffset(rom, sprite);
        return Enumerable.Range(0, 8).Select(f => (int)rom.ReadByte(record + 9 + f)).ToArray();
    }

    // the 16 colours of a sprite palette row (BGRA, colour 0 transparent)
    public static uint[] Colours(RomBuffer rom, int row)
    {
        if (!ReferenceEquals(rom, _rom)) { Forget(); _rom = rom; }
        _palette ??= LoadPalette(rom);
        return Enumerable.Range(0, 16).Select(c => c == 0 ? 0u : _palette[row * 16 + c]).ToArray();
    }

    // colour numbers (0-15) of all 8 frames side by side
    public static byte[] SheetIndices(RomBuffer rom, int sprite, out int w, out int h)
    {
        var info = Info(rom, sprite) ?? throw new InvalidOperationException("Not a character sprite.");
        int frameWidth = info.Width * 8; h = info.Height * 8; w = frameWidth * 8;
        var indices = new byte[w * h];
        var frames = FrameTiles(rom, sprite);
        for (int frame = 0; frame < 8; frame++)
            for (int tile = 0; tile < info.Width * info.Height; tile++)
            {
                int tileOffset = info.GraphicsOffset + (frames[frame] + tile) * 32;
                if (tileOffset + 32 > rom.Length) break;
                var tilePixels = Modules.TileViewer.SnesTileDecoder.DecodeTile(rom.ReadBytes(tileOffset, 32), 0, Modules.TileViewer.BitDepth.Bpp4);
                int tx = frame * frameWidth + tile % info.Width * 8, ty = tile / info.Width * 8;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) indices[(ty + y) * w + tx + x] = tilePixels[y * 8 + x];
            }
        return indices;
    }

    // all 8 frames side by side (BGRA, alpha 0 = transparent)
    public static (uint[] Px, int W, int H)? RenderSheet(RomBuffer rom, int sprite)
    {
        if (Info(rom, sprite) is not { } info) return null;
        var colours = Colours(rom, info.Palette);
        var indices = SheetIndices(rom, sprite, out int w, out int h);
        return (indices.Select(i => colours[i]).ToArray(), w, h);
    }

    // writes colour numbers (0-15, same layout as SheetIndices) into the sprite's graphics. frames that share tiles
    // end up with the last frame drawn there. returns how many tiles got written
    public static int ImportIndices(RomBuffer rom, int sprite, byte[] idx, int w, int h)
    {
        if (Info(rom, sprite) is not { } info) throw new InvalidOperationException("Not a character sprite.");
        int frameWidth = info.Width * 8;
        if (w != frameWidth * 8 || h != info.Height * 8)
            throw new InvalidOperationException($"The sheet must be {frameWidth * 8} x {info.Height * 8} pixels: 8 frames of {frameWidth} x {info.Height * 8}.");
        var frames = FrameTiles(rom, sprite);
        int written = 0;
        for (int frame = 0; frame < 8; frame++)
            for (int tile = 0; tile < info.Width * info.Height; tile++)
            {
                var tilePixels = new byte[64];
                int tx = frame * frameWidth + tile % info.Width * 8, ty = tile / info.Width * 8;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) tilePixels[y * 8 + x] = (byte)(idx[(ty + y) * w + tx + x] & 15);
                int tileOffset = info.GraphicsOffset + (frames[frame] + tile) * 32;
                if (tileOffset + 32 > rom.Length) continue;
                rom.WriteBytes(tileOffset, Modules.TileViewer.SnesTileDecoder.EncodeTile(tilePixels, Modules.TileViewer.BitDepth.Bpp4)); written++;
            }
        Forget();
        return written;
    }

    // draws a full-colour sheet (same layout as RenderSheet) into the sprite's graphics, in its palette row:
    // transparent pixels become colour 0, the others the nearest of colours 1-15
    public static int ImportSheet(RomBuffer rom, int sprite, uint[] px, int w, int h)
    {
        if (Info(rom, sprite) is not { } info) throw new InvalidOperationException("Not a character sprite.");
        var raw = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
        var palette = Enumerable.Range(0, 16).Select(c => raw[(info.Palette * 16 + c) * 2] | raw[(info.Palette * 16 + c) * 2 + 1] << 8).ToArray();
        var indices = new byte[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            uint pixel = px[i];
            if (pixel >> 24 < 128) continue;
            int r = (int)(pixel >> 16 & 255) >> 3, g = (int)(pixel >> 8 & 255) >> 3, b = (int)(pixel & 255) >> 3;
            long best = long.MaxValue;
            for (int k = 1; k < 16; k++)
            {
                int dr = r - (palette[k] & 31), dg = g - (palette[k] >> 5 & 31), db = b - (palette[k] >> 10 & 31);
                long error = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (error < best) { best = error; indices[i] = (byte)k; }
            }
        }
        return ImportIndices(rom, sprite, indices, w, h);
    }

    // gives the sprite another palette row (0-7) of the sprite palette
    public static void SetPaletteRow(RomBuffer rom, int sprite, int row)
    {
        if (Info(rom, sprite) == null) throw new InvalidOperationException("Not a character sprite.");
        int record = RecordOffset(rom, sprite);
        rom.WriteByte(record + 4, (byte)(rom.ReadByte(record + 4) & ~7 | row & 7));
        rom.FixChecksum();
        Forget();
    }

    // new sprites

    // record slots in the bank 01 area that no sprite uses
    private static List<int> FreeRecordSlots(RomBuffer rom)
    {
        var used = Enumerable.Range(0, Count(rom)).Select(s => RecordOffset(rom, s)).ToHashSet();
        var free = new List<int>();
        for (int slot = RecordArea; slot + RecordSize <= RecordAreaEnd; slot += RecordSize) if (!used.Contains(slot)) free.Add(slot);
        return free;
    }

    // how many more sprites can be added
    public static int Room(RomBuffer rom) => Math.Min(Capacity - Count(rom), FreeRecordSlots(rom).Count);

    // moves the pointer table into the added space (room for Capacity sprites)
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (TableOffset(rom) != RecordTable || rom.ReadByte(LookupOperand - 1) != 0xBF)
            throw new InvalidOperationException("The game's sprite lookup isn't the original, so its table can't be moved safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int blockOffset = freeSpace.AllocateInOneBank(Header + Capacity * 2, ExpansionSpace.Kind.Patch, 13);
        freeSpace.Save();
        var block = new byte[Header + Capacity * 2];
        Magic.CopyTo(block, 0);
        block[4] = SpriteCount;
        // (the entries past the last sprite are whatever data follows the table. we copy them too, so a number
        // the game might use there still reads the same "record")
        rom.ReadBytes(RecordTable, Capacity * 2).CopyTo(block, Header);
        rom.WriteBytes(blockOffset, block);
        int tableAddress = SnesAddress(blockOffset + Header);
        rom.WriteBytes(LookupOperand, new[] { (byte)tableAddress, (byte)(tableAddress >> 8), (byte)(tableAddress >> 16) });
        rom.FixChecksum();
    }

    // a new sprite the size of template, with the template's 8 frames copied into graphics of its own (every frame
    // gets its own tiles, so all 8 can be drawn). returns its number
    public static int Add(RomBuffer rom, int template)
    {
        var info = Info(rom, template) ?? throw new InvalidOperationException("Not a character sprite.");
        Move(rom);
        int number = Count(rom);
        if (number >= Capacity) throw new InvalidOperationException($"The ROM already has {Capacity} sprites.");
        var slots = FreeRecordSlots(rom);
        if (slots.Count == 0) throw new InvalidOperationException("There is no room left in bank 01 for another sprite record.");
        int tiles = info.Width * info.Height;
        var frames = FrameTiles(rom, template);
        var graphics = new byte[8 * tiles * 32];
        for (int frame = 0; frame < 8; frame++) rom.ReadBytes(info.GraphicsOffset + frames[frame] * 32, tiles * 32).CopyTo(graphics, frame * tiles * 32);
        var freeSpace = ExpansionSpace.Load(rom);
        int graphicsOffset = freeSpace.AllocateInOneBank(graphics.Length, ExpansionSpace.Kind.Patch, 0x100 + number);
        freeSpace.Save();
        rom.WriteBytes(graphicsOffset, graphics);
        int recordOffset = slots[0];
        var record = rom.ReadBytes(RecordOffset(rom, template), RecordSize);
        int address = SnesAddress(graphicsOffset);
        record[6] = (byte)address; record[7] = (byte)(address >> 8); record[8] = (byte)(address >> 16);
        for (int frame = 0; frame < 8; frame++) record[9 + frame] = (byte)(frame * tiles);
        rom.WriteBytes(recordOffset, record);
        int block = Block(rom);
        rom.WriteUInt16Le(block + Header + number * 2, (ushort)recordOffset);
        rom.WriteByte(block + 4, (byte)(number + 1));
        rom.FixChecksum();
        Forget();
        return number;
    }

    // sprites from a picture

    // the palette rows map characters don't use: the game's sprite palette has the same placeholder colours in
    // rows 0-3, and its map characters use rows 4-7
    public const int FirstCharacterRow = 4;

    private static ushort Bgr555(uint argb) =>
        (ushort)((argb >> 19 & 31) | (argb >> 11 & 31) << 5 | (argb >> 3 & 31) << 10);

    // a sprite palette row for colours (up to 15 BGR555 colours, colour 0 left out): a row that already has them
    // all, otherwise one of rows 0-3 that no sprite uses (it gets the colours and the palette resource is rewritten),
    // otherwise -1
    public static int PaletteRowFor(RomBuffer rom, IReadOnlyList<ushort> colours, bool allowExpand, IReadOnlyList<ushort>? whole = null)
    {
        // (a picture's whole palette, if it has one, goes into a new row, so pictures sharing it share the row too)
        if (whole != null && whole.Count <= 15 && colours.All(whole.Contains)) colours = whole;
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
                ushort colour = c - 1 < colours.Count ? colours[c - 1] : (ushort)0;
                raw[(row * 16 + c) * 2] = (byte)colour; raw[(row * 16 + c) * 2 + 1] = (byte)(colour >> 8);
            }
            ResourceWriter.Save(rom, PaletteResource, raw, allowExpand);
            Forget();
            return row;
        }
        return -1;
    }

    // a new sprite from a full-colour sheet: 8 frames side by side (side, side, side, side, front, front, back, back,
    // side frames face left, frames 0-1 are drawn as 2-3), each 16x24 or 32x32 (16x16 works too, like the game's few
    // small sprites).
    // its colours get a palette row of their own if one is free (PaletteRowFor), otherwise the closest row, and
    // exact says which. frames that are the same share tiles. returns its number
    public static int AddFromSheet(RomBuffer rom, uint[] px, int frameWidth, int frameHeight, out bool exact, bool allowExpand = true,
                                   IReadOnlyList<ushort>? palette = null)
    {
        int tilesWide = frameWidth / 8, tilesHigh = frameHeight / 8;
        if (frameWidth % 8 != 0 || frameHeight % 8 != 0 || px.Length != frameWidth * 8 * frameHeight)
            throw new InvalidOperationException("The sheet must be 8 frames side by side, each a whole number of 8x8 tiles.");
        // a game sprite of the same size gives us the record's other bytes (how the game draws it)
        // (a monster's first: 33 and 36 walk about as monsters in the game's dungeons)
        int template = new[] { 0x33, 0x36 }.Concat(Enumerable.Range(0, SpriteCount)).FirstOrDefault(s => Info(rom, s) is { } i && i.Width == tilesWide && i.Height == tilesHigh, -1);
        if (template < 0) throw new InvalidOperationException($"The game has no {frameWidth}x{frameHeight} sprites to model one on: use 16x24 or 32x32.");
        var colours = px.Where(p => p >> 24 >= 128).Select(Bgr555).Distinct().ToList();
        if (colours.Count > 15) throw new InvalidOperationException($"The picture has {colours.Count} colours; a sprite can have 15 (and transparent).");
        int row = PaletteRowFor(rom, colours, allowExpand, palette);
        exact = row >= 0;
        if (row < 0)
        {
            // the closest row = the least colour error over the picture
            var raw = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
            long Error(int r) => colours.Sum(c => Enumerable.Range(1, 15).Min(k => Dist(c, (ushort)(raw[(r * 16 + k) * 2] | raw[(r * 16 + k) * 2 + 1] << 8))));
            // (rows 0-3 count when a sprite uses them, their colours are real then, like another Lufia 2 monster's)
            var used = Enumerable.Range(0, Count(rom)).Select(n => Info(rom, n)?.Palette ?? -1).ToHashSet();
            row = Enumerable.Range(0, 8).Where(r => r >= FirstCharacterRow || used.Contains(r)).MinBy(Error);
        }
        var paletteBytes = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
        var rowColours = Enumerable.Range(0, 16).Select(k => (ushort)(paletteBytes[(row * 16 + k) * 2] | paletteBytes[(row * 16 + k) * 2 + 1] << 8)).ToArray();
        byte Index(uint pixel)
        {
            if (pixel >> 24 < 128) return 0;
            ushort colour = Bgr555(pixel);
            int best = 1; long bestError = long.MaxValue;
            for (int k = 1; k < 16; k++) { long error = Dist(colour, rowColours[k]); if (error < bestError) { bestError = error; best = k; } }
            return (byte)best;
        }
        // tiles of each frame, the same frame twice uses the same tiles
        int sheetWidth = frameWidth * 8, tiles = tilesWide * tilesHigh;
        var frameTiles = new List<byte[]>();
        var first = new int[8];
        for (int frame = 0; frame < 8; frame++)
        {
            var data = new byte[tiles * 32];
            for (int tile = 0; tile < tiles; tile++)
            {
                var tilePixels = new byte[64];
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        tilePixels[y * 8 + x] = Index(px[(tile / tilesWide * 8 + y) * sheetWidth + frame * frameWidth + tile % tilesWide * 8 + x]);
                Modules.TileViewer.SnesTileDecoder.EncodeTile(tilePixels, Modules.TileViewer.BitDepth.Bpp4).CopyTo(data, tile * 32);
            }
            int same = frameTiles.FindIndex(d => d.AsSpan().SequenceEqual(data));
            if (same < 0) { same = frameTiles.Count; frameTiles.Add(data); }
            first[frame] = same * tiles;
        }
        // (the game draws frames 0-1 from the tiles of 2-3, so they're the same frames)
        first[0] = first[2]; first[1] = first[3];
        Move(rom);
        int number = Count(rom);
        if (number >= Capacity) throw new InvalidOperationException($"The ROM already has {Capacity} sprites.");
        var slots = FreeRecordSlots(rom);
        if (slots.Count == 0) throw new InvalidOperationException("There is no room left in bank 01 for another sprite record.");
        var graphics = frameTiles.SelectMany(d => d).ToArray();
        var freeSpace = ExpansionSpace.Load(rom);
        int graphicsOffset = freeSpace.AllocateInOneBank(graphics.Length, ExpansionSpace.Kind.Patch, 0x100 + number);
        freeSpace.Save();
        rom.WriteBytes(graphicsOffset, graphics);
        var record = rom.ReadBytes(RecordOffset(rom, template), RecordSize);
        // byte 4: palette row, and bit 20 = drawn flipped when it faces right ($01:AC10: the character's $0C = byte 4
        // x 2 | 20, and bit 40 there flips frames 0-1). the game's 32x32 sprites (the same from every side) don't
        // have it, so a sheet with side views copied from them walked right facing left
        bool sides = first[0] != first[4] || first[0] != first[6];
        record[4] = (byte)(record[4] & ~0x27 | row | (sides ? 0x20 : record[4] & 0x20));
        // byte 3: which pairs of frames the map loader copies to VRAM ($01:AE7F, bit 1 = frames 2-3, 2 = 4-5,
        // 3 = 6-7, frames 0-1 are 2-3 flipped). the game's 32x32 sprites only load 2-3 (they look the same
        // every way), so a sheet with front and back views needs all three
        record[3] = (byte)(first[4] == first[2] && first[5] == first[3] && first[6] == first[2] && first[7] == first[3] ? 0x02 : 0x0E);
        int address = SnesAddress(graphicsOffset);
        record[6] = (byte)address; record[7] = (byte)(address >> 8); record[8] = (byte)(address >> 16);
        for (int frame = 0; frame < 8; frame++) record[9 + frame] = (byte)first[frame];
        int recordOffset = slots[0];
        rom.WriteBytes(recordOffset, record);
        int block = Block(rom);
        rom.WriteUInt16Le(block + Header + number * 2, (ushort)recordOffset);
        rom.WriteByte(block + 4, (byte)(number + 1));
        rom.FixChecksum();
        Forget();
        return number;
    }

    private static long Dist(ushort a, ushort b)
    {
        int dr = (a & 31) - (b & 31), dg = (a >> 5 & 31) - (b >> 5 & 31), db = (a >> 10 & 31) - (b >> 10 & 31);
        return dr * dr * 3 + dg * dg * 4 + db * db * 2;
    }

    private static uint[] LoadPalette(RomBuffer rom)
    {
        var raw = LufiaCompression.DecompressResource(rom, PaletteResource, out _);
        var palette = new uint[128];
        for (int i = 0; i < 128 && i * 2 + 1 < raw.Length; i++)
            palette[i] = Modules.TileViewer.SnesPalette.Bgr555ToArgb32((ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8)));
        return palette;
    }
}
