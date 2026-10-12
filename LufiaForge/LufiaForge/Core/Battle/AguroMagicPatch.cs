using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Spells and MP for Aguro (and Guy, who shares his party slot in the prologue), on top of the "Up to 128 spells"
/// patch (<see cref="SpellTablePatch"/>), whose spell memory gives every character room for 64 spells and holds the
/// four learn lists. This patch:
/// <list type="bullet">
/// <item>lets the battle spell menu open for slot 2 ($08:BD03 CPX #$02 / BEQ skipped it: the 02 becomes FF);</item>
/// <item>gives Aguro a learn list: a tanky bruiser (defence and attack for the party, life drain, healing, and
/// Trickstr, Trick on the whole party);</item>
/// <item>gives Aguro MP and MP growth and Guy MP (only where the ROM has none), and teaches Guy the list in the
/// prologue (map 93 event 2, where the others learn theirs).</item>
/// </list>
/// ROMs with an earlier version (its own lists and routines, before 0.4.0-beta.7) show Update: the spell memory
/// takes its lists over.
/// </summary>
public static class AguroMagicPatch
{
    public const int MenuSkip = SpellMemory.MenuSkip;

    /// <summary>Aguro's spells (Trickstr, at level 24, is added when the ROM has it).</summary>
    public static readonly (int Level, int Spell)[] DefaultAguro =
    {
        (0, 0x1E), (0, 0x22), (8, 0x1C), (12, 0x14), (16, 0x1F), (21, 0x23), (26, 0x1D), (31, 0x15), (37, 0x20), (44, 0x24),
    };
    /// <summary>Aguro's default list for this ROM (with Trickstr when the ROM has it).</summary>
    public static List<(int Level, int Spell)> DefaultFor(RomBuffer rom) =>
        SpellTablePatch.Find(rom, SpellTablePatch.TricksterName) is int t and >= 0
            ? DefaultAguro.Append((Level: 24, Spell: t)).OrderBy(e => e.Level).ToList()
            : DefaultAguro.ToList();
    /// <summary>MP gained over each band of 16 level-ups (about half the Hero's): ~210 MP at level 99.</summary>
    public static readonly int[] DefaultMpGrowth = { 24, 40, 56, 48, 24, 18, 12 };

    /// <summary>
    /// The battle code's other "slot 2 has no magic" checks (CMP / CPX #$02), switched off with the patch:
    /// $08:F1D1 made every MP change (MP items, spells, drain) miss Aguro; $08:B8FB made him immune to Mute.
    /// </summary>
    private static readonly int[] SlotChecks = { 0x471D2, 0x438FC };
    private static bool ChecksOff(RomBuffer rom) => SlotChecks.All(c => rom.ReadByte(c) == 0xFF);
    private static void SetChecks(RomBuffer rom, byte v) { foreach (int c in SlotChecks) if (rom.ReadByte(c) is 0x02 or 0xFF) rom.WriteByte(c, v); }

    public static bool IsApplied(RomBuffer rom) => SpellMemory.Installed(rom) && rom.ReadByte(MenuSkip) == 0xFF && ChecksOff(rom) && SpellMemory.SwapFixed(rom);

    /// <summary>An earlier version is in the ROM: Add (Update) moves it onto the spell memory (or, from before
    /// 0.4.0-beta.9, also lets MP effects and Mute reach Aguro).</summary>
    public static bool NeedsUpdate(RomBuffer rom) =>
        (!SpellMemory.Installed(rom) && SpellMemory.OldBlock(rom) > 0) ||
        (SpellMemory.Installed(rom) && rom.ReadByte(MenuSkip) == 0xFF && (!ChecksOff(rom) || !SpellMemory.SwapFixed(rom)));

    public static bool IsOriginal(RomBuffer rom) =>
        (rom.ReadByte(MenuSkip) == 0x02 || NeedsUpdate(rom)) && (SpellTablePatch.IsApplied(rom) || SpellTablePatch.IsOriginal(rom));

    /// <summary>The learn lists (Hero, Lufia, Aguro, Jerin); see <see cref="SpellMemory.ReadLists"/>.</summary>
    public static List<(int Level, int Spell)>[] ReadLists(RomBuffer rom) => SpellMemory.ReadLists(rom);
    public static void WriteLists(RomBuffer rom, IReadOnlyList<IReadOnlyList<(int Level, int Spell)>> lists) => SpellMemory.WriteLists(rom, lists);
    public static int MaxSpells(int slot) => SpellMemory.MaxPerCharacter;

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (SpellMemory.Installed(rom) && rom.ReadByte(MenuSkip) == 0xFF)
        {
            // applied by an earlier version: only the slot checks are missing
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

    /// <summary>Takes Aguro's magic out again: his menu, his list and Guy's prologue spells (MP stays).</summary>
    public static void Remove(RomBuffer rom)
    {
        if (!SpellMemory.Installed(rom)) return;
        rom.WriteByte(MenuSkip, 0x02);
        SetChecks(rom, 0x02);
        var lists = ReadLists(rom);
        if (lists[2].Count > 0) { lists[2].Clear(); WriteLists(rom, lists); }
        TakeGuySpells(rom);
        rom.FixChecksum();
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

    /// <summary>Takes Guy's prologue spells back out.</summary>
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
