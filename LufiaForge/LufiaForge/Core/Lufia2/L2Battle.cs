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

    public static int ItemOffset(RomBuffer rom, int i) => L2Rom.InBank(ItemTable, rom.ReadUInt16Le(ItemTable + i * 2));

    /// <summary>How many entries a table of u16 offsets has, read the way the game lays them out: the records start right
    /// after the table, so it ends where the first record starts; an entry that points back into the table (0000, or bytes
    /// a hack keeps there) ends it too. <paramref name="target"/> gives entry i's record (file offset).</summary>
    internal static int TableLength(RomBuffer rom, int table, int max, Func<int, int> target)
    {
        int first = target(0);
        if (first <= table) return 0;
        int n = Math.Min(max, (first - table) / 2);
        for (int i = 0; i < n; i++)
        {
            int t = target(i);
            if (t < table + n * 2 || t >= rom.Length) return i;
        }
        return n;
    }

    /// <summary>How many items the ROM has (its item table's length: 0x1D3 in the US game; ids are 9 bits).</summary>
    public static int ItemCountIn(RomBuffer rom) => TableLength(rom, ItemTable, 0x200, i => ItemOffset(rom, i));

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

    public static List<Item> Items(RomBuffer rom) => Enumerable.Range(0, ItemCountIn(rom)).Select(i => ReadItem(rom, i)).ToList();

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

    /// <summary>How many spells the spell table has, read from the table (the fan hacks made room for 64 entries but keep
    /// other bytes in the 24 past the game's 40, so those end it).</summary>
    internal static int SpellTableLength(RomBuffer rom)
    {
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(SpellTableRef + 1));
        return TableLength(rom, table, 0xFF, i => SpellOffset(rom, i));
    }

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

    /// <summary>File offset of monster <paramref name="i"/>'s record: table + its u16, added in the bank as the game does
    /// ($81:FCE2: LDA table,Y / ADC #table).</summary>
    public static int MonsterAt(RomBuffer rom, int i)
    {
        int table = MonsterTableAt(rom);
        return L2Rom.InBank(table, rom.ReadUInt16Le(table + i * 2));
    }

    /// <summary>How many monsters the ROM's table has. A monster id is a byte (the game takes AND #$00FF before reading the
    /// table), so up to 256: the US game has 0xE0 (224); Spekkio and Kureji Lufia add monsters past that, Frue Lufia and
    /// The Wrath of Foomy fill the table to 256 with entries that all point at one blank record.</summary>
    public static int MonsterCountIn(RomBuffer rom) => TableLength(rom, MonsterTableAt(rom), 0x100, i => MonsterAt(rom, i));

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
        /// <summary>A lower id whose table entry points at the same record (an edit changes both), or -1.</summary>
        public int SameAs { get; init; } = -1;
        /// <summary>A slot the hack filled with a placeholder: its entry shares a record with other ids and that record has
        /// no name the game could show (Frue Lufia's and Foomy's E0-FF, a few of Spekkio Lufia's).</summary>
        public bool Unused { get; init; }
    }

    /// <summary>Whether a monster name reads as the game's text (letters, ended by 00 or padded with spaces).</summary>
    private static bool IsName(RomBuffer rom, int o)
    {
        int n = 0;
        for (; n < MonsterNameLength; n++)
        {
            byte b = rom.ReadByte(o + n);
            if (b == 0) break;
            if (L2Charset.ToChar(rom, b) == null) return false;
        }
        return n > 0 && rom.ReadByte(o) != 0x20;
    }

    public static Monster ReadMonster(RomBuffer rom, int i)
    {
        int table = MonsterTableAt(rom);
        int o = MonsterAt(rom, i);
        int dropAt = -1, item = -1, chance = 0, atk = -1, def = -1;
        for (int p = o + 0x21, guard = 0; guard < 16; guard++)
        {
            byte tag = rom.ReadByte(p);
            if (tag == 0) break;
            if (tag == 3) { dropAt = p; byte ii = rom.ReadByte(p + 1), cc = rom.ReadByte(p + 2); item = ii | (cc & 1) << 8; chance = cc >> 1; }
            else if (tag == 7) atk = L2Rom.InBank(o, rom.ReadUInt16Le(p + 1));
            else if (tag == 8) def = L2Rom.InBank(o, rom.ReadUInt16Le(p + 1));
            p += 3;
        }
        int entry = rom.ReadUInt16Le(table + i * 2), sameAs = -1, sharing = 0, count = MonsterCountIn(rom);
        for (int k = 0; k < count; k++)
        {
            if (k == i || rom.ReadUInt16Le(table + k * 2) != entry) continue;
            sharing++;
            if (k < i && sameAs < 0) sameAs = k;
        }
        return new Monster
        {
            SameAs = sameAs, Unused = sharing > 0 && !IsName(rom, o),
            Index = i, Offset = o, Name = L2Rom.Name(rom, o, MonsterNameLength), Level = rom.ReadByte(o + 0x0D),
            Sprite = rom.ReadByte(o + 0x0F), Palette = rom.ReadByte(o + 0x10),
            Hp = rom.ReadUInt16Le(o + 0x11), Mp = rom.ReadUInt16Le(o + 0x13), Atp = rom.ReadUInt16Le(o + 0x15), Dfp = rom.ReadUInt16Le(o + 0x17),
            Agl = rom.ReadByte(o + 0x19), Int = rom.ReadByte(o + 0x1A), Gut = rom.ReadByte(o + 0x1B), Mgr = rom.ReadByte(o + 0x1C),
            Exp = rom.ReadUInt16Le(o + 0x1D), Gold = rom.ReadUInt16Le(o + 0x1F),
            DropAt = dropAt, DropItem = item, DropChance = chance, AttackScript = atk, DefenseScript = def,
        };
    }

    public static List<Monster> Monsters(RomBuffer rom) => Enumerable.Range(0, MonsterCountIn(rom)).Select(i => ReadMonster(rom, i)).ToList();

    /// <summary>How many letters monster <paramref name="m"/>'s name may have. The US game pads every name to 13 with
    /// spaces; the fan hacks end names with 00 and keep script bytes in the rest of the field, so a name there may grow
    /// only up to the first byte a script uses (<see cref="L2AiScripts.MonsterScriptBytes"/>).</summary>
    public static int NameRoom(RomBuffer rom, Monster m, ISet<int>? scriptBytes = null)
    {
        int free = NameFree(rom, m, scriptBytes);
        // a shorter name needs its 00 too: the script's own byte can be it when that is a 00 (an End)
        return free == MonsterNameLength || rom.ReadByte(m.Offset + free) == 0 ? free : Math.Max(0, free - 1);
    }

    /// <summary>The name field's bytes before the first one a script uses.</summary>
    private static int NameFree(RomBuffer rom, Monster m, ISet<int>? scriptBytes)
    {
        scriptBytes ??= L2AiScripts.MonsterScriptBytes(rom);
        for (int k = 0; k < MonsterNameLength; k++)
            if (scriptBytes.Contains(m.Offset + k)) return k;
        return MonsterNameLength;
    }

    /// <summary>Writes a monster name the way the record has it: space padded (the US game) or ended by 00 (the fan
    /// hacks), never past <see cref="NameRoom"/>; bytes after a 00 are left as they are.</summary>
    private static void WriteMonsterName(RomBuffer rom, Monster m)
    {
        int o = m.Offset;
        var old = rom.ReadBytes(o, MonsterNameLength);
        if (L2Rom.Name(rom, o, MonsterNameLength) == m.Name) return;
        int zero = Array.IndexOf(old, (byte)0);
        if (zero < 0) { L2Rom.WriteName(rom, o, MonsterNameLength, m.Name); return; }
        var used = L2AiScripts.MonsterScriptBytes(rom);
        int room = NameRoom(rom, m, used), free = NameFree(rom, m, used);
        string name = m.Name.Length > room ? m.Name[..room] : m.Name;
        var bytes = new List<byte>(name.Select(c => L2Charset.ToByte(rom, c) ?? (byte)' '));
        if (bytes.Count < free) bytes.Add(0);
        rom.WriteBytes(o, bytes.ToArray());
    }

    public static void WriteMonster(RomBuffer rom, Monster m)
    {
        int o = m.Offset;
        WriteMonsterName(rom, m);
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
