namespace LufiaForge.Core.Lufia2;

/// <summary>
/// New Lufia II monsters (found 2026-10-27 by reading the game's code).
/// <para>
/// A monster id is a byte; the game reads the record table at $96:85C0 (u16 offsets from the table, the records right
/// behind it) in these places only: twelve LDA $96:85C0+k,X in $81:FB1A-$81:FC0A (drops, gold and EXP, the sprite and
/// palette), the record lookup $81:FCE2 (LDA #$96 / PHA / PLB / LDA $85C0,Y / ADC #$85C0), the battle loader $81:FC0B
/// (the same with the record's bank kept at +$47 for its "when hit" script) and the turn script's bank at $81:B1E3. The
/// fan hacks' Expansion patch (Frue, Spekkio, Kureji, Wrath of Foomy) changed exactly these to $D0:A000.
/// </para>
/// <para>
/// Bank $96 is full (the monsters fill $85C0-$CF68, the items the rest), so new monsters need another bank: the first
/// time one is added, the table, records and scripts ($96:85C0-$96:CF68) are copied to a bank of Lufia Forge's at the
/// same addresses, the table goes to $xx:8200 with room for 256 ids, and those places read the new bank. Every record
/// keeps its address in the bank, so scripts (their offsets count from the record) and Lufia Forge's edited scripts
/// (looked up by record address, <see cref="L2AiScripts"/>) stay as they were. The added space is in banks $C0 and up,
/// where $0000-$1FFF is ROM, not RAM: the battle loader reads the monster id ($09F2) and calls $81:F4D5 with the
/// record's bank as data bank, so it runs from a copy in the new bank that reads $7E:09F2 and calls $81:F4D5 with bank
/// $96 (as the game does). New records and their scripts go to the bank's free part ($xx:CF69 on, about 12 KB).
/// </para>
/// <para>
/// The fan hacks keep their monsters in bank $D0 already, with 256 ids (Frue and Foomy point E0-FF at one blank
/// record, Spekkio some): a new monster takes such a placeholder id (or the next id, Kureji) and its record goes to the
/// empty end of bank $D0 when there is one (Frue and Foomy: $D0:F7C1-$FFFF, 2 KB). Spekkio and Kureji fill the bank, so
/// they can't have more (refused, with the reason).
/// </para>
/// <para>
/// Id FF is not used: monster groups and story battles end their lists with FF.
/// </para>
/// </summary>
public static class L2NewMonsters
{
    public const int MaxId = 0xFE;
    private const string Magic = "LF2MN";
    private const int Version = 1;
    private const int CodeAt = 0x10, NewTable = 0x200;          // in the bank: header, loader copy, then the 256-entry table
    private const int OldTableAddr = 0x85C0, DataEnd = 0xCF69;   // the game's monster data in bank $96

    private static readonly int[] LongSites = { 0xFB27, 0xFB32, 0xFB3F, 0xFB81, 0xFB88, 0xFBCF, 0xFBD4, 0xFBE6, 0xFBED, 0xFBF4, 0xFBFB, 0xFC02 };
    private const int LookupBank = 0xFCEE, LookupTable = 0xFCF4, LookupAdc = 0xFCF8, TurnBank = 0xB1E4, LoaderHook = 0xFC18;
    private const int LoaderStart = 0xFC18, LoaderEnd = 0xFCE2;  // PHB ... PLB RTL
    private static readonly byte[] LookupOriginal = Hex("8b 5a c2 20 29 ff 00 0a a8 e2 20 a9 96 48 ab c2 20 b9 c0 85 18 69 c0 85 aa e2 20 7a ab 6b");
    private const int LookupStart = 0xFCE2;
    private static readonly byte[] TurnOriginal = Hex("8e 42 0a a9 96 80 08");

    private static byte[] Hex(string s) => s.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();

