namespace LufiaForge.Core.Battle;

public static partial class AiCommandsPatch
{
    /// <summary>
    /// Commands 1E-24: Ok Impala's "Enemy AI extensions" (Lufia1_AI_Ext, ops 14-1A there), ported from the source
    /// they shared: the same routines, renumbered to follow Lufia Forge's own 14-1D and called from Lufia Forge's
    /// command hook instead of their own. Their bonus damage hook is the one at $08:C225.
    /// Ops 17/18 (lowest / highest HP target) use the same party scan (characters in the party list $14D2, KO bit
    /// $20 of $157B).
    /// <code>
    /// 1E who stat cmp val16 to   if a party member check is true, go to
    /// 1F stat mode               target the standing party member with the highest (00) / lowest (01) stat
    /// 20 n val                   number n (0-3) = val
    /// 21 n add                   number n += add (signed, stays 0-255)
    /// 22 n cmp val to            if number n cmp val, go to
    /// 23 mode pct                this turn each hit adds pct% of the target's max (00) / current (01) HP, up to 9999
    /// 24 n who stat              number n = a party value (stays 0-255)
    /// </code>
    /// who: 00-03 a character, 10 any standing member (24: the highest), 11 all standing (24: the lowest),
    /// 12 the current target, 13 how many are standing. stat: 0 level, 1 HP, 2 max HP, 3 MP, 4 max MP, 5 HP %,
    /// 6 status ($157B, $20 = KO), 7 standing (1/0). cmp: 0 =, 1 not =, 2 &lt;, 3 &lt;=, 4 &gt;, 5 &gt;=, 6 any bits, 7 no bits.
    /// Numbers are battle record +$78-$7B, the bonus +$7C (percent) / +$7D (mode), as in Ok Impala's patch;
    /// scratch work RAM $7F:FF00-$FF1F.
    /// </summary>
    private const string ExtSource = """
        !who     = $7FFF00
        !stat    = $7FFF01
        !cmp     = $7FFF02
        !char    = $7FFF03
        !val     = $7FFF04
        !opnd    = $7FFF06
        !off     = $7FFF08
        !cnt     = $7FFF0B
        !best    = $7FFF0C
        !bestpos = $7FFF0E
        !mode    = $7FFF0F
        !var     = $7FFF10
        !VAROFS   = $78
        !BONUSOFS = $7C

        ; 1E who stat cmp val16 off16
        Op1E_HeroCheck:
            LDA ($09),y : INY : STA !who
            LDA ($09),y : INY : STA !stat
            LDA ($09),y : INY : STA !cmp
            LDA ($09),y : INY : STA !opnd
            LDA ($09),y : INY : STA !opnd+1
            LDA ($09),y : INY : STA !off
            LDA ($09),y : INY : STA !off+1
            PHY
            JSR EvalWho
            PLY
            BCC +
            JMP DoJump
        +   RTS

        ; 1F stat mode
        Op1F_TargetHero:
            LDA ($09),y : INY : STA !stat
            LDA ($09),y : INY : STA !mode
        TargetBest:
            PHY
            JSR ScanBest
            LDA !bestpos
            CMP.b #$FF
            BEQ +
            JSR HeroPos
            TXA
            ORA.b #$80
            STA $7E143F
        +   PLY
            RTS

        ; 17 / 18: the standing party member with the lowest / highest HP
        Op17_LowestHp:
            LDA.b #$01 : STA !mode
            LDA.b #$01 : STA !stat
            BRA TargetBest
        Op18_HighestHp:
            LDA.b #$00 : STA !mode
            LDA.b #$01 : STA !stat
            BRA TargetBest

        ; 20 v val
        Op20_VarSet:
            LDA ($09),y : INY : AND.b #$03 : CLC : ADC.b #!VAROFS : STA $00 : STZ $01
            LDA ($09),y : INY : PHA
            LDA $08
            JSR SlotX
            PLA
            STA $7E0000,x
            SEP #$10
            RTS

        ; 21 v n   (n is signed)
        Op21_VarAdd:
            LDA ($09),y : INY : AND.b #$03 : CLC : ADC.b #!VAROFS : STA $00 : STZ $01
            LDA ($09),y : INY : STA $02
            LDA $08
            JSR SlotX
            LDA $02
            BMI .neg
            CLC
            ADC $7E0000,x
            BCC .st
            LDA.b #$FF
            BRA .st
        .neg
            CLC
            ADC $7E0000,x
            BCS .st
            LDA.b #$00
        .st
            STA $7E0000,x
            SEP #$10
            RTS

        ; 22 v cmp val off16
        Op22_VarCmp:
            LDA ($09),y : INY : AND.b #$03 : CLC : ADC.b #!VAROFS : STA $00 : STZ $01
            LDA ($09),y : INY : STA !cmp
            LDA ($09),y : INY : STA !opnd
            LDA.b #$00 : STA !opnd+1
            LDA ($09),y : INY : STA !off
            LDA ($09),y : INY : STA !off+1
            PHY
            LDA $08
            JSR SlotX
            LDA $7E0000,x
            STA !val
            LDA.b #$00
            STA !val+1
            SEP #$10
            JSR Compare
            PLY
            BCC +
            JMP DoJump
        +   RTS

        ; 23 mode pct
        Op23_HPBonus:
            LDA ($09),y : INY : STA $02
            LDA ($09),y : INY : STA $03
            LDA.b #!BONUSOFS : STA $00 : STZ $01
            LDA $08
            JSR SlotX
            LDA $03
            STA $7E0000,x
            LDA $02
            STA $7E0001,x
            SEP #$10
            RTS

        ; 24 v who stat
        Op24_VarGet:
            LDA ($09),y : INY : AND.b #$03 : CLC : ADC.b #!VAROFS : STA !var
            LDA ($09),y : INY : STA !who
            LDA ($09),y : INY : STA !stat
            PHY
            JSR GetWhoValue
            LDA !val+1
            BEQ +
            LDA.b #$FF
            STA !val
        +   LDA !var : STA $00 : STZ $01
            LDA $08
            JSR SlotX
            LDA !val
            STA $7E0000,x
            SEP #$10
            PLY
            RTS

        ; Jump: $09 = record base ($0C) + off, Y = 0
        DoJump:
            REP #$20
            LDA !off
            CLC
            ADC $0C
            STA $09
            SEP #$20
            LDY.b #$00
            RTS

        ; SlotX: A(8) = monster slot, $00/$01 = byte offset -> X = $E500 + slot*$80 + offset (exit M=8, X=16)
        SlotX:
            REP #$30
            AND.w #$0007
            XBA
            LSR
            CLC
            ADC $00
            CLC
            ADC.w #$E500
            TAX
            SEP #$20
            RTS

        ; HeroPos: A = character (0-3). C=1 if in the battle party, X = position
        HeroPos:
            TAX
            LDA $7E156F,x
            BEQ .no
            TXA
            LDX.b #$03
        -   CMP $7E14D2,x
            BEQ .yes
            DEX
            BPL -
        .no
            CLC
            RTS
        .yes
            SEC
            RTS

        ; HeroAlive: A = character. C=1 if in battle and not KO'd
        HeroAlive:
            PHA
            JSR HeroPos
            PLA
            BCC .no
            TAX
            LDA $7E157B,x
            AND.b #$20
            BNE .no
            SEC
            RTS
        .no
            CLC
            RTS

        ; TargetChar: C=1 and A = character if the current target is a party member
        TargetChar:
            LDA $7E143F
            BPL .no
            CMP.b #$FF
            BEQ .no
            AND.b #$03
            TAX
            LDA $7E14D2,x
            CMP.b #$04
            BCS .no
            PHA
            JSR HeroPos
            PLA
            RTS
        .no
            CLC
            RTS

        ; CountLiving -> A
        CountLiving:
            LDA.b #$00
            STA !cnt
            STA !char
        -   LDA !char
            JSR HeroAlive
            BCC +
            LDA !cnt
            INC A
            STA !cnt
        +   LDA !char
            INC A
            STA !char
            CMP.b #$04
            BCC -
            LDA !cnt
            RTS

        ; GetHeroStat: !char, !stat -> !val (16-bit)
        GetHeroStat:
            REP #$20
            LDA.w #$0000
            STA !val
            SEP #$20
            LDA !stat
            CMP.b #$08
            BCS .done
            ASL A
            TAX
            JMP (.tbl,x)
        .done
            RTS
        .tbl
            dw .lv, .chp, .mhp, .cmp, .mmp, .pct, .st, .alive
        .lv
            LDA !char : TAX
            LDA $7E156F,x
            STA !val
            RTS
        .st
            LDA !char : TAX
            LDA $7E157B,x
            STA !val
            RTS
        .alive
            LDA !char
            JSR HeroAlive
            LDA.b #$00
            ROL A
            STA !val
            RTS
        .chp
            LDA !char : ASL A : TAX
            REP #$20 : LDA $7E157F,x : STA !val : SEP #$20
            RTS
        .mhp
            LDA !char : ASL A : TAX
            REP #$20 : LDA $7E16E0,x : STA !val : SEP #$20
            RTS
        .cmp
            LDA !char : ASL A : TAX
            REP #$20 : LDA $7E1587,x : STA !val : SEP #$20
            RTS
        .mmp
            LDA !char : ASL A : TAX
            REP #$20 : LDA $7E16E8,x : STA !val : SEP #$20
            RTS
        .pct
            LDA !char : ASL A : TAX
            REP #$20
            LDA $7E16E0,x
            BEQ +
            STA $04
            LDA $7E157F,x
            STA $00
            SEP #$20
            LDA.b #100
            STA $02
            JSR MulDiv
            REP #$20
            LDA $00
            STA !val
        +   SEP #$20
            RTS

        ; Compare: !val <!cmp> !opnd -> C=1 if true (16-bit compare)
        Compare:
            LDA !cmp
            CMP.b #$08
            BCS .f8
            ASL A
            TAX
            REP #$20
            LDA !val
            JMP (.tbl,x)
        .tbl
            dw .eq, .ne, .lt, .le, .gt, .ge, .any, .none
        .eq
            CMP !opnd : BEQ .t : BRA .f
        .ne
            CMP !opnd : BNE .t : BRA .f
        .lt
            CMP !opnd : BCC .t : BRA .f
        .le
            CMP !opnd : BCC .t : BEQ .t : BRA .f
        .gt
            CMP !opnd : BEQ .f : BCS .t : BRA .f
        .ge
            CMP !opnd : BCS .t : BRA .f
        .any
            AND !opnd : BNE .t : BRA .f
        .none
            AND !opnd : BEQ .t : BRA .f
        .t
            SEP #$20
            SEC
            RTS
        .f
            SEP #$20
        .f8
            CLC
            RTS

        ; EvalWho: !who/!stat/!cmp/!opnd -> C=1 if the condition is true
        EvalWho:
            SEP #$30
            LDA !who
            CMP.b #$04
            BCS .group
            STA !char
        .single
            LDA !char
            JSR HeroPos
            BCC .false
            JSR GetHeroStat
            JMP Compare
        .group
            CMP.b #$10 : BEQ .any
            CMP.b #$11 : BEQ .all
            CMP.b #$12 : BEQ .target
            CMP.b #$13 : BEQ .count
        .false
            CLC
            RTS
        .target
            JSR TargetChar
            BCC .false
            STA !char
            BRA .single
        .count
            JSR CountLiving
            STA !val
            LDA.b #$00
            STA !val+1
            JMP Compare
        .any
            LDA.b #$00
            STA !char
        -   LDA !char
            JSR HeroAlive
            BCC +
            JSR GetHeroStat
            JSR Compare
            BCS .true
        +   LDA !char
            INC A
            STA !char
            CMP.b #$04
            BCC -
            CLC
            RTS
        .true
            SEC
            RTS
        .all
            LDA.b #$00
            STA !char
            STA !cnt
        -   LDA !char
            JSR HeroAlive
            BCC +
            LDA !cnt
            INC A
            STA !cnt
            JSR GetHeroStat
            JSR Compare
            BCC .false
        +   LDA !char
            INC A
            STA !char
            CMP.b #$04
            BCC -
            LDA !cnt
            BEQ .false
            SEC
            RTS

        ; ScanBest: !stat, !mode (0 = highest, 1 = lowest) over standing members
        ;   -> !bestpos = character (or $FF if nobody), !best = value
        ScanBest:
            LDA.b #$FF
            STA !bestpos
            LDA.b #$00
            STA !char
        .loop
            LDA !char
            JSR HeroAlive
            BCC .next
            JSR GetHeroStat
            LDA !bestpos
            CMP.b #$FF
            BEQ .take
            REP #$20
            LDA !val
            CMP !best
            SEP #$20
            BEQ .next
            LDA !mode
            BNE .min
            BCS .take
            BRA .next
        .min
            BCC .take
            BRA .next
        .take
            REP #$20
            LDA !val
            STA !best
            SEP #$20
            LDA !char
            STA !bestpos
        .next
            LDA !char
            INC A
            STA !char
            CMP.b #$04
            BCC .loop
            RTS

        ; GetWhoValue: !who/!stat -> !val
        GetWhoValue:
            SEP #$30
            REP #$20
            LDA.w #$0000
            STA !val
            SEP #$20
            LDA !who
            CMP.b #$04
            BCS .g
            STA !char
            JSR HeroPos
            BCC .r
            JMP GetHeroStat
        .g
            CMP.b #$10 : BEQ .max
            CMP.b #$11 : BEQ .min
            CMP.b #$12 : BEQ .tgt
            CMP.b #$13 : BEQ .cnt
        .r
            RTS
        .max
            LDA.b #$00
            BRA .scan
        .min
            LDA.b #$01
        .scan
            STA !mode
            JSR ScanBest
            LDA !bestpos
            CMP.b #$FF
            BEQ .r
            REP #$20
            LDA !best
            STA !val
            SEP #$20
            RTS
        .tgt
            JSR TargetChar
            BCC .r
            STA !char
            JMP GetHeroStat
        .cnt
            JSR CountLiving
            STA !val
            RTS

        ; MulDiv: $00/$01 = x (16), $02 = m (8), $04/$05 = d (16, nonzero) -> $00/$01 = x*m/d (up to $FFFF). Uses $06/$07.
        MulDiv:
            PHP
            SEP #$30
            LDA $00 : STA $004202
            LDA $02 : STA $004203
            NOP : NOP : NOP : NOP
            LDA $004216 : STA $06
            LDA $004217 : STA $07
            LDA $01 : STA $004202
            LDA $02 : STA $004203
            NOP : NOP : NOP : NOP
            REP #$20
            LDA $07
            AND.w #$00FF
            CLC
            ADC $004216
            STA $01
            SEP #$20
            LDA $06
            STA $00
            REP #$20
            STZ $06
            SEP #$20
            LDX.b #24
        .loop
            ASL $00
            ROL $01
            ROL $02
            ROL $06
            ROL $07
            REP #$20
            LDA $06
            SEC
            SBC $04
            BCC .skip
            STA $06
            INC $00
        .skip
            SEP #$20
            DEX
            BNE .loop
            LDA $02
            BEQ +
            LDA.b #$FF
            STA $00
            STA $01
        +   PLP
            RTS

        ; ClearBonus: at the start of every scripted monster turn (from TurnStart)
        ClearBonus:
            PHP
            SEP #$30
            LDA.b #!BONUSOFS : STA $00 : STZ $01
            LDA $08
            JSR SlotX
            LDA.b #$00
            STA $7E0000,x
            PLP
            RTS

        ; DamageHook ($08:C225, every hit just before the number is shown and applied): when the attacker is a
        ; monster with a 23 bonus this turn, add pct% of the target's max / current HP to the damage ($1443).
        DamageHook:
            PHP
            PHB
            PHD
            REP #$30
            PHA
            PHX
            PHY
            LDA.w #$0000
            TCD
            LDA $00 : PHA
            LDA $02 : PHA
            LDA $04 : PHA
            LDA $06 : PHA
            SEP #$30
            LDA $7E1440
            CMP.b #$08
            BCC .k1
            JMP .out
        .k1
            STA $03
            LDA.b #!BONUSOFS : STA $00 : STZ $01
            LDA $03
            JSR SlotX
            LDA $7E0000,x
            BNE .k2
            JMP .out
        .k2
            STA $02
            LDA $7E0001,x
            STA $03
            SEP #$30
            REP #$20
            LDA $7E1443
            SEP #$20
            BNE .k3
            JMP .out
        .k3
            LDA $7E143F
            CMP.b #$FF
            BNE .k4
            JMP .out
        .k4
            CMP.b #$80
            BCS .hero
            PHA
            LDA $03
            BNE +
            LDA.b #$11
            BRA ++
        +   LDA.b #$42
        ++  STA $00
            STZ $01
            PLA
            JSR SlotX
            REP #$20
            LDA $7E0000,x
            BRA .have
        .hero
            AND.b #$03
            TAX
            LDA $7E14D2,x
            CMP.b #$04
            BCC .k5
            JMP .out
        .k5
            ASL A
            TAX
            LDA $03
            BNE +
            REP #$20
            LDA $7E16E0,x
            BRA .have
        +   REP #$20
            LDA $7E157F,x
        .have
            STA $00
            LDA.w #100
            STA $04
            SEP #$30
            JSR MulDiv
            REP #$20
            LDA $7E1443
            CLC
            ADC $00
            BCS .clamp
            CMP.w #10000
            BCC .store
        .clamp
            LDA.w #9999
        .store
            STA $7E1443
        .out
            REP #$30
            PLA : STA $06
            PLA : STA $04
            PLA : STA $02
            PLA : STA $00
            PLY
            PLX
            PLA
            PLD
            PLB
            PLP
            LDA.b #$32
            STA $7EDAC6
            RTL
        """;
}
