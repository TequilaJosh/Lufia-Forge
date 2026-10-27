namespace LufiaForge.Core.Lufia2;

/// <summary>
/// New map sprites and new map sprite palettes (US ROM, from the game's code 2026-10-25; docs/lufia2/graphics.md).
/// <list type="bullet">
/// <item>A map sprite number is one byte ($05D2,X per actor): 0-FA are the game's 251 sprites (table $CF:F000), FE is
/// an event object and FF is no sprite, so FB, FC and FD are the only numbers left for new sprites. Each map says which
/// new sprite FB, FC and FD show there (a table by map number, $05AC), so up to <see cref="MaxNew"/> new sprites can
/// be made, 3 of them on any one map. A map that names none shows new sprites 1, 2 and 3.</item>
/// <item>$83:A9E5 sets an actor up from its sprite's 5-byte entry (flags, colour row, u24 tiles). The colour row is
/// one of the 8 sprite rows the map loads from $A6:BF00 ($83:B062 copies them to the CGRAM buffer $0420).</item>
/// </list>
/// Two hooks (the game's other bytes stay as they are): $83:A9F5 (LDA $CFF000,X) jumps to code in the added space
/// that reads the entries of FB-FD from Lufia Forge's block, and, for a sprite given a new palette, copies its 15
/// colours into a sprite row of the CGRAM buffer: a row that already shows them, else the first row no other actor on
/// the map uses in the order 5, 4, 3, 2, 0, 7, 6, 1 (field monster rows first, the party's last), else the row its
/// entry names. $83:A956 (LDA $CFF6B5,X: the walking
/// script of a sprite from $80 up) reads a new sprite's from its entry.
/// <para>In Lufia Forge a new sprite has the number <see cref="GameCount"/> + n (n from 0), shown as "New n + 1"; on a
/// map it is written as FB, FC or FD (<see cref="ToGame"/>, <see cref="ToEditor"/>).</para>
/// </summary>
public static class L2Sprites
{
    /// <summary>The game's sprites, the new sprites there can be in all (3 on one map: FB, FC, FD), and the numbers
    /// Lufia Forge gives sprites (new ones from <see cref="GameCount"/>).</summary>
    public const int GameCount = 251, MaxNew = 128, MaxCount = GameCount + MaxNew, FirstSlot = 0xFB, SlotsPerMap = 3;

    /// <summary>New palettes: 1 to this many (a sprite's palette byte: 0 = its row's colours).</summary>
    public const int MaxPalettes = 127;

    private const int LookupSite = 0x1A9F5, WalkSite = 0x1A956;   // $83:A9F5, $83:A956
    private static readonly byte[] LookupOriginal = { 0xBF, 0x00, 0xF0, 0xCF }, WalkOriginal = { 0xBF, 0xB5, 0xF6, 0xCF };

    // the block: "LF2SP", version, new sprite count, palette count; bit masks; row order; code; tables. Version 1
    // (FB-FD only, a palette byte per sprite number) is still read; anything that writes makes it version 2: the
    // palette of a new sprite is byte 6 of its entry, a map table (4 bytes a map: the new sprite + 1 that FB, FC, FD
    // show, 0 = new sprite 1, 2, 3), and one more entry (a copy of sprite 0's) for FE and FF.
    private const string Magic = "LF2SP";
    private const int BitsAt = 0x08, OrderAt = 0x10, CodeAt = 0x20, EntrySize = 8, Version = 2;
    private sealed record Layout(int PaletteOf, int Maps, int Entries, int Palettes, int MaxNew);
    private static readonly Layout V1 = new(0x200, -1, 0x300, 0x400, 3), V2 = new(0x300, 0x400, 0x800, 0xC20, MaxNew);
    private const int BlockSize = 0xC20 + MaxPalettes * 32 + 0x20, V1Size = 0x400 + MaxPalettes * 32 + 0x20;
    private static Layout LayoutOf(RomBuffer rom, int block) => rom.ReadByte(block + 5) >= 2 ? V2 : V1;
    private static readonly byte[] RowOrder = { 5, 4, 3, 2, 0, 7, 6, 1 };

