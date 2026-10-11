using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Room for 128 spells (the game has 56, 00-37). Spell records live in bank 1F and are found through 56 16-bit
/// pointers at $1F:DB00, read only by $08:FABD (A8 X16, DB = 1F, A = spell; returns X = the record). That table
/// can't grow (other data follows it), so the lookup is hooked:
/// <code>
/// $08:FABD  08 22 xx xx xx 28 60  PHP / JSL lookup / PLP / RTS
/// </code>
/// The new lookup reads spells 00-37 from the game's table as before. For 38-7F it reads a 3-byte pointer from its
/// own table and also sets DB to the record's bank: the three callers ($08:FA2D, $08:FA66, $08:FA7D) read the record
/// with DB-relative loads and restore DB themselves (PHB at entry, PLB at exit). A record's +15 word (the spell's
/// script, relative to $1F:DB00) is read by the caller from bank 1F, so new spells copied from an existing one share
/// its animation and effect script.
///
/// Block (one bank, in the added ROM space): the lookup, then "LFSP", the number of new spells, their record
/// lengths, the pointer table (unused entries point at spell 00) and 72 record slots of 32 bytes.
/// </summary>
public static class SpellTablePatch
{
    public const int FirstNew = 0x38, MaxSpells = 0x80, NewSlots = MaxSpells - FirstNew, SlotSize = 32;
    public const int LookupSite = 0x47ABD;   // $08:FABD
    public const int Trick = 0x1A;
    public const string TricksterName = "Trickstr";
    private static readonly byte[] LookupOriginal = Convert.FromHexString("08EBA900EBC2200AAABD00DB186900DBAA2860");
    private const int Magic = 64, Count = 68, Lengths = 72, Table = Lengths + NewSlots, Records = Table + NewSlots * 3;
    public const int BlockSize = Records + NewSlots * SlotSize;

    private static readonly byte[] Routine =
    {
        0xC9, 0x38,                //  0 CMP #$38
        0xB0, 0x11,                //  2 BCS new
        0xEB, 0xA9, 0x00, 0xEB,    //  4 XBA / LDA #0 / XBA
        0xC2, 0x20,                //  8 REP #$20
        0x0A, 0xAA,                // 10 ASL / TAX
        0xBD, 0x00, 0xDB,          // 12 LDA $DB00,X        the game's table
        0x18, 0x69, 0x00, 0xDB,    // 15 CLC / ADC #$DB00
        0xAA,                      // 19 TAX
        0x6B,                      // 20 RTL
        0xC9, 0x80,                // 21 new: CMP #$80
        0x90, 0x02,                // 23 BCC ok
        0xA9, 0x38,                // 25 LDA #$38           (out of range: the first new entry)
        0x38, 0xE9, 0x38,          // 27 ok: SEC / SBC #$38
        0xC2, 0x20,                // 30 REP #$20
        0x29, 0xFF, 0x00,          // 32 AND #$00FF
        0x48,                      // 35 PHA
        0x0A,                      // 36 ASL
        0x18, 0x63, 0x01,          // 37 CLC / ADC 1,S      x 3
        0xAA,                      // 40 TAX
        0x68,                      // 41 PLA
        0xE2, 0x20,                // 42 SEP #$20
        0xBF, 0x00, 0x00, 0x00,    // 44 LDA table+2,X      the record's bank
        0x48,                      // 48 PHA
        0xAB,                      // 49 PLB
        0xC2, 0x20,                // 50 REP #$20
        0xBF, 0x00, 0x00, 0x00,    // 52 LDA table,X
        0xAA,                      // 56 TAX
        0x6B,                      // 57 RTL
    };

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    public static bool IsOriginal(RomBuffer rom) => rom.ReadBytes(LookupSite, LookupOriginal.Length).SequenceEqual(LookupOriginal) || OldTrickster(rom) > 0;
    public static bool IsApplied(RomBuffer rom) => BlockOffset(rom) > 0;

