namespace LufiaForge.Core;

// lufia 1: the button that opens the menu on the field (B in the game). $01:823E reads it:
// LDA $31 / AND #$80 / BEQ / TRB $35 / BEQ = held ($30/$31 = buttons low/high byte) and just pressed
// ($34/$35, same bits, TRB clears the press). the idea and the button masks are colmines92's menu button remap
// (shared on the lufia forge discord). we also point the just-pressed check at the matching byte, which the low byte's
// buttons (X, L, R) need
public static class MenuButtonPatch
{
    public const int Site = 0x823E;   // $01:823E

    // the buttons: name, byte (0x30 low: A X L R, 0x31 high: B Y Select Start ...) and bit
    public static readonly (string Name, byte Reg, byte Mask)[] Buttons =
    {
        ("B (the game's)", 0x31, 0x80), ("Y", 0x31, 0x40), ("Select", 0x31, 0x20), ("Start", 0x31, 0x10),
        ("X", 0x30, 0x40), ("L", 0x30, 0x20), ("R", 0x30, 0x10),
    };

    // the button the rom opens the menu with (index into Buttons), or -1 if the code there isn't the game's check
    // (another patch changed it)
    public static int Current(RomBuffer rom)
    {
        if (rom.Length < Site + 8) return -1;
        var code = rom.ReadBytes(Site, 8);
        if (code[0] != 0xA5 || code[2] != 0x29 || code[4] != 0xF0 || code[5] != 0x1B || code[6] != 0x14) return -1;
        // the press byte colmines92's patch leaves at $35 counts too
        if (code[1] is not (0x30 or 0x31) || code[7] is not (0x34 or 0x35)) return -1;
        return Array.FindIndex(Buttons, x => x.Reg == code[1] && x.Mask == code[3]);
    }

    // makes button index open the menu
    public static void Set(RomBuffer rom, int index)
    {
        if (Current(rom) < 0) throw new InvalidOperationException("The menu button check at $01:823E isn't the game's, so it can't be changed.");
        var (_, reg, mask) = Buttons[index];
        rom.WriteBytes(Site, new byte[] { 0xA5, reg, 0x29, mask, 0xF0, 0x1B, 0x14, (byte)(reg + 4) });
    }
}
