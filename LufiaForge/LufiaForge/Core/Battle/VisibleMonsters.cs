using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// monsters you can see, like in Lufia II: on a map that has them, monster characters walk around and a fight starts
// when one reaches the party, and random battles are off there. beaten monsters vanish and come back when the map is
// entered again. (not on the world map)
//
// a monster is an ordinary map character n (sprite, place, wander box) whose talk event (n + 1) is
// 1F gg (a battle with encounter group gg, its own, like a random battle would pick: $142D = group) then
// 5C n+1 00 (hide it, 5C k hides character k - 1). a 256-entry table (first monster character, count) says which
// characters of each map are monsters, and the step check for random battles ($01:881D) is hooked so there are none
// on those maps (their setup scripts stay as they were, so taking the monsters off brings the random battles back):
//   $01:8820  22 NoRandom / D0 4E / EA   (was LDA $07B4 / BNE $8874 / LDA $94. BNE $8874 stays)
//
// they move like Lufia II's (watched in Lufia II: they stand still while the hero does, take a step for each of his,
// drift towards him). the map characters' wander routine ($01:9FF1, X = the character's record, $18 = actor id,
// map character n = actor n + 8) is hooked where it checks the "frozen" bit. a monster only takes a step when
// the hero has reached a new tile, towards him within 6 tiles (else at random), inside its box, not onto him, using
// the game's own target ($01:A053) and wall ($01:AFAC) checks:
//   $01:9FFF  22 Step         (was LDA $1D,X / BIT #$20. BNE $9FDC stays: not 0 = only animate)
// when a monster stands next to the hero (both still) it waits there, and we make the talk check ($01:8394, A pressed)
// happen with the hero facing it, so the game runs its event as if he'd talked to it:
//   $01:8394  22 Talk         (was LDA $30 / AND #$80. BEQ $840E stays)
// a beaten monster (hidden by its event: $1D bit 80) gets taken out of the map's actor list ($07BE, 32 actor ids),
// which the wandering and the "who is on that tile" check of talking ($01:87DC) go through.
// after a fight the other monsters can't start one until the hero has taken GraceSteps steps.
// RAM: $7F:FEC0-FEC7 (the hero's tile, the turn count, the waiting monster and its direction, the grace steps left),
// $7F:FC00-FC3F (the turn each actor last moved on. before version 5 it was $7F:FF80-FFBF, which the Ancient Cave's
// spell chests also use).
//
// talking to character n runs event n + 1 of the map's script block (the talk's $16 = actor - 7 = n + 1 is the event
// number, traced at $01:C2BA. roms made before 2026-10-22 had event n, which only worked while the monsters' events
// all had the same group, and froze on the last one. EventOf reads both), and it uses the map the character's
// record names ($1C), which can still be the map it came from, so we make the talk use the map the party is on.
// the monsters take characters whose events nobody else uses (no script, no step-on area, no "run event"), skipped
// ones stay unused. a cutscene that brings in characters a map doesn't list (the opening on map 4F does) can clash
// with them, so those maps want a try in the game
public static class VisibleMonsters
{
    private const int StepSite = 0x9FFF;        // $01:9FFF
    private static readonly byte[] StepOriginal = { 0xB5, 0x1D, 0x89, 0x20 };
    private const int TalkSite = 0x8394;        // $01:8394
    private static readonly byte[] TalkOriginal = { 0xA5, 0x30, 0x29, 0x80 };
    private const int RandomSite = 0x8820;      // $01:8820
    private static readonly byte[] RandomOriginal = { 0xAD, 0xB4, 0x07, 0xD0, 0x4F, 0xA5, 0x94 };
    private const string Magic = "LFVM";
    // the block (version 3): header, code, the table of each map's monsters, a behaviour byte per monster,
    // each map's paths (u16 into the heap, 0 = none) and the heap: per map 16 u16 (each monster's path, 0 = none),
    // a path = [mode][count][x y]... versions 1-2 had the code up to 0x300 and the tables after it (2 added behaviours)
    private const int Header = 8, MapTable = 0x1000, BehaviourTable = MapTable + 512, PathMaps = BehaviourTable + 256 * 16,
                      Heap = PathMaps + 512, BlockSize = 0x7000, HeapSize = BlockSize - Heap;
    // the block's version (header byte 4). an older block gets rebuilt, keeping its monsters
    private const int Version = 6;
    public const int MaxPathPoints = 32;

