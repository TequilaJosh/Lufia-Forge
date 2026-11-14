; Lufia II (US): 16 new statuses (Lufia Forge, 2026-11-11). HOOK = where the added code goes (any bank; everything in
; it is reached by JSL / JML / BRL, so the block can be put anywhere).
;
; Bits of a fighter's new-status word:
;  0 Zombie  1 Frozen  2 Decay  3 Burnt  4 Berserk  5 Spooked  6 Amnesia  7 Piety
;  8 Regen   9 Doom   10 Blind 11 Stone 12 Bleed   13 Curse   14 Stop    15 Mana leak
; Zombie, Decay, Burnt and Blind stay on a hero after the battle (and in the save); the rest end with it.
;
; Entries (k*2): heroes 0-6 by their record ($0BAD + id * $BE), monster slots 7-12 ($0A6E), the capsule monster 13 ($0A6C).
;
; Battle script commands (new): 5D s c cause new status s with c% / 5E s c cure it with c% / 5F s p resist it by p%
; (in a "when hit" or armor script: this target only) / 60 s ward the targets against it for the battle.
; For 5E, 5F and 60, s = $10 means every new status at once (5F 10 64: immune to all of them, as the game's shared
; script 11 is to its own eight).

; ---------------------------------------------------------------- RAM ($7F:FC00-$7F:FD7F, cleared on first use)
MAGIC   = $7FFC00
NS      = $7FFC04
TBERS   = $7FFC20
TSTOP   = $7FFC40
TDOOM   = $7FFC60
ACTED   = $7FFC80
WARD    = $7FFCA0
CAUSED  = $7FFCC0
CURED   = $7FFCE0
EXTRA   = $7FFD00
INF     = $7FFD20
CURE    = $7FFD30
TINF    = $7FFD40
TCURE   = $7FFD50
ENT     = $7FFD60
XTRA    = $7FFD62
WARDACT = $7FFD64
INTGT   = $7FFD66
TMP0    = $7FFD68
TMP1    = $7FFD6A
TMP2    = $7FFD6C
TMP3    = $7FFD6E
TMPK    = $7FFD70
AMT     = $7FFD72
BLK     = $7FFD74
CNT     = $7FFD76
SLOT    = $7FFD78
PASS    = $7FFD7A
TMPS    = $7FFD7C
TMP4    = $7FFDDE
DOOMT   = $7FFD80
MONID   = $7FFD90
INACT   = $7FFD9C
RPAGE   = $7FFD9E
STON    = $7FFD7E
RSUM    = $7FFDE0
ALLK    = $7FFDEA          ; the status 5E / 5F work on when s = $10 (every new status)
; the element table's work (Lufia Forge's element percentages)
EK      = $7FFDA0
EBASE   = $7FFDA2
ESUM    = $7FFDA4
EBLK    = $7FFDA6
EMAG    = $7FFDA8
ENEG    = $7FFDAA
EF      = $7FFDAC
EPL     = $7FFDAE
EPH     = $7FFDB0
EE      = $7FFDB2
ECNT    = $7FFDB4
ETMP    = $7FFDB6
ESTART  = $7FFDB8
NBUF    = $7FFDC0

; ================================================================ the game's code: where the hooks go
org $85B464
  jsl OpCheck            ; LDA [$BB] / REP #$20 / ASL / TAX: commands 5D-60 go to the added code
  nop
  nop
org $81A7DA
  jsl TurnCheck          ; LDA $0F,X / BIT #$2C: frozen, stone, stopped (and spooked 1 in 4) skip; berserk = confused
  nop
org $81CA9B
  jsl CmdCheck           ; (16-bit) LDA $0F,Y / BIT #$3C: no command for frozen, stone, stopped, berserk heroes
  nop
  nop
org $859288
  jsl QConf              ; (16-bit) LDA $0F,Y / SEP #$20: berserk heroes are queued as confused
  nop
org $85925A
  jsl AllDis             ; LDA $0F,X / AND #$34
  nop
org $81AF3F
  jsl ConfAct            ; LDA $0F,X / BIT #$10 (the confused action checks it again)
  nop
org $81AF64
  jsl ConfSide           ; LDA #2 / JSL random / LSR / TDC / ROR: berserk leans towards its foes
  nop
  nop
  nop
  nop
  nop
org $85D1BC
  jsl ElemHit            ; LDA $7FF4D0: after the target's "when hit" script, before the damage formula
org $85D2C5
  jsl DmgMod             ; STA $7FF462: the HP change after the formula
org $85D18E
  jsl DefMod             ; STA $7FF4CC: the target's DFP (amnesia: +50%)
org $85D0CD
  jsl TargetStart        ; LDA $09F4 / STA $0A2D: each target starts with the action's chances
  nop
  nop
org $85D454
  jsl ApplyNew           ; EOR $0F,X / STA $0F,X / BIT #$04 / BEQ: then the new statuses
  beq $85D461
  nop
  nop
  nop
  nop
org $85CE43
  jsl ActEnd             ; LDA $7FF4E2: the action's chances end with it
org $81A82C
  jsl DoActMsg           ; JSL DoAction: then the messages of what it caused
org $818943
  jsl RoundEnd           ; JSL $859099: the round's ticks first
org $81C6BB
  jsl PoisonZ            ; (16-bit) LDA $11,X / SEC / SBC $54: poison heals a zombie
  nop
  nop
org $8593CC
  jsl BOver              ; LDA $0F,X / AND #$04: stone heroes count as fallen
  nop
org $8593E1
  jsl BOver
  nop
org $81884A
  jsl BattleStart        ; LDA #$FF / STA $0A13
  nop
org $818859
  jsl BattleEnd          ; JSL $85EDBB
org $8090D3
  jsl SaveHook           ; JSL $8EBA0D (the field state into the save buffer)
org $8090B1
  jsl LoadHook           ; JSL $8EB993
org $85D508
  jsl CurseIP            ; (16-bit) LDA $7FF44E: no IP for a cursed hero
org $82B32A
  jsl StatusLoop         ; LDA #0 / BRA (the STATUS screen's cursor loop: it is open); on to LDA #1 / BRA, as after NEXT
org $82B332
  jsl StatusExit         ; JSL $868B32 (leaving the STATUS screen)
org $828B3C
  jsl MenuFrameY         ; JSL $868B55 in the cursor's wait loop: Y on the STATUS screen steps the resistance pages
org $82B31A
  jsl StatusKey          ; another button on the STATUS screen: back to the game's page
  cmp #$02
  beq $82B33D
  cmp #$03
  beq $82B332
  cmp #$06
  bcs $82B35E
org $818877
  jsl RoundStart         ; JSL $85ECF0 (a round starts): a monster new to the battle gets its starting statuses

; ================================================================ the added code
org HOOK

; ---- Ent: X = a fighter's block (16-bit A, X, DB $7E). A = its new-status word, ENT = its entry * 2, carry set; else
; A = 0, ENT = $FFFF, carry clear. Keeps X, Y.
Ent:
  phy
  txa
  beq Ent_none
  sec
  sbc #$0BAD
  bcc Ent_mon
  cmp #$0532
  bcs Ent_mon
  ldy #$0000
Ent_div:
  cmp #$00BE
  bcc Ent_dd
  sbc #$00BE
  iny
  iny
  bra Ent_div
Ent_dd:
  cmp #$0000
  bne Ent_mon
  tya
  bra Ent_found
Ent_mon:
  ldy #$000A
Ent_ml:
  txa
  cmp $0A6E,y
  beq Ent_mf
  dey
  dey
  bpl Ent_ml
  cpx $0A6C
  bne Ent_none
  lda #$001A
  bra Ent_found
Ent_mf:
  tya
  clc
  adc #$000E
Ent_found:
  sta ENT
  phx
  tax
  lda NS,x
  plx
  ply
  sec
  rtl
Ent_none:
  lda #$FFFF
  sta ENT
  lda #$0000
  ply
  clc
  rtl

; ---- BlkOf: A = entry * 2 (16-bit, DB $7E) -> X = its block (0 = none).
BlkOf:
  cmp #$000E
  bcs BlkOf_m
  lsr a
  tax
  lda #$0BAD
BlkOf_h:
  cpx #$0000
  beq BlkOf_hd
  clc
  adc #$00BE
  dex
  bra BlkOf_h
BlkOf_hd:
  tax
  rtl
BlkOf_m:
  cmp #$001A
  beq BlkOf_c
  sec
  sbc #$000E
  tax
  lda $0A6E,x
  tax
  rtl
BlkOf_c:
  ldx $0A6C
  rtl

; ---- InitTables: the first time (no marker) the RAM is cleared.
InitTables:
  php
  rep #$30
  lda MAGIC
  cmp #$464C
  beq IT_o
  ldx #$01FE
  lda #$0000
