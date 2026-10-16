using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// More monster AI commands (14-1D) for every monster: the game's own and new ones. The battle's AI interpreter
/// ($08:CACB) reads a command and runs it through a table of 19 handlers ($08:CB0C), with no check of the number.
/// Three hooks:
/// <code>
/// $08:CAC6  22 TurnStart EA            a monster's turn starts: count it (was SEP #$30 / STZ $1460)
/// $08:CACB  A0 00                      LDY #0
///           22 OpHook                  reads the command: C = end of script, V = done by the hook,
///           B0 35                      BCS $CB08 (end)
///           70 03                      BVS $CAD8 (carry on with the next command)
///           FC 0C CB                   JSR ($CB0C,X): the game's own commands 01-13 as before
/// $01:F658  22 InitHook EA EA          a monster enters a battle: clear its counters (was LDA $13,X / STA $44,X)
/// </code>
/// Each monster's battle record ($7E:E500 + slot x $80) keeps, in bytes the game never uses: +$70 its turn count and
/// +$71 eight switches for phases. Party: current HP $7E:DA48 (FFFF = empty slot), max HP $16E0, slot order;
/// a party member as a target is $80 + slot ($143F). Jumps count from the record's start, as the game's own.
/// </summary>
public static class AiCommandsPatch
{
    public const int First = 0x14, Count = 10;

    private const int TurnSite = 0x44AC6;   // $08:CAC6
    private const int OpSite = 0x44ACB;     // $08:CACB
    private const int InitSite = 0xF658;    // $01:F658
    private static readonly byte[] TurnOriginal = { 0xE2, 0x30, 0x9C, 0x60, 0x14 };
    private static readonly byte[] OpOriginal = { 0xA0, 0x00, 0xB1, 0x09, 0xF0, 0x37, 0xC8, 0x3A, 0x0A, 0xAA, 0xFC, 0x0C, 0xCB };
    private static readonly byte[] InitOriginal = { 0xBD, 0x13, 0x00, 0x9D, 0x44, 0x00 };
    private static readonly byte[] Magic = "LFAI"u8.ToArray();
    private const byte Version = 1;

    public static bool IsOriginal(RomBuffer rom) =>
        Matches(rom, TurnSite, TurnOriginal) && Matches(rom, OpSite, OpOriginal) && Matches(rom, InitSite, InitOriginal);

    public static bool IsApplied(RomBuffer rom) => BlockOffset(rom) > 0;