    /// <summary>The battle loader $81:FC18-$81:FCE1 as in the US game (and the place the hook goes).</summary>
    private static readonly byte[] LoaderOriginal = Hex(
        "8b a9 96 48 ab a0 47 00 97 b2 ad f2 09 a0 50 00 97 b2 c2 20 29 ff 00 0a a8 b9 c0 85 18 69 c0 85 aa a0 45 00 97 b2 " +
        "e2 20 a0 00 00 bd 00 00 97 b2 c8 e8 c0 0d 00 d0 f4 e8 bd 00 00 a0 0e 00 97 b2 e8 e8 e8 c2 20 a0 25 00 bd 00 00 97 " +
        "b2 e8 e8 c8 c8 c0 29 00 d0 f2 a0 78 00 bd 00 00 97 b2 e8 e8 c8 c8 c0 7c 00 d0 f2 c8 c8 bd 00 00 29 ff 00 0a 97 b2 " +
        "e8 c8 c8 c0 86 00 d0 ef e2 20 e8 e8 e8 e8 80 18 e8 c9 06 90 11 38 e9 06 0a c2 20 69 48 00 a8 bd 00 00 97 b2 e2 20 " +
        "e8 e8 7b bd 00 00 d0 e2 c2 20 a0 25 00 b7 b2 a0 11 00 97 b2 a0 51 00 97 b2 a0 27 00 b7 b2 a0 13 00 97 b2 a0 53 00 " +
        "97 b2 a6 b2 22 d5 f4 81 e2 20 ab 6b");

    /// <summary>The bank the monster records are in (the table's bank).</summary>
    public static int Bank(RomBuffer rom) => L2Rom.FileToSnes(L2Battle.MonsterTableAt(rom)) >> 16;

