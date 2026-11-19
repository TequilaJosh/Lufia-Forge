namespace LufiaForge.Core.Lufia2;

// gold in treasure (US rom, read from the code 2026-10-27). the game's treasure records only hold an item number
// (L2Chests): opening one ($8E:C153) puts the item in $7F:D4EF (low) / $7F:D4F0 (bit 0 = item + 0x100) and calls
// $8E:C1DE, which plays the chest's opening ($83:F559), writes "Found " ($85:8000) and the item's name ($0B77, 12
// letters) into the message ($8E:B5FB), picks the icon ($7F:E5A6 / $7F:E5CE) and the sound, shows the message and
// then gives the item ($09CF / $09D0). with bit 7 of $7F:D4F0 set nothing is given, and the caller sets the opened
// flag either way. gold is $0A8A (u16) + $0A8C (u8), at most 9,999,999
//
// the hook: the JSL $8E:C1DE at $8E:C195 (the one a town or dungeon treasure uses, the chests read from a RAM list,
// the Ancient Cave's by the look of it, use the other caller at $8E:C0F5 and are left alone) goes to our block. items
// 0x1E0-0x1FF (past the game's 0x1D3 items) stand for gold: slot n = item 0x1E0 + n is an amount and its text in the
// block ("1500 GOLD"). for those the block adds the amount to the party's gold (kept at 9,999,999), writes the text
// where the item name goes, sets bit 7 of $7F:D4F0 (so no item is given and the flag still gets set: the treasure
// gives its gold once), does the start of $8E:C1DE itself and goes on at $8E:C26E with the item sound. any other item
// goes to $8E:C1DE like before
public static class L2Gold
{
    public const int Site = 0x74195, First = 0x1E0, Slots = 32, Max = 9_999_999;
    // how the editors keep a gold treasure in their item field: this plus the amount
    public const int EditorBase = 0x10000;
    private static readonly byte[] SiteOriginal = { 0x22, 0xDE, 0xC1, 0x8E };
    private const string Magic = "LF2GD";
    private const int BlockSize = 0x300, CodeAt = 0x10, TableAt = 0x100, Entry = 16, TextLength = 12;

