using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Spells and MP for Aguro (and Guy, who shares his party slot in the prologue). The game shuts slot 2 out of magic
/// in several places and gives it no spell memory of its own:
/// <list type="bullet">
/// <item>learned spells live at $158F + a per-slot offset (tables $08:E84C, learning, and $08:BD3F, the spell menu:
/// 00 20 00 40 - slot 2 shared the Hero's 32 entries); the menu ($08:BCF4) skipped slot 2 (CPX #$02).</item>
/// <item>spell lists (level, spell pairs, FF end) are read through 8-bit offsets $08:E7EB from $08:EE0D, with no room
/// for a fourth list; level-up ($08:E65E) and joining ($08:EC07) skipped slot 2.</item>
/// <item>a new member's spells are cleared at $08:EAEC (slot 2 skipped), and the spell menu copied 32 entries for every slot; event 45 "knows spell" ($01:D2A7) read slot 2
/// at Jerin's entries.</item>
/// </list>
/// The patch splits the Hero's 32 entries: Hero 0-19 (up to 19 spells), Aguro 20-31 (up to 11), each list ending at
/// an empty entry (the spell menu stops at the first empty entry - checked in game). The four spell lists move to the
/// expanded ROM next to four small routines that replace the level-up, join, clear and "knows spell" code. Aguro also
/// gets MP growth, and Guy learns spells in the prologue (map 93 event 2, where the others learn theirs) and gets MP.
/// The save file layout is unchanged: spell memory was already saved.
/// </summary>
public static class AguroMagicPatch
{
    public const int LevelUpSite = 0x4665E, JoinSite = 0x46C07, ClearSite = 0x46AEC, KnowsSite = 0xD2A7;
    public const int LearnTable = 0x4684C, MenuTable = 0x43D3F, MenuSkip = 0x43D04, MenuSite = 0x43D07;
    /// <summary>The field menu's spell memory pointers ($00:9CAD, one word per slot: $158F $15AF $14B5 $15CF); it
    /// counts and lists each slot's spells from there. Slot 2's pointed at unrelated RAM ($14B5).</summary>
    public const int FieldPointers = 0x1CAD;
    private const int FieldSlot2Original = 0x14B5;
    private static int FieldSlot2(RomBuffer rom) => rom.ReadUInt16Le(FieldPointers + 4);
    private static bool FieldFixed(RomBuffer rom) => FieldSlot2(rom) == 0x158F + SpellOffset[2];
    private static readonly byte[] LevelUpOriginal = Convert.FromHexString("8AC902F0210AA8B9EBE7A8B90DEEF012C9FFF012DD6F15D009B90EEE85D82229E808C8C880E5");
    private static readonly byte[] JoinOriginal = Convert.FromHexString("C902F01DAA0AA8B9EBE7A8B90DEEC9FFF00F0900D007B90EEE2229E808C8C880EA");
    private static readonly byte[] ClearOriginal = Convert.FromHexString("ACF114C002F00CBE4CE8A0209E8F15E888D0F9");
    private static readonly byte[] KnowsOriginal = Convert.FromHexString("2903C9039002A9020A0A0A0A0AAAA9208522");
    private static readonly byte[] MenuOriginal = Convert.FromHexString("BC3FBDA97E48ABA200B98F159D00D8222DFA08AD9D179D40D8AD9A179D80D8AD9C179DC0D8ADA4179D40D9E8C8E020D0D8");
    private const int LearnLevel = 0, LearnInitial = 50, ClearSpells = 94, KnowsSetup = 117, BuildMenu = 132, Offs = 191, SpOff = 195, SpCnt = 199, Blob = 203;
    /// <summary>Room for the four lists (each: (level, spell) pairs and an FF).</summary>
    public const int BlobSize = 252;
    private static readonly (int At, int Data, int Add)[] Fixups =
    {
        (6, Offs, 0), (11, Blob, 0), (27, Blob, 1), (56, Offs, 0), (61, Blob, 0), (73, Blob, 1),
        (98, SpOff, 0), (103, SpCnt, 0), (121, SpCnt, 0), (127, SpOff, 0), (133, SpOff, 0), (138, SpCnt, 0),
    };
    /// <summary>Spell memory per slot: where it starts in $158F and how many entries (one always stays empty).</summary>
    public static readonly int[] SpellOffset = { 0x00, 0x20, 0x14, 0x40 };
    public static readonly int[] SpellEntries = { 20, 32, 12, 32 };
    public static int MaxSpells(int slot) => SpellEntries[slot] - 1;