    /// <summary>File offset of Lufia Forge's block, or -1 (not added yet).</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= LookupSite + 4 || rom.ReadByte(LookupSite) != 0x5C) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(LookupSite + 1)) - CodeAt;
        return block >= L2Rom.OriginalSize && block + V1Size <= rom.Length &&
               System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Whether the hooks can go in (both places still hold the game's code).</summary>
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
        if (Same(LookupSite, LookupOriginal) && Same(WalkSite, WalkOriginal)) return true;
        why = "This ROM's map sprite code isn't the US game's where the hooks go (another patch or hack changed it), so new sprites and palettes can't be added.";
        return false;
    }

    /// <summary>Map sprites there are: the game's 251 and the new ones (numbered from <see cref="GameCount"/>).</summary>
    public static int Count(RomBuffer rom) => Block(rom) is var b && b >= 0 ? GameCount + Math.Min((int)rom.ReadByte(b + 6), LayoutOf(rom, b).MaxNew) : GameCount;

    /// <summary>How a sprite is named: its number, or "New n" for an added one.</summary>
    public static string Name(int sprite) => sprite < GameCount ? sprite.ToString("X2") : $"New {sprite - GameCount + 1}";

    /// <summary>New palettes there are.</summary>
    public static int PaletteCount(RomBuffer rom) => Block(rom) is var b && b >= 0 ? Math.Min((int)rom.ReadByte(b + 7), MaxPalettes) : 0;

    public static bool IsNew(int sprite) => sprite >= GameCount;

    /// <summary>File offset of a sprite's 5-byte entry (flags, colour row, u24 tiles).</summary>
    public static int EntryAt(RomBuffer rom, int sprite)
    {
        if (sprite < GameCount) return L2Rom.SnesToFile(0xCF0000 | rom.ReadUInt16Le(L2Graphics.MapSpriteTable + sprite * 2));
        int b = Block(rom);
        if (b < 0 || sprite >= Count(rom)) throw new ArgumentOutOfRangeException(nameof(sprite));
        return b + LayoutOf(rom, b).Entries + (sprite - GameCount) * EntrySize;
    }

    /// <summary>The new palette a sprite uses (1 up), or 0: the colours of its row.</summary>
    public static int PaletteOf(RomBuffer rom, int sprite)
    {
        int b = Block(rom);
        if (b < 0 || sprite < 0 || sprite >= Count(rom)) return 0;
        var l = LayoutOf(rom, b);
        int p = sprite >= GameCount && l == V2 ? rom.ReadByte(EntryAt(rom, sprite) + 6) : rom.ReadByte(b + l.PaletteOf + sprite);
        return p <= PaletteCount(rom) ? p : 0;
    }

    /// <summary>File offset of new palette <paramref name="p"/> (1 up): 16 colours, colour 0 unused (see-through).</summary>
    public static int PaletteAt(RomBuffer rom, int p)
    {
        int b = Block(rom);
        if (b < 0 || p < 1 || p > PaletteCount(rom)) throw new ArgumentOutOfRangeException(nameof(p));
        return b + LayoutOf(rom, b).Palettes + (p - 1) * 32;
    }

    // ── which new sprites a map shows ───────────────────────────────────────

    /// <summary>The new sprites (0 up) FB, FC and FD show on <paramref name="map"/>.</summary>
    public static int[] Slots(RomBuffer rom, int map)
    {
        var r = new[] { 0, 1, 2 };
        int b = Block(rom);
        if (b < 0 || LayoutOf(rom, b) != V2 || map is < 0 or > 255) return r;
        for (int s = 0; s < SlotsPerMap; s++) if (rom.ReadByte(b + V2.Maps + map * 4 + s) is var v && v > 0) r[s] = v - 1;
        return r;
    }