IT_l:
  sta $7FFC00,x
  dex
  dex
  bpl IT_l
  lda #$464C
  sta MAGIC
IT_o:
  plp
  rtl

; ---- Roll: TMP3 = a chance (0-100); carry set when it happens. Any width; keeps X, Y.
Roll:
  php
  sep #$20
  lda #$64
  jsl $808299
  rep #$20
  and #$00FF
  cmp TMP3
  bcs Roll_no
  plp
  sec
  rtl
Roll_no:
  plp
  clc
  rtl

; ---- RandA: A (16-bit) = n -> a random 0 to n-1 (n up to 255). Keeps X, Y.
RandA:
  php
  sep #$20
  jsl $808299
  rep #$20
  and #$00FF
  plp
  rtl

; ---- ShowMsg: X = block, Y = text offset (from Texts): "<name><text>" for 45 frames, as the game's status messages.
ShowMsg:
  php
  phb
  sep #$20
  rep #$10
  pea $7E7E
  plb
  plb
  phy
  jsl $859150            ; the name to $1269, $1266 = its length
  jsl TrimName
  ply
  tyx
  ldy $1266
SM_cp:
  lda Texts,x
  sta $1269,y
  beq SM_done
  inx
  iny
  bra SM_cp
SM_done:
  sty $1266
  jsl ShowText
  plb
  plp
  rtl

; ---- TrimName: a monster's name ends in spaces: they go ($1266 = the length without them). 8-bit A, DB $7E.
TrimName:
  ldy $1266
TN_l:
  cpy #$0000
  beq TN_d
  lda $1268,y
  cmp #$20
  bne TN_d
  lda #$00
  sta $1268,y
  dey
  bra TN_l
TN_d:
  sty $1266
  rtl

