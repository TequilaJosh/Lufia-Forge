using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// More monster AI commands (14-25) for every monster: the game's own and new ones (1E-24 and the party scan of
/// 17/18 are Ok Impala's AI extensions, see AiCommandsPatch.Ext.cs). The battle's AI interpreter
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
/// $08:C225  22 DamageHook EA EA        a hit's damage is about to be shown: add the 23 bonus (was LDA #$32 / STA $7EDAC6)
/// $08:CAE2, $08:B578                    command 25 (several actions a turn), see AiCommandsPatch.Multi.cs
/// </code>
/// Each monster's battle record ($7E:E500 + slot x $80) keeps, in bytes the game never uses: +$70 its turn count and
/// +$71 eight switches for phases, +$78-$7B numbers 0-3, +$7C/+$7D the bonus damage of command 23. Party: current HP $7E:DA48 (FFFF = empty slot), max HP $16E0, slot order;
/// a party member as a target is $80 + slot ($143F). Jumps count from the record's start, as the game's own.
/// </summary>
public static partial class AiCommandsPatch
{
    public const int First = 0x14, Count = 18;

    private const int TurnSite = 0x44AC6;   // $08:CAC6
    private const int OpSite = 0x44ACB;     // $08:CACB
    private const int InitSite = 0xF658;    // $01:F658
    private const int DamageSite = 0x44225; // $08:C225
    private static readonly byte[] TurnOriginal = { 0xE2, 0x30, 0x9C, 0x60, 0x14 };
    private static readonly byte[] OpOriginal = { 0xA0, 0x00, 0xB1, 0x09, 0xF0, 0x37, 0xC8, 0x3A, 0x0A, 0xAA, 0xFC, 0x0C, 0xCB };
    private static readonly byte[] InitOriginal = { 0xBD, 0x13, 0x00, 0x9D, 0x44, 0x00 };
    private static readonly byte[] DamageOriginal = { 0xA9, 0x32, 0x8F, 0xC6, 0xDA, 0x7E };
    private static readonly byte[] Magic = "LFAI"u8.ToArray();
    private const byte Version = 3;

    public static bool IsOriginal(RomBuffer rom) =>
        Matches(rom, TurnSite, TurnOriginal) && Matches(rom, OpSite, OpOriginal) && Matches(rom, InitSite, InitOriginal) && Matches(rom, DamageSite, DamageOriginal) &&
        Matches(rom, PostSite, PostOriginal) && Matches(rom, TimerSite, TimerOriginal);

    /// <summary>True when the ROM has Ok Impala's own AI extension patch (its ops 14-1A clash with Lufia Forge's).</summary>
    public static bool HasOkImpalaPatch(RomBuffer rom) =>
        Matches(rom, 0x47F90, new byte[] { 0xE0, 0x26, 0xB0, 0x03, 0x7C, 0x0C, 0xCB }) && rom.ReadByte(0x44AD5) == 0x20 && rom.ReadByte(0x44AD7) == 0xFF;

    public static bool IsApplied(RomBuffer rom) => BlockOffset(rom) > 0;

