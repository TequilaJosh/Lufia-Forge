using System.IO;

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
/// empty end of bank $D0 when there is one (Frue and Foomy: $D0:F7C1-$FFFF, 2 KB). Spekkio and Kureji fill the bank.
/// </para>
/// <para>
/// Making room in a hack (2026-10-28, <see cref="PrepareRoom"/>): the hacks' Expansion patch reads bank $D0 in the same
/// places as the US game reads bank $96 (the twelve long reads, the record lookup $81:FCE2, the turn script's bank
/// $81:B1E3) plus its own copy of the battle loader at $81:FE00 (the game's $81:FC0B jumps there; it already reads the
/// monster id at $7E:09F2, and the hack's $81:F4D5 sets bank $96 itself). Bank $D0 also holds the monster attack names
/// ($D0:8000-, read by the hack's code at $85:F2D7 with bank $D0), so the monsters move instead: everything a record or
/// a battle script uses is copied to a bank of Lufia Forge's at the same addresses (every offset, wrapped or not, and
/// every edited script looked up by record address stays right), the table goes to $xx:8010 (records are read with
/// LDA table+k,X, so they have to sit after it), every other byte of the new bank is 00, and those places read the new
/// bank. Bank $D0 keeps the names and its old copy, which nothing reads any more. New monsters then go to the largest
/// free run of the new bank (the attack names' 3 KB, the old table, what the hack left empty), each laid out again
/// from its steps right after its record (<see cref="L2Tidy.LayOut"/>). The hack's data is tidied first
/// (<see cref="L2Tidy"/>), so the tidied scripts move along and a later tidy has nothing left to do.
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
        return (table - at == NewTable || table - at == HackTable) && at + 0x8000 <= rom.Length && rom.ReadAscii(at, Magic.Length) == Magic ? at : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Whether the monsters were moved out of a hack's bank (<see cref="MoveHackBank"/>).</summary>
    public static bool IsHackMoved(RomBuffer rom) => Block(rom) is var b && b >= 0 && rom.ReadByte(b + FromBankAt) != 0;

    // ── the fan hacks' layout ($D0:A000) ───────────────────────────────────────

    private const int HackTable = 0x10, FromBankAt = 6;               // in a moved hack's bank: header, then the table
    private const int HackJml = 0xFC0B, HackLoaderAt = 0xFE00;
    private static readonly byte[] HackJmlBytes = Hex("5c 00 fe 81"); // JML $81:FE00
    private static readonly byte[] HackLoaderPrefix = Hex("a9 7e 85 b4 a0 bd 00 7b 97 b2 88 10 fb");   // the game's $81:FC0B-$81:FC17

    /// <summary>The hacks' battle loader at $81:FE00: the game's $81:FC0B-$81:FCE1 reading bank <paramref name="bank"/>, the
    /// table at <paramref name="tableAddr"/> and the monster id at $7E:09F2 (their $81:F4D5 sets bank $96 itself).</summary>
    private static byte[] HackLoader(int bank, int tableAddr) => HackLoaderPrefix.Concat(Loader(bank, tableAddr, setBank96: false)).ToArray();

    /// <summary>Whether the ROM reads its monsters the way the fan hacks' Expansion patch does (Frue, Spekkio, Kureji,
    /// Wrath of Foomy: every place checked byte for byte), from bank <paramref name="bank"/>, table
    /// <paramref name="tableAddr"/> (also after <see cref="MoveHackBank"/>, which keeps that code).</summary>
    public static bool HackLayout(RomBuffer rom, out int bank, out int tableAddr)
    {
        bank = -1; tableAddr = 0;
        if (rom.Length < 0x10000 || rom.ReadByte(LongSites[0]) != 0xBF) return false;
        int b = rom.ReadByte(LongSites[0] + 3), t = rom.ReadUInt16Le(LongSites[0] + 1);
        if (b < 0xC0) return false;
        bool Same(int at, byte[] x) => at + x.Length <= rom.Length && rom.ReadBytes(at, x.Length).SequenceEqual(x);
        foreach (int s in LongSites)
            if (rom.ReadByte(s) != 0xBF || rom.ReadByte(s + 3) != b || (rom.ReadUInt16Le(s + 1) - t) is < 0 or > 0x40) return false;
        var lookup = (byte[])LookupOriginal.Clone();
        lookup[LookupBank - LookupStart] = (byte)b;
        lookup[LookupTable - LookupStart] = (byte)t; lookup[LookupTable - LookupStart + 1] = (byte)(t >> 8);
        lookup[LookupAdc - LookupStart] = (byte)t; lookup[LookupAdc - LookupStart + 1] = (byte)(t >> 8);
        if (!Same(LookupStart, lookup) || !Same(HackJml, HackJmlBytes) || !Same(HackLoaderAt, HackLoader(b, t))) return false;
        var turn = (byte[])TurnOriginal.Clone();
        turn[4] = (byte)b;
        if (!Same(TurnBank - 4, turn) && !L2AiScripts.IsInstalled(rom)) return false;
        bank = b; tableAddr = t;
        return true;
    }

    /// <summary>Whether a hack's monsters can be moved to a bank of their own (<see cref="PrepareRoom"/>), and if not why.</summary>
    public static bool CanMakeRoom(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) { why = $"The monsters already have a bank of their own (${Bank(rom):X2}): {Room(rom)} bytes free for new ones."; return false; }
        if (!HackLayout(rom, out _, out _))
        {
            why = IsGameLayout(rom) ? "The US game's monsters move to a bank of their own when the first new monster is added (Monsters page)."
                                    : "Another patch or hack changed how this ROM reads its monsters, so they can't be moved safely.";
            return false;
        }
        if (L2Rom.FreeSpace(rom) < 0x8000 + 0x20) { why = "The ROM has no 32 KB of added space left for the monsters' bank."; return false; }
        return true;
    }

    /// <summary>Whether adding a monster first needs <see cref="PrepareRoom"/> (a hack whose bank $D0 is full).</summary>
    public static bool NeedsRoom(RomBuffer rom) =>
        !IsInstalled(rom) && Bank(rom) >= 0xC0 && !IsGameLayout(rom) && NextId(rom) >= 0 && FreeRun(rom).Length < 0x40 && CanMakeRoom(rom, out _);

    /// <summary>Records and the bytes of their battle scripts (file offsets), as the game reads them; throws when a
    /// script can't be read (its bytes would not be known).</summary>
    private static HashSet<int> MonsterBytes(RomBuffer rom, bool strict)
    {
        var keep = new HashSet<int>();
        var seen = new HashSet<int>();
        for (int i = 0, n = L2Battle.MonsterCountIn(rom); i < n; i++)
        {
            int o = L2Battle.MonsterAt(rom, i);
            if (!seen.Add(o)) continue;
            int end = RecordEnd(rom, o);
            for (int p = o; p < end; p++) keep.Add(p);
            for (int p = o + 0x21; p < end - 1; p += 3)
            {
                if (rom.ReadByte(p) is not (7 or 8)) continue;
                int off = rom.ReadUInt16Le(p + 1);
                if (off == 0) continue;
                int start = L2Rom.InBank(o, off);
                List<L2AiScripts.Step> steps;
                try
                {
                    if (start < 0) throw new InvalidDataException("outside the ROM");
                    steps = L2AiScripts.Parse(rom, start, o);
                    if (strict && steps.Any(s => s.OutsideTargets.Any(t => t >= 0))) throw new InvalidDataException("a jump into the middle of a step");
                }
                catch (InvalidDataException e)
                {
                    if (strict) throw new InvalidOperationException($"Monster {i:X2}'s battle script can't be read ({e.Message}), so the bytes it uses aren't known.");
                    // (a script Lufia Forge can't read: keep what may be its bytes)
                    if (start >= 0) for (int k = 0; k < 0x100; k++) keep.Add(L2Rom.InBank(o, (off + k) & 0xFFFF));
                    continue;
                }
                foreach (var s in steps)
                    if (s.Offset >= 0)
                        for (int k = 0; k < s.Length; k++) keep.Add(L2Rom.InBank(o, (s.Offset + k) & 0xFFFF));
            }
        }
        return keep;
    }

    /// <summary>Moves a hack's monsters out of their bank (see the class): everything the records and their scripts use is
    /// copied to a bank of Lufia Forge's at the same addresses, the table to $xx:8010, every other byte of the bank 00,
    /// and the hack's code reads the new bank. Returns the bank's file offset. Use <see cref="PrepareRoom"/> to tidy
    /// first and check the result.</summary>
    public static int MoveHackBank(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!HackLayout(rom, out int oldBankNo, out int tableAddr)) throw new InvalidOperationException("This ROM doesn't read its monsters the way the fan hacks do, so they can't be moved safely.");
        int oldTable = L2Battle.MonsterTableAt(rom), count = L2Battle.MonsterCountIn(rom);
        int oldBank = oldTable / 0x8000 * 0x8000;
        var keep = MonsterBytes(rom, strict: true);
        int below = 0x8000 + HackTable + 0x200;   // the new header and table
        foreach (int p in keep)
        {
            if (p < oldBank || p >= oldBank + 0x8000) throw new InvalidOperationException("A monster record or script lies outside the monsters' bank, so they can't be moved safely.");
            if ((L2Rom.FileToSnes(p) & 0xFFFF) < below) throw new InvalidOperationException($"A monster record or script uses ${L2Rom.FileToSnes(p):X6}, where the moved table would go, so the monsters can't be moved safely.");
        }

        int block = L2Rom.Allocate(rom, 0x8000);
        if (block % 0x8000 != 0) throw new InvalidOperationException("No whole bank of added space was free for the monsters.");
        int bank = L2Rom.FileToSnes(block) >> 16;
        var data = new byte[0x8000];
        foreach (int p in keep) data[p - oldBank] = rom.ReadByte(p);
        System.Text.Encoding.ASCII.GetBytes(Magic).CopyTo(data, 0);
        data[Magic.Length] = (byte)Version;
        data[FromBankAt] = (byte)oldBankNo;
        // the table: the same records (same addresses in the bank), offsets from $xx:8010; ids past the hack's are 0000
        for (int i = 0; i < count; i++)
        {
            int e = (L2Rom.FileToSnes(L2Battle.MonsterAt(rom, i)) & 0xFFFF) - (0x8000 + HackTable);
            data[HackTable + i * 2] = (byte)e; data[HackTable + i * 2 + 1] = (byte)(e >> 8);
        }
        rom.WriteBytes(block, data);

        int newTable = bank << 16 | 0x8000 + HackTable;
        foreach (int s in LongSites)
        {
            int k = rom.ReadUInt16Le(s + 1) - tableAddr;
            rom.WriteBytes(s + 1, new[] { (byte)(newTable + k), (byte)(newTable + k >> 8), (byte)bank });
        }
        rom.WriteByte(LookupBank, (byte)bank);
        rom.WriteUInt16Le(LookupTable, (ushort)newTable);
        rom.WriteUInt16Le(LookupAdc, (ushort)newTable);
        if (rom.ReadByte(TurnBank - 1) == 0xA9 && rom.ReadByte(TurnBank) == oldBankNo) rom.WriteByte(TurnBank, (byte)bank);
        rom.WriteBytes(HackLoaderAt, HackLoader(bank, newTable & 0xFFFF));
        L2AiScripts.SetMonsterBank(rom, bank);
        return block;
    }

    /// <summary>The bytes <see cref="MoveHackBank"/> changes outside the new bank (file offsets).</summary>
    private static IEnumerable<int> HackSites()
    {
        foreach (int s in LongSites) for (int k = 1; k <= 3; k++) yield return s + k;
        yield return LookupBank; yield return LookupTable; yield return LookupTable + 1; yield return LookupAdc; yield return LookupAdc + 1;
        yield return TurnBank;
        for (int k = 0; k < HackLoaderPrefix.Length + LoaderOriginal.Length + 1; k++) yield return HackLoaderAt + k;
    }

    /// <summary>The free runs of a moved hack's bank (file offset, length): bytes after the table that are 00 and that no
    /// record or script uses, smallest first.</summary>
    public static List<(int At, int Length)> FreeRuns(RomBuffer rom)
    {
        var runs = new List<(int, int)>();
        int block = Block(rom);
        if (block < 0) return runs;
        var used = MonsterBytes(rom, strict: false);
        int from = L2Battle.MonsterTableAt(rom) + 0x200, end = block + 0x8000;
        for (int p = from; p < end;)
        {
            if (used.Contains(p) || rom.ReadByte(p) != 0) { p++; continue; }
            int q = p;
            while (q < end && !used.Contains(q) && rom.ReadByte(q) == 0) q++;
            runs.Add((p, q - p));
            p = q;
        }
        return runs.OrderBy(r => r.Item2).ToList();
    }

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
            if (IsHackMoved(rom) && FreeRuns(rom).Select(r => r.Length).DefaultIfEmpty(0).Max() < 0x40)
            {
                why = $"The monsters' bank (${Bank(rom):X2}) is full, so there's no room for another monster record.";
                return false;
            }
            if (!IsInstalled(rom) && FreeRun(rom).Length < 0x40)
            {
                why = CanMakeRoom(rom, out _)
                    ? $"This ROM's monster bank (${Bank(rom):X2}, set up by the hack it started from) is full. \"Add a new monster\" first moves the monsters to a bank of their own (Lufia II Settings, HACK DATA, \"Make room for new monsters\" does only that)."
                    : $"This ROM's monster bank (${Bank(rom):X2}, set up by the hack it started from) is full, so there's no room for another monster record.";
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
    public static int Room(RomBuffer rom) =>
        IsHackMoved(rom) ? FreeRuns(rom).Sum(r => Math.Max(0, r.Length - 2)) : IsInstalled(rom) || Bank(rom) >= 0xC0 ? FreeRun(rom).Length : DataEndRoom;
    private const int DataEndRoom = 0x10000 - DataEnd;

    // ── moving the game's monsters to a bank of their own ─────────────────────

    /// <summary>The loader's copy for a bank whose code starts at SNES <paramref name="code"/>: the game's
    /// $81:FC18-$81:FCE1 with the records' bank, the table, $7E:09F2 for the monster id and (<paramref name="setBank96"/>)
    /// bank $96 for $81:F4D5.</summary>
    private static byte[] Loader(int bank, int tableAddr, bool setBank96 = true)
    {
        var b = LoaderOriginal.ToList();
        // from the end backwards, so the earlier offsets stay right
        int jsl = b.Count - 8;                                   // 22 d5 f4 81 / e2 20 / ab / 6b
        if (setBank96) b.InsertRange(jsl, new byte[] { 0xF4, 0x96, 0x96, 0xAB, 0xAB });   // PEA $9696 / PLB / PLB (the data bank $81:F4D5 needs)
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
        if (IsHackMoved(rom)) return AddLaidOut(rom, from, name);
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
        if (span + 1 > room)
            throw new InvalidOperationException($"The monster bank has {Math.Max(0, room - 1)} bytes left; this copy needs {span}." +
                (CanMakeRoom(rom, out _) ? " Lufia II Settings, HACK DATA, \"Make room for new monsters\" moves the monsters to a bank of their own with more room." : ""));
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

    /// <summary>A new monster in a moved hack's bank: the record, then its turn and "when hit" scripts laid out again right
    /// after it (the steps a path reaches, <see cref="L2Tidy.LayOut"/>; a script both tags use is written once), in the
    /// smallest free run it fits. Both scripts are checked to run step by step as the original's before anything is
    /// written. Returns the new id.</summary>
    private static int AddLaidOut(RomBuffer rom, int from, string? name)
    {
        var src = L2Battle.ReadMonster(rom, from);
        int id = NextId(rom);
        if (id < 0) throw new InvalidOperationException("Every monster id (up to FE) is used.");
        int recLen = RecordEnd(rom, src.Offset) - src.Offset;
        var tags = new List<(int Pos, int Start)>();   // (tag's place in the record, the script's start)
        for (int p = src.Offset + 0x21; p < src.Offset + recLen - 1; p += 3)
            if (rom.ReadByte(p) is 7 or 8 && rom.ReadUInt16Le(p + 1) != 0) tags.Add((p - src.Offset, L2Rom.InBank(src.Offset, rom.ReadUInt16Le(p + 1))));
        var scripts = new Dictionary<int, List<L2AiScripts.Step>>();
        foreach (var (_, start) in tags)
        {
            if (scripts.ContainsKey(start)) continue;
            List<L2AiScripts.Step> steps;
            try { steps = start < 0 ? new() : L2Tidy.Reachable(L2AiScripts.Parse(rom, start, src.Offset)); }
            catch (InvalidDataException e) { throw new InvalidOperationException($"{src.Name}'s battle script can't be read ({e.Message}), so it can't be copied; pick another monster to copy."); }
            if (steps.Count == 0 || steps.Any(s => s.OutsideTargets.Any(t => t >= 0)))
                throw new InvalidOperationException($"A \"go to\" in {src.Name}'s battle script jumps into the middle of a step, so it can't be copied; pick another monster to copy.");
            scripts[start] = steps;
        }

        // lays the copy out from run start "at" on a copy of the ROM; returns (record, end)
        (RomBuffer W, int Rec, int End) Lay(int runAt, int runEnd)
        {
            var w = new RomBuffer(rom.ToArray(), rom.FilePath);
            int rec = runAt + 1, at = rec + recLen;
            if (at > runEnd) throw new InvalidOperationException("no room");
            w.WriteBytes(rec, rom.ReadBytes(src.Offset, recLen));
            var pieces = new Dictionary<string, int>();
            var placed = new Dictionary<int, int>();
            foreach (var (pos, start) in tags)
            {
                if (!placed.TryGetValue(start, out int s)) placed[start] = s = L2Tidy.LayOut(w, scripts[start], rec, ref at, runEnd, pieces);
                w.WriteUInt16Le(rec + pos + 1, (ushort)L2Rom.OffsetInBank(rec, s));
            }
            return (w, rec, at);
        }

        var runs = FreeRuns(rom);
        if (runs.Count == 0) throw new InvalidOperationException($"The monsters' bank (${Bank(rom):X2}) is full.");
        var big = runs[^1];
        int need;
        try { var t = Lay(big.At, big.At + big.Length - 1); need = t.End - big.At + 1; }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException($"The monsters' bank (${Bank(rom):X2}) has {Math.Max(0, big.Length - 2)} bytes free in one piece; a copy of {src.Name} needs more.");
        }
        var run = runs.First(r => r.Length >= need);
        var (w2, record, end) = Lay(run.At, run.At + run.Length - 1);
        // the copy's scripts run as the original's, step by step
        foreach (var (pos, start) in tags)
        {
            int ns = L2Rom.InBank(record, w2.ReadUInt16Le(record + pos + 1));
            string where = "it can't be read";
            if (start < 0 || !L2Tidy.SameRun(rom, src.Offset, start, w2, record, ns, out _, out where))
                throw new InvalidOperationException($"The copy of {src.Name}'s battle script wouldn't run the same ({where}), so nothing was added.");
        }
        rom.WriteBytes(record, w2.ReadBytes(record, end - record));
        int table = L2Battle.MonsterTableAt(rom);
        int entry = L2Rom.OffsetInBank(table, record);
        if (L2Rom.FileToSnes(table) % 0x10000 + entry > 0xFFFF) throw new InvalidOperationException("The new record would sit before the table, where the game can't read it.");
        rom.WriteUInt16Le(table + id * 2, (ushort)entry);
        var m = L2Battle.ReadMonster(rom, id);
        m.Name = name ?? $"New {id:X2}";
        L2Battle.WriteMonster(rom, m);
        return id;
    }

    // ── making room in a hack ──────────────────────────────────────────────────

    public sealed class RoomResult
    {
        /// <summary>The ROM with room made, or null (<see cref="Refused"/>).</summary>
        public byte[]? Bytes { get; set; }
        public string? Refused { get; set; }
        public List<string> Moves { get; } = new();
        public List<string> Gains { get; } = new();
        public List<string> Checked { get; } = new();
        public bool Tidied { get; set; }
        public int FromBank { get; set; }
        public int ToBank { get; set; }
        public int RoomBefore { get; set; }
        public int RoomAfter { get; set; }

        public string Preview()
        {
            if (Refused != null) return Refused;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("What moves:");
            foreach (var l in Moves) sb.AppendLine("- " + l);
            if (Gains.Count > 0) { sb.AppendLine(); sb.AppendLine("What it makes possible:"); foreach (var l in Gains) sb.AppendLine("- " + l); }
            sb.AppendLine(); sb.AppendLine("Checked on the new ROM before anything is written:");
            foreach (var l in Checked) sb.AppendLine("- " + l);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Builds (in memory) a hack ROM whose monsters have a bank of their own: the hack's data tidied first when
    /// there is anything to tidy (<see cref="L2Tidy.Prepare"/>, with its own checks), then <see cref="MoveHackBank"/>.
    /// The result is proven to read the same as before the move (every monster at its new place: name, stats, drop, the
    /// record's bytes, both scripts step by step; every capsule form, item, spell, IP attack and effect, description and
    /// shop), and no byte outside the new bank, the code places it changes, Lufia Forge's script hook and the added
    /// space's mark differs. Nothing in <paramref name="rom"/> changes.</summary>
    public static RoomResult PrepareRoom(RomBuffer rom)
    {
        var res = new RoomResult();
        if (!rom.IsLufia2) { res.Refused = "This isn't a Lufia II ROM."; return res; }
        if (!CanMakeRoom(rom, out var why)) { res.Refused = why; return res; }
        try
        {
            res.FromBank = Bank(rom);
            res.RoomBefore = FreeRun(rom).Length;
            var tidy = L2Tidy.Prepare(rom);
            if (tidy.Refused != null) { res.Refused = "No room made: the hack's data has to be tidied first, and it can't be. " + tidy.Refused; return res; }
            byte[] mid = tidy.Tidied ?? rom.ToArray();
            if (tidy.Tidied != null)
            {
                res.Tidied = true;
                res.Moves.Add("First the hack's data is tidied (the same as \"Tidy the hack's data\"), so the scripts move along and a later tidy has nothing to do:");
                res.Moves.AddRange(tidy.Moves.Select(l => "  " + l));
                res.Gains.AddRange(tidy.Gains);
                res.Checked.AddRange(tidy.Checked.Select(l => "tidy: " + l));
            }
            var before = new RomBuffer((byte[])mid.Clone(), rom.FilePath);
            var w = new RomBuffer((byte[])mid.Clone(), rom.FilePath);
            foreach (var p in rom.Protected) w.Protected.Add(p);
            int oldBank = L2Battle.MonsterTableAt(before) / 0x8000 * 0x8000;
            int block = MoveHackBank(w);
            res.ToBank = L2Rom.FileToSnes(block) >> 16;
            int Map(int p) => p >= oldBank && p < oldBank + 0x8000 ? block + (p - oldBank) : p;
            var check = new L2Tidy.Result();
            L2Tidy.Prove(before, w, check, Map);

            // every byte that changed is the new bank, a code place, the script hook or the added space's mark
            var allowed = new HashSet<int>(HackSites());
            if (L2AiScripts.BlockRange(before) is { } ai) for (int k = 0; k < ai.Length; k++) allowed.Add(ai.At + k);
            var after = w.ToArray();
            bool Mark(byte[] d, int p) => p + 4 <= d.Length && d[p] == 'L' && d[p + 1] == 'F' && d[p + 2] == '2' && d[p + 3] == 'X';
            for (int i = 0; i < Math.Min(mid.Length, after.Length); i++)
            {
                if (mid[i] == after[i] || i >= block && i < block + 0x8000 || allowed.Contains(i)) continue;
                int m16 = i & ~15;
                if (i >= L2Rom.OriginalSize && (Mark(mid, m16) || Mark(after, m16))) continue;
                throw new L2Tidy.Mismatch($"a byte at 0x{i:X6} that the move shouldn't change");
            }
            if (after.Length != mid.Length) throw new L2Tidy.Mismatch("the ROM's size");

            int count = L2Battle.MonsterCountIn(w);
            res.RoomAfter = Room(w);
            res.Moves.Add($"The monsters (the table, {count} entries, every record and every battle script they run) are copied from bank ${res.FromBank:X2} to a bank " +
                          $"of their own, ${res.ToBank:X2}, at the same addresses, so every script offset and every edited script still finds its record. " +
                          $"The table goes to ${res.ToBank:X2}:8010; the game's 16 places that read the monsters, and the hack's battle loader, read the new bank. " +
                          $"Every other byte of ${res.ToBank:X2} is 00. Bank ${res.FromBank:X2} keeps the monster attack names (the hack reads them there) and its old copy, which nothing reads any more.");
            res.Gains.Add($"Room for new monsters: {res.RoomAfter} bytes in ${res.ToBank:X2} (was {res.RoomBefore} in ${res.FromBank:X2}). A copy takes its record (about 45 bytes) and its battle scripts.");
            res.Checked.AddRange(check.Checked);
            res.Checked.Add($"every byte that changed is in the new bank ${res.ToBank:X2}, one of the code places that read the monsters, Lufia Forge's script hook or the added space's mark");
            res.Bytes = after;
        }
        catch (L2Tidy.Mismatch m) { res.Refused = $"No room made: on the new ROM {m.Message} would not be the same, so nothing was changed."; res.Bytes = null; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { res.Refused = "No room made: " + e.Message; res.Bytes = null; }
        return res;
    }

    /// <summary>Puts a prepared result in the ROM (the caller records it as one Undo step).</summary>
    public static void ApplyRoom(RomBuffer rom, RoomResult r)
    {
        if (r.Bytes == null) throw new InvalidOperationException(r.Refused ?? "No room to make.");
        rom.ReplaceAll(r.Bytes);
    }
}
