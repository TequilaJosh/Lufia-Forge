using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// "Hold L to avoid random battles". The step code ($01:881D) checks the safe steps at $01:8854
/// (LDA $078C / CMP #$04 / BCC skip) before rolling for a battle. The patch replaces that check with a JSL to a
/// routine in the expanded ROM that does the same compare and also skips the roll while the L button is held
/// (controller word at $7E:0030, read from $4218 at $00:827F; L = bit 5 of the low byte). Forced battles
/// ($01:8848, danger >= C0) and scripted battles don't go through this check.
///
/// <code>
/// $01:8854  22 xx xx xx  JSL routine      ; carry clear = no battle this step
/// $01:8858  90 14        BCC $886E
/// $01:885A  EA           NOP
/// routine:  AD 8C 07  LDA $078C   C9 nn  CMP #safe   90 0A  BCC done
///           AF 30 00 7E  LDA $7E0030   29 20  AND #$20   F0 02  BEQ roll   18 CLC  6B RTL
///           roll: 38 SEC  6B RTL     done: 6B RTL
/// </code>
/// </summary>
public static class EncounterPatch
{
    public const int Site = 0x8854;
    private static readonly byte[] Original = { 0xAD, 0x8C, 0x07, 0xC9, 0x04, 0x90, 0x13 };
    /// <summary>Where the safe-steps number sits inside the routine.</summary>
    private const int SafeStepsInRoutine = 4;

    public static bool IsOriginal(RomBuffer rom) =>
        rom.ReadByte(Site) == 0xAD && rom.ReadByte(Site + 1) == 0x8C && rom.ReadByte(Site + 2) == 0x07 &&
        rom.ReadByte(Site + 3) == 0xC9 && rom.ReadByte(Site + 5) == 0x90 && rom.ReadByte(Site + 6) == 0x13;

    public static bool IsApplied(RomBuffer rom) => rom.ReadByte(Site) == 0x22 && rom.ReadByte(Site + 4) == 0x90 && RoutineOffset(rom) > 0;

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    public static int RoutineOffset(RomBuffer rom)
    {
        int r = FileOffset(rom.ReadByte(Site + 1) | rom.ReadByte(Site + 2) << 8 | rom.ReadByte(Site + 3) << 16);
        return r > 0 && r + Routine(4).Length <= rom.Length && rom.ReadByte(r) == 0xAD ? r : 0;
    }

    /// <summary>File offset of the safe-steps number (the CMP operand), patched or not.</summary>
    public static int SafeStepsOffset(RomBuffer rom) => IsApplied(rom) ? RoutineOffset(rom) + SafeStepsInRoutine : Site + 4;

    public static byte[] Routine(int safeSteps) => new byte[]
    {
        0xAD, 0x8C, 0x07,           // LDA $078C      steps since the last battle
        0xC9, (byte)safeSteps,      // CMP #safe
        0x90, 0x0A,                 // BCC done       (carry clear: no battle)
        0xAF, 0x30, 0x00, 0x7E,     // LDA $7E0030    buttons held (low byte: A X L R)
        0x29, 0x20,                 // AND #$20       L
        0xF0, 0x02,                 // BEQ roll
        0x18,                       // CLC            L held: no battle
        0x6B,                       // RTL
        0x38,                       // roll: SEC
        0x6B,                       // RTL
        0x6B,                       // done: RTL
    };

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's step code at $01:8854 isn't the original, so the hold-L patch can't be applied safely.");
        int safe = rom.ReadByte(Site + 4);
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        var code = Routine(safe);
        int at = space.AllocateInOneBank(code.Length, ExpansionSpace.Kind.Patch, 2);
        rom.WriteBytes(at, code);
        int r = SnesAddress(at);
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)r, (byte)(r >> 8), (byte)(r >> 16), 0x90, 0x14, 0xEA });
        space.Save();
        rom.FixChecksum();
    }

    /// <summary>Puts the original check back (the routine's block is freed); the safe steps carry over.</summary>
    public static void Remove(RomBuffer rom)
    {
        if (!IsApplied(rom)) return;
        int r = RoutineOffset(rom);
        int safe = rom.ReadByte(r + SafeStepsInRoutine);
        var code = (byte[])Original.Clone();
        code[4] = (byte)safe;
        rom.WriteBytes(Site, code);
        var space = ExpansionSpace.Load(rom);
        space.Free(r);
        space.Save();
        rom.FixChecksum();
    }
}
