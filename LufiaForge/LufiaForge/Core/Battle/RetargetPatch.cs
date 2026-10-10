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
///
/// Spells and items aimed at a whole group take another path: $08:B2DC (action type 4) hits the living monsters
/// in the group's slot range ($13FA,X first slot, $13EA,X slots), so a beaten group meant no effect at all. There
/// a second routine checks the group and, when nobody in it is left, switches to the group of a random living
/// monster:
/// <code>
/// $08:B2E1  22 xx xx xx  JSL group routine  ; A = group in; X = the group to hit and A = $13FA,X out
/// $08:B2E5  8D 62 14     STA $1462         (as before)
/// </code>
/// </summary>
public static class RetargetPatch
{
    public const int Site = 0x4347F;   // $08:B47F
    private static readonly byte[] Original =
        { 0xBD, 0xEA, 0x13, 0x22, 0x29, 0xB8, 0x08, 0x18, 0x7D, 0xFA, 0x13, 0x8D, 0x3F, 0x14 };
    public const int GroupSite = 0x432E1;   // $08:B2E1
    private static readonly byte[] GroupOriginal = { 0xAA, 0xBD, 0xFA, 0x13, 0x8D, 0x62, 0x14 };

    private static bool SingleOriginal(RomBuffer rom) => rom.ReadBytes(Site, Original.Length).SequenceEqual(Original);
    private static bool SingleApplied(RomBuffer rom) => rom.ReadByte(Site) == 0x22 && rom.ReadByte(Site + 4) == 0x8D && RoutineOffset(rom) > 0;
    private static bool GroupIsOriginal(RomBuffer rom) => rom.ReadBytes(GroupSite, GroupOriginal.Length).SequenceEqual(GroupOriginal);
    private static bool GroupApplied(RomBuffer rom) => rom.ReadByte(GroupSite) == 0x22 && rom.ReadByte(GroupSite + 4) == 0x8D && GroupRoutineOffset(rom) > 0;

    /// <summary>True when the fix can be added: both places hold the game's own code (or one already has the fix, from an older version).</summary>
    public static bool IsOriginal(RomBuffer rom) =>
        !IsApplied(rom) && (SingleOriginal(rom) || SingleApplied(rom)) && (GroupIsOriginal(rom) || GroupApplied(rom));
    public static bool IsApplied(RomBuffer rom) => SingleApplied(rom) && GroupApplied(rom);

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    public static int RoutineOffset(RomBuffer rom)
    {
        int r = FileOffset(rom.ReadByte(Site + 1) | rom.ReadByte(Site + 2) << 8 | rom.ReadByte(Site + 3) << 16);
        return r > 0 && r + Routine.Length <= rom.Length && rom.ReadByte(r) == 0xDA ? r : 0;
    }

