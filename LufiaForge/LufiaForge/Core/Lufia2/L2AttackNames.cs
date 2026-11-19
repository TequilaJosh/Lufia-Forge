namespace LufiaForge.Core.Lufia2;

// monster attack names we add after the game's 8A (numbers 8B-FF), for an attack that gets its own name
// the game shows a name with EnemyAttackName ($85:9532): the number (battle register 25) minus one indexes the
// table at $85:F2D7, and the record there (an animation byte, the name, 00) gets copied to $7E:1262 (animation byte
// and first letter) and $7E:1269 (the name, 37 bytes). our hook at $85:954A (in place of DEC / ASL / TAX /
// LDY $F2D7,X) does the same lookup for 01-8A, for 8B and up it copies the record from our block itself and
// returns past the game's copy ($85:9572). read from the game's code 2026-11-11
// block: "LF2AN", version, how many names, code at +10, a table of 0x75 record addresses at +80, records of 0x28
// bytes from +180 (animation, up to 26 letters, 00)
public static class L2AttackNames
{
    public const int First = L2AiScripts.AttackNameCount + 1, Last = 0xFF, Slots = Last - First + 1;
    private const string Magic = "LF2AN";
    private const int Version = 1, CodeAt = 0x10, TableAt = 0x80, RecordsAt = 0x180, SlotSize = 0x28;
    private const int BlockSize = RecordsAt + Slots * SlotSize;
    private const int Site = 0x2954A;
    private static readonly byte[] SiteOriginal = { 0x3A, 0x0A, 0xAA, 0xBC, 0xD7, 0xF2 };

