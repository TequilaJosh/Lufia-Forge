namespace LufiaForge.Core.Maps;

/// <summary>
/// The tileset tables, indexed by a map's tileset number (header byte +0x08):
/// <list type="bullet">
/// <item>tile graphics: resource 0xB1 + byte table $01:E134 (read at $01:BE42);</item>
/// <item>blocks: resource 0xB1 + byte table $01:E140 (read at $01:BDC5);</item>
/// <item>composite (ground, overlay) pairs: 3-byte pointers $01:E14B (read at $01:BF06 / $01:BF12 and $01:DA07 /
/// $01:DA13).</item>
/// </list>
/// The game has 11 tilesets and the tables run into each other, so new tilesets move them to a bigger copy in the
/// added space (only those long operands change). Block: "LFTS", count, then the three tables for
/// <see cref="Capacity"/> tilesets.
/// </summary>
public static class Tilesets
{
    public const int OriginalCount = 11, Capacity = 64;
    // file offsets of the long operands (the instruction byte is one before)
    private const int TilesOp = 0xBE43, BlocksOp = 0xBDC6, CompositeOp1 = 0xBF07, CompositeOp2 = 0xDA08, CompositeBankOp1 = 0xBF13, CompositeBankOp2 = 0xDA14;
    private const int Header = 8, TilesAt = Header, BlocksAt = TilesAt + Capacity, CompositeAt = BlocksAt + Capacity, BlockSize = CompositeAt + Capacity * 3;
    private static readonly byte[] Magic = "LFTS"u8.ToArray();

    // Spot drawing ($01:9C19 / $01:9C63, doors and chests) tests the tileset number itself: LDA $7F0008, CMP #$07 -
    // tilesets 0-6 (towns) draw one way, 7 and up (dungeons) another. A new tileset (11+) would always get the dungeon
    // way, so both tests call a routine that compares a style byte per tileset instead (block "LFTY").
    private static readonly int[] StyleSites = { 0x9C2A, 0x9C95 };
    private static readonly byte[] StyleOriginal = { 0xAF, 0x08, 0x00, 0x7F, 0xC9, 0x07 };
    private static readonly byte[] StyleMagic = "LFTY"u8.ToArray();
    private const int StyleTable = 8, StyleCode = StyleTable + Capacity;

    private static int StyleBlock(RomBuffer rom)
    {
        if (rom.ReadByte(StyleSites[0]) != 0x22) return 0;
        int b = FileOffset(Long(rom, StyleSites[0] + 1)) - StyleCode;
        return b > 0 && rom.ReadBytes(b, 4).SequenceEqual(StyleMagic) ? b : 0;
    }

    /// <summary>1 = drawn like a dungeon (the game's tilesets 7-10), 0 = like a town (0-6).</summary>
    public static int Style(RomBuffer rom, int tileset)
    {
        int b = StyleBlock(rom);
        return b > 0 ? rom.ReadByte(b + StyleTable + tileset) : tileset >= 7 ? 1 : 0;
    }

