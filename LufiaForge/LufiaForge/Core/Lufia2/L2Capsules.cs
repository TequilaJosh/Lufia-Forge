using System.Text;

namespace LufiaForge.Core.Lufia2;

// lufia 2 capsule monsters (US rom, found 2026-10-23 reading the code, not checked in the game yet)
// - 7 monsters x 5 forms: u16 offsets from the table at $97:DCB8 (file 0xBDCB8), record = monster * 5 + form - 1
//   ($82:C38B). a record: form name (12, space padded), 00, form number, type bit (00 neutral, 01 light, 02 wind,
//   04 water, 08 dark, 10 soil, 20 fire), 3 attack name ids, 3 attack name ids shown once learned, HP, ATP, DFP, STR,
//   AGL, INT, GUT, MGR (bytes, level 1, ATP and DFP don't grow), 6 growth curve ids (HP STR AGL INT GUT MGR), then
//   u16 offsets of the AI (+0x25) and defense (+0x27) battle scripts
// - their names (5 letters each, JELZE FLASH GUSTO ZEPPY DARBI SULLY BLAZE) at $8E:D558 (file 0x75558)
// - growth curves at $A6:F420 (file 0x137420): 0x1E of 8 bytes, one per 16 levels ($82:D31C). a level up in band b
//   adds curve[b] / 16, half that if b equals the form number, a quarter if b is above it
// - attack names: u16 offsets from $A5:DF00 (file 0x12DF00), 0x4E names. attack effects (battle script op 3E):
//   u16 offsets from $97:F63B (file 0xBF63B), 0x54 records: name id, battle animation, battle script
public static class L2Capsules
{
    public const int Table = 0xBDCB8, Monsters = 7, Forms = 5, Count = Monsters * Forms;
    public const int Names = 0x75558, NameLength = 5;
    public const int Curves = 0x137420, CurveCount = 0x1F;
    public const int AttackNames = 0x12DF00, AttackNameCount = 0x4E;
    public const int Attacks = 0xBF63B, AttackCount = 0x54;
    public static readonly string[] Types = { "Neutral", "Light", "Wind", "Water", "Dark", "Soil", "Fire" };
    public static readonly string[] StatNames = { "HP", "ATP", "DFP", "STR", "AGL", "INT", "GUT", "MGR" };
    // which of StatNames each growth curve id is for
    public static readonly string[] CurveStats = { "HP", "STR", "AGL", "INT", "GUT", "MGR" };

    public sealed class Record
    {
        public int Index { get; init; }
        public int At { get; init; }
        public string Name { get; set; } = "";
        public int Form { get; set; }
        public int Type { get; set; }
        public int[] Attacks { get; init; } = new int[3];
        public int[] Learned { get; init; } = new int[3];
        public int[] Stats { get; init; } = new int[8];
        public int[] Curves { get; init; } = new int[6];
        public int AiScript { get; init; }
        public int DefenseScript { get; init; }
        public int Monster => Index / Forms;
    }

    public static int At(RomBuffer rom, int i) => Table + rom.ReadUInt16Le(Table + i * 2);

    public static Record Read(RomBuffer rom, int i)
    {
        int recordAt = At(rom, i);
        var record = new Record
        {
            Index = i, At = recordAt,
            Name = L2Rom.Name(rom, recordAt, 12),
            Form = rom.ReadByte(recordAt + 0x0D), Type = rom.ReadByte(recordAt + 0x0E),
            AiScript = rom.ReadUInt16Le(recordAt + 0x25), DefenseScript = rom.ReadUInt16Le(recordAt + 0x27),
        };
        for (int s = 0; s < 3; s++) { record.Attacks[s] = rom.ReadByte(recordAt + 0x0F + s); record.Learned[s] = rom.ReadByte(recordAt + 0x12 + s); }
        for (int s = 0; s < 8; s++) record.Stats[s] = rom.ReadByte(recordAt + 0x15 + s);
        for (int s = 0; s < 6; s++) record.Curves[s] = rom.ReadByte(recordAt + 0x1D + s);
        return record;
    }

    public static List<Record> All(RomBuffer rom) => Enumerable.Range(0, Count).Select(i => Read(rom, i)).ToList();

    public static void Write(RomBuffer rom, Record record)
    {
        int recordAt = record.At;
        L2Rom.WriteName(rom, recordAt, 12, record.Name);
        rom.WriteByte(recordAt + 0x0D, (byte)Math.Clamp(record.Form, 1, Forms));
        rom.WriteByte(recordAt + 0x0E, (byte)record.Type);
        for (int s = 0; s < 3; s++) { rom.WriteByte(recordAt + 0x0F + s, (byte)record.Attacks[s]); rom.WriteByte(recordAt + 0x12 + s, (byte)record.Learned[s]); }
        for (int s = 0; s < 8; s++) rom.WriteByte(recordAt + 0x15 + s, (byte)Math.Clamp(record.Stats[s], 0, 255));
        for (int s = 0; s < 6; s++) rom.WriteByte(recordAt + 0x1D + s, (byte)Math.Clamp(record.Curves[s], 0, CurveCount - 1));
    }

    // the 7 monsters' default names (5 letters)
    public static string MonsterName(RomBuffer rom, int m) => L2Rom.Name(rom, Names + m * NameLength, NameLength);

    public static void WriteMonsterName(RomBuffer rom, int m, string name)
    {
        L2Rom.WriteName(rom, Names + m * NameLength, NameLength, name);
    }

    public static int[] Curve(RomBuffer rom, int id) => rom.ReadBytes(Curves + id * 8, 8).Select(b => (int)b).ToArray();

    public static void WriteCurve(RomBuffer rom, int id, int[] values)
    {
        for (int i = 0; i < 8 && i < values.Length; i++) rom.WriteByte(Curves + id * 8 + i, (byte)Math.Clamp(values[i], 0, 255));
    }

    // stats at level in the record's form ($82:D31C, the game's final stats add more on top)
    public static int[] StatsAt(RomBuffer rom, Record record, int level)
    {
        var stats = (int[])record.Stats.Clone();
        int[] which = { 0, 3, 4, 5, 6, 7 };
        for (int c = 0; c < 6; c++)
        {
            var curve = Curve(rom, record.Curves[c]);
            int total = 0;
            for (int lv = 1; lv < level; lv++)
            {
                int band = lv >> 4, growth = curve[Math.Min(band, 7)];
                total += band < record.Form ? growth : band == record.Form ? growth >> 1 : growth >> 2;
            }
            stats[which[c]] += total >> 4;
        }
        return stats;
    }

    public static string[] AttackNameList(RomBuffer rom) => Enumerable.Range(0, AttackNameCount)
        .Select(i => new string(rom.ReadUntil(AttackNames + rom.ReadUInt16Le(AttackNames + i * 2), 0, 32).Select(b => L2Charset.ToChar(rom, b) ?? '?').ToArray())).ToArray();

    public static int AttackAt(RomBuffer rom, int i) => Attacks + rom.ReadUInt16Le(Attacks + i * 2);

    // the 0x54 attacks (what battle script op 3E uses), each named by its name id
    public static string[] AttackList(RomBuffer rom)
    {
        var names = AttackNameList(rom);
        return Enumerable.Range(0, AttackCount).Select(i => { int nameId = rom.ReadByte(AttackAt(rom, i)); return nameId < names.Length ? names[nameId] : $"name {nameId:X2}"; }).ToArray();
    }
}