    /// <summary>Aguro's spells: a tanky bruiser - defence and attack for the party, life drain, healing; with
    /// <see cref="SpellTablePatch"/> also Trickstr (Trick on the whole party) at level 24.</summary>
    public static readonly (int Level, int Spell)[] DefaultAguro =
    {
        (0, 0x1E), (0, 0x22), (8, 0x1C), (12, 0x14), (16, 0x1F), (21, 0x23), (26, 0x1D), (31, 0x15), (37, 0x20), (44, 0x24),
    };
    /// <summary>Aguro's default list for this ROM (without Trickstr when the new spell isn't in it).</summary>
    public static List<(int Level, int Spell)> DefaultFor(RomBuffer rom) =>
        SpellTablePatch.Find(rom, SpellTablePatch.TricksterName) is int t and >= 0
            ? DefaultAguro.Append((Level: 24, Spell: t)).OrderBy(e => e.Level).ToList()
            : DefaultAguro.ToList();
    /// <summary>MP gained over each band of 16 level-ups (about half the Hero's): ~210 MP at level 99.</summary>
    public static readonly int[] DefaultMpGrowth = { 24, 40, 56, 48, 24, 18, 12 };

    public static readonly byte[] Routine =
    {
        // learnLevel: from $08:E65E (A8 X8, X = slot, DB = 08)
        0xDA,                      //   0 PHX
        0xBD, 0x6F, 0x15,          //   1 LDA $156F,X       level
        0x48,                      //   4 PHA
        0xBF, 0x00, 0x00, 0x00,    //   5 LDA offs,X        where the slot's list starts
        0xAA,                      //   9 TAX
        0xBF, 0x00, 0x00, 0x00,    //  10 L1: LDA blob,X
        0xC9, 0xFF,                //  14 CMP #$FF
        0xF0, 0x1D,                //  16 BEQ L9
        0xC9, 0x00,                //  18 CMP #$00          (level 0: learned on joining)
        0xF0, 0x15,                //  20 BEQ L2
        0xC3, 0x01,                //  22 CMP 1,S           this level?
        0xD0, 0x11,                //  24 BNE L2
        0xBF, 0x00, 0x00, 0x00,    //  26 LDA blob+1,X      the spell
        0x85, 0xD8,                //  30 STA $D8
        0xDA,                      //  32 PHX
        0x48,                      //  33 PHA
        0xA3, 0x04,                //  34 LDA 4,S           slot
        0xAA,                      //  36 TAX
        0x68,                      //  37 PLA
        0x22, 0x29, 0xE8, 0x08,    //  38 JSL $08E829       learn it
        0xFA,                      //  42 PLX
        0xE8,                      //  43 L2: INX
        0xE8,                      //  44 INX
        0x80, 0xDB,                //  45 BRA L1
        0x68,                      //  47 L9: PLA
        0xFA,                      //  48 PLX
        0x6B,                      //  49 RTL
        // learnInitial: from $08:EC07 (A8 X16, A = character 0-3, DB = 08)
        0x08,                      //  50 PHP
        0xE2, 0x10,                //  51 SEP #$10
        0xAA,                      //  53 TAX
        0xDA,                      //  54 PHX
        0xBF, 0x00, 0x00, 0x00,    //  55 LDA offs,X
        0xAA,                      //  59 TAX
        0xBF, 0x00, 0x00, 0x00,    //  60 M1: LDA blob,X
        0xC9, 0xFF,                //  64 CMP #$FF
        0xF0, 0x17,                //  66 BEQ M9
        0xC9, 0x00,                //  68 CMP #$00          known from the start
        0xD0, 0x0F,                //  70 BNE M2
        0xBF, 0x00, 0x00, 0x00,    //  72 LDA blob+1,X
        0xDA,                      //  76 PHX
        0x48,                      //  77 PHA
        0xA3, 0x03,                //  78 LDA 3,S           slot
        0xAA,                      //  80 TAX
        0x68,                      //  81 PLA
        0x22, 0x29, 0xE8, 0x08,    //  82 JSL $08E829
        0xFA,                      //  86 PLX
        0xE8,                      //  87 M2: INX
        0xE8,                      //  88 INX
        0x80, 0xE1,                //  89 BRA M1
        0xFA,                      //  91 M9: PLX
        0x28,                      //  92 PLP
        0x6B,                      //  93 RTL
        // clearSpells: from $08:EAEC (A8 X8, DB = 08; the new member's slot in $14F1)
        0xAE, 0xF1, 0x14,          //  94 LDX $14F1
        0xBF, 0x00, 0x00, 0x00,    //  97 LDA spoff,X
        0x48,                      // 101 PHA
        0xBF, 0x00, 0x00, 0x00,    // 102 LDA spcnt,X
        0xA8,                      // 106 TAY
        0x68,                      // 107 PLA
        0xAA,                      // 108 TAX
        0x9E, 0x8F, 0x15,          // 109 C1: STZ $158F,X
        0xE8,                      // 112 INX
        0x88,                      // 113 DEY
        0xD0, 0xF9,                // 114 BNE C1
        0x6B,                      // 116 RTL
        // knowsSetup: from $01:D2A7, event 45 (A8 X16, B = 0, A = character)
        0x29, 0x03,                // 117 AND #$03
        0xAA,                      // 119 TAX
        0xBF, 0x00, 0x00, 0x00,    // 120 LDA spcnt,X
        0x85, 0x22,                // 124 STA $22           entries to look through
        0xBF, 0x00, 0x00, 0x00,    // 126 LDA spoff,X
        0xAA,                      // 130 TAX
        0x6B,                      // 131 RTL
        // buildMenu: from $08:BD07, the spell menu's list (A8 X8, X = slot, DB = 08) - only the slot's own entries,
        // so one character's page count never takes in the next one's spells
        0xBF, 0x00, 0x00, 0x00,    // 132 LDA spoff,X
        0xA8,                      // 136 TAY
        0xBF, 0x00, 0x00, 0x00,    // 137 LDA spcnt,X
        0x48,                      // 141 PHA
        0xA9, 0x7E,                // 142 LDA #$7E
        0x48,                      // 144 PHA
        0xAB,                      // 145 PLB
        0xA2, 0x00,                // 146 LDX #$00
        0xB9, 0x8F, 0x15,          // 148 B1: LDA $158F,Y
        0x9D, 0x00, 0xD8,          // 151 STA $D800,X
        0x22, 0x2D, 0xFA, 0x08,    // 154 JSL $08FA2D       the spell's data
        0xAD, 0x9D, 0x17,          // 158 LDA $179D
        0x9D, 0x40, 0xD8,          // 161 STA $D840,X
        0xAD, 0x9A, 0x17,          // 164 LDA $179A
        0x9D, 0x80, 0xD8,          // 167 STA $D880,X
        0xAD, 0x9C, 0x17,          // 170 LDA $179C
        0x9D, 0xC0, 0xD8,          // 173 STA $D8C0,X
        0xAD, 0xA4, 0x17,          // 176 LDA $17A4
        0x9D, 0x40, 0xD9,          // 179 STA $D940,X
        0xE8,                      // 182 INX
        0xC8,                      // 183 INY
        0x8A,                      // 184 TXA
        0xC3, 0x01,                // 185 CMP 1,S           all of the slot's entries?
        0xD0, 0xD7,                // 187 BNE B1
        0x68,                      // 189 PLA
        0x6B,                      // 190 RTL               (the game's own exit restores DB)
    };

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    public static bool IsOriginal(RomBuffer rom) =>
        rom.ReadBytes(LevelUpSite, LevelUpOriginal.Length).SequenceEqual(LevelUpOriginal) &&
        rom.ReadBytes(JoinSite, JoinOriginal.Length).SequenceEqual(JoinOriginal) &&
        rom.ReadBytes(ClearSite, ClearOriginal.Length).SequenceEqual(ClearOriginal) &&
        rom.ReadBytes(KnowsSite, KnowsOriginal.Length).SequenceEqual(KnowsOriginal) &&
        rom.ReadBytes(MenuSite, MenuOriginal.Length).SequenceEqual(MenuOriginal) &&
        FieldSlot2(rom) == FieldSlot2Original
        || NeedsFieldFix(rom);

