namespace LufiaForge.Core;

// lufia 1: foul water with fast walking (colmines92's fix, shared on the lufia forge discord).
// foul water gives a battle every step for a number of steps ($16BA, counted down at $01:8CA5). off the world map the
// game only counts a step while the walking speed ($0789) is the slow one, 10 (dungeons, towns walk at 20), so with fast
// walking (every map at 20) the count never went down and the effect lasted until you reset.
// the fix counts on maps that have random battles instead ($078B, set by the map's setup script and read by the
// encounter code at $01:883B): LDA $078B / BEQ past the count / NOP NOP in place of LDA $0789 / CMP #$10 / BNE.
// it goes in and out with fast walking. lufia restored has its own fix there (it reads $078A)
public static class FoulWaterFix
{
    public const int Site = 0x8C99;   // $01:8C99
    private static readonly byte[] Original = { 0xAD, 0x89, 0x07, 0xC9, 0x10, 0xD0, 0x08 };
    private static readonly byte[] Fixed = { 0xAD, 0x8B, 0x07, 0xF0, 0x0A, 0xEA, 0xEA };

    private static bool Holds(RomBuffer rom, byte[] bytes) =>
        rom.Length >= Site + bytes.Length && rom.ReadBytes(Site, bytes.Length).AsSpan().SequenceEqual(bytes);

    public static bool IsApplied(RomBuffer rom) => Holds(rom, Fixed);
    public static bool IsOriginal(RomBuffer rom) => Holds(rom, Original);

    // puts the fix in (on) or the game's bytes back, but only if the place has one of the two
    // (another hack's code there is left alone). true when something got written
    public static bool Set(RomBuffer rom, bool on)
    {
        if (on && IsOriginal(rom)) { rom.WriteBytes(Site, Fixed); return true; }
        if (!on && IsApplied(rom)) { rom.WriteBytes(Site, Original); return true; }
        return false;
    }

    // the rom walks fast (the fast walking setting) but still has the game's count, so the fix belongs in
    public static bool Needed(RomBuffer rom) =>
        rom.Length > 0x9A4D && !rom.IsLufia2 && rom.ReadByte(0x9829) == 0x20 &&
        rom.ReadBytes(0x9A49, 4).AsSpan().SequenceEqual(new byte[] { 0xEA, 0xEA, 0xA9, 0x20 }) && IsOriginal(rom);
}
