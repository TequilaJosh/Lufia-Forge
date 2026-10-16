namespace LufiaForge.Core;

/// <summary>
/// Lufia Restored's faster walking, which can be switched back to the game's normal speed. Restored replaces the two
/// places that copy the map's walking speed ($078A) into the current one ($0789) with a call to its routine at
/// $20:81F0, which doubles the slow speed (10: world map and dungeons) to town speed (20), and switches off a check of
/// the slow speed at $01:962A. Switching it off puts the game's own bytes back; the routine stays (unused). Restored's
/// faster ship and airship are the Fast sailing / Fast flying settings (the same bytes).
/// </summary>
public static class RestoredWalk
{
    private static readonly (int Offset, byte[] Original, byte[] Restored)[] Places =
    {
        (0x8A89, new byte[] { 0xAC, 0x8A, 0x07, 0x8C, 0x89, 0x07 }, new byte[] { 0x22, 0xF0, 0x81, 0x20, 0xEA, 0xEA }),
        (0x90C1, new byte[] { 0xAD, 0x8A, 0x07, 0x8D, 0x89, 0x07 }, new byte[] { 0x22, 0xF0, 0x81, 0x20, 0xEA, 0xEA }),
        (0x962A, new byte[] { 0xAD, 0x89, 0x07, 0xC9, 0x10, 0xD0, 0x11 }, new byte[] { 0xEA, 0xEA, 0xEA, 0xEA, 0xEA, 0xEA, 0xEA }),
    };

    /// <summary>Restored's routine at $20:81F0 (file 0x1001F0).</summary>
    private const int Routine = 0x1001F0;
    private static readonly byte[] RoutineBytes = { 0xAD, 0x8A, 0x07, 0xC9, 0x10, 0xD0, 0x01, 0x0A, 0x8D, 0x89, 0x07, 0x6B };

    private static bool Matches(RomBuffer rom, int offset, byte[] bytes) =>
        offset + bytes.Length <= rom.Length && rom.ReadBytes(offset, bytes.Length).AsSpan().SequenceEqual(bytes);

    /// <summary>A ROM with Restored's walking routine, where the three places are either Restored's or the game's.</summary>
    public static bool IsAvailable(RomBuffer rom) =>
        Matches(rom, Routine, RoutineBytes) && Places.All(p => Matches(rom, p.Offset, p.Original) || Matches(rom, p.Offset, p.Restored));

    /// <summary>Restored's faster walking is on.</summary>
    public static bool IsOn(RomBuffer rom) => Places.All(p => Matches(rom, p.Offset, p.Restored));

    public static void Set(RomBuffer rom, bool on)
    {
        if (!IsAvailable(rom)) throw new InvalidOperationException("This ROM doesn't have Lufia Restored's walking routine.");
        foreach (var (offset, original, restored) in Places) rom.WriteBytes(offset, on ? restored : original);
    }
}