    /// <summary>File offset of Lufia Forge's monster bank, or -1.</summary>
    public static int Block(RomBuffer rom)
    {
        int table = L2Battle.MonsterTableAt(rom);
        int at = table / 0x8000 * 0x8000;
        return table - at == NewTable && rom.ReadAscii(at, Magic.Length) == Magic ? at : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Whether the table is the US game's own (bank $96, every place that reads it unchanged).</summary>
    private static bool IsGameLayout(RomBuffer rom)
    {
        bool Same(int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
        if (L2Battle.MonsterTableAt(rom) != L2Battle.MonsterTable) return false;
        foreach (int s in LongSites)
            if (rom.ReadByte(s) != 0xBF || rom.ReadByte(s + 3) != 0x96 || (rom.ReadUInt16Le(s + 1) - OldTableAddr) is < 0 or > 0x40) return false;
        if (!Same(LookupStart, LookupOriginal) || !Same(LoaderStart, LoaderOriginal)) return false;
        // the turn script's bank: the game's code, or Lufia Forge's script hook (which keeps the bank itself)
        return Same(TurnBank - 4, TurnOriginal) || L2AiScripts.IsInstalled(rom);
    }

    /// <summary>Whether a new monster can be added, and if not why (for the page's note and the button).</summary>
    public static bool CanAdd(RomBuffer rom, out string why)
    {
        why = "";
        int n = L2Battle.MonsterCountIn(rom);
        if (IsInstalled(rom) || (!IsGameLayout(rom) && Bank(rom) >= 0xC0))
        {
            if (NextId(rom) < 0) { why = "Every monster id (up to FE) is used."; return false; }
            if (!IsInstalled(rom) && FreeRun(rom).Length < 0x40)
            {
                why = $"This ROM's monster bank (${Bank(rom):X2}, set up by the hack it started from) is full, so there's no room for another monster record.";
                return false;
            }
            return true;
        }
        if (!IsGameLayout(rom))
        {
            why = "Another patch or hack changed how this ROM reads its monsters, so new ones can't be added safely.";
            return false;
        }
        return n <= MaxId;
    }

    /// <summary>The id a new monster gets: the next one past the table's monsters, or a hack's placeholder slot; -1 when
    /// none is free.</summary>
    public static int NextId(RomBuffer rom)
    {
        int n = L2Battle.MonsterCountIn(rom);
        if (n <= MaxId) return n;
        // a hack's placeholder slot (an id pointing at the blank record several ids share)
        var slot = L2Battle.Monsters(rom).FirstOrDefault(m => m.Unused && m.Index <= MaxId);
        return slot?.Index ?? -1;
    }

    /// <summary>The empty end of the monster bank: after the last byte any record or script uses, up to the bank's end
    /// (file offset and length).</summary>
    public static (int At, int Length) FreeRun(RomBuffer rom)
    {
        int table = L2Battle.MonsterTableAt(rom);
        int bankStart = table / 0x8000 * 0x8000, bankEnd = bankStart + 0x8000;
        int last = table + L2Battle.MonsterCountIn(rom) * 2 - 1;
        if (IsInstalled(rom)) last = Math.Max(last, bankStart + NewTable + 0x200 - 1);
        foreach (var m in L2Battle.Monsters(rom)) last = Math.Max(last, RecordEnd(rom, m.Offset) - 1);
        foreach (int b in L2AiScripts.MonsterScriptBytes(rom))
            if (b >= bankStart && b < bankEnd) last = Math.Max(last, b);
        int at = last + 1;
        for (int p = at; p < bankEnd; p++)
        {
            byte v = rom.ReadByte(p);
            if (v != 0 && v != 0xFF) at = p + 1;
        }
        return (at, Math.Max(0, bankEnd - at));
    }

    /// <summary>Where a record's tags end (after its 00).</summary>
    private static int RecordEnd(RomBuffer rom, int o)
    {
        int p = o + 0x21;
        for (int g = 0; g < 16 && rom.ReadByte(p) != 0; g++) p += 3;
        return p + 1;
    }

    /// <summary>Bytes still free for new monsters.</summary>
    public static int Room(RomBuffer rom) => IsInstalled(rom) || Bank(rom) >= 0xC0 ? FreeRun(rom).Length : DataEndRoom;
    private const int DataEndRoom = 0x10000 - DataEnd;

    // ── moving the game's monsters to a bank of their own ─────────────────────

    /// <summary>The loader's copy for a bank whose code starts at SNES <paramref name="code"/>: the game's
    /// $81:FC18-$81:FCE1 with the records' bank, the table, $7E:09F2 for the monster id and bank $96 for $81:F4D5.</summary>
    private static byte[] Loader(int bank, int tableAddr)
    {
        var b = LoaderOriginal.ToList();
        // from the end backwards, so the earlier offsets stay right
        int jsl = b.Count - 8;                                   // 22 d5 f4 81 / e2 20 / ab / 6b
        b.InsertRange(jsl, new byte[] { 0xF4, 0x96, 0x96, 0xAB, 0xAB });   // PEA $9696 / PLB / PLB (the data bank $81:F4D5 needs)
        b[0x1A] = (byte)tableAddr; b[0x1B] = (byte)(tableAddr >> 8);   // LDA $85C0,Y
        b[0x1E] = (byte)tableAddr; b[0x1F] = (byte)(tableAddr >> 8);   // ADC #$85C0
        b.RemoveRange(0x0A, 3);
        b.InsertRange(0x0A, new byte[] { 0xAF, 0xF2, 0x09, 0x7E });   // LDA $7E:09F2 (the bank's $09F2 is ROM)
        b[2] = (byte)bank;                                       // LDA #bank
        return b.ToArray();
    }

    /// <summary>Moves the game's monsters to a bank of Lufia Forge's (once; see the class). Returns the bank's file offset.</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!IsGameLayout(rom)) throw new InvalidOperationException("Another patch or hack changed how this ROM reads its monsters, so they can't be moved.");
        int oldTable = L2Battle.MonsterTableAt(rom), count = L2Battle.MonsterCountIn(rom);
        int oldBank = oldTable / 0x8000 * 0x8000;
        // everything the monsters use must be in $85C0-$CF68 (checked: the US game's are)
        int dataEnd = oldBank + (DataEnd - 0x8000);
        foreach (var m in L2Battle.Monsters(rom))
            if (m.Offset < oldTable || RecordEnd(rom, m.Offset) > dataEnd) throw new InvalidOperationException("A monster record lies outside the game's monster data, so the monsters can't be moved safely.");
        foreach (int b in L2AiScripts.MonsterScriptBytes(rom))
            if (b < oldTable || b >= dataEnd) throw new InvalidOperationException("A monster script lies outside the game's monster data, so the monsters can't be moved safely.");

        int block = L2Rom.Allocate(rom, 0x8000);
        if (block % 0x8000 != 0) throw new InvalidOperationException("No whole bank of added space was free for the monsters.");
        int bank = L2Rom.FileToSnes(block) >> 16;
        rom.WriteBytes(block, new byte[0x8000]);
        rom.WriteBytes(block + (OldTableAddr - 0x8000), rom.ReadBytes(oldTable, dataEnd - oldTable));
        // the new table at $xx:8200: the same records, offsets from the new table; ids past the game's are 0000 (none)
        int delta = OldTableAddr - (0x8000 + NewTable);
        var table = new byte[0x200];
        for (int i = 0; i < count; i++)
        {
            int e = rom.ReadUInt16Le(oldTable + i * 2) + delta;
            table[i * 2] = (byte)e; table[i * 2 + 1] = (byte)(e >> 8);
        }
        rom.WriteBytes(block + NewTable, table);
        var loader = Loader(bank, 0x8000 + NewTable);
        if (CodeAt + loader.Length > NewTable) throw new InvalidOperationException("The loader copy doesn't fit.");
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + Magic.Length, Version);
        rom.WriteBytes(block + CodeAt, loader);

        int newTable = bank << 16 | 0x8000 + NewTable;
        foreach (int s in LongSites)
        {
            int k = rom.ReadUInt16Le(s + 1) - OldTableAddr;
            rom.WriteBytes(s + 1, new[] { (byte)(newTable + k), (byte)(newTable + k >> 8), (byte)bank });
        }
        rom.WriteByte(LookupBank, (byte)bank);
        rom.WriteUInt16Le(LookupTable, (ushort)newTable);
        rom.WriteUInt16Le(LookupAdc, (ushort)newTable);
        if (rom.ReadByte(TurnBank - 1) == 0xA9 && rom.ReadByte(TurnBank) == 0x96) rom.WriteByte(TurnBank, (byte)bank);
        L2AiScripts.SetMonsterBank(rom, bank);
        int code = L2Rom.FileToSnes(block + CodeAt);
        rom.WriteBytes(LoaderHook, new byte[] { 0x5C, (byte)code, (byte)(code >> 8), (byte)(code >> 16) });
        return block;
    }

