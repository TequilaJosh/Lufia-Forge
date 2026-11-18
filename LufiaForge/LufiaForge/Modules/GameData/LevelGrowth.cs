using LufiaForge.Core;

namespace LufiaForge.Modules.GameData;

// how the four main characters level up, copied from the game's level-up code ($08:E601)
//
// EXP: each character record holds the EXP of the first level-up (+17) and an increment (+20, both 24-bit).
// after every level-up the increment grows by itself shifted right by k, where k = how many of the
// character's 5 thresholds (table $08:ED11, compared with growth counter + 1) have been passed. past the last
// one it stops growing. the next level-up comes at next + increment
//
// stats: a growth counter (record +1, +1 per level-up) picks a band of 16 level-ups (counter / 16) and how far
// into it the character is (counter % 16 + 1). the stat = the value at the start of the band plus
// (band value x that / 16), plus a random -2..+2 when it isn't 0, and it never goes down. the band values
// (table $08:EC69: 6 stats x 7 bands x 4 characters) are what a stat gains over each band of 16 level-ups
//
// checked against real save data: EXP thresholds and stats match the game exactly at the levels I tested
public static class LevelGrowth
{
    public const int GrowthTable = 0x46C69;      // $08:EC69
    public const int ExpThresholds = 0x46D11;    // $08:ED11, 5 bytes per character
    public const int Characters = 4;             // Hero, Lufia, Aguro, Jerin (the others never level up)
    public const int Bands = 7;
    public static readonly string[] StatNames = { "HP", "MP", "STR", "INT", "AGL", "MGR" };
    // where each growth stat sits in the character record (u16 at +2: HP MP AGL INT STR MGR)
    public static readonly int[] RecordStatOffset = { 2, 4, 10, 8, 6, 12 };

    public static int GrowthOffset(int stat, int band, int character) => GrowthTable + stat * 28 + band * 4 + character;

    public sealed record Level(int Number, long ExpToReach, int[] Stats);

    // everything that decides one character's levels
    public sealed class Setup
    {
        public int StartLevel, Counter;
        public long StartExp, FirstLevelExp, Increment;
        public int[] Thresholds = new int[5];
        public int[] StartStats = new int[6];         // HP MP STR INT AGL MGR
        public int[,] Growth = new int[6, Bands];     // stat, band
    }

    public static Setup Read(RomBuffer rom, int character)
    {
        int record = GameDataOffsets.CharacterTable + character * GameDataOffsets.CharacterStride;
        var setup = new Setup
        {
            StartLevel = rom.ReadByte(record), Counter = rom.ReadByte(record + 1),
            StartExp = rom.ReadUInt24Le(record + 14), FirstLevelExp = rom.ReadUInt24Le(record + 17), Increment = rom.ReadUInt24Le(record + 20),
        };
        for (int i = 0; i < 5; i++) setup.Thresholds[i] = rom.ReadByte(ExpThresholds + character * 5 + i);
        for (int stat = 0; stat < 6; stat++)
        {
            setup.StartStats[stat] = rom.ReadUInt16Le(record + RecordStatOffset[stat]);
            for (int band = 0; band < Bands; band++) setup.Growth[stat, band] = rom.ReadByte(GrowthOffset(stat, band, character));
        }
        return setup;
    }

    // writes the growth parts (not the starting stats, the Characters tab edits those)
    public static void Write(RomBuffer rom, int character, Setup setup)
    {
        int record = GameDataOffsets.CharacterTable + character * GameDataOffsets.CharacterStride;
        rom.WriteByte(record + 1, (byte)Math.Clamp(setup.Counter, 0, 255));
        WriteU24(rom, record + 17, setup.FirstLevelExp);
        WriteU24(rom, record + 20, setup.Increment);
        for (int i = 0; i < 5; i++) rom.WriteByte(ExpThresholds + character * 5 + i, (byte)Math.Clamp(setup.Thresholds[i], 0, 255));
        for (int stat = 0; stat < 6; stat++)
            for (int band = 0; band < Bands; band++) rom.WriteByte(GrowthOffset(stat, band, character), (byte)Math.Clamp(setup.Growth[stat, band], 0, 255));
    }

    private static void WriteU24(RomBuffer rom, int offset, long value)
    {
        value = Math.Clamp(value, 0, 0xFFFFFF);
        rom.WriteBytes(offset, new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16) });
    }

    // levels from the character's starting level up to 99, without the random -2..+2
    public static List<Level> Simulate(Setup setup)
    {
        int level = setup.StartLevel, counter = setup.Counter;
        long next = setup.FirstLevelExp, increment = setup.Increment;
        var stats = (int[])setup.StartStats.Clone(); var bandStart = (int[])setup.StartStats.Clone();
        var levels = new List<Level> { new(level, setup.StartExp, (int[])stats.Clone()) };
        while (level < 99)
        {
            // stats ($08:E692 / $08:E73D)
            int band = (counter >> 4) & 0x0F, progress = (counter & 0x0F) + 1;
            for (int stat = 0; stat < 6; stat++)
            {
                int gain = band < Bands ? setup.Growth[stat, band] : 0;
                int value = bandStart[stat] + ((gain * progress + 8) >> 4);
                if (value > stats[stat]) stats[stat] = value;
                if (progress == 16) bandStart[stat] = value;
            }
            levels.Add(new Level(level + 1, next, (int[])stats.Clone()));
            level++;
            counter = (counter + 1) & 0xFF;
            // EXP ($08:E6B9)
            int passed = 0; long growth = increment;
            while (true)
            {
                if (counter + 1 < setup.Thresholds[passed]) break;
                if (passed == 4) { growth = 0; break; }
                growth >>= 1; passed++;
            }
            increment += growth;
            next += increment;
        }
        return levels;
    }
}
