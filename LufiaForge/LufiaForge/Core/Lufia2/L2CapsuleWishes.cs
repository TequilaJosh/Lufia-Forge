using System.Text;

namespace LufiaForge.Core.Lufia2;

// each capsule monster's own wish lists (our hook, US rom). in the game all 7 monsters draw the item they ask for
// from the same 16 lists, one per pickiness tier (L2CapsuleFeed.ReadLists), in the routine at $82:C554 (A = tier,
// its only caller is $82:C593, the wish draw for the active capsule $7E:11A3 in its form $7E:11A4)
// the hook: the routine's first 6 bytes (AND #$00FF / ASL / ASL / TAX) become JSL + NOP NOP into a block in the
// added space. the block holds the code, a directory of 7 monsters x 8 u16 (entry 0 = all its forms, 1-5 = that
// form, 0 = none) and the lists, each set laid out like the game's table (16 x u16 offset from the table, u16 count,
// then the items). the code tries the set for the monster's form, then its "all forms" set: the first whose list for
// the tier has items gives the item, and it returns to the routine's RTS ($82:C576, by changing the return address).
// if neither has one it does the 6 bytes it replaced and returns to the game's code, which draws from the shared
// lists. the game's other bytes stay as they are, so the shared lists still get edited like before
public static class L2CapsuleWishes
{
    public const int HookAt = 0x14554, ReturnRts = 0x14576;
    private static readonly byte[] Original = { 0x29, 0xFF, 0x00, 0x0A, 0x0A, 0xAA };
    public const string Magic = "LFCW";
    public const byte Version = 1;
    public const int CodeAt = 0x08, DirAt = 0xC0, Slots = 8, DataAt = DirAt + L2Capsules.Monsters * Slots * 2;
    // forms that can be fed (and so ask for items): 1-3. 0 = all of the monster's forms
    public const int FeedForms = L2CapsuleFeed.FeedForms;

