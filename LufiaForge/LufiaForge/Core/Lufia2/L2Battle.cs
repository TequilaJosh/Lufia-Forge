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
    /// <summary>The game's spell table ($95:FA5B) and its 40 spells. Every spell read goes through $81:F446 (LDA $95FA5B,X /
    /// CLC / ADC #$FA5B at file 0xF450), so the table's place is read from there: Lufia Forge moves the spells to the added
    /// space to add new ones (<see cref="L2Magic"/>).</summary>
    public const int SpellTable = 0xAFA5B, SpellCountOriginal = 0x28, SpellNameLength = 8;
    private const int SpellTableRef = 0xF450, SpellBankRef = 0xF3FC;

    /// <summary>How many spells the ROM has (40, more once Lufia Forge added some).</summary>
    public static int SpellCount(RomBuffer rom) => L2Magic.SpellCount(rom);

    /// <summary>A spell record (0x13 bytes): name 8, +08 flags (40 usable in the menu, 80 in battle), +09 (an icon or
    /// group, read by no code found), +0A who can learn it (hero bits), +0B who it targets (<see cref="L2Magic.Targets"/>),
    /// +0C MP, +0F price, +11 its effect script (offset from the record).</summary>
    public sealed class Spell
    {
        public int Index { get; init; }
        public int Offset { get; init; }
        public string Name { get; set; } = "";
        public byte Flags { get; set; }
        public byte LearnBy { get; set; }
        public byte Target { get; set; }
        public ushort Mp { get; set; }
        public ushort Price { get; set; }
    }

    /// <summary>File offset of spell <paramref name="i"/>'s record, read the way the game does ($81:F446 and the bank
    /// its readers set, LDA #$95 at $81:F3FB).</summary>
    public static int SpellOffset(RomBuffer rom, int i)
    {
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(SpellTableRef + 1));
        int adc = rom.ReadUInt16Le(SpellTableRef + 6), bank = rom.ReadByte(SpellBankRef);
        return L2Rom.SnesToFile(bank << 16 | (rom.ReadUInt16Le(table + i * 2) + adc) & 0xFFFF);
    }

    public static Spell ReadSpell(RomBuffer rom, int i)
    {
        int o = SpellOffset(rom, i);
        return new Spell
        {
            Index = i, Offset = o, Name = L2Rom.Name(rom, o, SpellNameLength), Flags = rom.ReadByte(o + 8), LearnBy = rom.ReadByte(o + 0x0A),
            Target = rom.ReadByte(o + 0x0B), Mp = rom.ReadUInt16Le(o + 0x0C), Price = rom.ReadUInt16Le(o + 0x0F),
        };
    }

    public static List<Spell> Spells(RomBuffer rom) => Enumerable.Range(0, SpellCount(rom)).Select(i => ReadSpell(rom, i)).ToList();

    public static void WriteSpell(RomBuffer rom, Spell s)
    {
        L2Rom.WriteName(rom, s.Offset, SpellNameLength, s.Name);
        rom.WriteByte(s.Offset + 8, s.Flags); rom.WriteByte(s.Offset + 0x0A, s.LearnBy); rom.WriteByte(s.Offset + 0x0B, s.Target);
        rom.WriteUInt16Le(s.Offset + 0x0C, s.Mp); rom.WriteUInt16Le(s.Offset + 0x0F, s.Price);
    }

    // ---------------------------------------------------------------- monsters
    public const int MonsterTable = 0xB05C0, MonsterCount = 0xE0, MonsterNameLength = 13;
    /// <summary>The game reads the monster table with LDA long,X at $81:FB27 (file 0xFB27); hacks that move it (the
    /// Expansion patch in Frue, Spekkio and Kureji Lufia puts it at $D0:A000) change that operand, so it's read from there.</summary>
    private const int MonsterTableRef = 0xFB27;

    /// <summary>File offset of the monster table in this ROM.</summary>
    public static int MonsterTableAt(RomBuffer rom) =>
        rom.ReadByte(MonsterTableRef) == 0xBF ? L2Rom.SnesToFile((int)rom.ReadUInt24Le(MonsterTableRef + 1)) : MonsterTable;

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
        int table = MonsterTableAt(rom);
        int o = table + rom.ReadUInt16Le(table + i * 2);
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
    /// <summary>The game's IP attack table ($84:8F10, u16 pointers into bank $84, read by $81:F45E with the attack's number
    /// counted from 1, as items give it, and by $82:FF15). Lufia Forge moves the table and the records to add attacks and
    /// longer names (<see cref="L2Magic"/>), so their place is read from the code.</summary>
    public const int IpTable = 0x20F10, IpCountOriginal = 0xA8;
    private const int IpTableRef = 0xF463, IpBankRef = 0xC1C1;

    /// <summary>How many IP attacks the ROM has (168, more once Lufia Forge added some).</summary>
    public static int IpCount(RomBuffer rom) => L2Magic.IpCount(rom);

    /// <summary>An IP attack record: u16 effect number (into the effect scripts' table), +2 animation, +3 the target
    /// cursor's look, +4 who it targets, +5 IP cost (out of 255), +6 its name, 00 ends it.</summary>
    public sealed class IpAttack
    {
        public int Index { get; init; }
        public int Offset { get; init; }
        public string Name { get; set; } = "";
        public int NameLength { get; init; }
        public ushort Effect { get; set; }
        public byte Animation { get; set; }
        public byte Cursor { get; set; }
        public byte Targeting { get; set; }
        public byte Cost { get; set; }
    }

    /// <summary>File offset of IP attack <paramref name="i"/>'s record (counted from 0).</summary>
    public static int IpOffset(RomBuffer rom, int i)
    {
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(IpTableRef + 1));
        int bank = rom.ReadByte(IpBankRef + 3);
        return L2Rom.SnesToFile(bank << 16 | rom.ReadUInt16Le(table + i * 2));
    }

    public static IpAttack ReadIp(RomBuffer rom, int i)
    {
        int o = IpOffset(rom, i);
        var raw = rom.ReadUntil(o + 6, 0, 32);
        return new IpAttack
        {
            Index = i, Offset = o, Name = L2Rom.Name(rom, o + 6, raw.Length), NameLength = raw.Length, Effect = rom.ReadUInt16Le(o),
            Animation = rom.ReadByte(o + 2), Cursor = rom.ReadByte(o + 3), Targeting = rom.ReadByte(o + 4), Cost = rom.ReadByte(o + 5),
        };
    }

    public static List<IpAttack> IpAttacks(RomBuffer rom) => Enumerable.Range(0, IpCount(rom)).Select(i => ReadIp(rom, i)).ToList();

    /// <summary>Writes the attack's numbers (the name goes through <see cref="L2Magic.SetIpName"/>, as it can need room).</summary>
    public static void WriteIp(RomBuffer rom, IpAttack a)
    {
        rom.WriteByte(a.Offset + 2, a.Animation); rom.WriteByte(a.Offset + 3, a.Cursor);
        rom.WriteByte(a.Offset + 4, a.Targeting); rom.WriteByte(a.Offset + 5, a.Cost);
    }
}