; ---- ShowText: shows the battle text $1269 for 45 frames (the game's $85:903F).
ShowText:
  php
  phb
  sep #$20
  rep #$10
  pea $8585
  plb
  plb
  phx
  jsl $859AAA
  ldx #$FFFF
  stx $1264
  jsl $8595FE
  jsl $8591A1
  jsl $81BAE8
  rep #$20
  jsl $859BDA
  sep #$20
  jsl $858A2F
  lda #$FF
  sta $0012F3
  jsl $85EC81
  lda #$2D
ST_l:
  pha
  jsl $858A2F
  lda #$FF
  sta $0012F3
  jsl $85EC81
  pla
  dec a
  bne ST_l
  jsl $859671
  jsl $858A2F
  lda #$FF
  sta $0012F3
  jsl $85EC81
  jsl $859ABC
  plx
  plb
  plp
  rtl

; ---- the hook prologue every hook below uses: PHP / PHB / DB $7E / REP #$30; epilogue PLB / PLP.

; ================================================================ battle script commands 5D-60
OpCheck:                 ; 8-bit A, DB as the runner has it
  lda [$BB]
  cmp #$5D
  bcs OC_mine
  rep #$20
  asl a
  tax
  rtl
OC_mine:
  rep #$20
  pla                    ; the return address: the command is ours
  sep #$20
  pla
  lda [$BB]
  pha
  rep #$20
  inc $BB
  sep #$20
  pla
  cmp #$5D
  beq Op5D
  cmp #$5E
  beq Op5E
  cmp #$5F
  bne OC_n5f
  brl Op5F
OC_n5f:
  cmp #$60
  bne OC_bad
  brl Op60
OC_bad:
  jml $85B473            ; an unknown command ends the script (as 00)

ArgB:                    ; the next byte of the script
  lda [$BB]
  pha
  rep #$20
  inc $BB
  sep #$20
  pla
  rtl

Op5D:
  jsl ArgB
  and #$0F
  sta TMPS
  jsl ArgB
  sta TMP4
  rep #$30
  lda TMPS
  and #$000F
  tax
  sep #$20
  lda TMP4
  pha
  lda INTGT
  bne O5D_t
  pla
  sta INF,x
  jml $85B45E
O5D_t:
  pla
  sta TINF,x
  jml $85B45E

Op5E:                    ; s = $10: every new status
  jsl ArgB
  sta TMPS
  jsl ArgB
  sta TMP4
  rep #$30
  lda TMPS
  and #$00FF
  cmp #$0010
  bcc O5E_one
  lda #$000F
O5E_all:
  sta ALLK
  jsl O5E_set
  lda ALLK
  dec a
  bpl O5E_all
  jml $85B45E
O5E_one:
  jsl O5E_set
  jml $85B45E

O5E_set:                 ; A (16-bit) = the status: its cure chance (the action's, or this target's) = TMP4
  tax
  sep #$20
  lda TMP4
  pha
  lda INTGT
  bne O5E_t
  pla
  sta CURE,x
  rep #$30
  rtl
O5E_t:
  pla
  sta TCURE,x
  rep #$30
  rtl

Op5F:                    ; chance = chance * (100 - p) / 100; s = $10: every new status
  jsl ArgB
  sta TMPS
  jsl ArgB
  cmp #$64
  bcc O5F_p
  lda #$64
O5F_p:
  sta TMP4
  lda #$64
  sec
  sbc TMP4
  sta TMP4               ; 100 - p
  rep #$30
  lda TMPS
  and #$00FF
  cmp #$0010
  bcc O5F_one
  lda #$000F
O5F_all:
  sta ALLK
  jsl O5F_cut
  lda ALLK
  dec a
  bpl O5F_all
  jml $85B45E
O5F_one:
  jsl O5F_cut
  jml $85B45E

O5F_cut:                 ; A (16-bit) = the status: its chance (the action's, or this target's) x TMP4 / 100
  tax
  lda INTGT
  and #$00FF
  beq O5F_g
  txa
  clc
  adc #$0020             ; TINF = INF + $20
  tax
O5F_g:
  sep #$20
  lda INF,x
  sta $004202
  lda TMP4
  sta $004203
  nop
  nop
  nop
  nop
  rep #$20
  lda $004216
  sta $004204
  sep #$20
  lda #$64
  sta $004206
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  lda $004214
  sta INF,x
  rep #$30
  rtl

Op60:                    ; s = $10: every new status
  jsl ArgB
  rep #$30
  and #$00FF
  cmp #$0010
  bcc O60_one
  lda #$FFFF
  bra O60_set
O60_one:
  asl a
  tax
  lda BitTab,x
O60_set:
  ora WARDACT
  sta WARDACT
  sep #$20
  jml $85B45E

; ================================================================ turns
TurnCheck:               ; X = the fighter (8-bit A, DB $97)
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  lda #$0000
  sta XTRA
  lda $000F,x
  and #$002C
  bne TC_out
  jsl Ent
  bcc TC_out
  sta TMP0
  bit #$0800
  bne TC_stone
  bit #$0002
  bne TC_frozen
  bit #$4000
  bne TC_stop
  bit #$0020
  beq TC_act
  lda #$0004
  jsl RandA
  cmp #$0000
  bne TC_act
  ldy #TSpook-Texts
  bra TC_skip
TC_stone:
  ldy #TStone-Texts
  bra TC_skip
TC_frozen:
  ldy #TFroz-Texts
  bra TC_skip
TC_stop:
  ldy #TStop-Texts
TC_skip:
  phx
  jsl ShowMsg
  plx
  lda #$0008
  sta XTRA
  bra TC_out
TC_act:
  phx
  lda ENT
  tax
  lda #$0001
  sta ACTED,x
  plx
  lda TMP0
  and #$0010
  sta XTRA
TC_out:
  plb
  plp
  lda $000F,x
  ora XTRA
  bit #$2C
  rtl

BerXtra:                 ; X = block: XTRA = $10 when berserk and able to act, else 0
  jsl Ent
  sta TMP0
  and #$4802
  bne BX_no
  lda TMP0
  and #$0010
  sta XTRA
  rtl
BX_no:
  lda #$0000
  sta XTRA
  rtl

CmdCheck:                ; Y = the hero (16-bit A, DB $97)
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  phx
  tyx
  jsl Ent
  plx
  and #$4812
  beq CC_n
  lda #$0008
CC_n:
  sta XTRA
  plb
  plp
  lda $000F,y
  ora XTRA
  bit #$003C
  rtl

QConf:                   ; Y = the hero (16-bit A)
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  phx
  tyx
  jsl BerXtra
  plx
  plb
  plp
  lda $000F,y
  ora XTRA
  sep #$20
  rtl

AllDis:                  ; X = the hero (8-bit A)
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl Ent
  and #$4812
  beq AD_n
  lda #$0020
AD_n:
  sta XTRA
  plb
  plp
  lda $000F,x
  ora XTRA
  and #$34
  rtl

ConfAct:                 ; X = the attacker (8-bit A)
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl BerXtra
  plb
  plp
  lda $000F,x
  ora XTRA
  bit #$10
  rtl

ConfSide:                ; X = the attacker (8-bit A): A = $00 heroes' side, $80 monsters' side
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl Ent
  and #$0010
  beq CS_norm
  lda #$0064
  jsl RandA
  cmp #$0046             ; 70%: a foe
  sep #$20
  lda $7FF44E
  bcs CS_own
  eor #$80
CS_own:
  and #$80
  bra CS_ret
CS_norm:
  sep #$20
  lda #$02
  jsl $808299
  lsr a
  lda #$00
  ror a
CS_ret:
  sta XTRA
  plb
  plp
  rep #$20
  lda XTRA
  and #$00FF
  sep #$20
  rtl

; ================================================================ damage
DmgMod:                  ; 16-bit A = the HP change (minus = damage); the target block is at 4,S (after the JSL)
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  sta AMT
  lda $06,s
  tax
  jsl Ent
  sta TMP1               ; the target's word
  lda ENT
  sta TMPK
  lda #$0000
  sta TMP2               ; the attacker's word
  lda #$FFFF
  sta TMP3
  sep #$20
  lda $0A13
  beq DM_noatk
  lda $7FF44E
  jsl $81B2B5
  rep #$30
  jsl Ent
  sta TMP2
  lda ENT
  sta TMP3               ; the attacker's entry
DM_noatk:
  rep #$30
  lda TMP2
  bit #$0080
  beq DM_np
  lda $7FF468            ; piety: the MP it restores +25% (register 2D, before the spread)
  beq DM_np
  bmi DM_np
  lsr a
  lsr a
  clc
  adc $7FF468
  sta $7FF468
DM_np:
  lda AMT
  bmi DM_dmg
  bne DM_heal
  brl DM_out
DM_heal:
  lda TMP2
  bit #$0080
  beq DM_h0
  lda AMT                ; piety: the HP it restores +25%
  lsr a
  lsr a
  clc
  adc AMT
  sta AMT
DM_h0:
  lda TMP1
  bit #$0800
  bne DM_hzero
  bit #$0001
  beq DM_hcur
  lda AMT                ; zombie: healing hurts
  eor #$FFFF
  inc a
  sta AMT
  brl DM_out
DM_hcur:
  bit #$2000
  bne DM_hhalf
  brl DM_out
DM_hhalf:
  lda AMT
  lsr a
  sta AMT
  brl DM_out
DM_hzero:
  lda #$0000
  sta AMT
  brl DM_out
DM_dmg:
  eor #$FFFF
  inc a
  sta AMT                ; the damage, positive while it is worked on
  lda $7FF45A
  and #$0008
  sta TMP0               ; 0: physical
  bne DM_mag
  lda TMP2
  bit #$0010
  beq DM_a1
  lda AMT                ; berserk: ATP doubled
  asl a
  sta AMT
DM_a1:
  lda TMP2
  bit #$0008
  beq DM_a2
  lda AMT                ; burnt: ATP halved
  lsr a
  sta AMT
DM_a2:
  lda TMP2
  bit #$0400
  beq DM_tgt
  lda #$0002             ; blind: half the hits miss
  jsl RandA
  cmp #$0000
  bne DM_tgt
  lda #$0000
  sta AMT
  lda TMP3
  bmi DM_tgt
  tax
  lda EXTRA,x
  ora #$0008
  sta EXTRA,x
  bra DM_tgt
DM_mag:
  lda TMP2
  bit #$0040
  beq DM_tgt
  lda AMT                ; amnesia: magic -25%
  lsr a
  lsr a
  sta TMP4
  lda AMT
  sec
  sbc TMP4
  sta AMT
DM_tgt:
  lda TMP2
  bit #$0080
  beq DM_tz
  lda $7FF45C
  bit #$0010
  beq DM_tz
  lda AMT                ; piety: its light attacks +25%
  lsr a
  lsr a
  clc
  adc AMT
  sta AMT
DM_tz:
  lda TMP1
  bit #$0001
  beq DM_t1
  lda $7FF45C
  bit #$0010
  beq DM_t1
  lda AMT                ; zombie hit by light: x3
  asl a
  clc
  adc AMT
  sta AMT
DM_t1:
  lda TMP1
  bit #$0080
  beq DM_t2
  lda $7FF45C
  bit #$0020
  beq DM_t2
  lda AMT                ; piety hit by dark: x1.5
  lsr a
  clc
  adc AMT
  sta AMT
DM_t2:
  lda TMP1
  bit #$0002
  beq DM_t3
  lda TMP0
  bne DM_melt
  lda AMT                ; frozen, physical: x2.5 and the ice shatters
  lsr a
  sta TMP4
  lda AMT
  asl a
  clc
  adc TMP4
  sta AMT
  lda #$0001
  bra DM_unf
DM_melt:
  lda $7FF45C
  bit #$0002
  beq DM_t3
  lda #$0002             ; fire melts it
DM_unf:
  sta TMP4
  lda TMPK
  tax
  lda EXTRA,x
  ora TMP4
  sta EXTRA,x
  lda NS,x
  and #$FFFD
  sta NS,x
  sta TMP1
DM_t3:
  lda TMP1
  bit #$0008
  beq DM_t4
  lda TMP0
  beq DM_t4
  lda $7FF45C
  bit #$0404
  beq DM_t4
  lda TMPK               ; burnt, hit by water or ice magic: cured
  tax
  lda EXTRA,x
  ora #$0004
  sta EXTRA,x
  lda NS,x
  and #$FFF7
  sta NS,x
  sta TMP1
DM_t4:
  lda TMP1
  bit #$0800
  beq DM_cap
  lda #$0000             ; stone: nothing gets through
  sta AMT
DM_cap:
  lda AMT
  cmp #$270F
  bcc DM_neg
  lda #$270F
DM_neg:
  eor #$FFFF
  inc a
  sta AMT
DM_out:
  lda AMT
  plb
  plp
  sta $7FF462
  rtl

DefMod:                  ; 16-bit A = the target's DFP, X = the target
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  sta AMT
  jsl Ent
  and #$0040
  beq DF_o
  lda AMT                ; amnesia: DFP +50%
  lsr a
  clc
  adc AMT
  sta AMT
DF_o:
  lda AMT
  plb
  plp
  sta $7FF4CC
  rtl

CurseIP:                 ; X = the hero hurt: no IP gain when cursed
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl Ent
  and #$2000
  sta XTRA
  plb
  plp
  lda XTRA
  bne CI_y
  lda $7FF44E
  rtl
CI_y:
  lda #$0000
  rtl

; ================================================================ causing and curing
TargetStart:             ; each target starts with the action's chances
  php
  rep #$30
  phx
  ldx #$001E
TS_l:
  lda INF,x
  sta TINF,x
  dex
  dex
  bpl TS_l
  lda #$0001
  sta INTGT
  plx
  plp
  lda $09F4
  sta $0A2D
  rtl

ApplyNew:                ; X = the target, 8-bit A = the game's new status (as EOR / STA)
  eor $000F,x
  sta $000F,x
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  lda #$0000
  sta INTGT
  lda $000F,x
  and #$0004
  beq AN_alive
  brl AN_out
AN_alive:
  jsl Ent
  bcs AN_found
  brl AN_out
AN_found:
  sta TMP1
  phx
  ldy #$0000
AN_loop:
  tyx
  lda TCURE,x
  and #$00FF
  beq AN_inf
  sta TMP3
  txa
  asl a
  tax
  lda BitTab,x
  sta TMP2
  and TMP1
  beq AN_inf0
  jsl Roll
  bcc AN_inf0
  lda TMP1
  eor TMP2
  sta TMP1
  lda ENT
  tax
  lda CURED,x
  ora TMP2
  sta CURED,x
AN_inf0:
  tyx
AN_inf:
  lda TINF,x
  and #$00FF
  bne AN_hasinf
  brl AN_next
AN_hasinf:
  sta TMP3
  txa
  asl a
  tax
  lda BitTab,x
  sta TMP2
  and TMP1
  bne AN_next
  lda ENT
  tax
  lda WARD,x
  and TMP2
  bne AN_next
  jsl Roll
  bcc AN_next
  lda TMP1
  ora TMP2
  sta TMP1
  lda CAUSED,x
  ora TMP2
  sta CAUSED,x
  cpy #$000B
  bne AN_t0
  cpx #$000E             ; stone on a monster: its EXP and gold
  bcc AN_t0
  cpx #$001A
  bcs AN_t0
  lda $01,s
  phx
  tax
  jsl Reward
  plx
AN_t0:
  cpy #$0004
  bne AN_t1
  lda #$0008             ; berserk: 3-10 rounds
  jsl RandA
  clc
  adc #$0003
  sta TBERS,x
AN_t1:
  cpy #$000E
  bne AN_t2
  lda #$0003             ; stop: 2-4 rounds
  jsl RandA
  clc
  adc #$0002
  sta TSTOP,x
AN_t2:
  cpy #$0009
  bne AN_next
  lda #$0003             ; doom: 3 rounds
  sta TDOOM,x
AN_next:
  iny
  cpy #$0010
  beq AN_done
  brl AN_loop
AN_done:
  lda ENT
  tax
  lda WARD,x
  ora WARDACT
  sta WARD,x
  lda TMP1
  sta NS,x
  plx
AN_out:
  plb
  plp
  lda $000F,x
  bit #$04
  rtl

ActEnd:                  ; the action's chances and wards end with it
  php
  rep #$30
  phx
  ldx #$003E
  lda #$0000
AE_l:
  sta INF,x
  dex
  dex
  bpl AE_l
  sta WARDACT
  sta INTGT
  plx
  plp
  lda $7FF4E2
  rtl

; ================================================================ messages after an action
DoActMsg:                ; while the action runs, stone doesn't end the battle (its message comes first)
  php
  rep #$20
  lda #$0001
  sta INACT
  plp
  jsl $81A832
  php
  rep #$20
  lda #$0000
  sta INACT
  plp
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl ShowPending
  plb
  plp
  rtl

ShowPending:             ; for every entry: what was caused, cured, shattered...
  lda #$0000
  sta TMPK
SP_k:
  lda TMPK
  tax
  lda CAUSED,x
  ora CURED,x
  ora EXTRA,x
  beq SP_next
  lda TMPK
  jsl BlkOf
  cpx #$0000
  beq SP_clr
  txa
  sta BLK
  lda $000F,x
  and #$0004
  bne SP_clr
  lda TMPK
  tax
  lda EXTRA,x
  sta TMP1
  lda #$0002
  sta TMP0
  jsl MsgBits
  lda TMPK
  tax
  lda CURED,x
  sta TMP1
  lda #$0001
  sta TMP0
  jsl MsgBits
  lda TMPK
  tax
  lda CAUSED,x
  sta TMP1
  lda #$0000
  sta TMP0
  jsl MsgBits
SP_clr:
  lda TMPK
  tax
  lda #$0000
  sta CAUSED,x
  sta CURED,x
  sta EXTRA,x
SP_next:
  lda TMPK
  inc a
  inc a
  sta TMPK
  cmp #$001C
  beq SP_done
  brl SP_k
SP_done:
  rtl

MsgBits:                 ; TMP1 = bits, TMP0 = 0 caused / 1 cured / 2 extra; BLK = the fighter
  ldx #$0000
MB_l:
  lda TMP1
  beq MB_end
  lsr a
  sta TMP1
  bcc MB_n
  phx
  lda TMP0
  beq MB_c
  cmp #$0001
  beq MB_u
  lda ExtraTab,x
  bra MB_go
MB_c:
  lda CauseTab,x
  bra MB_go
MB_u:
  lda CureTab,x
MB_go:
  tay
  lda BLK
  tax
  jsl ShowMsg
  plx
MB_n:
  inx
  inx
  bra MB_l
MB_end:
  rtl

; ================================================================ the end of a round
RoundEnd:
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl InitTables
  jsl DoomCount
  lda #$0000
  jsl TickPass           ; burns
  lda #$0001
  jsl TickPass           ; bleeding
  lda #$0002
  jsl TickPass           ; regen
  lda #$0003
  jsl TickPass           ; mana leak
  lda #$0004
  jsl TickPass           ; doom
  jsl Decays
  jsl Timers
  ldx #$001A
  lda #$0000
RE_l:
  sta ACTED,x
  dex
  dex
  bpl RE_l
  plb
  plp
  jsl $859099
  rtl

; For each fighter slot (heroes 0-4 at $0A64, monsters 5-10 at $0A6E) alive: X = block, Ent loaded. Used as a loop.
; TickPass: A = the pass (0 burns, 1 bleeding, 2 regen, 3 mana leak, 4 doom). Writes the result records and shows
; the numbers as the game does after poison.
TickPass:
  sta PASS
  ldx #$0148
  lda #$0000
TP_c:
  sta $7FF60C,x
  dex
  dex
  bpl TP_c
  sta CNT
  ldy #$0000
TP_s:
  lda $0A64,y
  bne TP_have
  brl TP_n
TP_have:
  tax
  lda $000F,x
  and #$0004
  beq TP_live
  brl TP_n
TP_live:
  tya
  sta SLOT
  txa
  sta BLK
  jsl Ent
  bcs TP_ent
  brl TP_n2
TP_ent:
  sta TMP1
  lda #$0000
  sta TMP2               ; 0: HP, 1: MP
  lda PASS
  bne TP_p1
  lda TMP1               ; burns: HP / 16 (as poison, about)
  bit #$0008
  bne TP_b0
  brl TP_n2
TP_b0:
  lda $0011,x
  lsr a
  lsr a
  lsr a
  lsr a
  bne TP_b1
  inc a
TP_b1:
  eor #$FFFF
  inc a
  brl TP_hp
TP_p1:
  cmp #$0001
  bne TP_p2
  lda TMP1               ; bleeding when it acted: max HP / 16
  bit #$1000
  bne TP_bl0
  brl TP_n2
TP_bl0:
  lda ENT
  tax
  lda ACTED,x
  sta TMP3
  lda BLK
  tax
  lda TMP3
  bne TP_bl1
  brl TP_n2
TP_bl1:
  lda $0025,x
  lsr a
  lsr a
  lsr a
  lsr a
  bne TP_bl2
  inc a
TP_bl2:
  eor #$FFFF
  inc a
  brl TP_hp
TP_p2:
  cmp #$0002
  bne TP_p3
  lda TMP1               ; regen: max HP / 12, up to the max
  bit #$0100
  bne TP_r0
  brl TP_n2
TP_r0:
  lda $0025,x
  sec
  sbc $0011,x
  beq TP_r00
  bcs TP_r1
TP_r00:
  brl TP_n2              ; at (or past) the max
TP_r1:
  sta TMP3
  lda $0025,x
  sta $004204
  sep #$20
  lda #$0C
  sta $004206
  rep #$20
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  lda $004214
  bne TP_r2
  inc a
TP_r2:
  cmp TMP3
  bcc TP_r3
  lda TMP3
TP_r3:
  brl TP_hp
TP_p3:
  cmp #$0003
  bne TP_p4
  lda TMP1               ; mana leak: max MP / 12, down to 0
  bit #$8000
  bne TP_m0
  brl TP_n2
TP_m0:
  lda $0013,x
  bne TP_m1
  brl TP_n2
TP_m1:
  sta TMP3
  lda $0027,x
  sta $004204
  sep #$20
  lda #$0C
  sta $004206
  rep #$20
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  lda $004214
  bne TP_m2
  inc a
TP_m2:
  cmp TMP3
  bcc TP_m3
  lda TMP3
TP_m3:
  sta TMP4
  lda $0013,x
  sec
  sbc TMP4
  sta $0013,x
  lda TMP4
  eor #$FFFF
  inc a
  sta AMT
  lda #$0001
  sta TMP2
  brl TP_rec
TP_p4:
  lda TMP1               ; doom at 0: all its HP
  bit #$0200
  bne TP_d0
  brl TP_n2
TP_d0:
  lda ENT
  tax
  lda TDOOM,x
  sta TMP3
  lda BLK
  tax
  lda TMP3
  beq TP_d1
  brl TP_n2
TP_d1:
  lda ENT
  tax
  lda NS,x
  and #$FDFF
  sta NS,x
  lda BLK
  tax
  lda $0011,x
  eor #$FFFF
  inc a
TP_hp:                   ; A = the HP change (minus = damage)
  sta AMT
  clc
  adc $0011,x
  beq TP_kill
  bmi TP_kill
  sta $0011,x
  brl TP_rec
TP_kill:
  lda $0011,x
  eor #$FFFF
  inc a
  sta AMT
  jsl Kill
TP_rec:
  lda SLOT
  lsr a
  tax
  lda SlotId,x
  sep #$20
  jsl $85D9C9            ; X = its result record (bank $7F)
  lda #$80
  sta $7F0000,x
  lda TMP2
  bne TP_rmp
  lda #$04
  sta $7F0002,x
  rep #$20
  lda AMT
  sta $7F0004,x
  bra TP_rd
TP_rmp:
  lda #$06
  sta $7F0002,x
  rep #$20
  lda AMT
  sta $7F0006,x
TP_rd:
  rep #$30
  lda CNT
  inc a
  sta CNT
TP_n2:
  lda SLOT
  tay
TP_n:
  iny
  iny
  cpy #$0016
  beq TP_shown
  brl TP_s
TP_shown:
  lda CNT
  bne TP_show
  rtl
TP_show:
  lda PASS
  asl a
  tax
  lda PassTab,x
  tax
  ldy #$0000
  sep #$20
TP_cp:
  lda Texts,x
  sta $1269,y
  beq TP_cpd
  inx
  iny
  bra TP_cp
TP_cpd:
  rep #$20
  lda #$0001
  sta $1266
  lda #$FFFF
  sta $1264
  sep #$20
  pea $8181
  plb
  plb
  jsl $8595FE
  lda #$00
  jsl $81C35F
  lda #$DC
  jsl $81895E
  jsl $859671
  jsl $85EC81
  pea $7E7E
  plb
  plb
  rep #$30
  rtl

Kill:                    ; X = the fighter whose HP ran out (as poison does it)
  stz $0011,x
  lda #$0004
  sta $000F,x
  sep #$20
  inc $00BA,x
  bne KL_m
  dec $00BA,x
KL_m:
  rep #$20
  lda SLOT
  cmp #$000A
  bcc KL_h
  jsl Reward
KL_h:
  rep #$30
  lda BLK
  tax
  lda ENT
  tax
  lda NS,x
  and #$040D
  sta NS,x
  lda BLK
  tax
  rtl

Reward:                  ; X = a monster: its EXP and gold go to the battle's totals (as when it falls)
  php
  sep #$20
  lda $0050,x
  sta $09FA
  pea $8181
  plb
  plb
  jsl $81FBDB
  pea $7E7E
  plb
  plb
  rep #$20
  lda $09FC
  clc
  adc $1605
  sta $1605
  bcc RW_g
  inc $1607
RW_g:
  lda $09FE
  clc
  adc $1608
  sta $1608
  bcc RW_o
  inc $160A
RW_o:
  plp
  rtl

DoomCount:               ; doom counts down; at 0 the doom pass takes the fighter
  ldy #$0000
DC_s:
  lda $0A64,y
  beq DC_n
  tax
  lda $000F,x
  and #$0004
  bne DC_n
  jsl Ent
  bcc DC_n
  and #$0200
  beq DC_n
  phx
  lda ENT
  tax
  lda TDOOM,x
  beq DC_z
  dec a
  sta TDOOM,x
DC_z:
  plx
  cmp #$0000
  beq DC_n
  clc
  adc #$0030             ; the digit
  sta TMP4
  phy
  phx
  ldx #$0000
DC_cp:
  lda TDoomC,x           ; "'s doom: " (9 letters)
  sta DOOMT,x
  inx
  inx
  cpx #$000A
  bne DC_cp
  lda TMP4
  and #$00FF
  sta DOOMT+9
  plx
  jsl ShowMsgRam
  ply
DC_n:
  iny
  iny
  cpy #$0016
  bne DC_s
  rtl

; ShowMsgRam: as ShowMsg, but the text is DoomTxt in RAM (its digit changes)
ShowMsgRam:
  php
  phb
  sep #$20
  rep #$10
  pea $7E7E
  plb
  plb
  jsl $859150
  jsl TrimName
  ldy $1266
  ldx #$0000
SR_cp:
  lda DOOMT,x
  sta $1269,y
  beq SR_d
  inx
  iny
  bra SR_cp
SR_d:
  sty $1266
  jsl ShowText
  plb
  plp
  rtl

Decays:                  ; decay: a random stat loses 1-15% of itself (its battle bonus)
  ldy #$0000
DE_s:
  lda $0A64,y
  bne DE_h
  brl DE_n
DE_h:
  tax
  lda $000F,x
  and #$0004
  beq DE_l
  brl DE_n
DE_l:
  jsl Ent
  bcs DE_e
  brl DE_n
DE_e:
  and #$0004
  bne DE_go
  brl DE_n
DE_go:
  txa
  sta BLK
  phy
  lda #$0007
  jsl RandA
  asl a
  sta TMPK               ; which stat * 2
  tax
  lda StatOff,x
  clc
  adc BLK
  tax
  lda $0000,x            ; the stat
  sta TMP3
  lda #$000F
  jsl RandA
  inc a                  ; 1-15
  sta TMP4
  ; TMP3 * TMP4 / 100 (TMP3 up to 999: shifts and adds)
  lda #$0000
  sta AMT
DE_mul:
  lda TMP4
  beq DE_md
  lsr a
  sta TMP4
  bcc DE_ms
  lda AMT
  clc
  adc TMP3
  sta AMT
DE_ms:
  lda TMP3
  asl a
  sta TMP3
  bra DE_mul
DE_md:
  lda AMT
  sta $004204
  sep #$20
  lda #$64
  sta $004206
  rep #$20
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  lda $004214
  bne DE_a
  inc a
DE_a:
  sta AMT
  txa
  clc
  adc #$000E             ; its battle bonus
  tax
  lda $0000,x
  sec
  sbc AMT
  sta $0000,x
  lda TMPK
  tax
  lda DecayTab,x
  tay
  lda BLK
  tax
  jsl ShowMsg
  ply
DE_n:
  iny
  iny
  cpy #$0016
  beq DE_end
  brl DE_s
DE_end:
  rtl

Timers:                  ; berserk and stop run out; spooked calms down 1 in 8
  ldy #$0000
TM_s:
  lda $0A64,y
  bne TM_h
  brl TM_n
TM_h:
  tax
  lda $000F,x
  and #$0004
  beq TM_l
  brl TM_n
TM_l:
  jsl Ent
  bcs TM_e
  brl TM_n
TM_e:
  sta TMP1
  txa
  sta BLK
  phy
  lda ENT
  tax
  lda TMP1
  bit #$0010
  beq TM_1
  lda TBERS,x
  dec a
  sta TBERS,x
  bne TM_1
  lda NS,x
  and #$FFEF
  sta NS,x
  phx
  ldy #UBers-Texts
  lda BLK
  tax
  jsl ShowMsg
  plx
TM_1:
  lda TMP1
  bit #$4000
  beq TM_2
  lda TSTOP,x
  dec a
  sta TSTOP,x
  bne TM_2
  lda NS,x
  and #$BFFF
  sta NS,x
  phx
  ldy #UStop-Texts
  lda BLK
  tax
  jsl ShowMsg
  plx
TM_2:
  lda TMP1
  bit #$0020
  beq TM_3
  lda #$0008
  jsl RandA
  cmp #$0000
  bne TM_3
  lda NS,x
  and #$FFDF
  sta NS,x
  phx
  ldy #USpook-Texts
  lda BLK
  tax
  jsl ShowMsg
  plx
TM_3:
  ply
TM_n:
  iny
  iny
  cpy #$0016
  beq TM_end
  brl TM_s
TM_end:
  rtl

; ================================================================ poison, battle over, battle start and end, save
PoisonZ:                 ; 16-bit A, X = the fighter, $54 = the poison damage
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl Ent
  and #$0001
  bne PZ_z
  plb
  plp
  lda $0011,x
  sec
  sbc $54
  rtl
PZ_z:
  lda $0011,x            ; a zombie: poison heals
  clc
  adc $54
  cmp $0025,x
  bcc PZ_ok
  lda $0025,x
PZ_ok:
  sta $0011,x
  lda $54
  eor #$FFFF
  inc a
  sta $54
  plb
  plp
  lda #$C715             ; back to $81:C716 (the result record)
  sta $01,s
  sep #$20
  rtl

BOver:                   ; X = a fighter (8-bit A): stone counts as fallen (after the action that caused it)
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  lda #$0000
  sta XTRA
  lda INACT
  bne BO_i
  jsl Ent
  and #$0800
  beq BO_n
  lda #$0004
BO_n:
  sta XTRA
BO_i:
  plb
  plp
  lda $000F,x
  ora XTRA
  and #$04
  rtl

BattleStart:
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl InitTables
  ldx #$001A
BS_l:
  lda #$0000
  sta TBERS,x
  sta TSTOP,x
  sta TDOOM,x
  sta ACTED,x
  sta WARD,x
  sta CAUSED,x
  sta CURED,x
  sta EXTRA,x
  cpx #$000E
  bcc BS_h
  sta NS,x
  bra BS_n
BS_h:
  lda NS,x
  and #$040D
  sta NS,x
BS_n:
  dex
  dex
  bpl BS_l
  ldx #$003E
  lda #$0000
BS_c:
  sta INF,x
  dex
  dex
  bpl BS_c
  ldx #$000A
BS_m:
  sta MONID,x
  dex
  dex
  bpl BS_m
  sta WARDACT
  sta INTGT
  plb
  plp
  lda #$FF
  sta $7E0A13
  rtl

BattleEnd:
  jsl $85EDBB
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  ldx #$001A
BE_l:
  cpx #$000E
  bcc BE_h
  lda #$0000
  sta NS,x
  bra BE_n
BE_h:
  lda NS,x
  and #$040D
  sta NS,x
BE_n:
  dex
  dex
  bpl BE_l
  plb
  plp
  rtl

SaveHook:                ; the 7 heroes' lasting statuses into the save buffer's unused $7E:3BBB-3BC3
  jsl $8EBA0D
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl InitTables
  lda #$464C
  sta $3BBB
  ldx #$0000
  ldy #$0000
SH_l:
  lda NS,x
  and #$000D
  sta TMP0
  lda NS,x
  and #$0400
  beq SH_b
  lda TMP0
  ora #$0002
  sta TMP0
SH_b:
  sep #$20
  lda TMP0
  sta $3BBD,y
  rep #$20
  inx
  inx
  iny
  cpy #$0007
  bne SH_l
  plb
  plp
  rtl

LoadHook:
  jsl $8EB993
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl InitTables
  ldx #$0000
  ldy #$0000
  lda $3BBB
  cmp #$464C
  bne LH_zero
LH_l:
  lda $3BBD,y
  and #$000F
  sta TMP0
  and #$000D
  sta TMP1
  lda TMP0
  and #$0002
  beq LH_b
  lda TMP1
  ora #$0400
  sta TMP1
LH_b:
  lda TMP1
  sta NS,x
  inx
  inx
  iny
  cpy #$0007
  bne LH_l
  bra LH_out
LH_zero:
  lda #$0000
LH_z:
  sta NS,x
  inx
  inx
  cpx #$000E
  bne LH_z
LH_out:
  plb
  plp
  rtl

; ---- RoundStart: a monster new in its slot (its id + 1 differs from MONID) gets the statuses MonStart gives its kind
RoundStart:
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  jsl InitTables
  ldy #$0000
RS_l:
  lda $0A6E,y
  beq RS_n
  tax
  lda $0050,x
  and #$00FF
  inc a
  tyx
  cmp MONID,x
  beq RS_n
  sta MONID,x
  dec a
  asl a
  tax
  lda MonStart,x
  sta TMP0
  tya
  clc
  adc #$000E
  tax
  lda NS,x
  ora TMP0
  sta NS,x
RS_n:
  iny
  iny
  cpy #$000C
  bne RS_l
  ldy #$0000
RS_h:
  lda $0A64,y
  beq RS_hn
  tax
  lda $000F,x
  and #$0004
  bne RS_hn
  jsl Ent
  bcc RS_hn
  phy
  bit #$0800
  bne RS_st
  bit #$0002
  bne RS_fr
  bit #$4000
  beq RS_hp
  ldy #TStop-Texts
  bra RS_msg
RS_st:
  ldy #TStone-Texts
  bra RS_msg
RS_fr:
  ldy #TFroz-Texts
RS_msg:
  jsl ShowMsg
RS_hp:
  ply
RS_hn:
  iny
  iny
  cpy #$000A
  bne RS_h
  plb
  plp
  jsl $85ECF0
  rtl

; ================================================================ the STATUS screen's resistance pages
; A = the cursor's result (2 next, 3 return, 6/7 left/right). Y (new press: $4B bit 6, held $47) steps the page:
; 0 the game's status page, 1 elements, 2 statuses. Any other button goes back to page 0 (the game draws it again).
StatusKey:                ; A = the cursor's result: anything but 0 brings the game's page back (it draws it)
  php
  sep #$20
  sta TMPS
  cmp #$08
  bcc SK_ok
  lda #$00
  sta TMPS
SK_ok:
  lda TMPS
  beq SK_out
  lda #$00
  sta RPAGE
SK_out:
  plp
  lda TMPS
  rtl

StatusLoop:               ; the STATUS screen's loop: Y works now
  php
  rep #$20
  lda #$5A5A
  sta STON
  plp
  rtl

StatusExit:
  php
  rep #$20
  lda #$0000
  sta STON
  sta RPAGE
  plp
  jsl $868B32
  rtl

; one menu frame of the cursor loop; on the STATUS screen a new Y press steps the page: 0 the game's, 1 elements,
; 2 statuses ($4B bit 6 pressed, $47 held; the game reads A, X, L, R and the d-pad but not Y)
MenuFrameY:
  jsl $868B55
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  lda STON
  cmp #$5A5A
  bne MY_out
  sep #$20
  lda $4B
  and #$40
  beq MY_out
  lda $47
  and #$40
  beq MY_out
  lda #$40
  trb $4B
  lda RPAGE
  inc a
  cmp #$03
  bcc MY_pg
  lda #$00
MY_pg:
  sta RPAGE
  beq MY_back
  jsl DrawResist
  bra MY_out
MY_back:
  rep #$30
  jsl ClearArea          ; the game's page draws over what is there
  jsl RedrawStatus
MY_out:
  plb
  plp
  rtl

; the game's DrawStatusScreen ($82:9E67, an RTS routine): its RTS lands on the RTL at $82:8027, which comes back here
RedrawStatus:
  php
  phb
  sep #$20
  rep #$10
  phk
  per RD_back-1
  pea $8026
  jml $829E67
RD_back:
  sep #$20
  lda #$88
  sta $0074
  plb
  plp
  rtl

; DrawResist: the hero of party slot $14B3: the union of its six worn items' records (ResTab), drawn over the stats
DrawResist:
  php
  phb
  pea $7E7E
  plb
  plb
  rep #$30
  lda #$0000
  ldx #$0008
DR_z:
  sta RSUM,x
  dex
  dex
  bpl DR_z
  lda $14B3
  and #$00FF
  asl a
  tax
  lda $0A80,x
  tax                    ; the hero's record
  ldy #$0066
DR_eq:
  phy
  phx
  txa
  clc
  adc $01,s
  sta TMP0
  ply
  phy
  tya
  clc
  adc $03,s
  tax
  lda $0000,x            ; the item worn there (0: none)
  and #$01FF
  beq DR_none
  sta TMP1
  asl a
  asl a
  asl a
  clc
  adc TMP1
  adc TMP1
  tax                    ; item * 10
  lda MonStart+$0200,x
  ora RSUM
  sta RSUM
  lda MonStart+$0202,x
  ora RSUM+2
  sta RSUM+2
  lda MonStart+$0204,x
  ora RSUM+4
  sta RSUM+4
  lda MonStart+$0206,x
  ora RSUM+6
  sta RSUM+6
  lda MonStart+$0208,x
  ora RSUM+8
  sta RSUM+8
DR_none:
  plx
  ply
  iny
  iny
  cpy #$0072
  bne DR_eq
  jsl ClearArea
  lda RPAGE
  and #$00FF
  cmp #$0001
  beq DR_el
  brl DR_st
DR_el:
  ; elements: "Fire   -42%" in two columns; each from ElemTab (element, name)
  ldy #TElTitle-Texts
  ldx #$31C2
  jsl DrawLine
  lda #$0000
  sta TMPK
DR_el1:
  lda TMPK
  asl a
  asl a
  tax
  lda ElemTab+2,x        ; its name's text
  sta TMP3
  lda ElemTab,x          ; the element (0-15 a bit, 16 plain)
  pha
  lda $14B3
  and #$00FF
  asl a
  tax
  lda $0A80,x
  tax                    ; the hero's block
  pla
  jsl EPct               ; its percent: the hero's own and its worn items' added up
  jsl NumText
  ; place: entry n at row 6 + (n/2)*2, column 2 or 17
  lda TMPK
  lsr a
  asl a
  asl a
  asl a
  asl a
  asl a
  asl a                  ; (n/2) * 64: a row
  clc
  adc #$3202
  sta TMP0
  lda TMPK
  and #$0001
  beq DR_e4
  lda TMP0
  clc
  adc #$001E
  sta TMP0
DR_e4:
  lda TMP3
  tay
  lda TMP0
  tax
  jsl DrawLine
  lda TMP0
  clc
  adc #$0010             ; the value 8 letters on
  tax
  jsl DrawRam
  lda TMPK
  inc a
  sta TMPK
  cmp #$0008
  beq DR_eld
  brl DR_el1
DR_eld:
  brl DR_up
DR_st:
  ; statuses: those guarded against, "Poison IMM" / "Sleep  1/2", two columns, up to 12
  ldy #TStTitle-Texts
  ldx #$31C2
  jsl DrawLine
  lda #$0000
  sta TMPK               ; which status (0-21)
  sta CNT                ; how many drawn
DR_s1:
  lda TMPK
  asl a
  asl a
  tax
  lda StatTab,x          ; its bit (in the old word RSUM+4 for 0-5, the new RSUM+6 for 6-21)
  sta TMP2
  lda StatTab+2,x
  sta TMP3
  lda TMPK
  cmp #$0006
  bcs DR_snew
  lda RSUM+4
  and TMP2
  bne DR_simm
  lda RSUM+4
  xba
  and TMP2
  bne DR_shalf
  brl DR_snext
DR_snew:
  lda RSUM+6
  and TMP2
  bne DR_simm
  lda RSUM+8
  and TMP2
  bne DR_sres
  brl DR_snext
DR_simm:
  ldy #TImm-Texts
  bra DR_sgo
DR_shalf:
  ldy #THalf-Texts
  bra DR_sgo
DR_sres:
  ldy #TRes-Texts
DR_sgo:
  tya
  sta TMP4
  lda CNT
  cmp #$0010
  bcs DR_snext
  lsr a
  asl a
  asl a
  asl a
  asl a
  asl a
  asl a
  clc
  adc #$3202
  sta TMP0
  lda CNT
  and #$0001
  beq DR_s4
  lda TMP0
  clc
  adc #$001E
  sta TMP0
DR_s4:
  jsl DrawPair
  lda CNT
  inc a
  sta CNT
DR_snext:
  lda TMPK
  inc a
  sta TMPK
  cmp #$0016
  beq DR_sd
  brl DR_s1
DR_sd:
  lda CNT
  bne DR_up
  ldy #TNone-Texts
  ldx #$3202
  jsl DrawLine
DR_up:
  sep #$20
  lda #$08               ; upload the text map
  tsb $0074
  plb
  plp
  rtl

; ClearArea: the stats and equipment area of the text map: rows 6-15 (a line a row), columns 1-31 (HP, MP stay).
; 16-bit A and X, DB $7E.
ClearArea:
  ldx #$0000
CA_c:
  txa
  and #$003F
  beq CA_n
  lda #$0000
  sta $3180,x
CA_n:
  inx
  inx
  cpx #$0280
  bne CA_c
  rtl

; DrawPair: TMP3 = a name's text, TMP4 = its value's text, TMP0 = the map place: "name" then the value 7 letters on
DrawPair:
  lda TMP3
  tay
  lda TMP0
  tax
  jsl DrawLine
  lda TMP4
  tay
  lda TMP0
  clc
  adc #$0010             ; the value 8 letters on
  tax
  jsl DrawLine
  rtl

; DrawLine: Y = a text's offset in Texts, X = the place in the text map: drawn with the game's DrawMenuText ($80:8878),
; which reads the text from this bank (PER gives Texts' address here, PHK the bank)
DrawLine:
  php
  phb
  rep #$30
  phx
  tya
  per Texts
  clc
  adc $01,s
  tay
  pla
  plx
  sep #$20
  phk
  pla
  sta $5F
  lda #$3C                ; the status page's own letters (palette 7)
  sta $0564
  pea $7E7E
  plb
  plb
  jsl $808878
  plb
  plp
  rtl

; ================================================================ element percentages
; Lufia Forge's element table: a row of 17 signed words per fighter (the 16 element bits in order, then "plain": an
; attack with no element), each a percent (+22 = 22% more, -42 = 42% less, -100 = nothing, below -100 the hit heals).
; Rows: monsters 0-255 at +$0000, heroes 0-6 at +$2200, items 0-511 at +$2300 (a hero's row plus the rows of the six
; items it wears). The table's address is kept after the block's tables, at MonStart + $1600 (Lufia Forge fills it
; in; bank 0 = no table).

; ---- Row34: A = a row number -> A = its offset (x 34). 16-bit A.
Row34:
  sta ETMP
  asl a
  asl a
  asl a
  asl a
  asl a
  clc
  adc ETMP
  adc ETMP
  rtl

; ---- EAdd: A = a row's offset -> ESUM += its word for element EK (DB = the table's bank). 16-bit; changes X.
EAdd:
  clc
  adc EBASE
  clc
  adc EK
  tax
  lda $0000,x
  clc
  adc ESUM
  sta ESUM
  rtl

