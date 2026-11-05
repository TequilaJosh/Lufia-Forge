; Lufia II (US): levels to 255 (saved, shown with 3 digits: Colmines92's idea), HP and MP to 9999 (saved, shown with 4 digits).
; HOOK = where the added routines go (the expanded ROM, any bank: everything is reached by JSL/JML)

; ================= save data (a hero record keeps its size: spare bits carry the new ones; old saves read the same)
org $85CAB7
  jsl LvlPack            ; level byte = level rotated left (bit 7 into bit 0)
  nop
  nop
org $85CA5E
  jsl ExtPack            ; +95 byte = HP bits 10-13 (low nibble), MP bits 10-13 (high nibble)
  nop
  nop
  nop
org $85CB17
  ldy #$0014             ; HP/MP high bytes' bits 8-9 in bits 7-4 (bits 3-0: 0, max HP/MP are worked out on loading)
  lda [$30],y
  and #$03
  sta $11
  ldy #$0012
  lda [$30],y
  asl
  asl
  ora $11
  asl
  asl
  asl
  asl
  nop
  nop
  nop
  nop
org $85C7FE
  jsl LvlUnpack
org $85C86A
  pla
  lsr
  lsr
  jsl HpMpUnpack
  inx
  nop
org $82F291
  jsl LvlUnpack          ; the file screen's level

; ================= growth rows: row 12 for every level from 96 on (the table has 13 rows)
org $81F8C5
  jsl RowStep

; ================= EXP: up to FFFFFF, and a table for levels 98-255
org $81DB0B
  db $FF
org $81DB14
  db $FF
org $81DB1D
  db $FF
org $81DB21
  db $FF
org $81DB26
  db $FF
org $81DB2B
  db $FF
org $81DC2B
  db $FF
org $81DC34
  db $FF
org $81DC3D
  db $FF
org $81DC41
  db $FF
org $81DC46
  db $FF
org $81DC4B
  db $FF
org $81DA28
  db $FF
org $81DA31
  db $FF
org $81DA3A
  db $FF
org $81DA3E
  db $FF
org $81DA43
  db $FF
org $81DA48
  db $FF
org $81FABB
  jml ExpHigh

; ================= field menu hero header: LV 3 digits, HP/MP 4 digits
org $8ED451
  db $07                 ; no "V" (Colmines92)
org $8ED456
  db $05                 ; level: 3 digits (Colmines92)
org $8ED483
  db $84                 ; HP from column 2
org $8ED485
  db $0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F   ; no 999 branch
org $8ED493
  db $04                 ; HP 4 digits
org $8ED49B
  db $0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F
org $8ED4A9
  db $04                 ; max HP 4 digits (right-aligned: blanks clear an older, longer number)
org $8ED4AF
  db $C4
org $8ED4B1
  db $0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F
org $8ED4BF
  db $04
org $8ED4C7
  db $0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F,$0F
org $8ED4D5
  db $04

; ================= save / load screen: level 3 digits (Colmines92)
org $8ECA96
  db $05

; ================= battle status bar
org $81E1EC              ; level: "L" and 3 digits (no "V")
  tdc
  lda $000E,x
  phx
  tax
  jsr $E808
  lda #$60
  sta $000A,y
  lda $B4
  beq _bl1
  ora #$40
_bl1:
  sta $000C,y
  ora $B3
  beq _bl2
  lda $B3
  ora #$40
_bl2:
  sta $000E,y
  lda $B2
  ora #$40
  sta $0010,y
  nop
org $81E2C8              ; HP / MP numbers: 3 digits as the game, 4 from 1000 (one label tile)
  jsl HpMp4
  rts
org $81E308              ; gauge: HP * 26 no longer overflows from 2521
  jsl GaugePre
  nop
  nop

; ================================================================ added code
org HOOK
LvlPack:
  ldy #$000E
  lda [$30],y
  cmp #$80
  rol
  rtl
LvlUnpack:
  lda $0000,x
  lsr
  bcc _lu
  ora #$80
_lu:
  rtl
ExtPack:
  ldy #$0014
  lda [$30],y
  asl
  asl
  and #$F0
  sta $0000,x
  ldy #$0012
  lda [$30],y
  lsr
  lsr
  and #$0F
  ora $0000,x
  sta $0000,x
  inx
  rtl
HpMpUnpack:
  pha
  ldy #$0095
  lda [$30],y
  asl
  asl
  and #$3C
  ora $01,s
  ldy #$0012
  sta [$30],y
  pla
  ldy #$0095
  lda [$30],y
  lsr
  lsr
  and #$3C
  ldy #$0014
  ora [$30],y
  sta [$30],y
  lda #$00
  ldy #$0095
  sta [$30],y
  rtl
RowStep:
  txa
  inx
  cmp #$68
  bcs _rs
  and #$07
  rtl
_rs:
  lda #$01
  rtl
ExpHigh:
  rep #$20
  lda $09FE
  and #$00FF
  sec
  sbc #$0062
  sta $54
  asl
  clc
  adc $54
  tax
  lda ExpTable,x
  sta $0A2A
  sep #$20
  lda ExpTable+2,x
  sta $0A2C
  plx
  plb
  rtl
HpMp4:
  phy
  rep #$20
  lda $11
  cmp #$2710
  bcc _h0
  lda #$270F
_h0:
  ldx #$0000
_h1:
  cmp #$03E8
  bcc _h2
  sbc #$03E8
  inx
  bra _h1
_h2:
  phx
  ldx #$0000
_h3:
  cmp #$0064
  bcc _h4
  sbc #$0064
  inx
  bra _h3
_h4:
  phx
  ldx #$0000
_h5:
  cmp #$000A
  bcc _h6
  sbc #$000A
  inx
  bra _h5
_h6:
  phx
  pha
  sep #$20
  lda $07,s
  beq _small
  lda $16
  sta $0000,y
  lda $07,s
  ora #$40
  sta $0002,y
  lda $05,s
  ora #$40
  sta $0004,y
  lda $03,s
  ora #$40
  sta $0006,y
  bra _ones
_small:
  lda $16
  sta $0000,y
  inc
  sta $0002,y
  lda $05,s
  beq _hz
  ora #$40
  sta $0004,y
  lda $03,s
  ora #$40
  sta $0006,y
  bra _ones
_hz:
  sta $0004,y
  lda $03,s
  beq _tz
  ora #$40
_tz:
  sta $0006,y
_ones:
  lda $01,s
  ora #$40
  sta $0008,y
  rep #$20
  pla
  pla
  pla
  pla
  sep #$20
  ply
  rtl
GaugePre:
  rep #$20
_g1:
  lda $13
  cmp #$0800
  bcc _g2
  lsr $13
  lsr $11
  bra _g1
_g2:
  lda $11
  sta $4E
  rtl
ExpTable:
