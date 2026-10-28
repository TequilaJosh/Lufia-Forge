namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Gold in treasure (US ROM, read from the code 2026-10-27). The game's treasure records only hold an item number
/// (<see cref="L2Chests"/>): opening one ($8E:C153) puts the item in $7F:D4EF (low) / $7F:D4F0 (bit 0 = item + 0x100)
/// and calls $8E:C1DE, which plays the chest's opening ($83:F559), writes "Found " ($85:8000) and the item's name
/// ($0B77, 12 letters) into the message ($8E:B5FB), picks the icon ($7F:E5A6 / $7F:E5CE) and the sound, shows the message
/// and then gives the item ($09CF / $09D0); with bit 7 of $7F:D4F0 set nothing is given, and the opened flag is set
/// by the caller either way. Gold is $0A8A (u16) + $0A8C (u8), at most 9,999,999.
///
/// The hook: the JSL $8E:C1DE at $8E:C195 (the one a town or dungeon treasure uses; the chests read from a RAM list, the
/// Ancient Cave's by the look of it, use the other caller at $8E:C0F5 and are left alone) goes to Lufia Forge's block.
/// Items 0x1E0-0x1FF (past the game's 0x1D3 items) stand for gold: slot n = item 0x1E0 + n is an amount and its text in the block ("1500 GOLD"). For those the
/// block adds the amount to the party's gold (kept at 9,999,999), writes the text where the item name goes, sets bit 7
/// of $7F:D4F0 (so no item is given and the flag is still set: the treasure gives its gold once), does the start of
/// $8E:C1DE itself and goes on at $8E:C26E with the item sound. Any other item goes to $8E:C1DE as before.
/// </summary>
public static class L2Gold
{
    public const int Site = 0x74195, First = 0x1E0, Slots = 32, Max = 9_999_999;
    /// <summary>How the editors keep a gold treasure in their item field: this plus the amount.</summary>
    public const int EditorBase = 0x10000;
    private static readonly byte[] SiteOriginal = { 0x22, 0xDE, 0xC1, 0x8E };
    private const string Magic = "LF2GD";
    private const int BlockSize = 0x300, CodeAt = 0x10, TableAt = 0x100, Entry = 16, TextLength = 12;

