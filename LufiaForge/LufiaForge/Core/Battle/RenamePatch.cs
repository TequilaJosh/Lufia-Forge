using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// a new event command the game didn't have: 6E cc n1 n2 n3 n4 n5 renames party member cc (0-7, Maxim shares
// the Hero's slot, Selan Lufia's, Guy Aguro's, Artea Jerin's) to the five letters n1-n5 (00 = nothing). n1 = FF gives
// the character their normal name back (for the Hero that's the name the player typed). party names live in RAM at
// $14B6 + 6 x slot (5 letters, 00 after). the old heroes' names only get written when a new game starts, so without
// this a flashback that swaps in Maxim's sprite still shows the Hero's name.
//
// commands 6E-7F had no handler (their table entries at $01:C78C run into other data), so we hook the engine's
// dispatcher:
//   $01:C674  5C xx xx xx  JML routine   (was CMP #$80 / BCS / ASL / TAX)
// the routine handles 6E and goes back to $01:C67A / $01:C67D for every other command. it reads its operands the way
// $01:C740 does (DB = script bank, Y = pointer, next bank at $10000)
public static class RenamePatch
{
    public const int Op = 0x6E;
    public const int Site = 0x0C674;   // $01:C674
    private static readonly byte[] Original = { 0xC9, 0x80, 0xB0, 0x05, 0x0A, 0xAA };
    // offsets in Routine of the JSR operands that call its own reader (fixed up when it gets placed)
    private static readonly int[] ReaderCalls = { 19, 23, 27, 31, 35, 39 };
    private const int Reader = 185;

