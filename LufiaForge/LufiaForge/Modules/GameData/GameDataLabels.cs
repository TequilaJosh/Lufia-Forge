namespace LufiaForge.Modules.GameData;

/// <summary>A byte value with a display label, for ComboBoxes bound via SelectedValue.</summary>
public sealed record ByteOption(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Display names for enumerated fields (labels taken from L1ME, "?" = not yet understood).</summary>
public static class GameDataLabels
{
    public static readonly string[] CharacterSlots =
        { "Hero", "Lufia", "Aguro", "Jerin", "Maxim", "Selan", "Guy", "Artea" };

    public static readonly ByteOption[] ItemTypes =
    {
        new(0x00, "00  ?"),
        new(0x01, "01  None / key item"),
        new(0x02, "02  Equipment"),
        new(0x06, "06  Equipment with special effect"),
        new(0x41, "41  Used out of battle"),
        new(0x81, "81  Used in battle"),
        new(0xC1, "C1  Used in & out of battle"),
    };

    public static readonly ByteOption[] EquipSlots =
    {
        new(0, "0  None"), new(1, "1  Weapon"), new(2, "2  Armor"), new(3, "3  Shield"),
        new(4, "4  Helmet"), new(5, "5  Shoes"), new(6, "6  Ring / accessory"),
    };

    public static readonly ByteOption[] Targets =
    {
        new(0, "0  Single party member"),
        new(1, "1  All party (auto)"),
        new(2, "2  Single party member"),
        new(3, "3  Single monster"),
        new(4, "4  Monster group"),
        new(5, "5  All monsters (choose)"),
        new(6, "6  None (crashes battle)"),
        new(7, "7  All monsters (auto)"),
        new(8, "8  None (crashes battle)"),
        new(9, "9  Self (auto)"),
    };

    public static readonly ByteOption[] ItemIcons =
    {
        new(0x0, "0  Blank"), new(0x1, "1  Sword"), new(0x2, "2  Armor"), new(0x3, "3  Shield"),
        new(0x4, "4  Helmet"), new(0x5, "5  Shoes"), new(0x6, "6  Ring"), new(0x7, "7  Potion"),
        new(0x8, "8  Key"), new(0x9, "9  Whip"), new(0xA, "A  Staff"), new(0xB, "B  Spear"),
        new(0xC, "C  Bow & arrow"),
    };

    /// <summary>Equipment property ids (3-byte entries: id, u16 value).</summary>
    public static readonly ByteOption[] ItemProperties = BuildItemProperties();

    private static ByteOption[] BuildItemProperties()
    {
        var known = new Dictionary<int, string>
        {
            [0x05] = "ATP", [0x06] = "DFP", [0x07] = "MGR", [0x08] = "AGL", [0x09] = "STR",
            [0x0A] = "INT", [0x0C] = "Extra damage", [0x11] = "Death property",
            [0x14] = "Price modifier", [0x17] = "Extra resistance", [0x1E] = "Death property (?)",
        };
        var list = new ByteOption[256];
        for (int i = 0; i < 256; i++)
            list[i] = new ByteOption(i, $"{i:X2}  {(known.TryGetValue(i, out var s) ? s : "?")}");
        return list;
    }

    public static readonly ByteOption[] ShopTypes =
    {
        new(0, "0  Nothing"), new(1, "1  Unused (?)"), new(2, "2  Weapons"), new(3, "3  Armor"),
        new(4, "4  Weapons & armor"), new(5, "5  Items"), new(6, "6  Battle"), new(7, "7  Accessories"),
        new(8, "8  Battle"), new(9, "9  Unused (?)"),
    };

    /// <summary>Shop locations (index = shop id).</summary>
    public static readonly string[] ShopTowns =
    {
        "Unused", "Alekia", "Alekia", "Alekia", "Chatam", "Treck", "Unused", "Treck", "Treck", "Treck",
        "Lorbenia", "Lorbenia", "Lorbenia", "Lorbenia", "Lorbenia", "Lorbenia", "Grenoble", "Grenoble",
        "Grenoble", "Unused", "Medan", "Medan", "Medan", "Jenoba", "Jenoba", "Jenoba", "Unused", "Linze",
        "Linze", "Linze", "Bakku", "Surinagal", "Unused", "Unused", "Jenoba", "Ruan", "Elfrea", "Elfrea",
        "Unused", "Unused", "Unused", "Forfeit", "Forfeit", "Unused", "Forfeit", "Unused", "Unused",
        "Forfeit", "Unknown", "Bakku", "Bakku", "Bakku", "Arus", "Unused", "Unused", "Unused", "Herat",
        "Herat", "Herat", "Arubus", "Arubus", "Arubus", "Arubus", "Marse", "Marse", "Marse", "Soshette",
        "Epro", "Epro", "Epro", "Unused", "Frederia", "Frederia", "Frederia", "Treck (?)", "Ranqs",
        "Ranqs", "Lyden", "Arubus", "Unused", "Odel", "Odel", "Odel", "Surinagal", "Surinagal", "Surinagal",
    };

    public static readonly ByteOption[] MonsterTypes =
    {
        new(0, "0  Normal"), new(1, "1  Flying"), new(2, "2  ?"), new(3, "3  Ghost"),
        new(4, "4  Undead"), new(5, "5  ?"), new(6, "6  Sea"), new(7, "7  Dragon"),
    };

    public static readonly ByteOption[] Elements =
    {
        new(0, "0  None"), new(1, "1  Fire"), new(2, "2  Ice"),
        new(3, "3  Bolt"), new(4, "4  Water"), new(5, "5  Blast"),
    };

    public static readonly ByteOption[] SpellUsages =
    {
        new(0x00, "00  Nothing"),
        new(0x48, "48  Out of battle"),
        new(0x81, "81  Battle (monster side)"),
        new(0x82, "82  Battle (party, all)"),
        new(0x84, "84  Battle (party, single)"),
        new(0x88, "88  Battle (party, single)"),
        new(0xC8, "C8  In & out of battle (party)"),
    };

    public static readonly string[] SpellEffects =
    {
        "00 Nothing", "01 Restore HP", "02 ?", "03 ?", "04 ?", "05 Restore all HP",
        "06 Cure ailment(s)", "07 Raise stat", "08 ?", "09 ?", "0A ?", "0B Elemental damage",
        "0C ?", "0D Lower stat", "0E Inflict ailment(s)", "0F Drain MP", "10 Teleport", "11 Float", "12 ?",
    };

    public static readonly ByteOption[] CureKinds =
    {
        new(0x00, "Nothing"), new(0x40, "HP"), new(0x48, "MP"), new(0xC8, "HP & MP"),
    };

    public static readonly ByteOption[] StatKinds =
    {
        new(0x00, "Nothing"), new(0x10, "ATP"), new(0x18, "DFP"), new(0x20, "AGL"),
        new(0x28, "INT"), new(0x38, "MGR"),
    };

    public static readonly string[] Ailments =
        { "Paralysis", "Stone", "Sleep", "Confuse", "Poison", "Death", "Mute", "Mirror" };

    public static readonly string[] TeleportKinds = { "None", "Warp", "Escape", "Elf" };

    /// <summary>Returns options, adding an "unknown" entry for a value not in the list so it is preserved.</summary>
    public static ByteOption[] WithValue(ByteOption[] options, int value)
    {
        if (options.Any(o => o.Value == value)) return options;
        return options.Append(new ByteOption(value, $"{value:X2}  (unknown)")).ToArray();
    }
}
