using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// patch for more than 67 encounter subgroups. the game reads subgroup s at $08:DBE2 + 4 x (s - 1) with a 16-bit
// pointer in bank 08 ($08:DA20-$08:DA4D), and bank 08 is full, so the table can't grow there. we swap
// that piece for a JSL to a small routine in the expanded rom that does the same thing (pick the k-th monster
// of the subgroup, k = random(4) + 1, skipping empty FF slots, cycling) but reads a 255-entry table with long
// addressing. the old table in bank 08 is unused after that, so the group lists can grow into it (up to the boss
// formations at $08:DCEE).
//
// bank 08 after patching:
//   DA20  B9 00 00     LDA $0000,Y       ; subgroup number (unchanged)
//   DA23  22 xx xx xx  JSL routine       ; returns the monster in A
//   DA27  85 01        STA $01
//   DA29  80 24        BRA $DA4F         ; the rest as before (monster count, STZ $02/$03, return)
//   DA2B  EA ...       NOP up to DA4E
public static class SubgroupPatch
{
    public const int Site = 0x45A20;                 // $08:DA20
    public const int SiteEnd = 0x45A4F;              // $08:DA4F (first byte kept)
    public const int MaxSubgroups = 255;
    public const int TableBytes = MaxSubgroups * 4;

    // the game's code at the patch site, to recognise an unpatched rom
    private static readonly byte[] Original = Convert.FromHexString("B900003AC2200A0A1869E2DB8502E220A9042229B8081AAAA00000B1021AF003CAF008C8C00400D0F280EDB1028501");

    public static bool IsOriginal(RomBuffer rom) => rom.ReadBytes(Site, Original.Length).SequenceEqual(Original);

    public static bool IsApplied(RomBuffer rom) =>
        rom.ReadByte(Site + 3) == 0x22 && rom.ReadByte(Site + 7) == 0x85 && rom.ReadByte(Site + 9) == 0x80 && TableOffset(rom) > 0;

    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);

    // the routine's first byte (file offset), from the JSL at the patch site
    public static int RoutineOffset(RomBuffer rom) =>
        FileOffset(rom.ReadByte(Site + 4) | rom.ReadByte(Site + 5) << 8 | rom.ReadByte(Site + 6) << 16);

    // where the 255-entry table is (file offset), read from the routine's LDA long,X. 0 if not found
    public static int TableOffset(RomBuffer rom)
    {
        int routineOffset = RoutineOffset(rom);
        if (routineOffset <= 0 || routineOffset + Routine(0).Length > rom.Length) return 0;
        for (int i = 0; i < 64; i++)
            if (rom.ReadByte(routineOffset + i) == 0xBF)
                return FileOffset(rom.ReadByte(routineOffset + i + 1) | rom.ReadByte(routineOffset + i + 2) << 8 | rom.ReadByte(routineOffset + i + 3) << 16);
        return 0;
    }

    // the routine, for a table at SNES address table. entered with M = 8-bit, X/Y = 16-bit,
    // A = subgroup number, B = 0, DB = 08 (the random number routine $08:B829 needs nothing else). $02-$03 are
    // the scratch word the original used (the code after it clears them)
    public static byte[] Routine(int table)
    {
        byte t0 = (byte)table, t1 = (byte)(table >> 8), t2 = (byte)(table >> 16);
        var code = new List<byte>
        {
            0xC2, 0x20,             // REP #$20
            0x29, 0xFF, 0x00,       // AND #$00FF
            0x3A,                   // DEC A
            0x0A, 0x0A,             // ASL A x2        ; 4 * (s - 1)
            0x18,                   // CLC
            0x69, 0x04, 0x00,       // ADC #$0004
            0x85, 0x02,             // STA $02         ; end of this subgroup's 4 bytes
            0xE2, 0x20,             // SEP #$20
            0xA9, 0x04,             // LDA #$04
            0x22, 0x29, 0xB8, 0x08, // JSL $08B829     ; random 0-3
            0x1A,                   // INC A
            0xC2, 0x20,             // REP #$20
            0x29, 0xFF, 0x00,       // AND #$00FF
            0xA8,                   // TAY             ; Y = which non-empty slot (1-4)
            // again:
            0xA5, 0x02,             // LDA $02
            0x38,                   // SEC
            0xE9, 0x04, 0x00,       // SBC #$0004
            0xAA,                   // TAX             ; X = first slot
            0xE2, 0x20,             // SEP #$20
            // loop:
            0xBF, t0, t1, t2,       // LDA table,X
            0x1A,                   // INC A           ; FF = empty slot
            0xF0, 0x03,             // BEQ skip
            0x88,                   // DEY
            0xF0, 0x09,             // BEQ found
            // skip:
            0xE8,                   // INX
            0xE4, 0x02,             // CPX $02
            0xD0, 0xF1,             // BNE loop
            0xC2, 0x20,             // REP #$20
            0x80, 0xE4,             // BRA again
            // found:
            0xBF, t0, t1, t2,       // LDA table,X
            0x6B,                   // RTL
        };
        return code.ToArray();
    }

    // applies the patch: expands the rom if needed, puts the routine and a 255-entry table (the 67 subgroups,
    // then empty FF slots) in the expanded space, and points the game at them
    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's code at $08:DA20 isn't the original, so the subgroup patch can't be applied safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int blockOffset = freeSpace.AllocateInOneBank(TableBytes + Routine(0).Length, ExpansionSpace.Kind.Patch, 1);
        int table = blockOffset, routine = blockOffset + TableBytes;
        var tableBytes = Enumerable.Repeat((byte)0xFF, TableBytes).ToArray();
        rom.ReadBytes(Encounters.OldSubgroups, Encounters.OldSubgroupCount * 4).CopyTo(tableBytes, 0);
        rom.WriteBytes(table, tableBytes);
        rom.WriteBytes(routine, Routine(SnesAddress(table)));
        int routineSnes = SnesAddress(routine);
        var site = new List<byte> { 0xB9, 0x00, 0x00, 0x22, (byte)routineSnes, (byte)(routineSnes >> 8), (byte)(routineSnes >> 16), 0x85, 0x01, 0x80, 0x24 };
        while (site.Count < SiteEnd - Site) site.Add(0xEA);
        rom.WriteBytes(Site, site.ToArray());
        freeSpace.Save();
        rom.FixChecksum();
    }

    // a copy of the patch for a test rom at a place we pick (inside 1 MB), so it can run from a save state
    public static void ApplyAtForTest(RomBuffer rom, int offset)
    {
        int table = offset, routine = offset + TableBytes;
        var tableBytes = Enumerable.Repeat((byte)0xFF, TableBytes).ToArray();
        rom.ReadBytes(Encounters.OldSubgroups, Encounters.OldSubgroupCount * 4).CopyTo(tableBytes, 0);
        rom.WriteBytes(table, tableBytes);
        rom.WriteBytes(routine, Routine(SnesAddress(table)));
        int routineSnes = SnesAddress(routine);
        var site = new List<byte> { 0xB9, 0x00, 0x00, 0x22, (byte)routineSnes, (byte)(routineSnes >> 8), (byte)(routineSnes >> 16), 0x85, 0x01, 0x80, 0x24 };
        while (site.Count < SiteEnd - Site) site.Add(0xEA);
        rom.WriteBytes(Site, site.ToArray());
    }
}