    // ── adding one ─────────────────────────────────────────────────────────────

    /// <summary>A new monster: a copy of monster <paramref name="from"/> (its record and its turn and "when hit" scripts as
    /// the game has them, laid out as they are around the record, so their jumps stay right), named
    /// <paramref name="name"/>. Returns the new id. Put it in monster groups and story battles (Encounters page).</summary>
    public static int Add(RomBuffer rom, int from, string? name = null)
    {
        if (!CanAdd(rom, out var why)) throw new InvalidOperationException(why);
        var src = L2Battle.ReadMonster(rom, from);
        // what to copy: the record and its scripts, as one piece (offsets from the record)
        int lo = src.Offset, hi = RecordEnd(rom, src.Offset) - 1;
        foreach (int at in new[] { src.AttackScript, src.DefenseScript })
        {
            if (at < 0) continue;
            foreach (var s in L2AiScripts.Parse(rom, at, src.Offset))
                for (int k = 0; k < s.Length; k++)
                {
                    int b = L2Rom.InBank(src.Offset, s.Offset + k);
                    if (b < 0) continue;
                    lo = Math.Min(lo, b); hi = Math.Max(hi, b);
                }
        }
        int span = hi - lo + 1;
        if (span > 0x800)
            throw new InvalidOperationException($"{src.Name}'s scripts are spread far from its record (another hack's layout), so it can't be copied; pick another monster to copy.");
        var piece = rom.ReadBytes(lo, span);
        int id = NextId(rom);
        if (id < 0) throw new InvalidOperationException("Every monster id (up to FE) is used.");

        if (Bank(rom) < 0xC0 || IsGameLayout(rom)) Install(rom);
        int table = L2Battle.MonsterTableAt(rom);
        var (free, room) = FreeRun(rom);
        // keep a byte of space after what is there (a script's last byte may be a 00 that looks free)
        int at0 = free + 1;
        if (span + 1 > room) throw new InvalidOperationException($"The monster bank has {Math.Max(0, room - 1)} bytes left; this copy needs {span}.");
        rom.WriteBytes(at0, piece);
        int record = at0 + (src.Offset - lo);
        int entry = L2Rom.OffsetInBank(table, record);
        if (L2Rom.FileToSnes(table) % 0x10000 + entry > 0xFFFF) throw new InvalidOperationException("The new record would sit before the table, where the game can't read it.");
        rom.WriteUInt16Le(table + id * 2, (ushort)entry);
        var m = L2Battle.ReadMonster(rom, id);
        m.Name = name ?? $"New {id:X2}";
        L2Battle.WriteMonster(rom, m);
        return id;
    }
}