    /// <summary>
    /// The first version (a 0.4.0-beta.7 test build): only spell 38 Trickstr, its record at $1F:D040 and a 39-byte
    /// lookup whose new-spell branch starts SBC #$38. Returns that lookup's file offset, or 0. Add updates it.
    /// </summary>
    public static int OldTrickster(RomBuffer rom)
    {
        if (rom.ReadByte(LookupSite) != 0x08 || rom.ReadByte(LookupSite + 1) != 0x22) return 0;
        int r = FileOffset(rom.ReadByte(LookupSite + 2) | rom.ReadByte(LookupSite + 3) << 8 | rom.ReadByte(LookupSite + 4) << 16);
        return r > 0 && r + 39 <= rom.Length && rom.ReadByte(r) == 0xC9 && rom.ReadByte(r + 1) == FirstNew &&
               rom.ReadByte(r + 21) == 0xE9 && rom.ReadByte(r + 22) == 0x38 ? r : 0;
    }
    private const int OldRecord = 0xFD040, OldRecordLength = 27;
    public static bool NeedsUpdate(RomBuffer rom) => OldTrickster(rom) > 0;

    /// <summary>File offset of the patch's block, or 0.</summary>
    public static int BlockOffset(RomBuffer rom)
    {
        if (rom.ReadByte(LookupSite) != 0x08 || rom.ReadByte(LookupSite + 1) != 0x22) return 0;
        int r = FileOffset(rom.ReadByte(LookupSite + 2) | rom.ReadByte(LookupSite + 3) << 8 | rom.ReadByte(LookupSite + 4) << 16);
        return r > 0 && r + BlockSize <= rom.Length && rom.ReadByte(r) == 0xC9 && rom.ReadByte(r + 1) == FirstNew &&
               rom.ReadByte(r + Magic) == 'L' && rom.ReadByte(r + Magic + 3) == 'P' ? r : 0;
    }

    /// <summary>How many spells were added (0-72).</summary>
    public static int NewCount(RomBuffer rom) { int b = BlockOffset(rom); return b == 0 ? (OldTrickster(rom) > 0 ? 1 : 0) : Math.Min((int)rom.ReadByte(b + Count), NewSlots); }

    /// <summary>How many spells the ROM has: 56, plus the ones added with this patch.</summary>
    public static int SpellCount(RomBuffer rom) => FirstNew + NewCount(rom);

    /// <summary>File offset of spell <paramref name="id"/>'s record.</summary>
    public static int SpellOffset(RomBuffer rom, int id)
    {
        if (id < FirstNew) return GameDataSpells.Offsets[id];
        int b = BlockOffset(rom);
        if (b == 0 && id == FirstNew && OldTrickster(rom) > 0) return OldRecord;
        return b == 0 ? GameDataSpells.Offsets[0] : b + Records + (id - FirstNew) * SlotSize;
    }

