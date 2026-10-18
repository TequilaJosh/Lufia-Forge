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
}
