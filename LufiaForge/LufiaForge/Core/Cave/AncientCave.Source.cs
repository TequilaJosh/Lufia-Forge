namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    /// <summary>
    /// The floor generator and its hooks, from the Ancient Cave project's ac.asm (assembled by Lufia Forge's
    /// assembler to the same bytes as asar makes; the map numbers, encounter table and loot table are added by
    /// <see cref="Assemble"/>).
    /// </summary>
    private const string Source = """
        !AC_FLOOR   = $7E1332       ; current floor 1-100 (Lufia Forge: in saved RAM, chest flags 3E0-3E7 no map uses)
        !AC_CHECK   = $7E1333       ; last checkpoint floor (1, 11, 21 ... 91): a game over brings you back there
        !AC_INCAVE  = $7E1334       ; 1 while the party is in the cave (a lost battle first moves to map 10, the game-over room)
        !AC_ACTIVE  = $7FFF21       ; 1 while in an Ancient Cave run
        !AC_RNG     = $7FFF22       ; 16-bit RNG state
        !AC_SPAWNX  = $7FFF24
        !AC_SPAWNY  = $7FFF26
        !FGRID = $E040       ; floor grid, stride 64, row -1 at $7FE000
        !FSTRIDE    = 64
        !ROOMS = $ED00       ; x[8] y[8] w[8] h[8] at +0/+8/+16/+24
        !NROOMS = $ED20
        !TREE = $ED28       ; 8 bytes: in-tree flags
        !GV = $ED40       ; generator scratch (32 bytes)
        !HDRSCRIPT  = $7FF000       ; RAM map header script for map $85
        !MAPW = 54
        !MAPH = 45
        !CAMX = 7                   ; player is drawn this many tiles from the camera corner
        !CAMY = 7
        !CH   = 3                   ; cliff height above a room
        !ENTRANCES = $7F151C    ; map $8E's entrance list in the loaded map data (6 bytes each: ?, x, y)
        !TPLMAP    = $8E            ; template map whose data we reuse
        !OBJBASE   = $03C0          ; $12B6 flag index base for cave chests
        !CELLS     = $7F0040
        !ZCOUNT    = $7F133F
        !OCOUNT    = $7F1340
        !ZONES     = $7F1570
        !OBJS      = $7F1588
        !gx = $ED40
        !gy = $ED42
        !gw = $ED44
        !gh = $ED46
        !tries = $ED48
        !target = $ED4A
        !i = $ED4C
        !j = $ED4E
        !t0 = $ED50
        !t1 = $ED52
        !t2 = $ED54
        !t3 = $ED56
        !best = $ED58
        !ba = $ED5A
        !bb = $ED5C
        !x1 = $ED5E
        !y1 = $ED60
        !x2 = $ED62
        !y2 = $ED64
        !dbv = $ED66
        !nchest = $ED68
        !aroom = $ED6A
        !sroom = $ED6C
        ;---------------------------------------------------------------------
        MapLoadHook:
            JSL $00A47D             ; decompress the map resource (original call)
            PHP
            SEP #$20
            LDA $7E077F
            JSR IsCaveMap
            BNE +
            LDA.b #$01
            STA !AC_INCAVE
            JSL GenerateFloor
            PHX
            LDA !AC_FLOOR           ; (Lufia Forge) the cave's colours change every 25 floors (tileset 6's palettes)
            DEC A
            REP #$20
            AND.w #$007F
            TAX
            SEP #$20
            LDA.l FloorPalette,x
            STA $7F0009             ; the map's palette (read from the unpacked header, as the intro's own override)
            PLX
            BRA ++
        +   CMP.b #$10              ; (the game-over room doesn't count as leaving)
            BEQ ++
            LDA.b #$00
            STA !AC_INCAVE
        ++  PLP
            RTL

        IsCaveMap:                  ; A = map id -> Z set if it is a cave floor map
            CMP.b #!CAVEMAP
            BEQ +
            CMP.b #!CAVEMAP2
        +   RTS

        PostHeader:                 ; runs after map header parsing (DB=$03)
            PHP
            SEP #$20
            LDA $7E077F
            JSR IsCaveMap
            BNE .out
            REP #$30
            PHX
            PHY
            SEP #$20
            ; encounter group for this floor
            LDA !AC_FLOOR
            DEC A
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            LDA.l EncounterByFloor,x
            STA $7E078B
            LDA.b #$00
            STA $7E0799             ; no template warps (map $8E's holes/stairs to other floors)
            ; chest contents
            LDX.w #$0000
            LDY.w #$0000
        -   TYA
            CMP !OCOUNT
            BCS +
            JSR ChestItem           ; (Lufia Forge: by depth, see AncientCave.Loot.cs)
            STA !OBJS+1,x
            REP #$20
            TXA
            CLC
            ADC.w #12
            TAX
            SEP #$20
            INY
            BRA -
        +   REP #$30
            PLY
            PLX
        .out
            PLP
            LDA $92                 ; original instructions
            CMP $078E
            RTL

        RandL:                      ; Rand usable from any DB
            PHB
            PHA
            LDA.b #$7F
            PHA
            PLB
            PLA
            JSR Rand
            PLB
            RTS

        ;---------------------------------------------------------------------
        ; RNG: 16-bit xorshift. Rand: A(8) = n -> A = 0..n-1
        ;---------------------------------------------------------------------
        RngStep:
            PHP
            REP #$20
            LDA !AC_RNG
            BNE +
            LDA.w #$ACE1
        +   STA $00
            ASL #7
            EOR $00
            STA $00
            LSR #9
            EOR $00
            STA $00
            XBA
            AND.w #$FF00
            EOR $00
            STA !AC_RNG
            PLP
            RTS

        Rand:                       ; A(8) = n (1..255) -> A(8) = 0..n-1
            PHP
            SEP #$20
            PHA
            JSR RngStep
            PLA
            STA $004202
            LDA !AC_RNG+1
            STA $004203
            NOP #4
            LDA $004217
            PLP
            RTS

        ;---------------------------------------------------------------------
        ; F grid helpers.  FIdx: X(16) = x, Y(16) = y -> X = offset into FGRID
        ;---------------------------------------------------------------------
        FAddr:                      ; in: !t0 = x, !t1 = y (16-bit) -> X = index
            REP #$20
            LDA !t1
            ASL #6
            CLC
            ADC !t0
            TAX
            RTS

        ;---------------------------------------------------------------------
        GenerateFloor:
            PHP
            PHB
            PHD
            REP #$30
            PHA
            PHX
            PHY
            LDA.w #$0000
            TCD
            LDA $00
            PHA
            LDA $02
            PHA
            SEP #$20
            LDA.b #$7F
            PHA
            PLB
            ; --- came down the stairs? (flag $F0) -> next floor
            LDA $7E1296+$1E
            BIT.b #$01
            BEQ +
            AND.b #$FE
            STA $7E1296+$1E
            LDA !AC_FLOOR
            CMP.b #100
            BCC .down
            LDA.b #$00              ; (Lufia Forge) past floor 100 (after the Sinistrals): a new run from floor 1
        .down
            INC A
            STA !AC_FLOOR
        +   LDA !AC_FLOOR
            BNE +
            LDA.b #$01
            STA !AC_FLOOR
        +
            ; (Lufia Forge) checkpoint floors 1, 11, 21 ... 91: a game over brings you back to the last one, and from
            ; floor 11 on their arrival door offers a save (a free story flag, !SAVEBYTE / !SAVEBIT, read by event 1)
            LDA !SAVEBYTE
            AND.b #!SAVEBIT^$FF
            STA !SAVEBYTE
            LDA !AC_FLOOR
            DEC A
        -   CMP.b #10
            BCC +
            SBC.b #10
            BRA -
        +   CMP.b #$00
            BNE +
            LDA !AC_FLOOR
            STA !AC_CHECK
            CMP.b #1
            BEQ +
            LDA !SAVEBYTE
            ORA.b #!SAVEBIT
            STA !SAVEBYTE
        +
            ; (Lufia Forge) floor 100: the stairs start the battle with the four Sinistrals (flag !FINALBYTE / !FINALBIT)
            LDA !FINALBYTE
            AND.b #!FINALBIT^$FF
            STA !FINALBYTE
            LDA !AC_FLOOR
            CMP.b #100
            BNE +
            LDA !FINALBYTE
            ORA.b #!FINALBIT
            STA !FINALBYTE
        +
            ; --- mix the frame counter into the RNG
            REP #$20
            LDA $38
            AND.w #$00FF
            EOR !AC_RNG
            STA !AC_RNG
            SEP #$20
            LDA $38
            AND.b #$0F
            XBA
            LDA.b #$00
            XBA
            TAX
        -   JSR RngStep
            DEX
            BPL -

        .build
            JSR ClearGrid
            JSR PlaceRooms
            JSR CarveRooms
            JSR Connect
            JSR RenderCells
            JSR PlaceDoors          ; C=1: no room for doorways, build another layout
            BCS .build
            JSR PlaceChests
            JSR BuildHeader

            REP #$30
            PLA
            STA $02
            PLA
            STA $00
            PLY
            PLX
            PLA
            PLD
            PLB
            PLP
            RTL

        ;---------------------------------------------------------------------
        ClearGrid:
            REP #$30
            LDX.w #$0000
            LDA.w #$0000
        -   STA $E000,x
            INX
            INX
            CPX.w #64*50
            BCC -
            SEP #$20
            STZ.w !NROOMS
            RTS

        ;---------------------------------------------------------------------
        PlaceRooms:
            SEP #$20
            LDA.b #4
            JSR Rand
            CLC
            ADC.b #5
            STA !target
            LDA.b #200
            STA !tries
        .loop
            LDA !NROOMS
            CMP !target
            BCC +
            RTS
        +   DEC !tries
            BNE +
            RTS
        +   LDA.b #7
            JSR Rand
            CLC
            ADC.b #5
            STA !gw                 ; 5..11
            LDA.b #5
            JSR Rand
            CLC
            ADC.b #4
            STA !gh                 ; 4..8
            ; x = 2 + rand(MAPW-3-w)   -> 2..MAPW-2-w
            LDA.b #!MAPW-3
            SEC
            SBC !gw
            JSR Rand
            CLC
            ADC.b #2
            STA !gx
            ; y = 7 + rand(MAPH-9-h)   -> 7..MAPH-3-h
            LDA.b #!MAPH-9
            SEC
            SBC !gh
            JSR Rand
            CLC
            ADC.b #7
            STA !gy
            ; overlap test against existing rooms
            LDX.w #$0000
        .chk
            TXA
            CMP !NROOMS
            BCS .ok
            ; x < rx+rw+3 ?
            LDA !ROOMS,x
            CLC
            ADC !ROOMS+16,x
            ADC.b #3
            CMP !gx
            BCC .next
            BEQ .next
            ; rx < x+w+3 ?
            LDA !gx
            CLC
            ADC !gw
            ADC.b #3
            CMP !ROOMS,x
            BCC .next
            BEQ .next
            ; y < ry+rh+8 ?
            LDA !ROOMS+8,x
            CLC
            ADC !ROOMS+24,x
            ADC.b #8
            CMP !gy
            BCC .next
            BEQ .next
            ; ry < y+h+8 ?
            LDA !gy
            CLC
            ADC !gh
            ADC.b #8
            CMP !ROOMS+8,x
            BCC .next
            BEQ .next
            JMP .loop               ; overlaps
        .next
            INX
            BRA .chk
        .ok
            LDA !NROOMS
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !gx
            STA !ROOMS,x
            LDA !gy
            STA !ROOMS+8,x
            LDA !gw
            STA !ROOMS+16,x
            LDA !gh
            STA !ROOMS+24,x
            LDA !NROOMS
            INC A
            STA !NROOMS
            JMP .loop

        ;---------------------------------------------------------------------
        SetF:                       ; !t0 = x, !t1 = y (16-bit) -> F = 1
            JSR FAddr
            SEP #$20
            LDA.b #$01
            STA !FGRID,x
            RTS

        CarveRooms:
            SEP #$20
            REP #$10
            LDA.b #$00
            STA !i
        .room
            LDA !i
            CMP !NROOMS
            BCC +
            RTS
        +   XBA
            LDA.b #$00
            XBA
            TAX
            LDA !ROOMS+8,x
            STA !gy
            STZ.w !gy+1
            LDA !ROOMS+24,x
            STA !gh
        .row
            LDX.w #$0000
            LDA !i
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !ROOMS,x
            STA !gx
            LDA !ROOMS+16,x
            STA !gw
        .col
            REP #$20
            LDA !gx
            AND.w #$00FF
            STA !t0
            LDA !gy
            AND.w #$00FF
            STA !t1
            JSR SetF
            SEP #$20
            INC !gx
            DEC !gw
            BNE .col
            INC !gy
            DEC !gh
            BNE .row
            INC !i
            BRA .room

        ;---------------------------------------------------------------------
        ; Connect rooms (Prim's MST on Manhattan distance of centres)
        ;---------------------------------------------------------------------
        Center:                     ; A(8) = room -> !x1/!y1 = centre (16-bit)
            SEP #$20
            REP #$10
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !ROOMS+16,x
            LSR
            CLC
            ADC !ROOMS,x
            STA !t2
            LDA !ROOMS+24,x
            LSR
            CLC
            ADC !ROOMS+8,x
            STA !t3
            RTS

        Connect:
            SEP #$20
            REP #$10
            LDX.w #$0007
        -   STZ.w !TREE,x
            DEX
            BPL -
            LDA.b #$01
            STA !TREE
            LDA !NROOMS
            CMP.b #2
            BCS .round
            RTS
        .round
            ; find the shortest edge from the tree to a room outside it
            LDA.b #$FF
            STA !best
            STA !bb
            LDA.b #$00
            STA !i
        .ia
            LDA !i
            CMP !NROOMS
            BCS .done_a
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !TREE,x
            BEQ .na
            LDA.b #$00
            STA !j
        .jb
            LDA !j
            CMP !NROOMS
            BCS .na
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !TREE,x
            BNE .nb
            ; d = |cx_i - cx_j| + |cy_i - cy_j|
            LDA !i
            JSR Center
            LDA !t2
            STA !x1
            LDA !t3
            STA !y1
            LDA !j
            JSR Center
            LDA !t2
            SEC
            SBC !x1
            BCS +
            EOR.b #$FF
            INC A
        +   STA !t0
            LDA !t3
            SEC
            SBC !y1
            BCS +
            EOR.b #$FF
            INC A
        +   CLC
            ADC !t0
            CMP !best
            BCS .nb
            STA !best
            LDA !i
            STA !ba
            LDA !j
            STA !bb
        .nb
            INC !j
            BRA .jb
        .na
            INC !i
            BRA .ia
        .done_a
            LDA !bb
            CMP.b #$FF
            BNE +
            RTS
        +   XBA
            LDA.b #$00
            XBA
            TAX
            LDA.b #$01
            STA !TREE,x
            JSR Corridor
            JMP .round

        Corridor:                   ; between rooms !ba and !bb, 2 tiles wide
            SEP #$20
            LDA !ba
            JSR Center
            LDA !t2
            STA !x1
            LDA !t3
            STA !y1
            LDA !bb
            JSR Center
            LDA !t2
            STA !x2
            LDA !t3
            STA !y2
            LDA.b #2
            JSR Rand
            BEQ .vfirst
            ; horizontal along y1 then vertical along x2
            LDA !y1
            JSR HRun
            LDA !x2
            JSR VRun
            RTS
        .vfirst
            LDA !x1
            JSR VRun
            LDA !y2
            JSR HRun
            RTS

        HRun:                       ; A = row y; carve x in [min(x1,x2), max(x1,x2)+1], rows y,y+1
            SEP #$20
            STA !gy
            LDA !x1
            CMP !x2
            BCC +
            LDA !x2
        +   STA !gx
            LDA !x1
            CMP !x2
            BCS +
            LDA !x2
        +   INC A
            INC A
            STA !gw                 ; end (exclusive)
        -   REP #$20
            LDA !gx
            AND.w #$00FF
            STA !t0
            LDA !gy
            AND.w #$00FF
            STA !t1
            JSR SetF
            REP #$20
            INC !t1
            JSR SetF
            SEP #$20
            INC !gx
            LDA !gx
            CMP !gw
            BCC -
            RTS

        VRun:                       ; A = column x; carve y in [min(y1,y2), max(y1,y2)+1], cols x,x+1
            SEP #$20
            STA !gx
            LDA !y1
            CMP !y2
            BCC +
            LDA !y2
        +   STA !gy
            LDA !y1
            CMP !y2
            BCS +
            LDA !y2
        +   INC A
            INC A
            STA !gh
        -   REP #$20
            LDA !gx
            AND.w #$00FF
            STA !t0
            LDA !gy
            AND.w #$00FF
            STA !t1
            JSR SetF
            REP #$20
            INC !t0
            JSR SetF
            SEP #$20
            INC !gy
            LDA !gy
            CMP !gh
            BCC -
            RTS

        ;---------------------------------------------------------------------
        ; Render the F grid into map cells
        ;---------------------------------------------------------------------
        FloorVariants:
            db $0D,$0D,$0D,$0D,$0D,$0D,$0D,$0D,$0D,$0D,$0D,$0D,$0D,$0D
            db $4F,$50,$51,$52,$4E
        CliffBase:  db $25,$15,$13,$14      ; index = L | R<<1  (L,R = same-height neighbours)
        CliffMid:   db $36,$37,$35,$36
        CliffTop:   db $25,$21,$23,$11

        ; Cliff: !t0=x, !t1=y -> A(8) = k (1..CH) if wall at (x,y) with floor k rows below
        ; (and wall in between), else 0
        Cliff:
            JSR FAddr
            SEP #$20
            LDA !FGRID,x
            BNE .no
            LDY.w #$0001
        -   REP #$20
            TXA
            CLC
            ADC.w #!FSTRIDE
            TAX
            SEP #$20
            LDA !FGRID,x
            BNE .yes
            INY
            CPY.w #!CH+1
            BCC -
        .no
            LDA.b #$00
            RTS
        .yes
            TYA
            RTS

        RenderCells:
            REP #$30
            STZ.w !t1
            LDA.w #(!CELLS&$FFFF)
            STA !best               ; running cell address (16-bit)
        .row
            STZ.w !t0
        .col
            JSR FAddr
            SEP #$20
            LDA !FGRID,x
            BEQ .wall
            ; ---- floor
            LDA !FGRID-!FSTRIDE,x
            BNE +
            LDA.b #$0F              ; wall above: shadow floor
            BRL .put
        +   LDA !FGRID-1,x
            BNE +
            LDA.b #$0E              ; wall left
            BRL .put
        +   LDA.b #19
            JSR Rand
            REP #$10
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA.l FloorVariants,x
            BRL .put
        .wall
            REP #$20
            JSR Cliff
            SEP #$20
            BEQ .noncliff
            STA !dbv
            ; same-height neighbours
            REP #$20
            DEC !t0
            JSR Cliff
            SEP #$20
            STZ !t2
            CMP !dbv
            BNE +
            INC !t2                 ; L
        +   REP #$20
            INC !t0
            INC !t0
            JSR Cliff
            REP #$20
            DEC !t0
            SEP #$20
            CMP !dbv
            BNE +
            LDA !t2
            ORA.b #$02              ; R
            STA !t2
        +   REP #$10
            LDA !t2
            AND.b #$03
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !dbv
            CMP.b #1
            BNE +
            LDA.l CliffBase,x
            BRL .put
        +   CMP.b #!CH
            BNE +
            LDA.l CliffTop,x
            BRL .put
        +   LDA.l CliffMid,x
            BRL .put
        .noncliff
            JSR FAddr
            SEP #$20
            LDA !FGRID-!FSTRIDE,x   ; floor above?
            BEQ .nofloorabove
            LDA !FGRID-1,x
            BEQ +
            LDA.b #$18              ; floor above and left
            BRL .put
        +   LDA !FGRID+1,x
            BEQ +
            LDA.b #$1A              ; floor above and right
            BRL .put
        +   LDA.b #$19              ; floor above
            BRL .put
        .nofloorabove
            LDA !FGRID+1,x
            BEQ +
            LDA.b #$17              ; floor to the right
            BRL .put
        +   LDA !FGRID-1,x
            BEQ +
            LDA.b #$16              ; floor to the left
            BRL .put
        +   LDA !FGRID-!FSTRIDE-1,x
            BEQ +
            LDA.b #$1C
            BRL .put
        +   LDA !FGRID-!FSTRIDE+1,x
            BEQ +
            LDA.b #$1D
            BRL .put
        +   LDA.b #$1E              ; solid rock
        .put
            REP #$30
            AND.w #$00FF
            LDX !best
            STA $0000,x             ; DB = $7F
            INX
            INX
            STX !best
            INC !t0
            LDA !t0
            CMP.w #!MAPW
            BCS +
            JMP .col
        +   INC !t1
            LDA !t1
            CMP.w #!MAPH
            BCS +
            JMP .row
        +   SEP #$20
            RTS

        ;---------------------------------------------------------------------
        ; Doorways: arrival in room 0, stairs in the room farthest from it
        ;---------------------------------------------------------------------
        CellAddr:                   ; !t0=x, !t1=y -> X = cell address (bank $7F)
            REP #$20
            LDA !t1
            STA $00
            ASL #5                  ; *32
            STA $02
            LDA $00
            ASL #4                  ; *16
            CLC
            ADC $02                 ; *48
            STA $02
            LDA $00
            ASL #2                  ; *4
            CLC
            ADC $02                 ; *52
            STA $02
            LDA $00
            ASL                     ; *2
            CLC
            ADC $02                 ; *54
            CLC
            ADC !t0
            ASL
            CLC
            ADC.w #(!CELLS&$FFFF)
            TAX
            RTS

        ; Door: A(8) = room -> !t0/!t1 = doorway cell (x, roomtop-1). C=1 found, C=0 none.
        ; Scans every column of the room's top wall from a random start. A doorway needs
        ; a full-height cliff (wall at y, y-1, y-2), wall on both sides and floor below.
        Door:
            SEP #$20
            REP #$10
            STA !j
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !ROOMS+16,x
            SEC
            SBC.b #2
            STA !gw                 ; candidate columns
            STA !tries
            JSR Rand
            STA !gx                 ; start offset
        .try
            LDA !j
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !gx
            INC A
            CLC
            ADC !ROOMS,x
            STA !t0
            STZ.w !t0+1
            LDA !ROOMS+8,x
            DEC A
            STA !t1
            STZ.w !t1+1
            JSR FAddr
            SEP #$20
            LDA !FGRID,x
            ORA !FGRID-!FSTRIDE,x
            ORA !FGRID-(!FSTRIDE*2),x
            ORA !FGRID-1,x
            ORA !FGRID+1,x
            BNE .bad
            LDA !FGRID+!FSTRIDE,x
            CMP.b #$01
            BNE .bad
            SEC
            RTS
        .bad
            LDA !gx
            INC A
            CMP !gw
            BCC +
            LDA.b #$00
        +   STA !gx
            DEC !tries
            BNE .try
            CLC
            RTS

        ; FindDoor: try room A first, then every other room except !aroom. C=0 if none.
        FindDoor:
            SEP #$20
            STA !i
            JSR Door
            BCS .ok
            STZ !ba
        -   LDA !ba
            CMP !NROOMS
            BCS .none
            CMP !aroom
            BEQ +
            CMP !i
            BEQ +
            JSR Door
            BCS .ok
        +   INC !ba
            BRA -
        .none
            CLC
        .ok
            RTS

        PlaceDoors:
            SEP #$20
            REP #$10
            ; stairs room = farthest from room 0
            LDA.b #$00
            JSR Center
            LDA !t2
            STA !x1
            LDA !t3
            STA !y1
            STZ !sroom
            STZ !best
            LDA.b #$01
            STA !i
        -   LDA !i
            CMP !NROOMS
            BCS +
            JSR Center
            LDA !t2
            SEC
            SBC !x1
            BCS ++
            EOR.b #$FF
            INC A
        ++  STA !t0
            LDA !t3
            SEC
            SBC !y1
            BCS ++
            EOR.b #$FF
            INC A
        ++  CLC
            ADC !t0
            CMP !best
            BCC ++
            STA !best
            LDA !i
            STA !sroom
        ++  INC !i
            BRA -
        +
            ; arrival doorway: room 0, or any room that has room for one
            LDA.b #$FF
            STA !aroom
            LDA.b #$00
            JSR FindDoor
            BCS +
            SEC                     ; C=1 -> GenerateFloor builds a new layout
            RTS
        +   LDA !j
            STA !aroom
            LDA !t0
            STA !x1
            LDA !t1
            STA !y1
            JSR MakeDoor
            ; zone 0 = arrival
            LDX.w #$0000
            JSR SetZoneRect
            ; player spawn = the floor cell below the doorway
            REP #$20
            LDA !x1
            AND.w #$00FF
            STA !AC_SPAWNX
            SEC
            SBC.w #!CAMX
            BPL .camx               ; (Lufia Forge) near the left edge: the map wraps, so does the camera (was -1..-5)
            CLC
            ADC.w #!MAPW
        .camx
            STA $7E134A             ; camera top-left = player - screen offset
            LDA !y1
            AND.w #$00FF
            INC A
            STA !AC_SPAWNY
            SEC
            SBC.w #!CAMY
            BPL .camy
            CLC
            ADC.w #!MAPH
        .camy
            STA $7E134C
            ; every entrance of the template map leads to the arrival spot
            LDX.w #$0000
        -   LDA !AC_SPAWNX
            STA.l !ENTRANCES+2,x
            LDA !AC_SPAWNY
            STA.l !ENTRANCES+4,x
            TXA
            CLC
            ADC.w #6
            TAX
            CPX.w #6*14
            BCC -
            SEP #$20
            ; stairs doorway: farthest room, else any other room
            LDA !sroom
            CMP !aroom
            BNE +
            LDA !aroom
            INC A                   ; arrival room is the farthest one? try the next room first
            CMP !NROOMS
            BCC +
            LDA.b #$00
        +   JSR FindDoor
            BCS +
            PLA
            PLA
            SEC
            RTS
        +   LDA !t0
            STA !x1
            LDA !t1
            STA !y1
            JSR MakeDoor
            LDX.w #12
            JSR SetZoneRect
            LDA.b #$02
            STA !ZCOUNT
            CLC
            RTS

        MakeDoor:                   ; doorway at !x1/!y1: $4B at (x,y), $E8 at (x,y-1)
            REP #$20
            LDA !x1
            AND.w #$00FF
            STA !t0
            LDA !y1
            AND.w #$00FF
            STA !t1
            JSR CellAddr
            LDA.w #$004B
            STA $0000,x
            TXA
            SEC
            SBC.w #!MAPW*2
            TAX
            LDA.w #$00E8
            STA $0000,x
            JSR FAddr               ; mark the doorway in the F grid (3)
            SEP #$20
            LDA.b #$03
            STA !FGRID,x
            RTS

        SetZoneRect:                ; X = zone*12 ; rect = (x1,y1)-(x1+1,y1+1)
            REP #$20
            LDA !x1
            AND.w #$00FF
            STA !ZONES+4,x
            INC A
            STA !ZONES+8,x
            LDA !y1
            AND.w #$00FF
            STA !ZONES+6,x
            INC A
            STA !ZONES+10,x
            SEP #$20
            RTS

        ;---------------------------------------------------------------------
        ; Chests
        ;---------------------------------------------------------------------
        PlaceChests:
            SEP #$20
            REP #$10
            LDA.b #4
            JSR Rand
            CLC
            ADC.b #2
            STA !nchest             ; 2..5 (header script supports up to 8)
            STZ !i
        .next
            LDA !i
            CMP !nchest
            BCC +
            LDA !nchest
            STA !OCOUNT
            RTS
        +   LDA.b #40
            STA !tries
        .try
            LDA !NROOMS
            JSR Rand
            REP #$10
            XBA
            LDA.b #$00
            XBA
            TAX
            LDA !ROOMS+16,x
            SEC
            SBC.b #2
            JSR Rand
            INC A
            CLC
            ADC !ROOMS,x
            STA !t0
            STZ.w !t0+1
            LDA !ROOMS+24,x
            SEC
            SBC.b #2
            JSR Rand
            INC A
            CLC
            ADC !ROOMS+8,x
            STA !t1
            STZ.w !t1+1
            ; must be floor and not already used (F=2 marks a chest)
            JSR FAddr
            SEP #$20
            LDA !FGRID,x
            CMP.b #$01
            BNE .bad
            LDA !FGRID-!FSTRIDE,x   ; open floor on all four sides (no corridors, edges or doorways)
            CMP.b #$01
            BNE .bad
            LDA !FGRID+!FSTRIDE,x
            CMP.b #$01
            BNE .bad
            LDA !FGRID-1,x
            CMP.b #$01
            BNE .bad
            LDA !FGRID+1,x
            CMP.b #$01
            BNE .bad
            JSR NearDoor            ; not right next to either doorway
            BCS .bad
            JSR FAddr
            SEP #$20
            LDA.b #$02
            STA !FGRID,x
            ; object entry i: copy template chest entry (object 2), set rect
            LDA !i
            STA $004202
            LDA.b #12
            STA $004203
            NOP #4
            REP #$20
            LDA $004216
            CLC
            ADC.w #(!OBJS&$FFFF)
            TAY
            LDX.w #$0000
        -   SEP #$20
            LDA !OBJS+24,x          ; template chest = object 2
            STA $0000,y
            INY
            INX
            CPX.w #4
            BCC -
            REP #$20
            LDA !t0
            STA $0000,y
            INC A
            STA $0004,y
            LDA !t1
            STA $0002,y
            INC A
            STA $0006,y
            SEP #$20
            INC !i
            JMP .next
        .bad
            DEC !tries
            BEQ +
            JMP .try
        +   INC !i                  ; give up on this chest
            JMP .next

        ;---------------------------------------------------------------------
        NearDoor:                   ; carry set if (t0,t1) is within 2 cells of a doorway's floor cell
            REP #$20
            LDA !AC_SPAWNY             ; arrival: floor cell below the doorway
            TAX
            LDA !AC_SPAWNX
            JSR .chk
            BCS .out
            LDA !ZONES+12+4         ; stairs doorway cell; floor cell is one below
            PHA
            LDA !ZONES+12+6
            INC A
            TAX
            PLA
            JSR .chk
        .out
            SEP #$20
            RTS
        .chk                        ; A = x, X = y (16-bit)
            SEC
            SBC !t0
            BPL +
            EOR.w #$FFFF
            INC A
        +   CMP.w #3
            BCS .no
            TXA
            SEC
            SBC !t1
            BPL +
            EOR.w #$FFFF
            INC A
        +   CMP.w #3
            BCS .no
            SEC
            RTS
        .no
            CLC
            RTS

        ;---------------------------------------------------------------------
        ; RAM header script for map $85
        ;---------------------------------------------------------------------

        BuildHeader:                ; new floor: all cave chests closed again
            REP #$20
            LDA.w #$0000
            STA $7E12B6+(!OBJBASE/8)
            STA $7E12B6+(!OBJBASE/8)+2
            SEP #$20
            RTS

        ;---------------------------------------------------------------------
        ; Cave-only New Game
        ;---------------------------------------------------------------------
        CaveNewGame:
            PHP
            PHB
            SEP #$20
            REP #$10
            LDA.b #$7E
            PHA
            PLB
            ; clear party, levels, inventory, gold, spells
            LDX.w #$0000
        -   STZ $156F,x
            STZ $1577,x
            INX
            CPX.w #8
            BCC -
            LDX.w #$0000
        -   STZ $14F7,x
            INX
            CPX.w #120
            BCC -
            STZ $14CF
            STZ $14D0
            STZ $14D1
            LDX.w #$0000
        -   STZ $158F,x
            INX
            CPX.w #$60
            BCC -
            LDA.b #$FF
            STA $14D2
            STA $14D3
            STA $14D4
            STA $14D5
            STZ $14CE
            LDA.b #$00
            JSL $08EA8F
            LDA.b #$00
            JSR Level1
            LDA.b #$01
            JSL $08EA8F
            LDA.b #$01
            JSR Level1
            LDA.b #$02
            JSL $08EA8F
            LDA.b #$02
            JSR Level1
            LDA.b #$03
            JSL $08EA8F
            LDA.b #$03
            JSR Level1
            LDX.w #$0000            ; (Lufia Forge) the play statistics ($134E-$1363: deaths, battles, boxes...) from 0
        -   STZ $134E,x
            INX
            CPX.w #$0016
            BCC -
            STZ $14CF               ; (joining the Hero gives 100 GP: the cave starts with none)
            STZ $14D0
            STZ $14D1
            ; (names: the join gives the Hero the name the player typed, the others theirs)
            ; cave state
            LDA.b #$01
            STA !AC_FLOOR
            STA !AC_CHECK
            STA !AC_ACTIVE
            REP #$20
            LDA $38
            ORA.w #$0001
            STA !AC_RNG
            SEP #$20
            LDA.b #!CAVEMAP
            STA $1345
            STZ $1346
            STZ $1347
            PLB
            PLP
            RTL

        """;
}
