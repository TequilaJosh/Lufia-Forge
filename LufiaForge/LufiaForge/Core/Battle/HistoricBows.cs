using LufiaForge.Modules.GameData;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Five more bows for Jerin / Artea (equip bit 80), named after historic bows and fitted around the game's three
/// (Elven Bow 58, Artea's Bow 100, Might Bow 148), and Jerin starts with the weakest, the Self Bow. They are new items
/// in the game's unused item numbers ("Dummy" / "Not Use" that nothing gives out), copies of the Elven Bow with their
/// own name, price, ATP and weight / hit chance. The Ancient Cave's chests take them up with the rest of the gear.
/// </summary>
public static class HistoricBows
{
    private const int ElvenBow = 0xE8;
    private const int JerinRecord = 3;

    /// <summary>Name (12 characters, '@' for a space), ATP, price, weight (property 0E's first byte).</summary>
    public static readonly (string Name, int Atp, int Price, int Weight)[] Bows =
    {
        ("Self Bow", 18, 150, 0x1E),
        ("Scythian Bow", 36, 900, 0x22),
        ("Longbow", 80, 7000, 0x2C),
        ("Yumi", 124, 26000, 0x36),
        ("Mongol Bow", 172, 42000, 0x42),
    };

    private static string RomName(string name) => name.Replace(' ', '@').PadRight(12).Substring(0, 12);

    private static int Find(RomBuffer rom, string name)
    {
        var want = RomName(name).Select(c => (byte)c).ToArray();
        for (int id = 1; id < 0xFF; id++)
            if (rom.ReadBytes(ItemTable.RecordOffset(rom, id), 12).SequenceEqual(want)) return id;
        return -1;
    }

    public static bool IsApplied(RomBuffer rom) => Find(rom, Bows[0].Name) > 0;

    public static bool IsOriginal(RomBuffer rom) =>
        !IsApplied(rom) && ItemTable.FreeIds(rom, new HashSet<int>()).Count(id => id != 0xFE) >= Bows.Length;

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        var free = ItemTable.FreeIds(rom, ItemEditorViewModel.ReferencedItems(rom)).Where(id => id != 0xFE).ToList();
        if (free.Count < Bows.Length) throw new InvalidOperationException($"The new bows need {Bows.Length} free item numbers; this ROM has {free.Count}.");
        var template = ItemTable.Properties(rom, ElvenBow) ?? new List<(int, int, int)>();
        int hit = template.FirstOrDefault(p => p.Id == 0x0E).B;
        int first = -1;
        for (int i = 0; i < Bows.Length; i++)
        {
            var (name, atp, price, weight) = Bows[i];
            int id = free[i];
            ItemTable.NewItem(rom, id, ElvenBow);
            int o = ItemTable.RecordOffset(rom, id);
            rom.WriteBytes(o, RomName(name).Select(c => (byte)c).ToArray());
            rom.WriteUInt16Le(o + 16, (ushort)price);
            ItemTable.SetProperties(rom, id, new List<(int, int, int)> { (0x05, atp & 0xFF, atp >> 8), (0x0E, weight, hit == 0 ? 0xFF : hit) });
            if (i == 0) first = id;
        }
        rom.WriteByte(GameDataOffsets.CharacterTable + JerinRecord * GameDataOffsets.CharacterStride + 23, (byte)first);
        rom.FixChecksum();
    }
}