    private static void HookStyles(RomBuffer rom)
    {
        if (StyleBlock(rom) > 0) return;
        if (!StyleSites.All(a => rom.ReadBytes(a, StyleOriginal.Length).SequenceEqual(StyleOriginal)))
            throw new InvalidOperationException("The game's door / chest drawing code isn't the original, so new tilesets can't be added safely.");
        var space = ExpansionSpace.Load(rom);
        int size = StyleCode + 32;
        int at = space.AllocateInOneBank(size, ExpansionSpace.Kind.Patch, 17);
        space.Save();
        var block = new byte[size];
        StyleMagic.CopyTo(block, 0);
        for (int t = 0; t < Capacity; t++) block[StyleTable + t] = (byte)(t >= 7 ? 1 : 0);
        int table = SnesAddress(at + StyleTable);
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
        rom.WriteBytes(at, block);
        int c = SnesAddress(at + StyleCode);
        foreach (int a in StyleSites) rom.WriteBytes(a, new byte[] { 0x22, (byte)c, (byte)(c >> 8), (byte)(c >> 16), 0xEA, 0xEA });
    }

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int Long(RomBuffer rom, int at) => rom.ReadByte(at) | rom.ReadByte(at + 1) << 8 | rom.ReadByte(at + 2) << 16;
    private static void WriteLong(RomBuffer rom, int at, int v) => rom.WriteBytes(at, new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16) });

    public static int TilesTable(RomBuffer rom) => FileOffset(Long(rom, TilesOp));
    public static int BlocksTable(RomBuffer rom) => FileOffset(Long(rom, BlocksOp));
    public static int CompositeTable(RomBuffer rom) => FileOffset(Long(rom, CompositeOp1));

    public static bool IsOriginal(RomBuffer rom) =>
        Long(rom, TilesOp) == 0x01E134 && Long(rom, BlocksOp) == 0x01E140 && Long(rom, CompositeOp1) == 0x01E14B &&
        Long(rom, CompositeOp2) == 0x01E14B && Long(rom, CompositeBankOp1) == 0x01E14D && Long(rom, CompositeBankOp2) == 0x01E14D;

    private static int Block(RomBuffer rom)
    {
        int b = TilesTable(rom) - TilesAt;
        return !IsOriginal(rom) && b > 0 && rom.ReadBytes(b, 4).SequenceEqual(Magic) ? b : 0;
    }
    public static bool IsMoved(RomBuffer rom) => Block(rom) > 0;

    /// <summary>How many tilesets the ROM has (11 in the game).</summary>
    public static int Count(RomBuffer rom) { int b = Block(rom); return b > 0 ? rom.ReadByte(b + 4) : OriginalCount; }

    public static int TilesResource(RomBuffer rom, int tileset) => 0xB1 + rom.ReadByte(TilesTable(rom) + tileset);
    public static int BlocksResource(RomBuffer rom, int tileset) => 0xB1 + rom.ReadByte(BlocksTable(rom) + tileset);
    /// <summary>File offset of the tileset's composite pairs (meaningful only for maps with a composite threshold).</summary>
    public static int CompositePairs(RomBuffer rom, int tileset) => FileOffset(Long(rom, CompositeTable(rom) + tileset * 3));

    /// <summary>Moves the three tables into the added space (room for <see cref="Capacity"/> tilesets).</summary>
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's tileset loader isn't the original, so its tables can't be moved safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 12);
        space.Save();
        var block = new byte[BlockSize];
        Magic.CopyTo(block, 0);
        block[4] = OriginalCount;
        rom.ReadBytes(0xE134, OriginalCount).CopyTo(block, TilesAt);
        rom.ReadBytes(0xE140, OriginalCount).CopyTo(block, BlocksAt);
        // composite pointers: the game has 7 (tilesets 0-6); the others read the next table, as before
        rom.ReadBytes(0xE14B, OriginalCount * 3).CopyTo(block, CompositeAt);
        rom.WriteBytes(at, block);
        WriteLong(rom, TilesOp, SnesAddress(at + TilesAt));
        WriteLong(rom, BlocksOp, SnesAddress(at + BlocksAt));
        WriteLong(rom, CompositeOp1, SnesAddress(at + CompositeAt));
        WriteLong(rom, CompositeOp2, SnesAddress(at + CompositeAt));
        WriteLong(rom, CompositeBankOp1, SnesAddress(at + CompositeAt) + 2);
        WriteLong(rom, CompositeBankOp2, SnesAddress(at + CompositeAt) + 2);
        rom.FixChecksum();
    }

    /// <summary>A new tileset: copies of <paramref name="template"/>'s tile graphics and blocks under new resource
    /// ids (and its composite pairs). Returns its number.</summary>
    public static int Add(RomBuffer rom, int template)
    {
        Move(rom);
        int b = Block(rom), n = Count(rom);
        if (n >= Capacity) throw new InvalidOperationException($"The ROM already has {Capacity} tilesets.");
        if (ResourceIds.Count(rom) + 1 - 0xB1 > 0xFF)
            throw new InvalidOperationException("No resource ids left that a tileset can use (they must be below 1B1).");
        var tiles = LufiaCompression.DecompressResource(rom, TilesResource(rom, template), out _);
        var blocks = LufiaCompression.DecompressResource(rom, BlocksResource(rom, template), out _);
        int t = ResourceIds.Add(rom, tiles), k = ResourceIds.Add(rom, blocks);
        if (t - 0xB1 > 0xFF || k - 0xB1 > 0xFF)
            throw new InvalidOperationException("No resource ids left that a tileset can use (they must be below 1B1).");
        HookStyles(rom);
        rom.WriteByte(StyleBlock(rom) + StyleTable + n, (byte)Style(rom, template));
        b = Block(rom);
        rom.WriteByte(b + TilesAt + n, (byte)(t - 0xB1));
        rom.WriteByte(b + BlocksAt + n, (byte)(k - 0xB1));
        rom.WriteBytes(b + CompositeAt + n * 3, rom.ReadBytes(CompositeTable(rom) + template * 3, 3));
        rom.WriteByte(b + 4, (byte)(n + 1));
        rom.FixChecksum();
        return n;
    }
}
