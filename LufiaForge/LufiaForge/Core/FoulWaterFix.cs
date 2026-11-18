namespace LufiaForge.Core;

/// <summary>
/// Lufia 1: Foul Water with fast walking (Colmines92's fix, shared on the Lufia Forge Discord). Foul Water brings a
/// battle every step for a number of steps ($16BA, counted down at <c>$01:8CA5</c>). Away from the world map the game
/// counts a step only while the walking speed ($0789) is the slow one, 10 (dungeons; towns walk at 20), so with fast
/// walking (every map at 20) the count never went down and the effect lasted until the game was reset. The fix counts
/// on maps that have random battles instead ($078B, set by the map's setup script and read by the encounter code at
/// <c>$01:883B</c>): <c>LDA $078B / BEQ</c> past the count / <c>NOP NOP</c> in place of <c>LDA $0789 / CMP #$10 / BNE</c>.
/// It goes in with Fast walking and out with it. Lufia Restored has its own fix there (it reads $078A).
/// </summary>
public static class FoulWaterFix
{
    public const int Site = 0x8C99;   // $01:8C99
    private static readonly byte[] Original = { 0xAD, 0x89, 0x07, 0xC9, 0x10, 0xD0, 0x08 };
    private static readonly byte[] Fixed = { 0xAD, 0x8B, 0x07, 0xF0, 0x0A, 0xEA, 0xEA };

    private static bool Holds(RomBuffer rom, byte[] bytes) =>
        rom.Length >= Site + bytes.Length && rom.ReadBytes(Site, bytes.Length).AsSpan().SequenceEqual(bytes);

    public static bool IsApplied(RomBuffer rom) => Holds(rom, Fixed);
    public static bool IsOriginal(RomBuffer rom) => Holds(rom, Original);

    /// <summary>Puts the fix in (<paramref name="on"/>) or the game's bytes back; only where the place holds one of the
    /// two (another hack's code there is left alone). True when something was written.</summary>
    public static bool Set(RomBuffer rom, bool on)
    {
        if (on && IsOriginal(rom)) { rom.WriteBytes(Site, Fixed); return true; }
        if (!on && IsApplied(rom)) { rom.WriteBytes(Site, Original); return true; }
        return false;
    }

    /// <summary>The ROM walks fast (the Fast walking setting) but still has the game's count: the fix belongs in.</summary>
    public static bool Needed(RomBuffer rom) =>
        rom.Length > 0x9A4D && !rom.IsLufia2 && rom.ReadByte(0x9829) == 0x20 &&
        rom.ReadBytes(0x9A49, 4).AsSpan().SequenceEqual(new byte[] { 0xEA, 0xEA, 0xA9, 0x20 }) && IsOriginal(rom);
}
