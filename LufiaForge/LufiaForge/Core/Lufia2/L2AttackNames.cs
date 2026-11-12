namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Monster attack names Lufia Forge adds after the game's 8A (numbers 8B-FF), for an attack with a name of its own.
/// <para>The game shows a name with EnemyAttackName ($85:9532): the number (battle register 25) minus one indexes the
/// table at $85:F2D7, and the record found there (an animation byte, the name, 00) is copied to $7E:1262 (the
/// animation byte and the first letter) and $7E:1269 (the name, 37 bytes). A hook at $85:954A (in place of DEC / ASL /
/// TAX / LDY $F2D7,X) does the same lookup for 01-8A; for 8B and up it copies the record from Lufia Forge's block itself
/// and returns past the game's copy ($85:9572). Read from the game's code (2026-11-11).</para>
/// <para>The block: "LF2AN", version, how many names, code at +10, a table of 0x75 record addresses at +80, records
/// of 0x28 bytes from +180 (animation, up to 26 letters, 00).</para>
/// </summary>
public static class L2AttackNames
{
    public const int First = L2AiScripts.AttackNameCount + 1, Last = 0xFF, Slots = Last - First + 1;
    private const string Magic = "LF2AN";
    private const int Version = 1, CodeAt = 0x10, TableAt = 0x80, RecordsAt = 0x180, SlotSize = 0x28;
    private const int BlockSize = RecordsAt + Slots * SlotSize;
    private const int Site = 0x2954A;
    private static readonly byte[] SiteOriginal = { 0x3A, 0x0A, 0xAA, 0xBC, 0xD7, 0xF2 };

    /// <summary>File offset of the block, or -1 when no name was added yet.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x22) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    /// <summary>How many names were added.</summary>
    public static int Count(RomBuffer rom) => Block(rom) is var b && b >= 0 ? Math.Min((int)rom.ReadByte(b + Magic.Length + 1), Slots) : 0;

    private static int Slot(int block, int name) => block + RecordsAt + (name - First) * SlotSize;

    /// <summary>The added names, in order (name <see cref="First"/> first).</summary>
    public static List<string> Names(RomBuffer rom)
    {
        var l = new List<string>();
        int b = Block(rom);
        for (int n = First; n < First + Count(rom); n++)
        {
            var sb = new System.Text.StringBuilder();
            int at = Slot(b, n) + 1;
            for (int i = 0; i < SlotSize - 1; i++) { byte c = rom.ReadByte(at + i); if (c == 0) break; sb.Append(L2Charset.ToChar(rom, c) ?? '?'); }
            l.Add(sb.ToString());
        }
        return l;
    }

    public static bool IsAdded(RomBuffer rom, int name) => name >= First && name < First + Count(rom);

    /// <summary>The animation an added name brings (E0 = none).</summary>
    public static int Animation(RomBuffer rom, int name) => IsAdded(rom, name) ? rom.ReadByte(Slot(Block(rom), name)) : 0xE0;

    /// <summary>Why names can't be added to this ROM, or null when they can.</summary>
    public static string? CantAdd(RomBuffer rom)
    {
        if (!rom.IsLufia2) return "Only Lufia II has these attack names.";
        if (Block(rom) is var b && b >= 0) return Count(rom) >= Slots ? $"All {Slots} added names are taken: rename one of them instead." : null;
        if (!rom.ReadBytes(Site, SiteOriginal.Length).SequenceEqual(SiteOriginal))
            return "This ROM's code that shows attack names isn't the US game's (another patch changed it), so names can't be added.";
        return null;
    }

    /// <summary>Why <paramref name="text"/> can't be a name, or null.</summary>
    public static string? Check(RomBuffer rom, string text)
    {
        if (text.Length == 0) return "A name can't be empty.";
        if (text.Length > L2Attacks.NameMax) return $"Up to {L2Attacks.NameMax} letters (the battle window shows one line).";
        foreach (char c in text) if (L2Charset.ToByte(rom, c) == null) return $"The game's letters have no '{c}'.";
        return null;
    }

