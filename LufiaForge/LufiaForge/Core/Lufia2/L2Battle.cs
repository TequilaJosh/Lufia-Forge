namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II's battle data: items, spells, monsters, IP attacks (US addresses; record layouts from LEdit's L2Item,
/// L2Spell, L2Monster, L2IP, checked on the US ROM). Records vary in length, so an edit changes the fields of a
/// record where they are (no record grows or moves): names, prices, stats, flags, and the optional words a
/// record already has.
/// </summary>
public static class L2Battle
{
    // ---------------------------------------------------------------- items
    public const int ItemTable = 0xB4F69, ItemCount = 0x1D3, ItemNames = 0xF47E8, ItemNameLength = 12;

    /// <summary>Which optional words follow an item's 13-byte head (bit order), and their names.</summary>
    public static readonly (int Bit, string Name)[] ItemWords =
    {
        (0x0001, "Menu script"), (0x0002, "Battle script"), (0x0004, "Weapon script"), (0x0008, "Armor script"),
        (0x0010, "ATP"), (0x0020, "DFP"), (0x0040, "STR"), (0x0080, "AGL"), (0x0100, "INT"), (0x0200, "GUT"), (0x0400, "MGR"),
        (0x2000, "Battle animation"), (0x4000, "Unknown"), (0x8000, "IP attack"),
    };
    public static readonly string[] EquipTypes = { "Weapon", "Armor", "Shield", "Helmet", "Ring", "Jewel" };
    public static readonly string[] ItemUse = { "Consumable", "Equipment", "Boots?", "Cursed", "Fruit", "Can't be dropped", "Usable in the menu", "Usable in battle" };

    public sealed class Item
    {
        public int Index { get; init; }
        public int Offset { get; init; }
        public string Name { get; set; } = "";
        public byte Use { get; set; }
        public byte Class { get; set; }
        public byte Target { get; set; }
        public byte Icon { get; set; }
        public byte MapSprite { get; set; }
        public ushort Price { get; set; }
        public byte EquipType { get; set; }
        public byte EquipBy { get; set; }
        public ushort Words { get; init; }
        /// <summary>The optional words the record has, by bit (see <see cref="ItemWords"/>).</summary>
        public Dictionary<int, ushort> Values { get; init; } = new();
    }

    public static int ItemOffset(RomBuffer rom, int i) => ItemTable + rom.ReadUInt16Le(ItemTable + i * 2);

    public static Item ReadItem(RomBuffer rom, int i)
    {
        int o = ItemOffset(rom, i);
        ushort words = rom.ReadUInt16Le(o + 9);
        var values = new Dictionary<int, ushort>();
        int p = o + 0x0D;
        for (int bit = 1; bit <= 0x8000; bit <<= 1)
        {
            if ((words & bit) == 0 || bit is 0x0800 or 0x1000) continue;
            values[bit] = rom.ReadUInt16Le(p); p += 2;
        }
        return new Item
        {
            Index = i, Offset = o, Name = L2Rom.Name(rom, ItemNames + i * ItemNameLength, ItemNameLength),
            Use = rom.ReadByte(o), Class = rom.ReadByte(o + 1), Target = rom.ReadByte(o + 2), Icon = rom.ReadByte(o + 3),
            MapSprite = rom.ReadByte(o + 4), Price = rom.ReadUInt16Le(o + 5), EquipType = rom.ReadByte(o + 7), EquipBy = rom.ReadByte(o + 8),
            Words = words, Values = values,
        };
    }

    public static List<Item> Items(RomBuffer rom) => Enumerable.Range(0, ItemCount).Select(i => ReadItem(rom, i)).ToList();