    /// <summary>The sprite (Lufia Forge's number) a sprite byte of a script on <paramref name="map"/> shows: FB, FC
    /// and FD are that map's new sprites.</summary>
    public static int ToEditor(RomBuffer rom, int map, int spriteByte) =>
        spriteByte is >= FirstSlot and < FirstSlot + SlotsPerMap && Block(rom) >= 0 ? GameCount + Slots(rom, map)[spriteByte - FirstSlot] : spriteByte;

    /// <summary>The byte a script on <paramref name="map"/> makes <paramref name="sprite"/> with: its own number, or for
    /// a new sprite FB, FC or FD (one the map already shows it with, else one of those that
    /// <paramref name="bytesInUse"/> doesn't hold, which then shows it on this map).</summary>
    public static int ToGame(RomBuffer rom, int map, int sprite, ICollection<int> bytesInUse)
    {
        if (sprite < GameCount) return sprite;
        int k = sprite - GameCount;
        var slots = Slots(rom, map);
        for (int s = 0; s < SlotsPerMap; s++) if (slots[s] == k) return FirstSlot + s;
        for (int s = 0; s < SlotsPerMap; s++)
            if (!bytesInUse.Contains(FirstSlot + s))
            {
                int b = Install(rom);
                rom.WriteByte(b + V2.Maps + map * 4 + s, (byte)(k + 1));
                return FirstSlot + s;
            }
        throw new InvalidOperationException($"Map {map:X2} already shows 3 new sprites (FB, FC and FD: {string.Join(", ", slots.Select(x => Name(GameCount + x)))}), the most one map can.");
    }

    /// <summary>The colour row the sprite's entry names (0-7).</summary>
    public static int RowOf(RomBuffer rom, int sprite) => rom.ReadByte(EntryAt(rom, sprite) + 1) & 7;

    /// <summary>Other sprites that share this one's entry (a change of row changes them too).</summary>
    public static List<int> SharingEntry(RomBuffer rom, int sprite)
    {
        int e = EntryAt(rom, sprite);
        return Enumerable.Range(0, Count(rom)).Where(s => s != sprite && EntryAt(rom, s) == e).ToList();
    }

    /// <summary>Other sprites whose tiles start at the same place (painting one paints them too).</summary>
    public static List<int> SharingTiles(RomBuffer rom, int sprite)
    {
        int t = (int)rom.ReadUInt24Le(EntryAt(rom, sprite) + 2);
        return Enumerable.Range(0, Count(rom)).Where(s => s != sprite && (int)rom.ReadUInt24Le(EntryAt(rom, s) + 2) == t).ToList();
    }

    /// <summary>Sets the colour row a sprite names. True when it changed.</summary>
    public static bool SetRow(RomBuffer rom, int sprite, int row)
    {
        int e = EntryAt(rom, sprite);
        byte old = rom.ReadByte(e + 1), v = (byte)(old & ~7 | row & 7);
        if (v == old) return false;
        rom.WriteByte(e + 1, v);
        return true;
    }

    /// <summary>Gives a sprite new palette <paramref name="p"/> (0: back to its row's colours). True when it changed.</summary>
    public static bool SetPalette(RomBuffer rom, int sprite, int p)
    {
        if (p < 0 || p > MaxPalettes || sprite < 0 || sprite >= Math.Max(Count(rom), GameCount)) throw new ArgumentOutOfRangeException(nameof(p));
        if (p == 0 && !IsInstalled(rom)) return false;
        int b = Install(rom);
        if (p > PaletteCount(rom)) throw new ArgumentOutOfRangeException(nameof(p));
        int at = sprite >= GameCount ? EntryAt(rom, sprite) + 6 : b + V2.PaletteOf + sprite;
        if (rom.ReadByte(at) == p) return false;
        rom.WriteByte(at, (byte)p);
        return true;
    }

