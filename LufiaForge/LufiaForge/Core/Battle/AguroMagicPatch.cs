using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// spells and MP for Aguro (and Guy, who has his party slot in the prologue). sits on top of the "Up to 128 spells"
// patch (SpellTablePatch), its spell memory gives every character room for 64 spells and holds the four learn lists.
// what it does:
// - lets the battle spell menu open for slot 2 ($08:BD03 CPX #$02 / BEQ skipped it, the 02 becomes FF)
// - gives Aguro a learn list: tanky bruiser (defence and attack for the party, life drain, healing, and
//   Trickstr = Trick on the whole party)
// - gives Aguro MP and MP growth and Guy MP (only where the rom has none), and teaches Guy the list in the
//   prologue (map 93 event 2, where the others learn theirs)
// roms with an earlier version (own lists and routines, before 0.4.0-beta.7) show Update, the spell memory
// takes their lists over
public static class AguroMagicPatch
{
    public const int MenuSkip = SpellMemory.MenuSkip;

    // Aguro's spells (Trickstr at level 24 gets added when the rom has it)
    public static readonly (int Level, int Spell)[] DefaultAguro =
    {
        (0, 0x1E), (0, 0x22), (8, 0x1C), (12, 0x14), (16, 0x1F), (21, 0x23), (26, 0x1D), (31, 0x15), (37, 0x20), (44, 0x24),
    };
    // Aguro's default list for this rom (with Trickstr if the rom has it)
    public static List<(int Level, int Spell)> DefaultFor(RomBuffer rom) =>
        SpellTablePatch.Find(rom, SpellTablePatch.TricksterName) is int tricksterSpell and >= 0
            ? DefaultAguro.Append((Level: 24, Spell: tricksterSpell)).OrderBy(e => e.Level).ToList()
            : DefaultAguro.ToList();
    // MP gained over each band of 16 level-ups (about half the Hero's), ~210 MP at level 99
    public static readonly int[] DefaultMpGrowth = { 24, 40, 56, 48, 24, 18, 12 };

    // the battle code's other "slot 2 has no magic" checks (CMP / CPX #$02), switched off with the patch.
    // $08:F1D1 made every MP change (MP items, spells, drain) miss Aguro, $08:B8FB made him immune to Mute
    private static readonly int[] SlotChecks = { 0x471D2, 0x438FC };
    private static bool ChecksOff(RomBuffer rom) => SlotChecks.All(checkOffset => rom.ReadByte(checkOffset) == 0xFF);
    private static void SetChecks(RomBuffer rom, byte value) { foreach (int checkOffset in SlotChecks) if (rom.ReadByte(checkOffset) is 0x02 or 0xFF) rom.WriteByte(checkOffset, value); }

    public static bool IsApplied(RomBuffer rom) => SpellMemory.Installed(rom) && rom.ReadByte(MenuSkip) == 0xFF && ChecksOff(rom) && SpellMemory.SwapFixed(rom);

    // an earlier version is in the rom: Add (Update) moves it onto the spell memory (and for versions from before
    // 0.4.0-beta.9 also lets MP effects and Mute reach Aguro)
    public static bool NeedsUpdate(RomBuffer rom) =>
        (!SpellMemory.Installed(rom) && SpellMemory.OldBlock(rom) > 0) ||
        (SpellMemory.Installed(rom) && rom.ReadByte(MenuSkip) == 0xFF && (!ChecksOff(rom) || !SpellMemory.SwapFixed(rom)));

    public static bool IsOriginal(RomBuffer rom) =>
        (rom.ReadByte(MenuSkip) == 0x02 || NeedsUpdate(rom)) && (SpellTablePatch.IsApplied(rom) || SpellTablePatch.IsOriginal(rom));