    // file offset of our block, -1 if the hook isn't in the rom
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= HookAt + 6 || rom.ReadByte(HookAt) != 0x22) return -1;
        int code = L2Rom.SnesToFile((int)rom.ReadUInt24Le(HookAt + 1));
        int block = code - CodeAt;
        if (block < 0 || block + DataAt > rom.Length) return -1;
        return Encoding.ASCII.GetString(rom.ReadBytes(block, 4)) == Magic ? block : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    // whether the hook can go in (or is in), and if not why
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        if (L2CapsuleFeed.Unsupported(rom) is { } unsupported) { why = unsupported; return false; }
        if (!rom.ReadBytes(HookAt, Original.Length).SequenceEqual(Original) || rom.ReadByte(ReturnRts) != 0x60)
        {
            why = "This ROM's wish draw code isn't the US game's (another hack changed it), so capsules can't have their own lists.";
            return false;
        }
        return true;
    }

    // the set of 16 lists for monster m in form (0 = all its forms, 1-5), null if it has none (then it uses the
    // game's lists). an empty list means the game's list for that tier
    public static List<int>[]? Read(RomBuffer rom, int m, int form)
    {
        int block = Block(rom);
        if (block < 0) return null;
        int addr = rom.ReadUInt16Le(block + DirAt + (m * Slots + form) * 2);
        if (addr == 0) return null;
        int bank = L2Rom.FileToSnes(block) & 0xFF0000;
        int table = L2Rom.SnesToFile(bank | addr);
        return Enumerable.Range(0, L2CapsuleFeed.Tiers).Select(i =>
        {
            int offset = rom.ReadUInt16Le(table + i * 4), count = rom.ReadUInt16Le(table + i * 4 + 2);
            return Enumerable.Range(0, Math.Min(count, 255)).Select(k => rom.ReadUInt16Le(table + offset + k * 2) & 0x1FF).ToList();
        }).ToArray();
    }

    // every set in the rom: (monster, form) -> lists
    public static Dictionary<(int M, int Form), List<int>[]> ReadAll(RomBuffer rom)
    {
        var sets = new Dictionary<(int, int), List<int>[]>();
        if (!IsInstalled(rom)) return sets;
        for (int m = 0; m < L2Capsules.Monsters; m++)
            for (int form = 0; form < Slots; form++)
                if (Read(rom, m, form) is { } lists) sets[(m, form)] = lists;
        return sets;
    }

    // gives monster m in form (0 = all its forms) its own lists, or takes them away (null, or all 16 lists empty).
    // puts the hook in the first time
    public static void Write(RomBuffer rom, int m, int form, IReadOnlyList<IReadOnlyList<int>>? lists)
    {
        if (m < 0 || m >= L2Capsules.Monsters || form < 0 || form >= Slots) throw new ArgumentOutOfRangeException(nameof(m));
        if (lists != null && lists.Count != L2CapsuleFeed.Tiers) throw new ArgumentException("16 lists are needed.");
        if (lists != null && lists.Any(l => l.Count > 255)) throw new ArgumentException("A list holds up to 255 items.");
        var all = ReadAll(rom);
        if (lists == null || lists.All(l => l.Count == 0)) all.Remove((m, form));
        else all[(m, form)] = lists.Select(l => l.ToList()).ToArray();
        if (all.Count == 0 && !IsInstalled(rom)) return;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);

        // lay out the sets
        int size = DataAt + all.Values.Sum(s => L2CapsuleFeed.Tiers * 4 + s.Sum(l => l.Count) * 2);
        int block = Block(rom);
        int room = block >= 0 ? rom.ReadUInt16Le(block + 5) : 0;
        if (block < 0 || size > room)
        {
            int capacity = Math.Min(0x8000, size + 0x200);
            if (size > capacity) throw new InvalidOperationException("The capsules' own lists don't fit one bank of the added space.");
            int newBlock = L2Rom.Allocate(rom, capacity);
            if (block >= 0) rom.WriteBytes(block, new byte[room]);   // the old block isn't used any more
            block = newBlock;
            room = capacity;
        }
        var data = new byte[room];
        Encoding.ASCII.GetBytes(Magic).CopyTo(data, 0);
        data[4] = Version;
        data[5] = (byte)room; data[6] = (byte)(room >> 8);
        int snes = L2Rom.FileToSnes(block);
        int bank = snes >> 16, baseAddr = snes & 0xFFFF;
        int position = DataAt;
        foreach (var ((monster, setForm), sets) in all.OrderBy(k => k.Key))
        {
            int table = position;
            int addr = baseAddr + table;
            data[DirAt + (monster * Slots + setForm) * 2] = (byte)addr; data[DirAt + (monster * Slots + setForm) * 2 + 1] = (byte)(addr >> 8);
            position += L2CapsuleFeed.Tiers * 4;
            for (int i = 0; i < L2CapsuleFeed.Tiers; i++)
            {
                int offset = position - table;
                data[table + i * 4] = (byte)offset; data[table + i * 4 + 1] = (byte)(offset >> 8);
                data[table + i * 4 + 2] = (byte)sets[i].Count; data[table + i * 4 + 3] = 0;
                foreach (int item in sets[i]) { data[position] = (byte)item; data[position + 1] = (byte)(item >> 8); position += 2; }
            }
        }
        var code = Code(bank, snes + DirAt, snes + CodeAt);
        if (code.Length > DirAt - CodeAt) throw new InvalidOperationException("Hook code too long.");
        code.CopyTo(data, CodeAt);
        rom.WriteBytes(block, data);
        int entry = snes + CodeAt;
        rom.WriteBytes(HookAt, new byte[] { 0x22, (byte)entry, (byte)(entry >> 8), (byte)(entry >> 16), 0xEA, 0xEA });
    }

    // the hook's code (see the top of the file). entered by JSL from $82:C554 with A = the tier (16-bit A)
    private static byte[] Code(int bank, int dir, int codeAddr)
    {
        var code = new List<byte>();
        void E(params byte[] b) => code.AddRange(b);
        byte d0 = (byte)dir, d1 = (byte)(dir >> 8), d2 = (byte)(dir >> 16), bk = (byte)bank;
        E(0x08);                         // PHP
        E(0xC2, 0x30);                   // REP #$30
        E(0x29, 0xFF, 0x00);             // AND #$00FF
        E(0x0A, 0x0A);                   // ASL / ASL        tier * 4
        E(0x85, 0x54);                   // STA $54
        E(0xAD, 0xA3, 0x11);             // LDA $11A3        monster
        E(0x29, 0xFF, 0x00);             // AND #$00FF
        E(0x0A, 0x0A, 0x0A);             // ASL x3           monster * 8
        E(0x48);                         // PHA
        E(0xAD, 0xA4, 0x11);             // LDA $11A4        form
        E(0x29, 0x07, 0x00);             // AND #$0007
        E(0x03, 0x01);                   // ORA 1,S
        E(0x0A);                         // ASL
        E(0xAA);                         // TAX
        E(0x68);                         // PLA
        E(0xDA);                         // PHX
        E(0xBF, d0, d1, d2);             // LDA dir,X        this form's set
        int jsr1 = code.Count; E(0x20, 0x00, 0x00);   // JSR try
        E(0xFA);                         // PLX
        int bcs1 = code.Count; E(0xB0, 0x00);   // BCS done
        E(0x8A);                         // TXA
        E(0x29, 0xF0, 0xFF);             // AND #$FFF0       entry 0: all its forms
        E(0xAA);                         // TAX
        E(0xBF, d0, d1, d2);             // LDA dir,X
        int jsr2 = code.Count; E(0x20, 0x00, 0x00);   // JSR try
        int bcs2 = code.Count; E(0xB0, 0x00);   // BCS done
        E(0xA5, 0x54);                   // LDA $54          the game's code goes on with X = tier * 4
        E(0xAA);                         // TAX
        E(0x28);                         // PLP
        E(0x6B);                         // RTL              (to the NOPs, then LDA $95FF16,X)
        int done = code.Count;
        E(0x85, 0x54);                   // STA $54
        E(0xA9, 0x75, 0xC5);             // LDA #$C575
        E(0x83, 0x02);                   // STA 2,S          return to $82:C576 (RTS)
        E(0xA5, 0x54);                   // LDA $54
        E(0x28);                         // PLP
        E(0x6B);                         // RTL
        // try: A = a set's table (0 = none). carry set and A = an item of its list for the tier, else carry clear
        int tryAt = code.Count;
        int beq1 = code.Count; E(0xF0, 0x00);   // BEQ no
        E(0x48);                         // PHA              [table]
        E(0x18, 0x65, 0x54);             // CLC / ADC $54
        E(0xAA);                         // TAX
        E(0xBF, 0x02, 0x00, bk);         // LDA bank:0002,X  count
        int beq2 = code.Count; E(0xF0, 0x00);   // BEQ empty
        E(0xBF, 0x00, 0x00, bk);         // LDA bank:0000,X  offset
        E(0x18, 0x63, 0x01);             // CLC / ADC 1,S
        E(0x83, 0x01);                   // STA 1,S          [items]
        E(0xBF, 0x02, 0x00, bk);         // LDA bank:0002,X  count
        E(0x22, 0x99, 0x82, 0x80);       // JSL $808299      random 0..count-1
        E(0x29, 0xFF, 0x00);             // AND #$00FF
        E(0x0A);                         // ASL
        E(0x18, 0x63, 0x01);             // CLC / ADC 1,S
        E(0xAA);                         // TAX
        E(0x68);                         // PLA
        E(0xBF, 0x00, 0x00, bk);         // LDA bank:0000,X  the item
        E(0x38);                         // SEC
        E(0x60);                         // RTS
        int empty = code.Count;
        E(0x68);                         // PLA
        int no = code.Count;
        E(0x18);                         // CLC
        E(0x60);                         // RTS
        void Branch(int at, int to) => code[at + 1] = (byte)(sbyte)(to - (at + 2));
        void Jsr(int at) { int addr = (codeAddr + tryAt) & 0xFFFF; code[at + 1] = (byte)addr; code[at + 2] = (byte)(addr >> 8); }
        Branch(bcs1, done); Branch(bcs2, done); Branch(beq1, no); Branch(beq2, empty);
        Jsr(jsr1); Jsr(jsr2);
        return code.ToArray();
    }
}
