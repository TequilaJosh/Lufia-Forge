using LufiaForge.Core;

namespace LufiaForge.Modules.GameData;

/// <summary>
/// How the four main characters level up, reproduced from the game's level-up code ($08:E601):
///
/// EXP: each character record holds the EXP of the first level-up (+17) and an increment (+20, both 24-bit).
/// After every level-up the increment grows by itself shifted right by k, where k is how many of the
/// character's 5 thresholds (table $08:ED11, compared with growth counter + 1) have been passed; past the last
/// one it stops growing. The next level-up comes at next + increment.
///
/// Stats: a growth counter (record +1, +1 per level-up) picks a band of 16 level-ups (counter / 16) and how far
/// into it the character is (counter % 16 + 1). The stat is the value at the start of the band plus
/// (band value x that / 16), plus a random -2..+2 when it isn't 0; it never goes down. The band values
/// (table $08:EC69: 6 stats x 7 bands x 4 characters) are what a stat gains over each band of 16 level-ups.
///
/// Checked against real save data: EXP thresholds and stats match the game exactly at the levels tested.
/// </summary>
public static class LevelGrowth
{
    public const int GrowthTable = 0x46C69;      // $08:EC69
    public const int ExpThresholds = 0x46D11;    // $08:ED11, 5 bytes per character
    public const int Characters = 4;             // Hero, Lufia, Aguro, Jerin (the others never level up)
    public const int Bands = 7;
    public static readonly string[] StatNames = { "HP", "MP", "STR", "INT", "AGL", "MGR" };
    /// <summary>Where each growth stat sits in the character record (u16 at +2: HP MP AGL INT STR MGR).</summary>
    public static readonly int[] RecordStatOffset = { 2, 4, 10, 8, 6, 12 };

    public static int GrowthOffset(int stat, int band, int ch) => GrowthTable + stat * 28 + band * 4 + ch;

    public sealed record Level(int Number, long ExpToReach, int[] Stats);

    /// <summary>Everything that decides one character's levels.</summary>
    public sealed class Setup
    {
        public int StartLevel, Counter;
        public long StartExp, FirstLevelExp, Increment;
        public int[] Thresholds = new int[5];
        public int[] StartStats = new int[6];         // HP MP STR INT AGL MGR
        public int[,] Growth = new int[6, Bands];     // stat, band
    }

    public static Setup Read(RomBuffer rom, int ch)
    {
        int rec = GameDataOffsets.CharacterTable + ch * GameDataOffsets.CharacterStride;
        var p = new Setup
        {
            StartLevel = rom.ReadByte(rec), Counter = rom.ReadByte(rec + 1),
            StartExp = rom.ReadUInt24Le(rec + 14), FirstLevelExp = rom.ReadUInt24Le(rec + 17), Increment = rom.ReadUInt24Le(rec + 20),
        };
        for (int i = 0; i < 5; i++) p.Thresholds[i] = rom.ReadByte(ExpThresholds + ch * 5 + i);
        for (int s = 0; s < 6; s++)
        {
            p.StartStats[s] = rom.ReadUInt16Le(rec + RecordStatOffset[s]);
            for (int b = 0; b < Bands; b++) p.Growth[s, b] = rom.ReadByte(GrowthOffset(s, b, ch));
        }
        return p;
    }

    /// <summary>Writes the growth parts (not the starting stats, which the Characters tab edits).</summary>
    public static void Write(RomBuffer rom, int ch, Setup p)
    {
        int rec = GameDataOffsets.CharacterTable + ch * GameDataOffsets.CharacterStride;
        rom.WriteByte(rec + 1, (byte)Math.Clamp(p.Counter, 0, 255));
        WriteU24(rom, rec + 17, p.FirstLevelExp);
        WriteU24(rom, rec + 20, p.Increment);
        for (int i = 0; i < 5; i++) rom.WriteByte(ExpThresholds + ch * 5 + i, (byte)Math.Clamp(p.Thresholds[i], 0, 255));
        for (int s = 0; s < 6; s++)
            for (int b = 0; b < Bands; b++) rom.WriteByte(GrowthOffset(s, b, ch), (byte)Math.Clamp(p.Growth[s, b], 0, 255));
    }

    private static void WriteU24(RomBuffer rom, int at, long v)
    {
        v = Math.Clamp(v, 0, 0xFFFFFF);
        rom.WriteBytes(at, new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16) });
    }

    /// <summary>Levels from the character's starting level up to 99, without the random -2..+2.</summary>
    public static List<Level> Simulate(Setup p)
    {
        int lv = p.StartLevel, counter = p.Counter;
        long next = p.FirstLevelExp, inc = p.Increment;
        var live = (int[])p.StartStats.Clone(); var band = (int[])p.StartStats.Clone();
        var levels = new List<Level> { new(lv, p.StartExp, (int[])live.Clone()) };
        while (lv < 99)
        {
            // stats ($08:E692 / $08:E73D)
            int row = (counter >> 4) & 0x0F, m = (counter & 0x0F) + 1;
            for (int s = 0; s < 6; s++)
            {
                int t = row < Bands ? p.Growth[s, row] : 0;
                int value = band[s] + ((t * m + 8) >> 4);
                if (value > live[s]) live[s] = value;
                if (m == 16) band[s] = value;
            }
            levels.Add(new Level(lv + 1, next, (int[])live.Clone()));
            lv++;
            counter = (counter + 1) & 0xFF;
            // EXP ($08:E6B9)
            int y = 0; long add = inc;
            while (true)
            {
                if (counter + 1 < p.Thresholds[y]) break;
                if (y == 4) { add = 0; break; }
                add >>= 1; y++;
            }
            inc += add;
            next += inc;
        }
        return levels;
    }
}
