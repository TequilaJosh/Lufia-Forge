namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Small Lufia II patches (US ROM, from the game's code 2026-10-23; see docs/lufia2/settings.md). Each is a list of
/// same-size changes: added only when every place still has the game's bytes, taken out only when every place has
/// the patch's.
/// </summary>
public static class L2Patches
{
    public sealed record Change(int At, byte[] Original, byte[] Patched);

    public sealed record Patch(string Id, string Name, string Summary, string Description, Change[] Changes)
    {
        public bool IsApplied(RomBuffer rom) => Changes.All(c => Has(rom, c.At, c.Patched));
        public bool IsOriginal(RomBuffer rom) => Changes.All(c => Has(rom, c.At, c.Original));
        public void Apply(RomBuffer rom) { if (IsOriginal(rom)) foreach (var c in Changes) rom.WriteBytes(c.At, c.Patched); }
        public void Remove(RomBuffer rom) { if (IsApplied(rom)) foreach (var c in Changes) rom.WriteBytes(c.At, c.Original); }
    }

    private static bool Has(RomBuffer rom, int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
    private static Change C(int at, string original, string patched) => new(at, Bytes(original), Bytes(patched));
    private static byte[] Bytes(string hex) => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

    public static readonly Patch[] All =
    {
        new("l2-avoid", "Hold B to avoid random battles",
            "No random battle starts on the world map while B is held.",
            "The world map's step check ($86:9C71) is called through a small routine put in the place of the developers' unused " +
            "debug keys ($86:96BC): while B is held it clears the step counter, so no battle can start. Without B the game is as before.",
            new[] { C(0x3125F, "F0 03", "80 03"), C(0x31279, "20 71 9C", "20 BC 96"),
                    C(0x316BC, "AD E8 11 29 0F C9 08 D0 09 AD EA 11 29", "A5 47 89 80 F0 04 9C E3 11 60 4C 71 9C") }),
        new("l2-worldfast", "Fast walking on the world map",
            "Walk the world map at twice the speed.",
            "The world map walking speed ($1204) goes from 2 to 4 pixels a frame, in the three places the game sets it.",
            new[] { C(0x34C4A, "02", "04"), C(0x320BB, "02", "04"), C(0x320C8, "02", "04") }),
        new("l2-townfast", "Fast walking in towns and dungeons",
            "Always walk at twice the speed outside the world map.",
            "Turns on the developers' run code ($83:FC69) for every step. Can't be added with \"Hold R to run\".",
            new[] { C(0x1FC6C, "F0 1C A9 80 25 47 F0 08", "EA EA A9 80 25 47 EA EA") }),
        new("l2-run", "Hold R to run",
            "In towns and dungeons, hold R to walk twice as fast.",
            "Turns on the developers' run code ($83:FC69), which ran with B; R is used because B opens a window on the field. " +
            "Can't be added with \"Fast walking in towns and dungeons\".",
            new[] { C(0x1FC6C, "F0 1C A9 80 25 47 F0 08", "EA EA A9 10 25 46 F0 08") }),
        new("l2-freeinn", "Free inns",
            "Staying at an inn costs nothing.",
            "The inn command (event 17) no longer takes the gold ($80:A2E1 skips its JSR $BF43). Any gold check an inn's script makes before it stays.",
            new[] { C(0x2325, "20 43 BF", "EA EA EA") }),
        new("l2-fastip", "Faster IP gauge",
            "The IP gauge fills about twice as fast when a hero is hit.",
            "IP gained on taking damage ($85:D56F): the GUT part is multiplied by $FF instead of $80, and the flat 12 becomes 24.",
            new[] { C(0x2D587, "80", "FF"), C(0x2D5B8, "0C", "18") }),
    };

    public static Patch Get(string id) => All.First(p => p.Id == id);
}