    // file offset of our gold block, or -1
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x22) return -1;
        int blockAt = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return blockAt >= 0 && blockAt + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(blockAt, Magic.Length)) == Magic ? blockAt : -1;
    }

    public static bool Installed(RomBuffer rom) => Block(rom) >= 0;

    // why gold can't go in treasure in this rom, or null
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

    // the amount gold slot slot gives
    public static int Amount(RomBuffer rom, int slot)
    {
        int block = Block(rom);
        return block < 0 ? 0 : (int)rom.ReadUInt24Le(block + TableAt + slot * Entry);
    }

    // a treasure record's item the way the editors keep it: EditorBase + amount for gold
    public static int ToEditor(RomBuffer rom, int item) => item >= First && item < First + Slots && Installed(rom) ? EditorBase + Amount(rom, item - First) : item;

    // what an editor item is, in words ("1500 gold" or the item's name)
    public static string Text(int amount) => $"{amount} GOLD";

    // the gold slots treasure uses now, besides map exceptMap's
    private static HashSet<int> UsedSlots(RomBuffer rom, int exceptMap)
    {
        var used = new HashSet<int>();
        for (int m = 2; m < L2Maps.Count(rom); m++)
            if (m != exceptMap)
                foreach (var record in L2Chests.Records(rom, m))
                    if (record.Item >= First && record.Item < First + Slots) used.Add(record.Item - First);
        return used;
    }

    // the records the way the game reads them: gold amounts become gold items (slots shared by equal amounts, free
    // slots taken for new ones, the hook goes in the first time). map is the map they're for (its old records don't
    // hold slots)
    public static List<L2Chests.Rec> ToRom(RomBuffer rom, int map, IReadOnlyList<L2Chests.Rec> recs)
    {
        if (!recs.Any(r => IsGold(r.Item))) return recs.ToList();
        if (CantInstall(rom) is { } why) throw new InvalidOperationException(why);
        Install(rom);
        int block = Block(rom);
        var used = UsedSlots(rom, map);
        var bySlot = used.ToDictionary(s => s, s => Amount(rom, s));
        var output = new List<L2Chests.Rec>();
        foreach (var record in recs)
        {
            if (!IsGold(record.Item)) { output.Add(record); continue; }
            int amount = Math.Clamp(AmountOf(record.Item), 1, Max);
            int slot = bySlot.FirstOrDefault(kv => kv.Value == amount, new KeyValuePair<int, int>(-1, 0)).Key;
            if (slot < 0)
            {
                slot = Enumerable.Range(0, Slots).FirstOrDefault(s => !bySlot.ContainsKey(s), -1);
                if (slot < 0) throw new InvalidOperationException($"Treasure can give at most {Slots} different amounts of gold; use an amount another treasure gives.");
                bySlot[slot] = amount;
                WriteSlot(rom, block, slot, amount);
            }
            output.Add(record with { Item = First + slot });
        }
        return output;
    }

    private static void WriteSlot(RomBuffer rom, int block, int slot, int amount)
    {
        int entryAt = block + TableAt + slot * Entry;
        rom.WriteBytes(entryAt, new[] { (byte)amount, (byte)(amount >> 8), (byte)(amount >> 16) });
        var text = System.Text.Encoding.ASCII.GetBytes(Text(amount).PadRight(TextLength)[..TextLength]);
        rom.WriteBytes(entryAt + 3, text);
        rom.WriteByte(entryAt + 3 + TextLength, 0);
    }

    // puts the hook and its block in (once)
    public static void Install(RomBuffer rom)
    {
        if (Installed(rom)) return;
        if (CantInstall(rom) is { } why) throw new InvalidOperationException(why);
        int block = L2Rom.Allocate(rom, BlockSize);
        int table = L2Rom.FileToSnes(block + TableAt);
        var codeBytes = new List<byte>();
        void Op(params int[] bytes) => codeBytes.AddRange(bytes.Select(x => (byte)x));
        void Long(int op, int addr) => Op(op, addr & 0xFF, addr >> 8 & 0xFF, addr >> 16 & 0xFF);
        // (8-bit A, 16-bit X here, same as at $8E:C195)
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
        Op(0xC9, 0x98); int toKeep = codeBytes.Count; Op(0x90, 0x00);   // CMP #$98 / BCC ok
        int toCap = codeBytes.Count; Op(0xD0, 0x00);                    // BNE cap (more than $98xxxx)
        Op(0xC2, 0x20, 0xAD, 0x8A, 0x0A, 0xC9, 0x7F, 0x96, 0xE2, 0x20);   // REP / LDA $0A8A / CMP #$967F / SEP
        int toKeep2 = codeBytes.Count; Op(0x90, 0x00);                  // BCC ok
        codeBytes[toCap + 1] = (byte)(codeBytes.Count - toCap - 2);
        Op(0xC2, 0x20, 0xA9, 0x7F, 0x96, 0x8D, 0x8A, 0x0A, 0xE2, 0x20, 0xA9, 0x98, 0x8D, 0x8C, 0x0A);   // cap
        codeBytes[toKeep + 1] = (byte)(codeBytes.Count - toKeep - 2);
        codeBytes[toKeep2 + 1] = (byte)(codeBytes.Count - toKeep2 - 2);
        // the text where the item's name goes ($0B77, 12 letters, then 0)
        Op(0xA0, 0x00, 0x00);                                   // LDY #0
        int loop = codeBytes.Count;
        Long(0xBF, table + 3); Op(0x99, 0x77, 0x0B);            // LDA text,X / STA $0B77,Y
        Op(0xE8, 0xC8, 0xC0, TextLength, 0x00);                 // INX / INY / CPY #12
        Op(0xD0, (byte)(loop - (codeBytes.Count + 2)));         // BNE loop
        Op(0xA9, 0x00, 0x8D, 0x83, 0x0B);                       // LDA #0 / STA $0B83
        Op(0xA9, 0x80); Long(0x8F, 0x7FD4F0);                   // no item: bit 7 of $7F:D4F0
        // the start of $8E:C1DE: the chest opens, "Found " + the text
        Long(0xAF, 0x7FD4EE); Long(0x8F, 0x7FD04E); Op(0x09, 0x80, 0xEB, 0xA9, 0x00); Long(0x22, 0x83F559);
        Op(0xA2, 0x00, 0x80, 0x86, 0x60);                       // LDX #$8000 / STX $60
        Op(0xA2, 0x77, 0x0B); Long(0x22, 0x8EB5FB);             // LDX #$0B77 / JSL $8E:B5FB
        // the item window's picture and the item sound, then the rest of $8E:C1DE
        Op(0xA9, 0x1C); Long(0x8F, 0x7FE5A6); Op(0xA9, 0x0A); Long(0x8F, 0x7FE5CE);
        Op(0xA9, 0x36); Long(0x5C, 0x8EC26E);                   // LDA #$36 / JML $8E:C26E
        if (CodeAt + codeBytes.Count > TableAt) throw new InvalidOperationException("The gold code doesn't fit its block.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + Magic.Length, 1);
        rom.WriteBytes(block + CodeAt, codeBytes.ToArray());
        int code = L2Rom.FileToSnes(block + CodeAt);
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)code, (byte)(code >> 8), (byte)(code >> 16) });
    }
}
