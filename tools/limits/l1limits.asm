; Lufia 1 (US): levels to 255 shown with 3 digits (Colmines92's idea), HP and MP to 9999 shown with 4 digits.
; HOOK = where the added routines go (upper 1 MB of a 2 MB ROM)

; ---- field party box (main menu, item/magic target lists, status/equip top box): start at column 20
org $00B93B
  dw $40A8, $41A8, $42A8, $43A8
org $00B907
  db $35            ; level: 3 digits
org $00B913
  db $44            ; HP 4 digits
org $00B918
  db $05            ; no space before '/'
org $00B91B
  db $44            ; max HP
org $00B927
  db $44            ; MP
org $00B92C
  db $05
org $00B92F
  db $44            ; max MP

; ---- save / party screen
org $00ABB0
  db $44
org $00ABC0
  db $44
org $00ABC9
  db $35
org $01AB3B
  ldy #$0018        ; sprites 8 px right, over the 3 digits

; ---- battle party windows: one column wider toward the screen edge (left windows start at column 1, right ones end
;      at column 30), so 10 columns of text fit beside the heroes' sprites
org $08926C
  db $35
org $08927B
  db $44
org $089282
  db $05
org $08928A
  db $44
org $08A29D
  db $0C            ; frame inner width 12 (redraw)
org $08A2D9
  db $0C            ; (first draw)
org $08A369
  dw $3442, $3462, $35C2, $35E2
org $08A291
  dw $4484, $44A8, $4604, $4628

; ---- field magic list: casts "(1234)"
org $00BA6E
  db $05
org $00BA79
  db $14

; ---- battle message amounts: 4 digits
org $08FB95
  db $14
org $08FC79
  db $14
org $08FCA5
  db $14
org $08FCCA
  db $14
org $08FCE8
  db $14
org $08FD06
  db $14
org $08FD24
  db $14
org $08FD41
  db $14
org $08FD5E
  db $14
org $08FD91
  db $14
org $08FE09
  db $14
org $08FE6D
  db $14
org $08FF68
  db $14

; ---- battle damage / heal numbers: 4 digits (two 16x16 sprites of two digits each, in the popup's own tiles)
org $08A4DF
  db $04            ; buffer characters 4-7
org $08A4F7
  txa               ; tile = X + 2 * slot base - 2
  clc
  adc $7EDAC4
  adc $7EDAC4
  adc #$FFFE
  tay
org $08A4C5
  db $01            ; MISS: blank + 3 tiles
org $08A4D1
  nop               ; next tile +1
org $08DE6A
  db $10            ; sprites 16 px apart
org $08DE6F
  db $05
org $08DE73
  db $02
org $08DE31
  db $04
org $08DE36
  db $01
org $08DD87
  db $10            ; centred on the monster

; ---- equipment bonus sums capped at 9999 instead of 999
org $08F7E1
  dw $270F
org $08F7E6
  dw $270F

; ---- max HP / MP never above 9999
org $08E8D4
  jsl HookHP
  nop
  nop
  nop
  nop
  nop
  nop
org $08E8E6
  jsl HookMP
  nop
  nop
  nop
  nop
  nop
  nop

; ---- growth band: bands 7+ use band 6 (levels past about 113)
org $08E6A1
  jsl HookBand
  nop
  nop
  nop

; ---- next-level EXP stops at FFFFFF instead of wrapping
org $08E722
  jsl HookExp
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop

org HOOK
HookHP:
  lda $15F1,x
  clc
  adc $1651,x
  bcs _hpcap
  cmp #$2710
  bcc _hpok
_hpcap:
  lda #$270F
_hpok:
  sta $16E0,x
  rtl
HookMP:
  lda $15F9,x
  clc
  adc $1659,x
  bcs _mpcap
  cmp #$2710
  bcc _mpok
_mpcap:
  lda #$270F
_mpok:
  sta $16E8,x
  rtl
HookBand:
  lda $1573,x
  cmp #$70
  bcc _band
  lda #$60
_band:
  and #$F0
  lsr
  lsr
  rtl
HookExp:
  lda $169D,x
  clc
  adc $1C
  sta $169D,x
  lda $16A1,x
  adc $1D
  sta $16A1,x
  lda $16A5,x
  adc $1E
  sta $16A5,x
  bcc _expok
  lda #$FF
  sta $169D,x
  sta $16A1,x
  sta $16A5,x
_expok:
  rtl
