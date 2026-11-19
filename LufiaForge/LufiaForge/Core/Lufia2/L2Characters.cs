namespace LufiaForge.Core.Lufia2;

// lufia 2 heroes (US rom, found 2026-10-22/23). seven heroes, always in this order: Maxim, Selan, Guy, Artea, Tia,
// Dekar, Lexis
// - starting stats: 7 records of 7 u16 at $97:B93C (file 0xBB93C): HP, MP, STR, AGL, INT, GUT, MGR (Maxim's checked
//   in the game). right after them, at 0xBB99E, a u16 per hero is the EXP curve's base (0x0014 = 20.0)
// - growth: 7 tables of 0x70 bytes at $97:B62C (file 0xBB62C), 14 rows of 8 bytes (HP MP STR AGL INT GUT MGR, EXP
//   growth). row k/8 is used going from level k to k+1. $81:F87F: stat = start + (sum of the rows' bytes) >> 4, and
//   a level-up ($81:F7ED) adds 0-4 at random (not GUT). EXP ($81:F9E9): v = base, for k = 2..L v += v * (EXPg + 1)
//   >> 8, summed. needed = sum - 10, and 9,999,999 from level 98
// - new game ($81:EC56 reads $85:B2A1, file 0x2B2A1): party size, 4 hero ids, u16 gold, u16 ?, the inventory (u16:
//   item in the low 9 bits, count above, 0000 ends it), then a record per hero: name (6), u16 level, u16 ?, spells
//   (FF ends them), u24 EXP, 7 u16 equipment (weapon, armor, shield, helmet, ring, jewel, Maxim's field tool), u8 IP.
//   every hero's state gets set here, joining (event 2B xx) only adds them to the party
// - the Ancient Cave's reset ($85:B395, file 0x2B395) has the same records without names, and 9 u16 bonuses (HP, MP,
//   ATP, DFP, STR, AGL, INT, GUT, MGR) instead of the equipment
public static class L2Characters
{
    public const int Table = 0xBB93C, Count = 7, RecordSize = 14, ExpBase = 0xBB99E;
    public const int Growth = 0xBB62C, GrowthSize = 0x70, GrowthRows = 13, MaxLevel = 99;

    // the highest level: 99, or 255 with the higher limits (L2Limits)
    public static int MaxLevelOf(RomBuffer rom) => L2Limits.IsApplied(rom) ? L2Limits.MaxLevel : MaxLevel;
    public const int NewGame = 0x2B2A1, CaveReset = 0x2B395;
    public static readonly string[] Stats = { "HP", "MP", "STR", "AGL", "INT", "GUT", "MGR" };
    public static readonly string[] Names = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis" };
    public static readonly string[] EquipSlots = { "Weapon", "Armor", "Shield", "Helmet", "Ring", "Jewel", "Field tool" };
    public static readonly string[] CaveBonuses = { "HP", "MP", "ATP (sets)", "DFP (sets)", "STR", "AGL", "INT", "GUT", "MGR" };

    public static int[] Read(RomBuffer rom, int c) => Enumerable.Range(0, Stats.Length).Select(i => (int)rom.ReadUInt16Le(Table + c * RecordSize + i * 2)).ToArray();

    public static void Write(RomBuffer rom, int c, int[] values)
    {
        for (int i = 0; i < Stats.Length && i < values.Length; i++) rom.WriteUInt16Le(Table + c * RecordSize + i * 2, (ushort)Math.Clamp(values[i], 0, 0xFFFF));
    }

    // growth and EXP

    // growth row row of hero c: 7 stat bytes and the EXP growth
    public static int GrowthAt(int c, int row) => Growth + c * GrowthSize + row * 8;

    public static int[] ReadGrowth(RomBuffer rom, int c, int row) => rom.ReadBytes(GrowthAt(c, row), 8).Select(b => (int)b).ToArray();

