using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

public static partial class SpellAnimations
{
    /// <summary>
    /// Animations with more than one sheet (a start, middle and end that each need a sheet of their own): track command
    /// <c>70 lo hi bank</c> (7x was never used by the game) loads another sheet in the middle of an animation. The extra
    /// sheets are kept unpacked in the added space (the game's unpacker, called in the middle of a battle frame, crashed),
    /// so the command copies one to $7E:7000 (where the animation's setup unpacks the first) and has it put on screen as
    /// the setup does: its tiles at the next refresh ($C4 bit 6), its colours queued on DMA channel 2 ($BF/$C0/$C2,
    /// as $08:9FDA). The track interpreter's dispatch is hooked, as bank 08 has no room:
    /// <code>
    /// $08:E13A  22 Dispatch FC 58 E1 EA EA   (was LSR / LSR / LSR / AND #$1E / TAX / JSR ($E158,X))
    /// </code>
    /// Dispatch hands the game's commands on (X = table index); for 70 it switches the sheet and returns entry 0, which
    /// for 70 (low nibble 0) just carries on (entry 7, where 7x pointed, is a BRK: the game never used 7x).
    /// </summary>
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

    /// <summary>The sheet switch is in (the hook and its code).</summary>
    public static bool HasSheetSwitch(RomBuffer rom) => rom.ReadByte(DispatchSite) == 0x22;

    /// <summary>Puts the sheet switch in (the area must be moved first).</summary>
    private static void AddSheetSwitch(RomBuffer rom)
    {
        if (HasSheetSwitch(rom)) return;
        if (!rom.ReadBytes(DispatchSite, DispatchOriginal.Length).SequenceEqual(DispatchOriginal))
            throw new InvalidOperationException("The battle animation interpreter ($08:E13A) isn't the original, so animations with several sheets can't be added safely.");
        int b = Block(rom);
        int at = SnesAddress(b + DispatchCode);
        var a = new Asm65816();
        a.Source(DispatchSource);
        var code = a.Build(at);
        if (DispatchCode + code.Length > DataStart) throw new InvalidOperationException("No room for the sheet switch.");
        rom.WriteBytes(b + DispatchCode, code);
        rom.WriteBytes(DispatchSite, new byte[] { 0x22, (byte)at, (byte)(at >> 8), (byte)(at >> 16), 0xFC, 0x58, 0xE1, 0xEA, 0xEA });
    }

    /// <summary>
    /// A new animation from several sheets (a start, a middle repeated, an end, each on a sheet of its own): one frame
    /// size for all, the first sheet loaded as usual, each next one by a 70 command before its frames. Returns its id.
    /// </summary>
    public static int ImportParts(RomBuffer rom, IReadOnlyList<(byte[] Indices, uint[] Palette, IReadOnlyList<int> Order)> parts,
        int frameWidth, int frameHeight, int stepFrames, bool fromBottom, uint? flash)
    {
        if (parts.Count == 1)
            return Import(rom, parts[0].Indices, parts[0].Palette, new ImportOptions(frameWidth, frameHeight, parts[0].Order.Max() + 1, stepFrames, fromBottom, flash, parts[0].Order));
        Move(rom);
        AddSheetSwitch(rom);
        // the first part as an ordinary animation; then its track gets the others
        int id = Import(rom, parts[0].Indices, parts[0].Palette, new ImportOptions(frameWidth, frameHeight, parts[0].Order.Max() + 1, stepFrames, fromBottom, flash, parts[0].Order));
        int b = Block(rom);
        int h = HeaderOffset(rom, id);
        int track = At(rom, rom.ReadUInt16Le(h + 4));
        // the first track as written: speed, frames (5 bytes each), 01
        int len = 1 + parts[0].Order.Count * 5;
        var t = rom.ReadBytes(track, len).ToList();
        int shape = rom.ReadByte(track + 2);
        int cols = frameWidth / 16, rows = frameHeight / 16, perRow = 8 / cols;
        for (int k = 1; k < parts.Count; k++)
        {
            var raw = BuildResource(parts[k].Indices, parts[k].Palette);
            var space = ExpansionSpace.Load(rom);
            int at = space.AllocateInOneBank(raw.Length, ExpansionSpace.Kind.Patch, 0);
            space.Save();
            rom.WriteBytes(at, raw);
            int snes = SnesAddress(at);
            t.AddRange(new byte[] { 0x70, (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
            foreach (int f in parts[k].Order)
            {
                int cx = f % perRow * cols, cy = f / perRow * rows;
                t.AddRange(new byte[] { 0x50, (byte)shape, 0x03, (byte)(2 * cx + 0x20 * cy), 0x0F });
            }
        }
        t.Add(0x01);
        b = Block(rom);
        int newTrack = Put(rom, t.ToArray());
        rom.WriteUInt16Le(h + 4, (ushort)newTrack);
        rom.FixChecksum();
        return id;
    }
}
