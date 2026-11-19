namespace LufiaForge.Core.Maps;

// the tileset tables, indexed by a map's tileset number (header byte +0x08):
// - tile graphics: resource 0xB1 + byte table $01:E134 (read at $01:BE42)
// - blocks: resource 0xB1 + byte table $01:E140 (read at $01:BDC5)
// - composite (ground, overlay) pairs: 3-byte pointers $01:E14B (read at $01:BF06 / $01:BF12 and $01:DA07 / $01:DA13)
// the game has 11 tilesets and the tables run into each other, so new tilesets move them to a bigger copy in the
// added space (only those long operands change). block: "LFTS", count, then the three tables for Capacity tilesets
public static class Tilesets
{
    public const int OriginalCount = 11, Capacity = 64;
    // file offsets of the long operands (the instruction byte is one before)
    private const int TilesOp = 0xBE43, BlocksOp = 0xBDC6, CompositeOp1 = 0xBF07, CompositeOp2 = 0xDA08, CompositeBankOp1 = 0xBF13, CompositeBankOp2 = 0xDA14;
    private const int Header = 8, TilesAt = Header, BlocksAt = TilesAt + Capacity, CompositeAt = BlocksAt + Capacity, BlockSize = CompositeAt + Capacity * 3;
    private static readonly byte[] Magic = "LFTS"u8.ToArray();

    // spot drawing ($01:9C19 / $01:9C63, doors and chests) tests the tileset number itself: LDA $7F0008, CMP #$07.
    // tilesets 0-6 (towns) draw one way, 7 and up (dungeons) another. a new tileset (11+) would always get the dungeon
    // way, so both tests call a routine that compares a style byte per tileset instead (block "LFTY")
    private static readonly int[] StyleSites = { 0x9C2A, 0x9C95 };
    private static readonly byte[] StyleOriginal = { 0xAF, 0x08, 0x00, 0x7F, 0xC9, 0x07 };
    private static readonly byte[] StyleMagic = "LFTY"u8.ToArray();
    private const int StyleTable = 8, StyleCode = StyleTable + Capacity;

    private static int StyleBlock(RomBuffer rom)
    {
        if (rom.ReadByte(StyleSites[0]) != 0x22) return 0;
        int block = FileOffset(Long(rom, StyleSites[0] + 1)) - StyleCode;
        return block > 0 && rom.ReadBytes(block, 4).SequenceEqual(StyleMagic) ? block : 0;
    }

    // 1 = drawn like a dungeon (the game's tilesets 7-10), 0 = like a town (0-6)
    public static int Style(RomBuffer rom, int tileset)
    {
        int block = StyleBlock(rom);
        return block > 0 ? rom.ReadByte(block + StyleTable + tileset) : tileset >= 7 ? 1 : 0;
    }