    /// <summary>The patch's spell lists are in the ROM (any version of it).</summary>
    public static bool HasLists(RomBuffer rom) => rom.ReadByte(LevelUpSite) == 0x22 && BlockOffset(rom) > 0;

    public static bool IsApplied(RomBuffer rom) => rom.ReadByte(LevelUpSite) == 0x22 && BlockOffset(rom) > 0 && FieldFixed(rom);

    /// <summary>An earlier version of the patch (before 0.4.0-beta.7) that left the field Magic menu empty for Guy /
    /// Aguro: adding the patch again only fixes that.</summary>
    public static bool NeedsFieldFix(RomBuffer rom) =>
        rom.ReadByte(LevelUpSite) == 0x22 && BlockOffset(rom) > 0 && FieldSlot2(rom) == FieldSlot2Original;

    /// <summary>File offset of the routines and lists, or 0.</summary>
    public static int BlockOffset(RomBuffer rom)
    {
        int r = FileOffset(rom.ReadByte(LevelUpSite + 1) | rom.ReadByte(LevelUpSite + 2) << 8 | rom.ReadByte(LevelUpSite + 3) << 16);
        return r > 0 && r + Blob + BlobSize <= rom.Length && rom.ReadByte(r) == 0xDA && rom.ReadByte(r + 1) == 0xBD ? r : 0;
    }