    // file offset of the block, -1 if no name was added yet
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x22) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    // how many names were added
    public static int Count(RomBuffer rom) => Block(rom) is var block && block >= 0 ? Math.Min((int)rom.ReadByte(block + Magic.Length + 1), Slots) : 0;

    private static int Slot(int block, int name) => block + RecordsAt + (name - First) * SlotSize;

    // the added names in order (name First first)
    public static List<string> Names(RomBuffer rom)
    {
        var names = new List<string>();
        int block = Block(rom);
        for (int name = First; name < First + Count(rom); name++)
        {
            var sb = new System.Text.StringBuilder();
            int textOffset = Slot(block, name) + 1;
            for (int i = 0; i < SlotSize - 1; i++) { byte c = rom.ReadByte(textOffset + i); if (c == 0) break; sb.Append(L2Charset.ToChar(rom, c) ?? '?'); }
            names.Add(sb.ToString());
        }
        return names;
    }

    public static bool IsAdded(RomBuffer rom, int name) => name >= First && name < First + Count(rom);

    // the animation an added name brings (E0 = none)
    public static int Animation(RomBuffer rom, int name) => IsAdded(rom, name) ? rom.ReadByte(Slot(Block(rom), name)) : 0xE0;

    // why names can't be added to this rom, null if they can
    public static string? CantAdd(RomBuffer rom)
    {
        if (!rom.IsLufia2) return "Only Lufia II has these attack names.";
        if (Block(rom) is var block && block >= 0) return Count(rom) >= Slots ? $"All {Slots} added names are taken: rename one of them instead." : null;
        if (!rom.ReadBytes(Site, SiteOriginal.Length).SequenceEqual(SiteOriginal))
            return "This ROM's code that shows attack names isn't the US game's (another patch changed it), so names can't be added.";
        return null;
    }

    // why text can't be a name, or null
    public static string? Check(RomBuffer rom, string text)
    {
        if (text.Length == 0) return "A name can't be empty.";
        if (text.Length > L2Attacks.NameMax) return $"Up to {L2Attacks.NameMax} letters (the battle window shows one line).";
        foreach (char c in text) if (L2Charset.ToByte(rom, c) == null) return $"The game's letters have no '{c}'.";
        return null;
    }

    // adds a name (with the animation it brings, E0 = none), returns its number
    public static int Add(RomBuffer rom, string text, int animation = 0xE0)
    {
        text = text.Trim();
        if (CantAdd(rom) is { } whyNot) throw new InvalidOperationException(whyNot);
        if (Check(rom, text) is { } problem) throw new InvalidOperationException(problem);
        int block = Install(rom);
        int name = First + Count(rom);
        Write(rom, block, name, text, animation);
        rom.WriteByte(block + Magic.Length + 1, (byte)(name - First + 1));
        return name;
    }

    // renames an added name, null when done, else why not
    public static string? Rename(RomBuffer rom, int name, string text)
    {
        text = text.Trim();
        if (!IsAdded(rom, name)) return "There's no such added name.";
        if (Check(rom, text) is { } problem) return problem;
        int block = Block(rom);
        Write(rom, block, name, text, rom.ReadByte(Slot(block, name)));
        return null;
    }

    private static void Write(RomBuffer rom, int block, int name, string text, int animation)
    {
        var record = new byte[SlotSize];
        record[0] = (byte)animation;
        for (int i = 0; i < text.Length; i++) record[1 + i] = L2Charset.ToByte(rom, text[i]) ?? (byte)' ';
        rom.WriteBytes(Slot(block, name), record);
    }

    // puts the block and the hook in (once)
    private static int Install(RomBuffer rom)
    {
        int existing = Block(rom);
        if (existing >= 0) return existing;
        int block = L2Rom.Allocate(rom, BlockSize);
        int code = L2Rom.FileToSnes(block + CodeAt), table = L2Rom.FileToSnes(block + TableAt);
        int bank = code >> 16;
        var bytes = Assemble(code, table, bank);
        if (CodeAt + bytes.Length > TableAt) throw new InvalidOperationException("The name hook doesn't fit.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + Magic.Length, Version);
        rom.WriteByte(block + Magic.Length + 1, 0);
        rom.WriteBytes(block + CodeAt, bytes);
        for (int i = 0; i < Slots; i++)
            rom.WriteUInt16Le(block + TableAt + i * 2, (ushort)L2Rom.FileToSnes(block + RecordsAt + i * SlotSize));
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)(code), (byte)(code >> 8), (byte)(code >> 16), 0xEA, 0xEA });
        return block;
    }

    // the hook (JSL from $85:954A, A = the name number, 16-bit A and X, data bank $85)
    // 01-8A: Y = the game's record, back to its copy
    // 8B and up: copies the record like the game does (data bank $7E for the stores), then returns to $85:9572,
    // past the game's copy
    private static byte[] Assemble(int code, int table, int bank)
    {
        var asm = new L2AiScripts.Asm(code);
        void Long(byte op, int addr) => asm.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        asm.B(0xC9, First, 0x00); asm.Br(0xB0, "added");          // CMP #First / BCS
        asm.B(0x3A, 0x0A, 0xAA);                                // DEC / ASL / TAX (same as the game)
        Long(0xBF, 0x85F2D7);                                 // LDA $85:F2D7,X
        asm.B(0xA8, 0x6B);                                      // TAY / RTL
        asm.Label("added");
        asm.B(0x38, 0xE9, First, 0x00, 0x0A, 0xAA);             // SEC / SBC #First / ASL / TAX
        Long(0xBF, table); asm.B(0xAA);                         // LDA table,X / TAX (the record)
        asm.B(0x8B, 0xE2, 0x20, 0xA9, 0x7E, 0x48, 0xAB, 0xC2, 0x20);   // PHB / SEP / LDA #$7E / PHA / PLB / REP
        Long(0xBF, bank << 16); asm.B(0x8D, 0x62, 0x12);        // LDA bank:0000,X / STA $1262
        asm.B(0xA0, 0x00, 0x00);                                // LDY #0
        asm.Label("copy");
        Long(0xBF, bank << 16 | 1); asm.B(0x99, 0x69, 0x12);    // LDA bank:0001,X / STA $1269,Y
        asm.B(0xE8, 0xE8, 0xC8, 0xC8, 0xC0, 0x24, 0x00); asm.Br(0xD0, "copy");   // INX INX INY INY / CPY #$24 / BNE
        asm.B(0xE2, 0x20); Long(0xBF, bank << 16 | 1); asm.B(0x99, 0x69, 0x12, 0xC2, 0x20);   // the last byte, same as the game
        asm.B(0xAB);                                            // PLB
        asm.B(0xA9, 0x71, 0x95, 0x83, 0x01, 0x6B);              // LDA #$9571 / STA 1,S / RTL (to $85:9572)
        return asm.Done();
    }
}