    public static int GroupRoutineOffset(RomBuffer rom)
    {
        int r = FileOffset(rom.ReadByte(GroupSite + 1) | rom.ReadByte(GroupSite + 2) << 8 | rom.ReadByte(GroupSite + 3) << 16);
        return r > 0 && r + GroupRoutine.Length <= rom.Length && rom.ReadByte(r) == 0xAA ? r : 0;
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

    /// <summary>
    /// Group-target spells and items: entered with M = X = 8-bit, A = the target group, DB = 08. Returns X = the group
    /// to hit (the same one while anybody in it is alive, else the group of a random living monster) and A = its first slot.
    /// </summary>
    public static readonly byte[] GroupRoutine =
    {
        0xAA,                      // 00 TAX
        0xDA,                      // 01 PHX               keep the chosen group
        0xBD, 0xEA, 0x13,          // 02 LDA $13EA,X       its slots
        0xF0, 0x17,                // 05 BEQ dead
        0xA8,                      // 07 TAY
        0xBD, 0xFA, 0x13,          // 08 LDA $13FA,X       first slot
        0xAA,                      // 11 TAX
        0xBD, 0x7A, 0x13,          // 12 c: LDA $137A,X    FF = gone
        0xC9, 0xFF,                // 15 CMP #$FF
        0xD0, 0x06,                // 17 BNE live
        0xE8,                      // 19 INX
        0x88,                      // 20 DEY
        0xD0, 0xF5,                // 21 BNE c
        0x80, 0x05,                // 23 BRA dead
        0xFA,                      // 25 live: PLX         somebody left: as the game does
        0xBD, 0xFA, 0x13,          // 26 LDA $13FA,X
        0x6B,                      // 29 RTL
        0xFA,                      // 30 dead: PLX
        0xDA,                      // 31 PHX
        0xA0, 0x00,                // 32 LDY #0            count living monsters
        0xA2, 0x00,                // 34 LDX #0
        0xBD, 0x7A, 0x13,          // 36 cnt: LDA $137A,X
        0xC9, 0xFF,                // 39 CMP #$FF
        0xF0, 0x01,                // 41 BEQ cskip
        0xC8,                      // 43 INY
        0xE8,                      // 44 cskip: INX
        0xE0, 0x08,                // 45 CPX #8
        0xD0, 0xF3,                // 47 BNE cnt
        0x98,                      // 49 TYA
        0xF0, 0x3C,                // 50 BEQ fallback      nobody left at all
        0x22, 0x29, 0xB8, 0x08,    // 52 JSL $08B829       random 0..count-1
        0xA8,                      // 56 TAY
        0xA2, 0x00,                // 57 LDX #0
        0xBD, 0x7A, 0x13,          // 59 find: LDA $137A,X
        0xC9, 0xFF,                // 62 CMP #$FF
        0xF0, 0x05,                // 64 BEQ nxt
        0xC0, 0x00,                // 66 CPY #0
        0xF0, 0x08,                // 68 BEQ got
        0x88,                      // 70 DEY
        0xE8,                      // 71 nxt: INX
        0xE0, 0x08,                // 72 CPX #8
        0xD0, 0xEF,                // 74 BNE find
        0x80, 0x22,                // 76 BRA fallback
        0x8A,                      // 78 got: TXA          the monster's slot
        0x48,                      // 79 PHA
        0xA2, 0x00,                // 80 LDX #0            which group holds it
        0xBD, 0xEA, 0x13,          // 82 grp: LDA $13EA,X
        0xF0, 0x13,                // 85 BEQ gnext
        0xA3, 0x01,                // 87 LDA 1,S
        0x38,                      // 89 SEC
        0xFD, 0xFA, 0x13,          // 90 SBC $13FA,X
        0x90, 0x0B,                // 93 BCC gnext
        0xDD, 0xEA, 0x13,          // 95 CMP $13EA,X
        0xB0, 0x06,                // 98 BCS gnext
        0x68,                      // 100 PLA
        0x68,                      // 101 PLA
        0xBD, 0xFA, 0x13,          // 102 LDA $13FA,X
        0x6B,                      // 105 RTL
        0xE8,                      // 106 gnext: INX
        0xE0, 0x08,                // 107 CPX #8
        0xD0, 0xE3,                // 109 BNE grp
        0x68,                      // 111 PLA
        0xFA,                      // 112 fallback: PLX
        0xBD, 0xFA, 0x13,          // 113 LDA $13FA,X
        0x6B,                      // 116 RTL
    };

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's battle code at $08:B47F / $08:B2E1 isn't the original, so the retarget fix can't be applied safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        if (!SingleApplied(rom)) WriteSingle(rom, space.AllocateInOneBank(Routine.Length, ExpansionSpace.Kind.Patch, 3));
        if (!GroupApplied(rom)) WriteGroup(rom, space.AllocateInOneBank(GroupRoutine.Length, ExpansionSpace.Kind.Patch, 3));
        space.Save();
        rom.FixChecksum();
    }

    /// <summary>Writes both routines from <paramref name="at"/> and points the game at them (for 1 MB test copies).</summary>
    public static void WriteAt(RomBuffer rom, int at)
    {
        WriteSingle(rom, at);
        WriteGroup(rom, at + Routine.Length);
    }

    private static void WriteSingle(RomBuffer rom, int at)
    {
        rom.WriteBytes(at, Routine);
        int r = SnesAddress(at);
        var site = new List<byte> { 0x22, (byte)r, (byte)(r >> 8), (byte)(r >> 16), 0x8D, 0x3F, 0x14 };
        while (site.Count < Original.Length) site.Add(0xEA);
        rom.WriteBytes(Site, site.ToArray());
    }

    private static void WriteGroup(RomBuffer rom, int at)
    {
        rom.WriteBytes(at, GroupRoutine);
        int r = SnesAddress(at);
        rom.WriteBytes(GroupSite, new byte[] { 0x22, (byte)r, (byte)(r >> 8), (byte)(r >> 16), 0x8D, 0x62, 0x14 });
    }

    public static void Remove(RomBuffer rom)
    {
        var space = ExpansionSpace.Load(rom);
        if (SingleApplied(rom)) { int r = RoutineOffset(rom); rom.WriteBytes(Site, Original); space.Free(r); }
        if (GroupApplied(rom)) { int r = GroupRoutineOffset(rom); rom.WriteBytes(GroupSite, GroupOriginal); space.Free(r); }
        space.Save();
        rom.FixChecksum();
    }
}
