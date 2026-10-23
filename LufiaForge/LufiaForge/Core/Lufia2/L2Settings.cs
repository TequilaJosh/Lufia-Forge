namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II game-wide settings (US ROM, read from the code 2026-10-23). Each is a same-size change at a place whose
/// original bytes are checked first, so a ROM changed some other way there is left alone.
/// <list type="bullet">
/// <item>Random battles (world map only; dungeon monsters are visible): $86:9C71 rolls r = rand(min(steps + D6, FF))
/// after 6 steps and fights when r &gt;= the byte at 0x31C9C (DC: 25 steps a battle on average; FF: never).</item>
/// <item>Walking: world map $1204 = 2 px a frame (LDX #$0200 at 0x34C48, 0x320B9, 0x320C6; a speed must divide 16
/// pixels). Towns and dungeons: the developers' run code at $83:FC69, called on every step (debug flag $057C, B held
/// = speed 10 instead of 8), turned on with R, or for every step (always fast). Changing the 08 at $83:BD96 alone
/// does nothing in the game: the continuing-walk path keeps the speed. Checked in the game: R runs 7 tiles where
/// walking does 4.</item>
/// <item>Selling: $82:9946 LSR / ADC #0 = half price, rounded up.</item>
/// <item>Level cap: CMP #$63 at 0xF9C0 (level up) and 0x2C94E (after battle). The EXP curve ends at 99.</item>
/// </list>
/// </summary>
public static class L2Settings
{
    public const int EncounterByte = 0x31C9C;
    public static readonly (string Label, byte Value)[] EncounterPresets =
    {
        ("Off: no random battles", 0xFF), ("Very rare (a fifth)", 0xFC), ("Rare (about 0.4)", 0xF6), ("Fewer (half)", 0xF0),
        ("Somewhat fewer (0.65)", 0xE8), ("Normal (the game's)", 0xDC), ("More (1.5x)", 0xD0), ("Many more (2x)", 0xC0),
    };

    private static readonly int[] WorldWalk = { 0x34C48, 0x320B9, 0x320C6 };
    private const int RunAt = 0x1FC6C, SellAt = 0x11946, LevelCap1 = 0xF9BF, LevelCap2 = 0x2C94D;
    private static readonly byte[][] Walks =
    {
        new byte[] { 0xF0, 0x1C, 0xA9, 0x80, 0x25, 0x47, 0xF0, 0x08 },   // normal (run code off)
        new byte[] { 0xEA, 0xEA, 0xA9, 0x10, 0x25, 0x46, 0xF0, 0x08 },   // R held: run
        new byte[] { 0xEA, 0xEA, 0xA9, 0x80, 0x25, 0x47, 0xEA, 0xEA },   // always run
    };
    public static readonly string[] FieldWalkLabels = { "Normal (the game's)", "Normal, hold R to run", "Always fast (twice the speed)" };
    private static readonly byte[] SellHalf = { 0x4A, 0x69, 0x00, 0x00 }, SellFull = { 0xEA, 0xEA, 0xEA, 0xEA }, SellQuarter = { 0x4A, 0x4A, 0xEA, 0xEA };
    public static readonly string[] SellLabels = { "Half the price (the game's)", "The full price", "A quarter of the price" };

    private static bool Is(RomBuffer rom, int at, params byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);

    // ── random battles ──
    public static int EncounterThreshold(RomBuffer rom) => rom.ReadByte(EncounterByte);
    public static bool CanSetEncounters(RomBuffer rom) => rom.ReadByte(EncounterByte - 1) == 0xC9;   // CMP #
    public static void SetEncounters(RomBuffer rom, byte threshold) { if (CanSetEncounters(rom)) rom.WriteByte(EncounterByte, threshold); }

    /// <summary>Average steps between battles for a threshold (as the game rolls; infinite for FF).</summary>
    public static double MeanSteps(byte threshold)
    {
        if (threshold == 0xFF) return double.PositiveInfinity;
        double alive = 1, mean = 0;
        for (int s = 1; s < 2000; s++)
        {
            double p = 0;
            if (s >= 7)
            {
                int a = Math.Min(s - 1 + 0xD6, 0xFF);
                p = Math.Max(0, a - threshold) / (double)a;
            }
            mean += s * alive * p;
            alive *= 1 - p;
        }
        return mean;
    }

    // ── walking ──
    /// <summary>World map walking in pixels a frame (2 normal), or -1 when the code isn't the original.</summary>
    public static int WorldSpeed(RomBuffer rom) => WorldWalk.All(a => Is(rom, a, 0xA2, 0x00)) && WorldWalk.Select(a => rom.ReadByte(a + 2)).Distinct().Count() == 1 ? rom.ReadByte(WorldWalk[0] + 2) : -1;
    public static void SetWorldSpeed(RomBuffer rom, int px) { if (WorldSpeed(rom) > 0 && px is 1 or 2 or 4 or 8) foreach (var a in WorldWalk) rom.WriteByte(a + 2, (byte)px); }

    /// <summary>Town and dungeon walking: 0 normal, 1 run with R, 2 always fast; -1 when the code was changed some other way.</summary>
    public static int FieldWalk(RomBuffer rom) => Array.FindIndex(Walks, w => Is(rom, RunAt, w));
    public static void SetFieldWalk(RomBuffer rom, int mode) { if (FieldWalk(rom) >= 0 && mode is >= 0 and <= 2) rom.WriteBytes(RunAt, Walks[mode]); }

    // ── selling ──
    public static int Sell(RomBuffer rom) => Is(rom, SellAt, SellHalf) ? 0 : Is(rom, SellAt, SellFull) ? 1 : Is(rom, SellAt, SellQuarter) ? 2 : -1;
    public static void SetSell(RomBuffer rom, int mode) { if (Sell(rom) >= 0) rom.WriteBytes(SellAt, mode switch { 1 => SellFull, 2 => SellQuarter, _ => SellHalf }); }

    // ── level cap ──
    public static int LevelCap(RomBuffer rom) => rom.ReadByte(LevelCap1) == 0xC9 && rom.ReadByte(LevelCap2) == 0xC9 && rom.ReadByte(LevelCap1 + 1) == rom.ReadByte(LevelCap2 + 1) ? rom.ReadByte(LevelCap1 + 1) : -1;
    public static void SetLevelCap(RomBuffer rom, int cap) { if (LevelCap(rom) > 0) { cap = Math.Clamp(cap, 2, 99); rom.WriteByte(LevelCap1 + 1, (byte)cap); rom.WriteByte(LevelCap2 + 1, (byte)cap); } }
}