    // the learn lists (Hero, Lufia, Aguro, Jerin), see SpellMemory.ReadLists
    public static List<(int Level, int Spell)>[] ReadLists(RomBuffer rom) => SpellMemory.ReadLists(rom);
    public static void WriteLists(RomBuffer rom, IReadOnlyList<IReadOnlyList<(int Level, int Spell)>> lists) => SpellMemory.WriteLists(rom, lists);
    public static int MaxSpells(int slot) => SpellMemory.MaxPerCharacter;

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (SpellMemory.Installed(rom) && rom.ReadByte(MenuSkip) == 0xFF)
        {
            // applied by an earlier version, only the slot checks are missing
            SpellTablePatch.Apply(rom);
            SetChecks(rom, 0xFF);
            rom.FixChecksum();
            return;
        }
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's spell code isn't the original, so Aguro's magic can't be added safely.");
        bool hadOld = NeedsUpdate(rom);
        SpellTablePatch.Apply(rom);   // the 128-spell table and the spell memory (takes an earlier version's lists over)
        if (SpellTablePatch.Find(rom, SpellTablePatch.TricksterName) < 0 && SpellTablePatch.NewCount(rom) < SpellTablePatch.NewSlots)
            SpellTablePatch.AddTrickster(rom);
        var lists = ReadLists(rom);
        if (lists[2].Count == 0) { lists[2] = DefaultFor(rom); WriteLists(rom, lists); }
        rom.WriteByte(MenuSkip, 0xFF);
        SetChecks(rom, 0xFF);
        if (!hadOld) { GiveMp(rom); GiveGuySpells(rom); }
        rom.FixChecksum();
    }

    // takes Aguro's magic out again: his menu, his list and Guy's prologue spells (MP stays)
    public static void Remove(RomBuffer rom)
    {
        if (!SpellMemory.Installed(rom)) return;
        rom.WriteByte(MenuSkip, 0x02);
        SetChecks(rom, 0x02);
        var lists = ReadLists(rom);
        if (lists[2].Count > 0) { lists[2].Clear(); WriteLists(rom, lists); }
        TakeGuySpells(rom);
        Cave.AncientCave.Refresh(rom);   // its spell chests teach from the learn lists
        rom.FixChecksum();
    }

    // Aguro's MP: a start value and growth (only where the rom still has none), plus Guy's prologue MP
    private static void GiveMp(RomBuffer rom)
    {
        int aguroRecord = GameDataAddresses.CharacterRecord(2), guyRecord = GameDataAddresses.CharacterRecord(6);
        if (rom.ReadUInt16Le(aguroRecord + 4) == 0) rom.WriteUInt16Le(aguroRecord + 4, 8);
        bool noGrowth = Enumerable.Range(0, 7).All(band => rom.ReadByte(GameDataAddresses.MpGrowth(band, 2)) == 0);
        if (noGrowth) for (int band = 0; band < 7; band++) rom.WriteByte(GameDataAddresses.MpGrowth(band, 2), (byte)DefaultMpGrowth[band]);
        if (rom.ReadUInt16Le(guyRecord + 4) == 0) rom.WriteUInt16Le(guyRecord + 4, 190);
    }

    // Guy learns Aguro's spells where the prologue party learns theirs (map 93 event 2)
    private static void GiveGuySpells(RomBuffer rom)
    {
        try
        {
            var script = EventScript.Load(rom, 0x93, 2);
            if (script == null) return;
            int lastLearn = script.Ops.FindLastIndex(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x3D);
            if (lastLearn < 0 || script.Ops.Any(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x3D && (o.Bytes[1] & 3) == 2)) return;
            int insertAt = lastLearn + 1;
            foreach (var (_, spell) in DefaultFor(rom)) script.Ops.Insert(insertAt++, new ScriptOp { Bytes = new byte[] { 0x3D, 0x02, (byte)spell } });
            script.Save(true);
        }
        catch (Exception) { /* a hack that changed the prologue: Guy keeps none */ }
    }

    // takes Guy's prologue spells back out
    private static void TakeGuySpells(RomBuffer rom)
    {
        try
        {
            var script = EventScript.Load(rom, 0x93, 2);
            if (script == null) return;
            if (script.Ops.RemoveAll(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x3D && (o.Bytes[1] & 3) == 2) > 0) script.Save(true);
        }
        catch (Exception) { }
    }
}

// character data addresses the core patches need (the Game Data editors have their own copies)
internal static class GameDataAddresses
{
    public const int CharacterTable = 0x46D25, CharacterStride = 29, GrowthTable = 0x46C69;
    public static int CharacterRecord(int character) => CharacterTable + character * CharacterStride;
    // MP a character (0-3) gains over one growth band (stat 1 of the growth table)
    public static int MpGrowth(int band, int character) => GrowthTable + 1 * 28 + band * 4 + character;
}