    /// <summary>Adds a palette of 16 colours (BGR555; colour 0 is see-through on a sprite). Returns its number.</summary>
    public static int AddPalette(RomBuffer rom, IReadOnlyList<int> colours)
    {
        int b = Install(rom);
        int n = PaletteCount(rom);
        if (n >= MaxPalettes) throw new InvalidOperationException($"There are already {MaxPalettes} new palettes, the most Lufia Forge keeps.");
        rom.WriteByte(b + 7, (byte)(n + 1));
        int at = PaletteAt(rom, n + 1);
        for (int c = 0; c < 16; c++) rom.WriteUInt16Le(at + c * 2, (ushort)(c < colours.Count ? colours[c] & 0x7FFF : 0));
        return n + 1;
    }

    /// <summary>The walking script ($070A) a sprite from $80 up gets when its character names none: the game's table
    /// $CF:F6B5, or a new sprite's entry. 0 below $80.</summary>
    public static int WalkOf(RomBuffer rom, int sprite) =>
        sprite < 0x80 ? 0 : sprite < GameCount ? rom.ReadByte(L2Rom.SnesToFile(0xCFF6B5) + sprite - 0x80) : rom.ReadByte(EntryAt(rom, sprite) + 5);

    /// <summary>The number of tiles of a sprite with these flags (frames of 2x2, 2x4, 4x2 or 4x4 tiles).</summary>
    public static int TileCount(int flags) =>
        ((flags & 2) != 0 ? 4 : 2) * ((flags & 1) != 0 ? 4 : 2) * ((flags & 8) != 0 ? 2 : (flags & 0x10) != 0 ? 6 : 1);

    /// <summary>
    /// Adds a map sprite like <paramref name="from"/>: the same size, frames, colours and walking; its tiles are a copy
    /// of that sprite's, or empty. The tiles go into the added space. Returns the new number (<see cref="GameCount"/> + n).
    /// </summary>
    public static int AddSprite(RomBuffer rom, int from, bool empty)
    {
        if (from < 0 || from >= Count(rom)) throw new ArgumentOutOfRangeException(nameof(from));
        int have = Count(rom);
        if (have >= MaxCount) throw new InvalidOperationException($"There are already {MaxNew} new map sprites, the most Lufia Forge keeps.");
        var src = rom.ReadBytes(EntryAt(rom, from), 5);
        int walk = WalkOf(rom, from), pal = PaletteOf(rom, from);
        int bytes = TileCount(src[0]) * 32;
        var tiles = empty ? new byte[bytes] : rom.ReadBytes(L2Rom.SnesToFile((int)(src[2] | src[3] << 8 | src[4] << 16)), bytes);
        int b = Install(rom);
        int at = L2Rom.Allocate(rom, bytes, upper: true);
        rom.WriteBytes(at, tiles);
        int snes = L2Rom.FileToSnes(at), id = have;
        int e = b + V2.Entries + (id - GameCount) * EntrySize;
        rom.WriteBytes(e, new[] { src[0], src[1], (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16), (byte)walk, (byte)pal, (byte)0 });
        rom.WriteByte(b + 6, (byte)(id - GameCount + 1));
        return id;
    }

