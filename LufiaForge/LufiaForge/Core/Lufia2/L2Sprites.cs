namespace LufiaForge.Core.Lufia2;

/// <summary>
/// New map sprites and new map sprite palettes (US ROM, from the game's code 2026-10-25; docs/lufia2/graphics.md).
/// <list type="bullet">
/// <item>A map sprite number is one byte ($05D2,X per actor): 0-FA are the game's 251 sprites (table $CF:F000), FE is
/// an event object and FF is no sprite, so FB, FC and FD are the only numbers left for new sprites.</item>
/// <item>$83:A9E5 sets an actor up from its sprite's 5-byte entry (flags, colour row, u24 tiles). The colour row is
/// one of the 8 sprite rows the map loads from $A6:BF00 ($83:B062 copies them to the CGRAM buffer $0420).</item>
/// </list>
/// Two hooks (the game's other bytes stay as they are): $83:A9F5 (LDA $CFF000,X) jumps to code in the added space
/// that reads the entries of FB-FD from Lufia Forge's block, and, for a sprite given a new palette, copies its 15
/// colours into a sprite row of the CGRAM buffer: a row that already shows them, else a row no other actor on the map
/// uses (its own row first, then 5, 4, 3, 2, 0, 7, 6, 1), else its own row. $83:A956 (LDA $CFF6B5,X: the walking
/// script of a sprite from $80 up) reads a new sprite's from its entry.
/// </summary>
public static class L2Sprites
{
    /// <summary>The game's sprites, and the most there can be (FB, FC, FD added).</summary>
    public const int GameCount = 251, MaxCount = 0xFE, MaxNew = MaxCount - GameCount;

    /// <summary>New palettes: 1 to this many (a sprite's palette byte: 0 = its row's colours).</summary>
    public const int MaxPalettes = 127;

    private const int LookupSite = 0x1A9F5, WalkSite = 0x1A956;   // $83:A9F5, $83:A956
    private static readonly byte[] LookupOriginal = { 0xBF, 0x00, 0xF0, 0xCF }, WalkOriginal = { 0xBF, 0xB5, 0xF6, 0xCF };

    // the block: "LF2SP", version, new sprite count, palette count; bit masks; row order; code; tables
    private const string Magic = "LF2SP";
    private const int BitsAt = 0x08, OrderAt = 0x10, CodeAt = 0x20, PaletteOfAt = 0x200, EntriesAt = 0x300, PalettesAt = 0x400;
    private const int EntrySize = 8, BlockSize = PalettesAt + MaxPalettes * 32 + 0x20;
    private static readonly byte[] RowOrder = { 5, 4, 3, 2, 0, 7, 6, 1 };

    /// <summary>File offset of Lufia Forge's block, or -1 (not added yet).</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= LookupSite + 4 || rom.ReadByte(LookupSite) != 0x5C) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(LookupSite + 1)) - CodeAt;
        return block >= L2Rom.OriginalSize && block + BlockSize <= rom.Length &&
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

    /// <summary>Map sprites there are: the game's 251 and the new ones.</summary>
    public static int Count(RomBuffer rom) => Block(rom) is var b && b >= 0 ? GameCount + Math.Min((int)rom.ReadByte(b + 6), MaxNew) : GameCount;

    /// <summary>New palettes there are.</summary>
    public static int PaletteCount(RomBuffer rom) => Block(rom) is var b && b >= 0 ? Math.Min((int)rom.ReadByte(b + 7), MaxPalettes) : 0;

    public static bool IsNew(int sprite) => sprite >= GameCount;

    /// <summary>File offset of a sprite's 5-byte entry (flags, colour row, u24 tiles).</summary>
    public static int EntryAt(RomBuffer rom, int sprite)
    {
        if (sprite < GameCount) return L2Rom.SnesToFile(0xCF0000 | rom.ReadUInt16Le(L2Graphics.MapSpriteTable + sprite * 2));
        int b = Block(rom);
        if (b < 0 || sprite >= Count(rom)) throw new ArgumentOutOfRangeException(nameof(sprite));
        return b + EntriesAt + (sprite - GameCount) * EntrySize;
    }

    /// <summary>The new palette a sprite uses (1 up), or 0: the colours of its row.</summary>
    public static int PaletteOf(RomBuffer rom, int sprite) =>
        Block(rom) is var b && b >= 0 && sprite is >= 0 and < 256 ? rom.ReadByte(b + PaletteOfAt + sprite) is var p && p <= PaletteCount(rom) ? p : 0 : 0;