    public static void WriteGrowth(RomBuffer rom, int c, int row, int[] values)
    {
        for (int i = 0; i < 8 && i < values.Length; i++) rom.WriteByte(GrowthAt(c, row) + i, (byte)Math.Clamp(values[i], 0, 255));
    }

    // stats at level the way the formula gives them (the game adds 0-4 at random to each but GUT)
    public static int[] StatsAt(RomBuffer rom, int c, int level)
    {
        var start = Read(rom, c);
        var sums = new int[7];
        // past level 104 the game uses row 12 again (with the higher limits, without them the cap is 99)
        for (int k = 1; k < Math.Min(level, MaxLevelOf(rom)); k++)
            for (int i = 0; i < 7; i++) sums[i] += rom.ReadByte(GrowthAt(c, Math.Min(k / 8, GrowthRows - 1)) + i);
        return start.Select((s, i) => s + (sums[i] >> 4)).ToArray();
    }

    // total EXP needed to go from level to the next
    public static int ExpNeeded(RomBuffer rom, int c, int level)
    {
        if (level >= 98) return L2Limits.ExpForLevel(rom, level) ?? 9_999_999;
        int baseWord = rom.ReadUInt16Le(ExpBase + c * 2);
        long value = (baseWord & 0xFF) << 8 | baseWord >> 8, sum = value;
        for (int k = 2; k <= level; k++)
        {
            long growth = rom.ReadByte(GrowthAt(c, k / 8) + 7) + 1;
            value = (value + (value * growth >> 8)) & 0xFFFFFFFF;
            sum += value;
        }
        return (int)Math.Min((sum >> 8) - 10, 9_999_999);
    }

    // the EXP base as a number (20.0 = 20): the low byte is the whole part, the high byte the 1/256ths
    public static double ReadExpBase(RomBuffer rom, int c) { int baseWord = rom.ReadUInt16Le(ExpBase + c * 2); return (baseWord & 0xFF) + (baseWord >> 8) / 256.0; }

    public static void WriteExpBase(RomBuffer rom, int c, double value)
    {
        int whole = Math.Clamp((int)value, 0, 255), frac = Math.Clamp((int)Math.Round((value - whole) * 256), 0, 255);
        rom.WriteUInt16Le(ExpBase + c * 2, (ushort)(whole | frac << 8));
    }

    // new game

    public sealed class HeroRecord
    {
        public int At { get; init; }
        public bool HasName { get; init; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public int Unknown { get; set; }
        // file offset of the first spell (the list keeps its length)
        public int SpellsAt { get; init; }
        public List<int> Spells { get; init; } = new();
        public int Exp { get; set; }
        // 7 equipment items (new game) or 9 bonuses (Ancient Cave)
        public int[] Words { get; init; } = Array.Empty<int>();
        public int Ip { get; set; }
        public int Size { get; init; }
    }

    public sealed class Start
    {
        public int PartySize { get; set; }
        public int[] Party { get; init; } = new int[4];
        public int Gold { get; set; }
        public int Unknown { get; set; }
        // (item, count) pairs, the list keeps its length
        public List<(int Item, int Count)> Inventory { get; init; } = new();
        public List<HeroRecord> Heroes { get; init; } = new();
    }

    private static HeroRecord ReadRecord(RomBuffer rom, int at, bool named)
    {
        int position = at;
        string name = "";
        if (named) { name = System.Text.Encoding.ASCII.GetString(rom.ReadBytes(position, 6)).TrimEnd('\0'); position += 6; }
        int level = rom.ReadUInt16Le(position), unknown = rom.ReadUInt16Le(position + 2);
        position += 4;
        int spellsAt = position;
        var spells = new List<int>();
        while (rom.ReadByte(position) != 0xFF && spells.Count < 36) spells.Add(rom.ReadByte(position++));
        position++;
        int exp = rom.ReadByte(position) | rom.ReadByte(position + 1) << 8 | rom.ReadByte(position + 2) << 16;
        position += 3;
        int wordCount = named ? 7 : 9;
        var words = Enumerable.Range(0, wordCount).Select(i => (int)rom.ReadUInt16Le(position + i * 2)).ToArray();
        position += wordCount * 2;
        int ip = rom.ReadByte(position++);
        return new HeroRecord { At = at, HasName = named, Name = name, Level = level, Unknown = unknown, SpellsAt = spellsAt, Spells = spells, Exp = exp, Words = words, Ip = ip, Size = position - at };
    }