    /// <summary>Puts the hooks and the block in (once; a version 1 block is moved into a new one). Returns the block's
    /// file offset.</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0 && LayoutOf(rom, have) == V2) return have;
        if (have < 0 && !CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int block = L2Rom.Allocate(rom, BlockSize);
        int snes = L2Rom.FileToSnes(block);
        var (code, h2) = Assemble(snes);
        if (CodeAt + code.Length > V2.PaletteOf) throw new InvalidOperationException("The hook code doesn't fit.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + 5, Version);
        for (int r = 0; r < 8; r++) { rom.WriteByte(block + BitsAt + r, (byte)(1 << r)); rom.WriteByte(block + OrderAt + r, RowOrder[r]); }
        rom.WriteBytes(block + CodeAt, code);
        // entries start as copies of sprite 0's (only those Count says are in use are new sprites); the last is FE's and FF's
        var zero = rom.ReadBytes(EntryAt(rom, 0), 5);
        for (int i = 0; i <= MaxNew; i++) rom.WriteBytes(block + V2.Entries + i * EntrySize, zero);
        if (have >= 0)
        {
            // from version 1: the new sprites (palette byte into the entry), the palettes, the game sprites' palettes
            int n = Math.Min((int)rom.ReadByte(have + 6), V1.MaxNew), pals = Math.Min((int)rom.ReadByte(have + 7), MaxPalettes);
            rom.WriteByte(block + 6, (byte)n);
            rom.WriteByte(block + 7, (byte)pals);
            rom.WriteBytes(block + V2.PaletteOf, rom.ReadBytes(have + V1.PaletteOf, GameCount));
            for (int k = 0; k < n; k++)
            {
                rom.WriteBytes(block + V2.Entries + k * EntrySize, rom.ReadBytes(have + V1.Entries + k * EntrySize, 6));
                rom.WriteByte(block + V2.Entries + k * EntrySize + 6, rom.ReadByte(have + V1.PaletteOf + GameCount + k));
            }
            rom.WriteBytes(block + V2.Palettes, rom.ReadBytes(have + V1.Palettes, pals * 32));
        }
        int h1 = snes + CodeAt;
        rom.WriteBytes(LookupSite, new[] { (byte)0x5C, (byte)h1, (byte)(h1 >> 8), (byte)(h1 >> 16) });
        int w = snes + CodeAt + h2;
        rom.WriteBytes(WalkSite, new[] { (byte)0x22, (byte)w, (byte)(w >> 8), (byte)(w >> 16) });
        return block;
    }

    /// <summary>The hook code for a block at SNES <paramref name="block"/>: the lookup (JML from $83:A9F5, at the
    /// start) and the walking script (JSL from $83:A956, at the offset returned).</summary>
    private static (byte[] Code, int Walk) Assemble(int block)
    {
        int bank = block >> 16 << 16, entries = block + V2.Entries, palettes = block + V2.Palettes, maps = block + V2.Maps;
        int paletteOf = block + V2.PaletteOf, bits = block + BitsAt, order = block + OrderAt;
        var a = new Asm(block + CodeAt);
        void Long(byte op, int addr) => a.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        byte[] RowToX = { 0xC2, 0x20, 0x29, 0x07, 0x00, 0xAA, 0xE2, 0x20 };   // REP #$20 / AND #7 / TAX / SEP #$20

        // lookup: m0 x0, A = X = sprite * 2; the PHB of $83:A9E5 is on the stack (PLB / RTL at the end as there)
        a.B(0xE0, 0xF6, 0x01); a.Br(0xB0, "new");                 // CPX #$01F6 (FB) / BCS
        Long(0xBF, 0xCFF000);                                      // LDA $CFF000,X
        a.B(0x85, 0x5D, 0xE2, 0x30, 0xA9, 0xCF); a.Br(0x80, "have"); // STA $5D / SEP #$30 / LDA #$CF / BRA
        a.Label("new");
        a.B(0x8A, 0x4A); a.Jsr("resolve");                         // TXA / LSR (the sprite) / JSR resolve: X = entry * 8
        a.B(0x8A, 0x18, 0x69, (byte)entries, (byte)(entries >> 8)); // TXA / CLC / ADC #entries
        a.B(0x85, 0x5D, 0xE2, 0x30, 0xA9, (byte)(entries >> 16));  // STA $5D / SEP #$30 / LDA #bank
        a.Label("have");
        a.B(0x85, 0x5F, 0xA6, 0xA7);                               // STA $5F / LDX $A7 (the actor)
        a.B(0xA7, 0x5D, 0x29, 0x07); Long(0x9F, 0x7FE216);         // LDA [$5D] / AND #7 / STA $7FE216,X
        a.B(0xA7, 0x5D, 0x29, 0xF8, 0x9D, 0x91, 0x12);            // LDA [$5D] / AND #$F8 / STA $1291,X
        a.B(0xA0, 0x01, 0xB7, 0x5D, 0x0A); Long(0x9F, 0x7FE1CE);   // LDY #1 / LDA [$5D],Y / ASL / STA $7FE1CE,X
        a.Jsr("pal");
        a.B(0xA0, 0x02, 0xC2, 0x20, 0xA6, 0xAB, 0xB7, 0x5D, 0x9D, 0xB9, 0x12, 0xC8, 0xC8);   // LDY #2 / REP #$20 / LDX $AB / LDA [$5D],Y / STA $12B9,X / INY / INY
        a.B(0xE2, 0x20, 0xB7, 0x5D, 0x9D, 0xBB, 0x12, 0xAB, 0x6B); // SEP #$20 / LDA [$5D],Y / STA $12BB,X / PLB / RTL

        // pal: m1 x1, X = the actor. A sprite with a new palette: its colours into a sprite row, the row into $7FE1CE,X
        a.Label("pal");
        a.B(0x8B, 0xDA, 0xF4, 0x00, 0x00, 0xAB, 0xAB);            // PHB / PHX / PEA $0000 / PLB / PLB (DB = 0)
        a.B(0xBD, 0xD2, 0x05, 0xC9, (byte)FirstSlot); a.Br(0xB0, "newpal");   // LDA $05D2,X / CMP #$FB / BCS
        a.B(0xAA); Long(0xBF, paletteOf); a.Br(0x80, "test");      // TAX / LDA paletteOf,X / BRA
        a.Label("newpal");
        a.Jsr("resolve"); a.B(0xE2, 0x20); Long(0xBF, entries + 6); a.B(0xE2, 0x10);   // JSR resolve / SEP #$20 / LDA entries+6,X / SEP #$10
        a.Label("test");
        a.Br(0xD0, "custom");
        a.B(0xFA, 0xAB, 0x60);                                     // PLX / PLB / RTS
        a.Label("custom");
        a.B(0xC2, 0x30, 0x29, 0xFF, 0x00, 0x3A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x18);   // REP #$30 / AND #$FF / DEC / ASL x5 / CLC
        a.B(0x69, (byte)palettes, (byte)(palettes >> 8), 0x48);    // ADC #palettes / PHA     (1-2 palette, 3 actor, 4 DB)
        // 1. a row already showing colours 1-15 (rows 7 to 0)
        a.B(0xA0, 0xE0, 0x00);                                     // LDY #$00E0
        a.Label("same");
        a.B(0xA3, 0x01, 0xAA, 0x5A);                               // LDA 1,S / TAX / PHY   (1-2 row, 3-4 palette)
        a.Label("same1");
        a.B(0xE8, 0xE8, 0xC8, 0xC8); Long(0xBF, bank);             // INX / INX / INY / INY / LDA bank:0000,X
        a.B(0xD9, 0x20, 0x04); a.Br(0xD0, "differ");               // CMP $0420,Y / BNE
        a.B(0x98, 0x29, 0x1E, 0x00, 0xC9, 0x1E, 0x00); a.Br(0xD0, "same1");   // TYA / AND #$1E / CMP #$1E / BNE
        a.B(0x7A); a.BrL("use");                                   // PLY / BRL
        a.Label("differ");
        a.B(0x7A, 0x98, 0x38, 0xE9, 0x20, 0x00, 0xA8); a.Br(0x10, "same");   // PLY / TYA / SEC / SBC #$20 / TAY / BPL
        // 2. the rows other actors show
        a.B(0xF4, 0x00, 0x00, 0xE2, 0x20, 0xA2, 0x27, 0x00);       // PEA $0000 / SEP #$20 / LDX #$27   (1-2 used, 3-4 palette, 5 actor)
        a.Label("used");
        a.B(0x8A, 0xC3, 0x05); a.Br(0xF0, "unext");                // TXA / CMP 5,S / BEQ (itself)
        a.B(0xBD, 0xD2, 0x05, 0xC9, 0xFF); a.Br(0xF0, "unext");    // LDA $05D2,X / CMP #$FF / BEQ (no actor)
        Long(0xBF, 0x7FE1CE); a.B(0x4A, 0xDA); a.B(RowToX);        // LDA $7FE1CE,X / LSR / PHX / X = row  (1-2 X, 3-4 used)
        Long(0xBF, bits); a.B(0x03, 0x03, 0x83, 0x03, 0xFA);       // LDA bits,X / ORA 3,S / STA 3,S / PLX
        a.Label("unext");
        a.B(0xCA); a.Br(0x10, "used");                             // DEX / BPL
        // 3. the first free row in the order (the rows people use last: a character made later in a scene is most
        //    likely a person), else its own row
        a.B(0xA2, 0x00, 0x00);                                     // LDX #0
        a.Label("pick");
        Long(0xBF, order); a.B(0xDA); a.B(RowToX);                 // LDA order,X / PHX / X = row  (1-2 index, 3-4 used)
        Long(0xBF, bits); a.B(0x23, 0x03); a.Br(0xF0, "found2");   // LDA bits,X / AND 3,S / BEQ
        a.B(0xFA, 0xE8, 0xE0, 0x08, 0x00); a.Br(0xD0, "pick");     // PLX / INX / CPX #8 / BNE
        a.Jsr("own"); a.Br(0x80, "found");
        a.Label("found2");
        a.B(0x68, 0x68);                                           // PLA / PLA (the index)
        a.Label("found");                                          // X = row
        a.B(0xC2, 0x20, 0x8A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0xA8, 0x68);   // REP #$20 / TXA / ASL x5 / TAY / PLA (used)
        // 4. m0 x0, Y = row * 32, 1-2 palette: copy colours 1-15, ask for the upload, the row into the actor
        a.Label("use");
        a.B(0xA3, 0x01, 0xAA, 0x5A);                               // LDA 1,S / TAX / PHY
        a.Label("copy");
        a.B(0xE8, 0xE8, 0xC8, 0xC8); Long(0xBF, bank); a.B(0x99, 0x20, 0x04);   // INX x2 / INY x2 / LDA bank:0000,X / STA $0420,Y
        a.B(0x98, 0x29, 0x1E, 0x00, 0xC9, 0x1E, 0x00); a.Br(0xD0, "copy");
        a.B(0xE2, 0x20, 0xA9, 0x80, 0x04, 0x73);                   // SEP #$20 / LDA #$80 / TSB $73 (upload the colours)
        a.B(0xC2, 0x20, 0x68, 0x4A, 0x4A, 0x4A, 0x4A, 0x7A);       // REP #$20 / PLA / LSR x4 (row * 2) / PLY (the palette)
        a.B(0xE2, 0x30, 0xFA); Long(0x9F, 0x7FE1CE);               // SEP #$30 / PLX / STA $7FE1CE,X
        a.B(0xAB, 0x60);                                           // PLB / RTS
        // own: X = the row the actor's entry names (m1 x0; called with 1-2 used, 3-4 palette, 5 actor under the return)
        a.Label("own");
        a.B(0xA3, 0x07, 0xC2, 0x20, 0x29, 0xFF, 0x00, 0xAA, 0xE2, 0x20);   // LDA 7,S / REP #$20 / AND #$FF / TAX / SEP #$20
        Long(0xBF, 0x7FE1CE); a.B(0x4A); a.B(RowToX); a.B(0x60);  // LDA $7FE1CE,X / LSR / X = row / RTS

        // walk: JSL from $83:A956 (m1 x0, X = sprite - $80): the game's table, or a new sprite's entry byte 5
        a.Label("walk");
        a.B(0xE0, 0x7B, 0x00); a.Br(0xB0, "walknew");              // CPX #$7B / BCS
        Long(0xBF, 0xCFF6B5); a.B(0x6B);                           // LDA $CFF6B5,X / RTL
        a.Label("walknew");
        a.B(0xDA, 0x8A, 0x18, 0x69, 0x80); a.Jsr("resolve");      // PHX / TXA / CLC / ADC #$80 (the sprite) / JSR resolve
        a.B(0xE2, 0x20); Long(0xBF, entries + 5);                  // SEP #$20 / LDA entries+5,X
        a.B(0xFA, 0xC9, 0x00, 0x6B);                               // PLX / CMP #0 (the flags of the LDA) / RTL

        // resolve: A (low byte) = a sprite from FB up; returns m0 x0, X = its entry * 8: FB-FD the map's table ($05AC;
        // 0 = new sprite 1, 2, 3), FE and FF the spare entry
        a.Label("resolve");
        a.B(0xC2, 0x30, 0x29, 0xFF, 0x00, 0x38, 0xE9, FirstSlot, 0x00);   // REP #$30 / AND #$FF / SEC / SBC #$FB
        a.B(0xC9, SlotsPerMap, 0x00); a.Br(0x90, "slot");          // CMP #3 / BCC
        a.B(0xA2, MaxNew * EntrySize & 0xFF, MaxNew * EntrySize >> 8, 0x60);   // LDX #spare / RTS
        a.Label("slot");
        a.B(0x48); Long(0xAF, 0x7E05AC);                           // PHA / LDA $7E05AC (the map)
        a.B(0x29, 0xFF, 0x00, 0x0A, 0x0A, 0x18, 0x63, 0x01, 0xAA); // AND #$FF / ASL / ASL / CLC / ADC 1,S / TAX
        Long(0xBF, maps); a.B(0x29, 0xFF, 0x00); a.Br(0xD0, "named");   // LDA maps,X / AND #$FF / BNE
        a.B(0xA3, 0x01, 0x1A);                                     // LDA 1,S / INC
        a.Label("named");
        a.B(0x3A, 0x0A, 0x0A, 0x0A, 0xAA, 0x68, 0x60);             // DEC / ASL x3 / TAX / PLA / RTS
        var code = a.Done();
        return (code, a.At("walk"));
    }

    /// <summary>A tiny 65816 assembler: bytes, labels, 8- and 16-bit branches and JSRs within the block.</summary>
    private sealed class Asm(int origin)
    {
        private readonly List<byte> _b = new();
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int At, string Label, int Kind)> _fix = new();   // 0 branch, 1 JSR, 2 BRL
        public void B(params byte[] b) => _b.AddRange(b);
        public void Label(string l) => _labels[l] = _b.Count;
        public void Br(byte op, string l) { _b.Add(op); _fix.Add((_b.Count, l, 0)); _b.Add(0); }
        public void BrL(string l) { _b.Add(0x82); _fix.Add((_b.Count, l, 2)); _b.Add(0); _b.Add(0); }
        public void Jsr(string l) { _b.Add(0x20); _fix.Add((_b.Count, l, 1)); _b.Add(0); _b.Add(0); }
        public int At(string l) => _labels[l];
        public byte[] Done()
        {
            foreach (var (at, l, kind) in _fix)
            {
                int to = _labels[l];
                if (kind == 1) { int addr = (origin + to) & 0xFFFF; _b[at] = (byte)addr; _b[at + 1] = (byte)(addr >> 8); continue; }
                if (kind == 2) { int r = to - (at + 2); _b[at] = (byte)r; _b[at + 1] = (byte)(r >> 8); continue; }
                int rel = to - (at + 1);
                if (rel is < -128 or > 127) throw new InvalidOperationException($"Branch to {l} too far.");
                _b[at] = (byte)rel;
            }
            return _b.ToArray();
        }
    }
}
