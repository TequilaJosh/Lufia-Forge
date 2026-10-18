namespace LufiaForge.Core.Battle;

public static partial class AiCommandsPatch
{
    /// <summary>
    /// Command 25 n: the monster takes n actions this turn (1-8, as Ok Impala's op 1B in the AI extension v2). After
    /// each action has played out, the monster acts again at once and its script carries on from the next line (not
    /// from the top). Hooks:
    /// <code>
    /// $08:CAC6  22 TurnStart EA          a monster's turn starts: count it, or carry on where an extra action left off
    /// $08:CAE2  22 PostAction D0 20 80 E1 EA   after each command (was STA $0A / LDA $1460 / BNE $CB08 / BRA $CACB):
    ///                                    an action was chosen with extra actions left: note where the script stopped
    /// $08:B578  22 TimerHook             a battler's turn timer is set after its action (was ASL / STA $06C1,X): a
    ///                                    monster with an extra action left gets 0, so it acts again right away
    /// </code>
    /// Battle record (+$70 turn count ...): +$72 extra actions left, +$73 "carry on" flag, +$74/+$75 where to carry on.
    /// +$72/+$73 are cleared when a monster enters a battle (InitHook).
    /// </summary>
    private const string MultiSource = """
        !EXTRA  = $7EE572
        !RESUME = $7EE573
        !RESPTR = $7EE574

        TurnStart:                  ; $08:CAC6 (in place of SEP #$30 / STZ $1460); $09 = the script's start
            SEP #$30
            STZ $1460
            JSR RecX                ; X = the monster's record (16-bit), M = 8
            LDA.l !RESUME,x
            BEQ .fresh
            LDA.b #$00              ; an extra action: carry on where the last one stopped (not a new turn)
            STA.l !RESUME,x
            REP #$20
            LDA.l !RESPTR,x
            STA $09
            SEP #$20
            BRA .bonus
        .fresh
            LDA.b #$00
            STA.l !EXTRA,x          ; no extra actions until command 25 says so
            LDA.l $7EE570,x         ; turn count + 1 (stops at 255)
            INC A
            BEQ .bonus
            STA.l $7EE570,x
        .bonus
            JSR ClearBonus          ; (each action has its own bonus damage, command 23)
            SEP #$30
            RTL

        Op25_MultiAction:           ; 25 n: n actions this turn (1-8) -> n-1 extra
            LDA ($09),y
            INY
            PHY
            CMP.b #$00
            BEQ +
            DEC A
        +   CMP.b #$08
            BCC +
            LDA.b #$07
        +   PHA
            JSR RecX
            PLA
            STA.l !EXTRA,x
            SEP #$10
            PLY
            RTS

        PostAction:                 ; $08:CAE2, M = X = 8; A = the pointer's high byte, $09 = the next command
            STA $0A                 ; (replaced)
            LDA $1460
            BEQ .out                ; no action chosen yet: the script goes on
            JSR RecX
            LDA.l !EXTRA,x
            BEQ .last
            DEC A
            STA.l !EXTRA,x
            LDA.b #$01
            STA.l !RESUME,x
            REP #$20
            LDA $09
            STA.l !RESPTR,x
            SEP #$20
        .last
            SEP #$30
        .out
            LDA $1460               ; (the hook's BNE: the turn ends when an action was chosen)
            RTL

        TimerHook:                  ; $08:B578, M = 16, X = 8, Y = the battler (monsters 0-7), DB = the caller's
            ASL A                   ; (replaced)
            STA $06C1,x
            PHP
            CPY.b #$08
            BCS .out
            REP #$30
            PHX
            TYA
            AND.w #$00FF
            XBA
            LSR
            TAX
            SEP #$20
            LDA.l !RESUME,x
            BEQ .keep
            REP #$20
            PLX
            STZ $06C1,x             ; an extra action is waiting: it acts again at once
            BRA .out
        .keep
            PLX
        .out
            PLP
            RTL
        """;

    private const int PostSite = 0x44AE2;   // $08:CAE2
    private const int TimerSite = 0x43578;  // $08:B578
    private static readonly byte[] PostOriginal = { 0x85, 0x0A, 0xAD, 0x60, 0x14, 0xD0, 0x1F, 0x80, 0xE0 };
    private static readonly byte[] TimerOriginal = { 0x0A, 0x9D, 0xC1, 0x06 };
}
