using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Party members retarget instead of missing. Party attacks and spells aim at a monster group ($076F,Y); when the
/// action plays, $08:B39E picks a living monster of that group. If the whole group is already dead it falls
/// through to $08:B47F, which picks a random slot of the group anyway (all dead), so the action misses.
/// The patch replaces that fallback with a JSL to a routine that picks a random living monster of any group
/// (monster slots 0-7, $137A,X = FF when gone); only with no monster left does it do what the game did.
///
/// <code>
/// $08:B47F  22 xx xx xx  JSL routine     ; A = target slot
/// $08:B483  8D 3F 14     STA $143F
/// $08:B486  EA x 7       (to $B48D: PLY / PLP / RTS as before)
/// </code>
/// </summary>
public static class RetargetPatch
{
    public const int Site = 0x4347F;   // $08:B47F
    private static readonly byte[] Original =
        { 0xBD, 0xEA, 0x13, 0x22, 0x29, 0xB8, 0x08, 0x18, 0x7D, 0xFA, 0x13, 0x8D, 0x3F, 0x14 };

    public static bool IsOriginal(RomBuffer rom) => rom.ReadBytes(Site, Original.Length).SequenceEqual(Original);
    public static bool IsApplied(RomBuffer rom) => rom.ReadByte(Site) == 0x22 && rom.ReadByte(Site + 4) == 0x8D && RoutineOffset(rom) > 0;

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    public static int RoutineOffset(RomBuffer rom)
    {
        int r = FileOffset(rom.ReadByte(Site + 1) | rom.ReadByte(Site + 2) << 8 | rom.ReadByte(Site + 3) << 16);
        return r > 0 && r + Routine.Length <= rom.Length && rom.ReadByte(r) == 0xDA ? r : 0;
    }

    /// <summary>Entered with M = X = 8-bit, X = the target group, DB = 08 (low RAM visible); returns A = slot.</summary>
    public static readonly byte[] Routine =
    {
        0xDA,                   // 00 PHX               the group
        0x5A,                   // 01 PHY
        0xA0, 0x00,             // 02 LDY #$00          count living monsters
        0xA2, 0x00,             // 04 LDX #$00
        0xBD, 0x7A, 0x13,       // 06 cnt: LDA $137A,X
        0xC9, 0xFF,             // 09 CMP #$FF
        0xF0, 0x01,             // 11 BEQ +1
        0xC8,                   // 13 INY
        0xE8,                   // 14 INX
        0xE0, 0x08,             // 15 CPX #$08
        0xD0, 0xF3,             // 17 BNE cnt
        0x98,                   // 19 TYA
        0xF0, 0x1E,             // 20 BEQ none          nobody left
        0x22, 0x29, 0xB8, 0x08, // 22 JSL $08B829       random 0..count-1
        0xA8,                   // 26 TAY
        0xA2, 0x00,             // 27 LDX #$00
        0xBD, 0x7A, 0x13,       // 29 find: LDA $137A,X
        0xC9, 0xFF,             // 32 CMP #$FF
        0xF0, 0x05,             // 34 BEQ next
        0xC0, 0x00,             // 36 CPY #$00
        0xF0, 0x08,             // 38 BEQ found
        0x88,                   // 40 DEY
        0xE8,                   // 41 next: INX
        0xE0, 0x08,             // 42 CPX #$08
        0xD0, 0xEF,             // 44 BNE find
        0x80, 0x04,             // 46 BRA none
        0x8A,                   // 48 found: TXA
        0x7A,                   // 49 PLY
        0xFA,                   // 50 PLX
        0x6B,                   // 51 RTL
        0x7A,                   // 52 none: PLY
        0xFA,                   // 53 PLX
        0xBD, 0xEA, 0x13,       // 54 LDA $13EA,X       the game's own choice
        0x22, 0x29, 0xB8, 0x08, // 57 JSL $08B829
        0x18,                   // 61 CLC
        0x7D, 0xFA, 0x13,       // 62 ADC $13FA,X
        0x6B,                   // 65 RTL
    };

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's battle code at $08:B47F isn't the original, so the retarget fix can't be applied safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(Routine.Length, ExpansionSpace.Kind.Patch, 3);
        WriteAt(rom, at);
        space.Save();
        rom.FixChecksum();
    }

    /// <summary>Writes the routine at <paramref name="at"/> and points the game at it (also used for 1 MB test copies).</summary>
    public static void WriteAt(RomBuffer rom, int at)
    {
        rom.WriteBytes(at, Routine);
        int r = SnesAddress(at);
        var site = new List<byte> { 0x22, (byte)r, (byte)(r >> 8), (byte)(r >> 16), 0x8D, 0x3F, 0x14 };
        while (site.Count < Original.Length) site.Add(0xEA);
        rom.WriteBytes(Site, site.ToArray());
    }

    public static void Remove(RomBuffer rom)
    {
        if (!IsApplied(rom)) return;
        int r = RoutineOffset(rom);
        rom.WriteBytes(Site, Original);
        var space = ExpansionSpace.Load(rom);
        space.Free(r);
        space.Save();
        rom.FixChecksum();
    }
}