    /// <summary>The four spell lists (Hero, Lufia, Aguro, Jerin): the patch's, or the game's own (Aguro empty).</summary>
    public static List<(int Level, int Spell)>[] ReadLists(RomBuffer rom)
    {
        var lists = new List<(int, int)>[4];
        int block = BlockOffset(rom);
        for (int s = 0; s < 4; s++)
        {
            lists[s] = new List<(int, int)>();
            int p;
            if (block > 0) p = block + Blob + rom.ReadByte(block + Offs + s);
            else if (s == 2) continue;
            else p = 0x46E0D + rom.ReadByte(0x467EB + s * 2);
            for (int n = 0; n < 64 && rom.ReadByte(p) != 0xFF; n++, p += 2) lists[s].Add((rom.ReadByte(p), rom.ReadByte(p + 1)));
        }
        return lists;
    }

    /// <summary>Why the lists can't be stored, or null.</summary>
    public static string? Problem(IReadOnlyList<IReadOnlyList<(int Level, int Spell)>> lists)
    {
        string[] names = { "The Hero", "Lufia", "Aguro", "Jerin" };
        for (int s = 0; s < 4; s++)
            if (lists[s].Count > MaxSpells(s)) return $"{names[s]} can learn at most {MaxSpells(s)} spells (their spell memory holds {SpellEntries[s]}, one kept empty).";
        int bytes = lists.Sum(l => l.Count * 2 + 1);
        if (bytes > BlobSize) return $"The four spell lists need {bytes} bytes; there is room for {BlobSize}.";
        return null;
    }