    public static void WriteItem(RomBuffer rom, Item it)
    {
        int o = it.Offset;
        L2Rom.WriteName(rom, ItemNames + it.Index * ItemNameLength, ItemNameLength, it.Name);
        rom.WriteByte(o, it.Use); rom.WriteByte(o + 1, it.Class); rom.WriteByte(o + 2, it.Target); rom.WriteByte(o + 3, it.Icon);
        rom.WriteByte(o + 4, it.MapSprite); rom.WriteUInt16Le(o + 5, it.Price); rom.WriteByte(o + 7, it.EquipType); rom.WriteByte(o + 8, it.EquipBy);
        int p = o + 0x0D;
        for (int bit = 1; bit <= 0x8000; bit <<= 1)
        {
            if ((it.Words & bit) == 0 || bit is 0x0800 or 0x1000) continue;
            if (it.Values.TryGetValue(bit, out var v)) rom.WriteUInt16Le(p, v);
            p += 2;
        }
    }

    // ---------------------------------------------------------------- spells
    public const int SpellTable = 0xAFA5B, SpellCount = 0x28, SpellNameLength = 8;

    public sealed class Spell
    {
        public int Index { get; init; }
        public int Offset { get; init; }
        public string Name { get; set; } = "";
        public byte Flags { get; set; }
        public byte LearnBy { get; set; }
        public ushort Mp { get; set; }
        public ushort Price { get; set; }
    }

    public static Spell ReadSpell(RomBuffer rom, int i)
    {
        int o = SpellTable + rom.ReadUInt16Le(SpellTable + i * 2);
        return new Spell
        {
            Index = i, Offset = o, Name = L2Rom.Name(rom, o, SpellNameLength), Flags = rom.ReadByte(o + 8), LearnBy = rom.ReadByte(o + 0x0A),
            Mp = rom.ReadUInt16Le(o + 0x0C), Price = rom.ReadUInt16Le(o + 0x0F),
        };
    }

    public static List<Spell> Spells(RomBuffer rom) => Enumerable.Range(0, SpellCount).Select(i => ReadSpell(rom, i)).ToList();

    public static void WriteSpell(RomBuffer rom, Spell s)
    {
        L2Rom.WriteName(rom, s.Offset, SpellNameLength, s.Name);
        rom.WriteByte(s.Offset + 8, s.Flags); rom.WriteByte(s.Offset + 0x0A, s.LearnBy);
        rom.WriteUInt16Le(s.Offset + 0x0C, s.Mp); rom.WriteUInt16Le(s.Offset + 0x0F, s.Price);
    }

    // ---------------------------------------------------------------- monsters
    public const int MonsterTable = 0xB05C0, MonsterCount = 0xE0, MonsterNameLength = 13;

    public sealed class Monster
    {
        public int Index { get; init; }
        public int Offset { get; init; }
        public string Name { get; set; } = "";
        public byte Level { get; set; }
        public byte Sprite { get; set; }
        public byte Palette { get; set; }
        public ushort Hp { get; set; }
        public ushort Mp { get; set; }
        public ushort Atp { get; set; }
        public ushort Dfp { get; set; }
        public byte Agl { get; set; }
        public byte Int { get; set; }
        public byte Gut { get; set; }
        public byte Mgr { get; set; }
        public ushort Exp { get; set; }
        public ushort Gold { get; set; }
        /// <summary>Where its drop entry (03 ii cc) is, or -1 without one.</summary>
        public int DropAt { get; init; } = -1;
        public int DropItem { get; set; } = -1;
        /// <summary>The drop chance byte: 0-127 (the game's chance, out of 128 as far as is known).</summary>
        public int DropChance { get; set; }
        public int AttackScript { get; init; } = -1;
        public int DefenseScript { get; init; } = -1;
    }

