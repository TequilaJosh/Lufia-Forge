using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

public static partial class SpellAnimations
{
    // animations with more than one sheet (a start, middle and end that each need their own sheet): track command
    // 70 lo hi bank (the game never used 7x) loads another sheet in the middle of an animation. the extra
    // sheets are kept unpacked in the added space (calling the game's unpacker in the middle of a battle frame crashed),
    // so the command copies one to $7E:7000 (where the animation's setup unpacks the first) and has it put on screen
    // the way the setup does: its tiles at the next refresh ($C4 bit 6), its colours queued on DMA channel 2
    // ($BF/$C0/$C2, like $08:9FDA). bank 08 has no room, so we hook the track interpreter's dispatch:
    //   $08:E13A  22 Dispatch FC 58 E1 EA EA   (was LSR / LSR / LSR / AND #$1E / TAX / JSR ($E158,X))
    // Dispatch hands the game's commands on (X = table index). for 70 it switches the sheet and returns entry 0, which
    // for 70 (low nibble 0) just carries on (entry 7, where 7x pointed, is a BRK since the game never used 7x)
    private const int DispatchSite = 0x4613A;   // $08:E13A
    private static readonly byte[] DispatchOriginal = { 0x4A, 0x4A, 0x4A, 0x29, 0x1E, 0xAA, 0xFC, 0x58, 0xE1 };
    private const int DispatchCode = 0x0650;   // in the animation bank, after the resource hook and the state
    private const int RawSheetSize = 0x40 + 128 * 32;

    private const string DispatchSource = """
        Dispatch:                   ; A(8) = the command (high byte 0), X/Y = 16, Y past the command, DB = 7E
            PHA
            AND.b #$F0
            CMP.b #$70
            BEQ .switch
            PLA
            LSR
            LSR
            LSR
            AND.b #$1E
            TAX
            RTL
        .switch
            PLA
            LDA $00                 ; $00-$02 kept
            STA $7FFFC0
            LDA $01
            STA $7FFFC1
            LDA $02
            STA $7FFFC2
            LDA [$1F],y             ; the sheet's address
            STA $00
            INY
            LDA [$1F],y
            STA $01
            INY
            LDA [$1F],y
            STA $02
            INY
            PHY
            REP #$20
            LDY.w #$0000
        -   LDA [$00],y             ; to $7E:7000, where the setup unpacks the first sheet
            TYX
            STA $7E7000,x
            INY
            INY
            CPY.w #$1040
            BCC -
            SEP #$20
            LDA $7FFFC0
            STA $00
            LDA $7FFFC1
            STA $01
            LDA $7FFFC2
            STA $02
            LDA.b #$40              ; its tiles at the next refresh
            TSB $C4
            LDA.b #$02              ; its colours (as $08:9FDA)
            STA $C2
            REP #$20
            LDA.w #$00F0
            STA $C0
            LDA.w #$7000
            STA $004322
            LDA.w #$0020
            STA $004325
            SEP #$20
            LDA.b #$00
            STA $004320
            LDA.b #$22
            STA $004321
            LDA.b #$7E
            STA $004324
            LDA.b #$04
            STA $BF
            PLY
            LDX.w #$0000            ; entry 0 with the command's low nibble 0: nothing more (entry 7 is a BRK)
            RTL
        """;

    // the sheet switch is in (the hook and its code)
    public static bool HasSheetSwitch(RomBuffer rom) => rom.ReadByte(DispatchSite) == 0x22;

    // puts the sheet switch in (the area has to be moved first)
    private static void AddSheetSwitch(RomBuffer rom)
    {
        if (HasSheetSwitch(rom)) return;
        if (!rom.ReadBytes(DispatchSite, DispatchOriginal.Length).SequenceEqual(DispatchOriginal))
            throw new InvalidOperationException("The battle animation interpreter ($08:E13A) isn't the original, so animations with several sheets can't be added safely.");
        int block = Block(rom);
        int dispatchAddress = SnesAddress(block + DispatchCode);
        var asm = new Asm65816();
        asm.Source(DispatchSource);
        var code = asm.Build(dispatchAddress);
        if (DispatchCode + code.Length > DataStart) throw new InvalidOperationException("No room for the sheet switch.");
        rom.WriteBytes(block + DispatchCode, code);
        rom.WriteBytes(DispatchSite, new byte[] { 0x22, (byte)dispatchAddress, (byte)(dispatchAddress >> 8), (byte)(dispatchAddress >> 16), 0xFC, 0x58, 0xE1, 0xEA, 0xEA });
    }

    // a new animation from several sheets (a start, a middle repeated, an end, each on its own sheet): one frame
    // size for all, the first sheet loaded as usual, each next one by a 70 command before its frames. returns its id
    public static int ImportParts(RomBuffer rom, IReadOnlyList<(byte[] Indices, uint[] Palette, IReadOnlyList<int> Order)> parts,
        int frameWidth, int frameHeight, int stepFrames, bool fromBottom, uint? flash)
    {
        if (parts.Count == 1)
            return Import(rom, parts[0].Indices, parts[0].Palette, new ImportOptions(frameWidth, frameHeight, parts[0].Order.Max() + 1, stepFrames, fromBottom, flash, parts[0].Order));
        Move(rom);
        AddSheetSwitch(rom);
        // the first part as an ordinary animation, then its track gets the others
        int id = Import(rom, parts[0].Indices, parts[0].Palette, new ImportOptions(frameWidth, frameHeight, parts[0].Order.Max() + 1, stepFrames, fromBottom, flash, parts[0].Order));
        int block = Block(rom);
        int header = HeaderOffset(rom, id);
        int track = At(rom, rom.ReadUInt16Le(header + 4));
        // the first track as written: speed, frames (5 bytes each), 01
        int length = 1 + parts[0].Order.Count * 5;
        var trackBytes = rom.ReadBytes(track, length).ToList();
        int shape = rom.ReadByte(track + 2);
        int cols = frameWidth / 16, rows = frameHeight / 16, perRow = 8 / cols;
        for (int part = 1; part < parts.Count; part++)
        {
            var raw = BuildResource(parts[part].Indices, parts[part].Palette);
            var freeSpace = ExpansionSpace.Load(rom);
            int sheetOffset = freeSpace.AllocateInOneBank(raw.Length, ExpansionSpace.Kind.Patch, 0);
            freeSpace.Save();
            rom.WriteBytes(sheetOffset, raw);
            int sheetSnes = SnesAddress(sheetOffset);
            trackBytes.AddRange(new byte[] { 0x70, (byte)sheetSnes, (byte)(sheetSnes >> 8), (byte)(sheetSnes >> 16) });
            foreach (int frame in parts[part].Order)
            {
                int cx = frame % perRow * cols, cy = frame / perRow * rows;
                trackBytes.AddRange(new byte[] { 0x50, (byte)shape, 0x03, (byte)(2 * cx + 0x20 * cy), 0x0F });
            }
        }
        trackBytes.Add(0x01);
        block = Block(rom);
        int newTrack = Put(rom, trackBytes.ToArray());
        rom.WriteUInt16Le(header + 4, (ushort)newTrack);
        rom.FixChecksum();
        return id;
    }
}