; ---- EPct: X = a fighter's block, A = an element (0-15 its bit, 16 plain) -> A = its percent (a hero: its row and the
; rows of its six worn items added up; a monster: its row; the capsule monster or no table: 0). 16-bit A, X, Y (call
; with them so); keeps X and Y; any DB.
EPct:
  php
  rep #$30
  phb
  phy
  phx
  asl a
  sta EK
  txa
  sta EBLK
  lda #$0000
  sta ESUM
  lda MonStart+$1602
  and #$00FF
  bne EP_go
  brl EP_out
EP_go:
  sep #$20
  pha
  plb                    ; DB = the table's bank
  rep #$20
  lda MonStart+$1600
  sta EBASE
  lda EBLK
  beq EP_out
  sec
  sbc #$0BAD
  bcc EP_mon
  cmp #$0532
  bcs EP_mon
  ldy #$0000
EP_hd:
  cmp #$00BE
  bcc EP_hdd
  sbc #$00BE
  iny
  bra EP_hd
EP_hdd:
  cmp #$0000
  bne EP_mon
  tya                    ; hero Y: its own row
  jsl Row34
  clc
  adc #$2200
  jsl EAdd
  ldy #$0066             ; then the six items it wears (+$66-$71)
EP_eq:
  tya
  clc
  adc EBLK
  tax
  lda $7E0000,x
  and #$01FF
  beq EP_en
  jsl Row34
  clc
  adc #$2300
  jsl EAdd
