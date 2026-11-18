namespace LufiaForge.Core.Lufia2;

// new map sprites and new map sprite palettes (US rom, from the game's code 2026-10-25, docs/lufia2/graphics.md)
// - a map sprite number is one byte ($05D2,X per actor): 0-FA are the game's 251 sprites (table $CF:F000), FE is an
//   event object and FF is no sprite, so FB, FC and FD are the only numbers left for new sprites. each map says which
//   new sprite FB, FC and FD show there (a table by map number, $05AC), so up to MaxNew new sprites can be made, 3 of
//   them on any one map. a map that names none shows new sprites 1, 2 and 3
// - $83:A9E5 sets an actor up from its sprite's 5-byte entry (flags, colour row, u24 tiles). the colour row is one of
//   the 8 sprite rows the map loads from $A6:BF00 ($83:B062 copies them to the CGRAM buffer $0420)
// two hooks (the game's other bytes stay as they are). $83:A9F5 (LDA $CFF000,X) jumps to code in the added space that
// reads the entries of FB-FD from our block, and for a sprite given a new palette, copies its 15 colours into a sprite
// row of the CGRAM buffer: a row that already shows them, otherwise the first row no other actor on the map uses in
// the order 5, 4, 3, 2, 0, 7, 6, 1 (field monster rows first, the party's last), otherwise the row its entry names.
// $83:A956 (LDA $CFF6B5,X, the walking script of a sprite from $80 up) reads a new sprite's from its entry.
// in lufia forge a new sprite has the number GameCount + n (n from 0), shown as "New n + 1". on a map it's written as
// FB, FC or FD (ToGame, ToEditor)
public static class L2Sprites
{
    // the game's sprites, how many new sprites there can be in all (3 on one map: FB, FC, FD), and the numbers we give
    // sprites (new ones from GameCount)
    public const int GameCount = 251, MaxNew = 128, MaxCount = GameCount + MaxNew, FirstSlot = 0xFB, SlotsPerMap = 3;

    // new palettes go from 1 to this many (a sprite's palette byte, 0 = its row's colours)
    public const int MaxPalettes = 127;

    private const int LookupSite = 0x1A9F5, WalkSite = 0x1A956;   // $83:A9F5, $83:A956
    private static readonly byte[] LookupOriginal = { 0xBF, 0x00, 0xF0, 0xCF }, WalkOriginal = { 0xBF, 0xB5, 0xF6, 0xCF };

    // the block: "LF2SP", version, new sprite count, palette count, then bit masks, row order, code, tables. version 1
    // (FB-FD only, a palette byte per sprite number) still gets read, anything that writes makes it version 2: the
    // palette of a new sprite is byte 6 of its entry, a map table (4 bytes a map: the new sprite + 1 that FB, FC, FD
    // show, 0 = new sprite 1, 2, 3), and one more entry (a copy of sprite 0's) for FE and FF
    private const string Magic = "LF2SP";
    private const int BitsAt = 0x08, OrderAt = 0x10, CodeAt = 0x20, EntrySize = 8, Version = 2;
    private sealed record Layout(int PaletteOf, int Maps, int Entries, int Palettes, int MaxNew);
    private static readonly Layout V1 = new(0x200, -1, 0x300, 0x400, 3), V2 = new(0x300, 0x400, 0x800, 0xC20, MaxNew);
    private const int BlockSize = 0xC20 + MaxPalettes * 32 + 0x20, V1Size = 0x400 + MaxPalettes * 32 + 0x20;
    private static Layout LayoutOf(RomBuffer rom, int block) => rom.ReadByte(block + 5) >= 2 ? V2 : V1;
    private static readonly byte[] RowOrder = { 5, 4, 3, 2, 0, 7, 6, 1 };