    /// <summary>Writes the four lists into the patch's block.</summary>
    public static void WriteLists(RomBuffer rom, IReadOnlyList<IReadOnlyList<(int Level, int Spell)>> lists)
    {
        int block = BlockOffset(rom);
        if (block == 0) throw new InvalidOperationException("The Aguro magic patch isn't in this ROM.");
        if (Problem(lists) is string bad) throw new InvalidOperationException(bad);
        var blob = new List<byte>();
        for (int s = 0; s < 4; s++)
        {
            rom.WriteByte(block + Offs + s, (byte)blob.Count);
            foreach (var (lv, sp) in lists[s]) { blob.Add((byte)Math.Clamp(lv, 0, 0xFE)); blob.Add((byte)sp); }
            blob.Add(0xFF);
        }
        while (blob.Count < BlobSize) blob.Add(0xFF);
        rom.WriteBytes(block + Blob, blob.ToArray());
    }

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (NeedsFieldFix(rom)) { rom.WriteUInt16Le(FieldPointers + 4, (ushort)(0x158F + SpellOffset[2])); rom.FixChecksum(); return; }
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's magic code for Aguro's party slot isn't the original, so this patch can't be applied safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        if (!SpellTablePatch.IsApplied(rom) && SpellTablePatch.IsOriginal(rom)) SpellTablePatch.Apply(rom);
        if (SpellTablePatch.IsApplied(rom) && SpellTablePatch.NewCount(rom) < SpellTablePatch.NewSlots) SpellTablePatch.AddTrickster(rom);
        var lists = ReadLists(rom);
        lists[2] = DefaultFor(rom);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(Blob + BlobSize, ExpansionSpace.Kind.Patch, 7);
        WriteAt(rom, at, lists);
        space.Save();
        GiveMp(rom);
        GiveGuySpells(rom);
        rom.FixChecksum();
    }

    /// <summary>Writes the routines and lists at <paramref name="at"/> and points the game at them (also for 1 MB test copies).</summary>
    public static void WriteAt(RomBuffer rom, int at, IReadOnlyList<IReadOnlyList<(int Level, int Spell)>> lists)
    {
        var code = new byte[Blob + BlobSize];
        Routine.CopyTo(code, 0);
        int baseSnes = SnesAddress(at);
        foreach (var (o, d, add) in Fixups)
        {
            int a = baseSnes + d + add;
            code[o] = (byte)a; code[o + 1] = (byte)(a >> 8); code[o + 2] = (byte)(a >> 16);
        }
        for (int s = 0; s < 4; s++) { code[SpOff + s] = (byte)SpellOffset[s]; code[SpCnt + s] = (byte)SpellEntries[s]; }
        rom.WriteBytes(at, code);
        // the game's code: five hooks, two table entries and the menu's slot-2 skip
        void Hook(int site, int entry, int length, byte[] after)
        {
            var b = new byte[length];
            Array.Fill(b, (byte)0xEA);
            int r = baseSnes + entry;
            b[0] = 0x22; b[1] = (byte)r; b[2] = (byte)(r >> 8); b[3] = (byte)(r >> 16);
            after.CopyTo(b, 4);
            rom.WriteBytes(site, b);
        }
        Hook(LevelUpSite, LearnLevel, LevelUpOriginal.Length, new byte[] { 0x80, 0x20 });   // then BRA $E684
        Hook(JoinSite, LearnInitial, JoinOriginal.Length, new byte[] { 0x80, 0x1B });       // then BRA $EC28
        Hook(ClearSite, ClearSpells, ClearOriginal.Length, Array.Empty<byte>());
        Hook(KnowsSite, KnowsSetup, KnowsOriginal.Length, Array.Empty<byte>());
        Hook(MenuSite, BuildMenu, MenuOriginal.Length, new byte[] { 0x80, 0x2B });         // then BRA $BD38
        rom.WriteByte(LearnTable + 2, (byte)SpellOffset[2]);
        rom.WriteByte(MenuTable + 2, (byte)SpellOffset[2]);
        rom.WriteByte(MenuSkip, 0xFF);
        rom.WriteUInt16Le(FieldPointers + 4, (ushort)(0x158F + SpellOffset[2]));   // the field Magic menu
        WriteLists(rom, lists);
    }

    /// <summary>Aguro's MP: a start value and growth (only where the ROM still has none); Guy's prologue MP.</summary>
    private static void GiveMp(RomBuffer rom)
    {
        int aguro = GameDataAddresses.CharacterRecord(2), guy = GameDataAddresses.CharacterRecord(6);
        if (rom.ReadUInt16Le(aguro + 4) == 0) rom.WriteUInt16Le(aguro + 4, 8);
        bool noGrowth = Enumerable.Range(0, 7).All(b => rom.ReadByte(GameDataAddresses.MpGrowth(b, 2)) == 0);
        if (noGrowth) for (int b = 0; b < 7; b++) rom.WriteByte(GameDataAddresses.MpGrowth(b, 2), (byte)DefaultMpGrowth[b]);
        if (rom.ReadUInt16Le(guy + 4) == 0) rom.WriteUInt16Le(guy + 4, 190);
    }

    /// <summary>Guy learns Aguro's spells where the prologue party learns theirs (map 93 event 2).</summary>
    private static void GiveGuySpells(RomBuffer rom)
    {
        try
        {
            var ev = EventScript.Load(rom, 0x93, 2);
            if (ev == null) return;
            int last = ev.Ops.FindLastIndex(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x3D);
            if (last < 0 || ev.Ops.Any(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x3D && (o.Bytes[1] & 3) == 2)) return;
            int k = last + 1;
            foreach (var (_, sp) in DefaultFor(rom)) ev.Ops.Insert(k++, new ScriptOp { Bytes = new byte[] { 0x3D, 0x02, (byte)sp } });
            ev.Save(true);
        }
        catch (Exception) { /* a hack that changed the prologue: Guy keeps none */ }
    }

    public static void Remove(RomBuffer rom)
    {
        int block = BlockOffset(rom);
        if (block == 0) return;
        rom.WriteBytes(LevelUpSite, LevelUpOriginal);
        rom.WriteBytes(JoinSite, JoinOriginal);
        rom.WriteBytes(ClearSite, ClearOriginal);
        rom.WriteBytes(KnowsSite, KnowsOriginal);
        rom.WriteBytes(MenuSite, MenuOriginal);
        rom.WriteByte(LearnTable + 2, 0x00);
        rom.WriteByte(MenuTable + 2, 0x00);
        rom.WriteByte(MenuSkip, 0x02);
        rom.WriteUInt16Le(FieldPointers + 4, FieldSlot2Original);
        TakeGuySpells(rom);
        var space = ExpansionSpace.Load(rom);
        space.Free(block);
        space.Save();
        rom.FixChecksum();
    }

    /// <summary>Takes Guy's prologue spells back out: without the patch slot 2 would learn them into the Hero's spells.</summary>
    private static void TakeGuySpells(RomBuffer rom)
    {
        try
        {
            var ev = EventScript.Load(rom, 0x93, 2);
            if (ev == null) return;
            if (ev.Ops.RemoveAll(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x3D && (o.Bytes[1] & 3) == 2) > 0) ev.Save(true);
        }
        catch (Exception) { }
    }
}

/// <summary>Character data addresses the core patches need (the Game Data editors have their own copies).</summary>
internal static class GameDataAddresses
{
    public const int CharacterTable = 0x46D25, CharacterStride = 29, GrowthTable = 0x46C69;
    public static int CharacterRecord(int c) => CharacterTable + c * CharacterStride;
    /// <summary>MP gained over growth band <paramref name="band"/> by character 0-3 (stat 1 of the growth table).</summary>
    public static int MpGrowth(int band, int c) => GrowthTable + 1 * 28 + band * 4 + c;
}