    /// <summary>File offset of new palette <paramref name="p"/> (1 up): 16 colours, colour 0 unused (see-through).</summary>
    public static int PaletteAt(RomBuffer rom, int p)
    {
        int b = Block(rom);
        if (b < 0 || p < 1 || p > PaletteCount(rom)) throw new ArgumentOutOfRangeException(nameof(p));
        return b + PalettesAt + (p - 1) * 32;
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
        if (p < 0 || p > MaxPalettes || sprite is < 0 or >= MaxCount) throw new ArgumentOutOfRangeException(nameof(p));
        if (p == 0 && !IsInstalled(rom)) return false;
        int b = Install(rom);
        if (p > PaletteCount(rom)) throw new ArgumentOutOfRangeException(nameof(p));
        if (rom.ReadByte(b + PaletteOfAt + sprite) == p) return false;
        rom.WriteByte(b + PaletteOfAt + sprite, (byte)p);
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
    /// Adds a map sprite (FB, then FC, then FD) like <paramref name="from"/>: the same size, frames, colours and walking;
    /// its tiles are a copy of that sprite's, or empty. The tiles go into the added space. Returns the new number.
    /// </summary>
    public static int AddSprite(RomBuffer rom, int from, bool empty)
    {
        if (from < 0 || from >= Count(rom)) throw new ArgumentOutOfRangeException(nameof(from));
        int have = Count(rom);
        if (have >= MaxCount) throw new InvalidOperationException("There are already 3 new map sprites (FB, FC and FD). The game keeps a sprite number in one byte and FE and FF mean an event object and no sprite, so there can't be more.");
        var src = rom.ReadBytes(EntryAt(rom, from), 5);
        int walk = WalkOf(rom, from), pal = PaletteOf(rom, from);
        int bytes = TileCount(src[0]) * 32;
        var tiles = empty ? new byte[bytes] : rom.ReadBytes(L2Rom.SnesToFile((int)(src[2] | src[3] << 8 | src[4] << 16)), bytes);
        int b = Install(rom);
        int at = L2Rom.Allocate(rom, bytes);
        rom.WriteBytes(at, tiles);
        int snes = L2Rom.FileToSnes(at), id = have;
        int e = b + EntriesAt + (id - GameCount) * EntrySize;
        rom.WriteBytes(e, new[] { src[0], src[1], (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16), (byte)walk, (byte)0, (byte)0 });
        rom.WriteByte(b + PaletteOfAt + id, (byte)pal);
        rom.WriteByte(b + 6, (byte)(id - GameCount + 1));
        return id;
    }

    /// <summary>Puts the hooks and the block in (once). Returns the block's file offset.</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int block = L2Rom.Allocate(rom, BlockSize);
        int snes = L2Rom.FileToSnes(block);
        var (code, h2) = Assemble(snes);
        if (CodeAt + code.Length > PaletteOfAt) throw new InvalidOperationException("The hook code doesn't fit.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + 5, 1);   // version
        for (int r = 0; r < 8; r++) { rom.WriteByte(block + BitsAt + r, (byte)(1 << r)); rom.WriteByte(block + OrderAt + r, RowOrder[r]); }
        rom.WriteBytes(block + CodeAt, code);
        // entries FB-FF start as copies of sprite 0's (only those Count says are in use are new sprites)
        var zero = rom.ReadBytes(EntryAt(rom, 0), 5);
        for (int i = 0; i < 0xFF - GameCount + 1; i++) rom.WriteBytes(block + EntriesAt + i * EntrySize, zero);
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
        int bank = block >> 16 << 16, entries = block + EntriesAt, palettes = block + PalettesAt;
        int paletteOf = block + PaletteOfAt, bits = block + BitsAt, order = block + OrderAt;
        var a = new Asm(block + CodeAt);
        void Long(byte op, int addr) => a.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        byte[] RowToX = { 0xC2, 0x20, 0x29, 0x07, 0x00, 0xAA, 0xE2, 0x20 };   // REP #$20 / AND #7 / TAX / SEP #$20

        // lookup: m0 x0, A = X = sprite * 2; the PHB of $83:A9E5 is on the stack (PLB / RTL at the end as there)
        a.B(0xE0, 0xF6, 0x01); a.Br(0xB0, "new");                 // CPX #$01F6 (FB) / BCS
        Long(0xBF, 0xCFF000);                                      // LDA $CFF000,X
        a.B(0x85, 0x5D, 0xE2, 0x30, 0xA9, 0xCF); a.Br(0x80, "have"); // STA $5D / SEP #$30 / LDA #$CF / BRA
        a.Label("new");
        a.B(0x8A, 0x38, 0xE9, 0xF6, 0x01, 0x0A, 0x0A, 0x18);      // TXA / SEC / SBC #$01F6 / ASL / ASL / CLC: (sprite - FB) * 8
        a.B(0x69, (byte)entries, (byte)(entries >> 8));            // ADC #entries
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
        a.B(0xBD, 0xD2, 0x05, 0xAA); Long(0xBF, paletteOf);        // LDA $05D2,X / TAX / LDA paletteOf,X
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
        // 3. its own row when free, else the first free row in the order, else its own row
        a.Jsr("own"); Long(0xBF, bits); a.B(0x23, 0x01); a.Br(0xF0, "found");   // LDA bits,X / AND 1,S / BEQ
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
        a.B(0xC2, 0x20, 0x8A, 0x38, 0xE9, 0x7B, 0x00, 0x0A, 0x0A, 0x0A, 0xAA, 0xE2, 0x20);   // REP / TXA / SEC / SBC #$7B / ASL x3 / TAX / SEP
        Long(0xBF, entries + 5); a.B(0x6B);                        // LDA entries+5,X / RTL
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