    public static bool IsOriginal(RomBuffer rom) => rom.ReadBytes(Site, Original.Length).SequenceEqual(Original);
    public static bool IsApplied(RomBuffer rom) => rom.ReadByte(Site) == 0x5C && RoutineOffset(rom) > 0;

    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);

    public static int RoutineOffset(RomBuffer rom)
    {
        int routineOffset = FileOffset(rom.ReadByte(Site + 1) | rom.ReadByte(Site + 2) << 8 | rom.ReadByte(Site + 3) << 16);
        return routineOffset > 0 && routineOffset + Routine.Length <= rom.Length && rom.ReadByte(routineOffset) == 0xC9 && rom.ReadByte(routineOffset + 1) == Op ? routineOffset : 0;
    }

    // true when the bytes at offset are a rename command (the event decoder uses this)
    public static bool IsCommandAt(RomBuffer rom, int offset)
    {
        if (offset + 7 > rom.Length || rom.ReadByte(offset) != Op || rom.ReadByte(offset + 1) > 7 || !IsApplied(rom)) return false;
        if (rom.ReadByte(offset + 2) == 0xFF) return true;
        for (int i = 2; i < 7; i++) { int letter = rom.ReadByte(offset + i); if (letter != 0 && (letter < 0x20 || letter > 0x7E)) return false; }
        return true;
    }

    public static readonly byte[] Routine =
    {
        // hook:
        0xC9, 0x6E,                  //   0 CMP #$6E
        0xF0, 0x0E,                  //   2 BEQ ren
        0xC9, 0x80,                  //   4 CMP #$80
        0xB0, 0x06,                  //   6 BCS go80
        0x0A,                        //   8 ASL
        0xAA,                        //   9 TAX
        0x5C, 0x7A, 0xC6, 0x01,      //  10 JML $01C67A  (JMP ($C78C,X) as before)
        // go80:
        0x5C, 0x7D, 0xC6, 0x01,      //  14 JML $01C67D  (commands 80-FF as before)
        // ren:
        0x20, 0xB9, 0x00,            //  18 JSR rd
        0x48,                        //  21 PHA
        0x20, 0xB9, 0x00,            //  22 JSR rd
        0x48,                        //  25 PHA
        0x20, 0xB9, 0x00,            //  26 JSR rd
        0x48,                        //  29 PHA
        0x20, 0xB9, 0x00,            //  30 JSR rd
        0x48,                        //  33 PHA
        0x20, 0xB9, 0x00,            //  34 JSR rd
        0x48,                        //  37 PHA
        0x20, 0xB9, 0x00,            //  38 JSR rd
        0x48,                        //  41 PHA
        0x5A,                        //  42 PHY
        0x8B,                        //  43 PHB
        0xA9, 0x08,                  //  44 LDA #$08
        0x48,                        //  46 PHA
        0xAB,                        //  47 PLB  (DB 08: low RAM and the name table)
        0xA3, 0x09,                  //  48 LDA 9,S  (character)
        0x29, 0x03,                  //  50 AND #$03
        0x0A,                        //  52 ASL
        0x48,                        //  53 PHA
        0x0A,                        //  54 ASL
        0x18,                        //  55 CLC
        0x63, 0x01,                  //  56 ADC 1,S
        0xAA,                        //  58 TAX  (slot * 6)
        0x68,                        //  59 PLA
        0xA3, 0x08,                  //  60 LDA 8,S  (first letter)
        0xC9, 0xFF,                  //  62 CMP #$FF
        0xF0, 0x19,                  //  64 BEQ deflt
        0x9D, 0xB6, 0x14,            //  66 STA $14B6,X
        0xA3, 0x07,                  //  69 LDA 7,S
        0x9D, 0xB7, 0x14,            //  71 STA $14B7,X
        0xA3, 0x06,                  //  74 LDA 6,S
        0x9D, 0xB8, 0x14,            //  76 STA $14B8,X
        0xA3, 0x05,                  //  79 LDA 5,S
        0x9D, 0xB9, 0x14,            //  81 STA $14B9,X
        0xA3, 0x04,                  //  84 LDA 4,S
        0x9D, 0xBA, 0x14,            //  86 STA $14BA,X
        0x80, 0x4D,                  //  89 BRA term
        // deflt:
        0xA3, 0x09,                  //  91 LDA 9,S
        0x29, 0x07,                  //  93 AND #$07
        0xF0, 0x29,                  //  95 BEQ hero
        0x3A,                        //  97 DEC
        0x48,                        //  98 PHA
        0x0A,                        //  99 ASL
        0x0A,                        // 100 ASL
        0x18,                        // 101 CLC
        0x63, 0x01,                  // 102 ADC 1,S
        0xA8,                        // 104 TAY  ((character-1) * 5)
        0x68,                        // 105 PLA
        0xB9, 0x8E, 0xEE,            // 106 LDA $EE8E,Y  (the game's names: Lufia, Aguro, Jerin, Maxim...)
        0x9D, 0xB6, 0x14,            // 109 STA $14B6,X
        0xB9, 0x8F, 0xEE,            // 112 LDA $EE8F,Y
        0x9D, 0xB7, 0x14,            // 115 STA $14B7,X
        0xB9, 0x90, 0xEE,            // 118 LDA $EE90,Y
        0x9D, 0xB8, 0x14,            // 121 STA $14B8,X
        0xB9, 0x91, 0xEE,            // 124 LDA $EE91,Y
        0x9D, 0xB9, 0x14,            // 127 STA $14B9,X
        0xB9, 0x92, 0xEE,            // 130 LDA $EE92,Y
        0x9D, 0xBA, 0x14,            // 133 STA $14BA,X
        0x80, 0x1E,                  // 136 BRA term
        // hero:
        0xAD, 0xB0, 0x14,            // 138 LDA $14B0  (the name the player typed)
        0x9D, 0xB6, 0x14,            // 141 STA $14B6,X
        0xAD, 0xB1, 0x14,            // 144 LDA $14B1
        0x9D, 0xB7, 0x14,            // 147 STA $14B7,X
        0xAD, 0xB2, 0x14,            // 150 LDA $14B2
        0x9D, 0xB8, 0x14,            // 153 STA $14B8,X
        0xAD, 0xB3, 0x14,            // 156 LDA $14B3
        0x9D, 0xB9, 0x14,            // 159 STA $14B9,X
        0xAD, 0xB4, 0x14,            // 162 LDA $14B4
        0x9D, 0xBA, 0x14,            // 165 STA $14BA,X
        // term:
        0xA9, 0x00,                  // 168 LDA #$00
        0x9D, 0xBB, 0x14,            // 170 STA $14BB,X
        0xAB,                        // 173 PLB
        0x7A,                        // 174 PLY
        0x68,                        // 175 PLA
        0x68,                        // 176 PLA
        0x68,                        // 177 PLA
        0x68,                        // 178 PLA
        0x68,                        // 179 PLA
        0x68,                        // 180 PLA
        0x5C, 0x6E, 0xC6, 0x01,      // 181 JML $01C66E  (next command)
        // rd:
        0xB9, 0x00, 0x00,            // 185 LDA $0000,Y
        0xC8,                        // 188 INY
        0xD0, 0x0A,                  // 189 BNE ok
        0x48,                        // 191 PHA
        0x8B,                        // 192 PHB
        0x68,                        // 193 PLA
        0x1A,                        // 194 INC
        0x48,                        // 195 PHA
        0xAB,                        // 196 PLB
        0x68,                        // 197 PLA
        0xA0, 0x00, 0x80,            // 198 LDY #$8000
        // ok:
        0x60,                        // 201 RTS

    };

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The event engine's code at $01:C674 isn't the original, so the rename command can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        WriteAt(rom, freeSpace.AllocateInOneBank(Routine.Length, ExpansionSpace.Kind.Patch, 6));
        freeSpace.Save();
        rom.FixChecksum();
    }

    // writes the routine at offset (one bank) and hooks the dispatcher to it
    public static void WriteAt(RomBuffer rom, int offset)
    {
        var code = (byte[])Routine.Clone();
        int reader = (SnesAddress(offset) & 0xFFFF) + Reader;
        foreach (int callAt in ReaderCalls) { code[callAt] = (byte)reader; code[callAt + 1] = (byte)(reader >> 8); }
        rom.WriteBytes(offset, code);
        int routineSnes = SnesAddress(offset);
        rom.WriteBytes(Site, new byte[] { 0x5C, (byte)routineSnes, (byte)(routineSnes >> 8), (byte)(routineSnes >> 16), 0xEA, 0xEA });
    }

    // events that use the rename command (map, event), found while the patch is in
    public static List<(int Map, int Event)> UsedBy(RomBuffer rom)
    {
        var list = new List<(int, int)>();
        if (!IsApplied(rom)) return list;
        foreach (var map in Maps.MapCatalog.Scan(rom))
            for (int eventId = 0, slotCount = Maps.EventScript.SlotCount(rom, map.MapId); eventId < slotCount; eventId++)
            {
                try
                {
                    var script = Maps.EventScript.Load(rom, map.MapId, eventId);
                    if (script != null && script.Ops.Any(o => !o.IsText && o.Bytes.Length > 0 && o.Bytes[0] == Op)) list.Add((map.MapId, eventId));
                }
                catch { }
            }
        return list;
    }

    public static void Remove(RomBuffer rom)
    {
        if (!IsApplied(rom)) return;
        if (EmotePatch.IsApplied(rom))
            throw new InvalidOperationException("The emote command for cutscenes is built on this patch. Remove it first (Patch Manager), then this one.");
        // without the patch the game jumps into data on command 6E, so events using it would crash
        var used = UsedBy(rom);
        if (used.Count > 0)
            throw new InvalidOperationException("Events still use the rename command (" +
                string.Join(", ", used.Take(6).Select(u => $"map {u.Map:X2} event {u.Event}")) + (used.Count > 6 ? ", ..." : "") +
                "). Take it out of those events first; removing the patch now would make them crash the game.");
        int routineOffset = RoutineOffset(rom);
        rom.WriteBytes(Site, Original);
        var freeSpace = ExpansionSpace.Load(rom);
        freeSpace.Free(routineOffset);
        freeSpace.Save();
        rom.FixChecksum();
    }
}
