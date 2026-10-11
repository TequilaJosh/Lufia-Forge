using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// A new spell, 38 "Trickstr" (spell names are 8 letters): Trick (raises ATP) cast on the whole party.
/// Spell records live in bank 1F and are found through 56 16-bit pointers at $1F:DB00 (only read by $08:FABD); the
/// table can't grow (other data follows it), so the lookup is hooked:
/// <code>
/// $08:FABD  08 22 xx xx xx 28 60  PHP / JSL lookup / PLP / RTS   (A8 X16, DB = 1F, A = spell)
/// </code>
/// The new lookup reads spells below 38 from the game's table as before and spell 38 from its own one. The record
/// itself goes in bank 1F at $1F:D040, unused filler (FF) the game never reads.
/// </summary>
public static class TricksterPatch
{
    public const int Id = 0x38, Trick = 0x1A;
    public const int LookupSite = 0x47ABD;   // $08:FABD
    public const int RecordAt = 0xFD040;     // $1F:D040
    public const int RecordLength = 27;      // Trick's record size
    public const string Name = "Trickstr";
    private static readonly byte[] LookupOriginal = Convert.FromHexString("08EBA900EBC2200AAABD00DB186900DBAA2860");

    private static readonly byte[] Routine =
    {
        0xC9, 0x38,                //  0 CMP #$38
        0xB0, 0x11,                //  2 BCS new
        0xEB, 0xA9, 0x00, 0xEB,    //  4 XBA / LDA #0 / XBA
        0xC2, 0x20,                //  8 REP #$20
        0x0A, 0xAA,                // 10 ASL / TAX
        0xBD, 0x00, 0xDB,          // 12 LDA $DB00,X      the game's table
        0x18, 0x69, 0x00, 0xDB,    // 15 CLC / ADC #$DB00
        0xAA,                      // 19 TAX
        0x6B,                      // 20 RTL
        0xE9, 0x38,                // 21 new: SBC #$38
        0xEB, 0xA9, 0x00, 0xEB,    // 23 XBA / LDA #0 / XBA
        0xC2, 0x20,                // 27 REP #$20
        0x0A, 0xAA,                // 29 ASL / TAX
        0xBF, 0x00, 0x00, 0x00,    // 31 LDA table,X      (fixed up)
        0xAA,                      // 35 TAX
        0x6B,                      // 36 RTL
        0x40, 0xD0,                // 37 table: $D040
    };
    private const int TableOperand = 32, Table = 37;

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    public static bool IsOriginal(RomBuffer rom) =>
        rom.ReadBytes(LookupSite, LookupOriginal.Length).SequenceEqual(LookupOriginal) &&
        rom.ReadBytes(RecordAt, RecordLength).All(b => b == 0xFF);

    public static bool IsApplied(RomBuffer rom) => RoutineOffset(rom) > 0;

    public static int RoutineOffset(RomBuffer rom)
    {
        if (rom.ReadByte(LookupSite) != 0x08 || rom.ReadByte(LookupSite + 1) != 0x22) return 0;
        int r = FileOffset(rom.ReadByte(LookupSite + 2) | rom.ReadByte(LookupSite + 3) << 8 | rom.ReadByte(LookupSite + 4) << 16);
        return r > 0 && r + Routine.Length <= rom.Length && rom.ReadByte(r) == 0xC9 && rom.ReadByte(r + 1) == Id ? r : 0;
    }

    /// <summary>How many spells the ROM has (56, or 57 with this patch).</summary>
    public static int SpellCount(RomBuffer rom) => IsApplied(rom) ? Id + 1 : Id;

    /// <summary>File offset of spell <paramref name="id"/>'s record.</summary>
    public static int SpellOffset(RomBuffer rom, int id) => id == Id ? RecordAt : GameDataSpells.Offsets[id];

    /// <summary>Size of spell <paramref name="id"/>'s record (the last of the game's own is taken as 22).</summary>
    public static int SpellLength(int id) => id == Id ? RecordLength : id + 1 < Id ? GameDataSpells.Offsets[id + 1] - GameDataSpells.Offsets[id] : 22;

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's spell lookup ($08:FABD) or the free space at $1F:D040 isn't the original, so Trickster can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        WriteAt(rom, space.AllocateInOneBank(Routine.Length, ExpansionSpace.Kind.Patch, 8));
        space.Save();
        rom.FixChecksum();
    }

    /// <summary>Writes the lookup at <paramref name="at"/>, the new spell's record, and hooks the game's lookup.</summary>
    public static void WriteAt(RomBuffer rom, int at)
    {
        var code = (byte[])Routine.Clone();
        int table = SnesAddress(at) + Table;
        code[TableOperand] = (byte)table; code[TableOperand + 1] = (byte)(table >> 8); code[TableOperand + 2] = (byte)(table >> 16);
        rom.WriteBytes(at, code);

        var rec = rom.ReadBytes(GameDataSpells.Offsets[Trick], RecordLength);
        for (int i = 0; i < 8; i++) rec[i] = (byte)Name[i];
        rec[10] = 0x01;   // target: all of the party (Trick: one member)
        rec[11] = 9;      // MP (Trick 5)
        rom.WriteBytes(RecordAt, rec);

        int r = SnesAddress(at);
        var hook = Enumerable.Repeat((byte)0xEA, LookupOriginal.Length).ToArray();
        new byte[] { 0x08, 0x22, (byte)r, (byte)(r >> 8), (byte)(r >> 16), 0x28, 0x60 }.CopyTo(hook, 0);
        rom.WriteBytes(LookupSite, hook);
    }

    public static void Remove(RomBuffer rom)
    {
        int r = RoutineOffset(rom);
        if (r == 0) return;
        // nobody may keep learning a spell that no longer exists
        if (AguroMagicPatch.IsApplied(rom))
        {
            var lists = AguroMagicPatch.ReadLists(rom);
            if (lists.Any(l => l.RemoveAll(e => e.Spell == Id) > 0)) AguroMagicPatch.WriteLists(rom, lists);
        }
        try
        {
            var ev = EventScript.Load(rom, 0x93, 2);   // the prologue party's spells
            if (ev != null && ev.Ops.RemoveAll(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x3D && o.Bytes[2] == Id) > 0) ev.Save(true);
        }
        catch (Exception) { }
        rom.WriteBytes(LookupSite, LookupOriginal);
        rom.WriteBytes(RecordAt, Enumerable.Repeat((byte)0xFF, RecordLength).ToArray());
        var space = ExpansionSpace.Load(rom);
        space.Free(r);
        space.Save();
        rom.FixChecksum();
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