EP_en:
  iny
  iny
  cpy #$0072
  bne EP_eq
  bra EP_out
EP_mon:
  lda EBLK
  cmp $7E0A6C            ; the capsule monster: none
  beq EP_out
  tax
  lda $7E0050,x          ; a monster: its id
  and #$00FF
  jsl Row34
  jsl EAdd
EP_out:
  lda ESUM
  plx
  ply
  plb
  plp
  rtl

; ---- EApply: X = the target's block, ECNT = an element: EMAG (the hit's size) x (100 + its percent) / 100; a factor
; below 0 turns the hit into healing (ENEG flips). 16-bit; keeps X.
EApply:
  lda ECNT
  jsl EPct
  cmp #$0000
  bne EA_go
  rtl
EA_go:
  clc
  adc #$0064
  bpl EA_pos
  eor #$FFFF
  inc a
  sta EF
  lda ENEG
  eor #$FFFF
  sta ENEG
  bra EA_mul
EA_pos:
  sta EF
EA_mul:
  phx
  lda #$0000
  sta EPL
  sta EPH
  ldx #$0010
EA_ml:
  lda EPL                ; product (EPH:EPL) = EMAG x EF
  asl a
  sta EPL
  lda EPH
  rol a
  sta EPH
  lda EF
  asl a
  sta EF
  bcc EA_mn
  lda EPL
  clc
  adc EMAG
  sta EPL
  lda EPH
  adc #$0000
  sta EPH