    public static Monster ReadMonster(RomBuffer rom, int i)
    {
        int o = MonsterTable + rom.ReadUInt16Le(MonsterTable + i * 2);
        int dropAt = -1, item = -1, chance = 0, atk = -1, def = -1;
        for (int p = o + 0x21, guard = 0; guard < 16; guard++)
        {
            byte tag = rom.ReadByte(p);
            if (tag == 0) break;
            if (tag == 3) { dropAt = p; byte ii = rom.ReadByte(p + 1), cc = rom.ReadByte(p + 2); item = ii | (cc & 1) << 8; chance = cc >> 1; }
            else if (tag == 7) atk = o + rom.ReadUInt16Le(p + 1);
            else if (tag == 8) def = o + rom.ReadUInt16Le(p + 1);
            p += 3;
        }
        return new Monster
        {
            Index = i, Offset = o, Name = L2Rom.Name(rom, o, MonsterNameLength), Level = rom.ReadByte(o + 0x0D),
            Sprite = rom.ReadByte(o + 0x0F), Palette = rom.ReadByte(o + 0x10),
            Hp = rom.ReadUInt16Le(o + 0x11), Mp = rom.ReadUInt16Le(o + 0x13), Atp = rom.ReadUInt16Le(o + 0x15), Dfp = rom.ReadUInt16Le(o + 0x17),
            Agl = rom.ReadByte(o + 0x19), Int = rom.ReadByte(o + 0x1A), Gut = rom.ReadByte(o + 0x1B), Mgr = rom.ReadByte(o + 0x1C),
            Exp = rom.ReadUInt16Le(o + 0x1D), Gold = rom.ReadUInt16Le(o + 0x1F),
            DropAt = dropAt, DropItem = item, DropChance = chance, AttackScript = atk, DefenseScript = def,
        };
    }

    public static List<Monster> Monsters(RomBuffer rom) => Enumerable.Range(0, MonsterCount).Select(i => ReadMonster(rom, i)).ToList();

    public static void WriteMonster(RomBuffer rom, Monster m)
    {
        int o = m.Offset;
        L2Rom.WriteName(rom, o, MonsterNameLength, m.Name);
        rom.WriteByte(o + 0x0D, m.Level); rom.WriteByte(o + 0x0F, m.Sprite); rom.WriteByte(o + 0x10, m.Palette);
        rom.WriteUInt16Le(o + 0x11, m.Hp); rom.WriteUInt16Le(o + 0x13, m.Mp); rom.WriteUInt16Le(o + 0x15, m.Atp); rom.WriteUInt16Le(o + 0x17, m.Dfp);
        rom.WriteByte(o + 0x19, m.Agl); rom.WriteByte(o + 0x1A, m.Int); rom.WriteByte(o + 0x1B, m.Gut); rom.WriteByte(o + 0x1C, m.Mgr);
        rom.WriteUInt16Le(o + 0x1D, m.Exp); rom.WriteUInt16Le(o + 0x1F, m.Gold);
        if (m.DropAt > 0 && m.DropItem >= 0)
        {
            rom.WriteByte(m.DropAt + 1, (byte)m.DropItem);
            rom.WriteByte(m.DropAt + 2, (byte)(Math.Clamp(m.DropChance, 0, 127) << 1 | (m.DropItem >> 8 & 1)));
        }
    }

    // ---------------------------------------------------------------- IP attacks
    public const int IpTable = 0x20F10, IpCount = 0xA8;

    public sealed class IpAttack
    {
        public int Index { get; init; }
        public int Offset { get; init; }
        public string Name { get; init; } = "";
        public int NameLength { get; init; }
        public byte Animation { get; set; }
        public byte Cursor { get; set; }
        public byte Targeting { get; set; }
        public byte Cost { get; set; }
    }

    public static IpAttack ReadIp(RomBuffer rom, int i)
    {
        int o = 0x20000 + rom.ReadUInt16Le(IpTable + i * 2) - 0x8000;
        var raw = rom.ReadUntil(o + 6, 0, 32);
        return new IpAttack
        {
            Index = i, Offset = o, Name = L2Rom.Name(rom, o + 6, raw.Length), NameLength = raw.Length,
            Animation = rom.ReadByte(o + 2), Cursor = rom.ReadByte(o + 3), Targeting = rom.ReadByte(o + 4), Cost = rom.ReadByte(o + 5),
        };
    }

    public static List<IpAttack> IpAttacks(RomBuffer rom) => Enumerable.Range(0, IpCount).Select(i => ReadIp(rom, i)).ToList();

    public static void WriteIp(RomBuffer rom, IpAttack a)
    {
        rom.WriteByte(a.Offset + 2, a.Animation); rom.WriteByte(a.Offset + 3, a.Cursor);
        rom.WriteByte(a.Offset + 4, a.Targeting); rom.WriteByte(a.Offset + 5, a.Cost);
    }
}
