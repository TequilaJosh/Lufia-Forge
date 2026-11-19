; Lufia II (US): 128 more story flags (Lufia Forge, 2026-11-16). HOOK = where the added code goes (any bank; it is
; reached by JML / JSL only).
;
; The game has 256 story flags, one bit each at $7E:077E-$7E:079D, and 32 event counters, a byte each at
; $7E:079E-$7E:07BD; its events use counters 01-0E only. The extra flags 100-17F are the bits of counters 10-1F
; ($7E:07AE-$7E:07BD): flag 100 + e is bit (e & 7) (mask table $80:BE45, as the story flags) of byte $07AE + (e >> 3).
; Those bytes are in the field state a save keeps ($077E, 0x22C bytes, $8E:BA0D / $8E:B993) and a new game clears
; ($83:ADDF), so the extra flags are saved, loaded and cleared with the story flags.
;
; New event commands (the interpreter's table at $80:CA14 ends at CC; CD and up ran whatever followed it):
;   CD e        turn extra flag 100 + e on
;   CE e        turn extra flag 100 + e off
;   CF e w      if extra flag 100 + e is on, go to w (as 15)
;   D0 e w      if extra flag 100 + e is off, go to w (as 6A)
; Command 14 (check conditions): a story flag check (codes 00-0F, 40-4F, 80-8F) with bit 02 set checks extra flag
; 100 + its byte instead (the game's checks leave bits 02-08 clear: 0 of its 1,197 flag checks set them).

; ---------------------------------------------------------------- the game's code
org $809D35
  jmp Dispatch           ; REP #$20 / ASL / TAX / SEP #$20 / JMP ($CA14,X): commands CD-D0 go to the added code

org $80A0AF
  jsr Check14            ; JSR $C0B7 / JSR $BE1E: "story flag is on" check of command 14
  nop
  nop
  nop

org $80A0C1
  jsr Check14            ; the same for "is off"
  nop
  nop
  nop

; ---------------------------------------------------------------- bank $80's unused bytes ($80:9B80-$80:9C0E are FF)
org $809B80
Dispatch:
  cmp #$CD
  bcs _ext
  rep #$20
  asl A
  tax
  sep #$20
  jmp ($CA14,x)
_ext:
  jml ExtOps
ReadByte:                ; the next byte of the script ($80:C0B7 for the added code)
  jsr $C0B7
  rtl
Check14:                 ; A flag check of command 14: Z clear when the flag is on
  jsr $C0B7
  pha
  lda $22
  and #$02
  bne _x14
  pla
  jmp $BE1E
_x14:
  pla
  jsl TestExtra
  rts

; ---------------------------------------------------------------- the added code
org HOOK
ExtOps:                  ; A = the command (8-bit A, 16-bit X / Y), Y = the script's next byte
  cmp #$D1
  bcc _ok
  jml $809D00
_ok:
  sta $54
  jsl ReadByte
  jsl Locate
  lda $54
  cmp #$CE
  beq _clear
  bcs _test
  lda $7E077E,x          ; CD: on
  ora $57
  sta $7E077E,x
  jml $809D00
_clear:
  lda $57                ; CE: off
  eor #$FF
  and $7E077E,x
  sta $7E077E,x
  jml $809D00
_test:
  lda $7E077E,x
  and $57
  beq _off
  lda $54
  cmp #$CF
  beq _go
  bra _skip
_off:
  lda $54
  cmp #$D0
  beq _go
_skip:
  jsl ReadByte           ; not taken: past the step it would go to
  jsl ReadByte
  jml $809D00
_go:
  jml $80A3C6            ; 1C's go to: reads the step and goes there

TestExtra:               ; A = e: Z clear when extra flag 100 + e is on
  jsl Locate
  lda $7E077E,x
  and $57
  rtl

Locate:                  ; A = e: X = its byte (from $077E), $57 = its bit
  and #$7F
  pha
  rep #$20
  and #$0007
  tax
  sep #$20
  lda $80BE45,x
  sta $57
  pla
  rep #$20
  and #$00FF
  lsr A
  lsr A
  lsr A
  clc
  adc #$0030
  tax
  sep #$20
  rtl
