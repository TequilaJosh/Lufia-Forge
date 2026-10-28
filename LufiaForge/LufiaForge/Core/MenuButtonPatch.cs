namespace LufiaForge.Core;

/// <summary>
/// Lufia 1: the button that opens the menu on the field (B in the game). <c>$01:823E</c> reads it:
/// <c>LDA $31 / AND #$80 / BEQ / TRB $35 / BEQ</c>: held (<c>$30</c>/<c>$31</c>: buttons low/high byte) and just pressed
/// (<c>$34</c>/<c>$35</c>, the same bits; TRB clears the press). The idea and the button masks are FrankCG92's menu
/// button remap (shared on the Lufia Forge Discord); this also points the just-pressed check at the matching byte, which
/// the buttons of the low byte (X, L, R) need.
/// </summary>
public static class MenuButtonPatch
{
    public const int Site = 0x823E;   // $01:823E

    /// <summary>The buttons: name, byte (0x30 low: A X L R, 0x31 high: B Y Select Start ...) and bit.</summary>
    public static readonly (string Name, byte Reg, byte Mask)[] Buttons =
    {
        ("B (the game's)", 0x31, 0x80), ("Y", 0x31, 0x40), ("Select", 0x31, 0x20), ("Start", 0x31, 0x10),
        ("X", 0x30, 0x40), ("L", 0x30, 0x20), ("R", 0x30, 0x10),
    };

    /// <summary>The button the ROM opens the menu with (index into <see cref="Buttons"/>), or -1 when the code there
    /// isn't the game's check (another patch changed it).</summary>
    public static int Current(RomBuffer rom)
    {
        if (rom.Length < Site + 8) return -1;
        var b = rom.ReadBytes(Site, 8);
        if (b[0] != 0xA5 || b[2] != 0x29 || b[4] != 0xF0 || b[5] != 0x1B || b[6] != 0x14) return -1;
        // the press byte FrankCG92's patch leaves at $35 counts too
        if (b[1] is not (0x30 or 0x31) || b[7] is not (0x34 or 0x35)) return -1;
        return Array.FindIndex(Buttons, x => x.Reg == b[1] && x.Mask == b[3]);
    }

    /// <summary>Makes button <paramref name="index"/> open the menu.</summary>
    public static void Set(RomBuffer rom, int index)
    {
        if (Current(rom) < 0) throw new InvalidOperationException("The menu button check at $01:823E isn't the game's, so it can't be changed.");
        var (_, reg, mask) = Buttons[index];
        rom.WriteBytes(Site, new byte[] { 0xA5, reg, 0x29, mask, 0xF0, 0x1B, 0x14, (byte)(reg + 4) });
    }
}