    private static bool Matches(RomBuffer rom, int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    /// <summary>File offset of the patch's block (its magic), or 0.</summary>
    public static int BlockOffset(RomBuffer rom)
    {
        if (rom.ReadByte(OpSite + 2) != 0x22) return 0;
        int hook = FileOffset(rom.ReadByte(OpSite + 3) | rom.ReadByte(OpSite + 4) << 8 | rom.ReadByte(OpSite + 5) << 16);
        // the hook is a little way into the block (where depends on the version): look back for the magic
        for (int block = hook - 4; block >= Math.Max(0, hook - 0x100); block--)
            if (Matches(rom, block, Magic)) return block;
        return 0;
    }

    /// <summary>Adds the commands (a no-op when they're there; an older version is brought up to date).</summary>
    public static void Apply(RomBuffer rom)
    {
        int old = BlockOffset(rom);
        if (old > 0 && rom.ReadByte(old + 4) == Version) return;
        if (old == 0 && HasOkImpalaPatch(rom))
            throw new InvalidOperationException("This ROM has Ok Impala's AI extension patch. Lufia Forge has the same commands built in (1E-24 here, 14-1A there), so use a ROM without that patch to edit monster scripts with the new commands.");
        if (old == 0 && !IsOriginal(rom))
            throw new InvalidOperationException("The game's monster AI code ($08:CAC6, $08:C225, $01:F658) isn't the original, so the new AI commands can't be added safely.");
        if (old > 0 && !Matches(rom, DamageSite, DamageOriginal) && rom.ReadByte(DamageSite) != 0x22)
            throw new InvalidOperationException("The game's damage code at $08:C225 isn't the original, so the new AI commands can't be added safely.");
        if (old > 0 && (!Matches(rom, PostSite, PostOriginal) && rom.ReadByte(PostSite) != 0x22 || !Matches(rom, TimerSite, TimerOriginal) && rom.ReadByte(TimerSite) != 0x22))
            throw new InvalidOperationException("The game's battle turn code ($08:CAE2, $08:B578) isn't the original, so the multi-action command can't be added safely.");
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
        Jsl(DamageSite, DamageHookOffset); rom.WriteBytes(DamageSite + 4, new byte[] { 0xEA, 0xEA });
        Jsl(PostSite, PostActionOffset); rom.WriteBytes(PostSite + 4, new byte[] { 0xD0, 0x20, 0x80, 0xE1, 0xEA });   // BNE $CB08, BRA $CACB
        Jsl(TimerSite, TimerHookOffset);
        rom.FixChecksum();
    }

    private static int TurnStartOffset, OpHookOffset, InitHookOffset, DamageHookOffset, PostActionOffset, TimerHookOffset;

    static AiCommandsPatch()
    {
        var a = Assemble();
        TurnStartOffset = a.Offset("TurnStart"); OpHookOffset = a.Offset("OpHook"); InitHookOffset = a.Offset("InitHook"); DamageHookOffset = a.Offset("DamageHook");
        PostActionOffset = a.Offset("PostAction"); TimerHookOffset = a.Offset("TimerHook");
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

        // (a monster's turn starts: TurnStart, in AiCommandsPatch.Multi.cs)

        // ── reads a command (M = X = 8-bit, Y = 0): C = end, V = done here, else X = (op-1)*2 for the game's table ──
        a.Label("OpHook").B(LDA_indY, 0x09).Br(BNE, "oh_op").B(0x38, 0xB8, RTL)
         .Label("oh_op").B(0xC8).B(0xC9, First).Br(BCS, "oh_new")
         .B(0x3A, 0x0A, 0xAA, 0x18, 0xB8, RTL)
         .Label("oh_new").B(0xC9, First + Count).Br(BCS, "oh_bad")
         .B(0x38, 0xE9, First, 0x0A, 0xAA).Abs(JSRix, "Table")
         .B(0xE2, 0x30, 0x18, 0xE2, 0x40, RTL)        // SEP #$30, CLC, SEP #$40 (V): carry on with the next command
         .Label("oh_bad").B(0x38, 0xB8, RTL);         // a number nobody knows: the script ends

        a.Label("Table");
        foreach (var l in new[] { "op14", "op15", "op16", "Op17_LowestHp", "Op18_HighestHp", "op19", "op1A", "op1B", "op1C", "op1D",
                                  "Op1E_HeroCheck", "Op1F_TargetHero", "Op20_VarSet", "Op21_VarAdd", "Op22_VarCmp", "Op23_HPBonus", "Op24_VarGet",
                                  "Op25_MultiAction" }) a.Word(l);

        // ── a monster enters a battle (M = X = 16-bit, DB = 7E, X = its record): as the game, then clear +$70-$73, +$78-$7D ──
        a.Label("InitHook").B(LDA_absX, 0x13, 0x00, STA_absX, 0x44, 0x00).B(STZ_absX, Turn, 0x00).B(STZ_absX, 0x72, 0x00)
         .B(STZ_absX, 0x78, 0x00).B(STZ_absX, 0x7A, 0x00).B(STZ_absX, 0x7C, 0x00).B(RTL);

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

        a.Source(ExtSource);
        a.Source(MultiSource);
        return a;
    }
}
