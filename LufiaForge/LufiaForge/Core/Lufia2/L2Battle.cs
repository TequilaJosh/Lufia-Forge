namespace LufiaForge.Core.Lufia2;

// lufia 2's battle data: items, spells, monsters, IP attacks (US addresses, record layouts from LEdit's L2Item,
// L2Spell, L2Monster, L2IP, checked on the US rom). records vary in length, so an edit changes a record's fields
// where they are (no record grows or moves): names, prices, stats, flags, and the optional words a record already has
public static class L2Battle
{
    // items
    public const int ItemTable = 0xB4F69, ItemCount = 0x1D3, ItemNames = 0xF47E8, ItemNameLength = 12;

    // which optional words follow an item's 13-byte head (bit order), and their names
    public static readonly (int Bit, string Name)[] ItemWords =
    {
        (0x0001, "Menu script"), (0x0002, "Battle script"), (0x0004, "Weapon script"), (0x0008, "Armor script"),
        (0x0010, "ATP"), (0x0020, "DFP"), (0x0040, "STR"), (0x0080, "AGL"), (0x0100, "INT"), (0x0200, "GUT"), (0x0400, "MGR"),
        (0x2000, "Battle animation"), (0x4000, "Unknown"), (0x8000, "IP attack"),
    };
    public static readonly string[] EquipTypes = { "Weapon", "Armor", "Shield", "Helmet", "Ring", "Jewel" };
    public static readonly string[] ItemUse = { "Used on a hero", "Equipment", "Boots?", "Cursed", "Fruit", "Can't be dropped", "Usable in the menu", "Usable in battle" };

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
        // the optional words the record has, by bit (see ItemWords)
        public Dictionary<int, ushort> Values { get; init; } = new();
    }

    public static int ItemOffset(RomBuffer rom, int i) => L2Rom.InBank(ItemTable, rom.ReadUInt16Le(ItemTable + i * 2));

    // how many entries a table of u16 offsets has, read the way the game lays them out: the records start right
    // after the table, so it ends where the first record starts. an entry that points back into the table (0000, or
    // bytes a hack keeps there) ends it too. target gives entry i's record (file offset)
    internal static int TableLength(RomBuffer rom, int table, int max, Func<int, int> target)
    {
        int first = target(0);
        if (first <= table) return 0;
        int count = Math.Min(max, (first - table) / 2);
        for (int i = 0; i < count; i++)
        {
            int recordAt = target(i);
            if (recordAt < table + count * 2 || recordAt >= rom.Length) return i;
        }
        return count;
    }

    // how many items the rom has (its item table's length, 0x1D3 in the US game, ids are 9 bits)
    public static int ItemCountIn(RomBuffer rom) => TableLength(rom, ItemTable, 0x200, i => ItemOffset(rom, i));

    public static Item ReadItem(RomBuffer rom, int i)
    {
        int offset = ItemOffset(rom, i);
        ushort words = rom.ReadUInt16Le(offset + 9);
        var values = new Dictionary<int, ushort>();
        int wordAt = offset + 0x0D;
        // a word for every bit set, the way the game's loader reads them ($81:F217, bits 0800 and 1000 too, which we
        // used to skip, so a hack's item with them got read from the wrong bytes). the words of the mask's bytes 11-12
        // come after these, so we don't need them to place these
        for (int bit = 1; bit <= 0x8000; bit <<= 1)
        {
            if ((words & bit) == 0) continue;
            values[bit] = rom.ReadUInt16Le(wordAt); wordAt += 2;
        }
        return new Item
        {
            Index = i, Offset = offset, Name = L2Rom.Name(rom, ItemNames + i * ItemNameLength, ItemNameLength),
            Use = rom.ReadByte(offset), Class = rom.ReadByte(offset + 1), Target = rom.ReadByte(offset + 2), Icon = rom.ReadByte(offset + 3),
            MapSprite = rom.ReadByte(offset + 4), Price = rom.ReadUInt16Le(offset + 5), EquipType = rom.ReadByte(offset + 7), EquipBy = rom.ReadByte(offset + 8),
            Words = words, Values = values,
        };
    }

    public static List<Item> Items(RomBuffer rom) => Enumerable.Range(0, ItemCountIn(rom)).Select(i => ReadItem(rom, i)).ToList();

    public static void WriteItem(RomBuffer rom, Item item)
    {
        int offset = item.Offset;
        L2Rom.WriteName(rom, ItemNames + item.Index * ItemNameLength, ItemNameLength, item.Name);
        rom.WriteByte(offset, item.Use); rom.WriteByte(offset + 1, item.Class); rom.WriteByte(offset + 2, item.Target); rom.WriteByte(offset + 3, item.Icon);
        rom.WriteByte(offset + 4, item.MapSprite); rom.WriteUInt16Le(offset + 5, item.Price); rom.WriteByte(offset + 7, item.EquipType); rom.WriteByte(offset + 8, item.EquipBy);
        int wordAt = offset + 0x0D;
        for (int bit = 1; bit <= 0x8000; bit <<= 1)
        {
            if ((item.Words & bit) == 0) continue;
            if (item.Values.TryGetValue(bit, out var value)) rom.WriteUInt16Le(wordAt, value);
            wordAt += 2;
        }
    }

    // spells

    // the game's spell table ($95:FA5B) and its 40 spells. every spell read goes through $81:F446 (LDA $95FA5B,X /
    // CLC / ADC #$FA5B at file 0xF450), so we read the table's place from there. we move the spells to the added
    // space to add new ones (L2Magic)
    public const int SpellTable = 0xAFA5B, SpellCountOriginal = 0x28, SpellNameLength = 8;
    private const int SpellTableRef = 0xF450, SpellBankRef = 0xF3FC;

    // how many spells the rom has (40, more once we added some)
    public static int SpellCount(RomBuffer rom) => L2Magic.SpellCount(rom);

    // how many spells the spell table has, read from the table (the fan hacks made room for 64 entries but keep
    // other bytes in the 24 past the game's 40, so those end it)
    internal static int SpellTableLength(RomBuffer rom)
    {
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(SpellTableRef + 1));
        return TableLength(rom, table, 0xFF, i => SpellOffset(rom, i));
    }

    // a spell record (0x13 bytes): name 8, +08 flags (40 usable in the menu, 80 in battle), +09 (an icon or group,
    // no code found reads it), +0A who can learn it (hero bits), +0B who it targets (L2Magic.Targets), +0C MP,
    // +0F price, +11 its effect script (offset from the record)
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

    // file offset of spell i's record, read the way the game does ($81:F446 and the bank its readers set, LDA #$95
    // at $81:F3FB)
    public static int SpellOffset(RomBuffer rom, int i)
    {
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(SpellTableRef + 1));
        int adc = rom.ReadUInt16Le(SpellTableRef + 6), bank = rom.ReadByte(SpellBankRef);
        return L2Rom.SnesToFile(bank << 16 | (rom.ReadUInt16Le(table + i * 2) + adc) & 0xFFFF);
    }

    public static Spell ReadSpell(RomBuffer rom, int i)
    {
        int offset = SpellOffset(rom, i);
        return new Spell
        {
            Index = i, Offset = offset, Name = L2Rom.Name(rom, offset, SpellNameLength), Flags = rom.ReadByte(offset + 8), LearnBy = rom.ReadByte(offset + 0x0A),
            Target = rom.ReadByte(offset + 0x0B), Mp = rom.ReadUInt16Le(offset + 0x0C), Price = rom.ReadUInt16Le(offset + 0x0F),
        };
    }

    public static List<Spell> Spells(RomBuffer rom) => Enumerable.Range(0, SpellCount(rom)).Select(i => ReadSpell(rom, i)).ToList();

    public static void WriteSpell(RomBuffer rom, Spell spell)
    {
        WriteSpellName(rom, spell);
        rom.WriteByte(spell.Offset + 8, spell.Flags); rom.WriteByte(spell.Offset + 0x0A, spell.LearnBy); rom.WriteByte(spell.Offset + 0x0B, spell.Target);
        rom.WriteUInt16Le(spell.Offset + 0x0C, spell.Mp); rom.WriteUInt16Le(spell.Offset + 0x0F, spell.Price);
    }

    // writes a spell name the way the rom has them: space padded (the US game) or ended by 00 (the fan hacks pad
    // their names with 00, an 8-letter name has none, so the other spells tell us which). an unchanged name is left alone
    private static void WriteSpellName(RomBuffer rom, Spell spell)
    {
        if (L2Rom.Name(rom, spell.Offset, SpellNameLength) == spell.Name) return;
        bool HasZero(int nameAt) => Array.IndexOf(rom.ReadBytes(nameAt, SpellNameLength), (byte)0) >= 0;
        if (!HasZero(spell.Offset) && !Enumerable.Range(0, SpellCount(rom)).Any(i => HasZero(SpellOffset(rom, i))))
        {
            L2Rom.WriteName(rom, spell.Offset, SpellNameLength, spell.Name);
            return;
        }
        var bytes = new byte[SpellNameLength];
        for (int i = 0; i < SpellNameLength && i < spell.Name.Length; i++) bytes[i] = L2Charset.ToByte(rom, spell.Name[i]) ?? (byte)' ';
        rom.WriteBytes(spell.Offset, bytes);
    }

    // monsters
    public const int MonsterTable = 0xB05C0, MonsterCount = 0xE0, MonsterNameLength = 13;
    // the game reads the monster table with LDA long,X at $81:FB27 (file 0xFB27). hacks that move it (the Expansion
    // patch in Frue, Spekkio and Kureji Lufia puts it at $D0:A000) change that operand, so we read it from there
    private const int MonsterTableRef = 0xFB27;

    // file offset of the monster table in this rom
    public static int MonsterTableAt(RomBuffer rom) =>
        rom.ReadByte(MonsterTableRef) == 0xBF ? L2Rom.SnesToFile((int)rom.ReadUInt24Le(MonsterTableRef + 1)) : MonsterTable;

    // file offset of monster i's record: table + its u16, added inside the bank like the game does
    // ($81:FCE2: LDA table,Y / ADC #table)
    public static int MonsterAt(RomBuffer rom, int i)
    {
        int table = MonsterTableAt(rom);
        return L2Rom.InBank(table, rom.ReadUInt16Le(table + i * 2));
    }

    // how many monsters the rom's table has. a monster id is a byte (the game does AND #$00FF before reading the
    // table), so up to 256. the US game has 0xE0 (224), Spekkio and Kureji Lufia add monsters past that, Frue Lufia
    // and The Wrath of Foomy fill the table to 256 with entries that all point at one blank record
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
        // where its drop entry (03 ii cc) is, -1 if it has none
        public int DropAt { get; init; } = -1;
        public int DropItem { get; set; } = -1;
        // the drop chance byte: 0-127 (the game's chance, out of 128 as far as we know)
        public int DropChance { get; set; }
        public int AttackScript { get; init; } = -1;
        public int DefenseScript { get; init; } = -1;
        // a lower id whose table entry points at the same record (an edit changes both), or -1
        public int SameAs { get; init; } = -1;
        // a slot the hack filled with a placeholder: its entry shares a record with other ids and that record has no
        // name the game could show (Frue Lufia's and Foomy's E0-FF, a few of Spekkio Lufia's)
        public bool Unused { get; init; }
    }

    // whether a monster name reads as the game's text (letters, ended by 00 or padded with spaces)
    private static bool IsName(RomBuffer rom, int nameAt)
    {
        int length = 0;
        for (; length < MonsterNameLength; length++)
        {
            byte b = rom.ReadByte(nameAt + length);
            if (b == 0) break;
            if (L2Charset.ToChar(rom, b) == null) return false;
        }
        return length > 0 && rom.ReadByte(nameAt) != 0x20;
    }

    public static Monster ReadMonster(RomBuffer rom, int i)
    {
        int table = MonsterTableAt(rom);
        int offset = MonsterAt(rom, i);
        int dropAt = -1, item = -1, chance = 0, attackScript = -1, defenseScript = -1;
        for (int tagAt = offset + 0x21, guard = 0; guard < 16; guard++)
        {
            byte tag = rom.ReadByte(tagAt);
            if (tag == 0) break;
            if (tag == 3) { dropAt = tagAt; byte ii = rom.ReadByte(tagAt + 1), cc = rom.ReadByte(tagAt + 2); item = ii | (cc & 1) << 8; chance = cc >> 1; }
            else if (tag == 7) attackScript = L2Rom.InBank(offset, rom.ReadUInt16Le(tagAt + 1));
            else if (tag == 8) defenseScript = L2Rom.InBank(offset, rom.ReadUInt16Le(tagAt + 1));
            tagAt += 3;
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
            SameAs = sameAs, Unused = sharing > 0 && !IsName(rom, offset),
            Index = i, Offset = offset, Name = L2Rom.Name(rom, offset, MonsterNameLength), Level = rom.ReadByte(offset + 0x0D),
            Sprite = rom.ReadByte(offset + 0x0F), Palette = rom.ReadByte(offset + 0x10),
            Hp = rom.ReadUInt16Le(offset + 0x11), Mp = rom.ReadUInt16Le(offset + 0x13), Atp = rom.ReadUInt16Le(offset + 0x15), Dfp = rom.ReadUInt16Le(offset + 0x17),
            Agl = rom.ReadByte(offset + 0x19), Int = rom.ReadByte(offset + 0x1A), Gut = rom.ReadByte(offset + 0x1B), Mgr = rom.ReadByte(offset + 0x1C),
            Exp = rom.ReadUInt16Le(offset + 0x1D), Gold = rom.ReadUInt16Le(offset + 0x1F),
            DropAt = dropAt, DropItem = item, DropChance = chance, AttackScript = attackScript, DefenseScript = defenseScript,
        };
    }

    public static List<Monster> Monsters(RomBuffer rom) => Enumerable.Range(0, MonsterCountIn(rom)).Select(i => ReadMonster(rom, i)).ToList();

    // how many letters monster m's name can have. the US game pads every name to 13 with spaces. the fan hacks end
    // names with 00 and keep script bytes in the rest of the field, so a name there can only grow up to the first
    // byte a script uses (L2AiScripts.MonsterScriptBytes)
    public static int NameRoom(RomBuffer rom, Monster m, ISet<int>? scriptBytes = null)
    {
        int free = NameFree(rom, m, scriptBytes);
        // a shorter name needs its 00 too, the script's own byte can be it if that's a 00 (an End)
        return free == MonsterNameLength || rom.ReadByte(m.Offset + free) == 0 ? free : Math.Max(0, free - 1);
    }

    // the name field's bytes before the first one a script uses
    private static int NameFree(RomBuffer rom, Monster monster, ISet<int>? scriptBytes)
    {
        scriptBytes ??= L2AiScripts.MonsterScriptBytes(rom);
        for (int k = 0; k < MonsterNameLength; k++)
            if (scriptBytes.Contains(monster.Offset + k)) return k;
        return MonsterNameLength;
    }

    // writes a monster name the way the record has it: space padded (the US game) or ended by 00 (the fan hacks),
    // never past NameRoom. bytes after a 00 are left alone
    private static void WriteMonsterName(RomBuffer rom, Monster monster)
    {
        int offset = monster.Offset;
        var old = rom.ReadBytes(offset, MonsterNameLength);
        if (L2Rom.Name(rom, offset, MonsterNameLength) == monster.Name) return;
        int zero = Array.IndexOf(old, (byte)0);
        if (zero < 0) { L2Rom.WriteName(rom, offset, MonsterNameLength, monster.Name); return; }
        var used = L2AiScripts.MonsterScriptBytes(rom);
        int room = NameRoom(rom, monster, used), free = NameFree(rom, monster, used);
        string name = monster.Name.Length > room ? monster.Name[..room] : monster.Name;
        var bytes = new List<byte>(name.Select(c => L2Charset.ToByte(rom, c) ?? (byte)' '));
        if (bytes.Count < free) bytes.Add(0);
        rom.WriteBytes(offset, bytes.ToArray());
    }

    public static void WriteMonster(RomBuffer rom, Monster m)
    {
        int offset = m.Offset;
        WriteMonsterName(rom, m);
        rom.WriteByte(offset + 0x0D, m.Level); rom.WriteByte(offset + 0x0F, m.Sprite); rom.WriteByte(offset + 0x10, m.Palette);
        rom.WriteUInt16Le(offset + 0x11, m.Hp); rom.WriteUInt16Le(offset + 0x13, m.Mp); rom.WriteUInt16Le(offset + 0x15, m.Atp); rom.WriteUInt16Le(offset + 0x17, m.Dfp);
        rom.WriteByte(offset + 0x19, m.Agl); rom.WriteByte(offset + 0x1A, m.Int); rom.WriteByte(offset + 0x1B, m.Gut); rom.WriteByte(offset + 0x1C, m.Mgr);
        rom.WriteUInt16Le(offset + 0x1D, m.Exp); rom.WriteUInt16Le(offset + 0x1F, m.Gold);
        if (m.DropAt > 0 && m.DropItem >= 0)
        {
            rom.WriteByte(m.DropAt + 1, (byte)m.DropItem);
            rom.WriteByte(m.DropAt + 2, (byte)(Math.Clamp(m.DropChance, 0, 127) << 1 | (m.DropItem >> 8 & 1)));
        }
    }

    // IP attacks

    // the game's IP attack table ($84:8F10, u16 pointers into bank $84, read by $81:F45E with the attack's number
    // counted from 1 like items give it, and by $82:FF15). we move the table and the records to add attacks and
    // longer names (L2Magic), so their place gets read from the code
    public const int IpTable = 0x20F10, IpCountOriginal = 0xA8;
    private const int IpTableRef = 0xF463, IpBankRef = 0xC1C1;

    // how many IP attacks the rom has (168, more once we added some)
    public static int IpCount(RomBuffer rom) => L2Magic.IpCount(rom);

    // an IP attack record: u16 effect number (into the effect scripts' table), +2 animation, +3 the target cursor's
    // look, +4 who it targets, +5 IP cost (out of 255), +6 its name, 00 ends it
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

    // file offset of IP attack i's record (counted from 0)
    public static int IpOffset(RomBuffer rom, int i)
    {
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(IpTableRef + 1));
        int bank = rom.ReadByte(IpBankRef + 3);
        return L2Rom.SnesToFile(bank << 16 | rom.ReadUInt16Le(table + i * 2));
    }

    public static IpAttack ReadIp(RomBuffer rom, int i)
    {
        int offset = IpOffset(rom, i);
        var rawName = rom.ReadUntil(offset + 6, 0, 32);
        return new IpAttack
        {
            Index = i, Offset = offset, Name = L2Rom.Name(rom, offset + 6, rawName.Length), NameLength = rawName.Length, Effect = rom.ReadUInt16Le(offset),
            Animation = rom.ReadByte(offset + 2), Cursor = rom.ReadByte(offset + 3), Targeting = rom.ReadByte(offset + 4), Cost = rom.ReadByte(offset + 5),
        };
    }

    public static List<IpAttack> IpAttacks(RomBuffer rom) => Enumerable.Range(0, IpCount(rom)).Select(i => ReadIp(rom, i)).ToList();

    // writes the attack's numbers (the name goes through L2Magic.SetIpName, since it can need room)
    public static void WriteIp(RomBuffer rom, IpAttack attack)
    {
        rom.WriteByte(attack.Offset + 2, attack.Animation); rom.WriteByte(attack.Offset + 3, attack.Cursor);
        rom.WriteByte(attack.Offset + 4, attack.Targeting); rom.WriteByte(attack.Offset + 5, attack.Cost);
    }
}