    // where the block keeps its tables (depends on its version)
    private static (int Maps, int Behaviours, int Paths) Layout(RomBuffer rom, int block) => rom.ReadByte(block + 4) switch
    {
        Version or 5 or 4 or 3 => (MapTable, BehaviourTable, PathMaps),
        2 => (0x300, 0x500, -1),
        _ => (0x300, -1, -1),
    };
    // behaviour bits (a byte per monster: map x 16 + its number among the map's monsters)
    private const int KeepsToItself = 0x01, ChasesInItsArea = 0x02;
    public const int ChaseRange = 6;
    // characters a map can have (actor records $07DE + 32 x (n + 8) run up to the event engine's RAM, the
    // biggest towns have 32)
    public const int MaxCharacters = 32;
    // steps the hero can take after a fight before another monster can start one
    public const int GraceSteps = 3;

    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);
    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);

    private static string Source(int maps, int behaviour, int pathMaps, int heap) => $$"""
        !MAPS = ${{maps:X6}}
        !BEHAV = ${{behaviour:X6}}
        !PMAPS = ${{pathMaps:X6}}
        !HEAP = ${{heap:X6}}
        !BEH = $7FFEC8
        !PIDX = $7FFD00
        !PFLG = $7FFD30
        !TX = $7FFD60
        !TY = $7FFD90
        !CURMAP = $7FFDC0
        !QAREA = $7FFDC2
        !ATRIG = $7FFDC8
        !CNT = $7FFDD0
        !AIDX = $7FFDD1
        !TX16 = $7FFDD2
        !TY16 = $7FFDD4
        !PP = $7FFDD6
        !PMODE = $7FFDD8
        !PCNT = $7FFDD9
        !TGX = $7FFDDA
        !TGY = $7FFDDB
        !HEROX = $7FFEC0
        !HEROY = $7FFEC2
        !TURN = $7FFEC4
        !PEND = $7FFEC5
        !PDIR = $7FFEC6
        !SAFE = $7FFEC7
        !LAST = $7FFC00

        Step:                       ; M = 8, X = 16, X = the character's record, $18 = its actor id
            JSR IsMonster
            BCS .monster
            LDA $1D,x               ; (replaced) frozen?
            BIT.b #$20
            RTL
        .monster
            JSR Fresh
            LDA $077F               ; its events come from this map's script block, however it is talked to (the talk
            STA $1C,x               ; reads the map from $1C, which after a trip out can still name the world map)
            LDA $1D,x               ; beaten (hidden by its event): out of the map's actor list, so it can't be
            BIT.b #$80              ; talked to (fought) again, until the map is entered again
            BNE .gone
            LDA $0A,x
            BNE .go                 ; a step under way: carry on
            JSR Arrive              ; (on a tile: a step-on area there?)
            JSR Turn
            JSR Contact
            BCS .rest               ; next to the hero: wait for the talk check
            PHX
            REP #$20
            LDA $18
            AND.w #$003F
            TAX
            SEP #$20
            LDA !TURN
            CMP !LAST,x
            BEQ .done               ; already moved this turn
            STA !LAST,x
            PLX
            JSR Choose
            CMP.b #$80
            BCS .rest               ; (at the end of a path it walks once)
            STA $1B
            PHX
            JSL Target              ; $12/$14 = the tile that way, $1A = its direction bit
            PLX
            SEP #$20
            LDA $12                 ; inside the box
            CMP $11,x
            BCC .rest
            CMP $15,x
            BCS .rest
            LDA $14
            CMP $13,x
            BCC .rest
            CMP $17,x
            BCS .rest
            JSR Hero                ; not onto the hero
            LDA $0001,y
            CMP $12
            BNE +
            LDA $0004,y
            CMP $14
            BEQ .rest
        +   PHX
            JSL Wall
            PLX
            SEP #$20
            BCS .rest
            LDA $1A                 ; the step (as the game's wandering does)
            STA $0A,x
            LDA $1B
            STA $0B,x
        .go
            LDA.b #$00
            RTL
        .done
            PLX
        .rest
            LDA.b #$01              ; only animate
            RTL
        .gone
            JSR Sweep
            BRA .rest

        Sweep:                      ; M = 8, X = 16: beaten monsters (hidden: $1D bit 80) out of the actor list; all kept
            PHB
            PHX
            PHY
            LDA $18
            PHA
            LDA.b #$7E
            PHA
            PLB
            LDY.w #$001F
        .next
            LDA $07BE,y
            BEQ .skip
            STA $18
            JSR IsMonster
            BCC .skip
            REP #$20
            LDA $18
            AND.w #$003F
            ASL #5
            CLC
            ADC.w #$07DE
            TAX
            SEP #$20
            LDA $1D,x
            BIT.b #$80
            BEQ .skip
            LDA.b #$00
            STA $07BE,y
        .skip
            DEY
            BPL .next
            PLA
            STA $18
            PLY
            PLX
            PLB
            RTS

        Behav:                      ; A = this monster's behaviour byte (M = 8, X = 16; X kept)
            PHX
            REP #$20
            LDA $077F
            AND.w #$00FF
            ASL
            TAX
            SEP #$20
            LDA $18
            SEC
            SBC.b #$08
            SEC
            SBC !MAPS,x             ; its number among the map's monsters
            REP #$20
            AND.w #$000F
            PHA
            TXA
            ASL #3                  ; map x 16
            CLC
            ADC $01,s
            TAX
            PLA
            SEP #$20
            LDA !BEHAV,x
            PLX
            RTS

        IsMonster:                  ; C = this character is one of the map's monsters; X kept
            PHX
            REP #$20
            LDA $077F
            AND.w #$00FF
            ASL
            TAX
            SEP #$20
            LDA !MAPS+1,x           ; how many
            BEQ .no
            PHA
            LDA $18
            SEC
            SBC.b #$08
            BCC .no2
            SEC
            SBC !MAPS,x             ; minus the first
            BCC .no2
            CMP $01,s
            BCS .no2
            PLA
            PLX
            SEC
            RTS
        .no2
            PLA
        .no
            PLX
            CLC
            RTS

        Hero:                       ; Y = the hero's record (X kept)
            REP #$20
            LDA $8D
            AND.w #$00FF
            ASL #5
            CLC
            ADC.w #$07DE
            TAY
            SEP #$20
            RTS

        Turn:                       ; a new turn each time the hero reaches a new tile
            JSR Hero
            LDA $0001,y
            CMP !HEROX
            BNE .new
            LDA $0004,y
            CMP !HEROY
            BEQ .same
        .new
            LDA $0001,y
            STA !HEROX
            LDA $0004,y
            STA !HEROY
            LDA !TURN
            INC
            STA !TURN
            LDA.b #$00              ; (a waiting monster is forgotten once he moves on)
            STA !PEND
            LDA !SAFE               ; a step of the grace after a fight
            BEQ .same
            DEC
            STA !SAFE
        .same
            RTS

        Contact:                    ; C = next to the hero, he on a tile and it still (then it waits and the talk check is set up)
            LDA !SAFE               ; not right after a fight
            BEQ +
            CMP.b #{{GraceSteps + 1}}
            BCC .no
            LDA.b #$00              ; (more is what RAM held at power-on)
            STA !SAFE
        +   JSR Hero
            LDA $0000,y             ; the hero exactly on a tile (not between two)
            ORA $0003,y
            BNE .no
            LDA $0001,y             ; dx = monster - hero
            STA $1A
            LDA $01,x
            SEC
            SBC $1A
            STA $1A
            LDA $0004,y             ; dy
            STA $1B
            LDA $04,x
            SEC
            SBC $1B
            STA $1B
            LDA $1A
            BEQ .vertical
            LDA $1B
            BNE .no
            LDA $1A
            CMP.b #$01
            BEQ .right
            CMP.b #$FF
            BNE .no
            LDA.b #$01              ; the monster is to his left
            BRA .found
        .right
            LDA.b #$00
            BRA .found
        .vertical
            LDA $1B
            CMP.b #$01
            BEQ .down
            CMP.b #$FF
            BNE .no
            LDA.b #$03              ; above him
            BRA .found
        .down
            LDA.b #$02
        .found
            STA !PDIR
            LDA !PEND
            BNE +
            LDA $18
            STA !PEND
        +   SEC
            RTS
        .no
            CLC
            RTS

        Choose:                     ; A = a direction (0 right, 1 left, 2 down, 3 up; 80 = stay): towards the hero when near, else along its path
            JSR Behav
            STA !BEH
            BIT.b #{{KeepsToItself}}
            BNE .path               ; it doesn't come after the party
            JSR Hero
            LDA $0001,y             ; the hero inside its roaming area
            CMP $11,x
            BCC .path
            CMP $15,x
            BCS .path
            LDA $0004,y
            CMP $13,x
            BCC .path
            CMP $17,x
            BCS .path
            LDA $0001,y             ; dx = hero - monster
            SEC
            SBC $01,x
            STA $1A
            LDA $0004,y
            SEC
            SBC $04,x
            STA $1B
            LDA $1A                 ; |dx| -> $16, |dy| -> $17
            BPL +
            EOR.b #$FF
            INC
        +   STA $16
            LDA $1B
            BPL +
            EOR.b #$FF
            INC
        +   STA $17
            LDA !BEH                ; (one that roams only part of the map comes from anywhere in it)
            BIT.b #{{ChasesInItsArea}}
            BNE +
            LDA $17
            CLC
            ADC $16
            CMP.b #{{ChaseRange + 1}}
            BCS .path
        +
            PHX                     ; now and then a random step anyway (round walls)
            LDA.b #$04
            JSL $08B829
            PLX
            CMP.b #$00
            BEQ .random
            LDA $17                 ; |dy| > |dx|: up or down
            CMP $16
            BEQ .sideways
            BCS .updown
        .sideways
            LDA $1A
            BMI +
            LDA.b #$00
            RTS
        +   LDA.b #$01
            RTS
        .updown
            LDA $1B
            BMI +
            LDA.b #$02
            RTS
        +   LDA.b #$03
            RTS
        .path
            JSR PathDir
            BCC .random
            RTS
        .random
            PHX
            LDA.b #$04
            JSL $08B829
            PLX
            RTS

        Fresh:                      ; a map entered: paths from their start, no areas set off yet (X, Y kept)
            PHX
            REP #$20
            LDA $077F
            AND.w #$00FF
            ORA.w #$A500            ; (with a mark, so RAM's power-on contents don't pass for a map)
            CMP !CURMAP
            BEQ .same
            STA !CURMAP
            LDA.w #$0000
            STA !QAREA
            SEP #$20
            LDX.w #$002F
        -   LDA.b #$00
            STA !PIDX,x
            STA !PFLG,x
            LDA.b #$FF
            STA !TX,x
            STA !TY,x
            DEX
            BPL -
            LDX.w #$0007
        -   LDA.b #$00
            STA !ATRIG,x
            DEX
            BPL -
        .same
            SEP #$20
            PLX
            RTS

        Arrive:                     ; X = the monster's record, on a tile: a new one in a step-on area sets it off (X kept)
            PHX
            TXY
            REP #$20
            LDA $18
            AND.w #$003F
            TAX
            SEP #$20
            LDA $0001,y
            CMP !TX,x
            BNE .new
            LDA $0004,y
            CMP !TY,x
            BEQ .out
        .new
            LDA !TX,x
            CMP.b #$FF              ; (where it starts doesn't count)
            PHP
            LDA $0001,y
            STA !TX,x
            LDA $0004,y
            STA !TY,x
            PLP
            BEQ .out
            JSR AreaAt
        .out
            PLX
            RTS

        AreaAt:                     ; Y = a monster's record: queue the step-on area it stands in (once a map visit)
            LDA $079C               ; the map's areas: $079C of them, 12 bytes each from $7F:($07A8) ($01:9494)
            BNE +
            RTS
        +   STA !CNT
            LDA.b #$00
            STA !AIDX
            REP #$20
            LDA $0001,y
            STA !TX16
            LDA $0004,y
            STA !TY16
            LDX $07A8
        .loop
            REP #$20
            LDA !TX16
            CMP $7F0004,x
            BCC .next
            CMP $7F0008,x
            BCS .next
            LDA !TY16
            CMP $7F0006,x
            BCC .next
            CMP $7F000A,x
            BCS .next
            SEP #$20
            LDA $7F0002,x           ; switched on
            BEQ .next
            PHX
            JSR AreaBit             ; X = its byte in !ATRIG, A = its bit
            AND !ATRIG,x
            BNE .done               ; set off already
            JSR AreaBit
            ORA !ATRIG,x
            STA !ATRIG,x
            PLX
            REP #$20
            LDA !QAREA
            BNE +                   ; (one at a time: a second waits for the next step)
            TXA
            STA !QAREA
        +   SEP #$20
            RTS
        .done
            PLX
        .next
            REP #$20
            TXA
            CLC
            ADC.w #$000C
            TAX
            SEP #$20
            LDA !AIDX
            INC
            STA !AIDX
            LDA !CNT
            DEC
            STA !CNT
            BNE .loop
        .none
            SEP #$20
            RTS

        AreaBit:                    ; area !AIDX: X = its byte in !ATRIG, A = its bit
            REP #$20
            LDA !AIDX
            AND.w #$0007
            TAX
            SEP #$20
            LDA.b #$01
        -   DEX
            BMI +
            ASL
            BRA -
        +   PHA
            REP #$20
            LDA !AIDX
            AND.w #$0038
            LSR #3
            TAX
            SEP #$20
            PLA
            RTS

        PathDir:                    ; X = the monster's record: C = it has a path; A = the way to its next point (80 = stay)
            PHX
            TXY
            REP #$20
            LDA $077F
            AND.w #$00FF
            ASL
            TAX
            LDA !PMAPS,x            ; the map's paths in the heap (0 = none)
            BNE +
        .none
            SEP #$20
            PLX
            CLC
            RTS
        +   STA !PP
            SEP #$20
            LDA $18
            SEC
            SBC.b #$08
            SEC
            SBC !MAPS,x             ; its number among the map's monsters
            REP #$20
            AND.w #$000F
            ASL
            CLC
            ADC !PP
            TAX
            LDA !HEAP,x             ; its path (0 = none)
            BEQ .none
            STA !PP
            TAX
            SEP #$20
            LDA !HEAP,x
            STA !PMODE
            LDA !HEAP+1,x
            STA !PCNT
            BEQ .none
            REP #$20
            LDA $18
            AND.w #$003F
            TAX
            SEP #$20
            LDA !PFLG,x
            BMI .stay               ; walked a path that goes once
            LDA !PIDX,x
            CMP !PCNT
            BCC +
            LDA.b #$00
            STA !PIDX,x
        +   JSR Point
            LDA $0001,y             ; at the point: on to the next
            CMP !TGX
            BNE .go
            LDA $0004,y
            CMP !TGY
            BNE .go
            JSR Advance
            BCS .stay
            LDA !PIDX,x
            JSR Point
        .go
            LDA !TGX                ; the longer way first
            SEC
            SBC $0001,y
            STA $1A
            BPL +
            EOR.b #$FF
            INC
        +   STA $16
            LDA !TGY
            SEC
            SBC $0004,y
            STA $1B
            BPL +
            EOR.b #$FF
            INC
        +   STA $17
            CMP $16
            BEQ +
            BCS .vertical
        +   LDA $16
            BEQ .vertical
            LDA $1A
            BMI .left
            LDA.b #$00
            BRA .out
        .left
            LDA.b #$01
            BRA .out
        .vertical
            LDA $1B
            BMI .up
            LDA.b #$02
            BRA .out
        .up
            LDA.b #$03
            BRA .out
        .stay
            LDA.b #$80
        .out
            PLX
            SEC
            RTS

        Point:                      ; A = a point of the path at !PP: !TGX / !TGY (X kept)
            PHX
            REP #$20
            AND.w #$00FF
            ASL
            CLC
            ADC !PP
            TAX
            SEP #$20
            LDA !HEAP+2,x
            STA !TGX
            LDA !HEAP+3,x
            STA !TGY
            PLX
            RTS

        Advance:                    ; X = actor id: the next point (loop, back and forth, once); C = the path is done
            LDA !PMODE
            CMP.b #$02
            BEQ .both
            LDA !PIDX,x
            INC
            CMP !PCNT
            BCC .set
            LDA !PMODE
            CMP.b #$03
            BEQ .end
            LDA.b #$00
        .set
            STA !PIDX,x
            CLC
            RTS
        .end
            LDA.b #$80
            STA !PFLG,x
            SEC
            RTS
        .both
            LDA !PFLG,x
            BIT.b #$01
            BNE .back
            LDA !PIDX,x
            INC
            CMP !PCNT
            BCC .set
            LDA.b #$01
            STA !PFLG,x
            LDA !PCNT
            SEC
            SBC.b #$02
            BCS .set
            LDA.b #$00
            BRA .set
        .back
            LDA !PIDX,x
            BEQ .turn               ; back at the first point: forwards again
            DEC
            BRA .set
        .turn
            LDA.b #$00
            STA !PFLG,x
            LDA !PCNT
            CMP.b #$02
            LDA.b #$00
            BCC .set
            LDA.b #$01
            BRA .set

        Talk:                       ; M = 8, in place of LDA $30 / AND #$80 (A pressed?) before BEQ
            JSR Fresh
            JSR Sweep               ; (a monster beaten just now can't be talked to)
            JSR OwnMap
            LDA !PEND
            BNE .pend
            REP #$20                ; a step-on area a monster set off: its event, as the game runs it for the party
            LDA !QAREA              ; ($01:90E8: "once" areas are marked done)
            BEQ .normal8
            PHX
            TAX
            LDA.w #$0000
            STA !QAREA
            SEP #$20
            LDA $7F0000,x
            STA $00
            LDA $7F0001,x
            STA $04
            LDA $7F0002,x
            STA $1A
            LDA $7F0003,x
            STA $16
            PHB
            LDA.b #$00
            PHA
            PLB
            JSL RunArea
            PLB
            PLX
            SEP #$20
            LDA.b #$00              ; (no talk this time)
            RTL
        .normal8
            SEP #$20
            BRA .normal
        .pend
            PHX
            REP #$20                ; its event comes from this map's script block (the talk reads the map from
            AND.w #$003F            ; the character's $1C, which can still name the map it came from)
            ASL #5
            CLC
            ADC.w #$07DE
            TAX
            SEP #$20
            LDA $077F
            STA $1C,x
            LDA.b #$00
            STA !PEND
            LDA.b #{{GraceSteps}}   ; then the others leave him alone for a few steps
            STA !SAFE
            REP #$20                ; the hero faces the monster
            LDA $8D
            AND.w #$00FF
            ASL #5
            CLC
            ADC.w #$07DE
            TAX
            SEP #$20
            LDA !PDIR
            STA $0B,x
            PLX
            SEP #$20
            LDA.b #$80              ; a fresh press for TRB $34
            TSB $34
            RTL
        .normal
            LDA $30
            AND.b #$80
            RTL

        OwnMap:                     ; M = 8, X = 16: every monster of this map takes its events from this map's block,
            PHX                     ; also when A is pressed at one ($1C can still name the map the party came from,
            PHY                     ; often the world map, whose event would run instead)
            REP #$20
            LDA $077F
            AND.w #$00FF
            ASL
            TAX
            LDA !MAPS+1,x           ; the map's monster count
            AND.w #$00FF
            BEQ .out
            TAY
            LDA !MAPS,x             ; its first character: record $07DE + 32 x (n + 8)
            AND.w #$00FF
            CLC
            ADC.w #$0008
            ASL #5
            CLC
            ADC.w #$07DE
            TAX
        .next
            SEP #$20
            LDA $077F
            STA $1C,x
            REP #$21                ; (and C clear)
            TXA
            ADC.w #$0020
            TAX
            DEY
            BNE .next
        .out
            SEP #$20
            PLY
            PLX
            RTS

        NoRandom:                   ; M = 8, X = 8: A not 0 = no random battle on this step
            PHP
            REP #$10
            JSR Fresh               ; (maps without monsters too: coming back is a new visit)
            PLP
            LDA $07B4               ; (replaced: LDA $07B4 / BNE, LDA $94 / BNE)
            ORA $94
            BNE .out
            PHP
            REP #$10
            PHX
            REP #$20
            LDA $077F
            AND.w #$00FF
            ASL
            TAX
            SEP #$20
            LDA !MAPS+1,x           ; the map's monster count
            PLX
            PLP
            CMP.b #$00
        .out
            RTL

        RunArea:
            PEA $AF2B
            JML $0190E8

        Target:
            PEA $AF2B               ; its RTS goes to the RTL at $01:AF2C
            JML $01A053
        Wall:
            PEA $AF2B
            JML $01AFAC
        """;

    public static bool IsApplied(RomBuffer rom) => Block(rom) > 0;
    public static bool IsOriginal(RomBuffer rom) =>
        rom.ReadBytes(StepSite, 4).SequenceEqual(StepOriginal) && rom.ReadBytes(TalkSite, 4).SequenceEqual(TalkOriginal) &&
        rom.ReadBytes(RandomSite, RandomOriginal.Length).SequenceEqual(RandomOriginal);

    private static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(StepSite) != 0x22) return -1;
        int block = FileOffset(rom.ReadByte(StepSite + 1) | rom.ReadByte(StepSite + 2) << 8 | rom.ReadByte(StepSite + 3) << 16) - Header;
        return block >= 0 && block + 4 < rom.Length && rom.ReadBytes(block, 4).SequenceEqual(Magic.Select(c => (byte)c)) ? block : -1;
    }

    // which characters of a map are monsters: (first character, count), count 0 = none
    public static (int First, int Count) Monsters(RomBuffer rom, int mapId)
    {
        int block = Block(rom);
        if (block < 0) return (0, 0);
        int table = Layout(rom, block).Maps;
        return (rom.ReadByte(block + table + mapId * 2), rom.ReadByte(block + table + mapId * 2 + 1));
    }

    // puts the code in (only once)
    public static void Apply(RomBuffer rom)
    {
        int oldBlock = Block(rom);
        if (oldBlock > 0 && rom.ReadByte(oldBlock + 4) == Version) return;
        // an older block: we read its monsters the way it has them, take them off, and put them back the way this
        // version does (their events in slot n + 1)
        var putBack = new Dictionary<int, List<Monster>>();
        if (oldBlock > 0)
        {
            foreach (int mapId in Enumerable.Range(0, 256).Where(m => Monsters(rom, m).Count > 0))
            {
                putBack[mapId] = Placed(rom, LufiaMap.Load(rom, mapId));
                RemoveFromMap(rom, mapId);
            }
            oldBlock = Block(rom);
        }
        if (oldBlock < 0 && !IsOriginal(rom)) throw new InvalidOperationException("The game's code at $01:9FFF, $01:8394 or $01:8820 isn't the original (another patch changed it), so visible monsters can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        // (a block of an older version: its tables come along, the code is new)
        var oldLayout = oldBlock > 0 ? Layout(rom, oldBlock) : (-1, -1, -1);
        var maps = oldBlock > 0 ? rom.ReadBytes(oldBlock + oldLayout.Item1, 512) : new byte[512];
        var behaviours = oldLayout.Item2 > 0 ? rom.ReadBytes(oldBlock + oldLayout.Item2, 256 * 16) : new byte[256 * 16];
        var freeSpace = ExpansionSpace.Load(rom);
        if (oldBlock > 0 && freeSpace.At(oldBlock)?.Start == oldBlock) freeSpace.Free(oldBlock);
        int block = freeSpace.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 0);
        freeSpace.Save();
        int blockSnes = SnesAddress(block);
        var asm = new Asm65816();
        asm.Source(Source(blockSnes + MapTable, blockSnes + BehaviourTable, blockSnes + PathMaps, blockSnes + Heap));
        var code = asm.Build(blockSnes + Header);
        if (Header + code.Length > MapTable) throw new InvalidOperationException("The visible monster code doesn't fit.");
        rom.WriteBytes(block, Magic.Select(c => (byte)c).ToArray());
        rom.WriteByte(block + 4, Version);
        rom.WriteBytes(block + Header, code);
        rom.WriteBytes(block + MapTable, maps);
        // (monsters of a block without behaviours came after the party within 6 tiles, so bits 0)
        rom.WriteBytes(block + BehaviourTable, behaviours);
        rom.WriteBytes(block + PathMaps, new byte[512 + HeapSize]);
        int step = blockSnes + Header + asm.Offset("Step"), talk = blockSnes + Header + asm.Offset("Talk");
        rom.WriteBytes(StepSite, new byte[] { 0x22, (byte)step, (byte)(step >> 8), (byte)(step >> 16) });
        rom.WriteBytes(TalkSite, new byte[] { 0x22, (byte)talk, (byte)(talk >> 8), (byte)(talk >> 16) });
        int noRandom = blockSnes + Header + asm.Offset("NoRandom");
        rom.WriteBytes(RandomSite, new byte[] { 0x22, (byte)noRandom, (byte)(noRandom >> 8), (byte)(noRandom >> 16), 0xD0, 0x4E, 0xEA });
        rom.FixChecksum();
        foreach (var (mapId, list) in putBack) AddToMap(rom, mapId, list);
    }

    // the map's encounter group from its setup script (command 0D gg), or -1
    public static int Group(RomBuffer rom, int mapId)
    {
        var script = SetupScripts.Read(rom, mapId);
        var (ops, _) = SetupScripts.Parse(script);
        foreach (int opAt in ops) if (script[opAt] == 0x0D) return script[opAt + 1];
        return -1;
    }

    // the group the map's monsters fight (from the first one's event), or -1
    public static int MonsterGroup(RomBuffer rom, int mapId)
    {
        var (first, count) = Monsters(rom, mapId);
        if (count == 0) return -1;
        int eventOffset = EventOf(rom, mapId, first);
        return eventOffset >= 0 ? rom.ReadByte(eventOffset + 1) : -1;
    }

    // OldEvents: the block keeps monster character n's event in slot n (versions 1-3, written before 2026-10-22,
    // which was wrong, see the notes at the top) instead of n + 1.
    // NeedsUpdate: visible monsters are in the rom with an older version of the code (Apply updates it)
    public static bool NeedsUpdate(RomBuffer rom) => Block(rom) is int block && block > 0 && rom.ReadByte(block + 4) < Version;
    public static bool IsCurrent(RomBuffer rom) => Block(rom) is int block && block > 0 && rom.ReadByte(block + 4) == Version;

    private static bool OldEvents(RomBuffer rom) => Block(rom) is int block && block > 0 && rom.ReadByte(block + 4) < 4;

    // file offset of monster character's event (1F gg 5C k 00 00), or -1
    private static int EventOf(RomBuffer rom, int mapId, int character)
    {
        int slot = OldEvents(rom) ? character : character + 1;
        if (!HasScript(rom, mapId, slot)) return -1;
        int eventOffset = LufiaMap.EventScriptOffset(rom, mapId, slot);
        return eventOffset >= 0 && eventOffset + 6 <= rom.Length && rom.ReadByte(eventOffset) == 0x1F && rom.ReadByte(eventOffset + 2) == 0x5C && rom.ReadByte(eventOffset + 3) == slot ? eventOffset : -1;
    }

    // a monster on a map: where it starts, its sprite, how far it can wander (0 = anywhere on the map) and
    // the encounter group it fights
    public sealed record Monster(int X, int Y, int Sprite, int Range, int Group, bool Chases = true,
                                 PathMode Mode = PathMode.None, IReadOnlyList<(int X, int Y)>? Path = null);

    // how a monster walks its path: round and round, there and back, or once (then it stays at the end)
    public enum PathMode { None = 0, Loop = 1, BackAndForth = 2, Once = 3 }

    // the block has the behaviour table (version 2 on, before that every monster came after the party)
    private static bool HasBehaviours(RomBuffer rom) => Block(rom) is int block && block > 0 && Layout(rom, block).Behaviours > 0;

    // each map's paths, read from the heap (map -> 16 paths, null = none)
    private static Dictionary<int, (PathMode, List<(int, int)>)?[]> ReadPaths(RomBuffer rom)
    {
        var all = new Dictionary<int, (PathMode, List<(int, int)>)?[]>();
        int block = Block(rom);
        if (block < 0 || Layout(rom, block).Paths < 0) return all;
        for (int mapId = 0; mapId < 256; mapId++)
        {
            int blob = rom.ReadUInt16Le(block + PathMaps + mapId * 2);
            if (blob == 0) continue;
            var paths = new (PathMode, List<(int, int)>)?[16];
            for (int index = 0; index < 16; index++)
            {
                int pathAt = rom.ReadUInt16Le(block + Heap + blob + index * 2);
                if (pathAt == 0) continue;
                int mode = rom.ReadByte(block + Heap + pathAt), count = rom.ReadByte(block + Heap + pathAt + 1);
                var points = Enumerable.Range(0, count).Select(i => ((int)rom.ReadByte(block + Heap + pathAt + 2 + i * 2), (int)rom.ReadByte(block + Heap + pathAt + 3 + i * 2))).ToList();
                paths[index] = ((PathMode)mode, points);
            }
            all[mapId] = paths;
        }
        return all;
    }

    // writes every map's paths into the heap again (packed), with mapId's replaced
    private static void WritePaths(RomBuffer rom, int mapId, (PathMode, List<(int, int)>)?[]? paths)
    {
        int block = Block(rom);
        var all = ReadPaths(rom);
        if (paths != null && paths.Any(p => p != null)) all[mapId] = paths; else all.Remove(mapId);
        var heap = new List<byte> { 0, 0 };   // (offset 0 means none)
        var table = new byte[512];
        foreach (var (map, mapPaths) in all.OrderBy(kv => kv.Key))
        {
            int blob = heap.Count;
            table[map * 2] = (byte)blob; table[map * 2 + 1] = (byte)(blob >> 8);
            heap.AddRange(new byte[32]);
            for (int index = 0; index < 16; index++)
            {
                if (mapPaths[index] is not { } path || path.Item2.Count == 0) continue;
                int pathAt = heap.Count;
                heap[blob + index * 2] = (byte)pathAt; heap[blob + index * 2 + 1] = (byte)(pathAt >> 8);
                heap.Add((byte)path.Item1); heap.Add((byte)path.Item2.Count);
                foreach (var (x, y) in path.Item2) { heap.Add((byte)x); heap.Add((byte)y); }
            }
        }
        if (heap.Count > HeapSize) throw new InvalidOperationException($"The monsters' paths need {heap.Count} bytes and there are {HeapSize}: shorten some.");
        rom.WriteBytes(block + PathMaps, table);
        rom.WriteBytes(block + Heap, heap.Concat(new byte[HeapSize - heap.Count]).ToArray());
    }

    // the map's monsters as placed
    public static List<Monster> Placed(RomBuffer rom, LufiaMap map)
    {
        var (first, count) = Monsters(rom, map.MapId);
        if (count == 0) return new();
        int GroupOf(int character)
        {
            int eventOffset = EventOf(rom, map.MapId, character);
            return eventOffset >= 0 ? rom.ReadByte(eventOffset + 1) : -1;
        }
        int block = Block(rom);
        var behaviours = Layout(rom, block).Behaviours;
        ReadPaths(rom).TryGetValue(map.MapId, out var paths);
        return map.Npcs.Where(n => n.Index >= first && n.Index < first + count)
                       .Select(npc =>
                       {
                           int slot = Math.Min(15, npc.Index - first);
                           var path = paths?[slot];
                           return new Monster(npc.X, npc.Y, npc.Sprite,
                                     path != null || (npc.BoxX1 == 0 && npc.BoxY1 == 0 && npc.BoxX2 >= map.Width && npc.BoxY2 >= map.Height) ? 0 : Math.Max(npc.X - npc.BoxX1, npc.BoxX2 - 1 - npc.X),
                                     GroupOf(npc.Index),
                                     behaviours < 0 || (rom.ReadByte(block + behaviours + map.MapId * 16 + slot) & KeepsToItself) == 0,
                                     path?.Item1 ?? PathMode.None, path?.Item2);
                       })
                       .ToList();
    }

    private static bool IsMonsterEvent(EventScript? script, int character) =>
        script != null && script.Ops.Count == 3 && script.Ops[0].Bytes is [0x1F, _] && script.Ops[1].Bytes is [0x5C, var who, 0x00] && who == character && script.Ops[2].Bytes is [0x00];

    private static byte[] MonsterScript(int character, int group) => new byte[] { 0x1F, (byte)group, 0x5C, (byte)character, 0x00, 0x00 };

    // slot holds what taking a monster off leaves behind: its script with the first byte
    // ended (00 gg 5C n 00 00)
    private static bool IsRemovedMonster(RomBuffer rom, int mapId, int slot)
    {
        if (!HasScript(rom, mapId, slot)) return false;
        int scriptAt = LufiaMap.EventScriptOffset(rom, mapId, slot);
        if (scriptAt < 0 || scriptAt + 6 > rom.Length) return false;
        var bytes = rom.ReadBytes(scriptAt, 6);
        return bytes[0] == 0x00 && bytes[2] == 0x5C && bytes[3] == slot && bytes[4] == 0x00 && bytes[5] == 0x00;
    }

    // the monster events of chars: rewritten where a removed monster's script is, the
    // rest added after the map's script block (copied to the added space once, for all of them)
    private static void WriteEvents(RomBuffer rom, int mapId, List<(int N, int Group)> chars)
    {
        var fresh = new List<int>();
        var groups = chars.ToDictionary(c => c.N, c => c.Group);
        foreach (var (character, group) in chars)
        {
            if (IsRemovedMonster(rom, mapId, character)) rom.WriteBytes(LufiaMap.EventScriptOffset(rom, mapId, character), MonsterScript(character, group));
            else fresh.Add(character);
        }
        if (fresh.Count == 0) return;
        int blockOffset = EventScript.BlockOffset(rom, mapId), length = MapEvents.BlockLength(rom, mapId);
        var block = new List<byte>(rom.ReadBytes(blockOffset, length));
        var offsets = new List<int>();
        foreach (int character in fresh) { offsets.Add(block.Count); block.AddRange(MonsterScript(character, groups[character])); }
        block.Add(0x00);   // guard
        if (block.Count > 0xFFFF) throw new InvalidOperationException("This map's events are too big to add monsters.");
        var data = block.ToArray();
        for (int i = 0; i < fresh.Count; i++) { data[fresh[i] * 2] = (byte)offsets[i]; data[fresh[i] * 2 + 1] = (byte)(offsets[i] >> 8); }
        var freeSpace = ExpansionSpace.Load(rom);
        int newBlock = freeSpace.Allocate(data.Length, ExpansionSpace.Kind.ScriptBlock, mapId, EventScript.ScriptLimit);
        rom.WriteBytes(newBlock, data);
        int pointer = newBlock - 0x18000, tableAt = LufiaMap.MapScriptTable + mapId * 5;
        rom.WriteBytes(tableAt, new[] { (byte)pointer, (byte)(pointer >> 8), (byte)(pointer >> 16) });
        if (blockOffset >= MapWriter.ExpansionStart && freeSpace.At(blockOffset)?.Start == blockOffset && !MapCatalog.Scan(rom).Any(m => m.MapId != mapId && EventScript.BlockOffset(rom, m.MapId) == blockOffset))
            freeSpace.Free(blockOffset);
        freeSpace.Save();
        rom.FixChecksum();
    }

    // slot has a script of its own (not FFFF, and not 0 / 1, which mean no event)
    private static bool HasScript(RomBuffer rom, int mapId, int slot)
    {
        if (!MapEvents.HasBlock(rom, mapId)) return false;
        int entryAt = EventScript.BlockOffset(rom, mapId) + slot * 2;
        if (entryAt + 1 >= rom.Length) return false;
        int relative = rom.ReadUInt16Le(entryAt);
        return relative != 0xFFFF && relative >= 2;
    }

    // gives a map (not the world map) monsters, in place of the ones it has: one character per place in
    // places with sprite, wandering anywhere on the map (range 0) or up to range tiles from where it starts, each
    // fighting the encounter group in group (-1 = the group they fight now, else the one the map's setup script gives random
    // battles) when it reaches the party. the map has no random battles while it has monsters
    public static void AddToMap(RomBuffer rom, int mapId, IReadOnlyList<(int X, int Y)> places, int sprite, int range = 0, int group = -1)
    {
        if (places.Count == 0) { RemoveFromMap(rom, mapId); return; }
        if (group <= 0) group = MonsterGroup(rom, mapId);
        if (group <= 0) group = Group(rom, mapId);
        if (group <= 0) throw new InvalidOperationException($"Map {mapId:X2} has no random battles: pick the group its monsters fight.");
        AddToMap(rom, mapId, places.Select(p => new Monster(p.X, p.Y, sprite, range, group)).ToList());
    }

    // gives a map (not the world map) monsters, in place of the ones it has: each with its own sprite,
    // wandering range and encounter group (the fight its event starts when it reaches the party). the map has no
    // random battles while it has monsters
    public static void AddToMap(RomBuffer rom, int mapId, IReadOnlyList<Monster> monsters)
    {
        if (mapId == LufiaMap.WorldMap) throw new InvalidOperationException("Visible monsters aren't for the world map.");
        if (monsters.Count == 0) { RemoveFromMap(rom, mapId); return; }
        if (monsters.FirstOrDefault(m => m.Group is < 1 or > 0xFF) is { } bad)
            throw new InvalidOperationException($"The monster at {bad.X},{bad.Y} has no encounter group.");
        var places = monsters.Select(m => (m.X, m.Y)).ToList();
        Apply(rom);
        RemoveFromMap(rom, mapId);
        var map = LufiaMap.Load(rom, mapId);
        // talking to character n runs event n + 1 of the block, a step-on area runs event base + area + 1 and "run
        // event" rr runs base + rr. the monsters take characters whose events nobody else uses (characters skipped on
        // the way stay unused, never shown)
        int eventBase = LufiaMap.EventBase(rom, mapId), slots = MapEvents.HasBlock(rom, mapId) ? MapEvents.TableLength(rom, mapId) : 0;
        var taken = new HashSet<int>();
        var cast = new HashSet<int>();
        foreach (var trigger in map.Triggers) taken.Add((eventBase + trigger.Index + 1) & 0xFF);
        for (int slot = 0; slot < slots; slot++)
        {
            if (!HasScript(rom, mapId, slot) || IsRemovedMonster(rom, mapId, slot)) continue;
            taken.Add(slot);
            EventScript? script;
            try { script = EventScript.Load(rom, mapId, slot); } catch { continue; }
            if (script == null) continue;
            foreach (var op in script.Ops)
            {
                if (op.IsText || op.Bytes.Length == 0) continue;
                if (op.Bytes.Length >= 3 && op.Bytes[0] == 0x51 && op.Bytes[1] == mapId) taken.Add(MapEvents.Chained(rom, op.Bytes));
                // characters a cutscene shows, moves or places (even ones the map doesn't list yet) aren't for monsters
                // (the editor's "character k" and the game's can be one apart, so both)
                if (EventCommands.Find(op.Bytes[0]) is { } def)
                    foreach (var prm in def.Params.Where(pr => pr.Kind == ParamKind.Who))
                    {
                        int who;
                        try { who = prm.Get(op.Bytes); } catch { continue; }
                        if (who >= 0x100) { cast.Add(who - 0x100); cast.Add(who - 0x101); }
                    }
            }
        }
        bool Free(int character) => !taken.Contains(character + 1) && !cast.Contains(character) && (character >= map.Npcs.Count || map.Npcs[character].IsUnused);
        int first = 0;
        while (first < MaxCharacters && !Enumerable.Range(first, places.Count).All(Free)) first++;
        if (first + places.Count > MaxCharacters)
            throw new InvalidOperationException($"Map {mapId:X2} has no room for {places.Count} monsters: a map can have {MaxCharacters} characters, and the free ones (with no event of their own) don't reach that many.");
        for (int i = map.Npcs.Count; i < first + places.Count; i++)
            map.Npcs.Add(new MapNpc { Index = i, Sprite = 0xFF, X = 0, Y = 0, BoxX1 = 0, BoxY1 = 0, BoxX2 = 1, BoxY2 = 1 });
        for (int i = 0; i < monsters.Count; i++)
        {
            var (x, y, sprite, range, _, _, mode, path) = monsters[i];
            if (mode != PathMode.None && path is { Count: > 0 }) range = 0;   // (one on a path goes wherever its path does)
            var (x1, y1, x2, y2) = range <= 0 ? (0, 0, map.Width, map.Height)
                : (Math.Max(0, x - range), Math.Max(0, y - range), Math.Min(map.Width, x + range + 1), Math.Min(map.Height, y + range + 1));
            map.Npcs[first + i] = new MapNpc { Index = first + i, Sprite = sprite, Flags = 0x20, X = x, Y = y, BoxX1 = x1, BoxY1 = y1, BoxX2 = x2, BoxY2 = y2 };
        }
        MapEvents.EnsureTable(rom, mapId, first + places.Count + 2);
        WriteEvents(rom, mapId, monsters.Select((m, i) => (first + i + 1, m.Group)).ToList());   // (slot n + 1: 1F gg 5C n+1)
        MapWriter.Save(rom, map, true);
        CompactSetup(rom, mapId);
        int block = Block(rom);
        rom.WriteByte(block + MapTable + mapId * 2, (byte)first);
        rom.WriteByte(block + MapTable + mapId * 2 + 1, (byte)places.Count);
        var behaviour = new byte[16];
        for (int i = 0; i < monsters.Count && i < 16; i++)
        {
            bool onPath = monsters[i].Mode != PathMode.None && monsters[i].Path is { Count: > 0 };
            behaviour[i] = (byte)((monsters[i].Chases ? 0 : KeepsToItself) | (monsters[i].Range > 0 && !onPath ? ChasesInItsArea : 0));
        }
        rom.WriteBytes(block + BehaviourTable + mapId * 16, behaviour);
        var paths = new (PathMode, List<(int, int)>)?[16];
        for (int i = 0; i < monsters.Count && i < 16; i++)
            if (monsters[i].Mode != PathMode.None && monsters[i].Path is { Count: > 0 } points)
            {
                if (points.Count > MaxPathPoints) throw new InvalidOperationException($"A path can have {MaxPathPoints} points.");
                paths[i] = (monsters[i].Mode, points.Select(p => (p.X, p.Y)).ToList());
            }
        WritePaths(rom, mapId, paths);
        rom.FixChecksum();
    }

    // the map's doors and gates: setup script spots with FE (event command 52 n + 1 opens spot n for good)
    public static List<int> Doors(RomBuffer rom, int mapId) =>
        MapSetupScript.ReadSpots(rom, mapId).Where(sp => sp.Kind == SpotKind.Door).Select(sp => sp.Spot).Distinct().OrderBy(n => n).ToList();

    // a switch tile at x, y: a step-on area of one tile (the party and monsters set it off, once a visit, areas that
    // only run once stay done) whose event opens door for good (52 n), or does nothing yet
    // (-1, to write in the Events editor). its area number is one whose event nobody uses. returns (area, event)
    public static (int Area, int Event) AddSwitch(RomBuffer rom, int mapId, int x, int y, int door = -1)
    {
        if (mapId == LufiaMap.WorldMap) throw new InvalidOperationException("Switch tiles aren't for the world map.");
        var map = LufiaMap.Load(rom, mapId);
        int area = MapEvents.NextAreaIndex(rom, map);
        map.AddTrigger(x, y, x + 1, y + 1, area);
        MapWriter.Save(rom, map, true);   // (the new area gets its event and gets switched on)
        int eventId = MapEvents.AreaEvent(rom, mapId, area);
        if (door >= 0)
        {
            var script = EventScript.Load(rom, mapId, eventId) ?? throw new InvalidOperationException($"Event {eventId} of map {mapId:X2} can't be read.");
            script.Ops.Clear();
            script.Ops.Add(new ScriptOp { Bytes = new byte[] { 0x52, (byte)(door + 1) } });   // (52 counts objects from 1)
            script.Ops.Add(new ScriptOp { Bytes = new byte[] { 0x00 } });
            script.Save(true);
        }
        rom.FixChecksum();
        return (area, eventId);
    }

    // takes a step-on area off (the switch tiles of AddSwitch, or any area)
    public static void RemoveArea(RomBuffer rom, int mapId, int area)
    {
        var map = LufiaMap.Load(rom, mapId);
        var trigger = map.Triggers.FirstOrDefault(t => t.Index == area) ?? throw new InvalidOperationException($"Map {mapId:X2} has no area {area}.");
        map.RemoveTrigger(trigger);
        MapWriter.Save(rom, map, true);
        rom.FixChecksum();
    }

    // drops the "nothing" commands (40) at the start of the map's setup script. switching characters off
    // leaves them where their "switch on" (41 + n) was, and the new ones go in front, so the script would grow a
    // little with every change. jumps (04 ww, from the script's start) move with the rest
    private static void CompactSetup(RomBuffer rom, int mapId)
    {
        byte[] script;
        try { script = SetupScripts.Read(rom, mapId); } catch (InvalidOperationException) { return; }
        int skip = 0;
        while (2 + skip < script.Length && script[2 + skip] == SetupScripts.Nothing) skip++;
        if (skip == 0) return;
        var (ops, _) = SetupScripts.Parse(script);
        var result = new byte[script.Length - skip];
        script.AsSpan(0, 2).CopyTo(result);
        script.AsSpan(2 + skip).CopyTo(result.AsSpan(2));
        foreach (int opAt in ops)
            if (opAt >= 2 + skip && script[opAt] == 0x04)
            {
                int newAt = opAt - skip, target = result[newAt + 1] | result[newAt + 2] << 8;
                if (target >= 2 + skip) target -= skip; else if (target > 2) target = 2;
                result[newAt + 1] = (byte)target; result[newAt + 2] = (byte)(target >> 8);
            }
        SetupScripts.Replace(rom, mapId, result);
    }

    // takes the map's monsters off (their characters become unused, their events empty). random battles
    // come back the way the map's setup script has them
    public static void RemoveFromMap(RomBuffer rom, int mapId)
    {
        var (first, count) = Monsters(rom, mapId);
        if (count == 0) return;
        var map = LufiaMap.Load(rom, mapId);
        for (int character = first; character < first + count && character < map.Npcs.Count; character++)
        {
            var old = map.Npcs[character];
            map.Npcs[character] = new MapNpc { Index = character, Sprite = 0xFF, X = old.X, Y = old.Y, BoxX1 = 0, BoxY1 = 0, BoxX2 = 1, BoxY2 = 1 };
            int eventOffset = EventOf(rom, mapId, character);
            if (eventOffset >= 0) rom.WriteByte(eventOffset, 0x00);   // ends right away (the slot stays as it is)
        }
        MapWriter.Save(rom, map, true);
        CompactSetup(rom, mapId);
        int block = Block(rom);
        var layout = Layout(rom, block);
        rom.WriteByte(block + layout.Maps + mapId * 2, 0);
        rom.WriteByte(block + layout.Maps + mapId * 2 + 1, 0);
        if (layout.Behaviours > 0) rom.WriteBytes(block + layout.Behaviours + mapId * 16, new byte[16]);
        if (layout.Paths > 0) WritePaths(rom, mapId, null);
        rom.FixChecksum();
    }

    // floor a monster can stand on and get around from: enterable from every side, not near an exit or an
    // arrival point (the party comes in there), not a step-on area or a character's tile, and in the part of the map
    // the arrival points lead to (or its biggest open space)
    public static List<(int X, int Y)> OpenTiles(RomBuffer rom, LufiaMap map)
    {
        var tileset = MapTileset.ForMap(rom, map);
        int width = map.Width, height = map.Height;
        var open = new bool[width * height];
        var stand = new bool[width * height];
        var blank = new Dictionary<int, bool>();
        for (int i = 0; i < open.Length; i++)
        {
            int metatile = map.Layer[i] & 0x3FF, attributes;
            if (tileset.CompositeParts(metatile) is var (ground, overlay)) attributes = tileset.Attribute(ground) | tileset.Attribute(overlay); else attributes = tileset.Attribute(metatile);
            open[i] = (attributes & BlockSet.AttrBlocked) == 0;
            // (stand on floor with no one-sided walls either, so it can step off any way)
            stand[i] = (attributes & (BlockSet.AttrBlocked | BlockSet.AttrLeft | BlockSet.AttrRight | BlockSet.AttrUp | BlockSet.AttrDown)) == 0;
            // (and not on the empty black around a building, open in the game but nowhere the party goes)
            if (stand[i] && !blank.TryGetValue(metatile, out _))
            {
                var pixels = tileset.MetatilePixels(metatile);
                blank[metatile] = pixels.All(c => c == pixels[0]) && (pixels[0] & 0xFF) < 0x20 && ((pixels[0] >> 8) & 0xFF) < 0x20 && ((pixels[0] >> 16) & 0xFF) < 0x20;
            }
            if (stand[i] && blank[metatile]) stand[i] = false;
        }
        var avoid = new bool[width * height];
        void Mark(int x1, int y1, int x2, int y2)
        {
            for (int y = Math.Max(0, y1); y < Math.Min(height, y2); y++)
                for (int x = Math.Max(0, x1); x < Math.Min(width, x2); x++) avoid[y * width + x] = true;
        }
        foreach (var exit in map.Exits) Mark(exit.X1 - 1, exit.Y1 - 1, exit.X2 + 1, exit.Y2 + 1);
        foreach (var arrival in map.Arrivals) Mark(arrival.X - 3, arrival.Y - 3, arrival.X + 4, arrival.Y + 4);
        foreach (var trigger in map.Triggers.Where(t => !t.IsUnused)) Mark(trigger.X1, trigger.Y1, trigger.X2, trigger.Y2);
        foreach (var npc in map.Npcs.Where(n => !n.IsUnused)) Mark(npc.X, npc.Y, npc.X + 1, npc.Y + 1);
        // open spaces, numbered. the ones with an arrival point are where the party walks
        var region = new int[width * height];
        var sizes = new List<int> { 0 };
        for (int start = 0; start < open.Length; start++)
        {
            if (!open[start] || region[start] != 0) continue;
            int regionId = sizes.Count, size = 0;
            var queue = new Queue<int>(); queue.Enqueue(start); region[start] = regionId;
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue(); size++;
                int cx = cell % width, cy = cell / width;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int neighbour = ny * width + nx;
                    if (open[neighbour] && region[neighbour] == 0) { region[neighbour] = regionId; queue.Enqueue(neighbour); }
                }
            }
            sizes.Add(size);
        }
        List<(int, int)> In(HashSet<int> regions)
        {
            var list = new List<(int, int)>();
            for (int i = 0; i < open.Length; i++)
                if (stand[i] && !avoid[i] && regions.Contains(region[i])) list.Add((i % width, i / width));
            return list;
        }
        var walked = map.Arrivals.Where(a => a.X >= 0 && a.Y >= 0 && a.X < width && a.Y < height).Select(a => region[a.Y * width + a.X]).Where(r => r != 0).ToHashSet();
        var found = In(walked);
        if (found.Count == 0 && sizes.Count > 1) found = In(new HashSet<int> { Enumerable.Range(1, sizes.Count - 1).MaxBy(r => sizes[r]) });
        return found;
    }

    // count places for monsters on the map's open floor (OpenTiles), spread
    // out: each about as far from the others as it can be, the first one at random
    public static List<(int X, int Y)> AutoPlaces(RomBuffer rom, LufiaMap map, int count, int seed)
    {
        var tiles = OpenTiles(rom, map);
        var random = new Random(seed);
        var chosen = new List<(int X, int Y)>();
        if (tiles.Count == 0) return chosen;
        chosen.Add(tiles[random.Next(tiles.Count)]);
        while (chosen.Count < count && chosen.Count < tiles.Count)
        {
            var best = tiles.Where(t => !chosen.Contains(t))
                            .Select(t => (t, distance: chosen.Min(c => Math.Abs(c.X - t.X) + Math.Abs(c.Y - t.Y))))
                            .OrderByDescending(x => x.distance).Take(4).ToList();
            chosen.Add(best[random.Next(best.Count)].t);
        }
        return chosen;
    }
}
