namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    /// <summary>
    /// Battles in the cave give 1/8 of the gold: each defeated monster's gold (its base plus the random part) is added to
    /// the battle's total at $141D-$141F; in the cave it's divided by 8 first, so the victory message shows what the party
    /// gets.
    /// <code>
    /// $08:BC70  22 GoldHook EA          a monster's gold (was LDA $1C / CLC / ADC $16)
    /// </code>
    /// </summary>
    private const string GoldSource = """
        GoldHook:                   ; M = 16: A = this monster's gold ($1C random part + $16 base)
            LDA $1C
            CLC
            ADC $16
            PHA
            LDA.l !AC_INCAVE
            AND.w #$00FF
            BEQ +
            PLA
            LSR
            LSR
            LSR
            RTL
        +   PLA
            RTL
        """;

    private const int GoldSite = 0x43C70;   // $08:BC70
    private static readonly byte[] GoldOriginal = { 0xA5, 0x1C, 0x18, 0x65, 0x16 };

    /// <summary>EXP in the cave (x <see cref="ExpMultiplier"/>): the party starts at level 1 and has to keep up with the
    /// floors, so each defeated monster's EXP is multiplied before it goes into the battle's total at $141A-$141C (at
    /// most FFFF a monster).
    /// <code>
    /// $08:BC3E  22 ExpHook               a monster's EXP (was LDA $001F,X / CLC)
    /// </code>
    /// </summary>
    public const int ExpMultiplier = 3;

    private const string ExpSource = """
        ExpHook:                    ; M = 16, X = the monster's battle record, DB = 7E
            LDA $001F,x
            PHA
            LDA.l !AC_INCAVE
            AND.w #$00FF
            BEQ +
            LDA $01,s               ; x 3
            ASL
            BCS .max
            CLC
            ADC $01,s
            BCC ++
        .max
            LDA.w #$FFFF
        ++  STA $01,s
        +   PLA
            CLC
            RTL
        """;

    private const int ExpSite = 0x43C3E;    // $08:BC3E
    private static readonly byte[] ExpOriginal = { 0xBD, 0x1F, 0x00, 0x18 };
}