    /// <summary>Size of spell <paramref name="id"/>'s record (the last of the game's own is taken as 22).</summary>
    public static int SpellLength(RomBuffer rom, int id)
    {
        if (id >= FirstNew)
        {
            int b = BlockOffset(rom);
            if (b == 0) return id == FirstNew && OldTrickster(rom) > 0 ? OldRecordLength : 0;
            return rom.ReadByte(b + Lengths + id - FirstNew);
        }
        return id + 1 < FirstNew ? GameDataSpells.Offsets[id + 1] - GameDataSpells.Offsets[id] : 22;
    }

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's spell lookup ($08:FABD) isn't the original, so the spell table can't be enlarged safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        // the first version: keep Trickstr as spell 38 (characters and monsters keep it), drop its lookup and record
        byte[]? trick = null;
        int old = OldTrickster(rom);
        if (old > 0)
        {
            trick = rom.ReadBytes(OldRecord, OldRecordLength);
            rom.WriteBytes(OldRecord, Enumerable.Repeat((byte)0xFF, OldRecordLength).ToArray());
            if (space.At(old) is { } e && e.Start == old) space.Free(old);
            rom.WriteBytes(LookupSite, LookupOriginal);
        }
        WriteAt(rom, space.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 8));
        space.Save();
        if (trick != null)
        {
            int b = BlockOffset(rom), at = b + Records;
            rom.WriteBytes(at, trick);
            rom.WriteByte(b + Lengths, OldRecordLength);
            int a = SnesAddress(at);
            rom.WriteBytes(b + Table, new[] { (byte)a, (byte)(a >> 8), (byte)(a >> 16) });
            rom.WriteByte(b + Count, 1);
        }
        rom.FixChecksum();
    }

    /// <summary>Writes an empty block (no new spells) at <paramref name="at"/> and hooks the game's lookup.</summary>
    public static void WriteAt(RomBuffer rom, int at)
    {
        var block = new byte[BlockSize];
        Routine.CopyTo(block, 0);
        int table = SnesAddress(at) + Table;
        void Long(int o, int a) { block[o] = (byte)a; block[o + 1] = (byte)(a >> 8); block[o + 2] = (byte)(a >> 16); }
        Long(45, table + 2);
        Long(53, table);
        "LFSP"u8.ToArray().CopyTo(block, Magic);
        for (int i = 0; i < NewSlots; i++) Long(Table + i * 3, 0x1F0000 | (GameDataSpells.Offsets[0] & 0x7FFF | 0x8000));   // spell 00
        rom.WriteBytes(at, block);
        int r = SnesAddress(at);
        var hook = Enumerable.Repeat((byte)0xEA, LookupOriginal.Length).ToArray();
        new byte[] { 0x08, 0x22, (byte)r, (byte)(r >> 8), (byte)(r >> 16), 0x28, 0x60 }.CopyTo(hook, 0);
        rom.WriteBytes(LookupSite, hook);
    }

    /// <summary>Adds a spell copied from <paramref name="template"/> (its effect, animation and numbers) and names it.
    /// Returns its id.</summary>
    public static int AddSpell(RomBuffer rom, int template, string name)
    {
        int b = BlockOffset(rom);
        if (b == 0) throw new InvalidOperationException("Add the \"Up to 128 spells\" patch first.");
        int n = NewCount(rom);
        if (n >= NewSlots) throw new InvalidOperationException($"The ROM already has {MaxSpells} spells.");
        int len = SpellLength(rom, template);
        if (len < 20 || len > SlotSize) throw new InvalidOperationException($"Spell {template:X2}'s record can't be copied.");
        var rec = rom.ReadBytes(SpellOffset(rom, template), len);
        var nm = (name ?? "").PadRight(8).Substring(0, 8);
        for (int i = 0; i < 8; i++) rec[i] = (byte)(nm[i] == ' ' ? 0x20 : nm[i]);
        int at = b + Records + n * SlotSize;
        rom.WriteBytes(at, rec);
        rom.WriteByte(b + Lengths + n, (byte)len);
        int a = SnesAddress(at);
        rom.WriteBytes(b + Table + n * 3, new[] { (byte)a, (byte)(a >> 8), (byte)(a >> 16) });
        rom.WriteByte(b + Count, (byte)(n + 1));
        return FirstNew + n;
    }

    /// <summary>Takes out the last added spell, and every character's and monster's use of it.</summary>
    public static void RemoveLastSpell(RomBuffer rom)
    {
        int b = BlockOffset(rom), n = NewCount(rom);
        if (b == 0 || n == 0) return;
        int id = FirstNew + n - 1;
        ForgetSpells(rom, s => s == id);
        int sp0 = 0x1F0000 | (GameDataSpells.Offsets[0] & 0x7FFF | 0x8000);
        rom.WriteBytes(b + Table + (n - 1) * 3, new[] { (byte)sp0, (byte)(sp0 >> 8), (byte)(sp0 >> 16) });
        rom.WriteBytes(b + Records + (n - 1) * SlotSize, new byte[SlotSize]);
        rom.WriteByte(b + Lengths + n - 1, 0);
        rom.WriteByte(b + Count, (byte)(n - 1));
    }

    /// <summary>The id of an added spell with this name, or -1.</summary>
    public static int Find(RomBuffer rom, string name)
    {
        for (int id = FirstNew; id < SpellCount(rom); id++)
            if (new string(rom.ReadBytes(SpellOffset(rom, id), 8).Select(c => (char)c).ToArray()).TrimEnd() == name) return id;
        return -1;
    }

    /// <summary>Trickstr: Trick (raises ATP) on the whole party, 9 MP. Added once; returns its id.</summary>
    public static int AddTrickster(RomBuffer rom)
    {
        int id = Find(rom, TricksterName);
        if (id >= 0) return id;
        id = AddSpell(rom, Trick, TricksterName);
        int at = SpellOffset(rom, id);
        rom.WriteByte(at + 10, 0x01);   // target: all of the party (Trick: one member)
        rom.WriteByte(at + 11, 9);      // MP (Trick 5)
        return id;
    }

    public static void Remove(RomBuffer rom)
    {
        int b = BlockOffset(rom);
        if (b == 0) return;
        ForgetSpells(rom, s => s >= FirstNew);
        rom.WriteBytes(LookupSite, LookupOriginal);
        var space = ExpansionSpace.Load(rom);
        space.Free(b);
        space.Save();
        rom.FixChecksum();
    }

    /// <summary>Nobody may keep learning or casting a spell that no longer exists: spell lists (with the Guy /
    /// Aguro patch), the prologue party's spells, and monsters' "cast spell" steps (which become "attack").</summary>
    private static void ForgetSpells(RomBuffer rom, Func<int, bool> gone)
    {
        if (AguroMagicPatch.IsApplied(rom))
        {
            var lists = AguroMagicPatch.ReadLists(rom);
            if (lists.Sum(l => l.RemoveAll(e => gone(e.Spell))) > 0) AguroMagicPatch.WriteLists(rom, lists);
        }
        try
        {
            var ev = EventScript.Load(rom, 0x93, 2);   // the prologue party's spells
            if (ev != null && ev.Ops.RemoveAll(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x3D && gone(o.Bytes[2])) > 0) ev.Save(true);
        }
        catch (Exception) { }
        foreach (var (record, cmd) in MonsterAi.AllCommands(rom))
            if (cmd.Op == 0x07 && gone(cmd.Args[0])) { rom.WriteByte(record + cmd.Offset, 0x05); rom.WriteByte(record + cmd.Offset + 1, 0x00); }
    }
}