    // file offset of our block, or -1 (not added yet)
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= LookupSite + 4 || rom.ReadByte(LookupSite) != 0x5C) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(LookupSite + 1)) - CodeAt;
        return block >= L2Rom.OriginalSize && block + V1Size <= rom.Length &&
               System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    // true if the hooks can go in (both places still hold the game's code)
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int at, byte[] expected) => at + expected.Length <= rom.Length && rom.ReadBytes(at, expected.Length).SequenceEqual(expected);
        if (Same(LookupSite, LookupOriginal) && Same(WalkSite, WalkOriginal)) return true;
        why = "This ROM's map sprite code isn't the US game's where the hooks go (another patch or hack changed it), so new sprites and palettes can't be added.";
        return false;
    }

    // how many map sprites there are: the game's 251 plus the new ones (numbered from GameCount)
    public static int Count(RomBuffer rom) => Block(rom) is var block && block >= 0 ? GameCount + Math.Min((int)rom.ReadByte(block + 6), LayoutOf(rom, block).MaxNew) : GameCount;

    // how a sprite is named: its number, or "New n" for an added one
    public static string Name(int sprite) => sprite < GameCount ? sprite.ToString("X2") : $"New {sprite - GameCount + 1}";

    // how many new palettes there are
    public static int PaletteCount(RomBuffer rom) => Block(rom) is var block && block >= 0 ? Math.Min((int)rom.ReadByte(block + 7), MaxPalettes) : 0;

    public static bool IsNew(int sprite) => sprite >= GameCount;

    // file offset of a sprite's 5-byte entry (flags, colour row, u24 tiles)
    public static int EntryAt(RomBuffer rom, int sprite)
    {
        if (sprite < GameCount) return L2Rom.SnesToFile(0xCF0000 | rom.ReadUInt16Le(L2Graphics.MapSpriteTable + sprite * 2));
        int block = Block(rom);
        if (block < 0 || sprite >= Count(rom)) throw new ArgumentOutOfRangeException(nameof(sprite));
        return block + LayoutOf(rom, block).Entries + (sprite - GameCount) * EntrySize;
    }

    // the new palette a sprite uses (1 up), or 0 = the colours of its row
    public static int PaletteOf(RomBuffer rom, int sprite)
    {
        int block = Block(rom);
        if (block < 0 || sprite < 0 || sprite >= Count(rom)) return 0;
        var layout = LayoutOf(rom, block);
        int palette = sprite >= GameCount && layout == V2 ? rom.ReadByte(EntryAt(rom, sprite) + 6) : rom.ReadByte(block + layout.PaletteOf + sprite);
        return palette <= PaletteCount(rom) ? palette : 0;
    }

    // file offset of new palette p (1 up): 16 colours, colour 0 unused (see-through)
    public static int PaletteAt(RomBuffer rom, int p)
    {
        int block = Block(rom);
        if (block < 0 || p < 1 || p > PaletteCount(rom)) throw new ArgumentOutOfRangeException(nameof(p));
        return block + LayoutOf(rom, block).Palettes + (p - 1) * 32;
    }

    // which new sprites a map shows

    // the new sprites (0 up) FB, FC and FD show on the map
    public static int[] Slots(RomBuffer rom, int map)
    {
        var slots = new[] { 0, 1, 2 };
        int block = Block(rom);
        if (block < 0 || LayoutOf(rom, block) != V2 || map is < 0 or > 255) return slots;
        for (int s = 0; s < SlotsPerMap; s++) if (rom.ReadByte(block + V2.Maps + map * 4 + s) is var value && value > 0) slots[s] = value - 1;
        return slots;
    }

    // the sprite (our number) a sprite byte of a script on the map shows: FB, FC and FD are that map's new sprites
    public static int ToEditor(RomBuffer rom, int map, int spriteByte) =>
        spriteByte is >= FirstSlot and < FirstSlot + SlotsPerMap && Block(rom) >= 0 ? GameCount + Slots(rom, map)[spriteByte - FirstSlot] : spriteByte;

    // the byte a script on the map makes sprite with: its own number, or for a new sprite FB, FC or FD (one the map
    // already shows it with, otherwise one of those that bytesInUse doesn't hold, which then shows it on this map)
    public static int ToGame(RomBuffer rom, int map, int sprite, ICollection<int> bytesInUse)
    {
        if (sprite < GameCount) return sprite;
        int newIndex = sprite - GameCount;
        var slots = Slots(rom, map);
        for (int s = 0; s < SlotsPerMap; s++) if (slots[s] == newIndex) return FirstSlot + s;
        for (int s = 0; s < SlotsPerMap; s++)
            if (!bytesInUse.Contains(FirstSlot + s))
            {
                int block = Install(rom);
                rom.WriteByte(block + V2.Maps + map * 4 + s, (byte)(newIndex + 1));
                return FirstSlot + s;
            }
        throw new InvalidOperationException($"Map {map:X2} already shows 3 new sprites (FB, FC and FD: {string.Join(", ", slots.Select(x => Name(GameCount + x)))}), the most one map can.");
    }

    // the colour row the sprite's entry names (0-7)
    public static int RowOf(RomBuffer rom, int sprite) => rom.ReadByte(EntryAt(rom, sprite) + 1) & 7;

    // other sprites that share this one's entry (changing the row changes them too)
    public static List<int> SharingEntry(RomBuffer rom, int sprite)
    {
        int entry = EntryAt(rom, sprite);
        return Enumerable.Range(0, Count(rom)).Where(s => s != sprite && EntryAt(rom, s) == entry).ToList();
    }

    // other sprites whose tiles start at the same place (painting one paints them too)
    public static List<int> SharingTiles(RomBuffer rom, int sprite)
    {
        int tiles = (int)rom.ReadUInt24Le(EntryAt(rom, sprite) + 2);
        return Enumerable.Range(0, Count(rom)).Where(s => s != sprite && (int)rom.ReadUInt24Le(EntryAt(rom, s) + 2) == tiles).ToList();
    }

    // sets the colour row a sprite names. true if it changed
    public static bool SetRow(RomBuffer rom, int sprite, int row)
    {
        int entry = EntryAt(rom, sprite);
        byte old = rom.ReadByte(entry + 1), value = (byte)(old & ~7 | row & 7);
        if (value == old) return false;
        rom.WriteByte(entry + 1, value);
        return true;
    }

    // gives a sprite new palette p (0 = back to its row's colours). true if it changed
    public static bool SetPalette(RomBuffer rom, int sprite, int p)
    {
        if (p < 0 || p > MaxPalettes || sprite < 0 || sprite >= Math.Max(Count(rom), GameCount)) throw new ArgumentOutOfRangeException(nameof(p));
        if (p == 0 && !IsInstalled(rom)) return false;
        int block = Install(rom);
        if (p > PaletteCount(rom)) throw new ArgumentOutOfRangeException(nameof(p));
        int at = sprite >= GameCount ? EntryAt(rom, sprite) + 6 : block + V2.PaletteOf + sprite;
        if (rom.ReadByte(at) == p) return false;
        rom.WriteByte(at, (byte)p);
        return true;
    }

    // adds a palette of 16 colours (BGR555, colour 0 is see-through on a sprite). returns its number
    public static int AddPalette(RomBuffer rom, IReadOnlyList<int> colours)
    {
        int block = Install(rom);
        int count = PaletteCount(rom);
        if (count >= MaxPalettes) throw new InvalidOperationException($"There are already {MaxPalettes} new palettes, the most Lufia Forge keeps.");
        rom.WriteByte(block + 7, (byte)(count + 1));
        int at = PaletteAt(rom, count + 1);
        for (int c = 0; c < 16; c++) rom.WriteUInt16Le(at + c * 2, (ushort)(c < colours.Count ? colours[c] & 0x7FFF : 0));
        return count + 1;
    }

    // the walking script ($070A) a sprite from $80 up gets if its character names none: the game's table $CF:F6B5,
    // or a new sprite's entry. 0 below $80
    public static int WalkOf(RomBuffer rom, int sprite) =>
        sprite < 0x80 ? 0 : sprite < GameCount ? rom.ReadByte(L2Rom.SnesToFile(0xCFF6B5) + sprite - 0x80) : rom.ReadByte(EntryAt(rom, sprite) + 5);

    // how many tiles a sprite with these flags has (frames of 2x2, 2x4, 4x2 or 4x4 tiles)
    public static int TileCount(int flags) =>
        ((flags & 2) != 0 ? 4 : 2) * ((flags & 1) != 0 ? 4 : 2) * ((flags & 8) != 0 ? 2 : (flags & 0x10) != 0 ? 6 : 1);

    // adds a map sprite like from: the same size, frames, colours and walking, with its tiles a copy of that sprite's,
    // or empty. the tiles go into the added space. returns the new number (GameCount + n)
    public static int AddSprite(RomBuffer rom, int from, bool empty)
    {
        if (from < 0 || from >= Count(rom)) throw new ArgumentOutOfRangeException(nameof(from));
        int have = Count(rom);
        if (have >= MaxCount) throw new InvalidOperationException($"There are already {MaxNew} new map sprites, the most Lufia Forge keeps.");
        var source = rom.ReadBytes(EntryAt(rom, from), 5);
        int walk = WalkOf(rom, from), palette = PaletteOf(rom, from);
        int byteCount = TileCount(source[0]) * 32;
        var tiles = empty ? new byte[byteCount] : rom.ReadBytes(L2Rom.SnesToFile((int)(source[2] | source[3] << 8 | source[4] << 16)), byteCount);
        int block = Install(rom);
        int at = L2Rom.Allocate(rom, byteCount, upper: true);
        rom.WriteBytes(at, tiles);
        int snesAddress = L2Rom.FileToSnes(at), id = have;
        int entry = block + V2.Entries + (id - GameCount) * EntrySize;
        rom.WriteBytes(entry, new[] { source[0], source[1], (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16), (byte)walk, (byte)palette, (byte)0 });
        rom.WriteByte(block + 6, (byte)(id - GameCount + 1));
        return id;
    }

    // puts the hooks and the block in (once, a version 1 block gets moved into a new one). returns the block's file
    // offset
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0 && LayoutOf(rom, have) == V2) return have;
        if (have < 0 && !CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int block = L2Rom.Allocate(rom, BlockSize);
        int blockSnes = L2Rom.FileToSnes(block);
        var (code, walkOffset) = Assemble(blockSnes);
        if (CodeAt + code.Length > V2.PaletteOf) throw new InvalidOperationException("The hook code doesn't fit.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + 5, Version);
        for (int r = 0; r < 8; r++) { rom.WriteByte(block + BitsAt + r, (byte)(1 << r)); rom.WriteByte(block + OrderAt + r, RowOrder[r]); }
        rom.WriteBytes(block + CodeAt, code);
        // entries start as copies of sprite 0's (only the ones Count says are in use are new sprites). the last is FE's and FF's
        var zero = rom.ReadBytes(EntryAt(rom, 0), 5);
        for (int i = 0; i <= MaxNew; i++) rom.WriteBytes(block + V2.Entries + i * EntrySize, zero);
        if (have >= 0)
        {
            // from version 1: the new sprites (palette byte into the entry), the palettes, the game sprites' palettes
            int spriteCount = Math.Min((int)rom.ReadByte(have + 6), V1.MaxNew), paletteCount = Math.Min((int)rom.ReadByte(have + 7), MaxPalettes);
            rom.WriteByte(block + 6, (byte)spriteCount);
            rom.WriteByte(block + 7, (byte)paletteCount);
            rom.WriteBytes(block + V2.PaletteOf, rom.ReadBytes(have + V1.PaletteOf, GameCount));
            for (int k = 0; k < spriteCount; k++)
            {
                rom.WriteBytes(block + V2.Entries + k * EntrySize, rom.ReadBytes(have + V1.Entries + k * EntrySize, 6));
                rom.WriteByte(block + V2.Entries + k * EntrySize + 6, rom.ReadByte(have + V1.PaletteOf + GameCount + k));
            }
            rom.WriteBytes(block + V2.Palettes, rom.ReadBytes(have + V1.Palettes, paletteCount * 32));
        }
        int lookupHook = blockSnes + CodeAt;
        rom.WriteBytes(LookupSite, new[] { (byte)0x5C, (byte)lookupHook, (byte)(lookupHook >> 8), (byte)(lookupHook >> 16) });
        int walkHook = blockSnes + CodeAt + walkOffset;
        rom.WriteBytes(WalkSite, new[] { (byte)0x22, (byte)walkHook, (byte)(walkHook >> 8), (byte)(walkHook >> 16) });
        return block;
    }

    // the hook code for a block at snes address block: the lookup (JML from $83:A9F5, at the start) and the walking
    // script (JSL from $83:A956, at the offset returned)
    private static (byte[] Code, int Walk) Assemble(int block)
    {
        int bank = block >> 16 << 16, entries = block + V2.Entries, palettes = block + V2.Palettes, maps = block + V2.Maps;
        int paletteOf = block + V2.PaletteOf, bits = block + BitsAt, order = block + OrderAt;
        var asm = new Asm(block + CodeAt);
        void Long(byte op, int addr) => asm.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        byte[] RowToX = { 0xC2, 0x20, 0x29, 0x07, 0x00, 0xAA, 0xE2, 0x20 };   // REP #$20 / AND #7 / TAX / SEP #$20

        // lookup: m0 x0, A = X = sprite * 2; the PHB of $83:A9E5 is on the stack (PLB / RTL at the end as there)
        asm.B(0xE0, 0xF6, 0x01); asm.Br(0xB0, "new");                 // CPX #$01F6 (FB) / BCS
        Long(0xBF, 0xCFF000);                                      // LDA $CFF000,X
        asm.B(0x85, 0x5D, 0xE2, 0x30, 0xA9, 0xCF); asm.Br(0x80, "have"); // STA $5D / SEP #$30 / LDA #$CF / BRA
        asm.Label("new");
        asm.B(0x8A, 0x4A); asm.Jsr("resolve");                         // TXA / LSR (the sprite) / JSR resolve: X = entry * 8
        asm.B(0x8A, 0x18, 0x69, (byte)entries, (byte)(entries >> 8)); // TXA / CLC / ADC #entries
        asm.B(0x85, 0x5D, 0xE2, 0x30, 0xA9, (byte)(entries >> 16));  // STA $5D / SEP #$30 / LDA #bank
        asm.Label("have");
        asm.B(0x85, 0x5F, 0xA6, 0xA7);                               // STA $5F / LDX $A7 (the actor)
        asm.B(0xA7, 0x5D, 0x29, 0x07); Long(0x9F, 0x7FE216);         // LDA [$5D] / AND #7 / STA $7FE216,X
        asm.B(0xA7, 0x5D, 0x29, 0xF8, 0x9D, 0x91, 0x12);            // LDA [$5D] / AND #$F8 / STA $1291,X
        asm.B(0xA0, 0x01, 0xB7, 0x5D, 0x0A); Long(0x9F, 0x7FE1CE);   // LDY #1 / LDA [$5D],Y / ASL / STA $7FE1CE,X
        asm.Jsr("pal");
        asm.B(0xA0, 0x02, 0xC2, 0x20, 0xA6, 0xAB, 0xB7, 0x5D, 0x9D, 0xB9, 0x12, 0xC8, 0xC8);   // LDY #2 / REP #$20 / LDX $AB / LDA [$5D],Y / STA $12B9,X / INY / INY
        asm.B(0xE2, 0x20, 0xB7, 0x5D, 0x9D, 0xBB, 0x12, 0xAB, 0x6B); // SEP #$20 / LDA [$5D],Y / STA $12BB,X / PLB / RTL

        // pal: m1 x1, X = the actor. A sprite with a new palette: its colours into a sprite row, the row into $7FE1CE,X
        asm.Label("pal");
        asm.B(0x8B, 0xDA, 0xF4, 0x00, 0x00, 0xAB, 0xAB);            // PHB / PHX / PEA $0000 / PLB / PLB (DB = 0)
        asm.B(0xBD, 0xD2, 0x05, 0xC9, (byte)FirstSlot); asm.Br(0xB0, "newpal");   // LDA $05D2,X / CMP #$FB / BCS
        asm.B(0xAA); Long(0xBF, paletteOf); asm.Br(0x80, "test");      // TAX / LDA paletteOf,X / BRA
        asm.Label("newpal");
        asm.Jsr("resolve"); asm.B(0xE2, 0x20); Long(0xBF, entries + 6); asm.B(0xE2, 0x10);   // JSR resolve / SEP #$20 / LDA entries+6,X / SEP #$10
        asm.Label("test");
        asm.Br(0xD0, "custom");
        asm.B(0xFA, 0xAB, 0x60);                                     // PLX / PLB / RTS
        asm.Label("custom");
        asm.B(0xC2, 0x30, 0x29, 0xFF, 0x00, 0x3A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x18);   // REP #$30 / AND #$FF / DEC / ASL x5 / CLC
        asm.B(0x69, (byte)palettes, (byte)(palettes >> 8), 0x48);    // ADC #palettes / PHA     (1-2 palette, 3 actor, 4 DB)
        // 1. a row already showing colours 1-15 (rows 7 to 0)
        asm.B(0xA0, 0xE0, 0x00);                                     // LDY #$00E0
        asm.Label("same");
        asm.B(0xA3, 0x01, 0xAA, 0x5A);                               // LDA 1,S / TAX / PHY   (1-2 row, 3-4 palette)
        asm.Label("same1");
        asm.B(0xE8, 0xE8, 0xC8, 0xC8); Long(0xBF, bank);             // INX / INX / INY / INY / LDA bank:0000,X
        asm.B(0xD9, 0x20, 0x04); asm.Br(0xD0, "differ");               // CMP $0420,Y / BNE
        asm.B(0x98, 0x29, 0x1E, 0x00, 0xC9, 0x1E, 0x00); asm.Br(0xD0, "same1");   // TYA / AND #$1E / CMP #$1E / BNE
        asm.B(0x7A); asm.BrL("use");                                   // PLY / BRL
        asm.Label("differ");
        asm.B(0x7A, 0x98, 0x38, 0xE9, 0x20, 0x00, 0xA8); asm.Br(0x10, "same");   // PLY / TYA / SEC / SBC #$20 / TAY / BPL
        // 2. the rows other actors show
        asm.B(0xF4, 0x00, 0x00, 0xE2, 0x20, 0xA2, 0x27, 0x00);       // PEA $0000 / SEP #$20 / LDX #$27   (1-2 used, 3-4 palette, 5 actor)
        asm.Label("used");
        asm.B(0x8A, 0xC3, 0x05); asm.Br(0xF0, "unext");                // TXA / CMP 5,S / BEQ (itself)
        asm.B(0xBD, 0xD2, 0x05, 0xC9, 0xFF); asm.Br(0xF0, "unext");    // LDA $05D2,X / CMP #$FF / BEQ (no actor)
        Long(0xBF, 0x7FE1CE); asm.B(0x4A, 0xDA); asm.B(RowToX);        // LDA $7FE1CE,X / LSR / PHX / X = row  (1-2 X, 3-4 used)
        Long(0xBF, bits); asm.B(0x03, 0x03, 0x83, 0x03, 0xFA);       // LDA bits,X / ORA 3,S / STA 3,S / PLX
        asm.Label("unext");
        asm.B(0xCA); asm.Br(0x10, "used");                             // DEX / BPL
        // 3. the first free row in the order (the rows people use last, a character made later in a scene is most
        //    likely a person), otherwise its own row
        asm.B(0xA2, 0x00, 0x00);                                     // LDX #0
        asm.Label("pick");
        Long(0xBF, order); asm.B(0xDA); asm.B(RowToX);                 // LDA order,X / PHX / X = row  (1-2 index, 3-4 used)
        Long(0xBF, bits); asm.B(0x23, 0x03); asm.Br(0xF0, "found2");   // LDA bits,X / AND 3,S / BEQ
        asm.B(0xFA, 0xE8, 0xE0, 0x08, 0x00); asm.Br(0xD0, "pick");     // PLX / INX / CPX #8 / BNE
        asm.Jsr("own"); asm.Br(0x80, "found");
        asm.Label("found2");
        asm.B(0x68, 0x68);                                           // PLA / PLA (the index)
        asm.Label("found");                                          // X = row
        asm.B(0xC2, 0x20, 0x8A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0xA8, 0x68);   // REP #$20 / TXA / ASL x5 / TAY / PLA (used)
        // 4. m0 x0, Y = row * 32, 1-2 palette: copy colours 1-15, ask for the upload, the row into the actor
        asm.Label("use");
        asm.B(0xA3, 0x01, 0xAA, 0x5A);                               // LDA 1,S / TAX / PHY
        asm.Label("copy");
        asm.B(0xE8, 0xE8, 0xC8, 0xC8); Long(0xBF, bank); asm.B(0x99, 0x20, 0x04);   // INX x2 / INY x2 / LDA bank:0000,X / STA $0420,Y
        asm.B(0x98, 0x29, 0x1E, 0x00, 0xC9, 0x1E, 0x00); asm.Br(0xD0, "copy");
        asm.B(0xE2, 0x20, 0xA9, 0x80, 0x04, 0x73);                   // SEP #$20 / LDA #$80 / TSB $73 (upload the colours)
        asm.B(0xC2, 0x20, 0x68, 0x4A, 0x4A, 0x4A, 0x4A, 0x7A);       // REP #$20 / PLA / LSR x4 (row * 2) / PLY (the palette)
        asm.B(0xE2, 0x30, 0xFA); Long(0x9F, 0x7FE1CE);               // SEP #$30 / PLX / STA $7FE1CE,X
        asm.B(0xAB, 0x60);                                           // PLB / RTS
        // own: X = the row the actor's entry names (m1 x0; called with 1-2 used, 3-4 palette, 5 actor under the return)
        asm.Label("own");
        asm.B(0xA3, 0x07, 0xC2, 0x20, 0x29, 0xFF, 0x00, 0xAA, 0xE2, 0x20);   // LDA 7,S / REP #$20 / AND #$FF / TAX / SEP #$20
        Long(0xBF, 0x7FE1CE); asm.B(0x4A); asm.B(RowToX); asm.B(0x60);  // LDA $7FE1CE,X / LSR / X = row / RTS

        // walk: JSL from $83:A956 (m1 x0, X = sprite - $80): the game's table, or a new sprite's entry byte 5
        asm.Label("walk");
        asm.B(0xE0, 0x7B, 0x00); asm.Br(0xB0, "walknew");              // CPX #$7B / BCS
        Long(0xBF, 0xCFF6B5); asm.B(0x6B);                           // LDA $CFF6B5,X / RTL
        asm.Label("walknew");
        asm.B(0xDA, 0x8A, 0x18, 0x69, 0x80); asm.Jsr("resolve");      // PHX / TXA / CLC / ADC #$80 (the sprite) / JSR resolve
        asm.B(0xE2, 0x20); Long(0xBF, entries + 5);                  // SEP #$20 / LDA entries+5,X
        asm.B(0xFA, 0xC9, 0x00, 0x6B);                               // PLX / CMP #0 (the flags of the LDA) / RTL

        // resolve: A (low byte) = a sprite from FB up. returns m0 x0, X = its entry * 8: FB-FD the map's table ($05AC,
        // 0 = new sprite 1, 2, 3), FE and FF the spare entry
        asm.Label("resolve");
        asm.B(0xC2, 0x30, 0x29, 0xFF, 0x00, 0x38, 0xE9, FirstSlot, 0x00);   // REP #$30 / AND #$FF / SEC / SBC #$FB
        asm.B(0xC9, SlotsPerMap, 0x00); asm.Br(0x90, "slot");          // CMP #3 / BCC
        asm.B(0xA2, MaxNew * EntrySize & 0xFF, MaxNew * EntrySize >> 8, 0x60);   // LDX #spare / RTS
        asm.Label("slot");
        asm.B(0x48); Long(0xAF, 0x7E05AC);                           // PHA / LDA $7E05AC (the map)
        asm.B(0x29, 0xFF, 0x00, 0x0A, 0x0A, 0x18, 0x63, 0x01, 0xAA); // AND #$FF / ASL / ASL / CLC / ADC 1,S / TAX
        Long(0xBF, maps); asm.B(0x29, 0xFF, 0x00); asm.Br(0xD0, "named");   // LDA maps,X / AND #$FF / BNE
        asm.B(0xA3, 0x01, 0x1A);                                     // LDA 1,S / INC
        asm.Label("named");
        asm.B(0x3A, 0x0A, 0x0A, 0x0A, 0xAA, 0x68, 0x60);             // DEC / ASL x3 / TAX / PLA / RTS
        var code = asm.Done();
        return (code, asm.At("walk"));
    }

    // a tiny 65816 assembler: bytes, labels, 8- and 16-bit branches and JSRs within the block
    private sealed class Asm(int origin)
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int At, string Label, int Kind)> _fixups = new();   // 0 branch, 1 JSR, 2 BRL
        public void B(params byte[] bytes) => _bytes.AddRange(bytes);
        public void Label(string label) => _labels[label] = _bytes.Count;
        public void Br(byte op, string label) { _bytes.Add(op); _fixups.Add((_bytes.Count, label, 0)); _bytes.Add(0); }
        public void BrL(string label) { _bytes.Add(0x82); _fixups.Add((_bytes.Count, label, 2)); _bytes.Add(0); _bytes.Add(0); }
        public void Jsr(string label) { _bytes.Add(0x20); _fixups.Add((_bytes.Count, label, 1)); _bytes.Add(0); _bytes.Add(0); }
        public int At(string label) => _labels[label];
        public byte[] Done()
        {
            foreach (var (at, label, kind) in _fixups)
            {
                int to = _labels[label];
                if (kind == 1) { int address = (origin + to) & 0xFFFF; _bytes[at] = (byte)address; _bytes[at + 1] = (byte)(address >> 8); continue; }
                if (kind == 2) { int longRel = to - (at + 2); _bytes[at] = (byte)longRel; _bytes[at + 1] = (byte)(longRel >> 8); continue; }
                int rel = to - (at + 1);
                if (rel is < -128 or > 127) throw new InvalidOperationException($"Branch to {label} too far.");
                _bytes[at] = (byte)rel;
            }
            return _bytes.ToArray();
        }
    }
}
