namespace LufiaForge.Core.Lufia2;

/// <summary>
/// The chance an item breaks (is used up) when it's used, per item, in battle and in the menu.
/// <para>The game takes one of every item a hero uses: in battle $81:AA2E (the item action) always calls $81:F15B (the
/// item picked when the commands were chosen was only taken from a copy of the bag, $85:96A2 / $85:96B0), and the
/// menu's USE always calls $81:F034 ($81:F04C). Record byte +0 bit 01 is not "used up" (it is on the 23 items used on
/// a hero only). So a weapon or armor given a battle use would be used up like a potion. Read from the game's code
/// (2026-11-12).</para>
/// <para>The patch replaces those two calls: the item's chance (0-100) is rolled, and only a break takes it. An item
/// with no chance of its own (FF) keeps the default: equipment never breaks, other items always do (as the game). When
/// an item that can break 1-99% does in battle, "&lt;item&gt; broke!" is shown after its effect ($81:AA6E).</para>
/// <para>The block: "LF2IB", version, code at +10, a table of 0x200 bytes (one per item id) at +200.</para>
/// </summary>
public static class L2ItemBreak
{
    private const string Magic = "LF2IB";
    private const int Version = 1, CodeAt = 0x10, TableAt = 0x200, BlockSize = TableAt + 0x200;
    public const int Default = 0xFF;
    private const int Flag = 0x7FFDF0, SavedItem = 0x7FFDF2;   // after the status patch's RAM ($7F:FC00-$7F:FDE1)

    private sealed record Site(int At, byte[] Original);
    // $81:AA53 JSL $81F15B (battle use), $81:AA6E JSL $81B057 (the item's effect), $81:F04C JSL $81F15B (the menu's USE)
    private static readonly Site Battle = new(0x0AA53, new byte[] { 0x22, 0x5B, 0xF1, 0x81 });
    private static readonly Site After = new(0x0AA6E, new byte[] { 0x22, 0x57, 0xB0, 0x81 });
    private static readonly Site Menu = new(0x0F04C, new byte[] { 0x22, 0x5B, 0xF1, 0x81 });
    private static readonly Site[] Sites = { Battle, After, Menu };