EA_mn:
  dex
  bne EA_ml
  lda #$0000             ; then / 100 (the remainder in ETMP)
  sta ETMP
  ldx #$0020
EA_dl:
  lda EPL
  asl a
  sta EPL
  lda EPH
  rol a
  sta EPH
  lda ETMP
  rol a
  sta ETMP
  cmp #$0064
  bcc EA_dn
  sbc #$0064
  sta ETMP
  lda EPL
  ora #$0001
  sta EPL
EA_dn:
  dex
  bne EA_dl
  plx
  lda EPH
  bne EA_big
  lda EPL
  bpl EA_st
EA_big:
  lda #$7FFF
EA_st:
  sta EMAG
  rtl

; ---- ElemHit: from $85:D1BC (X = the target's block, A 8-bit), after its "when hit" script and before the damage
; formula: a hit (the damage register below 0) is changed by the target's percent for each element of the attack
; (multiplied: fire +50% and light +50% make x2.25), or for "plain" when it has none. Then what the hook replaced.
ElemHit:
  php
  rep #$30
  phx
  phy
  lda $7FF462
  bmi EH_hit
  brl EH_out
EH_hit:
  eor #$FFFF
  inc a
  sta EMAG
  lda #$FFFF
  sta ENEG
  lda $7FF45C
  sta EE
  lda #$0000
  sta ECNT
  lda EE
  bne EH_loop
  lda #$0010
  sta ECNT
  jsl EApply
  bra EH_fin
EH_loop:
  lda EE
  beq EH_fin
  lsr a
  sta EE
  bcc EH_next
  jsl EApply
EH_next:
  lda ECNT
  inc a
  sta ECNT
  bra EH_loop
EH_fin:
  lda ENEG
  beq EH_pos
  lda EMAG
  eor #$FFFF
  inc a
  bra EH_st
EH_pos:
  lda EMAG
EH_st:
  sta $7FF462
EH_out:
  ply
  plx
  plp
  lda $7FF4D0
  rtl

; ---- NumText: A = a percent (signed, 16-bit) -> NBUF = "+22%", "-42%" or "-" for 0 (00 at the end; % is the menu
; font's tile $117). Keeps X.
NumText:
  php
  rep #$30
  phx
  ldx #$0000
  cmp #$0000
  bne NT_some
  lda #$002D
  sta NBUF
  plx
  plp
  rtl
NT_some:
  bpl NT_p
  eor #$FFFF
  inc a
  sta ETMP
  lda #$002D
  bra NT_s
NT_p:
  sta ETMP
  lda #$002B
NT_s:
  sep #$20
  sta NBUF,x
  rep #$20
  inx
  lda #$0000
  sta ESTART
  lda #$03E8
  jsl NT_dig
  lda #$0064
  jsl NT_dig
  lda #$000A
  jsl NT_dig
  lda ETMP
  clc
  adc #$0030
  sep #$20
  sta NBUF,x
  inx
  lda #$09               ; the menu font's % is tile $117: attribute $3D, letter $17, attribute $3C again (the
  sta NBUF,x             ; STATUS screen's own "IP 0%" draws it so)
  inx
  lda #$3D
  sta NBUF,x
  inx
  lda #$17
  sta NBUF,x
  inx
  lda #$09
  sta NBUF,x
  inx
  lda #$3C
  sta NBUF,x
  inx
  lda #$00
  sta NBUF,x
  rep #$20
  plx
  plp
  rtl

; NT_dig: A = a power of ten: its digit of ETMP into NBUF,X (none while only zeros came), ETMP keeps the rest
NT_dig:
  sta EF
  lda #$0030
  sta EPL
NT_dl:
  lda ETMP
  cmp EF
  bcc NT_dd
  sbc EF
  sta ETMP
  lda EPL
  inc a
  sta EPL
  bra NT_dl
NT_dd:
  lda EPL
  cmp #$0030
  bne NT_put
  lda ESTART
  beq NT_skip
NT_put:
  lda EPL
  sep #$20
  sta NBUF,x
  rep #$20
  inx
  lda #$0001
  sta ESTART
NT_skip:
  rtl

; ---- DrawRam: X = the place in the text map: draws NBUF there (the game's DrawMenuText, the text read from $7F)
DrawRam:
  php
  phb
  rep #$30
  ldy #$FDC0
  sep #$20
  lda #$7F
  sta $5F
  lda #$3C
  sta $0564
  pea $7E7E
  plb
  plb
  jsl $808878
  plb
  plp
  rtl

; ================================================================ tables and texts
BitTab:
  dw $0001, $0002, $0004, $0008, $0010, $0020, $0040, $0080, $0100, $0200, $0400, $0800, $1000, $2000, $4000, $8000
SlotId:
  db $01, $02, $04, $08, $10, $81, $82, $84, $88, $90, $A0
  db $00
StatOff:
  dw $0029, $002B, $002D, $002F, $0031, $0033, $0035
CauseTab:
  dw CZom-Texts, CFro-Texts, CDec-Texts, CBur-Texts, CBer-Texts, CSpo-Texts, CAmn-Texts, CPie-Texts
  dw CReg-Texts, CDoo-Texts, CBli-Texts, CSto-Texts, CBle-Texts, CCur-Texts, CStp-Texts, CMan-Texts
CureTab:
  dw UZom-Texts, UFro-Texts, UDec-Texts, UBur-Texts, UBers-Texts, USpook-Texts, UAmn-Texts, UPie-Texts
  dw UReg-Texts, UDoo-Texts, UBli-Texts, USto-Texts, UBle-Texts, UCur-Texts, UStop-Texts, UMan-Texts
ExtraTab:
  dw XSha-Texts, XMel-Texts, XWas-Texts, XMis-Texts
DecayTab:
  dw DAtp-Texts, DDfp-Texts, DStr-Texts, DAgl-Texts, DInt-Texts, DGut-Texts, DMgr-Texts
PassTab:
  dw PBur-Texts, PBle-Texts, PReg-Texts, PMan-Texts, PDoo-Texts

ElemTab:                 ; element (0-15 its bit, 16 plain), name
  dw $0001, NFire-Texts, $0002, NWater-Texts, $000A, NIce-Texts, $000B, NThun-Texts
  dw $000C, NEarth-Texts, $0004, NLight-Texts, $0005, NDark-Texts, $0010, NPlain-Texts
StatTab:                 ; bit (in the old word for 0-5, the new one after), name
  dw $0001, NPoi-Texts, $0002, NSil-Texts, $0004, NDea-Texts, $0008, NPar-Texts, $0010, NCon-Texts, $0020, NSle-Texts
  dw $0001, NZom-Texts, $0002, NFro-Texts, $0004, NDec-Texts, $0008, NBur-Texts, $0010, NBer-Texts, $0020, NSpo-Texts
  dw $0040, NAmn-Texts, $0080, NPie-Texts, $0100, NReg-Texts, $0200, NDoo-Texts, $0400, NBli-Texts, $0800, NSto-Texts
  dw $1000, NBle-Texts, $2000, NCur-Texts, $4000, NStp-Texts, $8000, NMan-Texts

Texts:
TElTitle: db "ELEMENTS        Y: STATUS", $00
TStTitle: db "STATUS GUARD    Y: BACK", $00
TNone: db "Nothing guarded.", $00
TDash: db "-", $00
THalf: db "1/2", $00
TNull: db "NONE", $00
TImm: db "IMMUNE", $00
TRes: db "RESIST", $00
NFire: db "Fire", $00
NWater: db "Water", $00
NIce: db "Ice", $00
NThun: db "Thunder", $00
NEarth: db "Earth", $00
NLight: db "Light", $00
NDark: db "Dark", $00
NPlain: db "Plain", $00
NPoi: db "Poison", $00
NSil: db "Silence", $00
NDea: db "Death", $00
NPar: db "Paralyze", $00
NCon: db "Confuse", $00
NSle: db "Sleep", $00
NZom: db "Zombie", $00
NFro: db "Frozen", $00
NDec: db "Decay", $00
NBur: db "Burnt", $00
NBer: db "Berserk", $00
NSpo: db "Spooked", $00
NAmn: db "Amnesia", $00
NPie: db "Piety", $00
NReg: db "Regen", $00
NDoo: db "Doom", $00
NBli: db "Blind", $00
NSto: db "Stone", $00
NBle: db "Bleed", $00
NCur: db "Curse", $00
NStp: db "Stop", $00
NMan: db "Mana leak", $00
CZom: db " becomes a zombie.", $00
CFro: db " is frozen.", $00
CDec: db " starts to decay.", $00
CBur: db " is burnt.", $00
CBer: db " goes berserk!", $00
CSpo: db " is spooked.", $00
CAmn: db " has amnesia.", $00
CPie: db " is filled with piety.", $00
CReg: db " starts to regenerate.", $00
CDoo: db " is doomed.", $00
CBli: db " is blinded.", $00
CSto: db " turns to stone.", $00
CBle: db " is bleeding.", $00
CCur: db " is cursed.", $00
CStp: db " is stopped.", $00
CMan: db "'s mana starts to leak.", $00
UZom: db " is human again.", $00
UFro: db " thaws out.", $00
UDec: db " stops decaying.", $00
UBur: db "'s burns heal.", $00
UBers: db " calms down.", $00
USpook: db " is not afraid now.", $00
UAmn: db " remembers.", $00
UPie: db "'s piety fades.", $00
UReg: db " stops regenerating.", $00
UDoo: db " escapes doom.", $00
UBli: db " can see again.", $00
USto: db " turns back to flesh.", $00
UBle: db " stops bleeding.", $00
UCur: db "'s curse is lifted.", $00
UStop: db " can move again.", $00
UMan: db "'s mana stops leaking.", $00
XSha: db "'s ice shatters!", $00
XMel: db "'s ice melts.", $00
XWas: db "'s burns are cooled.", $00
XMis: db " misses!", $00
TSpook: db " is too scared to act.", $00
TStone: db " is stone.", $00
TFroz: db " is frozen solid.", $00
TStop: db " is stopped.", $00
TDoomC: db "'s doom: ", $00
DAtp: db "'s ATP decays.", $00
DDfp: db "'s DFP decays.", $00
DStr: db "'s STR decays.", $00
DAgl: db "'s AGL decays.", $00
DInt: db "'s INT decays.", $00
DGut: db "'s GUT decays.", $00
DMgr: db "'s MGR decays.", $00
PBur: db "The burns hurt.", $00
PBle: db "Wounds bleed.", $00
PReg: db "Regen heals.", $00
PMan: db "Mana leaks away.", $00
PDoo: db "Doom strikes!", $00

; after the block Lufia Forge allocates the monsters' starting statuses (a word per monster id, 256: zeros = none) and
; then, at MonStart + $200, a 10-byte record per item (512) for the STATUS screen's resistance pages: elements halved,
; elements nullified, the game's statuses immune / halved (a byte each), the new statuses immune, resisted (words);
; then, at MonStart + $1600, the element table's address (4 bytes)
MonStart:
