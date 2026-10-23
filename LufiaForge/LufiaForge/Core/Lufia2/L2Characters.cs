namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II heroes (US ROM, found 2026-10-22/23). Seven heroes, in this order everywhere: Maxim, Selan, Guy, Artea,
/// Tia, Dekar, Lexis.
/// <list type="bullet">
/// <item>Starting stats: 7 records of 7 u16 at $97:B93C (file 0xBB93C): HP, MP, STR, AGL, INT, GUT, MGR (Maxim's
/// checked in the game). Right after them, at 0xBB99E, a u16 per hero is the EXP curve's base (0x0014 = 20.0).</item>
/// <item>Growth: 7 tables of 0x70 bytes at $97:B62C (file 0xBB62C), 14 rows of 8 bytes (HP MP STR AGL INT GUT MGR,
/// EXP growth); row k/8 is used going from level k to k+1. $81:F87F: stat = start + (sum of the rows' bytes) &gt;&gt; 4;
/// a level-up ($81:F7ED) adds 0-4 at random (not GUT). EXP ($81:F9E9): v = base, for k = 2..L v += v * (EXPg + 1)
/// &gt;&gt; 8, summed; needed = sum - 10, and 9,999,999 from level 98.</item>
/// <item>New game ($81:EC56 reads $85:B2A1, file 0x2B2A1): party size, 4 hero ids, u16 gold, u16 ?, the inventory
/// (u16: item in the low 9 bits, count above, 0000 ends it), then a record per hero: name (6), u16 level, u16 ?,
/// spells (FF ends them), u24 EXP, 7 u16 equipment (weapon, armor, shield, helmet, ring, jewel, Maxim's field
/// tool), u8 IP. Every hero's state is set here; joining (event 2B xx) only adds them to the party.</item>
/// <item>The Ancient Cave's reset ($85:B395, file 0x2B395) has the same records without names, and 9 u16 bonuses
/// (HP, MP, ATP, DFP, STR, AGL, INT, GUT, MGR) instead of the equipment.</item>
/// </list>
/// </summary>
public static class L2Characters
{
    public const int Table = 0xBB93C, Count = 7, RecordSize = 14, ExpBase = 0xBB99E;
    public const int Growth = 0xBB62C, GrowthSize = 0x70, GrowthRows = 13, MaxLevel = 99;
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

    // ------------------------------------------------------------------ growth and EXP

    /// <summary>Growth row <paramref name="row"/> of hero <paramref name="c"/>: 7 stat bytes and the EXP growth.</summary>
    public static int GrowthAt(int c, int row) => Growth + c * GrowthSize + row * 8;

    public static int[] ReadGrowth(RomBuffer rom, int c, int row) => rom.ReadBytes(GrowthAt(c, row), 8).Select(b => (int)b).ToArray();

    public static void WriteGrowth(RomBuffer rom, int c, int row, int[] values)
    {
        for (int i = 0; i < 8 && i < values.Length; i++) rom.WriteByte(GrowthAt(c, row) + i, (byte)Math.Clamp(values[i], 0, 255));
    }

    /// <summary>Stats at <paramref name="level"/> as the formula gives them (the game adds 0-4 at random to each but GUT).</summary>
    public static int[] StatsAt(RomBuffer rom, int c, int level)
    {
        var start = Read(rom, c);
        var acc = new int[7];
        for (int k = 1; k < Math.Min(level, MaxLevel); k++)
            for (int i = 0; i < 7; i++) acc[i] += rom.ReadByte(GrowthAt(c, k / 8) + i);
        return start.Select((s, i) => s + (acc[i] >> 4)).ToArray();
    }

    /// <summary>Total EXP needed to go from <paramref name="level"/> to the next.</summary>
    public static int ExpNeeded(RomBuffer rom, int c, int level)
    {
        if (level >= 98) return 9_999_999;
        int w = rom.ReadUInt16Le(ExpBase + c * 2);
        long v = (w & 0xFF) << 8 | w >> 8, acc = v;
        for (int k = 2; k <= level; k++)
        {
            long m = rom.ReadByte(GrowthAt(c, k / 8) + 7) + 1;
            v = (v + (v * m >> 8)) & 0xFFFFFFFF;
            acc += v;
        }
        return (int)Math.Min((acc >> 8) - 10, 9_999_999);
    }

    /// <summary>The EXP base as a number (20.0 = 20): the low byte is the whole part, the high byte the 1/256ths.</summary>
    public static double ReadExpBase(RomBuffer rom, int c) { int w = rom.ReadUInt16Le(ExpBase + c * 2); return (w & 0xFF) + (w >> 8) / 256.0; }

    public static void WriteExpBase(RomBuffer rom, int c, double value)
    {
        int whole = Math.Clamp((int)value, 0, 255), frac = Math.Clamp((int)Math.Round((value - whole) * 256), 0, 255);
        rom.WriteUInt16Le(ExpBase + c * 2, (ushort)(whole | frac << 8));
    }

    // ------------------------------------------------------------------ new game

    public sealed class HeroRecord
    {
        public int At { get; init; }
        public bool HasName { get; init; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public int Unknown { get; set; }
        /// <summary>File offset of the first spell (the list keeps its length).</summary>
        public int SpellsAt { get; init; }
        public List<int> Spells { get; init; } = new();
        public int Exp { get; set; }
        /// <summary>7 equipment items (new game) or 9 bonuses (Ancient Cave).</summary>
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
        /// <summary>(item, count) pairs; the list keeps its length.</summary>
        public List<(int Item, int Count)> Inventory { get; init; } = new();
        public List<HeroRecord> Heroes { get; init; } = new();
    }

    private static HeroRecord ReadRecord(RomBuffer rom, int at, bool named)
    {
        int p = at;
        string name = "";
        if (named) { name = System.Text.Encoding.ASCII.GetString(rom.ReadBytes(p, 6)).TrimEnd('\0'); p += 6; }
        int level = rom.ReadUInt16Le(p), unk = rom.ReadUInt16Le(p + 2);
        p += 4;
        int spellsAt = p;
        var spells = new List<int>();
        while (rom.ReadByte(p) != 0xFF && spells.Count < 36) spells.Add(rom.ReadByte(p++));
        p++;
        int exp = rom.ReadByte(p) | rom.ReadByte(p + 1) << 8 | rom.ReadByte(p + 2) << 16;
        p += 3;
        int n = named ? 7 : 9;
        var words = Enumerable.Range(0, n).Select(i => (int)rom.ReadUInt16Le(p + i * 2)).ToArray();
        p += n * 2;
        int ip = rom.ReadByte(p++);
        return new HeroRecord { At = at, HasName = named, Name = name, Level = level, Unknown = unk, SpellsAt = spellsAt, Spells = spells, Exp = exp, Words = words, Ip = ip, Size = p - at };
    }

    public static Start ReadStart(RomBuffer rom)
    {
        var s = new Start { PartySize = rom.ReadByte(NewGame), Gold = rom.ReadUInt16Le(NewGame + 5), Unknown = rom.ReadUInt16Le(NewGame + 7) };
        for (int i = 0; i < 4; i++) s.Party[i] = rom.ReadByte(NewGame + 1 + i);
        int p = NewGame + 9;
        for (int w; (w = rom.ReadUInt16Le(p)) != 0 && s.Inventory.Count < 96; p += 2) s.Inventory.Add((w & 0x1FF, w >> 9));
        p += 2;
        for (int c = 0; c < Count; c++) { var r = ReadRecord(rom, p, true); s.Heroes.Add(r); p += r.Size; }
        return s;
    }

    public static List<HeroRecord> ReadCave(RomBuffer rom)
    {
        var list = new List<HeroRecord>();
        int p = CaveReset;
        for (int c = 0; c < Count; c++) { var r = ReadRecord(rom, p, false); list.Add(r); p += r.Size; }
        return list;
    }

    /// <summary>Writes a hero record back in place (the name is cut to 6 letters; the spell list keeps its length).</summary>
    public static void WriteRecord(RomBuffer rom, HeroRecord r)
    {
        int p = r.At;
        if (r.HasName)
        {
            var name = new byte[6];
            var src = System.Text.Encoding.ASCII.GetBytes(r.Name.Length > 6 ? r.Name[..6] : r.Name);
            Array.Copy(src, name, src.Length);
            rom.WriteBytes(p, name);
            p += 6;
        }
        rom.WriteUInt16Le(p, (ushort)Math.Clamp(r.Level, 1, MaxLevel));
        rom.WriteUInt16Le(p + 2, (ushort)r.Unknown);
        for (int i = 0; i < r.Spells.Count; i++) rom.WriteByte(r.SpellsAt + i, (byte)r.Spells[i]);
        p = r.SpellsAt + r.Spells.Count + 1;
        int exp = Math.Clamp(r.Exp, 0, 9_999_999);
        rom.WriteByte(p, (byte)exp); rom.WriteByte(p + 1, (byte)(exp >> 8)); rom.WriteByte(p + 2, (byte)(exp >> 16));
        p += 3;
        for (int i = 0; i < r.Words.Length; i++) rom.WriteUInt16Le(p + i * 2, (ushort)r.Words[i]);
        rom.WriteByte(p + r.Words.Length * 2, (byte)Math.Clamp(r.Ip, 0, 255));
    }

    /// <summary>Writes the party, gold and inventory back (the inventory keeps its length; counts 1-127).</summary>
    public static void WriteStart(RomBuffer rom, Start s)
    {
        rom.WriteByte(NewGame, (byte)Math.Clamp(s.PartySize, 1, 4));
        for (int i = 0; i < 4; i++) rom.WriteByte(NewGame + 1 + i, (byte)s.Party[i]);
        rom.WriteUInt16Le(NewGame + 5, (ushort)Math.Clamp(s.Gold, 0, 0xFFFF));
        rom.WriteUInt16Le(NewGame + 7, (ushort)s.Unknown);
        for (int i = 0; i < s.Inventory.Count; i++)
            rom.WriteUInt16Le(NewGame + 9 + i * 2, (ushort)(s.Inventory[i].Item & 0x1FF | Math.Clamp(s.Inventory[i].Count, 1, 127) << 9));
    }
}