    public static Start ReadStart(RomBuffer rom)
    {
        var start = new Start { PartySize = rom.ReadByte(NewGame), Gold = rom.ReadUInt16Le(NewGame + 5), Unknown = rom.ReadUInt16Le(NewGame + 7) };
        for (int i = 0; i < 4; i++) start.Party[i] = rom.ReadByte(NewGame + 1 + i);
        int position = NewGame + 9;
        for (int word; (word = rom.ReadUInt16Le(position)) != 0 && start.Inventory.Count < 96; position += 2) start.Inventory.Add((word & 0x1FF, word >> 9));
        position += 2;
        for (int c = 0; c < Count; c++) { var record = ReadRecord(rom, position, true); start.Heroes.Add(record); position += record.Size; }
        return start;
    }

    public static List<HeroRecord> ReadCave(RomBuffer rom)
    {
        var list = new List<HeroRecord>();
        int position = CaveReset;
        for (int c = 0; c < Count; c++) { var record = ReadRecord(rom, position, false); list.Add(record); position += record.Size; }
        return list;
    }

    // writes a hero record back in place (the name gets cut to 6 letters, the spell list keeps its length)
    public static void WriteRecord(RomBuffer rom, HeroRecord record)
    {
        int position = record.At;
        if (record.HasName)
        {
            var name = new byte[6];
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(record.Name.Length > 6 ? record.Name[..6] : record.Name);
            Array.Copy(nameBytes, name, nameBytes.Length);
            rom.WriteBytes(position, name);
            position += 6;
        }
        rom.WriteUInt16Le(position, (ushort)Math.Clamp(record.Level, 1, MaxLevelOf(rom)));
        rom.WriteUInt16Le(position + 2, (ushort)record.Unknown);
        for (int i = 0; i < record.Spells.Count; i++) rom.WriteByte(record.SpellsAt + i, (byte)record.Spells[i]);
        position = record.SpellsAt + record.Spells.Count + 1;
        int exp = Math.Clamp(record.Exp, 0, L2Limits.IsApplied(rom) ? 0xFFFFFF : 9_999_999);
        rom.WriteByte(position, (byte)exp); rom.WriteByte(position + 1, (byte)(exp >> 8)); rom.WriteByte(position + 2, (byte)(exp >> 16));
        position += 3;
        for (int i = 0; i < record.Words.Length; i++) rom.WriteUInt16Le(position + i * 2, (ushort)record.Words[i]);
        rom.WriteByte(position + record.Words.Length * 2, (byte)Math.Clamp(record.Ip, 0, 255));
    }

    // writes the party, gold and inventory back (the inventory keeps its length, counts 1-127)
    public static void WriteStart(RomBuffer rom, Start start)
    {
        rom.WriteByte(NewGame, (byte)Math.Clamp(start.PartySize, 1, 4));
        for (int i = 0; i < 4; i++) rom.WriteByte(NewGame + 1 + i, (byte)start.Party[i]);
        rom.WriteUInt16Le(NewGame + 5, (ushort)Math.Clamp(start.Gold, 0, 0xFFFF));
        rom.WriteUInt16Le(NewGame + 7, (ushort)start.Unknown);
        for (int i = 0; i < start.Inventory.Count; i++)
            rom.WriteUInt16Le(NewGame + 9 + i * 2, (ushort)(start.Inventory[i].Item & 0x1FF | Math.Clamp(start.Inventory[i].Count, 1, 127) << 9));
    }
}