    private static bool Matches(RomBuffer rom, int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    /// <summary>File offset of the patch's block (its magic), or 0.</summary>
    public static int BlockOffset(RomBuffer rom)
    {
        if (rom.ReadByte(OpSite + 2) != 0x22) return 0;
        int hook = FileOffset(rom.ReadByte(OpSite + 3) | rom.ReadByte(OpSite + 4) << 8 | rom.ReadByte(OpSite + 5) << 16);
        int block = hook - OpHookOffset;
        return block > 0 && Matches(rom, block, Magic) ? block : 0;
    }

    /// <summary>Adds the commands (a no-op when they're there; an older version is brought up to date).</summary>
    public static void Apply(RomBuffer rom)
    {
        int old = BlockOffset(rom);
        if (old > 0 && rom.ReadByte(old + 4) == Version) return;
        if (old == 0 && !IsOriginal(rom))
            throw new InvalidOperationException("The game's monster AI code ($08:CAC6, $01:F658) isn't the original, so the new AI commands can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        if (old > 0) space.Free(old);
        var probe = Build(0x808000);
        int at = space.AllocateInOneBank(probe.Length, ExpansionSpace.Kind.Patch, 30);
        space.Save();
        int snes = SnesAddress(at);
        rom.WriteBytes(at, Build(snes));
        void Jsl(int site, int offset)
        {
            int a = snes + offset;
            rom.WriteBytes(site, new byte[] { 0x22, (byte)a, (byte)(a >> 8), (byte)(a >> 16) });
        }
        Jsl(TurnSite, TurnStartOffset); rom.WriteByte(TurnSite + 4, 0xEA);
        rom.WriteBytes(OpSite, new byte[] { 0xA0, 0x00 });
        Jsl(OpSite + 2, OpHookOffset);
        rom.WriteBytes(OpSite + 6, new byte[] { 0xB0, 0x35, 0x70, 0x03, 0xFC, 0x0C, 0xCB });
        Jsl(InitSite, InitHookOffset); rom.WriteBytes(InitSite + 4, new byte[] { 0xEA, 0xEA });
        rom.FixChecksum();
    }

    private static int TurnStartOffset, OpHookOffset, InitHookOffset;

    static AiCommandsPatch()
    {
        var a = Assemble();
        TurnStartOffset = a.Offset("TurnStart"); OpHookOffset = a.Offset("OpHook"); InitHookOffset = a.Offset("InitHook");
    }

    private static byte[] Build(int snes) => Assemble().Build(snes);

    // opcodes used below
    private const byte LDA_dp = 0xA5, LDA_indY = 0xB1, CMP_indY = 0xD1, STA_dp = 0x85, LDA_absX = 0xBD, LDA_absl = 0xAF,
        LDA_longX = 0xBF, STA_longX = 0x9F, STZ_abs = 0x9C, STZ_absX = 0x9E, STA_abs = 0x8D, STA_absX = 0x9D,
        JSR = 0x20, JMP = 0x4C, JSL = 0x22, JSRix = 0xFC, RTS = 0x60, RTL = 0x6B,
        BRA = 0x80, BEQ = 0xF0, BNE = 0xD0, BCC = 0x90, BCS = 0xB0;

    private const int Rec = 0x7EE500, Turn = 0x70, Flags = 0x71, PartyHp = 0x7EDA48, MaxHp = 0x16E0;

    private static Asm65816 Assemble()
    {
        var a = new Asm65816();
        a.B(Magic).B(Version);

        // ── a monster's turn starts (M = X = 16-bit): as the game, then its turn count + 1 (stops at 255) ──
        a.Label("TurnStart").B(0xE2, 0x30).B(STZ_abs, 0x60, 0x14)
         .Abs(JSR, "RecX")
         .Long(LDA_longX, Rec + Turn).B(0x1A).Br(BEQ, "ts_full").Long(STA_longX, Rec + Turn)
         .Label("ts_full").B(0xE2, 0x30).B(RTL);

        // ── reads a command (M = X = 8-bit, Y = 0): C = end, V = done here, else X = (op-1)*2 for the game's table ──
        a.Label("OpHook").B(LDA_indY, 0x09).Br(BNE, "oh_op").B(0x38, 0xB8, RTL)
         .Label("oh_op").B(0xC8).B(0xC9, First).Br(BCS, "oh_new")
         .B(0x3A, 0x0A, 0xAA, 0x18, 0xB8, RTL)
         .Label("oh_new").B(0xC9, First + Count).Br(BCS, "oh_bad")
         .B(0x38, 0xE9, First, 0x0A, 0xAA).Abs(JSRix, "Table")
         .B(0x18, 0xE2, 0x40, RTL)                    // CLC, SEP #$40 (V): carry on with the next command
         .Label("oh_bad").B(0x38, 0xB8, RTL);         // a number nobody knows: the script ends

        a.Label("Table");
        foreach (var l in new[] { "op14", "op15", "op16", "op17", "op18", "op19", "op1A", "op1B", "op1C", "op1D" }) a.Word(l);

        // ── a monster enters a battle (M = X = 16-bit, DB = 7E, X = its record): as the game, then clear +$70-$73 ──
        a.Label("InitHook").B(LDA_absX, 0x13, 0x00, STA_absX, 0x44, 0x00).B(STZ_absX, Turn, 0x00).B(STZ_absX, 0x72, 0x00).B(RTL);

        // ── helpers ──
        // X = this monster's slot x $80 (M = 8-bit, X = 16-bit on return)
        a.Label("RecX").B(LDA_dp, 0x08, 0xC2, 0x30, 0x29, 0xFF, 0x00, 0xEB, 0x4A, 0xAA, 0xE2, 0x20, RTS);
        // C set: go to the word at ($09),Y (from the record's start); clear: step over it
        a.Label("Cond").Br(BCS, "c_go").B(0xC8, 0xC8, RTS)
         .Label("c_go").B(0xC2, 0x20, LDA_indY, 0x09, 0x18, 0x65, 0x0C, STA_dp, 0x09, 0xE2, 0x20, 0xA0, 0x00, RTS);
        // A = 1 << (A & 7)
        a.Label("Bit").B(0x29, 0x07, 0xAA, 0xA9, 0x01)
         .Label("b_l").B(0xE0, 0x00).Br(BEQ, "b_d").B(0x0A, 0xCA).Br(BRA, "b_l")
         .Label("b_d").B(RTS);

        // 14 N, to: if its turn count >= N, go to
        a.Label("op14").B(0x5A).Abs(JSR, "RecX").Long(LDA_longX, Rec + Turn).B(0xE2, 0x10, 0x7A)
         .B(CMP_indY, 0x09, 0xC8).Abs(JMP, "Cond");

        // 15 N, to: every N turns (turn count a multiple of N), go to
        a.Label("op15").B(LDA_indY, 0x09).Br(BEQ, "e_never").B(0x48, 0x5A).Abs(JSR, "RecX").Long(LDA_longX, Rec + Turn).B(0xE2, 0x10, 0x7A)
         .Label("e_l").B(0xC3, 0x01).Br(BCC, "e_d").B(0x38, 0xE3, 0x01).Br(BRA, "e_l")
         .Label("e_d").B(0xFA, 0xC9, 0x00).Br(BEQ, "e_yes").B(0x18).Br(BRA, "e_go")
         .Label("e_yes").B(0x38)
         .Label("e_go").B(0xC8).Abs(JMP, "Cond")
         .Label("e_never").B(0xC8, 0x18).Abs(JMP, "Cond");

        // 16 share, to: if a party member (there and standing) has HP below max HP x share / 256, go to
        a.Label("op16").B(LDA_indY, 0x09, STA_dp, 0xEA, 0x5A, 0xA2, 0x00)
         .Label("h_l").B(0xC2, 0x20).Long(LDA_longX, PartyHp).Br(BEQ, "h_n").B(0xC9, 0xFF, 0xFF).Br(BEQ, "h_n")
         .B(0x48, LDA_absX, MaxHp & 0xFF, MaxHp >> 8, STA_dp, 0xE8).Long(JSL, 0x01F535)
         .B(0x68, 0xC5, 0xEC).Br(BCC, "h_yes")
         .Label("h_n").B(0xE2, 0x20, 0xE8, 0xE8, 0xE0, 0x08).Br(BNE, "h_l")
         .B(0x7A, 0x18, 0xC8).Abs(JMP, "Cond")
         .Label("h_yes").B(0xE2, 0x20, 0x7A, 0x38, 0xC8).Abs(JMP, "Cond");

        // 17 / 18: target the party member (there and standing) with the lowest / highest HP
        foreach (var (name, low) in new[] { ("op17", true), ("op18", false) })
        {
            string p = name + "_";
            a.Label(name).B(0x5A, 0xA0, 0xFF, 0xC2, 0x20).B(0xA9).B(low ? new byte[] { 0xFF, 0xFF } : new byte[] { 0x00, 0x00 }).B(STA_dp, 0xE8, 0xA2, 0x00)
             .Label(p + "l").Long(LDA_longX, PartyHp).Br(BEQ, p + "n").B(0xC9, 0xFF, 0xFF).Br(BEQ, p + "n")
             .B(0xC5, 0xE8).Br(low ? BCS : BCC, p + "n").B(STA_dp, 0xE8, 0x9B)
             .Label(p + "n").B(0xE8, 0xE8, 0xE0, 0x08).Br(BNE, p + "l")
             .B(0xE2, 0x20, 0x98, 0xC9, 0xFF).Br(BEQ, p + "x").B(0x4A, 0x09, 0x80, STA_abs, 0x3F, 0x14)
             .Label(p + "x").B(0x7A, RTS);
        }

        // 19 to: if it is the only monster left, go to
        a.Label("op19").B(0x5A, 0xA0, 0x00, 0xA2, 0x00)
         .Label("a_l").B(LDA_absX, 0x7A, 0x13, 0xC9, 0xFF).Br(BEQ, "a_n").B(0xC8)
         .Label("a_n").B(0xE8, 0xE0, 0x08).Br(BNE, "a_l")
         .B(0x98, 0x7A, 0xC9, 0x02).Br(BCS, "a_no").B(0x38).Abs(JMP, "Cond")
         .Label("a_no").B(0x18).Abs(JMP, "Cond");

        // 1A N, to: if N or fewer party members are standing, go to
        a.Label("op1A").B(LDA_indY, 0x09, 0x48, 0x5A, 0xA0, 0x00, 0xA2, 0x00)
         .Label("s_l").B(0xC2, 0x20).Long(LDA_longX, PartyHp).Br(BEQ, "s_n").B(0xC9, 0xFF, 0xFF).Br(BEQ, "s_n").B(0xC8)
         .Label("s_n").B(0xE2, 0x20, 0xE8, 0xE8, 0xE0, 0x08).Br(BNE, "s_l")
         .B(0x98, 0x7A, 0xC3, 0x01).Br(BCC, "s_yes").Br(BEQ, "s_yes")
         .B(0xFA, 0x18, 0xC8).Abs(JMP, "Cond")
         .Label("s_yes").B(0xFA, 0x38, 0xC8).Abs(JMP, "Cond");

        // 1B n / 1C n: switch n (0-7) on / off
        foreach (var (name, on) in new[] { ("op1B", true), ("op1C", false) })
        {
            a.Label(name).B(LDA_indY, 0x09).Abs(JSR, "Bit");
            if (!on) a.B(0x49, 0xFF);
            a.B(0x48, 0x5A).Abs(JSR, "RecX").Long(LDA_longX, Rec + Flags).B(on ? (byte)0x03 : (byte)0x23, 0x02).Long(STA_longX, Rec + Flags)
             .B(0xE2, 0x10, 0x7A, 0xFA, 0xC8, RTS);
        }

        // 1D n, to: if switch n is on, go to
        a.Label("op1D").B(LDA_indY, 0x09).Abs(JSR, "Bit").B(0x48, 0x5A).Abs(JSR, "RecX").Long(LDA_longX, Rec + Flags).B(0x23, 0x02)
         .B(0xE2, 0x10, 0x7A, 0xFA, 0xC9, 0x00).Br(BEQ, "f_no").B(0x38, 0xC8).Abs(JMP, "Cond")
         .Label("f_no").B(0x18, 0xC8).Abs(JMP, "Cond");

        return a;
    }
}