    private static void HookStyles(RomBuffer rom)
    {
        if (StyleBlock(rom) > 0) return;
        if (!StyleSites.All(site => rom.ReadBytes(site, StyleOriginal.Length).SequenceEqual(StyleOriginal)))
            throw new InvalidOperationException("The game's door / chest drawing code isn't the original, so new tilesets can't be added safely.");
        var freeSpace = ExpansionSpace.Load(rom);
        int size = StyleCode + 32;
        int blockOffset = freeSpace.AllocateInOneBank(size, ExpansionSpace.Kind.Patch, 17);
        freeSpace.Save();
        var block = new byte[size];
        StyleMagic.CopyTo(block, 0);
        for (int tileset = 0; tileset < Capacity; tileset++) block[StyleTable + tileset] = (byte)(tileset >= 7 ? 1 : 0);
        int table = SnesAddress(blockOffset + StyleTable);
        var code = new byte[]
        {
            0x08, 0xDA,                                   // PHP; PHX        (entered with M = 8)
            0xC2, 0x30, 0xAF, 0x08, 0x00, 0x7F,           // REP #$30; LDA $7F0008
            0x29, 0xFF, 0x00, 0xAA,                       // AND #$00FF; TAX
            0xE2, 0x20,                                   // SEP #$20
            0xBF, (byte)table, (byte)(table >> 8), (byte)(table >> 16),   // LDA style,X
            0xFA, 0x28,                                   // PLX; PLP
            0xC9, 0x01, 0x6B,                             // CMP #$01 (carry set = dungeon, as CMP #$07 did); RTL
        };
        code.CopyTo(block, StyleCode);
        rom.WriteBytes(blockOffset, block);
        int codeAddress = SnesAddress(blockOffset + StyleCode);
        foreach (int site in StyleSites) rom.WriteBytes(site, new byte[] { 0x22, (byte)codeAddress, (byte)(codeAddress >> 8), (byte)(codeAddress >> 16), 0xEA, 0xEA });
    }

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int Long(RomBuffer rom, int offset) => rom.ReadByte(offset) | rom.ReadByte(offset + 1) << 8 | rom.ReadByte(offset + 2) << 16;
    private static void WriteLong(RomBuffer rom, int offset, int value) => rom.WriteBytes(offset, new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16) });

    public static int TilesTable(RomBuffer rom) => FileOffset(Long(rom, TilesOp));
    public static int BlocksTable(RomBuffer rom) => FileOffset(Long(rom, BlocksOp));
    public static int CompositeTable(RomBuffer rom) => FileOffset(Long(rom, CompositeOp1));

    public static bool IsOriginal(RomBuffer rom) =>
        Long(rom, TilesOp) == 0x01E134 && Long(rom, BlocksOp) == 0x01E140 && Long(rom, CompositeOp1) == 0x01E14B &&
        Long(rom, CompositeOp2) == 0x01E14B && Long(rom, CompositeBankOp1) == 0x01E14D && Long(rom, CompositeBankOp2) == 0x01E14D;

    private static int Block(RomBuffer rom)
    {
        int block = TilesTable(rom) - TilesAt;
        return !IsOriginal(rom) && block > 0 && rom.ReadBytes(block, 4).SequenceEqual(Magic) ? block : 0;
    }
    public static bool IsMoved(RomBuffer rom) => Block(rom) > 0;

    // how many tilesets the rom has (11 in the game)
    public static int Count(RomBuffer rom) { int block = Block(rom); return block > 0 ? rom.ReadByte(block + 4) : OriginalCount; }

    public static int TilesResource(RomBuffer rom, int tileset) => 0xB1 + rom.ReadByte(TilesTable(rom) + tileset);
    public static int BlocksResource(RomBuffer rom, int tileset) => 0xB1 + rom.ReadByte(BlocksTable(rom) + tileset);
    // file offset of the tileset's composite pairs (only means something for maps with a composite threshold)
    public static int CompositePairs(RomBuffer rom, int tileset) => FileOffset(Long(rom, CompositeTable(rom) + tileset * 3));

    // points a tileset at another (ground, overlay) pair table (file offset, the tilesets table has to be moved)
    public static void SetCompositePairs(RomBuffer rom, int tileset, int fileOffset)
    {
        Move(rom);
        WriteLong(rom, CompositeTable(rom) + tileset * 3, SnesAddress(fileOffset));
    }

    // moves the three tables into the added space (room for Capacity tilesets)
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's tileset loader isn't the original, so its tables can't be moved safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int blockOffset = freeSpace.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 12);
        freeSpace.Save();
        var block = new byte[BlockSize];
        Magic.CopyTo(block, 0);
        block[4] = OriginalCount;
        rom.ReadBytes(0xE134, OriginalCount).CopyTo(block, TilesAt);
        rom.ReadBytes(0xE140, OriginalCount).CopyTo(block, BlocksAt);
        // composite pointers: the game has 7 (tilesets 0-6), the others read the next table, same as before
        rom.ReadBytes(0xE14B, OriginalCount * 3).CopyTo(block, CompositeAt);
        rom.WriteBytes(blockOffset, block);
        WriteLong(rom, TilesOp, SnesAddress(blockOffset + TilesAt));
        WriteLong(rom, BlocksOp, SnesAddress(blockOffset + BlocksAt));
        WriteLong(rom, CompositeOp1, SnesAddress(blockOffset + CompositeAt));
        WriteLong(rom, CompositeOp2, SnesAddress(blockOffset + CompositeAt));
        WriteLong(rom, CompositeBankOp1, SnesAddress(blockOffset + CompositeAt) + 2);
        WriteLong(rom, CompositeBankOp2, SnesAddress(blockOffset + CompositeAt) + 2);
        rom.FixChecksum();
    }

    // a new tileset: copies of template's tile graphics and blocks under new resource ids (and its composite pairs).
    // returns its number
    public static int Add(RomBuffer rom, int template)
    {
        Move(rom);
        int block = Block(rom), number = Count(rom);
        if (number >= Capacity) throw new InvalidOperationException($"The ROM already has {Capacity} tilesets.");
        if (ResourceIds.Count(rom) + 1 - 0xB1 > 0xFF)
            throw new InvalidOperationException("No resource ids left that a tileset can use (they must be below 1B1).");
        var tiles = LufiaCompression.DecompressResource(rom, TilesResource(rom, template), out _);
        var blocks = LufiaCompression.DecompressResource(rom, BlocksResource(rom, template), out _);
        int tilesId = ResourceIds.Add(rom, tiles), blocksId = ResourceIds.Add(rom, blocks);
        if (tilesId - 0xB1 > 0xFF || blocksId - 0xB1 > 0xFF)
            throw new InvalidOperationException("No resource ids left that a tileset can use (they must be below 1B1).");
        HookStyles(rom);
        rom.WriteByte(StyleBlock(rom) + StyleTable + number, (byte)Style(rom, template));
        block = Block(rom);
        rom.WriteByte(block + TilesAt + number, (byte)(tilesId - 0xB1));
        rom.WriteByte(block + BlocksAt + number, (byte)(blocksId - 0xB1));
        rom.WriteBytes(block + CompositeAt + number * 3, rom.ReadBytes(CompositeTable(rom) + template * 3, 3));
        rom.WriteByte(block + 4, (byte)(number + 1));
        rom.FixChecksum();
        return number;
    }
}