/// <summary>The game's 56 spell records (bank 1F, variable length); the Game Data editors use the same list.</summary>
internal static class GameDataSpells
{
    public static readonly int[] Offsets =
    {
        0xFE354, 0xFE368, 0xFE382, 0xFE39C, 0xFE3B6, 0xFE3D0, 0xFE3EA, 0xFE404,
        0xFE41E, 0xFE438, 0xFE452, 0xFE46C, 0xFE486, 0xFE4A0, 0xFE4BA, 0xFE4D4,
        0xFE4EE, 0xFE506, 0xFE51E, 0xFE536, 0xFE54E, 0xFE567, 0xFE580, 0xFE598,
        0xFE5B0, 0xFE5C7, 0xFE5E2, 0xFE5FD, 0xFE615, 0xFE630, 0xFE64B, 0xFE666,
        0xFE681, 0xFE699, 0xFE6B1, 0xFE6CA, 0xFE6E3, 0xFE6FC, 0xFE715, 0xFE72E,
        0xFE746, 0xFE75E, 0xFE77A, 0xFE796, 0xFE7AE, 0xFE7C6, 0xFE7DD, 0xFE7F4,
        0xFE80B, 0xFE822, 0xFE83B, 0xFE853, 0xFE86B, 0xFE885, 0xFE89F, 0xFE8B9,
    };
}