    /// <summary>File offset of Lufia Forge's gold block, or -1.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x22) return -1;
        int at = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return at >= 0 && at + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(at, Magic.Length)) == Magic ? at : -1;
    }

    public static bool Installed(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Why gold can't go in treasure in this ROM, or null.</summary>
    public static string? CantInstall(RomBuffer rom)
    {
        if (Installed(rom)) return null;
        if (rom.Length <= Site + 4 || !rom.ReadBytes(Site, 4).SequenceEqual(SiteOriginal))
            return "This ROM's treasure code isn't the US game's (another hack changed it), so gold can't be put in treasure.";
        if (L2Battle.ItemCountIn(rom) > First)
            return $"This ROM has {L2Battle.ItemCountIn(rom)} items, so item numbers {First:X3} and up (which Lufia Forge uses for gold) are taken.";
        return null;
    }

    public static bool IsGold(int editorItem) => editorItem >= EditorBase;
    public static int AmountOf(int editorItem) => editorItem - EditorBase;
    public static int Item(int amount) => EditorBase + Math.Clamp(amount, 1, Max);

    /// <summary>The amount gold slot <paramref name="slot"/> gives.</summary>
    public static int Amount(RomBuffer rom, int slot)
    {
        int b = Block(rom);
        return b < 0 ? 0 : (int)rom.ReadUInt24Le(b + TableAt + slot * Entry);
    }

    /// <summary>A treasure record's item as the editors keep it: <see cref="EditorBase"/> + amount for gold.</summary>
    public static int ToEditor(RomBuffer rom, int item) => item >= First && item < First + Slots && Installed(rom) ? EditorBase + Amount(rom, item - First) : item;

    /// <summary>What an editor item is, in words ("1500 gold" or the item's name).</summary>
    public static string Text(int amount) => $"{amount} GOLD";

    /// <summary>The gold slots treasure uses now, besides map <paramref name="exceptMap"/>'s.</summary>
    private static HashSet<int> UsedSlots(RomBuffer rom, int exceptMap)
    {
        var used = new HashSet<int>();
        for (int m = 2; m < L2Maps.Count; m++)
            if (m != exceptMap)
                foreach (var r in L2Chests.Records(rom, m))
                    if (r.Item >= First && r.Item < First + Slots) used.Add(r.Item - First);
        return used;
    }

    /// <summary>The records as the game reads them: gold amounts become gold items (slots shared by equal amounts,
    /// free slots taken for new ones; the hook goes in the first time). <paramref name="map"/> is the map they are for
    /// (its old records don't hold slots).</summary>
    public static List<L2Chests.Rec> ToRom(RomBuffer rom, int map, IReadOnlyList<L2Chests.Rec> recs)
    {
        if (!recs.Any(r => IsGold(r.Item))) return recs.ToList();
        if (CantInstall(rom) is { } why) throw new InvalidOperationException(why);
        Install(rom);
        int b = Block(rom);
        var used = UsedSlots(rom, map);
        var bySlot = used.ToDictionary(s => s, s => Amount(rom, s));
        var outp = new List<L2Chests.Rec>();
        foreach (var r in recs)
        {
            if (!IsGold(r.Item)) { outp.Add(r); continue; }
            int amount = Math.Clamp(AmountOf(r.Item), 1, Max);
            int slot = bySlot.FirstOrDefault(kv => kv.Value == amount, new KeyValuePair<int, int>(-1, 0)).Key;
            if (slot < 0)
            {
                slot = Enumerable.Range(0, Slots).FirstOrDefault(s => !bySlot.ContainsKey(s), -1);
                if (slot < 0) throw new InvalidOperationException($"Treasure can give at most {Slots} different amounts of gold; use an amount another treasure gives.");
                bySlot[slot] = amount;
                WriteSlot(rom, b, slot, amount);
            }
            outp.Add(r with { Item = First + slot });
        }
        return outp;
    }

    private static void WriteSlot(RomBuffer rom, int block, int slot, int amount)
    {
        int at = block + TableAt + slot * Entry;
        rom.WriteBytes(at, new[] { (byte)amount, (byte)(amount >> 8), (byte)(amount >> 16) });
        var text = System.Text.Encoding.ASCII.GetBytes(Text(amount).PadRight(TextLength)[..TextLength]);
        rom.WriteBytes(at + 3, text);
        rom.WriteByte(at + 3 + TextLength, 0);
    }

    /// <summary>Puts the hook and its block in (once).</summary>
    public static void Install(RomBuffer rom)
    {
        if (Installed(rom)) return;
        if (CantInstall(rom) is { } why) throw new InvalidOperationException(why);
        int b = L2Rom.Allocate(rom, BlockSize);
        int table = L2Rom.FileToSnes(b + TableAt);
        var c = new List<byte>();
        void Op(params int[] bytes) => c.AddRange(bytes.Select(x => (byte)x));
        void Long(int op, int addr) => Op(op, addr & 0xFF, addr >> 8 & 0xFF, addr >> 16 & 0xFF);
        // (8-bit A, 16-bit X here, as at $8E:C195)
        Long(0xAF, 0x7FD4F0); Op(0xC9, 0x01);                  // LDA $7F:D4F0 / CMP #1 (item + 0x100, no bit 7)
        Op(0xD0, 0x08);                                         // BNE game
        Long(0xAF, 0x7FD4EF); Op(0xC9, First & 0xFF);           // LDA $7F:D4EF / CMP #$E0
        Op(0xB0, 0x04);                                         // BCS gold
        Long(0x5C, 0x8EC1DE);                                   // game: JML $8E:C1DE
        // gold: X = slot * 16
        Op(0x38, 0xE9, First & 0xFF);                           // SEC / SBC #$E0
        Op(0xC2, 0x20, 0x29, 0xFF, 0x00, 0x0A, 0x0A, 0x0A, 0x0A, 0xAA);   // REP #$20 / AND #$FF / ASL x4 / TAX
        Op(0x18); Op(0xAD, 0x8A, 0x0A);                         // CLC / LDA $0A8A
        Long(0x7F, table); Op(0x8D, 0x8A, 0x0A);                // ADC amount,X / STA $0A8A
        Op(0xE2, 0x20); Op(0xAD, 0x8C, 0x0A);                   // SEP #$20 / LDA $0A8C
        Long(0x7F, table + 2); Op(0x8D, 0x8C, 0x0A);            // ADC amount+2,X / STA $0A8C
        // keep it at 9,999,999 ($98967F)
        Op(0xC9, 0x98); int toKeep = c.Count; Op(0x90, 0x00);   // CMP #$98 / BCC ok
        int toCap = c.Count; Op(0xD0, 0x00);                    // BNE cap (more than $98xxxx)
        Op(0xC2, 0x20, 0xAD, 0x8A, 0x0A, 0xC9, 0x7F, 0x96, 0xE2, 0x20);   // REP / LDA $0A8A / CMP #$967F / SEP
        int toKeep2 = c.Count; Op(0x90, 0x00);                  // BCC ok
        c[toCap + 1] = (byte)(c.Count - toCap - 2);
        Op(0xC2, 0x20, 0xA9, 0x7F, 0x96, 0x8D, 0x8A, 0x0A, 0xE2, 0x20, 0xA9, 0x98, 0x8D, 0x8C, 0x0A);   // cap
        c[toKeep + 1] = (byte)(c.Count - toKeep - 2);
        c[toKeep2 + 1] = (byte)(c.Count - toKeep2 - 2);
        // the text where the item's name goes ($0B77, 12 letters, then 0)
        Op(0xA0, 0x00, 0x00);                                   // LDY #0
        int loop = c.Count;
        Long(0xBF, table + 3); Op(0x99, 0x77, 0x0B);            // LDA text,X / STA $0B77,Y
        Op(0xE8, 0xC8, 0xC0, TextLength, 0x00);                 // INX / INY / CPY #12
        Op(0xD0, (byte)(loop - (c.Count + 2)));                 // BNE loop
        Op(0xA9, 0x00, 0x8D, 0x83, 0x0B);                       // LDA #0 / STA $0B83
        Op(0xA9, 0x80); Long(0x8F, 0x7FD4F0);                   // no item: bit 7 of $7F:D4F0
        // the start of $8E:C1DE: the chest opens, "Found " + the text
        Long(0xAF, 0x7FD4EE); Long(0x8F, 0x7FD04E); Op(0x09, 0x80, 0xEB, 0xA9, 0x00); Long(0x22, 0x83F559);
        Op(0xA2, 0x00, 0x80, 0x86, 0x60);                       // LDX #$8000 / STX $60
        Op(0xA2, 0x77, 0x0B); Long(0x22, 0x8EB5FB);             // LDX #$0B77 / JSL $8E:B5FB
        // the item window's picture and the item sound, then the rest of $8E:C1DE
        Op(0xA9, 0x1C); Long(0x8F, 0x7FE5A6); Op(0xA9, 0x0A); Long(0x8F, 0x7FE5CE);
        Op(0xA9, 0x36); Long(0x5C, 0x8EC26E);                   // LDA #$36 / JML $8E:C26E
        if (CodeAt + c.Count > TableAt) throw new InvalidOperationException("The gold code doesn't fit its block.");
        rom.WriteBytes(b, new byte[BlockSize]);
        rom.WriteBytes(b, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(b + Magic.Length, 1);
        rom.WriteBytes(b + CodeAt, c.ToArray());
        int code = L2Rom.FileToSnes(b + CodeAt);
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)code, (byte)(code >> 8), (byte)(code >> 16) });
    }
}