    /// <summary>Adds a name (with the animation it brings, E0 = none) and returns its number.</summary>
    public static int Add(RomBuffer rom, string text, int animation = 0xE0)
    {
        text = text.Trim();
        if (CantAdd(rom) is { } no) throw new InvalidOperationException(no);
        if (Check(rom, text) is { } bad) throw new InvalidOperationException(bad);
        int b = Install(rom);
        int n = First + Count(rom);
        Write(rom, b, n, text, animation);
        rom.WriteByte(b + Magic.Length + 1, (byte)(n - First + 1));
        return n;
    }

    /// <summary>Renames an added name; null when done, else why not.</summary>
    public static string? Rename(RomBuffer rom, int name, string text)
    {
        text = text.Trim();
        if (!IsAdded(rom, name)) return "There's no such added name.";
        if (Check(rom, text) is { } bad) return bad;
        int b = Block(rom);
        Write(rom, b, name, text, rom.ReadByte(Slot(b, name)));
        return null;
    }

    private static void Write(RomBuffer rom, int block, int name, string text, int animation)
    {
        var rec = new byte[SlotSize];
        rec[0] = (byte)animation;
        for (int i = 0; i < text.Length; i++) rec[1 + i] = L2Charset.ToByte(rom, text[i]) ?? (byte)' ';
        rom.WriteBytes(Slot(block, name), rec);
    }

    /// <summary>Puts the block and the hook in (once).</summary>
    private static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
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

    /// <summary>The hook (JSL from $85:954A; A = the name number, 16-bit A and X, data bank $85). 01-8A: Y = the game's
    /// record, back to its copy. 8B and up: copies the record as the game does (data bank $7E for the stores), then
    /// returns to $85:9572, past the game's copy.</summary>
    private static byte[] Assemble(int code, int table, int bank)
    {
        var a = new L2AiScripts.Asm(code);
        void Long(byte op, int addr) => a.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        a.B(0xC9, First, 0x00); a.Br(0xB0, "added");          // CMP #First / BCS
        a.B(0x3A, 0x0A, 0xAA);                                // DEC / ASL / TAX (as the game)
        Long(0xBF, 0x85F2D7);                                 // LDA $85:F2D7,X
        a.B(0xA8, 0x6B);                                      // TAY / RTL
        a.Label("added");
        a.B(0x38, 0xE9, First, 0x00, 0x0A, 0xAA);             // SEC / SBC #First / ASL / TAX
        Long(0xBF, table); a.B(0xAA);                         // LDA table,X / TAX (the record)
        a.B(0x8B, 0xE2, 0x20, 0xA9, 0x7E, 0x48, 0xAB, 0xC2, 0x20);   // PHB / SEP / LDA #$7E / PHA / PLB / REP
        Long(0xBF, bank << 16); a.B(0x8D, 0x62, 0x12);        // LDA bank:0000,X / STA $1262
        a.B(0xA0, 0x00, 0x00);                                // LDY #0
        a.Label("copy");
        Long(0xBF, bank << 16 | 1); a.B(0x99, 0x69, 0x12);    // LDA bank:0001,X / STA $1269,Y
        a.B(0xE8, 0xE8, 0xC8, 0xC8, 0xC0, 0x24, 0x00); a.Br(0xD0, "copy");   // INX INX INY INY / CPY #$24 / BNE
        a.B(0xE2, 0x20); Long(0xBF, bank << 16 | 1); a.B(0x99, 0x69, 0x12, 0xC2, 0x20);   // the last byte, as the game
        a.B(0xAB);                                            // PLB
        a.B(0xA9, 0x71, 0x95, 0x83, 0x01, 0x6B);              // LDA #$9571 / STA 1,S / RTL (to $85:9572)
        return a.Done();
    }
}