    /// <summary>File offset of the block, or -1 when the patch isn't in.</summary>
    public static int Block(RomBuffer rom)
    {
        if (!rom.IsLufia2 || rom.Length <= Menu.At + 4 || rom.ReadByte(Battle.At) != 0x22) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Battle.At + 1)) - CodeAt;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    public static bool IsIn(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Why the patch can't go in, or null when it can (or is in).</summary>
    public static string? CantInstall(RomBuffer rom)
    {
        if (!rom.IsLufia2) return "Only Lufia II has this.";
        if (IsIn(rom)) return null;
        if (!Sites.All(s => rom.ReadBytes(s.At, s.Original.Length).SequenceEqual(s.Original)))
            return "This ROM's item use code isn't the US game's (another patch changed it).";
        return null;
    }

    /// <summary>The game's rule (and the patch's default): equipment never breaks, other items always.</summary>
    public static int DefaultChance(RomBuffer rom, int item) => L2Battle.ReadItem(rom, item).EquipType != 0 ? 0 : 100;

    /// <summary>What the game does with <paramref name="item"/> now: its chance in percent. Without the patch, every
    /// item is used up (100).</summary>
    public static int Chance(RomBuffer rom, int item)
    {
        int b = Block(rom);
        if (b < 0) return 100;
        int v = Own(rom, item);
        return v == Default ? DefaultChance(rom, item) : v;
    }

    /// <summary>The item's own chance, or <see cref="Default"/> (none: the default rule).</summary>
    public static int Own(RomBuffer rom, int item)
    {
        int b = Block(rom);
        return b < 0 || item < 0 || item >= 0x200 ? Default : rom.ReadByte(b + TableAt + item);
    }

    /// <summary>Sets the item's chance (0-100, or <see cref="Default"/>); the patch goes in first. False when it was so.</summary>
    public static bool Set(RomBuffer rom, int item, int chance)
    {
        if (item < 0 || item >= 0x200) throw new ArgumentOutOfRangeException(nameof(item));
        if (chance != Default && chance is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(chance));
        bool was = IsIn(rom);
        int b = Install(rom);
        if (rom.ReadByte(b + TableAt + item) == chance) return !was;
        rom.WriteByte(b + TableAt + item, (byte)chance);
        return true;
    }

    /// <summary>Puts the patch in (once); every item keeps the default.</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (CantInstall(rom) is { } no) throw new InvalidOperationException(no);
        int block = L2Rom.Allocate(rom, BlockSize);
        int code = L2Rom.FileToSnes(block + CodeAt), table = L2Rom.FileToSnes(block + TableAt);
        var (bytes, entries) = Assemble(code, table);
        if (CodeAt + bytes.Length > TableAt) throw new InvalidOperationException("The item break code doesn't fit.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + Magic.Length, Version);
        rom.WriteBytes(block + CodeAt, bytes);
        var fill = new byte[0x200];
        Array.Fill(fill, (byte)Default);
        rom.WriteBytes(block + TableAt, fill);
        void Jsl(Site s, int at) { int a = code + at; rom.WriteBytes(s.At, new byte[] { 0x22, (byte)a, (byte)(a >> 8), (byte)(a >> 16) }); }
        Jsl(Battle, entries.Battle);
        Jsl(After, entries.After);
        Jsl(Menu, entries.Menu);
        return block;
    }

    /// <summary>The hooks. Battle (JSL from $81:AA53, A 8-bit, X/Y 16-bit, $0A06 = the item): rolls and takes it on a
    /// break, keeping the item in $7F:FDF2 and a flag in $7F:FDF0 for the message. After (from $81:AA6E): runs the
    /// item's effect ($81:B057), then shows "&lt;item&gt; broke!" when the flag is set. Menu (from $81:F04C): rolls and
    /// takes it on a break, silently.</summary>
    private static (byte[] Bytes, (int Battle, int After, int Menu) Entries) Assemble(int code, int table)
    {
        var a = new L2AiScripts.Asm(code);
        void Long(byte op, int addr) => a.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        void Jsl(int addr) => Long(0x22, addr);

        // ---- Battle
        int battle = a.Here;
        a.B(0x08, 0xE2, 0x20, 0xC2, 0x10);                 // PHP / SEP #$20 / REP #$10
        a.B(0xC2, 0x20); Long(0xAF, 0x000A06); Long(0x8F, SavedItem);   // REP #$20 / LDA $00:0A06 / STA saved
        a.B(0xE2, 0x20, 0xA9, 0x00); Long(0x8F, Flag);     // SEP #$20 / LDA #0 / STA flag
        a.Jsr("roll");
        a.Br(0x90, "b_done");                              // BCC: it holds
        a.B(0x48); Jsl(0x81F15B); a.B(0x68);               // PHA / JSL take / PLA (the chance)
        a.B(0xC9, 100); a.Br(0xB0, "b_done");              // CMP #100 / BCS: always used up, no message
        a.B(0xA9, 0x01); Long(0x8F, Flag);                 // LDA #1 / STA flag
        a.Label("b_done");
        a.B(0x28, 0x6B);                                   // PLP / RTL

        // ---- Menu
        int menu = a.Here;
        a.B(0x08, 0xE2, 0x20, 0xC2, 0x10);                 // PHP / SEP #$20 / REP #$10
        a.Jsr("roll");
        a.Br(0x90, "m_done");
        Jsl(0x81F15B);
        a.Label("m_done");
        a.B(0x28, 0x6B);

        // ---- After
        int after = a.Here;
        Jsl(0x81B057);                                     // the item's effect, as the game
        a.B(0x08, 0x8B, 0xE2, 0x20, 0xC2, 0x10);           // PHP / PHB / SEP #$20 / REP #$10
        Long(0xAF, Flag); a.Br(0xD0, "a_go");              // LDA flag / BNE
        a.B(0x4C); a.Abs("a_done");                        // JMP a_done (too far for a branch)
        a.Label("a_go");
        a.B(0xA9, 0x00); Long(0x8F, Flag);                 // LDA #0 / STA flag
        a.B(0xF4, 0x7E, 0x7E, 0xAB, 0xAB);                 // PEA $7E7E / PLB / PLB
        a.B(0xC2, 0x20); Long(0xAF, SavedItem); a.B(0x8D, 0x06, 0x0A, 0xE2, 0x20);   // REP / LDA saved / STA $0A06 / SEP
        Jsl(0x81F1C5);                                     // its name to $0B77
        a.B(0xA2, 0x0C, 0x00);                             // LDX #$000C
        a.Label("c1");
        a.B(0xBD, 0x77, 0x0B, 0x9D, 0x69, 0x12, 0xCA);     // LDA $0B77,X / STA $1269,X / DEX
        a.Br(0x10, "c1");                                  // BPL
        a.B(0xA0, 0x0D, 0x00);                             // LDY #$000D: the name without its trailing spaces
        a.Label("t1");
        a.B(0xB9, 0x68, 0x12); a.Br(0xF0, "t2");           // LDA $1268,Y / BEQ
        a.B(0xC9, 0x20); a.Br(0xD0, "t3");                 // CMP #' ' / BNE
        a.Label("t2");
        a.B(0x88); a.Br(0xD0, "t1");                       // DEY / BNE
        a.Label("t3");
        a.B(0xA2, 0x00, 0x00);                             // LDX #0
        a.Label("c2");
        a.B(0xBF); a.Abs("text"); a.B((byte)(code >> 16)); // LDA text,X (long)
        a.B(0x99, 0x69, 0x12); a.Br(0xF0, "c3");           // STA $1269,Y / BEQ
        a.B(0xE8, 0xC8); a.Br(0x80, "c2");                 // INX / INY / BRA
        a.Label("c3");
        a.B(0x8C, 0x66, 0x12);                             // STY $1266 (the length)
        // show it for 45 frames, as the status patch's messages ($85:903F's way)
        a.B(0xF4, 0x85, 0x85, 0xAB, 0xAB);                 // PEA $8585 / PLB / PLB
        Jsl(0x859AAA);
        a.B(0xA2, 0xFF, 0xFF, 0x8E, 0x64, 0x12);           // LDX #$FFFF / STX $1264
        Jsl(0x8595FE); Jsl(0x8591A1); Jsl(0x81BAE8);
        a.B(0xC2, 0x20); Jsl(0x859BDA); a.B(0xE2, 0x20);
        a.Jsr("frame");
        a.B(0xA9, 0x2D);                                   // LDA #45
        a.Label("wait");
        a.B(0x48); a.Jsr("frame"); a.B(0x68, 0x3A);        // PHA / frame / PLA / DEC
        a.Br(0xD0, "wait");
        Jsl(0x859671);
        a.Jsr("frame");
        Jsl(0x859ABC);
        a.Label("a_done");
        a.B(0xAB, 0x28, 0x6B);                             // PLB / PLP / RTL

        // ---- frame: one frame of the battle screen (8-bit A)
        a.Label("frame");
        Jsl(0x858A2F); a.B(0xA9, 0xFF); Long(0x8F, 0x0012F3); Jsl(0x85EC81);
        a.B(0x60);                                         // RTS

        // ---- roll (A 8-bit, X 16-bit, $0A06 = the item): C set = it breaks; A = its chance
        a.Label("roll");
        a.B(0xC2, 0x20); Long(0xAF, 0x000A06); a.B(0x29, 0xFF, 0x01, 0xAA, 0xE2, 0x20);   // REP / LDA $00:0A06 (the DB is the caller's) / AND #$01FF / TAX / SEP
        Long(0xBF, table);                                 // LDA table,X
        a.B(0xC9, 0xFF); a.Br(0xD0, "have");               // CMP #$FF / BNE: its own
        Jsl(0x81F291);                                     // $0A09 = its record (bank $96)
        a.B(0xC2, 0x20); Long(0xAF, 0x000A09); a.B(0xAA, 0xE2, 0x20);   // REP / LDA $00:0A09 / TAX / SEP
        Long(0xBF, 0x960007);                              // LDA $96:0007,X (equipment type)
        a.Br(0xF0, "use");                                 // BEQ: not equipment, used up
        a.B(0xA9, 0x00); a.Br(0x80, "have");               // LDA #0 / BRA
        a.Label("use");
        a.B(0xA9, 100);
        a.Label("have");
        a.B(0xC9, 100); a.Br(0xB0, "yes");                 // CMP #100 / BCS: always
        a.B(0xC9, 0x00); a.Br(0xF0, "no");                 // CMP #0 / BEQ: never
        a.B(0x48, 0xA9, 100); Jsl(0x808299);               // PHA / LDA #100 / JSL random (0-99)
        a.B(0xC3, 0x01, 0x68);                             // CMP 1,S / PLA (C clear: the roll is under the chance)
        a.Br(0x90, "yes");
        a.Label("no");
        a.B(0x18, 0x60);                                   // CLC / RTS
        a.Label("yes");
        a.B(0x38, 0x60);                                   // SEC / RTS

        a.Label("text");
        a.B(System.Text.Encoding.ASCII.GetBytes(" broke!"));
        a.B(0x00);
        return (a.Done(), (battle, after, menu));
    }

}
