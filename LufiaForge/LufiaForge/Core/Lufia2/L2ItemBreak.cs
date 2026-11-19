namespace LufiaForge.Core.Lufia2;

// the chance an item breaks (gets used up) when it's used, per item, in battle and in the menu
// the game takes one of every item a hero uses: in battle $81:AA2E (the item action) always calls $81:F15B (the item
// picked when the commands were chosen was only taken from a copy of the bag, $85:96A2 / $85:96B0), and the menu's
// USE always calls $81:F034 ($81:F04C). record byte +0 bit 01 isn't "used up" (it's on the 23 items only used on a
// hero). so a weapon or armor given a battle use would get used up like a potion. read from the game's code
// (2026-11-12)
// the patch replaces those two calls: we roll the item's chance (0-100), and only a break takes it. an item with no
// chance of its own (FF) keeps the default: equipment never breaks, other items always do (like the game). when an
// item that can break 1-99% does in battle, "<item> broke!" shows after its effect ($81:AA6E)
// the block: "LF2IB", version, code at +10, a table of 0x200 bytes (one per item id) at +200
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

    // file offset of the block, -1 if the patch isn't in
    public static int Block(RomBuffer rom)
    {
        if (!rom.IsLufia2 || rom.Length <= Menu.At + 4 || rom.ReadByte(Battle.At) != 0x22) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Battle.At + 1)) - CodeAt;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    public static bool IsIn(RomBuffer rom) => Block(rom) >= 0;

    // why the patch can't go in, or null if it can (or is in)
    public static string? CantInstall(RomBuffer rom)
    {
        if (!rom.IsLufia2) return "Only Lufia II has this.";
        if (IsIn(rom)) return null;
        if (!Sites.All(s => rom.ReadBytes(s.At, s.Original.Length).SequenceEqual(s.Original)))
            return "This ROM's item use code isn't the US game's (another patch changed it).";
        return null;
    }

    // the game's rule (and the patch's default): equipment never breaks, other items always do
    public static int DefaultChance(RomBuffer rom, int item) => L2Battle.ReadItem(rom, item).EquipType != 0 ? 0 : 100;

    // what the game does with item now: its chance in percent. without the patch every item gets used up (100)
    public static int Chance(RomBuffer rom, int item)
    {
        int block = Block(rom);
        if (block < 0) return 100;
        int own = Own(rom, item);
        return own == Default ? DefaultChance(rom, item) : own;
    }

    // the item's own chance, or Default (none = the default rule)
    public static int Own(RomBuffer rom, int item)
    {
        int block = Block(rom);
        return block < 0 || item < 0 || item >= 0x200 ? Default : rom.ReadByte(block + TableAt + item);
    }

    // sets the item's chance (0-100, or Default), the patch goes in first. false if it already was that
    public static bool Set(RomBuffer rom, int item, int chance)
    {
        if (item < 0 || item >= 0x200) throw new ArgumentOutOfRangeException(nameof(item));
        if (chance != Default && chance is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(chance));
        bool was = IsIn(rom);
        int block = Install(rom);
        if (rom.ReadByte(block + TableAt + item) == chance) return !was;
        rom.WriteByte(block + TableAt + item, (byte)chance);
        return true;
    }

    // puts the patch in (once), every item keeps the default
    public static int Install(RomBuffer rom)
    {
        int existing = Block(rom);
        if (existing >= 0) return existing;
        if (CantInstall(rom) is { } whyNot) throw new InvalidOperationException(whyNot);
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
        void Jsl(Site site, int offset) { int addr = code + offset; rom.WriteBytes(site.At, new byte[] { 0x22, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16) }); }
        Jsl(Battle, entries.Battle);
        Jsl(After, entries.After);
        Jsl(Menu, entries.Menu);
        return block;
    }

    // the hooks:
    // - Battle (JSL from $81:AA53, A 8-bit, X/Y 16-bit, $0A06 = the item): rolls and takes it on a break, keeping the
    //   item in $7F:FDF2 and a flag in $7F:FDF0 for the message
    // - After (from $81:AA6E): runs the item's effect ($81:B057), then shows "<item> broke!" if the flag is set
    // - Menu (from $81:F04C): rolls and takes it on a break, silently
    private static (byte[] Bytes, (int Battle, int After, int Menu) Entries) Assemble(int code, int table)
    {
        var asm = new L2AiScripts.Asm(code);
        void Long(byte op, int addr) => asm.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        void Jsl(int addr) => Long(0x22, addr);

        // Battle
        int battle = asm.Here;
        asm.B(0x08, 0xE2, 0x20, 0xC2, 0x10);                 // PHP / SEP #$20 / REP #$10
        asm.B(0xC2, 0x20); Long(0xAF, 0x000A06); Long(0x8F, SavedItem);   // REP #$20 / LDA $00:0A06 / STA saved
        asm.B(0xE2, 0x20, 0xA9, 0x00); Long(0x8F, Flag);     // SEP #$20 / LDA #0 / STA flag
        asm.Jsr("roll");
        asm.Br(0x90, "b_done");                              // BCC: it holds
        asm.B(0x48); Jsl(0x81F15B); asm.B(0x68);               // PHA / JSL take / PLA (the chance)
        asm.B(0xC9, 100); asm.Br(0xB0, "b_done");              // CMP #100 / BCS: always used up, no message
        asm.B(0xA9, 0x01); Long(0x8F, Flag);                 // LDA #1 / STA flag
        asm.Label("b_done");
        asm.B(0x28, 0x6B);                                   // PLP / RTL

        // Menu
        int menu = asm.Here;
        asm.B(0x08, 0xE2, 0x20, 0xC2, 0x10);                 // PHP / SEP #$20 / REP #$10
        asm.Jsr("roll");
        asm.Br(0x90, "m_done");
        Jsl(0x81F15B);
        asm.Label("m_done");
        asm.B(0x28, 0x6B);

        // After
        int after = asm.Here;
        Jsl(0x81B057);                                     // the item's effect, as the game
        asm.B(0x08, 0x8B, 0xE2, 0x20, 0xC2, 0x10);           // PHP / PHB / SEP #$20 / REP #$10
        Long(0xAF, Flag); asm.Br(0xD0, "a_go");              // LDA flag / BNE
        asm.B(0x4C); asm.Abs("a_done");                        // JMP a_done (too far for a branch)
        asm.Label("a_go");
        asm.B(0xA9, 0x00); Long(0x8F, Flag);                 // LDA #0 / STA flag
        asm.B(0xF4, 0x7E, 0x7E, 0xAB, 0xAB);                 // PEA $7E7E / PLB / PLB
        asm.B(0xC2, 0x20); Long(0xAF, SavedItem); asm.B(0x8D, 0x06, 0x0A, 0xE2, 0x20);   // REP / LDA saved / STA $0A06 / SEP
        Jsl(0x81F1C5);                                     // its name to $0B77
        asm.B(0xA2, 0x0C, 0x00);                             // LDX #$000C
        asm.Label("c1");
        asm.B(0xBD, 0x77, 0x0B, 0x9D, 0x69, 0x12, 0xCA);     // LDA $0B77,X / STA $1269,X / DEX
        asm.Br(0x10, "c1");                                  // BPL
        asm.B(0xA0, 0x0D, 0x00);                             // LDY #$000D: the name without its trailing spaces
        asm.Label("t1");
        asm.B(0xB9, 0x68, 0x12); asm.Br(0xF0, "t2");           // LDA $1268,Y / BEQ
        asm.B(0xC9, 0x20); asm.Br(0xD0, "t3");                 // CMP #' ' / BNE
        asm.Label("t2");
        asm.B(0x88); asm.Br(0xD0, "t1");                       // DEY / BNE
        asm.Label("t3");
        asm.B(0xA2, 0x00, 0x00);                             // LDX #0
        asm.Label("c2");
        asm.B(0xBF); asm.Abs("text"); asm.B((byte)(code >> 16)); // LDA text,X (long)
        asm.B(0x99, 0x69, 0x12); asm.Br(0xF0, "c3");           // STA $1269,Y / BEQ
        asm.B(0xE8, 0xC8); asm.Br(0x80, "c2");                 // INX / INY / BRA
        asm.Label("c3");
        asm.B(0x8C, 0x66, 0x12);                             // STY $1266 (the length)
        // show it for 45 frames, as the status patch's messages ($85:903F's way)
        asm.B(0xF4, 0x85, 0x85, 0xAB, 0xAB);                 // PEA $8585 / PLB / PLB
        Jsl(0x859AAA);
        asm.B(0xA2, 0xFF, 0xFF, 0x8E, 0x64, 0x12);           // LDX #$FFFF / STX $1264
        Jsl(0x8595FE); Jsl(0x8591A1); Jsl(0x81BAE8);
        asm.B(0xC2, 0x20); Jsl(0x859BDA); asm.B(0xE2, 0x20);
        asm.Jsr("frame");
        asm.B(0xA9, 0x2D);                                   // LDA #45
        asm.Label("wait");
        asm.B(0x48); asm.Jsr("frame"); asm.B(0x68, 0x3A);        // PHA / frame / PLA / DEC
        asm.Br(0xD0, "wait");
        Jsl(0x859671);
        asm.Jsr("frame");
        Jsl(0x859ABC);
        asm.Label("a_done");
        asm.B(0xAB, 0x28, 0x6B);                             // PLB / PLP / RTL

        // frame: one frame of the battle screen (8-bit A)
        asm.Label("frame");
        Jsl(0x858A2F); asm.B(0xA9, 0xFF); Long(0x8F, 0x0012F3); Jsl(0x85EC81);
        asm.B(0x60);                                         // RTS

        // roll (A 8-bit, X 16-bit, $0A06 = the item): C set = it breaks, A = its chance
        asm.Label("roll");
        asm.B(0xC2, 0x20); Long(0xAF, 0x000A06); asm.B(0x29, 0xFF, 0x01, 0xAA, 0xE2, 0x20);   // REP / LDA $00:0A06 (the DB is the caller's) / AND #$01FF / TAX / SEP
        Long(0xBF, table);                                 // LDA table,X
        asm.B(0xC9, 0xFF); asm.Br(0xD0, "have");               // CMP #$FF / BNE: its own
        Jsl(0x81F291);                                     // $0A09 = its record (bank $96)
        asm.B(0xC2, 0x20); Long(0xAF, 0x000A09); asm.B(0xAA, 0xE2, 0x20);   // REP / LDA $00:0A09 / TAX / SEP
        Long(0xBF, 0x960007);                              // LDA $96:0007,X (equipment type)
        asm.Br(0xF0, "use");                                 // BEQ: not equipment, used up
        asm.B(0xA9, 0x00); asm.Br(0x80, "have");               // LDA #0 / BRA
        asm.Label("use");
        asm.B(0xA9, 100);
        asm.Label("have");
        asm.B(0xC9, 100); asm.Br(0xB0, "yes");                 // CMP #100 / BCS: always
        asm.B(0xC9, 0x00); asm.Br(0xF0, "no");                 // CMP #0 / BEQ: never
        asm.B(0x48, 0xA9, 100); Jsl(0x808299);               // PHA / LDA #100 / JSL random (0-99)
        asm.B(0xC3, 0x01, 0x68);                             // CMP 1,S / PLA (C clear: the roll is under the chance)
        asm.Br(0x90, "yes");
        asm.Label("no");
        asm.B(0x18, 0x60);                                   // CLC / RTS
        asm.Label("yes");
        asm.B(0x38, 0x60);                                   // SEC / RTS

        asm.Label("text");
        asm.B(System.Text.Encoding.ASCII.GetBytes(" broke!"));
        asm.B(0x00);
        return (asm.Done(), (battle, after, menu));
    }

}
